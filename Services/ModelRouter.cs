using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland.Services;

/// <summary>
/// Provides automatic model routing based on task complexity analysis.
/// Routes between Haiku, Sonnet, and Opus models according to task difficulty.
/// </summary>
public static class ModelRouter
{
    /// <summary>
    /// Represents different AI model tiers for routing decisions.
    /// </summary>
    public enum ModelTier
    {
        /// <summary>Fast, capable model for routine tasks</summary>
        Haiku,

        /// <summary>Balanced model for most tasks</summary>
        Sonnet,

        /// <summary>Most capable model for complex reasoning</summary>
        Opus,
    }

    /// <summary>
    /// Converter for ModelTier enum to/from string for JSON serialization.
    /// </summary>
    public class ModelTierConverter : JsonConverter<ModelTier>
    {
        public override ModelTier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value switch
            {
                "Haiku" => ModelTier.Haiku,
                "Sonnet" => ModelTier.Sonnet,
                "Opus" => ModelTier.Opus,
                _ => ModelTier.Sonnet,
            };
        }

        public override void Write(Utf8JsonWriter writer, ModelTier value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case ModelTier.Haiku:
                    writer.WriteStringValue("Haiku");
                    break;
                case ModelTier.Sonnet:
                    writer.WriteStringValue("Sonnet");
                    break;
                case ModelTier.Opus:
                    writer.WriteStringValue("Opus");
                    break;
                default:
                    writer.WriteStringValue("Sonnet");
                    break;
            }
        }
    }

    /// <summary>
    /// Configuration for model routing preferences.
    /// </summary>
    public class ModelRouterSettings
    {
        /// <summary>Default model tier to use when complexity is unknown.</summary>
        [JsonConverter(typeof(ModelTierConverter))]
        public ModelTier DefaultTier { get; set; } = ModelTier.Sonnet;

        /// <summary>Whether to automatically select tier based on task complexity.</summary>
        public bool AutoSelect { get; set; } = true;

        /// <summary>Minimum complexity threshold for using Opus tier.</summary>
        public double? OpusThreshold { get; set; } = 0.7;

        /// <summary>Minimum complexity threshold for using Sonnet tier.</summary>
        public double? SonnetThreshold { get; set; } = 0.4;

        /// <summary>Cache duration for complexity analysis (in minutes).</summary>
        public int ComplexityCacheMinutes { get; set; } = 30;
    }

    private static readonly Lazy<ModelRouterSettings> _settings =
        new(() => LoadSettings());

    /// <summary>Gets the current router settings.</summary>
    public static ModelRouterSettings Settings => _settings.Value;

    /// <summary>Determines the appropriate model tier for a given task complexity.</summary>
    /// <param name="complexity">Task complexity score from 0.0 (trivial) to 1.0 (very complex)</param>
    /// <returns>The recommended model tier.</returns>
    public static ModelTier GetTierForComplexity(double complexity)
    {
        if (complexity < 0.0 || complexity > 1.0)
            throw new ArgumentOutOfRangeException(nameof(complexity), "Complexity must be between 0.0 and 1.0");

        var settings = Settings;

        if (!settings.AutoSelect)
            return settings.DefaultTier;

        // Apply thresholds (lower thresholds mean more likely to use higher-tier models)
        if (complexity >= (settings.OpusThreshold ?? 0.7))
            return ModelTier.Opus;
        if (complexity >= (settings.SonnetThreshold ?? 0.4))
            return ModelTier.Sonnet;

        return ModelTier.Haiku;
    }

    /// <summary>Analyzes a task description to estimate its complexity.</summary>
    /// <param name="taskDescription">Description of the task to analyze.</param>
    /// <returns>Complexity score from 0.0 to 1.0.</returns>
    public static double AnalyzeComplexity(string taskDescription)
    {
        if (string.IsNullOrWhiteSpace(taskDescription))
            return 0.1;

        var lower = taskDescription.ToLowerInvariant();
        var score = 0.0;

        // Complexity indicators that increase the score
        var complexIndicators = new[]
        {
            "complex", "difficult", "advanced", "optimize", "performance",
            "algorithm", "architecture", "design", "scalable", "distributed",
            "refactor", "security", "encrypt", "cache", "concurrency",
            "microservice", "integration", "api", "framework", "library",
            "research", "analysis", "proof", "proof-of-concept",
            "beta", "production", "deploy", "migration",
            "bug", "fix", "issue", "error", "troubleshoot"
        };

        // Simplicity indicators that decrease the score
        var simpleIndicators = new[]
        {
            "simple", "easy", "quick", "trivial", "typo", "format",
            "rename", "move", "copy", "basic", "simple", "minor",
            "update", "change", "fix", "patch"
        };

        var words = taskDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (complexIndicators.Contains(word))
                score += 0.08;
            else if (simpleIndicators.Contains(word))
                score -= 0.05;
        }

        // Normalize to 0.0-1.0 range
        score = Math.Max(0.0, Math.Min(1.0, score));

        // If no specific indicators found, use length-based heuristic
        if (score < 0.2 && words.Length > 20)
            score = 0.3;
        else if (score < 0.2)
            score = 0.1;

        return Math.Round(score, 2);
    }

    /// <summary>Gets the model tier for a Claude Code hook event.</summary>
    /// <param name="hookEvent">The Claude Code hook event name.</param>
    /// <param name="toolName">The tool being used (optional).</param>
    /// <returns>Recommended model tier.</returns>
    public static ModelTier GetTierForHook(string hookEvent, string? toolName = null)
    {
        // Certain hook events are typically more complex
        var complexEvents = new[] { "PreToolUse", "PostToolUse", "Task", "Agent" };
        var mediumEvents = new[] { "WebFetch", "WebSearch", "Edit", "Read" };

        if (complexEvents.Contains(hookEvent))
            return ModelTier.Opus;
        if (mediumEvents.Contains(hookEvent))
            return ModelTier.Sonnet;

        return ModelTier.Haiku;
    }

    /// <summary>Loads router settings from application settings files.</summary>
    private static ModelRouterSettings LoadSettings()
    {
        // Try project settings first, then fall back to AppData
        var possiblePaths = new[]
        {
            System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "settings.json"),
            System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "DynamicIsland", "model-router-settings.json"),
        };

        foreach (var path in possiblePaths)
        {
            if (System.IO.File.Exists(path))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(path);
                    var settings = System.Text.Json.JsonSerializer.Deserialize<ModelRouterSettings>(json);
                    if (settings != null) return settings;
                }
                catch
                {
                    // Fall through to next path
                }
            }
        }

        return new ModelRouterSettings();
    }

    /// <summary>Saves current settings to application data.</summary>
    public static void SaveSettings()
    {
        var settings = Settings;
        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        var settingsPath = System.IO.Path.Combine(appData, "DynamicIsland", "model-router-settings.json");

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath)!);
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        System.IO.File.WriteAllText(settingsPath, json);
    }
}