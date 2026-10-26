using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// Timer, Windows notifications and the microphone/camera indicator.
public partial class IslandWindow
{
    static readonly Brush TimerBrush = Frozen(AgentSession.Waiting);
    static readonly Brush TimerDoneBrush = Frozen(AgentSession.Done);

    readonly DispatcherTimer _countdownTimer;
    DateTime? _timerEnd;
    TimeSpan _timerTotal, _timerLeft;
    bool _timerPaused;

    bool TimerActive => _timerEnd != null || _timerPaused;

    TimeSpan TimerRemaining => _timerPaused ? _timerLeft
        : _timerEnd is { } end && end > DateTime.Now ? end - DateTime.Now : TimeSpan.Zero;

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ───────────────────────── timer ─────────────────────────

    void BuildTimerPresets()
    {
        foreach (var minutes in new[] { 5, 15, 25, 45, 60 })
        {
            var chip = new Button
            {
                Style = (Style)FindResource("Chip"),
                Content = minutes < 60 ? $"{minutes} min" : "1 h",
                ToolTip = minutes == 25 ? "Pomodoro" : null,
            };
            chip.Click += (_, _) => StartTimer(minutes);
            TimerPresets.Children.Add(chip);
        }
    }

    void StartTimer(int minutes)
    {
        _timerTotal = TimeSpan.FromMinutes(minutes);
        _timerEnd = DateTime.Now + _timerTotal;
        _timerPaused = false;
        _settings.TimerMinutes = minutes;
        _settings.Save();
        _countdownTimer.Start();
        CountdownTick();
        Retarget();
    }

    void StopTimer()
    {
        _countdownTimer.Stop();
        _timerEnd = null;
        _timerPaused = false;
        UpdateTimerUi();
        Retarget();
    }

    void TimerPause_Click(object sender, RoutedEventArgs e)
    {
        if (_timerPaused)
        {
            _timerEnd = DateTime.Now + _timerLeft;
            _timerPaused = false;
            _countdownTimer.Start();
        }
        else if (_timerEnd != null)
        {
            _timerLeft = TimerRemaining;
            _timerEnd = null;
            _timerPaused = true;
            _countdownTimer.Stop();
        }
        UpdateTimerUi();
    }

    void TimerAdd_Click(object sender, RoutedEventArgs e)
    {
        var minute = TimeSpan.FromMinutes(1);
        _timerTotal += minute;
        if (_timerPaused) _timerLeft += minute;
        else if (_timerEnd != null) _timerEnd += minute;
        UpdateTimerUi();
    }

    void TimerStop_Click(object sender, RoutedEventArgs e) => StopTimer();

    void CountdownTick()
    {
        if (_timerEnd != null && TimerRemaining <= TimeSpan.Zero)
        {
            var total = _timerTotal;
            StopTimer();
            PlayChime();
            ShowAlert(TimerDoneBrush, "\uE916", "Tempo esgotado",
                $"Timer de {FormatMinutes(total)} concluído", false, 6);
            return;
        }
        UpdateTimerUi();
    }

    void UpdateTimerUi()
    {
        var active = TimerActive;
        TimerIdle.Visibility = Vis(!active);
        TimerRunning.Visibility = Vis(active);
        if (!active) return;

        var left = TimerRemaining;
        var text = FormatClock(left);
        var progress = _timerTotal > TimeSpan.Zero ? 1 - left / _timerTotal : 0;
        CompactTimerText.Text = text;
        CompactTimerArc.Data = Arc(8, 8, 6.75, Math.Clamp(1 - progress, 0, 1));
        TimerBig.Text = text;
        TimerProgressScale.ScaleX = Math.Clamp(progress, 0, 1);
        TimerPauseButton.Content = _timerPaused ? "\uE768" : "\uE769";
        TimerCaption.Text = _timerPaused
            ? $"Pausado · timer de {FormatMinutes(_timerTotal)}"
            : $"Termina às {(_timerEnd ?? DateTime.Now).ToString("HH:mm", CultureInfo.CurrentCulture)}";
        CompactTimerText.Opacity = _timerPaused ? 0.55 : 1;
    }

    static string FormatClock(TimeSpan t)
    {
        var seconds = (int)Math.Ceiling(t.TotalSeconds);
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60:00}:{seconds % 60:00}";
    }

    static string FormatMinutes(TimeSpan t) =>
        t.TotalMinutes >= 60 && t.Minutes == 0 ? $"{(int)t.TotalHours} h" : $"{(int)Math.Round(t.TotalMinutes)} min";

    /// <summary>Clockwise arc from 12 o'clock covering <paramref name="fraction"/> of a circle.</summary>
    static Geometry Arc(double cx, double cy, double r, double fraction)
    {
        if (fraction <= 0.001) return Geometry.Empty;
        if (fraction >= 0.999) return new EllipseGeometry(new Point(cx, cy), r, r);
        var angle = fraction * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(angle), cy - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    static void PlayChime()
    {
        try
        {
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
            if (File.Exists(path)) new System.Media.SoundPlayer(path).Play();
            else System.Media.SystemSounds.Asterisk.Play();
        }
        catch { }
    }

    // ───────────────────────── notifications ─────────────────────────

    void OnNotification(IslandNotification n)
    {
        if (!_settings.ShowNotifications) return;
        NotifyLogo.Background = n.Logo != null
            ? new ImageBrush(n.Logo) { Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
        NotifyGlyph.Visibility = Vis(n.Logo == null);
        NotifyTitle.Text = n.Title;
        NotifyApp.Text = n.App;
        NotifyBody.Text = n.Body;
        ShowTransient(Transient.Notification, 5);
    }

    // ───────────────────────── microphone / camera ─────────────────────────

    void OnPrivacy(PrivacyState state)
    {
        if (state.Same(_privacy)) return;
        var previous = _privacy;
        _privacy = state;

        var mic = state.Microphone.Count > 0;
        var cam = state.Camera.Count > 0;
        MicDot.Visibility = Vis(mic);
        CamDot.Visibility = Vis(cam);
        PrivacySpacer.Width = (mic ? 13 : 0) + (cam ? 13 : 0);
        MicDot.ToolTip = mic ? "Microfone: " + string.Join(", ", state.Microphone) : null;
        CamDot.ToolTip = cam ? "Câmera: " + string.Join(", ", state.Camera) : null;

        var newCam = state.Camera.Except(previous.Camera).ToList();
        var newMic = state.Microphone.Except(previous.Microphone).ToList();
        if (newCam.Count > 0) ShowPrivacy("\uE714", "Câmera em uso", AgentSession.Done, newCam);
        else if (newMic.Count > 0) ShowPrivacy("\uE720", "Microfone em uso", AgentSession.Waiting, newMic);
    }

    void ShowPrivacy(string glyph, string text, Color color, List<string> apps)
    {
        PrivacyIcon.Text = glyph;
        PrivacyIcon.Foreground = Frozen(color);
        PrivacyText.Text = text;
        PrivacyApp.Text = string.Join(", ", apps);
        ShowTransient(Transient.Privacy, 2.6);
    }
}
