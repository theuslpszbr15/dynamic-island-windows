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
        var composer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(send, Dock.Right);
        DockPanel.SetDock(stop, Dock.Right);
        composer.Children.Add(send);
        composer.Children.Add(stop);
        composer.Children.Add(field);

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
        top.Children.Add(new TextBlock
        {
            Text = JarvisBrain.HasKey ? "No PC e no celular. Ele age no PC como pela ilha." : "Sem a chave da Groq ele só conversa (Jarvis (IA) › Cérebro).",
            Foreground = Muted, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });

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

        send.Click += (_, _) => { var text = input.Text; input.Clear(); Ask(text); input.Focus(); };
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                var at = input.CaretIndex;
                input.Text = input.Text.Insert(at, "\n");
                input.CaretIndex = at + 1;
                return;
            }
            var text = input.Text;
            input.Clear();
            Ask(text);
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
            var signature = $"{turns.Count}|{(turns.Count > 0 ? turns[^1].Text.GetHashCode() : 0)}|{busy}";
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
            for (var i = 0; i < turns.Count; i++)
                list.Children.Add(Message(i, turns[i].User, turns[i].Text, i == turns.Count - 1 && !busy));
            if (busy)
                list.Children.Add(new TextBlock { Text = "Jarvis está pensando…", Foreground = Muted, FontSize = 14, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 8, 0, 8) });
            messages.ScrollToEnd();
        }
        Fill();
        // JarvisChanged is raised on the UI thread
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

    static void Copy(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }
}
