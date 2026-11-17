using System.Text.Json;
using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class PhoneCommandTests
{
    static DynamicIsland.Services.PhoneLink.PhoneCommand Parse(string json) =>
        PhoneLink.ParseCommand(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void PingAsksForAPong() =>
        Assert.Equal(PhoneLink.PhoneAction.Ping, Parse("""{"type":"ping"}""").Action);

    [Theory]
    [InlineData("allow")]
    [InlineData("deny")]
    public void DecisionCarriesTheFingerprint(string decision)
    {
        var command = Parse($$"""{"type":"decision","fingerprint":"fp123","decision":"{{decision}}"}""");
        Assert.Equal(PhoneLink.PhoneAction.Decision, command.Action);
        Assert.Equal("fp123", command.Fingerprint);
        Assert.Equal(decision, command.Decision);
    }

    [Theory]
    [InlineData("""{"type":"decision","fingerprint":"fp123","decision":"maybe"}""")] // unknown decision
    [InlineData("""{"type":"decision","decision":"allow"}""")]                      // no fingerprint
    [InlineData("""{"type":"open"}""")]                                             // no id
    [InlineData("""{"type":"open","id":""}""")]                                     // empty id
    [InlineData("""{"type":"open","id":"way-too-long-0123456789012345678901234567890123456789012345678901234567890123456789"}""")] // > 64 chars
    [InlineData("""{"type":"fileDecline"}""")]                                      // no id
    [InlineData("""{"type":"sendCancel"}""")]                                       // no id
    [InlineData("""{"type":"anything-else"}""")]                                    // unknown type
    public void IncompleteLinesAskForNothing(string json) =>
        Assert.Equal(PhoneLink.PhoneAction.None, Parse(json).Action);

    [Fact]
    public void OpenIdInsideTheLimitIsAccepted()
    {
        var command = Parse("""{"type":"open","id":"abc"}""");
        Assert.Equal(PhoneLink.PhoneAction.Open, command.Action);
        Assert.Equal("abc", command.Id);
    }

    [Fact]
    public void FileDoneKeepsWhetherThePhoneGotTheFile()
    {
        var ok = Parse("""{"type":"fileDone","id":"42","ok":true}""");
        Assert.Equal(PhoneLink.PhoneAction.FileDone, ok.Action);
        Assert.Equal("42", ok.Id);
        Assert.True(ok.Ok);
        var failed = Parse("""{"type":"fileDone","id":"42","ok":false}""");
        Assert.False(failed.Ok);
        var silent = Parse("""{"type":"fileDone","id":"42"}""");
        Assert.False(silent.Ok);
    }

    [Fact]
    public void DeclineAndCancelCarryTheId()
    {
        Assert.Equal("77", Parse("""{"type":"fileDecline","id":"77"}""").Id);
        Assert.Equal(PhoneLink.PhoneAction.SendCancel, Parse("""{"type":"sendCancel","id":"88"}""").Action);
    }

    [Fact]
    public void SendOfferKeepsTheWholePayload()
    {
        var command = Parse("""{"type":"sendOffer","id":"deadbeef","text":"oi"}""");
        Assert.Equal(PhoneLink.PhoneAction.SendOffer, command.Action);
        Assert.Equal("oi", command.Offer.GetProperty("text").GetString());
    }

    [Fact]
    public void ByeEndsTheSession() =>
        Assert.Equal(PhoneLink.PhoneAction.Bye, Parse("""{"type":"bye"}""").Action);

    [Fact]
    public void ClipCarriesTheTextWithinTheLimit()
    {
        var command = Parse("""{"type":"clip","text":"copiado"}""");
        Assert.Equal(PhoneLink.PhoneAction.Clip, command.Action);
        Assert.Equal("copiado", command.Text);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse("""{"type":"clip","text":""}""").Action);
        var tooLong = new string('a', PhoneLink.MaxClipChars + 1);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse($$"""{"type":"clip","text":"{{tooLong}}"}""").Action);
    }

    [Fact]
    public void RemoteOnlyTakesKnownActionsAndClampsMinutes()
    {
        var command = Parse("""{"type":"remote","action":"shutdown","minutes":9999}""");
        Assert.Equal(PhoneLink.PhoneAction.Remote, command.Action);
        Assert.Equal("shutdown", command.Text);
        Assert.Equal(720, command.Minutes);
        Assert.Equal(0, Parse("""{"type":"remote","action":"lock"}""").Minutes);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse("""{"type":"remote","action":"format-c"}""").Action);
    }

    [Fact]
    public void CallCutsNameAndNumber()
    {
        var command = Parse($$"""{"type":"call","state":"ringing","name":"{{new string('n', 100)}}","number":"{{new string('9', 50)}}"}""");
        Assert.Equal(PhoneLink.PhoneAction.Call, command.Action);
        Assert.Equal("ringing", command.State);
        Assert.Equal(64, command.Name!.Length);
        Assert.Equal(32, command.Number!.Length);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse("""{"type":"call","state":"weird"}""").Action);
    }
}

