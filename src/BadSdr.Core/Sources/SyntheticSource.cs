using System.Diagnostics;
using System.Numerics;
using IqChannelizer.Abstractions;

namespace BadSdr.Core.Sources;

/// <summary>Two continuous test carriers with noise. Does not invent DMR traffic.</summary>
public sealed class SyntheticSource(RadioSettings settings) : IIqSource
{
    private readonly Stopwatch clock = new();
    private readonly Random random = new(42);
    private long samples;
    private long startSamples;
    private Complex phaseA = Complex.One, phaseB = Complex.One;
    private readonly Complex stepA = Complex.FromPolarCoordinates(1, -2 * Math.PI * .125);
    private readonly Complex stepB = Complex.FromPolarCoordinates(1, 2 * Math.PI * .23);
    public SourceInfo Info { get; } = new(settings.SampleRate, settings.CenterFrequencyHz, "Synthetic · test carriers (no DMR)");
    public void Start() { startSamples = samples; clock.Restart(); }
    public SourceRead Read(Span<ComplexF> destination, CancellationToken cancellationToken)
    {
        var remaining = TimeSpan.FromSeconds((samples - startSamples) / Info.SampleRate) - clock.Elapsed;
        if (remaining > TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(remaining))
            cancellationToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        for (int i = 0; i < destination.Length; i++)
        {
            var value = .35 * phaseA + .13 * phaseB;
            destination[i] = new((float)(value.Real + .012 * (random.NextDouble() - .5)),
                (float)(value.Imaginary + .012 * (random.NextDouble() - .5)));
            phaseA *= stepA;
            phaseB *= stepB;
        }
        phaseA /= phaseA.Magnitude;
        phaseB /= phaseB.Magnitude;
        samples += destination.Length;
        return new(destination.Length);
    }
    public void Stop() => clock.Stop();
    public void Dispose() { }
}
