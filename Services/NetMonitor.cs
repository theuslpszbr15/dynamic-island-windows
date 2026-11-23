using System.Net.NetworkInformation;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>
/// Is the internet up, how fast is it answering (ping), and how much is going through the network right now. The ping
/// runs every 20 seconds (a few bytes); throughput is only read while someone looks at it (see <see cref="Sample"/>).
/// </summary>
public sealed class NetMonitor : IDisposable
{
    const string Target = "1.1.1.1";
    // three missed pings in a row (about a minute) = down; one Windows "no network" = down at once
    const int FailuresForDown = 3;

    readonly DispatcherTimer _timer;
    readonly Ping _ping = new();
    int _failures;
    bool _busy;
    long _lastReceived, _lastSent;
    DateTime _lastSample;

    public bool Down { get; private set; }
    public DateTime DownSince { get; private set; }
    public long? PingMs { get; private set; }
    public double DownloadBps { get; private set; }
    public double UploadBps { get; private set; }

    /// <summary>true = it went down, false = it came back.</summary>
    public event Action<bool>? Changed;

    public NetMonitor(Dispatcher ui)
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => _ = CheckAsync(), ui);
        NetworkChange.NetworkAvailabilityChanged += (_, e) => ui.InvokeAsync(() =>
        {
            if (!e.IsAvailable) SetDown(true);
            else _ = CheckAsync();
        });
    }

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync();
    }

    public void Stop() => _timer.Stop();

    async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var reply = await _ping.SendPingAsync(Target, 2000);
            if (reply.Status == IPStatus.Success)
            {
                PingMs = reply.RoundtripTime;
                _failures = 0;
                SetDown(false);
                return;
            }
        }
        catch (PingException) { }
        catch (InvalidOperationException) { }
        finally { _busy = false; }
        PingMs = null;
        if (++_failures >= FailuresForDown || !NetworkInterface.GetIsNetworkAvailable()) SetDown(true);
    }

    void SetDown(bool down)
    {
        if (down == Down) return;
        Down = down;
        if (down) DownSince = DateTime.Now;
        Changed?.Invoke(down);
    }

    /// <summary>Bytes per second in and out since the previous call, over the adapters that are up.</summary>
    public void Sample()
    {
        long received = 0, sent = 0;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var stats = adapter.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
            catch (NetworkInformationException) { }
        }
        var now = DateTime.Now;
        var seconds = (now - _lastSample).TotalSeconds;
        if (_lastSample != default && seconds is > 0.2 and < 10)
        {
            DownloadBps = Math.Max(0, (received - _lastReceived) / seconds);
            UploadBps = Math.Max(0, (sent - _lastSent) / seconds);
        }
        (_lastReceived, _lastSent, _lastSample) = (received, sent, now);
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1_000_000 => $"{bytesPerSecond / 1_000_000:0.0} MB/s",
        >= 1_000 => $"{bytesPerSecond / 1_000:0} KB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };

    public void Dispose()
    {
        _timer.Stop();
        _ping.Dispose();
    }
}
