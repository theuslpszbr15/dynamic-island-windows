using System.Text;
using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class VoiceTests
{
    const int Rate = 22050;

    // 16-bit mono WAV: (seconds, loud) pieces in a row
    static byte[] Wave(params (double Seconds, bool Loud)[] pieces)
    {
        var samples = new List<short>();
        foreach (var (seconds, loud) in pieces)
            for (var i = 0; i < (int)(seconds * Rate); i++) samples.Add(loud ? (short)(Math.Sin(i * 0.2) * 8000) : (short)0);
        var data = samples.SelectMany(BitConverter.GetBytes).ToArray();
        var wav = new List<byte>();
        wav.AddRange(Encoding.ASCII.GetBytes("RIFF"));
        wav.AddRange(BitConverter.GetBytes(36 + data.Length));
        wav.AddRange(Encoding.ASCII.GetBytes("WAVEfmt "));
        wav.AddRange(BitConverter.GetBytes(16));
        wav.AddRange(BitConverter.GetBytes((short)1));
        wav.AddRange(BitConverter.GetBytes((short)1));
        wav.AddRange(BitConverter.GetBytes(Rate));
        wav.AddRange(BitConverter.GetBytes(Rate * 2));
        wav.AddRange(BitConverter.GetBytes((short)2));
        wav.AddRange(BitConverter.GetBytes((short)16));
        wav.AddRange(Encoding.ASCII.GetBytes("data"));
        wav.AddRange(BitConverter.GetBytes(data.Length));
        wav.AddRange(data);
        return [.. wav];
    }

    [Fact]
    public void ACommaPauseGetsLonger()
    {
        var wav = Wave((0.4, true), (0.11, false), (0.4, true));
        var stretched = Jarvis.StretchPauses(wav);
        Assert.Equal(0.91 + 0.17, Jarvis.Duration(stretched).TotalSeconds, 2);
    }

    [Fact]
    public void SentenceEndsAndTinyGapsStayAsTheyAre()
    {
        var wav = Wave((0.4, true), (0.4, false), (0.4, true), (0.03, false), (0.4, true), (0.3, false));
        Assert.Same(wav, Jarvis.StretchPauses(wav));
        Assert.Equal(1.93, Jarvis.Duration(wav).TotalSeconds, 2);
    }
}
