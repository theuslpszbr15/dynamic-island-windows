using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DynamicIsland;

// The mascot, the colour themes (including frosted glass) and the agent detail page.
public partial class IslandWindow
{
    static readonly TimeSpan SleepAfter = TimeSpan.FromSeconds(90);
    DateTime _lastActivity = DateTime.Now, _lastGlass;
    System.Windows.Threading.DispatcherTimer? _glassTimer;
    bool _glassBusy;
    // False when Glass is chosen but the window can't hide itself from capture (see ApplyGlass).
    bool _glassLive;
    // Windows 10: no exclusion from capture, so the glass is a snapshot taken with the island cloaked.
    bool _glassSnap;
    (int, int, int, int) _snapRect;
    int[]? _snapRing;
    bool GlassBlur => _glassLive || _glassSnap;

    PetView[] Pets => [CompactPet, HeaderPet, AlertPet, AgentPet, ApprovalPet];
    bool PetOn => _settings.Mascot != PetSpecies.None;
    bool Glass => _settings.Theme == IslandThemes.Glass;

    public void ApplyLook()
    {
        ApplyGlass();
        var tint = IslandThemes.Of(_settings.Theme);
        // Without the live blur the glass is plain smoked glass: darker, so text stays readable over any window.
        if (Glass && !GlassBlur) tint.A = 0xE4;
        var ground = new SolidColorBrush(tint);
        ground.Freeze();
        IslandBg.Background = ground;
        EarLeft.Fill = EarRight.Fill = ground;
        ApplyInk(_settings.Theme == IslandThemes.Light);
        MenuPalette.For(_settings.Theme).ApplyToResources();

        var pet = PetOn;
        PetView.SetDefaultSpecies(_settings.Mascot);
        CompactPet.Visibility = HeaderPetBox.Visibility = AlertPetBox.Visibility = ApprovalPetBox.Visibility = Vis(pet);
        AlertOrb.Visibility = AlertGlyph.Visibility = Vis(!pet);
        // The mascot takes the slot of the album art, the agent orb and the timer ring's left edge.
        CompactArt.Visibility = Vis(!pet);
        CompactOrb.Visibility = Vis(!pet);
        CompactClock.Margin = new Thickness(pet ? 26 : 0, 0, 0, 0);
        CompactTimerRing.Margin = new Thickness(pet ? 38 : 12, 0, 0, 0);
        PageTitle.Margin = new Thickness(pet ? 27 : 0, 0, 0, 0);
        UpdatePet();
    }

    // Text and hairline brushes are shared app resources; the light theme turns them dark.
    static void ApplyInk(bool light)
    {
        static void Set(string key, long argb)
        {
            var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            var resources = Application.Current.Resources;
            if (resources[key] is SolidColorBrush { IsFrozen: false } brush) brush.Color = color;
            else resources[key] = new SolidColorBrush(color);
        }
        Set("Ink", light ? 0xFF1C1C1E : 0xFFFFFFFF);
        Set("Muted", light ? 0xFF6E6E73 : 0xFF8E8E93);
        Set("Track", light ? 0x1F000000 : 0x2EFFFFFF);
        Set("Card", light ? 0x0F000000 : 0x1CFFFFFF);
        Set("CardHover", light ? 0x1F000000 : 0x33FFFFFF);
        Set("Faint", light ? 0x66000000 : 0x66FFFFFF);
    }

    // DWM's own blur only covers whole window rectangles, so the glass is made here from a capture of the
    // screen behind the island (see LiquidGlass). The island must not be in that capture, otherwise it
    // blurs itself into a ghost copy of its own text and icons.
    // Live: the island is excluded from capture (WDA_EXCLUDEFROMCAPTURE) and the glass follows the screen.
    // Windows 10 refuses that for WPF's transparent (layered) windows (SetWindowDisplayAffinity fails with
    // ERROR_NOT_ENOUGH_MEMORY). There the island is cloaked for about two frames per capture instead, so the
    // glass is a snapshot taken only when the island settles in a new size or the background next to it changes.
    void ApplyGlass()
    {
        var excluded = _hwnd != IntPtr.Zero && Native.SetWindowDisplayAffinity(_hwnd, Glass ? Native.WDA_EXCLUDEFROMCAPTURE : 0);
        _glassLive = Glass && excluded;
        _glassSnap = Glass && !excluded && _hwnd != IntPtr.Zero && Native.SetCloaked(_hwnd, true) & Native.SetCloaked(_hwnd, false);
        GlassFill.Visibility = GlassRim.Visibility = Vis(GlassBlur);
        // Smoked glass without any blur (cloak refused too) would show the stacked shadow outlines as a halo.
        ShadowLayers.Visibility = Vis(!Glass || GlassBlur);
        if (!GlassBlur)
        {
            _glassTimer?.Stop();
            GlassFill.Background = null;
            return;
        }
        _glassTimer ??= MakeTimer(0.2, GlassTick);
        _glassTimer.Interval = TimeSpan.FromSeconds(_glassSnap ? 1 : 0.2);
        _glassTimer.Start();
        _snapRect = default;
        CaptureGlass(true);
    }

    void GlassTick()
    {
        if (!_glassSnap) { CaptureGlass(true); return; }
        if (_glassBusy || _animating || _userHidden || _hiddenFullscreen || PresentationSource.FromVisual(Island) == null) return;
        var (x, y, w, h, _, _, _) = GlassGeometry();
        if (_snapRect != (x, y, w, h)) { CaptureGlass(true); return; }
        if (DateTime.Now - _lastGlass < TimeSpan.FromSeconds(2.5)) return;
        if (SampleRing(x, y, w, h) is not { } ring) return;
        if (_snapRing == null || RingChanged(_snapRing, ring)) CaptureGlass(true);
    }

    (int X, int Y, int W, int H, double Radius, bool FlatTop, double Dpi) GlassGeometry()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = Island.PointToScreen(new Point(0, 0));
        return ((int)Math.Round(origin.X), (int)Math.Round(origin.Y),
            (int)Math.Round(Island.Width * dpi.DpiScaleX), (int)Math.Round(Island.Height * dpi.DpiScaleY),
            IslandBg.CornerRadius.BottomLeft * dpi.DpiScaleX, IslandBg.CornerRadius.TopLeft < IslandBg.CornerRadius.BottomLeft, dpi.DpiScaleX);
    }

    // What is just beside and below the island (never the island itself), to notice the background changing.
    static int[]? SampleRing(int x, int y, int w, int h)
    {
        const int gap = 6, thick = 40;
        var all = new List<int>();
        foreach (var (rx, ry, rw, rh) in new[] { (x - thick - gap, y, thick, h), (x + w + gap, y, thick, h), (x, y + h + gap, w, thick) })
            if (Native.CapturePixels(rx, ry, rw, rh, 8) is { } part) all.AddRange(part.Pixels);
        return all.Count > 0 ? all.ToArray() : null;
    }

    static bool RingChanged(int[] before, int[] after)
    {
        if (before.Length != after.Length) return true;
        long diff = 0;
        for (var i = 0; i < before.Length; i++)
            diff += Math.Abs(((before[i] >> 16) & 0xFF) - ((after[i] >> 16) & 0xFF))
                  + Math.Abs(((before[i] >> 8) & 0xFF) - ((after[i] >> 8) & 0xFF))
                  + Math.Abs((before[i] & 0xFF) - (after[i] & 0xFF));
        return diff / (before.Length * 3.0) > 3;
    }

    async void CaptureGlass(bool force = false)
    {
        // A snapshot hides the island for a moment, so it is never taken on every animation frame.
        if (!GlassBlur || _glassBusy || _hiddenFullscreen || PresentationSource.FromVisual(Island) == null || (_glassSnap && !force)) return;
        var now = DateTime.Now;
        if (!force && now - _lastGlass < TimeSpan.FromMilliseconds(_animating ? 120 : 50)) return;
        _lastGlass = now;
        var (x, y, w, h, radius, flatTop, dpiScale) = GlassGeometry();
        var hide = _glassSnap ? _hwnd : IntPtr.Zero;
        if (_glassSnap)
        {
            _snapRect = (x, y, w, h);
            _snapRing = SampleRing(x, y, w, h);
        }

        _glassBusy = true;
        try
        {
            var shot = await Task.Run(() => LiquidGlass.Render(x, y, w, h, radius, flatTop, dpiScale, hide));
            if (shot == null || !GlassBlur) return;
            var brush = new ImageBrush(shot) { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
            brush.Freeze();
            GlassFill.Background = brush;
        }
        finally { _glassBusy = false; }
    }

    double PetExtraWidth(CompactKind kind) => PetOn && kind is CompactKind.Clock or CompactKind.Timer ? 24 : 0;

    void Touch() => _lastActivity = DateTime.Now;

    void UpdatePet()
    {
        if (!PetOn)
        {
            foreach (var p in Pets) p.Active = false;
            return;
        }

        var kind = Kind();
        var waiting = Agents.Any(a => a.State == "waiting");
        var mood = kind switch
        {
            CompactKind.Agent => waiting ? PetView.Mood.Waiting : PetView.Mood.Working,
            CompactKind.Media => PetView.Mood.Dance,
            CompactKind.Timer => PetView.Mood.Idle,
            _ => DateTime.Now - _lastActivity > SleepAfter ? PetView.Mood.Sleep : PetView.Mood.Idle,
        };
        Color? tint = kind == CompactKind.Agent ? (waiting ? AgentSession.Waiting : AgentSession.Working) : null;
        foreach (var p in new[] { CompactPet, HeaderPet })
        {
            p.BaseMood = mood;
            p.Tint(tint);
        }

        var transient = !_expanded && _transient != Transient.None;
        // A hidden island (fullscreen app, or hidden by the user) keeps its pets still.
        var seen = !_hiddenFullscreen;
        CompactPet.Active = seen && !_expanded && !transient && !IsHandle;
        HeaderPet.Active = seen && _expanded;
        AlertPet.Active = seen && _transient == Transient.AgentAlert;
        ApprovalPet.Active = seen && _transient == Transient.Approval;
        AgentPet.Active = seen && _expanded && _page == PageAgents;
        ((App)Application.Current).DesktopPet?.Follow(mood, tint);
    }

    void PetsLookAt(Point cursorInRoot)
    {
        if (!PetOn) return;
        foreach (var pet in Pets)
        {
            if (!pet.Active || !pet.IsVisible) continue;
            var centre = pet.TranslatePoint(new Point(12, 12), Root);
            pet.LookAt(cursorInRoot - centre);
        }
    }

    void PetsHello()
    {
        var wasAsleep = DateTime.Now - _lastActivity > SleepAfter;
        Touch();
        if (!PetOn) return;
        if (wasAsleep) foreach (var p in Pets) p.React(PetView.Mood.Happy, 1.2);
        UpdatePet();
    }

    void PetsCelebrate()
    {
        Touch();
        if (PetOn) foreach (var p in Pets) p.React(PetView.Mood.Happy, 1.6);
        ((App)Application.Current).DesktopPet?.Celebrate();
    }

    void AlertPetFor(Brush orb, bool waiting)
    {
        if (waiting) ((App)Application.Current).DesktopPet?.Visit(IslandAnchor());
        if (!PetOn) return;
        AlertPet.Tint((orb as SolidColorBrush)?.Color);
        AlertPet.BaseMood = waiting ? PetView.Mood.Waiting : PetView.Mood.Idle;
        if (!waiting) AlertPet.React(PetView.Mood.Happy, 2);
    }

    /// <summary>Screen point (physical pixels) just under the island, where the desktop pet comes to call you.</summary>
    Point IslandAnchor()
    {
        if (PresentationSource.FromVisual(Island) == null) return default;
        return Island.PointToScreen(new Point(Island.ActualWidth / 2, Island.ActualHeight + 12));
    }

    void OnPetDizzy()
    {
        if (_expanded) return;
        ShowAlert(new SolidColorBrush(PetView.Cream), "\uE76E", "Calma, cliques demais!",
            "Me dá um segundo — volto ao trabalho em três segundos.", false, 3);
        AlertPet.Tint(null);
        AlertPet.React(PetView.Mood.Dizzy, 3);
    }

    // ───────────────────────── agent detail ─────────────────────────

    AgentSession? FocusedAgent => Agents.FirstOrDefault(a => a.State == "waiting") ?? Agents.FirstOrDefault();

    void UpdateAgentDetail()
    {
        var session = FocusedAgent;
        AgentDetail.Visibility = Vis(session != null);
        AgentsEmpty.Visibility = Vis(session == null);
        if (session == null) return;

        AgentProject.Text = string.IsNullOrEmpty(session.Project) ? session.Agent : session.Project;
        AgentName.Text = string.IsNullOrEmpty(session.Project) ? session.StateLabel : session.Agent;
        AgentBadge.Visibility = Vis(session.IsActive);
        AgentBadge.Background = session.OrbBrush;
        if (!ReferenceEquals(AgentSteps.ItemsSource, session.Steps)) AgentSteps.ItemsSource = session.Steps;

        AgentPet.Visibility = Vis(PetOn);
        AgentPet.Tint(session.StateColor);
        AgentPet.BaseMood = session.State switch
        {
            "waiting" => PetView.Mood.Waiting,
            "working" => PetView.Mood.Working,
            _ => PetView.Mood.Idle,
        };

        // One session with an edit: show its diff. Otherwise, everyone that is running.
        var showDiff = session.HasDiff && Agents.Count(a => a.IsActive) <= 1;
        DiffCard.Visibility = Vis(showDiff);
        AgentsList.Visibility = Vis(!showDiff);
        if (showDiff)
        {
            DiffFile.Text = session.FileName;
            DiffList.ItemsSource = session.DiffLines;
        }
    }
}
