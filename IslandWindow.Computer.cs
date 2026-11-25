using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Services;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace DynamicIsland;

// Computer use: Jarvis looks at the screen (screenshot + Windows OCR), a vision model picks one action at a time, and the
// island does it with the mouse and keyboard. The safeguards: off until turned on in the settings; the person approves
// each action (or only the dangerous ones); Esc stops everything at any moment; the island shows "Jarvis no controle";
// it never types into a password field; at most 20 steps and 5 minutes.
public partial class IslandWindow
{
    CancellationTokenSource? _computer;
    ComputerAskWindow? _computerAsk;
    string? _computerStop;

    public bool ComputerBusy => _computer != null;

    // what the Jarvis page lists while Jarvis works on the screen: the steps done and the one under way
    readonly List<string> _computerSteps = [];
    string? _computerNow;
    bool _computerClicking;

    /// <summary>Shows what Jarvis is doing now: on the open Jarvis page, else in the pill.</summary>
    void ComputerShow(string? now, int step)
    {
        _computerNow = now;
        if (JarvisOnScreen) UpdateJarvisUi();
        else ComputerPill($"Jarvis no controle · passo {step}");
    }

    void RenderComputerSteps()
    {
        JarvisSteps.Children.Clear();
        var shown = _computerSteps.TakeLast(_computerNow == null ? 5 : 4).ToList();
        if (_computerSteps.Count > shown.Count)
            JarvisSteps.Children.Add(StepLine("…", $"{_computerSteps.Count - shown.Count} passo(s) antes", false));
        foreach (var text in shown) JarvisSteps.Children.Add(StepLine("✓", text, false));
        if (_computerNow != null) JarvisSteps.Children.Add(StepLine("●", _computerNow, true));
    }

    TextBlock StepLine(string mark, string text, bool current)
    {
        var line = new TextBlock { FontSize = current ? 14 : 13, Margin = new Thickness(0, 0, 0, 5), TextTrimming = TextTrimming.CharacterEllipsis };
        line.Inlines.Add(new System.Windows.Documents.Run(mark + "  ")
        {
            Foreground = new SolidColorBrush(current ? JarvisColor : mark == "✓" ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0x8E, 0x8E, 0x93)),
        });
        line.Inlines.Add(new System.Windows.Documents.Run(text));
        if (current) line.FontWeight = FontWeights.SemiBold;
        else line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return line;
    }

    void JarvisStop_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        StopJarvis();
    }

    /// <summary>"Parar": the screen work, the request still thinking and the voice, all at once.</summary>
    public void StopJarvis()
    {
        StopComputer();
        if (JarvisBrain.HasKey) Brain.Stop();
        Jarvis.StopSpeaking();
    }

    /// <summary>--try-computer: each step's picture, answer and approval card go here, and the card is allowed by itself.</summary>
    public string? ComputerTestFolder { get; init; }

    void TestLog(int step, string line, byte[]? jpeg = null)
    {
        if (ComputerTestFolder == null) return;
        File.AppendAllText(Path.Combine(ComputerTestFolder, "steps.txt"), $"[{step}] {line}\n");
        if (jpeg != null) File.WriteAllBytes(Path.Combine(ComputerTestFolder, $"step{step:00}.jpg"), jpeg);
    }

    /// <summary>Stops Jarvis's computer use now (the pill's "Parar").</summary>
    public void StopComputer(string why = "O usuário parou.")
    {
        if (_computer == null) return;
        _computerStop ??= why;
        _computer.Cancel();
        _computerAsk?.Finish(ComputerAnswer.Stop);
        // the model doesn't get to start another round on its own
        if (JarvisBrain.HasKey) Brain.Stop();
        _computerNow = "Parando…";
        if (JarvisOnScreen) UpdateJarvisUi();
    }

    async Task<string> IJarvisHost.UseComputerAsync(string task)
    {
        if (!_settings.ComputerUse)
            return "O controle do computador está desligado. Diga ao usuário que ele pode ligar em Configurações › Jarvis › Controle do computador.";
        if (_computer != null) return "Já estou usando o computador em outra tarefa.";
        if (!JarvisBrain.HasKey) return "O controle do computador precisa da chave da Groq.";
        if (Native.IsInputDesktopLocked()) return "A tela do PC está bloqueada: o usuário precisa desbloquear antes.";

        using var cts = new CancellationTokenSource(ComputerUse.MaxTime);
        _computer = cts;
        _computerStop = null;
        var done = new List<string>();
        var askAll = _settings.ComputerUseApproval != "perigosas";
        var allowRest = false;
        byte[]? lastPrint = null;
        var stuck = 0;
        var esc = WatchEscAsync(cts);
        string Stopped() => _computerStop ?? $"Parei no limite de {ComputerUse.MaxTime.TotalMinutes:0} minutos.";
        string Summary() => done.Count == 0 ? "" : " Feito: " + string.Join("; ", done) + ".";
        _computerSteps.Clear();
        _computerNow = "Começando…";
        // Jarvis's screenshots see what is under the island, not the island
        if (_hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        ShowJarvisPage();
        Retarget();
        UpdateJarvisUi();
        try
        {
            // the window the task names, else the one the person was in when they asked (not the Jarvis box)
            if (ComputerUse.NamedWindow(task, Native.TitledWindows().Where(w => w.Hwnd != _hwnd)) is { } named)
            {
                if (!Native.BringForward(named))
                    return "Não consegui trazer a janela pedida para a frente. Peça ao usuário para abrir ou clicar nela e tente de novo.";
            }
            else if (_jarvisTarget != IntPtr.Zero && Native.IsWindowVisibleNow(_jarvisTarget)) Native.BringForward(_jarvisTarget);
            await Task.Delay(400);
            for (var step = 1; step <= ComputerUse.MaxSteps; step++)
            {
                if (cts.IsCancellationRequested) return Stopped() + Summary();
                if (Native.IsInputDesktopLocked()) return "Parei: a tela do PC foi bloqueada." + Summary();
                ComputerShow("Olhando a tela…", step);

                var front = Native.GetForegroundWindow();
                var screen = WinForms.Screen.FromHandle(front != IntPtr.Zero ? front : _hwnd).Bounds;
                using var shot = ScreenOcr.Capture(screen);
                var print = ComputerUse.Thumbprint(shot);
                if (lastPrint != null && done.Count > 0 && ComputerUse.Unchanged(lastPrint, print))
                {
                    if (++stuck >= 3) return "Não consegui: a tela não muda com o que eu faço, então parei para não ficar repetindo." + Summary();
                    done[^1] += " (a tela não mudou)";
                }
                else stuck = 0;
                lastPrint = print;
                var lines = await ScreenOcr.ReadLinesAsync(shot) ?? [];
                var items = await ComputerUse.ItemsAsync(front, screen);
                var scale = Math.Min(1.0, (double)ComputerUse.ImageWidth / shot.Width);
                byte[] jpeg;
                using (var marked = new Drawing.Bitmap(shot))
                {
                    ComputerUse.Mark(marked, items, screen);
                    jpeg = ComputerUse.Jpeg(marked, scale);
                }
                var json = await JarvisBrain.VisionJsonAsync(ComputerUse.SystemPrompt,
                    ComputerUse.StepText(task, step, done, lines, scale, (int)(shot.Width * scale), (int)(shot.Height * scale),
                        ComputerUse.ItemsText(items, screen, scale)),
                    jpeg, cts.Token);
                TestLog(step, "modelo: " + (json?.ToJsonString() ?? "(nada)"), jpeg);
                if (cts.IsCancellationRequested) return Stopped() + Summary();
                if (json == null)
                    return (JarvisBrain.VisionLimitWait is { } wait
                        ? $"Parei: o limite grátis da Groq acabou por agora; volta em uns {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))} min."
                        : "Não consegui ver a tela (sem internet?).") + Summary();

                var action = ComputerUse.Parse(json);
                if (action == null)
                {
                    done.Add("(resposta fora do formato; responda com uma das ações)");
                    continue;
                }
                if (action.Kind == "concluir") return "Concluído: " + (action.Reason.Length > 0 ? action.Reason : "tarefa feita.") + Summary();
                if (action.Kind == "desistir") return "Não consegui: " + (action.Reason.Length > 0 ? action.Reason : "sem motivo.") + Summary();

                // where on the screen (physical pixels), and the text there
                Drawing.Point? point = null;
                string? near = null;
                if (action.Kind == "clicar_item")
                {
                    if (items.FirstOrDefault(i => i.Id == action.Item) is not { } item)
                    {
                        done.Add($"não existe o item {action.Item}");
                        continue;
                    }
                    point = new Drawing.Point(item.Box.X + item.Box.Width / 2, item.Box.Y + item.Box.Height / 2);
                    near = item.Name.Length > 0 ? item.Name : item.Kind;
                }
                else if (action.IsClick || action is { Kind: "rolar", X: >= 0, Y: >= 0 })
                {
                    Drawing.PointF spot;
                    if (action.Kind == "clicar_texto" || (action.X < 0 && action.Text.Length > 0))
                    {
                        if (ComputerUse.Locate(lines, action.Text) is not { } hit)
                        {
                            done.Add($"não achei “{action.Text}” na tela");
                            continue;
                        }
                        (spot, near) = hit;
                    }
                    else
                    {
                        spot = new Drawing.PointF((float)(action.X / scale), (float)(action.Y / scale));
                        near = ComputerUse.Near(lines, spot);
                    }
                    point = new Drawing.Point(
                        screen.Left + Math.Clamp((int)spot.X, 0, screen.Width - 1),
                        screen.Top + Math.Clamp((int)spot.Y, 0, screen.Height - 1));
                }

                if (action.Kind == "digitar")
                {
                    // the label next to the text cursor's field is unknown; what the model says and the focused control are not
                    if (await ComputerUse.FocusIsPasswordAsync() || ComputerUse.LooksSecret(action, near))
                        return "Parei: o campo é de senha ou código, e isso eu nunca digito. O usuário digita e depois pode me pedir para continuar." + Summary();
                }

                var what = ComputerUse.Describe(action, near);
                var risky = ComputerUse.IsRisky(action, near);
                var asking = risky || (askAll && !allowRest);
                ComputerShow(asking ? "Esperando sua permissão: " + what : what, step);
                if (asking)
                {
                    _computerAsk = new ComputerAskWindow(what + (action.Reason.Length > 0 ? $"\n{action.Reason}" : ""), risky, offerRest: !risky && askAll, screen, point);
                    _computerAsk.Show();
                    if (ComputerTestFolder != null)
                    {
                        await Task.Delay(1200);
                        using (var card = ScreenOcr.Capture(screen))
                            card.Save(Path.Combine(ComputerTestFolder, $"step{step:00}-aprovar.png"));
                        TestLog(step, "aprovação: " + what + (risky ? " (importante)" : ""));
                        _computerAsk.Finish(ComputerAnswer.Allow);
                    }
                    var answer = await _computerAsk.Answer;
                    _computerAsk = null;
                    if (answer == ComputerAnswer.Stop || cts.IsCancellationRequested)
                    {
                        _computerStop ??= "O usuário não permitiu: " + what + ".";
                        return Stopped() + Summary();
                    }
                    if (answer == ComputerAnswer.AllowRest) allowRest = true;
                    // the card never takes the focus, but a click on it can bring our process forward
                    if (front != IntPtr.Zero && Native.GetForegroundWindow() != front) Native.SetForegroundWindow(front);
                    await Task.Delay(120);
                }

                if (cts.IsCancellationRequested) return Stopped() + Summary();
                ComputerShow(what, step);
                // the island may sit right where Jarvis clicks: let the click go through it
                _computerClicking = true;
                SetClickThrough(true);
                try
                {
                    await PerformAsync(action, point, screen);
                    await Task.Delay(250);
                }
                finally { _computerClicking = false; }
                TestLog(step, "feito: " + what + (point is { } at ? $" @ {at.X},{at.Y}" : ""));
                done.Add(what);
                _computerSteps.Add(what);
                ComputerShow(null, step);
                try { await Task.Delay(action.Kind == "esperar" ? 2500 : 900, cts.Token); }
                catch (OperationCanceledException) { }
            }
            return $"Parei no limite de {ComputerUse.MaxSteps} passos." + Summary();
        }
        finally
        {
            _computerAsk?.Finish(ComputerAnswer.Stop);
            _computerAsk = null;
            cts.Cancel();
            await esc;
            _computer = null;
            _computerNow = null;
            if (_hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(_hwnd, Glass ? Native.WDA_EXCLUDEFROMCAPTURE : 0);
            UpdateJarvisUi();
            Retarget();
            if (!JarvisOnScreen) ShowInfo("\uE7F4", "Jarvis devolveu o controle", "", glyphColor: JarvisColor, seconds: 2.5);
        }
    }

    void ComputerPill(string text) =>
        ShowInfo("\uE7F4", text, "Esc · Parar", glyphColor: JarvisColor, seconds: 120, action: StopJarvis);

    /// <summary>Esc, pressed anywhere, stops at once (polled: it works whatever app has the focus).</summary>
    async Task WatchEscAsync(CancellationTokenSource cts)
    {
        Native.WasKeyPressed(0x1B); // forget an Esc from before
        while (!cts.IsCancellationRequested)
        {
            if (Native.WasKeyPressed(0x1B))
            {
                StopComputer("O usuário apertou Esc e parou.");
                return;
            }
            try { await Task.Delay(40, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    static async Task PerformAsync(ComputerAction action, Drawing.Point? point, Drawing.Rectangle screen)
    {
        switch (action.Kind)
        {
            case "clicar" or "clicar_texto" or "clicar_item" when point is { } p:
                Native.Click(p.X, p.Y);
                break;
            case "clique_duplo" when point is { } p:
                Native.Click(p.X, p.Y, twice: true);
                break;
            case "clique_direito" when point is { } p:
                Native.Click(p.X, p.Y, right: true);
                break;
            case "digitar":
                await TextPaster.PasteAsync(action.Text);
                break;
            case "tecla":
                Native.PressKeys(ComputerUse.Keys[action.Key]);
                break;
            case "rolar":
                var at = point ?? new Drawing.Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
                Native.SetCursorPos(at.X, at.Y);
                Native.Wheel(action.Key == "cima" ? 600 : -600);
                break;
        }
    }
}
