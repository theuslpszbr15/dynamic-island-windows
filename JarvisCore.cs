using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>
/// JARVIS's "núcleo" (public/app.js, drawCore) redrawn in WPF: four slow arcs and a crown of 96 ticks that dance while
/// Jarvis listens, thinks or speaks (orange while it listens). Animates only while it is on screen.
/// </summary>
public sealed class JarvisCore : FrameworkElement
{
    public enum Mood { Idle, Listening, Active }

    static readonly Pen[] Rings =
    [
        Frozen(0x51, 0x7E, 0x69, 1), Frozen(0x83, 0xD4, 0xB4, 2), Frozen(0x3F, 0x65, 0x56, 1), Frozen(0x2D, 0x45, 0x3B, 1),
    ];
    static readonly Pen TickBright = Frozen(0xD6, 0xF9, 0xDF, 1.4), Tick = Frozen(0x4D, 0x95, 0x79, 1.4), TickListen = Frozen(0xE9, 0x9A, 0x59, 1.4);
    static readonly Brush Marker = FrozenBrush(0xE9, 0x9A, 0x59);
    const int Ticks = 96;
    // JARVIS draws on a ~340 px canvas: tick lengths are in those pixels
    const double DesignSize = 340;

    Mood _mood;
    TimeSpan _last;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public Mood State
    {
        get => _mood;
        set { _mood = value; InvalidateVisual(); }
    }

    public JarvisCore()
    {
        IsVisibleChanged += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            if (IsVisible) CompositionTarget.Rendering += OnFrame;
        };
    }

    void OnFrame(object? sender, EventArgs e)
    {
        // 30 fps while active, 12 when idle (the arcs only drift)
        var interval = TimeSpan.FromMilliseconds(_mood == Mood.Idle ? 83 : 33);
        if (_clock.Elapsed - _last < interval) return;
        _last = _clock.Elapsed;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var scale = size / DesignSize;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var t = SystemParameters.ClientAreaAnimation ? _clock.Elapsed.TotalSeconds : 0;
        var radius = size * 0.32;
        var active = _mood != Mood.Idle;

        for (var ring = 0; ring < Rings.Length; ring++)
        {
            var offset = t * (ring % 2 == 1 ? 0.13 : -0.09);
            Arc(dc, Rings[ring], center, radius + ring * size * 0.033, offset + 0.2, offset + Math.PI * (ring == 1 ? 1.65 : 1.94));
        }

        var inner = radius * 0.78;
        for (var i = 0; i < Ticks; i++)
        {
            var angle = i / (double)Ticks * Math.PI * 2;
            var length = (active ? 5 + (Math.Sin(t * 6 + i * 0.8) + 1) * 8 : i % 4 == 0 ? 9 : 3) * scale;
            var pen = _mood == Mood.Listening ? TickListen : i % 8 == 0 ? TickBright : Tick;
            var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
            dc.DrawLine(pen, new Point(center.X + cos * inner, center.Y + sin * inner),
                new Point(center.X + cos * (inner + length), center.Y + sin * (inner + length)));
        }
        dc.DrawRectangle(Marker, null, new Rect(center.X - 2 * scale, center.Y - radius - size * 0.105, 4 * scale, 8 * scale));
    }

    static void Arc(DrawingContext dc, Pen pen, Point c, double r, double from, double to)
    {
        var start = new Point(c.X + r * Math.Cos(from), c.Y + r * Math.Sin(from));
        var end = new Point(c.X + r * Math.Cos(to), c.Y + r * Math.Sin(to));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false, false);
            g.ArcTo(end, new Size(r, r), 0, to - from > Math.PI, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    static Pen Frozen(byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(FrozenBrush(r, g, b), thickness);
        pen.Freeze();
        return pen;
    }

    static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
