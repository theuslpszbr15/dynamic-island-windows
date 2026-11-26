using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>Which full-screen app in front is a game: one Windows' Game Bar knows (GameConfigStore), one installed by a
/// game launcher, or anything in exclusive Direct3D full screen. Browsers and players never count.</summary>
public static class GameDetector
{
    static readonly string[] LauncherFolders =
    [
        @"\steamapps\common\", @"\Epic Games\", @"\XboxGames\", @"\Riot Games\", @"\GOG Galaxy\Games\", @"\EA Games\",
        @"\Electronic Arts\", @"\Ubisoft Game Launcher\games\", @"\Battle.net\", @"\Rockstar Games\", @"\Roblox\Versions\",
        @"\Minecraft Launcher\", @"\Hoyoverse\", @"\Wargaming.net\",
    ];

    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "zen", "vlc", "mpc-hc64", "mpc-be64",
        "PotPlayerMini64", "mpv", "wmplayer", "Video.UI", "Microsoft.Media.Player", "Photos", "explorer", "ApplicationFrameHost",
        "POWERPNT", "Teams", "ms-teams", "Zoom", "obs64", "Spotify", "Discord", "Code", "devenv", "Netflix", "DynamicIsland",
    };

    static HashSet<string>? _known;
    static DateTime _knownAt;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref uint size);

    /// <summary>The game in front (its process and exe), or null. <paramref name="exclusive"/>: Windows reports exclusive
    /// Direct3D full screen, which only games use.</summary>
    public static (uint Pid, string Path)? Foreground(bool exclusive)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == Environment.ProcessId) return null;
        var path = ProcessPath(pid);
        if (path == null) return exclusive ? (pid, "") : null;
        if (NotGames.Contains(Path.GetFileNameWithoutExtension(path))) return null;
        return exclusive || IsGame(path) ? (pid, path) : null;
    }

    public static bool IsGame(string path) =>
        LauncherFolders.Any(folder => path.Contains(folder, StringComparison.OrdinalIgnoreCase)) || Known().Contains(path);

    /// <summary>The games Windows itself has recognized (the Game Bar's list), refreshed every 10 minutes.</summary>
    static HashSet<string> Known()
    {
        if (_known != null && DateTime.Now - _knownAt < TimeSpan.FromMinutes(10)) return _known;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            foreach (var name in children?.GetSubKeyNames() ?? [])
            {
                using var child = children!.OpenSubKey(name);
                if (child?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0) known.Add(exe);
            }
        }
        catch { }
        (_known, _knownAt) = (known, DateTime.Now);
        return known;
    }

    static string? ProcessPath(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(process); }
    }
}

/// <summary>The graphics card for the game meter: how busy its 3D engine is (the same counters as Task Manager) and its
/// temperature (D3DKMT, what Task Manager shows for dedicated cards). Neither needs administrator rights.</summary>
public sealed class GpuStats : IDisposable
{
    const uint PdhFmtDouble = 0x200, PdhMoreData = 0x800007D2;
    const int KmtqaiAdapterPerfData = 62;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)] struct AdapterInfo { public uint Adapter; public uint LuidLow; public int LuidHigh; public uint Sources; public int Precise; }
    [StructLayout(LayoutKind.Sequential)] struct EnumAdapters { public uint Count; public IntPtr Adapters; }
    [StructLayout(LayoutKind.Sequential)] struct QueryInfo { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }
    [StructLayout(LayoutKind.Sequential)]
    struct PerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PcieBandwidth;
        public uint FanRpm, Power, Temperature; // Temperature in tenths of a degree Celsius
        public byte PowerStateOverride;
    }
    [StructLayout(LayoutKind.Sequential)] struct CloseAdapter { public uint Adapter; }

    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref EnumAdapters args);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QueryInfo args);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter args);

    IntPtr _query, _counter;

    public GpuStats()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0) _counter = IntPtr.Zero;
        PdhCollectQueryData(_query); // a rate: the first reading only sets the start
    }

    /// <summary>The busiest card's 3D engines, 0–100, since the last call; null when Windows has no GPU counters.</summary>
    public double? Usage()
    {
        if (_query == IntPtr.Zero || _counter == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        uint size = 0;
        if (PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out _, IntPtr.Zero) != PdhMoreData || size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out var count, buffer) != 0) return null;
            var perCard = new Dictionary<string, double>();
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * 24; // PDH_FMT_COUNTERVALUE_ITEM_W: name, status, (pad), double
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                if (!name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase) || Marshal.ReadInt32(item + 8) != 0) continue;
                var luid = name.IndexOf("luid_", StringComparison.Ordinal) is var at and >= 0 && name.IndexOf("_phys", at, StringComparison.Ordinal) is var end and > 0
                    ? name[at..end] : "";
                perCard[luid] = perCard.GetValueOrDefault(luid) + BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + 16));
            }
            return perCard.Count == 0 ? 0 : Math.Clamp(perCard.Values.Max(), 0, 100);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The hottest card in °C, or null when none reports it (integrated graphics usually don't).</summary>
    public static double? Temperature()
    {
        var args = new EnumAdapters();
        if (D3DKMTEnumAdapters2(ref args) != 0 || args.Count == 0) return null;
        var size = Marshal.SizeOf<AdapterInfo>();
        args.Adapters = Marshal.AllocHGlobal(size * (int)args.Count);
        double? hottest = null;
        try
        {
            if (D3DKMTEnumAdapters2(ref args) != 0) return null;
            for (var i = 0; i < args.Count; i++)
            {
                var adapter = Marshal.PtrToStructure<AdapterInfo>(args.Adapters + i * size);
                var perf = new PerfData();
                var data = Marshal.AllocHGlobal(Marshal.SizeOf<PerfData>());
                try
                {
                    Marshal.StructureToPtr(perf, data, false);
                    var query = new QueryInfo { Adapter = adapter.Adapter, Type = KmtqaiAdapterPerfData, Data = data, Size = (uint)Marshal.SizeOf<PerfData>() };
                    if (D3DKMTQueryAdapterInfo(ref query) == 0)
                    {
                        perf = Marshal.PtrToStructure<PerfData>(data);
                        var celsius = perf.Temperature / 10.0;
                        if (celsius is > 5 and < 130 && (hottest == null || celsius > hottest)) hottest = celsius;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(data);
                    var close = new CloseAdapter { Adapter = adapter.Adapter };
                    D3DKMTCloseAdapter(ref close);
                }
            }
        }
        finally { Marshal.FreeHGlobal(args.Adapters); }
        return hottest;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = _counter = IntPtr.Zero;
    }
}

/// <summary>Frames per second of a game, counted from the Present calls DirectX reports to Windows' event tracing
/// (the way PresentMon and the Game Bar do it). Reading that trace needs the "Performance Log Users" group, which
/// <see cref="RequestAccess"/> joins once (an administrator prompt, then signing out and in).</summary>
public sealed class FpsCounter : IDisposable
{
    const string Session = "DynamicIslandFps";
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9"), D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    const ushort DxgiPresentStart = 42, D3D9PresentStart = 1;
    const int PropertiesSize = 120, NameBytes = 128;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTrace(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTrace(ulong handle, string? name, IntPtr properties, uint code);
    [DllImport("advapi32.dll")]
    static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern ulong OpenTrace(IntPtr logfile);
    [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void RecordCallback(IntPtr record);

    readonly ConcurrentDictionary<uint, int[]> _presents = new();
    readonly RecordCallback _callback;
    ulong _session, _trace;
    IntPtr _properties, _logfile, _name;
    Thread? _thread;
    DateTime _since = DateTime.Now;

    FpsCounter() => _callback = OnRecord;

    /// <summary>This account may read the trace: it is in the group (from the last sign-in) or runs elevated.</summary>
    public static bool Allowed
    {
        get
        {
            using var me = WindowsIdentity.GetCurrent();
            return me.Groups?.Any(g => g.Value == "S-1-5-32-559") == true || new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts counting; null when Windows refuses (no permission) or the trace can't start.</summary>
    public static FpsCounter? Start()
    {
        var counter = new FpsCounter();
        if (counter.Open()) return counter;
        counter.Dispose();
        return null;
    }

    bool Open()
    {
        _properties = Properties();
        var error = StartTrace(out _session, Session, _properties);
        if (error == 183) // ERROR_ALREADY_EXISTS: left over from a crash
        {
            ControlTrace(0, Session, _properties, 1);
            Marshal.FreeHGlobal(_properties);
            _properties = Properties();
            error = StartTrace(out _session, Session, _properties);
        }
        if (error != 0)
        {
            Log.Warn("Fps", $"StartTrace failed ({error})");
            _session = 0;
            return false;
        }
        foreach (var provider in new[] { Dxgi, D3D9 })
        {
            var guid = provider;
            EnableTraceEx2(_session, ref guid, 1, 4, ulong.MaxValue, 0, 0, IntPtr.Zero);
        }
        // EVENT_TRACE_LOGFILEW: LoggerName at 8, ProcessTraceMode at 28, EventRecordCallback at 424 (448 bytes, x64)
        _logfile = Marshal.AllocHGlobal(448);
        for (var i = 0; i < 448; i++) Marshal.WriteByte(_logfile, i, 0);
        _name = Marshal.StringToHGlobalUni(Session);
        Marshal.WriteIntPtr(_logfile, 8, _name);
        Marshal.WriteInt32(_logfile, 28, 0x100 | 0x10000000); // PROCESS_TRACE_MODE_REAL_TIME | EVENT_RECORD
        Marshal.WriteIntPtr(_logfile, 424, Marshal.GetFunctionPointerForDelegate(_callback));
        _trace = OpenTrace(_logfile);
        if (_trace == ulong.MaxValue)
        {
            Log.Warn("Fps", "OpenTrace failed");
            _trace = 0;
            return false;
        }
        _thread = new Thread(() => ProcessTrace([_trace], 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "FPS trace" };
        _thread.Start();
        return true;
    }

    /// <summary>EVENT_TRACE_PROPERTIES for a real-time session, with room for its name after it.</summary>
    static IntPtr Properties()
    {
        var size = PropertiesSize + NameBytes;
        var p = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, size);          // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);            // Wnode.ClientContext: QPC time stamps
        Marshal.WriteInt32(p, 44, 0x00020000);   // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);           // BufferSize, KB
        Marshal.WriteInt32(p, 64, 0x100);        // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);            // FlushTimer, seconds
        Marshal.WriteInt32(p, 116, PropertiesSize); // LoggerNameOffset
        return p;
    }

    void OnRecord(IntPtr record)
    {
        // EVENT_RECORD.EventHeader: ProcessId at 12, ProviderId at 24, EventDescriptor.Id at 40
        var id = (ushort)Marshal.ReadInt16(record, 40);
        if (id != DxgiPresentStart && id != D3D9PresentStart) return;
        var provider = Marshal.PtrToStructure<Guid>(record + 24);
        if (!(provider == Dxgi && id == DxgiPresentStart) && !(provider == D3D9 && id == D3D9PresentStart)) return;
        var pid = (uint)Marshal.ReadInt32(record, 12);
        Interlocked.Increment(ref _presents.GetOrAdd(pid, _ => new int[1])[0]);
    }

    /// <summary>Frames per second of <paramref name="pid"/> since the last call; null before its first frame.</summary>
    public double? Read(uint pid)
    {
        var now = DateTime.Now;
        var seconds = (now - _since).TotalSeconds;
        _since = now;
        if (!_presents.TryGetValue(pid, out var count)) { _presents.Clear(); return null; }
        var frames = Interlocked.Exchange(ref count[0], 0);
        foreach (var other in _presents.Keys.Where(k => k != pid).ToList()) _presents.TryRemove(other, out _);
        return seconds > 0.2 ? frames / seconds : null;
    }

    /// <summary>Joins "Performance Log Users" (by its well-known SID, so any Windows language) after one administrator
    /// prompt. It counts from the next sign-in. False when the person said no.</summary>
    public static bool RequestAccess()
    {
        using var me = WindowsIdentity.GetCurrent();
        var user = me.Name.Replace("'", "''");
        var script = $"$g = (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-559').Translate([System.Security.Principal.NTAccount]).Value.Split('\\')[-1]; " +
                     $"net localgroup \"$g\" '{user}' /add | Out-Null; exit 0";
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script.Replace("\"", "\\\"")}\"")
            {
                Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(60_000);
            return process?.ExitCode == 0;
        }
        catch { return false; } // cancelled at the UAC prompt
    }

    public void Dispose()
    {
        if (_session != 0 && _properties != IntPtr.Zero) ControlTrace(_session, null, _properties, 1); // EVENT_TRACE_CONTROL_STOP
        if (_trace != 0) CloseTrace(_trace);
        _thread?.Join(2000);
        if (_properties != IntPtr.Zero) Marshal.FreeHGlobal(_properties);
        if (_logfile != IntPtr.Zero) Marshal.FreeHGlobal(_logfile);
        if (_name != IntPtr.Zero) Marshal.FreeHGlobal(_name);
        _session = _trace = 0;
        _properties = _logfile = _name = IntPtr.Zero;
    }
}
