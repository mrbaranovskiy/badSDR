using IqChannelizer.Abstractions;

namespace BadSdr.Core;

public enum SourceKind { Usrp, Replay, Synthetic }
public enum ProcessingMode { Auto, Channelizer, Direct }

public sealed record RadioSettings
{
    public SourceKind Source { get; init; } = SourceKind.Usrp;
    public string DeviceArguments { get; init; } = "";
    public double CenterFrequencyHz { get; init; } = 435_000_000;
    public double SampleRate { get; init; } = 1_000_000;
    public double GainDb { get; init; } = 30;
    public double BandwidthHz { get; init; } = 1_000_000;
    public string Antenna { get; init; } = "RX2";
    public uint RxChannel { get; init; }
    public string ReplayPath { get; init; } = "";
    public string ReplayFormat { get; init; } = "wav";
    public bool ReplayRealtime { get; init; } = true;
    public int BlockSize { get; init; } = 32768;
    public int QueueCapacity { get; init; } = 8;
    public ProcessingMode Processing { get; init; } = ProcessingMode.Auto;
    public ChannelizerStrategy Strategy { get; init; } = ChannelizerStrategy.Auto;
    public ComputeBackendPreference Backend { get; init; } = ComputeBackendPreference.Cpu;

    public void Validate()
    {
        if (!double.IsFinite(CenterFrequencyHz) || CenterFrequencyHz <= 0)
            throw new ArgumentException("Centre frequency must be positive.");
        if (!double.IsFinite(SampleRate) || SampleRate < 24000 || SampleRate > 200_000_000)
            throw new ArgumentException("Sample rate must be between 24 kS/s and 200 MS/s.");
        if (!double.IsFinite(GainDb) || !double.IsFinite(BandwidthHz) || BandwidthHz <= 0)
            throw new ArgumentException("Gain and bandwidth must be finite; bandwidth must be positive.");
        if (BlockSize is < 1024 or > 1_048_576 || QueueCapacity is < 1 or > 256)
            throw new ArgumentException("Use blocks of 1,024–1,048,576 samples and a queue of 1–256 blocks.");
        if ((long)BlockSize * QueueCapacity > 16_777_216)
            throw new ArgumentException("The IQ queue may hold at most 128 MiB of sample data.");
    }
}

public sealed record ListenChannel(int Id, double FrequencyHz, string Name = "DMR")
{
    public const double BandwidthHz = 12_500;
    public string Label => $"{Name} {Id:00} · {FrequencyHz / 1e6:F6} MHz";

    public void Validate(RadioSettings settings)
    {
        if (Id < 1 || !double.IsFinite(FrequencyHz) ||
            Math.Abs(FrequencyHz - settings.CenterFrequencyHz) + 6500 >= settings.SampleRate / 2)
            throw new ArgumentException($"Channel {Id} must fit inside the sampled band (including its 6.5 kHz half-passband).");
    }
}

public sealed record SourceInfo(double SampleRate, double CenterFrequencyHz, string Description);
