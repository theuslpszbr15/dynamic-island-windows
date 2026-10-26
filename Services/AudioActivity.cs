using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DynamicIsland.Services;

/// <summary>
/// Apps currently making sound, from Core Audio sessions on the default output device.
/// Fallback for players that don't publish to Windows' media controls (or browsers where that is disabled).
/// </summary>
public static class AudioActivity
{
    const int eRender = 0, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23, AudioSessionStateActive = 1;

    public sealed record Source(int ProcessId, string ProcessName, float Peak);

    /// <summary>Sessions that are active and above <paramref name="threshold"/> right now on any output device, loudest first.</summary>
    public static List<Source> Sample(float threshold = 0.001f)
    {
        var result = new List<Source>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            // Not a ComImport class: VolumeService already binds this CLSID to its own RCW type.
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out devices) != 0 || devices == null) return result;
            devices.GetCount(out var deviceCount);
            for (var d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out var device) != 0 || device == null) continue;
                try { SampleDevice(device, threshold, result); }
                finally { Marshal.ReleaseComObject(device); }
            }
        }
        catch { }
        finally
        {
            if (devices != null) Marshal.ReleaseComObject(devices);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
        result.Sort((a, b) => b.Peak.CompareTo(a.Peak));
        return result;
    }

    static void SampleDevice(IMMDevice device, float threshold, List<Source> result)
    {
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) != 0) return;
            manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null) return;
            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var control) != 0 || control == null) continue;
                try
                {
                    var control2 = (IAudioSessionControl2)control;
                    if (control2.GetState(out var state) != 0 || state != AudioSessionStateActive) continue;
                    if (control2.IsSystemSoundsSession() == 0) continue;
                    control2.GetProcessId(out var pid);
                    if (pid == 0 || pid == Environment.ProcessId) continue;
                    ((IAudioMeterInformation)control).GetPeakValue(out var peak);
                    if (peak < threshold) continue;
                    string name;
                    try { name = Process.GetProcessById((int)pid).ProcessName; } catch { continue; }
                    result.Add(new Source((int)pid, name, peak));
                }
                finally { Marshal.ReleaseComObject(control); }
            }
        }
        catch { }
        finally
        {
            if (sessions != null) Marshal.ReleaseComObject(sessions);
            if (manager != null) Marshal.ReleaseComObject(manager);
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(IntPtr param, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
