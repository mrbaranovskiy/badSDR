using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using BadSdr.App.Controls;
using BadSdr.Core;

namespace BadSdr.App;

internal static class UiSmoke
{
    public static int Run(string[] args)
    {
        string output = Path.GetFullPath(args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? "artifacts/ui-smoke");
        Directory.CreateDirectory(output);
        try
        {
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
            VerifyChannelInteractions(window);
            window.FindControl<DataGrid>("SessionsGrid")!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            using (var image = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No UI frame rendered."))
                image.Save(Path.Combine(output, "receiver.png"));
            window.Width = 1140;
            window.Height = 780;
            Dispatcher.UIThread.RunJobs();
            using (var image = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No compact UI frame rendered."))
                image.Save(Path.Combine(output, "receiver-compact.png"));
            window.Close();
            PumpUntil(() => !window.IsVisible, 20);
            File.WriteAllText(Path.Combine(output, "result.txt"), $"PASS: rendered desktop, replayed DMR IQ, displayed {window.SessionCount} sessions, added/dragged/removed a channel, resized and shut down cleanly.");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), error.ToString());
            return 1;
        }
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
