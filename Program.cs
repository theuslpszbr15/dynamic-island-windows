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

        using var mutex = new Mutex(true, @"Local\DynamicIsland.SingleInstance", out var isFirst);
        if (!isFirst) return 0;

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
