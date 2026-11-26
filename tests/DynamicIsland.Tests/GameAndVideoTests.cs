using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class GameAndVideoTests
{
    [Theory]
    [InlineData("H264", 1080, 1920, 60, "1080p a 60 fps")]
    [InlineData("H264", 1920, 1080, 59.94, "1080p a 60 fps")]
    [InlineData("HEVC", 1920, 1080, 30, "HEVC (H.265)")]
    [InlineData("H264", 3840, 2160, 30, "4K")]
    [InlineData("AV01", 1280, 720, 30, "AV1")]
    public void HeavyVideosAreNamed(string codec, uint w, uint h, double fps, string problem) =>
        Assert.Equal(problem, VideoFit.Problem(new VideoFit.Info(codec, w, h, fps, TimeSpan.FromSeconds(20), true)));

    [Theory]
    [InlineData("H264", 1080, 1920, 30)]
    [InlineData("H264", 1280, 720, 60)]
    [InlineData("H264", 720, 1280, 30)]
    public void PlainVideosPass(string codec, uint w, uint h, double fps) =>
        Assert.Null(VideoFit.Problem(new VideoFit.Info(codec, w, h, fps, TimeSpan.FromSeconds(20), true)));

    [Fact]
    public void ConvertedSizeKeepsTheShapeUnder1920()
    {
        Assert.Equal((1080u, 1920u), VideoFit.Fit(2160, 3840));
        Assert.Equal((1920u, 1080u), VideoFit.Fit(1920, 1080));
        Assert.Equal((1920u, 818u), VideoFit.Fit(2560, 1090));
    }

    [Fact]
    public void LongVideosGetALowerBitrateToFit()
    {
        var shortOne = VideoFit.Profile(new VideoFit.Info("H264", 1920, 1080, 60, TimeSpan.FromSeconds(20), true));
        var longOne = VideoFit.Profile(new VideoFit.Info("H264", 1920, 1080, 60, TimeSpan.FromMinutes(3), true));
        Assert.Equal(8_000_000u, shortOne.Video.Bitrate);
        Assert.True(longOne.Video.Bitrate < 2_500_000);
        Assert.Equal(30u, shortOne.Video.FrameRate.Numerator);
    }

    /// <summary>VIDEO_E2E=1: the 1080p60 teaser in docs/ is flagged, converted by Windows to H.264 at 30 fps, and the copy passes.</summary>
    [Fact]
    public async Task ConvertsTheTeaserForPhones()
    {
        if (Environment.GetEnvironmentVariable("VIDEO_E2E") != "1") return;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "video", "dynamic-island-motion.mp4"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var teaser = Path.Combine(dir!.FullName, "docs", "video", "dynamic-island-motion.mp4");
        var info = await VideoFit.ProbeAsync(teaser);
        Assert.NotNull(info);
        Assert.NotNull(VideoFit.Problem(info!));
        var output = await VideoFit.ConvertAsync(teaser, info!);
        Assert.NotNull(output);
        try
        {
            var fitted = await VideoFit.ProbeAsync(output!);
            Assert.NotNull(fitted);
            Assert.Null(VideoFit.Problem(fitted!));
            Assert.InRange(fitted!.Fps, 29, 31);
            Assert.True(new FileInfo(output!).Length < 60L * 1024 * 1024);
        }
        finally { if (Environment.GetEnvironmentVariable("VIDEO_KEEP") != "1") File.Delete(output!); }
    }

    [Fact]
    public void LauncherFoldersAreGames()
    {
        Assert.True(GameDetector.IsGame(@"D:\SteamLibrary\steamapps\common\Hades\x64\Hades.exe"));
        Assert.True(GameDetector.IsGame(@"C:\Users\a\AppData\Local\Roblox\Versions\version-1\RobloxPlayerBeta.exe"));
        Assert.False(GameDetector.IsGame(@"C:\Program Files\Google\Chrome\Application\chrome.exe"));
    }

    [Fact]
    public void GraphicsCardReadingsDontThrow()
    {
        var temperature = GpuStats.Temperature();
        Assert.True(temperature is null or (> 5 and < 130));
        using var gpu = new GpuStats();
        Thread.Sleep(300);
        var usage = gpu.Usage();
        Assert.True(usage is null or (>= 0 and <= 100));
    }

    [Fact]
    public void FpsNeedsThePermissionAndSaysSo()
    {
        using var counter = FpsCounter.Start();
        if (!FpsCounter.Allowed) Assert.Null(counter);
    }

    [Fact]
    public void ScreenshotsBecomeOcrBitmaps()
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0x20, 0x40, 0x80, 0xFF);
        var source = System.Windows.Media.Imaging.BitmapSource.Create(4, 4, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 16);
        using var bitmap = ScreenOcr.FromBitmapSource(source);
        var c = bitmap.GetPixel(2, 2);
        Assert.Equal((0x80, 0x40, 0x20, 0xFF), (c.R, c.G, c.B, c.A));
    }
}
