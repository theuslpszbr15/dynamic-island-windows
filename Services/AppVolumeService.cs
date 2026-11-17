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
        _poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Sample();
    }

    public void Start() => _poll.Start();

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
            }));
            _ = _ui.InvokeAsync(() => Changed?.Invoke(state?.Level ?? level, state?.Muted ?? false, state != null));
        }
        catch { }
        finally { _busy.Release(); }
    }

    /// <summary>Toggles the app's mute (right-click on the volume column).</summary>
    public async void ToggleMute()
    {
        if (!await _busy.WaitAsync(0)) return;
        try
        {
            var state = await Task.Run(() => WithSession(volume =>
            {
                volume.GetMute(out var muted);
                var ctx = Guid.Empty;
                volume.SetMute(!muted, ref ctx);
            }));
            _ = _ui.InvokeAsync(() => Changed?.Invoke(state?.Level ?? 0, state?.Muted ?? false, state != null));
        }
        catch { }
        finally { _busy.Release(); }
    }

    (float Level, bool Muted)? Read()
    {
        var volume = FindVolume();
        if (volume == null) return null;
        volume.GetMasterVolume(out var level);
        volume.GetMute(out var muted);
        return (level, muted);
    }

    /// <summary>Applies <paramref name="apply"/> to the app's session volume, then reads the resulting state.</summary>
    (float Level, bool Muted)? WithSession(Action<ISimpleAudioVolume> apply)
    {
        var volume = FindVolume();
        if (volume == null) return null;
        apply(volume);
        volume.GetMasterVolume(out var level);
        volume.GetMute(out var muted);
        return (level, muted);
    }

    /// <summary>The app's ISimpleAudioVolume on the default output device, or null while it plays nothing.
    /// Everything except the returned volume is released here; the volume RCW is left to the finalizer
    /// (releasing the session control it was cast from would invalidate it).</summary>
    ISimpleAudioVolume? FindVolume()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0 || device == null) return null;
            try
            {
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) != 0 || obj is not IAudioSessionManager2 manager) return null;
                try
                {
                    if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions == null) return null;
                    try
                    {
                        if (sessions.GetCount(out var count) != 0) return null;
                        for (var i = 0; i < count; i++)
                        {
                            if (sessions.GetSession(i, out var control) != 0 || control == null) continue;
                            try
                            {
                                var control2 = (IAudioSessionControl2)control;
                                if (control2.GetProcessId(out var pid) != 0 || pid == 0) continue;
                                if (!ProcessNameOf(pid).Equals(_processName, StringComparison.OrdinalIgnoreCase)) continue;
                                return (ISimpleAudioVolume)control;
                            }
                            catch { }
                        }
                        return null;
                    }
                    finally { Marshal.ReleaseComObject(sessions); }
                }
                finally { Marshal.ReleaseComObject(manager); }
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    string ProcessNameOf(uint pid)
    {
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return ""; }
    }

    [ComVisible(true)]
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A7B7F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    // The full vtable matters (slots are positional): GetProcessId lives on IAudioSessionControl2,
    // which inherits these six IAudioSessionControl methods first.
    [ComImport, Guid("F4B0A507-916F-4F82-B88D-A1C5E58E7FE0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("B3EA8B37-B612-4F60-A0B8-8F58CE1B4037"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47F880D8E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
