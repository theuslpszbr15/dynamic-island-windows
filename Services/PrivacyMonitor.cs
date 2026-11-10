using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

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

    /// <summary>True while a <see cref="Watch"/> thread is delivering changes; otherwise callers poll <see cref="Read"/>.</summary>
    public static bool Watching { get; private set; }

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, int filter, SafeWaitHandle signal, bool asynchronous);

    const int NotifyName = 0x1, NotifyLastSet = 0x4;

    /// <summary>
    /// Calls <paramref name="changed"/> on a worker thread once now and again whenever Windows rewrites the
    /// microphone/camera data, so nothing polls the registry (it used to be read every 0.75 s).
    /// </summary>
    public static void Watch(Action changed, CancellationToken cancel)
    {
        var keys = new List<RegistryKey>();
        foreach (var capability in new[] { "microphone", "webcam" })
        {
            try { if (Registry.CurrentUser.OpenSubKey(Root + capability) is { } key) keys.Add(key); }
            catch { }
        }
        if (keys.Count == 0) return;
        Watching = true;
        new Thread(() => WatchLoop(keys, changed, cancel)) { IsBackground = true, Name = "PrivacyWatch" }.Start();
    }

    static void WatchLoop(List<RegistryKey> keys, Action changed, CancellationToken cancel)
    {
        var signals = keys.Select(_ => new AutoResetEvent(false)).ToList();
        var armed = new bool[keys.Count];
        var waits = signals.Cast<WaitHandle>().Append(cancel.WaitHandle).ToArray();
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                // Arm before reading, so a change in between is never missed. Each key is armed once per change:
                // registrations on the same key would otherwise pile up.
                for (var i = 0; i < keys.Count; i++)
                {
                    if (armed[i]) continue;
                    if (RegNotifyChangeKeyValue(keys[i].Handle, true, NotifyName | NotifyLastSet, signals[i].SafeWaitHandle, true) != 0) return;
                    armed[i] = true;
                }
                changed();
                var fired = WaitHandle.WaitAny(waits);
                if (fired == keys.Count) return;
                armed[fired] = false;
                // Windows writes the start and stop times in a quick burst; read once it has landed.
                Thread.Sleep(150);
                for (var i = 0; i < keys.Count; i++)
                    if (armed[i] && signals[i].WaitOne(0)) armed[i] = false;
            }
        }
        catch (Exception ex) { Log.Error("PrivacyMonitor.Watch", ex); }
        finally
        {
            Watching = false;
            foreach (var key in keys) key.Dispose();
            foreach (var signal in signals) signal.Dispose();
        }
    }

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
