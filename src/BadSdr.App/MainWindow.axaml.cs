using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BadSdr.Core;
using BadSdr.Core.Pipeline;
using BadSdr.Core.Sources;
using BadSdr.Core.Storage;
using IqChannelizer.Abstractions;

namespace BadSdr.App;

public partial class MainWindow : Window
{
    private readonly List<ListenChannel> channels = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly bool smoke;
    private readonly string databasePath;
    private SessionStore? store;
    private RadioController? controller;
    private bool initialized, busy, closing, allowClose, refreshing;
    private bool hasError;
    private int ticks, nextChannelId = 1;
    private MetricsSnapshot? previousMetrics;
    private Task refreshTask = Task.CompletedTask;
    internal Task Initialization { get; private set; } = Task.CompletedTask;
    internal int SessionCount { get; private set; }
    internal string CurrentStatus => StatusText.Text ?? "";

    public MainWindow() : this(null) { }
    internal MainWindow(string? testDatabase)
    {
        InitializeComponent();
        smoke = testDatabase is not null;
        databasePath = testDatabase ?? Path.Combine(WorkspaceSettings.DataDirectory, "sessions.db");
        SourceBox.ItemsSource = Enum.GetValues<SourceKind>();
        ProcessingBox.ItemsSource = Enum.GetValues<ProcessingMode>();
        StrategyBox.ItemsSource = Enum.GetValues<ChannelizerStrategy>();
        BackendBox.ItemsSource = Enum.GetValues<ComputeBackendPreference>();
        FormatBox.ItemsSource = new[] { "wav", "cf32", "cs16", "cs32", "cu8" };
        initialized = true;
        try
        {
            var saved = smoke ? new WorkspaceSettings(new(), []) : WorkspaceSettings.Load();
            Populate(saved.Radio);
            channels.AddRange(saved.Channels);
            nextChannelId = channels.Select(c => c.Id).DefaultIfEmpty(0).Max() + 1;
        }
        catch (Exception error)
        {
            Populate(new());
            Status($"Settings could not be restored: {error.Message}", true);
        }
        RefreshChannels();
        Spectrum.AddRequested += frequency => _ = GuardAsync(() => AddChannelAsync(frequency));
        Spectrum.RetuneRequested += (id, frequency) => _ = GuardAsync(() => RetuneAsync(id, frequency));
        Spectrum.RemoveRequested += id => _ = GuardAsync(() => ApplyChannelsAsync(channels.Where(c => c.Id != id).ToArray()));
        timer.Tick += OnTick;
        Opened += (_, _) => Initialization = InitializeAsync();
        Closing += OnClosing;
    }

    private async Task InitializeAsync()
    {
        await GuardAsync(async () =>
        {
            store = await Task.Run(() => new SessionStore(databasePath));
            controller = new(store);
            await RefreshSessionsAsync();
            timer.Start();
        });
    }

    private static double Number(TextBox box, string label)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new ArgumentException($"{label}: enter a finite number, using a dot as the decimal separator.");
        return value;
    }
    private static int Integer(TextBox box, string label)
    {
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{label}: enter a whole number.");
        return value;
    }
    private RadioSettings ReadSettings() => new()
    {
        Source = (SourceKind)SourceBox.SelectedItem!, DeviceArguments = ArgumentsBox.Text?.Trim() ?? "",
        CenterFrequencyHz = Number(CenterBox, "Centre frequency") * 1e6, SampleRate = Number(RateBox, "Sample rate"),
        GainDb = Number(GainBox, "Gain"), BandwidthHz = Number(BandwidthBox, "Bandwidth"), Antenna = AntennaBox.Text?.Trim() ?? "",
        RxChannel = checked((uint)Integer(RxChannelBox, "RX channel")), ReplayPath = ReplayPathBox.Text?.Trim() ?? "",
        ReplayFormat = (string)FormatBox.SelectedItem!, ReplayRealtime = RealtimeBox.IsChecked == true,
        Processing = (ProcessingMode)ProcessingBox.SelectedItem!, Strategy = (ChannelizerStrategy)StrategyBox.SelectedItem!,
        Backend = (ComputeBackendPreference)BackendBox.SelectedItem!, BlockSize = Integer(BlockBox, "IQ block size"),
        QueueCapacity = Integer(QueueBox, "IQ queue size")
    };

    private void Populate(RadioSettings settings)
    {
        SourceBox.SelectedItem = settings.Source;
        ArgumentsBox.Text = settings.DeviceArguments;
        CenterBox.Text = (settings.CenterFrequencyHz / 1e6).ToString("F6", CultureInfo.InvariantCulture);
        ChannelFrequencyBox.Text = CenterBox.Text;
        RateBox.Text = settings.SampleRate.ToString("G", CultureInfo.InvariantCulture);
        GainBox.Text = settings.GainDb.ToString(CultureInfo.InvariantCulture);
        BandwidthBox.Text = settings.BandwidthHz.ToString(CultureInfo.InvariantCulture);
        AntennaBox.Text = settings.Antenna;
        RxChannelBox.Text = settings.RxChannel.ToString(CultureInfo.InvariantCulture);
        ReplayPathBox.Text = settings.ReplayPath;
        FormatBox.SelectedItem = settings.ReplayFormat;
        RealtimeBox.IsChecked = settings.ReplayRealtime;
        ProcessingBox.SelectedItem = settings.Processing;
        StrategyBox.SelectedItem = settings.Strategy;
        BackendBox.SelectedItem = settings.Backend;
        BlockBox.Text = settings.BlockSize.ToString(CultureInfo.InvariantCulture);
        QueueBox.Text = settings.QueueCapacity.ToString(CultureInfo.InvariantCulture);
        Spectrum.SetBand(settings.CenterFrequencyHz, settings.SampleRate);
    }

    private void SourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        DevicePanel.IsVisible = SourceBox.SelectedItem is SourceKind.Usrp;
        ReplayPanel.IsVisible = SourceBox.SelectedItem is SourceKind.Replay;
    }
    private void DeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (initialized && DevicesBox.SelectedItem is string address) ArgumentsBox.Text = address;
    }

    private async void DiscoverClicked(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        Status("Discovering USRP devices…");
        var arguments = ArgumentsBox.Text?.Trim() ?? "";
        var found = await Task.Run(() => UsrpSource.Discover(arguments));
        DevicesBox.ItemsSource = found;
        DevicesBox.IsVisible = found.Count > 0;
        if (found.Count > 0) DevicesBox.SelectedIndex = 0;
        Status(found.Count > 0 ? $"Found {found.Count} USRP device(s). Select one, then connect." : "No USRP devices found. Check power, network/USB connection, and UHD device arguments.");
    });

    private async void ConnectClicked(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var radio = RequireController();
        bool restart = radio.IsRunning;
        Status("Connecting and applying receiver settings…");
        await radio.ConnectAsync(ReadSettings());
        ShowActualSettings();
        if (restart) await radio.StartAsync(radio.Settings, channels);
        SaveSettings();
        ShowPlan();
        Status($"Connected · {radio.Info!.SampleRate:N0} S/s · {radio.Info.CenterFrequencyHz / 1e6:F6} MHz");
    });

    private async void StartClicked(object? sender, RoutedEventArgs e) => await GuardAsync(StartReceiverAsync);
    private async Task StartReceiverAsync()
    {
        var radio = RequireController();
        Status("Preparing receiver and channelizer…");
        StateText.Text = "●  PREPARING";
        await radio.StartAsync(ReadSettings(), channels);
        previousMetrics = null;
        ShowActualSettings();
        ShowPlan();
        SaveSettings();
        Status("Receiving IQ. Channel edits pause capture briefly while the processing path rebuilds.");
    }
    private async void StopClicked(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        Status("Stopping receiver and flushing session data…");
        await RequireController().StopAsync();
        await RefreshSessionsAsync();
        Status("Stopped. Session data has been flushed to SQLite.");
    });

    private RadioController RequireController() => controller ?? throw new InvalidOperationException("The session database is still initializing.");

    private void ShowActualSettings()
    {
        var settings = RequireController().Settings;
        CenterBox.Text = (settings.CenterFrequencyHz / 1e6).ToString("F6", CultureInfo.InvariantCulture);
        RateBox.Text = settings.SampleRate.ToString("G", CultureInfo.InvariantCulture);
        Spectrum.SetBand(settings.CenterFrequencyHz, settings.SampleRate);
        SpanText.Text = $"{settings.SampleRate / 1e6:F3} MHz input span";
    }
    private void ShowPlan()
    {
        PlanText.Text = RequireController().Description.Split('\n')[0];
        ToolTip.SetTip(PlanText, controller!.Description);
    }
    private void SaveSettings()
    {
        if (!smoke) new WorkspaceSettings(ReadSettings(), channels.ToArray()).Save();
    }

    private async void BrowseClicked(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open IQ recording", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("IQ recordings") { Patterns = ["*.wav", "*.cf32", "*.cs16", "*.cs32", "*.cu8"] }, FilePickerFileTypes.All]
        });
        if (files.Count == 0) return;
        ReplayPathBox.Text = files[0].TryGetLocalPath() ?? throw new IOException("Choose a local IQ recording.");
        var extension = Path.GetExtension(ReplayPathBox.Text).TrimStart('.').ToLowerInvariant();
        if (extension is "wav" or "cf32" or "cs16" or "cs32" or "cu8") FormatBox.SelectedItem = extension;
    });

    private async void FixtureClicked(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var path = LocateFixture();
        await ConfigureFixtureAsync(path);
        Status("DMR fixture loaded at −12.5 kHz. Start receiver to replay the supplied recording.");
    });

    internal static string LocateFixture()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
            {
                var path = Path.Combine(folder.FullName, "dmr", "dmr_test.wav");
                if (File.Exists(path)) return path;
            }
        throw new FileNotFoundException("Cannot find the sibling dmr/dmr_test.wav recording. Use Browse to choose it.");
    }

    internal async Task ConfigureFixtureAsync(string path)
    {
        await RequireController().DisconnectAsync();
        Populate(new RadioSettings { Source = SourceKind.Replay, ReplayPath = path, CenterFrequencyHz = 435_000_000,
            SampleRate = 54688, BandwidthHz = 54688, Processing = ProcessingMode.Direct,
            ReplayRealtime = !smoke, QueueCapacity = smoke ? 64 : 8, BlockSize = 16384 });
        await ApplyChannelsAsync([new(1, 434_987_500, "Fixture DMR")]);
        nextChannelId = 2;
    }
    internal Task StartForSmokeAsync() => GuardAsync(StartReceiverAsync);

    private void ChannelSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ChannelsList.SelectedItem is ListenChannel channel)
            ChannelFrequencyBox.Text = (channel.FrequencyHz / 1e6).ToString("F6", CultureInfo.InvariantCulture);
    }
    private async void AddClicked(object? sender, RoutedEventArgs e) => await GuardAsync(() =>
        AddChannelAsync(Number(ChannelFrequencyBox, "Channel frequency") * 1e6));
    private async void RetuneClicked(object? sender, RoutedEventArgs e) => await GuardAsync(() =>
    {
        if (ChannelsList.SelectedItem is not ListenChannel channel) throw new ArgumentException("Select a channel to retune.");
        return RetuneAsync(channel.Id, Number(ChannelFrequencyBox, "Channel frequency") * 1e6);
    });
    private async void RemoveClicked(object? sender, RoutedEventArgs e) => await GuardAsync(() =>
    {
        if (ChannelsList.SelectedItem is not ListenChannel channel) throw new ArgumentException("Select a channel to remove.");
        return ApplyChannelsAsync(channels.Where(c => c.Id != channel.Id).ToArray());
    });

    private async Task AddChannelAsync(double frequency)
    {
        await ApplyChannelsAsync(channels.Append(new ListenChannel(nextChannelId, frequency)).ToArray());
        nextChannelId++;
        ChannelsList.SelectedIndex = channels.Count - 1;
    }
    private Task RetuneAsync(int id, double frequency) => ApplyChannelsAsync(channels.Select(c => c.Id == id ? c with { FrequencyHz = frequency } : c).ToArray());

    private async Task ApplyChannelsAsync(IReadOnlyList<ListenChannel> updated)
    {
        if (updated.Count > 128) throw new ArgumentException("This receiver supports up to 128 listening channels.");
        var settings = controller?.IsConnected == true ? controller.Settings : ReadSettings();
        settings.Validate();
        foreach (var channel in updated) channel.Validate(settings);
        bool running = controller?.IsRunning == true;
        Status(running ? "Pausing capture and rebuilding channels…" : "Updating listening channels…");
        // The desired configuration remains visible if planning fails; capture stays stopped for correction.
        channels.Clear(); channels.AddRange(updated);
        RefreshChannels();
        if (controller is not null) await controller.SetChannelsAsync(channels);
        if (running) { previousMetrics = null; ShowPlan(); }
        SaveSettings();
        Status($"{channels.Count} channel(s) configured." + (running ? " Receiver restarted." : ""));
    }

    private void RefreshChannels()
    {
        int? selected = (ChannelsList.SelectedItem as ListenChannel)?.Id;
        ChannelsList.ItemsSource = channels.ToArray();
        ChannelsList.SelectedItem = channels.FirstOrDefault(c => c.Id == selected);
        Spectrum.SetChannels(channels.ToArray());
        ChannelCountText.Text = channels.Count.ToString("D2");
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (closing || controller is null || store is null) return;
        Spectrum.SetFrame(controller.Spectrum);
        ShowMetrics(controller.Metrics);
        DatabaseText.Text = $"{store.Written:N0} events";
        DatabaseDetail.Text = $"SQLite · {store.Pending:N0} queued";
        if (!busy)
        {
            if (store.Failure is not null)
            {
                timer.Stop();
                _ = GuardAsync(async () => { await controller.StopAsync(); store.ThrowIfFaulted(); });
                return;
            }
            if (controller.HasFinished)
            {
                _ = GuardAsync(async () =>
                {
                    await controller.DisconnectAsync();
                    await RefreshSessionsAsync();
                    Status("Input ended. Session data has been flushed to SQLite.");
                });
                return;
            }
            UpdateControls();
        }
        if (++ticks % 5 == 0 && !refreshing) refreshTask = RefreshSessionsAsync();
    }

    private void ShowMetrics(MetricsSnapshot? value)
    {
        if (value is null) return;
        var prior = previousMetrics;
        double elapsed = value.ElapsedSeconds - (prior?.ElapsedSeconds ?? 0);
        long received = value.Received - (prior?.Received ?? 0), dropped = value.Dropped - (prior?.Dropped ?? 0);
        DropText.Text = $"{value.DropPercent:F2}%";
        DropDetail.Text = $"{value.Dropped:N0} lost · {(elapsed > 0 ? dropped / elapsed : 0):N0}/s";
        ToolTip.SetTip(DropText, $"Total received {value.Received:N0}. Recent application loss: {(received > 0 ? 100.0 * dropped / received : 0):F2}%. Hardware loss is separate.");
        ThroughputText.Text = $"{value.ProcessingSamplesPerSecond / 1e6:F2} MS/s";
        double rate = controller?.Settings.SampleRate ?? 1;
        ThroughputDetail.Text = $"{value.ProcessingSamplesPerSecond / rate:F2}× real time · {value.Processed / 1e6:F2} M IQ";
        OverflowText.Text = value.Overflows.ToString("N0");
        OverflowDetail.Text = $"{value.Timeouts:N0} timeouts · {value.Discontinuities:N0} resets";
        QueueText.Text = $"IQ queue {value.QueuedBlocks} / {controller?.Settings.QueueCapacity} · tail {value.TailSamples:N0}";
        TimingText.Text = $"Channelizer {value.ChannelizerSeconds * 1000:N0} ms  /  DMR {value.DecoderSeconds * 1000:N0} ms";
        previousMetrics = value;
    }

    private async Task RefreshSessionsAsync()
    {
        if (store is null || refreshing || closing) return;
        refreshing = true;
        try
        {
            var selectedId = (SessionsGrid.SelectedItem as SessionRow)?.SessionId;
            var rows = await store.ReadRecentAsync();
            SessionCount = rows.Count;
            SessionsGrid.ItemsSource = rows;
            if (selectedId is not null) SessionsGrid.SelectedItem = rows.FirstOrDefault(r => r.SessionId == selectedId);
            EmptySessions.IsVisible = rows.Count == 0;
            SessionCountText.Text = $"{rows.Count} recent sessions · latest 500";
        }
        catch (Exception error) { Status("Cannot read sessions: " + error.Message, true); }
        finally { refreshing = false; }
    }
    private void SessionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (SessionsGrid.SelectedItem is not SessionRow row) return;
        using var json = JsonDocument.Parse(row.MetadataJson);
        MetadataBox.Text = $"Session: {row.SessionId}\nRun: {row.RunId}\n" + JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
    }
    private void OpenDataFolderClicked(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(databasePath)!) { UseShellExecute = true }); }
        catch (Exception error) { Status(error.Message, true); }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        if (busy || closing) return;
        busy = true;
        UpdateControls();
        try { await action(); }
        catch (Exception error) { Status(error.Message, true); }
        finally { busy = false; UpdateControls(); }
    }
    private void UpdateControls()
    {
        bool running = controller?.IsRunning == true;
        StartButton.IsEnabled = !busy && !running && controller is not null;
        StopButton.IsEnabled = !busy && running;
        ConnectButton.IsEnabled = DiscoverButton.IsEnabled = !busy && controller is not null;
        AddButton.IsEnabled = RetuneButton.IsEnabled = RemoveButton.IsEnabled = Spectrum.IsEnabled = !busy;
        if (!busy) StateText.Text = hasError ? "●  ERROR" : running ? "●  RECEIVING" : controller?.IsConnected == true ? "●  CONNECTED" : "●  IDLE";
    }
    private void Status(string text, bool error = false)
    {
        StatusText.Text = text;
        hasError = error;
        StatusText.Foreground = Avalonia.Media.Brush.Parse(error ? "#F59BA5" : "#93A7C3");
        ToolTip.SetTip(StatusText, text);
        if (error) StateText.Text = "●  ERROR";
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        timer.Stop();
        try
        {
            // Let any serialized setup finish before releasing device/database resources.
            while (busy) await Task.Delay(25);
            if (!smoke)
            {
                try { ReadSettings().Validate(); SaveSettings(); }
                catch (Exception error) { Status("Settings were not saved: " + error.Message, true); }
            }
            await refreshTask;
            if (controller is not null) await controller.DisposeAsync();
        }
        catch (Exception error) { Status("Shutdown: " + error.Message, true); }
        finally
        {
            try { if (store is not null) await store.DisposeAsync(); }
            catch (Exception error) { Status("Database shutdown: " + error.Message, true); }
            allowClose = true;
            Close();
        }
    }
}
