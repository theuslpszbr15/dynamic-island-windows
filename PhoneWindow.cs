using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// The "Celular" window, in the spirit of LocalSend but for the one paired phone (no list of nearby devices):
/// Enviar (Arquivo, Pasta, Texto, Colar, or drop things on the window), Recebidos and Enviados (the history).
/// Everything goes through the same link as the island: the phone asks before taking anything.
/// </summary>
public sealed class PhoneWindow : DarkWindow
{
    static readonly (string Id, string Glyph, string Title)[] Sections =
    [
        ("send", "", "Enviar"),
        ("received", "", "Recebidos"),
        ("sent", "", "Enviados"),
        ("settings", "", "Configurações"),
    ];

    static readonly Brush Good = Frozen(0xFF30D158), Warn = Frozen(0xFFFF9F0A), Bad = Frozen(0xFFFF453A), Internet = Frozen(0xFF64D2FF);

    readonly App _app;
    PhoneLink? _link;
    // Kept across renders so a half-written text survives the phone coming and going.
    readonly TextBox _text;
    bool _textOpen;
    string? _status;
    bool _statusBad;

    IslandWindow Island => _app.Island;

    public PhoneWindow(App app) : base("Celular — Dynamic Island", "Celular", Sections)
    {
        _app = app;
        Resources.MergedDictionaries.Add(TileStyle());
        _text = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, MinHeight = 150, MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Hover, Foreground = Brushes.White,
            CaretBrush = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8),
            FontSize = 13.5, MaxLength = PhoneLink.MaxTextChars,
        };
        _text.TextChanged += (_, _) => UpdateCounter();

        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += OnDrop;

        Island.History.Changed += OnHistory;
        Activated += (_, _) => Watch();
        Closed += (_, _) =>
        {
            Island.History.Changed -= OnHistory;
            if (_link != null) _link.PhonesChanged -= OnPhones;
        };
        Watch();
        Render();
    }

    /// <summary>The link exists only once "Ligação com o celular" was turned on, so look for it again on the way in.</summary>
    void Watch()
    {
        if (_link == Island.Phone) return;
        if (_link != null) _link.PhonesChanged -= OnPhones;
        _link = Island.Phone;
        if (_link != null) _link.PhonesChanged += OnPhones;
        Render();
    }

    void OnPhones()
    {
        if (_page is "send" or "settings") Render();
    }

    void OnHistory() => Dispatcher.InvokeAsync(() =>
    {
        if (_page is "received" or "sent") Render();
    });

    protected override void Fill(string section, Panel page)
    {
        switch (section)
        {
            case "send": Send(page); break;
            case "received": History(page, sent: false); break;
            case "sent": History(page, sent: true); break;
            case "settings": Settings(page); break;
        }
    }

    // ───────────────────────── Enviar ─────────────────────────

    void Send(Panel page)
    {
        var ready = Island.PhoneReady;
        page.Children.Add(PhoneCard());

        var tiles = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        tiles.Children.Add(Tile("", "Arquivo", ready, PickFiles));
        tiles.Children.Add(Tile("", "Pasta", ready, PickFolder));
        tiles.Children.Add(Tile("", "Texto", ready, () => { _textOpen = true; Render(); _text.Focus(); }));
        tiles.Children.Add(Tile("", "Colar", ready, async () => Done(await Island.SendClipboardToPhoneAsync(), "Enviado: aceite no celular")));
        var selection = new StackPanel();
        selection.Children.Add(tiles);
        selection.Children.Add(Note(ready
            ? "Também dá para arrastar arquivos ou um texto para esta janela. O celular pergunta antes de receber. " +
              $"Até {PhoneLink.MaxBatchItems} arquivos de uma vez, 30 MB cada (vídeo 60 MB); texto até 100 mil caracteres."
            : "Os botões funcionam quando o celular estiver conectado."));
        if (_status != null)
            selection.Children.Add(new TextBlock
            {
                Text = _status, Foreground = _statusBad ? Bad : Good, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
        page.Children.Add(Card("Seleção", selection));

        if (_textOpen) page.Children.Add(TextCard(ready));
    }

    Border PhoneCard()
    {
        var phones = _link is { Running: true } ? _link.Phones : [];
        var row = new DockPanel();
        var icon = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 30,
            Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = phones.Count > 0 ? Brushes.White : Muted,
        };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (!_app.Settings.PhoneLink || _link is not { Running: true })
        {
            text.Children.Add(new TextBlock { Text = "Ligação com o celular desligada", FontSize = 16, FontWeight = FontWeights.SemiBold });
            text.Children.Add(Small("Ligue e pareie o celular nas Configurações."));
        }
        else if (phones.Count == 0)
        {
            text.Children.Add(new TextBlock { Text = "Celular desconectado", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Warn });
            text.Children.Add(Small("Abra o app Dynamic Island no celular. Ele conecta sozinho pelo Wi-Fi ou pela internet."));
        }
        else
        {
            foreach (var phone in phones)
            {
                text.Children.Add(new TextBlock { Text = phone.Device.Length > 0 ? phone.Device : "Android", FontSize = 16, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock
                {
                    Text = $"✓ Conectado {(phone.ViaRelay ? "pela internet" : "pela rede local")} · desde {phone.Since:HH:mm}",
                    Foreground = phone.ViaRelay ? Internet : Good, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 4),
                });
            }
            if (!_app.Settings.PhoneFiles)
                text.Children.Add(Small("\"Arquivos e textos com o celular\" está desligado (em Configurações, aqui ao lado)."));
        }
        row.Children.Add(text);
        if (!_app.Settings.PhoneLink || phones.Count == 0)
        {
            var pair = new Button { Content = "Parear", Style = (Style)Resources["Action"], Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            pair.Click += (_, _) => _app.ShowPhonePairing();
            DockPanel.SetDock(pair, Dock.Right);
            row.Children.Insert(1, pair);
        }
        return Card("Para", row);
    }

    Border TextCard(bool ready)
    {
        (_text.Parent as Panel)?.Children.Remove(_text);
        var send = new Button { Content = "Enviar", Style = (Style)Resources["Action"], IsEnabled = ready, Margin = new Thickness(8, 0, 0, 0) };
        send.Click += (_, _) => SendText();
        var paste = new Button { Content = "Colar aqui", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
        paste.Click += (_, _) =>
        {
            try { if (Clipboard.ContainsText()) _text.SelectedText = Clipboard.GetText(); } catch { }
            _text.Focus();
        };
        var cancel = new Button { Content = "Fechar", Style = (Style)Resources["Action"] };
        cancel.Click += (_, _) => { _textOpen = false; Render(); };
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        _counter = Small("");
        _counter.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_counter, Dock.Left);
        buttons.Children.Add(_counter);
        foreach (var b in new[] { send, paste, cancel }) { DockPanel.SetDock(b, Dock.Right); buttons.Children.Add(b); }
        _text.PreviewKeyDown -= TextKeys;
        _text.PreviewKeyDown += TextKeys;
        UpdateCounter();
        var stack = new StackPanel();
        stack.Children.Add(_text);
        stack.Children.Add(buttons);
        stack.Children.Add(Small("Ctrl+Enter envia. Texto curto chega numa notificação com Copiar; o longo vai direto pra área de transferência do celular."));
        return Card("Texto", stack);
    }

    TextBlock? _counter;

    void UpdateCounter()
    {
        if (_counter != null) _counter.Text = $"{_text.Text.Length:N0} / {PhoneLink.MaxTextChars:N0}".Replace(',', '.');
    }

    void TextKeys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SendText();
        }
    }

    void SendText()
    {
        if (string.IsNullOrWhiteSpace(_text.Text)) { Done("Escreva ou cole um texto primeiro.", null); return; }
        var error = Island.SendTextToPhone(_text.Text);
        if (error == null) _text.Clear();
        Done(error, "Texto enviado");
    }

    void PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Enviar pro celular" };
        if (dialog.ShowDialog(this) == true) SendFiles(dialog.FileNames);
    }

    /// <summary>A folder goes as its files (not its subfolders), since the phone saves single files.</summary>
    void PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Enviar os arquivos de uma pasta pro celular" };
        if (dialog.ShowDialog(this) != true) return;
        string[] files;
        try
        {
            files = new DirectoryInfo(dialog.FolderName).GetFiles()
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Select(f => f.FullName).ToArray();
        }
        catch { Done("Não deu para ler a pasta.", null); return; }
        if (files.Length == 0) { Done("A pasta não tem arquivos (subpastas não vão).", null); return; }
        if (files.Length > PhoneLink.MaxBatchItems)
        {
            Done($"A pasta tem {files.Length} arquivos; vão até {PhoneLink.MaxBatchItems} de uma vez.", null);
            return;
        }
        SendFiles(files);
    }

    async void SendFiles(IReadOnlyList<string> paths)
    {
        var folders = paths.Where(Directory.Exists).ToList();
        var files = paths.Where(p => !Directory.Exists(p)).ToList();
        if (files.Count == 0) { Done("Pastas não vão: use o botão Pasta para mandar os arquivos dela.", null); return; }
        var error = await Island.SendFilesToPhoneAsync(files);
        Done(error, folders.Count > 0 ? "Enviado (as pastas ficaram de fora): aceite no celular" : "Enviado: aceite no celular");
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            Navigate("send");
            SendFiles(paths);
        }
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
        {
            _textOpen = true;
            _text.Text = text.Length > PhoneLink.MaxTextChars ? text[..PhoneLink.MaxTextChars] : text;
            Navigate("send");
            Render();
            _text.Focus();
        }
    }

    /// <summary>Shows how an action went under the buttons: the error, or <paramref name="ok"/>.</summary>
    void Done(string? error, string? ok)
    {
        _status = error ?? ok;
        _statusBad = error != null;
        if (_page == "send") Render();
    }

    // ───────────────────────── Recebidos / Enviados ─────────────────────────

    void History(Panel page, bool sent)
    {
        var entries = Island.History.Entries.Where(e => e.Sent == sent).ToList();
        var list = new StackPanel();
        if (entries.Count == 0)
            list.Children.Add(Note(sent
                ? "Nada enviado ainda. Use Enviar, solte arquivos no lado \"celular\" da ilha ou o botão de celular na bandeja."
                : "Nada recebido ainda. No celular, use Compartilhar → Mandar pro PC em qualquer app."));
        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0) list.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 6, 0, 6) });
            list.Children.Add(EntryRow(entries[i]));
        }
        var clear = new Button { Content = "Limpar lista", Style = (Style)Resources["Action"], Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        clear.Click += (_, _) =>
        {
            if (DarkDialog.Ask(this, sent ? "Limpar Enviados?" : "Limpar Recebidos?", "Os arquivos continuam onde estão; só a lista some.",
                    "Limpar", danger: true)) Island.History.Clear(sent);
        };
        var stack = new StackPanel();
        stack.Children.Add(Note(sent
            ? $"Os últimos {TransferHistory.MaxEntries} envios. A lista fica só neste PC, protegida para o seu usuário do Windows."
            : $"Os últimos {TransferHistory.MaxEntries}. Os arquivos ficam em {PhoneLink.ReceiveFolder}; a lista fica só neste PC, protegida para o seu usuário do Windows."));
        stack.Children.Add(list);
        if (entries.Count > 0) stack.Children.Add(clear);
        if (!sent)
        {
            var folder = new Button { Content = "Abrir a pasta", Style = (Style)Resources["Action"], Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            folder.Click += (_, _) => { Directory.CreateDirectory(PhoneLink.ReceiveFolder); Open(PhoneLink.ReceiveFolder); };
            stack.Children.Add(folder);
        }
        page.Children.Add(Card(sent ? "Do PC pro celular" : "Do celular pro PC", stack));
    }

    FrameworkElement EntryRow(TransferEntry entry)
    {
        var row = new DockPanel();
        var glyph = entry.IsText ? "" : entry.Count > 1 ? "" : "";
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20, Width = 36,
            VerticalAlignment = VerticalAlignment.Center, Foreground = Muted,
        };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Act(string label, Action click)
        {
            var b = new Button { Content = label, Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
            return b;
        }
        if (entry.Text is { } text)
        {
            Act("Copiar", () => { try { Clipboard.SetText(text); } catch { } });
            if (entry.Sent) Act("Mandar de novo", () => { Navigate("send"); Done(Island.SendTextToPhone(text), "Texto enviado"); }).IsEnabled = Island.PhoneReady;
        }
        else
        {
            var existing = entry.Paths.Where(File.Exists).ToList();
            if (existing.Count > 0)
                Act("Mostrar na pasta", () => Select(existing[0]));
            if (entry.Sent && existing.Count == entry.Paths.Count && existing.Count > 0)
                Act("Mandar de novo", () => { Navigate("send"); SendFiles(existing); }).IsEnabled = Island.PhoneReady;
        }
        var remove = new Button
        {
            Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
            Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0), ToolTip = "Tirar da lista",
        };
        remove.Click += (_, _) => Island.History.Remove(entry.Id, entry.Sent);
        buttons.Children.Add(remove);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        info.Children.Add(new TextBlock
        {
            Text = entry.Text is { } t ? Preview(t) : entry.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var detail = new TextBlock { FontSize = 12.5, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        detail.Inlines.Add(new System.Windows.Documents.Run(entry.Status) { Foreground = StatusBrush(entry.Status) });
        detail.Inlines.Add(new System.Windows.Documents.Run(
            $" · {When(entry.When)} · {(entry.IsText ? $"{entry.Text!.Length:N0} caracteres".Replace(',', '.') : SizeText(entry.Bytes))}"));
        info.Children.Add(detail);
        if (!entry.IsText && entry.Count > 1)
            info.Children.Add(Small(string.Join(", ", entry.Paths.Select(Path.GetFileName))));
        row.Children.Add(info);
        return row;
    }

    static Brush StatusBrush(string status) => status switch
    {
        "No celular" or "Recebido" or "Copiado" => Good,
        "Esperando o celular" or "Enviando" => Warn,
        "Recusado" or "Não aceito" => Muted,
        _ => Bad,
    };

    static string When(DateTime when) =>
        when.Date == DateTime.Today ? $"hoje {when:HH:mm}"
        : when.Date == DateTime.Today.AddDays(-1) ? $"ontem {when:HH:mm}"
        : $"{when:dd/MM HH:mm}";

    static string SizeText(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / (1024.0 * 1024.0):0.#} MB";

    static string Preview(string text)
    {
        var line = string.Join(' ', text.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    // Received files are only ever shown in Explorer, never opened from here.
    static void Select(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    // ───────────────────────── Configurações ─────────────────────────

    void Settings(Panel page)
    {
        var more = new Button { Content = "Abrir", Style = (Style)Resources["Action"] };
        more.Click += (_, _) => _app.ShowSettings("phone");
        page.Children.Add(Card("Celular",
            Toggle("Ligação com o celular", "Desligada, nada fica escutando na rede.", _app.Settings.PhoneLink, _ => { _app.TogglePhoneLink(); Watch(); }),
            Toggle("Arquivos e textos com o celular", "Desligado, a ilha recusa o que vier do celular e esta janela não envia nada.",
                _app.Settings.PhoneFiles, _ => _app.TogglePhoneFiles()),
            Row("Parear, repasse pela internet e app", "Nas Configurações da ilha, em Celular.", more)));
    }

    // ───────────────────────── building blocks ─────────────────────────

    TextBlock Small(string text) => new()
    {
        Text = text, Foreground = Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
    };

    Button Tile(string glyph, string label, bool enabled, Action click)
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8),
        });
        content.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        var button = new Button { Content = content, Style = (Style)Resources["Tile"], IsEnabled = enabled, Margin = new Thickness(0, 0, 12, 12) };
        button.Click += (_, _) => click();
        return button;
    }

    // Big rounded buttons, like LocalSend's selection row.
    static ResourceDictionary TileStyle() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="Tile" TargetType="Button">
            <Setter Property="Foreground" Value="White" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Width" Value="112" />
            <Setter Property="Height" Value="74" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="Bd" CornerRadius="12" Background="#FF2C2C32" BorderBrush="#FF3A3A40" BorderThickness="1">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF0A84FF" />
                      <Setter TargetName="Bd" Property="BorderBrush" Value="#FF0A84FF" />
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF0A6FD6" />
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter Property="Opacity" Value="0.4" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """);
}
