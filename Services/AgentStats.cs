using System.Text.Json;

namespace DynamicIsland.Services;

public sealed class DayStats
{
    public double WorkSeconds { get; set; }
    public int Edits { get; set; }
    public int Commands { get; set; }
    public int Sessions { get; set; }
    public int Approvals { get; set; }
    public Dictionary<string, double> Projects { get; set; } = [];
}

public sealed record WeekRecap(DateOnly From, DateOnly To, double WorkSeconds, int Edits, int Commands, int Sessions,
    int Approvals, string? TopProject, DayOfWeek? BusiestDay, int ActiveDays);

/// <summary>Per-day agent activity, kept locally in stats.json for the weekly recap.</summary>
public static class AgentStats
{
    static string FilePath => Path.Combine(IslandSettings.Folder, "stats.json");
    static Dictionary<string, DayStats>? _days;
    static DateTime _savedAt;

    static Dictionary<string, DayStats> Days
    {
        get
        {
            if (_days != null) return _days;
            try
            {
                if (File.Exists(FilePath))
                    _days = JsonSerializer.Deserialize<Dictionary<string, DayStats>>(File.ReadAllText(FilePath));
            }
            catch { }
            return _days ??= [];
        }
    }

    static DayStats Today
    {
        get
        {
            var key = DateTime.Today.ToString("yyyy-MM-dd");
            if (!Days.TryGetValue(key, out var day)) Days[key] = day = new DayStats();
            return day;
        }
    }

    public static void Record(string? step, bool newSession, bool approval, TimeSpan worked, string? project)
    {
        var day = Today;
        if (newSession) day.Sessions++;
        if (approval) day.Approvals++;
        if (step == "edit") day.Edits++;
        else if (step == "run") day.Commands++;
        if (worked > TimeSpan.Zero)
        {
            day.WorkSeconds += worked.TotalSeconds;
            if (!string.IsNullOrEmpty(project))
                day.Projects[project] = day.Projects.GetValueOrDefault(project) + worked.TotalSeconds;
        }
        if (DateTime.Now - _savedAt > TimeSpan.FromSeconds(30)) Save();
    }

    public static void Save()
    {
        if (_days == null) return;
        _savedAt = DateTime.Now;
        try
        {
            // keep three months
            var cutoff = DateTime.Today.AddDays(-90).ToString("yyyy-MM-dd");
            foreach (var old in _days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) _days.Remove(old);
            Directory.CreateDirectory(IslandSettings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_days));
        }
        catch { }
    }

    /// <summary>The 7 days ending yesterday (on Monday that's last Monday–Sunday).</summary>
    public static WeekRecap LastWeek()
    {
        var to = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        var from = to.AddDays(-6);
        double work = 0;
        int edits = 0, commands = 0, sessions = 0, approvals = 0, active = 0;
        Dictionary<string, double> projects = [];
        DayOfWeek? busiest = null;
        double busiestSeconds = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (!Days.TryGetValue(d.ToString("yyyy-MM-dd"), out var day)) continue;
            work += day.WorkSeconds;
            edits += day.Edits;
            commands += day.Commands;
            sessions += day.Sessions;
            approvals += day.Approvals;
            if (day.WorkSeconds > 0 || day.Sessions > 0) active++;
            if (day.WorkSeconds > busiestSeconds) { busiestSeconds = day.WorkSeconds; busiest = d.DayOfWeek; }
            foreach (var (name, seconds) in day.Projects) projects[name] = projects.GetValueOrDefault(name) + seconds;
        }
        var top = projects.OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault();
        return new WeekRecap(from, to, work, edits, commands, sessions, approvals, top, busiest, active);
    }
}
