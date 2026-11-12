using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>
/// Who joins and leaves the user's Discord call, read from the Discord window itself through Windows UI Automation
/// (what screen readers use): no Discord app, no tester list, nothing leaves the PC. It only looks while Discord is using
/// the microphone (<see cref="SetInCall"/>, from the privacy monitor), wakes on the window's structure-changed events and
/// checks again every few seconds as a safety net.
/// Limits: Discord shows the call's member list only while the call's server is open in the channel sidebar, the list
/// has display names (no @username), and pictures are cut from the screen when the window is visible.
/// It finds the list by Discord's CSS class prefixes (voiceUser_, username_, modeConnected_…), not by text, so the
/// Discord language doesn't matter; a Discord redesign can rename them and then this needs updating.
/// </summary>
public sealed class DiscordWindow(Dispatcher dispatcher) : IDisposable
{
    static readonly string[] Processes = ["Discord", "DiscordPTB", "DiscordCanary"];
    static readonly TimeSpan Safety = TimeSpan.FromSeconds(3), Searching = TimeSpan.FromSeconds(8);

    public event Action<DiscordVoiceEvent>? Changed;
    /// <summary>In a call, but its member list isn't on screen in Discord (another server open, or Discord in the tray).</summary>
    public event Action? ListHidden;

    readonly AutoResetEvent _wake = new(false);
    readonly object _gate = new();
    Thread? _thread;
    volatile bool _enabled, _inCall, _disposed;

    /// <summary>The feature is on (Configurações).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Kick();
        }
    }

    /// <summary>Discord holds the microphone: the user is in a call (muted or not, Discord keeps it open).</summary>
    public void SetInCall(bool inCall)
    {
        if (_inCall == inCall) return;
        _inCall = inCall;
        Kick();
    }

    void Kick()
    {
        lock (_gate)
        {
            if (_enabled && _inCall && _thread == null && !_disposed)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "DiscordWindow" };
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
            }
        }
        _wake.Set();
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
    }

    bool Active => _enabled && _inCall && !_disposed;

    // ───────────────────────── the watch loop ─────────────────────────

    AutomationElement? _container, _panel;
    StructureChangedEventHandler? _handler;
    string? _channel;
    Dictionary<string, int>? _present; // display name → how many rows (two people can share a name)
    readonly Dictionary<string, BitmapSource> _avatars = [];
    bool _warned, _woken;

    void Run()
    {
        try
        {
            while (Active)
            {
                var found = false;
                try { found = Scan(); }
                catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
                {
                    Forget();
                }
                catch (Exception ex)
                {
                    Log.Error("DiscordWindow", ex);
                    Forget();
                }
                if (!found && !_warned)
                {
                    _warned = true; // once per call
                    dispatcher.InvokeAsync(() => ListHidden?.Invoke());
                }
                _wake.WaitOne(found ? Safety : Searching);
            }
        }
        finally
        {
            Forget();
            _present = null;
            _channel = null;
            _warned = _woken = false;
            _avatars.Clear();
            lock (_gate) _thread = null;
            if (Active) Kick(); // turned back on while this thread was leaving
        }
    }

    void Forget()
    {
        if (_container != null && _handler != null)
            try { Automation.RemoveStructureChangedEventHandler(_container, _handler); } catch { }
        _container = _panel = null;
        _handler = null;
    }

    static IntPtr DiscordHwnd()
    {
        foreach (var name in Processes)
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
        return IntPtr.Zero;
    }

    static readonly CacheRequest Props = MakeRequest();

    static CacheRequest MakeRequest()
    {
        var request = new CacheRequest { TreeScope = TreeScope.Subtree, TreeFilter = Automation.RawViewCondition };
        request.Add(AutomationElement.ClassNameProperty);
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        return request;
    }

    /// <summary>Reads the call's member list; false when it isn't on screen.</summary>
    bool Scan(bool retry = true)
    {
        if (_container == null && !Locate()) return false;
        AutomationElement tree;
        using (Props.Activate()) tree = _container!.GetUpdatedCache(Props);
        if (!Class(tree, "containerDefault_") || Find(tree, e => Class(e, "modeConnected_")) is not { } channelRow)
        {
            Forget(); // the user moved to another channel or the row was rebuilt
            return retry && Locate() && Scan(retry: false);
        }
        var channel = ChannelName(channelRow) ?? "";
        var rows = new List<(string Name, Rect Row, Rect NameRect)>();
        foreach (var user in All(tree, e => Class(e, "voiceUser_")))
        {
            if (Find(user, e => Class(e, "username_")) is not { } nameGroup) continue;
            var text = Find(nameGroup, e => e.Cached.ControlType == ControlType.Text && e.Cached.Name.Length > 0);
            if (text == null) continue;
            rows.Add((text.Cached.Name, user.Cached.BoundingRectangle, text.Cached.BoundingRectangle));
        }

        var now = new Dictionary<string, int>();
        foreach (var r in rows) now[r.Name] = now.GetValueOrDefault(r.Name) + 1;
        var panel = PanelAvatars();
        CaptureAvatars(rows, panel);

        if (_present == null || channel != _channel)
        {
            // Just joined (or switched channels): the people already there aren't news.
            _present = now;
            _channel = channel;
            return true;
        }
        var count = rows.Count;
        foreach (var (name, n) in now)
            for (var i = _present.GetValueOrDefault(name); i < n; i++) Raise(true, name, channel, count);
        foreach (var (name, n) in _present)
            for (var i = now.GetValueOrDefault(name); i < n; i++) Raise(false, name, channel, count);
        _present = now;
        return true;
    }

    /// <summary>Finds the connected channel's container (and the bottom "voice connected" panel) in the window.</summary>
    bool Locate()
    {
        var hwnd = DiscordHwnd();
        if (hwnd == IntPtr.Zero) return false;
        var root = AutomationElement.FromHandle(hwnd);
        if (!_woken)
        {
            // Chromium builds its accessibility tree once a client asks for it; the first walk is what wakes it.
            _woken = true;
            root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            Thread.Sleep(1500);
            root = AutomationElement.FromHandle(hwnd);
        }
        var request = new CacheRequest();
        request.Add(AutomationElement.ClassNameProperty);
        AutomationElementCollection groups;
        using (request.Activate())
            groups = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));
        AutomationElement? connected = null;
        foreach (AutomationElement g in groups)
        {
            var cls = g.Cached.ClassName ?? "";
            if (connected == null && cls.Contains("modeConnected_") && cls.Contains("selectedChannel_")) connected = g;
            else if (_panel == null && cls.Contains("voiceUsers_")) _panel = g;
        }
        if (connected == null) return false;
        var container = TreeWalker.RawViewWalker.GetParent(connected);
        if (container == null) return false;
        _container = container;
        _handler = (_, _) => _wake.Set();
        try { Automation.AddStructureChangedEventHandler(container, TreeScope.Subtree, _handler); }
        catch { _handler = null; } // the safety-net timer still covers it
        return true;
    }

    /// <summary>"🍄・Mushyo (canal de voz), Bea, …" → "🍄・Mushyo" (the text before " (" in any language).</summary>
    static string? ChannelName(AutomationElement row)
    {
        var link = Find(row, e => e.Cached.ControlType == ControlType.Hyperlink && e.Cached.Name.Length > 0);
        if (link == null) return null;
        var name = link.Cached.Name;
        var cut = name.IndexOf(" (", StringComparison.Ordinal);
        return cut > 0 ? name[..cut] : name.Split(',')[0];
    }

    // ───────────────────────── pictures ─────────────────────────

    /// <summary>The 28 px avatars of the bottom panel, each followed by its (hidden) "Name, state" text.</summary>
    List<(string Label, Rect Rect)> PanelAvatars()
    {
        var list = new List<(string, Rect)>();
        if (_panel == null) return list;
        try
        {
            AutomationElement tree;
            using (Props.Activate()) tree = _panel.GetUpdatedCache(Props);
            Rect? pending = null;
            Walk(tree, e =>
            {
                if (Class(e, "avatarContainer_")) pending = e.Cached.BoundingRectangle;
                else if (pending is { } r && e.Cached.ControlType == ControlType.Text && e.Cached.Name.Length > 0)
                {
                    list.Add((e.Cached.Name, r));
                    pending = null;
                }
            });
        }
        catch { _panel = null; }
        return list;
    }

    void CaptureAvatars(List<(string Name, Rect Row, Rect NameRect)> rows, List<(string Label, Rect Rect)> panel)
    {
        var hwnd = DiscordHwnd();
        foreach (var (name, row, nameRect) in rows)
        {
            if (_avatars.ContainsKey(name)) continue;
            // Prefer the panel's exact square; otherwise the round picture left of the name in the channel list.
            Rect? square = null;
            foreach (var (label, rect) in panel)
                if (label == name || label.StartsWith(name + ",", StringComparison.Ordinal)) { square = rect; break; }
            if (square == null && !row.IsEmpty && !nameRect.IsEmpty)
            {
                var gap = nameRect.X - row.X;
                var size = Math.Round(Math.Min(gap * 0.6, row.Height - 6));
                if (size >= 12) square = new Rect(nameRect.X - size - gap * 0.22, row.Y + (row.Height - size) / 2, size, size);
            }
            if (square is { } s && Grab(hwnd, s) is { } image)
            {
                if (_avatars.Count > 100) _avatars.Clear();
                _avatars[name] = image;
            }
        }
    }

    /// <summary>A small piece of the screen, only when Discord itself is what's showing there.</summary>
    static BitmapSource? Grab(IntPtr discord, Rect r)
    {
        if (discord == IntPtr.Zero || r.IsEmpty || r.Width > 128 || r.Height > 128) return null;
        var center = new POINT { X = (int)(r.X + r.Width / 2), Y = (int)(r.Y + r.Height / 2) };
        if (GetAncestor(WindowFromPoint(center), 2 /* GA_ROOT */) != discord || IsIconic(discord)) return null;
        try
        {
            using var bitmap = new System.Drawing.Bitmap((int)r.Width, (int)r.Height);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
                g.CopyFromScreen((int)r.X, (int)r.Y, 0, 0, bitmap.Size);
            var handle = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(handle); }
        }
        catch { return null; }
    }

    void Raise(bool joined, string name, string channel, int count)
    {
        _avatars.TryGetValue(name, out var avatar);
        dispatcher.InvokeAsync(() => Changed?.Invoke(new DiscordVoiceEvent(joined, "", name, "", channel, count, avatar)));
    }

    // ───────────────────────── cached tree helpers ─────────────────────────

    static bool Class(AutomationElement e, string prefix)
    {
        var cls = e.Cached.ClassName;
        return !string.IsNullOrEmpty(cls) && (cls.StartsWith(prefix, StringComparison.Ordinal) || cls.Contains(" " + prefix, StringComparison.Ordinal));
    }

    static void Walk(AutomationElement e, Action<AutomationElement> visit)
    {
        visit(e);
        foreach (AutomationElement child in e.CachedChildren) Walk(child, visit);
    }

    static AutomationElement? Find(AutomationElement e, Func<AutomationElement, bool> match)
    {
        foreach (AutomationElement child in e.CachedChildren)
        {
            if (match(child)) return child;
            if (Find(child, match) is { } deeper) return deeper;
        }
        return null;
    }

    static List<AutomationElement> All(AutomationElement e, Func<AutomationElement, bool> match)
    {
        var list = new List<AutomationElement>();
        Walk(e, x => { if (!ReferenceEquals(x, e) && match(x)) list.Add(x); });
        return list;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
}
