using System.Diagnostics;
using System.Numerics;
using Dmr.Input;
using IqChannelizer.Abstractions;

namespace BadSdr.Core.Sources;

public sealed class ReplaySource : IIqSource
{
    private readonly IqReader reader;
    private readonly Complex[] conversion = new Complex[16384];
    private readonly bool realtime;
    private readonly Stopwatch clock = new();
    private long samples;
    private long startSamples;
    public SourceInfo Info { get; }

    public ReplaySource(RadioSettings settings)
    {
        reader = new(settings.ReplayPath, settings.ReplayFormat,
            settings.ReplayFormat == "wav" ? null : checked((int)settings.SampleRate));
        Info = new(reader.SampleRate, settings.CenterFrequencyHz, $"Replay · {Path.GetFileName(settings.ReplayPath)}");
        realtime = settings.ReplayRealtime;
    }
    public void Start() { startSamples = samples; clock.Restart(); }
    public SourceRead Read(Span<ComplexF> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (realtime)
        {
            var remaining = TimeSpan.FromSeconds((samples - startSamples) / Info.SampleRate) - clock.Elapsed;
            if (remaining > TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(remaining))
                cancellationToken.ThrowIfCancellationRequested();
        }
        int count = reader.Read(conversion.AsSpan(0, Math.Min(conversion.Length, destination.Length)));
        for (int i = 0; i < count; i++) destination[i] = new((float)conversion[i].Real, (float)conversion[i].Imaginary);
        samples += count;
        return new(count, EndOfStream: count == 0);
    }
    public void Stop() => clock.Stop();
    public void Dispose() => reader.Dispose();
}
