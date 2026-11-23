using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

// The "Copiados" page: the newest copied texts (pinned first). A click copies again; pin keeps it, the phone icon
// sends it to the paired phone. "Buscar e ver tudo" opens a window that can take the keyboard.
public partial class IslandWindow
{
    const int ClipRows = 5;
    ClipboardHistory? _clips;
    ClipboardWindow? _clipWindow;

    public ClipboardHistory Clips
    {
        get
        {
            if (_clips != null) return _clips;
            _clips = new ClipboardHistory();
            _clips.Changed += () => { if (_expanded && _page == PageClipboard) UpdateClipboardUi(); };
            return _clips;
        }
    }

    bool CanSendClipToPhone => _settings.PhoneLink && _phone?.Running == true && _phone.ConnectedPhones > 0;

    void UpdateClipboardUi()
    {
        var items = Clips.Items;
        ClipList.Children.Clear();
        foreach (var item in items.Take(ClipRows)) ClipList.Children.Add(ClipRow(item, compact: true));
        ClipEmpty.Visibility = Vis(items.Count == 0);
        ClipCount.Text = items.Count == 0 ? "" : $"{items.Count} {(items.Count == 1 ? "item" : "itens")}";
    }

    /// <summary>One copied text: the click copies it; pin, phone and remove on the right.</summary>
    public FrameworkElement ClipRow(ClipEntry item, bool compact)
    {
        var preview = string.Join(' ', item.Text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var row = new Grid { Height = compact ? 28 : 34, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Clique para copiar" };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = preview, FontSize = compact ? 12.5 : 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(item.Pinned ? 18 : 0, 0, 8, 0),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        row.Children.Add(text);
        if (item.Pinned)
            row.Children.Add(new TextBlock
            {
                Text = "\uE718", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(JarvisColor),
            });

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(actions, 1);
        Button Small(string glyph, string tip, Action click)
        {
            var b = new Button { Content = glyph, Style = (Style)FindResource("TinyButton"), ToolTip = tip, Margin = new Thickness(4, 0, 0, 0) };
            b.Click += (_, e) => { e.Handled = true; click(); };
            return b;
        }
        actions.Children.Add(Small(item.Pinned ? "\uE77A" : "\uE718", item.Pinned ? "Desafixar" : "Fixar", () => Clips.TogglePin(item)));
        if (CanSendClipToPhone) actions.Children.Add(Small("\uE8EA", "Mandar para o celular", () => SendClipNow(item.Text)));
        actions.Children.Add(Small("\uE711", "Apagar", () => Clips.Remove(item)));
        row.Children.Add(actions);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CopyClip(item.Text);
        };
        return row;
    }

    public void CopyClip(string text)
    {
        try { Clipboard.SetText(text); }
        catch { return; } // another app holds the clipboard
        ShowInfo("\uE8C8", Preview(text), "Copiado de novo", glyphColor: JarvisColor);
    }

    public void SendClipNow(string text)
    {
        if (!CanSendClipToPhone) return;
        _phone!.PublishClip(text);
        ShowInfo("\uE8EA", Preview(text), "Mandado ao celular", glyphColor: AgentSession.Done);
    }

    void ClipSearch_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_clipWindow != null)
        {
            _clipWindow.Activate();
            return;
        }
        _clipWindow = new ClipboardWindow(this);
        _clipWindow.Closed += (_, _) => _clipWindow = null;
        _clipWindow.Left = Left + ActualWidth / 2 - ClipboardWindow.CardWidth / 2;
        _clipWindow.Top = Top + ExpandedFor(PageClipboard).Height + 6;
        _clipWindow.Show();
        _clipWindow.Activate();
    }
}
