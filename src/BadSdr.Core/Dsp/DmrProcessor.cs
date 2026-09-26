using System.Diagnostics;
using System.Numerics;
using Dmr;
using IqChannelizer;
using IqChannelizer.Abstractions;
using BadSdr.Core.Pipeline;
using BadSdr.Core.Storage;

namespace BadSdr.Core.Dsp;

public interface IIqProcessor : IDisposable
{
    string Description { get; }
    // Empty input may carry a terminal discontinuity after the last delivered IQ block.
    void Process(ReadOnlySpan<ComplexF> input, long firstSample, bool discontinuity);
    void Complete(bool interrupted);
}

public sealed class DmrProcessor : IIqProcessor, IChannelOutputSink
{
    private sealed class Decoder(DmrReceiver receiver, double rate, int bufferSize)
    {
        public readonly DmrReceiver Receiver = receiver;
        public readonly double Rate = rate;
        public Complex[] Conversion = new Complex[bufferSize];
    }
    private readonly Dictionary<int, Decoder> decoders = new();
    private readonly PipelineMetrics metrics;
    private readonly double inputRate;
    private IStreamingChannelizer? channelizer;
    private ComplexF[] buffer = [];
    private int pending;
    private long expected;
    private bool completed;
    public string Description { get; private set; } = "Spectrum only · no DMR channels";

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
                decoders.Add(channel.Id, new(receiver, rate, plan?.OutputSamplesPerProcess ?? settings.BlockSize));
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
        }
        catch { channelizer?.Dispose(); throw; }
    }

    public void Process(ReadOnlySpan<ComplexF> input, long firstSample, bool discontinuity)
    {
        if (completed) throw new InvalidOperationException("The processor is complete.");
        if (firstSample < expected) throw new ArgumentException("IQ sample indices must be monotonic.");
        if (discontinuity || firstSample != expected)
        {
            Interlocked.Increment(ref metrics.Discontinuities);
            Interlocked.Add(ref metrics.TailSamples, pending);
            pending = 0;
            Array.Clear(buffer);
            channelizer?.Reset(checked((ulong)firstSample));
            foreach (var decoder in decoders.Values)
            {
                long target = (long)Math.Round(firstSample * decoder.Rate / inputRate);
                decoder.Receiver.Discontinuity(Math.Max(0, target - decoder.Receiver.SamplesProcessed));
            }
        }
        expected = firstSample + input.Length;
        if (channelizer is null)
        {
            foreach (var channel in decoders.Keys) Write(channel, input);
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
            long decoderBefore = metrics.DecoderTicks;
            channelizer.Process(buffer);
            Interlocked.Add(ref metrics.ChannelizerTicks, Stopwatch.GetTimestamp() - start - (metrics.DecoderTicks - decoderBefore));
            buffer.AsSpan(requirements.ChunkSize, requirements.HistorySize).CopyTo(buffer);
            pending = 0;
        }
    }

    public void Write(int channelId, ReadOnlySpan<ComplexF> samples)
    {
        var decoder = decoders[channelId];
        long start = Stopwatch.GetTimestamp();
        if (decoder.Conversion.Length < samples.Length) Array.Resize(ref decoder.Conversion, samples.Length);
        for (int i = 0; i < samples.Length; i++) decoder.Conversion[i] = new(samples[i].Real, samples[i].Imaginary);
        decoder.Receiver.Push(decoder.Conversion.AsSpan(0, samples.Length));
        Interlocked.Add(ref metrics.DecoderTicks, Stopwatch.GetTimestamp() - start);
    }

    public void Complete(bool interrupted)
    {
        if (completed) return;
        completed = true;
        // A partial input block is not zero-padded: doing so would invent RF samples.
        Interlocked.Add(ref metrics.TailSamples, pending);
        pending = 0;
        channelizer?.Flush();
        foreach (var decoder in decoders.Values)
        {
            if (interrupted) decoder.Receiver.Discontinuity(0);
            decoder.Receiver.Complete();
        }
    }

    public void Dispose() => channelizer?.Dispose();
}
