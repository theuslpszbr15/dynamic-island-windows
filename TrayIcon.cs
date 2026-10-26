using System.Drawing;
using WinForms = System.Windows.Forms;

namespace DynamicIsland;

sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly Icon _image;

    public TrayIcon(App app)
    {
        var pill = new WinForms.ToolStripMenuItem("Pílula flutuante", null, (_, _) => app.SetShape(IslandShape.Pill));
        var notch = new WinForms.ToolStripMenuItem("Notch no topo", null, (_, _) => app.SetShape(IslandShape.Notch));
        var shape = new WinForms.ToolStripMenuItem("Formato");
        shape.DropDownItems.AddRange([pill, notch]);
        var discreet = new WinForms.ToolStripMenuItem("Recolher quando ocioso", null, (_, _) => app.ToggleDiscreet());
        var startup = new WinForms.ToolStripMenuItem("Iniciar com o Windows", null, (_, _) => app.ToggleStartup());
        var copilot = new WinForms.ToolStripMenuItem("Conectar Copilot (VS Code)", null, (_, _) => app.ToggleCopilotHooks());
        var hooks = new WinForms.ToolStripMenuItem("Copiar hooks do Claude Code", null, (_, _) => app.CopyClaudeHooks());
        var monitor = new WinForms.ToolStripMenuItem("Monitor");
        var notifications = new WinForms.ToolStripMenuItem("Mostrar notificações do Windows", null, (_, _) => app.ToggleNotifications());
        var install = new WinForms.ToolStripMenuItem("Instalar no computador", null, (_, _) =>
        {
            if (Services.Installer.IsInstalled) app.Uninstall();
            else app.Install();
        });
        var exit = new WinForms.ToolStripMenuItem("Sair", null, (_, _) => app.Quit());

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange([shape, discreet, monitor, notifications, startup, new WinForms.ToolStripSeparator(),
            copilot, hooks, new WinForms.ToolStripSeparator(), install, exit]);
        menu.Opening += (_, _) =>
        {
            copilot.Checked = App.CopilotConnected;
            discreet.Checked = app.Settings.DiscreetIdle;
            notifications.Checked = app.Settings.ShowNotifications;
            pill.Checked = app.Settings.Shape == IslandShape.Pill;
            notch.Checked = app.Settings.Shape == IslandShape.Notch;
            startup.Checked = StartupRegistration.IsEnabled;
            install.Text = Services.Installer.IsInstalled ? "Desinstalar" : "Instalar no computador";
            monitor.DropDownItems.Clear();
            foreach (var (id, label) in App.MonitorChoices())
                monitor.DropDownItems.Add(new WinForms.ToolStripMenuItem(label, null, (_, _) => app.SetMonitor(id))
                {
                    Checked = app.Settings.Monitor == id,
                });
        };

        _image = CreateIcon();
        _icon = new WinForms.NotifyIcon
        {
            Icon = _image,
            Text = "Dynamic Island",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void Balloon(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.Info);

    static Icon CreateIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("DynamicIsland.ico")!;
        return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
