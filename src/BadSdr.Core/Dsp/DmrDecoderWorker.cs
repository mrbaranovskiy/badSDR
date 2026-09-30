using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
using BadSdr.Core.Pipeline;
using Dmr;
using IqChannelizer.Abstractions;

namespace BadSdr.Core.Dsp;

/// <summary>One receiver, one consumer thread, bounded FIFO. All receiver calls stay on this thread.</summary>
internal sealed class DmrDecoderWorker
{
    public const int QueueCapacity = 4;
    private readonly record struct Work(ComplexF[]? Buffer, int Count, long ResetTo = 0);
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
        // The reader blocks on its own thread; a synchronous continuation only wakes it.
        AllowSynchronousContinuations = true
    });
    private readonly DmrReceiver receiver;
    private readonly PipelineMetrics metrics;
    private readonly CancellationToken cancellation;
    private readonly Action<Exception> fail;
    private readonly Task worker;
    private readonly int batchSize;
    private readonly Complex[] conversion;
    private ComplexF[]? pending;
    private int pendingCount;
    private long busyTicks;
    private bool interrupted;
    public double Rate { get; }
    public static int BatchSize(double rate) => Math.Clamp((int)Math.Ceiling(rate / 100), 256, 4096);

    public DmrDecoderWorker(DmrReceiver receiver, double rate, PipelineMetrics metrics,
        CancellationToken cancellation, Action<Exception> fail)
    {
        this.receiver = receiver;
        Rate = rate;
        this.metrics = metrics;
        this.cancellation = cancellation;
        this.fail = fail;
        // About 10 ms per handoff amortizes scheduling of small channelizer callbacks.
        batchSize = BatchSize(rate);
        conversion = new Complex[batchSize];
        worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Append(ReadOnlySpan<ComplexF> samples)
    {
        cancellation.ThrowIfCancellationRequested();
        while (!samples.IsEmpty)
        {
            pending ??= ArrayPool<ComplexF>.Shared.Rent(batchSize);
            int count = Math.Min(samples.Length, batchSize - pendingCount);
            samples[..count].CopyTo(pending.AsSpan(pendingCount));
            pendingCount += count;
            samples = samples[count..];
            if (pendingCount == batchSize) Flush();
        }
    }

    public void Reset(long targetSample)
    {
        // Accepted pre-gap samples must be decoded before closing their sessions.
        Flush();
        Enqueue(new(null, 0, targetSample));
    }

    private void Flush()
    {
        if (pendingCount == 0) return;
        Enqueue(new(pending!, pendingCount));
        // Ownership transfers only after a successful enqueue.
        pending = null;
        pendingCount = 0;
    }

    private void Enqueue(Work work)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!queue.Writer.TryWrite(work))
        {
            long start = Stopwatch.GetTimestamp();
            try { queue.Writer.WriteAsync(work, cancellation).AsTask().GetAwaiter().GetResult(); }
            finally { Interlocked.Add(ref metrics.DecoderWaitTicks, Stopwatch.GetTimestamp() - start); }
        }
        PipelineMetrics.UpdateMaximum(ref metrics.DecoderPeakQueuedBatches, queue.Reader.Count);
    }

    public void FinishInput(bool wasInterrupted)
    {
        interrupted = wasInterrupted;
        try { Flush(); }
        finally
        {
            if (pending is not null) ArrayPool<ComplexF>.Shared.Return(pending);
            pending = null;
            pendingCount = 0;
            queue.Writer.TryComplete();
        }
    }

    public void Join() => worker.GetAwaiter().GetResult();

    private void Run()
    {
        Thread.CurrentThread.Name ??= "DMR receiver";
        try
        {
            while (queue.Reader.WaitToReadAsync(cancellation).AsTask().GetAwaiter().GetResult())
            {
                while (queue.Reader.TryRead(out var work))
                {
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (work.Buffer is null) ResetReceiver(work.ResetTo);
                        else Decode(work.Buffer, work.Count);
                    }
                    finally { if (work.Buffer is not null) ArrayPool<ComplexF>.Shared.Return(work.Buffer); }
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { fail(error); }
        finally
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out var work))
                if (work.Buffer is not null) ArrayPool<ComplexF>.Shared.Return(work.Buffer);
            long start = Stopwatch.GetTimestamp();
            try
            {
                if (interrupted || cancellation.IsCancellationRequested) receiver.Discontinuity(0);
                receiver.Complete();
            }
            catch (Exception error) { fail(error); }
            finally { RecordTime(start, reset: true); }
        }
    }

    private void Decode(ComplexF[] samples, int count)
    {
        long start = Stopwatch.GetTimestamp();
        int active = Interlocked.Increment(ref metrics.ActiveDecoderWorkers);
        PipelineMetrics.UpdateMaximum(ref metrics.PeakActiveDecoderWorkers, active);
        try
        {
            for (int i = 0; i < count; i++) conversion[i] = new(samples[i].Real, samples[i].Imaginary);
            receiver.Push(conversion.AsSpan(0, count));
            Interlocked.Add(ref metrics.DecoderInputNanoseconds, (long)Math.Round(count / Rate * 1e9));
        }
        finally
        {
            RecordTime(start, reset: false);
            Interlocked.Decrement(ref metrics.ActiveDecoderWorkers);
        }
    }

    private void ResetReceiver(long target)
    {
        long start = Stopwatch.GetTimestamp();
        try { receiver.Discontinuity(Math.Max(0, target - receiver.SamplesProcessed)); }
        finally { RecordTime(start, reset: true); }
    }

    private void RecordTime(long start, bool reset)
    {
        long elapsed = Stopwatch.GetTimestamp() - start;
        if (reset) Interlocked.Add(ref metrics.DecoderResetTicks, elapsed);
        else Interlocked.Add(ref metrics.DecoderTicks, elapsed);
        busyTicks += elapsed;
        PipelineMetrics.UpdateMaximum(ref metrics.BusiestDecoderTicks, busyTicks);
    }
}
