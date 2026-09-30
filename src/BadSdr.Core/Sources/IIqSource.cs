using IqChannelizer.Abstractions;

namespace BadSdr.Core.Sources;

// The producer exclusively owns Start/Read/Stop. Cancellation must interrupt reads promptly.
public interface IIqSource : IDisposable
{
    SourceInfo Info { get; }
    void Start();
    SourceRead Read(Span<ComplexF> destination, CancellationToken cancellationToken);
    void Stop();
}

public readonly record struct SourceRead(int Count, bool EndOfStream = false,
    bool Discontinuity = false, bool Overflow = false, bool Timeout = false, bool RxRestarted = false);

public static class SourceFactory
{
    public static IIqSource Open(RadioSettings settings) => settings.Source switch
    {
        SourceKind.Usrp => new UsrpSource(settings),
        SourceKind.Replay => new ReplaySource(settings),
        SourceKind.Synthetic => new SyntheticSource(settings),
        _ => throw new ArgumentOutOfRangeException(nameof(settings))
    };
}
