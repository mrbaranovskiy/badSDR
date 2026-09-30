using System.Collections.Concurrent;
using System.Numerics;
using BadSdr.Core;
using BadSdr.Core.Dsp;
using BadSdr.Core.Pipeline;
using BadSdr.Core.Sources;
using Dmr;
using Dmr.Input;
using IqChannelizer.Abstractions;

namespace BadSdr.Tests;

internal static partial class Program
{
    private static (long Sample, string? Payload)[] Frames(IEnumerable<DmrEvent> events, long origin = 0) => events
        .Where(e => e.Type == "vocoder_frame")
        .Select(e => (e.RfSampleIndex - origin, e.Data.GetValueOrDefault("payload_hex")?.ToString())).ToArray();

    private static Complex[] ReadFixture()
    {
        using var reader = new IqReader(fixture);
        var samples = new Complex[checked((int)reader.Samples)];
        int offset = 0, count;
        while ((count = reader.Read(samples.AsSpan(offset))) > 0) offset += count;
        Check(offset == samples.Length, "Incomplete test fixture.");
        return samples;
    }

    private static (long Sample, string? Payload)[] ReferenceFrames(Complex[] samples)
    {
        var events = new List<DmrEvent>();
        var receiver = new DmrReceiver(new(FixtureSettings.SampleRate, -12500), events.Add);
        receiver.Push(samples);
        receiver.Complete();
        return Frames(events);
    }

    private static async Task ParallelWorkers()
    {
        var expected = ReferenceFrames(ReadFixture());
        ConcurrentQueue<DmrEvent>[] events = [new(), new()];
        var threads = new ConcurrentDictionary<int, int>();
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var metrics = new PipelineMetrics();
        using var processor = new DmrProcessor(FixtureSettings, [new(1, 434_987_500), new(2, 434_987_500)], metrics, ev =>
        {
            events[ev.Channel.Id - 1].Enqueue(ev.Event);
            if (ev.Event.Type == "burst" && threads.TryAdd(ev.Channel.Id, Environment.CurrentManagedThreadId))
            {
                entered.Signal();
                Check(release.Wait(TimeSpan.FromSeconds(5)), "Parallel receiver handoff timed out.");
            }
        }, "parallel");
        int dispatcher = 0;
        var producer = Task.Run(() =>
        {
            dispatcher = Environment.CurrentManagedThreadId;
            ReplayInto(processor, poisonAfterCopy: true);
            processor.Complete(false);
        });
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "Two receivers did not decode simultaneously.");
            Check(threads.Values.Distinct().Count() == 2 && !threads.Values.Contains(dispatcher),
                "Receivers shared a thread or ran on the channelizer dispatcher.");
            await Task.Delay(50);
            Check(!producer.IsCompleted && metrics.DecoderPeakQueuedBatches == 4,
                "A stalled receiver did not apply bounded backpressure.");
            Check(metrics.PeakActiveDecoderWorkers == 2, "Concurrent decoder work was not measured.");
        }
        finally { release.Set(); await producer.WaitAsync(TimeSpan.FromSeconds(10)); }
        Check(events.All(e => Frames(e).SequenceEqual(expected)), "Parallel decoding changed payloads or sample order.");
        Check(metrics.ActiveDecoderWorkers == 0, "Completion returned with decoder work still running.");
    }

    private static Task ParallelGaps()
    {
        var iq = ReadFixture();
        var expected = ReferenceFrames(iq);
        var single = iq.Select(s => new ComplexF((float)s.Real, (float)s.Imaginary)).ToArray();
        const int count = 4;
        int firstLength = single.Length / 2 + 13;
        var firstExpected = ReferenceFrames(iq[..firstLength]);
        long secondOrigin = firstLength + 4096;
        var events = Enumerable.Range(0, count).Select(_ => new ConcurrentQueue<DmrEvent>()).ToArray();
        var metrics = new PipelineMetrics();
        using var processor = new DmrProcessor(FixtureSettings,
            Enumerable.Range(1, count).Select(id => new ListenChannel(id, 434_987_500)).ToArray(), metrics,
            ev => events[ev.Channel.Id - 1].Enqueue(ev.Event), "parallel-gaps");
        processor.Process(single.AsSpan(0, firstLength), 0, false);
        processor.Process(single, secondOrigin, false);
        processor.Complete(true);
        foreach (var channel in events)
        {
            var actual = Frames(channel);
            Check(actual.Take(firstExpected.Length).SequenceEqual(firstExpected) &&
                actual.Skip(firstExpected.Length).Select(f => (f.Sample - secondOrigin, f.Payload)).SequenceEqual(expected),
                "A receiver crossed a gap, reordered samples, or lost its final partial batch.");
            Check(channel.Any(e => e.Type == "sync_lost" && Convert.ToInt64(e.Data.GetValueOrDefault("missing_samples")) == 4096),
                "A worker lost the gap marker.");
            Check(channel.Any(e => e.Type == "call_end" && Equals(e.Data.GetValueOrDefault("reason"), "discontinuity") &&
                e.RfSampleIndex == firstLength), "A worker failed to close its active session before the gap.");
            var sequence = channel.Select(e => e.Sequence).ToArray();
            Check(sequence.SequenceEqual(sequence.Order()), "Events within a receiver were reordered.");
        }
        Check(Math.Abs(metrics.Snapshot().DecoderInputSeconds - count * (firstLength + single.Length) / FixtureSettings.SampleRate) < 1e-5,
            "Parallel sample accounting included gaps or lost the final batch.");
        return Task.CompletedTask;
    }

    private static async Task ParallelFault()
    {
        var first = ReadFixture().Select(s => new ComplexF((float)s.Real, (float)s.Imaginary)).ToArray();
        var metrics = new PipelineMetrics();
        var processor = new DmrProcessor(FixtureSettings, [new(1, 434_987_500), new(2, 434_987_500)], metrics, ev =>
        {
            if (ev.Channel.Id == 1 && ev.Event.Type == "burst") throw new IOException("Injected asynchronous decoder failure");
        }, "parallel-failure");
        await using var pipeline = new CapturePipeline(new OneBlockThenWaitSource(first), processor, metrics, first.Length, 2);
        pipeline.Start();
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(pipeline.Failure?.Message == "Injected asynchronous decoder failure", "Worker error did not cancel acquisition with its original cause.");
        Check(metrics.ActiveDecoderWorkers == 0 && pipeline.Snapshot.QueuedBlocks == 0,
            "Faulted capture left decoder work or IQ buffers running.");
    }

    private sealed class OneBlockThenWaitSource(ComplexF[] first) : IIqSource
    {
        private bool sent;
        public SourceInfo Info { get; } = new(FixtureSettings.SampleRate, FixtureSettings.CenterFrequencyHz, "Single block then blocked RX");
        public void Start() { }
        public SourceRead Read(Span<ComplexF> destination, CancellationToken cancellationToken)
        {
            if (!sent) { first.CopyTo(destination); sent = true; return new(first.Length); }
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            return new(0);
        }
        public void Stop() { }
        public void Dispose() { }
    }
}
