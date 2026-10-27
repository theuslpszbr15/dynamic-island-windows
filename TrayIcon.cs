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
        var theme = new WinForms.ToolStripMenuItem("Tema");
        var pet = new WinForms.ToolStripMenuItem("Mascote");
        var startup = new WinForms.ToolStripMenuItem("Iniciar com o Windows", null, (_, _) => app.ToggleStartup());
        var copilot = new WinForms.ToolStripMenuItem("Conectar Copilot (VS Code)", null, (_, _) => app.ToggleCopilotHooks());
        var hooks = new WinForms.ToolStripMenuItem("Copiar hooks do Claude Code", null, (_, _) => app.CopyClaudeHooks());
        var monitor = new WinForms.ToolStripMenuItem("Monitor");
        var notifications = new WinForms.ToolStripMenuItem("Notificações do Windows", null, (_, _) => app.ToggleNotifications());
        var lyrics = new WinForms.ToolStripMenuItem("Letra da música (LRCLIB)", null, (_, _) => app.ToggleLyrics());
        var clipboard = new WinForms.ToolStripMenuItem("O que foi copiado", null, (_, _) => app.ToggleClipboard());
        var lockKeys = new WinForms.ToolStripMenuItem("Caps Lock / Num Lock", null, (_, _) => app.ToggleLockKeys());
        var videos = new WinForms.ToolStripMenuItem("Vídeos (YouTube, Netflix…)", null, (_, _) => app.ToggleQuietVideos());
        var shows = new WinForms.ToolStripMenuItem("Mostrar na ilha");
        shows.DropDownItems.AddRange([notifications, lyrics, clipboard, lockKeys, videos]);
        var hide = new WinForms.ToolStripMenuItem("Esconder a ilha", null, (_, _) => app.ToggleHidden()) { ShortcutKeyDisplayString = "Ctrl+Alt+D" };
        var hideHour = new WinForms.ToolStripMenuItem("Esconder por 1 hora", null, (_, _) => app.HideFor(TimeSpan.FromHours(1)));
        var outfit = new WinForms.ToolStripMenuItem("Roupinha do mascote");
        var desktopPet = new WinForms.ToolStripMenuItem("Mascote na área de trabalho", null, (_, _) => app.ToggleDesktopPet());
        var recap = new WinForms.ToolStripMenuItem("Resumo da semana", null, (_, _) => app.ShowRecap());
        var update = new WinForms.ToolStripMenuItem("Procurar atualizações", null, (_, _) =>
        {
            if (app.Update != null) app.InstallUpdate();
            else app.CheckForUpdates(true);
        });
        var install = new WinForms.ToolStripMenuItem("Instalar no computador", null, (_, _) =>
        {
            if (Services.Installer.IsInstalled) app.Uninstall();
            else app.Install();
        });
        var exit = new WinForms.ToolStripMenuItem("Sair", null, (_, _) => app.Quit());

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange([hide, hideHour, new WinForms.ToolStripSeparator(), shape, theme, pet, outfit, desktopPet, discreet, monitor, shows, recap, startup, new WinForms.ToolStripSeparator(),
            copilot, hooks, new WinForms.ToolStripSeparator(), update, install, exit]);
        menu.Opening += (_, _) =>
        {
            hide.Checked = app.IslandHidden;
            videos.Checked = !app.Settings.QuietVideos;
            copilot.Checked = App.CopilotConnected;
            discreet.Checked = app.Settings.DiscreetIdle;
            lyrics.Checked = app.Settings.ShowLyrics;
            clipboard.Checked = app.Settings.ShowClipboard;
            lockKeys.Checked = app.Settings.ShowLockKeys;
            desktopPet.Checked = app.Settings.DesktopPet;
            update.Text = app.Update is { } u ? $"Atualizar para {u.Version.ToString(3)}" : "Procurar atualizações";
            outfit.DropDownItems.Clear();
            foreach (var (o, label) in App.Outfits)
                outfit.DropDownItems.Add(new WinForms.ToolStripMenuItem(label, null, (_, _) => app.SetOutfit(o))
                {
                    Checked = app.Settings.Outfit == o,
                });
            pet.DropDownItems.Clear();
            foreach (var (species, label) in App.Mascots)
                pet.DropDownItems.Add(new WinForms.ToolStripMenuItem(label, null, (_, _) => app.SetMascot(species))
                {
                    Checked = app.Settings.Mascot == species,
                });
            theme.DropDownItems.Clear();
            foreach (var (name, _) in IslandThemes.All)
                theme.DropDownItems.Add(new WinForms.ToolStripMenuItem(name, null, (_, _) => app.SetTheme(name))
                {
                    Checked = app.Settings.Theme == name,
                });
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
        // left click: hide / show the island
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) app.ToggleHidden();
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
