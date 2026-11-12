using System.Text.Json.Nodes;
using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

// What the phone's floating island gets when someone joins or leaves the Discord call (see Island.kt on Android).
public class PhoneDiscordTests
{
    [Fact]
    public void JoinCarriesNameChannelCountAndAvatar()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 };
        var o = JsonNode.Parse(PhoneLink.DiscordLine(true, "Ana", "Geral", 3, png))!.AsObject();
        Assert.Equal("discord", (string?)o["type"]);
        Assert.Equal("join", (string?)o["event"]);
        Assert.Equal("Ana", (string?)o["name"]);
        Assert.Equal("Geral", (string?)o["channel"]);
        Assert.Equal(3, (int?)o["count"]);
        Assert.Equal(Convert.ToBase64String(png), (string?)o["avatar"]);
    }

    [Fact]
    public void LeaveWithoutAvatarAndLimitsHold()
    {
        var o = JsonNode.Parse(PhoneLink.DiscordLine(false, new string('x', 300), "", 5000, new byte[PhoneLink.MaxDiscordAvatarBytes + 1]))!.AsObject();
        Assert.Equal("leave", (string?)o["event"]);
        Assert.Equal(64, ((string?)o["name"])!.Length);
        Assert.Equal(999, (int?)o["count"]);
        Assert.False(o.ContainsKey("avatar"));
    }
}
