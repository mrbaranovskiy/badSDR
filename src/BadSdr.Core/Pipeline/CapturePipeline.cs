using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using BadSdr.Core.Dsp;
using BadSdr.Core.Sources;
using IqChannelizer.Abstractions;

namespace BadSdr.Core.Pipeline;

public sealed class CapturePipeline : IAsyncDisposable
{
    private readonly record struct Block(ComplexF[] Buffer, int Count, long FirstSample, long Generation);
    private readonly IIqSource source;
    private readonly IIqProcessor processor;
    private readonly SpectrumAnalyzer spectrum;
    private readonly int blockSize;
    private readonly Channel<Block> queue;
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationTokenRegistration processorFailureRegistration;
    private readonly Action checkStorage;
    private readonly bool ownsSource;
    private Task completion = Task.CompletedTask;
    private bool started;
    private Exception? failure;
    private long sourceGeneration;
    public PipelineMetrics Metrics { get; }
    public SourceInfo Info => source.Info;
    public string Description => processor.Description;
    public SpectrumFrame? Spectrum => spectrum.Latest;
    public Task Completion => completion;
    public Exception? Failure => Volatile.Read(ref failure);
    public MetricsSnapshot Snapshot => Metrics.Snapshot(queue.Reader.Count);

    public CapturePipeline(IIqSource source, IIqProcessor processor, PipelineMetrics metrics,
        int blockSize, int queueCapacity, Action? checkStorage = null, bool ownsSource = true)
    {
        this.source = source;
        this.processor = processor;
        Metrics = metrics;
        this.blockSize = blockSize;
        this.checkStorage = checkStorage ?? (() => { });
        this.ownsSource = ownsSource;
        spectrum = new(source.Info);
        queue = Channel.CreateBounded<Block>(new BoundedChannelOptions(queueCapacity)
        {
            // TryWrite implements drop-newest explicitly so ownership and exact counts remain visible.
            FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true,
            // The dedicated consumer blocks on AsTask/GetResult; completing its waiter
            // only wakes that thread. Avoid routing this wake-up through the thread pool.
            AllowSynchronousContinuations = true
        });
        processorFailureRegistration = processor.FailureToken.Register(() =>
            Fail(processor.Failure ?? new IOException("A decoder worker failed.")));
    }

    public void Start()
    {
        if (started) throw new InvalidOperationException("Capture has already started.");
        started = true;
        Metrics.Start();
        var consumer = Task.Factory.StartNew(Consume, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var producer = Task.Factory.StartNew(Produce, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        completion = FinishAsync(producer, consumer);
    }

    private void Produce()
    {
        ComplexF[]? owned = null;
        long sampleIndex = 0, generation = 0;
        try
        {
            // Pool initialization and first-buffer page faults must happen before the
            // radio starts producing IQ; at 40 MS/s its transport can fill in milliseconds.
            owned = ArrayPool<ComplexF>.Shared.Rent(blockSize);
            source.Start();
            while (!cancellation.IsCancellationRequested)
            {
                owned ??= ArrayPool<ComplexF>.Shared.Rent(blockSize);
                var read = source.Read(owned.AsSpan(0, blockSize), cancellation.Token);
                if (read.Count < 0 || read.Count > blockSize) throw new IOException("Source returned an invalid IQ count.");
                if (read.Overflow) Interlocked.Increment(ref Metrics.Overflows);
                if (read.Timeout) Interlocked.Increment(ref Metrics.Timeouts);
                if (read.RxRestarted) Interlocked.Increment(ref Metrics.RxRestarts);
                if (read.Discontinuity) { generation++; Interlocked.Exchange(ref sourceGeneration, generation); }
                if (read.Count > 0)
                {
                    Interlocked.Add(ref Metrics.Received, read.Count);
                    var block = new Block(owned, read.Count, sampleIndex, generation);
                    sampleIndex += read.Count;
                    if (queue.Writer.TryWrite(block))
                    {
                        owned = null;
                        // Only the producer writes this high-water mark.
                        int queued = queue.Reader.Count;
                        if (queued > Metrics.PeakQueuedBlocks) Volatile.Write(ref Metrics.PeakQueuedBlocks, queued);
                    }
                    else Interlocked.Add(ref Metrics.Dropped, read.Count);
                }
                if (read.EndOfStream) break;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
        finally
        {
            if (owned is not null) ArrayPool<ComplexF>.Shared.Return(owned);
            try { source.Stop(); } catch (Exception error) { Fail(error); }
            queue.Writer.TryComplete();
        }
    }

    private void Consume()
    {
        long generation = 0;
        long lastSample = 0;
        try
        {
            while (queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (queue.Reader.TryRead(out var block))
                {
                    try
                    {
                        checkStorage();
                        var input = block.Buffer.AsSpan(0, block.Count);
                        spectrum.Offer(input);
                        long start = Stopwatch.GetTimestamp();
                        try { processor.Process(input, block.FirstSample, block.Generation != generation); }
                        finally
                        {
                            long ticks = Stopwatch.GetTimestamp() - start;
                            Interlocked.Add(ref Metrics.ProcessingTicks, ticks);
                            if (ticks > Metrics.MaxBlockTicks) Interlocked.Exchange(ref Metrics.MaxBlockTicks, ticks);
                        }
                        Interlocked.Add(ref Metrics.Processed, block.Count);
                        generation = block.Generation;
                        lastSample = block.FirstSample + block.Count;
                    }
                    catch { Interlocked.Add(ref Metrics.Dropped, block.Count); throw; }
                    finally { ArrayPool<ComplexF>.Shared.Return(block.Buffer); }
                }
            }
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                // A gap at the very end has no following buffer on which to attach its marker.
                if (Failure is null && (lastSample != Metrics.Received || generation != sourceGeneration))
                    processor.Process([], Metrics.Received, generation != sourceGeneration);
                processor.Complete(cancellation.IsCancellationRequested || Failure is not null);
            }
            catch (Exception error) { Fail(error); }
            finally { Interlocked.Add(ref Metrics.ProcessingTicks, Stopwatch.GetTimestamp() - start); }
        }
    }

    private void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref failure, error, null);
        cancellation.Cancel();
    }

    private async Task FinishAsync(Task producer, Task consumer)
    {
        await Task.WhenAll(producer, consumer);
        while (queue.Reader.TryRead(out var block))
        {
            Interlocked.Add(ref Metrics.Dropped, block.Count);
            ArrayPool<ComplexF>.Shared.Return(block.Buffer);
        }
        Metrics.Stop();
    }

    public async Task StopAsync()
    {
        cancellation.Cancel();
        await completion;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        processorFailureRegistration.Dispose();
        try { processor.Dispose(); }
        finally
        {
            try { if (ownsSource) source.Dispose(); }
            finally { spectrum.Dispose(); cancellation.Dispose(); }
        }
    }
}
