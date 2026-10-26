using System.Windows.Threading;
using Windows.System.Power;

namespace DynamicIsland.Services;

public enum BatteryEvent { PluggedIn, Unplugged, Low }

public sealed class BatteryService(Dispatcher ui)
{
    int _lastPercent = -1;

    /// <summary>Raised on the UI thread.</summary>
    public event Action<BatteryEvent>? Changed;

    public bool HasBattery { get { try { return PowerManager.BatteryStatus != BatteryStatus.NotPresent; } catch { return false; } } }
    public int Percent { get { try { return PowerManager.RemainingChargePercent; } catch { return 0; } } }
    public bool PluggedIn { get { try { return PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent; } catch { return true; } } }
    public bool Charging { get { try { return PowerManager.BatteryStatus == BatteryStatus.Charging; } catch { return false; } } }

    public void Start()
    {
        if (!HasBattery) return;
        _lastPercent = Percent;
        try
        {
            PowerManager.PowerSupplyStatusChanged += (_, _) =>
                ui.InvokeAsync(() => Changed?.Invoke(PluggedIn ? BatteryEvent.PluggedIn : BatteryEvent.Unplugged));
            PowerManager.RemainingChargePercentChanged += (_, _) => ui.InvokeAsync(OnPercent);
        }
        catch { }
    }

    void OnPercent()
    {
        var pct = Percent;
        var crossed = !PluggedIn && ((_lastPercent > 20 && pct <= 20) || (_lastPercent > 10 && pct <= 10));
        _lastPercent = pct;
        if (crossed) Changed?.Invoke(BatteryEvent.Low);
    }
}
