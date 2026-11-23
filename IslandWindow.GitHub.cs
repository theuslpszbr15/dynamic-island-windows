using System.Diagnostics;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// GitHub on the island: a CI run that failed (or passed again), a new pull request or a new release on the watched
// repositories shows as a card; a click opens it on github.com.
public partial class IslandWindow
{
    readonly GitHubWatch _github = new();
    DispatcherTimer? _githubTimer;
    bool _githubBusy;

    public void ApplyGitHub()
    {
        if (!_settings.GitHubWatch || SnapshotMode)
        {
            _githubTimer?.Stop();
            return;
        }
        _githubTimer ??= MakeTimer(300, () => _ = CheckGitHubAsync());
        _githubTimer.Start();
        _ = CheckGitHubAsync();
    }

    public async Task CheckGitHubAsync()
    {
        if (_githubBusy || !_settings.GitHubWatch) return;
        _githubBusy = true;
        try
        {
            var events = await _github.CheckAsync(_settings.GitHubRepos, _settings.GitHubCi, _settings.GitHubPulls, _settings.GitHubReleases);
            // a few at a time, the rest summed up
            foreach (var e in events.Take(3))
            {
                ShowGitHub(e);
                await Task.Delay(TimeSpan.FromSeconds(6.5));
            }
            if (events.Count > 3)
                ShowInfo("\uE943", $"+{events.Count - 3} novidades no GitHub", "", seconds: 4);
        }
        finally { _githubBusy = false; }
    }

    void ShowGitHub(GitHubEvent e)
    {
        var glyph = e.Kind switch
        {
            "ci-failed" => "\uE783",
            "ci-fixed" => "\uE73E",
            "pr" => "\uE8F1",
            _ => "\uE7B8",
        };
        var repo = e.Repo[(e.Repo.IndexOf('/') + 1)..];
        ShowCard(null, $"{e.Detail}: {e.Title}", repo, e.Kind == "release" ? "Clique para ver o que mudou" : "Clique para abrir no GitHub",
            seconds: e.Kind == "ci-failed" ? 10 : 6, important: e.Kind == "ci-failed", glyph: glyph);
        // only github.com pages open from here
        if (Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com")
            _notifyAction = () => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
