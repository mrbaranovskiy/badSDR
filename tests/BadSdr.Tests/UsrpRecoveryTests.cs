using System.Collections.Concurrent;
using BadSdr.Core;
using BadSdr.Core.Dsp;
using BadSdr.Core.Pipeline;
using BadSdr.Core.Sources;
using Dmr;
using IqChannelizer.Abstractions;

namespace BadSdr.Tests;

internal static partial class Program
{
    private static Task UsrpTransientOverflow()
    {
        var clock = new RxTestClock();
        var transport = new ScriptedRx(clock,
            (new(128), 0), (new(0, Overflow: true, Discontinuity: true), .1),
            (new(0, Timeout: true), .1), (new(128, Discontinuity: true), .1), (new(128), .1));
        using var source = new UsrpSource(transport, clock);
        source.Start();
        var buffer = new ComplexF[2048];
        for (int i = 0; i < 5; i++)
            Check(!source.Read(buffer, default).RxRestarted, "An overflow that recovered itself restarted RX.");
        Check(transport.Starts == 1 && transport.Stops == 0, "Normal overflow recovery issued stream commands.");
        return Task.CompletedTask;
    }

    private static Task UsrpStalledOverflow()
    {
        var clock = new RxTestClock();
        var transport = new ScriptedRx(clock, (new(128), 0), (new(0, Overflow: true), .1),
            (new(0, Timeout: true), .4), (new(0, Timeout: true), .5),
            (new(0, Timeout: true), .1), (new(128), 0), (new(128), 0));
        using var source = new UsrpSource(transport, clock);
        source.Start();
        var buffer = new ComplexF[2048];
        source.Read(buffer, default);
        source.Read(buffer, default);
        Check(!source.Read(buffer, default).RxRestarted, "Restarted before the idle deadline.");
        var restart = source.Read(buffer, default);
        Check(restart is { Count: 0, Discontinuity: true, Timeout: true, RxRestarted: true }, "Restart lost timeout/gap accounting.");
        Check(transport.Starts == 2 && transport.Stops == 1, "Stalled RX was not stopped and restarted once.");
        source.Read(buffer, default);
        Check(source.Read(buffer, default) is { Count: 128, Discontinuity: true }, "First post-restart IQ lost its gap marker.");
        Check(!source.Read(buffer, default).Discontinuity, "Recovery repeatedly reset healthy IQ.");
        return Task.CompletedTask;
    }

    private static async Task UsrpRetryLimit()
    {
        // Repeated overflow metadata and a few stale samples must not keep dead RX alive forever.
        var clock = new RxTestClock();
        var script = Enumerable.Range(0, 4).SelectMany(_ => new (SourceRead, double)[]
            { (new(0, Overflow: true), 1.1), (new(16), 0) }).ToArray();
        var transport = new ScriptedRx(clock, script);
        var metrics = new PipelineMetrics();
        await using var pipeline = new CapturePipeline(new UsrpSource(transport, clock), new SlowProcessor(), metrics, 2048, 8);
        pipeline.Start();
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(pipeline.Failure is IOException && pipeline.Failure.Message.Contains("3 RX restart attempts"),
            "Unrecoverable RX did not stop capture with an actionable error.");
        Check(metrics.RxRestarts == 3 && transport.Starts == 4 && transport.Stops == 4,
            "RX exceeded its retry budget or did not stop after failing.");
        Check(pipeline.Snapshot.QueuedBlocks == 0 && metrics.Received == metrics.Processed + metrics.Dropped,
            "RX failure did not drain accepted IQ buffers.");
    }

    private static Task UsrpRecoveryBudget()
    {
        var clock = new RxTestClock();
        var script = Enumerable.Range(0, 5).SelectMany(_ => new (SourceRead, double)[]
            { (new(0, Timeout: true), 1.1), (new(1000), .1) }).ToArray();
        var transport = new ScriptedRx(clock, script);
        using var source = new UsrpSource(transport, clock);
        source.Start();
        var buffer = new ComplexF[2048];
        for (int i = 0; i < 5; i++)
        {
            Check(source.Read(buffer, default).RxRestarted, "A later independent stall could not recover.");
            Check(source.Read(buffer, default).Count == 1000, "Recovered IQ was not delivered.");
        }
        Check(transport.Starts == 6, "Sustained successful RX did not renew the retry budget.");
        return Task.CompletedTask;
    }

    private static Task UsrpRecoveryCancellation()
    {
        var clock = new RxTestClock();
        using var cancellation = new CancellationTokenSource();
        var transport = new ScriptedRx(clock, (new(0, Timeout: true), 1.1)) { OnStop = cancellation.Cancel };
        using var source = new UsrpSource(transport, clock);
        source.Start();
        bool cancelled = false;
        try { source.Read(new ComplexF[128], cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && transport.Starts == 1 && transport.Stops == 1, "Stopping during recovery restarted the device.");
        return Task.CompletedTask;
    }

    private static async Task UsrpRecoveredDmr()
    {
        var samples = ReadFixture();
        var expected = ReferenceFrames(samples);
        var iq = samples.Select(v => new ComplexF((float)v.Real, (float)v.Imaginary)).ToArray();
        var clock = new RxTestClock();
        var transport = new StalledFixtureRx(clock, iq);
        var metrics = new PipelineMetrics();
        ConcurrentQueue<DmrEvent>[] events = [new(), new()];
        using var processor = new DmrProcessor(FixtureSettings, [new(1, 434987500), new(2, 434987500)],
            metrics, ev => events[ev.Channel.Id - 1].Enqueue(ev.Event), "rx-recovery");
        await using var pipeline = new CapturePipeline(new UsrpSource(transport, clock), processor, metrics, 16384, 128);
        pipeline.Start();
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Check(pipeline.Failure is null, pipeline.Failure?.ToString() ?? "");
        Check(metrics.RxRestarts == 1 && metrics.Overflows == 1 && metrics.Discontinuities == 1,
            "Recovered RX did not propagate one processing gap.");
        Check(metrics.Dropped == 0 && metrics.Processed == 2L * iq.Length, "Recovered IQ was lost or duplicated.");
        foreach (var channel in events)
        {
            var before = Frames(channel.Where(e => e.RfSampleIndex < iq.Length));
            var after = Frames(channel.Where(e => e.RfSampleIndex >= iq.Length), iq.Length);
            Check(before.SequenceEqual(expected) && after.SequenceEqual(expected),
                "DMR payloads or sample order changed across an RX restart.");
        }
    }

    private sealed class RxTestClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(double seconds) => timestamp += (long)(seconds * TimestampFrequency);
    }

    private sealed class ScriptedRx(RxTestClock clock, params (SourceRead Read, double Seconds)[] script) : IIqSource
    {
        private int index;
        public int Starts, Stops;
        public Action? OnStop;
        public SourceInfo Info { get; } = new(1000, 435e6, "Scripted UHD reads");
        public void Start() => Starts++;
        public SourceRead Read(Span<ComplexF> destination, CancellationToken cancellationToken)
        {
            var step = script[index++];
            clock.Advance(step.Seconds);
            destination[..step.Read.Count].Clear();
            return step.Read;
        }
        public void Stop() { Stops++; OnStop?.Invoke(); }
        public void Dispose() { }
    }

    private sealed class StalledFixtureRx(RxTestClock clock, ComplexF[] iq) : IIqSource
    {
        private int position, starts;
        private bool overflow;
        public SourceInfo Info { get; } = new(FixtureSettings.SampleRate, FixtureSettings.CenterFrequencyHz, "Fixture with stalled UHD");
        public void Start() => starts++;
        public SourceRead Read(Span<ComplexF> destination, CancellationToken cancellationToken)
        {
            if (position == iq.Length && starts == 1)
            {
                clock.Advance(.1);
                bool first = !overflow;
                overflow = true;
                return new(0, Discontinuity: first, Overflow: first, Timeout: !first);
            }
            if (position == 2 * iq.Length) return new(0, EndOfStream: true);
            int offset = position % iq.Length;
            int count = Math.Min(destination.Length, iq.Length - offset);
            iq.AsSpan(offset, count).CopyTo(destination);
            position += count;
            return new(count);
        }
        public void Stop() { }
        public void Dispose() { }
    }
}
