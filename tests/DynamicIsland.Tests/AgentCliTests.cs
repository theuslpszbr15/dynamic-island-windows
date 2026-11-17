using System.Text.Json;
using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class AgentCliTests
{
    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ShellCommandsShowTheirFirstLineOnly()
    {
        var input = Json("""{"command":"npm test\nrm -rf build"}""");
        Assert.Equal("$ npm test", AgentCli.DescribeTool("Bash", input));
        // ...but the full command (what Allow would run) keeps every line
        Assert.Equal("$ npm test\nrm -rf build", AgentCli.FullCommand("Bash", input));
    }

    [Fact]
    public void EditsAreNamedByFile()
    {
        var input = Json("""{"file_path":"C:\\dev\\app\\Program.cs","old_string":"a","new_string":"b"}""");
        Assert.Equal("Editando Program.cs", AgentCli.DescribeTool("Edit", input));
        var (step, file, diff) = AgentCli.Inspect("Edit", input);
        Assert.Equal("edit", step);
        Assert.Equal(@"C:\dev\app\Program.cs", file);
        Assert.Equal("-a\n+b", diff);
    }

    [Fact]
    public void McpToolsShowTheirShortName() =>
        Assert.Equal("search_issues", AgentCli.DescribeTool("mcp__github__search_issues", default));

    [Fact]
    public void DiffDropsUnchangedLinesAndCapsTheRest()
    {
        Assert.Equal("-two\n+2", AgentCli.Diff("one\ntwo\nthree", "one\n2\nthree"));
        Assert.Null(AgentCli.Diff("same", "same"));
        var added = AgentCli.Diff("", string.Join('\n', Enumerable.Range(1, 9)))!;
        Assert.Equal(5, added.Split('\n').Length);
    }

    [Fact]
    public void FullCommandIsCappedForThePipe()
    {
        var input = Json($$"""{"command":"{{new string('x', 5000)}}"}""");
        Assert.Equal(4000, AgentCli.FullCommand("Bash", input).Length);
    }

    [Fact]
    public void HookPayloadBecomesAnIslandMessage()
    {
        var message = AgentCli.FromHookPayload("""{"hook_event_name":"PreToolUse","session_id":"s1","cwd":"C:\\dev","tool_name":"Bash","tool_input":{"command":"npm test"}}""", "claude");
        Assert.NotNull(message);
        Assert.Equal("Claude Code", message!.Agent);
        Assert.Equal("s1", message.Session);
        Assert.Equal("working", message.State);
        Assert.Equal("run", message.Step);
        Assert.Null(message.RequestId); // only PermissionRequest waits for a decision
    }

    [Fact]
    public void PermissionRequestFromClaudeWaitsForADecision()
    {
        var message = AgentCli.FromHookPayload("""{"hook_event_name":"PermissionRequest","session_id":"s1","tool_name":"Bash","tool_input":{"command":"git push"}}""", "claude");
        Assert.NotNull(message);
        Assert.Equal("waiting", message!.State);
        Assert.NotNull(message.RequestId);
        Assert.Equal("$ git push", message.Command);

        // The other CLIs don't read an answer back, so no request id: nothing waits.
        var copilot = AgentCli.FromHookPayload("""{"hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"git push"}}""", "copilot");
        Assert.NotNull(copilot);
        Assert.Null(copilot!.RequestId);
        Assert.Null(copilot.Command);
    }

    [Theory]
    [InlineData("""{"hook_event_name":"Notification","message":"Saia do sofá"}""", "waiting", "Saia do sofá")]
    [InlineData("""{"hook_event_name":"Stop"}""", "done", "Concluído")]
    [InlineData("""{"hook_event_name":"SessionStart"}""", "idle", "Sessão iniciada")]
    public void HookEventsMapToStates(string payload, string state, string detail)
    {
        var message = AgentCli.FromHookPayload(payload, "claude");
        Assert.NotNull(message);
        Assert.Equal(state, message!.State);
        Assert.Equal(detail, message.Detail);
    }

    [Fact]
    public void UnknownHookEventsAskForNothing() =>
        Assert.Null(AgentCli.FromHookPayload("""{"hook_event_name":"SomethingElse"}""", "claude"));

    [Fact]
    public void LongDetailsAreClipped()
    {
        var message = AgentCli.FromHookPayload("""{"hook_event_name":"Notification","message":"saiba o que vem depois de tudo isso aqui"}""", "claude");
        Assert.NotNull(message);
        Assert.True(message!.Detail!.Length <= 120);
    }
}
