using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace DynamicIsland.Services;

/// <summary>Finds the tab that is playing sound in a browser, through UI Automation (call off the UI thread).</summary>
public static partial class BrowserTabs
{
    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "opera", "vivaldi", "firefox",
    };

    // Chromium appends this to the accessible name of a tab that is making sound.
    static readonly string[] AudioMarkers =
    [
        " - Reprodução de áudio", " - Audio playing", " - Reproduciendo audio", " - Áudio em reprodução",
    ];

    [GeneratedRegex(@"^(Uso( elevado)? da memória em|(High )?[Mm]emory usage (in|-))\s+")]
    private static partial Regex MemoryPrefix();

    [GeneratedRegex(@":\s*[\d.,]+\s*[KMG]B$")]
    private static partial Regex MemorySuffix();

    public static bool IsBrowser(string processName) => Browsers.Contains(processName);

    public static string? PlayingTabTitle(string processName)
    {
        try
        {
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            foreach (var process in Process.GetProcessesByName(processName))
            {
                foreach (var hwnd in Native.TopLevelWindows(process.Id))
                {
                    foreach (AutomationElement tab in AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, condition))
                    {
                        var name = tab.Current.Name ?? "";
                        foreach (var marker in AudioMarkers)
                        {
                            var at = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                            if (at <= 0) continue;
                            var title = MemorySuffix().Replace(MemoryPrefix().Replace(name[..at], ""), "").Trim();
                            if (title.Length > 0) return title;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }
}
