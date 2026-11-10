using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using DynamicIsland.Services;
using QRCoder;

namespace DynamicIsland;

/// <summary>All of the island's settings in one window: a section list on the left, live changes on the right.</summary>
public sealed class SettingsWindow : Window
{
    static string ReleasesUrl => Services.Updater.RepositoryUrl + "/releases/latest";
    static string ApkUrl => ReleasesUrl + "/download/DynamicIsland-Android.apk";

    static readonly Brush Ground = Frozen(0xFF141416), Panel = Frozen(0xFF1C1C1F), CardBg = Frozen(0xFF222226),
        Line = Frozen(0xFF2E2E33), Muted = Frozen(0xFF9A9AA0), Accent = Frozen(0xFF0A84FF), Hover = Frozen(0xFF2A2A2F);

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
    readonly StackPanel _nav = new() { Margin = new Thickness(10, 8, 10, 10) };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(34, 26, 34, 30) };
    string _page = "general";
    bool _downloadQr;

    IslandSettings S => _app.Settings;

    public SettingsWindow(App app)
    {
        _app = app;
        Title = "Configurações — Dynamic Island";
        Width = 940;
        Height = 680;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ground;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        Icon = BitmapFrame.Create(typeof(SettingsWindow).Assembly.GetManifestResourceStream("DynamicIsland.ico")!);
        Resources.MergedDictionaries.Add(Styles());

        // No Windows title bar: a dark bar of our own (mac-style dots, like Maestri) that still drags, snaps,
        // resizes and double-click maximizes through WindowChrome.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = CaptionHeight,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1), // keeps the DWM shadow on Windows 10/11
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        var sidebar = new DockPanel { Background = Panel };
        var lights = TrafficLights();
        DockPanel.SetDock(lights, Dock.Top);
        sidebar.Children.Add(lights);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 4, 16, 16) };
        header.Children.Add(new Viewbox { Width = 38, Height = 38, Child = new PetView { FixedSpecies = PetSpecies.Bit, BaseMood = PetView.Mood.Happy } });
        var name = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "Dynamic Island", FontSize = 16, FontWeight = FontWeights.SemiBold });
        name.Children.Add(new TextBlock { Text = "versão " + Updater.Current.ToString(3), FontSize = 12, Foreground = Muted });
        header.Children.Add(name);
        DockPanel.SetDock(header, Dock.Top);
        sidebar.Children.Add(header);
        foreach (var (id, glyph, title) in Sections) _nav.Children.Add(NavButton(id, glyph, title));
        sidebar.Children.Add(_nav);

        var root = new Grid { Background = Ground };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CaptionHeight) });
        root.RowDefinitions.Add(new RowDefinition());
        Grid.SetRowSpan(sidebar, 2);
        root.Children.Add(sidebar);
        // The window's name, centered in the content side of the bar (drag area, not clickable).
        var caption = new TextBlock
        {
            Text = "Configurações", FontSize = 12.5, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        Grid.SetColumn(caption, 1);
        root.Children.Add(caption);
        Grid.SetColumn(_scroll, 1);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);
        _scroll.Padding = new Thickness(34, 6, 34, 30);
        // A hairline edge, since there is no Windows frame any more; maximized, WPF overhangs the screen by
        // the resize border, so the content is pulled back in.
        var frame = new Border { BorderBrush = Line, BorderThickness = new Thickness(1), Child = root };
        Content = frame;
        void Fit()
        {
            frame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
            frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        }
        StateChanged += (_, _) => Fit();
        Activated += (_, _) => SetLightsActive(true);
        Deactivated += (_, _) => SetLightsActive(false);

        if (_app.Island.Phone is { } link) link.PhonesChanged += OnPhones;
        Closed += (_, _) => { if (_app.Island.Phone is { } l) l.PhonesChanged -= OnPhones; };
        Render();
    }

    const double CaptionHeight = 40;
    readonly List<(Ellipse Dot, Brush On)> _lights = [];
    static readonly Brush LightOff = Frozen(0xFF3A3A3F), LightGlyph = Frozen(0x99000000);

    /// <summary>Close, minimize and maximize as three colored dots; their symbols show on hover.</summary>
    FrameworkElement TrafficLights()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 14, 0, 0), Height = CaptionHeight - 14 };
        var glyphs = new List<TextBlock>();
        void Add(uint color, string glyph, string tip, Action click)
        {
            var on = Frozen(color);
            var dot = new Ellipse { Width = 12, Height = 12, Fill = on };
            var symbol = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 6.5, Foreground = LightGlyph,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0,
            };
            glyphs.Add(symbol);
            var hit = new Grid { Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), Background = Brushes.Transparent, ToolTip = tip, Cursor = Cursors.Hand };
            hit.Children.Add(dot);
            hit.Children.Add(symbol);
            WindowChrome.SetIsHitTestVisibleInChrome(hit, true);
            hit.MouseLeftButtonDown += (_, e) => e.Handled = true; // don't start a window drag
            hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            row.Children.Add(hit);
            _lights.Add((dot, on));
        }
        Add(0xFFFF5F57, "", "Fechar", Close);
        Add(0xFFFEBC2E, "", "Minimizar", () => WindowState = WindowState.Minimized);
        Add(0xFF28C840, "", "Maximizar", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        WindowChrome.SetIsHitTestVisibleInChrome(row, true);
        row.MouseEnter += (_, _) => { foreach (var g in glyphs) g.Opacity = 1; foreach (var (dot, on) in _lights) dot.Fill = on; };
        row.MouseLeave += (_, _) => { foreach (var g in glyphs) g.Opacity = 0; SetLightsActive(IsActive); };
        return row;
    }

    // Like macOS: the dots go grey when the window isn't in front.
    void SetLightsActive(bool active)
    {
        foreach (var (dot, on) in _lights) dot.Fill = active ? on : LightOff;
    }

    void OnPhones()
    {
        if (_page == "phone") Render();
    }

    public void Navigate(string page)
    {
        if (Sections.All(s => s.Id != page)) return;
        _page = page;
        Render();
    }

    public void ShowDownloadQr()
    {
        _downloadQr = true;
        Navigate("phone");
    }

    void Render()
    {
        foreach (RadioButton b in _nav.Children) b.IsChecked = (string)b.Tag == _page;
        var offset = _scroll.VerticalOffset;
        var page = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        var (_, _, title) = Sections.First(s => s.Id == _page);
        page.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 18) });
        switch (_page)
        {
            case "general": General(page); break;
            case "look": Look(page); break;
            case "island": OnIsland(page); break;
            case "agents": Agents(page); break;
            case "phone": Phone(page); break;
            case "tray": Tray(page); break;
            case "updates": Updates(page); break;
        }
        _scroll.Content = page;
        _scroll.ScrollToVerticalOffset(offset);
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

    void Phone(Panel page)
    {
        var island = _app.Island;
        page.Children.Add(Card("Aprovar pelo celular",
            Note("Com o app Dynamic Island no Android, os pedidos de permissão chegam no celular: Negar direto da notificação, " +
                 "Permitir com a sua digital. O celular conversa direto com este PC pela rede Wi-Fi (TLS, código de pareamento)."),
            Toggle("Ligação com o celular", "Desligada, nada fica escutando na rede.", S.PhoneLink, _ => _app.TogglePhoneLink())));
        page.Children.Add(InstallCard());

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
            side.Children.Add(Note(PhoneLink.LocalAddress() is { } ip
                ? $"Este PC: {ip}, porta {link.Port}"
                : "Sem rede local: conecte o PC ao Wi-Fi."));
            side.Children.Add(Note("1. Instale o app no celular (Baixar no celular, acima).\n2. Celular e PC na mesma rede Wi-Fi.\n3. No app, toque em Escanear e aponte para este código."));
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
        page.Children.Add(Card("No celular",
            Toggle("Bandeja de arquivos no celular", "O celular vê os itens fixados na bandeja da ilha e pode abri-los neste PC (pede sua digital).",
                S.PhoneTray, _ => _app.TogglePhoneTray()),
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

    // ───────────────────────── building blocks ─────────────────────────

    RadioButton NavButton(string id, string glyph, string title)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Width = 30, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var button = new RadioButton { Content = content, Tag = id, Style = (Style)Resources["Nav"], GroupName = "nav" };
        button.Checked += (_, _) => { if (_page != id) { _page = id; Render(); } };
        return button;
    }

    Border Card(string title, params UIElement[] rows)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0 && rows[i] is Grid { Tag: "row" } && rows[i - 1] is Grid { Tag: "row" })
                stack.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 4, 0, 4) });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            Background = CardBg, CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(0, 0, 0, 14), BorderBrush = Line, BorderThickness = new Thickness(1), Child = stack,
        };
    }

    TextBlock Note(string text) => new()
    {
        Text = text, Foreground = Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
    };

    Grid Row(string title, string? description, FrameworkElement? control)
    {
        var grid = new Grid { Tag = "row", MinHeight = 48 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 18, 6) };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(description))
            text.Children.Add(new TextBlock { Text = description, Foreground = Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(text);
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        return grid;
    }

    Grid Toggle(string title, string description, bool value, Action<bool> changed, FrameworkElement? extra = null, bool enabled = true)
    {
        var toggle = new CheckBox { IsChecked = value, Style = (Style)Resources["Switch"], IsEnabled = enabled };
        toggle.Click += (_, _) => { changed(toggle.IsChecked == true); Render(); };
        if (extra == null) return Row(title, description, toggle);
        var both = new StackPanel { Orientation = Orientation.Horizontal };
        both.Children.Add(extra);
        both.Children.Add(toggle);
        return Row(title, description, both);
    }

    Grid Choice<T>((T Value, string Label)[] options, T current, Action<T> changed, string title, string description)
    {
        var combo = new ComboBox { Width = 250, Style = (Style)Resources["Combo"] };
        foreach (var (value, label) in options)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value, Style = (Style)Resources["ComboItem"] });
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => EqualityComparer<T>.Default.Equals(o.Value, current)));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T value } && !EqualityComparer<T>.Default.Equals(value, current)) changed(value);
        };
        return Row(title, description, combo);
    }

    Grid Choice<T>(string title, string description, (T Value, string Label)[] options, T current, Action<T> changed) =>
        Choice(options, current, changed, title, description);

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
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

    static SolidColorBrush Frozen(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    // Dark controls: a switch, sidebar items, buttons and a combo box with a dark drop-down.
    static ResourceDictionary Styles() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="Switch" TargetType="CheckBox">
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                  <Grid Width="44" Height="24" Background="Transparent">
                    <Border x:Name="Track" CornerRadius="12" Background="#FF3A3A3F" BorderBrush="#FF55555C" BorderThickness="1" />
                    <Ellipse x:Name="Thumb" Width="16" Height="16" Fill="#FFDADADF" HorizontalAlignment="Left" Margin="4,0,0,0" />
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Track" Property="Background" Value="#FF0A84FF" />
                      <Setter TargetName="Track" Property="BorderBrush" Value="#FF0A84FF" />
                      <Setter TargetName="Thumb" Property="Fill" Value="White" />
                      <Setter TargetName="Thumb" Property="HorizontalAlignment" Value="Right" />
                      <Setter TargetName="Thumb" Property="Margin" Value="0,0,4,0" />
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter Property="Opacity" Value="0.4" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="Nav" TargetType="RadioButton">
            <Setter Property="Foreground" Value="#FFE5E5EA" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="RadioButton">
                  <Border x:Name="Bd" CornerRadius="8" Padding="12,9" Margin="0,1" Background="Transparent">
                    <Grid>
                      <Border x:Name="Bar" Width="3" Height="16" CornerRadius="1.5" Background="#FF0A84FF" HorizontalAlignment="Left" Margin="-10,0,0,0" Visibility="Hidden" />
                      <ContentPresenter />
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF26262B" />
                    </Trigger>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF2C2C32" />
                      <Setter TargetName="Bar" Property="Visibility" Value="Visible" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="Action" TargetType="Button">
            <Setter Property="Foreground" Value="White" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="Bd" CornerRadius="8" Padding="14,7" Background="#FF34343A" BorderBrush="#FF44444B" BorderThickness="1">
                    <ContentPresenter HorizontalAlignment="Center" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF404047" />
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF2A2A30" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="ComboItem" TargetType="ComboBoxItem">
            <Setter Property="Foreground" Value="White" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ComboBoxItem">
                  <Border x:Name="Bd" CornerRadius="6" Padding="10,7" Margin="4,1" Background="Transparent">
                    <ContentPresenter />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF34343A" />
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF0A84FF" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="Combo" TargetType="ComboBox">
            <Setter Property="Foreground" Value="White" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ComboBox">
                  <Grid>
                    <ToggleButton IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" Focusable="False" ClickMode="Press">
                      <ToggleButton.Template>
                        <ControlTemplate TargetType="ToggleButton">
                          <Border x:Name="Bd" CornerRadius="8" Background="#FF34343A" BorderBrush="#FF44444B" BorderThickness="1">
                            <TextBlock Text="&#xE70D;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="11" Foreground="#FFB0B0B8"
                                       HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,0" />
                          </Border>
                          <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                              <Setter TargetName="Bd" Property="Background" Value="#FF404047" />
                            </Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </ToggleButton.Template>
                    </ToggleButton>
                    <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" IsHitTestVisible="False"
                                      Margin="12,7,32,7" VerticalAlignment="Center" />
                    <Popup IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                      <Border Background="#FF26262B" BorderBrush="#FF44444B" BorderThickness="1" CornerRadius="10" Padding="0,4"
                              MinWidth="{TemplateBinding ActualWidth}" MaxHeight="320" Margin="0,4,0,0">
                        <ScrollViewer VerticalScrollBarVisibility="Auto">
                          <ItemsPresenter />
                        </ScrollViewer>
                      </Border>
                    </Popup>
                  </Grid>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <!-- A thin dark scrollbar instead of the light Windows one: just a rounded thumb, wider on hover. -->
          <Style x:Key="ThumbBar" TargetType="Thumb">
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Thumb">
                  <Border x:Name="T" CornerRadius="3" Background="#3A3A40" />
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="T" Property="Background" Value="#55555C" /></Trigger>
                    <Trigger Property="IsDragging" Value="True"><Setter TargetName="T" Property="Background" Value="#6A6A72" /></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="ScrollBar">
            <Setter Property="Width" Value="10" />
            <Setter Property="MinWidth" Value="10" />
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ScrollBar">
                  <Grid Background="Transparent">
                    <Track x:Name="PART_Track" IsDirectionReversed="True" Margin="2,4,2,4">
                      <Track.Thumb><Thumb x:Name="Th" Style="{StaticResource ThumbBar}" Width="5" HorizontalAlignment="Right" /></Track.Thumb>
                    </Track>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Th" Property="Width" Value="7" /></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """);
}
