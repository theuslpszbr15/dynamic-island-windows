using System.Windows;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// Dictation anywhere: hold the shortcut, speak, let go and the words are pasted where you type. A quick tap starts
// hands-free dictation (tap again to paste); Esc cancels. Speech goes to Groq's Whisper with a key, else the local one.
public partial class IslandWindow
{
    static readonly TimeSpan DictationLimit = TimeSpan.FromMinutes(2), DictationMinimum = TimeSpan.FromMilliseconds(350);
    const uint VK_ESCAPE = 0x1B;

    JarvisMic? _dictMic;
    Task<string?>? _dictStart;
    DispatcherTimer? _dictTimer;
    DateTime _dictSince;
    bool _dictHandsFree;

    public bool Dictating => _dictMic != null;

    void TriggerDictation()
    {
        if (_dictMic != null)
        {
            if (_dictHandsFree) _ = StopDictationAsync();
            return;
        }
        // one microphone at a time: Jarvis is listening
        if (_mic != null) return;
        if (!CanListen)
        {
            ShowInfo("\uE720", "Ditado: falta a chave da Groq ou o microfone", "Configurar", glyphColor: JarvisColor,
                action: () => ((App)Application.Current).ShowSettings("jarvis"), seconds: 6);
            return;
        }
        var key = Hotkeys.Parse(Hotkeys.Keys(_settings, "dictate"))?.Key ?? 0;
        Jarvis.StopSpeaking();
        _dictSince = DateTime.Now;
        _dictHandsFree = false;
        var mic = _dictMic = new JarvisMic();
        _dictStart = mic.StartAsync();
        _glow.Listening = true;
        ShowInfo("\uE720", "Ditando…", "solte para colar", glyphColor: JarvisColor, seconds: DictationLimit.TotalSeconds + 5);

        var held = true;
        _dictTimer?.Stop();
        _dictTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _dictTimer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_dictMic, mic)) return;
            var elapsed = DateTime.Now - _dictSince;
            if (Native.IsKeyDown(VK_ESCAPE)) _ = StopDictationAsync(cancel: true);
            else if (elapsed > DictationLimit) _ = StopDictationAsync();
            else if (held && (!Native.IsKeyDown(key) || !TextPaster.ModifiersDown()))
            {
                held = false;
                if (elapsed >= TapLimit) _ = StopDictationAsync();
                else
                {
                    _dictHandsFree = true;
                    ShowInfo("\uE720", "Ditando (mãos livres)…", "toque de novo", glyphColor: JarvisColor, seconds: DictationLimit.TotalSeconds + 5);
                }
            }
        };
        _dictTimer.Start();
        _dictStart.ContinueWith(t =>
        {
            if (t.Result is not { } error || !ReferenceEquals(_dictMic, mic)) return;
            _ = StopDictationAsync(cancel: true);
            ShowInfo("\uE720", error, "", glyphColor: Meter.Red, seconds: 6,
                action: () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    async Task StopDictationAsync(bool cancel = false)
    {
        if (_dictMic is not { } mic || _dictStart is not { } starting) return;
        _dictMic = null;
        _dictTimer?.Stop();
        _glow.Listening = false;
        var tooShort = DateTime.Now - _dictSince < DictationMinimum;
        var silent = mic.Peak < SilentPeak;
        var wave = await starting == null ? await mic.StopAsync() : null;
        mic.Dispose();
        try
        {
            if (wave == null || cancel)
            {
                if (cancel) ShowInfo("\uE711", "Ditado cancelado", "", seconds: 1.6);
                return;
            }
            if (tooShort)
            {
                ShowInfo("\uE720", "Não ouvi nada", "", glyphColor: JarvisColor, seconds: 2);
                return;
            }
            if (silent)
            {
                MicrophoneSilent(this);
                return;
            }
            _glow.Processing = true;
            ShowInfo("\uE720", "Transcrevendo…", "", glyphColor: JarvisColor, seconds: 60);
            var (heard, problem) = await HearAsync(wave);
            if (Dictation.Clean(heard) is not { } text)
            {
                ShowInfo("\uE720", problem ?? "Não entendi", problem != null ? "" : "tente de novo", glyphColor: problem != null ? Meter.Red : JarvisColor,
                    seconds: problem != null ? 7 : 2.5, action: problem != null ? () => ((App)Application.Current).ShowSettings("jarvis") : null);
                return;
            }
            if (_settings.DictationStyle != "said" && JarvisBrain.HasKey)
            {
                ShowInfo("\uE70F", "Melhorando o texto…", "", glyphColor: JarvisColor, seconds: 30);
                text = await Dictation.PolishAsync(text, _settings.DictationStyle);
            }
            if (!await TextPaster.PasteAsync(text))
            {
                ShowInfo("\uE783", "A área de transferência está ocupada", "tente de novo", glyphColor: Meter.Red, seconds: 3);
                return;
            }
            ShowInfo("\uE73E", Preview(text), "Colado", glyphColor: JarvisColor, seconds: 2.5);
        }
        finally
        {
            _glow.Processing = false;
            if (wave != null) try { File.Delete(wave); } catch { }
        }
    }
}
