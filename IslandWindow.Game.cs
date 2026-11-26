using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// Modo jogo: a game in full screen (Steam, Epic, Xbox, Riot, Roblox… or anything in exclusive full screen) holds the
// Windows notifications until it closes, then one card says what came; meanwhile a small meter shows FPS, the
// graphics card's temperature and use, and the CPU.
public partial class IslandWindow
{
    (uint Pid, string Path)? _game;
    DateTime _gameLostAt;
    readonly List<IslandNotification> _gameHeld = [];
    DispatcherTimer? _hudTimer;
    GpuStats? _gpu;
    FpsCounter? _fps;
    double? _gpuTemp;
    int _hudTicks;

    bool GameActive => _game != null;

    /// <summary>From <see cref="GuardTick"/> (every 2 s): starts or ends the game mode. Alt+Tab out for a moment
    /// doesn't end it; 6 s away does.</summary>
    void CheckGame(int fullscreenState)
    {
        var game = _settings.GameMode && _fullscreen && !SnapshotMode ? GameDetector.Foreground(fullscreenState == 3) : null;
        if (game != null)
        {
            _gameLostAt = default;
            if (_game?.Pid == game.Value.Pid) return;
            var starting = _game == null;
            _game = game;
            if (starting) StartGame();
            return;
        }
        if (_game == null) return;
        if (_gameLostAt == default) { _gameLostAt = DateTime.Now; return; }
        if (DateTime.Now - _gameLostAt < TimeSpan.FromSeconds(6)) return;
        _game = null;
        EndGame();
    }

    void StartGame()
    {
        Log.Warn("Game", "game mode on: " + System.IO.Path.GetFileName(_game?.Path));
        _hudTicks = 0;
        if (!_settings.GameHud) return;
        _gpu = new GpuStats();
        _fps = FpsCounter.Allowed ? FpsCounter.Start() : null;
        _hudTimer ??= MakeTimer(1, HudTick);
        _hudTimer.Start();
        HudTick();
    }

    void EndGame()
    {
        _hudTimer?.Stop();
        GameHud.Visibility = Visibility.Collapsed;
        _gpu?.Dispose();
        _gpu = null;
        var fps = _fps;
        _fps = null;
        if (fps != null) Task.Run(fps.Dispose); // stopping the trace waits for its thread
        ShowHeldWhilePlaying();
    }

    /// <summary>The Windows notifications that came while playing, as one card; a click opens Windows' notification center.</summary>
    void ShowHeldWhilePlaying()
    {
        if (_gameHeld.Count == 0) return;
        var count = _gameHeld.Count;
        var lines = _gameHeld.TakeLast(3).Reverse().Select(n => string.IsNullOrWhiteSpace(n.Title) ? n.App : $"{n.App}: {n.Title}");
        _gameHeld.Clear();
        ShowCard(null, count == 1 ? "1 aviso enquanto você jogava" : $"{count} avisos enquanto você jogava", "Modo jogo",
            string.Join(" · ", lines), seconds: 8, glyph: "\uE7FC");
        _notifyAction = () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-actioncenter:") { UseShellExecute = true }); }
            catch { }
        };
    }

    /// <summary>Holds a notification while a game runs (true); the Claude app waiting for you still shows.</summary>
    bool HoldForGame(IslandNotification n)
    {
        if (!GameActive || n.IsClaude) return false;
        _gameHeld.Add(n);
        if (_gameHeld.Count > 50) _gameHeld.RemoveAt(0);
        return true;
    }

    void HudTick()
    {
        if (_game is not { } game || !_settings.GameHud)
        {
            GameHud.Visibility = Visibility.Collapsed;
            return;
        }
        // the temperature changes slowly; reading it every 3 s is enough
        if (_hudTicks++ % 3 == 0) _gpuTemp = GpuStats.Temperature();
        ShowGameHud(_fps?.Read(game.Pid), _gpuTemp, _gpu?.Usage(), _monitor.CpuPercent());
    }

    /// <summary>The meter's line: "144 FPS · GPU 62° 48% · CPU 35%", each part only when known. Shown only while the
    /// island itself is hidden over the game.</summary>
    void ShowGameHud(double? fps, double? gpuTemp, double? gpuUse, double cpu)
    {
        var text = GameHudText;
        text.Inlines.Clear();
        void Part(string value, string label, Brush? color = null)
        {
            if (text.Inlines.Count > 0) text.Inlines.Add(new Run("  ·  ") { Foreground = HudMuted });
            text.Inlines.Add(new Run(value) { Foreground = color ?? text.Foreground });
            if (label.Length > 0) text.Inlines.Add(new Run(" " + label) { Foreground = HudMuted });
        }
        if (fps is { } f) Part(Math.Round(f).ToString("0"), "FPS", f >= 55 ? HudGood : f >= 30 ? HudWarn : HudBad);
        var gpu = (gpuTemp is { } t ? $"{Math.Round(t):0}°" : "") + (gpuUse is { } u ? $"{(gpuTemp != null ? " " : "")}{Math.Round(u):0}%" : "");
        if (gpu.Length > 0) Part(gpu, "GPU", gpuTemp is >= 85 ? HudBad : gpuTemp is >= 75 ? HudWarn : null);
        Part($"{Math.Round(cpu):0}%", "CPU");
        GameHud.Visibility = Vis(_hiddenFullscreen);
    }

    static readonly Brush HudMuted = Frozen(Color.FromRgb(0xA8, 0xA8, 0xAD)), HudGood = Frozen(Color.FromRgb(0x30, 0xD1, 0x58)),
        HudWarn = Frozen(Color.FromRgb(0xFF, 0xD6, 0x0A)), HudBad = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
}
