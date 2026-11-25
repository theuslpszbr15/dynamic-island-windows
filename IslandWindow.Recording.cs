using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// --record <folder>: a scripted tour played with the island's real animations, saved as transparent 2x PNG frames
// (island\NNNNN.png, settings\NNNNN.png) with their times (frames.txt) and the start of each part (marks.txt).
public partial class IslandWindow
{
    public async Task RunRecordingAsync(string folder)
    {
        foreach (var sub in new[] { "island", "settings" }) Directory.CreateDirectory(System.IO.Path.Combine(folder, sub));
        _ready = true;
        PetView.SetDefaultOutfit(PetOutfit.None);
        var clock = Stopwatch.StartNew();
        var queue = new BlockingCollection<(string Sub, int Index, BitmapSource Frame)>(900);
        // DI_RECORD_SCALE: 2 is sharper, 1.5 keeps the capture near 25 frames a second
        var scale = double.TryParse(Environment.GetEnvironmentVariable("DI_RECORD_SCALE"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1.5;
        var writers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            foreach (var (sub, index, frame) in queue.GetConsumingEnumerable())
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame));
                using var file = File.Create(System.IO.Path.Combine(folder, sub, $"{index:00000}.png"));
                encoder.Save(file);
            }
        })).ToArray();
        var times = new StringBuilder();
        var marks = new StringBuilder();
        int islandFrames = 0, settingsFrames = 0;
        FrameworkElement? extra = null;

        void Capture()
        {
            var ms = clock.ElapsedMilliseconds;
            var cost = Stopwatch.StartNew();
            var island = new RenderTargetBitmap((int)(Root.ActualWidth * scale), (int)(Root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            island.Render(Root);
            island.Freeze();
            queue.Add(("island", islandFrames, island));
            var line = $"{islandFrames++}\t{ms}\t{cost.ElapsedMilliseconds}";
            if (extra is { ActualWidth: > 0 } panel)
            {
                var shot = new RenderTargetBitmap((int)(panel.ActualWidth * 1.5), (int)(panel.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                shot.Render(panel);
                shot.Freeze();
                queue.Add(("settings", settingsFrames, shot));
                line += $"\t{settingsFrames++}";
            }
            times.AppendLine(line);
        }

        void Mark(string name) => marks.AppendLine($"{name}\t{clock.ElapsedMilliseconds}");
        static Task D(int ms) => Task.Delay(ms);

        // on WPF's own frame clock (a 33 ms DispatcherTimer lands on the 15.6 ms system tick: ~16 frames a second)
        var next = 0L;
        void OnFrame(object? sender, EventArgs e)
        {
            if (clock.ElapsedMilliseconds < next) return;
            next = Math.Max(next + 33, clock.ElapsedMilliseconds - 10);
            Capture();
        }
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) => { }; // keeps the frame clock running when nothing animates
        await D(800);
        // drawing the layered window on screen costs as much as each capture: the frames don't need it
        if (Environment.GetEnvironmentVariable("DI_RECORD_SHOW") != "1") Hide();
        timer.Start();
        CompositionTarget.Rendering += OnFrame;
        try
        {
            Mark("clock");
            await D(1500);

            // a song arrives: the pill grows with the cover, then the lyrics run under it
            Mark("music");
            var song = new MediaSnapshot("Luzes da Cidade", "Banda Demo", DemoArt(), Color.FromRgb(0xFF, 0x6B, 0x8A), true,
                TimeSpan.FromSeconds(39), TimeSpan.FromSeconds(196), DateTimeOffset.Now, "Spotify");
            _lyricsKey = $"{song.Title}|{song.Artist}";
            _lyrics = LyricsService.Parse(DemoLyrics);
            OnMedia(song);
            _lyricsTimer?.Start();
            await D(5200);

            Mark("volume");
            foreach (var level in new[] { 0.42f, 0.5f, 0.58f, 0.66f, 0.74f })
            {
                OnVolume(level, false);
                await D(260);
            }
            await D(1300);
            _transient = Transient.None;
            Retarget();
            await D(700);

            Mark("expand");
            SetExpanded(true, PageMedia);
            await D(4200);

            Mark("pages");
            ShowPage(PageSystem, 1);
            await D(1800);
            SetWeather(new WeatherNow("Joinville", 24, 26, 78, 11, "Parcialmente nublado", 116, 19, 28));
            SetEvents([
                new(DateTime.Now.AddMinutes(5), DateTime.Now.AddMinutes(35), "Daily do time", "https://teams.microsoft.com/l/meetup-join/demo", "Outlook"),
                new(DateTime.Today.AddHours(16), DateTime.Today.AddHours(17), "Revisão do app", "https://meet.google.com/abc-defg-hij", "google"),
            ]);
            ShowPage(PageCalendar, 1);
            await D(2000);
            ShowPage(PageMedia, -1);
            await D(1300);
            SetExpanded(false);
            await D(1000);

            // an agent at work, then asking to push
            Mark("agent");
            OnAgent(new AgentMessage("Claude Code", "r1", "working", "Editando Pagamento.cs", @"C:\dev\meu-app", "edit", @"C:\dev\meu-app\Pagamento.cs"));
            await D(2600);
            SetExpanded(true, PageAgents);
            await D(2600);
            SetExpanded(false);
            await D(700);
            Mark("approval");
            OnAgent(new AgentMessage("Claude Code", "r1", "waiting", "Quer rodar um comando", @"C:\dev\meu-app",
                RequestId: "rec-approval", Command: "git push origin main"));
            await D(1800);
            OpenApproval();
            await D(2600);
            Answer("allow");
            await D(1600);
            Agents.Clear();
            _transient = Transient.None;
            Retarget();
            await D(800);

            // Jarvis: listening (the glow follows a made-up voice), thinking, then a long answer read out
            Mark("jarvis");
            var realGlow = _glow.Source;
            var started = DateTime.Now;
            _glow.Source = () =>
            {
                var t = (DateTime.Now - started).TotalSeconds;
                var level = 0.45 + 0.35 * Math.Sin(t * 7) * Math.Sin(t * 2.3);
                return (Math.Max(0, level), 0.5 + 0.3 * Math.Sin(t * 5), 0.5 + 0.3 * Math.Cos(t * 6), 0.4 + 0.3 * Math.Sin(t * 9));
            };
            JarvisHistory.Clear();
            JarvisHistory.Add((true, "o que você pode fazer?"));
            SetExpanded(true, PageJarvis);
            SetJarvisState("Ouvindo…");
            await D(2600);
            SetJarvisState("Pensando…");
            await D(2000);
            JarvisHistory.Add((false, "Posso abrir sites e apps, criar arquivos e pastas, escrever textos, controlar o volume e a música, " +
                "buscar na web, ver o clima, criar lembretes e timers, pôr tarefas na ilha, usar o mouse e o teclado por você e rodar " +
                "comandos do PowerShell. Também leio a tela, traduzo, mando coisas pro celular e aviso quando a internet cai."));
            SetJarvisState("Falando…");
            await D(400);
            var room = JarvisScroll.ScrollableHeight;
            for (var i = 0; i <= 90; i++)
            {
                var p = i / 90.0;
                JarvisScroll.ScrollToVerticalOffset(room * (p < 0.5 ? 2 * p * p : 1 - Math.Pow(-2 * p + 2, 2) / 2));
                await D(50);
            }
            await D(800);
            SetJarvisState("Pronto");
            await D(800);

            // Jarvis using the computer: the island grows and lists the steps as they happen
            Mark("computer");
            JarvisHistory.Add((true, "No Bloco de Notas, escreva a lista de compras"));
            _computer = new CancellationTokenSource();
            _computerSteps.Clear();
            _computerNow = "Olhando a tela…";
            SetJarvisState("Pensando…");
            Retarget();
            UpdateJarvisUi();
            await D(1400);
            foreach (var step in new[] { "Clicar em “Bloco de Notas”", "Clicar em “Sem título”", "Esperando sua permissão: Digitar “arroz, feijão, café”" })
            {
                _computerNow = step;
                UpdateJarvisUi();
                await D(step.StartsWith("Esperando") ? 1800 : 1100);
                _computerSteps.Add(step.StartsWith("Esperando") ? "Digitar “arroz, feijão, café”" : step);
                _computerNow = "Olhando a tela…";
                UpdateJarvisUi();
                await D(700);
            }
            _computer = null;
            _computerSteps.Clear();
            _computerNow = null;
            JarvisHistory.Add((false, "Pronto, escrevi a lista no Bloco de Notas."));
            SetJarvisState("Pronto");
            Retarget();
            UpdateJarvisUi();
            await D(2000);
            _glow.Source = realGlow;
            SetExpanded(false);
            await D(900);

            // the Appearance page picking colours, the island changing with it
            Mark("themes");
            var app = (App)Application.Current;
            var settings = new SettingsWindow(app) { Left = -4000, Top = 40, ShowActivated = false };
            settings.Show();
            settings.Navigate("look");
            await D(800);
            extra = (FrameworkElement)settings.Content;
            SetExpanded(true, PageMedia);
            await D(1200);
            foreach (var theme in new[] { "ocean", "forest", "plum", "cherry", IslandThemes.Light, "slate", "coffee", IslandThemes.Default })
            {
                app.SetTheme(theme);
                settings.Navigate("look");
                await D(800);
            }
            _settings.AuroraLook = true;
            ApplyShape();
            settings.Navigate("look");
            await D(2600);
            _settings.AuroraLook = false;
            ApplyShape();
            settings.Navigate("look");
            Mark("seasons");
            PetView.SetDefaultOutfit(PetOutfit.Auto);
            foreach (var day in new[] { new DateTime(2026, 10, 31), new DateTime(2026, 12, 20) })
            {
                PetView.CalendarDay = day;
                ApplyAurora();
                PetView.CalendarChanged();
                await D(2200);
            }
            extra = null;
            settings.Close();
            SetExpanded(false);
            await D(1500);
            PetView.CalendarDay = null;
            PetView.SetDefaultOutfit(PetOutfit.None);
            ApplyAurora();
            PetView.CalendarChanged();

            // the mascot: pats, a dizzy spin
            Mark("pet");
            await D(600);
            for (var i = 0; i < 3; i++)
            {
                CompactPet.Pat();
                await D(700);
            }
            CompactPet.React(PetView.Mood.Dizzy, 2);
            await D(2400);

            Mark("timer");
            StartTimer(25);
            _timerEnd = DateTime.Now + TimeSpan.FromSeconds(18 * 60 + 42);
            await D(3200);
            StopTimer();
            _transient = Transient.None;
            Retarget();
            await D(600);

            Mark("notify");
            ShowCard(null, "Ana", "WhatsApp", "Viu a ilha nova? Ficou muito boa", seconds: 3.5);
            await D(4300);
            Mark("end");
            await D(600);
        }
        finally
        {
            CompositionTarget.Rendering -= OnFrame;
            timer.Stop();
            queue.CompleteAdding();
            await Task.WhenAll(writers);
            File.WriteAllText(System.IO.Path.Combine(folder, "frames.txt"), times.ToString());
            File.WriteAllText(System.IO.Path.Combine(folder, "marks.txt"), marks.ToString());
        }
    }
}
