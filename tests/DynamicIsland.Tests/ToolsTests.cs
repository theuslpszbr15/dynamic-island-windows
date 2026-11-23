using System.Text.Json;
using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class TranslatorTests
{
    [Fact]
    public void ReadsTheModelsJsonEvenWrappedInText()
    {
        var t = Translator.Parse("Claro!\n```json\n{\"de\":\"inglês\",\"para\":\"português\",\"texto\":\"Bom dia, equipe.\"}\n```");
        Assert.Equal(new Translation("Inglês", "Português", "Bom dia, equipe."), t);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Não sei traduzir isso.")]
    [InlineData("{\"de\":\"inglês\",\"para\":\"português\",\"texto\":\"\"}")]
    public void NothingUsableIsNull(string? answer) => Assert.Null(Translator.Parse(answer));

    /// <summary>JARVIS_E2E_GROQ=1 with the Groq key saved: English comes back in Portuguese, and Portuguese in English.</summary>
    [Fact]
    public async Task TranslatesBothWays()
    {
        if (Environment.GetEnvironmentVariable("JARVIS_E2E_GROQ") != "1") return;
        var pt = await Translator.TranslateAsync("Can we move the meeting to Thursday at 3 pm?");
        Assert.True(pt?.Text.Contains("quinta", StringComparison.OrdinalIgnoreCase) == true, "traduziu: " + pt);
        var en = await Translator.TranslateAsync("Bom dia, o relatório está pronto.");
        Assert.True(en?.Text.Contains("report", StringComparison.OrdinalIgnoreCase) == true, "traduziu: " + en);
    }
}

public class DictationStyleTests
{
    [Fact]
    public void KeepsWhatWasSaidWhenTheModelAnswersInstead()
    {
        Assert.Equal("Bom dia, tudo bem?", Dictation.Accept("bom dia tudo bem", "\"Bom dia, tudo bem?\""));
        Assert.Null(Dictation.Accept("oi", new string('x', 200)));
        Assert.Null(Dictation.Accept("oi", "  "));
    }

    /// <summary>JARVIS_E2E_GROQ=1: the "clean" style punctuates without answering the question that was dictated.</summary>
    [Fact]
    public async Task CleanStyleFixesPunctuation()
    {
        if (Environment.GetEnvironmentVariable("JARVIS_E2E_GROQ") != "1") return;
        var text = await Dictation.PolishAsync("oi joão você pode me mandar o relatório até amanhã", "clean");
        Assert.Contains("?", text);
        Assert.Contains("João", text);
        Assert.Contains("relatório", text);
    }
}

public class GitHubWatchTests
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Theory]
    [InlineData("theuslpszbr15/dynamic-island-windows", "theuslpszbr15/dynamic-island-windows")]
    [InlineData("https://github.com/theuslpszbr15/QuickVoice/", "theuslpszbr15/QuickVoice")]
    [InlineData("github.com/a-b/c.d.git", "a-b/c.d")]
    [InlineData("nao e repo", null)]
    [InlineData("a/b/c", null)]
    [InlineData("../etc", null)]
    public void UnderstandsRepositoryNames(string text, string? repo) => Assert.Equal(repo, GitHubWatch.Normalize(text));

    static string Run(long id, string conclusion) =>
        $"{{\"id\":{id},\"name\":\"CI\",\"head_branch\":\"main\",\"status\":\"completed\",\"conclusion\":\"{conclusion}\",\"html_url\":\"https://github.com/o/r/actions/runs/{id}\"}}";

    [Fact]
    public void WarnsOnceWhenCiFailsAndWhenItPassesAgain()
    {
        var watch = new GitHubWatch();
        var events = new List<GitHubEvent>();
        var id = DateTime.UtcNow.Ticks; // ids never seen before
        // the first look only learns what is there
        watch.Runs("o/r", Json($"{{\"workflow_runs\":[{Run(id, "success")}]}}"), first: true, events);
        Assert.Empty(events);
        // newest first, like GitHub: a failure arrives
        watch.Runs("o/r", Json($"{{\"workflow_runs\":[{Run(id + 1, "failure")},{Run(id, "success")}]}}"), first: false, events);
        Assert.Equal("ci-failed", Assert.Single(events).Kind);
        // the same list again: nothing new
        watch.Runs("o/r", Json($"{{\"workflow_runs\":[{Run(id + 1, "failure")},{Run(id, "success")}]}}"), first: false, events);
        Assert.Single(events);
        // fixed
        watch.Runs("o/r", Json($"{{\"workflow_runs\":[{Run(id + 2, "success")},{Run(id + 1, "failure")}]}}"), first: false, events);
        Assert.Equal("ci-fixed", events[^1].Kind);
    }

    [Fact]
    public void NewPullRequestsShowWithTheirAuthor()
    {
        var watch = new GitHubWatch();
        var events = new List<GitHubEvent>();
        var number = DateTime.UtcNow.Ticks % 1_000_000_000;
        watch.Pulls("o/r" + number, Json($"[{{\"number\":{number},\"title\":\"Tela nova\",\"user\":{{\"login\":\"brendo\"}},\"html_url\":\"https://github.com/o/r/pull/1\"}}]"), first: false, events);
        Assert.Equal("PR #" + number + " de brendo", Assert.Single(events).Detail);
    }
}

public class PlanUsageTests
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    /// <summary>PLAN_E2E=1 on a PC with the GitHub CLI logged in to a Copilot account: the real usage comes back.</summary>
    [Fact]
    public async Task ReadsTheRealCopilotUsage()
    {
        if (Environment.GetEnvironmentVariable("PLAN_E2E") != "1") return;
        var (plans, notes) = await PlanUsage.ReadAllAsync();
        var copilot = plans.FirstOrDefault(p => p.Name == "Copilot");
        Assert.True(copilot != null, "notas: " + string.Join(" | ", notes));
        Assert.InRange(copilot!.UsedPercent, 0, 100);
    }

    [Fact]
    public void CopilotUsesThePremiumRequests()
    {
        var plan = PlanUsage.ParseCopilot(Json("""
            {"copilot_plan":"enterprise","quota_reset_date":"2026-12-01","quota_snapshots":{
              "chat":{"unlimited":true,"percent_remaining":100},
              "premium_interactions":{"unlimited":false,"percent_remaining":47.8,"entitlement":277000}}}
            """));
        Assert.NotNull(plan);
        Assert.Equal(52.2, plan!.UsedPercent, 1);
        Assert.Equal(new DateTime(2026, 12, 1), plan.ResetsAt!.Value.Date);
        Assert.Null(PlanUsage.ParseCopilot(Json("""{"quota_snapshots":{"premium_interactions":{"unlimited":true,"percent_remaining":100}}}""")));
    }

    [Fact]
    public void ClaudeShowsTheFullerWindow()
    {
        var plan = PlanUsage.ParseClaude(Json("""
            {"five_hour":{"utilization":23.0,"resets_at":"2026-11-23T15:00:00Z"},"seven_day":{"utilization":61.5,"resets_at":"2026-11-27T10:00:00Z"},"seven_day_opus":null}
            """));
        Assert.Equal(61.5, plan!.UsedPercent);
        Assert.Equal("semana", plan.Window);
    }

    [Fact]
    public void CodexReadsItsRateLimits()
    {
        var future = DateTimeOffset.Now.AddHours(2).ToUnixTimeSeconds();
        var line = Json(
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{" +
            $"\"primary\":{{\"used_percent\":12.0,\"window_minutes\":300,\"resets_at\":{future}}}," +
            $"\"secondary\":{{\"used_percent\":40.0,\"window_minutes\":10080,\"resets_at\":{future}}}" +
            "}}}");
        var plan = PlanUsage.ParseCodex(line, DateTime.UtcNow);
        Assert.Equal(40, plan!.UsedPercent);
        Assert.Equal("semana", plan.Window);

        // a window that already renewed since the log was written is empty again
        var old = Json("""{"payload":{"rate_limits":{"primary":{"used_percent":80.0,"window_minutes":300,"resets_in_seconds":60}}}}""");
        Assert.Equal(0, PlanUsage.ParseCodex(old, DateTime.UtcNow.AddHours(-1))!.UsedPercent);
    }
}
