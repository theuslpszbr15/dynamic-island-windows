using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>Per-user install: %LOCALAPPDATA%\Programs\DynamicIsland, Start menu shortcut and an "Apps" entry. No admin needed.</summary>
public static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DynamicIsland");

    public static string InstalledExe => Path.Combine(InstallDir, "DynamicIsland.exe");

    static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Dynamic Island.lnk");

    public static bool IsInstalled => string.Equals(
        Path.GetDirectoryName(Environment.ProcessPath)?.TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies the running build into the install folder and registers it. Returns the installed exe.</summary>
    public static string Install()
    {
        var source = AppContext.BaseDirectory.TrimEnd('\\');
        Directory.CreateDirectory(InstallDir);
        StopInstalled();
        long bytes = 0;
        // A single-file build may sit in Downloads next to anything, so only the exe itself is copied.
        var singleFile = string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
        IEnumerable<string> files = singleFile
            ? [Environment.ProcessPath!]
            : Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(InstallDir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            bytes += new FileInfo(file).Length;
        }

        CreateShortcut(ShortcutPath, InstalledExe, "Dynamic Island para Windows");

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "Dynamic Island");
            key.SetValue("DisplayIcon", InstalledExe);
            key.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
            key.SetValue("Publisher", "theuslpszbr15");
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        StartupRegistration.Set(true, InstalledExe);
        return InstalledExe;
    }

    public const string QuitEventName = @"Local\DynamicIsland.Quit";

    // The installed exe is locked while it runs: ask it to quit, then make sure.
    static void StopInstalled()
    {
        var running = Process.GetProcessesByName("DynamicIsland").Where(p =>
        {
            try
            {
                return p.Id != Environment.ProcessId
                       && string.Equals(Path.GetDirectoryName(p.MainModule?.FileName), InstallDir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }).ToList();
        if (running.Count == 0) return;

        try { if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit)) using (quit) quit.Set(); } catch { }
        foreach (var process in running)
        {
            try { if (!process.WaitForExit(4000)) { process.Kill(); process.WaitForExit(3000); } }
            catch { }
        }
    }

    /// <summary>Removes registrations, closes the running island and deletes the install folder once this process exits.</summary>
    public static void Uninstall()
    {
        foreach (var process in Process.GetProcessesByName("DynamicIsland"))
        {
            if (process.Id == Environment.ProcessId) continue;
            try
            {
                if (!string.Equals(Path.GetDirectoryName(process.MainModule?.FileName), InstallDir, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill();
                process.WaitForExit(3000);
            }
            catch { }
        }

        StartupRegistration.Set(false);
        try { File.Delete(ShortcutPath); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        App.RemoveCopilotHooks();

        if (Directory.Exists(InstallDir))
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/d /c for /l %i in (1,1,15) do (ping -n 2 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\" 2>nul & if not exist \"{InstallDir}\" exit /b)")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
    }

    static void CreateShortcut(string path, string target, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
        link.SetDescription(description);
        link.SetIconLocation(target, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ((IPersistFile)link).Save(path, true);
        Marshal.FinalReleaseComObject(link);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int max, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relPath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
