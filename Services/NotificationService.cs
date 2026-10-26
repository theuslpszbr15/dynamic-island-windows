using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Services;

public sealed record IslandNotification(string App, string Title, string Body, ImageSource? Logo);

/// <summary>
/// Mirrors new Windows toasts. NotificationChanged needs package identity, so new toasts are found by polling.
/// </summary>
public sealed class NotificationService(Dispatcher dispatcher)
{
    UserNotificationListener? _listener;
    DispatcherTimer? _timer;
    readonly HashSet<uint> _seen = [];
    readonly Dictionary<string, ImageSource?> _logos = [];
    bool _primed, _busy;

    public event Action<IslandNotification>? Received;
    public bool Available => _listener != null;

    public async Task StartAsync()
    {
        if (_timer != null) { _timer.Start(); return; }
        try
        {
            var listener = UserNotificationListener.Current;
            if (await listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed) return;
            _listener = listener;
        }
        catch { return; }
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, async (_, _) => await PollAsync(), dispatcher);
        await PollAsync();
    }

    public void Stop()
    {
        _timer?.Stop();
        _primed = false;
    }

    async Task PollAsync()
    {
        if (_listener == null || _busy) return;
        _busy = true;
        try
        {
            var current = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var ids = new HashSet<uint>();
            foreach (var n in current)
            {
                ids.Add(n.Id);
                if (!_primed || _seen.Contains(n.Id) || DateTimeOffset.Now - n.CreationTime > TimeSpan.FromSeconds(30)) continue;
                if (await ToIsland(n) is { } item) Received?.Invoke(item);
            }
            _seen.Clear();
            _seen.UnionWith(ids);
            _primed = true;
        }
        catch { }
        finally { _busy = false; }
    }

    async Task<IslandNotification?> ToIsland(UserNotification n)
    {
        var app = n.AppInfo?.DisplayInfo?.DisplayName ?? "Notificação";
        if (app.Replace(" ", "").Equals("DynamicIsland", StringComparison.OrdinalIgnoreCase)) return null;
        var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()
            .Select(t => t.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList() ?? [];
        if (texts.Count == 0) return null;
        var title = texts[0]!;
        var body = string.Join(" ", texts.Skip(1));
        return new IslandNotification(app, title, body, await Logo(n.AppInfo));
    }

    async Task<ImageSource?> Logo(Windows.ApplicationModel.AppInfo? info)
    {
        if (info == null) return null;
        var key = info.AppUserModelId ?? info.DisplayInfo.DisplayName;
        if (_logos.TryGetValue(key, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            using var stream = await info.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64)).OpenReadAsync();
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream.AsStream();
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch { }
        _logos[key] = image;
        return image;
    }
}
