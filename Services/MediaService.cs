using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

public sealed record MediaSnapshot(
    string Title,
    string Artist,
    BitmapSource? Art,
    Color Accent,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset LastUpdated)
{
    public TimeSpan PositionNow()
    {
        var pos = Position;
        var elapsed = DateTimeOffset.Now - LastUpdated;
        if (IsPlaying && elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(6)) pos += elapsed;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
        return pos;
    }
}

/// <summary>Tracks the current Windows media session (Spotify, browsers, etc.) via GSMTC.</summary>
public sealed class MediaService(Dispatcher ui)
{
    public static readonly Color DefaultAccent = Color.FromRgb(0x0A, 0x84, 0xFF);

    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _artKey;
    BitmapSource? _art;
    Color _accent = DefaultAccent;
    int _version;

    public event Action<MediaSnapshot?>? Changed;

    public async Task StartAsync()
    {
        try { _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { return; }
        _manager.CurrentSessionChanged += (_, _) => ui.InvokeAsync(Attach);
        _manager.SessionsChanged += (_, _) => ui.InvokeAsync(Attach);
        Attach();
    }

    void Attach()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaProperties;
            _session.PlaybackInfoChanged -= OnPlaybackInfo;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }
        try { _session = _manager?.GetCurrentSession(); } catch { _session = null; }
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaProperties;
            _session.PlaybackInfoChanged += OnPlaybackInfo;
            _session.TimelinePropertiesChanged += OnTimeline;
        }
        Refresh();
    }

    void OnMediaProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => ui.InvokeAsync(Refresh);
    void OnPlaybackInfo(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => ui.InvokeAsync(Refresh);
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => ui.InvokeAsync(Refresh);

    async void Refresh()
    {
        var version = ++_version;
        MediaSnapshot? snapshot;
        try { snapshot = await BuildAsync(); }
        catch { snapshot = null; }
        if (version != _version) return;
        Changed?.Invoke(snapshot);
    }

    async Task<MediaSnapshot?> BuildAsync()
    {
        var session = _session;
        if (session == null) return null;

        var props = await session.TryGetMediaPropertiesAsync();
        if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;

        var key = $"{session.SourceAppUserModelId}|{props.Title}|{props.Artist}";
        if (key != _artKey || (_art == null && props.Thumbnail != null))
        {
            _artKey = key;
            _art = await LoadArtAsync(props.Thumbnail);
            _accent = _art != null ? ColorUtil.Accent(_art) : DefaultAccent;
        }

        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        return new MediaSnapshot(
            props.Title,
            props.Artist ?? "",
            _art,
            _accent,
            playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            timeline.Position,
            timeline.EndTime - timeline.StartTime,
            timeline.LastUpdatedTime);
    }

    static async Task<BitmapSource?> LoadArtAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail == null) return null;
        try
        {
            using var winStream = await thumbnail.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 240;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    public async void PlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }
}
