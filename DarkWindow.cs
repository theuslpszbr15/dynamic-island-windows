using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using DynamicIsland.Services;

namespace DynamicIsland;
/// <summary>The dark window of the island (Configurações, Celular): a section list on the left, the page on the right,
/// and a flat title bar of our own. Subclasses fill each page in <see cref="Fill"/> and call <see cref="Render"/>.</summary>
public abstract class DarkWindow : Window
{
    protected static readonly Brush Ground = Frozen(0xFF141416), Panel = Frozen(0xFF1C1C1F), CardBg = Frozen(0xFF222226),
        Line = Frozen(0xFF2E2E33), Muted = Frozen(0xFF9A9AA0), Accent = Frozen(0xFF0A84FF), Hover = Frozen(0xFF2A2A2F);

    readonly (string Id, string Glyph, string Title)[] _sections;
    readonly StackPanel _nav = new() { Margin = new Thickness(10, 8, 10, 10) };
    protected readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(34, 26, 34, 30) };
    protected string _page;

    protected DarkWindow(string windowTitle, string barTitle, (string Id, string Glyph, string Title)[] sections)
    {
        _sections = sections;
        _page = sections[0].Id;
        Title = windowTitle;
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

        // No Windows title bar: a dark bar of our own (flat buttons, like Obsidian and Maestri) that still drags,
        // snaps, resizes and double-click maximizes through WindowChrome.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = CaptionHeight,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1), // keeps the DWM shadow on Windows 10/11
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        var sidebar = new DockPanel { Background = Panel };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 18, 16, 16) };
        header.Children.Add(new Viewbox { Width = 38, Height = 38, Child = new PetView { FixedSpecies = PetSpecies.Bit, BaseMood = PetView.Mood.Happy } });
        var name = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "Dynamic Island", FontSize = 16, FontWeight = FontWeights.SemiBold });
        name.Children.Add(new TextBlock { Text = "versão " + Updater.Current.ToString(3), FontSize = 12, Foreground = Muted });
        header.Children.Add(name);
        DockPanel.SetDock(header, Dock.Top);
        sidebar.Children.Add(header);
        foreach (var (id, glyph, title) in sections) _nav.Children.Add(NavButton(id, glyph, title));
        sidebar.Children.Add(_nav);

        var root = new Grid { Background = Ground };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CaptionHeight) });
        root.RowDefinitions.Add(new RowDefinition());
        Grid.SetRowSpan(sidebar, 2);
        root.Children.Add(sidebar);
        // The bar over the content: same tone as the sidebar with a hairline under it (like Obsidian's tab
        // strip), the window's name on the left (drag area) and the buttons on the right.
        var bar = new Border { Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        var barContent = new Grid();
        barContent.Children.Add(new TextBlock
        {
            Text = barTitle, FontSize = 12.5, Foreground = Muted, Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        });
        barContent.Children.Add(CaptionButtons());
        bar.Child = barContent;
        Grid.SetColumn(bar, 1);
        root.Children.Add(bar);
        Grid.SetColumn(_scroll, 1);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);
        _scroll.Padding = new Thickness(34, 22, 34, 30);
        // A hairline edge, since there is no Windows frame any more; maximized, WPF overhangs the screen by
        // the resize border, so the content is pulled back in.
        var frame = new Border { BorderBrush = Line, BorderThickness = new Thickness(1), Child = root };
        Content = frame;
        void Fit()
        {
            frame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
            frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            if (_maxGlyph != null)
            {
                _maxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
                ((FrameworkElement)_maxGlyph.Parent).ToolTip = WindowState == WindowState.Maximized ? "Restaurar" : "Maximizar";
            }
        }
        StateChanged += (_, _) => Fit();
        Activated += (_, _) => SetCaptionActive(true);
        Deactivated += (_, _) => SetCaptionActive(false);
    }

    const double CaptionHeight = 38;
    static readonly Brush CloseHover = Frozen(0xFFE81123), CaptionIdle = Frozen(0xFFB4B4BA);
    TextBlock? _maxGlyph;
    readonly List<TextBlock> _captionGlyphs = [];

    /// <summary>Minimize, maximize and close as flat, thin buttons on the right (like Obsidian and Maestri):
    /// grey symbols, a soft highlight on hover, red under close.</summary>
    FrameworkElement CaptionButtons()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        TextBlock Add(string glyph, string tip, Brush hover, Action click)
        {
            var symbol = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = CaptionIdle,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            _captionGlyphs.Add(symbol);
            var button = new Border { Width = 46, Height = CaptionHeight, Background = Brushes.Transparent, Child = symbol, ToolTip = tip };
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            button.MouseEnter += (_, _) => { button.Background = hover; symbol.Foreground = Brushes.White; };
            button.MouseLeave += (_, _) => { button.Background = Brushes.Transparent; symbol.Foreground = IsActive ? CaptionIdle : Muted; };
            button.MouseLeftButtonDown += (_, e) => { e.Handled = true; button.CaptureMouse(); }; // no window drag
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                button.ReleaseMouseCapture();
                if (button.IsMouseOver) click();
            };
            row.Children.Add(button);
            return symbol;
        }
        Add("", "Minimizar", Hover, () => WindowState = WindowState.Minimized);
        _maxGlyph = Add("", "Maximizar", Hover, () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        Add("", "Fechar", CloseHover, Close);
        return row;
    }

    // Symbols dim when the window isn't in front, like the system bar.
    void SetCaptionActive(bool active)
    {
        foreach (var g in _captionGlyphs) g.Foreground = active ? CaptionIdle : Muted;
    }

    public void Navigate(string page)
    {
        if (_sections.All(s => s.Id != page)) return;
        _page = page;
        Render();
    }

    /// <summary>Builds the current page again (keeping the scroll position).</summary>
    protected void Render()
    {
        foreach (RadioButton b in _nav.Children) b.IsChecked = (string)b.Tag == _page;
        var offset = _scroll.VerticalOffset;
        var page = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        var (_, _, title) = _sections.First(s => s.Id == _page);
        page.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 18) });
        Fill(_page, page);
        _scroll.Content = page;
        _scroll.ScrollToVerticalOffset(offset);
    }

    protected abstract void Fill(string section, Panel page);

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

    protected Border Card(string title, params UIElement[] rows)
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

    protected TextBlock Note(string text) => new()
    {
        Text = text, Foreground = Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
    };

    protected Grid Row(string title, string? description, FrameworkElement? control)
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

    protected Grid Toggle(string title, string description, bool value, Action<bool> changed, FrameworkElement? extra = null, bool enabled = true)
    {
        var toggle = new CheckBox { IsChecked = value, Style = (Style)Resources["Switch"], IsEnabled = enabled };
        toggle.Click += (_, _) => { changed(toggle.IsChecked == true); Render(); };
        if (extra == null) return Row(title, description, toggle);
        var both = new StackPanel { Orientation = Orientation.Horizontal };
        both.Children.Add(extra);
        both.Children.Add(toggle);
        return Row(title, description, both);
    }

    protected Grid Choice<T>((T Value, string Label)[] options, T current, Action<T> changed, string title, string description)
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

    protected Grid Choice<T>(string title, string description, (T Value, string Label)[] options, T current, Action<T> changed) =>
        Choice(options, current, changed, title, description);

    protected static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    protected static SolidColorBrush Frozen(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    // Dark controls: a switch, sidebar items, buttons and a combo box with a dark drop-down.
    protected static ResourceDictionary Styles() => (ResourceDictionary)XamlReader.Parse("""
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
