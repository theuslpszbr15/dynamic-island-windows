using System.Windows;
using DynamicIsland.Services;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace DynamicIsland;

// Text from the screen: Ctrl+Alt+O, drag over it, and it is copied (Windows' own OCR, offline); a click on the card
// asks Jarvis to explain it. Jarvis also reads the window you were looking at when asked about "this error".
public partial class IslandWindow
{
    IntPtr _jarvisTarget;
    bool _snipping;

    async void CopyTextFromScreen()
    {
        if (_snipping) return;
        _snipping = true;
        try
        {
            Native.GetCursorPos(out var cursor);
            var screen = WinForms.Screen.FromPoint(new Drawing.Point(cursor.X, cursor.Y)).Bounds;
            var snip = new SnipWindow(screen);
            snip.ShowDialog();
            using var picked = snip.Result;
            if (picked == null) return;
            var text = await ScreenOcr.ReadAsync(picked);
            if (text == null)
            {
                ShowInfo("\uE8C8", "O Windows est\u00e1 sem o idioma de OCR", "Configurar", glyphColor: Meter.Red, seconds: 6,
                    action: () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }));
                return;
            }
            if (text.Length == 0)
            {
                ShowInfo("\uE8C8", "N\u00e3o achei texto a\u00ed", "", glyphColor: JarvisColor, seconds: 2.5);
                return;
            }
            try { Clipboard.SetText(text); } catch { }
            ShowCard(null, "Texto copiado da tela", "OCR", "Clique para o Jarvis explicar: " + Preview(text), seconds: 6, glyph: "\uE8C8");
            if (JarvisBrain.HasKey || Jarvis.Folder(Jarvis.Engine.Ai) != null)
                _notifyAction = () =>
                {
                    ShowJarvisPage();
                    _ = AskJarvisAsync("Explique de forma simples o que \u00e9 isto que est\u00e1 na minha tela:\n" + text);
                };
        }
        finally { _snipping = false; }
    }

    /// <summary>Remembers the window in front, so \"what is this error?\" reads that one and not the Jarvis bar.</summary>
    void RememberJarvisTarget()
    {
        var front = Native.GetForegroundWindow();
        if (front != IntPtr.Zero && front != _hwnd && (_jarvisWindow == null || front != new System.Windows.Interop.WindowInteropHelper(_jarvisWindow).Handle))
            _jarvisTarget = front;
    }

    async Task<string?> IJarvisHost.ReadScreenAsync()
    {
        if (Native.IsInputDesktopLocked()) return "(a tela do PC está bloqueada; não há nada para ler até o usuário desbloquear)";
        var target = _jarvisTarget != IntPtr.Zero && Native.IsWindowVisibleNow(_jarvisTarget) ? _jarvisTarget : Native.GetForegroundWindow();
        if (target == IntPtr.Zero || !Native.GetWindowRect(target, out var r)) return null;
        // a maximized window reaches past the screen edge; only what is on screen counts
        var screen = WinForms.Screen.FromHandle(target).Bounds;
        var area = Drawing.Rectangle.Intersect(screen, Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
        if (area.Width < 20 || area.Height < 20) return null;
        using var shot = ScreenOcr.Capture(area);
        return await ScreenOcr.ReadAsync(shot);
    }
}
