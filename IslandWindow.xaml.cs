using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

public partial class IslandWindow : Window
{
    enum Transient { None, Volume, Battery, NowPlaying, AgentAlert }
    enum CompactKind { Clock, Media, Agent }

    const int PageMedia = 0, PageSystem = 1, PageFiles = 2, PageAgents = 3;
    static readonly string[] PageTitles = ["Tocando agora", "Sistema", "Bandeja de arquivos", "Agentes"];
    static readonly Size ExpandedSize = new(450, 190);
    static readonly Duration Quick = TimeSpan.FromMilliseconds(140);

    readonly IslandSettings _settings;
    readonly MediaService _media;
    readonly VolumeService _volume;
    readonly BatteryService _battery;
    readonly AgentHub _hub;
    readonly SystemMonitor _monitor = new();

    readonly Spring _w = new(132), _h = new(34);
    bool _animating;
    TimeSpan _lastFrame;

    readonly Spring _top = new(8);
    bool _expanded, _hover, _dragOver, _draggingOut, _menuOpen, _ready, _hiddenFullscreen, _compactPulsing;
    bool _clickThrough, _dwellBlocked, _dragCandidate;
    DateTime _lastDragOver, _lastWheel, _hoverSince;
    DateTime? _buttonSince;
    Transient _transient;
    int _page, _liveTicks;
    MediaSnapshot? _mediaSnap;
    string? _trackKey;
    IntPtr _hwnd;

    readonly DispatcherTimer _transientTimer, _hoverTimer, _clockTimer, _liveTimer, _guardTimer, _dragTimer, _pollTimer;
    readonly List<Rectangle> _eqBars = [];
    readonly Border[] _dots = new Border[4];
    readonly Meter _cpu, _ram, _disk, _power;
    FileItem? _pressed;
    Point _pressPoint;

    public ObservableCollection<FileItem> Files { get; } = [];
    public ObservableCollection<AgentSession> Agents { get; } = [];

    public IslandWindow(IslandSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _media = new MediaService(Dispatcher);
        _volume = new VolumeService(Dispatcher);
        _battery = new BatteryService(Dispatcher);
        _hub = new AgentHub(Dispatcher);
        _page = Math.Clamp(settings.LastPage, 0, 3);

        _transientTimer = MakeTimer(1, () => { _transientTimer!.Stop(); _transient = Transient.None; Retarget(); });
        _hoverTimer = MakeTimer(0.2, CloseTick);
        _pollTimer = MakeTimer(0.033, PollTick);
        _clockTimer = MakeTimer(1, ClockTick);
        _liveTimer = MakeTimer(0.5, LiveTick);
        _guardTimer = MakeTimer(2, GuardTick);
        _dragTimer = MakeTimer(0.25, DragTick);

        FilesList.ItemsSource = Files;
        AgentsList.ItemsSource = Agents;
        foreach (var path in settings.TrayFiles)
            if (File.Exists(path) || Directory.Exists(path)) Files.Add(new FileItem(path));

        BuildEq(CompactEq, 4, 14);
        BuildEq(NowEq, 4, 16);
        BuildDots();
        _cpu = AddMeter("CPU");
        _ram = AddMeter("Memória");
        _disk = AddMeter("Disco");
        _power = AddMeter(_battery.HasBattery ? "Bateria" : "Ligado há");

        _media.Changed += OnMedia;
        _volume.Changed += OnVolume;
        _battery.Changed += OnBattery;
        _hub.MessageReceived += OnAgent;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    DispatcherTimer MakeTimer(double seconds, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => tick();
        return timer;
    }

    // ───────────────────────── lifecycle ─────────────────────────

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
            new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT));
        _clickThrough = true;
    }

    void SetClickThrough(bool on)
    {
        if (on == _clickThrough || _hwnd == IntPtr.Zero) return;
        _clickThrough = on;
        var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex = on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        ApplyShape();
        ClockTick();
        _clockTimer.Start();
        if (SnapshotMode) return;
        _guardTimer.Start();
        _pollTimer.Start();

        _volume.Start();
        _battery.Start();
        _hub.Start();
        await _media.StartAsync();

        await Task.Delay(1500);
        _ready = true;
    }

    void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(PositionWindow);

    void PositionWindow()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    public void Teardown()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _hub.Dispose();
        _volume.Dispose();
        Close();
    }

    public void ApplyShape()
    {
        CompactView.Height = CompactHeight;
        Retarget();
        ApplySize();
    }

    bool IsNotch => _settings.Shape == IslandShape.Notch;
    double CompactHeight => IsNotch ? 32 : 34;

    // Discreet idle: nothing urgent on screen, so shrink to a slim handle hugging the top edge.
    bool IsHandle => !_expanded && _transient == Transient.None && _settings.DiscreetIdle
                     && !Agents.Any(a => a.IsActive);

    // ───────────────────────── sizing & animation ─────────────────────────

    CompactKind Kind()
    {
        if (Agents.Any(a => a.IsActive)) return CompactKind.Agent;
        if (_mediaSnap?.IsPlaying == true) return CompactKind.Media;
        return CompactKind.Clock;
    }

    Size TargetSize()
    {
        if (_expanded) return ExpandedSize;
        switch (_transient)
        {
            case Transient.Volume:
            case Transient.Battery: return new(300, 40);
            case Transient.NowPlaying: return new(360, 62);
            case Transient.AgentAlert: return new(400, 78);
        }
        if (IsHandle) return new(110, 8);
        var h = CompactHeight;
        var extra = IsNotch ? 16 : 0;
        return Kind() switch
        {
            CompactKind.Agent => new(290 + extra, h),
            CompactKind.Media => new(220 + extra, h),
            _ => new(132 + extra, h),
        };
    }

    void Retarget()
    {
        var size = TargetSize();
        _w.Target = size.Width;
        _h.Target = size.Height;
        _top.Target = IsHandle || IsNotch ? 0 : 8;
        UpdateLayers();
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame == TimeSpan.Zero) { _lastFrame = now; return; }
        var dt = (now - _lastFrame).TotalSeconds;
        if (dt <= 0) return;
        _lastFrame = now;

        dt = Math.Min(dt, 1 / 30.0);
        _w.Step(dt);
        _h.Step(dt);
        _top.Step(dt);
        if (_w.Settled && _h.Settled && _top.Settled)
        {
            _w.Snap();
            _h.Snap();
            _top.Snap();
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
        ApplySize();
    }

    void ApplySize()
    {
        double w = Math.Max(24, _w.Value), h = Math.Max(4, _h.Value);
        Island.Width = w;
        Island.Height = h;
        IslandHost.Margin = new Thickness(0, Math.Max(0, _top.Value), 0, 0);
        var flatTop = IsNotch || _top.Value < 2;
        var r = flatTop ? Math.Min(h / 2, 24) : Math.Min(h / 2, 34);
        IslandBg.CornerRadius = flatTop ? new CornerRadius(0, 0, r, r) : new CornerRadius(r);
        IslandContent.Clip = flatTop
            ? new RectangleGeometry(new Rect(0, -r, w, h + r), r, r)
            : new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    void UpdateLayers()
    {
        var expanded = _expanded;
        var transient = !expanded && _transient != Transient.None;
        Fade(ExpandedView, expanded);
        Fade(TransientView, transient);
        var handle = IsHandle;
        Fade(CompactView, !expanded && !transient && !handle);
        Fade(HandleBar, handle && HandleColor() != null);
        if (HandleColor() is { } color) HandleBar.Background = new SolidColorBrush(color);
        EarLeft.Visibility = EarRight.Visibility = Vis(IsNotch && !handle);

        VolumePanel.Visibility = Vis(_transient == Transient.Volume);
        BatteryPanel.Visibility = Vis(_transient == Transient.Battery);
        NowPlayingPanel.Visibility = Vis(_transient == Transient.NowPlaying);
        AlertPanel.Visibility = Vis(_transient == Transient.AgentAlert);

        var kind = Kind();
        CompactClock.Visibility = Vis(kind == CompactKind.Clock);
        CompactMedia.Visibility = Vis(kind == CompactKind.Media);
        CompactAgent.Visibility = Vis(kind == CompactKind.Agent);
        if (kind == CompactKind.Agent) UpdateCompactAgent();
        if ((kind == CompactKind.Agent) != _compactPulsing)
        {
            _compactPulsing = kind == CompactKind.Agent;
            Pulse((ScaleTransform)CompactOrb.RenderTransform, _compactPulsing);
        }
    }

    Color? HandleColor() => Kind() switch
    {
        CompactKind.Agent => AgentSession.Working,
        CompactKind.Media => _mediaSnap?.Accent,
        _ => null,
    };

    static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    static void Fade(FrameworkElement element, bool show)
    {
        if (element.Tag is bool current && current == show) return;
        element.Tag = show;
        element.IsHitTestVisible = show;
        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 240 : 110))
        {
            BeginTime = show ? TimeSpan.FromMilliseconds(60) : TimeSpan.Zero,
        };
        element.BeginAnimation(OpacityProperty, fade);
        if (element.RenderTransform is ScaleTransform scale)
        {
            var grow = new DoubleAnimation(show ? 0.94 : 1, show ? 1 : 0.96, TimeSpan.FromMilliseconds(show ? 360 : 110))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    // ───────────────────────── expand / transient ─────────────────────────

    void SetExpanded(bool expanded, int? page = null)
    {
        if (expanded)
        {
            _transientTimer.Stop();
            _transient = Transient.None;
            var target = page ?? (Agents.Any(a => a.State == "waiting") ? PageAgents : _page);
            if (!_expanded || target != _page) ShowPage(target, 0);
            if (!_expanded) { _liveTicks = 0; LiveTick(); _liveTimer.Start(); }
        }
        else if (_expanded)
        {
            _liveTimer.Stop();
            _settings.Save();
        }
        if (_expanded == expanded) return;
        _expanded = expanded;
        Retarget();
    }

    static int Priority(Transient t) => t switch
    {
        Transient.AgentAlert => 3,
        Transient.Battery => 2,
        _ => 1,
    };

    void ShowTransient(Transient kind, double seconds)
    {
        if (!_ready || _expanded || _hiddenFullscreen) return;
        if (_transient != Transient.None && _transient != kind && Priority(kind) < Priority(_transient)) return;
        _transient = kind;
        _transientTimer.Stop();
        _transientTimer.Interval = TimeSpan.FromSeconds(seconds);
        _transientTimer.Start();
        Retarget();
    }

    // ───────────────────────── mouse ─────────────────────────

    // The window is click-through (WS_EX_TRANSPARENT) unless the island really needs input,
    // so clicks on browser tabs etc. underneath always reach the app below. Hover is polled.
    void PollTick()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetCursorPos(out var cursor)) return;
        var local = Root.PointFromScreen(new Point(cursor.X, cursor.Y));
        var bounds = Island.TransformToAncestor(Root).TransformBounds(new Rect(Island.RenderSize));
        bounds.Inflate(IsHandle ? 10 : 2, IsHandle ? 4 : 2);
        var over = !_hiddenFullscreen && bounds.Contains(local);
        var button = Native.IsMouseButtonDown();
        var now = DateTime.Now;

        if (over != _hover)
        {
            _hover = over;
            _hoverSince = now;
            if (over) _hoverTimer.Stop();
            else
            {
                _dwellBlocked = false;
                ScheduleClose(0.4);
            }
        }

        if (button)
        {
            if (over && _clickThrough) _dwellBlocked = true; // user is clicking whatever is underneath
            _buttonSince ??= now;
            // Holding a button over the island for a moment = probably dragging a file onto it.
            if (over && now - _buttonSince > TimeSpan.FromMilliseconds(250)) _dragCandidate = true;
        }
        else
        {
            _buttonSince = null;
            _dragCandidate = false;
        }

        var dwell = IsHandle ? 0.6 : 0.4;
        if (over && !_expanded && !button && !_dwellBlocked && (now - _hoverSince).TotalSeconds >= dwell)
            SetExpanded(true);

        var interactive = over && (_expanded || _dragCandidate || _dragOver || _draggingOut || _menuOpen
                                   || (!_settings.DiscreetIdle && !IsHandle));
        SetClickThrough(!interactive);
    }

    void ScheduleClose(double seconds)
    {
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromSeconds(seconds);
        _hoverTimer.Start();
    }

    void CloseTick()
    {
        _hoverTimer.Stop();
        if (!_hover && !_dragOver && !_draggingOut && !_menuOpen) SetExpanded(false);
    }

    void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        int? page = _transient switch
        {
            Transient.AgentAlert => PageAgents,
            Transient.NowPlaying => PageMedia,
            Transient.Battery => PageSystem,
            _ => Kind() switch { CompactKind.Agent => PageAgents, CompactKind.Media => PageMedia, _ => null },
        };
        SetExpanded(true, page);
    }

    void Island_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = ((App)Application.Current).BuildContextMenu();
        menu.PlacementTarget = Island;
        menu.Placement = PlacementMode.MousePoint;
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!_hover) ScheduleClose(0.3);
        };
        menu.IsOpen = true;
        e.Handled = true;
    }

    void Island_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_expanded && _transient == Transient.Volume)
        {
            _volume.Nudge(e.Delta > 0 ? 0.02f : -0.02f);
            e.Handled = true;
            return;
        }
        if (!_expanded) return;
        e.Handled = true;

        if (_page == PageFiles && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            FilesScroll.ScrollToHorizontalOffset(FilesScroll.HorizontalOffset - e.Delta / 2.0);
            return;
        }
        if ((DateTime.Now - _lastWheel).TotalMilliseconds < 280) return;
        _lastWheel = DateTime.Now;
        var direction = e.Delta < 0 ? 1 : -1;
        ShowPage(_page + direction, direction);
    }

    // ───────────────────────── pages ─────────────────────────

    FrameworkElement[] Pages => [MediaPage, SystemPage, FilesPage, AgentsPage];

    void ShowPage(int index, int direction)
    {
        _page = ((index % 4) + 4) % 4;
        _settings.LastPage = _page;
        var pages = Pages;
        for (var i = 0; i < pages.Length; i++)
        {
            var page = pages[i];
            if (i != _page) { page.Visibility = Visibility.Collapsed; continue; }

            page.Visibility = Visibility.Visible;
            var slide = (TranslateTransform)page.RenderTransform;
            if (direction == 0)
            {
                slide.BeginAnimation(TranslateTransform.XProperty, null);
                page.BeginAnimation(OpacityProperty, null);
                continue;
            }
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40 * direction, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        }
        PageTitle.Text = PageTitles[_page];
        for (var i = 0; i < _dots.Length; i++)
        {
            var active = i == _page;
            _dots[i].BeginAnimation(WidthProperty, new DoubleAnimation(active ? 16 : 5, Quick));
            _dots[i].Background = active ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        }
        RefreshPage();
    }

    void RefreshPage()
    {
        UpdateHeader();
        AccentWash.BeginAnimation(OpacityProperty,
            new DoubleAnimation(_page == PageMedia && _mediaSnap?.Art != null ? 1 : 0, TimeSpan.FromMilliseconds(250)));
        switch (_page)
        {
            case PageMedia: UpdateMediaUi(); break;
            case PageSystem: SampleSystem(); break;
            case PageFiles: FilesEmpty.Visibility = Vis(Files.Count == 0); break;
            case PageAgents: AgentsEmpty.Visibility = Vis(Agents.Count == 0); break;
        }
    }

    void BuildDots()
    {
        for (var i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Border { Height = 5, Width = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(2.5, 0, 2.5, 0) };
            Dots.Children.Add(_dots[i]);
        }
    }

    // ───────────────────────── timers ─────────────────────────

    void ClockTick()
    {
        var text = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (CompactClock.Text != text) CompactClock.Text = text;
    }

    void LiveTick()
    {
        _liveTicks++;
        UpdateHeader();
        switch (_page)
        {
            case PageMedia: UpdateMediaProgress(); break;
            case PageSystem when _liveTicks % 2 == 1: SampleSystem(); break;
            case PageAgents: foreach (var a in Agents) a.Tick(); break;
        }
    }

    void UpdateHeader()
    {
        var text = DateTime.Now.ToString("ddd, d MMM · HH:mm", CultureInfo.CurrentCulture);
        if (_battery.HasBattery) text += $" · {_battery.Percent}%";
        HeaderInfo.Text = text;
    }

    void GuardTick()
    {
        var hide = Native.SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
        if (hide != _hiddenFullscreen)
        {
            _hiddenFullscreen = hide;
            Root.IsHitTestVisible = !hide;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(hide ? 0 : 1, TimeSpan.FromMilliseconds(200)));
        }
        if (!hide && _hwnd != IntPtr.Zero)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        var now = DateTime.Now;
        var removed = false;
        foreach (var session in Agents.ToList())
        {
            var age = now - session.UpdatedAt;
            if ((!session.IsActive && age > TimeSpan.FromMinutes(2)) || age > TimeSpan.FromMinutes(30))
            {
                Agents.Remove(session);
                removed = true;
            }
        }
        if (removed) { Retarget(); if (_expanded) RefreshPage(); }
    }

    // ───────────────────────── media ─────────────────────────

    void OnMedia(MediaSnapshot? snapshot)
    {
        _mediaSnap = snapshot;
        var key = snapshot == null ? null : $"{snapshot.Title}|{snapshot.Artist}";
        var newTrack = key != null && key != _trackKey;
        _trackKey = key;

        var accent = new SolidColorBrush(snapshot?.Accent ?? MediaService.DefaultAccent);
        accent.Freeze();
        foreach (var bar in _eqBars) bar.Fill = accent;
        SetEqPlaying(snapshot?.IsPlaying == true);

        Brush art = snapshot?.Art is { } image
            ? new ImageBrush(image) { Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
        CompactArt.Background = art;
        NowArt.Background = art;
        NowTitle.Text = snapshot?.Title ?? "";
        NowArtist.Text = snapshot?.Artist ?? "";

        if (_expanded && _page == PageMedia) RefreshPage();
        if (newTrack && snapshot!.IsPlaying) ShowTransient(Transient.NowPlaying, 3.2);
        else Retarget();
    }

    void UpdateMediaUi()
    {
        var m = _mediaSnap;
        MediaContent.Visibility = Vis(m != null);
        MediaEmpty.Visibility = Vis(m == null);
        if (m == null) return;

        MediaTitle.Text = m.Title;
        MediaArtist.Text = m.Artist;
        MediaArt.Background = m.Art != null
            ? new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        MediaArtGlyph.Visibility = Vis(m.Art == null);
        PlayButton.Content = m.IsPlaying ? "\uE769" : "\uE768";
        MediaProgress.Background = new SolidColorBrush(m.Accent);
        AccentWash.Background = new RadialGradientBrush(
            Color.FromArgb(0x5A, m.Accent.R, m.Accent.G, m.Accent.B),
            Color.FromArgb(0x00, m.Accent.R, m.Accent.G, m.Accent.B))
        {
            Center = new Point(0.12, 0.45),
            GradientOrigin = new Point(0.12, 0.45),
            RadiusX = 0.8,
            RadiusY = 1.2,
        };
        UpdateMediaProgress();
    }

    void UpdateMediaProgress()
    {
        var m = _mediaSnap;
        if (m == null) return;
        var position = m.PositionNow();
        var hasTimeline = m.Duration > TimeSpan.FromSeconds(1);
        MediaPos.Text = hasTimeline ? Format(position) : "";
        MediaDur.Text = hasTimeline ? "-" + Format(m.Duration - position) : "";
        MediaProgressScale.ScaleX = hasTimeline ? position / m.Duration : 0;
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void Play_Click(object sender, RoutedEventArgs e) => _media.PlayPause();
    void Next_Click(object sender, RoutedEventArgs e) => _media.Next();
    void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();

    void BuildEq(Panel host, int count, double height)
    {
        for (var i = 0; i < count; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = height,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                Fill = new SolidColorBrush(MediaService.DefaultAccent),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 0.3),
            };
            host.Children.Add(bar);
            _eqBars.Add(bar);
        }
    }

    void SetEqPlaying(bool playing)
    {
        double[] periods = [0.42, 0.58, 0.36, 0.5];
        for (var i = 0; i < _eqBars.Count; i++)
        {
            var scale = (ScaleTransform)_eqBars[i].RenderTransform;
            if (!playing)
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, Quick));
                continue;
            }
            var bounce = new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(periods[i % periods.Length]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                BeginTime = TimeSpan.FromMilliseconds(i * 70),
            };
            Timeline.SetDesiredFrameRate(bounce, 30);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        }
    }

    // ───────────────────────── volume & battery ─────────────────────────

    void OnVolume(float level, bool muted)
    {
        var pct = (int)Math.Round(level * 100);
        VolumeText.Text = muted ? "—" : pct.ToString(CultureInfo.CurrentCulture);
        VolumeIcon.Text = muted || pct == 0 ? "\uE74F" : pct < 34 ? "\uE993" : pct < 67 ? "\uE994" : "\uE995";
        VolumeFill.Background = muted ? new SolidColorBrush(Color.FromRgb(0x63, 0x63, 0x66)) : Brushes.White;
        VolumeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(muted ? 0 : level, Quick));
        ShowTransient(Transient.Volume, 1.6);
    }

    void OnBattery(BatteryEvent ev)
    {
        var pct = _battery.Percent;
        var (icon, text, color) = ev switch
        {
            BatteryEvent.PluggedIn => ("\uE945", "Carregando", AgentSession.Done),
            BatteryEvent.Unplugged => ("\uE83F", "Na bateria", Colors.White),
            _ => ("\uE851", "Bateria fraca", Color.FromRgb(0xFF, 0x45, 0x3A)),
        };
        var brush = new SolidColorBrush(color);
        BatteryIcon.Text = icon;
        BatteryIcon.Foreground = brush;
        BatteryText.Text = text;
        BatteryPct.Text = $"{pct}%";
        BatteryPct.Foreground = brush;
        ShowTransient(Transient.Battery, ev == BatteryEvent.Low ? 4 : 2.4);
    }

    // ───────────────────────── system ─────────────────────────

    Meter AddMeter(string label)
    {
        var meter = new Meter(label);
        SystemPage.Children.Add(meter.Root);
        return meter;
    }

    void SampleSystem()
    {
        var cpu = _monitor.CpuPercent();
        _cpu.Set(cpu / 100, $"{cpu:0}%", $"{Environment.ProcessorCount} núcleos lógicos");

        var (ram, used, total) = _monitor.Memory();
        _ram.Set(ram / 100, $"{ram:0}%", $"{used:0.0} de {total:0} GB");

        if (_monitor.Disk() is { } disk)
            _disk.Set(disk.Percent / 100, $"{disk.Percent:0}%", $"{disk.FreeGb:0} GB livres");

        if (_battery.HasBattery)
        {
            var pct = _battery.Percent;
            var state = _battery.Charging ? "Carregando" : _battery.PluggedIn ? "Conectado" : "Na bateria";
            _power.Set(pct / 100.0, $"{pct}%", state, pct <= 20 ? Meter.Red : AgentSession.Done);
        }
        else
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            _power.Set(0, up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{up.Hours}h {up.Minutes:00}m", "desde a última inicialização");
        }
    }

    // ───────────────────────── agents ─────────────────────────

    void OnAgent(AgentMessage m)
    {
        var key = $"{m.Agent}|{m.Session ?? "default"}";
        var session = Agents.FirstOrDefault(a => a.Key == key);

        if (m.State == "end")
        {
            if (session != null) Agents.Remove(session);
        }
        else
        {
            if (session == null)
            {
                var project = string.IsNullOrEmpty(m.Cwd) ? "" : System.IO.Path.GetFileName(m.Cwd.TrimEnd('\\', '/'));
                session = new AgentSession(key, m.Agent, project);
                Agents.Insert(0, session);
            }
            else if (Agents.IndexOf(session) > 0)
            {
                Agents.Move(Agents.IndexOf(session), 0);
            }
            var wasWaiting = session.State == "waiting";
            session.Update(m.State, m.Detail);

            if (m.State == "waiting" && !wasWaiting) ShowAlert(session, "\uEA8F", $"{session.Agent} precisa de você");
            else if (m.State == "done") ShowAlert(session, "\uE73E", $"{session.Agent} terminou");
        }

        if (_expanded && _page == PageAgents) RefreshPage();
        Retarget();
    }

    void ShowAlert(AgentSession session, string glyph, string title)
    {
        AlertOrb.Fill = session.OrbBrush;
        AlertGlyph.Text = glyph;
        AlertTitle.Text = title;
        AlertDetail.Text = string.IsNullOrEmpty(session.Project) ? session.Detail : $"{session.Project} — {session.Detail}";
        Pulse((ScaleTransform)AlertOrb.RenderTransform, session.State == "waiting");
        ShowTransient(Transient.AgentAlert, session.State == "waiting" ? 8 : 3.5);
    }

    void UpdateCompactAgent()
    {
        var active = Agents.Where(a => a.IsActive).ToList();
        var session = active.FirstOrDefault(a => a.State == "waiting") ?? active.FirstOrDefault();
        if (session == null) return;

        var waiting = session.State == "waiting";
        CompactOrb.Fill = session.OrbBrush;
        CompactAgentText.Inlines.Clear();
        CompactAgentText.Inlines.Add(new Run(session.Agent) { FontWeight = FontWeights.SemiBold });
        var detail = waiting ? "precisa de você" : session.Detail;
        if (!string.IsNullOrEmpty(detail))
            CompactAgentText.Inlines.Add(new Run("  " + detail)
            {
                Foreground = waiting ? session.OrbBrush : (Brush)FindResource("Muted"),
            });

        var many = active.Count > 1;
        CompactAgentCount.Visibility = Vis(many);
        CompactAgentCountText.Text = active.Count.ToString(CultureInfo.CurrentCulture);
        CompactAgentText.Margin = new Thickness(32, 0, many ? 36 : 16, 0);
    }

    static void Pulse(ScaleTransform scale, bool on)
    {
        if (!on)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }
        var pulse = new DoubleAnimation(0.78, 1.08, TimeSpan.FromSeconds(0.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(pulse, 30);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    // ───────────────────────── file tray ─────────────────────────

    void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_draggingOut || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        _lastDragOver = DateTime.Now;
        if (_dragOver) return;

        _dragOver = true;
        DropHint.Visibility = Visibility.Visible;
        SetExpanded(true, PageFiles);
    }

    void Island_DragLeave(object sender, DragEventArgs e)
    {
        if (_dragOver) _dragTimer.Start();
    }

    void DragTick()
    {
        if ((DateTime.Now - _lastDragOver).TotalMilliseconds < 200) return;
        EndDrag();
    }

    void EndDrag()
    {
        _dragTimer.Stop();
        _dragOver = false;
        DropHint.Visibility = Visibility.Collapsed;
        if (!_hover) ScheduleClose(0.8);
    }

    void Island_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_draggingOut && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths)
                if (!Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                    Files.Add(new FileItem(path));
            SaveFiles();
        }
        EndDrag();
        if (!_hover) ScheduleClose(1.6);
    }

    void SaveFiles()
    {
        _settings.TrayFiles = Files.Select(f => f.Path).ToList();
        _settings.Save();
        FilesEmpty.Visibility = Vis(Files.Count == 0);
    }

    void FileTile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = (sender as FrameworkElement)?.DataContext as FileItem;
        _pressPoint = e.GetPosition(this);
    }

    void FileTile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _pressed;
        _pressed = null;
        _draggingOut = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            var result = DragDrop.DoDragDrop((DependencyObject)sender, data,
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (result == DragDropEffects.Move && !File.Exists(item.Path) && !Directory.Exists(item.Path))
            {
                Files.Remove(item);
                SaveFiles();
            }
        }
        catch { }
        finally
        {
            _draggingOut = false;
            if (!_hover) ScheduleClose(0.6);
        }
    }

    void FileTile_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as FileItem;
        if (item == null || item != _pressed) return;
        _pressed = null;
        e.Handled = true;
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
        {
            Files.Remove(item);
            SaveFiles();
            return;
        }
        try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); } catch { }
    }

    void FileRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileItem item)
        {
            Files.Remove(item);
            SaveFiles();
        }
        e.Handled = true;
    }
}

/// <summary>Label + value + bar + caption, used on the system page.</summary>
sealed class Meter
{
    public static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
    static readonly Color Amber = Color.FromRgb(0xFF, 0x9F, 0x0A);
    static readonly Color Normal = Color.FromRgb(0x0A, 0x84, 0xFF);

    public Grid Root { get; }
    readonly TextBlock _value, _caption;
    readonly Border _fill;
    readonly ScaleTransform _scale = new(0, 1);

    public Meter(string label)
    {
        var muted = (Brush)Application.Current.Resources["Muted"];
        Root = new Grid { Margin = new Thickness(0, 0, 18, 8), VerticalAlignment = VerticalAlignment.Center };
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Root.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = muted });
        _value = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        Root.Children.Add(_value);

        _fill = new Border { CornerRadius = new CornerRadius(2.5), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _scale };
        var track = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Margin = new Thickness(0, 4, 0, 4),
            Background = (Brush)Application.Current.Resources["Track"],
            ClipToBounds = true,
            Child = _fill,
        };
        Grid.SetRow(track, 1);
        Root.Children.Add(track);

        _caption = new TextBlock { FontSize = 10.5, Foreground = muted, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(_caption, 2);
        Root.Children.Add(_caption);
    }

    public void Set(double fraction, string value, string caption, Color? color = null)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        _value.Text = value;
        _caption.Text = caption;
        _fill.Background = new SolidColorBrush(color ?? (fraction > 0.9 ? Red : fraction > 0.75 ? Amber : Normal));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fraction, TimeSpan.FromMilliseconds(300)));
    }
}
