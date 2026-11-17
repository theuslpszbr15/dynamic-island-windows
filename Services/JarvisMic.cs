using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace DynamicIsland.Services;

/// <summary>Records the microphone to a 16 kHz mono WAV, what whisper.cpp reads, for "segurar para falar".</summary>
public sealed class JarvisMic : IDisposable
{
    MediaCapture? _capture;
    LowLagMediaRecording? _recording;
    string _path = "";

    /// <summary>Null when recording started, otherwise why not (no microphone, or Windows blocks desktop apps from it).</summary>
    public async Task<string?> StartAsync()
    {
        try
        {
            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio, MediaCategory = MediaCategory.Speech,
            });
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
            var file = await folder.CreateFileAsync($"jarvis-mic-{Environment.ProcessId}.wav", CreationCollisionOption.ReplaceExisting);
            _path = file.Path;
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            profile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);
            _recording = await _capture.PrepareLowLagRecordToStorageFileAsync(profile, file);
            await _recording.StartAsync();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            Dispose();
            return "O Windows está bloqueando o microfone: Configurações › Privacidade › Microfone › permitir apps da área de trabalho.";
        }
        catch (Exception ex)
        {
            Log.Error("Jarvis mic", ex);
            Dispose();
            return "Não achei um microfone.";
        }
    }

    /// <summary>Stops and returns the WAV file (the caller deletes it).</summary>
    public async Task<string?> StopAsync()
    {
        if (_recording == null) return null;
        try
        {
            await _recording.StopAsync();
            await _recording.FinishAsync();
            return _path;
        }
        catch (Exception ex)
        {
            Log.Error("Jarvis mic stop", ex);
            return null;
        }
        finally
        {
            _recording = null;
            Dispose();
        }
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _capture = null;
    }
}
