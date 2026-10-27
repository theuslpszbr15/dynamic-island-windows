using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Services;

namespace DynamicIsland;

// Dev tool: `DynamicIsland.exe --snapshot <folder>` renders every island state to PNG with demo data.
public partial class IslandWindow
{
    public bool SnapshotMode { get; init; }

    public async Task RunSnapshotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        _ready = true;
        PetView.Still = true;

        async Task Shot(string name)
        {
            await Task.Delay(1100);
            const double scale = 2;
            var bounds = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1F, 0x4E, 0x8C), Color.FromRgb(0x6A, 0x8F, 0xC9), 90), null, bounds);
            var bitmap = new RenderTargetBitmap((int)(bounds.Width * scale), (int)(bounds.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Render(Root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using var stream = File.Create(System.IO.Path.Combine(folder, name + ".png"));
            encoder.Save(stream);
        }

        await Shot("01-compact-clock");

        OnMedia(new MediaSnapshot("Blinding Lights", "The Weeknd", DemoArt(), Color.FromRgb(0xFF, 0x6B, 0x8A), true,
            TimeSpan.FromSeconds(74), TimeSpan.FromSeconds(200), DateTimeOffset.Now));
        await Shot("02-now-playing");
        _transient = Transient.None; Retarget();
        await Shot("03-compact-media");

        OnVolume(0.62f, false);
        await Shot("04-volume");
        _transient = Transient.None;

        SetExpanded(true, PageMedia);
        await Shot("05-expanded-media");
        ShowPage(PageSystem, 1);
        await Shot("06-expanded-system");
        foreach (var path in new[] { Environment.ProcessPath!, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"C:\Windows\notepad.exe" })
            Files.Add(new FileItem(path));
        ShowPage(PageFiles, 1);
        await Shot("07-expanded-files");
        SetExpanded(false);

        // Real end-to-end path: hook process -> named pipe -> island.
        _hub.Start();
        async Task Hook(string json)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "hook claude")
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            await process.StandardInput.WriteAsync(json);
            process.StandardInput.Close();
            await process.WaitForExitAsync();
        }

        await Hook("""{"hook_event_name":"PreToolUse","session_id":"s1","cwd":"C:\\dev\\dynamic-island","tool_name":"Edit","tool_input":{"file_path":"C:\\dev\\dynamic-island\\IslandWindow.xaml.cs"}}""");
        OnAgent(new AgentMessage("Codex", "s2", "working", "$ npm test", @"C:\dev\api"));
        await Shot("08-compact-agent");
        await Hook("""{"hook_event_name":"Notification","session_id":"s1","cwd":"C:\\dev\\dynamic-island","message":"Claude needs your permission to use Bash"}""");
        await Shot("09-agent-alert");
        SetExpanded(true, PageAgents);
        await Shot("10-expanded-agents");
        SetExpanded(false);

        _settings.Shape = IslandShape.Notch;
        ApplyShape();
        await Shot("11-notch-compact");
        SetExpanded(true, PageMedia);
        await Shot("12-notch-expanded");
        SetExpanded(false);

        _settings.DiscreetIdle = true;
        Agents.Clear();
        ApplyShape();
        await Shot("13-discreet-handle");

        _settings.Shape = IslandShape.Pill;
        ApplyShape();
        SetExpanded(true, PageTimer);
        await Shot("14-timer-presets");
        StartTimer(25);
        _timerEnd = DateTime.Now + TimeSpan.FromSeconds(18 * 60 + 42);
        await Shot("15-expanded-timer");
        SetExpanded(false);
        await Shot("16-compact-timer");
        StopTimer();

        OnNotification(new IslandNotification("Slack", "Gabriel Paiva", "Bora testar a ilha nova hoje? Ficou muito boa", null));
        await Shot("17-notification");
        _transient = Transient.None;
        OnPrivacy(new PrivacyState(["Teams"], []));
        await Shot("18-microphone");
        _transient = Transient.None;
        Retarget();
        OnPrivacy(new PrivacyState(["Teams"], ["Teams"]));
        _transient = Transient.None;
        Retarget();
        await Shot("19-privacy-dots");

        OnPrivacy(PrivacyState.None);
        _settings.DiscreetIdle = false;
        _mediaSnap = null;
        ApplyShape();
        _lastActivity = DateTime.Now - TimeSpan.FromMinutes(5);
        UpdatePet();
        await Shot("20-pet-sleep");
        Touch();
        UpdatePet();
        CompactPet.React(PetView.Mood.Dizzy, 3);
        await Shot("21-pet-dizzy");

        _settings.Theme = "Ameixa";
        ApplyShape();
        OnMedia(new MediaSnapshot("Blinding Lights", "The Weeknd", DemoArt(), Color.FromRgb(0xFF, 0x6B, 0x8A), true,
            TimeSpan.FromSeconds(74), TimeSpan.FromSeconds(200), DateTimeOffset.Now));
        _transient = Transient.None;
        SetExpanded(true, PageMedia);
        await Shot("22-theme-ameixa");
        SetExpanded(false);
        _settings.Theme = "Oceano";
        ApplyShape();
        await Shot("23-theme-oceano-dance");

        var info = GC.GetGCMemoryInfo();
        File.WriteAllText(System.IO.Path.Combine(folder, "memory.txt"),
            $"managed={GC.GetTotalMemory(true) / 1048576.0:0.0}MB heap={info.HeapSizeBytes / 1048576.0:0.0}MB " +
            $"committed={info.TotalCommittedBytes / 1048576.0:0.0}MB private={Environment.WorkingSet / 1048576.0:0.0}MB");
    }

    static BitmapSource DemoArt()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2B, 0x0F, 0x4C), Color.FromRgb(0xE8, 0x3A, 0x6A), 45), null, new Rect(0, 0, 240, 240));
            dc.DrawEllipse(new RadialGradientBrush(Color.FromRgb(0xFF, 0xC8, 0x6B), Colors.Transparent), null, new Point(150, 95), 90, 90);
        }
        var bitmap = new RenderTargetBitmap(240, 240, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
