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
public sealed partial class SettingsWindow : DarkWindow
{
    static string ReleasesUrl => Services.Updater.RepositoryUrl + "/releases/latest";
    static string ApkUrl => ReleasesUrl + "/download/DynamicIsland-Android.apk";

    static readonly (string Id, string Glyph, string Title)[] Sections =
    [
        ("home", "\uE80F", "Início"),
        ("general", "\uE713", "Geral"),
        ("look", "\uE790", "Aparência"),
        ("island", "\uE8A1", "Na ilha"),
        ("media", "\uE767", "Mídia e áudio"),
        ("productivity", "\uE73A", "Produtividade"),
        ("jarvis", "\uE945", "Jarvis (IA)"),
        ("agents", "\uE99A", "Agentes"),
        ("phone", "\uE8EA", "Celular"),
        (TransferPage, "\uE724", "Enviar e receber"),
        ("tray", "\uE718", "Bandeja de arquivos"),
        ("updates", "\uE895", "Atualizações e sobre"),
    ];

    readonly App _app;
    bool _downloadQr;
    bool? _firewallOpen;
    bool _firewallBusy, _firewallChecked;
    string _usbShown = "";

    IslandSettings S => _app.Settings;
    protected override bool GlowOn => _app?.Settings.GlowLook ?? true; // runs from the base constructor, before _app is set

    public SettingsWindow(App app) : base("Configurações — Dynamic Island", "Configurações", Sections)
    {
        _app = app;
        ApplyGlow();
        SetUpTransfers();
        _app.Island.Discord.StatusChanged += OnDiscordStatus;
        Closed += (_, _) => _app.Island.Discord.StatusChanged -= OnDiscordStatus;
        Services.Jarvis.Changed += OnJarvisChanged;
        Closed += (_, _) => Services.Jarvis.Changed -= OnJarvisChanged;
        Activated += (_, _) => AdoptOwnRelay();
        // the USB bridge works in the background; show what it finds without waiting for a click
        var usbWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        usbWatch.Tick += (_, _) =>
        {
            var now = _app.Island.Usb?.Status ?? "";
            if (now == _usbShown) return;
            _usbShown = now;
            if (_page == "phone") Render();
        };
        usbWatch.Start();
        Closed += (_, _) => usbWatch.Stop();
        Render();
    }

    string? _relayAdopted;

    // The "Criar meu repasse" window leaves its address behind; coming back here puts it to use.
    void AdoptOwnRelay()
    {
        if (RelaySetup.TakeResult() is not { } relay) return;
        _app.SetPhoneRelay(relay);
        _app.SetPhoneRelayMode(PhoneRelayMode.Own);
        if (!S.PhoneLink) _app.TogglePhoneLink();
        _relayAdopted = relay;
        Navigate("phone");
    }

    FrameworkElement RelaySetupRows()
    {
        var create = new Button { Content = "Criar meu repasse", Style = (Style)Resources["Action"] };
        create.Click += (_, _) =>
        {
            try { RelaySetup.Start(); }
            catch (Exception ex) { DarkDialog.Ask(this, "Criar meu repasse", "Não consegui abrir o assistente: " + ex.Message, "OK"); }
        };
        var panel = new StackPanel();
        panel.Children.Add(Row("Criar meu repasse (grátis)",
            "Se o repasse público falhar, ou para não ter limites: a ilha publica um repasse só seu na Cloudflare. " +
            "Você só entra na sua conta pelo navegador." + (RelaySetup.HasNode() ? "" : " Precisa do Node.js: o assistente abre o download."),
            create));
        panel.Children.Add(Note(
            "Passo a passo:\n" +
            "1. Clique em Criar meu repasse. Abre uma janela azul do PowerShell que faz o resto.\n" +
            "2. Se faltar o Node.js, ela abre nodejs.org: instale a versão LTS e clique no botão de novo.\n" +
            "3. O navegador abre a Cloudflare: entre (ou crie uma conta grátis, só e-mail e senha) e clique em Allow.\n" +
            "4. Se a janela perguntar sobre um subdomínio workers.dev, digite y e escolha um nome qualquer.\n" +
            "5. Quando aparecer \"Pronto!\", volte aqui: a ilha passa a usar o seu repasse sozinha.\n" +
            "6. No celular, escaneie o código de pareamento de novo."));
        return panel;
    }

    // download progress arrives from a worker thread
    void OnJarvisChanged() => Dispatcher.BeginInvoke(() => { if (_page == "jarvis") Render(); });

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
            case "home": Home(page); break;
            case "general": General(page); break;
            case "look": Look(page); break;
            case "island": OnIsland(page); break;
            case "media": Media(page); break;
            case "productivity": Productivity(page); break;
            case "jarvis": Jarvis(page); break;
            case "agents": Agents(page); break;
            case "phone": Phone(page); break;
            case TransferPage: Transfers(page); break;
            case "tray": Tray(page); break;
            case "updates": Updates(page); break;
        }
    }

    // ───────────────────────── pages ─────────────────────────

    void Home(Panel page)
    {
        var green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
        var orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));

        var stats = new UniformGrid { Columns = 4, Margin = new Thickness(0, 4, 0, 4) };
        var pendingTasks = S.Tasks.Count(t => !t.Done);
        foreach (var (value, label, go) in new (string, string, string)[]
                 {
                     (App.CopilotConnected ? "Ativo" : "Desligado", "Copilot Hooks", "agents"),
                     (S.PhoneLink && (_app.Island.Phone?.ConnectedPhones ?? 0) > 0 ? "Conectado" : S.PhoneLink ? "Esperando" : "Desligado", "Celular Android", "phone"),
                     ($"{S.WaterGlasses}/{S.WaterGoal} copos", "Água hoje", "productivity"),
                     ($"{pendingTasks} pendentes", "Tarefas", "productivity"),
                 })
        {
            var card = new Border
            {
                Background = Hover, CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
            };
            var col = new StackPanel();
            col.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.SemiBold });
            col.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
            card.Child = col;
            card.MouseLeftButtonUp += (_, _) => Navigate(go);
            stats.Children.Add(card);
        }
        page.Children.Add(Card("Visão geral · Ctrl+K para buscar qualquer opção", stats));

        Button QuickBtn(string text, Action click)
        {
            var b = new Button { Content = text, Style = (Style)Resources["Action"] };
            b.Click += (_, _) => click();
            return b;
        }

        page.Children.Add(Card("O que configurar agora",
            Row("GitHub Copilot e Claude Code", App.CopilotConnected ? "✓ Copilot conectado na ilha." : "Conecte para ver passos, edições e aprovar comandos na ilha.",
                QuickBtn(App.CopilotConnected ? "Ver agentes" : "Conectar Copilot", () => { if (!App.CopilotConnected) _app.ToggleCopilotHooks(); else Navigate("agents"); Render(); })),
            Row("App Android e ilha no celular", S.PhoneLink ? "✓ Ligação ativa — escaneie o QR Code para parear." : "Aprove comandos com a digital, mande arquivos até 30 MB e veja calls do Discord.",
                QuickBtn(S.PhoneLink ? "Abrir QR Code" : "Ligar celular", () => { if (!S.PhoneLink) _app.TogglePhoneLink(); else Navigate("phone"); Render(); })),
            Row("Visual Aurora e Mascote vivo", S.AuroraLook ? "✓ Borda Aurora ligada." : "Experimente a borda iluminada Aurora ou faça carinho no Bit com Shift + clique.",
                QuickBtn(S.AuroraLook ? "Personalizar" : "Ligar Aurora", () => { S.AuroraLook = true; S.Save(); _app.Island.ApplyLook(); Render(); })),
            Row("Produtividade na ilha", "Tarefas rápidas, meta de água (💧) e textos rápidos (PIX, e-mail, links) com 1 clique.",
                QuickBtn("Abrir tarefas", () => Navigate("productivity"))),
            Row("Jarvis (sua IA pessoal)", "Pergunte pela ilha: segure " + Hotkeys.Keys(S, "jarvis") + " para falar ou toque para escrever.",
                QuickBtn("Abrir Jarvis", () => Navigate("jarvis"))),
            Row("Demonstração", "Um passeio de 40 segundos pelo que a ilha faz, com dados de exemplo. Nada é salvo.",
                QuickBtn("Ver demo", () => _ = _app.Island.RunDemoAsync()))));
    }

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
            Toggle("Esconder quando janela encostar no topo", "Se a janela focada estiver maximizada com abas no topo (ex.: Chrome/Edge), esconde a ilha até você passar o mouse ou sair dela.",
                S.SmartHideUnderTopBar, v => { S.SmartHideUnderTopBar = v; S.Save(); }),
            Toggle("Arrastar para outro monitor (Alt + arrastar)", "Segure Alt e arraste a ilha para levá-la para outro monitor; ela lembra onde você deixou.",
                S.DragMonitor, v => { S.DragMonitor = v; S.Save(); }),
            Choice("Monitor", "Em qual tela a ilha fica.", App.MonitorChoices().Select(m => (m.Id, m.Label)).ToArray(),
                S.Monitor, _app.SetMonitor),
            Choice("Em tela cheia", "Jogos, vídeos e apresentações. A ilha fica escondida e aparece quando você usa um atalho.",
                [(FullscreenMode.Alerts, "Atalhos e avisos importantes"), (FullscreenMode.Shortcuts, "Só atalhos"),
                    (FullscreenMode.Always, "Sempre mostrar")],
                S.Fullscreen, m => { S.Fullscreen = m; S.Save(); _app.Island.RefreshHidden(); })));
        page.Children.Add(GameCard());

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
                    : new SolidColorBrush(IslandThemes.Of(id)),
                ToolTip = name,
            };
            var light = IslandThemes.IsLight(id);
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
        page.Children.Add(Card("Tema", Note("A cor da ilha. Vidro desfoca o que está atrás dela (e some de prints e do compartilhamento de tela)."), themes,
            Toggle("Visual Aurora (borda de luz animada)", "Adiciona uma borda iluminada com gradiente animado e brilho suave ao redor da ilha.",
                S.AuroraLook, v => { S.AuroraLook = v; S.Save(); _app.Island.ApplyLook(); }),
            Toggle("Temas da época", "No Halloween (25 a 31/10), no Natal (dezembro), no Ano-Novo e nas festas juninas a ilha ganha uma borda com as cores da data, sozinha." +
                (Seasons.For(DateTime.Today) is { } now ? $" Agora: {now.Name}." : ""),
                S.SeasonalLook, v => { S.SeasonalLook = v; S.Save(); _app.Island.ApplyLook(); }),
            Toggle("Brilho laranja", "Um brilho suave atrás desta janela e em volta da ilha aberta. Desligue se preferir tudo liso.",
                S.GlowLook, v => { S.GlowLook = v; S.Save(); ApplyGlow(); _app.Island.ApplyLook(); })));

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
            tile.MouseLeftButtonUp += (_, _) =>
            {
                if (species == PetSpecies.Custom && PetView.CustomImage == null && !ImportMascot()) return;
                _app.SetMascot(species);
                Render();
            };
            mascots.Children.Add(tile);
        }
        var importMascot = new Button { Content = "Importar PNG…", Style = (Style)Resources["Action"] };
        importMascot.Click += (_, _) => { if (ImportMascot()) { _app.SetMascot(PetSpecies.Custom); Render(); } };
        page.Children.Add(Card("Mascote", Note("Ele segue o mouse com os olhos, dança com a música e muda de cor com os agentes."), mascots,
            Choice("Roupinha", "No modo automático ele se veste sozinho no Halloween, no Natal, nas festas juninas e no Ano-Novo.",
                App.Outfits.Select(o => (o.Outfit, o.Label)).ToArray(), S.Outfit, o => { _app.SetOutfit(o); Render(); }),
            Toggle("Animações ociosas (mosquinha e bolinha)", "Quando parado, o mascote persegue uma mosquinha brilhante ou brinca com uma bolinha. Shift + clique faz carinho (PatPat).",
                S.PlayfulPet, v => { S.PlayfulPet = v; S.Save(); _app.Island.ApplyLook(); }),
            Toggle("Mascote na área de trabalho", "Um mascote solto na tela: arraste para onde quiser; ele corre até a ilha quando um agente chama.",
                S.DesktopPet, _ => _app.ToggleDesktopPet()),
            Row("Meu mascote", "Um PNG seu (pixel art fica nítido; fundo transparente, até 512 × 512). Ele pula, dança, dorme e ganha carinho como os outros.",
                importMascot)));

        var colorBox = new TextBox
        {
            Text = S.CustomThemeColor, Width = 100, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13,
            Padding = new Thickness(8, 5, 8, 5), Background = Hover, Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderThickness = new Thickness(1), BorderBrush = Line, ToolTip = "Cor em hexadecimal, ex.: #1E2533",
        };
        var applyColor = new Button { Content = "Usar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
        applyColor.Click += (_, _) =>
        {
            if (IslandThemes.ParseHex(colorBox.Text) is not { } c) { colorBox.BorderBrush = Brushes.Orange; return; }
            IslandThemes.CustomColor = c;
            S.CustomThemeColor = IslandThemes.Hex(c);
            _app.SetTheme(IslandThemes.Custom);
            Render();
        };
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal };
        colorRow.Children.Add(colorBox);
        colorRow.Children.Add(applyColor);

        var export = new Button { Content = "Exportar…", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 0) };
        export.Click += (_, _) => ExportLook();
        var import = new Button { Content = "Importar…", Style = (Style)Resources["Action"] };
        import.Click += (_, _) => { if (ImportLook()) Render(); };
        var shareRow = new StackPanel { Orientation = Orientation.Horizontal };
        shareRow.Children.Add(export);
        shareRow.Children.Add(import);

        page.Children.Add(Card("Personalizar e compartilhar",
            Row("Cor própria", "Vira o tema Personalizado. Cores claras ganham texto escuro.", colorRow),
            Row("Visual da ilha em um arquivo", "Exporta tema, cor, Aurora, mascote (com o PNG) e roupinha num .ilha.json para mandar a quem quiser; " +
                "Importar aplica o de outra pessoa.", shareRow)));
    }

    /// <summary>Copies a PNG chosen by the person into the settings folder as the custom mascot.</summary>
    bool ImportMascot()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Escolha o PNG do seu mascote", Filter = "Imagem PNG|*.png" };
        if (dialog.ShowDialog(this) != true) return false;
        return UseMascotBytes(File.ReadAllBytes(dialog.FileName));
    }

    bool UseMascotBytes(byte[] png)
    {
        try
        {
            if (png.Length > 1024 * 1024) throw new InvalidDataException("PNG maior que 1 MB.");
            var temp = Path.Combine(IslandSettings.Folder, "mascot.new.png");
            Directory.CreateDirectory(IslandSettings.Folder);
            File.WriteAllBytes(temp, png);
            if (!PetView.LoadCustomImage(temp)) throw new InvalidDataException("Não é uma imagem válida (até 512 × 512).");
            File.Move(temp, IslandSettings.CustomMascotFile, true);
            PetView.LoadCustomImage(IslandSettings.CustomMascotFile);
            return true;
        }
        catch (Exception ex)
        {
            DarkDialog.Ask(this, "Meu mascote", "Não deu para usar esse arquivo: " + ex.Message, "OK");
            return false;
        }
    }

    const string LookFormat = "dynamic-island-look";

    void ExportLook()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Exportar visual da ilha", FileName = "meu-visual.ilha.json", Filter = "Visual da ilha|*.ilha.json" };
        if (dialog.ShowDialog(this) != true) return;
        var o = new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = LookFormat, ["v"] = 1, ["theme"] = S.Theme, ["color"] = S.CustomThemeColor, ["aurora"] = S.AuroraLook,
            ["mascot"] = S.Mascot.ToString(), ["outfit"] = S.Outfit.ToString(),
        };
        if (S.Mascot == PetSpecies.Custom && File.Exists(IslandSettings.CustomMascotFile))
            o["mascotPng"] = Convert.ToBase64String(File.ReadAllBytes(IslandSettings.CustomMascotFile));
        File.WriteAllText(dialog.FileName, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Applies a .ilha.json: only known fields, checked one by one (it may come from anyone).</summary>
    bool ImportLook()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Importar visual da ilha", Filter = "Visual da ilha|*.ilha.json;*.json" };
        if (dialog.ShowDialog(this) != true) return false;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 2 * 1024 * 1024) throw new InvalidDataException("Arquivo grande demais.");
            var o = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(dialog.FileName))?.AsObject()
                    ?? throw new InvalidDataException("Arquivo vazio.");
            if ((string?)o["format"] != LookFormat) throw new InvalidDataException("Não é um visual da Dynamic Island.");
            if (IslandThemes.ParseHex((string?)o["color"]) is { } c) { IslandThemes.CustomColor = c; S.CustomThemeColor = IslandThemes.Hex(c); }
            if (o["aurora"] is System.Text.Json.Nodes.JsonValue aurora && aurora.TryGetValue<bool>(out var on)) S.AuroraLook = on;
            if ((string?)o["mascotPng"] is { Length: > 0 and < 1_500_000 } png && !UseMascotBytes(Convert.FromBase64String(png))) return false;
            if (Enum.TryParse<PetOutfit>((string?)o["outfit"], out var outfit)) _app.SetOutfit(outfit);
            if (Enum.TryParse<PetSpecies>((string?)o["mascot"], out var mascot)) _app.SetMascot(mascot);
            _app.SetTheme(IslandThemes.Normalize((string?)o["theme"]));
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or FormatException or IOException or InvalidOperationException)
        {
            DarkDialog.Ask(this, "Importar visual", "Não deu para importar: " + ex.Message, "OK");
            return false;
        }
    }

    void Media(Panel page)
    {
        var presetChoice = Choice("Modo de reprodução",
            "Escolha quais fontes de áudio e vídeo podem assumir a ilha.",
            [
                (MediaFilterPreset.MusicOnly, "Apenas músicas (recomendado)"),
                (MediaFilterPreset.MusicAndWeb, "Músicas e navegadores web"),
                (MediaFilterPreset.All, "Todos os aplicativos"),
                (MediaFilterPreset.Custom, "Personalizado (escolher na mão)")
            ],
            S.MediaFilterPreset,
            p => { _app.SetMediaPreset(p); Render(); });

        var presetDesc = S.MediaFilterPreset switch
        {
            MediaFilterPreset.MusicOnly => "Apenas tocadores de música dedicados (Spotify, Apple Music, Deezer, VLC...). Chamadas de voz (Discord, Teams) e vídeos rápidos em navegadores (TikTok, Reels, Shorts) são ignorados automaticamente.",
            MediaFilterPreset.MusicAndWeb => "Tocadores de música e abas de navegadores web (YouTube, vídeos na web). Chamadas de voz (Discord, Teams) continuam bloqueadas.",
            MediaFilterPreset.All => "Qualquer aplicativo do sistema que emitir som. Chamadas de voz continuam protegidas.",
            MediaFilterPreset.Custom => "Configuração manual: você escolhe exatamente quais aplicativos têm permissão para aparecer na ilha.",
            _ => ""
        };

        var restore = new Button { Content = "Restaurar padrões", Style = (Style)Resources["Action"] };
        restore.Click += (_, _) => { _app.ResetMediaFilter(); Render(); };

        page.Children.Add(Card("Fontes de mídia",
            presetChoice,
            Note(presetDesc)));

        var apps = MediaFilter.GetAllKnownApps();
        var appRows = new List<UIElement>();

        foreach (var category in new[] { MediaAppCategory.Music, MediaAppCategory.Browser, MediaAppCategory.Call, MediaAppCategory.Other })
        {
            var categoryApps = apps.Where(a => a.Category == category).OrderBy(a => a.DisplayName).ToList();
            if (categoryApps.Count == 0) continue;

            var catTitle = category switch
            {
                MediaAppCategory.Music => "Tocadores de música",
                MediaAppCategory.Browser => "Navegadores de internet",
                MediaAppCategory.Call => "Comunicação e chamadas",
                _ => "Outros aplicativos"
            };

            appRows.Add(new TextBlock
            {
                Text = catTitle,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = Accent,
                Margin = new Thickness(0, 10, 0, 4)
            });

            foreach (var app in categoryApps)
            {
                var allowed = S.IsMediaAppAllowed(app.Key);
                var row = Toggle(app.DisplayName, $"Processo: {app.Key}", allowed, state =>
                {
                    _app.SetMediaAppAllowed(app.Key, state);
                    Render();
                });
                appRows.Add(row);
            }
        }

        page.Children.Add(Card("Escolher aplicativos na mão",
            [
                Note("Marque os aplicativos autorizados a exibir mídia na ilha. Alterar uma opção ativa o modo Personalizado automaticamente."),
                .. appRows,
                Row("Padrões", "Voltar para o modo 'Apenas músicas' e redefinir a lista.", restore)
            ]));

        page.Children.Add(Card("Opções de mídia",
            Toggle("Letra da música (LRCLIB)", "A linha que está sendo cantada (requer player compatível como Spotify).",
                S.ShowLyrics, _ => _app.ToggleLyrics()),
            Toggle("Vídeos (YouTube, Netflix…)", "Abre avisos ou mantém a pílula aberta durante a reprodução de vídeo.",
                !S.QuietVideos, _ => _app.ToggleQuietVideos())));
    }

    void OnIsland(Panel page)
    {
        var openMedia = new Button { Content = "Configurar…", Style = (Style)Resources["Action"] };
        openMedia.Click += (_, _) => Navigate("media");

        page.Children.Add(Card("O que aparece na ilha",
            Row("Filtro de mídia e som", "Controle quais aplicativos (músicas, navegadores, chamadas) aparecem na ilha.", openMedia),
            Toggle("Notificações do Windows", "WhatsApp, Teams, e-mail… aparecem na ilha com o ícone do app.",
                S.ShowNotifications, _ => _app.ToggleNotifications()),
            Toggle("Letra da música", "A linha que está sendo cantada, do LRCLIB (precisa de um player que informe a posição, como o app do Spotify).",
                S.ShowLyrics, _ => _app.ToggleLyrics()),
            Toggle("O que foi copiado", "Texto, imagem ou arquivos copiados aparecem por um instante. Gerenciadores de senha são ignorados.",
                S.ShowClipboard, _ => _app.ToggleClipboard()),
            Toggle("Print inteligente", "Depois de um print (Win+Shift+S, PrtScn ou Win+PrtScn) a ilha mostra a imagem com Salvar ou Copiar, " +
                   "Celular, Ler texto e Traduzir. Vale também para uma imagem copiada.",
                S.SmartShots, v => { S.SmartShots = v; S.Save(); _app.Island.ApplySmartShots(); }),
            Toggle("Caps Lock e Num Lock", "Avisa quando liga ou desliga.", S.ShowLockKeys, _ => _app.ToggleLockKeys()),
            Toggle("Vídeos", "YouTube, Netflix, Prime Video, Twitch… Desligado: vídeo não abre aviso nem deixa a pílula aberta.",
                !S.QuietVideos, _ => _app.ToggleQuietVideos())));
        page.Children.Add(DiscordCard());
    }

    void Productivity(Panel page)
    {
        var taskInput = new TextBox
        {
            Width = 260, FontSize = 13, Padding = new Thickness(8, 5, 8, 5),
            Background = Hover, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Line,
            ToolTip = "Ex.: Enviar proposta às 15h",
        };
        var dueInput = new TextBox
        {
            Width = 86, FontSize = 13, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(8, 0, 8, 0),
            Background = Hover, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Line,
            ToolTip = "Horário/prazo opcional (ex.: 15:00)",
        };
        var addTask = new Button { Content = "Adicionar", Style = (Style)Resources["Action"] };
        void AddCurrentTask()
        {
            var text = taskInput.Text.Trim();
            if (text.Length == 0) return;
            S.Tasks.Add(new TaskItem(Guid.NewGuid().ToString("N")[..8], text, false, string.IsNullOrWhiteSpace(dueInput.Text) ? null : dueInput.Text.Trim()));
            S.Save();
            _app.Island.UpdateTasksUi();
            Render();
        }
        addTask.Click += (_, _) => AddCurrentTask();
        taskInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AddCurrentTask(); } };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        addRow.Children.Add(taskInput);
        addRow.Children.Add(dueInput);
        addRow.Children.Add(addTask);

        var taskStack = new StackPanel();
        foreach (var task in S.Tasks.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var del = new Button { Content = "Remover", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
            del.Click += (_, _) => { S.Tasks.RemoveAll(t => t.Id == task.Id); S.Save(); _app.Island.UpdateTasksUi(); Render(); };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            var chk = new CheckBox
            {
                IsChecked = task.Done, Content = string.IsNullOrEmpty(task.Due) ? task.Text : $"{task.Text}  ·  {task.Due}",
                Foreground = task.Done ? Muted : Brushes.White, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
            };
            chk.Click += (_, _) =>
            {
                var idx = S.Tasks.FindIndex(t => t.Id == task.Id);
                if (idx >= 0) { S.Tasks[idx] = task with { Done = chk.IsChecked == true }; S.Save(); _app.Island.UpdateTasksUi(); Render(); }
            };
            row.Children.Add(chk);
            taskStack.Children.Add(row);
        }
        if (S.Tasks.Count == 0) taskStack.Children.Add(Note("Nenhuma tarefa ainda. Elas aparecem na página \"Tarefas e foco\" da ilha (" + Hotkeys.Keys(S, "tasks") + ")."));
        page.Children.Add(Card("Tarefas e lembretes na ilha", Note("Marque como concluída direto no entalho ou aqui. Atalho rápido: " + Hotkeys.Keys(S, "tasks")), addRow, taskStack));

        var plusWater = new Button { Content = "+1 copo agora", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 0) };
        plusWater.Click += (_, _) => { _app.Island.AddWater(1); Render(); };
        var resetWater = new Button { Content = "Zerar hoje", Style = (Style)Resources["Action"] };
        resetWater.Click += (_, _) => { S.WaterGlasses = 0; S.Save(); _app.Island.UpdateTasksUi(); Render(); };
        var waterBtns = new StackPanel { Orientation = Orientation.Horizontal };
        waterBtns.Children.Add(plusWater);
        waterBtns.Children.Add(resetWater);
        page.Children.Add(Card("Hidratação e bem-estar (💧)",
            Row($"Hoje: {S.WaterGlasses} de {S.WaterGoal} copos", "Acompanhe e some copos de água direto pela ilha.", waterBtns),
            Choice("Meta diária de água", "Quantos copos por dia você quer beber.",
                [(6, "6 copos"), (8, "8 copos (recomendado)"), (10, "10 copos"), (12, "12 copos")],
                S.WaterGoal, g => { S.WaterGoal = g; S.Save(); _app.Island.UpdateTasksUi(); Render(); }),
            Toggle("Lembrar de beber água a cada 1 hora", "Mostra uma pílula discreta na ilha com botão +1 copo.",
                S.WaterReminder, v => { S.WaterReminder = v; S.Save(); })));

        var noteBox = new TextBox
        {
            Text = S.QuickNote, AcceptsReturn = true, Height = 64, TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6), Background = Hover, Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderThickness = new Thickness(1), BorderBrush = Line, Margin = new Thickness(0, 0, 0, 8),
        };
        var saveNote = new Button { Content = "Salvar nota", Style = (Style)Resources["Action"], HorizontalAlignment = HorizontalAlignment.Left };
        saveNote.Click += (_, _) => { S.QuickNote = noteBox.Text.Trim(); S.Save(); _app.Island.UpdateTasksUi(); Render(); };

        var snipLabel = new TextBox { Width = 120, FontSize = 13, Padding = new Thickness(8, 5, 8, 5), Background = Hover, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Line, ToolTip = "Nome curto (ex.: PIX, Email, GitHub)" };
        var snipVal = new TextBox { Width = 240, FontSize = 13, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(8, 0, 8, 0), Background = Hover, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Line, ToolTip = "Valor que será copiado com 1 clique" };
        var addSnip = new Button { Content = "Adicionar atalho", Style = (Style)Resources["Action"] };
        addSnip.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(snipLabel.Text) || string.IsNullOrWhiteSpace(snipVal.Text)) return;
            S.Snippets.Add(new QuickSnippet(snipLabel.Text.Trim(), snipVal.Text.Trim()));
            S.Save();
            _app.Island.UpdateTasksUi();
            Render();
        };
        var snipAddRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        snipAddRow.Children.Add(snipLabel);
        snipAddRow.Children.Add(snipVal);
        snipAddRow.Children.Add(addSnip);

        var snipList = new StackPanel();
        foreach (var snip in S.Snippets.ToList())
        {
            var r = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var rm = new Button { Content = "Remover", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
            rm.Click += (_, _) => { S.Snippets.Remove(snip); S.Save(); _app.Island.UpdateTasksUi(); Render(); };
            var cp = new Button { Content = "Copiar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
            cp.Click += (_, _) => { try { Clipboard.SetText(snip.Value); } catch { } };
            DockPanel.SetDock(rm, Dock.Right);
            DockPanel.SetDock(cp, Dock.Right);
            r.Children.Add(rm);
            r.Children.Add(cp);
            r.Children.Add(new TextBlock { Text = $"{snip.Label}  →  {snip.Value}", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            snipList.Children.Add(r);
        }
        page.Children.Add(Card("Nota rápida e Textos de 1 clique (PIX, links…)",
            Note("Sua nota fica visível na página Tarefas e foco da ilha. Os botões abaixo copiam seu PIX, e-mail ou links com 1 clique direto na ilha."),
            noteBox, saveNote, snipAddRow, snipList));

        var (focusToday, focusWeek) = _app.Island.FocusTotals();
        static string Min(int m) => m >= 60 ? $"{m / 60}h {m % 60:00}" : $"{m} min";
        page.Children.Add(Card("Modo foco",
            Toggle("Modo foco no timer", "Enquanto o timer (Pomodoro) corre, as notificações do Windows esperam e os minutos contam como foco. " +
                   "No fim, a ilha mostra quanto você focou no dia e na semana.",
                S.FocusMode, v => { S.FocusMode = v; S.Save(); }),
            Row($"Hoje: {Min(focusToday)} · últimos 7 dias: {Min(focusWeek)}", "Conta o tempo dos timers que você termina ou para.", null)));

        page.Children.Add(AgendaCard());
    }

    Border AgendaCard()
    {
        var feedBox = new TextBox
        {
            Width = 380, FontSize = 12.5, Padding = new Thickness(8, 5, 8, 5), Background = Hover, Foreground = Brushes.White,
            CaretBrush = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Line,
            ToolTip = "O endereço secreto no formato iCal (termina em .ics)",
        };
        var add = new Button { Content = "Adicionar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
        var problem = Note("");
        problem.Foreground = Brushes.Orange;
        problem.Visibility = Visibility.Collapsed;
        add.Click += (_, _) =>
        {
            if (CalendarService.NormalizeFeed(feedBox.Text) is not { } feed)
            {
                problem.Text = "Link inválido: cole o endereço que começa com https:// ou webcal://.";
                problem.Visibility = Visibility.Visible;
                return;
            }
            if (!S.CalendarFeeds.Contains(feed)) S.CalendarFeeds.Add(feed);
            S.Save();
            _app.Island.ReloadAgenda();
            Render();
        };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal };
        addRow.Children.Add(feedBox);
        addRow.Children.Add(add);

        var list = new StackPanel();
        foreach (var feed in S.CalendarFeeds.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var remove = new Button { Content = "Remover", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += (_, _) => { S.CalendarFeeds.Remove(feed); S.Save(); _app.Island.ReloadAgenda(); Render(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            // the link is a secret: only where it points
            row.Children.Add(new TextBlock { Text = $"{new Uri(feed).Host} · link secreto salvo", VerticalAlignment = VerticalAlignment.Center });
            list.Children.Add(row);
        }

        return Card("Agenda (reunião em 5 min)",
            Note("A ilha avisa 10, 5 e 1 minuto antes de cada reunião, com o botão Entrar (Teams, Meet, Zoom ou Webex), " +
                 "e mostra as próximas na página Agenda. Funciona com o link secreto iCal:\n" +
                 "• Google Agenda: calendar.google.com › Configurações › seu calendário › \"Endereço secreto no formato iCal\".\n" +
                 "• Outlook na web: Configurações › Calendário › Calendários compartilhados › Publicar um calendário › link ICS.\n" +
                 "O link fica só neste PC."),
            Toggle("Outlook deste PC", "Lê a agenda do Outlook clássico enquanto ele está aberto (o Outlook novo não deixa; use o link ICS).",
                S.OutlookCalendar, v => { S.OutlookCalendar = v; S.Save(); _app.Island.ReloadAgenda(); }),
            Row("Adicionar link iCal", null, addRow), problem, list);
    }

    void Jarvis(Panel page)
    {
        var keys = Hotkeys.Keys(S, "jarvis");
        UIElement Engine(Services.Jarvis.Engine engine, string what)
        {
            var folder = Services.Jarvis.Folder(engine);
            if (folder != null)
            {
                var ready = Note("✓ Pronto" + (Services.Jarvis.FromJarvisApp(engine) ? " (do app JARVIS)" : ""));
                ready.Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                return Row(Services.Jarvis.Label(engine), what, ready);
            }
            var busy = Services.Jarvis.Progress.TryGetValue(engine, out var progress);
            var get = new Button { Content = busy ? progress : $"Baixar ({Services.Jarvis.Size(engine)})", Style = (Style)Resources["Action"], IsEnabled = !busy };
            get.Click += async (_, _) =>
            {
                get.IsEnabled = false;
                var error = await Services.Jarvis.InstallAsync(engine);
                if (error != null) DarkDialog.Ask(this, "Jarvis", error, "OK", null);
                Render();
            };
            return Row(Services.Jarvis.Label(engine), what, get);
        }

        var open = new Button { Content = "Abrir o Jarvis", Style = (Style)Resources["Action"] };
        open.Click += (_, _) => _app.Island.OpenJarvis();

        page.Children.Add(Card("Jarvis",
            Note("O seu assistente (github.com/theuslpszbr15/JARVIS) dentro da ilha: segure " + keys + " e fale, ou toque no atalho para " +
                 "escrever. A resposta aparece na ilha e é falada. Com a chave da Groq ele executa tarefas; sem ela, roda só neste PC."),
            Choice("Atalho " + keys, "Segurar: fala. Tocar: abre para escrever.",
                [("hold", "Segurar para falar"), ("text", "Só escrever")],
                S.JarvisVoiceMode, m => { S.JarvisVoiceMode = m; S.Save(); }),
            Choice("Microfone", "Qual microfone o Jarvis e o ditado escutam. Se ele só entende \"E aí!\", o microfone está errado.",
                [("", "Padrão do Windows"), .. JarvisMic.Devices().Select(d => (d, d))],
                S.JarvisMic, m => { S.JarvisMic = m; S.Save(); }),
            Toggle("Falar as respostas", "O Jarvis lê a resposta em voz alta (voz baixada ou a voz em português do Windows).", S.JarvisSpeak, v => { S.JarvisSpeak = v; S.Save(); }),
            Toggle("\"Ei Jarvis\"", (_app.Island.WakeProblem ?? "Diga \"ei Jarvis\" e faça o pedido; ele envia quando você para de falar.") +
                " Usa o reconhecimento de voz do Windows, sem internet. Enquanto ligado, o Windows mostra o microfone em uso.",
                S.WakeWord, v => { S.WakeWord = v; S.Save(); _app.Island.ApplyWakeWord(); }),
            Choice("Brilho da voz", "As cores que sobem da borda da ilha quando você fala e quando o Jarvis responde.",
                [.. VoiceGlow.Variants, ("off", "Desligado")],
                S.JarvisGlow, v => { S.JarvisGlow = v; S.Save(); _app.Island.ApplyLook(); }),
            Row("Conversar agora", "Abre a caixa do Jarvis embaixo da ilha.", open)));

        page.Children.Add(ConversationCard());
        page.Children.Add(BrainCard());
        page.Children.Add(PluginsCard());
        page.Children.Add(ComputerCard());

        var dictate = Hotkeys.Keys(S, "dictate");
        page.Children.Add(Card("Ditado em qualquer lugar",
            Note($"Segure {dictate} em qualquer app, fale e solte: o texto é colado onde você está digitando. Um toque rápido liga o " +
                 "modo mãos livres (toque de novo para colar) e Esc cancela. Usa o Whisper da Groq com a chave, ou o microfone local. " +
                 "O que estava na sua área de transferência volta depois."),
            Toggle("Ditado", $"Atalho {dictate} (troque em Geral › Atalhos).", S.Dictation, v =>
            {
                S.Dictation = v;
                S.Save();
                _app.Island.RegisterHotkeys();
            }),
            Choice("Como colar o texto", JarvisBrain.HasKey ? "A Groq arruma o texto antes de colar (leva meio segundo)." : "Corrigido e Formal precisam da chave da Groq.",
                Dictation.Styles, S.DictationStyle, v => { S.DictationStyle = v; S.Save(); })));

        var translate = Hotkeys.Keys(S, "translate");
        page.Children.Add(Card("Tradução rápida",
            Note($"Selecione um texto em qualquer app e aperte {translate}: a tradução aparece na ilha, com Copiar e Trocar o texto " +
                 "(cola a tradução no lugar do que estava selecionado). Português vira inglês; qualquer outro idioma vira português. " +
                 "Usa a chave da Groq; o texto selecionado vai para a Groq."),
            Toggle("Tradução rápida", $"Atalho {translate} (troque em Geral › Atalhos).", S.QuickTranslate, v =>
            {
                S.QuickTranslate = v;
                S.Save();
                _app.Island.RegisterHotkeys();
            }),
            Choice("Traduzir para", "Também dá para trocar clicando no idioma, na própria ilha.",
                Translator.Targets.Select(t => (t, t == "auto" ? "Automático (português ↔ inglês)" : t)).ToArray(),
                S.TranslateTo, v => { S.TranslateTo = v; S.Save(); })));

        page.Children.Add(Card("Motores locais",
            Note("Os mesmos do app JARVIS. Se o JARVIS já baixou algum, a ilha usa o dele. Cada arquivo é conferido (SHA-256) antes de rodar. " +
                 "Sem a IA, o Jarvis já responde hora, data, contas e pesquisas."),
            Engine(Services.Jarvis.Engine.Ai, "Para conversar sobre qualquer assunto, sem internet."),
            Engine(Services.Jarvis.Engine.Voice, "Para o Jarvis falar as respostas."),
            Engine(Services.Jarvis.Engine.Microphone, "Para falar com o Jarvis segurando o atalho.")));
    }

    /// <summary>The conversation with Jarvis (PC and phone), newest at the bottom, kept between restarts.</summary>
    Border ConversationCard()
    {
        var island = _app.Island;
        var list = new StackPanel();
        var scroll = new ScrollViewer
        {
            MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list, Margin = new Thickness(0, 4, 0, 10),
        };
        var copy = new Button { Content = "Copiar tudo", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(string.Join("\n\n", island.JarvisHistory.Select(t => (t.User ? "Você: " : "Jarvis: ") + t.Text))); }
            catch { }
        };
        var clear = new Button { Content = "Nova conversa", Style = (Style)Resources["Action"] };
        clear.Click += (_, _) =>
        {
            if (DarkDialog.Ask(this, "Nova conversa", "A conversa some daqui e o Jarvis começa do zero. O que ele lembra sobre você fica.", "Apagar", danger: true))
                island.ClearJarvisConversation();
        };

        void Fill()
        {
            list.Children.Clear();
            var turns = island.JarvisHistory.ToList();
            if (turns.Count == 0) list.Children.Add(Note("Nenhuma conversa ainda. O que você pedir ao Jarvis, no PC ou no celular, aparece aqui."));
            foreach (var (user, text) in turns)
                list.Children.Add(user
                    ? new Border
                    {
                        Background = Hover, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 7, 12, 8),
                        Margin = new Thickness(90, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Right,
                        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13.5, TextWrapping = TextWrapping.Wrap },
                    }
                    : new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 90, 8) });
            copy.IsEnabled = clear.IsEnabled = turns.Count > 0;
            scroll.ScrollToEnd();
        }
        Fill();
        // JarvisChanged is raised on the UI thread
        scroll.Loaded += (_, _) => island.JarvisChanged += Fill;
        scroll.Unloaded += (_, _) => island.JarvisChanged -= Fill;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(copy);
        buttons.Children.Add(clear);
        return Card("Conversa", scroll, buttons);
    }

    /// <summary>MCP plugins: each one's tools become Jarvis's tools. Playwright's browser comes in the list, off.</summary>
    Border PluginsCard()
    {
        var island = _app.Island;
        TextBox Box(string text, double width, string tip) => new()
        {
            Text = text, Width = width, FontSize = 12.5, Padding = new Thickness(8, 5, 8, 5), Background = Hover, Foreground = Brushes.White,
            CaretBrush = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Line, ToolTip = tip,
        };
        var rows = new List<UIElement>
        {
            Note("Plugins dão ao Jarvis ferramentas novas: cada um é um servidor MCP (o padrão aberto de ferramentas para IA) que roda neste PC. " +
                 "O Navegador (Playwright) abre o Edge e deixa o Jarvis pesquisar, ler páginas e preencher formulários sozinho. " +
                 "Ele precisa do Node.js; a primeira vez baixa o plugin (pode levar um minuto). Precisa da chave da Groq."),
        };
        foreach (var plugin in S.Plugins.ToList())
        {
            var status = Note(island.PluginClients.TryGetValue(plugin.Name, out var client) && client.Running ? $"✓ Rodando · {client.Tools.Count} ferramentas" : "");
            var test = new Button { Content = "Testar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
            test.Click += async (_, _) =>
            {
                test.IsEnabled = false;
                status.Text = "Abrindo…";
                var problem = await island.TestPluginAsync(plugin);
                status.Text = problem ?? (island.PluginClients.TryGetValue(plugin.Name, out var c) ? $"✓ Funcionando · {c.Tools.Count} ferramentas" : "✓ Funcionando");
                status.Foreground = problem == null ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) : new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
                test.IsEnabled = true;
            };
            var remove = new Button { Content = "Remover", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += (_, _) =>
            {
                S.Plugins.Remove(plugin);
                S.Save();
                island.ApplyPlugins();
                Render();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(test);
            buttons.Children.Add(remove);
            rows.Add(Toggle(plugin.Name, plugin.Command, plugin.Enabled, v => { plugin.Enabled = v; S.Save(); island.ApplyPlugins(); }, buttons));
            rows.Add(status);
        }

        var name = Box("", 110, "Nome curto (ex.: arquivos)");
        var command = Box("", 260, "O comando que inicia o servidor MCP (ex.: npx -y @modelcontextprotocol/server-filesystem C:\\Users\\voce\\Documents)");
        var add = new Button { Content = "Adicionar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) =>
        {
            var label = new string(name.Text.Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            if (label.Length == 0 || command.Text.Trim().Length == 0)
            {
                DarkDialog.Ask(this, "Plugins", "Dê um nome e o comando que inicia o plugin.", "OK", null);
                return;
            }
            if (S.Plugins.Any(p => p.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
            {
                DarkDialog.Ask(this, "Plugins", "Já existe um plugin com esse nome.", "OK", null);
                return;
            }
            S.Plugins.Add(new McpPlugin { Name = label, Command = command.Text.Trim(), Enabled = true });
            S.Save();
            Render();
        };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        addRow.Children.Add(name);
        addRow.Children.Add(new Border { Width = 8 });
        addRow.Children.Add(command);
        addRow.Children.Add(add);
        rows.Add(Note("Adicionar plugin: um nome curto e o comando que inicia o servidor MCP. Só adicione plugins em que você confia: eles rodam com o seu usuário."));
        rows.Add(addRow);
        if (!S.Plugins.Any(p => p.Name == McpPlugin.Playwright().Name))
        {
            var restore = new Button { Content = "Adicionar", Style = (Style)Resources["Action"] };
            restore.Click += (_, _) => { S.Plugins.Insert(0, McpPlugin.Playwright()); S.Save(); Render(); };
            rows.Add(Row("Navegador (Playwright)", "Voltar com o plugin do navegador.", restore));
        }
        return Card("Plugins do Jarvis", [.. rows]);
    }

    /// <summary>Jarvis on the mouse and keyboard, with the safeguards spelled out.</summary>
    Border ComputerCard() =>
        Card("Controle do computador",
            Note("Peça \"Jarvis, no Spotify aberto, toca a playlist Foco\" e ele faz na tela: olha a tela, decide um passo e mexe o mouse e o teclado. " +
                 "Travas: você aprova antes (todas as ações ou só as importantes), Esc para tudo na hora, a ilha mostra \"Jarvis no controle\", " +
                 "ele nunca digita em campo de senha e para em 20 passos ou 5 minutos. A captura da tela vai para a Groq a cada passo."),
            Toggle("Deixar o Jarvis usar o mouse e o teclado", JarvisBrain.HasKey ? "Desligado, ele só responde e usa as outras ferramentas." : "Precisa da chave da Groq.",
                S.ComputerUse, v => { S.ComputerUse = v; S.Save(); Render(); }, enabled: JarvisBrain.HasKey || S.ComputerUse),
            Choice("Pedir permissão", "Importantes: digitar, Enter, Delete e cliques em comprar, pagar, enviar, excluir, confirmar…",
                [("cada", "Antes de cada ação"), ("perigosas", "Só nas importantes")],
                S.ComputerUseApproval, v => { S.ComputerUseApproval = v; S.Save(); }));

    /// <summary>The Project.Jarvis brain: a Groq key (kept with DPAPI) turns on the tools (Notepad, apps, notes, timer…).</summary>
    Border BrainCard()
    {
        var green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
        var brain = _app.Island.Brain;
        var state = Note(JarvisBrain.HasKey
            ? $"✓ Ligado. O Jarvis responde pela Groq e executa tarefas no PC. Lembra de {brain.FactCount} coisa(s) sobre você."
            : "Desligado: sem a chave, o Jarvis só conversa (com a IA local) e abre programas.");
        if (JarvisBrain.HasKey) state.Foreground = green;

        var key = new PasswordBox
        {
            Width = 230, FontSize = 12.5, Padding = new Thickness(8, 5, 8, 5), Background = Hover, Foreground = Brushes.White,
            CaretBrush = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Line, ToolTip = "Começa com gsk_",
        };
        var save = new Button { Content = "Salvar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            if (!JarvisBrain.SaveKey(key.Password.Trim()))
            {
                DarkDialog.Ask(this, "Jarvis", "Essa não parece uma chave da Groq (começa com gsk_).", "OK", null);
                return;
            }
            Render();
        };
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal };
        keyRow.Children.Add(key);
        keyRow.Children.Add(save);

        var create = new Button { Content = "Criar chave grátis", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 0) };
        create.Click += (_, _) => Process.Start(new ProcessStartInfo("https://console.groq.com/keys") { UseShellExecute = true });
        var import = new Button { Content = "Importar do Project.Jarvis", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 0) };
        import.Click += (_, _) =>
        {
            var folder = JarvisBrain.ProjectFolder();
            if (folder == null)
            {
                var pick = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta do Project.Jarvis" };
                if (pick.ShowDialog(this) != true) return;
                folder = pick.FolderName;
            }
            DarkDialog.Ask(this, "Jarvis", brain.ImportProject(folder), "OK", null);
            Render();
        };
        var forget = new Button { Content = "Esquecer tudo", Style = (Style)Resources["Action"], IsEnabled = brain.FactCount > 0 };
        forget.Click += (_, _) =>
        {
            if (!DarkDialog.Ask(this, "Esquecer tudo?", "O Jarvis apaga o que lembra sobre você e o histórico da conversa.", "Esquecer", danger: true)) return;
            brain.ForgetEverything();
            _app.Island.ClearJarvisConversation();
            Render();
        };
        var actions = new WrapPanel();
        actions.Children.Add(create);
        actions.Children.Add(import);
        actions.Children.Add(forget);

        var name = new TextBox
        {
            Text = S.JarvisUserName, Width = 230, FontSize = 12.5, Padding = new Thickness(8, 5, 8, 5), Background = Hover,
            Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Line,
        };
        name.LostFocus += (_, _) => { S.JarvisUserName = name.Text.Trim(); S.Save(); };

        return Card("Cérebro (Project.Jarvis)",
            Note("O cérebro do github.com/theuslpszbr15/Project.Jarvis: com uma chave grátis da Groq, o Jarvis escreve no Bloco de Notas, " +
                 "abre programas e sites, anota, cria pastas, lê arquivos, mostra o uso do PC, muda o volume, controla a música, " +
                 "põe timer, lembrete e tarefa, vê o clima, pesquisa na Wikipédia e lembra de você. Scripts só rodam depois que você " +
                 "lê o código e permite. O que você pede vai para a Groq; a chave fica guardada só neste PC (criptografada). " +
                 "Em outro PC, cole a chave de novo lá: ela não vai junto."),
            state,
            Row("Chave da Groq", JarvisBrain.HasKey ? "Salva. Cole outra para trocar, ou salve vazio para apagar." : "console.groq.com › API Keys", keyRow),
            Row("Seu nome", "Como o Jarvis te chama.", name),
            actions);
    }

    /// <summary>Who joins the Discord call. By default the island's own Discord app (nothing to set up: "Conectar" and
    /// accept in Discord); optionally the user's own app (id + secret, kept with DPAPI). The island talks to the Discord
    /// app on this PC; only the code → token exchange goes over the internet.</summary>
    Border DiscordCard()
    {
        var discord = _app.Island.Discord;
        var source = S.DiscordSource;
        var own = source == DiscordSource.OwnApp;
        var window = source == DiscordSource.Window;
        var calls = Toggle("Quem entra na call", "Mostra a foto e o nome de quem entra na call do Discord em que você está.",
            S.DiscordCalls, _ => _app.ToggleDiscord());
        var leaves = Toggle("Avisar quando alguém sai", "Uma pílula pequena com a foto de quem saiu.",
            S.DiscordLeaves, _ => _app.ToggleDiscordLeaves(), enabled: S.DiscordCalls);
        var phone = Toggle("Mostrar também no celular", "Quem entra e sai aparece na ilha flutuante do celular pareado (nome, canal e foto pequena).",
            S.DiscordToPhone, _ => _app.ToggleDiscordToPhone(), enabled: S.DiscordCalls);
        var which = Choice("Como a ilha vê a call",
            "Lendo a janela: não precisa configurar nada. Pelo app (RPC): mostra o @ e funciona com qualquer tela aberta, mas o dono do app precisa te cadastrar como tester.",
            [(DiscordSource.Window, "Lendo a janela do Discord (padrão)"), (DiscordSource.IslandApp, "Pelo app da ilha (RPC)"),
             (DiscordSource.OwnApp, "Pelo meu próprio app (RPC)")],
            source, o => { _app.SetDiscordSource(o); Render(); });

        var green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
        var orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        var state = Note(!S.DiscordCalls ? "Desligado." : window ? "Ligado. Funciona enquanto você está numa call." :
            (discord.Authenticated ? "✓ " : "") + discord.Status);
        state.Foreground = !S.DiscordCalls ? Muted : window || discord.Authenticated ? green : orange;

        var connect = new Button
        {
            Content = discord.HasToken ? "Conectar de novo" : "Conectar", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = S.DiscordCalls && (!own || discord.HasOwnApp) && !discord.Authenticated,
        };
        connect.Click += (_, _) => discord.Authorize();
        var forget = new Button { Content = own ? "Esquecer app" : "Desconectar", Style = (Style)Resources["Action"],
            IsEnabled = discord.HasToken || discord.HasOwnApp };
        forget.Click += (_, _) =>
        {
            if (own) discord.Forget();
            else discord.Disconnect();
            Render();
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(connect);
        actions.Children.Add(forget);

        var rows = new List<UIElement>
        {
            Note(window
                ? "A ilha lê a lista da call na própria janela do Discord, como um leitor de tela: nada sai do PC. " +
                  "Importante: o Discord só mostra essa lista com o servidor da call aberto. Se você abrir outro servidor " +
                  "ou fechar o Discord na bandeja, a ilha para de ver quem entra até você voltar. Aparece o nome como " +
                  "está na call (sem o @), e a foto quando a janela do Discord está visível."
                : "A ilha conversa com o Discord aberto neste PC. Só a troca da chave de acesso passa pela internet " +
                  (own ? "(direto com o Discord)." : "(pelo servidor da ilha, que guarda a senha do app).") +
                  " O Discord só libera isso para quem o dono do app cadastrou como tester (até 50 pessoas)."),
            calls, leaves, phone, which,
        };

        if (own)
        {
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
            var secretRow = new StackPanel { Orientation = Orientation.Horizontal };
            secretRow.Children.Add(secret);
            secretRow.Children.Add(save);
            var portal = new Button { Content = "Abrir Developer Portal", Style = (Style)Resources["Action"] };
            portal.Click += (_, _) => Open("https://discord.com/developers/applications");
            rows.Add(Note($"No Developer Portal: crie o app, em OAuth2 adicione o redirect {Services.DiscordVoice.RedirectUri} e cole aqui o Client ID e o Client Secret."));
            rows.Add(Row("Client ID", null, id));
            rows.Add(Row("Client Secret", discord.HasOwnApp ? "Salvo e criptografado neste PC. Cole de novo só para trocar." : "Fica criptografado neste PC.", secretRow));
            rows.Add(Row("Ajuda", "Onde criar o app e pegar o Client ID e o Client Secret.", portal));
        }
        if (!window) rows.Add(Row("Acesso", "Conectar abre um pedido de permissão no próprio Discord: é só aceitar.", actions));
        rows.Add(state);
        return Card("Discord", [.. rows]);
    }

    void Agents(Panel page)
    {
        var copy = new Button { Content = "Copiar hooks", Style = (Style)Resources["Action"] };
        copy.Click += (_, _) => _app.CopyClaudeHooks();
        var exe = Environment.ProcessPath ?? "DynamicIsland.exe";
        var rows = new List<UIElement>
        {
            Note("Ligue o agente que você usa: a ilha escreve os hooks na configuração dele (guardando uma cópia do arquivo antes) " +
                 "e mostra cada sessão ao vivo. Desligar tira só o que a ilha colocou. Vale para sessões novas."),
            Toggle("GitHub Copilot (VS Code e CLI)", "Cria ~/.copilot/hooks/dynamic-island.json. Vale para chats novos.",
                App.CopilotConnected, _ => _app.ToggleCopilotHooks()),
        };
        foreach (var agent in AgentHooks.All)
            rows.Add(Toggle(agent.Name, agent.About + " (" + agent.File.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~").Replace('\\', '/') + ")",
                AgentHooks.IsInstalled(agent), on =>
                {
                    if (AgentHooks.Apply(agent, on, exe) is { } problem) DarkDialog.Ask(this, agent.Name, problem, "OK", null);
                    Render();
                }));
        rows.Add(Toggle("Comandos do Antigravity na ilha", "Os comandos de terminal do Antigravity esperam Permitir / Negar na ilha (sem resposta, ele pergunta no terminal). " +
            "Vale também para os que ele rodaria sozinho.", S.AntigravityApprovals, v => { S.AntigravityApprovals = v; S.Save(); },
            enabled: AgentHooks.IsInstalled(AgentHooks.Get("antigravity"))));
        rows.Add(Row("Claude Code à mão", "Copia os hooks para colar em ~/.claude/settings.json, se preferir não deixar a ilha mexer no arquivo.", copy));
        rows.Add(Toggle("Pedido de permissão discreto", "O pedido aparece como uma pílula pequena, sem abrir a ilha. Passe o mouse ou clique nela para ver o comando e responder.",
            S.QuietApprovals, _ => _app.ToggleQuietApprovals()));
        rows.Add(Row("Qualquer script", "DynamicIsland.exe agent --name \"Build\" --state working --text \"dotnet build\"", null));
        page.Children.Add(Card("Conexões", [.. rows]));

        page.Children.Add(PlansCard());
        page.Children.Add(GitHubCard());

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

    /// <summary>GitHub on the island: which repositories, and which news (CI, pull requests, releases).</summary>
    Border GitHubCard()
    {
        var island = _app.Island;
        var repos = new TextBox
        {
            Text = string.Join(", ", S.GitHubRepos), Width = 300, FontSize = 12.5, Padding = new Thickness(8, 5, 8, 5), Background = Hover,
            Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Line,
            ToolTip = "dono/repositório, separados por vírgula (ou links do GitHub)",
        };
        repos.LostFocus += (_, _) =>
        {
            var list = repos.Text.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(GitHubWatch.Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.SequenceEqual(S.GitHubRepos)) return;
            S.GitHubRepos = list;
            S.Save();
            repos.Text = string.Join(", ", list);
            _ = island.CheckGitHubAsync();
        };
        void Changed() { S.Save(); island.ApplyGitHub(); }
        return Card("GitHub na ilha",
            Note("A ilha avisa quando o CI (os testes que o GitHub roda sozinho a cada envio) falha ou volta a passar, quando alguém " +
                 "abre um PR (um pedido de mudança no código) e quando sai uma versão nova. Clique no aviso para abrir no GitHub. " +
                 "Com o GitHub CLI logado ele usa a sua conta (vale para repositórios privados); sem ele, só os públicos."),
            Toggle("Avisos do GitHub", "Confere a cada 5 minutos.", S.GitHubWatch, v => { S.GitHubWatch = v; Changed(); Render(); }),
            Row("Repositórios", "Ex.: theuslpszbr15/dynamic-island-windows", repos),
            Toggle("CI falhou / voltou a passar", "", S.GitHubCi, v => { S.GitHubCi = v; Changed(); }, enabled: S.GitHubWatch),
            Toggle("PRs novos", "", S.GitHubPulls, v => { S.GitHubPulls = v; Changed(); }, enabled: S.GitHubWatch),
            Toggle("Versões novas", "", S.GitHubReleases, v => { S.GitHubReleases = v; Changed(); }, enabled: S.GitHubWatch));
    }

    /// <summary>Copilot, Claude and Codex plan usage on the island: what was found and what is missing.</summary>
    Border PlansCard()
    {
        var island = _app.Island;
        var refresh = new Button { Content = "Atualizar", Style = (Style)Resources["Action"], IsEnabled = S.PlanUsage };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            await island.RefreshPlansAsync();
            Render();
        };
        var rows = new List<UIElement>
        {
            Note("Ao lado do relógio da ilha: quanto do plano de cada IA você já usou (o limite que está mais perto de acabar). " +
                 "O Copilot é lido com o login do GitHub CLI, o Claude com o login do Claude Code e o Codex pelos registros dele. " +
                 "Os logins só são usados para perguntar ao próprio serviço; nada é guardado."),
            Toggle("Uso do plano das IAs", "Atualiza a cada 5 minutos.", S.PlanUsage, v =>
            {
                S.PlanUsage = v;
                S.Save();
                island.ApplyPlanUsage();
                Render();
            }, refresh),
        };
        if (S.PlanUsage)
        {
            foreach (var plan in island.Plans)
            {
                var value = Note($"{Math.Round(plan.UsedPercent)}% usado");
                value.Foreground = new SolidColorBrush(plan.Color);
                rows.Add(Row(plan.Name, $"Limite de {plan.Window}" + (PlanUsage.Renews(plan.ResetsAt) is { Length: > 0 } r ? $" · {r}" : ""), value));
            }
            foreach (var note in island.PlanNotes) rows.Add(Note(note));
        }
        return Card("Uso do plano das IAs", [.. rows]);
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
        else if (_relayAdopted != null && S.PhoneRelay == _relayAdopted && island.Phone is { RelayConnected: true })
            State($"✓ Seu repasse está no ar ({_relayAdopted}). Escaneie o código de pareamento de novo no celular.", green);
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
                    ? " O público é grátis e tem limites (300 MB por dia, até 60 MB por envio); para mais, use o seu próprio."
                    : "")),
                mode, state, RelaySetupRows());
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
            status, RelaySetupRows());
    }

    /// <summary>Every phone that has connected, online or not, with when it was last seen.</summary>
    Border KnownPhonesCard()
    {
        var online = _app.Island.Phone is { Running: true } link ? link.Phones.Select(p => p.Device).ToHashSet() : [];
        var green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
        var rows = new List<UIElement>();
        foreach (var phone in S.KnownPhones.OrderByDescending(p => online.Contains(p.Device)).ThenByDescending(p => p.LastSeen))
        {
            var now = online.Contains(phone.Device);
            var when = phone.LastSeen.Date == DateTime.Today ? $"hoje às {phone.LastSeen:HH:mm}"
                : phone.LastSeen.Date == DateTime.Today.AddDays(-1) ? $"ontem às {phone.LastSeen:HH:mm}" : $"{phone.LastSeen:dd/MM/yyyy HH:mm}";
            var path = phone.ViaRelay ? "pela internet" : "rede local";
            var remove = new Button { Content = "Tirar da lista", Style = (Style)Resources["Action"], IsEnabled = !now };
            remove.Click += (_, _) =>
            {
                S.KnownPhones.Remove(phone);
                S.Save();
                Render();
            };
            var row = Row(phone.Device, (now ? "Conectado agora" : "Visto por último " + when) + " · " + path + $" · pareado em {phone.FirstSeen:dd/MM/yyyy}", remove);
            if (now && row is FrameworkElement element) element.ToolTip = "Conectado agora";
            rows.Add(row);
        }
        var state = Note(online.Count > 0 ? $"✓ {online.Count} conectado agora" : "Nenhum conectado agora.");
        if (online.Count > 0) state.Foreground = green;
        return Card("Celulares",
        [
            state, .. rows,
            Note("Todos usam o mesmo pareamento: para tirar o acesso de um celular, use \"Parear de novo\" (os outros escaneiam o código de novo)."),
        ]);
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
            var url = link.PairingLink(S.PhoneUsb);
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
            side.Children.Add(Note(PhoneLink.LocalAddresses() is { Count: > 0 } ips
                ? $"Este PC: {string.Join(" / ", ips)}, porta {link.Port}"
                : "Sem rede local: conecte o PC ao Wi-Fi ou ao cabo de rede."));
            if (link.Relay.Length > 0)
            {
                var via = Note($"Fora de casa: {link.Relay[6..]} " + (link.RelayConnected ? "(conectado ✓, já está neste código)" : "(conectando…)"));
                via.Foreground = new SolidColorBrush(link.RelayConnected ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0xFF, 0x9F, 0x0A));
                side.Children.Add(via);
            }
            side.Children.Add(Note("1. Instale o app no celular (Baixar no celular, acima).\n" +
                                   (link.Relay.Length > 0 ? "2. Qualquer rede serve: com o repasse ligado o celular alcança o PC pela internet.\n"
                                                          : "2. Celular e PC no mesmo roteador (Wi-Fi ou cabo) — ou use \"Ligar pelo adb\" em Conexão, abaixo.\n") +
                                   "3. No app, toque em Escanear e aponte para este código."));
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var copy = new Button { Content = "Copiar link", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 8) };
            copy.Click += (_, _) => { try { Clipboard.SetText(url); } catch { } };
            var again = new Button { Content = "Parear de novo", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 8) };
            again.Click += (_, _) =>
            {
                if (!DarkDialog.Ask(this, "Parear de novo", "O celular pareado será desconectado e terá de escanear um código novo. Continuar?", "Parear de novo")) return;
                link.PairAgain();
                Render();
            };
            buttons.Children.Add(copy);
            if (phones > 0)
            {
                // the newest app straight to the phone: it arrives as a file to accept, then Android installs it
                var update = new Button { Content = "Enviar o app atualizado", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 8) };
                update.Click += async (_, _) =>
                {
                    update.IsEnabled = false;
                    await island.SendAppUpdateToPhoneAsync();
                    update.IsEnabled = true;
                };
                buttons.Children.Add(update);
            }
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
                Note("Se o celular não conectar: veja o Firewall e o cabo USB logo abaixo. " +
                     "Redes de visitante/corporativas costumam isolar os aparelhos, e o PC no cabo e o celular no Wi-Fi " +
                     "só se enxergam se estiverem no mesmo roteador.")));
            page.Children.Add(ConnectionCard(link));
        }
        else if (S.PhoneLink)
        {
            page.Children.Add(Card("Parear", Note("A ligação não iniciou: " + (island.PhoneError ?? "erro desconhecido"))));
        }
        if (S.KnownPhones.Count > 0) page.Children.Add(KnownPhonesCard());

        var download = new Button { Content = "Abrir no navegador", Style = (Style)Resources["Action"] };
        download.Click += (_, _) => Open(ReleasesUrl);
        var window = new Button { Content = "Abrir", Style = (Style)Resources["Action"] };
        window.Click += (_, _) => ShowTransfers("send");
        page.Children.Add(Card("No celular",
            Toggle("Bandeja de arquivos no celular", "O celular vê os itens fixados na bandeja da ilha e pode abri-los neste PC (pede sua digital).",
                S.PhoneTray, _ => _app.TogglePhoneTray()),
            Toggle("Arquivos e textos com o celular", "Solte arquivos ou texto no lado \"celular\" da ilha, use o botão no item da bandeja " +
                   "ou a aba Enviar e receber, aqui ao lado. Do celular, use Compartilhar → Mandar pro PC: a ilha pergunta antes, " +
                   "e tudo vai para Downloads\\Dynamic Island. Até 30 MB por arquivo (vídeo 60 MB), criptografado de ponta a ponta.",
                S.PhoneFiles, _ => _app.TogglePhoneFiles()),
            Choice("Vídeos pesados", "1080p a 60 fps, 4K ou HEVC travam em muitos celulares. Convertido, vai em H.264 a 30 fps, que todo celular toca.",
                [(VideoFitMode.Ask, "Perguntar na ilha"), (VideoFitMode.Always, "Converter sempre"), (VideoFitMode.Never, "Mandar como está")],
                S.FitVideos, m => { S.FitVideos = m; S.Save(); }),
            Toggle("Área de transferência junto com o celular", "Texto copiado no PC vai para o celular na hora; o que você copia no celular chega aqui " +
                   "quando abre o app (ou pelo bloco rápido \"Copiado → PC\"). Gerenciadores de senha são ignorados.",
                S.PhoneClipboard, v => { S.PhoneClipboard = v; S.Save(); }),
            Toggle("Controle remoto pelo celular", "Música, volume, bloquear e desligar daqui a X minutos (bloquear e desligar pedem a digital no celular).",
                S.PhoneRemote, v => { S.PhoneRemote = v; S.Save(); }),
            Toggle("Mouse e teclado pelo celular", "No app, PC › Touchpad e teclado: o celular vira mouse, teclado e controle de filme " +
                   "e de apresentação. A ilha avisa quando o celular está no controle.",
                S.PhoneMouse, v => { S.PhoneMouse = v; S.Save(); }, enabled: S.PhoneRemote),
            Toggle("Controle de videogame", "No app, PC › Touchpad e teclado › Jogo: o celular vira um controle de Xbox ou de PS4 que os jogos " +
                   "reconhecem. " + (VirtualPad.DriverInstalled ? "Driver ViGEmBus instalado."
                       : "Precisa do driver ViGEmBus (o mesmo do DS4Windows), instalado uma vez."),
                S.PhoneGamepad, v => { S.PhoneGamepad = v; S.Save(); }, PadDriverButton(), enabled: S.PhoneRemote),
            Toggle("Ligações do celular na ilha", "Quem está ligando aparece na ilha. Ligue também no app do celular (aba PC).",
                S.PhoneCalls, v => { S.PhoneCalls = v; S.Save(); }),
            Row("Enviar e receber", "Enviar arquivo, pasta, texto ou o que foi copiado, e ver o que foi recebido e enviado.", window),
            Row("Página de Releases", "Todas as versões do app e da ilha.", download)));
        page.Children.Add(NoticesCard());
        page.Children.Add(WakeCard());
    }

    /// <summary>"Avisos da ilha do celular": the same switches as the app's screen, kept in sync over the link.</summary>
    Border NoticesCard()
    {
        var isl = _app.Island;
        (string Kind, string Title, string? Note)[] rows =
        [
            ("Approval", "Pedidos de permissão dos agentes", "Segurança: desligado, a ilha do celular não avisa quando um agente espera sua resposta."),
            ("Offer", "Solicitações de envio recebidas", null),
            ("Outgoing", "Esperando o PC aceitar um envio", null),
            ("Claude", "Avisos do app do Claude", null),
            ("Transfer", "Transferências (andamento e fim)", null),
            ("Discord", "Quem entra e sai da call", "Vale também para a ilha do PC."),
            ("Call", "Estou numa call", null),
        ];
        return Card("Avisos da ilha do celular", rows.Select(r =>
            Toggle(r.Title, r.Note ?? "Muda aqui e no celular ao mesmo tempo.", isl.NoticeShown(r.Kind), v => isl.SetNoticeShown(r.Kind, v))).ToArray());
    }

    bool? _wakeArmed;
    bool _wakeBusy, _fpsAsked;

    /// <summary>The ViGEmBus page, while the driver isn't installed.</summary>
    FrameworkElement? PadDriverButton()
    {
        if (VirtualPad.DriverInstalled) return null;
        var download = new Button { Content = "Baixar o driver", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 12, 0) };
        download.Click += (_, _) => Open(VirtualPad.DriverUrl);
        return download;
    }

    /// <summary>Modo jogo: hold notifications in games, the meter, and letting it count FPS (one administrator prompt).</summary>
    Border GameCard()
    {
        var fpsAllowed = FpsCounter.Allowed;
        var allow = new Button
        {
            Content = _fpsAsked ? "Saia e entre de novo no Windows" : "Liberar FPS", Style = (Style)Resources["Action"],
            IsEnabled = !fpsAllowed && !_fpsAsked,
        };
        allow.Click += async (_, _) =>
        {
            allow.IsEnabled = false;
            if (await Task.Run(FpsCounter.RequestAccess))
            {
                _fpsAsked = true;
                DarkDialog.Ask(this, "Liberar FPS", "Pronto. O contador de FPS funciona depois que você sair e entrar de novo no Windows (ou reiniciar).", "OK", null);
            }
            else DarkDialog.Ask(this, "Liberar FPS", "O Windows precisa da sua permissão (clique em Sim na janela que aparece).", "OK", null);
            Render();
        };
        return Card("Modo jogo",
            Toggle("Modo jogo", "Num jogo em tela cheia (Steam, Epic, Xbox, Riot, Roblox…), as notificações do Windows esperam e aparecem " +
                   "num resumo quando o jogo fecha. Pedidos de permissão dos agentes continuam passando.",
                S.GameMode, v => { S.GameMode = v; S.Save(); _app.Island.RefreshHidden(); }),
            Toggle("Medidor no jogo", "Um contador discreto no topo da tela: FPS, temperatura e uso da placa de vídeo, e o uso do processador.",
                S.GameHud, v => { S.GameHud = v; S.Save(); }, enabled: S.GameMode),
            Row("Contador de FPS", fpsAllowed
                    ? "Liberado. O FPS aparece em jogos DirectX (a maioria); Vulkan e OpenGL mostram só GPU e CPU."
                    : "O Windows só mostra o FPS dos jogos para quem está no grupo \"Usuários de log de desempenho\" " +
                      "(como a Xbox Game Bar faz). Um clique entra no grupo; vale a partir do próximo login.",
                allow));
    }

    /// <summary>"Ligar o PC pelo celular" (Wake-on-LAN) in three steps: Windows (one click), BIOS (restart into it, with
    /// where the switch is on this board), phone (connect once).</summary>
    Border WakeCard()
    {
        var green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
        var orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        TextBlock Step(bool ok, string text)
        {
            var t = Note((ok ? "✓ " : "• ") + text);
            t.Foreground = ok ? green : orange;
            t.FontWeight = FontWeights.SemiBold;
            t.Margin = new Thickness(0, 10, 0, 2);
            return t;
        }
        Button Action(string text, bool enabled = true) =>
            new() { Content = text, Style = (Style)Resources["Action"], IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };

        var wired = WakeOnLan.Wired();
        if (wired == null)
            return Card("Ligar o PC pelo celular",
                Note("Este PC não tem placa de rede com cabo. Pelo Wi-Fi quase nenhum PC liga desligado: ligue um cabo de rede no roteador e volte aqui."));

        _wakeArmed ??= WakeOnLan.CardArmed(wired);
        var windowsReady = _wakeArmed == true && !WakeOnLan.FastStartupOn();
        var prepare = Action(_wakeBusy ? "Preparando…" : windowsReady ? "Preparar de novo" : "Preparar o Windows", !_wakeBusy);
        prepare.Click += async (_, _) =>
        {
            _wakeBusy = true;
            Render();
            var ok = await Task.Run(() => WakeOnLan.Prepare(wired));
            _wakeBusy = false;
            _wakeArmed = null;
            Render();
            if (!ok) DarkDialog.Ask(this, "Ligar o PC pelo celular", "O Windows precisa da sua permissão (clique em Sim na janela que aparece).", "OK", null);
        };

        var (maker, where) = WakeOnLan.BiosHint();
        var bios = Action("Reiniciar na BIOS");
        bios.Click += (_, _) =>
        {
            if (!DarkDialog.Ask(this, "Reiniciar na BIOS",
                    $"Salve o que estiver aberto: o PC reinicia agora e abre a BIOS.\n\nLá: {where}.\nDepois salve e saia (geralmente F10).",
                    "Reiniciar")) return;
            if (!WakeOnLan.RestartToBios())
                DarkDialog.Ask(this, "Reiniciar na BIOS",
                    "Este PC não abre a BIOS pelo Windows. Reinicie e aperte Delete ou F2 assim que a tela acender.", "OK", null);
        };

        var phoneReady = Island.PhoneReady;
        return Card("Ligar o PC pelo celular",
            Note($"Com o PC desligado e o celular no Wi-Fi de casa, o botão \"Ligar o PC\" do app liga este PC pela placa {wired.Name}" +
                 (wired.Connected ? "." : " (o cabo parece desconectado).") + " Faça os 3 passos uma vez:"),
            Step(windowsReady, windowsReady ? "1. Windows pronto" : "1. Windows: um clique arruma tudo"),
            Note("Desliga a inicialização rápida e deixa a placa de rede ligar o PC. O Windows pede permissão uma vez; a internet pisca por alguns segundos."),
            prepare,
            Step(false, "2. BIOS: uma opção, uma vez"),
            Note((maker.Length > 0 ? $"Placa {maker}: " : "") + where + ". O botão reinicia direto nela."),
            bios,
            Step(phoneReady, phoneReady ? "3. Celular conectado: já sabe o endereço desta placa" : "3. Celular: abra o app uma vez com o PC ligado"),
            Note("Pronto: desligue o PC e toque em \"Ligar o PC\" no celular (aba PC)."));
    }

    // What stands between the phone and this PC: the Windows firewall, and the USB cable as a way around the network.
    Border ConnectionCard(PhoneLink link)
    {
        if (!_firewallChecked && !_firewallBusy)
        {
            _firewallBusy = true;
            var port = link.Port;
            _ = Task.Run(() => FirewallRule.IsOpen(port)).ContinueWith(t =>
                Dispatcher.BeginInvoke(() =>
                {
                    _firewallBusy = false;
                    _firewallOpen = t.IsCompletedSuccessfully ? t.Result : null;
                    _firewallChecked = true;
                    if (_page == "phone") Render();
                }));
        }

        var open = new Button { Content = "Liberar no firewall", Style = (Style)Resources["Action"] };
        open.Click += (_, _) =>
        {
            var error = FirewallRule.Open();
            if (error != null) MessageBox.Show(this, error, "Firewall do Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
            _firewallOpen = null;
            _firewallChecked = false;
            Render();
        };
        var firewall = _firewallOpen switch
        {
            true => Row("Firewall do Windows", "\u2713 A ilha pode receber a conex\u00e3o do celular.", null),
            false => Row("Firewall do Windows",
                "Nenhuma regra libera a ilha, ent\u00e3o o Windows barra o celular (comum quando o PC est\u00e1 no cabo). " +
                "Pede permiss\u00e3o de administrador e vale s\u00f3 para redes privadas.", open),
            _ when _firewallBusy || !_firewallChecked => Row("Firewall do Windows", "Verificando\u2026", null),
            _ => Row("Firewall do Windows", "N\u00e3o consegui verificar as regras. Se o celular n\u00e3o conecta, tente liberar.", open),
        };

        var usb = _app.Island.Usb;
        var usbRow = Toggle("Ligar pelo adb (cabo ou Wi-Fi)",
            "Para quando o firewall do PC barra o celular. Ligue o cabo USB uma vez com a depura\u00e7\u00e3o USB ativa: depois o celular " +
            "continua ligado pelo Wi-Fi (at\u00e9 reiniciar). Precisa do adb (vem com o scrcpy). Depois de ligar, escaneie o QR de novo.",
            S.PhoneUsb, _ => _app.TogglePhoneUsb());
        UIElement[] rows = S.PhoneUsb && usb is { Status.Length: > 0 }
            ? [firewall, usbRow, Note(usb.Status)]
            : [firewall, usbRow];
        return Card("Conex\u00e3o", rows);
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
