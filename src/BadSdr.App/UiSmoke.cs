using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using BadSdr.App.Controls;
using BadSdr.Core;
using BadSdr.Core.Dsp;

namespace BadSdr.App;

internal static class UiSmoke
{
    // Uses normal saved channels with an isolated database. Explicit opt-in opens the live radio.
    public static int RunLive(string[] args)
    {
        string Value(string key, string fallback) => args.SkipWhile(a => a != key).Skip(1).FirstOrDefault() ?? fallback;
        string output = Path.GetFullPath(Value("--output", "artifacts/live-ui-probe"));
        Directory.CreateDirectory(output);
        MainWindow? window = null;
        try
        {
            var saved = WorkspaceSettings.Load();
            var settings = saved.Radio with { Source = SourceKind.Usrp,
                SampleRate = double.Parse(Value("--rate", saved.Radio.SampleRate.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture),
                DeviceArguments = Value("--device-args", saved.Radio.DeviceArguments),
                WireFormat = Value("--wire-format", saved.Radio.WireFormat),
                BlockSize = int.Parse(Value("--block-size", saved.Radio.BlockSize.ToString())),
                QueueCapacity = int.Parse(Value("--queue-blocks", saved.Radio.QueueCapacity.ToString())) };
            int seconds = int.Parse(Value("--seconds", "15"));
            if (seconds < 1) throw new ArgumentException("Probe duration must be positive.");
            settings.Validate();
            AppBuilder.Configure<App>().UseSkia().WithInterFont()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var dispatcherErrors = new List<string>();
            Dispatcher.UIThread.UnhandledException += (_, e) => dispatcherErrors.Add(e.Exception.ToString());
            window = new MainWindow(Path.Combine(output, $"probe-{Guid.NewGuid():N}.db"));
            window.Show();
            Pump(window.Initialization);
            Pump(window.ConfigureProbeAsync(saved with { Radio = settings }));
            var setup = Stopwatch.StartNew();
            Pump(window.StartForSmokeAsync());
            setup.Stop();
            var intervals = new List<double>();
            var renderTimes = new List<double>();
            var timeline = new List<object>();
            var clock = Stopwatch.StartNew();
            double previous = 0, nextRender = 0, nextSnapshot = 1;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            heartbeat.Tick += (_, _) =>
            {
                double now = clock.Elapsed.TotalMilliseconds;
                intervals.Add(now - previous);
                previous = now;
                if (now >= nextRender)
                {
                    long start = Stopwatch.GetTimestamp();
                    using var image = window.CaptureRenderedFrame();
                    renderTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    nextRender = now + 200;
                }
                if (clock.Elapsed.TotalSeconds >= nextSnapshot)
                {
                    timeline.Add(new { seconds = clock.Elapsed.TotalSeconds, status = window.CurrentStatus, metrics = window.CurrentMetrics });
                    nextSnapshot++;
                }
            };
            // RunJobs alone does not pump platform timers continuously. Use the real
            // dispatcher loop so MainWindow's 200 ms timer and database refresh run.
            heartbeat.Start();
            Dispatcher.UIThread.MainLoop(timeout.Token);
            heartbeat.Stop();
            using (var image = window.CaptureRenderedFrame()) image?.Save(Path.Combine(output, "receiver.png"));
            var metrics = window.CurrentMetrics;
            string status = window.CurrentStatus;
            var displayState = window.ProbeDisplayState;
            var display = new { capacity = window.FindControl<TextBlock>("ThroughputText")!.Text,
                received = window.FindControl<TextBlock>("ThroughputDetail")!.Text,
                timing = window.FindControl<TextBlock>("TimingText")!.Text,
                waterfallRows = window.FindControl<WaterfallControl>("Waterfall")!.HistoryCount };
            window.Close();
            PumpUntil(() => !window.IsVisible, 20);
            double Percentile(List<double> values, double percentile) => values.Order().ElementAt((int)((values.Count - 1) * percentile));
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new {
                settings, channels = saved.Channels.Length, setupSeconds = setup.Elapsed.TotalSeconds,
                durationSeconds = clock.Elapsed.TotalSeconds, heartbeatMaxMs = intervals.Max(), heartbeatP95Ms = Percentile(intervals, .95),
                renderMaxMs = renderTimes.Max(), renderP95Ms = Percentile(renderTimes, .95), renderedFrames = renderTimes.Count,
                status, display, displayState, dispatcherErrors, metrics, timeline
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString());
            if (window is not null) { window.Close(); PumpUntil(() => !window.IsVisible, 20); }
            return 1;
        }
    }

    public static int Run(string[] args)
    {
        string output = Path.GetFullPath(args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? "artifacts/ui-smoke");
        Directory.CreateDirectory(output);
        try
        {
            if (!DspBuildInfo.IsOptimized ||
                !bool.TryParse(AppContext.GetData("System.Runtime.TieredCompilation")?.ToString(), out bool tiered) || tiered)
                throw new InvalidOperationException("The app must load optimized DSP with tiered compilation disabled.");
            AppBuilder.Configure<App>().UseSkia().WithInterFont()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var window = new MainWindow(Path.Combine(output, $"smoke-{Guid.NewGuid():N}.db"));
            window.Show();
            PumpUntil(() => window.Initialization.IsCompleted, 20);
            window.Initialization.GetAwaiter().GetResult();
            Pump(window.ConfigureFixtureAsync(MainWindow.LocateFixture()));
            Pump(window.StartForSmokeAsync());
            PumpUntil(() => window.SessionCount > 0 && window.CurrentStatus.StartsWith("Input ended"), 45);
            if (window.SessionCount == 0) throw new InvalidOperationException("The UI did not display decoded fixture sessions.");
            if (window.FindControl<WaterfallControl>("Waterfall")!.HistoryCount == 0)
                throw new InvalidOperationException("The waterfall did not receive replay FFT frames.");
            VerifyChannelInteractions(window);
            window.FindControl<DataGrid>("SessionsGrid")!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            using (var image = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No UI frame rendered."))
                image.Save(Path.Combine(output, "receiver.png"));
            VerifyWaterfall(window);
            using (var image = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No waterfall frame rendered."))
                image.Save(Path.Combine(output, "waterfall-history.png"));
            window.Width = 1140;
            window.Height = 780;
            Dispatcher.UIThread.RunJobs();
            using (var image = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No compact UI frame rendered."))
                image.Save(Path.Combine(output, "receiver-compact.png"));
            window.Close();
            PumpUntil(() => !window.IsVisible, 20);
            File.WriteAllText(Path.Combine(output, "result.txt"), $"PASS: rendered desktop, replayed DMR IQ, displayed {window.SessionCount} sessions, added/dragged/removed a channel, verified waterfall ingestion/history/zoom/reset, resized and shut down cleanly. waterfall-history.png and receiver-compact.png use generated FFT history for display checks.");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), error.ToString());
            return 1;
        }
    }
    private static void VerifyWaterfall(MainWindow window)
    {
        var waterfall = window.FindControl<WaterfallControl>("Waterfall")!;
        var spectrum = window.FindControl<SpectrumControl>("Spectrum")!;
        waterfall.Clear();
        var frame = new SpectrumFrame(Enumerable.Repeat(-80f, 2048).ToArray(), 435e6, 54688);
        waterfall.SetFrame(frame);
        waterfall.SetFrame(frame);
        waterfall.SetFrame(null);
        if (waterfall.HistoryCount != 1) throw new InvalidOperationException("Waterfall duplicated an unchanged FFT frame.");
        spectrum.SetBand(436e6, 54688);
        if (waterfall.HistoryCount != 0) throw new InvalidOperationException("Retuning did not clear waterfall history.");
        waterfall.SetFrame(frame with { CenterFrequencyHz = 436e6 });
        spectrum.SetBand(436e6, 100000);
        if (waterfall.HistoryCount != 0) throw new InvalidOperationException("Sample rate changes did not clear waterfall history.");
        spectrum.SetBand(435e6, 54688);
        // Deterministic display fixture exercises a wrapped ring and gives visual QA a full history.
        var random = new Random(42);
        for (int t = 0; t < WaterfallControl.HistoryCapacity + 31; t++)
        {
            var power = new float[2048];
            for (int i = 0; i < power.Length; i++)
            {
                double frequency = -27344 + i * 54688.0 / power.Length;
                double signal = Math.Abs(frequency + 12500) < 4200 && t % 55 < 38 ? 38 : 0;
                double tone = 60 * Math.Exp(-Math.Pow((frequency - 11000 - 1500 * Math.Sin(t * .035)) / 250, 2));
                power[i] = (float)(-99 + random.NextDouble() * 9 + signal + tone);
            }
            frame = new SpectrumFrame(power, 435e6, 54688);
            waterfall.SetFrame(frame);
        }
        spectrum.SetFrame(frame);
        if (waterfall.HistoryCount != WaterfallControl.HistoryCapacity)
            throw new InvalidOperationException("Waterfall history is not bounded.");
        var point = spectrum.TranslatePoint(new Point(spectrum.Bounds.Width / 2, 90), window)!.Value;
        window.MouseWheel(point, new Vector(0, 1));
        if (Math.Abs(waterfall.ViewSpan - 54688 * .8) > .01 || waterfall.HistoryCount != WaterfallControl.HistoryCapacity)
            throw new InvalidOperationException("Spectrum zoom did not preserve and crop waterfall history.");
        window.MouseWheel(point, new Vector(0, -1));
        if (Math.Abs(waterfall.ViewSpan - 54688) > .01 || Math.Abs(waterfall.ViewCenter - 435e6) > .01)
            throw new InvalidOperationException("Waterfall did not return to the full input band.");
        Dispatcher.UIThread.RunJobs();
    }
    private static void VerifyChannelInteractions(MainWindow window)
    {
        var list = window.FindControl<ListBox>("ChannelsList")!;
        var input = window.FindControl<TextBox>("ChannelFrequencyBox")!;
        var spectrum = window.FindControl<SpectrumControl>("Spectrum")!;
        input.Text = "435.012500";
        window.FindControl<Button>("AddButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => list.ItemCount == 2, 5);
        var channels = ((IEnumerable<ListenChannel>)list.ItemsSource!).ToArray();
        var added = channels.Last();
        double x = 43 + (added.FrequencyHz - (435e6 - 54688.0 / 2)) / 54688 * (spectrum.Bounds.Width - 52);
        var point = spectrum.TranslatePoint(new Point(x, 90), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseMove(point + new Vector(18, 0));
        if (((IEnumerable<ListenChannel>)list.ItemsSource!).Last().FrequencyHz != added.FrequencyHz)
            throw new InvalidOperationException("Dragging rebuilt channels before pointer release.");
        window.MouseUp(point + new Vector(18, 0), MouseButton.Left);
        PumpUntil(() => ((IEnumerable<ListenChannel>)list.ItemsSource!).Last().FrequencyHz != added.FrequencyHz, 5);
        var moved = ((IEnumerable<ListenChannel>)list.ItemsSource!).Last();
        x = 43 + (moved.FrequencyHz - (435e6 - 54688.0 / 2)) / 54688 * (spectrum.Bounds.Width - 52);
        point = spectrum.TranslatePoint(new Point(x, 90), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        PumpUntil(() => list.ItemCount == 1, 5);
    }
    private static void Pump(Task task)
    {
        PumpUntil(() => task.IsCompleted, 30);
        task.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> condition, int seconds)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException("The headless UI check timed out.");
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
