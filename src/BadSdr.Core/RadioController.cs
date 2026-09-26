using System.Text.Json;
using BadSdr.Core.Dsp;
using BadSdr.Core.Pipeline;
using BadSdr.Core.Sources;
using BadSdr.Core.Storage;

namespace BadSdr.Core;

/// <summary>Serializes device lifetime and processing rebuilds. The UI never mutates a live decoder.</summary>
public sealed class RadioController(SessionStore store) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IIqSource? source;
    private CapturePipeline? pipeline;
    private string? runId;
    public RadioSettings Settings { get; private set; } = new();
    public IReadOnlyList<ListenChannel> Channels { get; private set; } = [];
    public bool IsConnected => source is not null;
    public bool IsRunning => pipeline is { Completion.IsCompleted: false };
    public bool HasFinished => pipeline is { Completion.IsCompleted: true };
    public string Description { get; private set; } = "Ready to connect";
    public SourceInfo? Info => source?.Info;
    public MetricsSnapshot? LastMetrics { get; private set; }
    public MetricsSnapshot? Metrics => pipeline?.Snapshot ?? LastMetrics;
    public SpectrumFrame? Spectrum => pipeline?.Spectrum;
    public Exception? Failure => pipeline?.Failure;

    public async Task ConnectAsync(RadioSettings settings)
    {
        settings.Validate();
        await gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            source?.Dispose(); source = null;
            var opened = await Task.Run(() => SourceFactory.Open(settings));
            Settings = settings with { SampleRate = opened.Info.SampleRate, CenterFrequencyHz = opened.Info.CenterFrequencyHz };
            source = opened;
            Description = opened.Info.Description;
        }
        finally { gate.Release(); }
    }

    public async Task StartAsync(RadioSettings settings, IReadOnlyList<ListenChannel> channels)
    {
        await gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            if (source is null || settings != Settings)
            {
                source?.Dispose(); source = null;
                settings.Validate();
                source = await Task.Run(() => SourceFactory.Open(settings));
                Settings = settings with { SampleRate = source.Info.SampleRate, CenterFrequencyHz = source.Info.CenterFrequencyHz };
            }
            await StartCoreAsync(channels);
        }
        finally { gate.Release(); }
    }

    public async Task SetChannelsAsync(IReadOnlyList<ListenChannel> channels)
    {
        await gate.WaitAsync();
        try
        {
            if (source is not null) foreach (var channel in channels) channel.Validate(Settings);
            bool restart = IsRunning;
            await StopCoreAsync();
            Channels = channels.ToArray();
            if (restart) await StartCoreAsync(Channels);
        }
        finally { gate.Release(); }
    }

    private async Task StartCoreAsync(IReadOnlyList<ListenChannel> channels)
    {
        if (source is null) throw new InvalidOperationException("Connect a source first.");
        store.ThrowIfFaulted();
        var metrics = new PipelineMetrics();
        var id = Guid.NewGuid().ToString("N");
        var processor = await Task.Run(() => new DmrProcessor(Settings, channels, metrics, store.Record, id));
        Channels = channels.ToArray();
        runId = id;
        Description = processor.Description;
        pipeline = new(source, processor, metrics, Settings.BlockSize, Settings.QueueCapacity, store.ThrowIfFaulted, ownsSource: false);
        pipeline.Start();
    }

    private async Task StopCoreAsync()
    {
        if (pipeline is null) return;
        var current = pipeline;
        pipeline = null;
        await current.StopAsync();
        LastMetrics = current.Snapshot;
        var error = current.Failure;
        try { await current.DisposeAsync(); }
        finally
        {
            if (runId is not null)
                await store.EndRunAsync(runId, Settings, Channels, Description, LastMetrics, error?.ToString());
            await store.FlushAsync();
        }
        if (error is not null) throw new IOException("Capture failed: " + error.Message, error);
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try
        {
            try { await StopCoreAsync(); }
            finally { var released = source; source = null; released?.Dispose(); }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync(); }
        finally { gate.Dispose(); }
    }
}

public sealed record WorkspaceSettings(RadioSettings Radio, ListenChannel[] Channels)
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BadSdr");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static WorkspaceSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new(new(), []);
        return JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(SettingsPath)) ?? new(new(), []);
    }
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        string temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, overwrite: true);
    }
}
