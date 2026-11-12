using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// Approvals, the info pill (clipboard, lock keys, updates…), hotkeys, lyrics, player chips,
// brightness, Bluetooth batteries, the calendar/weather page and the weekly recap prompt.
public partial class IslandWindow
{
    const int WM_HOTKEY = 0x0312, WM_CLIPBOARDUPDATE = 0x031D;
    const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern short GetKeyState(int key);

    sealed record Approval(string Id, string SessionKey, string Agent, string Project, string Detail, string Command, DateTime Since)
    {
        // What the phone refers to: tied to this exact request; the request id itself never leaves the PC.
        public string Fingerprint { get; } = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{Id}\u001f{SessionKey}\u001f{Command}"))).ToLowerInvariant();
    }

    static readonly TimeSpan ApprovalWait = TimeSpan.FromSeconds(54);
    readonly List<Approval> _approvals = [];
    /// <summary>The quiet pill was opened into the full Allow / Deny panel.</summary>
    bool _approvalOpen;
    DateTime _approvalShownAt;

    bool ApprovalQuiet => _settings.QuietApprovals && !_approvalOpen;
    Action? _infoAction;
    bool _userHidden, _extrasStarted;
    DateTime? _unhideAt;
    bool? _caps, _num;

    IReadOnlyList<LyricLine>? _lyrics;
    string? _lyricsKey;
    DispatcherTimer? _lyricsTimer, _extrasTimer;

    WeatherNow? _weather;
    DateTime _weatherAt;
    bool _weatherBusy;
    readonly HashSet<string> _btWarned = [];

    int? _brightness;
    int _brightTarget, _brightWritten = -1;
    bool _brightDragging, _brightWriting;

    void StartExtras()
    {
        if (_extrasStarted || _hwnd == IntPtr.Zero) return;
        _extrasStarted = true;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterHotkeys(announce: true);
        AddClipboardFormatListener(_hwnd);
        _media.SourcesChanged += BuildSourceChips;
        _lyricsTimer = MakeTimer(0.25, UpdateLyric);
        _extrasTimer = MakeTimer(60, ExtrasTick);
        _extrasTimer.Start();
        ApplyPhoneLink();
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(4000);
            ExtrasTick();
        });
    }

    void StopExtras()
    {
        if (!_extrasStarted) return;
        foreach (var action in Hotkeys.All) UnregisterHotKey(_hwnd, action.Id);
        RemoveClipboardFormatListener(_hwnd);
        _lyricsTimer?.Stop();
        _extrasTimer?.Stop();
        foreach (var approval in _approvals) _ = AgentHub.Reply(approval.Id, "none");
        _phone?.Dispose();
        AgentStats.Save();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            OnHotkey(wParam.ToInt32());
            handled = true;
        }
        else if (msg == WM_CLIPBOARDUPDATE)
        {
            // Let the owner finish writing every format before reading.
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(150);
                OnClipboard();
            });
        }
        return IntPtr.Zero;
    }

    // ───────────────────────── hotkeys ─────────────────────────

    public bool UserHidden => _userHidden;

    /// <summary>Hides everything (popups included) until shown again, or for a while.</summary>
    public void SetUserHidden(bool hidden, TimeSpan? duration = null)
    {
        _userHidden = hidden;
        _unhideAt = hidden && duration is { } d ? DateTime.Now + d : null;
        if (hidden) SetExpanded(false);
        GuardTick();
    }

    /// <summary>Shortcuts another app already owns (RegisterHotKey failed), as "Ctrl+Alt+A".</summary>
    public IReadOnlyList<string> HotkeyConflicts { get; private set; } = [];

    /// <summary>(Re)registers every shortcut from the settings; conflicts are kept in HotkeyConflicts and, when
    /// <paramref name="announce"/>, shown once on the island instead of failing silently.</summary>
    public void RegisterHotkeys(bool announce = false)
    {
        if (_hwnd == IntPtr.Zero) return;
        var conflicts = new List<string>();
        foreach (var action in Hotkeys.All)
        {
            UnregisterHotKey(_hwnd, action.Id);
            var keys = Hotkeys.Keys(_settings, action.Key);
            if (Hotkeys.Parse(keys) is not { } parsed) continue;
            if (!RegisterHotKey(_hwnd, action.Id, parsed.Modifiers | MOD_NOREPEAT, parsed.Key))
            {
                conflicts.Add(keys);
                Log.Warn("Hotkeys", $"{keys} ({action.Key}) is taken by another app");
            }
        }
        HotkeyConflicts = conflicts;
        if (announce && conflicts.Count > 0)
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(5000); // after startup settles, so the pill is visible
                ShowInfo("\uE765", $"{string.Join(", ", conflicts)} já é usado por outro app", "Mudar",
                    glyphColor: AgentSession.Waiting, action: () => ((App)Application.Current).ShowSettings("general"), seconds: 8);
            });
    }

    void OnHotkey(int id)
    {
        var action = Hotkeys.ById(id)?.Key;
        if (action == null) return;
        if (action == "hide")
        {
            SetUserHidden(!_userHidden);
            return;
        }
        if (_userHidden) SetUserHidden(false);
        var page = action switch
        {
            "media" => PageMedia,
            "timer" => PageTimer,
            "agents" => PageAgents,
            _ => PageCalendar,
        };
        if (_expanded && _page == page)
        {
            SetExpanded(false);
            return;
        }
        SetExpanded(true, page);
        if (!_hover) ScheduleClose(6);
    }

    // ───────────────────────── info pill ─────────────────────────

    public void ShowInfo(string glyph, string text, string right, BitmapSource? thumb = null, Color? glyphColor = null,
        Action? action = null, double seconds = 2.2, bool round = false)
    {
        if (!_ready || _hiddenFullscreen) return;
        InfoThumb.CornerRadius = new CornerRadius(round ? InfoThumb.Width / 2 : 6);
        InfoIcon.Text = glyph;
        if (glyphColor is { } c) InfoIcon.Foreground = new SolidColorBrush(c);
        else InfoIcon.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        InfoIcon.Visibility = Vis(thumb == null);
        InfoThumb.Background = thumb != null ? new ImageBrush(thumb) { Stretch = Stretch.UniformToFill } : null;
        InfoThumb.Visibility = Vis(thumb != null);
        InfoText.Text = text;
        InfoText.Margin = new Thickness(thumb != null ? 36 : 28, 0, 96, 0);
        InfoRight.Text = right;
        _infoAction = action;
        if (_expanded && action == null) return;
        if (_expanded) SetExpanded(false);
        ShowTransient(Transient.Info, seconds);
    }

    void OnClipboard()
    {
        if (!_ready || !_settings.ShowClipboard || _userHidden) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return;
            // Password managers and other private sources ask clipboard watchers to look away.
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore"))
                return;
            if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream flag
                && flag.Length >= 4 && BitConverter.ToInt32(flag.ToArray(), 0) == 0)
                return;

            if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                ShowInfo("\uE8C8", files.Length == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : $"{files.Length} itens", "Copiado");
            }
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                ShowInfo("\uE8C8", $"Imagem {image.PixelWidth} × {image.PixelHeight}", "Copiada", image);
            }
            else if (Clipboard.ContainsText())
            {
                var text = string.Join(' ', Clipboard.GetText().Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
                if (text.Length == 0) return;
                if (text.Length > 80) text = text[..80] + "…";
                ShowInfo("\uE8C8", text, "Copiado");
            }
        }
        catch { } // another app holds the clipboard
    }

    void CheckLockKeys()
    {
        bool caps = (GetKeyState(0x14) & 1) != 0, num = (GetKeyState(0x90) & 1) != 0;
        if (_settings.ShowLockKeys && _caps != null)
        {
            if (caps != _caps)
                ShowInfo("\uE765", caps ? "Caps Lock ativado" : "Caps Lock desativado", caps ? "ABC" : "abc",
                    glyphColor: caps ? AgentSession.Done : null, seconds: 1.4);
            else if (num != _num)
                ShowInfo("\uE765", num ? "Num Lock ativado" : "Num Lock desativado", num ? "123" : "", seconds: 1.4);
        }
        _caps = caps;
        _num = num;
    }

    // ───────────────────────── approvals ─────────────────────────

    void AddApproval(AgentSession session, AgentMessage m)
    {
        DropApprovals(a => a.SessionKey == session.Key);
        var approval = new Approval(m.RequestId!, session.Key, session.Agent, session.Project,
            string.IsNullOrEmpty(m.Detail) ? "Quer usar uma ferramenta" : m.Detail, m.Command ?? m.Detail ?? "", DateTime.Now);
        _approvals.Add(approval);
        PublishApproval(approval);
        ShowApproval();
    }

    void ClearApprovals(string sessionKey)
    {
        if (DropApprovals(a => a.SessionKey == sessionKey) == 0) return;
        if (_transient == Transient.Approval) NextApprovalOrClose();
    }

    int DropApprovals(Func<Approval, bool> match)
    {
        var gone = _approvals.Where(match).ToList();
        foreach (var approval in gone)
        {
            _approvals.Remove(approval);
            _phone?.PublishResolved(approval.Fingerprint);
        }
        return gone.Count;
    }

    void ShowApproval()
    {
        DropApprovals(a => DateTime.Now - a.Since > ApprovalWait);
        var approval = _approvals.FirstOrDefault();
        if (approval == null) return;
        ApprovalAgent.Text = approval.Agent;
        // The exact command, not the one-line summary: Allow must never approve text the user didn't see.
        var canAllow = ApprovalRules.IslandCanAllow(approval.Command);
        ApprovalDetail.Text = canAllow
            ? approval.Command
            : $"{approval.Detail}\nComando longo ou com várias linhas: confira e responda no terminal.";
        ApprovalAllow.IsEnabled = canAllow;
        ApprovalAllow.Opacity = canAllow ? 1 : 0.35;
        ApprovalAllowText.Text = canAllow ? "Permitir" : "Ver no terminal";
        UpdateApprovalElapsed();
        ApprovalPetBox.Visibility = Vis(PetOn);
        ApprovalPet.Tint(AgentSession.Waiting);
        ApprovalPet.BaseMood = PetView.Mood.Waiting;
        var waiting = _approvals.Count;
        ApprovalPillText.Text = waiting > 1 ? $"{waiting} pedidos de permissão" : $"{approval.Agent} quer permissão";
        // a new request starts quiet again, unless the user is looking at the open panel right now
        if (!(_approvalOpen && _hover)) _approvalOpen = false;
        if (_transient != Transient.Approval) _approvalShownAt = DateTime.Now;
        if (_expanded) SetExpanded(false);
        var left = ApprovalWait - (DateTime.Now - approval.Since);
        ShowTransient(Transient.Approval, Math.Max(1, left.TotalSeconds));
        ((App)Application.Current).DesktopPet?.Visit(IslandAnchor());
    }

    void UpdateApprovalElapsed()
    {
        if (_approvals.FirstOrDefault() is not { } approval) return;
        var left = ApprovalWait - (DateTime.Now - approval.Since);
        ApprovalElapsed.Text = left > TimeSpan.Zero ? $"  {(int)left.TotalSeconds}s" : "";
        ApprovalPillTime.Text = left > TimeSpan.Zero ? $"{(int)left.TotalSeconds}s" : "";
    }

    void OpenApproval()
    {
        if (_transient != Transient.Approval || _approvalOpen) return;
        _approvalOpen = true;
        Retarget();
    }

    /// <summary>Back to the pill when the pointer leaves an opened quiet request.</summary>
    void CloseApproval()
    {
        if (_transient != Transient.Approval || !_approvalOpen || !_settings.QuietApprovals) return;
        _approvalOpen = false;
        Retarget();
    }

    public void ApprovalStyleChanged()
    {
        _approvalOpen = false;
        if (_transient == Transient.Approval) Retarget();
    }

    void NextApprovalOrClose()
    {
        _transientTimer.Stop();
        _transient = Transient.None;
        if (_approvals.Count == 0) _approvalOpen = false;
        if (_approvals.Count > 0) ShowApproval();
        else Retarget();
    }

    // From the island's own buttons (the phone has its own limit, in OnPhoneDecision).
    void Answer(string decision)
    {
        if (_approvals.FirstOrDefault() is not { } approval) return;
        if (decision == "allow" && !ApprovalRules.IslandCanAllow(approval.Command)) return;
        Answer(approval, decision);
    }

    void Answer(Approval approval, string decision)
    {
        var showing = ReferenceEquals(approval, _approvals.FirstOrDefault());
        DropApprovals(a => ReferenceEquals(a, approval));
        _ = AgentHub.Reply(approval.Id, decision);
        if (decision == "allow")
        {
            AgentStats.Record(null, false, true, TimeSpan.Zero, null);
            PetsCelebrate();
        }
        if (showing && _transient == Transient.Approval) NextApprovalOrClose();
    }

    void ApprovalAllow_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Answer("allow");
    }

    void ApprovalDeny_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Answer("deny");
    }

    // ───────────────────────── lyrics ─────────────────────────

    bool LyricsOn => _settings.ShowLyrics && _lyrics is { Count: > 0 } && _mediaSnap?.IsPlaying == true;

    async void LoadLyrics(MediaSnapshot? m)
    {
        var key = m == null ? null : $"{m.Title}|{m.Artist}";
        if (key == _lyricsKey) return;
        _lyricsKey = key;
        _lyrics = null;
        _lyricsTimer?.Stop();
        UpdateLyric();
        // Without a timeline (audio fallback) there is nothing to sync to.
        if (m == null || !_settings.ShowLyrics || SnapshotMode || m.Duration < TimeSpan.FromSeconds(20)) return;
        try
        {
            var lines = await LyricsService.FindAsync(m.Title, m.Artist, m.Duration);
            if (key != _lyricsKey || lines == null || lines.Count == 0) return;
            _lyrics = lines;
            _lyricsTimer?.Start();
            UpdateLyric();
            Retarget();
        }
        catch { }
    }

    public void ApplyLyrics()
    {
        _lyricsKey = null;
        LoadLyrics(_mediaSnap);
        Retarget();
    }

    void UpdateLyric()
    {
        string? line = null;
        if (LyricsOn) line = LyricsService.LineAt(_lyrics!, _mediaSnap!.PositionNow()) ?? "";
        var show = line != null;
        CompactLyric.Visibility = Vis(show);
        CompactEq.Visibility = Vis(true);
        if (!show || CompactLyric.Text == line) return;
        CompactLyric.Text = line!.Length == 0 ? "♪" : line;
        CompactLyric.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    // ───────────────────────── players ─────────────────────────

    void BuildSourceChips()
    {
        MediaSources.Children.Clear();
        var sources = _media.Sources;
        if (sources.Count < 2) return;
        var px = (int)Math.Ceiling(16 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        foreach (var source in sources)
        {
            UIElement face = AppIcons.ForAppId(source.Id, Math.Max(px, 32)) is { } icon
                ? new Image
                {
                    Source = Upscaler.Fit(icon, px),
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Uniform,
                }
                : new TextBlock
                {
                    Text = source.Name[..1].ToUpperInvariant(),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            RenderOptions.SetBitmapScalingMode(face, BitmapScalingMode.HighQuality);
            var chip = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(1.5),
                Cursor = Cursors.Hand,
                Tag = source.Id,
                ToolTip = source.Playing ? source.Name + " (tocando)" : source.Name,
                Child = face,
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Card");
            if (face is TextBlock letter) letter.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _media.Pin(source.Id);
            };
            MediaSources.Children.Add(chip);
        }
        HighlightSourceChips();
    }

    BitmapSource? _artSource, _artSharp;
    int _artPx;

    // Small covers (browsers often publish ~100 px) are enlarged once for the big art on the media page.
    BitmapSource SharpArt(BitmapSource art, double dips)
    {
        var px = (int)Math.Ceiling(dips * VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (!ReferenceEquals(art, _artSource) || _artSharp == null || px != _artPx)
        {
            _artSource = art;
            _artPx = px;
            _artSharp = Upscaler.Fit(art, px);
        }
        return _artSharp;
    }

    void HighlightSourceChips()
    {
        var accent = new SolidColorBrush(_mediaSnap?.Accent ?? MediaService.DefaultAccent);
        foreach (var chip in MediaSources.Children.OfType<Border>())
        {
            var current = (string)chip.Tag == _media.CurrentId;
            chip.BorderBrush = current ? accent : Brushes.Transparent;
            chip.Opacity = current ? 1 : 0.6;
        }
    }

    // ───────────────────────── brightness & bluetooth ─────────────────────────

    async void SampleBrightness()
    {
        if (_brightDragging || _brightWriting || SnapshotMode) return;
        var hwnd = _hwnd;
        var value = await Task.Run(() => Brightness.Get(hwnd));
        _brightness = value;
        BrightnessRow.Visibility = Vis(value != null);
        if (value is { } v) ShowBrightness(v);
    }

    void ShowBrightness(int value)
    {
        BrightScale.ScaleX = value / 100.0;
        BrightText.Text = $"{value}%";
    }

    void Bright_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _brightDragging = true;
        BrightTrack.CaptureMouse();
        SetBrightnessFrom(e);
    }

    void Bright_MouseMove(object sender, MouseEventArgs e)
    {
        if (_brightDragging) SetBrightnessFrom(e);
    }

    void Bright_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_brightDragging) return;
        e.Handled = true;
        _brightDragging = false;
        BrightTrack.ReleaseMouseCapture();
        SetBrightnessFrom(e);
    }

    async void SetBrightnessFrom(MouseEventArgs e)
    {
        var fraction = e.GetPosition(BrightTrack).X / Math.Max(1, BrightTrack.ActualWidth);
        var value = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
        ShowBrightness(value);
        _brightTarget = value;
        if (_brightWriting) return;
        // DDC/CI is slow: write the latest value, then catch up if the slider moved meanwhile.
        _brightWriting = true;
        var hwnd = _hwnd;
        try
        {
            while (_brightWritten != _brightTarget)
            {
                var target = _brightTarget;
                await Task.Run(() => Brightness.Set(hwnd, target));
                _brightWritten = target;
            }
        }
        finally { _brightWriting = false; }
    }

    async void ExtrasTick()
    {
        try { ShowBluetooth(await BluetoothBattery.ListAsync()); }
        catch { }
        OfferRecap();
        TrimWhenIdle();
    }

    // After a couple of quiet minutes, return what opening pages, settings and album art left behind.
    // Once per quiet stretch: the next activity re-arms it.
    static readonly TimeSpan TrimAfter = TimeSpan.FromMinutes(2);
    DateTime _trimmedAt;

    void TrimWhenIdle()
    {
        var now = DateTime.Now;
        if (_expanded || _hover || _transient != Transient.None || now - _lastActivity < TrimAfter || _trimmedAt > _lastActivity) return;
        _trimmedAt = now;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Native.TrimWorkingSet();
    }

    void ShowBluetooth(List<BluetoothDevice> devices)
    {
        BluetoothList.Children.Clear();
        foreach (var device in devices.Take(3))
        {
            var low = device.Battery <= 15;
            var color = low ? Meter.Red : AgentSession.Done;
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), ToolTip = device.Name };
            chip.Children.Add(new TextBlock
            {
                Text = DeviceGlyph(device.Name),
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 13,
                Foreground = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            chip.Children.Add(new TextBlock { Text = $"{device.Battery}%", FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            BluetoothList.Children.Add(chip);

            if (low && _btWarned.Add(device.Name))
                ShowInfo(DeviceGlyph(device.Name), $"{device.Name} com pouca bateria", $"{device.Battery}%", glyphColor: Meter.Red, seconds: 4);
            else if (device.Battery > 25) _btWarned.Remove(device.Name);
        }
    }

    static string DeviceGlyph(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("mouse")) return "\uE962";
        if (n.Contains("keyboard") || n.Contains("teclado")) return "\uE765";
        if (n.Contains("controller") || n.Contains("controle") || n.Contains("xbox")) return "\uE7FC";
        if (n.Contains("bud") || n.Contains("pods") || n.Contains("fone") || n.Contains("head") || n.Contains("wh-") || n.Contains("wf-") || n.Contains("jbl"))
            return "\uE7F6";
        return "\uE702";
    }

    // ───────────────────────── calendar & weather ─────────────────────────

    void UpdateCalendar()
    {
        var culture = CultureInfo.CurrentCulture;
        var today = DateTime.Today;
        var month = today.ToString("MMMM 'de' yyyy", culture);
        CalMonth.Text = char.ToUpper(month[0], culture) + month[1..];

        CalGrid.Children.Clear();
        var first = culture.DateTimeFormat.FirstDayOfWeek;
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)first + i) % 7);
            CalGrid.Children.Add(new TextBlock
            {
                Text = culture.DateTimeFormat.GetShortestDayName(day)[..1].ToUpper(culture),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3),
            });
            ((TextBlock)CalGrid.Children[^1]).SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }

        var start = new DateTime(today.Year, today.Month, 1);
        var offset = ((int)start.DayOfWeek - (int)first + 7) % 7;
        for (var i = 0; i < offset; i++) CalGrid.Children.Add(new Border());
        for (var d = 1; d <= DateTime.DaysInMonth(today.Year, today.Month); d++)
        {
            var date = start.AddDays(d - 1);
            var isToday = date == today;
            var text = new TextBlock
            {
                Text = d.ToString(culture),
                FontSize = 11.5,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (isToday) text.Foreground = Brushes.White;
            else if (date < today || date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            CalGrid.Children.Add(new Border
            {
                Width = 22,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = isToday ? new SolidColorBrush(Meter.Red) : Brushes.Transparent,
                Child = text,
            });
        }
        ShowWeather();
        if (DateTime.Now - _weatherAt > TimeSpan.FromMinutes(30)) LoadWeather();
    }

    async void LoadWeather()
    {
        if (_weatherBusy || SnapshotMode) return;
        _weatherBusy = true;
        try
        {
            _weather = await WeatherService.GetAsync();
            _weatherAt = DateTime.Now;
        }
        catch
        {
            WeatherEmpty.Text = "Sem conexão para buscar o clima.";
            _weatherAt = DateTime.Now.AddMinutes(-25); // retry in five minutes
        }
        finally { _weatherBusy = false; }
        ShowWeather();
    }

    void ShowWeather()
    {
        var w = _weather;
        WeatherBox.Visibility = Vis(w != null);
        WeatherEmpty.Visibility = Vis(w == null);
        if (w == null) return;
        var night = DateTime.Now.Hour is < 6 or >= 18;
        WeatherPlace.Text = string.IsNullOrEmpty(w.Place) ? "Agora" : w.Place;
        WeatherIcon.Text = w.Sunny ? (night ? "\uE708" : "\uE706") : "\uE753";
        WeatherIcon.Foreground = new SolidColorBrush(w.Sunny ? Color.FromRgb(0xFF, 0xD6, 0x0A)
            : w.Rainy ? Color.FromRgb(0x64, 0xB5, 0xFF) : Color.FromRgb(0xC7, 0xC7, 0xCC));
        WeatherTemp.Text = $"{w.TempC}°";
        WeatherDesc.Text = w.Description;
        WeatherMore.Text = $"Mín {w.MinC}° · Máx {w.MaxC}°\nSensação {w.FeelsC}° · Umidade {w.Humidity}%";
    }

    /// <summary>For snapshots: show the page with a fixed forecast.</summary>
    void SetWeather(WeatherNow weather)
    {
        _weather = weather;
        _weatherAt = DateTime.Now;
    }

    // ───────────────────────── weekly recap ─────────────────────────

    void OfferRecap()
    {
        var today = DateTime.Today;
        if (today.DayOfWeek != DayOfWeek.Monday || DateTime.Now.Hour < 9) return;
        var stamp = today.ToString("yyyy-MM-dd");
        if (_settings.RecapShown == stamp) return;
        var week = AgentStats.LastWeek();
        if (week.Sessions == 0 && week.WorkSeconds < 60) return;
        _settings.RecapShown = stamp;
        _settings.Save();
        ShowInfo("\uE9D2", "Seu resumo da semana está pronto", "Abrir", glyphColor: AgentSession.Working,
            action: () => ((App)Application.Current).ShowRecap(), seconds: 10);
    }
}
