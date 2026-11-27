using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// JARVIS on the island: Ctrl+Alt+J opens its page; held = talk (released = ask), tapped = the bar to type. With a Groq key
// the Project.Jarvis brain answers and acts through its tools; without one, the local engines answer.
public partial class IslandWindow : IJarvisHost
{
    static readonly Color JarvisColor = Color.FromRgb(0x83, 0xD4, 0xB4);
    const int JarvisHistoryMax = 60;
    static readonly TimeSpan TapLimit = TimeSpan.FromMilliseconds(400), ListenLimit = TimeSpan.FromSeconds(30);

    /// <summary>The conversation, oldest first (true = said by the person).</summary>
    public List<(bool User, string Text)> JarvisHistory { get; } = [];
    public Jarvis.Reply? JarvisLast { get; private set; }
    /// <summary>"Pronto", "Ouvindo…", "Transcrevendo…", "Pensando…" or "Falando…".</summary>
    public string JarvisState { get; private set; } = "Pronto";
    public bool JarvisListening => _mic != null;
    /// <summary>Raised on the UI thread when the conversation or the state changes.</summary>
    public event Action? JarvisChanged;

    JarvisMic? _mic;
    Task<string?>? _micStart;
    DispatcherTimer? _holdTimer, _listenLimit;
    JarvisWindow? _jarvisWindow;
    readonly VoiceGlow _glow = new();
    JarvisBrain? _brain;
    public JarvisBrain Brain => _brain ??= new JarvisBrain(this, () => _settings.JarvisUserName);

    /// <summary>Speech goes to Groq's Whisper with a key, else to the local microphone engine.</summary>
    public static bool CanListen => JarvisBrain.HasKey || Jarvis.Folder(Jarvis.Engine.Microphone) != null;

    // ───────────────────────── what the brain's tools do on the island ─────────────────────────

    void IJarvisHost.Remind(double minutes, string text)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ShowCard(null, "Lembrete", "Jarvis", text, seconds: 12, glyph: "\uEA8F");
            if (_settings.JarvisSpeak && Jarvis.CanSpeak) _ = Jarvis.SpeakAsync("Lembrete: " + text);
        };
        timer.Start();
    }

    void IJarvisHost.StartTimer(int minutes) => StartTimer(minutes);

    void IJarvisHost.AddTask(string text)
    {
        _settings.Tasks.Add(new TaskItem(Guid.NewGuid().ToString("N")[..8], text.Trim(), false));
        _settings.Save();
        UpdateTasksUi();
    }

    void IJarvisHost.Media(string action)
    {
        switch (action)
        {
            case "proxima": _media.Next(); break;
            case "anterior": _media.Previous(); break;
            case "tocar": _ = _media.PlayAsync(true, TimeSpan.FromSeconds(12)); break;
            case "pausar": _ = _media.PlayAsync(false, TimeSpan.Zero); break;
            default: _media.PlayPause(); break;
        }
    }

    void IJarvisHost.RemoveTask(string text)
    {
        var trimmed = text.Trim();
        var at = _settings.Tasks.FindLastIndex(t => t.Text == trimmed);
        if (at < 0) return;
        _settings.Tasks.RemoveAt(at);
        _settings.Save();
        UpdateTasksUi();
    }

    async Task<string> IJarvisHost.FocusWindowAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Diga qual janela.";
        var own = Environment.ProcessId;
        var windows = Native.TitledWindows().Where(w => Native.ProcessOf(w.Hwnd) != own).ToList();
        var compare = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        const System.Globalization.CompareOptions Loose = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
        var found = windows.FirstOrDefault(w => compare.IndexOf(w.Title, name, Loose) >= 0);
        // "bloco de notas" → notepad.exe, whatever the window is called
        if (found.Hwnd == IntPtr.Zero && Jarvis.FindApp(name) is { } app && Path.GetFileNameWithoutExtension(app.Target) is { Length: > 0 } exe)
            found = windows.FirstOrDefault(w =>
            {
                try { return Process.GetProcessById(Native.ProcessOf(w.Hwnd)).ProcessName.Equals(exe, StringComparison.OrdinalIgnoreCase); }
                catch (ArgumentException) { return false; }
            });
        if (found.Hwnd == IntPtr.Zero) return $"Não há nenhuma janela aberta de \"{name}\". Dá para abrir com abrir_app.";
        _jarvisWindow?.Close();
        await Task.Delay(80);
        var front = Native.BringForward(found.Hwnd);
        _jarvisTarget = found.Hwnd;
        return front ? $"Trouxe \"{found.Title}\" para frente." : $"O Windows não deixou trazer \"{found.Title}\" para frente; ela está piscando na barra de tarefas.";
    }

    static readonly Dictionary<string, (byte Modifier, byte Key, string Name)> JarvisShortcuts = new()
    {
        ["desfazer"] = (0x11, 0x5A, "Ctrl+Z"), ["refazer"] = (0x11, 0x59, "Ctrl+Y"), ["salvar"] = (0x11, 0x53, "Ctrl+S"),
        ["selecionar_tudo"] = (0x11, 0x41, "Ctrl+A"), ["copiar"] = (0x11, 0x43, "Ctrl+C"), ["colar"] = (0x11, 0x56, "Ctrl+V"),
        ["localizar"] = (0x11, 0x46, "Ctrl+F"), ["nova_aba"] = (0x11, 0x54, "Ctrl+T"), ["fechar_aba"] = (0x11, 0x57, "Ctrl+W"),
        ["atualizar"] = (0, 0x74, "F5"),
    };

    async Task<string> IJarvisHost.ShortcutAsync(string action)
    {
        if (!JarvisShortcuts.TryGetValue(action, out var shortcut)) return "Atalho desconhecido.";
        var target = _jarvisTarget;
        if (target == IntPtr.Zero || !Native.IsWindowVisibleNow(target)) return "Não sei em qual janela: use trazer_janela antes.";
        var title = Native.TitledWindows().FirstOrDefault(w => w.Hwnd == target).Title ?? "a janela";
        _jarvisWindow?.Close();
        await Task.Delay(80);
        if (!Native.BringForward(target)) return $"O Windows não deixou voltar para \"{title}\", então não apertei nada.";
        await Task.Delay(120);
        if (shortcut.Modifier == 0) RemoteInput.Key("f5");
        else RemoteInput.Combo(shortcut.Modifier, shortcut.Key);
        return $"Apertei {shortcut.Name} em \"{title}\".";
    }

    Task<bool> IJarvisHost.ConfirmScriptAsync(string description, string code) =>
        Task.FromResult(DarkDialog.Ask(null, "O Jarvis quer rodar um script",
            (description.Length > 0 ? description : "Sem descrição.") + "\n\nLeia o código inteiro antes de permitir: ele roda com as suas permissões.",
            "Permitir", "Não", danger: true, code: code));

    void ApplyGlowLook() => _glow.Apply(_settings.JarvisGlow, IslandThemes.IsLight(_settings.Theme));

    void SetJarvisState(string state)
    {
        JarvisState = state;
        // the glow follows the mic while listening and Jarvis's voice while speaking; thinking sweeps a beam
        _glow.Listening = state is "Ouvindo…" or "Falando…";
        _glow.Processing = state is "Transcrevendo…" or "Pensando…";
        UpdateJarvisUi();
        FollowVoice(state == "Falando…");
        JarvisChanged?.Invoke();
    }

    bool _following;
    double _scrollAt;

    // A long answer scrolls down as the voice reads it, a little ahead of the spoken line.
    void FollowVoice(bool on)
    {
        if (on == _following) return;
        _following = on;
        if (on)
        {
            _scrollAt = 0;
            JarvisScroll.ScrollToVerticalOffset(0);
            System.Windows.Media.CompositionTarget.Rendering += FollowFrame;
        }
        else System.Windows.Media.CompositionTarget.Rendering -= FollowFrame;
        UpdateAnswerFade();
    }

    void FollowFrame(object? sender, EventArgs e)
    {
        if (!JarvisOnScreen) return;
        var room = JarvisScroll.ScrollableHeight;
        UpdateAnswerFade();
        if (room <= 0 || Jarvis.SpeechProgress is not { } progress) return;
        var target = Math.Clamp(progress * JarvisScroll.ExtentHeight - JarvisScroll.ViewportHeight * 0.45, 0, room);
        _scrollAt += (target - _scrollAt) * 0.08;
        JarvisScroll.ScrollToVerticalOffset(_scrollAt);
    }

    // ───────────────────────── the conversation ─────────────────────────

    static string ConversationFile => Path.Combine(IslandSettings.Folder, "jarvis-conversa.json");

    sealed record SavedTurn(bool User, string Text);

    void LoadJarvisConversation()
    {
        try
        {
            if (!File.Exists(ConversationFile))
            {
                // before 1.9.25 only the brain kept the conversation
                if (JarvisBrain.HasKey) JarvisHistory.AddRange(Brain.PastConversation().TakeLast(JarvisHistoryMax));
                return;
            }
            var turns = System.Text.Json.JsonSerializer.Deserialize<List<SavedTurn>>(File.ReadAllText(ConversationFile)) ?? [];
            JarvisHistory.AddRange(turns.Where(t => !string.IsNullOrWhiteSpace(t.Text)).TakeLast(JarvisHistoryMax).Select(t => (t.User, t.Text)));
        }
        catch (Exception ex) { Log.Error("Jarvis conversation", ex); }
    }

    void SaveJarvisConversation()
    {
        if (SnapshotMode) return;
        try
        {
            Directory.CreateDirectory(IslandSettings.Folder);
            AtomicFile.WriteAllText(ConversationFile, System.Text.Json.JsonSerializer.Serialize(JarvisHistory.Select(h => new SavedTurn(h.User, h.Text))));
        }
        catch (Exception ex) { Log.Error("Jarvis conversation", ex); }
    }

    /// <summary>A fresh conversation (Configurações › Jarvis): the past turns go, the permanent memory stays.</summary>
    public void ClearJarvisConversation()
    {
        JarvisHistory.Clear();
        SaveJarvisConversation();
        if (JarvisBrain.HasKey) Brain.ClearConversation();
        UpdateJarvisUi();
        JarvisChanged?.Invoke();
    }

    void JarvisScroll_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e) => UpdateAnswerFade();

    // the text fades out at the edges while there is more above or below
    (bool, bool) _fade;

    void UpdateAnswerFade()
    {
        var fade = (JarvisScroll.VerticalOffset > 1, JarvisScroll.ScrollableHeight - JarvisScroll.VerticalOffset > 1);
        if (fade == _fade) return;
        _fade = fade;
        var (above, below) = fade;
        JarvisScroll.OpacityMask = above || below
            ? new System.Windows.Media.LinearGradientBrush(
                [
                    new(above ? System.Windows.Media.Colors.Transparent : System.Windows.Media.Colors.Black, 0),
                    new(System.Windows.Media.Colors.Black, above ? 0.18 : 0),
                    new(System.Windows.Media.Colors.Black, below ? 0.8 : 1),
                    new(below ? System.Windows.Media.Colors.Transparent : System.Windows.Media.Colors.Black, 1),
                ], 90)
            : null;
    }

    bool JarvisOnScreen => _expanded && _page == PageJarvis;

    void UpdateJarvisUi()
    {
        JarvisStateText.Text = JarvisState == "Pronto" ? "" : JarvisState;
        var asked = JarvisHistory.LastOrDefault(h => h.User).Text;
        var answer = JarvisHistory.Count > 0 && !JarvisHistory[^1].User ? JarvisHistory[^1].Text : null;
        JarvisAsked.Text = asked != null ? "Você: " + asked : JarvisBrain.HasKey ? "Peça o que quiser: o Jarvis age no PC"
            : Jarvis.Folder(Jarvis.Engine.Ai) == null ? "Sem a IA: hora, data, contas, pesquisas e abrir programas" : "Pergunte o que quiser: tudo roda neste PC";
        JarvisAnswer.Text = JarvisState switch
        {
            "Ouvindo…" => "Estou ouvindo…",
            "Transcrevendo…" => "Entendendo o que você disse…",
            "Pensando…" => "Pensando…",
            _ => answer ?? "À disposição. Segure " + Hotkeys.Keys(_settings, "jarvis") + " e fale, ou toque em Escrever.",
        };
        JarvisTalkText.Text = _mic != null ? "Enviar" : "Falar";
        JarvisTalkIcon.Text = _mic != null ? "\uE724" : "\uE720";
        var busy = ComputerBusy;
        JarvisSteps.Visibility = Vis(busy);
        JarvisScroll.Visibility = Vis(!busy);
        JarvisStop.Visibility = Vis(busy || JarvisState is "Pensando…" or "Falando…");
        JarvisTalk.Visibility = Vis(!busy);
        JarvisType.Visibility = Vis(!busy);
        if (busy)
        {
            JarvisStateText.Text = $"Usando o computador · {_computerSteps.Count + (_computerNow != null ? 1 : 0)} passo(s)";
            RenderComputerSteps();
        }
    }

    /// <summary>Shows the island open on Jarvis's page, as if the mouse were over it.</summary>
    void ShowJarvisPage()
    {
        if (_userHidden) SetUserHidden(false);
        if (!JarvisOnScreen) SetExpanded(true, PageJarvis);
        if (!_hover) ScheduleClose(8);
    }

    void JarvisTalk_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!CanListen)
        {
            ((App)Application.Current).ShowSettings("jarvis");
            return;
        }
        if (_mic != null) _ = StopListeningAsync();
        else StartListening();
    }

    void JarvisType_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenJarvis();
    }

    /// <summary>The global shortcut: opens Jarvis's page; held = talk (with the microphone engine and "segurar para
    /// falar"), tapped = the bar to type.</summary>
    public void TriggerJarvis()
    {
        if (_mic != null) return;
        RememberJarvisTarget();
        ShowJarvisPage();
        if (_settings.JarvisVoiceMode != "hold" || !CanListen)
        {
            if (_settings.JarvisVoiceMode == "hold")
                ShowInfo("\uE720", "Para falar: chave da Groq ou microfone local", "Configurar", glyphColor: Meter.Red, seconds: 8,
                    action: () => ((App)Application.Current).ShowSettings("jarvis"));
            OpenJarvis();
            return;
        }
        var key = Hotkeys.Parse(Hotkeys.Keys(_settings, "jarvis"))?.Key ?? 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // opening the microphone can hold up this thread for half a second, so the key is timed on another one
        var released = Task.Run(async () =>
        {
            while (Native.IsKeyDown(key) && clock.Elapsed < ListenLimit) await Task.Delay(15);
            return clock.Elapsed;
        });
        // records from the first moment, but only says "Ouvindo…" once it's clearly a hold and not a tap
        StartListening(quiet: true);
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _holdTimer.Tick += (_, _) =>
        {
            if (released.IsCompleted || clock.Elapsed < TapLimit) return;
            _holdTimer!.Stop();
            ShowListening();
        };
        _holdTimer.Start();
        released.ContinueWith(t =>
        {
            _holdTimer?.Stop();
            var tap = t.Result < TapLimit;
            _ = StopListeningAsync(discard: tap);
            if (tap) OpenJarvis();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void OpenJarvis()
    {
        RememberJarvisTarget();
        if (_jarvisWindow != null)
        {
            _jarvisWindow.Activate();
            return;
        }
        _jarvisWindow = new JarvisWindow(this);
        _jarvisWindow.Closed += (_, _) => _jarvisWindow = null;
        var center = Left + ActualWidth / 2;
        _jarvisWindow.Left = center - JarvisWindow.CardWidth / 2;
        // under the island open on Jarvis's page
        _jarvisWindow.Top = Top + ExpandedFor(PageJarvis).Height + 6;
        _jarvisWindow.Show();
        _jarvisWindow.Activate();
    }

    /// <summary>Starts recording (the chat box's mic button and the held shortcut).</summary>
    public void StartListening(bool quiet = false)
    {
        if (_mic != null) return;
        Jarvis.StopSpeaking();
        var mic = _mic = new JarvisMic(_settings.JarvisMic);
        _micStart = _mic.StartAsync();
        // a click on "Falar" has no key to release: the limit ends it, and a mic that failed to start ends it at once
        _listenLimit?.Stop();
        _listenLimit = new DispatcherTimer { Interval = ListenLimit };
        _listenLimit.Tick += (_, _) => { if (ReferenceEquals(_mic, mic)) _ = StopListeningAsync(); };
        _listenLimit.Start();
        _micStart.ContinueWith(t => { if (t.Result != null && ReferenceEquals(_mic, mic)) _ = StopListeningAsync(); },
            TaskScheduler.FromCurrentSynchronizationContext());
        if (!quiet) ShowListening();
    }

    void ShowListening()
    {
        SetJarvisState("Ouvindo…");
        ShowInfo("\uE720", "Ouvindo…", "solte para enviar", glyphColor: JarvisColor, seconds: ListenLimit.TotalSeconds + 2);
    }

    /// <summary>Stops recording; unless discarded, what was said goes to Jarvis.</summary>
    public async Task StopListeningAsync(bool discard = false)
    {
        if (_mic is not { } mic || _micStart is not { } starting) return;
        _mic = null;
        _listenLimit?.Stop();
        var error = await starting;
        var wave = error == null ? await mic.StopAsync() : null;
        var silent = mic.Peak < SilentPeak;
        mic.Dispose();
        if (error != null)
        {
            SetJarvisState("Pronto");
            ShowInfo("\uE720", error, "", glyphColor: Meter.Red, seconds: 6,
                action: () => Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }));
            return;
        }
        if (wave == null || discard)
        {
            if (wave != null) try { File.Delete(wave); } catch { }
            SetJarvisState("Pronto");
            if (discard) HideJarvisPill();
            return;
        }
        if (silent)
        {
            try { File.Delete(wave); } catch { }
            SetJarvisState("Pronto");
            MicrophoneSilent(this);
            return;
        }
        SetJarvisState("Transcrevendo…");
        ShowInfo("\uE720", "Transcrevendo…", "", glyphColor: JarvisColor, seconds: 60);
        string? said, problem;
        try { (said, problem) = await HearAsync(wave); }
        finally { try { File.Delete(wave); } catch { } }
        if (string.IsNullOrWhiteSpace(said) || said.StartsWith('['))
        {
            SetJarvisState("Pronto");
            ShowInfo("\uE720", problem ?? "Não entendi", problem != null ? "" : "tente de novo", glyphColor: problem != null ? Meter.Red : JarvisColor,
                seconds: problem != null ? 7 : 3, action: problem != null ? () => ((App)Application.Current).ShowSettings("jarvis") : null);
            return;
        }
        await AskJarvisAsync(said);
    }

    // the meter is RMS × 15.5: a voice reads 0.3 and up, a muted or wrong microphone stays near 0
    const double SilentPeak = 0.04;

    static void MicrophoneSilent(IslandWindow island) =>
        island.ShowInfo("\uE720", "O microfone não captou som", "Som do Windows", glyphColor: Meter.Red, seconds: 7,
            action: () => Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }));

    /// <summary>What was said (Groq's Whisper with a key, else the local engine), or why nothing could be heard.</summary>
    async Task<(string? Text, string? Problem)> HearAsync(string wave)
    {
        string? problem = null;
        if (JarvisBrain.HasKey)
        {
            var (text, why) = await JarvisBrain.TranscribeWithReasonAsync(wave);
            if (!string.IsNullOrWhiteSpace(text)) return (text, null);
            problem = why;
        }
        if (Jarvis.Folder(Jarvis.Engine.Microphone) != null)
        {
            try
            {
                var local = await Jarvis.TranscribeAsync(wave);
                if (!string.IsNullOrWhiteSpace(local)) return (local, null);
            }
            catch (Exception ex)
            {
                Log.Error("Jarvis transcribe", ex);
                problem ??= "O microfone local falhou: baixe de novo em Configurações › Jarvis";
            }
            return (null, problem);
        }
        return (null, problem ?? "Sem chave da Groq e sem o microfone local: Configurações › Jarvis");
    }

    void HideJarvisPill()
    {
        if (_transient == Transient.Info) TransientEnded();
    }

    /// <summary>A question from the phone: answered on the phone (not out loud on the PC).</summary>
    async void OnPhoneJarvis(string id, string text)
    {
        if (JarvisState == "Pensando…" || ComputerBusy)
        {
            _phone?.PublishJarvis(id, "error", "O Jarvis está ocupado no PC. Tente de novo em instantes.");
            return;
        }
        _phone?.PublishJarvis(id, "thinking", "");
        string? answer = null;
        try { answer = await AskJarvisAsync(text, fromPhone: true); }
        catch (Exception ex) { Log.Error("Jarvis from phone", ex); }
        _phone?.PublishJarvis(id, answer == null ? "error" : "done", answer ?? "Não deu para responder agora.");
    }

    /// <param name="fromChat">From the Chat in Configurações: answered there, not spoken nor shown on the island.</param>
    public async Task<string?> AskJarvisAsync(string message, bool fromPhone = false, bool fromChat = false)
    {
        if (JarvisState == "Pensando…") return null;
        var history = JarvisHistory.TakeLast(10).ToList();
        JarvisHistory.Add((true, message));
        SetJarvisState("Pensando…");
        if (!fromChat)
            ShowInfo(fromPhone ? "\uE8EA" : "\uE99A", fromPhone ? "Pergunta do celular" : "Pensando…", Preview(message), glyphColor: JarvisColor, seconds: 150);
        var reply = JarvisBrain.HasKey ? new Jarvis.Reply(await Brain.RespondAsync(message)) : await Jarvis.AskAsync(message, history);
        JarvisHistory.Add((false, reply.Text));
        if (JarvisHistory.Count > JarvisHistoryMax) JarvisHistory.RemoveRange(0, JarvisHistory.Count - JarvisHistoryMax);
        SaveJarvisConversation();
        JarvisLast = reply;
        var speak = !fromPhone && !fromChat && _settings.JarvisSpeak && Jarvis.CanSpeak;
        SetJarvisState(speak ? "Falando…" : "Pronto");

        // "pesquise…", "abre o YouTube": the link opens right away (only Google and YouTube over https)
        if (reply.ActionUrl is { } url && Jarvis.SafeLink(url))
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        if (!JarvisOnScreen && !fromChat)
            ShowCard(null, "Jarvis", "", reply.Text, seconds: Math.Clamp(3 + reply.Text.Length / 18.0, 5, 14), glyph: "\uE99A");

        if (speak)
        {
            await Jarvis.SpeakAsync(reply.Text);
            if (JarvisState == "Falando…") SetJarvisState("Pronto");
        }
        if (JarvisOnScreen && !_hover && JarvisState == "Pronto" && _mic == null) ScheduleClose(Math.Clamp(3 + reply.Text.Length / 25.0, 5, 12));
        return reply.Text;
    }
}
