using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class NotificationTests
{
    [Theory]
    [InlineData("Claude", null, true)]
    [InlineData(" claude ", null, true)]
    [InlineData("Notificação", "com.squirrel.AnthropicClaude.claude", true)]
    [InlineData("Notificação", "Claude_pzs8sxrjxfjjc!Claude", true)]
    [InlineData("Claude Code Fan Club", null, false)]
    [InlineData("Slack", "com.squirrel.slack.slack", false)]
    public void OnlyTheClaudeAppIsClaude(string app, string? appId, bool expected) =>
        Assert.Equal(expected, new IslandNotification(app, "t", "b", null, appId).IsClaude);
}
