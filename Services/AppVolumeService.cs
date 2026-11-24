using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>Reads and sets one app's volume in the Windows Volume Mixer (its own audio session,
/// found by process name), so the island changes the music app's volume only — not the whole system.</summary>
public sealed class AppVolumeService : IDisposable
{
    const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23;

    readonly Dispatcher _ui;
    readonly string _processName;
    readonly DispatcherTimer _poll;
    readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>Raised on the UI thread with (level 0..1, muted, session found).</summary>
    public event Action<float, bool, bool>? Changed;

    public AppVolumeService(Dispatcher ui, string processName = "spotify")
    {
        _ui = ui;
        _processName = processName;
        // Background priority starves while the island animates every frame
        _poll = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Sample();
    }

    public void Start() => _poll.Start();

    /// <summary>Every second while its slider can be seen; a slow check otherwise (each check walks the audio sessions).</summary>
    public void SetWatching(bool watching)
    {
        var interval = TimeSpan.FromSeconds(watching ? 1 : 6);
        if (_poll.Interval == interval) return;
        _poll.Interval = interval;
        if (watching) Sample();
    }

    public void Dispose() => _poll.Stop();

    void Sample()
    {
        if (_busy.CurrentCount == 0) return; // a drag is writing; the next tick catches up
        _ = SampleAsync();
    }

    async Task SampleAsync()
    {
        if (!await _busy.WaitAsync(0)) return;
        try
        {
            var state = await Task.Run(Read);
            _ = _ui.InvokeAsync(() => Changed?.Invoke(state?.Level ?? 0, state?.Muted ?? false, state != null));
        }
        catch { }
        finally { _busy.Release(); }
    }

    /// <summary>Sets the app's volume (0..1); the poll then reports it back.</summary>
    public async void Set(float level)
    {
        if (!await _busy.WaitAsync(0)) return;
        try
        {
            var state = await Task.Run(() => WithSession(volume =>
            {
                var ctx = Guid.Empty;
                volume.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref ctx);
                if (level > 0) volume.SetMute(false, ref ctx);
            }));
            _ = _ui.InvokeAsync(() => Changed?.Invoke(state?.Level ?? level, state?.Muted ?? false, state != null));
        }
        catch { }
        finally { _busy.Release(); }
    }

    /// <summary>Toggles the app's mute (right-click on the volume column). Returns false when Spotify has no audio session.</summary>
    public bool ToggleMute()
    {
        if (!_busy.Wait(0)) return true;
        try
        {
            var state = WithSession(volume =>
            {
                volume.GetMute(out var muted);
                var ctx = Guid.Empty;
                volume.SetMute(!muted, ref ctx);
            });
            if (state != null) _ui.InvokeAsync(() => Changed?.Invoke(state.Value.Level, state.Value.Muted, true));
            return state != null;
        }
        catch { return false; }
        finally { _busy.Release(); }
    }

    (float Level, bool Muted)? Read()
    {
        var volumes = FindVolumes();
        if (volumes.Count == 0) return null;
        volumes[0].GetMasterVolume(out var level);
        volumes[0].GetMute(out var muted);
        return (level, muted);
    }

    /// <summary>Applies <paramref name="apply"/> to every Spotify session (all output devices), then reads the resulting state.</summary>
    (float Level, bool Muted)? WithSession(Action<ISimpleAudioVolume> apply)
    {
        var volumes = FindVolumes();
        if (volumes.Count == 0) return null;
        foreach (var v in volumes) { try { apply(v); } catch { } }
        volumes[0].GetMasterVolume(out var level);
        volumes[0].GetMute(out var muted);
        return (level, muted);
    }

    /// <summary>Raises/lowers Spotify's volume by <paramref name="delta"/> (mouse wheel). Returns false when Spotify has no audio session.</summary>
    public bool Nudge(float delta)
    {
        if (!_busy.Wait(0)) return true;
        try
        {
            var state = WithSession(volume =>
            {
                volume.GetMasterVolume(out var cur);
                var ctx = Guid.Empty;
                volume.SetMasterVolume(Math.Clamp(cur + delta, 0f, 1f), ref ctx);
                // raising a muted app should be heard
                if (delta > 0) volume.SetMute(false, ref ctx);
            });
            if (state != null) _ui.InvokeAsync(() => Changed?.Invoke(state.Value.Level, state.Value.Muted, true));
            return state != null;
        }
        catch { return false; }
        finally { _busy.Release(); }
    }

    /// <summary>For Jarvis: "aumentar", "diminuir", "mudo", "tirar_mudo" or "definir" (<paramref name="percent"/>);
    /// how it ended up, or null when Spotify has no audio session.</summary>
    public string? Apply(string action, double percent)
    {
        if (!_busy.Wait(0)) return null;
        try
        {
            var state = WithSession(volume =>
            {
                volume.GetMasterVolume(out var cur);
                volume.GetMute(out var muted);
                var ctx = Guid.Empty;
                var level = action switch
                {
                    "aumentar" => cur + 0.15f,
                    "diminuir" => cur - 0.15f,
                    "definir" => (float)(percent / 100),
                    _ => cur,
                };
                volume.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref ctx);
                var mute = action switch { "mudo" => true, "tirar_mudo" or "aumentar" => false, "definir" => level <= 0, _ => muted };
                volume.SetMute(mute, ref ctx);
            });
            if (state is not { } s) return null;
            _ui.InvokeAsync(() => Changed?.Invoke(s.Level, s.Muted, true));
            return s.Muted ? $"Spotify no mudo (volume guardado em {Math.Round(s.Level * 100)}%)." : $"Spotify agora em {Math.Round(s.Level * 100)}%.";
        }
        catch { return null; }
        finally { _busy.Release(); }
    }

    /// <summary>Every Spotify ISimpleAudioVolume on every active output device (any process named like the app, any session).
    /// Device/manager/enumerator are released here; the volume RCWs are left to the finalizer
    /// (releasing the session control they were cast from would invalidate them).</summary>
    List<ISimpleAudioVolume> FindVolumes()
    {
        var found = new List<ISimpleAudioVolume>();
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        try
        {
            if (enumerator.EnumAudioEndpoints(eRender, 1 /* DEVICE_STATE_ACTIVE */, out var devices) != 0 || devices == null) return found;
            try
            {
                if (devices.GetCount(out var deviceCount) != 0) return found;
                for (uint d = 0; d < deviceCount; d++)
                {
                    if (devices.Item(d, out var device) != 0 || device == null) continue;
                    try { CollectFromDevice(device, found); }
                    catch { }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally { Marshal.ReleaseComObject(devices); }
        }
        catch { }
        finally { Marshal.ReleaseComObject(enumerator); }
        return found;
    }

    void CollectFromDevice(IMMDevice device, List<ISimpleAudioVolume> found)
    {
        var iid = typeof(IAudioSessionManager2).GUID;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) != 0 || obj is not IAudioSessionManager2 manager) return;
        try
        {
            if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions == null) return;
            try
            {
                if (sessions.GetCount(out var count) != 0) return;
                for (var i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var control) != 0 || control == null) continue;
                    try
                    {
                        if (control.GetProcessId(out var pid) != 0 || pid == 0) continue;
                        if (!ProcessNameOf(pid).Equals(_processName, StringComparison.OrdinalIgnoreCase)) continue;
                        found.Add((ISimpleAudioVolume)control);
                    }
                    catch { }
                }
            }
            finally { Marshal.ReleaseComObject(sessions); }
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    string ProcessNameOf(uint pid)
    {
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return ""; }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        // IAudioSessionManager first (the vtable is shared)
        [PreserveSig] int GetAudioSessionControl(ref Guid sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionGuid, int flags, out ISimpleAudioVolume volume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        [PreserveSig] int RegisterSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notify);
        [PreserveSig] int RegisterDuckNotification(IntPtr notUsed, IntPtr notify);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notify);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    // slots are positional: IAudioSessionControl's nine methods come first
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
