using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicIsland.Services;

/// <summary>
/// Connects the island to an AI coding agent by writing its hooks into the agent's own config (or, for OpenCode, a
/// plugin file). The previous file is kept as a dated backup, the person's own hooks are never touched, and removing
/// takes out only the island's entries (recognized by "DynamicIsland" + "hook &lt;agent&gt;" in the command).
/// </summary>
public static class AgentHooks
{
    public sealed record Agent(string Id, string Name, string File, string About);

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static readonly Agent[] All =
    [
        new("claude", "Claude Code", Path.Combine(Home, ".claude", "settings.json"),
            "Passos, diff e Permitir / Negar na ilha e no celular."),
        new("codex", "Codex CLI (OpenAI)", Path.Combine(Home, ".codex", "hooks.json"),
            "Passos e Permitir / Negar. No Codex, rode /hooks uma vez e confie nos hooks."),
        new("gemini", "Gemini CLI", Path.Combine(Home, ".gemini", "settings.json"),
            "Passos e quando ele pede sua permissão (a resposta é no terminal)."),
        new("antigravity", "Antigravity (agy)", Path.Combine(Home, ".gemini", "config", "hooks.json"),
            "Passos; com a opção abaixo, os comandos do terminal pedem Permitir / Negar na ilha."),
        new("opencode", "OpenCode", Path.Combine(Home, ".config", "opencode", "plugins", "dynamic-island.js"),
            "Passos e Permitir / Negar na ilha. Reinicie o OpenCode depois de ligar."),
    ];

    public static Agent Get(string id) => All.First(a => a.Id == id);

    // Seconds, except Gemini (milliseconds). Approvals wait for the island a little longer than it keeps the card.
    static readonly (string Event, int Timeout, bool Matcher)[] ClaudeEvents =
    [
        ("SessionStart", 10, false), ("UserPromptSubmit", 10, false), ("PreToolUse", 10, true), ("PostToolUse", 10, true),
        ("Notification", 10, false), ("PermissionRequest", ApprovalTiming.HookTimeoutSeconds, true), ("Stop", 10, false), ("SessionEnd", 10, false),
    ];
    static readonly (string Event, int Timeout)[] CodexEvents =
    [
        ("SessionStart", 10), ("UserPromptSubmit", 10), ("PreToolUse", 10), ("PostToolUse", 10),
        ("PermissionRequest", ApprovalTiming.HookTimeoutSeconds), ("Stop", 10), ("SessionEnd", 5),
    ];
    // AfterModel is left out: it fires on every streamed chunk.
    static readonly string[] GeminiEvents = ["SessionStart", "SessionEnd", "BeforeAgent", "AfterAgent", "BeforeTool", "AfterTool", "Notification"];
    static readonly string[] AntigravityToolEvents = ["PreToolUse", "PostToolUse"];
    static readonly string[] AntigravityTurnEvents = ["PreInvocation", "Stop"];
    const string AntigravityGroup = "dynamic-island";

    /// <summary>The island's entries are in the agent's config.</summary>
    public static bool IsInstalled(Agent agent)
    {
        try
        {
            if (!System.IO.File.Exists(agent.File)) return false;
            var text = System.IO.File.ReadAllText(agent.File);
            return agent.Id == "opencode" ? text.Contains("DynamicIsland") : text.Contains("DynamicIsland") && text.Contains("hook " + agent.Id);
        }
        catch { return false; }
    }

    /// <summary>Writes (or removes) the island's hooks; the problem, or null when it worked.</summary>
    public static string? Apply(Agent agent, bool install, string exe)
    {
        try
        {
            if (agent.Id == "opencode")
            {
                if (install)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(agent.File)!);
                    AtomicFile.WriteAllText(agent.File, OpenCodePlugin(exe));
                }
                else if (System.IO.File.Exists(agent.File)) System.IO.File.Delete(agent.File);
                return null;
            }

            JsonObject root;
            if (System.IO.File.Exists(agent.File))
            {
                var text = System.IO.File.ReadAllText(agent.File);
                try
                {
                    root = text.Trim().Length == 0 ? new JsonObject()
                        : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                          ?? throw new JsonException();
                }
                catch (JsonException)
                {
                    return $"Não consegui ler {agent.File}: deixei como estava.";
                }
                System.IO.File.Copy(agent.File, $"{agent.File}.{DateTime.Now:yyyyMMdd-HHmmss}.bak", true);
            }
            else
            {
                if (!install) return null;
                root = new JsonObject();
            }

            var changed = Change(agent.Id, root, install, exe);
            Directory.CreateDirectory(Path.GetDirectoryName(agent.File)!);
            AtomicFile.WriteAllText(agent.File, changed.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                // keeps ' and & readable in the commands (the file is the person's, not a web page)
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>The config with the island's entries added (or taken out); everything else is left as it was.</summary>
    internal static JsonObject Change(string agentId, JsonObject root, bool install, string exe)
    {
        root = (JsonObject)root.DeepClone();
        if (agentId == "antigravity")
        {
            root.Remove(AntigravityGroup);
            if (install) root[AntigravityGroup] = AntigravityBlock(exe);
            return root;
        }

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        // take ours out of every event first, so a reinstall never doubles up
        foreach (var (name, value) in hooks.ToList())
        {
            if (value is not JsonArray groups) continue;
            var kept = new JsonArray(groups.Where(g => !IsOurs(g, agentId)).Select(g => g!.DeepClone()).ToArray());
            if (kept.Count == 0) hooks.Remove(name);
            else hooks[name] = kept;
        }
        if (install)
        {
            IEnumerable<(string Event, JsonObject Group)> ours = agentId switch
            {
                "claude" => ClaudeEvents.Select(e => (e.Event, Group(Command(exe, "claude", null, Shell.Bash), e.Timeout, e.Matcher ? "*" : null))),
                "codex" => CodexEvents.Select(e => (e.Event, Group(Command(exe, "codex", null, Shell.Cmd), e.Timeout, null))),
                "gemini" => GeminiEvents.Select(e => (e, Group(Command(exe, "gemini", e, Shell.PowerShell), e is "BeforeTool" or "AfterTool" ? 5000 : 10000, "*"))),
                _ => [],
            };
            foreach (var (name, group) in ours)
            {
                var list = hooks[name] as JsonArray ?? new JsonArray();
                list.Add(group);
                hooks[name] = list;
            }
        }
        if (hooks.Count > 0) root["hooks"] = hooks;
        else root.Remove("hooks");
        return root;
    }

    static bool IsOurs(JsonNode? group, string agentId)
    {
        var text = group?.ToJsonString() ?? "";
        return text.Contains("DynamicIsland", StringComparison.OrdinalIgnoreCase) && text.Contains("hook " + agentId);
    }

    static JsonObject Group(string command, int timeout, string? matcher)
    {
        var group = new JsonObject();
        if (matcher != null) group["matcher"] = matcher;
        group["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = timeout });
        return group;
    }

    static JsonObject AntigravityBlock(string exe)
    {
        var block = new JsonObject();
        JsonObject Handler(string name) => new()
        {
            ["type"] = "command",
            ["command"] = Command(exe, "antigravity", name, Shell.Cmd),
            // PreToolUse may wait for Allow / Deny on the island
            ["timeout"] = name == "PreToolUse" ? ApprovalTiming.HookTimeoutSeconds : 10,
        };
        foreach (var name in AntigravityToolEvents)
            block[name] = new JsonArray(new JsonObject { ["matcher"] = "*", ["hooks"] = new JsonArray(Handler(name)) });
        foreach (var name in AntigravityTurnEvents)
            block[name] = new JsonArray(Handler(name));
        return block;
    }

    enum Shell { Bash, Cmd, PowerShell }

    // Claude Code runs hooks through Git Bash on Windows, Codex and Antigravity through cmd, Gemini CLI through PowerShell.
    static string Command(string exe, string agent, string? hookEvent, Shell shell)
    {
        var args = "hook " + agent + (hookEvent != null ? " " + hookEvent : "");
        return shell switch
        {
            Shell.Bash => $"\"{exe.Replace('\\', '/')}\" {args}",
            Shell.PowerShell => $"& '{exe.Replace("'", "''")}' {args}",
            _ => $"\"{exe}\" {args}",
        };
    }

    /// <summary>The OpenCode plugin: forwards session and tool events to the island and asks it about permissions.
    /// It never waits on the island except for a permission, and answers "ask" (OpenCode's own prompt) when there is
    /// no click.</summary>
    internal static string OpenCodePlugin(string exe) =>
        """
        // Dynamic Island for Windows: shows OpenCode on the island. Written by the island; remove it in its settings.
        import { spawn } from 'node:child_process';

        const EXE = __EXE__;
        const TURN = {
          'session.created': 'SessionStart',
          'session.idle': 'Stop',
          'session.deleted': 'SessionEnd',
          'session.execution.started': 'UserPromptSubmit',
          'session.execution.succeeded': 'Stop',
        };

        function send(payload, wait) {
          return new Promise((resolve) => {
            let out = '';
            let p;
            try {
              p = spawn(EXE, ['hook', 'opencode'], { stdio: ['pipe', wait ? 'pipe' : 'ignore', 'ignore'], windowsHide: true });
            } catch { resolve(''); return; }
            p.on('error', () => resolve(''));
            if (wait) p.stdout.on('data', (chunk) => { out += chunk; });
            p.on('exit', () => resolve(out));
            p.stdin.on('error', () => {});
            p.stdin.end(JSON.stringify(payload));
            if (!wait) { p.unref(); resolve(''); }
            else setTimeout(() => resolve(out), 70000);
          });
        }

        export const DynamicIsland = async ({ directory } = {}) => ({
          event: async ({ event }) => {
            const name = TURN[event?.type];
            const props = event?.properties || {};
            if (name) send({ hook_event_name: name, session_id: props.sessionID || props.info?.id || '', cwd: directory || '' }, false);
          },
          'tool.execute.before': async (input, output) => {
            send({ hook_event_name: 'PreToolUse', session_id: input?.sessionID || '', cwd: directory || '',
              tool_name: String(input?.tool || ''), tool_input: output?.args ?? null }, false);
          },
          'tool.execute.after': async (input) => {
            send({ hook_event_name: 'PostToolUse', session_id: input?.sessionID || '', cwd: directory || '',
              tool_name: String(input?.tool || '') }, false);
          },
          'permission.ask': async (input, output) => {
            const reply = await send({ hook_event_name: 'PermissionRequest', session_id: input?.sessionID || '', cwd: directory || '',
              tool_name: String(input?.type || ''), tool_input: input?.metadata ?? { command: input?.title || '' } }, true);
            try {
              const behavior = JSON.parse(reply)?.hookSpecificOutput?.decision?.behavior;
              if (behavior === 'allow' || behavior === 'deny') output.status = behavior;
            } catch {}
          },
        });
        """.Replace("__EXE__", JsonSerializer.Serialize(exe));
}
