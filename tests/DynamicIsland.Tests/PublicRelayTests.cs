using System.Text.Json;
using System.Web;
using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

/// <summary>The public relay: no key in the QR, the Ed25519 answer to the relay's challenge, the 10 MB file limit
/// through the relay and the relay's close codes in words.</summary>
public class PublicRelayTests
{
    [Fact]
    public void QrForThePublicRelayHasTheRelayButNoKey()
    {
        var query = HttpUtility.ParseQueryString(new Uri(
            PhoneLink.BuildPairingLink("10.0.0.5", 47900, "ab12", "t", "PC", PhoneLink.PublicRelay)).Query);
        Assert.Equal(PhoneLink.PublicRelay, query["relay"]);
        Assert.Null(query["rk"]);
        Assert.Equal(PhoneLink.PublicRelay, PhoneLink.NormalizeRelay(PhoneLink.PublicRelay));
    }

    // RFC 8032, section 7.1, tests 1 and 2 (also checked with Node's crypto).
    [Theory]
    [InlineData("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
        "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    public void Ed25519MatchesTheRfcVectors(string seed, string publicKey, string message, string signature)
    {
        var s = Convert.FromHexString(seed);
        Assert.Equal(publicKey, Convert.ToHexString(Ed25519.PublicKey(s)).ToLowerInvariant());
        Assert.Equal(signature, Convert.ToHexString(Ed25519.Sign(s, Convert.FromHexString(message))).ToLowerInvariant());
    }

    [Fact]
    public void AnswersTheRelayChallengeWithTheRoomAndNonceSigned()
    {
        var seed = Enumerable.Repeat((byte)1, 32).ToArray();
        var room = new string('a', 64);
        var answer = PhoneLink.AnswerChallenge($$"""{"type":"challenge","nonce":"{{new string('b', 64)}}"}""", room, seed);
        Assert.NotNull(answer);
        using var doc = JsonDocument.Parse(answer);
        Assert.Equal("auth", doc.RootElement.GetProperty("type").GetString());
        // the same key and signature Node's crypto (and so the relay's WebCrypto verify) computes for this input
        Assert.Equal("8a88e3dd7409f195fd52db2d3cba5d72ca6709bf1d94121bf3748801b40f6f5c", doc.RootElement.GetProperty("key").GetString());
        Assert.Equal("8a3b3262aaa6589e990928af8a349204b5aee19cd5d9ca0edf305a88c79f041a49c287881d92c2dc465d866c25f658f5ebc9706b1102375e05808ba11e016506",
            doc.RootElement.GetProperty("sig").GetString());
    }

    [Fact]
    public void AWrongSignatureIsAWrongSignature()
    {
        // a different room, nonce or key changes the signature, so the relay (and its 4401) can tell
        var seed = Enumerable.Repeat((byte)1, 32).ToArray();
        string Sig(string room, string nonce, byte[] key) =>
            JsonDocument.Parse(PhoneLink.AnswerChallenge($$"""{"type":"challenge","nonce":"{{nonce}}"}""", room, key)!).RootElement.GetProperty("sig").GetString()!;
        var expected = Sig(new string('a', 64), new string('b', 64), seed);
        Assert.NotEqual(expected, Sig(new string('c', 64), new string('b', 64), seed));
        Assert.NotEqual(expected, Sig(new string('a', 64), new string('d', 64), seed));
        Assert.NotEqual(expected, Sig(new string('a', 64), new string('b', 64), Enumerable.Repeat((byte)2, 32).ToArray()));
    }

    [Theory]
    [InlineData("""{"type":"incoming","conn":"x"}""")]
    [InlineData("""{"type":"challenge","nonce":"short"}""")]
    [InlineData("""{"type":"challenge","nonce":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"}""")]
    [InlineData("not json")]
    public void IgnoresAnythingButAChallenge(string message) =>
        Assert.Null(PhoneLink.AnswerChallenge(message, new string('a', 64), new byte[32]));

    [Fact]
    public void FilesThroughTheRelayStopAt10MbPerBatch()
    {
        Assert.True(PhoneLink.FitsRoute(PhoneLink.RelayMaxBatchBytes, viaRelay: true));
        Assert.False(PhoneLink.FitsRoute(PhoneLink.RelayMaxBatchBytes + 1, viaRelay: true));
        // on the local network the usual limits apply
        Assert.True(PhoneLink.FitsRoute(PhoneLink.MaxFileBytes, viaRelay: false));
        Assert.Contains("10 MB", PhoneLink.RelayTooBigMessage);
    }

    [Fact]
    public void RelayCloseCodesInWords()
    {
        Assert.Contains("Limite do repasse", PhoneLink.RelayCloseMessage(4429));
        Assert.Contains("seu próprio repasse", PhoneLink.RelayCloseMessage(4429));
        Assert.Contains("Parear de novo", PhoneLink.RelayCloseMessage(4401));
        Assert.Null(PhoneLink.RelayCloseMessage(1000));
        Assert.Null(PhoneLink.RelayCloseMessage(null));
    }

    [Fact]
    public void NewInstallsUseThePublicRelay()
    {
        var fresh = new IslandSettings();
        fresh.MigrateRelay(existing: false);
        Assert.Equal(PhoneRelayMode.Public, fresh.PhoneRelayMode);
        Assert.Equal(PhoneLink.PublicRelay, fresh.EffectivePhoneRelay);
    }

    [Fact]
    public void ExistingSettingsKeepWhatTheyHad()
    {
        var own = new IslandSettings { PhoneRelay = "wss://meu.example.com", PhoneLink = true };
        own.MigrateRelay(existing: true);
        Assert.Equal(PhoneRelayMode.Own, own.PhoneRelayMode);
        Assert.Equal("wss://meu.example.com", own.EffectivePhoneRelay);

        var lanOnly = new IslandSettings { PhoneLink = true };
        lanOnly.MigrateRelay(existing: true);
        Assert.Equal(PhoneRelayMode.Off, lanOnly.PhoneRelayMode);
        Assert.Equal("", lanOnly.EffectivePhoneRelay);

        var neverPaired = new IslandSettings();
        neverPaired.MigrateRelay(existing: true);
        Assert.Equal(PhoneRelayMode.Public, neverPaired.PhoneRelayMode);
    }
}
