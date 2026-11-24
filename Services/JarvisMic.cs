using NAudio.Wave;

namespace DynamicIsland.Services;

/// <summary>
/// Records the microphone to a 16 kHz mono WAV (what whisper.cpp reads) and measures how loud it is, overall and in
/// three bands, for the voice glow (QuickVoice's MicMeter). Audio stays in the temp file until Jarvis transcribes it.
/// </summary>
public sealed class JarvisMic(string preferred = "") : IDisposable
{
    const int Rate = 16000;
    // voice-glow's gain chain: RMS × 5 × sensitivity 3.1
    const double Gain = 5 * 3.1;

    /// <summary>The microphones Windows lists (names cut at 31 characters by the old wave API).</summary>
    public static IReadOnlyList<string> Devices()
    {
        var list = new List<string>();
        try
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++) list.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }
        catch { }
        return list;
    }

    // The chosen microphone, else -1 (WAVE_MAPPER): the one picked in Windows' sound settings.
    static int DeviceFor(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        var devices = Devices();
        var index = devices.ToList().FindIndex(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));
        return index;
    }

    WaveInEvent? _mic;
    WaveFileWriter? _writer;
    TaskCompletionSource? _stopped;
    string _path = "";
    double _lowState, _midState, _level, _low, _mid, _high, _peak;

    /// <summary>The loudest moment so far (same scale as Level): near zero means the microphone heard nothing.</summary>
    public double Peak => Volatile.Read(ref _peak);

    public (double Level, double Low, double Mid, double High) Read() =>
        (Volatile.Read(ref _level), Volatile.Read(ref _low), Volatile.Read(ref _mid), Volatile.Read(ref _high));

    /// <summary>Null when recording started, otherwise why not.</summary>
    public Task<string?> StartAsync()
    {
        try
        {
            _path = Path.Combine(Path.GetTempPath(), $"jarvis-mic-{Environment.ProcessId}.wav");
            // A device unplugged since it was chosen falls back to Windows' microphone.
            _mic = new WaveInEvent { DeviceNumber = DeviceFor(preferred), WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 40 };
            _writer = new WaveFileWriter(_path, _mic.WaveFormat);
            var stopped = _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _mic.DataAvailable += (_, e) =>
            {
                _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                Measure(e.Buffer, e.BytesRecorded);
            };
            _mic.RecordingStopped += (_, _) =>
            {
                _writer?.Dispose();
                _writer = null;
                stopped.TrySetResult();
            };
            _mic.StartRecording();
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            Log.Error("Jarvis mic", ex);
            Dispose();
            return Task.FromResult<string?>(WaveInEvent.DeviceCount == 0
                ? "Não achei um microfone."
                : "O Windows está bloqueando o microfone: Configurações › Privacidade › Microfone › permitir apps da área de trabalho.");
        }
    }

    /// <summary>Stops and returns the WAV file (the caller deletes it).</summary>
    public async Task<string?> StopAsync()
    {
        if (_mic == null || _stopped == null) return null;
        _mic.StopRecording();
        await Task.WhenAny(_stopped.Task, Task.Delay(2000));
        Dispose();
        return File.Exists(_path) ? _path : null;
    }

    public void Dispose()
    {
        _mic?.Dispose();
        _mic = null;
        _writer?.Dispose();
        _writer = null;
        Volatile.Write(ref _level, 0);
        Volatile.Write(ref _low, 0);
        Volatile.Write(ref _mid, 0);
        Volatile.Write(ref _high, 0);
    }

    void Measure(byte[] buffer, int bytes)
    {
        var (level, low, mid, high) = Levels(buffer, 0, bytes, Rate, ref _lowState, ref _midState);
        Volatile.Write(ref _level, level);
        if (level > _peak) Volatile.Write(ref _peak, level);
        Volatile.Write(ref _low, low);
        Volatile.Write(ref _mid, mid);
        Volatile.Write(ref _high, high);
    }

    /// <summary>16-bit mono PCM → loudness overall and in three bands; one-pole filters split the voice at 300 Hz and
    /// 2 kHz, cheaper than an FFT and enough for a glow.</summary>
    internal static (double Level, double Low, double Mid, double High) Levels(byte[] buffer, int offset, int bytes, int rate,
        ref double lowState, ref double midState)
    {
        var count = bytes / 2;
        if (count == 0) return default;
        var aLow = 1 - Math.Exp(-2 * Math.PI * 300 / rate);
        var aMid = 1 - Math.Exp(-2 * Math.PI * 2000 / rate);
        double sum = 0, sumLow = 0, sumMid = 0, sumHigh = 0;
        for (var i = 0; i < count; i++)
        {
            var x = BitConverter.ToInt16(buffer, offset + i * 2) / 32768.0;
            lowState += aLow * (x - lowState);
            midState += aMid * (x - midState);
            sum += x * x;
            sumLow += lowState * lowState;
            sumMid += (midState - lowState) * (midState - lowState);
            sumHigh += (x - midState) * (x - midState);
        }
        return (Math.Sqrt(sum / count) * Gain, Math.Sqrt(sumLow / count) * Gain * 1.2,
                Math.Sqrt(sumMid / count) * Gain * 1.4, Math.Sqrt(sumHigh / count) * Gain * 2.5);
    }
}
