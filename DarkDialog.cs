using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>
/// The island's own yes/no box, in place of the Windows MessageBox: same dark card and buttons as the Configurações,
/// no title bar in the Windows accent colour.
/// </summary>
public sealed class DarkDialog : Window
{
    bool _ok;

    DarkDialog(string title, string message, string ok, string? cancel, bool danger)
    {
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(DarkWindow.Styles());

        var stack = new StackPanel { Width = 380 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock
        {
            Text = message, Foreground = DarkWindow.Frozen(0xFFB4B4BA), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (cancel != null)
        {
            var no = new Button { Content = cancel, Style = (Style)Resources["Action"], IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            no.Click += (_, _) => Close();
            buttons.Children.Add(no);
        }
        var yes = new Button { Content = ok, Style = (Style)Resources["Action"], IsDefault = true };
        if (danger) yes.Foreground = DarkWindow.Frozen(0xFFFF6961);
        yes.Click += (_, _) => { _ok = true; Close(); };
        buttons.Children.Add(yes);
        stack.Children.Add(buttons);

        var card = new Border
        {
            Background = DarkWindow.Frozen(0xFF1C1C1F), BorderBrush = DarkWindow.Frozen(0xFF3A3A40), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
            Child = stack,
        };
        card.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        Content = card;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>True when the person picks <paramref name="ok"/>. Without <paramref name="cancel"/> it is just a notice.</summary>
    public static bool Ask(Window? owner, string title, string message, string ok = "OK", string? cancel = "Cancelar", bool danger = false)
    {
        var dialog = new DarkDialog(title, message, ok, cancel, danger);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._ok;
    }
}
