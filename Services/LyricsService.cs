using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

public sealed record LyricLine(TimeSpan At, string Text);

/// <summary>Time-synced lyrics from LRCLIB (free, no key), cached on disk.</summary>
public static partial class LyricsService
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland (github.com/theuslpszbr15/dynamic-island-windows)");
        return http;
    }

    static string CacheFolder => Path.Combine(IslandSettings.Folder, "lyrics");

    [GeneratedRegex(@"^\s*\[(\d+):(\d+(?:[.:]\d+)?)\](.*)$")]
    private static partial Regex LrcLine();

    public static async Task<IReadOnlyList<LyricLine>?> FindAsync(string title, string artist, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{artist}\n{title}".ToLowerInvariant())));
        var cache = Path.Combine(CacheFolder, key + ".lrc");

        // An empty cache file means "LRCLIB has nothing"; ask again after a week.
        if (File.Exists(cache) && (new FileInfo(cache).Length > 0 || File.GetLastWriteTime(cache) > DateTime.Now.AddDays(-7)))
        {
            var cached = await File.ReadAllTextAsync(cache);
            return cached.Length > 0 ? Parse(cached) : null;
        }

        var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        if (duration > TimeSpan.FromSeconds(5)) url += $"&duration={(int)Math.Round(duration.TotalSeconds)}";

        string synced = "";
        using (var response = await Http.GetAsync(url))
        {
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (json.RootElement.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
                    synced = s.GetString() ?? "";
            }
            else if (response.StatusCode != System.Net.HttpStatusCode.NotFound) return null; // try again later
        }

        Directory.CreateDirectory(CacheFolder);
        await File.WriteAllTextAsync(cache, synced);
        return synced.Length > 0 ? Parse(synced) : null;
    }

    public static IReadOnlyList<LyricLine> Parse(string lrc)
    {
        List<LyricLine> lines = [];
        foreach (var raw in lrc.Split('\n'))
        {
            var match = LrcLine().Match(raw);
            if (!match.Success) continue;
            var seconds = double.Parse(match.Groups[2].Value.Replace(':', '.'), System.Globalization.CultureInfo.InvariantCulture);
            var at = TimeSpan.FromMinutes(int.Parse(match.Groups[1].Value)) + TimeSpan.FromSeconds(seconds);
            lines.Add(new LyricLine(at, match.Groups[3].Value.Trim()));
        }
        lines.Sort((a, b) => a.At.CompareTo(b.At));
        return lines;
    }

    /// <summary>The line being sung at <paramref name="position"/>, or null before the first one.</summary>
    public static string? LineAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        string? current = null;
        foreach (var line in lines)
        {
            if (line.At > position) break;
            current = line.Text;
        }
        return current;
    }
}
