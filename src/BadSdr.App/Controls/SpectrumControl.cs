using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BadSdr.Core;
using BadSdr.Core.Dsp;

namespace BadSdr.App.Controls;

public sealed class SpectrumControl : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#0B1420"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8094B2"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#54DCC5"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#203047")), 1);
    private static readonly string[] Colors = ["#59DFCC", "#80ACFF", "#E4B963", "#C799ED", "#F492AE", "#76CADD"];
    private IReadOnlyList<ListenChannel> channels = [];
    private SpectrumFrame? frame;
    private double center = 435_000_000, rate = 1_000_000, viewCenter = 435_000_000, span = 1_000_000;
    private int? dragging;
    private double dragFrequency, dragOffset;
    private Point? cursor;
    public event Action<double>? AddRequested;
    public event Action<int, double>? RetuneRequested;
    public event Action<int>? RemoveRequested;
    public event Action<double, double, double, double>? RangeChanged;

    public SpectrumControl() { Focusable = true; ClipToBounds = true; MinHeight = 150; }

    public void SetChannels(IReadOnlyList<ListenChannel> value) { channels = value; InvalidateVisual(); }
    public void SetFrame(SpectrumFrame? value)
    {
        if (value is null || ReferenceEquals(frame, value)) return;
        SetBand(value.CenterFrequencyHz, value.SampleRate);
        frame = value;
        InvalidateVisual();
    }
    public void SetBand(double frequency, double sampleRate)
    {
        if (center == frequency && rate == sampleRate) return;
        center = viewCenter = frequency;
        rate = span = sampleRate;
        frame = null;
        RangeChanged?.Invoke(center, rate, viewCenter, span);
        InvalidateVisual();
    }

    private Rect Plot => new(43, 23, Math.Max(1, Bounds.Width - 52), Math.Max(1, Bounds.Height - 49));
    private double X(double frequency) => Plot.Left + (frequency - (viewCenter - span / 2)) / span * Plot.Width;
    private double Frequency(double x) => viewCenter - span / 2 + (x - Plot.Left) / Plot.Width * span;
    private double Y(double db) => Plot.Top + Math.Clamp(-db / 110, 0, 1) * Plot.Height;
    private static double Snap(double frequency, KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Shift) ? frequency : Math.Round(frequency / 125) * 125;
    private double ClampFrequency(double value) => Math.Clamp(value, center - rate / 2 + 6501, center + rate / 2 - 6501);

    private static void Text(DrawingContext context, string value, Point location, IBrush brush, double size = 10)
    {
        context.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter"), size, brush), location);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, Bounds.WithX(0).WithY(0));
        var plot = Plot;
        for (int db = 0; db >= -100; db -= 20)
        {
            double y = Y(db);
            context.DrawLine(GridPen, new(plot.Left, y), new(plot.Right, y));
            Text(context, db.ToString(CultureInfo.InvariantCulture), new(4, y - 6), Muted);
        }
        Text(context, "dBFS", new(3, 1), Muted, 9);
        for (int i = 0; i <= 4; i++)
        {
            double frequency = viewCenter - span / 2 + i * span / 4;
            double x = X(frequency);
            context.DrawLine(GridPen, new(x, plot.Top), new(x, plot.Bottom));
            double left = Math.Clamp(x - 28, plot.Left - 12, Math.Max(plot.Left, Bounds.Width - 64));
            Text(context, (frequency / 1e6).ToString("F4", CultureInfo.InvariantCulture), new(left, plot.Bottom + 8), Muted);
        }
        using (context.PushClip(plot))
        {
            if (frame is { } data)
            {
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    bool began = false;
                    for (int i = 0; i < data.PowerDb.Length; i++)
                    {
                        double frequency = data.CenterFrequencyHz - data.SampleRate / 2 + i * data.SampleRate / data.PowerDb.Length;
                        double x = X(frequency);
                        if (x < plot.Left - 2 || x > plot.Right + 2) continue;
                        var point = new Point(x, Y(data.PowerDb[i]));
                        if (!began) { path.BeginFigure(point, false); began = true; }
                        else path.LineTo(point);
                    }
                    if (began) path.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(Accent, 1.2), geometry);
            }
            else Text(context, "Waiting for IQ samples", new(plot.Left + 24, plot.Top + plot.Height * .5), Muted, 13);

            for (int i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                double frequency = dragging == channel.Id ? dragFrequency : channel.FrequencyHz;
                double x = X(frequency);
                double width = Math.Max(10, ListenChannel.BandwidthHz / span * plot.Width);
                var color = Color.Parse(Colors[(channel.Id - 1) % Colors.Length]);
                var brush = new SolidColorBrush(color);
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(32, color.R, color.G, color.B)), new Rect(x - width / 2, plot.Top, width, plot.Height));
                context.DrawLine(new Pen(brush, dragging == channel.Id ? 2 : 1), new(x, plot.Top), new(x, plot.Bottom));
                double labelY = plot.Top + 6 + (i % 3) * 16;
                double labelX = Math.Clamp(x + 5, plot.Left + 3, Math.Max(plot.Left + 3, plot.Right - 92));
                context.FillRectangle(BackgroundBrush, new Rect(labelX - 2, labelY - 1, 92, 14));
                Text(context, $"{channel.Id:00}  {frequency / 1e6:F5}", new(labelX, labelY), brush, 10);
            }
            if (cursor is { } mouse && plot.Contains(mouse))
            {
                context.DrawLine(new Pen(Muted, .7, DashStyle.Dash), new(mouse.X, plot.Top), new(mouse.X, plot.Bottom));
                Text(context, $"{Frequency(mouse.X) / 1e6:F6} MHz", new(plot.Left + 8, plot.Bottom - 18), Muted, 11);
            }
        }
    }

    private ListenChannel? Hit(double x) => channels.OrderBy(c => Math.Abs(X(c.FrequencyHz) - x))
        .FirstOrDefault(c => Math.Abs(X(c.FrequencyHz) - x) <= Math.Max(9, ListenChannel.BandwidthHz / span * Plot.Width / 2));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!IsEnabled || !Plot.Contains(point.Position)) return;
        Focus();
        var hit = Hit(point.Position.X);
        if (point.Properties.IsRightButtonPressed)
        {
            if (hit is not null) RemoveRequested?.Invoke(hit.Id);
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            if (hit is not null)
            {
                dragging = hit.Id;
                dragFrequency = hit.FrequencyHz;
                dragOffset = hit.FrequencyHz - Frequency(point.Position.X);
                e.Pointer.Capture(this);
            }
            else if (e.ClickCount == 2) AddRequested?.Invoke(ClampFrequency(Snap(Frequency(point.Position.X), e.KeyModifiers)));
        }
        e.Handled = true;
        InvalidateVisual();
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        cursor = e.GetPosition(this);
        if (dragging is not null) dragFrequency = ClampFrequency(Snap(Frequency(cursor.Value.X) + dragOffset, e.KeyModifiers));
        Cursor = new Cursor(Hit(cursor.Value.X) is not null || dragging is not null ? StandardCursorType.SizeWestEast : StandardCursorType.Cross);
        InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var id = dragging;
        dragging = null;
        e.Pointer.Capture(null);
        if (id is not null) RetuneRequested?.Invoke(id.Value, dragFrequency);
        InvalidateVisual();
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragging = null;
        InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        cursor = null;
        InvalidateVisual();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double anchor = Frequency(e.GetPosition(this).X);
        double ratio = Math.Pow(.8, e.Delta.Y);
        double newSpan = Math.Clamp(span * ratio, Math.Min(25000, rate), rate);
        viewCenter = anchor + (viewCenter - anchor) * newSpan / span;
        span = newSpan;
        viewCenter = Math.Clamp(viewCenter, center - rate / 2 + span / 2, center + rate / 2 - span / 2);
        RangeChanged?.Invoke(center, rate, viewCenter, span);
        e.Handled = true;
        InvalidateVisual();
    }
}
