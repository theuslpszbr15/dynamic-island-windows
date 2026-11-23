using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media;

namespace DynamicIsland.Services;

/// <summary>How much of a plan is used: the binding window (the fuller one) and when it renews.</summary>
public sealed record PlanUse(string Name, Color Color, double UsedPercent, string Window, DateTimeOffset? ResetsAt);

/// <summary>
/// Plan usage of the AI tools on this PC, read where each tool keeps it: GitHub Copilot from GitHub's API (with the
/// GitHub CLI's login), Claude Code from Anthropic's usage API (with Claude Code's own login) and Codex from its
/// session logs. Tokens only go to their own service and are never saved or logged.
/// </summary>
public static class PlanUsage
{
    public static readonly Color CopilotColor = Color.FromRgb(0xA3, 0x7B, 0xF7), ClaudeColor = Color.FromRgb(0xD9, 0x77, 0x57),
        CodexColor = Color.FromRgb(0x10, 0xA3, 0x7F);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>What could be read, plus a line per tool explaining what is missing (for the settings).</summary>
    public static async Task<(List<PlanUse> Plans, List<string> Notes)> ReadAllAsync()
    {
        var plans = new List<PlanUse>();
        var notes = new List<string>();
        async Task Add(string name, Func<Task<PlanUse?>> read, string missing)
        {
            try
            {
                if (await read() is { } plan) plans.Add(plan);
                else notes.Add($"{name}: {missing}");
            }
            catch (Exception ex)
            {
                Log.Warn("PlanUsage", $"{name}: {ex.Message}");
                notes.Add($"{name}: não consegui ler agora.");
            }
        }
        await Add("Copilot", CopilotAsync, "entre no GitHub CLI (gh auth login) com a conta do Copilot.");
        await Add("Claude", ClaudeAsync, "entre no Claude Code (plano Pro ou Max) neste PC.");
        await Add("Codex", () => Task.FromResult(Codex()), "use o Codex CLI uma vez neste PC.");
        return (plans, notes);
    }

    // ───────────────────────── GitHub Copilot ─────────────────────────

    static async Task<PlanUse?> CopilotAsync()
    {
        if (await GitHubTokenAsync() is not { } token) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        request.Headers.UserAgent.ParseAdd("DynamicIsland");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseCopilot(json.RootElement);
    }

    internal static PlanUse? ParseCopilot(JsonElement root)
    {
        if (!root.TryGetProperty("quota_snapshots", out var snapshots)
            || !snapshots.TryGetProperty("premium_interactions", out var premium)
            || premium.TryGetProperty("unlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True
            || !premium.TryGetProperty("percent_remaining", out var remaining) || remaining.ValueKind != JsonValueKind.Number)
            return null;
        DateTimeOffset? reset = root.TryGetProperty("quota_reset_date", out var date) && DateTimeOffset.TryParse(date.GetString(), out var at) ? at : null;
        return new PlanUse("Copilot", CopilotColor, Math.Clamp(100 - remaining.GetDouble(), 0, 100), "mês", reset);
    }

    static Task<string?> GitHubTokenAsync() => GitHubWatch.TokenAsync();

    // ───────────────────────── Claude Code ─────────────────────────

    static async Task<PlanUse?> ClaudeAsync()
    {
        var file = Path.Combine(Home, ".claude", ".credentials.json");
        if (!File.Exists(file)) return null;
        using var credentials = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        if (!credentials.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
            || !oauth.TryGetProperty("accessToken", out var access) || access.GetString() is not { Length: > 0 } token) return null;
        // an expired login is renewed by Claude Code itself; the island never touches the refresh token
        if (oauth.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number
            && DateTimeOffset.FromUnixTimeMilliseconds(expires.GetInt64()) < DateTimeOffset.Now) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("DynamicIsland");
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseClaude(json.RootElement);
    }

    internal static PlanUse? ParseClaude(JsonElement root)
    {
        PlanUse? best = null;
        foreach (var (key, window) in new[] { ("five_hour", "5 h"), ("seven_day", "semana") })
        {
            if (!root.TryGetProperty(key, out var part) || part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number) continue;
            DateTimeOffset? reset = part.TryGetProperty("resets_at", out var at) && at.ValueKind == JsonValueKind.String
                                    && DateTimeOffset.TryParse(at.GetString(), out var when) ? when : null;
            var plan = new PlanUse("Claude", ClaudeColor, Math.Clamp(used.GetDouble(), 0, 100), window, reset);
            if (best == null || plan.UsedPercent > best.UsedPercent) best = plan;
        }
        return best;
    }

    // ───────────────────────── Codex ─────────────────────────

    /// <summary>The newest rate-limit snapshot Codex wrote in its session logs (~/.codex/sessions/…/rollout-*.jsonl).</summary>
    static PlanUse? Codex()
    {
        var folder = Path.Combine(Home, ".codex", "sessions");
        if (!Directory.Exists(folder)) return null;
        var files = new DirectoryInfo(folder).EnumerateFiles("rollout-*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(3);
        foreach (var file in files)
        {
            string[] lines;
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                lines = reader.ReadToEnd().Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var json = JsonDocument.Parse(lines[i]);
                    if (ParseCodex(json.RootElement, file.LastWriteTimeUtc) is { } plan) return plan;
                }
                catch (JsonException) { }
            }
        }
        return null;
    }

    internal static PlanUse? ParseCodex(JsonElement line, DateTime writtenUtc)
    {
        if (Find(line, "rate_limits") is not { } limits) return null;
        PlanUse? best = null;
        foreach (var (key, fallback) in new[] { ("primary", "5 h"), ("secondary", "semana") })
        {
            if (!limits.TryGetProperty(key, out var part) || part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("used_percent", out var used) || used.ValueKind != JsonValueKind.Number) continue;
            var window = part.TryGetProperty("window_minutes", out var minutes) && minutes.ValueKind == JsonValueKind.Number
                ? minutes.GetDouble() >= 1440 ? "semana" : $"{Math.Round(minutes.GetDouble() / 60)} h" : fallback;
            DateTimeOffset? reset = null;
            if (part.TryGetProperty("resets_at", out var at) && at.ValueKind == JsonValueKind.Number)
                reset = DateTimeOffset.FromUnixTimeSeconds(at.GetInt64());
            else if (part.TryGetProperty("resets_in_seconds", out var seconds) && seconds.ValueKind == JsonValueKind.Number)
                reset = new DateTimeOffset(writtenUtc, TimeSpan.Zero).AddSeconds(seconds.GetDouble());
            // a window that already renewed since the log was written is empty again
            var percent = reset is { } r && r < DateTimeOffset.Now ? 0 : used.GetDouble();
            var plan = new PlanUse("Codex", CodexColor, Math.Clamp(percent, 0, 100), window, reset);
            if (best == null || plan.UsedPercent > best.UsedPercent) best = plan;
        }
        return best;
    }

    static JsonElement? Find(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.Object) return property.Value;
            if (Find(property.Value, name) is { } inner) return inner;
        }
        return null;
    }

    /// <summary>"renova em 2 h", "renova dia 01/12".</summary>
    public static string Renews(DateTimeOffset? at)
    {
        if (at is not { } when) return "";
        var left = when - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return "renovado";
        if (left < TimeSpan.FromHours(1)) return $"renova em {Math.Max(1, (int)left.TotalMinutes)} min";
        if (left < TimeSpan.FromHours(24)) return $"renova em {(int)left.TotalHours} h";
        return $"renova dia {when.LocalDateTime:dd/MM}";
    }
}
