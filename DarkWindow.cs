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
    protected static readonly Brush Ground = Frozen(0xFF151515), Panel = Frozen(0xFF101010), CardBg = Frozen(0xFF202020),
        Line = Frozen(0xFF363635), Muted = Frozen(0xFFA19F9A), Accent = Frozen(0xFFD97757), Hover = Frozen(0xFF2C2C2B), Text = Frozen(0xFFF0EFEC);
    /// <summary>Serif for page titles (Newsreader in the mockups; Georgia ships with Windows).</summary>
    protected static readonly FontFamily Serif = new("Newsreader, Georgia, Cambria");
    /// <summary>The soft orange glow in the corner of the page; off when the setting is off.</summary>
    protected virtual bool GlowOn => true;
    readonly Border _glow = new() { IsHitTestVisible = false };
    protected void ApplyGlow() => _glow.Background = GlowOn ? new RadialGradientBrush
    {
        Center = new Point(0.92, 0.0), GradientOrigin = new Point(0.92, 0.0), RadiusX = 0.75, RadiusY = 0.7,
        GradientStops = { new GradientStop(Color.FromArgb(0x3A, 0xD9, 0x77, 0x57), 0), new GradientStop(Color.FromArgb(0x00, 0xD9, 0x77, 0x57), 1) },
    } : Brushes.Transparent;

    readonly (string Id, string Glyph, string Title)[] _sections;
    readonly StackPanel _nav = new() { Margin = new Thickness(10, 4, 10, 10) };
    protected readonly TextBox _searchBox = new();
    protected string _searchQuery = "";
    protected readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(34, 26, 34, 30) };
    protected string _page;

    protected DarkWindow(string windowTitle, string barTitle, (string Id, string Glyph, string Title)[] sections)
    {
        _sections = sections;
        _page = sections.Length > 0 ? sections[0].Id : "";
        Title = windowTitle;
        Width = 940;
        Height = 680;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ground;
        Foreground = Text;
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
        var logo = new Border { Width = 34, Height = 20, CornerRadius = new CornerRadius(10), Background = Brushes.Black, BorderBrush = Line, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        logo.Child = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Accent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(5, 0, 0, 0) };
        header.Children.Add(logo);
        var name = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "Dynamic Island", FontSize = 20, FontFamily = Serif });
        name.Children.Add(new TextBlock { Text = "versão " + Updater.Current.ToString(3), FontSize = 12, Foreground = Muted });
        header.Children.Add(name);
        DockPanel.SetDock(header, Dock.Top);
        sidebar.Children.Add(header);

        if (sections.Length > 0)
        {
            var searchWrap = new Border
            {
                Background = Hover, CornerRadius = new CornerRadius(8), BorderBrush = Line, BorderThickness = new Thickness(1),
                Margin = new Thickness(14, 0, 14, 6), Padding = new Thickness(10, 6, 10, 6),
            };
            var searchGrid = new Grid();
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition());
            searchGrid.Children.Add(new TextBlock
            {
                Text = "\uE721", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center,
            });
            var hint = new TextBlock
            {
                Text = "Buscar (Ctrl+K)…", FontSize = 12.5, Foreground = Muted, IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _searchBox.Background = Brushes.Transparent;
            _searchBox.Foreground = Brushes.White;
            _searchBox.CaretBrush = Brushes.White;
            _searchBox.BorderThickness = new Thickness(0);
            _searchBox.FontSize = 12.5;
            _searchBox.VerticalAlignment = VerticalAlignment.Center;
            _searchBox.TextChanged += (_, _) =>
            {
                hint.Visibility = _searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _searchQuery = _searchBox.Text.Trim();
                Render();
            };
            _searchBox.GotKeyboardFocus += (_, _) => searchWrap.BorderBrush = Accent;
            _searchBox.LostKeyboardFocus += (_, _) => searchWrap.BorderBrush = Line;
            Grid.SetColumn(hint, 1);
            Grid.SetColumn(_searchBox, 1);
            searchGrid.Children.Add(hint);
            searchGrid.Children.Add(_searchBox);
            searchWrap.Child = searchGrid;
            DockPanel.SetDock(searchWrap, Dock.Top);
            sidebar.Children.Add(searchWrap);
        }

        foreach (var (id, glyph, title) in sections) _nav.Children.Add(NavButton(id, glyph, title));
        sidebar.Children.Add(_nav);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control && sections.Length > 0)
            {
                e.Handled = true;
                _searchBox.Focus();
                _searchBox.SelectAll();
            }
            else if (e.Key == Key.Escape && _searchBox.Text.Length > 0)
            {
                e.Handled = true;
                _searchBox.Text = "";
                Keyboard.ClearFocus();
            }
        };

        var root = _root;
        root.Background = Ground;
        // no sections (the viewer): no sidebar, the bar and the body take the whole width
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sections.Length > 0 ? 240 : 0) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CaptionHeight) });
        root.RowDefinitions.Add(new RowDefinition());
        Grid.SetRowSpan(sidebar, 2);
        if (sections.Length > 0) root.Children.Add(sidebar);
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
        ApplyGlow();
        Grid.SetColumn(_glow, 1);
        Grid.SetRow(_glow, 1);
        root.Children.Add(_glow);
        Grid.SetColumn(_scroll, 1);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);
        _scroll.Padding = new Thickness(34, 22, 34, 30);
        // A hairline edge, since there is no Windows frame any more; maximized, WPF overhangs the screen by
        // the resize border, so the content is pulled back in.
        var frame = new Border { BorderBrush = Line, BorderThickness = new Thickness(1), Child = root };
        Content = frame;
        // Whatever DWM still draws (the 1 px frame, the caption while snapping or on Windows 10) goes dark too,
        // instead of taking the Windows accent colour when "show accent colour on title bars" is on.
        SourceInitialized += (_, _) => DarkFrame.Apply(this, 0xFF101010, 0xFF363635);
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

    readonly Grid _root = new();

    /// <summary>Puts <paramref name="body"/> under the bar instead of the scrolling page (the viewer, which sizes itself).</summary>
    protected void SetBody(UIElement body)
    {
        _root.Children.Remove(_scroll);
        Grid.SetColumn(body, 1);
        Grid.SetRow(body, 1);
        _root.Children.Add(body);
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
        if (_searchBox.Text.Length > 0) _searchBox.Text = "";
        _page = page;
        Render();
    }

    /// <summary>Builds the current page again (keeping the scroll position).</summary>
    protected void Render()
    {
        var searching = !string.IsNullOrEmpty(_searchQuery);
        foreach (RadioButton b in _nav.Children) b.IsChecked = !searching && (string)b.Tag == _page;
        var offset = _scroll.VerticalOffset;
        var page = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        if (searching)
        {
            page.Children.Add(new TextBlock { Text = $"Resultados para \"{_searchQuery}\"", FontSize = 28, FontFamily = Serif, Margin = new Thickness(0, 0, 0, 18) });
            var matched = 0;
            foreach (var (id, _, secTitle) in _sections)
            {
                if (id == "home") continue;
                var temp = new StackPanel();
                Fill(id, temp);
                foreach (var child in temp.Children.OfType<FrameworkElement>().ToList())
                {
                    if (!MatchesSearch(child, _searchQuery) && !secTitle.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)) continue;
                    temp.Children.Remove(child);
                    page.Children.Add(new TextBlock { Text = secTitle, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Accent, Margin = new Thickness(2, 4, 0, 6) });
                    page.Children.Add(child);
                    matched++;
                }
            }
            if (matched == 0)
                page.Children.Add(Note("Nenhuma opção encontrada para essa busca. Tente termos como \"aurora\", \"jarvis\", \"água\", \"tarefas\", \"atalho\" ou \"discord\"."));
            _scroll.Content = page;
            _scroll.ScrollToTop();
            return;
        }
        var (_, _, title) = _sections.First(s => s.Id == _page);
        page.Children.Add(new TextBlock { Text = title, FontSize = 36, FontFamily = Serif, Margin = new Thickness(0, 0, 0, 20) });
        Fill(_page, page);
        _scroll.Content = page;
        _scroll.ScrollToVerticalOffset(offset);
    }

    static bool MatchesSearch(DependencyObject root, string query)
    {
        if (root is TextBlock tb && tb.Text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (root is ContentControl { Content: string s } && s.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var count = VisualTreeHelper.GetChildrenCount(root);
        if (count == 0 && root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
                if (MatchesSearch(child, query)) return true;
            return false;
        }
        if (count == 0 && root is Decorator dec && dec.Child != null)
            return MatchesSearch(dec.Child, query);
        for (var i = 0; i < count; i++)
            if (MatchesSearch(VisualTreeHelper.GetChild(root, i), query)) return true;
        return false;
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
        stack.Children.Add(new TextBlock { Text = title, FontSize = 20, FontFamily = Serif, Margin = new Thickness(0, 0, 0, 10) });
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0 && rows[i] is Grid { Tag: "row" } && rows[i - 1] is Grid { Tag: "row" })
                stack.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 4, 0, 4) });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            Background = CardBg, CornerRadius = new CornerRadius(20), Padding = new Thickness(22, 18, 22, 16),
            Margin = new Thickness(0, 0, 0, 14), Child = stack,
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

    internal static SolidColorBrush Frozen(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    // Dark controls: a switch, sidebar items, buttons and a combo box with a dark drop-down.
    internal static ResourceDictionary Styles() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="Switch" TargetType="CheckBox">
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                  <Grid Width="44" Height="24" Background="Transparent">
                    <Border x:Name="Track" CornerRadius="12" Background="#FF2C2C2B" BorderBrush="#FF363635" BorderThickness="1" />
                    <Ellipse x:Name="Thumb" Width="16" Height="16" Fill="#FFA19F9A" HorizontalAlignment="Left" Margin="4,0,0,0" />
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Track" Property="Background" Value="#FFF0EFEC" />
                      <Setter TargetName="Track" Property="BorderBrush" Value="#FFF0EFEC" />
                      <Setter TargetName="Thumb" Property="Fill" Value="#FF141416" />
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
            <Setter Property="Foreground" Value="#FFA19F9A" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="RadioButton">
                  <Border x:Name="Bd" CornerRadius="10" Padding="12,9" Margin="0,1" Background="Transparent">
                    <Grid>
                      <Border x:Name="Bar" Width="3" Height="16" CornerRadius="1.5" Background="#FFD97757" HorizontalAlignment="Left" Margin="-10,0,0,0" Visibility="Collapsed" />
                      <ContentPresenter />
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF1A1A1A" />
                    </Trigger>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF202020" />
                      <Setter Property="Foreground" Value="#FFF0EFEC" />
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
                  <Border x:Name="Bd" CornerRadius="12" Padding="16,8" Background="#FF2C2C2B" BorderBrush="#FF363635" BorderThickness="1">
                    <ContentPresenter HorizontalAlignment="Center" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF3A3A38" />
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF242423" />
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
                      <Setter TargetName="Bd" Property="Background" Value="#FF2C2C2B" />
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#FF3A3A38" />
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
                          <Border x:Name="Bd" CornerRadius="8" Background="#FF2C2C2B" BorderBrush="#FF363635" BorderThickness="1">
                            <TextBlock Text="&#xE70D;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="11" Foreground="#FFA19F9A"
                                       HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,0" />
                          </Border>
                          <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                              <Setter TargetName="Bd" Property="Background" Value="#FF3A3A38" />
                            </Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </ToggleButton.Template>
                    </ToggleButton>
                    <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" IsHitTestVisible="False"
                                      Margin="12,7,32,7" VerticalAlignment="Center" />
                    <Popup IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                      <Border Background="#FF202020" BorderBrush="#FF363635" BorderThickness="1" CornerRadius="10" Padding="0,4"
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
                  <Border x:Name="T" CornerRadius="3" Background="#363635" />
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="T" Property="Background" Value="#4A4A48" /></Trigger>
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
