using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// "Ei Jarvis": the wake phrase opens Jarvis listening; it sends when you stop talking.
public partial class IslandWindow
{
    WakeWord? _wake;
    DispatcherTimer? _voiceEnd;

    public string? WakeProblem => _wake?.Problem;

    public async void ApplyWakeWord()
    {
        if (!_settings.WakeWord || SnapshotMode)
        {
            _wake?.Stop();
            return;
        }
        if (!CanListen)
        {
            ShowInfo("\uE720", "\"Ei Jarvis\" precisa da chave da Groq ou do microfone", "Configurar", glyphColor: JarvisColor,
                action: () => ((App)System.Windows.Application.Current).ShowSettings("jarvis"), seconds: 6);
            return;
        }
        if (_wake == null)
        {
            _wake = new WakeWord();
            _wake.Heard += () => Dispatcher.InvokeAsync(OnWakeWord);
        }
        if (!await _wake.StartAsync() && _wake.Problem is { } problem)
            ShowInfo("\uE720", problem, "", glyphColor: Meter.Red, seconds: 8);
    }

    void OnWakeWord()
    {
        // already talking, dictating or answering
        if (!_ready || _mic != null || _dictMic != null || JarvisState != "Pronto") return;
        RememberJarvisTarget();
        ShowJarvisPage();
        StartListening();
        var mic = _mic;
        var started = DateTime.Now;
        DateTime? lastVoice = null;
        _voiceEnd?.Stop();
        _voiceEnd = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _voiceEnd.Tick += (_, _) =>
        {
            if (_mic == null || !ReferenceEquals(_mic, mic))
            {
                _voiceEnd!.Stop();
                return;
            }
            var now = DateTime.Now;
            if (mic.Read().Level > 0.15) lastVoice = now;
            // the question ends after a short pause; nothing said in 6 s = never mind
            var done = lastVoice is { } spoke ? now - spoke > TimeSpan.FromSeconds(1.3) : now - started > TimeSpan.FromSeconds(6);
            if (!done && now - started < TimeSpan.FromSeconds(15)) return;
            _voiceEnd!.Stop();
            _ = StopListeningAsync(discard: lastVoice == null);
        };
        _voiceEnd.Start();
    }
}
