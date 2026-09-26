using System.Diagnostics;

namespace BadSdr.Core.Pipeline;

public sealed record MetricsSnapshot(long Received, long Processed, long Dropped, long Overflows,
    long Timeouts, long Discontinuities, long Events, long TailSamples, double ProcessingSeconds,
    double ChannelizerSeconds, double DecoderSeconds, double ElapsedSeconds, int QueuedBlocks)
{
    public double DropPercent => Received == 0 ? 0 : 100.0 * Dropped / Received;
    public double ProcessingSamplesPerSecond => ProcessingSeconds == 0 ? 0 : Processed / ProcessingSeconds;
}

public sealed class PipelineMetrics
{
    public long Received, Processed, Dropped, Overflows, Timeouts, Discontinuities, Events, TailSamples;
    public long ProcessingTicks, ChannelizerTicks, DecoderTicks;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public void Stop() => clock.Stop();
    public MetricsSnapshot Snapshot(int queuedBlocks = 0) => new(
        Interlocked.Read(ref Received), Interlocked.Read(ref Processed), Interlocked.Read(ref Dropped),
        Interlocked.Read(ref Overflows), Interlocked.Read(ref Timeouts), Interlocked.Read(ref Discontinuities),
        Interlocked.Read(ref Events), Interlocked.Read(ref TailSamples),
        Interlocked.Read(ref ProcessingTicks) / (double)Stopwatch.Frequency,
        Interlocked.Read(ref ChannelizerTicks) / (double)Stopwatch.Frequency,
        Interlocked.Read(ref DecoderTicks) / (double)Stopwatch.Frequency,
        clock.Elapsed.TotalSeconds, queuedBlocks);
}
