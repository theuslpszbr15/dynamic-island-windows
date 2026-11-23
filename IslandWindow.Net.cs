using System.Diagnostics;
using DynamicIsland.Services;

namespace DynamicIsland;

// Internet (speed and ping on the System page; a card when it drops and when it comes back) and browser downloads
// (the compact island shows the one in progress, and says when it is done).
public partial class IslandWindow
{
    NetMonitor? _net;
    DownloadWatch? _downloads;

    void StartNetAndDownloads()
    {
        _net = new NetMonitor(Dispatcher);
        _net.Changed += OnInternet;
        _net.Start();
        _downloads = new DownloadWatch(Dispatcher);
        _downloads.Changed += () => Retarget();
        _downloads.Finished += OnDownloaded;
        _downloads.Start();
    }

    void OnInternet(bool down)
    {
        UpdateNetLine();
        if (!_settings.NetAlerts) return;
        if (down)
        {
            ShowCard(null, "Sem internet", "Rede", "A ilha avisa quando voltar.", seconds: 6, glyph: "\uEB55");
            return;
        }
        var off = DateTime.Now - _net!.DownSince;
        ShowCard(null, "A internet voltou", "Rede",
            off.TotalMinutes >= 1 ? $"Ficou fora por {(off.TotalHours >= 1 ? $"{(int)off.TotalHours} h {off.Minutes} min" : $"{Math.Max(1, (int)off.TotalMinutes)} min")}." : "Foi rapidinho.",
            seconds: 5, glyph: "\uE701");
    }

    void UpdateNetLine()
    {
        if (_net == null) return;
        _net.Sample();
        NetIcon.Text = _net.Down ? "\uEB55" : "\uE701";
        NetLine.Text = _net.Down
            ? "Sem internet"
            : $"↓ {NetMonitor.Speed(_net.DownloadBps)}   ↑ {NetMonitor.Speed(_net.UploadBps)}" + (_net.PingMs is { } ping ? $"   ·   ping {ping} ms" : "");
    }

    void UpdateCompactDownload()
    {
        if (_downloads?.Active.OrderByDescending(d => d.Bytes).FirstOrDefault() is not { } download) return;
        var more = _downloads.Active.Count > 1 ? $" (+{_downloads.Active.Count - 1})" : "";
        CompactDownloadName.Text = (download.Name.Length > 0 ? download.Name : "Baixando…") + more;
        CompactDownloadInfo.Text = $"{DownloadWatch.Size(download.Bytes)} · {NetMonitor.Speed(download.BytesPerSecond)}";
    }

    void OnDownloaded(string path)
    {
        if (!_settings.DownloadProgress) return;
        // a click shows the file in its folder
        ShowInfo("\uE896", Path.GetFileName(path), "Baixado", glyphColor: AgentSession.Done, seconds: 5, action: () =>
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", ["/select,", path]) { UseShellExecute = false });
        });
    }
}
