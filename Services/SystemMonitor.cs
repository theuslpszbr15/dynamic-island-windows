namespace DynamicIsland.Services;

public sealed class SystemMonitor
{
    long _idle, _kernel, _user;
    bool _primed;

    public double CpuPercent()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        if (!_primed)
        {
            (_idle, _kernel, _user, _primed) = (idle, kernel, user, true);
            return 0;
        }
        var dIdle = idle - _idle;
        var total = (kernel - _kernel) + (user - _user); // kernel time includes idle
        (_idle, _kernel, _user) = (idle, kernel, user);
        return total <= 0 ? 0 : Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
    }

    public (double Percent, double UsedGb, double TotalGb) Memory()
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref status)) return (0, 0, 0);
        const double gb = 1024d * 1024 * 1024;
        var total = status.ullTotalPhys / gb;
        return (status.dwMemoryLoad, total - status.ullAvailPhys / gb, total);
    }

    public (double Percent, double FreeGb)? Disk()
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            const double gb = 1024d * 1024 * 1024;
            return ((drive.TotalSize - drive.TotalFreeSpace) * 100.0 / drive.TotalSize, drive.TotalFreeSpace / gb);
        }
        catch { return null; }
    }
}
