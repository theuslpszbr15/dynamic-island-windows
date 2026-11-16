using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

/// <summary>An event coming up: when, what, and the link to join (Teams, Meet, Zoom, Webex) when there is one.</summary>
public sealed record CalendarEvent(DateTime Start, DateTime End, string Title, string? JoinUrl, string Source);

/// <summary>
/// The person's agenda for today and tomorrow, from secret iCal links (Google Calendar, Outlook on the web, iCloud…)
/// and from the classic Outlook on this PC when it is open. Recurring events (daily, weekly, monthly) are expanded.
/// </summary>
public static partial class CalendarService
{
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var proxy = System.Net.WebRequest.GetSystemWebProxy();
        proxy.Credentials = System.Net.CredentialCache.DefaultNetworkCredentials;
        var http = new HttpClient(new HttpClientHandler { Proxy = proxy, UseProxy = true }) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland-Calendar/1.0");
        return http;
    }

    const long MaxFeedBytes = 8 * 1024 * 1024;

    [GeneratedRegex(@"https://(?:teams\.microsoft\.com/l/meetup-join/[^\s""<>]+|teams\.live\.com/meet/[^\s""<>]+|meet\.google\.com/[a-z]{3}-[a-z]{4}-[a-z]{3}|[\w.-]*zoom\.us/(?:j|my)/[^\s""<>]+|[\w.-]*webex\.com/[^\s""<>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex JoinLink();

    public static string? FindJoinUrl(params string?[] texts)
    {
        foreach (var text in texts)
            if (!string.IsNullOrEmpty(text) && JoinLink().Match(text) is { Success: true } m)
                return m.Value.TrimEnd('.', ',', ')', '>', ']');
        return null;
    }

    /// <summary>"webcal://…" and plain links become https; anything else is refused.</summary>
    public static string? NormalizeFeed(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) t = "https://" + t[9..];
        return Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.ToString() : null;
    }

    /// <summary>Everything that starts between now - 1 h and the end of tomorrow, sorted.</summary>
    public static async Task<List<CalendarEvent>> LoadAsync(IEnumerable<string> feeds, bool outlook, CancellationToken cancel = default)
    {
        var from = DateTime.Now.AddHours(-1);
        var to = DateTime.Today.AddDays(2);
        var all = new List<CalendarEvent>();
        foreach (var feed in feeds)
        {
            try
            {
                using var response = await Http.GetAsync(feed, HttpCompletionOption.ResponseHeadersRead, cancel);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxFeedBytes) continue;
                var text = await response.Content.ReadAsStringAsync(cancel);
                if (text.Length > MaxFeedBytes) continue;
                all.AddRange(ParseIcs(text, from, to, new Uri(feed).Host));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("Calendar", $"{new Uri(feed).Host}: {ex.Message}"); }
        }
        if (outlook)
            try { all.AddRange(await Task.Run(() => ReadOutlook(from, to), cancel)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("Calendar", "Outlook: " + ex.Message); }
        return all.GroupBy(e => (e.Start, e.Title)).Select(g => g.First()).OrderBy(e => e.Start).ToList();
    }

    // ───────────────────────── iCal ─────────────────────────

    public static List<CalendarEvent> ParseIcs(string ics, DateTime from, DateTime to, string source)
    {
        var events = new List<CalendarEvent>();
        // unfold: a line starting with a space or tab continues the previous one
        var lines = Regex.Replace(ics, @"\r?\n[ \t]", "").Split('\n');
        Dictionary<string, (string Params, string Value)>? ev = null;
        var exdates = new List<DateTime>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line == "BEGIN:VEVENT") { ev = new(StringComparer.OrdinalIgnoreCase); exdates.Clear(); continue; }
            if (line == "END:VEVENT" && ev != null)
            {
                Expand(ev, exdates, from, to, source, events);
                ev = null;
                continue;
            }
            if (ev == null) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var head = line[..colon];
            var value = line[(colon + 1)..];
            var semi = head.IndexOf(';');
            var name = semi < 0 ? head : head[..semi];
            var parameters = semi < 0 ? "" : head[(semi + 1)..];
            if (name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in value.Split(','))
                    if (ParseDate(parameters, part, out var ex, out _)) exdates.Add(ex);
                continue;
            }
            ev.TryAdd(name, (parameters, value));
        }
        return events;
    }

    static void Expand(Dictionary<string, (string Params, string Value)> ev, List<DateTime> exdates, DateTime from, DateTime to,
        string source, List<CalendarEvent> into)
    {
        if (!ev.TryGetValue("DTSTART", out var ds) || !ParseDate(ds.Params, ds.Value, out var start, out var allDay)) return;
        if (allDay) return; // all-day events don't need a countdown
        if (ev.TryGetValue("STATUS", out var status) && status.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) return;
        var end = ev.TryGetValue("DTEND", out var de) && ParseDate(de.Params, de.Value, out var e, out _) ? e : start.AddHours(1);
        var length = end - start;
        var title = Unescape(ev.TryGetValue("SUMMARY", out var s) ? s.Value : "Evento");
        var join = FindJoinUrl(ev.GetValueOrDefault("URL").Value, Unescape(ev.GetValueOrDefault("LOCATION").Value ?? ""),
            Unescape(ev.GetValueOrDefault("DESCRIPTION").Value ?? ""), ev.GetValueOrDefault("X-GOOGLE-CONFERENCE").Value);

        foreach (var occurrence in Occurrences(start, ev.TryGetValue("RRULE", out var rule) ? rule.Value : null, from, to))
            if (!exdates.Contains(occurrence) && occurrence + length >= from && occurrence <= to)
                into.Add(new CalendarEvent(occurrence, occurrence + length, title, join, source));
    }

    /// <summary>The starts of a (possibly recurring) event inside [from, to]: DAILY, WEEKLY (BYDAY), MONTHLY by date,
    /// with INTERVAL, COUNT and UNTIL.</summary>
    public static IEnumerable<DateTime> Occurrences(DateTime start, string? rrule, DateTime from, DateTime to)
    {
        if (string.IsNullOrEmpty(rrule))
        {
            yield return start;
            yield break;
        }
        var parts = rrule.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        var freq = parts.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var i) && i > 0 ? i : 1;
        var count = int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) ? c : int.MaxValue;
        var until = parts.TryGetValue("UNTIL", out var u) && ParseDate("", u, out var ud, out _) ? ud.Date.AddDays(1) : DateTime.MaxValue;
        var days = parts.TryGetValue("BYDAY", out var byday)
            ? byday.Split(',').Select(d => d.Length >= 2 ? d[^2..].ToUpperInvariant() : "").Select(DayOf).Where(d => d != null).Select(d => d!.Value).ToHashSet()
            : [start.DayOfWeek];

        var emitted = 0;
        switch (freq)
        {
            case "DAILY":
                for (var d = start; d <= to && d < until && emitted < count; d = d.AddDays(interval), emitted++)
                    if (d.AddDays(1) >= from) yield return d;
                break;
            case "WEEKLY":
                var weekStart = start.Date.AddDays(-(int)start.DayOfWeek);
                for (var w = weekStart; w <= to && emitted < count; w = w.AddDays(7 * interval))
                    for (var k = 0; k < 7 && emitted < count; k++)
                    {
                        var d = w.AddDays(k) + start.TimeOfDay;
                        if (d < start || !days.Contains(d.DayOfWeek)) continue;
                        if (d >= until || d > to) yield break;
                        emitted++;
                        if (d.AddDays(1) >= from) yield return d;
                    }
                break;
            case "MONTHLY":
                for (var d = start; d <= to && d < until && emitted < count; d = d.AddMonths(interval), emitted++)
                    if (d.AddDays(1) >= from) yield return d;
                break;
            default:
                yield return start;
                break;
        }
    }

    static DayOfWeek? DayOf(string code) => code switch
    {
        "SU" => DayOfWeek.Sunday, "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, _ => null,
    };

    /// <summary>"20261116T140000Z" (UTC), "20261116T140000" with TZID=… or floating, or "20261116" (all day), in local time.</summary>
    public static bool ParseDate(string parameters, string value, out DateTime local, out bool allDay)
    {
        local = default;
        allDay = false;
        value = value.Trim();
        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            local = day;
            allDay = true;
            return true;
        }
        var utc = value.EndsWith('Z');
        if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return false;
        if (utc)
        {
            local = DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime();
            return true;
        }
        var tzid = parameters.Split(';').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Equals("TZID", StringComparison.OrdinalIgnoreCase))?[1].Trim('"');
        local = tzid != null && FindZone(tzid) is { } zone
            ? TimeZoneInfo.ConvertTime(t, zone, TimeZoneInfo.Local)
            : t;
        return true;
    }

    static TimeZoneInfo? FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        try { if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows)) return TimeZoneInfo.FindSystemTimeZoneById(windows); } catch { }
        return null;
    }

    static string Unescape(string text) =>
        text.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    // ───────────────────────── Outlook (classic, on this PC) ─────────────────────────

    /// <summary>Only when Outlook is already open: creating its COM object would start it in the background.</summary>
    static List<CalendarEvent> ReadOutlook(DateTime from, DateTime to)
    {
        var list = new List<CalendarEvent>();
        if (System.Diagnostics.Process.GetProcessesByName("OUTLOOK").Length == 0) return list;
        var type = Type.GetTypeFromProgID("Outlook.Application");
        if (type == null) return list;
        dynamic app = Activator.CreateInstance(type)!;
        dynamic items = app.GetNamespace("MAPI").GetDefaultFolder(9).Items; // 9 = calendar
        items.IncludeRecurrences = true;
        items.Sort("[Start]");
        var filter = $"[Start] >= '{from.ToString("g", CultureInfo.CurrentCulture)}' AND [Start] <= '{to.ToString("g", CultureInfo.CurrentCulture)}'";
        dynamic found = items.Restrict(filter);
        var n = 0;
        foreach (dynamic item in found)
        {
            if (++n > 60) break;
            try
            {
                DateTime start = item.Start, end = item.End;
                if ((bool)item.AllDayEvent) continue;
                string subject = item.Subject ?? "Reunião", location = item.Location ?? "", body = "";
                try { body = item.Body ?? ""; } catch { }
                list.Add(new CalendarEvent(start, end, subject, FindJoinUrl(location, body), "Outlook"));
            }
            catch { }
        }
        return list;
    }
}
