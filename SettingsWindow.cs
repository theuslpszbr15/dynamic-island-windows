using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using DynamicIsland.Services;
using QRCoder;

namespace DynamicIsland;

/// <summary>All of the island's settings in one window: a section list on the left, live changes on the right.</summary>
public sealed class SettingsWindow : DarkWindow
{
    static string ReleasesUrl => Services.Updater.RepositoryUrl + "/releases/latest";
    static string ApkUrl => ReleasesUrl + "/download/DynamicIsland-Android.apk";

    static readonly (string Id, string Glyph, string Title)[] Sections =
    [
        ("general", "\uE713", "Geral"),
        ("look", "\uE790", "Aparência"),
        ("island", "\uE8A1", "Na ilha"),
        ("agents", "\uE99A", "Agentes"),
        ("phone", "\uE8EA", "Celular"),
        ("tray", "\uE718", "Bandeja de arquivos"),
        ("updates", "\uE895", "Atualizações e sobre"),
    ];

    readonly App _app;
    bool _downloadQr;

    IslandSettings S => _app.Settings;

    public SettingsWindow(App app) : base("Configurações — Dynamic Island", "Configurações", Sections)
    {
        _app = app;
        if (_app.Island.Phone is { } link) link.PhonesChanged += OnPhones;
        _app.Island.Discord.StatusChanged += OnDiscordStatus;
        Closed += (_, _) =>
        {
            if (_app.Island.Phone is { } l) l.PhonesChanged -= OnPhones;
            _app.Island.Discord.StatusChanged -= OnDiscordStatus;
        };
        Render();
    }

    void OnPhones()
    {
        if (_page == "phone") Render();
    }

    void OnDiscordStatus()
    {
        if (_page == "island") Render();
    }

    public void ShowDownloadQr()
    {
        _downloadQr = true;
        Navigate("phone");
    }

    protected override void Fill(string section, Panel page)
    {
        switch (section)
        {
            case "general": General(page); break;
            case "look": Look(page); break;
            case "island": OnIsland(page); break;
            case "agents": Agents(page); break;
            case "phone": Phone(page); break;
            case "tray": Tray(page); break;
            case "updates": Updates(page); break;
        }
    }

    // ───────────────────────── pages ─────────────────────────

    void General(Panel page)
    {
        var hide = new Button { Content = "Esconder por 1 hora", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 12, 0) };
        hide.Click += (_, _) => { _app.HideFor(TimeSpan.FromHours(1)); Render(); };
        page.Children.Add(Card("Ilha",
            Toggle("Esconder a ilha", $"Some da tela, inclusive os avisos. Também com {Hotkeys.Keys(S, "hide")} ou clicando no ícone da bandeja.",
                _app.IslandHidden, _ => _app.ToggleHidden(), hide),
            Choice("Formato", "Pílula flutuando abaixo do topo ou colada na borda, como um notch.",
                [(IslandShape.Pill, "Pílula flutuante"), (IslandShape.Notch, "Notch no topo")], S.Shape, _app.SetShape),
            Toggle("Recolher quando ociosa", "Sem nada acontecendo, vira uma barrinha fina no topo e deixa as abas clicáveis.",
                S.DiscreetIdle, _ => _app.ToggleDiscreet()),
            Choice("Monitor", "Em qual tela a ilha fica.", App.MonitorChoices().Select(m => (m.Id, m.Label)).ToArray(),
                S.Monitor, _app.SetMonitor)));

        var installed = Installer.IsInstalled;
        page.Children.Add(Card("Sistema",
            Toggle("Iniciar com o Windows", installed ? "Abre sozinha quando você entra no Windows."
                    : "Instale a ilha primeiro (em Atualizações e sobre) para ela iniciar com o Windows.",
                StartupRegistration.IsEnabled, _ => _app.ToggleStartup(), enabled: installed),
            Choice("Duração padrão do timer", "Usada quando você inicia o timer sem escolher.",
                [(5, "5 minutos"), (15, "15 minutos"), (25, "25 minutos (pomodoro)"), (45, "45 minutos"), (60, "1 hora")],
                S.TimerMinutes, m => { S.TimerMinutes = m; S.Save(); })));

        // Click a shortcut and press the new keys (Ctrl, Alt or Win + a letter, digit or F-key); Backspace restores
        // the default. Keys another app already owns are flagged instead of failing silently.
        var keys = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        keys.ColumnDefinitions.Add(new ColumnDefinition());
        var conflicts = _app.Island.HotkeyConflicts;
        for (var i = 0; i < Hotkeys.All.Length; i++)
        {
            var action = Hotkeys.All[i];
            var current = Hotkeys.Keys(S, action.Key);
            var taken = conflicts.Contains(current);
            keys.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            var box = new TextBox
            {
                Text = current, IsReadOnly = true, IsReadOnlyCaretVisible = false, Cursor = Cursors.Hand,
                Width = 136, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, Padding = new Thickness(8, 3, 8, 3),
                Background = Hover, Foreground = taken ? Brushes.Orange : Brushes.White, BorderThickness = new Thickness(1),
                BorderBrush = taken ? Brushes.Orange : Line, ToolTip = "Clique e pressione as novas teclas. Backspace volta ao padrão.",
            };
            box.GotKeyboardFocus += (_, _) => { box.Text = "pressione…"; box.BorderBrush = Accent; };
            box.LostKeyboardFocus += (_, _) => { box.Text = Hotkeys.Keys(S, action.Key); box.BorderBrush = taken ? Brushes.Orange : Line; };
            box.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
                string? chosen = key is Key.Back or Key.Delete ? action.Default : Hotkeys.Format(Keyboard.Modifiers, key);
                if (chosen == null) return; // only modifiers so far, or a key that can't be a shortcut
                if (Hotkeys.All.Any(other => other != action && Hotkeys.Keys(S, other.Key) == chosen))
                {
                    box.Text = "já usado aqui";
                    return;
                }
                if (chosen == action.Default) S.Hotkeys.Remove(action.Key);
                else S.Hotkeys[action.Key] = chosen;
                S.Save();
                _app.Island.RegisterHotkeys();
                Keyboard.ClearFocus();
                Dispatcher.BeginInvoke(new Action(Render)); // shows the new keys and whether they are free
            };
            var what = new TextBlock
            {
                Text = taken ? $"{action.Label} · em uso por outro app, escolha outra" : action.Label,
                VerticalAlignment = VerticalAlignment.Center, Foreground = taken ? Brushes.Orange : Muted,
            };
            Grid.SetRow(box, i);
            Grid.SetRow(what, i);
            Grid.SetColumn(what, 1);
            keys.Children.Add(box);
            keys.Children.Add(what);
        }
        page.Children.Add(Card("Atalhos de teclado", keys));
    }

    void Look(Panel page)
    {
        var themes = new WrapPanel();
        foreach (var (id, name, color) in IslandThemes.All)
        {
            var selected = S.Theme == id;
            var swatch = new Border
            {
                Width = 150, Height = 74, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 10, 10),
                BorderThickness = new Thickness(selected ? 2.5 : 1), BorderBrush = selected ? Accent : Line, Cursor = Cursors.Hand,
                Background = id == IslandThemes.Glass
                    ? new LinearGradientBrush(Color.FromRgb(0x5B, 0x6E, 0x8C), Color.FromRgb(0x2A, 0x2F, 0x3A), 45)
                    : new SolidColorBrush(color),
                ToolTip = name,
            };
            var light = id == IslandThemes.Light;
            var inner = new Grid();
            inner.Children.Add(new Border
            {
                Width = 62, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 12, 0, 0),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
                Background = light ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
            });
            inner.Children.Add(new TextBlock
            {
                Text = name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10),
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = light ? Brushes.Black : Brushes.White,
            });
            swatch.Child = inner;
            swatch.MouseLeftButtonUp += (_, _) => { _app.SetTheme(id); Render(); };
            themes.Children.Add(swatch);
        }
        page.Children.Add(Card("Tema", Note("A cor da ilha. Vidro desfoca o que está atrás dela (e some de prints e do compartilhamento de tela)."), themes));

        var mascots = new WrapPanel();
        foreach (var (species, label) in App.Mascots)
        {
            var selected = S.Mascot == species;
            var tile = new Border
            {
                Width = 128, Height = 128, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 10, 10),
                Background = CardBg, BorderThickness = new Thickness(selected ? 2.5 : 1), BorderBrush = selected ? Accent : Line,
                Cursor = Cursors.Hand,
            };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (species == PetSpecies.None)
                stack.Children.Add(new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 34, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 12) });
            else
                stack.Children.Add(new Viewbox { Width = 62, Height = 62, Child = new PetView { FixedSpecies = species, BaseMood = selected ? PetView.Mood.Happy : PetView.Mood.Idle } });
            stack.Children.Add(new TextBlock { Text = label, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 6, 6, 0) });
            tile.Child = stack;
            tile.MouseLeftButtonUp += (_, _) => { _app.SetMascot(species); Render(); };
            mascots.Children.Add(tile);
        }
        page.Children.Add(Card("Mascote", Note("Ele segue o mouse com os olhos, dança com a música e muda de cor com os agentes."), mascots,
            Choice("Roupinha", "No modo automático ele se veste sozinho no Natal, nas festas juninas e no Ano-Novo.",
                App.Outfits.Select(o => (o.Outfit, o.Label)).ToArray(), S.Outfit, o => { _app.SetOutfit(o); Render(); }),
            Toggle("Mascote na área de trabalho", "Um mascote solto na tela: arraste para onde quiser; ele corre até a ilha quando um agente chama.",
                S.DesktopPet, _ => _app.ToggleDesktopPet())));
    }

    void OnIsland(Panel page)
    {
        page.Children.Add(Card("O que aparece na ilha",
            Toggle("Notificações do Windows", "WhatsApp, Teams, e-mail… aparecem na ilha com o ícone do app.",
                S.ShowNotifications, _ => _app.ToggleNotifications()),
            Toggle("Letra da música", "A linha que está sendo cantada, do LRCLIB (precisa de um player que informe a posição, como o app do Spotify).",
                S.ShowLyrics, _ => _app.ToggleLyrics()),
            Toggle("O que foi copiado", "Texto, imagem ou arquivos copiados aparecem por um instante. Gerenciadores de senha são ignorados.",
                S.ShowClipboard, _ => _app.ToggleClipboard()),
            Toggle("Caps Lock e Num Lock", "Avisa quando liga ou desliga.", S.ShowLockKeys, _ => _app.ToggleLockKeys()),
            Toggle("Vídeos", "YouTube, Netflix, Prime Video, Twitch… Desligado: vídeo não abre aviso nem deixa a pílula aberta.",
                !S.QuietVideos, _ => _app.ToggleQuietVideos())));
        page.Children.Add(DiscordCard());
    }

    /// <summary>Who joins the Discord call: the user's own Discord app (id + secret, kept with DPAPI), then one consent in
    /// Discord. Nothing goes through a server; the island talks to the Discord app on this PC.</summary>
    Border DiscordCard()
    {
        var discord = _app.Island.Discord;
        var calls = Toggle("Quem entra na call", "Mostra a foto e o nome de quem entra na call do Discord em que você está.",
            S.DiscordCalls, _ => _app.ToggleDiscord());
        var leaves = Toggle("Avisar quando alguém sai", "Uma pílula pequena com a foto de quem saiu.",
            S.DiscordLeaves, _ => _app.ToggleDiscordLeaves(), enabled: S.DiscordCalls);

        var green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
        var orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        var state = Note(!S.DiscordCalls ? "Desligado." : (discord.Authenticated ? "✓ " : "") + discord.Status);
        state.Foreground = !S.DiscordCalls ? Muted : discord.Authenticated ? green : orange;

        TextBox Field(string text, string tip) => new()
        {
            Text = text, Width = 210, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5,
            Padding = new Thickness(8, 5, 8, 5), Background = Hover, Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderThickness = new Thickness(1), BorderBrush = Line, ToolTip = tip,
        };
        var id = Field(discord.ClientId, "Developer Portal › seu app › OAuth2 › Client ID");
        var secret = new PasswordBox
        {
            Width = 210, FontSize = 12.5, Padding = new Thickness(8, 5, 8, 5), Background = Hover, Foreground = Brushes.White,
            CaretBrush = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Line,
            ToolTip = "Developer Portal › seu app › OAuth2 › Client Secret (fica criptografado neste PC)",
        };
        var save = new Button { Content = "Salvar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            if (!discord.SetApp(id.Text, secret.Password))
            {
                state.Text = "Confira: o Client ID tem só números, e o Client Secret vem da mesma página.";
                state.Foreground = orange;
                return;
            }
            Render();
        };
        var appRow = new StackPanel { Orientation = Orientation.Horizontal };
        appRow.Children.Add(id);
        var secretRow = new StackPanel { Orientation = Orientation.Horizontal };
        secretRow.Children.Add(secret);
        secretRow.Children.Add(save);

        var portal = new Button { Content = "Abrir Developer Portal", Style = (Style)Resources["Action"] };
        portal.Click += (_, _) => Open("https://discord.com/developers/applications");
        var connect = new Button
        {
            Content = discord.HasToken ? "Conectar de novo" : "Conectar", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = S.DiscordCalls && discord.HasApp && !discord.Authenticated,
        };
        connect.Click += (_, _) => discord.Authorize();
        var forget = new Button { Content = "Esquecer app", Style = (Style)Resources["Action"], IsEnabled = discord.HasApp };
        forget.Click += (_, _) => { discord.Forget(); Render(); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(connect);
        actions.Children.Add(forget);

        return Card("Discord",
            Note("A ilha conversa com o Discord aberto neste PC, sem servidor no meio. Precisa de um app seu no Developer Portal " +
                 $"(uma vez só): crie o app, em OAuth2 adicione o redirect {Services.DiscordVoice.RedirectUri} e cole aqui o Client ID e o Client Secret. " +
                 "O Discord só libera isso para o dono do app e até 50 testers."),
            calls, leaves,
            Row("Client ID", null, appRow),
            Row("Client Secret", discord.HasApp ? "Salvo e criptografado neste PC. Cole de novo só para trocar." : "Fica criptografado neste PC.", secretRow),
            Row("Acesso", "Conectar abre um pedido de permissão no próprio Discord: é só aceitar.", actions),
            Row("Ajuda", "Onde criar o app e pegar o Client ID e o Client Secret.", portal),
            state);
    }

    void Agents(Panel page)
    {
        var copy = new Button { Content = "Copiar hooks", Style = (Style)Resources["Action"] };
        copy.Click += (_, _) => _app.CopyClaudeHooks();
        page.Children.Add(Card("Conexões",
            Toggle("GitHub Copilot (VS Code e CLI)", "Cria ~/.copilot/hooks/dynamic-island.json. Vale para chats novos.",
                App.CopilotConnected, _ => _app.ToggleCopilotHooks()),
            Row("Claude Code", "Copia os hooks para colar em ~/.claude/settings.json. Inclui o PermissionRequest: os pedidos de permissão aparecem na ilha (e no celular) com Permitir / Negar.", copy),
            Toggle("Pedido de permissão discreto", "O pedido aparece como uma pílula pequena, sem abrir a ilha. Passe o mouse ou clique nela para ver o comando e responder.",
                S.QuietApprovals, _ => _app.ToggleQuietApprovals()),
            Row("Qualquer script", "DynamicIsland.exe agent --name \"Build\" --state working --text \"dotnet build\"", null)));

        var week = AgentStats.LastWeek();
        var work = TimeSpan.FromSeconds(week.WorkSeconds);
        var open = new Button { Content = "Abrir resumo", Style = (Style)Resources["Action"] };
        open.Click += (_, _) => _app.ShowRecap();
        var numbers = new UniformGrid { Columns = 4, Margin = new Thickness(0, 6, 0, 6) };
        foreach (var (value, label) in new[]
                 {
                     (work.TotalHours >= 1 ? $"{(int)work.TotalHours}h {work.Minutes:00}" : $"{work.Minutes} min", "trabalhando"),
                     (week.Edits.ToString(), "edições"), (week.Commands.ToString(), "comandos"), (week.Sessions.ToString(), "sessões"),
                 })
        {
            var cell = new StackPanel();
            cell.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.Bold });
            cell.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 12.5 });
            numbers.Children.Add(cell);
        }
        page.Children.Add(Card("Semana passada", numbers, Row("Resumo da semana", "Toda segunda a ilha oferece o resumo; dá para salvar como imagem. Fica só no seu PC.", open)));
    }

    /// <summary>How the phone reaches the PC away from home (public relay, own relay or none), and whether the PC is in
    /// its room.</summary>
    Border RelayCard(IslandWindow island)
    {
        var mode = Choice("Repasse", "Público: funciona só escaneando o código. Próprio: um repasse seu no Cloudflare.",
            [(PhoneRelayMode.Public, "Público (padrão)"), (PhoneRelayMode.Own, "Usar meu próprio repasse"), (PhoneRelayMode.Off, "Desligado")],
            S.PhoneRelayMode, m => { _app.SetPhoneRelayMode(m); Render(); });
        var state = Note("");
        void State(string text, Brush color) { state.Text = text; state.Foreground = color; }
        var green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
        var orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        if (S.PhoneRelayMode == PhoneRelayMode.Off) State("Desligado: o celular só conecta na mesma rede.", Muted);
        else if (!S.PhoneLink || island.Phone is not { Running: true } running) State("Ligue a ligação com o celular para usar o repasse.", Muted);
        else if (running.RelayProblem is { } problem) State(problem, orange);
        else if (running.RelayConnected) State("✓ Conectado ao repasse: o celular alcança este PC de qualquer rede.", green);
        else if (S.EffectivePhoneRelay.Length == 0) State("Coloque o endereço do seu repasse abaixo.", Muted);
        else State("Conectando ao repasse… (sem internet" + (S.PhoneRelayMode == PhoneRelayMode.Own ? ", endereço ou chave errada?)" : "?)"), orange);

        const string about = "O repasse só passa adiante os dados já criptografados entre o celular e este PC: não vê o código de " +
                             "pareamento nem os comandos, e não consegue aprovar nada.";
        if (S.PhoneRelayMode != PhoneRelayMode.Own)
            return Card("Fora de casa (pela internet)",
                Note(about + (S.PhoneRelayMode == PhoneRelayMode.Public
                    ? " O público é grátis e tem limites (50 MB por dia, arquivos até 10 MB); para mais, use o seu próprio."
                    : "")),
                mode, state);
        return OwnRelayCard(island, mode, state, about);
    }

    /// <summary>The user's own relay: its address and its optional access key (the old way, before the public relay).</summary>
    Border OwnRelayCard(IslandWindow island, FrameworkElement mode, TextBlock state, string about)
    {
        var box = new TextBox
        {
            Text = S.PhoneRelay, Width = 270, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5,
            Padding = new Thickness(8, 5, 8, 5), Background = Hover, Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderThickness = new Thickness(1), BorderBrush = Line, ToolTip = "Ex.: wss://dynamic-island-relay.seu-usuario.workers.dev",
        };
        var status = state;
        void Show(string text, Brush color) { status.Text = text; status.Foreground = color; }

        // The relay's access key: masked, pasted in, kept with the pairing secrets (DPAPI), never shown in full.
        var key = new PasswordBox
        {
            Width = 186, FontSize = 12.5, Padding = new Thickness(8, 5, 8, 5), Background = Hover, Foreground = Brushes.White,
            CaretBrush = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Line,
            Password = island.Phone?.RelayKey ?? "", ToolTip = "A chave RELAY_KEY do seu repasse",
        };
        var paste = new Button { Content = "Colar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
        paste.Click += (_, _) => { try { if (Clipboard.ContainsText()) key.Password = Clipboard.GetText().Trim(); } catch { } };
        var saveKey = new Button { Content = "Salvar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
        void ApplyKey()
        {
            if (island.Phone is not { } phone)
            {
                Show("Ligue a ligação com o celular antes de salvar a chave.", Brushes.Orange);
                return;
            }
            if (!phone.SetRelayKey(key.Password))
            {
                Show("Chave inválida: 16 a 128 letras, números, - ou _.", Brushes.Orange);
                return;
            }
            Render();
        }
        saveKey.Click += (_, _) => ApplyKey();
        key.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; ApplyKey(); } };
        var keyEdit = new StackPanel { Orientation = Orientation.Horizontal };
        keyEdit.Children.Add(key);
        keyEdit.Children.Add(paste);
        keyEdit.Children.Add(saveKey);
        var hasKey = (island.Phone?.RelayKey.Length ?? 0) > 0;

        var save = new Button { Content = "Salvar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
        void Apply()
        {
            var text = box.Text.Trim();
            var relay = PhoneLink.NormalizeRelay(text);
            if (relay == null)
            {
                Show("Endereço inválido: use wss://seu-repasse.workers.dev (só o endereço, sem caminho nem porta).", Brushes.Orange);
                return;
            }
            _app.SetPhoneRelay(relay);
            Render();
        }
        save.Click += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Apply(); } };
        var edit = new StackPanel { Orientation = Orientation.Horizontal };
        edit.Children.Add(box);
        edit.Children.Add(save);
        return Card("Fora de casa (pela internet)",
            Note(about + " Seu próprio repasse no Cloudflare não tem os limites do público. Como publicar: pasta relay/ do projeto."),
            mode,
            Row("Endereço do repasse", "Ex.: wss://seu-repasse.workers.dev. O celular recebe o endereço sozinho.", edit),
            Row("Chave do repasse (opcional)", hasKey ? "Salva (protegida no Windows). O celular recebe junto com o endereço."
                                                      : "Só quem tem a chave usa o seu repasse. O celular recebe junto com o endereço.", keyEdit),
            status);
    }

    void Phone(Panel page)
    {
        var island = _app.Island;
        page.Children.Add(Card("Aprovar pelo celular",
            Note("Com o app Dynamic Island no Android, os pedidos de permissão chegam no celular: Negar direto da notificação, " +
                 "Permitir com a sua digital. O celular conversa direto com este PC pela rede Wi-Fi (TLS, código de pareamento), " +
                 "ou pelo repasse na web quando estiver fora de casa."),
            Toggle("Ligação com o celular", "Desligada, nada fica escutando na rede.", S.PhoneLink, _ => _app.TogglePhoneLink())));
        page.Children.Add(InstallCard());
        page.Children.Add(RelayCard(island));

        if (S.PhoneLink && island.Phone is { Running: true } link)
        {
            var url = link.PairingLink();
            var phones = link.ConnectedPhones;
            var qr = new Border
            {
                Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Margin = new Thickness(0, 0, 22, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Image { Source = Qr(url), Width = 210, Height = 210 },
            };
            var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            side.Children.Add(new TextBlock
            {
                Text = phones > 0 ? $"✓ {phones} celular conectado" : "Esperando o celular",
                FontSize = 17, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(phones > 0 ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0xFF, 0x9F, 0x0A)),
            });
            // Each phone, by path (rede local / internet) and since when: an unknown one over the internet stands out.
            foreach (var phone in link.Phones)
                side.Children.Add(new TextBlock
                {
                    Text = $"{(phone.ViaRelay ? "" : "")}  {phone.Device} · {(phone.ViaRelay ? "pela internet" : "rede local")} · desde {phone.Since:HH:mm}",
                    FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe UI Variable Text, Segoe UI"), FontSize = 13,
                    Foreground = phone.ViaRelay ? new SolidColorBrush(Color.FromRgb(0x64, 0xD2, 0xFF)) : Brushes.White,
                    Margin = new Thickness(0, 2, 0, 4), TextWrapping = TextWrapping.Wrap,
                });
            side.Children.Add(Note(PhoneLink.LocalAddress() is { } ip
                ? $"Este PC: {ip}, porta {link.Port}"
                : "Sem rede local: conecte o PC ao Wi-Fi."));
            side.Children.Add(Note("1. Instale o app no celular (Baixar no celular, acima).\n" +
                                   (link.Relay.Length > 0 ? "2. Qualquer rede serve: com o repasse ligado o celular alcança o PC pela internet.\n"
                                                          : "2. Celular e PC na mesma rede Wi-Fi.\n") +
                                   "3. No app, toque em Escanear e aponte para este código."));
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var copy = new Button { Content = "Copiar link", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 8) };
            copy.Click += (_, _) => { try { Clipboard.SetText(url); } catch { } };
            var again = new Button { Content = "Parear de novo", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 8) };
            again.Click += (_, _) =>
            {
                if (MessageBox.Show(this, "O celular pareado será desconectado e terá de escanear um código novo. Continuar?",
                        "Parear de novo", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                link.PairAgain();
                Render();
            };
            buttons.Children.Add(copy);
            buttons.Children.Add(again);
            var stop = new Button { Content = phones > 0 ? "Desconectar o celular" : "Parar de esperar", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 8) };
            stop.Click += (_, _) => { _app.TogglePhoneLink(); Render(); };
            buttons.Children.Add(stop);
            side.Children.Add(buttons);
            var pair = new DockPanel();
            DockPanel.SetDock(qr, Dock.Left);
            pair.Children.Add(qr);
            pair.Children.Add(side);
            page.Children.Add(Card("Parear", pair,
                Note("Se o celular não conectar: o Windows pode pedir para liberar a ilha no firewall (redes privadas), " +
                     "e redes de visitante/corporativas costumam isolar os aparelhos.")));
        }
        else if (S.PhoneLink)
        {
            page.Children.Add(Card("Parear", Note("A ligação não iniciou: " + (island.PhoneError ?? "erro desconhecido"))));
        }

        var download = new Button { Content = "Abrir no navegador", Style = (Style)Resources["Action"] };
        download.Click += (_, _) => Open(ReleasesUrl);
        var window = new Button { Content = "Abrir", Style = (Style)Resources["Action"] };
        window.Click += (_, _) => _app.ShowPhoneWindow("send");
        page.Children.Add(Card("No celular",
            Toggle("Bandeja de arquivos no celular", "O celular vê os itens fixados na bandeja da ilha e pode abri-los neste PC (pede sua digital).",
                S.PhoneTray, _ => _app.TogglePhoneTray()),
            Toggle("Arquivos e textos com o celular", "Solte arquivos ou texto no lado \"celular\" da ilha, use o botão no item da bandeja " +
                   "ou a janela Celular (menu da bandeja → Enviar pro celular…). Do celular, use Compartilhar → Mandar pro PC: a ilha pergunta antes, " +
                   "e tudo vai para Downloads\\Dynamic Island. Até 30 MB por arquivo (vídeo 60 MB), criptografado de ponta a ponta.",
                S.PhoneFiles, _ => _app.TogglePhoneFiles()),
            Row("Janela Celular", "Enviar arquivo, pasta, texto ou o que foi copiado, e ver o que foi recebido e enviado.", window),
            Row("Página de Releases", "Todas as versões do app e da ilha.", download)));
    }

    // Step one: get the APK onto the phone by scanning a QR code with its camera.
    Border InstallCard()
    {
        var toggle = new Button { Content = _downloadQr ? "Esconder QR code" : "Baixar no celular", Style = (Style)Resources["Action"] };
        toggle.Click += (_, _) => { _downloadQr = !_downloadQr; Render(); };
        var row = Row("Instalar o app no celular", "Android 11 ou mais novo. Escaneie um QR code com a câmera e o download do app começa.", toggle);
        if (!_downloadQr) return Card("App Android", row);

        var qr = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Margin = new Thickness(0, 6, 22, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Image { Source = Qr(ApkUrl), Width = 190, Height = 190 },
        };
        var steps = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        steps.Children.Add(new TextBlock { Text = "Aponte a câmera do celular", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        steps.Children.Add(Note("1. Abra a câmera (ou o Google Lens) e toque no link que aparecer.\n" +
                                "2. O navegador baixa DynamicIsland-Android.apk.\n" +
                                "3. Abra o arquivo baixado. Na primeira vez o Android pede para permitir instalações do navegador.\n" +
                                "4. Abra o app e pareie com o código de pareamento (abaixo)."));
        var link = new TextBox { Text = ApkUrl, IsReadOnly = true, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Background = Hover, Foreground = Muted, BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6) };
        steps.Children.Add(link);
        var both = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(qr, Dock.Left);
        both.Children.Add(qr);
        both.Children.Add(steps);
        return Card("App Android", row, both);
    }

    void Tray(Panel page)
    {
        var island = _app.Island;
        var list = new StackPanel();
        foreach (var item in island.Files.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var icon = new Image { Source = item.Icon, Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var remove = new Button { Content = "Remover", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += (_, _) => { island.RemoveTrayFile(item); Render(); };
            var open = new Button { Content = "Abrir", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
            open.Click += (_, _) => Open(item.Path);
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(open);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = item.Path, Foreground = Muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);
            list.Children.Add(row);
        }
        if (island.Files.Count == 0) list.Children.Add(Note("Nada fixado ainda. Arraste arquivos até a ilha ou use os botões abaixo."));

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var addFiles = new Button { Content = "Adicionar arquivos ou apps…", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 0) };
        addFiles.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Fixar na bandeja da ilha" };
            if (dialog.ShowDialog(this) == true) { island.AddTrayFiles(dialog.FileNames); Render(); }
        };
        var addFolder = new Button { Content = "Adicionar pasta…", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 0) };
        addFolder.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "Fixar pasta na bandeja da ilha" };
            if (dialog.ShowDialog(this) == true) { island.AddTrayFiles(dialog.FolderNames); Render(); }
        };
        buttons.Children.Add(addFiles);
        buttons.Children.Add(addFolder);
        page.Children.Add(Card("Itens fixados",
            Note(S.PhoneLink && S.PhoneTray
                ? "Estes itens aparecem no celular pareado, que pode abri-los neste PC."
                : "Arraste da ilha de volta para usar um arquivo. Com o celular ligado, eles também aparecem lá."),
            list, buttons));
    }

    void Updates(Panel page)
    {
        var check = new Button { Style = (Style)Resources["Action"] };
        if (_app.Update is { } update)
        {
            check.Content = $"Atualizar para {update.Version.ToString(3)}";
            check.Click += (_, _) => _app.InstallUpdate();
        }
        else
        {
            check.Content = "Procurar agora";
            check.Click += (_, _) => _app.CheckForUpdates(true);
        }
        page.Children.Add(Card("Atualizações",
            Row($"Versão {Updater.Current.ToString(3)}", _app.Update is { } u ? $"A versão {u.Version.ToString(3)} está disponível." : "Novas versões saem no GitHub.", check),
            Toggle("Avisar quando houver atualização", "Confere o GitHub ao abrir e duas vezes por dia. A atualização confere o SHA-256 do arquivo.",
                S.CheckUpdates, _ => _app.ToggleUpdates())));

        var install = new Button { Content = Installer.IsInstalled ? "Desinstalar" : "Instalar", Style = (Style)Resources["Action"] };
        install.Click += (_, _) => { if (Installer.IsInstalled) _app.Uninstall(); else _app.Install(); };
        var data = new Button { Content = "Abrir pasta", Style = (Style)Resources["Action"] };
        data.Click += (_, _) => Open(IslandSettings.Folder);
        var github = new Button { Content = "Abrir no GitHub", Style = (Style)Resources["Action"] };
        github.Click += (_, _) => Open(Services.Updater.RepositoryUrl);
        page.Children.Add(Card("Este computador",
            Row(Installer.IsInstalled ? "Instalada" : "Não instalada",
                Installer.IsInstalled ? Installer.InstallDir : "Instale para ganhar atalho no menu Iniciar e iniciar com o Windows.", install),
            Row("Dados e configurações", IslandSettings.Folder, data),
            Row("Código aberto", Services.Updater.RepositoryUrl[8..], github)));
    }

    internal static BitmapImage Qr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
