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
        PetView.SetDefaultOutfit(PetOutfit.None);

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
        await Task.Delay(300);
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

        await Hook("""{"hook_event_name":"PostToolUse","session_id":"s1","cwd":"C:\\dev\\dynamic-island","tool_name":"Read","tool_input":{"file_path":"C:\\dev\\dynamic-island\\IslandWindow.xaml.cs"}}""");
        await Hook("""{"hook_event_name":"PreToolUse","session_id":"s1","cwd":"C:\\dev\\dynamic-island","tool_name":"Edit","tool_input":{"file_path":"C:\\dev\\dynamic-island\\IslandWindow.xaml.cs","old_string":"if (_expanded) return ExpandedSize;\n    var h = CompactHeight;","new_string":"if (_expanded) return ExpandedFor(_page);\n    var h = CompactHeight;"}}""");
        OnAgent(new AgentMessage("Codex", "s2", "working", "$ npm test", @"C:\dev\api", "run"));
        await Shot("08-compact-agent");
        await Hook("""{"hook_event_name":"Notification","session_id":"s1","cwd":"C:\\dev\\dynamic-island","message":"Claude needs your permission to use Bash"}""");
        await Shot("09-agent-alert");
        SetExpanded(true, PageAgents);
        await Shot("10-expanded-agents");
        OnAgent(new AgentMessage("Codex", "s2", "end", null, null));
        await Hook("""{"hook_event_name":"PostToolUse","session_id":"s1","cwd":"C:\\dev\\dynamic-island","tool_name":"Edit","tool_input":{"file_path":"C:\\dev\\dynamic-island\\IslandWindow.xaml.cs","old_string":"if (_expanded) return ExpandedSize;\n    var h = CompactHeight;","new_string":"if (_expanded) return ExpandedFor(_page);\n    var h = CompactHeight;"}}""");
        RefreshPage();
        await Shot("10b-agent-detail");
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

        _settings.Theme = "plum";
        ApplyShape();
        OnMedia(new MediaSnapshot("Blinding Lights", "The Weeknd", DemoArt(), Color.FromRgb(0xFF, 0x6B, 0x8A), true,
            TimeSpan.FromSeconds(74), TimeSpan.FromSeconds(200), DateTimeOffset.Now));
        _transient = Transient.None;
        SetExpanded(true, PageMedia);
        await Shot("22-theme-ameixa");
        SetExpanded(false);
        _settings.Theme = "ocean";
        ApplyShape();
        await Shot("23-theme-oceano-dance");

        _settings.Theme = IslandThemes.Default;
        _mediaSnap = null;
        Agents.Clear();
        foreach (var (species, name) in new[] { (PetSpecies.Bit, "bit"), (PetSpecies.Coco, "coco"), (PetSpecies.Pipoca, "pipoca"), (PetSpecies.Bu, "bu") })
        {
            _settings.Mascot = species;
            ApplyShape();
            await Shot("25-mascot-" + name);
        }
        _settings.Mascot = PetSpecies.Bit;
        ApplyShape();

        // Bit up close: idle, working (blue hoodie), waiting (orange), happy, annoyed, asleep
        var moods = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)),
        };
        foreach (var (mood, tint) in new (PetView.Mood, Color?)[]
                 {
                     (PetView.Mood.Idle, null), (PetView.Mood.Working, AgentSession.Working), (PetView.Mood.Waiting, AgentSession.Waiting),
                     (PetView.Mood.Happy, null), (PetView.Mood.Annoyed, null), (PetView.Mood.Sleep, null),
                 })
        {
            var bit = new PetView { Species = PetSpecies.Bit, Active = false };
            bit.Tint(tint);
            bit.BaseMood = mood;
            moods.Children.Add(new System.Windows.Controls.Viewbox { Width = 110, Height = 110, Margin = new Thickness(12, 22, 12, 12), Child = bit });
        }
        var moodCard = new System.Windows.Controls.Border { Child = moods };
        moodCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        moodCard.Arrange(new Rect(moodCard.DesiredSize));
        moodCard.UpdateLayout();
        await Task.Delay(400);
        RecapWindow.SaveCard(moodCard, System.IO.Path.Combine(folder, "25-bit-moods.png"));

        // PatPat: the five frames of a pat, Bit on the top row and the other mascots below
        var patRows = new System.Windows.Controls.StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)) };
        foreach (var species in new[] { PetSpecies.Bit, PetSpecies.Coco, PetSpecies.Pipoca, PetSpecies.Bu })
        {
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            for (var frame = 0; frame < 5; frame++)
            {
                var pet = new PetView { FixedSpecies = species, Active = false };
                pet.ShowPatFrame(frame);
                row.Children.Add(new System.Windows.Controls.Viewbox { Width = 96, Height = 96, Margin = new Thickness(14, 30, 14, 8), Child = pet });
            }
            patRows.Children.Add(row);
        }
        var patCard = new System.Windows.Controls.Border { Child = patRows };
        patCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        patCard.Arrange(new Rect(patCard.DesiredSize));
        patCard.UpdateLayout();
        RecapWindow.SaveCard(patCard, System.IO.Path.Combine(folder, "26-patpat.png"));

        OnPetDizzy();
        await Shot("24-dizzy-card");

        // Glass blurs whatever is really on screen behind the island.
        _transient = Transient.None;
        _settings.Theme = IslandThemes.Glass;
        ApplyShape();
        OnMedia(new MediaSnapshot("Blinding Lights", "The Weeknd", DemoArt(), Color.FromRgb(0xFF, 0x6B, 0x8A), true,
            TimeSpan.FromSeconds(74), TimeSpan.FromSeconds(200), DateTimeOffset.Now, "Spotify"));
        _transient = Transient.None;
        SetExpanded(true, PageMedia);
        await Task.Delay(600);
        CaptureGlass(true);
        await Shot("26-glass-media");
        SetExpanded(false);
        await Task.Delay(600);
        CaptureGlass(true);
        await Shot("27-glass-compact");

        // ── 1.3: approvals, info pill, lyrics, system extras, calendar, outfits, light theme, recap
        _settings.Theme = IslandThemes.Default;
        ApplyShape();
        var approval = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "hook claude")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using (var hook = System.Diagnostics.Process.Start(approval)!)
        {
            await hook.StandardInput.WriteAsync("""{"hook_event_name":"PermissionRequest","session_id":"s9","cwd":"C:\\dev\\dynamic-island","tool_name":"Bash","tool_input":{"command":"git push origin main"}}""");
            hook.StandardInput.Close();
            await Task.Delay(1500);
            await Shot("28-approval");
            Answer("allow");
            var output = await hook.StandardOutput.ReadToEndAsync();
            await hook.WaitForExitAsync();
            File.WriteAllText(System.IO.Path.Combine(folder, "approval-output.txt"), output);
        }
        Agents.Clear();
        _transient = Transient.None;
        Retarget();

        ShowInfo("\uE8C8", "git commit -m \"feat: ilha 1.3\"", "Copiado");
        await Shot("29-info-clipboard");
        ShowInfo("\uE765", "Caps Lock ativado", "ABC", glyphColor: AgentSession.Done);
        await Shot("30-info-caps");
        _transient = Transient.None;

        OnMedia(new MediaSnapshot("Blinding Lights", "The Weeknd", DemoArt(), Color.FromRgb(0xFF, 0x6B, 0x8A), true,
            TimeSpan.FromSeconds(74), TimeSpan.FromSeconds(200), DateTimeOffset.Now, "Spotify"));
        _transient = Transient.None;
        _lyrics = LyricsService.Parse("[00:58.00]I've been on my own for long enough\n[01:12.00]I said, ooh, I'm blinded by the lights\n[01:20.00]No, I can't sleep until I feel your touch");
        Retarget();
        await Shot("31-compact-lyrics");
        _lyrics = null;

        SetExpanded(true, PageSystem);
        BrightnessRow.Visibility = Visibility.Visible;
        ShowBrightness(70);
        ShowBluetooth([new("Galaxy Buds2", 80), new("MX Master 3", 12)]);
        await Shot("32-system-brightness-bt");
        SetWeather(new WeatherNow("Joinville", 24, 26, 78, 11, "Parcialmente nublado", 116, 19, 28));
        ShowPage(PageCalendar, 1);
        await Shot("33-calendar-weather");
        SetExpanded(false);
        _mediaSnap = null;
        _transient = Transient.None;
        Retarget();

        var lineup = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
        };
        var outfits = new[] { PetOutfit.Gorro, PetOutfit.Festa, PetOutfit.Noel, PetOutfit.Palha, PetOutfit.Oculos, PetOutfit.Coroa };
        for (var i = 0; i < outfits.Length; i++)
        {
            var dressed = new PetView { Species = i % 2 == 0 ? PetSpecies.Bit : (PetSpecies)(1 + i / 2 % 3), BaseMood = PetView.Mood.Happy };
            dressed.Outfit = outfits[i];
            lineup.Children.Add(new System.Windows.Controls.Viewbox { Width = 96, Height = 96, Margin = new Thickness(14, 34, 14, 10), Child = dressed });
        }
        var lineupCard = new System.Windows.Controls.Border { CornerRadius = new CornerRadius(24), Child = lineup, ClipToBounds = true };
        lineupCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        lineupCard.Arrange(new Rect(lineupCard.DesiredSize));
        lineupCard.UpdateLayout();
        RecapWindow.SaveCard(lineupCard, System.IO.Path.Combine(folder, "34-outfits.png"));

        _settings.Theme = IslandThemes.Light;
        ApplyShape();
        OnMedia(new MediaSnapshot("Blinding Lights", "The Weeknd", DemoArt(), Color.FromRgb(0xFF, 0x6B, 0x8A), true,
            TimeSpan.FromSeconds(74), TimeSpan.FromSeconds(200), DateTimeOffset.Now, "Spotify"));
        _transient = Transient.None;
        SetExpanded(true, PageMedia);
        await Shot("35-light-media");
        ShowPage(PageSystem, 1);
        await Shot("36-light-system");
        SetExpanded(false);
        await Shot("37-light-compact");
        _settings.Theme = IslandThemes.Default;
        ApplyShape();

        var card = RecapWindow.BuildCard(new WeekRecap(DateOnly.FromDateTime(DateTime.Today.AddDays(-7)), DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
            5 * 3600 + 47 * 60, 128, 342, 23, 9, "dynamic-island", DayOfWeek.Wednesday, 6));
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();
        RecapWindow.SaveCard(card, System.IO.Path.Combine(folder, "38-recap.png"));

        _settings.Theme = IslandThemes.Default;
        _mediaSnap = null;
        Agents.Clear();
        _transient = Transient.None;
        ApplyShape();
        var perf = "default: " + await MeasureFrames() + Environment.NewLine;
        File.WriteAllText(System.IO.Path.Combine(folder, "perf.txt"), perf);
        File.WriteAllText(System.IO.Path.Combine(folder, "phone-link.txt"), await TestPhoneLink());

        // The settings panel, one image per section (with the phone link on, to show the pairing code).
        _settings.PhoneLink = true;
        ApplyPhoneLink();
        var settingsWindow = new SettingsWindow((App)Application.Current) { Left = 40, Top = 40 };
        settingsWindow.Show();
        foreach (var section in new[] { "general", "look", "island", "agents", "phone", "tray", "updates", "phone-download" })
        {
            if (section == "phone-download") settingsWindow.ShowDownloadQr();
            else settingsWindow.Navigate(section);
            await Task.Delay(700);
            var root = (FrameworkElement)settingsWindow.Content;
            var image = new RenderTargetBitmap((int)(root.ActualWidth * 1.5), (int)(root.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            image.Render(root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            await using var file = File.Create(System.IO.Path.Combine(folder, $"40-settings-{section}.png"));
            png.Save(file);
        }
        settingsWindow.Close();
        _settings.PhoneLink = false;
        ApplyPhoneLink();

        var info = GC.GetGCMemoryInfo();
        File.WriteAllText(System.IO.Path.Combine(folder, "memory.txt"),
            $"managed={GC.GetTotalMemory(true) / 1048576.0:0.0}MB heap={info.HeapSizeBytes / 1048576.0:0.0}MB " +
            $"committed={info.TotalCommittedBytes / 1048576.0:0.0}MB private={Environment.WorkingSet / 1048576.0:0.0}MB");
    }

    /// <summary>Frame pacing and CPU while the island opens and closes a few times.</summary>
    async Task<string> MeasureFrames()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var process = System.Diagnostics.Process.GetCurrentProcess();
        List<double> gaps = [];
        double last = -1;
        void OnFrame(object? sender, EventArgs e)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            if (_animating && last >= 0) gaps.Add(now - last);
            last = now;
        }
        var cpuBefore = process.TotalProcessorTime;
        CompositionTarget.Rendering += OnFrame;
        for (var i = 0; i < 4; i++)
        {
            SetExpanded(true, PageMedia);
            await Task.Delay(900);
            SetExpanded(false);
            await Task.Delay(900);
        }
        CompositionTarget.Rendering -= OnFrame;
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        gaps.Sort();
        if (gaps.Count == 0) return "no frames";
        return $"frames={gaps.Count} avg={gaps.Average():0.0}ms p95={gaps[(int)(gaps.Count * 0.95)]:0.0}ms max={gaps[^1]:0.0}ms " +
               $"slow(>25ms)={gaps.Count(g => g > 25)} cpu={cpu:0}ms";
    }

    /// <summary>Plays the phone over loopback: wrong token, pairing, a real hook's permission request, Allow from the "phone".</summary>
    async Task<string> TestPhoneLink()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            _settings.PhoneLink = true;
            ApplyPhoneLink();
            if (_phone?.Running != true) throw new InvalidOperationException("link não iniciou: " + PhoneError);
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(_phone!.PairingLink()).Query);
            var (fingerprint, token, port) = (query["fp"]!, query["token"]!, int.Parse(query["port"]!));

            async Task<(System.Net.Security.SslStream Ssl, StreamReader Reader)> Connect(string secret)
            {
                var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port);
                var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
                    cert != null && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant() == fingerprint);
                await ssl.AuthenticateAsClientAsync("dynamic-island");
                await ssl.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"{{\"type\":\"hello\",\"v\":1,\"token\":\"{secret}\",\"device\":\"teste\"}}\n"));
                return (ssl, new StreamReader(ssl));
            }

            var (bad, badReader) = await Connect("token-errado");
            log.AppendLine("token errado -> " + await badReader.ReadLineAsync());
            bad.Dispose();

            var (ssl, reader) = await Connect(token);
            log.AppendLine("pareado -> " + await reader.ReadLineAsync());
            log.AppendLine("primeiro -> " + await reader.ReadLineAsync());

            var hook = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "hook claude")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            await hook.StandardInput.WriteAsync("""{"hook_event_name":"PermissionRequest","session_id":"p1","cwd":"C:\\dev\\api","tool_name":"Bash","tool_input":{"command":"npm publish --access public"}}""");
            hook.StandardInput.Close();

            string? approvalFp = null, trayId = null;
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (approvalFp == null && await reader.ReadLineAsync(wait.Token) is { } line)
            {
                log.AppendLine("recebido -> " + (line.Length > 300 ? line[..300] + "…" : line));
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var type = doc.RootElement.GetProperty("type").GetString();
                if (type == "approval")
                    approvalFp = doc.RootElement.GetProperty("fingerprint").GetString();
                else if (type == "tray" && doc.RootElement.GetProperty("items").GetArrayLength() > 0)
                    trayId = doc.RootElement.GetProperty("items")[0].GetProperty("id").GetString();
            }
            await ssl.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"type\":\"decision\",\"fingerprint\":\"0000\",\"decision\":\"allow\"}\n"));
            await ssl.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"{{\"type\":\"decision\",\"fingerprint\":\"{approvalFp}\",\"decision\":\"allow\"}}\n"));
            log.AppendLine("hook respondeu -> " + await hook.StandardOutput.ReadToEndAsync());
            await hook.WaitForExitAsync();
            for (var i = 0; i < 2; i++) log.AppendLine("depois -> " + await reader.ReadLineAsync(wait.Token));
            await ssl.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"type\":\"open\",\"id\":\"naoexiste\"}\n"));
            await ssl.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"{{\"type\":\"open\",\"id\":\"{trayId}\"}}\n"));
            await Task.Delay(800);
            log.AppendLine("aberto pelo celular -> " + string.Join(", ", PhoneOpened));
            ssl.Dispose();
        }
        catch (Exception ex) { log.AppendLine("ERRO: " + ex); }
        finally
        {
            _settings.PhoneLink = false;
            ApplyPhoneLink();
            Agents.Clear();
        }
        return log.ToString();
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
