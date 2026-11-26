using System.Text.RegularExpressions;
using NAudio.Wave;

namespace DynamicIsland.Services;

/// <summary>
/// "Ei Jarvis": listens on Jarvis's microphone for short bursts of speech and checks each one with the local
/// whisper.cpp (nothing leaves the PC). Windows' own recognizer rejected every phrase on some PCs and only uses the
/// default microphone. Off by default: while on, Windows shows the microphone as in use.
/// </summary>
public sealed class WakeWord(string preferredMic = "") : IDisposable
{
    const int Rate = 16000;
    // a wake phrase is short: longer speech is a conversation, not a call
    static readonly TimeSpan Shortest = TimeSpan.FromMilliseconds(300), Longest = TimeSpan.FromSeconds(3),
        Silence = TimeSpan.FromMilliseconds(450), PreRoll = TimeSpan.FromMilliseconds(300);
    static readonly Regex Name = new(@"\b(d?jarv\w*|jarb\w*|jerv\w*|garv\w*|j[aá]vis)\b", RegexOptions.IgnoreCase);

    WaveInEvent? _mic;
    readonly List<byte> _speech = [];
    readonly Queue<byte[]> _preRoll = new();
    int _preRollBytes, _quietBytes;
    double _noise = 0.02;
    bool _inSpeech, _checking;

    /// <summary>Raised on a background thread.</summary>
    public event Action? Heard;
    /// <summary>When true (Jarvis already listening or answering) nothing is checked.</summary>
    public Func<bool>? Busy;
    public string? Problem { get; private set; }
    public bool Running => _mic != null;

    /// <summary>Whether a transcript is the wake phrase ("ei Jarvis", "ok Jarvis", "Jarvis"…).</summary>
    internal static bool IsCall(string? text) => text != null && Name.IsMatch(text);

    public Task<bool> StartAsync()
    {
        if (_mic != null) return Task.FromResult(true);
        if (Jarvis.Folder(Jarvis.Engine.Microphone) == null)
        {
            Problem = "O \"ei Jarvis\" precisa do microfone local (Configurações › Jarvis › baixar o microfone).";
            return Task.FromResult(false);
        }
        try
        {
            var device = JarvisMic.Devices().ToList().FindIndex(d => string.Equals(d, preferredMic, StringComparison.OrdinalIgnoreCase));
            _mic = new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 50 };
            _mic.DataAvailable += (_, e) => OnAudio(e.Buffer, e.BytesRecorded);
            _mic.StartRecording();
            Problem = null;
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error("WakeWord", ex);
            Problem = "Não consegui ligar o \"ei Jarvis\": " + ex.Message;
            Stop();
            return Task.FromResult(false);
        }
    }

    internal void OnAudio(byte[] buffer, int bytes)
    {
        double sum = 0;
        for (var i = 0; i + 1 < bytes; i += 2)
        {
            var x = BitConverter.ToInt16(buffer, i) / 32768.0;
            sum += x * x;
        }
        var level = Math.Sqrt(sum / Math.Max(1, bytes / 2));
        var chunk = buffer.AsSpan(0, bytes).ToArray();
        var loud = level > Math.Max(0.015, _noise * 3);
        if (!_inSpeech)
        {
            // the background level follows the room slowly, only while nobody talks
            _noise += (level - _noise) * 0.02;
            _preRoll.Enqueue(chunk);
            _preRollBytes += bytes;
            while (_preRollBytes > Bytes(PreRoll)) _preRollBytes -= _preRoll.Dequeue().Length;
            if (!loud) return;
            _inSpeech = true;
            _quietBytes = 0;
            _speech.Clear();
            foreach (var c in _preRoll) _speech.AddRange(c);
            _preRoll.Clear();
            _preRollBytes = 0;
            return;
        }
        _speech.AddRange(chunk);
        _quietBytes = loud ? 0 : _quietBytes + bytes;
        if (_speech.Count > Bytes(Longest) + Bytes(Silence))
        {
            // too long for a wake phrase: wait for the talk to end before listening again
            if (_quietBytes < Bytes(Silence)) return;
            _inSpeech = false;
            _speech.Clear();
            return;
        }
        if (_quietBytes < Bytes(Silence)) return;
        _inSpeech = false;
        var spoken = _speech.Count - _quietBytes - Bytes(PreRoll);
        var clip = _speech.ToArray();
        _speech.Clear();
        if (spoken < Bytes(Shortest) || _checking || Busy?.Invoke() == true) return;
        _checking = true;
        _ = Task.Run(() => CheckAsync(clip));
    }

    async Task CheckAsync(byte[] clip)
    {
        var wave = Path.Combine(Path.GetTempPath(), $"jarvis-wake-{Environment.ProcessId}.wav");
        try
        {
            await using (var writer = new WaveFileWriter(wave, new WaveFormat(Rate, 16, 1))) writer.Write(clip, 0, clip.Length);
            if (IsCall(await Jarvis.TranscribeAsync(wave, "Ei Jarvis."))) Heard?.Invoke();
        }
        catch (Exception ex) { Log.Warn("WakeWord", ex.Message); }
        finally
        {
            try { File.Delete(wave); } catch { }
            _checking = false;
        }
    }

    static int Bytes(TimeSpan span) => (int)(span.TotalSeconds * Rate) * 2;

    public void Stop()
    {
        var mic = _mic;
        _mic = null;
        if (mic == null) return;
        try { mic.StopRecording(); } catch { }
        mic.Dispose();
    }

    public void Dispose() => Stop();
}
