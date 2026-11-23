using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>Searches everything in the clipboard history (the island itself can't take the keyboard). Enter copies the
/// first result; Esc or clicking elsewhere closes it.</summary>
public sealed class ClipboardWindow : Window
{
    public const double CardWidth = 480;

    readonly IslandWindow _island;
    readonly TextBox _search;
    readonly StackPanel _list = new();

    public ClipboardWindow(IslandWindow island)
    {
        _island = island;
        Title = "Copiados";
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

        _search = new TextBox
        {
            FontSize = 14, Padding = new Thickness(12, 8, 12, 8), Background = DarkWindow.Frozen(0xFF2C2C30), Foreground = Brushes.White,
            CaretBrush = Brushes.White, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center,
        };
        var placeholder = new TextBlock
        {
            Text = "Buscar no que você copiou…", Foreground = DarkWindow.Frozen(0xFF6E6E73), IsHitTestVisible = false,
            Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        _search.TextChanged += (_, _) =>
        {
            placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Fill();
        };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || _island.Clips.Search(_search.Text) is not { Count: > 0 } found) return;
            _island.CopyClip(found[0].Text);
            Close();
        };
        var field = new Grid();
        field.Children.Add(_search);
        field.Children.Add(placeholder);

        var clear = new Button { Content = "Limpar recentes", Style = (Style)Resources["Action"], Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        clear.Click += (_, _) => _island.Clips.Clear();
        var stack = new StackPanel();
        stack.Children.Add(field);
        stack.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 360, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        stack.Children.Add(clear);

        Content = new Border
        {
            Background = DarkWindow.Frozen(0xFF1C1C1F), BorderBrush = DarkWindow.Frozen(0xFF3A3A40), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(12), Margin = new Thickness(10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
            Child = stack,
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => Close();
        Loaded += (_, _) => _search.Focus();
        _island.Clips.Changed += Fill;
        Closed += (_, _) => _island.Clips.Changed -= Fill;
        Fill();
    }

    void Fill()
    {
        _list.Children.Clear();
        var found = _island.Clips.Search(_search.Text);
        foreach (var item in found) _list.Children.Add(_island.ClipRow(item, compact: false));
        if (found.Count == 0)
            _list.Children.Add(new TextBlock
            {
                Text = _search.Text.Length == 0 ? "Nada copiado ainda." : "Nada encontrado.", Foreground = DarkWindow.Frozen(0xFF8E8E93),
                Margin = new Thickness(4, 6, 0, 6),
            });
    }
}
