using IqChannelizer.Abstractions;

namespace BadSdr.Core.Sources;

/// <summary>Continuous USRP input with bounded recovery of a stalled UHD stream.</summary>
public sealed class UsrpSource : IIqSource
{
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(1);
    internal const int MaximumRestarts = 3;
    private readonly IIqSource transport;
    private readonly TimeProvider clock;
    private long lastSamplesAt, healthySamples;
    private int restartAttempts;
    private bool started, gapPending;
    public SourceInfo Info => transport.Info;

    public UsrpSource(RadioSettings settings) : this(new UhdSource(settings), TimeProvider.System) { }

    internal UsrpSource(IIqSource transport, TimeProvider clock)
    {
        this.transport = transport;
        this.clock = clock;
    }

    public static IReadOnlyList<string> Discover(string args = "") => UhdSource.Discover(args);

    public void Start()
    {
        transport.Start();
        started = true;
        gapPending = false;
        restartAttempts = 0;
        healthySamples = 0;
        lastSamplesAt = clock.GetTimestamp();
    }

    public SourceRead Read(Span<ComplexF> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!started) throw new InvalidOperationException("USRP reception has not been started.");
        var read = transport.Read(destination, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        long now = clock.GetTimestamp();
        if (read.Count > 0)
        {
            lastSamplesAt = now;
            // A few buffered samples do not prove recovery. Require one RF second of
            // clean input before allowing another full set of restart attempts.
            if (read.Overflow || read.Discontinuity) healthySamples = 0;
            else if (restartAttempts > 0)
            {
                healthySamples += read.Count;
                if (healthySamples >= Info.SampleRate) restartAttempts = 0;
            }
            if (gapPending) { read = read with { Discontinuity = true }; gapPending = false; }
        }
        else
        {
            healthySamples = 0;
            if (clock.GetElapsedTime(lastSamplesAt, now) >= StallTimeout)
            {
                if (restartAttempts >= MaximumRestarts)
                    throw new IOException($"UHD stopped delivering IQ samples after {MaximumRestarts} RX restart attempts. " +
                        "Check the USRP connection and UHD runtime, then reconnect.");

                // UHD 4.1/B210 can stop indefinitely after an overflow (Ettus #475).
                // Allow normal driver recovery first. Only the acquisition thread
                // stops, drains and restarts the transport, without rebuilding DSP.
                transport.Stop();
                started = false;
                cancellationToken.ThrowIfCancellationRequested();
                transport.Start();
                started = true;
                restartAttempts++;
                lastSamplesAt = clock.GetTimestamp();
                gapPending = true;
                read = read with { Discontinuity = true, RxRestarted = true };
            }
        }
        return read;
    }

    public void Stop()
    {
        if (!started) return;
        try { transport.Stop(); }
        finally { started = false; }
    }

    public void Dispose()
    {
        try { Stop(); }
        finally { transport.Dispose(); }
    }
}
