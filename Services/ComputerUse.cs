using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Drawing = System.Drawing;

namespace DynamicIsland.Services;

/// <summary>One step Jarvis wants to take on the screen. X/Y are pixels of the picture it saw (-1 = not given);
/// Key holds the key for "tecla" and "cima"/"baixo" for "rolar".</summary>
public sealed record ComputerAction(string Kind, int X, int Y, string Text, string Key, string Reason)
{
    public bool IsClick => Kind is "clicar" or "clicar_texto" or "clique_duplo" or "clique_direito";
}

/// <summary>The rules of Jarvis's computer use: what it may do, what needs the person's OK, and what it never does.</summary>
public static partial class ComputerUse
{
    public const int MaxSteps = 20;
    public static readonly TimeSpan MaxTime = TimeSpan.FromMinutes(5);
    /// <summary>The screenshot is shrunk to this width before it goes to the model.</summary>
    public const int ImageWidth = 1280;

    static readonly string[] Kinds = ["clicar", "clicar_texto", "clique_duplo", "clique_direito", "digitar", "tecla", "rolar", "esperar", "concluir", "desistir"];

    const byte Ctrl = 0x11, Shift = 0x10, Alt = 0x12;

    /// <summary>The only keys Jarvis may press. Esc is left out: it is the person's stop key.</summary>
    public static readonly IReadOnlyDictionary<string, byte[]> Keys = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = [0x0D], ["tab"] = [0x09], ["shift+tab"] = [Shift, 0x09], ["espaco"] = [0x20], ["backspace"] = [0x08], ["delete"] = [0x2E],
        ["cima"] = [0x26], ["baixo"] = [0x28], ["esquerda"] = [0x25], ["direita"] = [0x27],
        ["home"] = [0x24], ["end"] = [0x23], ["pageup"] = [0x21], ["pagedown"] = [0x22],
        ["ctrl+a"] = [Ctrl, 0x41], ["ctrl+c"] = [Ctrl, 0x43], ["ctrl+v"] = [Ctrl, 0x56], ["ctrl+x"] = [Ctrl, 0x58], ["ctrl+z"] = [Ctrl, 0x5A],
        ["ctrl+f"] = [Ctrl, 0x46], ["ctrl+l"] = [Ctrl, 0x4C], ["ctrl+t"] = [Ctrl, 0x54], ["ctrl+w"] = [Ctrl, 0x57], ["ctrl+s"] = [Ctrl, 0x53],
        ["alt+tab"] = [Alt, 0x09], ["win"] = [0x5B],
    };

    static readonly HashSet<string> RiskyKeys = new(StringComparer.OrdinalIgnoreCase) { "enter", "delete", "ctrl+x", "ctrl+w", "ctrl+s" };

    // compared without accents, as whole words
    [GeneratedRegex(@"\b(comprar|compra|pagar|pagamento|pix|excluir|exclua|apagar|deletar|remover|desinstalar|enviar|envie|confirmar|confirme|" +
                    @"finalizar|transferir|transferencia|assinar|publicar|postar|formatar|buy|pay|purchase|checkout|delete|remove|send|submit|" +
                    @"confirm|uninstall|publish|post|transfer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RiskyWords();

    [GeneratedRegex(@"\b(senha|password|passwd|pin|cvv|cvc|token|codigo de verificacao|verification code|2fa)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SecretWords();

    public const string SystemPrompt =
        "Você controla o mouse e o teclado de um PC Windows para cumprir a tarefa do usuário, um passo por vez. " +
        "Você recebe a captura da tela e os textos lidos nela com o centro de cada linha (em pixels da imagem). " +
        "Responda só com JSON: {\"acao\":\"clicar|clicar_texto|clique_duplo|clique_direito|digitar|tecla|rolar|esperar|concluir|desistir\"," +
        "\"x\":0,\"y\":0,\"texto\":\"\",\"tecla\":\"\",\"motivo\":\"curto\"}. " +
        "clicar_texto clica no texto visível dado em texto: prefira ele sempre que o alvo tem texto. clicar usa x,y da imagem. " +
        "digitar escreve texto no campo que está com o cursor (clique no campo antes). " +
        "tecla aceita só: enter, tab, shift+tab, espaco, backspace, delete, cima, baixo, esquerda, direita, home, end, pageup, pagedown, " +
        "ctrl+a, ctrl+c, ctrl+v, ctrl+x, ctrl+z, ctrl+f, ctrl+l, ctrl+t, ctrl+w, ctrl+s, alt+tab, win. " +
        "rolar: tecla = cima ou baixo (x,y onde rolar, opcional). esperar: a tela está carregando. " +
        "concluir quando a tarefa estiver feita (motivo = o que foi feito, em uma frase); desistir quando não der (motivo = por quê). " +
        "Nunca digite senhas, PIN, dados de cartão ou códigos de verificação: nesse caso, desista e peça ao usuário. " +
        "Não compre, pague, envie ou apague nada que o usuário não tenha pedido exatamente.";

    /// <summary>The model's answer as an action; null when it isn't one of the allowed ones.</summary>
    public static ComputerAction? Parse(JsonObject? json)
    {
        if (json == null) return null;
        var kind = Str(json, "acao").Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind)) return null;
        int x = Int(json, "x"), y = Int(json, "y");
        var text = Str(json, "texto");
        var key = Plain(Str(json, "tecla")).Replace(" ", "");
        var reason = Str(json, "motivo").Trim();
        if (reason.Length > 200) reason = reason[..200];
        switch (kind)
        {
            case "clicar" or "clique_duplo" or "clique_direito":
                if ((x < 0 || y < 0) && text.Trim().Length == 0) return null;
                break;
            case "clicar_texto":
                if (text.Trim().Length == 0) return null;
                x = y = -1;
                break;
            case "digitar":
                if (text.Length == 0 || text.Length > 1000) return null;
                break;
            case "tecla":
                if (!Keys.ContainsKey(key)) return null;
                break;
            case "rolar":
                var way = key.Length > 0 ? key : Plain(text);
                key = way is "cima" or "up" or "acima" ? "cima" : "baixo";
                text = "";
                break;
        }
        return new ComputerAction(kind, x, y, kind is "digitar" ? text : text.Trim(), key, reason);
    }

    /// <summary>Needs the person's OK even in "only dangerous ones" mode: typing, Enter/Delete-like keys, and clicks on
    /// words like buy, pay, send, delete, confirm (on the screen near the click, or in what the model says it is doing).</summary>
    public static bool IsRisky(ComputerAction action, string? near) => action.Kind switch
    {
        "digitar" => true,
        "tecla" => RiskyKeys.Contains(action.Key),
        _ when action.IsClick => RiskyWords().IsMatch(Plain(near ?? "")) || RiskyWords().IsMatch(Plain(action.Text)) || RiskyWords().IsMatch(Plain(action.Reason)),
        _ => false,
    };

    /// <summary>Typing that looks like a password, PIN, card or code (by the field's label or what the model says).</summary>
    public static bool LooksSecret(ComputerAction action, string? near) =>
        action.Kind == "digitar" && (SecretWords().IsMatch(Plain(near ?? "")) || SecretWords().IsMatch(Plain(action.Reason)));

    /// <summary>Where <paramref name="text"/> is among the OCR lines: the center of the matching part (image pixels) and
    /// the whole line. Exact line first, then the shortest line containing it.</summary>
    public static (Drawing.PointF Point, string Line)? Locate(IEnumerable<(string Text, Drawing.RectangleF Box)> lines, string text)
    {
        var wanted = Plain(text);
        if (wanted.Length == 0) return null;
        (string Text, Drawing.RectangleF Box)? best = null;
        foreach (var line in lines)
        {
            var plain = Plain(line.Text);
            if (plain == wanted) { best = line; break; }
            if (plain.Contains(wanted) && (best == null || line.Text.Length < best.Value.Text.Length)) best = line;
        }
        if (best is not { } hit) return null;
        var hay = Plain(hit.Text);
        var at = hay.IndexOf(wanted, StringComparison.Ordinal);
        // a part of a longer line: guess its spot by where the characters fall
        var middle = hay.Length == 0 || at < 0 ? 0.5f : (at + wanted.Length / 2f) / hay.Length;
        return (new Drawing.PointF(hit.Box.Left + hit.Box.Width * middle, hit.Box.Top + hit.Box.Height / 2), hit.Text);
    }

    /// <summary>The OCR line under or nearest to a point (within 40 px), to judge what a click by coordinates hits.</summary>
    public static string? Near(IEnumerable<(string Text, Drawing.RectangleF Box)> lines, Drawing.PointF point)
    {
        string? best = null;
        var bestDistance = 40.0;
        foreach (var (text, box) in lines)
        {
            var dx = Math.Max(0, Math.Max(box.Left - point.X, point.X - box.Right));
            var dy = Math.Max(0, Math.Max(box.Top - point.Y, point.Y - box.Bottom));
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < bestDistance) { bestDistance = distance; best = text; }
        }
        return best;
    }

    /// <summary>The action in a few words, for the approval card and the log.</summary>
    public static string Describe(ComputerAction action, string? near)
    {
        var target = action.Text.Length > 0 && action.IsClick ? action.Text : near;
        var on = string.IsNullOrWhiteSpace(target) ? "" : $" em “{Cut(target, 60)}”";
        return action.Kind switch
        {
            "clicar" or "clicar_texto" => "Clicar" + on,
            "clique_duplo" => "Clicar duas vezes" + on,
            "clique_direito" => "Clicar com o botão direito" + on,
            "digitar" => $"Digitar “{Cut(action.Text, 80)}”",
            "tecla" => "Apertar " + action.Key,
            "rolar" => "Rolar para " + action.Key,
            "esperar" => "Esperar a tela carregar",
            _ => action.Kind,
        };
    }

    /// <summary>What the model reads besides the picture: the task, the steps so far and the text on screen.</summary>
    public static string StepText(string task, int step, IReadOnlyList<string> done, IEnumerable<(string Text, Drawing.RectangleF Box)> lines,
        double scale, int width, int height)
    {
        var text = new StringBuilder();
        text.Append("Tarefa: ").AppendLine(task);
        text.Append(CultureInfo.InvariantCulture, $"Passo {step} de {MaxSteps}. Imagem: {width}x{height} pixels.").AppendLine();
        if (done.Count > 0)
        {
            text.AppendLine("Feito até agora:");
            foreach (var item in done.TakeLast(8)) text.Append("- ").AppendLine(item);
        }
        text.AppendLine("Textos na tela (texto @ x,y):");
        var budget = 4000;
        foreach (var (line, box) in lines)
        {
            var entry = string.Create(CultureInfo.InvariantCulture,
                $"{Cut(line, 80)} @ {(int)((box.Left + box.Width / 2) * scale)},{(int)((box.Top + box.Height / 2) * scale)}");
            if ((budget -= entry.Length + 1) < 0) break;
            text.AppendLine(entry);
        }
        return text.ToString();
    }

    /// <summary>The screenshot as JPEG, shrunk by <paramref name="scale"/>.</summary>
    public static byte[] Jpeg(Drawing.Bitmap shot, double scale)
    {
        using var small = new Drawing.Bitmap(shot, Math.Max(1, (int)(shot.Width * scale)), Math.Max(1, (int)(shot.Height * scale)));
        using var stream = new MemoryStream();
        var codec = Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var options = new Drawing.Imaging.EncoderParameters(1);
        options.Param[0] = new Drawing.Imaging.EncoderParameter(Drawing.Imaging.Encoder.Quality, 70L);
        small.Save(stream, codec, options);
        return stream.ToArray();
    }

    /// <summary>The control with the keyboard focus is a password box (UI Automation; false when it can't tell).</summary>
    public static Task<bool> FocusIsPasswordAsync() => Task.Run(() =>
    {
        try { return System.Windows.Automation.AutomationElement.FocusedElement?.Current.IsPassword == true; }
        catch { return false; }
    });

    static string Str(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";

    static int Int(JsonObject json, string name)
    {
        if (json[name] is not JsonValue value) return -1;
        if (value.TryGetValue<double>(out var d)) return d >= 0 ? (int)Math.Round(d) : -1;
        if (value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d >= 0 ? (int)Math.Round(d) : -1;
        return -1;
    }

    /// <summary>Lowercase without accents, so "Excluir"/"EXCLUÍR" and "Código" match the word lists.</summary>
    static string Plain(string text)
    {
        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(c);
        return plain.ToString().Normalize(NormalizationForm.FormC);
    }

    static string Cut(string text, int max) => text.Length > max ? text[..(max - 1)] + "…" : text;
}
