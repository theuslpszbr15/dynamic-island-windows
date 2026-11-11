using System.Web;
using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class PhoneRelayTests
{
    [Fact]
    public void RoomIsLowercaseHexSha256OfThePrefixedToken()
    {
        // printf 'dynamicisland-room|abc' | sha256sum  (same vector in the Android tests)
        Assert.Equal("2861962cd12a5d450d9da6106e809a3ea1958c9973b02b9993e4f0edcc8861b4", PhoneLink.RoomId("abc"));
        Assert.Equal("8384880fdcad1db084c3e9c416d0d0c73b072ac1b3db6f23c26b8f03cf9ecdef", PhoneLink.RoomId(""));
        Assert.Matches("^[0-9a-f]{64}$", PhoneLink.RoomId("tok-en_123"));
        Assert.NotEqual(PhoneLink.RoomId("a"), PhoneLink.RoomId("b"));
    }

    [Theory]
    [InlineData("wss://relay.example.com", "wss://relay.example.com")]
    [InlineData("wss://Relay.Example.com/", "wss://relay.example.com")]
    [InlineData("  relay.example.workers.dev ", "wss://relay.example.workers.dev")]
    public void AcceptsAWssOrigin(string input, string expected) => Assert.Equal(expected, PhoneLink.NormalizeRelay(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ws://relay.example.com")]           // no TLS
    [InlineData("https://relay.example.com")]
    [InlineData("wss://relay.example.com:8443")]     // port
    [InlineData("wss://relay.example.com/path")]     // path
    [InlineData("wss://relay.example.com/?a=1")]     // query
    [InlineData("wss://user:pw@relay.example.com")]  // user info
    [InlineData("wss://192.168.0.10")]               // not a host name
    [InlineData("wss://relay.example.com//")]
    public void RefusesAnythingElse(string input) => Assert.Null(PhoneLink.NormalizeRelay(input));

    [Fact]
    public void QrCarriesTheRelayAsAnOptionalParameter()
    {
        var link = PhoneLink.BuildPairingLink("10.0.0.5", 47900, "ab12", "tok_en-1", "MEU PC", "wss://relay.example.com");
        var uri = new Uri(link);
        Assert.Equal("dynamicisland", uri.Scheme);
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("1", query["v"]);
        Assert.Equal("10.0.0.5", query["host"]);
        Assert.Equal("47900", query["port"]);
        Assert.Equal("ab12", query["fp"]);
        Assert.Equal("tok_en-1", query["token"]);
        Assert.Equal("MEU PC", query["name"]);
        Assert.Equal("wss://relay.example.com", query["relay"]);
        Assert.Equal("wss://relay.example.com", PhoneLink.NormalizeRelay(query["relay"]));
    }

    [Fact]
    public void QrCarriesTheRelayKeyAsRk()
    {
        var key = PhoneLink.NewRelayKey();
        Assert.True(PhoneLink.ValidRelayKey(key));
        var query = HttpUtility.ParseQueryString(new Uri(
            PhoneLink.BuildPairingLink("10.0.0.5", 47900, "ab12", "t", "PC", "wss://relay.example.com", key)).Query);
        Assert.Equal(key, query["rk"]);
        Assert.Equal("wss://relay.example.com", query["relay"]);
    }

    [Fact]
    public void NoRelayMeansNoKeyInTheQr()
    {
        var query = HttpUtility.ParseQueryString(new Uri(
            PhoneLink.BuildPairingLink("10.0.0.5", 47900, "ab12", "t", "PC", "", PhoneLink.NewRelayKey())).Query);
        Assert.Null(query["rk"]);
        Assert.Null(query["relay"]);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has space inside 1234567890")]
    [InlineData("plus+slash/equals=0123456789")]
    public void RefusesBadRelayKeys(string key) => Assert.False(PhoneLink.ValidRelayKey(key));

    [Fact]
    public void QrWithoutRelayHasNoRelayParameter()
    {
        var query = HttpUtility.ParseQueryString(new Uri(PhoneLink.BuildPairingLink("10.0.0.5", 47900, "ab12", "t", "PC", "")).Query);
        Assert.Null(query["relay"]);
        Assert.Equal("t", query["token"]);
    }
}
