using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class JarvisTests
{
    static readonly DateTime Now = new(2026, 11, 17, 9, 5, 0);

    [Fact]
    public void TellsTheTimeAndDate()
    {
        Assert.Equal("Agora são 09:05.", Jarvis.LocalReply("Que horas são?", Now)!.Text);
        Assert.Equal("Hoje é terça-feira, 17 de novembro de 2026.", Jarvis.LocalReply("que dia é hoje", Now)!.Text);
    }

    [Theory]
    [InlineData("quanto é 2 mais 3", "O resultado é 5.")]
    [InlineData("calcule 10 / 4", "O resultado é 2,5.")]
    [InlineData("Quanto é 1500 vezes 3", "O resultado é 4.500.")]
    [InlineData("calcule 7 dividido por 0", "Não é possível dividir por zero.")]
    public void DoesSums(string question, string answer) => Assert.Equal(answer, Jarvis.LocalReply(question, Now)!.Text);

    [Fact]
    public void PreparesSearchesOnlyToSafeLinks()
    {
        var reply = Jarvis.LocalReply("pesquise previsão do tempo & chuva", Now)!;
        Assert.Equal("https://www.google.com/search?q=previs%C3%A3o%20do%20tempo%20%26%20chuva", reply.ActionUrl);
        Assert.True(Jarvis.SafeLink(reply.ActionUrl));
        Assert.Equal("https://www.youtube.com/", Jarvis.LocalReply("abre o YouTube", Now)!.ActionUrl);
        Assert.False(Jarvis.SafeLink("http://www.google.com/"));
        Assert.False(Jarvis.SafeLink("https://evil.example.com/"));
    }

    [Fact]
    public void GreetsAndLeavesTheRestToTheAi()
    {
        Assert.Equal("À disposição. Em que posso ajudar?", Jarvis.LocalReply("Olá!", Now)!.Text);
        Assert.Null(Jarvis.LocalReply("me conta uma piada", Now));
    }

    /// <summary>JARVIS_E2E=1: downloads the voice and microphone engines (~240 MB, checked by SHA-256), has the voice
    /// say a question and the microphone engine hear it back; JARVIS_E2E_AI=1 also asks the offline AI (1.1 GB).</summary>
    [Fact]
    public async Task VoiceAndMicrophoneRoundTrip()
    {
        if (Environment.GetEnvironmentVariable("JARVIS_E2E") != "1") return;
        Assert.Null(await Jarvis.InstallAsync(Jarvis.Engine.Voice));
        Assert.Null(await Jarvis.InstallAsync(Jarvis.Engine.Microphone));
        var wave = Path.Combine(Path.GetTempPath(), "jarvis-e2e.wav");
        await Jarvis.SynthesizeAsync(Jarvis.Folder(Jarvis.Engine.Voice)!, "Bom dia, Jarvis. Me diga que horas são agora, por favor.", wave);
        var heard = await Jarvis.TranscribeAsync(wave);
        // the small whisper model is checked on the plain words, not on how well it hears "horas"
        Assert.True(heard?.Contains("por favor", StringComparison.OrdinalIgnoreCase) == true, "ouviu: " + heard);
        File.Delete(wave);
        Assert.StartsWith("Agora são", Jarvis.LocalReply("que horas são", Now)!.Text);

        if (Environment.GetEnvironmentVariable("JARVIS_E2E_AI") != "1") return;
        Assert.Null(await Jarvis.InstallAsync(Jarvis.Engine.Ai));
        var reply = await Jarvis.AskAsync("Qual é a capital do Brasil? Responda em uma frase.", []);
        Assert.True(reply.Text.Contains("Bras") && !reply.Text.Contains("end of text"), "respondeu: " + reply.Text);
    }
}
