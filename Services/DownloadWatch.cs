using System.Windows.Threading;

namespace DynamicIsland.Services;

public sealed class Download
{
    public required string TempPath { get; init; }
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public double BytesPerSecond { get; set; }
    public DateTime Started { get; } = DateTime.Now;
}

/// <summary>
/// Browser downloads in the Downloads folder, seen by their temporary files (.crdownload for Chrome and Edge, .part for
/// Firefox, .opdownload for Opera): how big they are getting and how fast, and when they finish. Only a folder watcher
/// while idle; a 1-second look at the files only while something is downloading.
/// </summary>
public sealed class DownloadWatch : IDisposable
{
    static readonly string[] Partial = [".crdownload", ".part", ".partial", ".opdownload", ".download"];

    readonly FileSystemWatcher? _watcher;
    readonly DispatcherTimer _timer;
    readonly Dispatcher _ui;
    readonly Dictionary<string, Download> _active = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Download> Active => _active.Values;
    public event Action? Changed;
    /// <summary>The finished file's full path.</summary>
    public event Action<string>? Finished;

    public static string Folder
    {
        get
        {
            // the Downloads folder the user picked (it can live on another drive)
            var custom = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                "{374DE290-123F-4565-9164-39C4925E467B}", null) as string;
            return !string.IsNullOrEmpty(custom) ? Environment.ExpandEnvironmentVariables(custom)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    public DownloadWatch(Dispatcher ui)
    {
        _ui = ui;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), ui);
        var folder = Folder;
        if (!Directory.Exists(folder)) return;
        _watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
        _watcher.Created += (_, e) => _ui.InvokeAsync(() => Seen(e.FullPath));
        _watcher.Renamed += (_, e) => _ui.InvokeAsync(() => Renamed(e.OldFullPath, e.FullPath));
        _watcher.Deleted += (_, e) => _ui.InvokeAsync(() => Gone(e.FullPath));
    }

    public void Start()
    {
        if (_watcher != null) _watcher.EnableRaisingEvents = true;
    }

    public static bool IsPartial(string path) => Partial.Any(p => path.EndsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Unconfirmed 123.crdownload" → "", "video.mp4.crdownload" → "video.mp4".</summary>
    public static string FinalName(string partialPath)
    {
        var name = Path.GetFileName(partialPath);
        var extension = Partial.First(p => name.EndsWith(p, StringComparison.OrdinalIgnoreCase));
        name = name[..^extension.Length];
        return name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) ? "" : name;
    }

    void Seen(string path)
    {
        if (!IsPartial(path) || _active.ContainsKey(path)) return;
        _active[path] = new Download { TempPath = path, Name = FinalName(path) };
        _timer.Start();
        Changed?.Invoke();
    }

    void Renamed(string from, string to)
    {
        if (_active.Remove(from, out var download))
        {
            if (IsPartial(to))
            {
                // Chrome: "Unconfirmed 123.crdownload" → "video.mp4.crdownload" once the name is known
                download = new Download { TempPath = to, Name = FinalName(to), Bytes = download.Bytes };
                _active[to] = download;
                Changed?.Invoke();
                return;
            }
            Done(to);
            return;
        }
        Seen(to);
    }

    void Gone(string path)
    {
        if (!_active.Remove(path, out var download)) return;
        // Firefox writes the final file next to the .part and deletes the .part when it is done
        var final = Path.Combine(Path.GetDirectoryName(path)!, download.Name);
        if (download.Name.Length > 0 && File.Exists(final)) Done(final);
        else Changed?.Invoke(); // cancelled
    }

    void Done(string path)
    {
        if (_active.Count == 0) _timer.Stop();
        Changed?.Invoke();
        Finished?.Invoke(path);
    }

    void Tick()
    {
        foreach (var download in _active.Values.ToList())
        {
            long size;
            try { size = new FileInfo(download.TempPath).Length; }
            catch { continue; }
            download.BytesPerSecond = download.BytesPerSecond * 0.5 + Math.Max(0, size - download.Bytes) * 0.5;
            download.Bytes = size;
        }
        // a browser that crashed leaves its temp file behind: after a while without growing it stops counting
        foreach (var stale in _active.Values.Where(d => d.BytesPerSecond < 1 && DateTime.Now - d.Started > TimeSpan.FromMinutes(10)).ToList())
            _active.Remove(stale.TempPath);
        if (_active.Count == 0) _timer.Stop();
        Changed?.Invoke();
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.0} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0.0} MB",
        >= 1_000 => $"{bytes / 1_000.0:0} KB",
        _ => $"{bytes} B",
    };

    public void Dispose()
    {
        _timer.Stop();
        _watcher?.Dispose();
    }
}
