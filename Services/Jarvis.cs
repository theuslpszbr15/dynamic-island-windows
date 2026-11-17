using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

/// <summary>
/// JARVIS (github.com/theuslpszbr15/JARVIS) inside the island: its quick commands and the same local engines
/// (llama.cpp with Qwen 2.5 1.5B, the Piper pt-BR voice "faber", whisper.cpp base), without the Electron app.
/// Engines the JARVIS app already downloaded are reused; otherwise the island downloads its own, every file checked
/// against a pinned SHA-256 before anything runs. Nothing leaves the PC once they are here.
/// </summary>
public static class Jarvis
{
    public enum Engine { Ai, Voice, Microphone }

    public sealed record Reply(string Text, string? ActionLabel = null, string? ActionUrl = null);

    sealed record FileSpec(string Url, string Name, string Sha256);
    sealed record Package(string Folder, string Label, string Size, FileSpec Archive, string Executable, FileSpec[] Files);

    // Hashes: GitHub's release digests, Hugging Face's LFS sha256 (Piper's zip and the voice's json hashed by hand).
    static readonly Dictionary<Engine, Package> Packages = new()
    {
        [Engine.Ai] = new("ai", "IA offline (Qwen 2.5 1.5B)", "1,1 GB",
            new("https://github.com/ggml-org/llama.cpp/releases/download/b10819/llama-b10819-bin-win-cpu-x64.zip", "engine.zip",
                "4599e502b374196d24600ea9b03c842a448c853116a15b55e8ba502bdc727b3f"),
            // llama-cli became chat-only (it rejects -no-cnv, as JARVIS still passes it); llama-completion takes a raw prompt
            "llama-completion.exe",
            [new("https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf", "model.gguf",
                "6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e")]),
        [Engine.Voice] = new("voice", "Voz (Piper, pt-BR)", "85 MB",
            new("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip", "engine.zip",
                "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea"),
            "piper.exe",
            [
                // pinned before the 2026-11-12 retrain for Piper 1.5, whose two-letter phonemes this Windows Piper can't read
                new("https://huggingface.co/rhasspy/piper-voices/resolve/ae9d8352b73c1b1750748843e753a504b00e05b0/pt/pt_BR/faber/medium/pt_BR-faber-medium.onnx", "voice.onnx",
                    "858555e3a064209c57088fe6bd70c4c3dc54d03eaa00c45d5ecaf43a33f95aa7"),
                new("https://huggingface.co/rhasspy/piper-voices/resolve/ae9d8352b73c1b1750748843e753a504b00e05b0/pt/pt_BR/faber/medium/pt_BR-faber-medium.onnx.json", "voice.onnx.json",
                    "7e694de195ae3fc36dd732c445eb04fb49b649854893cb5506b978f0d50a1d6f"),
            ]),
        [Engine.Microphone] = new("microphone", "Microfone (whisper.cpp)", "156 MB",
            new("https://github.com/ggml-org/whisper.cpp/releases/download/b4938/whisper-bin-x64.zip", "engine.zip",
                "c2a4b60edb11f7e11a9191ffb50929535527d4d91c9903dbe3e554583bbbc63d"),
            "whisper-cli.exe",
            [new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin", "model.bin",
                "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe")]),
    };

    public static string Label(Engine engine) => Packages[engine].Label;
    public static string Size(Engine engine) => Packages[engine].Size;

    static string OwnRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "jarvis");

    // The JARVIS app keeps its engines in Electron's userData (packaged "JARVIS", from source "jarvis-local").
    static IEnumerable<string> Candidates(Engine engine)
    {
        var folder = Packages[engine].Folder;
        yield return Path.Combine(OwnRoot, folder);
        if (engine == Engine.Voice) yield break;
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(roaming, "JARVIS", "engines", folder);
        yield return Path.Combine(roaming, "jarvis-local", "engines", folder);
    }

    /// <summary>The folder of a ready engine, or null when it still has to be downloaded.</summary>
    public static string? Folder(Engine engine) => Candidates(engine).FirstOrDefault(d => Ready(engine, d));

    /// <summary>True when the engine comes from the JARVIS app's own downloads.</summary>
    public static bool FromJarvisApp(Engine engine) =>
        Folder(engine) is { } dir && !dir.StartsWith(OwnRoot, StringComparison.OrdinalIgnoreCase);

    static bool Ready(Engine engine, string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, "ready.json"))
                   && Packages[engine].Files.All(f => File.Exists(Path.Combine(dir, f.Name)))
                   && Executable(engine, dir) != null;
        }
        catch { return false; }
    }

    static string? Executable(Engine engine, string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var name = Packages[engine].Executable;
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault()
               ?? (engine == Engine.Microphone ? Directory.EnumerateFiles(dir, "main.exe", SearchOption.AllDirectories).FirstOrDefault() : null);
    }

    // ───────────────────────── downloads ─────────────────────────

    /// <summary>"Baixando… 120 / 1065 MB" while an engine downloads; read by the settings page.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<Engine, string> Progress = new();

    /// <summary>Raised (on any thread) when a download moves or ends.</summary>
    public static event Action? Changed;

    static readonly HttpClient Http = new(new HttpClientHandler { DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    static readonly SemaphoreSlim Installing = new(1, 1);

    /// <summary>Downloads, checks and unpacks one engine into the island's folder. Null when done, otherwise why not.</summary>
    public static async Task<string?> InstallAsync(Engine engine)
    {
        if (Folder(engine) != null) return null;
        if (!await Installing.WaitAsync(0)) return "Espere o download atual terminar.";
        var package = Packages[engine];
        var target = Path.Combine(OwnRoot, package.Folder);
        var staging = target + ".download";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            var archive = Path.Combine(staging, package.Archive.Name);
            await DownloadAsync(engine, package.Archive, archive);
            Report(engine, "Extraindo…");
            await Task.Run(() => ZipFile.ExtractToDirectory(archive, Path.Combine(staging, "bin")));
            File.Delete(archive);
            if (Executable(engine, staging) == null) throw new InvalidDataException($"{package.Executable} não veio no pacote.");
            foreach (var file in package.Files) await DownloadAsync(engine, file, Path.Combine(staging, file.Name));
            await File.WriteAllTextAsync(Path.Combine(staging, "ready.json"), $"{{\"kind\":\"{package.Folder}\"}}");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Jarvis install {engine}", ex);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            return ex is HttpRequestException ? "Sem internet ou o servidor não respondeu. Tente de novo." : ex.Message;
        }
        finally
        {
            Progress.TryRemove(engine, out _);
            Installing.Release();
            Changed?.Invoke();
        }
    }

    static async Task DownloadAsync(Engine engine, FileSpec file, string path)
    {
        using var response = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync())
        await using (var output = File.Create(path))
        {
            var buffer = new byte[1 << 16];
            long received = 0, shown = -1;
            int n;
            while ((n = await input.ReadAsync(buffer)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n));
                received += n;
                var mb = received >> 20;
                if (mb - shown >= 5)
                {
                    shown = mb;
                    Report(engine, total > 0 ? $"Baixando… {mb} / {total >> 20} MB" : $"Baixando… {mb} MB");
                }
            }
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("O arquivo baixado não confere (SHA-256). Nada foi instalado.");
        }
    }

    static void Report(Engine engine, string text)
    {
        Progress[engine] = text;
        Changed?.Invoke();
    }

    // ───────────────────────── answering ─────────────────────────

    const string SystemPrompt = "Você é JARVIS, um assistente pessoal que vive na Dynamic Island do Windows. Responda em português " +
                                "brasileiro de forma curta, educada e precisa. Não finja acesso à internet ou ao computador: " +
                                "nunca diga que abriu, fechou ou fez algo no PC. Não use markdown.";

    static readonly SemaphoreSlim Thinking = new(1, 1);
    static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>A quick command (time, date, sums, searches) or the offline AI. Never throws: errors come back as text.</summary>
    public static async Task<Reply> AskAsync(string message, IReadOnlyList<(bool User, string Text)> history)
    {
        message = message.Trim();
        if (message.Length is 0 or > 2000) return new("Escreva uma pergunta de até 2000 caracteres.");
        if (LocalReply(message, DateTime.Now) is { } local) return local;
        if (OpenRequest(message) is { } request)
        {
            if (FindApp(request) is not { } app) return new($"Não achei \"{request}\" neste PC.");
            try
            {
                Process.Start(new ProcessStartInfo(app.Target) { UseShellExecute = true });
                return new($"Abrindo {app.Name}.");
            }
            catch (Exception ex)
            {
                Log.Error("Jarvis open", ex);
                return new($"Não consegui abrir {app.Name}.");
            }
        }
        if (Folder(Engine.Ai) is not { } dir)
            return new("A IA offline ainda não foi baixada: Configurações › Jarvis › Baixar. Hora, data, contas e pesquisas já funcionam.");
        if (!await Thinking.WaitAsync(0)) return new("Ainda estou pensando na pergunta anterior.");
        var promptFile = Path.Combine(Path.GetTempPath(), $"jarvis-prompt-{Environment.ProcessId}.txt");
        try
        {
            static string Clean(string s)
            {
                var c = Regex.Replace(s, @"<\|[^>]*\|>", "").Trim();
                return c.Length > 2000 ? c[..2000] : c;
            }
            var prompt = new StringBuilder($"<|im_start|>system\n{SystemPrompt}\n<|im_end|>\n");
            foreach (var (user, text) in history.TakeLast(4))
                prompt.Append($"<|im_start|>{(user ? "user" : "assistant")}\n{Clean(text)}<|im_end|>\n");
            prompt.Append($"<|im_start|>user\n{Clean(message)}<|im_end|>\n<|im_start|>assistant\n");
            await File.WriteAllTextAsync(promptFile, prompt.ToString(), new UTF8Encoding(false));
            var output = await RunAsync(Executable(Engine.Ai, dir)!,
                ["-m", Path.Combine(dir, "model.gguf"), "-f", promptFile, "-n", "220", "-c", "4096", "-t", Math.Clamp(Environment.ProcessorCount / 2, 2, 8).ToString(),
                 "-ngl", "0", "-no-cnv", "--no-display-prompt", "--no-perf", "--no-warmup", "-r", "<|im_end|>"],
                timeout: TimeSpan.FromMinutes(2));
            var answer = Regex.Replace(Regex.Replace(output, @"\x1b\[[0-9;]*m", ""), @"<\|[^>]*\|>|\[end of text\]", "").Trim();
            return new(answer.Length > 0 ? answer : "A IA não respondeu. Tente perguntar de outro jeito.");
        }
        catch (Exception ex)
        {
            Log.Error("Jarvis ask", ex);
            return new(ex is TimeoutException ? "Demorei demais para pensar. Tente uma pergunta menor." : "A IA offline falhou: " + ex.Message);
        }
        finally
        {
            try { File.Delete(promptFile); } catch { }
            Thinking.Release();
        }
    }

    /// <summary>JARVIS's quick commands (lib/commands.mjs), answered without the AI.</summary>
    public static Reply? LocalReply(string input, DateTime now)
    {
        var text = Plain(input.Trim());
        if (Regex.IsMatch(text, @"\b(horas|hora certa|que hora)\b")) return new($"Agora são {now:HH:mm}.");
        if (Regex.IsMatch(text, @"\b(data|que dia|dia de hoje)\b")) return new($"Hoje é {now.ToString("dddd, d 'de' MMMM 'de' yyyy", PtBr)}.");
        var calc = Regex.Match(text, @"^(?:calcule|quanto e)\s+(-?\d+(?:[.,]\d+)?)\s*(\+|-|\*|/|x|mais|menos|vezes|dividido por)\s*(-?\d+(?:[.,]\d+)?)[?.!]?$");
        if (calc.Success)
        {
            var left = double.Parse(calc.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var right = double.Parse(calc.Groups[3].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var op = calc.Groups[2].Value;
            if (op is "/" or "dividido por" && right == 0) return new("Não é possível dividir por zero.");
            var result = op switch
            {
                "+" or "mais" => left + right,
                "-" or "menos" => left - right,
                "*" or "x" or "vezes" => left * right,
                _ => left / right,
            };
            return new(double.IsFinite(result) ? $"O resultado é {result.ToString("#,0.########", PtBr)}." : "Esse cálculo passa do limite.");
        }
        var search = Regex.Match(input.Trim(), @"^(?:pesquise|pesquisar|busque|buscar)\s+(.+)$", RegexOptions.IgnoreCase);
        if (search.Success)
        {
            var query = search.Groups[1].Value.Trim();
            return new($"Pesquisando {query} no Google.", "Abrir pesquisa", "https://www.google.com/search?q=" + Uri.EscapeDataString(query));
        }
        var site = Regex.Match(text, @"^(?:abra|abrir|abre)\s+(?:o\s+)?(youtube|google)[.!]?$");
        if (site.Success)
        {
            var youtube = site.Groups[1].Value == "youtube";
            var name = youtube ? "YouTube" : "Google";
            return new($"Abrindo o {name}.", $"Abrir {name}", youtube ? "https://www.youtube.com/" : "https://www.google.com/");
        }
        if (Regex.IsMatch(text, @"^(oi|ola|bom dia|boa tarde|boa noite|jarvis)[!.,\s]*$")) return new("À disposição. Em que posso ajudar?");
        if (Regex.IsMatch(text, @"\b(ajuda|comandos|o que voce faz)\b"))
            return new("Posso dizer a hora e a data, fazer contas, preparar pesquisas e abrir o Google ou o YouTube. " +
                       "Com a IA offline baixada, também converso sobre outros assuntos.");
        return null;
    }

    /// <summary>Lowercase, without accents ("Olá" → "ola").</summary>
    static string Plain(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // ───────────────────────── opening apps ─────────────────────────

    public sealed record App(string Name, string Target);

    // Windows' own apps by the names people say; anything else must be a shortcut in the Start menu.
    static readonly (string[] Says, App App)[] KnownApps =
    [
        (["calculadora", "calculator"], new("a Calculadora", "calc.exe")),
        (["bloco de notas", "notepad", "bloco de nota"], new("o Bloco de Notas", "notepad.exe")),
        (["paint"], new("o Paint", "mspaint.exe")),
        (["explorador de arquivos", "explorer", "meus arquivos", "arquivos"], new("o Explorador de Arquivos", "explorer.exe")),
        (["configuracoes", "configuracao", "ajustes do windows"], new("as Configurações", "ms-settings:")),
        (["gerenciador de tarefas", "task manager"], new("o Gerenciador de Tarefas", "taskmgr.exe")),
        (["terminal", "prompt de comando", "cmd", "powershell"], new("o Terminal", "wt.exe")),
        (["camera"], new("a Câmera", "microsoft.windows.camera:")),
        (["relogio", "alarme", "alarmes", "cronometro"], new("o Relógio", "ms-clock:")),
        (["loja", "microsoft store"], new("a Microsoft Store", "ms-windows-store:")),
        (["ferramenta de captura", "captura de tela", "recorte"], new("a Ferramenta de Captura", "ms-screenclip:")),
    ];

    static readonly Regex OpenVerb = new(
        @"^(?:jarvis[,!]?\s+)?(?:por favor\s+)?(?:abre|abra|abrir|abri|inicia|inicie|iniciar|executa|execute|executar|open)\s+(?:(?:o|a|os|as|meu|minha|um|uma)\s+)?(.+?)[\s.!?]*$");

    /// <summary>What to open in "abre a calculadora" (or just "calculadora", when that is an app), else null.</summary>
    public static string? OpenRequest(string input)
    {
        var text = Plain(input.Trim());
        var verb = OpenVerb.Match(text);
        if (verb.Success) return verb.Groups[1].Value.Trim();
        var bare = Regex.Replace(text, @"[\s.!?]+$", "");
        return bare.Length > 0 && bare.Split(' ').Length <= 3 && FindApp(bare, loose: false) != null ? bare : null;
    }

    /// <summary>A Windows app it knows by name, or a Start menu shortcut whose name matches (exactly unless
    /// <paramref name="loose"/>). Never a path from the text.</summary>
    public static App? FindApp(string name, bool loose = true)
    {
        name = Plain(name.Trim());
        if (name.Length < 2) return null;
        foreach (var (says, app) in KnownApps)
            if (says.Contains(name)) return app;
        var shortcuts = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.lnk", new EnumerationOptions
            {
                // localized junctions ("Programas") deny listing; the real folders are walked anyway
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            }))
            .Where(f => !Plain(Path.GetFileNameWithoutExtension(f)).Contains("uninstall") && !Plain(Path.GetFileNameWithoutExtension(f)).Contains("desinstalar"))
            .Select(f => (Path: f, Name: Plain(Path.GetFileNameWithoutExtension(f))))
            .ToList();
        var best = shortcuts.FirstOrDefault(s => s.Name == name);
        if (best.Path == null && loose) best = shortcuts.Where(s => s.Name.StartsWith(name)).OrderBy(s => s.Name.Length).FirstOrDefault();
        if (best.Path == null && loose && name.Length >= 4) best = shortcuts.Where(s => s.Name.Contains(name)).OrderBy(s => s.Name.Length).FirstOrDefault();
        return best.Path == null ? null : new App("o " + Path.GetFileNameWithoutExtension(best.Path), best.Path);
    }

    /// <summary>Only Google and YouTube over https, as in JARVIS.</summary>
    public static bool SafeLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host is "www.google.com" or "www.youtube.com";

    // ───────────────────────── voice ─────────────────────────

    static CancellationTokenSource? _speech;
    static System.Media.SoundPlayer? _player;

    /// <summary>Says the text with the Piper voice (stops what was being said). False without the voice engine.</summary>
    public static async Task<bool> SpeakAsync(string text)
    {
        if (Folder(Engine.Voice) is not { } dir || string.IsNullOrWhiteSpace(text)) return false;
        StopSpeaking();
        var cancel = _speech = new CancellationTokenSource();
        var wave = Path.Combine(Path.GetTempPath(), $"jarvis-voice-{Environment.ProcessId}-{Guid.NewGuid():N}.wav");
        try
        {
            await SynthesizeAsync(dir, text, wave, cancel.Token);
            if (cancel.IsCancellationRequested) return true;
            await Task.Run(() =>
            {
                using var player = new System.Media.SoundPlayer(wave);
                _player = player;
                using (cancel.Token.Register(player.Stop)) player.PlaySync();
                _player = null;
            });
            return true;
        }
        catch (OperationCanceledException) { return true; }
        catch (Exception ex)
        {
            Log.Error("Jarvis speak", ex);
            return false;
        }
        finally { try { File.Delete(wave); } catch { } }
    }

    internal static Task SynthesizeAsync(string dir, string text, string wave, CancellationToken cancel = default) =>
        RunAsync(Executable(Engine.Voice, dir)!,
            ["--model", Path.Combine(dir, "voice.onnx"), "--output_file", wave, "--length_scale", (1 / 0.95).ToString("0.00", CultureInfo.InvariantCulture)],
            input: text.Length > 4000 ? text[..4000] : text, timeout: TimeSpan.FromMinutes(1), cancel: cancel);

    public static void StopSpeaking()
    {
        _speech?.Cancel();
        try { _player?.Stop(); } catch { }
    }

    /// <summary>What was said in a 16 kHz mono WAV (whisper.cpp, Portuguese). Null without the microphone engine.</summary>
    public static async Task<string?> TranscribeAsync(string wave)
    {
        if (Folder(Engine.Microphone) is not { } dir) return null;
        try
        {
            await RunAsync(Executable(Engine.Microphone, dir)!,
                ["-m", Path.Combine(dir, "model.bin"), "-f", wave, "-l", "pt", "-nt", "-otxt", "-of", wave, "-t", "4"],
                timeout: TimeSpan.FromMinutes(1));
            var text = await File.ReadAllTextAsync(wave + ".txt");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }
        finally { try { File.Delete(wave + ".txt"); } catch { } }
    }

    static async Task<string> RunAsync(string exe, IEnumerable<string> args, string? input = null, TimeSpan? timeout = null,
        CancellationToken cancel = default)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("O motor não abriu.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancel);
        var stderr = process.StandardError.ReadToEndAsync(cancel);
        await process.StandardInput.WriteAsync((input ?? "") + (input != null ? "\n" : ""));
        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            if (cancel.IsCancellationRequested) throw;
            throw new TimeoutException();
        }
        var output = await stdout;
        if (process.ExitCode != 0)
        {
            var error = await stderr;
            throw new InvalidOperationException($"código {process.ExitCode}: {(error.Length > 300 ? error[^300..] : error)}");
        }
        return output;
    }
}
