using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicIsland.Services;

/// <summary>
/// Command-line entry points used by AI agents and scripts:
///   DynamicIsland.exe hook claude|codex|gemini|antigravity|opencode|copilot [Event]   (a hook payload on stdin)
///   DynamicIsland.exe agent --name X --state working|waiting|done|idle|end [--text T] [--session S] [--cwd C]
/// Writes to stdout only what each agent reads back (a decision the person made on the island, or the empty answer
/// Gemini CLI and Antigravity expect); always exits 0, so it can't block or break the agent.
/// </summary>
public static class AgentCli
{
    /// <summary>Agents that read Allow / Deny back from the island.</summary>
    static readonly HashSet<string> Approvers = ["claude", "codex", "opencode", "antigravity"];

    public static int Run(string[] args)
    {
        try
        {
            if (args[0] != "hook")
            {
                if (FromArgs(args) is { } message) Send(message);
                return 0;
            }
            var agent = args.Length > 1 ? args[1].ToLowerInvariant() : "claude";
            var payload = ReadStdin();
            if (payload == null) return 0;
            var claudeLike = ToClaudePayload(payload, agent, args.Length > 2 ? args[2] : null, AntigravityApprovals());
            var hookEvent = claudeLike == null ? null : JsonNode.Parse(claudeLike)?["hook_event_name"]?.GetValue<string>();
            var built = claudeLike == null ? null : FromHookPayload(claudeLike, agent);
            string? decision = null;
            if (built?.RequestId != null) decision = AwaitDecision(built);
            else if (built != null) Send(built);
            if (Reply(agent, hookEvent, decision) is { } reply)
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(Encoding.UTF8.GetBytes(reply));
            }
        }
        catch { }
        return 0;
    }

    // Shows Allow/Deny on the island and returns the click ("allow" / "deny"). No answer (or no island) = null, so
    // the agent falls back to its own prompt in the terminal.
    static string? AwaitDecision(AgentMessage message)
    {
        using var server = AgentHub.CreateOwnPipe(AgentHub.ReplyPipe(message.RequestId!), 1);
        if (!Send(message)) return null;
        if (!server.WaitForConnectionAsync().Wait(ApprovalTiming.DecisionWait)) return null;
        using var reader = new StreamReader(server, Encoding.UTF8);
        var decision = reader.ReadToEnd().Trim();
        return decision is "allow" or "deny" ? decision : null;
    }

    /// <summary>What the agent reads on stdout: a decision in its own format, the empty answer it expects, or nothing.</summary>
    internal static string? Reply(string agent, string? hookEvent, string? decision)
    {
        switch (agent)
        {
            case "antigravity":
                // Antigravity waits for a verdict before every tool: "ask" leaves it to its own rules and prompt
                if (hookEvent is "PreToolUse" or "PermissionRequest")
                    return decision switch
                    {
                        "allow" => """{"decision":"allow"}""",
                        "deny" => """{"decision":"deny","reason":"Negado na Dynamic Island."}""",
                        _ => """{"decision":"ask"}""",
                    };
                return "{}";
            case "gemini":
                return "{}";
        }
        if (decision == null) return null;
        var answer = new JsonObject { ["behavior"] = decision };
        if (decision == "deny") answer["message"] = "Negado na Dynamic Island.";
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = answer },
        }.ToJsonString();
    }

    /// <summary>Antigravity's commands wait for the island only when the person turned that on.</summary>
    static bool AntigravityApprovals()
    {
        try
        {
            var file = System.IO.Path.Combine(IslandSettings.Folder, "settings.json");
            return File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["AntigravityApprovals"]?.GetValue<bool>() == true;
        }
        catch { return false; }
    }

    /// <summary>Another agent's hook payload in Claude Code's terms (event and field names), so one reader handles them
    /// all; null when the event is nothing the island shows.</summary>
    internal static string? ToClaudePayload(string payload, string agent, string? eventArg, bool antigravityApprovals)
    {
        if (JsonNode.Parse(payload) is not JsonObject root) return null;
        var name = eventArg ?? root["hook_event_name"]?.GetValue<string>();
        switch (agent)
        {
            case "gemini":
                name = name switch
                {
                    "BeforeTool" => "PreToolUse",
                    "AfterTool" => "PostToolUse",
                    "BeforeAgent" => "UserPromptSubmit",
                    "AfterAgent" => "Stop",
                    _ => name,
                };
                if (name == "Notification" && root["notification_type"]?.GetValue<string>() == "ToolPermission")
                    root["notification_type"] = "permission_prompt";
                break;
            case "antigravity":
                name = name switch
                {
                    "PreInvocation" => "UserPromptSubmit",
                    "PostInvocation" => null,
                    _ => name,
                };
                if (root["toolCall"] is JsonObject call)
                {
                    root["tool_name"] = call["name"]?.DeepClone();
                    var input = call["args"] as JsonObject ?? new JsonObject();
                    input = (JsonObject)input.DeepClone();
                    if (input["CommandLine"] is { } line && input["command"] == null) input["command"] = line.DeepClone();
                    root["tool_input"] = input;
                }
                if (root["session_id"] == null && root["conversationId"] is { } conversation) root["session_id"] = conversation.DeepClone();
                // a shell command can wait for Allow / Deny on the island
                if (name == "PreToolUse" && antigravityApprovals && root["tool_name"]?.GetValue<string>() == "run_command")
                    name = "PermissionRequest";
                break;
        }
        if (name == null) return null;
        root["hook_event_name"] = name;
        return root.ToJsonString();
    }

    static string? ReadStdin()
    {
        var read = Task.Run(() =>
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
        return read.Wait(TimeSpan.FromSeconds(2)) && !string.IsNullOrWhiteSpace(read.Result) ? read.Result : null;
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

    /// <summary>The hook payload (the JSON a CLI prints on stdin) as an island message; null when it asks for
    /// nothing the island shows. Throws JsonException on a broken payload (the caller stays silent).</summary>
    internal static AgentMessage? FromHookPayload(string payload, string agent)
    {
        using var doc = JsonDocument.Parse(payload);
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

        // Only the agents that read a decision back get Allow / Deny.
        var requestId = hookEvent == "PermissionRequest" && Approvers.Contains(agent.ToLowerInvariant())
            ? Guid.NewGuid().ToString("N") : null;
        var command = requestId == null ? null
            : FullCommand(Str(root, "tool_name"), root.TryGetProperty("tool_input", out var full) ? full : default);
        return new AgentMessage(DisplayName(agent), Str(root, "session_id"), state, Clip(detail), Str(root, "cwd"), step, file, diff,
            requestId, command);
    }

    // What exactly is being allowed, for the phone (the island shows the one-line version).
    internal static string FullCommand(string? tool, JsonElement input)
    {
        string? Arg(string name) => input.ValueKind == JsonValueKind.Object ? Str(input, name) : null;
        var text = CommandText(input) is { } shell ? "$ " + shell
            : (Arg("file_path") ?? Arg("filePath") ?? Arg("path") ?? Arg("absolute_path")) is { } path ? $"{tool}: {path}"
            : DescribeTool(tool, input);
        return text.Length > 4000 ? text[..4000] : text;
    }

    // What kind of step a tool is, which file it touches and, for edits, a short "-old / +new" diff.
    internal static (string? Step, string? File, string? Diff) Inspect(string? tool, JsonElement input)
    {
        string? Arg(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object ? Str(e, name) : null;
        var path = Arg(input, "file_path") ?? Arg(input, "filePath") ?? Arg(input, "path") ?? Arg(input, "notebook_path") ?? Arg(input, "absolute_path");
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
            "Bash" or "PowerShell" or "bash" or "powershell" or "run_in_terminal" or "send_to_terminal"
                or "shell" or "run_shell_command" or "run_command" => "run",
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "edit" or "create" or "apply_patch" or "write" or "write_file" or "replace"
                or "replace_string_in_file" or "multi_replace_string_in_file" or "create_file" or "edit_notebook_file" => "edit",
            "Read" or "view" or "read_file" or "read" or "read_many_files" or "view_file" or "list_dir" => "read",
            "Grep" or "grep" or "rg" or "grep_search" or "Glob" or "glob" or "file_search" or "semantic_search" or "search_file_content" => "search",
            "WebFetch" or "web_fetch" or "webfetch" or "fetch_webpage" or "WebSearch" or "web_search" or "google_web_search" => "web",
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
    internal static string? Diff(string before, string after)
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
        "antigravity" => "Antigravity",
        "opencode" => "OpenCode",
        "cursor" => "Cursor",
        _ => agent,
    };

    internal static string DescribeTool(string? tool, JsonElement input)
    {
        string? Arg(string name) => input.ValueKind == JsonValueKind.Object ? Str(input, name) : null;
        string File(string? path) => string.IsNullOrEmpty(path) ? "arquivo" : System.IO.Path.GetFileName(path);
        string? Path() => Arg("file_path") ?? Arg("filePath") ?? Arg("path") ?? Arg("notebook_path") ?? Arg("absolute_path");
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
                or "shell" or "run_shell_command" or "run_command"
                => "$ " + (CommandText(input)?.Split('\n')[0] ?? ""),
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "edit" or "create" or "apply_patch" or "write" or "write_file" or "replace"
                or "replace_string_in_file" or "multi_replace_string_in_file" or "create_file" or "edit_notebook_file"
                => "Editando " + File(FirstFile()),
            "Read" or "view" or "read_file" or "read" or "view_file" => "Lendo " + File(Path()),
            "read_many_files" => "Lendo arquivos",
            "list_dir" => "Abrindo pasta " + File(Path()?.TrimEnd('\\', '/')),
            "Grep" or "grep" or "rg" or "grep_search" or "search_file_content" => $"Buscando “{Arg("pattern") ?? Arg("query")}”",
            "Glob" or "glob" or "file_search" => "Procurando " + (Arg("pattern") ?? Arg("query")),
            "semantic_search" => $"Pesquisando no código “{Arg("query")}”",
            "WebFetch" or "web_fetch" or "webfetch" or "fetch_webpage" => "Lendo página na web",
            "WebSearch" or "web_search" or "google_web_search" => "Pesquisando " + Arg("query"),
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

    /// <summary>The shell command, written as text or (Codex) as an argument list.</summary>
    static string? CommandText(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("command", out var command)) return null;
        if (command.ValueKind == JsonValueKind.String) return command.GetString();
        if (command.ValueKind != JsonValueKind.Array) return null;
        var parts = command.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToList();
        // ["bash", "-lc", "npm test"]: the script is what matters
        if (parts.Count == 3 && parts[1] is "-lc" or "-c" or "/c" or "-Command") return parts[2];
        return string.Join(' ', parts);
    }

    static string? Clip(string? text)
    {
        if (text == null) return null;
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > 120 ? text[..119] + "…" : text;
    }

    static bool Send(AgentMessage message)
    {
        using var client = AgentHub.ConnectOwnPipe(AgentHub.PipeName, 400);
        if (client == null) return false; // island not running
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web);
        client.Write(bytes);
        client.Flush();
        return true;
    }
}
