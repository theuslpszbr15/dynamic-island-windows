using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DynamicIsland.Services;

/// <summary>Something new on a watched repository: a CI run that failed (or passed again), a pull request, a release.</summary>
public sealed record GitHubEvent(string Kind, string Repo, string Title, string Detail, string Url);

/// <summary>
/// Watches GitHub repositories for what matters while you work: CI runs that fail (and pass again), new pull requests
/// and new releases. Uses the GitHub CLI's login when there is one (more requests per hour, private repositories),
/// otherwise GitHub's public API. What was already shown is remembered, so nothing repeats after a restart.
/// </summary>
public sealed class GitHubWatch
{
    static readonly HttpClient Http = CreateClient();
    static string SeenFile => Path.Combine(IslandSettings.Folder, "github-seen.json");

    readonly HashSet<string> _seen;
    readonly Dictionary<string, bool> _failing = []; // "repo|workflow|branch" → last run failed

    public GitHubWatch()
    {
        try { _seen = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(SeenFile)) ?? []; }
        catch { _seen = []; }
    }

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), BaseAddress = new Uri("https://api.github.com/") };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>"owner/name" from a repository name or a github.com link; null when it isn't one.</summary>
    public static string? Normalize(string text)
    {
        var repo = text.Trim().TrimEnd('/');
        foreach (var prefix in new[] { "https://github.com/", "http://github.com/", "github.com/" })
            if (repo.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) repo = repo[prefix.Length..];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        var parts = repo.Split('/');
        return parts.Length == 2 && parts.All(p => p.Trim('.').Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            ? repo : null;
    }

    /// <summary>What is new since the last check. The first look at a repository only learns what is already there.</summary>
    public async Task<List<GitHubEvent>> CheckAsync(IEnumerable<string> repos, bool ci, bool pulls, bool releases)
    {
        var events = new List<GitHubEvent>();
        var token = await TokenAsync();
        foreach (var repo in repos.Select(Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var first = _seen.Add("repo:" + repo);
            try
            {
                if (ci && await GetAsync($"repos/{repo}/actions/runs?per_page=15", token) is { } runs) Runs(repo, runs, first, events);
                if (pulls && await GetAsync($"repos/{repo}/pulls?state=open&per_page=10", token) is { } open) Pulls(repo, open, first, events);
                if (releases && await GetAsync($"repos/{repo}/releases?per_page=5", token) is { } list) Releases(repo, list, first, events);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                Log.Warn("GitHubWatch", $"{repo}: {ex.Message}");
            }
        }
        Save();
        return events;
    }

    internal void Runs(string repo, JsonElement json, bool first, List<GitHubEvent> events)
    {
        if (!json.TryGetProperty("workflow_runs", out var runs)) return;
        // oldest first, so "passed again" follows the failure it fixes
        foreach (var run in runs.EnumerateArray().Reverse())
        {
            if (Text(run, "status") != "completed") continue;
            var id = "run:" + Text(run, "id");
            var key = $"{repo}|{Text(run, "name")}|{Text(run, "head_branch")}";
            var failed = Text(run, "conclusion") is "failure" or "timed_out" or "startup_failure";
            var wasFailing = _failing.TryGetValue(key, out var before) && before;
            _failing[key] = failed;
            if (!_seen.Add(id) || first) continue;
            var title = $"{Text(run, "name")} · {Text(run, "head_branch")}";
            var url = Text(run, "html_url");
            if (failed) events.Add(new GitHubEvent("ci-failed", repo, title, "O CI falhou", url));
            else if (wasFailing && Text(run, "conclusion") == "success") events.Add(new GitHubEvent("ci-fixed", repo, title, "O CI voltou a passar", url));
        }
    }

    internal void Pulls(string repo, JsonElement json, bool first, List<GitHubEvent> events)
    {
        foreach (var pull in json.EnumerateArray())
        {
            if (!_seen.Add("pr:" + repo + "#" + Text(pull, "number")) || first) continue;
            var author = pull.TryGetProperty("user", out var user) ? Text(user, "login") : "";
            events.Add(new GitHubEvent("pr", repo, Text(pull, "title"), $"PR #{Text(pull, "number")} de {author}", Text(pull, "html_url")));
        }
    }

    void Releases(string repo, JsonElement json, bool first, List<GitHubEvent> events)
    {
        foreach (var release in json.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!_seen.Add("release:" + Text(release, "id")) || first) continue;
            var name = Text(release, "name");
            events.Add(new GitHubEvent("release", repo, name.Length > 0 ? name : Text(release, "tag_name"), "Versão nova", Text(release, "html_url")));
        }
    }

    static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        } : "";

    static async Task<JsonElement?> GetAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    void Save()
    {
        try
        {
            // the newest few hundred are enough to never repeat an alert
            var keep = _seen.Count > 600 ? _seen.TakeLast(600).ToHashSet() : _seen;
            Directory.CreateDirectory(IslandSettings.Folder);
            AtomicFile.WriteAllText(SeenFile, JsonSerializer.Serialize(keep));
        }
        catch (Exception ex) { Log.Error("GitHubWatch.Save", ex); }
    }

    static string? _token;
    static DateTime _tokenAt;

    /// <summary>The GitHub CLI's token (the account logged in there), kept in memory for half an hour; null without it.</summary>
    public static async Task<string?> TokenAsync()
    {
        if (_token != null && DateTime.Now - _tokenAt < TimeSpan.FromMinutes(30)) return _token;
        try
        {
            using var gh = Process.Start(new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (gh == null) return null;
            var output = await gh.StandardOutput.ReadToEndAsync();
            if (!gh.WaitForExit(5000) || gh.ExitCode != 0) return null;
            var token = output.Trim();
            if (token.Length < 20 || token.Any(char.IsWhiteSpace)) return null;
            (_token, _tokenAt) = (token, DateTime.Now);
            return token;
        }
        catch (System.ComponentModel.Win32Exception) { return null; } // no GitHub CLI
    }
}
