using System.Diagnostics;

namespace DynamicIsland.Services;

/// <summary>
/// Leftovers that only take disk space: the native DLLs a self-contained single-file exe unpacks to %TEMP%\.net
/// (a new copy for every build, never removed by .NET), and the relay's node_modules (~200 MB) once
/// "Criar meu repasse" is over.
/// </summary>
static class DiskCleanup
{
    public static void Run()
    {
        try { OldExtractions(); } catch (Exception ex) { Log.Error("DiskCleanup extractions", ex); }
        try { RelayModules(); } catch (Exception ex) { Log.Error("DiskCleanup relay", ex); }
    }

    static void OldExtractions()
    {
        var root = Path.Combine(Path.GetTempPath(), ".net", "DynamicIsland");
        if (!Directory.Exists(root)) return;
        // The folder this process loaded its DLLs from (none when running on the installed .NET runtime).
        using var me = Process.GetCurrentProcess();
        var inUse = me.Modules.Cast<ProcessModule>()
            .Select(m => m.FileName)
            .Where(f => f.Contains(@"\.net\DynamicIsland\", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(Path.GetDirectoryName(f)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(root))
            if (!inUse.Contains(Path.GetFileName(dir)))
                try { Directory.Delete(dir, true); } catch { } // another build still running keeps its own
    }

    static void RelayModules()
    {
        // Older than a couple of hours: a setup still running in its PowerShell window is left alone.
        foreach (var name in new[] { "node_modules", ".wrangler" })
        {
            var dir = Path.Combine(RelaySetup.Folder, name);
            if (Directory.Exists(dir) && Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-2))
                Directory.Delete(dir, true);
        }
    }
}
