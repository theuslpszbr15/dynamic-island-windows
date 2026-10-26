using Microsoft.Win32;

namespace DynamicIsland.Services;

public sealed record PrivacyState(IReadOnlyList<string> Microphone, IReadOnlyList<string> Camera)
{
    public static readonly PrivacyState None = new([], []);
    public bool Same(PrivacyState other) => Microphone.SequenceEqual(other.Microphone) && Camera.SequenceEqual(other.Camera);
}

/// <summary>Which apps are using the microphone/camera right now, from the same registry data Windows' privacy indicator uses.</summary>
public static class PrivacyMonitor
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Edge", ["chrome"] = "Chrome", ["firefox"] = "Firefox", ["brave"] = "Brave", ["opera"] = "Opera",
        ["Code"] = "VS Code", ["MSTeams"] = "Teams", ["ms-teams"] = "Teams", ["Teams"] = "Teams",
        ["slackdesktop"] = "Slack", ["slack"] = "Slack", ["Zoom"] = "Zoom", ["discord"] = "Discord",
        ["obs64"] = "OBS", ["WhatsAppDesktop"] = "WhatsApp", ["WindowsCamera"] = "Câmera",
        ["CBS"] = "Windows", ["WindowsSoundRecorder"] = "Gravador de Som",
    };

    public static PrivacyState Read() => new(InUse("microphone"), InUse("webcam"));

    static List<string> InUse(string capability)
    {
        var apps = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root + capability);
            if (root != null) Collect(root, apps);
        }
        catch { }
        return apps.Distinct().Order().ToList();
    }

    static void Collect(RegistryKey parent, List<string> apps)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            using var key = parent.OpenSubKey(name);
            if (key == null) continue;
            if (name == "NonPackaged") { Collect(key, apps); continue; }
            if (key.GetValue("LastUsedTimeStart") is long start && start > 0 && key.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                apps.Add(Friendly(name));
        }
    }

    static string Friendly(string key)
    {
        string name;
        if (key.Contains('#')) name = Path.GetFileNameWithoutExtension(key.Replace('#', '\\'));
        else
        {
            name = key.Split('_')[0];
            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name[(dot + 1)..];
        }
        if (Known.TryGetValue(name, out var friendly)) return friendly;
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : key;
    }
}
