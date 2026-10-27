using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DynamicIsland;

// The mascot, the colour themes (including frosted glass) and the agent detail page.
public partial class IslandWindow
{
    static readonly TimeSpan SleepAfter = TimeSpan.FromSeconds(90);
    DateTime _lastActivity = DateTime.Now, _lastGlass;
    Effect? _shadow;
    System.Windows.Threading.DispatcherTimer? _glassTimer;
    bool _glassBusy;

    PetView[] Pets => [CompactPet, HeaderPet, AlertPet, AgentPet];
    bool PetOn => _settings.Mascot != PetSpecies.None;
    bool Glass => _settings.Theme == IslandThemes.Glass;

    public void ApplyLook()
    {
        _shadow ??= IslandBg.Effect;
        var ground = new SolidColorBrush(IslandThemes.Of(_settings.Theme));
        ground.Freeze();
        IslandBg.Background = ground;
        EarLeft.Fill = EarRight.Fill = ground;
        // The glass carries the shadow: the tint above it is see-through and would only darken it.
        IslandBg.Effect = Glass ? null : _shadow;
        GlassFill.Effect = Glass ? _shadow : null;
        ApplyGlass();

        var pet = PetOn;
        PetView.SetDefaultSpecies(_settings.Mascot);
        CompactPet.Visibility = HeaderPetBox.Visibility = AlertPetBox.Visibility = Vis(pet);
        AlertOrb.Visibility = AlertGlyph.Visibility = Vis(!pet);
        // The mascot takes the slot of the album art, the agent orb and the timer ring's left edge.
        CompactArt.Visibility = Vis(!pet);
        CompactOrb.Visibility = Vis(!pet);
        CompactClock.Margin = new Thickness(pet ? 26 : 0, 0, 0, 0);
        CompactTimerRing.Margin = new Thickness(pet ? 38 : 12, 0, 0, 0);
        PageTitle.Margin = new Thickness(pet ? 27 : 0, 0, 0, 0);
        UpdatePet();
    }

    // DWM's own blur only covers whole window rectangles, so the glass is made here from a capture of the
    // screen behind the island (see LiquidGlass). The island is excluded from screen capture while glass
    // is on, otherwise it would capture itself.
    void ApplyGlass()
    {
        if (_hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(_hwnd, Glass ? Native.WDA_EXCLUDEFROMCAPTURE : 0);
        GlassFill.Visibility = GlassRim.Visibility = Vis(Glass);
        if (!Glass)
        {
            _glassTimer?.Stop();
            GlassFill.Background = null;
            return;
        }
        _glassTimer ??= MakeTimer(0.2, () => CaptureGlass(true));
        _glassTimer.Start();
        CaptureGlass(true);
    }

    async void CaptureGlass(bool force = false)
    {
        if (!Glass || _glassBusy || _hiddenFullscreen || PresentationSource.FromVisual(Island) == null) return;
        var now = DateTime.Now;
        if (!force && now - _lastGlass < TimeSpan.FromMilliseconds(50)) return;
        _lastGlass = now;
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = Island.PointToScreen(new Point(0, 0));
        int x = (int)Math.Round(origin.X), y = (int)Math.Round(origin.Y);
        int w = (int)Math.Round(Island.Width * dpi.DpiScaleX), h = (int)Math.Round(Island.Height * dpi.DpiScaleY);
        var radius = IslandBg.CornerRadius.BottomLeft * dpi.DpiScaleX;
        var flatTop = IslandBg.CornerRadius.TopLeft < IslandBg.CornerRadius.BottomLeft;

        _glassBusy = true;
        try
        {
            var shot = await Task.Run(() => LiquidGlass.Render(x, y, w, h, radius, flatTop, dpi.DpiScaleX));
            if (shot == null || !Glass) return;
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
        CompactPet.Active = !_expanded && !transient && !IsHandle;
        HeaderPet.Active = _expanded;
        AlertPet.Active = _transient == Transient.AgentAlert;
        AgentPet.Active = _expanded && _page == PageAgents;
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
    }

    void AlertPetFor(Brush orb, bool waiting)
    {
        if (!PetOn) return;
        AlertPet.Tint((orb as SolidColorBrush)?.Color);
        AlertPet.BaseMood = waiting ? PetView.Mood.Waiting : PetView.Mood.Idle;
        if (!waiting) AlertPet.React(PetView.Mood.Happy, 2);
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
