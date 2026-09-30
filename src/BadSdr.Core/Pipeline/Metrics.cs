using System.Diagnostics;

namespace BadSdr.Core.Pipeline;

public sealed record MetricsSnapshot(long Received, long Processed, long Dropped, long Overflows,
    long Timeouts, long Discontinuities, long Events, long TailSamples, double ProcessingSeconds,
    double ChannelizerSeconds, double DecoderSeconds, double ElapsedSeconds, int QueuedBlocks)
{
    public double DropPercent => Received == 0 ? 0 : 100.0 * Dropped / Received;
    public double ProcessingSamplesPerSecond => Math.Max(ProcessingSeconds, BusiestDecoderSeconds) == 0 ? 0 :
        Processed / Math.Max(ProcessingSeconds, BusiestDecoderSeconds);
    public double ResetSeconds { get; init; }
    public long RxRestarts { get; init; }
    public double DecoderInputSeconds { get; init; }
    public double DecoderRealtimeMultiple => DecoderSeconds == 0 ? 0 : DecoderInputSeconds / DecoderSeconds;
    public double OtherProcessingSeconds => Math.Max(0, ProcessingSeconds - ChannelizerSeconds - DispatchSeconds - ResetSeconds - DecoderDrainSeconds);
    public double DispatchSeconds { get; init; }
    public double DecoderWaitSeconds { get; init; }
    public double DecoderDrainSeconds { get; init; }
    public double DecoderResetSeconds { get; init; }
    public double BusiestDecoderSeconds { get; init; }
    public int DecoderWorkers { get; init; }
    public int PeakActiveDecoderWorkers { get; init; }
    public int DecoderPeakQueuedBatches { get; init; }
    public double MaxBlockMilliseconds { get; init; }
    public int PeakQueuedBlocks { get; init; }
    public long AllocatedBytes { get; init; }
    public int Gen2Collections { get; init; }
}

public sealed class PipelineMetrics
{
    public long Received, Processed, Dropped, Overflows, Timeouts, Discontinuities, Events, TailSamples;
    public long RxRestarts;
    public long ProcessingTicks, ChannelizerTicks, DecoderTicks;
    public long ResetTicks, DecoderInputNanoseconds, MaxBlockTicks;
    public long DispatchTicks, DecoderWaitTicks, DecoderDrainTicks, DecoderResetTicks, BusiestDecoderTicks;
    public int DecoderWorkers, ActiveDecoderWorkers, PeakActiveDecoderWorkers;
    public long DecoderPeakQueuedBatches;
    public int PeakQueuedBlocks;
    private readonly Stopwatch clock = new();
    private long allocationStart;
    private int gen2Start;
    private long allocatedBytes;
    private int gen2Collections;
    public static void UpdateMaximum(ref long target, long value)
    {
        long previous = Volatile.Read(ref target);
        while (value > previous)
        {
            long actual = Interlocked.CompareExchange(ref target, value, previous);
            if (actual == previous) break;
            previous = actual;
        }
    }
    public static void UpdateMaximum(ref int target, int value)
    {
        int previous = Volatile.Read(ref target);
        while (value > previous)
        {
            int actual = Interlocked.CompareExchange(ref target, value, previous);
            if (actual == previous) break;
            previous = actual;
        }
    }
    public void Start()
    {
        allocationStart = GC.GetTotalAllocatedBytes();
        gen2Start = GC.CollectionCount(2);
        clock.Restart();
    }
    public void Stop()
    {
        allocatedBytes = GC.GetTotalAllocatedBytes() - allocationStart;
        gen2Collections = GC.CollectionCount(2) - gen2Start;
        clock.Stop();
    }
    public MetricsSnapshot Snapshot(int queuedBlocks = 0) => new(
        Interlocked.Read(ref Received), Interlocked.Read(ref Processed), Interlocked.Read(ref Dropped),
        Interlocked.Read(ref Overflows), Interlocked.Read(ref Timeouts), Interlocked.Read(ref Discontinuities),
        Interlocked.Read(ref Events), Interlocked.Read(ref TailSamples),
        Interlocked.Read(ref ProcessingTicks) / (double)Stopwatch.Frequency,
        Interlocked.Read(ref ChannelizerTicks) / (double)Stopwatch.Frequency,
        Interlocked.Read(ref DecoderTicks) / (double)Stopwatch.Frequency,
        clock.Elapsed.TotalSeconds, queuedBlocks)
    {
        ResetSeconds = Interlocked.Read(ref ResetTicks) / (double)Stopwatch.Frequency,
        RxRestarts = Interlocked.Read(ref RxRestarts),
        DispatchSeconds = Interlocked.Read(ref DispatchTicks) / (double)Stopwatch.Frequency,
        DecoderWaitSeconds = Interlocked.Read(ref DecoderWaitTicks) / (double)Stopwatch.Frequency,
        DecoderDrainSeconds = Interlocked.Read(ref DecoderDrainTicks) / (double)Stopwatch.Frequency,
        DecoderResetSeconds = Interlocked.Read(ref DecoderResetTicks) / (double)Stopwatch.Frequency,
        BusiestDecoderSeconds = Interlocked.Read(ref BusiestDecoderTicks) / (double)Stopwatch.Frequency,
        DecoderWorkers = DecoderWorkers,
        PeakActiveDecoderWorkers = Volatile.Read(ref PeakActiveDecoderWorkers),
        DecoderPeakQueuedBatches = (int)Interlocked.Read(ref DecoderPeakQueuedBatches),
        DecoderInputSeconds = Interlocked.Read(ref DecoderInputNanoseconds) / 1e9,
        MaxBlockMilliseconds = Interlocked.Read(ref MaxBlockTicks) * 1000.0 / Stopwatch.Frequency,
        PeakQueuedBlocks = Volatile.Read(ref PeakQueuedBlocks),
        AllocatedBytes = clock.IsRunning ? GC.GetTotalAllocatedBytes() - allocationStart : allocatedBytes,
        Gen2Collections = clock.IsRunning ? GC.CollectionCount(2) - gen2Start : gen2Collections
    };
}
