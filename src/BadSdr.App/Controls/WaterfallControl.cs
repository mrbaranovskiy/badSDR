using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BadSdr.Core.Dsp;

namespace BadSdr.App.Controls;

/// <summary>Bounded display history. Only the UI thread touches the bitmap.</summary>
public sealed class WaterfallControl : Control
{
    internal const int HistoryCapacity = 256;
    private static readonly IBrush BackgroundBrush = Brush.Parse("#0B1420");
    private static readonly IBrush Muted = Brush.Parse("#8094B2");
    private static readonly int[] Palette = CreatePalette();
    private WriteableBitmap? bitmap;
    private int[] row = [];
    private SpectrumFrame? lastFrame;
    private int head;
    private double center = 435e6, rate = 1e6;
    internal int HistoryCount { get; private set; }
    internal double ViewCenter { get; private set; } = 435e6;
    internal double ViewSpan { get; private set; } = 1e6;

    public WaterfallControl()
    {
        ClipToBounds = true;
        MinHeight = 90;
    }

    public void SetRange(double frequency, double sampleRate, double viewCenter, double span)
    {
        if (center != frequency || rate != sampleRate)
        {
            Clear();
            center = frequency;
            rate = sampleRate;
        }
        ViewCenter = viewCenter;
        ViewSpan = span;
        InvalidateVisual();
    }

    public void Clear()
    {
        // Old pixels are never drawn until overwritten; no full bitmap clear is needed.
        HistoryCount = head = 0;
        lastFrame = null;
        InvalidateVisual();
    }

    public void SetFrame(SpectrumFrame? frame)
    {
        if (frame is null || ReferenceEquals(frame, lastFrame) || frame.PowerDb.Length == 0) return;
        if (center != frame.CenterFrequencyHz || rate != frame.SampleRate)
            SetRange(frame.CenterFrequencyHz, frame.SampleRate, frame.CenterFrequencyHz, frame.SampleRate);
        if (bitmap is null || row.Length != frame.PowerDb.Length)
        {
            bitmap?.Dispose();
            row = new int[frame.PowerDb.Length];
            bitmap = new WriteableBitmap(new PixelSize(row.Length, HistoryCapacity), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            Clear();
        }
        for (int i = 0; i < row.Length; i++)
        {
            float db = frame.PowerDb[i];
            int index = float.IsFinite(db) ? (int)Math.Clamp((db + 110) / 90 * 255, 0, 255) : 0;
            row[i] = Palette[index];
        }
        head = (head + HistoryCapacity - 1) % HistoryCapacity;
        using (var buffer = bitmap.Lock())
            Marshal.Copy(row, 0, IntPtr.Add(buffer.Address, head * buffer.RowBytes), row.Length);
        HistoryCount = Math.Min(HistoryCount + 1, HistoryCapacity);
        lastFrame = frame;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        // Match the spectrum plot's horizontal margins exactly.
        var plot = new Rect(43, 4, Math.Max(1, Bounds.Width - 52), Math.Max(1, Bounds.Height - 8));
        Text(context, "New", new Point(5, plot.Top));
        Text(context, "Older", new Point(5, plot.Bottom - 12));
        if (bitmap is null || HistoryCount == 0)
        {
            Text(context, "Waiting for IQ samples", new Point(plot.Left + 24, plot.Top + plot.Height / 2 - 6));
            return;
        }
        double sourceX = (ViewCenter - ViewSpan / 2 - (center - rate / 2)) / rate * row.Length;
        double sourceWidth = ViewSpan / rate * row.Length;
        double rowHeight = plot.Height / HistoryCapacity;
        int first = Math.Min(HistoryCount, HistoryCapacity - head);
        using (context.PushClip(plot))
        {
            context.DrawImage(bitmap, new Rect(sourceX, head, sourceWidth, first),
                new Rect(plot.Left, plot.Top, plot.Width, first * rowHeight));
            int remaining = HistoryCount - first;
            if (remaining > 0)
                context.DrawImage(bitmap, new Rect(sourceX, 0, sourceWidth, remaining),
                    new Rect(plot.Left, plot.Top + first * rowHeight, plot.Width, remaining * rowHeight));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        bitmap?.Dispose();
        bitmap = null;
        Clear();
    }

    private static void Text(DrawingContext context, string text, Point point) => context.DrawText(
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter"), 9, Muted), point);

    private static int[] CreatePalette()
    {
        Color[] stops = [Color.Parse("#0B1420"), Color.Parse("#183B89"), Color.Parse("#157CB5"),
            Color.Parse("#46D7C7"), Color.Parse("#F2D66A"), Color.Parse("#FFF6DF")];
        var colors = new int[256];
        for (int i = 0; i < colors.Length; i++)
        {
            double position = i / 255.0 * (stops.Length - 1);
            int stop = Math.Min((int)position, stops.Length - 2);
            double fraction = position - stop;
            var a = stops[stop];
            var b = stops[stop + 1];
            int r = (int)(a.R + (b.R - a.R) * fraction);
            int g = (int)(a.G + (b.G - a.G) * fraction);
            int blue = (int)(a.B + (b.B - a.B) * fraction);
            colors[i] = unchecked((int)0xFF000000) | r << 16 | g << 8 | blue;
        }
        return colors;
    }
}
