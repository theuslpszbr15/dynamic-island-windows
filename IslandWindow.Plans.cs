using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// AI plan usage next to the compact clock: a dot in the tool's colour and how much of the plan is used.
public partial class IslandWindow
{
    const double PlanChipWidth = 46;
    List<PlanUse> _plans = [];
    List<PlanUse>? _chipsFor;
    DispatcherTimer? _plansTimer;
    bool _plansBusy;

    public IReadOnlyList<PlanUse> Plans => _plans;
    public IReadOnlyList<string> PlanNotes { get; private set; } = [];
    public event Action? PlansChanged;

    double PlansWidth => PlanChipsOn ? _plans.Count * PlanChipWidth : 0;
    bool PlanChipsOn => _settings.PlanUsage && _plans.Count > 0;

    /// <summary>Reads now and then every 5 minutes (each tool's API is asked at most that often).</summary>
    public void ApplyPlanUsage()
    {
        if (!_settings.PlanUsage || SnapshotMode)
        {
            _plansTimer?.Stop();
            _plans = [];
            Retarget();
            return;
        }
        _plansTimer ??= MakeTimer(300, () => _ = RefreshPlansAsync());
        _plansTimer.Start();
        _ = RefreshPlansAsync();
    }

    public async Task RefreshPlansAsync()
    {
        if (_plansBusy) return;
        _plansBusy = true;
        try
        {
            var (plans, notes) = await PlanUsage.ReadAllAsync();
            _plans = _settings.PlanUsage ? plans : [];
            PlanNotes = notes;
            Retarget();
            PlansChanged?.Invoke();
        }
        finally { _plansBusy = false; }
    }

    void UpdatePlanChips(bool clock)
    {
        var show = clock && PlanChipsOn;
        CompactPlans.Visibility = Vis(show);
        // the clock moves left to make room, so clock and chips stay centred together
        CompactClock.Margin = new Thickness(0, 0, show ? PlansWidth : 0, 0);
        if (!show || ReferenceEquals(_chipsFor, _plans)) return;
        _chipsFor = _plans;
        CompactPlans.Children.Clear();
        foreach (var plan in _plans)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Width = PlanChipWidth, VerticalAlignment = VerticalAlignment.Center };
            chip.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(plan.Color), VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock
            {
                Text = $"{Math.Round(plan.UsedPercent)}%", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (plan.UsedPercent >= 90) text.Foreground = new SolidColorBrush(Meter.Red);
            else text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            chip.Children.Add(text);
            CompactPlans.Children.Add(chip);
        }
    }
}
