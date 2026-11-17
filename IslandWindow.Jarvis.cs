using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// JARVIS on the island: Ctrl+Alt+J held = talk (released = ask), tapped = the chat box; answers show as a card and are
// spoken with the Piper voice.
public partial class IslandWindow
{
    static readonly Color JarvisColor = Color.FromRgb(0xBF, 0x5A, 0xF2);
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
    DispatcherTimer? _holdTimer;
    JarvisWindow? _jarvisWindow;

    void SetJarvisState(string state)
    {
        JarvisState = state;
        JarvisChanged?.Invoke();
    }

    /// <summary>The global shortcut: hold to talk (with the microphone engine and "segurar para falar"), else the chat box.</summary>
    public void TriggerJarvis()
    {
        if (_mic != null) return;
        if (_settings.JarvisVoiceMode != "hold" || Jarvis.Folder(Jarvis.Engine.Microphone) == null)
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
        _jarvisWindow.Top = Top + 52;
        _jarvisWindow.Show();
        _jarvisWindow.Activate();
    }

    /// <summary>Starts recording (the chat box's mic button and the held shortcut).</summary>
    public void StartListening()
    {
        if (_mic != null) return;
        Jarvis.StopSpeaking();
        _mic = new JarvisMic();
        _micStart = _mic.StartAsync();
        SetJarvisState("Ouvindo…");
        ShowInfo("\uE720", "Ouvindo…", "solte para enviar", glyphColor: JarvisColor, seconds: ListenLimit.TotalSeconds + 2);
    }

    /// <summary>Stops recording; unless discarded, what was said goes to Jarvis.</summary>
    public async Task StopListeningAsync(bool discard = false)
    {
        if (_mic is not { } mic || _micStart is not { } starting) return;
        _mic = null;
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
        try { said = await Jarvis.TranscribeAsync(wave); }
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
        var reply = await Jarvis.AskAsync(message, history);
        JarvisHistory.Add((false, reply.Text));
        if (JarvisHistory.Count > JarvisHistoryMax) JarvisHistory.RemoveRange(0, JarvisHistory.Count - JarvisHistoryMax);
        JarvisLast = reply;
        SetJarvisState("Pronto");

        if (reply.ActionUrl is { } url && Jarvis.SafeLink(url))
            ShowInfo("\uE99A", reply.Text, reply.ActionLabel ?? "Abrir", glyphColor: JarvisColor, seconds: 8,
                action: () => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        else
            ShowCard(null, "Jarvis", "", reply.Text, seconds: Math.Clamp(3 + reply.Text.Length / 18.0, 5, 14), glyph: "\uE99A");

        if (!_settings.JarvisSpeak || Jarvis.Folder(Jarvis.Engine.Voice) == null) return;
        SetJarvisState("Falando…");
        await Jarvis.SpeakAsync(reply.Text);
        if (JarvisState == "Falando…") SetJarvisState("Pronto");
    }
}
