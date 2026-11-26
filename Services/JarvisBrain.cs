using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

/// <summary>What Jarvis's tools need from the island (each runs on the UI thread).</summary>
public interface IJarvisHost
{
    void Remind(double minutes, string text);
    void StartTimer(int minutes);
    void AddTask(string text);
    void Media(string action);
    /// <summary>Shows the whole script and asks; true only when the person allows it.</summary>
    Task<bool> ConfirmScriptAsync(string description, string code);
    /// <summary>The text on the window the person was looking at when they called Jarvis (OCR); null when unreadable.</summary>
    Task<string?> ReadScreenAsync();
    /// <summary>The enabled plugins' tools, in the model's function format (started on first use).</summary>
    Task<JsonArray> PluginToolsAsync();
    /// <summary>Runs a plugin tool; null when <paramref name="name"/> isn't one.</summary>
    Task<string?> CallPluginAsync(string name, JsonObject arguments);
    /// <summary>Mouse and keyboard on the person's screen for <paramref name="task"/>, under their watch; what happened.</summary>
    Task<string> UseComputerAsync(string task);
    /// <summary>Takes back a task <see cref="AddTask"/> added.</summary>
    void RemoveTask(string text);
    /// <summary>Brings an open window (by title or app name) to the front; what happened.</summary>
    Task<string> FocusWindowAsync(string name);
    /// <summary>A shortcut (desfazer = Ctrl+Z, salvar = Ctrl+S…) in the window the person was using; what happened.</summary>
    Task<string> ShortcutAsync(string action);
}

/// <summary>
/// The brain of Project.Jarvis (jarvis_brain.py) in C#: Groq's chat completions with function calling, so the model
/// really acts on the PC through a fixed set of tools, plus the long-term memory (facts it was asked to remember and the
/// recent conversation). Scripts run only after the person reads and approves them. The Groq key is kept with DPAPI.
/// </summary>
public sealed class JarvisBrain
{
    const string ApiUrl = "https://api.groq.com/openai/v1/chat/completions";
    const string WhisperUrl = "https://api.groq.com/openai/v1/audio/transcriptions";
    // each model has its own free limit; on 429 the next one answers
    static readonly string[] Models = ["openai/gpt-oss-20b", "qwen/qwen3.8-27b"];
    const int MaxSteps = 14, TurnsSent = 6, TurnsKept = 12, MaxFacts = 60, ToolResultChars = 5000, MaxUndo = 20;

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    /// <summary>Tests keep the memory and the notes here instead of the settings folder.</summary>
    public static string? MemoryOverride;
    static string MemoryFile => Path.Combine(MemoryOverride ?? IslandSettings.Folder, "jarvis-memoria.json");
    static string NotesFile => Path.Combine(MemoryOverride ?? IslandSettings.Folder, "jarvis-notas.txt");
    static string KeyFile => Path.Combine(IslandSettings.Folder, "groq.bin");
    static readonly byte[] Purpose = "DynamicIsland.Groq"u8.ToArray();

    static readonly HttpClient Http = new(new HttpClientHandler { DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials })
    {
        Timeout = TimeSpan.FromSeconds(40),
    };

    readonly IJarvisHost _host;
    readonly Func<string> _name;
    List<string> _facts = [];
    List<JsonObject> _history = [];
    readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>Something Jarvis did that it can take back, newest last.</summary>
    sealed record Undoable(string What, Func<string> Undo);
    readonly List<Undoable> _undo = [];

    void Did(string what, Func<string> undo)
    {
        _undo.Add(new Undoable(what, undo));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
    }

    public JarvisBrain(IJarvisHost host, Func<string> name)
    {
        _host = host;
        _name = name;
        LoadMemory();
    }

    // ───────────────────────── key ─────────────────────────

    public static bool HasKey => File.Exists(KeyFile);

    static string? LoadKey()
    {
        try { return File.Exists(KeyFile) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), Purpose, DataProtectionScope.CurrentUser)) : null; }
        catch { return null; }
    }

    /// <summary>Saves (or with an empty key, forgets) the Groq key. False when it doesn't look like one.</summary>
    public static bool SaveKey(string key)
    {
        key = key.Trim().Trim('"', '\'');
        if (key.Length == 0)
        {
            try { File.Delete(KeyFile); } catch { }
            return true;
        }
        if (!Regex.IsMatch(key, @"^gsk_[A-Za-z0-9]{20,}$")) return false;
        Directory.CreateDirectory(IslandSettings.Folder);
        AtomicFile.WriteAllBytes(KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Purpose, DataProtectionScope.CurrentUser), backup: false);
        return true;
    }

    /// <summary>Project.Jarvis next to the user's files (Documents, Desktop or the profile), if there is one.</summary>
    public static string? ProjectFolder() =>
        new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.UserProfile }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "Project.Jarvis"))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "jarvis_brain.py")));

    /// <summary>Brings Project.Jarvis's Groq key, memory and notes here. Returns what came.</summary>
    public string ImportProject(string folder)
    {
        var found = new List<string>();
        var env = Path.Combine(folder, ".env");
        if (File.Exists(env))
            foreach (var line in File.ReadAllLines(env))
            {
                var at = line.IndexOf('=');
                if (at > 0 && line[..at].Trim() == "GROQ_API_KEY" && SaveKey(line[(at + 1)..])) found.Add("a chave da Groq");
            }
        var memory = Path.Combine(folder, "memoria_longa.json");
        if (File.Exists(memory) && !File.Exists(MemoryFile))
        {
            File.Copy(memory, MemoryFile);
            LoadMemory();
            found.Add($"a memória ({_facts.Count} lembrança(s))");
        }
        var notes = Path.Combine(folder, "memoria_jarvis.txt");
        if (File.Exists(notes) && !File.Exists(NotesFile))
        {
            File.Copy(notes, NotesFile);
            found.Add("as anotações");
        }
        return found.Count == 0 ? "Não achei chave, memória nem anotações no Project.Jarvis." : "Trouxe " + string.Join(", ", found) + ".";
    }

    // ───────────────────────── memory ─────────────────────────

    public int FactCount => _facts.Count;

    void LoadMemory()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(MemoryFile)) is JsonObject root)
            {
                _facts = root["fatos"]?.AsArray().Select(f => f?.GetValue<string>() ?? "").Where(f => f.Length > 0).ToList() ?? [];
                _history = LastTurns(root["historico"]?.AsArray().OfType<JsonObject>()
                    .Where(m => m["role"]?.GetValue<string>() is "user" or "assistant" or "tool")
                    .Select(m => (JsonObject)m.DeepClone())
                    .ToList() ?? [], TurnsKept);
            }
        }
        catch { }
    }

    void SaveMemory()
    {
        var root = new JsonObject
        {
            ["fatos"] = new JsonArray(_facts.Select(f => (JsonNode)f).ToArray()),
            ["historico"] = new JsonArray(_history.Select(m => m.DeepClone()).ToArray()),
        };
        AtomicFile.WriteAllText(MemoryFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    /// <summary>The last <paramref name="turns"/> exchanges, each whole (the request, the tool calls and their results,
    /// the answer): the model sees that actions were done through tools, so it doesn't just repeat an old answer.</summary>
    static List<JsonObject> LastTurns(List<JsonObject> history, int turns)
    {
        var start = history.Count;
        for (var i = history.Count - 1; i >= 0 && turns > 0; i--)
            if (history[i]["role"]?.GetValue<string>() == "user")
            {
                start = i;
                turns--;
            }
        return history[start..];
    }

    public void ForgetEverything()
    {
        _facts = [];
        _history = [];
        SaveMemory();
    }

    /// <summary>The saved turns as the person saw them: what they asked and what Jarvis answered (no tool calls).</summary>
    public List<(bool User, string Text)> PastConversation() =>
        _history.Select(m => (Role: m["role"]?.GetValue<string>(), Text: m["content"]?.GetValue<string>()?.Trim() ?? ""))
            .Where(m => m.Role is "user" or "assistant" && m.Text.Length > 0)
            .Select(m => (m.Role == "user", m.Text)).ToList();

    /// <summary>A fresh conversation: the past turns go, the permanent memory stays.</summary>
    public void ClearConversation()
    {
        _history = [];
        _undo.Clear();
        SaveMemory();
    }

    // ───────────────────────── answering ─────────────────────────

    string SystemPrompt()
    {
        var name = _name().Trim();
        var prompt = $"Você é o Jarvis, assistente pessoal de PC {(name.Length > 0 ? "de " + name : "do usuário")}, e vive na Dynamic Island do Windows. " +
                     "Fale sempre em português do Brasil, de forma curta e natural (suas respostas são lidas em voz alta: sem markdown, sem emojis, sem listas longas). " +
                     "Tom amigável com um toque leve de humor. Use as ferramentas para agir de verdade no PC e nunca diga que fez algo sem chamar a ferramenta. " +
                     "Cada pedido de ação é novo: chame a ferramenta de novo mesmo que a conversa mostre que você já fez algo igual antes. " +
                     "Para informações atuais use buscar_web ou clima. Se pedirem um resumo do dia, combine hora_data, clima e ler_notas. " +
                     "Para escrever um texto no Bloco de Notas use escrever_no_bloco_de_notas; anotar guarda uma nota rápida na memória. " +
                     "Nunca revele chaves ou senhas. Você só executa as ações das ferramentas disponíveis. " +
                     "Não peça desculpas e não diga que não consegue antes de tentar as ferramentas: se algo falhar, diga em uma frase o que houve " +
                     "e o que você pode fazer em seguida. " +
                     "Os textos de escrever_no_bloco_de_notas ficam salvos (lista abaixo): para voltar a um texto anterior, abra o arquivo com " +
                     "abrir_arquivo; para ler o conteúdo, ler_arquivo. Para mudar um texto, escreva a versão nova. " +
                     "Para desfazer o que você mesmo fez (texto escrito, pasta criada, tarefa, lembrança, anotação) chame desfazer. " +
                     "Ctrl+Z, Ctrl+S e outros atalhos dentro de um programa: atalho. Trazer uma janela aberta para frente: trazer_janela. " +
                     "Quando o usuário pedir para você lembrar algo, mudar como o chama, ou adotar um comportamento daqui pra frente, chame a ferramenta lembrar. " +
                     "Scripts só com executar_script (o usuário aprova antes). " +
                     "Quando perguntarem sobre algo que está na tela (um erro, uma mensagem, uma página), chame ver_tela antes de responder. " +
                     "Para sites (pegar uma informação, preencher um formulário, baixar um arquivo), se houver ferramentas p_navegador_*: " +
                     "abra com browser_navigate, leia com browser_snapshot e use as refs dele em browser_click e browser_type. " +
                     "usar_computador mexe no mouse e no teclado do PC de verdade: use só quando o usuário pedir para fazer algo na tela dele " +
                     "e nenhuma outra ferramenta resolver, e nunca para digitar senhas. " +
                     $"Data e hora atuais (use esta, nunca invente): {Now()}.";
        if (_facts.Count > 0)
            prompt += "\n\nMEMÓRIA PERMANENTE (preferências e fatos que o usuário pediu; siga SEMPRE, inclusive como chamar o usuário):\n" +
                      string.Join("\n", _facts.Select(f => "- " + f));
        var written = WrittenTexts();
        if (written.Count > 0)
            prompt += "\n\nTEXTOS QUE VOCÊ ESCREVEU (mais novo primeiro):\n" +
                      string.Join("\n", written.Select(f => $"- {f.LastWriteTime:dd/MM HH:mm} {f.FullName}"));
        if (_undo.Count > 0)
            prompt += "\n\nO QUE VOCÊ FEZ HÁ POUCO E DÁ PARA DESFAZER (mais novo primeiro):\n" +
                      string.Join("\n", _undo.AsEnumerable().Reverse().Select(u => "- " + u.What));
        return prompt;
    }

    CancellationTokenSource? _stop;

    /// <summary>Stops the request under way: no more model calls or tools; it answers that it stopped.</summary>
    public void Stop()
    {
        try { _stop?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Answers, calling tools as the model asks (up to five rounds). Never throws: problems come back as text.</summary>
    public async Task<string> RespondAsync(string text)
    {
        if (LoadKey() is not { } key) return "Falta a chave da Groq: Configurações › Jarvis.";
        if (!await _busy.WaitAsync(0)) return "Ainda estou cuidando do pedido anterior.";
        using var stop = _stop = new CancellationTokenSource();
        var cancel = stop.Token;
        try
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt() } };
            // the plugins' tools are thousands of tokens: only sent when the request needs them (the free plan's per-minute cap)
            var plugins = NeedsPlugins(text) ? await _host.PluginToolsAsync() : [];
            var tools = new JsonArray([.. Tools.Select(t => t!.DeepClone()), .. plugins.Select(t => t!.DeepClone())]);
            var past = LastTurns(_history, TurnsSent);
            foreach (var m in past) messages.Add(m.DeepClone());
            var turn = new List<JsonObject> { new() { ["role"] = "user", ["content"] = text } };
            messages.Add(turn[0].DeepClone());
            var lean = false;
            for (var step = 0; step < MaxSteps; step++)
            {
                cancel.ThrowIfCancellationRequested();
                JsonObject message;
                try { message = await CallAsync(key, messages, tools, cancel); }
                catch (TooLargeException) when (!lean)
                {
                    // the free plan caps tokens per request: drop the plugins' tools and the old turns
                    lean = true;
                    tools = new JsonArray([.. Tools.Select(t => t!.DeepClone())]);
                    for (var i = 0; i < past.Count; i++) messages.RemoveAt(1);
                    message = await CallAsync(key, messages, tools, cancel);
                }
                var calls = message["tool_calls"] as JsonArray;
                if (calls is not { Count: > 0 })
                {
                    var answer = Clean(message["content"]?.GetValue<string>() ?? "");
                    if (answer.Length == 0) answer = "Pronto.";
                    turn.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer });
                    _history = LastTurns([.. _history, .. turn], TurnsKept);
                    SaveMemory();
                    return answer;
                }
                var asked = new JsonObject
                {
                    ["role"] = "assistant", ["content"] = message["content"]?.GetValue<string>() ?? "", ["tool_calls"] = calls.DeepClone(),
                };
                messages.Add(asked);
                turn.Add((JsonObject)asked.DeepClone());
                foreach (var call in calls.OfType<JsonObject>())
                {
                    cancel.ThrowIfCancellationRequested();
                    var result = await RunToolAsync(call["function"]?["name"]?.GetValue<string>() ?? "", call["function"]?["arguments"]?.GetValue<string>());
                    var done = new JsonObject
                    {
                        ["role"] = "tool", ["tool_call_id"] = call["id"]?.GetValue<string>() ?? "",
                        ["content"] = result.Length > ToolResultChars ? result[..ToolResultChars] + "…" : result,
                    };
                    messages.Add(done);
                    // what a file or a page said needn't be sent again in later turns
                    turn.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = done["tool_call_id"]!.GetValue<string>(),
                        ["content"] = result.Length > 300 ? result[..300] + "…" : result });
                }
            }
            return "Não consegui concluir isso. Tenta de novo de outro jeito.";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return "Parei, como você pediu."; }
        catch (LimitException ex)
        {
            return ex.Wait is { } wait ? $"Os modelos grátis da Groq estão no limite agora. Tenta de novo em {Math.Ceiling(wait)} segundos."
                : "Os modelos grátis da Groq estão no limite agora. Tenta daqui a pouco.";
        }
        catch (UnauthorizedAccessException) { return "A Groq recusou a chave. Confira em Configurações › Jarvis."; }
        catch (TooLargeException) { return "O pedido ficou grande demais para o plano grátis da Groq. Tenta um pedido mais curto."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error("JarvisBrain", ex);
            return "Não consegui falar com a Groq (sem internet?).";
        }
        catch (Exception ex)
        {
            Log.Error("JarvisBrain", ex);
            return "Algo deu errado: " + ex.Message;
        }
        finally
        {
            _stop = null;
            _busy.Release();
        }
    }

    static bool NeedsPlugins(string text) =>
        Regex.IsMatch(text, @"https?://|www\.|\.com|\.br\b|\bsite|navegador|p[áa]gina|formul[áa]rio|\blogin|\bbaix(e|ar)|\bentr(e|ar) no|youtube|google|chrome|edge|plugin",
            RegexOptions.IgnoreCase);

    sealed class LimitException(double? wait) : Exception
    {
        public double? Wait { get; } = wait;
    }

    sealed class TooLargeException : Exception;

    async Task<JsonObject> CallAsync(string key, JsonArray messages, JsonArray tools, CancellationToken cancel)
    {
        double? wait = null;
        bool badCall = false, tooLarge = false;
        foreach (var model in Models)
        {
            var body = new JsonObject
            {
                ["model"] = model, ["messages"] = messages.DeepClone(), ["tools"] = tools.DeepClone(), ["tool_choice"] = "auto",
                ["temperature"] = 0.5, ["max_tokens"] = 500,
            };
            if (model.StartsWith("openai/gpt-oss", StringComparison.Ordinal)) body["reasoning_effort"] = "low";
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await Http.SendAsync(request, cancel);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 10;
                wait = wait is { } w ? Math.Min(w, retry) : retry;
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException();
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                Log.Warn("JarvisBrain", $"{model}: 413 ({body.ToJsonString().Length} chars)");
                tooLarge = true;
                continue;
            }
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var problem = await response.Content.ReadAsStringAsync();
                Log.Warn("JarvisBrain", $"{model}: 400 {problem[..Math.Min(300, problem.Length)]}");
                // the model wrote a tool call Groq couldn't parse: the next model gets a try
                if (problem.Contains("tool_use_failed") || problem.Contains("failed_generation"))
                {
                    badCall = true;
                    continue;
                }
            }
            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel));
            return json?["choices"]?[0]?["message"] as JsonObject ?? throw new InvalidDataException("Resposta vazia da Groq.");
        }
        if (tooLarge && wait == null) throw new TooLargeException();
        if (badCall && wait == null) throw new InvalidDataException("O modelo se confundiu com as ferramentas. Tente pedir de outro jeito.");
        if (wait is <= 25)
        {
            await Task.Delay(TimeSpan.FromSeconds(wait.Value), cancel);
            return await CallAsync(key, messages, tools, cancel);
        }
        throw new LimitException(wait);
    }

    // models that see images, in order (Groq retires models: a 404 moves on to the next)
    static readonly string[] VisionModels = ["qwen/qwen3.8-27b", "meta-llama/llama-4-scout-17b-16e-instruct"];

    /// <summary>How long Groq said to wait the last time vision hit the free limit (null: it didn't).</summary>
    public static TimeSpan? VisionLimitWait { get; private set; }

    /// <summary>A model that sees: <paramref name="jpeg"/> plus the text, answered as JSON (computer use). Null without a
    /// key, at the limit or offline.</summary>
    public static async Task<JsonObject?> VisionJsonAsync(string system, string text, byte[] jpeg, CancellationToken cancel = default, int tries = 0)
    {
        if (LoadKey() is not { } key) return null;
        VisionLimitWait = null;
        foreach (var model in VisionModels)
            try
            {
                var body = new JsonObject
                {
                    ["model"] = model, ["temperature"] = 0.1, ["max_tokens"] = 700,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "system", ["content"] = system },
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = new JsonArray
                            {
                                new JsonObject { ["type"] = "text", ["text"] = text },
                                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) } },
                            },
                        },
                    },
                };
                using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await Http.SendAsync(request, cancel);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                // the free plan's per-minute limit: wait it out once if it's short
                if (response.StatusCode == HttpStatusCode.TooManyRequests && tries < 3
                    && (response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 8) is var wait and <= 30)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, wait)), cancel);
                    return await VisionJsonAsync(system, text, jpeg, cancel, tries + 1);
                }
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    VisionLimitWait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn("Groq vision", $"{model}: HTTP {(int)response.StatusCode}");
                    return null;
                }
                var content = JsonNode.Parse(await response.Content.ReadAsStringAsync())?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                if (content == null) return null;
                // a thinking model may reason first
                if (content.LastIndexOf("</think>", StringComparison.Ordinal) is var think and >= 0) content = content[(think + 8)..];
                int start = content.IndexOf('{'), end = content.LastIndexOf('}');
                return start >= 0 && end > start ? JsonNode.Parse(content[start..(end + 1)]) as JsonObject : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                Log.Error("Groq vision", ex);
                return null;
            }
        Log.Warn("Groq vision", "nenhum modelo de visão disponível");
        return null;
    }

    /// <summary>One plain answer (no tools, no memory) for a job like fixing a dictation or translating. Null without a
    /// key, at the free limit or offline: the caller keeps what it had.</summary>
    public static async Task<string?> CompleteAsync(string system, string user, int maxTokens = 900)
    {
        if (LoadKey() is not { } key) return null;
        foreach (var model in Models)
        {
            try
            {
                var body = new JsonObject
                {
                    ["model"] = model, ["temperature"] = 0.2, ["max_tokens"] = maxTokens,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "system", ["content"] = system },
                        new JsonObject { ["role"] = "user", ["content"] = user },
                    },
                };
                if (model.StartsWith("openai/gpt-oss", StringComparison.Ordinal)) body["reasoning_effort"] = "low";
                using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await Http.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.TooManyRequests) continue;
                if (!response.IsSuccessStatusCode) return null;
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
                var text = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(text)) continue;
                return Regex.Replace(text, "<think>.*?</think>", "", RegexOptions.Singleline).Trim();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                Log.Error("Groq", ex);
                return null;
            }
        }
        return null;
    }

    static string Clean(string text)
    {
        text = Regex.Replace(text, "<think>.*?</think>", "", RegexOptions.Singleline);
        // the answers are read aloud: markdown marks would be spoken
        text = Regex.Replace(text, @"\*{1,3}|_{2,}|`+|^#+\s*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, "[\u00a0\u202f\u2009]", " ");
        text = Regex.Replace(text, @"(\s*(\.\.\.|…)\s*){2,}", "... ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>Whisper on Groq (much better than the small local one). Null without a key or on failure.</summary>
    public static async Task<string?> TranscribeAsync(string wave) => (await TranscribeWithReasonAsync(wave)).Text;

    /// <summary>What was said, or why Groq didn't say (in words for the island).</summary>
    public static async Task<(string? Text, string? Problem)> TranscribeWithReasonAsync(string wave)
    {
        if (LoadKey() is not { } key) return (null, "Falta a chave da Groq: Configurações › Jarvis");
        try
        {
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(await File.ReadAllBytesAsync(wave)) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "file", "fala.wav" },
                { new StringContent("whisper-large-v3-turbo"), "model" },
                { new StringContent("pt"), "language" },
                { new StringContent("text"), "response_format" },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, WhisperUrl) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await Http.SendAsync(request);
            if (response.IsSuccessStatusCode) return ((await response.Content.ReadAsStringAsync()).Trim(), null);
            Log.Warn("Groq whisper", $"HTTP {(int)response.StatusCode}");
            return (null, response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "A Groq recusou a chave: cole uma nova em Configurações › Jarvis",
                HttpStatusCode.TooManyRequests => "Limite grátis da Groq: tente de novo em um minuto",
                _ => $"A Groq não transcreveu agora ({(int)response.StatusCode})",
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error("Groq whisper", ex);
            return (null, "Sem internet para falar com a Groq");
        }
    }

    // ───────────────────────── tools ─────────────────────────

    static JsonObject Tool(string name, string description, JsonObject? props = null, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name, ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = props ?? [], ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
            },
        },
    };

    static JsonObject Str(string? description = null) => description == null ? new() { ["type"] = "string" } : new() { ["type"] = "string", ["description"] = description };
    static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };

    static readonly JsonArray Tools =
    [
        Tool("abrir_site", "Abre um site no navegador (nome conhecido, domínio ou termo de busca).", new() { ["alvo"] = Str() }, "alvo"),
        Tool("abrir_app", "Abre um aplicativo instalado no PC (Calculadora, Bloco de Notas, Spotify, Discord, VS Code…).", new() { ["nome"] = Str() }, "nome"),
        Tool("hora_data", "Retorna dia da semana, data e hora atuais."),
        Tool("anotar", "Salva uma anotação rápida na memória permanente do Jarvis.", new() { ["texto"] = Str() }, "texto"),
        Tool("ler_notas", "Lê as anotações salvas."),
        Tool("escrever_no_bloco_de_notas", "Escreve um texto num arquivo novo e abre no Bloco de Notas, para o usuário ver ou editar.",
            new() { ["texto"] = Str(), ["titulo"] = Str("Nome curto para o arquivo") }, "texto"),
        Tool("abrir_arquivo", "Abre um arquivo (ou pasta) que já existe dentro da pasta do usuário: textos no Bloco de Notas, o resto no programa padrão.",
            new() { ["caminho"] = Str() }, "caminho"),
        Tool("desfazer", "Desfaz as últimas ações que você mesmo fez: textos escritos (vão para a Lixeira), pastas criadas, tarefas, lembranças e anotações.",
            new() { ["quantas"] = new JsonObject { ["type"] = "number", ["description"] = "Quantas ações, da mais nova para a mais velha (padrão 1; 99 = todas)." } }),
        Tool("atalho", "Aperta um atalho de teclado na janela que o usuário estava usando (ex.: desfazer = Ctrl+Z dentro do programa).",
            new() { ["acao"] = Enum("desfazer", "refazer", "salvar", "selecionar_tudo", "copiar", "colar", "localizar", "nova_aba", "fechar_aba", "atualizar") }, "acao"),
        Tool("trazer_janela", "Traz para frente uma janela já aberta, pelo título ou nome do programa (ex.: Bloco de Notas, Chrome).",
            new() { ["nome"] = Str() }, "nome"),
        Tool("criar_pasta", "Cria uma pasta na área de trabalho.", new() { ["nome"] = Str() }, "nome"),
        Tool("listar_pasta", "Lista arquivos de uma pasta dentro do usuário (ex: Desktop, Downloads).", new() { ["caminho"] = Str() }),
        Tool("ler_arquivo", "Lê um arquivo de texto dentro da pasta do usuário.", new() { ["caminho"] = Str() }, "caminho"),
        Tool("status_pc", "Mostra uso de RAM e espaço livre em disco."),
        Tool("ver_tela", "Lê o texto da janela que o usuário está olhando (erros, mensagens, páginas)."),
        Tool("usar_computador", "Controla o mouse e o teclado do PC para fazer uma tarefa na tela; o usuário acompanha, aprova e pode parar com Esc.",
            new() { ["tarefa"] = Str("O que fazer, em detalhes (ex.: 'no Spotify aberto, tocar a playlist Foco')") }, "tarefa"),
        Tool("volume", "Volume da música (do Spotify quando ele toca; senão o do Windows). Responde o nível em que ficou. " +
             "'definir' usa nivel (0 a 100); 'tirar_mudo' volta o som; 'mudo' silencia.",
            new() { ["acao"] = Enum("aumentar", "diminuir", "definir", "mudo", "tirar_mudo"), ["nivel"] = new JsonObject { ["type"] = "number", ["description"] = "Porcentagem para 'definir', de 0 a 100." } }, "acao"),
        Tool("midia", "Controla a música ou vídeo do PC. 'tocar' dá play (também logo depois de abrir o Spotify, ele espera o app carregar); " +
             "'pausar' pausa; 'tocar_pausar' só alterna.", new() { ["acao"] = Enum("tocar", "pausar", "tocar_pausar", "proxima", "anterior") }, "acao"),
        Tool("buscar_web", "Busca fatos na Wikipedia (pessoas, lugares, conceitos, tecnologia) e devolve resumos.", new() { ["consulta"] = Str() }, "consulta"),
        Tool("clima", "Previsão do tempo atual de uma cidade.", new() { ["cidade"] = Str() }, "cidade"),
        Tool("lembrete", "Agenda um lembrete que aparece na ilha depois de N minutos.",
            new() { ["minutos"] = new JsonObject { ["type"] = "number" }, ["texto"] = Str() }, "minutos", "texto"),
        Tool("timer", "Liga o timer da ilha por N minutos.", new() { ["minutos"] = new JsonObject { ["type"] = "number" } }, "minutos"),
        Tool("adicionar_tarefa", "Adiciona uma tarefa na lista de tarefas da ilha.", new() { ["texto"] = Str() }, "texto"),
        Tool("lembrar", "Grava na memória permanente uma preferência, regra ou fato sobre o usuário (ex: como ele quer ser chamado). " +
                        "Use sempre que ele pedir para lembrar algo para sempre.", new() { ["fato"] = Str() }, "fato"),
        Tool("esquecer", "Apaga da memória permanente os fatos que contêm o trecho informado.", new() { ["trecho"] = Str() }, "trecho"),
        Tool("executar_script", "Executa um script PowerShell no PC do usuário. O usuário precisa ler e aprovar antes; use para tarefas que as " +
                                "outras ferramentas não fazem. Retorna a saída do script.",
            new() { ["codigo"] = Str(), ["descricao"] = Str("O que o script faz, em uma frase simples") }, "codigo", "descricao"),
    ];

    static readonly Dictionary<string, string> Sites = new()
    {
        ["youtube"] = "https://www.youtube.com", ["google"] = "https://www.google.com", ["gmail"] = "https://mail.google.com",
        ["github"] = "https://github.com", ["whatsapp"] = "https://web.whatsapp.com", ["instagram"] = "https://www.instagram.com",
        ["roblox"] = "https://www.roblox.com", ["tiktok"] = "https://www.tiktok.com", ["discord"] = "https://discord.com/app",
    };

    async Task<string> RunToolAsync(string name, string? arguments)
    {
        try
        {
            var a = (arguments is { Length: > 0 } ? JsonNode.Parse(arguments) as JsonObject : null) ?? [];
            string S(string key) => a[key]?.ToString() ?? "";
            double N(string key) => double.TryParse(a[key]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
            return name switch
            {
                "abrir_site" => OpenSite(S("alvo")),
                "abrir_app" => OpenApp(S("nome")),
                "hora_data" => Now(),
                "anotar" => Note(S("texto")),
                "ler_notas" => ReadNotes(),
                "escrever_no_bloco_de_notas" => WriteInNotepad(S("texto"), S("titulo")),
                "abrir_arquivo" => OpenFile(S("caminho")),
                "desfazer" => Undo((int)Math.Round(N("quantas"))),
                "atalho" => await _host.ShortcutAsync(S("acao")),
                "trazer_janela" => await _host.FocusWindowAsync(S("nome")),
                "criar_pasta" => CreateFolder(S("nome")),
                "listar_pasta" => ListFolder(S("caminho")),
                "ler_arquivo" => ReadFile(S("caminho")),
                "status_pc" => PcStatus(),
                "ver_tela" => await _host.ReadScreenAsync() is { Length: > 0 } screen
                    ? "Texto na tela:\n" + (screen.Length > 3000 ? screen[..3000] : screen) : "Não consegui ler a tela.",
                "volume" => Volume(S("acao"), N("nivel")),
                "midia" => OnHost(() => _host.Media(S("acao")), "Feito."),
                "buscar_web" => await SearchWikipedia(S("consulta")),
                "clima" => await Weather(S("cidade")),
                "lembrete" => Reminder(N("minutos"), S("texto")),
                "timer" => OnHost(() => _host.StartTimer((int)Math.Clamp(Math.Round(N("minutos")), 1, 600)), $"Timer de {Math.Clamp(Math.Round(N("minutos")), 1, 600)} minuto(s) ligado."),
                "adicionar_tarefa" => AddTask(S("texto")),
                "lembrar" => Remember(S("fato")),
                "esquecer" => Forget(S("trecho")),
                "executar_script" => await RunScriptAsync(S("codigo"), S("descricao")),
                "usar_computador" => await _host.UseComputerAsync(S("tarefa")),
                _ => await _host.CallPluginAsync(name, a) ?? $"Ferramenta desconhecida: {name}",
            };
        }
        catch (Exception ex)
        {
            return $"Erro na ferramenta {name}: {ex.Message}";
        }
    }

    static string OnHost(Action action, string done)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } ui && !ui.CheckAccess()) ui.Invoke(action);
        else action();
        return done;
    }

    static string Now() => DateTime.Now.ToString("dddd, dd/MM/yyyy, HH:mm", CultureInfo.GetCultureInfo("pt-BR"));

    static string OpenSite(string target)
    {
        var name = target.Trim().ToLowerInvariant();
        var url = Sites.GetValueOrDefault(name, target.Trim());
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = url.Contains('.') && !url.Contains(' ') ? "https://" + url : "https://www.google.com/search?q=" + Uri.EscapeDataString(url);
        // only web pages: never file:, javascript: or another app's scheme
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "Endereço inválido.";
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return $"Abri {uri.AbsoluteUri}";
    }

    static string OpenApp(string name)
    {
        if (Jarvis.FindApp(name) is not { } app) return $"Não achei \"{name}\" neste PC.";
        Process.Start(new ProcessStartInfo(app.Target) { UseShellExecute = true });
        return $"Abri {app.Name}.";
    }

    string Note(string text)
    {
        Directory.CreateDirectory(IslandSettings.Folder);
        var line = $"- [{DateTime.Now:dd/MM/yyyy HH:mm}] {Regex.Replace(text, @"\s+", " ").Trim()}";
        File.AppendAllText(NotesFile, line + "\n");
        Did($"anotou \"{Short(text)}\"", () =>
        {
            var lines = File.Exists(NotesFile) ? File.ReadAllLines(NotesFile).ToList() : [];
            if (lines.LastIndexOf(line) is var at and >= 0) lines.RemoveAt(at);
            File.WriteAllLines(NotesFile, lines);
            return $"Apaguei a anotação \"{Short(text)}\".";
        });
        return "Anotado.";
    }

    static string Short(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 50 ? text[..49] + "…" : text;
    }

    string AddTask(string text)
    {
        OnHost(() => _host.AddTask(text), "");
        Did($"adicionou a tarefa \"{Short(text)}\"", () => OnHost(() => _host.RemoveTask(text), $"Tirei a tarefa \"{Short(text)}\"."));
        return "Tarefa adicionada.";
    }

    /// <summary>Takes back the last <paramref name="count"/> things Jarvis did, newest first.</summary>
    string Undo(int count)
    {
        if (_undo.Count == 0)
            return "Não há nenhuma ação sua para desfazer (sites, apps e scripts não se desfazem). Para o Ctrl+Z de um programa, use atalho.";
        var done = new List<string>();
        for (var i = 0; i < Math.Clamp(count, 1, MaxUndo) && _undo.Count > 0; i++)
        {
            var last = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            try { done.Add(last.Undo()); }
            catch (Exception ex) { done.Add($"Não deu para desfazer \"{last.What}\": {ex.Message}"); }
        }
        return string.Join("\n", done);
    }

    static readonly HashSet<string> OpenableFiles = [".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".mp4", ".mp3", ".wav", ".docx", ".xlsx", ".pptx"];

    static string OpenFile(string path)
    {
        var full = InsideHome(path);
        if (Directory.Exists(full))
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { full }, UseShellExecute = false });
            return $"Abri a pasta {full}";
        }
        if (!File.Exists(full)) return $"Não existe o arquivo {full}. Veja os nomes com listar_pasta.";
        var extension = Path.GetExtension(full).ToLowerInvariant();
        if (TextFiles.Contains(extension))
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { full }, UseShellExecute = false });
        // never a program or a script: only documents, pictures and media
        else if (OpenableFiles.Contains(extension)) Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
        else return "Esse tipo de arquivo eu não abro (só textos, documentos, imagens e mídia).";
        return $"Abri {full}";
    }

    static string WrittenFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Jarvis");

    static List<FileInfo> WrittenTexts()
    {
        try
        {
            return Directory.Exists(WrittenFolder)
                ? new DirectoryInfo(WrittenFolder).EnumerateFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Take(8).ToList()
                : [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    static string Recycle(string path)
    {
        if (!File.Exists(path)) return $"{Path.GetFileName(path)} já não existe.";
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        return $"Mandei {Path.GetFileName(path)} para a Lixeira (se ainda está aberto no Bloco de Notas, é só fechar).";
    }

    static string ReadNotes()
    {
        var notes = File.Exists(NotesFile) ? File.ReadAllText(NotesFile).Trim() : "";
        return notes.Length == 0 ? "Nenhuma anotação." : notes.Length > 4000 ? notes[^4000..] : notes;
    }

    string WriteInNotepad(string text, string title)
    {
        var folder = WrittenFolder;
        Directory.CreateDirectory(folder);
        var name = Regex.Replace(title, @"[<>:""/\\|?*\x00-\x1f]", "").Trim();
        if (name.Length == 0) name = $"Nota {DateTime.Now:yyyy-MM-dd HH'h'mm}";
        if (name.Length > 60) name = name[..60];
        var file = Path.Combine(folder, name + ".txt");
        for (var i = 2; File.Exists(file); i++) file = Path.Combine(folder, $"{name} ({i}).txt");
        File.WriteAllText(file, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));
        Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { file }, UseShellExecute = false });
        Did($"escreveu {file}", () => Recycle(file));
        return $"Escrevi no Bloco de Notas: {file}";
    }

    string CreateFolder(string name)
    {
        var clean = Regex.Replace(name, @"[<>:""/\\|?*\x00-\x1f]", "").Trim();
        if (clean.Length == 0) clean = "Nova pasta";
        if (clean.Length > 80) clean = clean[..80];
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), clean);
        if (Directory.Exists(path)) return $"A pasta já existia: {path}";
        Directory.CreateDirectory(path);
        Did($"criou a pasta {path}", () =>
        {
            if (!Directory.Exists(path)) return $"A pasta {clean} já não existe.";
            // something was put in it since: that stays
            if (Directory.EnumerateFileSystemEntries(path).Any()) return $"A pasta {clean} já tem arquivos; deixei como está.";
            Directory.Delete(path);
            return $"Apaguei a pasta {clean}.";
        });
        return $"Pasta criada: {path}";
    }

    /// <summary>A path the model gave, only if it stays inside the user's folder.</summary>
    static string InsideHome(string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Home, path));
        var home = Path.GetFullPath(Home).TrimEnd('\\') + "\\";
        if (!(full + "\\").StartsWith(home, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Acesso permitido somente dentro da sua pasta de usuário.");
        return full;
    }

    static string ListFolder(string path)
    {
        var folder = InsideHome(path.Length > 0 ? path : "Desktop");
        var items = new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Take(60)
            .Select(i => (i is DirectoryInfo ? "[pasta] " : "") + i.Name);
        var list = string.Join("\n", items);
        return list.Length > 0 ? list : "(vazia)";
    }

    static readonly HashSet<string> TextFiles = [".txt", ".md", ".py", ".json", ".csv", ".log", ".lua", ".luau", ".js", ".html", ".css", ".ini", ".cs", ".xml"];

    static string ReadFile(string path)
    {
        var file = InsideHome(path);
        if (!TextFiles.Contains(Path.GetExtension(file).ToLowerInvariant())) return "Tipo de arquivo não suportado para leitura.";
        var text = File.ReadAllText(file);
        return text.Length > 6000 ? text[..6000] : text;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, Load;
        public ulong Total, Free, PageTotal, PageFree, VirtualTotal, VirtualFree, ExtendedFree;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    static string PcStatus()
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref m);
        var disk = new DriveInfo("C");
        const double Gb = 1024.0 * 1024 * 1024;
        return $"RAM: {m.Load}% usada ({(m.Total - m.Free) / Gb:0.0} de {m.Total / Gb:0.0} GB). " +
               $"Disco C: {disk.AvailableFreeSpace / Gb:0.0} GB livres de {disk.TotalSize / Gb:0} GB.";
    }

    /// <summary>Set by the island: applies volume to Spotify only (null = no Spotify session).</summary>
    public static Func<string, double, string?>? SpotifyVolume;

    static string Volume(string action, double percent)
    {
        if (SpotifyVolume?.Invoke(action, percent) is { } spotify) return spotify;
        if (action == "tirar_mudo") action = "mudo";
        byte key = action switch { "aumentar" => 0xAF, "diminuir" => 0xAE, "mudo" => 0xAD, _ => 0 };
        if (key == 0) return "Sem Spotify tocando, só consigo aumentar, diminuir ou pôr no mudo o volume do Windows.";
        for (var i = 0; i < (action == "mudo" ? 1 : 5); i++) Native.PressKey(key);
        return $"Volume: {action}.";
    }

    static async Task<string> Download(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 DynamicIsland-Jarvis");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    static async Task<string> SearchWikipedia(string query)
    {
        var lines = new List<string>();
        foreach (var language in new[] { "pt", "en" })
        {
            var json = JsonNode.Parse(await Download($"https://{language}.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=3&srsearch=" +
                                                     Uri.EscapeDataString(query)));
            foreach (var item in json?["query"]?["search"]?.AsArray() ?? [])
                lines.Add($"- {item?["title"]}: {WebUtility.HtmlDecode(Regex.Replace(item?["snippet"]?.ToString() ?? "", "<[^>]+>", "")).Trim()}");
            if (lines.Count > 0) break;
        }
        return lines.Count > 0 ? string.Join("\n", lines) : "Sem resultados. Sugira abrir uma pesquisa no navegador com abrir_site.";
    }

    static async Task<string> Weather(string city)
    {
        var json = JsonNode.Parse(await Download("https://wttr.in/" + Uri.EscapeDataString(city) + "?format=j1&lang=pt"))!;
        var now = json["current_condition"]![0]!;
        var description = (now["lang_pt"] ?? now["weatherDesc"])?[0]?["value"]?.ToString();
        var today = json["weather"]![0]!;
        return $"{description}, {now["temp_C"]}°C (sensação {now["FeelsLikeC"]}°C), umidade {now["humidity"]}%. " +
               $"Hoje: mín {today["mintempC"]}°C, máx {today["maxtempC"]}°C.";
    }

    string Reminder(double minutes, string text)
    {
        minutes = Math.Clamp(minutes, 0.1, 24 * 60);
        OnHost(() => _host.Remind(minutes, text), "");
        return $"Lembrete agendado para daqui a {minutes.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR"))} minuto(s).";
    }

    string Remember(string fact)
    {
        fact = Regex.Replace(fact, @"\s+", " ").Trim();
        if (fact.Length > 300) fact = fact[..300];
        if (fact.Length > 0 && !_facts.Contains(fact))
        {
            _facts.Add(fact);
            if (_facts.Count > MaxFacts) _facts.RemoveRange(0, _facts.Count - MaxFacts);
            SaveMemory();
            Did($"guardou na memória \"{Short(fact)}\"", () =>
            {
                _facts.Remove(fact);
                SaveMemory();
                return $"Esqueci \"{Short(fact)}\".";
            });
        }
        return $"Guardado para sempre: {fact}";
    }

    string Forget(string part)
    {
        part = part.Trim();
        var before = _facts.Count;
        _facts.RemoveAll(f => f.Contains(part, StringComparison.OrdinalIgnoreCase));
        SaveMemory();
        return $"Apaguei {before - _facts.Count} lembrança(s).";
    }

    async Task<string> RunScriptAsync(string code, string description)
    {
        if (code.Trim().Length == 0) return "Script vazio.";
        var allowed = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _host.ConfirmScriptAsync(description, code)).Task.Unwrap();
        if (!allowed) return "O usuário NÃO autorizou a execução. Nada foi executado.";
        var start = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = Home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                     "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + code })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            return "O script passou de 90 s e foi interrompido.";
        }
        var o = (await output).Trim();
        var e = (await error).Trim();
        return $"Código de saída: {process.ExitCode}\n" + (o.Length > 0 ? $"Saída:\n{(o.Length > 3000 ? o[^3000..] : o)}\n" : "Sem saída.\n") +
               (e.Length > 0 ? $"Erros:\n{(e.Length > 1500 ? e[^1500..] : e)}" : "");
    }
}
