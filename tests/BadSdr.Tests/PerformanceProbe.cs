using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BadSdr.Core;
using BadSdr.Core.Dsp;
using BadSdr.Core.Pipeline;
using BadSdr.Core.Sources;
using BadSdr.Core.Storage;
using IqChannelizer.Abstractions;

namespace BadSdr.Tests;

internal static class PerformanceProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        string Value(string name, string fallback) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback;
        var saved = WorkspaceSettings.Load();
        int count = int.Parse(Value("--channels", saved.Channels.Length.ToString()));
        int seconds = int.Parse(Value("--seconds", "10"));
        int stallMilliseconds = int.Parse(Value("--stall-ms", "0"));
        int receiveStallMilliseconds = int.Parse(Value("--receive-stall-ms", "0"));
        int receiveStalls = int.Parse(Value("--receive-stalls", "1"));
        if (count < 0 || seconds < 1 || stallMilliseconds < 0 || receiveStallMilliseconds < 0 || receiveStalls < 1)
            throw new ArgumentOutOfRangeException(nameof(args), "Channel count and stall durations must be non-negative; seconds and receive-stall count must be positive.");
        string label = Value("--label", "probe");
        var settings = saved.Radio with { Source = SourceKind.Usrp,
            SampleRate = double.Parse(Value("--rate", "10000000"), CultureInfo.InvariantCulture),
            DeviceArguments = Value("--device-args", saved.Radio.DeviceArguments),
            WireFormat = Value("--wire-format", saved.Radio.WireFormat),
            BlockSize = int.Parse(Value("--block-size", saved.Radio.BlockSize.ToString())),
            QueueCapacity = int.Parse(Value("--queue-blocks", saved.Radio.QueueCapacity.ToString())) };
        settings.Validate();
        if (args.Contains("--cpu")) settings = settings with { Backend = ComputeBackendPreference.Cpu };
        if (args.Contains("--cuda")) settings = settings with { Backend = ComputeBackendPreference.Cuda };
        if (args.Contains("--strategy")) settings = settings with { Strategy = Enum.Parse<ChannelizerStrategy>(Value("--strategy", "Auto"), true) };
        var channels = Enumerable.Range(0, count).Select(i => i < saved.Channels.Length ? saved.Channels[i] :
            new ListenChannel(i + 100, count <= 32 ? settings.CenterFrequencyHz - 4e6 + i * 250_000 :
                settings.CenterFrequencyHz + Math.Round(((double)i / (count - 1) - .5) * (settings.SampleRate - 25000) / 125) * 125)).ToArray();
        string output = Path.GetFullPath($"artifacts/performance/{label}-{count}ch-{(args.Contains("--live") ? "live" : "noise")}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await using var store = new SessionStore(Path.ChangeExtension(output, ".db"));
        IIqSource input = args.Contains("--live") ? new UsrpSource(settings) : new PacedNoiseSource(settings);
        ReceiveTraceSource? receiveTrace = null;
        if (args.Contains("--receive-trace") || args.Contains("--rx-priority"))
            input = receiveTrace = new ReceiveTraceSource(input,
                Enum.Parse<ThreadPriority>(Value("--rx-priority", "Normal"), true));
        using IIqSource source = receiveStallMilliseconds > 0
            ? new ReceiveStallSource(input, receiveStallMilliseconds, receiveStalls) : input;
        settings = settings with { SampleRate = source.Info.SampleRate, CenterFrequencyHz = source.Info.CenterFrequencyHz };
        var metrics = new PipelineMetrics();
        string runId = Guid.NewGuid().ToString("N");
        var setup = Stopwatch.StartNew();
        Console.WriteLine($"PID {Environment.ProcessId}: planning {count} channels at {settings.SampleRate:N0} S/s, {settings.Backend}/{settings.Strategy}, input block {settings.BlockSize:N0}.");
        var processor = new DmrProcessor(settings, channels, metrics, store.Record, runId);
        setup.Stop();
        IIqProcessor processing = stallMilliseconds == 0 ? processor : new OneStallProcessor(processor, settings.SampleRate, stallMilliseconds);
        await using var pipeline = new CapturePipeline(source, processing, metrics, settings.BlockSize, settings.QueueCapacity,
            store.ThrowIfFaulted, ownsSource: false);
        long allocation = GC.GetTotalAllocatedBytes();
        using var process = Process.GetCurrentProcess();
        TimeSpan cpuStart = process.TotalProcessorTime;
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        Console.WriteLine($"{count} channels, {source.Info.Description}, {seconds}s\n{processor.Description.Split('\n')[0]}");
        pipeline.Start();
        var timeline = new List<MetricsSnapshot>();
        for (int i = 0; i < seconds; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            timeline.Add(pipeline.Snapshot);
            var current = timeline[^1];
            Console.WriteLine($"{current.ElapsedSeconds:F1}s: received {current.Received:N0}, processed {current.Processed:N0}, drop {current.DropPercent:F2}%, UHD {current.Overflows}, RX restarts {current.RxRestarts}, queue {current.QueuedBlocks}");
            if (pipeline.Completion.IsCompleted) break;
        }
        await pipeline.StopAsync();
        await store.EndRunAsync(runId, settings, channels, processor.Description, pipeline.Snapshot, pipeline.Failure?.ToString());
        await store.FlushAsync();
        process.Refresh();
        string? recoveryFailure = null;
        if (source is ReceiveStallSource stalled)
        {
            if (stalled.Stalls != receiveStalls || pipeline.Snapshot.Processed < (receiveStalls + 1) * source.Info.SampleRate)
                recoveryFailure = "RX did not process at least one RF second after all requested receive stalls. Allow enough probe duration for recovery.";
            else if (args.Contains("--live") && pipeline.Snapshot.Overflows == 0)
                recoveryFailure = "The injected receive stalls did not provoke a UHD overflow; recovery was not exercised.";
        }
        var result = new { settings, channels, channelCount = count, stallMilliseconds, receiveStallMilliseconds, receiveStalls,
            injectedReceiveStalls = (source as ReceiveStallSource)?.Stalls ?? 0, plan = processor.Description, metrics = pipeline.Snapshot,
            setupSeconds = setup.Elapsed.TotalSeconds,
            receiveTrace = receiveTrace?.Report(),
            dspBuild = DspBuildInfo.Details, timeline,
            processCpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds,
            allocatedBytes = GC.GetTotalAllocatedBytes() - allocation, gen0Collections = GC.CollectionCount(0) - gen0,
            gen2Collections = GC.CollectionCount(2) - gen2, error = pipeline.Failure?.ToString() ?? recoveryFailure };
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result.metrics));
        Console.WriteLine($"Allocated {result.allocatedBytes / 1e6:F1} MB; GC0 {result.gen0Collections}; GC2 {result.gen2Collections}; {output}");
        if (result.error is not null) Console.WriteLine(result.error);
        return result.error is null ? 0 : 1;
    }

    private sealed class ReceiveTraceSource(IIqSource inner, ThreadPriority priority) : IIqSource
    {
        private long previousEnd, calls, totalTicks, maxGoodReadTicks, maxGapTicks;
        private readonly List<object> slow = [];
        public SourceInfo Info => inner.Info;
        public void Start()
        {
            Thread.CurrentThread.Priority = priority;
            inner.Start();
            previousEnd = Stopwatch.GetTimestamp();
        }
        public SourceRead Read(Span<ComplexF> destination, CancellationToken token)
        {
            long begin = Stopwatch.GetTimestamp();
            long gap = begin - previousEnd;
            maxGapTicks = Math.Max(maxGapTicks, gap);
            var read = inner.Read(destination, token);
            previousEnd = Stopwatch.GetTimestamp();
            long elapsed = previousEnd - begin;
            calls++;
            totalTicks += elapsed;
            if (read.Count > 0) maxGoodReadTicks = Math.Max(maxGoodReadTicks, elapsed);
            if (slow.Count < 64 && (gap > Stopwatch.Frequency / 1000 || read.Overflow || read.RxRestarted ||
                (read.Count > 0 && elapsed > Stopwatch.Frequency / 100)))
                slow.Add(new { call = calls, count = read.Count, read.Overflow, read.Timeout, read.RxRestarted,
                    gapMs = gap * 1000.0 / Stopwatch.Frequency, readMs = elapsed * 1000.0 / Stopwatch.Frequency });
            return read;
        }
        public object Report() => new { priority = priority.ToString(), calls,
            totalSeconds = totalTicks / (double)Stopwatch.Frequency,
            maxGoodReadMs = maxGoodReadTicks * 1000.0 / Stopwatch.Frequency,
            maxGapMs = maxGapTicks * 1000.0 / Stopwatch.Frequency, slow };
        public void Stop() => inner.Stop();
        public void Dispose() => inner.Dispose();
    }

    // Leave the hardware running while deliberately pausing recv, provoking a real UHD overflow.
    private sealed class ReceiveStallSource(IIqSource inner, int milliseconds, int maximum) : IIqSource
    {
        private long samples;
        public int Stalls { get; private set; }
        public SourceInfo Info => inner.Info;
        public void Start() => inner.Start();
        public SourceRead Read(Span<ComplexF> destination, CancellationToken token)
        {
            if (Stalls < maximum && samples >= (Stalls + 1) * Info.SampleRate)
            {
                Stalls++;
                Console.WriteLine($"Injecting RX stall {Stalls}: {milliseconds} ms after {samples} IQ pairs.");
                if (token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested();
            }
            var read = inner.Read(destination, token);
            samples += read.Count;
            return read;
        }
        public void Stop() => inner.Stop();
        public void Dispose() => inner.Dispose();
    }

    // Inject one processing stall after one RF second, exercising recovery from a full queue.
    private sealed class OneStallProcessor(IIqProcessor inner, double rate, int milliseconds) : IIqProcessor
    {
        private bool stalled;
        public string Description => inner.Description;
        public Exception? Failure => inner.Failure;
        public CancellationToken FailureToken => inner.FailureToken;
        public void Process(ReadOnlySpan<ComplexF> input, long firstSample, bool discontinuity)
        {
            if (!stalled && firstSample >= rate) { stalled = true; Thread.Sleep(milliseconds); }
            inner.Process(input, firstSample, discontinuity);
        }
        public void Complete(bool interrupted) => inner.Complete(interrupted);
        public void Dispose() => inner.Dispose();
    }

    private sealed class PacedNoiseSource : IIqSource
    {
        private readonly ComplexF[] noise = new ComplexF[1 << 18];
        private readonly Stopwatch clock = new();
        private long samples;
        public SourceInfo Info { get; }
        public PacedNoiseSource(RadioSettings settings)
        {
            Info = new(settings.SampleRate, settings.CenterFrequencyHz, "Paced deterministic wideband noise");
            var random = new Random(489);
            for (int i = 0; i < noise.Length; i++) noise[i] = new((float)(random.NextDouble() - .5), (float)(random.NextDouble() - .5));
        }
        public void Start() => clock.Restart();
        public SourceRead Read(Span<ComplexF> destination, CancellationToken token)
        {
            double due = samples / Info.SampleRate;
            while (clock.Elapsed.TotalSeconds < due)
            {
                token.ThrowIfCancellationRequested();
                double ms = (due - clock.Elapsed.TotalSeconds) * 1000;
                if (ms > 2) Thread.Sleep(Math.Max(1, (int)ms - 1));
                else Thread.SpinWait(100);
            }
            int written = 0;
            while (written < destination.Length)
            {
                int offset = (int)((samples + written) % noise.Length);
                int count = Math.Min(destination.Length - written, noise.Length - offset);
                noise.AsSpan(offset, count).CopyTo(destination[written..]);
                written += count;
            }
            samples += written;
            return new(written);
        }
        public void Stop() => clock.Stop();
        public void Dispose() { }
    }
}
