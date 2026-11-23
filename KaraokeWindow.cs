using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Karaoke: the synced lyrics full screen on the island's monitor, over the album art washed out and blurred. The line
/// being sung is big and bright, the ones around it fade. Esc or a click closes it.
/// </summary>
public sealed class KaraokeWindow : Window
{
    readonly IslandWindow _island;
    readonly Rectangle _backdrop = new() { Opacity = 0.75 };
    readonly TextBlock _previous = Line(30, 0.3), _current = Line(56, 1, bold: true), _next = Line(32, 0.55), _after = Line(28, 0.28);
    readonly StackPanel _lines = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(140, 0, 140, 0) };
    readonly TextBlock _song = new() { FontSize = 18, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Border _progress = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
    readonly Grid _track = new() { Height = 4, Margin = new Thickness(0, 12, 0, 0) };
    readonly DispatcherTimer _timer;
    readonly bool _fullScreen;
    MediaSnapshot? _shownSong;
    int _shownIndex = int.MinValue;

    public KaraokeWindow(IslandWindow island, bool fullScreen = true)
    {
        _island = island;
        _fullScreen = fullScreen;
        Title = "Letra";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = fullScreen;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        Cursor = Cursors.Hand;

        _lines.Children.Add(_previous);
        _lines.Children.Add(_current);
        _lines.Children.Add(_next);
        _lines.Children.Add(_after);
        _current.Margin = new Thickness(0, 18, 0, 18);

        _track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)) });
        _track.Children.Add(_progress);
        var footer = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(140, 0, 140, 64) };
        footer.Children.Add(_song);
        footer.Children.Add(_track);

        var hint = new TextBlock
        {
            Text = "Esc ou clique para sair", FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 36, 0),
        };
        var shade = new Rectangle
        {
            Fill = new LinearGradientBrush(Color.FromArgb(0x66, 0, 0, 0), Color.FromArgb(0xE6, 0, 0, 0), 90),
        };
        var root = new Grid();
        root.Children.Add(_backdrop);
        root.Children.Add(shade);
        root.Children.Add(_lines);
        root.Children.Add(footer);
        root.Children.Add(hint);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonUp += (_, _) => Close();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Normal, (_, _) => Refresh(), Dispatcher);
        SourceInitialized += (_, _) => { if (_fullScreen) CoverIslandScreen(); };
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
            Activate();
        };
        Closed += (_, _) => _timer.Stop();
    }

    static TextBlock Line(double size, double opacity, bool bold = false) => new()
    {
        FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold, Opacity = opacity, Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 6),
    };

    void CoverIslandScreen()
    {
        var b = _island.ScreenBounds;
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, 0);
    }

    public void Refresh()
    {
        var song = _island.Song;
        var lines = _island.LyricLines;
        if (!ReferenceEquals(song, _shownSong))
        {
            if (song?.Title != _shownSong?.Title || song?.Artist != _shownSong?.Artist) ShowSong(song);
            _shownSong = song;
        }
        if (song != null && song.Duration > TimeSpan.FromSeconds(1))
        {
            var width = _track.ActualWidth * Math.Clamp(song.PositionNow() / song.Duration, 0, 1);
            _progress.Width = Math.Max(0, width);
        }

        if (song == null || lines is not { Count: > 0 })
        {
            Set(-2, song == null ? "Nada tocando agora" : "Sem letra sincronizada para esta música", "", "", "");
            return;
        }
        var i = LyricsService.IndexAt(lines, song.PositionNow());
        string At(int index) => index >= 0 && index < lines.Count ? (lines[index].Text.Length == 0 ? "♪" : lines[index].Text) : "";
        Set(i, i < 0 ? "♪" : At(i), At(i - 1), At(i + 1), At(i + 2));
    }

    void Set(int index, string current, string previous, string next, string after)
    {
        if (index == _shownIndex && _current.Text == current) return;
        var moved = index != _shownIndex && _shownIndex != int.MinValue;
        _shownIndex = index;
        _current.Text = current;
        _previous.Text = previous;
        _next.Text = next;
        _after.Text = after;
        if (!moved) return;
        var rise = new TranslateTransform();
        _lines.RenderTransform = rise;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        _current.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
    }

    void ShowSong(MediaSnapshot? song)
    {
        _song.Text = song == null ? "" : string.IsNullOrEmpty(song.Artist) ? song.Title : $"{song.Title} — {song.Artist}";
        var accent = song?.Accent ?? MediaService.DefaultAccent;
        _progress.Background = new SolidColorBrush(accent);
        // a tiny copy of the cover stretched over the screen is the blur, for free (no per-frame blur in software rendering)
        _backdrop.Fill = song?.Art is { } art
            ? new ImageBrush(new TransformedBitmap(art, new ScaleTransform(12.0 / art.PixelWidth, 12.0 / art.PixelHeight))) { Stretch = Stretch.UniformToFill }
            : new RadialGradientBrush(Color.FromArgb(0xAA, accent.R, accent.G, accent.B), Colors.Black);
        if (_backdrop.Fill is ImageBrush brush) RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
    }
}
