using System.Windows;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

// The demo: a 40-second tour of the island with made-up data (a song with lyrics, an agent at work, Jarvis listening,
// a message). Nothing is saved, sent to the phone or counted in the weekly recap; the real song comes back at the end.
public partial class IslandWindow
{
    bool _demo;
    MediaSnapshot? _demoSong, _demoRealMedia;

    public bool DemoRunning => _demo;

    /// <summary>A new install offers the tour once, a little after the island appears.</summary>
    async void OfferDemo()
    {
        if (!_settings.DemoPending || SnapshotMode) return;
        _settings.DemoPending = false;
        _settings.Save();
        await Task.Delay(2500);
        ShowInfo("\uE768", "Quer ver tudo que a ilha faz?", "Ver demo", glyphColor: JarvisColor, action: () => _ = RunDemoAsync(), seconds: 15);
    }

    public async Task RunDemoAsync()
    {
        if (_demo || !_ready) return;
        _demo = true;
        _demoRealMedia = _mediaSnap;
        var realGlow = _glow.Source;
        var agentKey = "Claude Code|demo";
        try
        {
            if (_userHidden) SetUserHidden(false);
            TryPeek(alert: false);
            SetExpanded(false);
            ShowInfo("\uE768", "Demonstração da ilha", "40 s", glyphColor: JarvisColor, seconds: 2.5);
            await Task.Delay(2800);

            // a song with synced lyrics
            _demoSong = new MediaSnapshot("Luzes da Cidade", "Banda Demo", DemoArt(), Color.FromRgb(0xFF, 0x6B, 0x8A), true,
                TimeSpan.FromSeconds(39), TimeSpan.FromSeconds(196), DateTimeOffset.Now, "Spotify");
            _lyricsKey = $"{_demoSong.Title}|{_demoSong.Artist}";
            _lyrics = LyricsService.Parse(DemoLyrics);
            OnMedia(_demoSong);
            _lyricsTimer?.Start();
            await Task.Delay(7000);

            OnVolume(_volumeLevel, _volumeMuted);
            await Task.Delay(2200);

            SetExpanded(true, PageMedia);
            await Task.Delay(5500);
            ShowPage(PageSystem, 1);
            await Task.Delay(3500);

            // an agent at work, shown here only (not on the phone, not in the stats)
            var session = new AgentSession(agentKey, "Claude Code", "meu-app");
            session.Update("working", "Editando Pagamento.cs");
            session.Record("edit", @"C:\dev\meu-app\Pagamento.cs", null);
            Agents.Insert(0, session);
            ShowPage(PageAgents, 1);
            await Task.Delay(4000);
            ShowPage(PageTimer, 1);
            await Task.Delay(3000);

            // Jarvis listening, then thinking
            var started = DateTime.Now;
            _glow.Source = () =>
            {
                var t = (DateTime.Now - started).TotalSeconds;
                var level = 0.45 + 0.35 * Math.Sin(t * 7) * Math.Sin(t * 2.3);
                return (Math.Max(0, level), 0.5 + 0.3 * Math.Sin(t * 5), 0.5 + 0.3 * Math.Cos(t * 6), 0.4 + 0.3 * Math.Sin(t * 9));
            };
            ShowPage(PageJarvis, 1);
            SetJarvisState("Ouvindo…");
            JarvisAsked.Text = "Você: abre o Bloco de Notas e escreve a lista de compras";
            await Task.Delay(3000);
            SetJarvisState("Pensando…");
            await Task.Delay(2200);
            SetJarvisState("Pronto");
            JarvisAsked.Text = "Você: abre o Bloco de Notas e escreve a lista de compras";
            JarvisAnswer.Text = "Pronto, abri o Bloco de Notas com a sua lista.";
            await Task.Delay(3000);

            SetExpanded(false);
            await Task.Delay(600);
            ShowCard(null, "Ana", "WhatsApp", "Viu a ilha nova? Ficou muito boa", seconds: 3.5);
            await Task.Delay(3800);
        }
        finally
        {
            _glow.Source = realGlow;
            if (JarvisState != "Pronto") SetJarvisState("Pronto");
            UpdateJarvisUi();
            if (Agents.FirstOrDefault(a => a.Key == agentKey) is { } demoAgent) Agents.Remove(demoAgent);
            SetExpanded(false);
            _demo = false;
            _demoSong = null;
            // the real song (or nothing) comes back without announcing itself as a new track
            var real = _demoRealMedia;
            _trackKey = real == null ? null : $"{real.Title}|{real.Artist}";
            _lyricsKey = null;
            OnMedia(real);
            _transient = Transient.None;
            Retarget();
            ShowInfo("\uE73E", "Pronto! Passe o mouse aqui", "Fim", glyphColor: JarvisColor, seconds: 5);
        }
    }
}
