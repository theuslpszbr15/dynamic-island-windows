using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;

namespace DynamicIsland;

/// <summary>
/// Picks a part of the screen: the monitor is frozen (captured first, so nothing moves while choosing), dimmed, and the
/// dragged rectangle stays clear. Esc or a right click cancels.
/// </summary>
public sealed class SnipWindow : Window
{
    readonly Drawing.Bitmap _shot;
    readonly Drawing.Rectangle _screen;
    readonly RectangleGeometry _hole = new();
    readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1.5, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    Point? _start;

    /// <summary>The chosen part of the screen, or null when cancelled.</summary>
    public Drawing.Bitmap? Result { get; private set; }

    public SnipWindow(Drawing.Rectangle screen)
    {
        _screen = screen;
        _shot = Services.ScreenOcr.Capture(screen);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;

        var handle = _shot.GetHbitmap();
        BitmapSource frozen;
        try { frozen = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
        finally { Native.DeleteObject(handle); }
        frozen.Freeze();

        var dim = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)), IsHitTestVisible = false };
        var root = new Grid();
        root.Children.Add(new Image { Source = frozen, Stretch = Stretch.Fill });
        root.Children.Add(dim);
        root.Children.Add(new Canvas { Children = { _frame } });
        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1E)), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 60, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = "Arraste sobre o texto · Esc cancela", Foreground = Brushes.White, FontSize = 14 },
        });
        Content = root;
        SizeChanged += (_, _) => dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(RenderSize)), _hole);

        SourceInitialized += (_, _) =>
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, screen.X, screen.Y, screen.Width, screen.Height, 0);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseRightButtonUp += (_, _) => Close();
        MouseLeftButtonDown += (_, e) =>
        {
            _start = e.GetPosition(this);
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            if (_start is not { } start) return;
            var rect = new Rect(start, e.GetPosition(this));
            _hole.Rect = rect;
            Canvas.SetLeft(_frame, rect.X);
            Canvas.SetTop(_frame, rect.Y);
            _frame.Width = rect.Width;
            _frame.Height = rect.Height;
            _frame.Visibility = Visibility.Visible;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_start is not { } start) return;
            ReleaseMouseCapture();
            var rect = new Rect(start, e.GetPosition(this));
            _start = null;
            if (rect.Width < 6 || rect.Height < 6) return; // a click, not a selection
            // window units → the captured pixels
            double sx = _shot.Width / ActualWidth, sy = _shot.Height / ActualHeight;
            var area = Drawing.Rectangle.Intersect(new Drawing.Rectangle(0, 0, _shot.Width, _shot.Height),
                new Drawing.Rectangle((int)(rect.X * sx), (int)(rect.Y * sy), (int)Math.Ceiling(rect.Width * sx), (int)Math.Ceiling(rect.Height * sy)));
            if (area.Width > 0 && area.Height > 0) Result = _shot.Clone(area, _shot.PixelFormat);
            Close();
        };
        Closed += (_, _) => _shot.Dispose();
    }
}
