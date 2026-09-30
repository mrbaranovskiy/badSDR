using System.Diagnostics;
using System.Numerics;
using BadSdr.Core;
using BadSdr.Core.Dsp;
using BadSdr.Core.Pipeline;
using BadSdr.Core.Sources;
using BadSdr.Core.Storage;
using Dmr;
using Dmr.Input;
using IqChannelizer.Abstractions;
using Microsoft.Data.Sqlite;

namespace BadSdr.Tests;

internal static partial class Program
{
    private static string fixture = "";
    private static readonly string Output = Path.GetFullPath("artifacts/tests/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--performance")) return await PerformanceProbe.RunAsync(args);
        Directory.CreateDirectory(Output);
        fixture = FindFixture();
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Loaded DSP assemblies are optimized", OptimizedBuild),
            ("FFT frequency and amplitude", Fft),
            ("Direct DMR fixture and SQLite persistence", DirectFixture),
            ("Wideband two-channel DMR extraction", Wideband),
            ("Parallel DMR overlap, bounded queues and borrowed-buffer ownership", ParallelWorkers),
            ("Parallel receivers preserve order across gaps and final drain", ParallelGaps),
            ("Asynchronous DMR failure cancels a waiting source", ParallelFault),
            ("Bounded queue overload and buffer ownership", Overload),
            ("Hardware gaps propagate through a full queue", HardwareGaps),
            ("USRP transient overflow preserves driver recovery", UsrpTransientOverflow),
            ("USRP stalled overflow restarts and marks resumed IQ", UsrpStalledOverflow),
            ("USRP retry limit stops a dead input and drains capture", UsrpRetryLimit),
            ("USRP sustained recovery renews retry budget", UsrpRecoveryBudget),
            ("USRP cancellation prevents restarting stopped capture", UsrpRecoveryCancellation),
            ("USRP restart preserves parallel DMR payloads and order", UsrpRecoveredDmr),
            ("Decoder closes sessions at sample gaps", DecoderGaps),
            ("Processing failure stops capture", Fault),
            ("Stop cancels blocking source reads", Cancellation),
            ("Live channel add / retune / remove / restart", Reconfiguration),
            ("SQLite keeps explicit null identities", NullIdentity),
            ("SQLite backpressure is bounded and explicit", DatabaseBackpressure),
            ("SQLite write failure unblocks flush", DatabaseFailure),
            ("Database lease and interrupted session recovery", Recovery),
            ("Invalid channels and direct wideband rejected", InvalidConfiguration)
        };
        int failures = 0;
        foreach (var (name, run) in tests)
        {
            var watch = Stopwatch.StartNew();
            try { await run(); Console.WriteLine($"PASS {name} ({watch.Elapsed.TotalSeconds:F2}s)"); }
            catch (Exception error) { failures++; Console.WriteLine($"FAIL {name}\n{error}"); }
        }
        if (args.Contains("--uhd-probe"))
        {
            try { Console.WriteLine("UHD discovery: " + string.Join("; ", UsrpSource.Discover())); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL UHD native discovery: " + error); }
        }
        SqliteConnection.ClearAllPools();
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed. Artifacts: {Output}");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static string FindFixture()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "dmr", "dmr_test.wav");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("The sibling DMR fixture is required for integration tests.");
    }
    private static RadioSettings FixtureSettings => new()
    {
        Source = SourceKind.Replay, ReplayPath = fixture, ReplayRealtime = false,
        CenterFrequencyHz = 435_000_000, SampleRate = 54688, Processing = ProcessingMode.Direct,
        BlockSize = 16384, QueueCapacity = 64
    };

    private static Task Fft()
    {
        var values = Enumerable.Range(0, 2048).Select(i => Complex.FromPolarCoordinates(.25, 2 * Math.PI * 128 * i / 2048)).ToArray();
        SpectrumAnalyzer.Transform(values);
        int peak = Enumerable.Range(0, values.Length).MaxBy(i => values[i].Magnitude);
        Check(peak == 128, "FFT frequency sign/bin is wrong.");
        Check(Math.Abs(values[128].Magnitude / 2048 - .25) < 1e-10, "FFT amplitude is wrong.");
        return Task.CompletedTask;
    }

    private static Task OptimizedBuild()
    {
        Check(DspBuildInfo.IsOptimized, "Debug UI builds must load optimized DSP assemblies: " + System.Text.Json.JsonSerializer.Serialize(DspBuildInfo.Details));
        Check(bool.TryParse(AppContext.GetData("System.Runtime.TieredCompilation")?.ToString(), out bool tiered) && !tiered,
            "Live DSP must start optimized, without the initial tiered-JIT throughput penalty.");
        return Task.CompletedTask;
    }

    private static async Task DirectFixture()
    {
        string path = Path.Combine(Output, "fixture.db");
        long voice = 0;
        var settings = FixtureSettings;
        var metrics = new PipelineMetrics();
        await using (var store = new SessionStore(path))
        {
            using var processor = new DmrProcessor(settings, [new(1, 434_987_500)], metrics, ev =>
            {
                if (ev.Event.Type == "vocoder_frame") voice++;
                store.Record(ev);
            }, "fixture");
            ReplayInto(processor);
            processor.Complete(false);
            await store.FlushAsync();
            var rows = await store.ReadRecentAsync();
            Check(voice == 216, $"Expected 216 voice frames; got {voice}.");
            Check(rows.Count > 0 && rows.All(r => r.State == "Ended"), "Sessions were not closed and persisted.");
            Check(rows.Any(r => r.Privacy == "unknown_vendor" && r.ColourCode == "15"), "Vendor metadata or colour code was lost.");
            Check(rows.All(r => r.SourceId is null && r.DestinationId is null), "Vendor IDs were fabricated.");
            File.WriteAllText(Path.Combine(Output, "fixture-sessions.json"), System.Text.Json.JsonSerializer.Serialize(rows));
        }
        await using var reopened = new SessionStore(path);
        Check((await reopened.ReadRecentAsync()).Count > 0, "Sessions did not survive reopening the database.");
    }

    private static void ReplayInto(IIqProcessor processor, bool poisonAfterCopy = false)
    {
        using var reader = new IqReader(fixture);
        var iq = new Complex[7139];
        var single = new ComplexF[iq.Length];
        long index = 0;
        int count;
        while ((count = reader.Read(iq)) > 0)
        {
            for (int i = 0; i < count; i++) single[i] = new((float)iq[i].Real, (float)iq[i].Imaginary);
            processor.Process(single.AsSpan(0, count), index, false);
            if (poisonAfterCopy) single.AsSpan(0, count).Fill(new(float.NaN, float.NaN));
            index += count;
        }
    }

    private static Task Wideband()
    {
        const int factor = 8;
        double rate = 54688 * factor;
        var settings = FixtureSettings with { SampleRate = rate, Processing = ProcessingMode.Channelizer,
            Strategy = ChannelizerStrategy.Fdc, BlockSize = 8192 };
        int[] frames = [0, 0];
        var metrics = new PipelineMetrics();
        using var processor = new DmrProcessor(settings, [new(1, 434_987_500), new(2, 435_087_500)], metrics,
            ev => { if (ev.Event.Type == "vocoder_frame") Interlocked.Increment(ref frames[ev.Channel.Id - 1]); }, "wideband");
        using var reader = new IqReader(fixture);
        var input = new Complex[1021];
        var wide = new ComplexF[input.Length * factor];
        Complex phase = Complex.One, step = Complex.FromPolarCoordinates(1, 2 * Math.PI * 100000 / rate);
        long position = 0;
        int count;
        while ((count = reader.Read(input)) > 0)
        {
            for (int i = 0; i < count * factor; i++)
            {
                var value = input[i / factor] * (.25 + .25 * phase);
                wide[i] = new((float)value.Real, (float)value.Imaginary);
                phase *= step;
            }
            phase /= phase.Magnitude;
            processor.Process(wide.AsSpan(0, count * factor), position, false);
            position += count * factor;
        }
        processor.Complete(false);
        File.WriteAllText(Path.Combine(Output, "channelizer.txt"), processor.Description + "\n" + string.Join("\n", frames.Select((count, i) => $"Channel {i + 1}: {count} frames")));
        Check(frames.All(count => count == 216), "Two-channel frame counts: " + string.Join(", ", frames));
        Check(metrics.ChannelizerTicks > 0 && metrics.DecoderTicks > 0, "Separate library timings were not collected.");
        return Task.CompletedTask;
    }

    private static async Task Overload()
    {
        using var processor = new SlowProcessor();
        var metrics = new PipelineMetrics();
        await using var pipeline = new CapturePipeline(new BurstSource(1500), processor, metrics, 1024, 2);
        pipeline.Start();
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = pipeline.Snapshot;
        Check(pipeline.Failure is null, pipeline.Failure?.ToString() ?? "");
        Check(snapshot.Dropped > 0, "Overload did not drop any samples.");
        Check(snapshot.Received == snapshot.Processed + snapshot.Dropped, "IQ sample accounting is not exact.");
        Check(processor.CorruptBlocks == 0, "Producer overwrote a consumer-owned buffer.");
        Check(snapshot.QueuedBlocks == 0, "Queue was not drained.");
        Check(snapshot.PeakQueuedBlocks is > 0 and <= 2 && snapshot.MaxBlockMilliseconds > 0,
            "Queue pressure or processing stalls were not measured.");
        Console.WriteLine($"  overload: {snapshot.Dropped:N0}/{snapshot.Received:N0} samples dropped");
    }

    private static async Task HardwareGaps()
    {
        using var handshake = new GapHandshake();
        using var processor = new GapProcessor(handshake);
        var metrics = new PipelineMetrics();
        await using var pipeline = new CapturePipeline(new GapSource(handshake), processor, metrics, 1024, 1);
        pipeline.Start();
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Check(pipeline.Failure is null, pipeline.Failure?.ToString() ?? "");
        Check(metrics.Overflows == 1 && processor.HardwareGaps > 0, "Hardware gap marker was lost when IQ blocks were dropped.");
        Check(processor.IndexGaps > 0, "Sample indices did not expose queue loss.");
    }

    private static Task DecoderGaps()
    {
        var events = new List<CapturedEvent>();
        bool active = false;
        var metrics = new PipelineMetrics();
        using var processor = new DmrProcessor(FixtureSettings, [new(1, 434_987_500)], metrics, ev =>
        {
            events.Add(ev);
            if (ev.Event.Type == "call_start") Volatile.Write(ref active, true);
            if (ev.Event.Type == "call_end") Volatile.Write(ref active, false);
        }, "gaps");
        using var reader = new IqReader(fixture);
        var buffer = new Complex[1024];
        var single = new ComplexF[1024];
        long position = 0;
        while (!Volatile.Read(ref active))
        {
            int count = reader.Read(buffer);
            Check(count > 0, "No active DMR call found before the test gap.");
            for (int i = 0; i < count; i++) single[i] = new((float)buffer[i].Real, (float)buffer[i].Imaginary);
            processor.Process(single.AsSpan(0, count), position, false);
            position += count;
        }
        processor.Process(single, position + 4096, false);
        processor.Complete(false);
        Check(metrics.Discontinuities == 1, "A gap did not reset the decoder.");
        Check(metrics.ResetTicks > 0 && metrics.DecoderResetTicks > 0, "Decoder gap recovery was omitted from the timing breakdown.");
        Check(Math.Abs(metrics.Snapshot().DecoderInputSeconds - (position + single.Length) / FixtureSettings.SampleRate) < 1e-5,
            "Per-channel real-time accounting included missing samples.");
        Check(events.Any(e => e.Event.Type == "sync_lost" && Convert.ToInt64(e.Event.Data.GetValueOrDefault("missing_samples")) == 4096), "Gap length did not reach DMR.");
        Check(events.Any(e => e.Event.Type == "call_end" && e.Event.Data.GetValueOrDefault("reason")?.ToString() == "discontinuity"), "Active call was joined across a gap.");
        return Task.CompletedTask;
    }

    private static async Task Fault()
    {
        using var processor = new SlowProcessor(throwOnProcess: true);
        await using var pipeline = new CapturePipeline(new BurstSource(10000, paced: true), processor, new(), 1024, 2);
        pipeline.Start();
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(pipeline.Failure?.Message == "Injected decoder failure", "Decoder error was not surfaced.");
        Check(pipeline.Snapshot.QueuedBlocks == 0, "Failure left pooled IQ buffers queued.");
    }

    private static async Task Cancellation()
    {
        using var processor = new SlowProcessor();
        await using var pipeline = new CapturePipeline(new WaitingSource(), processor, new(), 1024, 2);
        pipeline.Start();
        await Task.Delay(30);
        await pipeline.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(pipeline.Failure is null, "Normal cancellation was reported as an error.");
    }

    private static async Task Reconfiguration()
    {
        await using var store = new SessionStore(Path.Combine(Output, "reconfigure.db"));
        await using var radio = new RadioController(store);
        var settings = new RadioSettings { Source = SourceKind.Synthetic, SampleRate = 96000,
            Processing = ProcessingMode.Direct, BlockSize = 2048 };
        await radio.StartAsync(settings, []);
        await Task.Delay(75);
        await radio.SetChannelsAsync([new(1, settings.CenterFrequencyHz)]);
        Check(radio.IsRunning && radio.Channels.Count == 1, "Adding a live channel did not restart capture.");
        await Task.Delay(75);
        await radio.SetChannelsAsync([new(1, settings.CenterFrequencyHz + 12500), new(2, settings.CenterFrequencyHz - 12500)]);
        await Task.Delay(75);
        await radio.SetChannelsAsync([]);
        Check(radio.IsRunning && radio.Channels.Count == 0, "Removing the last channel did not keep spectrum capture running.");
        await radio.StopAsync();
        Check(!radio.IsRunning && radio.IsConnected, "Stop did not leave the source available for restart.");
        await radio.StartAsync(settings, [new(3, settings.CenterFrequencyHz)]);
        await Task.Delay(75);
        await radio.DisconnectAsync();
        Check(!radio.IsConnected && !radio.IsRunning, "Disconnect did not release the source.");
    }

    private static async Task NullIdentity()
    {
        await using var store = new SessionStore(Path.Combine(Output, "identity.db"));
        int sequence = 0;
        void Emit(string type, Dictionary<string, object?> data) => store.Record(new("identity", new(1, 435e6), DateTimeOffset.UtcNow,
            new DmrEvent(1, ++sequence, type, "identity/ch1", sequence * 48, sequence / 1000.0, 0, null, "identity/ch1:1", data)));
        Emit("call_start", new());
        Emit("call_update", new() { ["source_id"] = 123, ["destination_id"] = 456, ["privacy_status"] = "not_indicated" });
        Emit("call_update", new() { ["source_id"] = null, ["destination_id"] = null, ["privacy_status"] = "unknown_vendor", ["raw_lc_hex"] = "0068400044C000418C" });
        Emit("call_end", new() { ["reason"] = "terminator", ["frames"] = 216 });
        await store.FlushAsync();
        var row = (await store.ReadRecentAsync()).Single();
        Check(row.SourceId is null && row.DestinationId is null && row.Frames == 216 && row.Privacy == "unknown_vendor",
            "Unknown vendor update retained stale trusted IDs.");
    }

    private static Task InvalidConfiguration()
    {
        bool failed = false;
        try { new ListenChannel(1, 500e6).Validate(new()); } catch (ArgumentException) { failed = true; }
        Check(failed, "An out-of-band channel was accepted.");
        failed = false;
        try
        {
            using var processor = new DmrProcessor(new RadioSettings { Processing = ProcessingMode.Direct }, [new(1, 435e6)], new(), _ => { }, "bad");
        }
        catch (InvalidOperationException) { failed = true; }
        Check(failed, "Direct mode accepted a wideband stream.");
        Check(System.Text.Json.JsonSerializer.Deserialize<RadioSettings>("{}")!.WireFormat == "sc16",
            "Existing settings must retain 16-bit transport.");
        (new RadioSettings { WireFormat = "sc8" }).Validate();
        failed = false;
        try { (new RadioSettings { WireFormat = "invalid" }).Validate(); }
        catch (ArgumentException) { failed = true; }
        Check(failed, "An unsupported USRP wire format was accepted.");
        return Task.CompletedTask;
    }

    private static CapturedEvent DatabaseEvent(int sequence) => new("database-test", new(1, 435e6), DateTimeOffset.UtcNow,
        new DmrEvent(1, sequence, "control", "database-test/ch1", sequence, sequence / 48000.0, 0, null, null,
            new Dictionary<string, object?> { ["raw_hex"] = "00" }));

    private static async Task DatabaseBackpressure()
    {
        string path = Path.Combine(Output, "backpressure.db");
        await using var store = new SessionStore(path, capacity: 1);
        using var blocker = new SqliteConnection($"Data Source={path}");
        blocker.Open();
        using var transaction = blocker.BeginTransaction();
        bool rejected = false;
        for (int i = 0; i < 20; i++)
        {
            try { store.Record(DatabaseEvent(i)); }
            catch (IOException error) when (error.Message.Contains("queue is full")) { rejected = true; break; }
        }
        Check(rejected && store.Pending <= 1, "Database overload was silently dropped or buffered without a bound.");
        transaction.Rollback();
        await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(store.Written > 0, "Accepted database events did not drain after the lock was released.");
    }

    private static async Task DatabaseFailure()
    {
        string path = Path.Combine(Output, "failure.db");
        var store = new SessionStore(path);
        using (var editor = new SqliteConnection($"Data Source={path}"))
        {
            editor.Open();
            using var command = editor.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_writes BEFORE INSERT ON events BEGIN SELECT RAISE(FAIL, 'Injected storage failure'); END";
            command.ExecuteNonQuery();
        }
        store.Record(DatabaseEvent(1));
        bool failed = false;
        try { await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is not TimeoutException) { failed = true; }
        Check(failed && store.Failure is not null, "Writer failure did not unblock a pending flush.");
        Check(store.Written == 0, "An uncommitted transaction was counted as saved.");
        try { await store.DisposeAsync(); }
        catch (IOException) { }
    }

    private static async Task Recovery()
    {
        string path = Path.Combine(Output, "recovery.db");
        await using (var store = new SessionStore(path))
        {
            bool refused = false;
            try { await using var other = new SessionStore(path); }
            catch (IOException) { refused = true; }
            Check(refused, "Two application writers acquired the same database.");
            store.Record(DatabaseEvent(1) with { Event = DatabaseEvent(1).Event with { Type = "call_start", SessionId = "unfinished" } });
            await store.FlushAsync();
            Check((await store.ReadRecentAsync()).Single().State == "Active", "The test session was not active.");
        }
        await using var reopened = new SessionStore(path);
        var row = (await reopened.ReadRecentAsync()).Single();
        Check(row.State == "Interrupted" && row.EndReason == "application_restart", "A stale active call survived restart.");
    }

    private sealed class BurstSource(int blocks, bool hardwareGap = false, bool paced = false) : IIqSource
    {
        private int block;
        public SourceInfo Info { get; } = new(1e6, 435e6, "Test burst source");
        public void Start() { }
        public SourceRead Read(Span<ComplexF> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block >= blocks) return new(0, EndOfStream: true);
            if (paced) Thread.Sleep(1);
            destination.Fill(new(block, -block));
            block++;
            return new(destination.Length, Discontinuity: hardwareGap && block == 7, Overflow: hardwareGap && block == 7);
        }
        public void Stop() { }
        public void Dispose() { }
    }
    private sealed class WaitingSource : IIqSource
    {
        public SourceInfo Info { get; } = new(96000, 435e6, "Waiting test source");
        public void Start() { }
        public SourceRead Read(Span<ComplexF> destination, CancellationToken cancellationToken)
        {
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            return new(0);
        }
        public void Stop() { }
        public void Dispose() { }
    }
    private sealed class GapHandshake : IDisposable
    {
        public readonly ManualResetEventSlim FirstEntered = new(), ReleaseFirst = new(), SecondProcessed = new();
        public void Dispose() { FirstEntered.Dispose(); ReleaseFirst.Dispose(); SecondProcessed.Dispose(); }
    }
    private sealed class GapSource(GapHandshake handshake) : IIqSource
    {
        private int block;
        public SourceInfo Info { get; } = new(1e6, 435e6, "Controlled gap");
        public void Start() { }
        public SourceRead Read(Span<ComplexF> destination, CancellationToken cancellationToken)
        {
            if (block == 4) return new(0, EndOfStream: true);
            if (block == 1) handshake.FirstEntered.Wait(cancellationToken);
            if (block == 3)
            {
                handshake.ReleaseFirst.Set();
                handshake.SecondProcessed.Wait(cancellationToken);
            }
            destination.Fill(new(block, -block));
            block++;
            return new(destination.Length, Discontinuity: block == 3, Overflow: block == 3);
        }
        public void Stop() { }
        public void Dispose() { }
    }
    private sealed class GapProcessor(GapHandshake handshake) : IIqProcessor
    {
        private int blocks;
        private long expected;
        public int HardwareGaps, IndexGaps;
        public string Description => "Controlled gap processor";
        public void Process(ReadOnlySpan<ComplexF> input, long firstSample, bool discontinuity)
        {
            if (discontinuity) HardwareGaps++;
            if (firstSample != expected) IndexGaps++;
            expected = firstSample + input.Length;
            if (++blocks == 1)
            {
                handshake.FirstEntered.Set();
                Check(handshake.ReleaseFirst.Wait(TimeSpan.FromSeconds(3)), "Producer did not signal gap delivery.");
            }
            if (blocks == 2) handshake.SecondProcessed.Set();
        }
        public void Complete(bool interrupted) { }
        public void Dispose() { }
    }
    private sealed class SlowProcessor(bool throwOnProcess = false) : IIqProcessor
    {
        private long expected;
        public int CorruptBlocks, HardwareGaps, IndexGaps;
        public string Description => "Slow test processor";
        public void Process(ReadOnlySpan<ComplexF> input, long firstSample, bool discontinuity)
        {
            if (throwOnProcess) throw new InvalidOperationException("Injected decoder failure");
            if (discontinuity) HardwareGaps++;
            if (firstSample != expected) IndexGaps++;
            expected = firstSample + input.Length;
            if (input.IsEmpty) return;
            float value = input[0].Real;
            Thread.Sleep(5);
            foreach (var sample in input) if (sample.Real != value || sample.Imaginary != -value) CorruptBlocks++;
        }
        public void Complete(bool interrupted) { }
        public void Dispose() { }
    }
}
