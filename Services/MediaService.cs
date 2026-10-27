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
    DateTimeOffset LastUpdated,
    string? Source = null)
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

/// <summary>An app that has a media session; Playing is a snapshot at listing time.</summary>
public sealed record MediaSource(string Id, string Name, bool Playing);

/// <summary> (Spotify, browsers, etc.) via GSMTC. Apps that never publish
/// one (Photos, some players, browsers with media keys disabled) are picked up from their audio output.
/// </summary>
public sealed class MediaService(Dispatcher ui)
{
    public static readonly Color DefaultAccent = Color.FromRgb(0x0A, 0x84, 0xFF);

    // Calls, not music.
    static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "ms-teams", "Teams", "Zoom", "slack", "CiscoCollabHost", "webexhost", "msedgewebview2",
    };

    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _artKey;
    BitmapSource? _art;
    Color _accent = DefaultAccent;
    int _version;

    MediaSnapshot? _smtc, _fallback, _published;
    DispatcherTimer? _audioTimer;
    int _heardPid, _heardTicks;
    DateTime _lastHeard, _resolvedAt;
    bool _resolving, _sampling;
    string? _pinned;

    public event Action<MediaSnapshot?>? Changed;
    public event Action? SourcesChanged;

    /// <summary>Apps with a media session right now (Id = AppUserModelId).</summary>
    public IReadOnlyList<MediaSource> Sources { get; private set; } = [];

    /// <summary>The session the island is showing/controlling, or null for the audio fallback.</summary>
    public string? CurrentId => UsingFallback ? null : _session?.SourceAppUserModelId;

    /// <summary>Sticks to one app's session (while it exists) instead of following whatever plays.</summary>
    public void Pin(string id)
    {
        _pinned = id;
        Attach();
    }

    public async Task StartAsync()
    {
        _audioTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SampleAudio(), ui);
        _audioTimer.Start();
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
        _session = PickSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaProperties;
            _session.PlaybackInfoChanged += OnPlaybackInfo;
            _session.TimelinePropertiesChanged += OnTimeline;
        }
        UpdateSources();
        Refresh();
    }

    void UpdateSources()
    {
        List<MediaSource> sources = [];
        try
        {
            foreach (var s in _manager?.GetSessions() ?? [])
            {
                var id = s.SourceAppUserModelId;
                if (string.IsNullOrEmpty(id) || sources.Any(x => x.Id == id) || Ignored.Contains(SourceName(id))) continue;
                var playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                sources.Add(new MediaSource(id, SourceName(id), playing));
            }
        }
        catch { }
        if (sources.SequenceEqual(Sources)) return;
        Sources = sources;
        SourcesChanged?.Invoke();
    }

    // The "current" session can be a paused tab while another app plays; prefer whichever is playing.
    GlobalSystemMediaTransportControlsSession? PickSession()
    {
        try
        {
            var sessions = _manager?.GetSessions().ToList() ?? [];
            if (_pinned != null)
            {
                var pinned = sessions.FirstOrDefault(s => s.SourceAppUserModelId == _pinned);
                if (pinned != null) return pinned;
                _pinned = null;
            }
            var playing = sessions.FirstOrDefault(s =>
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            return playing ?? _manager?.GetCurrentSession();
        }
        catch { return null; }
    }

    void OnMediaProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => ui.InvokeAsync(Refresh);
    void OnPlaybackInfo(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => ui.InvokeAsync(Attach);
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => ui.InvokeAsync(Refresh);

    async void Refresh()
    {
        var version = ++_version;
        MediaSnapshot? snapshot;
        try { snapshot = await BuildAsync(); }
        catch { snapshot = null; }
        if (version != _version) return;
        _smtc = snapshot;
        Publish(force: true);
    }

    void Publish(bool force = false)
    {
        var effective = _smtc?.IsPlaying == true ? _smtc : _fallback ?? _smtc;
        if (!force && ReferenceEquals(effective, _published)) return;
        _published = effective;
        Changed?.Invoke(effective);
    }

    async void SampleAudio()
    {
        if (_smtc?.IsPlaying == true)
        {
            _heardTicks = 0;
            if (_fallback != null) { _fallback = null; Publish(); }
            return;
        }
        if (_sampling) return;

        // Core Audio session interfaces don't marshal to the STA UI thread; sample on an MTA worker.
        _sampling = true;
        List<AudioActivity.Source> sources;
        try { sources = await Task.Run(() => AudioActivity.Sample()); }
        finally { _sampling = false; }

        var source = sources.FirstOrDefault(s => !Ignored.Contains(s.ProcessName));
        var now = DateTime.Now;
        if (source != null)
        {
            _heardTicks = source.ProcessId == _heardPid ? _heardTicks + 1 : 1;
            _heardPid = source.ProcessId;
            _lastHeard = now;
            // Two seconds in a row, so notification sounds don't count as music; re-read the title now and then.
            if (_heardTicks >= 2 && !_resolving && (_fallback == null || now - _resolvedAt > TimeSpan.FromSeconds(3)))
                ResolveFallback(source);
        }
        else if (_fallback != null && now - _lastHeard > TimeSpan.FromSeconds(4))
        {
            _heardTicks = 0;
            _fallback = null;
            Publish();
        }
    }

    async void ResolveFallback(AudioActivity.Source source)
    {
        _resolving = true;
        try
        {
            var snapshot = await Task.Run(() => BuildFallback(source));
            _resolvedAt = DateTime.Now;
            if (_smtc?.IsPlaying == true || DateTime.Now - _lastHeard > TimeSpan.FromSeconds(4)) return;
            if (_fallback == null || _fallback.Title != snapshot.Title || _fallback.Artist != snapshot.Artist)
            {
                _fallback = snapshot;
                Publish();
            }
        }
        catch { }
        finally { _resolving = false; }
    }

    static string AppName(AudioActivity.Source source)
    {
        try
        {
            var description = System.Diagnostics.Process.GetProcessById(source.ProcessId).MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch { }
        return source.ProcessName;
    }

    static string WindowTitle(AudioActivity.Source source, string app)
    {
        var title = Native.WindowTitle(source.ProcessId).Trim();
        foreach (var separator in new[] { " - ", " – ", " — " })
        {
            var cut = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (cut > 0 && title[(cut + separator.Length)..].Trim() is { } tail
                && (app.Contains(tail, StringComparison.OrdinalIgnoreCase) || tail.Contains(source.ProcessName, StringComparison.OrdinalIgnoreCase)))
            {
                title = title[..cut].Trim();
                break;
            }
        }
        return title;
    }

    static MediaSnapshot BuildFallback(AudioActivity.Source source)
    {
        var app = AppName(source);
        var title = BrowserTabs.IsBrowser(source.ProcessName)
            ? BrowserTabs.PlayingTabTitle(source.ProcessName) ?? ""
            : WindowTitle(source, app);
        foreach (var site in new[] { " - YouTube Music", " - YouTube", " | Spotify", " - Spotify" })
            if (title.EndsWith(site, StringComparison.OrdinalIgnoreCase)) title = title[..^site.Length].Trim();

        // "Song • Artist" (Spotify Web, YouTube Music) carries its own artist.
        var artist = app;
        var dot = title.IndexOf(" • ", StringComparison.Ordinal);
        if (dot > 0)
        {
            artist = title[(dot + 3)..].Trim();
            title = title[..dot].Trim();
        }
        if (string.IsNullOrEmpty(title) || title.Equals(app, StringComparison.OrdinalIgnoreCase))
        {
            title = app;
            artist = "";
        }

        BitmapSource? icon = null;
        try
        {
            if (System.Diagnostics.Process.GetProcessById(source.ProcessId).MainModule?.FileName is { } exe)
                icon = Native.ShellIcon(exe) as BitmapSource;
        }
        catch { }
        return new MediaSnapshot(title, artist, icon, icon != null ? ColorUtil.Accent(icon) : DefaultAccent,
            true, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.Now, app);
    }

    // "Spotify.exe", "Chrome", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" -> a name people know.
    static string SourceName(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return "";
        var name = appId.Split('!')[0];
        if (name.Contains('_')) name = name.Split('_')[0];
        name = name.Split('.', '\\').LastOrDefault(p => p.Length > 0 && !p.Equals("exe", StringComparison.OrdinalIgnoreCase)) ?? name;
        return name.ToLowerInvariant() switch
        {
            "zunemusic" or "mediaplayer" => "Media Player",
            "msedge" => "Edge",
            "chrome" => "Chrome",
            "spotify" => "Spotify",
            "firefox" => "Firefox",
            _ => name,
        };
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
            timeline.LastUpdatedTime,
            SourceName(session.SourceAppUserModelId));
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

    bool UsingFallback => _published != null && ReferenceEquals(_published, _fallback);

    public async void PlayPause()
    {
        if (UsingFallback) { Native.PressKey(Native.VK_MEDIA_PLAY_PAUSE); return; }
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async void Next()
    {
        if (UsingFallback) { Native.PressKey(Native.VK_MEDIA_NEXT_TRACK); return; }
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    public async void Previous()
    {
        if (UsingFallback) { Native.PressKey(Native.VK_MEDIA_PREV_TRACK); return; }
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }
}
