using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicIsland.Services;

/// <summary>
/// Command-line entry points used by AI agents and scripts:
///   DynamicIsland.exe hook claude            (reads a Claude Code hook payload from stdin)
///   DynamicIsland.exe agent --name X --state working|waiting|done|idle|end [--text T] [--session S] [--cwd C]
/// Writes to stdout only to answer a Claude Code PermissionRequest that was decided on the island;
/// always exits 0, so it can't block or break the agent.
/// </summary>
public static class AgentCli
{
    static readonly TimeSpan DecisionWait = TimeSpan.FromSeconds(55);

    public static int Run(string[] args)
    {
        try
        {
            var message = args[0] == "hook" ? FromHook(args.Length > 1 ? args[1] : "claude") : FromArgs(args);
            if (message?.RequestId != null) AwaitDecision(message);
            else if (message != null) Send(message);
        }
        catch { }
        return 0;
    }

    // Shows Allow/Deny on the island and relays the click. No answer (or no island) = no output, so
    // Claude Code falls back to its own prompt in the terminal.
    static void AwaitDecision(AgentMessage message)
    {
        using var server = new NamedPipeServerStream(AgentHub.ReplyPipe(message.RequestId!), PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        if (!Send(message)) return;
        if (!server.WaitForConnectionAsync().Wait(DecisionWait)) return;
        using var reader = new StreamReader(server, Encoding.UTF8);
        var decision = reader.ReadToEnd().Trim();
        if (decision is not ("allow" or "deny")) return;

        var answer = new JsonObject { ["behavior"] = decision };
        if (decision == "deny") answer["message"] = "Negado na Dynamic Island.";
        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = answer },
        };
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(Encoding.UTF8.GetBytes(output.ToJsonString()));
    }

    static AgentMessage? FromArgs(string[] args)
    {
        string? Get(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var name = Get("--name");
        var state = Get("--state");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(state)) return null;
        return new AgentMessage(name, Get("--session"), state.ToLowerInvariant(), Clip(Get("--text")), Get("--cwd") ?? Environment.CurrentDirectory);
    }

    static AgentMessage? FromHook(string agent)
    {
        var read = Task.Run(() =>
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
        if (!read.Wait(TimeSpan.FromSeconds(2)) || string.IsNullOrWhiteSpace(read.Result)) return null;

        using var doc = JsonDocument.Parse(read.Result);
        var root = doc.RootElement;
        var hookEvent = Str(root, "hook_event_name");
        var notification = Str(root, "notification_type");
        var (state, detail) = hookEvent switch
        {
            "UserPromptSubmit" => ("working", "Pensando…"),
            "PreToolUse" or "PostToolUse" => ("working", DescribeTool(Str(root, "tool_name"), root.TryGetProperty("tool_input", out var input) ? input : default)),
            // Copilot also notifies shell/agent completions; only prompts for the user mean "waiting".
            "Notification" when notification is null or "permission_prompt" or "elicitation_dialog"
                => ("waiting", Str(root, "message") ?? "Precisa da sua atenção"),
            "PermissionRequest" => ("waiting", "Pedindo permissão: " + DescribeTool(Str(root, "tool_name"), root.TryGetProperty("tool_input", out var asked) ? asked : default)),
            "Stop" => ("done", "Concluído"),
            "SessionStart" => ("idle", "Sessão iniciada"),
            "SessionEnd" => ("end", null),
            _ => ((string?)null, (string?)null),
        };
        RecordLastHook(agent, hookEvent);
        if (state == null) return null;

        string? step = null, file = null, diff = null;
        if (hookEvent is "PreToolUse" or "PostToolUse")
        {
            var input = root.TryGetProperty("tool_input", out var i) ? i : default;
            (step, file, diff) = Inspect(Str(root, "tool_name"), input);
        }
        else if (hookEvent == "UserPromptSubmit") step = "think";

        // Only Claude Code reads a decision back from this hook.
        var requestId = hookEvent == "PermissionRequest" && agent.Equals("claude", StringComparison.OrdinalIgnoreCase)
            ? Guid.NewGuid().ToString("N") : null;
        return new AgentMessage(DisplayName(agent), Str(root, "session_id"), state, Clip(detail), Str(root, "cwd"), step, file, diff, requestId);
    }

    // What kind of step a tool is, which file it touches and, for edits, a short "-old / +new" diff.
    static (string? Step, string? File, string? Diff) Inspect(string? tool, JsonElement input)
    {
        string? Arg(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object ? Str(e, name) : null;
        var path = Arg(input, "file_path") ?? Arg(input, "filePath") ?? Arg(input, "path") ?? Arg(input, "notebook_path");
        var edit = input;
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("replacements", out var list)
            && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
        {
            edit = list[0];
            path ??= Arg(edit, "filePath");
        }
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("edits", out var edits)
            && edits.ValueKind == JsonValueKind.Array && edits.GetArrayLength() > 0)
            edit = edits[0];

        var step = tool switch
        {
            "Bash" or "PowerShell" or "bash" or "powershell" or "run_in_terminal" or "send_to_terminal" => "run",
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "edit" or "create" or "apply_patch"
                or "replace_string_in_file" or "multi_replace_string_in_file" or "create_file" or "edit_notebook_file" => "edit",
            "Read" or "view" or "read_file" or "list_dir" => "read",
            "Grep" or "grep" or "rg" or "grep_search" or "Glob" or "glob" or "file_search" or "semantic_search" => "search",
            "WebFetch" or "web_fetch" or "fetch_webpage" or "WebSearch" or "web_search" => "web",
            "Task" or "Agent" or "task" or "runSubagent" => "task",
            _ => "think",
        };

        string? diff = null;
        if (step == "edit")
        {
            var before = Arg(edit, "old_string") ?? Arg(edit, "oldString") ?? Arg(edit, "old_str") ?? "";
            var after = Arg(edit, "new_string") ?? Arg(edit, "newString") ?? Arg(edit, "new_str") ?? Arg(input, "content") ?? "";
            diff = Diff(before, after);
        }
        return (step, path, diff);
    }

    /// <summary>The changed lines only (common first/last lines dropped), at most 4 removed and 5 added.</summary>
    static string? Diff(string before, string after)
    {
        var a = before.ReplaceLineEndings("\n").Split('\n').ToList();
        var b = after.ReplaceLineEndings("\n").Split('\n').ToList();
        if (before.Length == 0) a.Clear();
        while (a.Count > 0 && b.Count > 0 && a[0] == b[0]) { a.RemoveAt(0); b.RemoveAt(0); }
        while (a.Count > 0 && b.Count > 0 && a[^1] == b[^1]) { a.RemoveAt(a.Count - 1); b.RemoveAt(b.Count - 1); }
        if (a.Count == 0 && b.Count == 0) return null;
        static string Line(char sign, string text) => sign + (text.Length > 90 ? text[..89] + "…" : text).TrimEnd();
        var lines = a.Take(4).Select(l => Line('-', l)).Concat(b.Take(5).Select(l => Line('+', l)));
        return string.Join('\n', lines);
    }

    // Event name + time only (no prompt/tool content), to check whether hooks are firing.
    static void RecordLastHook(string agent, string? hookEvent)
    {
        try
        {
            Directory.CreateDirectory(IslandSettings.Folder);
            File.WriteAllText(System.IO.Path.Combine(IslandSettings.Folder, "last-hook.txt"),
                $"{DateTime.Now:O} {agent} {hookEvent}");
        }
        catch { }
    }

    static string DisplayName(string agent) => agent.ToLowerInvariant() switch
    {
        "claude" => "Claude Code",
        "copilot" => "Copilot",
        "codex" => "Codex",
        "gemini" => "Gemini CLI",
        "cursor" => "Cursor",
        _ => agent,
    };

    static string DescribeTool(string? tool, JsonElement input)
    {
        string? Arg(string name) => input.ValueKind == JsonValueKind.Object ? Str(input, name) : null;
        string File(string? path) => string.IsNullOrEmpty(path) ? "arquivo" : System.IO.Path.GetFileName(path);
        string? Path() => Arg("file_path") ?? Arg("filePath") ?? Arg("path") ?? Arg("notebook_path");
        string? FirstFile()
        {
            if (Path() is { } single) return single;
            // multi_replace_string_in_file: { replacements: [{ filePath }] }
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("replacements", out var list)
                && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 && list[0].ValueKind == JsonValueKind.Object)
                return Str(list[0], "filePath");
            return null;
        }

        return tool switch
        {
            "Bash" or "PowerShell" or "bash" or "powershell" or "run_in_terminal" or "send_to_terminal"
                => "$ " + (Arg("command")?.Split('\n')[0] ?? ""),
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "edit" or "create" or "apply_patch"
                or "replace_string_in_file" or "multi_replace_string_in_file" or "create_file" or "edit_notebook_file"
                => "Editando " + File(FirstFile()),
            "Read" or "view" or "read_file" => "Lendo " + File(Path()),
            "list_dir" => "Abrindo pasta " + File(Path()?.TrimEnd('\\', '/')),
            "Grep" or "grep" or "rg" or "grep_search" => $"Buscando “{Arg("pattern") ?? Arg("query")}”",
            "Glob" or "glob" or "file_search" => "Procurando " + (Arg("pattern") ?? Arg("query")),
            "semantic_search" => $"Pesquisando no código “{Arg("query")}”",
            "WebFetch" or "web_fetch" or "fetch_webpage" => "Lendo página na web",
            "WebSearch" or "web_search" => "Pesquisando " + Arg("query"),
            "Task" or "Agent" or "task" or "runSubagent" => "Subagente: " + (Arg("description") ?? ""),
            "TodoWrite" or "update_todo" or "manage_todo_list" => "Atualizando tarefas",
            "get_errors" => "Verificando erros",
            null or "" => "Trabalhando…",
            _ when tool.StartsWith("mcp__") => tool.Split("__").Last(),
            _ => tool,
        };
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string? Clip(string? text)
    {
        if (text == null) return null;
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > 120 ? text[..119] + "…" : text;
    }

    static bool Send(AgentMessage message)
    {
        using var client = new NamedPipeClientStream(".", AgentHub.PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        try { client.Connect(400); }
        catch { return false; } // island not running
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web);
        client.Write(bytes);
        client.Flush();
        return true;
    }
}
