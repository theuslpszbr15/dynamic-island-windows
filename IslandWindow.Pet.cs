using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

// Pipoca (the pet) and the colour themes.
public partial class IslandWindow
{
    static readonly TimeSpan SleepAfter = TimeSpan.FromSeconds(90);
    DateTime _lastActivity = DateTime.Now;

    PetView[] Pets => [CompactPet, HeaderPet, AlertPet];
    bool PetOn => _settings.Pet;

    public void ApplyLook()
    {
        var ground = new SolidColorBrush(IslandThemes.Of(_settings.Theme));
        ground.Freeze();
        IslandBg.Background = ground;
        EarLeft.Fill = EarRight.Fill = ground;

        var pet = PetOn;
        CompactPet.Visibility = HeaderPetBox.Visibility = AlertPetBox.Visibility = Vis(pet);
        AlertOrb.Visibility = AlertGlyph.Visibility = Vis(!pet);
        // The pet takes the slot of the album art, the agent orb and the timer ring's left edge.
        CompactArt.Visibility = Vis(!pet);
        CompactOrb.Visibility = Vis(!pet);
        CompactClock.Margin = new Thickness(pet ? 26 : 0, 0, 0, 0);
        CompactTimerRing.Margin = new Thickness(pet ? 38 : 12, 0, 0, 0);
        PageTitle.Margin = new Thickness(pet ? 27 : 0, 0, 0, 0);
        UpdatePet();
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
}
