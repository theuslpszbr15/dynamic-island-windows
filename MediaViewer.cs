using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Shows a picture (wheel zooms, drag moves, double click fits) or plays a video or gif, in the same dark window as
/// the settings. ← and → go through the list it was opened from; Space plays and pauses; Esc closes. Videos play
/// with what Windows has (mp4, mkv and webm on Windows 10/11); anything it can't play gets "Abrir com…".
/// </summary>
public sealed class MediaViewer : DarkWindow
{
    readonly IReadOnlyList<string> _paths;
    int _index;
    readonly Grid _stage = new() { Background = Frozen(0xFF0E0E10), ClipToBounds = true };
    readonly TextBlock _name = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _detail = new() { FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    readonly StackPanel _controls = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // picture
    readonly ScaleTransform _zoom = new();
    readonly TranslateTransform _pan = new();
    Point? _dragFrom;

    // video
    MediaElement? _media;
    Slider? _seek;
    TextBlock? _time;
    Button? _play;
    bool _playing, _seeking;

    public MediaViewer(IReadOnlyList<string> paths, int index) : base("Visualizar — Dynamic Island", "Visualizar", [])
    {
        _paths = paths;
        _index = Math.Clamp(index, 0, Math.Max(0, paths.Count - 1));
        Width = 1000;
        Height = 720;
        MinWidth = 480;
        MinHeight = 360;

        var body = new DockPanel();
        var bottom = new Border { Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 8, 10, 8) };
        var bar = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Action("", "Anterior (←)", () => Step(-1)));
        actions.Children.Add(Action("", "Próximo (→)", () => Step(1)));
        actions.Children.Add(Action("", "Mostrar na pasta", () => Reveal(Current)));
        actions.Children.Add(Action("", "Abrir com…", () => OpenWith(Current)));
        DockPanel.SetDock(actions, Dock.Right);
        bar.Children.Add(actions);
        DockPanel.SetDock(_controls, Dock.Right);
        bar.Children.Add(_controls);
        var names = new StackPanel { Orientation = Orientation.Horizontal };
        names.Children.Add(_name);
        names.Children.Add(_detail);
        bar.Children.Add(names);
        bottom.Child = bar;
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(bottom);
        body.Children.Add(_stage);
        SetBody(body);

        _stage.MouseWheel += OnWheel;
        _stage.MouseLeftButtonDown += OnDown;
        _stage.MouseMove += OnMove;
        _stage.MouseLeftButtonUp += (_, _) => { _dragFrom = null; _stage.ReleaseMouseCapture(); };
        _clock.Tick += (_, _) => Tick();
        PreviewKeyDown += OnKey;
        Closed += (_, _) => Stop();
        Show(_index);
    }

    string Current => _paths.Count > 0 ? _paths[_index] : "";

    protected override void Fill(string section, Panel page) { }

    /// <summary>Another list (or another item) in the window that is already open.</summary>
    public void Show(int index)
    {
        Stop();
        _stage.Children.Clear();
        _controls.Children.Clear();
        if (_paths.Count == 0) return;
        _index = (index % _paths.Count + _paths.Count) % _paths.Count;
        var path = Current;
        _name.Text = Path.GetFileName(path);
        _detail.Text = _paths.Count > 1 ? $"{_index + 1} de {_paths.Count}" : "";
        try { _detail.Text = $"{SizeText(new FileInfo(path).Length)}{(_detail.Text.Length > 0 ? " · " + _detail.Text : "")}"; } catch { }
        if (!File.Exists(path)) Problem("O arquivo não está mais aí", "Ele foi apagado ou mudou de pasta.");
        else if (MediaFiles.IsVideo(path)) ShowVideo(path);
        else ShowPicture(path);
    }

    void Step(int by)
    {
        if (_paths.Count > 1) Show(_index + by);
    }

    // ───────────────────────── picture ─────────────────────────

    void ShowPicture(string path)
    {
        BitmapSource? picture = null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read now, so the file isn't kept open
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            picture = image;
        }
        catch { picture = MediaFiles.Thumbnail(path, 1600); } // no WIC codec (heic, avif…): Windows' own preview
        if (picture == null)
        {
            Problem("O Windows não consegue mostrar essa imagem", "Use Abrir com… para escolher um app.");
            return;
        }
        _zoom.ScaleX = _zoom.ScaleY = 1;
        _pan.X = _pan.Y = 0;
        var view = new Image
        {
            Source = picture, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            RenderTransformOrigin = new Point(0.5, 0.5), Margin = new Thickness(12),
            RenderTransform = new TransformGroup { Children = { _zoom, _pan } },
        };
        RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality);
        _stage.Children.Add(view);
        _detail.Text += $" · {picture.PixelWidth}×{picture.PixelHeight}";
        _controls.Children.Add(Action("", "Diminuir", () => Zoom(1 / 1.25)));
        _controls.Children.Add(Action("", "Aumentar", () => Zoom(1.25)));
        _controls.Children.Add(Action("", "Caber na janela (duplo clique)", Fit));
    }

    void Zoom(double by)
    {
        var scale = Math.Clamp(_zoom.ScaleX * by, 1, 8);
        _zoom.ScaleX = _zoom.ScaleY = scale;
        if (scale == 1) _pan.X = _pan.Y = 0;
    }

    void Fit()
    {
        _zoom.ScaleX = _zoom.ScaleY = 1;
        _pan.X = _pan.Y = 0;
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_media != null) return;
        Zoom(e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_media != null)
        {
            TogglePlay();
            return;
        }
        if (e.ClickCount == 2) { Fit(); return; }
        if (_zoom.ScaleX <= 1) return;
        _dragFrom = e.GetPosition(_stage);
        _stage.CaptureMouse();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var now = e.GetPosition(_stage);
        _pan.X += now.X - from.X;
        _pan.Y += now.Y - from.Y;
        _dragFrom = now;
    }

    // ───────────────────────── video and gif ─────────────────────────

    void ShowVideo(string path)
    {
        var gif = MediaFiles.IsGif(path);
        _media = new MediaElement
        {
            Source = new Uri(path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Volume = 0.8, IsMuted = gif,
        };
        _media.MediaOpened += (_, _) =>
        {
            if (_media.NaturalDuration.HasTimeSpan && _seek != null) _seek.Maximum = _media.NaturalDuration.TimeSpan.TotalSeconds;
            if (_media.NaturalVideoWidth > 0) _detail.Text += $" · {_media.NaturalVideoWidth}×{_media.NaturalVideoHeight}";
        };
        _media.MediaEnded += (_, _) =>
        {
            _media.Position = TimeSpan.Zero;
            if (gif) _media.Play(); // gifs loop
            else SetPlaying(false);
        };
        _media.MediaFailed += (_, _) =>
        {
            Stop();
            _stage.Children.Clear();
            _controls.Children.Clear();
            Problem("O Windows não consegue tocar esse vídeo aqui", "Use Abrir com… para escolher um app (VLC, Filmes e TV…).");
        };
        _stage.Children.Add(_media);
        if (!gif)
        {
            _play = Action("", "Tocar / pausar (espaço)", TogglePlay);
            _seek = new Slider { Width = 260, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0), Focusable = false };
            _seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _seeking = true));
            _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _seeking = false; SeekTo(_seek.Value); }));
            _seek.PreviewMouseLeftButtonUp += (_, _) => { if (!_seeking) SeekTo(_seek.Value); };
            _time = new TextBlock { FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, MinWidth = 86 };
            _controls.Children.Add(_play);
            _controls.Children.Add(_seek);
            _controls.Children.Add(_time);
            _controls.Children.Add(Action("", "Som ligado / mudo", () => { if (_media != null) _media.IsMuted = !_media.IsMuted; }));
        }
        _media.Play();
        SetPlaying(true);
        _clock.Start();
    }

    void SeekTo(double seconds)
    {
        if (_media != null) _media.Position = TimeSpan.FromSeconds(seconds);
    }

    void TogglePlay()
    {
        if (_media == null || MediaFiles.IsGif(Current)) return;
        if (_playing) _media.Pause(); else _media.Play();
        SetPlaying(!_playing);
    }

    void SetPlaying(bool playing)
    {
        _playing = playing;
        if (_play?.Content is TextBlock glyph) glyph.Text = playing ? "" : "";
    }

    void Tick()
    {
        if (_media == null || _seek == null || _time == null) return;
        var at = _media.Position;
        if (!_seeking) _seek.Value = at.TotalSeconds;
        var total = _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        _time.Text = $"{Clock(at)} / {Clock(total)}";
    }

    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void Stop()
    {
        _clock.Stop();
        if (_media != null)
        {
            try { _media.Stop(); _media.Close(); } catch { }
            _media = null;
        }
        _seek = null;
        _time = null;
        _play = null;
        _playing = false;
    }

    // ───────────────────────── bits ─────────────────────────

    void Problem(string title, string text)
    {
        var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 30, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center });
        box.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4), HorizontalAlignment = HorizontalAlignment.Center });
        box.Children.Add(new TextBlock { Text = text, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center });
        _stage.Children.Add(box);
    }

    Button Action(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 },
            Style = (Style)Resources["Action"], ToolTip = tip, Margin = new Thickness(6, 0, 0, 0), Focusable = false,
        };
        button.Click += (_, _) => click();
        return button;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left: Step(-1); break;
            case Key.Right: Step(1); break;
            case Key.Space: TogglePlay(); break;
            default: return;
        }
        e.Handled = true;
    }

    // Shown in Explorer, never run from here.
    static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    // Windows' own "Open with" list: the person picks the app.
    static void OpenWith(string path)
    {
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false }); } catch { }
    }

    static string SizeText(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / (1024.0 * 1024.0):0.#} MB";
}
