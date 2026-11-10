using System.Diagnostics;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

// Dev tool: `DynamicIsland.exe --perf <folder>` keeps the island in a few everyday states with demo data and
// writes how much CPU each one costs while nothing happens, plus memory, to perf-idle.txt.
public partial class IslandWindow
{
    const int PerfSettleMs = 3000, PerfSampleMs = 20000;

    public async Task RunPerfAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        _ready = true;
        var report = new List<string> { $"Dynamic Island perf  {DateTime.Now:yyyy-MM-dd HH:mm}  {Environment.ProcessorCount} logical cores" };
        await Task.Delay(PerfSettleMs);

        _lastActivity = DateTime.Now;
        UpdatePet();
        report.Add("compact clock, mascot awake:  " + await IdleCpu());

        _lastActivity = DateTime.Now - TimeSpan.FromMinutes(5);
        UpdatePet();
        report.Add("compact clock, mascot asleep: " + await IdleCpu());

        OnMedia(new MediaSnapshot("Blinding Lights", "The Weeknd", DemoArt(), Color.FromRgb(0xFF, 0x6B, 0x8A), true,
            TimeSpan.FromSeconds(74), TimeSpan.FromSeconds(200), DateTimeOffset.Now));
        _transient = Transient.None;
        Retarget();
        report.Add("compact media playing:        " + await IdleCpu());

        SetUserHidden(true);
        report.Add("hidden (fullscreen), playing: " + await IdleCpu());
        SetUserHidden(false);

        OnMedia(null);
        _transient = Transient.None;
        Retarget();

        // The microphone/camera indicator: what one look at the registry costs.
        var watch = Stopwatch.StartNew();
        const int reads = 200;
        for (var i = 0; i < reads; i++) PrivacyMonitor.Read();
        report.Add($"privacy registry read:        {watch.Elapsed.TotalMilliseconds / reads:0.00} ms each");

        await Task.Delay(PerfSettleMs);
        report.Add("memory:                       " + Memory());

        // What the idle trim gives back after a quiet stretch.
        _lastActivity = DateTime.Now - TrimAfter - TimeSpan.FromMinutes(1);
        TrimWhenIdle();
        await Task.Delay(PerfSettleMs);
        report.Add("memory after idle trim:       " + Memory());

        File.WriteAllLines(System.IO.Path.Combine(folder, "perf-idle.txt"), report);
    }

    static string Memory()
    {
        using var process = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        return $"working set={process.WorkingSet64 / 1048576.0:0.0}MB private={process.PrivateMemorySize64 / 1048576.0:0.0}MB " +
               $"managed heap={info.HeapSizeBytes / 1048576.0:0.0}MB committed={info.TotalCommittedBytes / 1048576.0:0.0}MB";
    }

    /// <summary>CPU used by this process over a quiet stretch, as a share of one core.</summary>
    static async Task<string> IdleCpu()
    {
        await Task.Delay(PerfSettleMs);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        await Task.Delay(PerfSampleMs);
        process.Refresh();
        var cpu = (process.TotalProcessorTime - before).TotalMilliseconds;
        return $"{cpu / clock.Elapsed.TotalMilliseconds * 100:0.00}% of one core ({cpu:0} ms CPU in {clock.Elapsed.TotalSeconds:0} s)";
    }
}
