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

    [Fact]
    public void AQuestionForJarvisNeedsAnIdAndText()
    {
        var command = Parse("""{"type":"jarvis","id":"abc","text":"que horas são?"}""");
        Assert.Equal(PhoneLink.PhoneAction.Jarvis, command.Action);
        Assert.Equal("abc", command.Id);
        Assert.Equal("que horas são?", command.Text);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse("""{"type":"jarvis","text":"oi"}""").Action);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse("""{"type":"jarvis","id":"abc","text":""}""").Action);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse($$"""{"type":"jarvis","id":"abc","text":"{{new string('a', 2001)}}"}""").Action);
    }

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

    [Fact]
    public void TouchpadMovesAreClamped()
    {
        var move = Parse("""{"type":"mouse","dx":12,"dy":-99999}""");
        Assert.Equal(PhoneLink.PhoneAction.Mouse, move.Action);
        Assert.Equal((12, -2000), (move.Dx, move.Dy));
        Assert.Equal(2400, Parse("""{"type":"scroll","dy":100000}""").Dy);
    }

    [Fact]
    public void ButtonsAndKeysAreOnlyTheKnownOnes()
    {
        var drag = Parse("""{"type":"button","button":"left","act":"down"}""");
        Assert.Equal(PhoneLink.PhoneAction.Button, drag.Action);
        Assert.Equal(("left", "down"), (drag.Text, drag.State));
        Assert.Equal(PhoneLink.PhoneAction.None, Parse("""{"type":"button","button":"x1","act":"click"}""").Action);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse("""{"type":"button","button":"left","act":"hold"}""").Action);
        Assert.Equal(PhoneLink.PhoneAction.Key, Parse("""{"type":"key","key":"pagedown"}""").Action);
        Assert.Equal(PhoneLink.PhoneAction.Key, Parse("""{"type":"key","key":"shift+f5"}""").Action);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse("""{"type":"key","key":"alt+f4"}""").Action);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse("""{"type":"key","key":"ctrl+alt+delete"}""").Action);
    }

    [Fact]
    public void PadFramesAreClampedToTheControllerRanges()
    {
        var pad = Parse("""{"type":"pad","kind":"ps4","b":99999999,"lx":-40000,"ly":32767,"rx":0,"ry":12,"lt":300,"rt":-4}""");
        Assert.Equal(PhoneLink.PhoneAction.Pad, pad.Action);
        Assert.True(pad.Pad.Ps4);
        Assert.Equal(0, pad.Pad.Buttons & ~0x7FFF);
        Assert.Equal((short)-32767, pad.Pad.Lx);
        Assert.Equal(((byte)255, (byte)0), (pad.Pad.Lt, pad.Pad.Rt));
        Assert.Equal(PhoneLink.PhoneAction.None, Parse("""{"type":"pad","kind":"switch"}""").Action);
    }

    [Fact]
    public void DualShockSticksAndDpadAreMapped()
    {
        Assert.Equal(128, VirtualPad.ToByte(0));
        Assert.Equal(0, VirtualPad.ToByte(short.MinValue));
        Assert.Equal(255, VirtualPad.ToByte(short.MaxValue));
        Assert.Equal(Nefarius.ViGEm.Client.Targets.DualShock4.DualShock4DPadDirection.Northeast, VirtualPad.DPad(true, false, false, true));
        Assert.Equal(Nefarius.ViGEm.Client.Targets.DualShock4.DualShock4DPadDirection.None, VirtualPad.DPad(false, false, false, false));
    }

    [Fact]
    public void WithoutTheDriverThePadSaysWhy()
    {
        if (VirtualPad.DriverInstalled) return;
        Assert.False(VirtualPad.Apply(new VirtualPad.State(false, 1, 0, 0, 0, 0, 0, 0)));
        Assert.Contains("ViGEmBus", VirtualPad.Problem);
    }

    [Fact]
    public void TypedTextIsShort()
    {
        Assert.Equal("olá", Parse("""{"type":"text","text":"olá"}""").Text);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse($$"""{"type":"text","text":"{{new string('a', 201)}}"}""").Action);
        Assert.Equal(PhoneLink.PhoneAction.None, Parse("""{"type":"text","text":""}""").Action);
    }
}


public class PhoneFilterTests
{
    static PhoneLink.PhoneCommand Parse(string json) =>
        PhoneLink.ParseCommand(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void FilterCarriesKnownKindsOnly()
    {
        var c = Parse("""{"type":"filter","off":["Discord","Nope","Discord","Media"],"at":1700000000000}""");
        Assert.Equal(PhoneLink.PhoneAction.Filter, c.Action);
        Assert.Equal(new[] { "Discord", "Media" }, c.Kinds);
        Assert.Equal(1700000000000, c.At);
    }

    [Theory]
    [InlineData("""{"type":"filter","off":["Discord"]}""")]               // no time
    [InlineData("""{"type":"filter","off":["Discord"],"at":0}""")]         // never changed
    [InlineData("""{"type":"filter","off":"Discord","at":5}""")]           // not a list
    [InlineData("""{"type":"filter","off":[1],"at":5}""")]                 // not strings
    public void BadFilterAsksForNothing(string json) =>
        Assert.Equal(PhoneLink.PhoneAction.None, Parse(json).Action);

    [Fact]
    public void TheNewestChangeWinsAndTheSameInstantStops()
    {
        Assert.True(PhoneLink.FilterIsNewer(10, 11));
        Assert.False(PhoneLink.FilterIsNewer(10, 10));
        Assert.False(PhoneLink.FilterIsNewer(10, 9));
    }

    [Fact]
    public void FilterLineRoundTrips()
    {
        var c = Parse(PhoneLink.FilterLine(["Call", "Offer"], 42));
        Assert.Equal(new[] { "Call", "Offer" }, c.Kinds);
        Assert.Equal(42, c.At);
    }
}
