using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;
using Wpf = System.Windows.Media;

namespace DynamicIsland;

/// <summary>Menu colours for an island theme: the surface is the theme's own colour, lifted a little from pure black.</summary>
readonly record struct MenuPalette(Wpf.Color Back, Wpf.Color Hover, Wpf.Color Text, Wpf.Color Muted, Wpf.Color Line, Wpf.Color Border, Wpf.Color Accent)
{
    public static MenuPalette For(string theme)
    {
        if (theme == IslandThemes.Light)
            return new(Rgb(0xF9, 0xF9, 0xFB), Argb(0x14, 0, 0, 0), Rgb(0x1C, 0x1C, 0x1E), Rgb(0x6E, 0x6E, 0x73),
                Argb(0x1A, 0, 0, 0), Argb(0x24, 0, 0, 0), Rgb(0x00, 0x7A, 0xFF));

        var color = IslandThemes.Of(theme);
        // the glass theme is a translucent tint: a menu can't blur, so it gets a solid smoky grey
        var back = theme == IslandThemes.Glass ? Rgb(0x22, 0x22, 0x27) : Lift(color);
        return new(back, Argb(0x24, 0xFF, 0xFF, 0xFF), Rgb(0xF5, 0xF5, 0xF7), Rgb(0x98, 0x98, 0x9F),
            Argb(0x1F, 0xFF, 0xFF, 0xFF), Argb(0x2E, 0xFF, 0xFF, 0xFF), AccentOf(color));
    }

    // Pure black menus read as holes on a dark desktop: give every dark theme a little light.
    static Wpf.Color Lift(Wpf.Color c) => Rgb(
        (byte)Math.Min(255, c.R + 0x14), (byte)Math.Min(255, c.G + 0x14), (byte)Math.Min(255, c.B + 0x16));

    // A bright version of the theme's hue for checks and highlights; greys get the system blue.
    static Wpf.Color AccentOf(Wpf.Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max - min < 0.04) return Rgb(0x0A, 0x84, 0xFF);
        double hue;
        if (max == r) hue = 60 * (((g - b) / (max - min) + 6) % 6);
        else if (max == g) hue = 60 * ((b - r) / (max - min) + 2);
        else hue = 60 * ((r - g) / (max - min) + 4);
        return FromHsl(hue, 0.85, 0.66);
    }

    static Wpf.Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Rgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    static Wpf.Color Rgb(byte r, byte g, byte b) => Wpf.Color.FromRgb(r, g, b);
    static Wpf.Color Argb(byte a, byte r, byte g, byte b) => Wpf.Color.FromArgb(a, r, g, b);

    /// <summary>The WPF brushes the island's right-click menu binds to (App.xaml styles), recoloured in place.</summary>
    public void ApplyToResources()
    {
        Set("MenuBack", Back);
        Set("MenuHover", Hover);
        Set("MenuText", Text);
        Set("MenuMuted", Muted);
        Set("MenuLine", Line);
        Set("MenuBorder", Border);
        Set("MenuAccent", Accent);
    }

    static void Set(string key, Wpf.Color color)
    {
        var resources = System.Windows.Application.Current.Resources;
        if (resources[key] is Wpf.SolidColorBrush { IsFrozen: false } brush) brush.Color = color;
        else resources[key] = new Wpf.SolidColorBrush(color);
    }
}

/// <summary>
/// Paints the tray menu (WinForms) in the island's theme: theme-coloured surface, rounded hover pills,
/// an accent check, thin separators and, on Windows 11, rounded corners with a matching hairline border.
/// </summary>
sealed class IslandMenuRenderer(Func<string> theme) : WinForms.ToolStripRenderer
{
    MenuPalette P => MenuPalette.For(theme());

    static Color C(Wpf.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Fonts, spacing and this renderer on a menu and every submenu (call again after rebuilding items).</summary>
    public static void Style(WinForms.ToolStrip strip, IslandMenuRenderer renderer)
    {
        strip.Renderer = renderer;
        strip.Font = MenuFont;
        strip.Padding = new WinForms.Padding(0, 5, 0, 5);
        if (strip is WinForms.ToolStripDropDown dropDown)
        {
            dropDown.Opened -= OnOpened;
            dropDown.Opened += OnOpened;
        }
        foreach (WinForms.ToolStripItem item in strip.Items)
        {
            if (item is WinForms.ToolStripSeparator) continue;
            item.Padding = new WinForms.Padding(2, 4, 2, 4);
            if (item.Font.Bold) item.Font = BoldFont;
            if (item is WinForms.ToolStripMenuItem { HasDropDownItems: true } parent) Style(parent.DropDown, renderer);
        }
    }

    static readonly Font MenuFont = CreateFont();
    static readonly Font BoldFont = new(MenuFont, FontStyle.Bold);

    static Font CreateFont()
    {
        using var probe = new Font("Segoe UI Variable Text", 9.75f);
        return probe.Name == "Segoe UI Variable Text" ? new Font("Segoe UI Variable Text", 9.75f) : new Font("Segoe UI", 9.5f);
    }

    // Windows 11: round the popup and colour DWM's border; older Windows ignores both attributes.
    static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not WinForms.ToolStripDropDown { Renderer: IslandMenuRenderer renderer } menu) return;
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var border = renderer.P.Border;
        // COLORREF is 0x00BBGGRR; DWM borders are opaque, so blend the hairline onto the surface first
        var back = renderer.P.Back;
        double a = border.A / 255.0;
        int Mix(byte f, byte b) => (int)Math.Round(f * a + b * (1 - a));
        var colorRef = Mix(border.R, back.R) | Mix(border.G, back.G) << 8 | Mix(border.B, back.B) << 16;
        DwmSetWindowAttribute(menu.Handle, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
    }

    static bool Windows11 => Environment.OSVersion.Version.Build >= 22000;

    protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e) =>
        e.Graphics.Clear(C(P.Back));

    protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
    {
        if (Windows11) return;  // DWM draws the rounded border
        using var pen = new Pen(C(Blend(P.Border, P.Back)));
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(WinForms.ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        if (!item.Selected || !item.Enabled) return;
        var r = new RectangleF(5, 1, item.Width - 10, item.Height - 2);
        Smooth(e.Graphics);
        using var path = Rounded(r, 6);
        using var brush = new SolidBrush(C(P.Hover));
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = C(e.Item.Enabled ? P.Text : P.Muted);
        // the shortcut ("Ctrl+Alt+D") is drawn as a second text run aligned right: make it quieter
        if (e.Item is WinForms.ToolStripMenuItem { ShortcutKeyDisplayString: { Length: > 0 } keys } && e.Text == keys)
            e.TextColor = C(P.Muted);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
    {
        // a thin chevron instead of the solid triangle
        Smooth(e.Graphics);
        var r = e.ArrowRectangle;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = 3.2f;
        using var pen = new Pen(C(P.Muted), 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, new[] { new PointF(cx - s / 2, cy - s), new PointF(cx + s / 2, cy), new PointF(cx - s / 2, cy + s) });
    }

    protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(C(Blend(P.Line, P.Back)));
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
    {
        Smooth(e.Graphics);
        var r = e.ImageRectangle;
        if (e.Item.Tag is Wpf.Color)
        {
            // theme swatch: an accent ring around the chosen colour (the swatch itself is drawn by OnRenderItemImage)
            using var ring = new Pen(C(P.Accent), 1.6f);
            e.Graphics.DrawEllipse(ring, r.X - 2.5f, r.Y - 2.5f, r.Width + 4, r.Height + 4);
            return;
        }
        // a check mark drawn as a path: crisp at any DPI, in the theme's accent
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        using var pen = new Pen(C(P.Accent), Math.Max(1.6f, w / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(x + w * 0.18f, y + h * 0.52f),
            new PointF(x + w * 0.40f, y + h * 0.74f),
            new PointF(x + w * 0.84f, y + h * 0.28f),
        });
    }

    protected override void OnRenderItemImage(WinForms.ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item.Tag is not Wpf.Color swatch) { base.OnRenderItemImage(e); return; }
        Smooth(e.Graphics);
        var r = e.ImageRectangle;
        using var fill = new SolidBrush(Color.FromArgb(255, swatch.R, swatch.G, swatch.B));
        e.Graphics.FillEllipse(fill, r.X, r.Y, r.Width - 1, r.Height - 1);
        using var edge = new Pen(C(Blend(P.Border, P.Back)), 1f);
        e.Graphics.DrawEllipse(edge, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    static Wpf.Color Blend(Wpf.Color top, Wpf.Color under)
    {
        double a = top.A / 255.0;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Wpf.Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A placeholder image so the menu reserves the image slot where a theme swatch is painted.</summary>
    public static Image SwatchSlot { get; } = new Bitmap(16, 16);

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
