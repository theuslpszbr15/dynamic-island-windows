using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Services;

namespace DynamicIsland;

// The action card: a picture or an icon, a line, and a few buttons (a screenshot's Salvar / Celular / Ler texto /
// Traduzir, or "converter o vídeo?"). It waits while the pointer is over it.
public partial class IslandWindow
{
    sealed record CardAction(string Glyph, string Label, Action Run, bool Primary = false);

    bool _actionHovered;
    /// <summary>Which card is on screen (each <see cref="ShowActions"/> makes a new one), for whoever waits on an answer.</summary>
    object? _actionCard;

    object ShowActions(string title, string detail, IReadOnlyList<CardAction> actions, BitmapSource? thumb = null,
        string glyph = "\uE946", double seconds = 8, bool important = false)
    {
        var card = new object();
        if (!_ready) return card;
        _actionCard = card;
        ActionTitle.Text = title;
        ActionDetail.Text = detail;
        ActionGlyph.Text = glyph;
        ActionGlyph.Visibility = Vis(thumb == null);
        ActionThumb.Width = thumb != null ? 112 : 42;
        ActionThumb.Height = thumb != null ? 70 : 42;
        ActionThumb.Background = thumb != null
            ? new ImageBrush(thumb) { Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
        ActionBody.Margin = new Thickness(thumb != null ? 126 : 56, 0, 0, 0);
        ActionButtons.Children.Clear();
        foreach (var action in actions)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock
            {
                FontFamily = (FontFamily)FindResource("Icons"), Text = action.Glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
            });
            label.Children.Add(new TextBlock { Text = action.Label, FontSize = 12.5 });
            var button = new Button
            {
                Style = (Style)FindResource("Chip"), Height = 28, MinWidth = 0,
                Margin = new Thickness(ActionButtons.Children.Count == 0 ? 0 : 6, 0, 0, 0), Content = label,
            };
            if (action.Primary)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xEF, 0xEC));
                button.Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16));
            }
            var run = action.Run;
            button.Click += (_, e) =>
            {
                e.Handled = true;
                if (_transient == Transient.Action) TransientEnded();
                run();
            };
            ActionButtons.Children.Add(button);
        }
        _actionHovered = false;
        if (_expanded) SetExpanded(false);
        ShowTransient(Transient.Action, seconds, important);
        return card;
    }

    /// <summary>As wide as its buttons need, between 380 and 600.</summary>
    Size ActionSize()
    {
        ActionButtons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ActionTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var content = Math.Max(ActionButtons.DesiredSize.Width, Math.Min(ActionTitle.DesiredSize.Width, 300));
        return new Size(Math.Clamp(30 + ActionBody.Margin.Left + content, 380, 600), 96);
    }

    /// <summary>A card with buttons doesn't run out under the pointer; when the pointer leaves, it stays a few more seconds.</summary>
    void HoldWhileHovered(bool hovered)
    {
        if (hovered == _actionHovered) return;
        _actionHovered = hovered;
        if (_transient is not (Transient.Action or Transient.Translation)) return;
        _transientTimer.Stop();
        if (hovered || _menuOpen) return;
        _transientTimer.Interval = TimeSpan.FromSeconds(3);
        _transientTimer.Start();
    }
}
