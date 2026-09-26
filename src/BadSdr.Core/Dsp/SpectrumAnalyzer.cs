using System.Diagnostics;
using System.Numerics;
using IqChannelizer.Abstractions;

namespace BadSdr.Core.Dsp;

public sealed record SpectrumFrame(float[] PowerDb, double CenterFrequencyHz, double SampleRate);

/// <summary>A small, lossy mailbox keeps display FFTs off the acquisition and decoder threads.</summary>
public sealed class SpectrumAnalyzer : IDisposable
{
    public const int Size = 2048;
    private readonly object gate = new();
    private readonly ComplexF[] mailbox = new ComplexF[Size];
    private readonly AutoResetEvent ready = new(false);
    private readonly Task worker;
    private readonly SourceInfo info;
    private int filled;
    private long nextFrame;
    private volatile bool stopping;
    private SpectrumFrame? latest;
    public SpectrumFrame? Latest => Volatile.Read(ref latest);

    public SpectrumAnalyzer(SourceInfo info)
    {
        this.info = info;
        worker = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Offer(ReadOnlySpan<ComplexF> input)
    {
        if (Stopwatch.GetTimestamp() < nextFrame || !Monitor.TryEnter(gate)) return;
        try
        {
            int count = Math.Min(input.Length, Size - filled);
            input[..count].CopyTo(mailbox.AsSpan(filled));
            filled += count;
            if (filled == Size)
            {
                nextFrame = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 12;
                ready.Set();
            }
        }
        finally { Monitor.Exit(gate); }
    }

    private void Work()
    {
        var fft = new Complex[Size];
        var window = Enumerable.Range(0, Size).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / (Size - 1))).ToArray();
        double scale = window.Sum();
        while (true)
        {
            ready.WaitOne();
            if (stopping) return;
            lock (gate)
            {
                if (filled != Size) continue;
                for (int i = 0; i < Size; i++) fft[i] = new Complex(mailbox[i].Real, mailbox[i].Imaginary) * window[i];
                filled = 0;
            }
            Transform(fft);
            var db = new float[Size];
            for (int i = 0; i < Size; i++)
            {
                var value = fft[(i + Size / 2) % Size] / scale;
                db[i] = (float)(10 * Math.Log10(Math.Max(1e-14, value.Real * value.Real + value.Imaginary * value.Imaginary)));
            }
            Volatile.Write(ref latest, new(db, info.CenterFrequencyHz, info.SampleRate));
        }
    }

    public static void Transform(Span<Complex> values)
    {
        int n = values.Length;
        if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two.");
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < n; start += length)
            {
                var phase = Complex.One;
                for (int j = 0; j < length / 2; j++)
                {
                    var even = values[start + j];
                    var odd = values[start + j + length / 2] * phase;
                    values[start + j] = even + odd;
                    values[start + j + length / 2] = even - odd;
                    phase *= step;
                }
            }
        }
    }

    public void Dispose()
    {
        stopping = true;
        ready.Set();
        worker.GetAwaiter().GetResult();
        ready.Dispose();
    }
}
