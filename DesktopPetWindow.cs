using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>
/// The mascot loose on the desktop: drag it anywhere, it follows the cursor with its eyes, mirrors the
/// island's mood and runs up to the island when an agent needs you.
/// </summary>
public sealed class DesktopPetWindow : Window
{
    readonly IslandSettings _settings;
    readonly PetView _pet = new();
    readonly DispatcherTimer _eyes, _back;
    Point? _home;

    public DesktopPetWindow(IslandSettings settings)
    {
        _settings = settings;
        Title = "Mascote da Dynamic Island";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 76;
        Height = 88;
        // room above the body for hats; the window handles clicks (drag vs poke) itself
        _pet.IsHitTestVisible = false;
        Cursor = System.Windows.Input.Cursors.Hand;
        Content = new Viewbox { Child = _pet, Margin = new Thickness(8, 20, 8, 4) };

        var area = SystemParameters.WorkArea;
        Left = settings.DesktopPetX ?? area.Right - Width - 24;
        Top = settings.DesktopPetY ?? area.Bottom - Height - 8;
        if (Left < SystemParameters.VirtualScreenLeft || Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 20
            || Top < SystemParameters.VirtualScreenTop || Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20)
        {
            Left = area.Right - Width - 24;
            Top = area.Bottom - Height - 8;
        }

        _eyes = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => LookAtCursor(), Dispatcher);
        _back = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (_, _) => GoHome(), Dispatcher);
        _back.Stop();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        };
        Loaded += (_, _) => _eyes.Start();
        Closed += (_, _) => { _eyes.Stop(); _back.Stop(); };

        PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            StopMoving();
            var before = new Point(Left, Top);
            try { DragMove(); } catch { return; }
            if (Math.Abs(Left - before.X) + Math.Abs(Top - before.Y) < 3)
            {
                _pet.Poke();
                if (_home != null) _back.Start();
                return;
            }
            _home = null;
            _settings.DesktopPetX = Left;
            _settings.DesktopPetY = Top;
            _settings.Save();
        };
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = ((App)Application.Current).BuildContextMenu();
            menu.PlacementTarget = this;
            menu.IsOpen = true;
        };
    }

    void LookAtCursor()
    {
        if (!IsVisible || !Native.GetCursorPos(out var cursor) || PresentationSource.FromVisual(_pet) == null) return;
        var local = _pet.PointFromScreen(new Point(cursor.X, cursor.Y));
        _pet.LookAt(local - new Point(12, 12));
    }

    public void Follow(PetView.Mood mood, Color? tint)
    {
        _pet.BaseMood = mood;
        _pet.Tint(tint);
    }

    public void Celebrate() => _pet.React(PetView.Mood.Happy, 1.6);

    /// <summary>Runs to a point (physical pixels) under the island, then back after a few seconds.</summary>
    public void Visit(Point anchor)
    {
        if (anchor == default || PresentationSource.FromVisual(this) is not { CompositionTarget: { } target }) return;
        var dip = target.TransformFromDevice.Transform(anchor);
        _home ??= new Point(Left, Top);
        MoveTo(dip.X - Width / 2, dip.Y);
        _pet.React(PetView.Mood.Waiting, 5);
        _back.Stop();
        _back.Start();
    }

    void GoHome()
    {
        _back.Stop();
        if (_home is not { } home) return;
        _home = null;
        MoveTo(home.X, home.Y);
    }

    void MoveTo(double left, double top)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromMilliseconds(650);
        var x = new DoubleAnimation(left, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var y = new DoubleAnimation(top, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        x.Completed += (_, _) => Left = left;
        y.Completed += (_, _) => Top = top;
        BeginAnimation(LeftProperty, x);
        BeginAnimation(TopProperty, y);
    }

    void StopMoving()
    {
        _back.Stop();
        var (left, top) = (Left, Top);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = left;
        Top = top;
    }
}
