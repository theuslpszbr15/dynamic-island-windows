using Windows.Media.SpeechRecognition;

namespace DynamicIsland.Services;

/// <summary>
/// "Ei Jarvis": Windows' own offline speech recognition listening only for a few fixed phrases (no audio leaves the PC,
/// nothing is recorded). Off by default: while on, Windows shows the microphone as in use.
/// </summary>
public sealed class WakeWord : IDisposable
{
    static readonly string[] Phrases = ["ei jarvis", "oi jarvis", "e aí jarvis", "hey jarvis", "ok jarvis"];

    SpeechRecognizer? _recognizer;
    bool _running;

    /// <summary>Raised on a background thread.</summary>
    public event Action? Heard;
    public string? Problem { get; private set; }
    public bool Running => _running;

    public async Task<bool> StartAsync()
    {
        if (_running) return true;
        try
        {
            var language = SpeechRecognizer.SupportedGrammarLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
                           ?? SpeechRecognizer.SystemSpeechLanguage;
            _recognizer = new SpeechRecognizer(language);
            _recognizer.Constraints.Add(new SpeechRecognitionListConstraint(Phrases, "wake"));
            var compiled = await _recognizer.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success)
            {
                Problem = "O reconhecimento de voz do Windows não está disponível (Configurações › Hora e idioma › Fala).";
                Stop();
                return false;
            }
            _recognizer.ContinuousRecognitionSession.ResultGenerated += (_, e) =>
            {
                if (e.Result.Status == SpeechRecognitionResultStatus.Success
                    && e.Result.Confidence is SpeechRecognitionConfidence.High or SpeechRecognitionConfidence.Medium)
                    Heard?.Invoke();
            };
            // the session ends by itself after long silences or errors: start it again while on
            _recognizer.ContinuousRecognitionSession.Completed += async (_, _) =>
            {
                if (!_running || _recognizer == null) return;
                try { await _recognizer.ContinuousRecognitionSession.StartAsync(); }
                catch (Exception ex) { Log.Warn("WakeWord", ex.Message); }
            };
            await _recognizer.ContinuousRecognitionSession.StartAsync();
            _running = true;
            Problem = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("WakeWord", ex);
            Problem = "Não consegui ligar o \"ei Jarvis\": " + ex.Message;
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        _running = false;
        var recognizer = _recognizer;
        _recognizer = null;
        if (recognizer == null) return;
        try { _ = recognizer.ContinuousRecognitionSession.CancelAsync(); } catch { }
        recognizer.Dispose();
    }

    public void Dispose() => Stop();
}
