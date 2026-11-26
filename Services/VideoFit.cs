using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace DynamicIsland.Services;

/// <summary>Videos many phones can't play (1080p at 60 fps, 4K, HEVC/AV1…) made into a plain one they all play:
/// H.264 at 30 fps, at most 1920 on the long side, sized to fit what the phone link takes. Windows' own encoder
/// (Media Foundation, the graphics card when it has one): nothing to download.</summary>
public static class VideoFit
{
    public sealed record Info(string Codec, uint Width, uint Height, double Fps, TimeSpan Duration, bool HasAudio);

    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".mkv", ".webm", ".3gp", ".avi", ".wmv" };

    /// <summary>What fits in one file the phone receives (the link's 60 MB, with a margin).</summary>
    const long TargetBytes = 55L * 1024 * 1024;

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>Codec, size, frame rate and length; null when Windows can't open it.</summary>
    public static async Task<Info?> ProbeAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            var clip = await MediaClip.CreateFromFileAsync(file);
            var video = clip.GetVideoEncodingProperties();
            var fps = video.FrameRate.Denominator > 0 ? video.FrameRate.Numerator / (double)video.FrameRate.Denominator : 0;
            return new Info(video.Subtype ?? "", video.Width, video.Height, fps, clip.OriginalDuration, clip.EmbeddedAudioTracks.Count > 0);
        }
        catch (Exception ex)
        {
            Log.Warn("VideoFit", $"probe {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Why this video may not play on a phone, or null when it plays everywhere.</summary>
    public static string? Problem(Info info)
    {
        var codec = info.Codec.ToUpperInvariant();
        var longSide = Math.Max(info.Width, info.Height);
        if (codec is "HEVC" or "H265" or "AV01" or "AV1") return codec is "AV01" or "AV1" ? "AV1" : "HEVC (H.265)";
        if (codec != "H264" && codec is not ("VP80" or "VP90")) return codec.Length > 0 ? codec : "formato pouco comum";
        if (longSide > 1920) return longSide >= 3800 ? "4K" : $"{info.Width}×{info.Height}";
        if (info.Fps > 31 && longSide > 1280) return $"{Math.Min(info.Width, info.Height)}p a {Math.Round(info.Fps)} fps";
        return null;
    }

    /// <summary>The plain copy in a temp folder only this user reads; null when Windows can't convert it.</summary>
    public static async Task<string?> ConvertAsync(string path, Info info, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Dynamic Island", "convertidos");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder))
            if (File.GetLastWriteTime(old) < DateTime.Now.AddHours(-6)) try { File.Delete(old); } catch { }
        var output = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + " (celular).mp4");
        try
        {
            File.Create(output).Dispose();
            var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            var target = await StorageFile.GetFileFromPathAsync(output);
            var profile = Profile(info);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareFileTranscodeAsync(source, target, profile);
            if (!prepared.CanTranscode)
            {
                Log.Warn("VideoFit", $"can't convert {Path.GetFileName(path)}: {prepared.FailureReason}");
                File.Delete(output);
                return null;
            }
            var run = prepared.TranscodeAsync();
            if (progress != null) run.Progress = (_, percent) => progress.Report(percent / 100);
            using (cancel.Register(run.Cancel)) await run;
            return output;
        }
        catch (Exception ex)
        {
            if (ex is not (OperationCanceledException or TaskCanceledException)) Log.Warn("VideoFit", $"convert {Path.GetFileName(path)}: {ex.Message}");
            try { File.Delete(output); } catch { }
            return null;
        }
    }

    /// <summary>H.264 High at 30 fps, the long side at most 1920 (even sizes), a bitrate that keeps it under the limit.</summary>
    public static MediaEncodingProfile Profile(Info info)
    {
        var (width, height) = Fit(info.Width, info.Height);
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = width;
        profile.Video.Height = height;
        profile.Video.FrameRate.Numerator = 30;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.ProfileId = H264ProfileIds.High;
        var audioBits = info.HasAudio ? 160_000u : 0u;
        var seconds = Math.Max(1, info.Duration.TotalSeconds);
        var budget = TargetBytes * 8 / seconds - audioBits;
        profile.Video.Bitrate = (uint)Math.Clamp(budget, 1_200_000, 8_000_000);
        if (info.HasAudio) profile.Audio = AudioEncodingProperties.CreateAac(44100, 2, audioBits);
        else profile.Audio = null;
        return profile;
    }

    public static (uint Width, uint Height) Fit(uint width, uint height)
    {
        if (width == 0 || height == 0) return (1920, 1080);
        var scale = Math.Min(1.0, 1920.0 / Math.Max(width, height));
        static uint Even(double v) => (uint)Math.Max(2, Math.Round(v / 2) * 2);
        return (Even(width * scale), Even(height * scale));
    }
}
