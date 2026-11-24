using System.Text.Json.Nodes;
using DynamicIsland.Services;

namespace DynamicIsland;

// Plugins for Jarvis: MCP servers the person adds in the settings (Playwright's browser comes ready). Each one starts
// the first time Jarvis needs tools, and its tools become Jarvis's tools (named p_<plugin>_<tool>).
public partial class IslandWindow
{
    readonly Dictionary<string, McpClient> _plugins = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, McpClient> PluginClients => _plugins;

    McpClient Client(McpPlugin plugin)
    {
        if (_plugins.TryGetValue(plugin.Name, out var client)) return client;
        client = new McpClient(plugin);
        _plugins[plugin.Name] = client;
        return client;
    }

    /// <summary>Stops plugins that were switched off, removed or changed (they start again on next use).</summary>
    public void ApplyPlugins()
    {
        foreach (var (name, client) in _plugins.ToList())
        {
            var plugin = _settings.Plugins.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (plugin is { Enabled: true } && ReferenceEquals(plugin, _pluginSeen.GetValueOrDefault(name))) continue;
            client.Dispose();
            _plugins.Remove(name);
        }
    }

    readonly Dictionary<string, McpPlugin> _pluginSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts a plugin now (the settings' "Testar"); its problem, or null when it answered with tools.</summary>
    public async Task<string?> TestPluginAsync(McpPlugin plugin)
    {
        _pluginSeen[plugin.Name] = plugin;
        var client = Client(plugin);
        return await client.StartAsync() ? null : client.Problem;
    }

    async Task<JsonArray> IJarvisHost.PluginToolsAsync()
    {
        var tools = new JsonArray();
        foreach (var plugin in _settings.Plugins.Where(p => p.Enabled && p.Name.Length > 0 && p.Command.Length > 0))
        {
            _pluginSeen[plugin.Name] = plugin;
            var client = Client(plugin);
            if (!client.Running)
            {
                ShowInfo("\uEA86", $"Abrindo o plugin {plugin.Name}…", "", glyphColor: JarvisColor, seconds: 60);
                if (!await client.StartAsync())
                {
                    ShowInfo("\uEA86", $"Plugin {plugin.Name}: {client.Problem}", "Configurar", glyphColor: Meter.Red, seconds: 8,
                        action: () => ((App)System.Windows.Application.Current).ShowSettings("jarvis"));
                    continue;
                }
            }
            foreach (var tool in client.Tools)
            {
                var name = tool["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                var description = tool["description"]?.GetValue<string>() ?? "";
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = client.ToolName(name),
                        // long descriptions cost the free plan's tokens on every step
                        ["description"] = $"[{plugin.Name}] " + (description.Length > 240 ? description[..240] : description),
                        ["parameters"] = tool["inputSchema"]?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                    },
                });
            }
        }
        return tools;
    }

    async Task<string?> IJarvisHost.CallPluginAsync(string name, JsonObject arguments)
    {
        foreach (var client in _plugins.Values)
            foreach (var tool in client.Tools)
            {
                var original = tool["name"]?.GetValue<string>() ?? "";
                if (client.ToolName(original) != name) continue;
                ShowInfo("\uEA86", $"{client.Name}: {original.Replace('_', ' ')}", "", glyphColor: JarvisColor, seconds: 30);
                return await client.CallAsync(original, arguments);
            }
        return null;
    }

    void StopPlugins()
    {
        foreach (var client in _plugins.Values) client.Dispose();
        _plugins.Clear();
    }
}
