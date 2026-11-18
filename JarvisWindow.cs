using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// The bar for typing to Jarvis, just under the open island (its Jarvis page shows the conversation). Enter sends,
/// Esc or clicking elsewhere closes it.
/// </summary>
public sealed class JarvisWindow : Window
{
    public const double CardWidth = 460;

    readonly IslandWindow _island;
    readonly TextBox _input;
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

        _input = new TextBox
        {
            FontSize = 14, Padding = new Thickness(12, 8, 12, 8), Background = DarkWindow.Frozen(0xFF2C2C30), Foreground = Brushes.White,
            CaretBrush = Brushes.White, BorderThickness = new Thickness(0), MaxLength = 2000, VerticalContentAlignment = VerticalAlignment.Center,
        };
        var placeholder = new TextBlock
        {
            Text = "Pergunte ao Jarvis…", Foreground = DarkWindow.Frozen(0xFF6E6E73), IsHitTestVisible = false,
            Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        _input.TextChanged += (_, _) => placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Send(); };
        var field = new Grid();
        field.Children.Add(_input);
        field.Children.Add(placeholder);

        _mic = new Button { Content = "\uE720", FontFamily = new FontFamily("Segoe MDL2 Assets"), Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
        _mic.Click += (_, _) => ToggleMic();
        var send = new Button { Content = "\uE724", FontFamily = new FontFamily("Segoe MDL2 Assets"), Style = (Style)Resources["Action"], Margin = new Thickness(8, 0, 0, 0), ToolTip = "Enviar (Enter)" };
        send.Click += (_, _) => Send();
        var row = new DockPanel();
        DockPanel.SetDock(send, Dock.Right);
        DockPanel.SetDock(_mic, Dock.Right);
        row.Children.Add(send);
        row.Children.Add(_mic);
        row.Children.Add(field);

        Content = new Border
        {
            Background = DarkWindow.Frozen(0xFF1C1C1F), BorderBrush = DarkWindow.Frozen(0xFF3A3A40), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(10), Margin = new Thickness(10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
            Child = row,
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) =>
        {
            // clicking elsewhere while talking sends what was said
            if (_island.JarvisListening) _ = _island.StopListeningAsync();
            Close();
        };
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
        if (!IslandWindow.CanListen)
        {
            ((App)Application.Current).ShowSettings("jarvis");
            return;
        }
        if (_island.JarvisListening) _ = _island.StopListeningAsync();
        else _island.StartListening();
    }

    void Refresh()
    {
        _mic.Foreground = _island.JarvisListening ? DarkWindow.Frozen(0xFFE99A59) : Brushes.White;
        _mic.ToolTip = !IslandWindow.CanListen ? "Para falar: chave da Groq ou microfone em Configurações › Jarvis"
            : _island.JarvisListening ? "Parar e enviar" : "Falar";
    }
}
