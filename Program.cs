using DynamicIsland.Services;

namespace DynamicIsland;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Hook/CLI mode: forward an agent event to the running island and exit without UI.
        if (args.Length > 0 && args[0] is "hook" or "agent")
            return AgentCli.Run(args);

        if (args.Length > 1 && args[0] == "--snapshot")
        {
            var devApp = new App { SnapshotFolder = args[1] };
            devApp.InitializeComponent();
            return devApp.Run();
        }

        if (args.Length > 0 && args[0] == "--uninstall")
        {
            Installer.Uninstall();
            return 0;
        }

        if (args.Length > 0 && args[0] == "--install")
        {
            var exe = Installer.Install();
            App.RefreshCopilotHooks(exe);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--replace") { UseShellExecute = true });
            return 0;
        }

        // Started from a downloaded release by the running island, which then quits: wait for it, install over it.
        if (args.Length > 0 && args[0] == "--update")
        {
            using (var old = new Mutex(false, @"Local\DynamicIsland.SingleInstance"))
            {
                try { if (old.WaitOne(TimeSpan.FromSeconds(20))) old.ReleaseMutex(); }
                catch (AbandonedMutexException) { }
            }
            var exe = Installer.Install();
            App.RefreshCopilotHooks(exe);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--replace") { UseShellExecute = true });
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\DynamicIsland.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            // Relaunched by the installer: wait for the old copy to exit.
            if (!args.Contains("--replace")) return 0;
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) return 0; }
            catch (AbandonedMutexException) { }
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
