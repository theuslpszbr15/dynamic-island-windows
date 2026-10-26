using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>Listens to the default output device's master volume through Core Audio.</summary>
public sealed class VolumeService : IDisposable
{
    const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23;

    readonly Dispatcher _ui;
    readonly Callback _callback;
    readonly DispatcherTimer _deviceWatch;
    IMMDeviceEnumerator? _enumerator;
    IAudioEndpointVolume? _volume;
    string? _deviceId;

    /// <summary>Raised on the UI thread with (level 0..1, muted).</summary>
    public event Action<float, bool>? Changed;

    public VolumeService(Dispatcher ui)
    {
        _ui = ui;
        _callback = new Callback(this);
        _deviceWatch = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(3) };
        _deviceWatch.Tick += (_, _) => EnsureDevice();
    }

    public void Start()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            EnsureDevice();
            _deviceWatch.Start();
        }
        catch { }
    }

    void EnsureDevice()
    {
        if (_enumerator == null) return;
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0 || device == null)
            {
                Detach();
                return;
            }
            device.GetId(out var id);
            if (id == _deviceId && _volume != null)
            {
                Marshal.ReleaseComObject(device);
                return;
            }
            Detach();
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) == 0)
            {
                _volume = (IAudioEndpointVolume)obj;
                _volume.RegisterControlChangeNotify(_callback);
                _deviceId = id;
            }
            Marshal.ReleaseComObject(device);
        }
        catch { Detach(); }
    }

    void Detach()
    {
        if (_volume != null)
        {
            try { _volume.UnregisterControlChangeNotify(_callback); } catch { }
            try { Marshal.ReleaseComObject(_volume); } catch { }
            _volume = null;
        }
        _deviceId = null;
    }

    public void Nudge(float delta)
    {
        if (_volume == null) return;
        try
        {
            _volume.GetMasterVolumeLevelScalar(out var level);
            var context = Guid.Empty;
            _volume.SetMasterVolumeLevelScalar(Math.Clamp(level + delta, 0f, 1f), ref context);
        }
        catch { }
    }

    void OnNotify(IntPtr data)
    {
        // AUDIO_VOLUME_NOTIFICATION_DATA: GUID (16) | BOOL bMuted | float fMasterVolume
        var muted = Marshal.ReadInt32(data, 16) != 0;
        var level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
        _ui.InvokeAsync(() => Changed?.Invoke(level, muted));
    }

    public void Dispose()
    {
        _deviceWatch.Stop();
        Detach();
        if (_enumerator != null) Marshal.ReleaseComObject(_enumerator);
        _enumerator = null;
    }

    [ComVisible(true)]
    sealed class Callback(VolumeService owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            owner.OnNotify(data);
            return 0;
        }
    }

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

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }
}
