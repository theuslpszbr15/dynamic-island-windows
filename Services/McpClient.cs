using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

/// <summary>A plugin: an MCP server started on this PC (stdio), e.g. Playwright's browser.</summary>
public sealed class McpPlugin
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Enabled { get; set; }

    public static McpPlugin Playwright() => new()
    {
        Name = "navegador", Command = "npx -y @playwright/mcp@latest --browser msedge", Enabled = false,
    };
}

/// <summary>
/// Talks to one MCP server over stdio (JSON-RPC, one message per line): starts it, lists its tools and calls them.
/// The server runs as this user, so only commands the person typed in the settings are started.
/// </summary>
public sealed partial class McpClient : IDisposable
{
    readonly McpPlugin _plugin;
    readonly SemaphoreSlim _gate = new(1, 1);
    Process? _process;
    int _id;
    List<JsonObject> _tools = [];

    public McpClient(McpPlugin plugin) => _plugin = plugin;

    public string Name => _plugin.Name;
    public IReadOnlyList<JsonObject> Tools => _tools;
    public string? Problem { get; private set; }
    public bool Running => _process is { HasExited: false };

    /// <summary>Starts the server and reads its tools; false (with <see cref="Problem"/>) when it doesn't answer.</summary>
    public async Task<bool> StartAsync(CancellationToken cancel = default)
    {
        if (Running && _tools.Count > 0) return true;
        Stop();
        try
        {
            // npx and friends are .cmd files: cmd.exe finds and runs them like a terminal would
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(_plugin.Command);
            _process = Process.Start(start) ?? throw new InvalidOperationException("não abriu");
            _process.ErrorDataReceived += (_, _) => { }; // the server's own log: drained, not kept
            _process.BeginErrorReadLine();

            using var first = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            // the first run of npx downloads the package
            first.CancelAfter(TimeSpan.FromMinutes(2));
            await RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "DynamicIsland", ["version"] = "1.9" },
            }, first.Token);
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            var list = await RequestAsync("tools/list", new JsonObject(), first.Token);
            _tools = list?["tools"]?.AsArray().OfType<JsonObject>().Select(t => (JsonObject)t.DeepClone()).ToList() ?? [];
            Problem = _tools.Count == 0 ? "O plugin não ofereceu nenhuma ferramenta." : null;
            return _tools.Count > 0;
        }
        catch (Exception ex)
        {
            Problem = ex is OperationCanceledException ? "O plugin não respondeu (o comando está certo? O Node está instalado?)" : "O plugin não abriu: " + ex.Message;
            Log.Warn("MCP " + _plugin.Name, ex.Message);
            Stop();
            return false;
        }
    }

    /// <summary>Calls one tool; the text it returned (images are summarized), or the error in words.</summary>
    public async Task<string> CallAsync(string tool, JsonObject arguments, CancellationToken cancel = default)
    {
        if (!Running && !await StartAsync(cancel)) return Problem ?? "Plugin parado.";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var result = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() }, limit.Token);
            var parts = new List<string>();
            foreach (var item in result?["content"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var type = item["type"]?.GetValue<string>();
                if (type == "text") parts.Add(item["text"]?.GetValue<string>() ?? "");
                else if (type == "image") parts.Add("[imagem]");
                else if (type == "resource") parts.Add(item["resource"]?["text"]?.GetValue<string>() ?? "[arquivo]");
            }
            var text = string.Join("\n", parts).Trim();
            if (result?["isError"]?.GetValue<bool>() == true) text = "Erro: " + text;
            return text.Length == 0 ? "Feito." : text;
        }
        catch (OperationCanceledException) { return "A ferramenta demorou demais."; }
        catch (Exception ex) { return "A ferramenta falhou: " + ex.Message; }
    }

    async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            var id = ++_id;
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            while (true)
            {
                var line = await _process!.StandardOutput.ReadLineAsync(cancel) ?? throw new IOException("o plugin fechou");
                if (line.Length == 0 || line[0] != '{') continue; // a server printing logs to stdout
                if (JsonNode.Parse(line) is not JsonObject message) continue;
                // the server asking something (ping, roots): answered empty, never left waiting
                if (message["method"] is not null && message["id"] is { } requestId)
                {
                    await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone(), ["result"] = new JsonObject() });
                    continue;
                }
                if (message["id"]?.GetValue<int>() != id) continue; // a notification
                if (message["error"] is JsonObject error) throw new InvalidOperationException(error["message"]?.GetValue<string>() ?? "erro");
                return message["result"];
            }
        }
        finally { _gate.Release(); }
    }

    async Task SendAsync(JsonObject message)
    {
        var input = _process!.StandardInput;
        await input.WriteLineAsync(message.ToJsonString());
        await input.FlushAsync();
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NotAllowed();

    /// <summary>The name the model sees for one of this plugin's tools (letters, digits, _ and -, at most 64).</summary>
    public string ToolName(string tool)
    {
        var name = $"p_{NotAllowed().Replace(_plugin.Name, "")}_{NotAllowed().Replace(tool, "_")}";
        return name.Length > 64 ? name[..64] : name;
    }

    public void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _process = null;
        _tools = [];
    }

    public void Dispose() => Stop();
}
