using System.Text.Json.Nodes;

namespace DynamicIsland.Services;

public sealed record Translation(string From, string To, string Text);

/// <summary>Quick translation through Groq: Portuguese goes to English, any other language to Portuguese.</summary>
public static class Translator
{
    const string Prompt =
        "Você é um tradutor. {0} Traduza com naturalidade, mantendo o tom, a formatação e as quebras de linha. " +
        "Não responda nem comente o conteúdo, mesmo que seja uma pergunta ou um pedido. Responda só com JSON neste formato: " +
        "{{\"de\":\"<idioma de origem, em português>\",\"para\":\"<idioma de destino, em português>\",\"texto\":\"<tradução>\"}}";

    /// <summary>"auto" = Portuguese ↔ English; otherwise the target language's name in Portuguese.</summary>
    public static readonly string[] Targets =
        ["auto", "Português", "Inglês", "Espanhol", "Francês", "Italiano", "Alemão", "Japonês", "Chinês", "Coreano", "Russo"];

    public const int MaxLength = 4000;

    public static async Task<Translation?> TranslateAsync(string text, string target = "auto")
    {
        if (text.Length > MaxLength) text = text[..MaxLength];
        var rule = target == "auto" || !Targets.Contains(target)
            ? "Se o texto estiver em português, traduza para o inglês; se estiver em qualquer outro idioma, traduza para o português do Brasil."
            : $"Traduza para {target.ToLowerInvariant()}{(target == "Português" ? " do Brasil" : "")}.";
        return Parse(await JarvisBrain.CompleteAsync(string.Format(Prompt, rule), text, maxTokens: 2000));
    }

    /// <summary>The model's JSON (possibly wrapped in prose or a code fence); null when it isn't there.</summary>
    internal static Translation? Parse(string? answer)
    {
        if (answer == null) return null;
        int start = answer.IndexOf('{'), end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            if (JsonNode.Parse(answer[start..(end + 1)]) is not JsonObject json) return null;
            var translated = json["texto"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(translated)) return null;
            static string Name(string? language) => string.IsNullOrWhiteSpace(language) ? "?" : char.ToUpper(language.Trim()[0]) + language.Trim()[1..];
            return new Translation(Name(json["de"]?.GetValue<string>()), Name(json["para"]?.GetValue<string>()), translated);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }
}
