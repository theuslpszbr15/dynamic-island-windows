using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>"Chat": the conversation with Jarvis as a chat screen (PC and phone turns), with Copiar and Refazer on each
/// message and a box to write from here.</summary>
public sealed partial class SettingsWindow
{
    const string ChatPage = "chat";

    void Chat(System.Windows.Controls.Panel page)
    {
        var island = _app.Island;
        var list = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var messages = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list,
        };

        var input = new TextBox
        {
            FontSize = 14, Padding = new Thickness(14, 10, 14, 10), Background = Hover, Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderThickness = new Thickness(1), BorderBrush = Line, MaxLength = 2000, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false,
            MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Center,
        };
        var placeholder = new TextBlock
        {
            Text = "Mensagem para o Jarvis (Enter envia, Shift+Enter quebra a linha)", Foreground = Muted, IsHitTestVisible = false,
            Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 14,
        };
        input.TextChanged += (_, _) => placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var field = new Grid();
        field.Children.Add(input);
        field.Children.Add(placeholder);
        var send = new Button { Content = "Enviar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        var stop = new Button { Content = "Parar", Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        stop.Click += (_, _) => island.StopJarvis();
        var attach = new Button
        {
            Content = "\uE723", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Style = (Style)Resources["Action"],
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Bottom,
            ToolTip = "Anexar um arquivo de texto ou uma imagem (também dá para arrastar aqui ou colar um print com Ctrl+V)",
        };
        var composerRow = new DockPanel();
        DockPanel.SetDock(attach, Dock.Left);
        DockPanel.SetDock(send, Dock.Right);
        DockPanel.SetDock(stop, Dock.Right);
        composerRow.Children.Add(attach);
        composerRow.Children.Add(send);
        composerRow.Children.Add(stop);
        composerRow.Children.Add(field);

        // the attachment waiting to go with the next message
        string? attachName = null, attachText = null;
        var chipText = new TextBlock { Foreground = Brushes.White, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 };
        var chipClose = new TextBlock
        {
            Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = Muted,
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Tirar o anexo",
        };
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        chipRow.Children.Add(new TextBlock { Text = "\uE723", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = Accent, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        chipRow.Children.Add(chipText);
        chipRow.Children.Add(chipClose);
        var chip = new Border
        {
            Background = Hover, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left, Child = chipRow, Visibility = Visibility.Collapsed,
        };
        void SetAttachment(string? name, string? text, string label)
        {
            attachName = name;
            attachText = text;
            chipText.Text = label;
            chip.Visibility = name != null ? Visibility.Visible : Visibility.Collapsed;
        }
        chipClose.MouseLeftButtonUp += (_, _) => SetAttachment(null, null, "");

        async Task AttachImage(System.Windows.Media.Imaging.BitmapSource image, string name)
        {
            SetAttachment(name, null, $"{name} · lendo o texto…");
            string? text;
            using (var bitmap = ScreenOcr.FromBitmapSource(image)) text = await ScreenOcr.ReadAsync(bitmap);
            if (attachName != name) return;
            text = text?.Trim();
            SetAttachment(name, string.IsNullOrEmpty(text) ? "(nenhum texto encontrado na imagem)" : text,
                string.IsNullOrEmpty(text) ? $"{name} · sem texto para ler" : $"{name} · {text.Length} letras lidas");
        }

        async Task AttachFile(string path)
        {
            var name = Path.GetFileName(path);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ChatImages.Contains(extension))
                {
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(path);
                    image.EndInit();
                    image.Freeze();
                    await AttachImage(image, name);
                }
                else if (ChatTexts.Contains(extension) && new FileInfo(path).Length < 2_000_000)
                {
                    var text = await File.ReadAllTextAsync(path);
                    var cut = text.Length > ChatAttachChars;
                    SetAttachment(name, cut ? text[..ChatAttachChars] + "\n(…cortado)" : text, $"{name} · {(cut ? "começo do arquivo" : $"{text.Length} letras")}");
                }
                else DarkDialog.Ask(this, "Anexar", "Dá para anexar arquivos de texto (txt, md, código, log, csv, json…) e imagens (png, jpg).", "OK", null);
            }
            catch (Exception ex) { DarkDialog.Ask(this, "Anexar", "Não consegui ler " + name + ": " + ex.Message, "OK", null); }
        }

        attach.Click += (_, _) =>
        {
            var pick = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Anexar ao Chat",
                Filter = "Textos e imagens|" + string.Join(";", ChatTexts.Concat(ChatImages).Select(e => "*" + e)) + "|Todos os arquivos|*.*",
            };
            if (pick.ShowDialog(this) == true) _ = AttachFile(pick.FileName);
        };

        var composer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        composer.Children.Add(chip);
        composer.Children.Add(composerRow);

        var copyAll = new Button { Content = "Copiar tudo", Style = (Style)Resources["Action"], Margin = new Thickness(0, 0, 8, 0) };
        copyAll.Click += (_, _) => Copy(string.Join("\n\n", island.JarvisHistory.Select(t => (t.User ? "Você: " : "Jarvis: ") + t.Text)));
        var clear = new Button { Content = "Nova conversa", Style = (Style)Resources["Action"] };
        clear.Click += (_, _) =>
        {
            if (DarkDialog.Ask(this, "Nova conversa", "A conversa some daqui e o Jarvis começa do zero. O que ele lembra sobre você fica.", "Apagar", danger: true))
                island.ClearJarvisConversation();
        };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(copyAll);
        actions.Children.Add(clear);
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(actions);
        var search = new TextBox
        {
            FontSize = 13, Padding = new Thickness(30, 6, 10, 6), Background = Hover, Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderThickness = new Thickness(1), BorderBrush = Line, Width = 280, HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = JarvisBrain.HasKey ? "No PC e no celular. Ele age no PC como pela ilha." : "Sem a chave da Groq ele só conversa (Jarvis (IA) › Cérebro).",
        };
        var searchHint = new TextBlock { Text = "Buscar na conversa", Foreground = Muted, FontSize = 13, IsHitTestVisible = false, Margin = new Thickness(31, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var searchGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        searchGrid.Children.Add(search);
        searchGrid.Children.Add(new TextBlock
        {
            Text = "\uE721", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = Muted, IsHitTestVisible = false,
            Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        searchGrid.Children.Add(searchHint);
        top.Children.Add(searchGrid);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(messages, 1);
        Grid.SetRow(composer, 2);
        layout.Children.Add(top);
        layout.Children.Add(messages);
        layout.Children.Add(composer);
        // the box stays at the bottom of the window: the page fills the view instead of growing with the messages
        void Size() => layout.Height = Math.Max(380, _scroll.ViewportHeight - 122);
        void Resized(object? s, SizeChangedEventArgs e) => Size();
        Size();

        bool Busy() => island.JarvisState == "Pensando…" || island.ComputerBusy;

        void Ask(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || Busy()) return;
            _ = island.AskJarvisAsync(text, fromChat: true);
        }

        void Send()
        {
            var text = input.Text.Trim();
            if (Busy() || (text.Length == 0 && attachName == null)) return;
            // still reading the image: wait for it
            if (attachName != null && attachText == null) return;
            input.Clear();
            if (attachName == null)
            {
                Ask(text);
                return;
            }
            if (text.Length == 0) text = "O que é isto? Explique de forma simples.";
            var full = $"{text}\n\n[Anexo: {attachName}]\n{attachText}";
            var shown = $"[{attachName}] {text}";
            SetAttachment(null, null, "");
            _ = island.AskJarvisAsync(full, fromChat: true, shown: shown);
        }

        send.Click += (_, _) => { Send(); input.Focus(); };
        input.PreviewKeyDown += (_, e) =>
        {
            // a print in the clipboard goes in as an attachment
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                e.Handled = true;
                image.Freeze();
                _ = AttachImage(image, "print colado");
                return;
            }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                var at = input.CaretIndex;
                input.Text = input.Text.Insert(at, "\n");
                input.CaretIndex = at + 1;
                return;
            }
            Send();
        };
        layout.AllowDrop = true;
        layout.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        layout.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = AttachFile(files[0]);
            e.Handled = true;
        };
        // TextBox handles drops itself (as text): files dropped on it come here too
        input.PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        input.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
            e.Handled = true;
            _ = AttachFile(files[0]);
        };

        Border Small(string glyph, string tip, Action click)
        {
            var icon = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var b = new Border
            {
                Width = 30, Height = 26, CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                ToolTip = tip, Margin = new Thickness(0, 0, 2, 0), Child = icon,
            };
            b.MouseEnter += (_, _) => { b.Background = Hover; icon.Foreground = Brushes.White; };
            b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; icon.Foreground = Muted; };
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (b.IsEnabled) click(); };
            return b;
        }

        UIElement Message(int index, bool user, string text, bool last)
        {
            var body = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, Foreground = Brushes.White,
                BorderThickness = new Thickness(0), FontSize = user ? 13.5 : 14.5, Padding = new Thickness(0), CaretBrush = Brushes.Transparent,
            };
            // Refazer on an answer asks the question before it again
            var asked = user ? text : island.JarvisHistory.Take(index).LastOrDefault(t => t.User).Text;
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 2, 0, 0), Opacity = last ? 1 : 0 };
            Border? copied = null;
            copied = Small("\uE8C8", "Copiar", () =>
            {
                Copy(text);
                var icon = (TextBlock)copied!.Child;
                icon.Text = "\uE73E";
                var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                back.Tick += (_, _) => { back.Stop(); icon.Text = "\uE8C8"; };
                back.Start();
            });
            tools.Children.Add(copied);
            if (asked != null)
            {
                var again = Small("\uE72C", user ? "Refazer: pedir de novo" : "Refazer: gerar a resposta de novo", () => Ask(asked));
                again.IsEnabled = !Busy();
                tools.Children.Add(again);
            }
            var stack = new StackPanel { Margin = user ? new Thickness(120, 4, 0, 4) : new Thickness(0, 4, 120, 6) };
            if (user)
            {
                tools.HorizontalAlignment = HorizontalAlignment.Right;
                tools.Margin = new Thickness(0, 2, -6, 0);
                stack.Children.Add(new Border
                {
                    Background = Hover, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 8, 14, 9), HorizontalAlignment = HorizontalAlignment.Right,
                    Child = body,
                });
            }
            else
            {
                stack.Children.Add(new TextBlock { Text = "Jarvis", Foreground = Accent, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
                stack.Children.Add(body);
            }
            stack.Children.Add(tools);
            var row = new Border { Background = Brushes.Transparent, Child = stack };
            if (!last)
            {
                row.MouseEnter += (_, _) => tools.Opacity = 1;
                row.MouseLeave += (_, _) => tools.Opacity = 0;
            }
            return row;
        }

        string shown = "";
        void Fill()
        {
            var turns = island.JarvisHistory.ToList();
            var busy = Busy();
            var query = search.Text.Trim();
            var signature = $"{turns.Count}|{(turns.Count > 0 ? turns[^1].Text.GetHashCode() : 0)}|{busy}|{query}";
            send.IsEnabled = !busy;
            stop.Visibility = busy || island.JarvisState == "Falando…" ? Visibility.Visible : Visibility.Collapsed;
            copyAll.IsEnabled = clear.IsEnabled = turns.Count > 0;
            if (signature == shown) return;
            shown = signature;
            list.Children.Clear();
            if (turns.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = "Nenhuma conversa ainda. Escreva abaixo, ou fale com o Jarvis pela ilha ou pelo celular: tudo aparece aqui.",
                    Foreground = Muted, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0),
                });
            if (query.Length > 0)
            {
                var compare = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
                const System.Globalization.CompareOptions Loose = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
                // a match brings its pair along: the question with its answer
                var keep = new SortedSet<int>();
                for (var i = 0; i < turns.Count; i++)
                    if (compare.IndexOf(turns[i].Text, query, Loose) >= 0)
                    {
                        keep.Add(i);
                        var pair = turns[i].User ? i + 1 : i - 1;
                        if (pair >= 0 && pair < turns.Count) keep.Add(pair);
                    }
                list.Children.Add(new TextBlock
                {
                    Text = keep.Count == 0 ? $"Nada encontrado para \"{query}\"." : $"Mensagens com \"{query}\":",
                    Foreground = Muted, FontSize = 13, Margin = new Thickness(0, 4, 0, 8),
                });
                foreach (var i in keep) list.Children.Add(Message(i, turns[i].User, turns[i].Text, false));
                messages.ScrollToTop();
                return;
            }
            for (var i = 0; i < turns.Count; i++)
                list.Children.Add(Message(i, turns[i].User, turns[i].Text, i == turns.Count - 1 && !busy));
            if (busy)
                list.Children.Add(new TextBlock { Text = "Jarvis está pensando…", Foreground = Muted, FontSize = 14, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 8, 0, 8) });
            messages.ScrollToEnd();
        }
        Fill();
        search.TextChanged += (_, _) =>
        {
            searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Fill();
        };
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) search.Clear(); };
        layout.Loaded += (_, _) =>
        {
            island.JarvisChanged += Fill;
            _scroll.SizeChanged += Resized;
            Size();
            input.Focus();
        };
        layout.Unloaded += (_, _) =>
        {
            island.JarvisChanged -= Fill;
            _scroll.SizeChanged -= Resized;
        };
        page.Children.Add(layout);
    }

    static readonly HashSet<string> ChatImages = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];
    static readonly HashSet<string> ChatTexts =
        [".txt", ".md", ".log", ".csv", ".json", ".xml", ".yaml", ".yml", ".ini", ".cs", ".js", ".ts", ".py", ".lua", ".html", ".css", ".kt", ".java", ".sql", ".ps1", ".bat"];
    const int ChatAttachChars = 6000;

    static void Copy(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }
}
