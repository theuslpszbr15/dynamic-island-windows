using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class DiscordVoiceTests
{
    [Fact]
    public void AvatarUsesTheCdnWhenTheUserHasOne() =>
        Assert.Equal("https://cdn.discordapp.com/avatars/80351110224678912/a1b2c3d4.png?size=128",
            DiscordVoice.AvatarUrl("80351110224678912", "a1b2c3d4", "0"));

    [Fact]
    public void DefaultAvatarFollowsTheIdForNewUsernames() =>
        // (80351110224678912 >> 22) % 6 == 5
        Assert.Equal("https://cdn.discordapp.com/embed/avatars/5.png", DiscordVoice.AvatarUrl("80351110224678912", null, "0"));

    [Fact]
    public void DefaultAvatarFollowsTheDiscriminatorForOldUsernames() =>
        Assert.Equal("https://cdn.discordapp.com/embed/avatars/2.png", DiscordVoice.AvatarUrl("80351110224678912", "", "1337"));

    [Fact]
    public void AHashThatIsNotAHashIsNotPutInTheUrl() =>
        Assert.StartsWith("https://cdn.discordapp.com/embed/avatars/", DiscordVoice.AvatarUrl("1", "../../x?y", "0"));

    [Theory]
    [InlineData("1234567890123456789", true)]
    [InlineData("12345", false)]
    [InlineData("12345678901234567a9", false)]
    public void ClientIdIsASnowflake(string id, bool valid) => Assert.Equal(valid, DiscordVoice.ValidClientId(id));
}
