using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>"Wrapped"-style card with last week's agent activity; can be saved as a PNG to share.</summary>
public sealed class RecapWindow : Window
{
    readonly Border _card;
    readonly WeekRecap _recap;

    public RecapWindow(WeekRecap recap)
    {
        _recap = recap;
        Title = "Resumo da semana";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _card = BuildCard(recap);
        var save = Button("Salvar imagem", "#FF0A84FF", SaveImage);
        var close = Button("Fechar", "#FF3A3A3C", Close);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(close);
        var root = new StackPanel();
        root.Children.Add(_card);
        root.Children.Add(buttons);
        Content = root;
        _card.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    public static Border BuildCard(WeekRecap r)
    {
        var culture = CultureInfo.CurrentCulture;
        var white = Brushes.White;
        var muted = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        TextBlock Text(string text, double size, FontWeight weight, Brush? brush = null, double top = 0) => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush ?? white,
            Margin = new Thickness(0, top, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel();
        var pet = new PetView { BaseMood = PetView.Mood.Happy };
        var petBox = new Viewbox { Width = 76, Height = 76, HorizontalAlignment = HorizontalAlignment.Left, Child = pet, Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(petBox);
        stack.Children.Add(Text("Sua semana com os agentes", 24, FontWeights.Bold, top: 10));
        var period = $"{r.From.ToString("d 'de' MMM", culture)} – {r.To.ToString("d 'de' MMM", culture)}";
        stack.Children.Add(Text(period, 13.5, FontWeights.SemiBold, muted, 2));

        var empty = r.WorkSeconds < 60 && r.Sessions == 0;
        if (empty)
        {
            stack.Children.Add(Text("Ainda sem atividade registrada. Os números aparecem conforme Claude Code, Copilot e companhia trabalham com a ilha aberta.",
                14, FontWeights.Normal, muted, 26));
        }
        else
        {
            var work = TimeSpan.FromSeconds(r.WorkSeconds);
            var hours = work.TotalHours >= 1 ? $"{(int)work.TotalHours}h {work.Minutes:00}min" : $"{Math.Max(1, work.Minutes)}min";
            stack.Children.Add(Text(hours, 52, FontWeights.Black, top: 22));
            stack.Children.Add(Text("de agentes trabalhando por você", 14, FontWeights.SemiBold, muted, -4));

            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 22, 0, 0) };
            foreach (var (value, label) in new[] { (r.Edits, "edições"), (r.Commands, "comandos"), (r.Sessions, "sessões") })
            {
                var cell = new StackPanel();
                cell.Children.Add(Text(value.ToString("N0", culture), 26, FontWeights.Bold));
                cell.Children.Add(Text(label, 12.5, FontWeights.SemiBold, muted));
                grid.Children.Add(cell);
            }
            stack.Children.Add(grid);

            var facts = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
            void Fact(string label, string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                var line = new TextBlock { FontSize = 14, Foreground = white, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                line.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = muted });
                line.Inlines.Add(new System.Windows.Documents.Run(value) { FontWeight = FontWeights.SemiBold });
                facts.Children.Add(line);
            }
            Fact("Projeto mais ativo", r.TopProject);
            Fact("Dia mais puxado", r.BusiestDay is { } day ? culture.DateTimeFormat.GetDayName(day) : null);
            Fact("Dias com agentes", $"{r.ActiveDays} de 7");
            if (r.Approvals > 0) Fact("Permissões dadas pela ilha", r.Approvals.ToString(culture));
            stack.Children.Add(facts);
        }

        stack.Children.Add(Text("Dynamic Island para Windows", 11.5, FontWeights.SemiBold, new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 26));

        return new Border
        {
            Width = 420,
            CornerRadius = new CornerRadius(30),
            Padding = new Thickness(30, 26, 30, 24),
            Background = new LinearGradientBrush(
                [
                    new GradientStop(Color.FromRgb(0x3A, 0x1C, 0x71), 0),
                    new GradientStop(Color.FromRgb(0xD7, 0x6D, 0x77), 0.62),
                    new GradientStop(Color.FromRgb(0xFF, 0xAF, 0x7B), 1),
                ], new Point(0, 0), new Point(1, 1)),
            Child = stack,
        };
    }

    static Button Button(string text, string color, Action click)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(5, 0, 5, 0),
            Padding = new Thickness(18, 8, 18, 8),
            Foreground = Brushes.White,
            Background = (Brush)new BrushConverter().ConvertFromString(color)!,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Renders the card (2×) to a PNG.</summary>
    public static void SaveCard(Border card, string path)
    {
        const double scale = 2;
        var size = new Size(card.ActualWidth, card.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(card), null, new Rect(size));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    void SaveImage()
    {
        try
        {
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Dynamic Island",
                $"resumo-semana-{_recap.To:yyyy-MM-dd}.png");
            SaveCard(_card, path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Não foi possível salvar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
