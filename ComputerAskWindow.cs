using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Drawing = System.Drawing;

namespace DynamicIsland;

public enum ComputerAnswer { Allow, AllowRest, AllowAlways, Stop }

/// <summary>"Jarvis quer: …" with Permitir / Permitir o resto / Parar, near the top of the screen Jarvis is working on,
/// plus a ring where the click will land. Neither takes the focus away from the app Jarvis is using.</summary>
public sealed class ComputerAskWindow : Window
{
    readonly TaskCompletionSource<ComputerAnswer> _answer = new();
    readonly Drawing.Rectangle _screen;
    MarkerWindow? _marker;

    public Task<ComputerAnswer> Answer => _answer.Task;

    public ComputerAskWindow(string what, bool risky, bool offerRest, Drawing.Rectangle screen, Drawing.Point? point)
    {
        _screen = screen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Jarvis no controle";

        var accent = risky ? Color.FromRgb(0xFF, 0x9F, 0x0A) : Color.FromRgb(0x0A, 0x84, 0xFF);
        var panel = new StackPanel { Width = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = risky ? "Jarvis quer fazer algo importante" : "Jarvis quer",
            Foreground = new SolidColorBrush(accent), FontSize = 12, FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = what, Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button Make(string text, ComputerAnswer answer, Color color)
        {
            var button = new Button
            {
                Content = text, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6), FontSize = 13,
                Foreground = Brushes.White, Background = new SolidColorBrush(color), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            };
            button.Click += (_, _) => Finish(answer);
            buttons.Children.Add(button);
            return button;
        }
        Make("Parar", ComputerAnswer.Stop, Color.FromRgb(0x3A, 0x3A, 0x3C));
        Make("Sempre permitir", ComputerAnswer.AllowAlways, Color.FromRgb(0x3A, 0x3A, 0x3C)).ToolTip =
            "Não pergunta mais (nem nesta tarefa nem nas próximas). Volta a perguntar em Configurações › Jarvis › Controle do computador.";
        if (offerRest) Make("Permitir o resto", ComputerAnswer.AllowRest, Color.FromRgb(0x3A, 0x3A, 0x3C));
        Make("Permitir", ComputerAnswer.Allow, accent);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock
        {
            Text = "Esc para tudo a qualquer momento.", Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9D)), FontSize = 11.5,
            Margin = new Thickness(0, 10, 0, 0),
        });
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 14, 18, 14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B)), BorderThickness = new Thickness(1.5),
            Child = panel,
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        };
        Loaded += (_, _) =>
        {
            // physical pixels: the screen Jarvis works on may have its own scale
            var scale = Native.MonitorScale(_screen.Left + _screen.Width / 2, _screen.Top + _screen.Height / 2);
            var width = (int)(ActualWidth * scale);
            var x = _screen.Left + (_screen.Width - width) / 2;
            var y = _screen.Top + (int)(90 * scale);
            // keep the target visible: below it when the click is up top
            if (point is { } p && p.Y < y + ActualHeight * scale + 40) y = Math.Min(_screen.Bottom - (int)(ActualHeight * scale) - 20, p.Y + (int)(60 * scale));
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        };
        Closed += (_, _) =>
        {
            _marker?.Close();
            _answer.TrySetResult(ComputerAnswer.Stop);
        };
        if (point is { } at)
        {
            _marker = new MarkerWindow(at, risky ? accent : Color.FromRgb(0x0A, 0x84, 0xFF));
            _marker.Show();
        }
    }

    public void Finish(ComputerAnswer answer)
    {
        _answer.TrySetResult(answer);
        Close();
    }

    /// <summary>A ring where Jarvis will click; clicks pass through it.</summary>
    sealed class MarkerWindow : Window
    {
        public MarkerWindow(Drawing.Point at, Color color)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Width = Height = 56;
            Content = new Grid
            {
                Children =
                {
                    new Ellipse { Stroke = new SolidColorBrush(color), StrokeThickness = 3, Fill = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)) },
                    new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color) },
                },
            };
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
                    new IntPtr(style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | 0x80000 /* WS_EX_LAYERED */));
                var size = (int)(56 * Native.MonitorScale(at.X, at.Y));
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, at.X - size / 2, at.Y - size / 2, size, size, Native.SWP_NOACTIVATE);
            };
        }
    }
}
