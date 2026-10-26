using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DynamicIsland;

public partial class App : Application
{
    public IslandSettings Settings { get; private set; } = null!;
    IslandWindow _window = null!;
    TrayIcon _tray = null!;

    public string? SnapshotFolder { get; init; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        // Layered windows are read back to system memory anyway; software rendering avoids loading
        // the GPU driver's D3D9 stack (hundreds of MB on some Intel drivers) for a tiny overlay.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        if (SnapshotFolder != null)
        {
            Settings = new IslandSettings { ReadOnly = true, DiscreetIdle = false };
            _window = new IslandWindow(Settings) { SnapshotMode = true };
            _window.Show();
            await _window.RunSnapshotsAsync(SnapshotFolder);
            Shutdown();
            return;
        }

        Settings = IslandSettings.Load();
        _window = new IslandWindow(Settings);
        _window.Show();
        _tray = new TrayIcon(this);
    }

    public void SetShape(IslandShape shape)
    {
        Settings.Shape = shape;
        Settings.Save();
        _window.ApplyShape();
    }

    public void ToggleDiscreet()
    {
        Settings.DiscreetIdle = !Settings.DiscreetIdle;
        Settings.Save();
        _window.ApplyShape();
    }

    public void ToggleStartup()
    {
        try { StartupRegistration.Set(!StartupRegistration.IsEnabled); } catch { }
    }

    public void CopyClaudeHooks()
    {
        var exe = (Environment.ProcessPath ?? "DynamicIsland.exe").Replace('\\', '/');
        var command = $"\"{exe}\" hook claude";

        JsonArray Entry(string? matcher)
        {
            var entry = new JsonObject();
            if (matcher != null) entry["matcher"] = matcher;
            entry["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command });
            return new JsonArray(entry);
        }

        var hooks = new JsonObject
        {
            ["UserPromptSubmit"] = Entry(null),
            ["PreToolUse"] = Entry("*"),
            ["PostToolUse"] = Entry("*"),
            ["Notification"] = Entry(null),
            ["Stop"] = Entry(null),
            ["SessionEnd"] = Entry(null),
        };
        var json = new JsonObject { ["hooks"] = hooks }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(json); break; }
            catch { Thread.Sleep(50); }
        }
        _tray.Balloon("Hooks copiados", "Mescle o conteúdo em ~/.claude/settings.json (chave \"hooks\").");
    }

    static string CopilotHookFile => System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } home
            ? home
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot"),
        "hooks", "dynamic-island.json");

    public static bool CopilotConnected => File.Exists(CopilotHookFile);

    /// <summary>Installs (or removes) a user-level hook file read by Copilot in VS Code and Copilot CLI.</summary>
    public void ToggleCopilotHooks()
    {
        try
        {
            if (CopilotConnected)
            {
                File.Delete(CopilotHookFile);
                _tray.Balloon("Copilot desconectado", "Os hooks da ilha foram removidos.");
                return;
            }

            // exec runs without a shell, so the JSON payload reaches stdin intact.
            // No PreToolUse: a failing preToolUse command hook would deny the agent's tools.
            JsonArray Entry() => new(new JsonObject
            {
                ["type"] = "command",
                ["exec"] = Environment.ProcessPath,
                ["args"] = new JsonArray("hook", "copilot"),
                ["timeoutSec"] = 5,
            });
            var config = new JsonObject
            {
                ["version"] = 1,
                ["hooks"] = new JsonObject
                {
                    ["UserPromptSubmit"] = Entry(),
                    ["PostToolUse"] = Entry(),
                    ["Notification"] = Entry(),
                    ["Stop"] = Entry(),
                    ["SessionEnd"] = Entry(),
                },
            };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CopilotHookFile)!);
            File.WriteAllText(CopilotHookFile, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _tray.Balloon("Copilot conectado", "Abra um novo chat no VS Code para a ilha começar a mostrar o Copilot.");
        }
        catch (Exception ex)
        {
            _tray.Balloon("Não foi possível alterar os hooks", ex.Message);
        }
    }

    public ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action click, bool? check = null)
        {
            var item = new MenuItem { Header = header };
            if (check is bool c) { item.IsCheckable = true; item.IsChecked = c; }
            item.Click += (_, _) => click();
            return item;
        }

        menu.Items.Add(Item("Pílula flutuante", () => SetShape(IslandShape.Pill), Settings.Shape == IslandShape.Pill));
        menu.Items.Add(Item("Notch no topo", () => SetShape(IslandShape.Notch), Settings.Shape == IslandShape.Notch));
        menu.Items.Add(Item("Recolher quando ocioso", ToggleDiscreet, Settings.DiscreetIdle));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Iniciar com o Windows", ToggleStartup, StartupRegistration.IsEnabled));
        menu.Items.Add(Item("Conectar Copilot (VS Code)", ToggleCopilotHooks, CopilotConnected));
        menu.Items.Add(Item("Copiar hooks do Claude Code", CopyClaudeHooks));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Sair", Quit));
        return menu;
    }

    public void Quit()
    {
        _tray.Dispose();
        _window.Teardown();
        Settings.Save();
        Shutdown();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(IslandSettings.Folder);
            File.AppendAllText(System.IO.Path.Combine(IslandSettings.Folder, "error.log"),
                $"[{DateTime.Now:O}] {e.Exception}{Environment.NewLine}");
        }
        catch { }
        e.Handled = true;
    }
}
