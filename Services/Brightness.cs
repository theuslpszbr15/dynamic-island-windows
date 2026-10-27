using System.Management;
using System.Runtime.InteropServices;

namespace DynamicIsland.Services;

/// <summary>
/// Brightness of the monitor the island is on: DDC/CI for external screens, WMI for laptop panels.
/// Both are slow (tens of ms to a second), so call them off the UI thread.
/// </summary>
public static class Brightness
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetMonitorBrightness(IntPtr monitor, uint value);

    /// <summary>0–100, or null when the screen can't be dimmed from software.</summary>
    public static int? Get(IntPtr hwnd)
    {
        int? value = null;
        WithMonitors(hwnd, m =>
        {
            if (value == null && GetMonitorBrightness(m.Handle, out var min, out var cur, out var max) && max > min)
                value = (int)Math.Round((cur - min) * 100.0 / (max - min));
        });
        return value ?? WmiGet();
    }

    public static void Set(IntPtr hwnd, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var done = false;
        WithMonitors(hwnd, m =>
        {
            if (GetMonitorBrightness(m.Handle, out var min, out _, out var max) && max > min)
                done |= SetMonitorBrightness(m.Handle, (uint)(min + (max - min) * percent / 100.0));
        });
        if (!done) WmiSet(percent);
    }

    static void WithMonitors(IntPtr hwnd, Action<PhysicalMonitor> action)
    {
        var monitor = MonitorFromWindow(hwnd, 2 /* nearest */);
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0) return;
        var monitors = new PhysicalMonitor[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors)) return;
        try { foreach (var m in monitors) action(m); }
        finally { DestroyPhysicalMonitors(count, monitors); }
    }

    static int? WmiGet()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var item in searcher.Get())
                using (item) return Convert.ToInt32(item["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    static void WmiSet(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject item in searcher.Get())
                using (item) item.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)percent]);
        }
        catch { }
    }
}
