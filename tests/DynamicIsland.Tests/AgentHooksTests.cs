using System.Text.Json.Nodes;
using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class AgentHooksTests
{
    const string Exe = @"C:\Users\Ana\AppData\Local\Programs\DynamicIsland\DynamicIsland.exe";

    static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void ClaudeKeepsTheOwnHooksAndNeverDoublesUp()
    {
        var mine = Parse("""{"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"say done"}]}]}}""");
        var once = AgentHooks.Change("claude", mine, true, Exe);
        var twice = AgentHooks.Change("claude", once, true, Exe);
        Assert.Equal("opus", twice["model"]!.GetValue<string>());
        var stop = twice["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count);
        Assert.Contains("say done", stop[0]!.ToJsonString());
        Assert.Equal("\"C:/Users/Ana/AppData/Local/Programs/DynamicIsland/DynamicIsland.exe\" hook claude", stop[1]!["hooks"]![0]!["command"]!.GetValue<string>());
        // Git Bash runs Claude Code's hooks: forward slashes
        Assert.Contains("C:/Users/Ana", twice["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(ApprovalTiming.HookTimeoutSeconds, twice["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>());

        var removed = AgentHooks.Change("claude", twice, false, Exe);
        Assert.Single(removed["hooks"]!.AsObject());
        Assert.Contains("say done", removed.ToJsonString());
        Assert.DoesNotContain("DynamicIsland", removed.ToJsonString());
    }

    [Fact]
    public void TheIslandsOldPastedClaudeHooksAreReplaced()
    {
        var pasted = Parse("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"\"C:/old/DynamicIsland.exe\" hook claude"}]}]}}""");
        var installed = AgentHooks.Change("claude", pasted, true, Exe);
        Assert.Single(installed["hooks"]!["Stop"]!.AsArray());
        Assert.DoesNotContain("C:/old", installed.ToJsonString());
    }

    [Fact]
    public void GeminiRunsThroughPowerShellWithItsOwnEventNames()
    {
        var root = AgentHooks.Change("gemini", new JsonObject { ["theme"] = "Dracula" }, true, Exe);
        var before = root["hooks"]!["BeforeTool"]![0]!;
        Assert.Equal("*", before["matcher"]!.GetValue<string>());
        Assert.Equal($"& '{Exe}' hook gemini BeforeTool", before["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(5000, before["hooks"]![0]!["timeout"]!.GetValue<int>());
        Assert.Null(root["hooks"]!["AfterModel"]);
        Assert.Equal("Dracula", AgentHooks.Change("gemini", root, false, Exe)["theme"]!.GetValue<string>());
        Assert.Null(AgentHooks.Change("gemini", root, false, Exe)["hooks"]);
    }

    [Fact]
    public void AntigravityGetsANamedGroup()
    {
        var other = Parse("""{"mine":{"Stop":[{"type":"command","command":"beep"}]}}""");
        var root = AgentHooks.Change("antigravity", other, true, Exe);
        var group = root["dynamic-island"]!;
        Assert.Equal("*", group["PreToolUse"]![0]!["matcher"]!.GetValue<string>());
        Assert.Equal($"\"{Exe}\" hook antigravity PreToolUse", group["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal($"\"{Exe}\" hook antigravity PreInvocation", group["PreInvocation"]![0]!["command"]!.GetValue<string>());
        var removed = AgentHooks.Change("antigravity", root, false, Exe);
        Assert.Null(removed["dynamic-island"]);
        Assert.NotNull(removed["mine"]);
    }

    [Fact]
    public void CodexHasNoMatcherAndWaitsForApprovals()
    {
        var root = AgentHooks.Change("codex", new JsonObject(), true, Exe);
        var permission = root["hooks"]!["PermissionRequest"]![0]!;
        Assert.Null(permission["matcher"]);
        Assert.Equal($"\"{Exe}\" hook codex", permission["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(ApprovalTiming.HookTimeoutSeconds, permission["hooks"]![0]!["timeout"]!.GetValue<int>());
    }

    [Fact]
    public void TheOpenCodePluginCarriesTheExeSafely()
    {
        var plugin = AgentHooks.OpenCodePlugin(@"C:\it's here\DynamicIsland.exe");
        Assert.Contains("""const EXE = "C:\\it\u0027s here\\DynamicIsland.exe";""", plugin);
        Assert.Contains("'permission.ask'", plugin);
        Assert.Contains("['hook', 'opencode']", plugin);
    }

    [Fact]
    public void GeminiEventsReadAsClaudeCodes()
    {
        var payload = AgentCli.ToClaudePayload("""{"hook_event_name":"BeforeTool","session_id":"g1","cwd":"C:\\p","tool_name":"run_shell_command","tool_input":{"command":"npm test"}}""",
            "gemini", "BeforeTool", false)!;
        var message = AgentCli.FromHookPayload(payload, "gemini")!;
        Assert.Equal("Gemini CLI", message.Agent);
        Assert.Equal("working", message.State);
        Assert.Equal("$ npm test", message.Detail);
        Assert.Equal("run", message.Step);
        Assert.Null(message.RequestId);

        var asked = AgentCli.FromHookPayload(AgentCli.ToClaudePayload(
            """{"hook_event_name":"Notification","notification_type":"ToolPermission","message":"Permitir rm?"}""", "gemini", "Notification", false)!, "gemini")!;
        Assert.Equal("waiting", asked.State);
        Assert.Equal("{}", AgentCli.Reply("gemini", "PreToolUse", null));
    }

    [Fact]
    public void AntigravityCommandsWaitOnlyWhenTurnedOn()
    {
        const string raw = """{"conversationId":"c9","toolCall":{"name":"run_command","args":{"CommandLine":"npm i"}}}""";
        var quiet = AgentCli.FromHookPayload(AgentCli.ToClaudePayload(raw, "antigravity", "PreToolUse", false)!, "antigravity")!;
        Assert.Null(quiet.RequestId);
        Assert.Equal("$ npm i", quiet.Detail);
        Assert.Equal("c9", quiet.Session);
        Assert.Equal("""{"decision":"ask"}""", AgentCli.Reply("antigravity", "PreToolUse", null));

        var asking = AgentCli.FromHookPayload(AgentCli.ToClaudePayload(raw, "antigravity", "PreToolUse", true)!, "antigravity")!;
        Assert.NotNull(asking.RequestId);
        Assert.Equal("$ npm i", asking.Command);
        Assert.Equal("""{"decision":"allow"}""", AgentCli.Reply("antigravity", "PermissionRequest", "allow"));
        Assert.Contains("\"deny\"", AgentCli.Reply("antigravity", "PermissionRequest", "deny"));
        Assert.Equal("{}", AgentCli.Reply("antigravity", "Stop", null));
        Assert.Null(AgentCli.ToClaudePayload("{}", "antigravity", "PostInvocation", true));
    }

    [Fact]
    public void CodexAndOpenCodeGetTheSameApprovalAsClaude()
    {
        var codex = AgentCli.FromHookPayload("""{"hook_event_name":"PermissionRequest","tool_name":"shell","tool_input":{"command":["bash","-lc","npm test"]}}""", "codex")!;
        Assert.NotNull(codex.RequestId);
        Assert.Equal("$ npm test", codex.Command);
        Assert.Equal("Codex", codex.Agent);
        Assert.Contains("\"behavior\":\"allow\"", AgentCli.Reply("codex", "PermissionRequest", "allow"));
        Assert.Null(AgentCli.Reply("codex", "PermissionRequest", null));

        var open = AgentCli.FromHookPayload("""{"hook_event_name":"PermissionRequest","tool_name":"bash","tool_input":{"command":"rm -rf dist"}}""", "opencode")!;
        Assert.NotNull(open.RequestId);
        Assert.Equal("OpenCode", open.Agent);
        Assert.Null(AgentCli.FromHookPayload("""{"hook_event_name":"PermissionRequest","tool_name":"bash"}""", "copilot")!.RequestId);
    }
}
