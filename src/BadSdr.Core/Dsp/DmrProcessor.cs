using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Dmr;
using IqChannelizer;
using IqChannelizer.Abstractions;
using BadSdr.Core.Pipeline;
using BadSdr.Core.Storage;

namespace BadSdr.Core.Dsp;

public interface IIqProcessor : IDisposable
{
    string Description { get; }
    // Asynchronous workers can fail while acquisition is waiting for its next buffer.
    Exception? Failure => null;
    CancellationToken FailureToken => CancellationToken.None;
    // Empty input may carry a terminal discontinuity after the last delivered IQ block.
    void Process(ReadOnlySpan<ComplexF> input, long firstSample, bool discontinuity);
    void Complete(bool interrupted);
}

public sealed class DmrProcessor : IIqProcessor, IChannelOutputSink
{
    private sealed class OutputBuffer(int size)
    {
        public ComplexF[] Samples = new ComplexF[size];
        public int Count;
    }
    private readonly Dictionary<int, DmrDecoderWorker> decoders = new();
    private readonly Dictionary<int, OutputBuffer> outputs = new();
    private readonly CancellationTokenSource decoderCancellation = new();
    private ExceptionDispatchInfo? decoderFailure;
    private readonly PipelineMetrics metrics;
    private readonly double inputRate;
    private IStreamingChannelizer? channelizer;
    private ComplexF[] buffer = [];
    private int pending;
    private long expected;
    private bool completed;
    private bool disposed;
    public Exception? Failure => Volatile.Read(ref decoderFailure)?.SourceException;
    public CancellationToken FailureToken => decoderCancellation.Token;
    public string Description { get; private set; } = "Spectrum only · no DMR channels";

    /// <param name="sink">Called concurrently across receivers, in order within each receiver.</param>
    public DmrProcessor(RadioSettings settings, IReadOnlyList<ListenChannel> channels,
        PipelineMetrics metrics, Action<CapturedEvent> sink, string runId)
    {
        this.metrics = metrics;
        inputRate = settings.SampleRate;
        if (channels.Select(c => c.Id).Distinct().Count() != channels.Count)
            throw new ArgumentException("Channel IDs must be unique.");
        foreach (var channel in channels) channel.Validate(settings);
        if (channels.Count == 0) return;
        string? fallbackReason = null;
        if (settings.Processing != ProcessingMode.Direct)
        {
            try
            {
                channelizer = ChannelizerFactory.Create(new ChannelizerRequest(
                    SampleRate.FromHz(inputRate),
                    channels.Select(c => new ChannelRequest(c.Id, c.FrequencyHz - settings.CenterFrequencyHz,
                        12_500, .5, MinimumOutputSampleRate: 24_000, PreferredOutputSampleRate: 48_000,
                        ExactOutputSampleRate: 48_000)).ToArray(), settings.Strategy,
                    new InputBlockConstraints(settings.BlockSize, settings.BlockSize),
                    new ChannelizerImplementationHints { ComputeBackend = settings.Backend,
                        FftPlanning = FftPlanning.Estimate }), this);
            }
            catch (Exception error) when (settings.Processing == ProcessingMode.Auto && inputRate is >= 24000 and <= 384000 &&
                error is ArgumentException or InvalidOperationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
            {
                fallbackReason = error.Message;
            }
        }
        try
        {
            if (channelizer == null && inputRate > 384000)
                throw new InvalidOperationException("Direct DMR decoding accepts at most 384 kS/s. Enable the channelizer or lower the tuner sample rate.");
            var origin = DateTimeOffset.UtcNow;
            foreach (var channel in channels)
            {
                var plan = channelizer?.Plan.Channels.Single(c => c.ChannelId == channel.Id);
                var rate = plan?.OutputSampleRate.Hz ?? inputRate;
                var captureTime = origin;
                if (plan is not null)
                    captureTime += TimeSpan.FromSeconds((double)plan.FirstOutputInputSampleOffset.Numerator /
                        plan.FirstOutputInputSampleOffset.Denominator / inputRate);
                var receiver = new DmrReceiver(new ReceiverOptions(rate,
                    channelizer == null ? channel.FrequencyHz - settings.CenterFrequencyHz : 0,
                    CaptureId: $"{runId}/ch{channel.Id}", CentreFrequencyHz: channel.FrequencyHz, CaptureTime: captureTime), ev =>
                    {
                        Interlocked.Increment(ref metrics.Events);
                        sink(new(runId, channel, captureTime, ev));
                    });
                decoders.Add(channel.Id, new(receiver, rate, metrics, decoderCancellation.Token, DecoderFailed));
                if (plan is not null) outputs.Add(channel.Id, new(Math.Max(1, plan.OutputSamplesPerProcess)));
            }
            if (channelizer is not null)
            {
                buffer = new ComplexF[channelizer.InputRequirements.InputSize];
                var plan = channelizer.Plan;
                Description = $"{plan.Strategy} · {plan.SelectedComputeBackend} · {plan.DspBackend} · " +
                    $"{plan.InputRequirements.ChunkSize:N0} samples/block → 48 kS/s/channel";
                if (plan.Warnings.Count > 0) Description += "\n" + string.Join("\n", plan.Warnings);
            }
            else Description = $"Direct DMR · {inputRate:N0} S/s per receiver" +
                (fallbackReason is null ? "" : $"\nChannelizer fallback: {fallbackReason}");
            metrics.DecoderWorkers = decoders.Count;
            Description += $"\nDMR: {decoders.Count} parallel receiver workers · ~10 ms batches · {DmrDecoderWorker.QueueCapacity} queued batches/receiver";
        }
        catch
        {
            decoderCancellation.Cancel();
            foreach (var decoder in decoders.Values) decoder.Join();
            channelizer?.Dispose();
            decoderCancellation.Dispose();
            throw;
        }
    }

    public void Process(ReadOnlySpan<ComplexF> input, long firstSample, bool discontinuity)
    {
        ThrowIfDecoderFailed();
        try { ProcessCore(input, firstSample, discontinuity); }
        catch { ThrowIfDecoderFailed(); throw; }
    }

    private void ProcessCore(ReadOnlySpan<ComplexF> input, long firstSample, bool discontinuity)
    {
        if (completed) throw new InvalidOperationException("The processor is complete.");
        if (firstSample < expected) throw new ArgumentException("IQ sample indices must be monotonic.");
        if (discontinuity || firstSample != expected)
        {
            long resetStart = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref metrics.Discontinuities);
            Interlocked.Add(ref metrics.TailSamples, pending);
            pending = 0;
            Array.Clear(buffer);
            channelizer?.Reset(checked((ulong)firstSample));
            foreach (var decoder in decoders.Values)
            {
                long target = (long)Math.Round(firstSample * decoder.Rate / inputRate);
                decoder.Reset(target);
            }
            Interlocked.Add(ref metrics.ResetTicks, Stopwatch.GetTimestamp() - resetStart);
        }
        expected = firstSample + input.Length;
        if (channelizer is null)
        {
            if (decoders.Count == 0) return;
            long start = Stopwatch.GetTimestamp();
            try
            {
                // Spread large direct/replay buffers across all receivers before filling any queue.
                while (!input.IsEmpty)
                {
                    int count = Math.Min(input.Length, DmrDecoderWorker.BatchSize(inputRate));
                    foreach (var decoder in decoders.Values) decoder.Append(input[..count]);
                    input = input[count..];
                }
            }
            finally { Interlocked.Add(ref metrics.DispatchTicks, Stopwatch.GetTimestamp() - start); }
            return;
        }
        var requirements = channelizer.InputRequirements;
        while (!input.IsEmpty)
        {
            int count = Math.Min(input.Length, requirements.ChunkSize - pending);
            input[..count].CopyTo(buffer.AsSpan(requirements.HistorySize + pending));
            pending += count;
            input = input[count..];
            if (pending != requirements.ChunkSize) continue;
            long start = Stopwatch.GetTimestamp();
            long dispatchBefore = metrics.DispatchTicks;
            channelizer.Process(buffer);
            Interlocked.Add(ref metrics.ChannelizerTicks, Stopwatch.GetTimestamp() - start - (metrics.DispatchTicks - dispatchBefore));
            DispatchOutputs();
            buffer.AsSpan(requirements.ChunkSize, requirements.HistorySize).CopyTo(buffer);
            pending = 0;
        }
    }

    public void Write(int channelId, ReadOnlySpan<ComplexF> samples)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            // The library owns this span. Stage one output round before dispatching it fairly.
            var output = outputs[channelId];
            int needed = checked(output.Count + samples.Length);
            if (output.Samples.Length < needed) Array.Resize(ref output.Samples, needed);
            samples.CopyTo(output.Samples.AsSpan(output.Count));
            output.Count = needed;
        }
        finally { Interlocked.Add(ref metrics.DispatchTicks, Stopwatch.GetTimestamp() - start); }
    }

    private void DispatchOutputs()
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            int maximum = outputs.Count == 0 ? 0 : outputs.Values.Max(output => output.Count);
            int batch = DmrDecoderWorker.BatchSize(48_000);
            for (int offset = 0; offset < maximum; offset += batch)
                foreach (var (id, output) in outputs)
                    if (offset < output.Count)
                        decoders[id].Append(output.Samples.AsSpan(offset, Math.Min(batch, output.Count - offset)));
            foreach (var output in outputs.Values) output.Count = 0;
        }
        finally { Interlocked.Add(ref metrics.DispatchTicks, Stopwatch.GetTimestamp() - start); }
    }

    public void Complete(bool interrupted)
    {
        if (completed) return;
        completed = true;
        // A partial input block is not zero-padded: doing so would invent RF samples.
        Interlocked.Add(ref metrics.TailSamples, pending);
        pending = 0;
        long flushStart = Stopwatch.GetTimestamp(), dispatchBefore = metrics.DispatchTicks;
        try { if (!decoderCancellation.IsCancellationRequested) channelizer?.Flush(); }
        catch (Exception error) { DecoderFailed(error); }
        finally { Interlocked.Add(ref metrics.ChannelizerTicks, Stopwatch.GetTimestamp() - flushStart - (metrics.DispatchTicks - dispatchBefore)); }
        try { if (!decoderCancellation.IsCancellationRequested) DispatchOutputs(); }
        catch (Exception error) { DecoderFailed(error); }
        long dispatchStart = Stopwatch.GetTimestamp();
        foreach (var decoder in decoders.Values)
        {
            try { decoder.FinishInput(interrupted); }
            catch (Exception error) { DecoderFailed(error); }
        }
        Interlocked.Add(ref metrics.DispatchTicks, Stopwatch.GetTimestamp() - dispatchStart);
        long drainStart = Stopwatch.GetTimestamp();
        foreach (var decoder in decoders.Values) decoder.Join();
        Interlocked.Add(ref metrics.DecoderDrainTicks, Stopwatch.GetTimestamp() - drainStart);
        ThrowIfDecoderFailed();
    }

    private void DecoderFailed(Exception error)
    {
        Interlocked.CompareExchange(ref decoderFailure, ExceptionDispatchInfo.Capture(error), null);
        decoderCancellation.Cancel();
    }

    private void ThrowIfDecoderFailed() => Volatile.Read(ref decoderFailure)?.Throw();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Complete(true); }
        finally { channelizer?.Dispose(); decoderCancellation.Dispose(); }
    }
}
