using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// The Jarvis chat box under the island (Ctrl+Alt+J tapped): the conversation, a text box and a microphone button.
/// Closes when it loses focus; the conversation stays with the island.
/// </summary>
public sealed class JarvisWindow : Window
{
    public const double CardWidth = 460;

    readonly IslandWindow _island;
    readonly StackPanel _messages = new();
    readonly ScrollViewer _scroll;
    readonly TextBox _input;
    readonly TextBlock _state;
    readonly Button _mic;

    public JarvisWindow(IslandWindow island)
    {
        _island = island;
        Title = "Jarvis";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = CardWidth;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(DarkWindow.Styles());

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var close = new Button { Content = "\uE711", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Style = (Style)Resources["Action"], Padding = new Thickness(8, 4, 8, 4) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        _state = new TextBlock { Foreground = DarkWindow.Frozen(0xFF8E8E93), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(_state, Dock.Right);
        header.Children.Add(_state);
        header.Children.Add(new TextBlock { Text = "Jarvis", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        _scroll = new ScrollViewer { Content = _messages, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _input = new TextBox
        {
            FontSize = 14, Padding = new Thickness(10, 7, 10, 7), Background = DarkWindow.Frozen(0xFF2C2C30), Foreground = Brushes.White,
            CaretBrush = Brushes.White, BorderThickness = new Thickness(0), MaxLength = 2000, VerticalContentAlignment = VerticalAlignment.Center,
        };
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Send(); };
        _mic = new Button { Content = "\uE720", FontFamily = new FontFamily("Segoe MDL2 Assets"), Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0), ToolTip = "Falar" };
        _mic.Click += (_, _) => ToggleMic();
        var send = new Button { Content = "\uE724", FontFamily = new FontFamily("Segoe MDL2 Assets"), Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0), ToolTip = "Enviar (Enter)" };
        send.Click += (_, _) => Send();
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(send, Dock.Right);
        DockPanel.SetDock(_mic, Dock.Right);
        row.Children.Add(send);
        row.Children.Add(_mic);
        row.Children.Add(_input);

        var hint = new TextBlock
        {
            Text = "Enter envia · segure " + Hotkeys.Keys(((App)Application.Current).Settings, "jarvis") + " para falar · Esc fecha",
            FontSize = 11.5, Foreground = DarkWindow.Frozen(0xFF6E6E73), Margin = new Thickness(2, 8, 0, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_scroll);
        stack.Children.Add(row);
        stack.Children.Add(hint);
        var card = new Border
        {
            Background = DarkWindow.Frozen(0xFF1C1C1F), BorderBrush = DarkWindow.Frozen(0xFF3A3A40), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
            Child = stack,
        };
        Content = card;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => { if (!_island.JarvisListening) Close(); };
        Loaded += (_, _) => _input.Focus();
        _island.JarvisChanged += Refresh;
        Closed += (_, _) => _island.JarvisChanged -= Refresh;
        Refresh();
    }

    void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _island.JarvisState == "Pensando…") return;
        _input.Clear();
        _ = _island.AskJarvisAsync(text);
    }

    void ToggleMic()
    {
        if (Jarvis.Folder(Jarvis.Engine.Microphone) == null)
        {
            ((App)Application.Current).ShowSettings("jarvis");
            return;
        }
        if (_island.JarvisListening) _ = _island.StopListeningAsync();
        else _island.StartListening();
    }

    void Refresh()
    {
        _state.Text = _island.JarvisState;
        _mic.Foreground = _island.JarvisListening ? DarkWindow.Frozen(0xFFBF5AF2) : Brushes.White;
        _mic.ToolTip = Jarvis.Folder(Jarvis.Engine.Microphone) == null ? "Baixe o microfone em Configurações › Jarvis" : _island.JarvisListening ? "Parar e enviar" : "Falar";
        _messages.Children.Clear();
        if (_island.JarvisHistory.Count == 0)
            _messages.Children.Add(new TextBlock
            {
                Text = Jarvis.Folder(Jarvis.Engine.Ai) == null
                    ? "Pergunte a hora, a data, faça contas ou peça uma pesquisa. Para conversar sobre qualquer coisa, baixe a IA offline em Configurações › Jarvis."
                    : "Pergunte o que quiser. Tudo roda neste PC.",
                Foreground = DarkWindow.Frozen(0xFF8E8E93), TextWrapping = TextWrapping.Wrap,
            });
        foreach (var (user, text) in _island.JarvisHistory)
            _messages.Children.Add(new Border
            {
                Background = DarkWindow.Frozen(user ? 0xFF2C2C30 : 0x00000000), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(user ? 10 : 0, 6, user ? 10 : 0, 6), Margin = new Thickness(user ? 60 : 0, 4, 0, 4),
                HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = user ? DarkWindow.Frozen(0xFFD1D1D6) : Brushes.White },
            });
        if (_island.JarvisLast is { ActionUrl: { } url } last && Jarvis.SafeLink(url) && _island.JarvisHistory.LastOrDefault().Text == last.Text)
        {
            var open = new Button { Content = last.ActionLabel ?? "Abrir", Style = (Style)Resources["Action"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
            open.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _messages.Children.Add(open);
        }
        _scroll.ScrollToEnd();
    }
}
