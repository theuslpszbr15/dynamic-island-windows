using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class DictationTests
{
    [Theory]
    [InlineData("  Comprar pão e leite.  ", "Comprar pão e leite.")]
    [InlineData("[BLANK_AUDIO] olá mundo", "olá mundo")]
    [InlineData("(música) bom dia", "bom dia")]
    public void KeepsWhatWasSaid(string heard, string pasted) => Assert.Equal(pasted, Dictation.Clean(heard));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Obrigado.")]
    [InlineData("Legendas pela comunidade Amara.org")]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("...")]
    public void DropsWhatWhisperInventsOverSilence(string? heard) => Assert.Null(Dictation.Clean(heard));

    /// <summary>DICTATION_E2E=1 (takes the keyboard focus for a second): the text lands in the focused text box through
    /// a real Ctrl+V and the clipboard gets its old content back.</summary>
    [Fact]
    public void PastesWhereYouTypeAndGivesTheClipboardBack()
    {
        if (Environment.GetEnvironmentVariable("DICTATION_E2E") != "1") return;
        string typed = "", clipboard = "", selected = "", clipboardAfterCopy = "", state = "";
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(ui));
            ui.InvokeAsync(async () =>
            {
                var box = new System.Windows.Controls.TextBox();
                var window = new System.Windows.Window { Width = 300, Height = 120, Topmost = true, Content = box };
                try
                {
                    window.Show();
                    var own = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    // Windows only hands the foreground to a process that just got input: an Alt tap counts
                    for (var i = 0; i < 10 && GetForegroundWindow() != own; i++)
                    {
                        keybd_event(0x12, 0, 0, UIntPtr.Zero);
                        keybd_event(0x12, 0, 2, UIntPtr.Zero);
                        SetForegroundWindow(own);
                        await Task.Delay(100);
                    }
                    if (GetForegroundWindow() != own) throw new InvalidOperationException("a janela de teste não recebeu o foco");
                    box.Focus();
                    System.Windows.Clipboard.SetText("o que já estava copiado");
                    await Task.Delay(300);
                    state = $"foreground={(GetForegroundWindow() == own ? "teste" : "outra")} modificadores={TextPaster.ModifiersDown()}";
                    await TextPaster.PasteAsync("Comprar pão, leite e café.");
                    await Task.Delay(200);
                    typed = box.Text;
                    clipboard = System.Windows.Clipboard.GetText();
                    // quick translation reads the selection the same way, and gives the clipboard back too
                    box.SelectAll();
                    selected = await TextPaster.CopySelectionAsync() ?? "";
                    clipboardAfterCopy = System.Windows.Clipboard.GetText();
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    window.Close();
                    ui.InvokeShutdown();
                }
            });
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
        Assert.True(typed == "Comprar pão, leite e café.", $"digitado: '{typed}' ({state})");
        Assert.Equal("o que já estava copiado", clipboard);
        Assert.True(selected == "Comprar pão, leite e café.", $"copiou: '{selected}' (digitado: '{typed}')");
        Assert.Equal("o que já estava copiado", clipboardAfterCopy);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}

public class LyricsTests
{
    static readonly IReadOnlyList<LyricLine> Lines = LyricsService.Parse("[00:10.00]primeira\n[00:20.50]segunda\n[00:30.00]");

    [Fact]
    public void FindsTheLineBeingSung()
    {
        Assert.Equal(-1, LyricsService.IndexAt(Lines, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, LyricsService.IndexAt(Lines, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, LyricsService.IndexAt(Lines, TimeSpan.FromSeconds(29)));
        Assert.Equal("", LyricsService.LineAt(Lines, TimeSpan.FromSeconds(31)));
        Assert.Null(LyricsService.LineAt(Lines, TimeSpan.Zero));
    }
}
