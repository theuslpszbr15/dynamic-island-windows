using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// JARVIS on the island: Ctrl+Alt+J opens its page; held = talk (released = ask), tapped = the bar to type. With a Groq key
// the Project.Jarvis brain answers and acts through its tools; without one, the local engines answer.
public partial class IslandWindow : IJarvisHost
{
    static readonly Color JarvisColor = Color.FromRgb(0x83, 0xD4, 0xB4);
    const int JarvisHistoryMax = 20;
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
            default: _media.PlayPause(); break;
        }
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
        JarvisChanged?.Invoke();
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
        ShowJarvisPage();
        if (_settings.JarvisVoiceMode != "hold" || !CanListen)
        {
            OpenJarvis();
            return;
        }
        var key = Hotkeys.Parse(Hotkeys.Keys(_settings, "jarvis"))?.Key ?? 0;
        var since = DateTime.Now;
        StartListening();
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _holdTimer.Tick += (_, _) =>
        {
            if (Native.IsKeyDown(key) && DateTime.Now - since < ListenLimit) return;
            _holdTimer!.Stop();
            _ = StopListeningAsync(discard: DateTime.Now - since < TapLimit);
            if (DateTime.Now - since < TapLimit) OpenJarvis();
        };
        _holdTimer.Start();
    }

    public void OpenJarvis()
    {
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
    public void StartListening()
    {
        if (_mic != null) return;
        Jarvis.StopSpeaking();
        var mic = _mic = new JarvisMic();
        _micStart = _mic.StartAsync();
        // a click on "Falar" has no key to release: the limit ends it, and a mic that failed to start ends it at once
        _listenLimit?.Stop();
        _listenLimit = new DispatcherTimer { Interval = ListenLimit };
        _listenLimit.Tick += (_, _) => { if (ReferenceEquals(_mic, mic)) _ = StopListeningAsync(); };
        _listenLimit.Start();
        _micStart.ContinueWith(t => { if (t.Result != null && ReferenceEquals(_mic, mic)) _ = StopListeningAsync(); },
            TaskScheduler.FromCurrentSynchronizationContext());
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
        SetJarvisState("Transcrevendo…");
        ShowInfo("\uE720", "Transcrevendo…", "", glyphColor: JarvisColor, seconds: 60);
        string? said;
        try { said = (JarvisBrain.HasKey ? await JarvisBrain.TranscribeAsync(wave) : null) ?? await Jarvis.TranscribeAsync(wave); }
        catch (Exception ex)
        {
            Log.Error("Jarvis transcribe", ex);
            said = null;
        }
        finally { try { File.Delete(wave); } catch { } }
        if (string.IsNullOrWhiteSpace(said) || said.StartsWith('['))
        {
            SetJarvisState("Pronto");
            ShowInfo("\uE720", "Não entendi", "tente de novo", glyphColor: JarvisColor, seconds: 3);
            return;
        }
        await AskJarvisAsync(said);
    }

    void HideJarvisPill()
    {
        if (_transient == Transient.Info) TransientEnded();
    }

    public async Task AskJarvisAsync(string message)
    {
        if (JarvisState == "Pensando…") return;
        var history = JarvisHistory.ToList();
        JarvisHistory.Add((true, message));
        SetJarvisState("Pensando…");
        ShowInfo("\uE99A", "Pensando…", Preview(message), glyphColor: JarvisColor, seconds: 150);
        var reply = JarvisBrain.HasKey ? new Jarvis.Reply(await Brain.RespondAsync(message)) : await Jarvis.AskAsync(message, history);
        JarvisHistory.Add((false, reply.Text));
        if (JarvisHistory.Count > JarvisHistoryMax) JarvisHistory.RemoveRange(0, JarvisHistory.Count - JarvisHistoryMax);
        JarvisLast = reply;
        var speak = _settings.JarvisSpeak && Jarvis.CanSpeak;
        SetJarvisState(speak ? "Falando…" : "Pronto");

        // "pesquise…", "abre o YouTube": the link opens right away (only Google and YouTube over https)
        if (reply.ActionUrl is { } url && Jarvis.SafeLink(url))
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        if (!JarvisOnScreen)
            ShowCard(null, "Jarvis", "", reply.Text, seconds: Math.Clamp(3 + reply.Text.Length / 18.0, 5, 14), glyph: "\uE99A");

        if (speak)
        {
            await Jarvis.SpeakAsync(reply.Text);
            if (JarvisState == "Falando…") SetJarvisState("Pronto");
        }
        if (JarvisOnScreen && !_hover) ScheduleClose(Math.Clamp(3 + reply.Text.Length / 25.0, 5, 12));
    }
}
