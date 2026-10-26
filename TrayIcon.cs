using System.Drawing;
using System.Drawing.Drawing2D;
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
        var exit = new WinForms.ToolStripMenuItem("Sair", null, (_, _) => app.Quit());

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange([shape, discreet, startup, new WinForms.ToolStripSeparator(), copilot, hooks, new WinForms.ToolStripSeparator(), exit]);
        menu.Opening += (_, _) =>
        {
            copilot.Checked = App.CopilotConnected;
            discreet.Checked = app.Settings.DiscreetIdle;
            pill.Checked = app.Settings.Shape == IslandShape.Pill;
            notch.Checked = app.Settings.Shape == IslandShape.Notch;
            startup.Checked = StartupRegistration.IsEnabled;
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
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            var r = new RectangleF(2, 9, 28, 14);
            path.AddArc(r.X, r.Y, r.Height, r.Height, 90, 180);
            path.AddArc(r.Right - r.Height, r.Y, r.Height, r.Height, 270, 180);
            path.CloseFigure();
            g.FillPath(Brushes.Black, path);
            using var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 2f);
            g.DrawPath(pen, path);
            g.FillEllipse(new SolidBrush(Color.FromArgb(10, 132, 255)), 21, 13, 6, 6);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
