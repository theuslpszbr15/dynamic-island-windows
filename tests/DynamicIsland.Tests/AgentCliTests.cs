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
}
