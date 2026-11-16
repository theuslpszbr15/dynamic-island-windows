using System.Diagnostics;

namespace DynamicIsland.Services;

/// <summary>
/// Keeps <c>adb reverse</c> for the phone link up while a phone with USB debugging is plugged in, so the app can reach
/// this PC through the cable (127.0.0.1 on the phone) when Wi-Fi and the PC's cable network can't see each other.
/// </summary>
public sealed class UsbBridge : IDisposable
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(4);
    CancellationTokenSource? _cts;
    string _status = "";

    /// <summary>What to tell the user, e.g. "Celular ligado pelo cabo".</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            Changed?.Invoke();
        }
    }

    /// <summary>True while at least one authorized phone has the reverse tunnel.</summary>
    public bool Ready { get; private set; }

    /// <summary>Raised on a background thread.</summary>
    public event Action? Changed;

    public bool Running => _cts != null;

    public void Start(Func<int> port)
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        Status = "Procurando o celular no cabo…";
        _ = Task.Run(() => Loop(port, cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        Ready = false;
        Status = "";
    }

    public void Dispose() => Stop();

    async Task Loop(Func<int> port, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Check(port(), token); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Ready = false; Status = "Não deu para falar com o adb: " + ex.Message; }
            try { await Task.Delay(Every, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task Check(int port, CancellationToken token)
    {
        var adb = FindAdb();
        if (adb == null)
        {
            Ready = false;
            Status = "Precisa do adb (platform-tools). Instale com: winget install Google.PlatformTools";
            return;
        }

        var devices = ParseDevices(await Run(adb, token, "devices", "-l"));
        // Phones seen on the cable once are reached again over Wi-Fi: the PC dials out to the phone's adb, so this
        // works even when the PC's firewall blocks every incoming connection.
        foreach (var ip in KnownWifi())
            if (!devices.Any(d => d.Serial == $"{ip}:{WifiPort}" && d.State == "device"))
                await Run(adb, token, "connect", $"{ip}:{WifiPort}");
        foreach (var usb in devices.Where(d => d.State == "device" && !d.Serial.Contains(':')).ToList())
        {
            if (await WifiAddress(adb, usb.Serial, token) is not { } ip || devices.Any(d => d.Serial == $"{ip}:{WifiPort}" && d.State == "device"))
                continue;
            await Run(adb, token, "-s", usb.Serial, "tcpip", WifiPort.ToString());
            await Task.Delay(3000, token);
            await Run(adb, token, "connect", $"{ip}:{WifiPort}");
            Remember(ip);
        }
        devices = ParseDevices(await Run(adb, token, "devices", "-l"));

        var ready = devices.Where(d => d.State == "device").ToList();
        if (ready.Count == 0)
        {
            Ready = false;
            Status = devices.Any(d => d.State == "unauthorized")
                ? "Toque em \"Permitir\" na tela do celular (depuração USB)."
                : KnownWifi().Count > 0
                    ? "Celular fora do alcance (ou reiniciou: ligue o cabo uma vez para reativar o Wi-Fi)."
                    : "Nenhum celular. Ligue o cabo USB uma vez com a depuração USB ativa.";
            return;
        }

        var spec = $"tcp:{port}";
        foreach (var device in ready)
        {
            var current = await Run(adb, token, "-s", device.Serial, "reverse", "--list");
            if (current.Contains(spec, StringComparison.Ordinal)) continue;
            var result = await Run(adb, token, "-s", device.Serial, "reverse", spec, spec);
            if (result.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                Ready = false;
                Status = "O adb recusou o túnel: " + result.Trim().Split('\n').Last();
                return;
            }
        }
        Ready = true;
        var wifi = ready.Any(d => d.Serial.Contains(':'));
        Status = (wifi ? "Celular ligado pelo Wi-Fi: " : "Celular ligado pelo cabo: ")
                 + string.Join(", ", ready.Select(d => d.Model ?? d.Serial).Distinct());
    }

    const int WifiPort = 5555;
    static string KnownFile => Path.Combine(IslandSettings.Folder, "adb-wifi.txt");

    static List<string> KnownWifi()
    {
        try { return File.Exists(KnownFile) ? File.ReadAllLines(KnownFile).Where(l => l.Length > 0).Distinct().ToList() : []; }
        catch { return []; }
    }

    static void Remember(string ip)
    {
        var list = KnownWifi();
        if (list.Contains(ip)) return;
        list.Insert(0, ip);
        try
        {
            Directory.CreateDirectory(IslandSettings.Folder);
            File.WriteAllLines(KnownFile, list.Take(3));
        }
        catch { }
    }

    static async Task<string?> WifiAddress(string adb, string serial, CancellationToken token)
    {
        var output = await Run(adb, token, "-s", serial, "shell", "ip", "-4", "addr", "show", "wlan0");
        var match = System.Text.RegularExpressions.Regex.Match(output, @"inet (\d+\.\d+\.\d+\.\d+)/");
        return match.Success ? match.Groups[1].Value : null;
    }

    sealed record Device(string Serial, string State, string? Model);

    static List<Device> ParseDevices(string output)
    {
        var list = new List<Device>();
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] == "List" || parts[0].StartsWith('*')) continue;
            if (parts[1] is not ("device" or "unauthorized" or "offline")) continue;
            var model = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?[6..].Replace('_', ' ');
            list.Add(new Device(parts[0], parts[1], model));
        }
        return list;
    }

    // adb starts its server on the first call and the server keeps our pipes open, so wait for the client to
    // exit and then take whatever arrived instead of reading to the end of the stream.
    static async Task<string> Run(string adb, CancellationToken token, params string[] args)
    {
        var start = new ProcessStartInfo(adb)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("adb não abriu");
        var text = new System.Text.StringBuilder();
        void Add(object _, DataReceivedEventArgs e)
        {
            if (e.Data != null) lock (text) text.AppendLine(e.Data);
        }
        process.OutputDataReceived += Add;
        process.ErrorDataReceived += Add;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        await Task.Delay(150, token);
        lock (text) return text.ToString();
    }

    /// <summary>adb from PATH, winget (scrcpy / Platform Tools) or the Android SDK.</summary>
    public static string? FindAdb()
    {
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "adb.exe"));
        foreach (var sdk in new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"), Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") })
            if (!string.IsNullOrEmpty(sdk)) candidates.Add(Path.Combine(sdk, "platform-tools", "adb.exe"));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "adb.exe"));
        try
        {
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
                candidates.AddRange(Directory.EnumerateFiles(packages, "adb.exe", SearchOption.AllDirectories));
        }
        catch { }
        return candidates.FirstOrDefault(File.Exists);
    }
}
