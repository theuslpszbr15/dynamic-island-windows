using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Microsoft.Win32;

namespace DynamicIsland;

public enum IslandShape { Pill, Notch }

public sealed class IslandSettings
{
    public IslandShape Shape { get; set; } = IslandShape.Pill;
    public List<string> TrayFiles { get; set; } = [];
    public int LastPage { get; set; }
    public bool DiscreetIdle { get; set; } = true;
    /// <summary>"primary", "active" (follows the focused window) or a monitor device name.</summary>
    public string Monitor { get; set; } = "primary";
    public bool ShowNotifications { get; set; } = true;
    public int TimerMinutes { get; set; } = 25;
    public PetSpecies Mascot { get; set; } = PetSpecies.Coco;
    public string Theme { get; set; } = IslandThemes.Default;
    public PetOutfit Outfit { get; set; } = PetOutfit.Auto;
    public bool ShowLyrics { get; set; } = true;
    public bool ShowClipboard { get; set; } = true;
    public bool ShowLockKeys { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public bool DesktopPet { get; set; }
    public double? DesktopPetX { get; set; }
    public double? DesktopPetY { get; set; }
    /// <summary>Monday the weekly recap was last offered (yyyy-MM-dd).</summary>
    public string? RecapShown { get; set; }

    [JsonIgnore] public bool ReadOnly { get; init; }

    public static string Folder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");

    static string FilePath => System.IO.Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IslandSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch { }
    }
}

public static class IslandThemes
{
    public const string Default = "Obsidiana";
    public const string Glass = "Vidro (desfocado)";
    public const string Light = "Porcelana (claro)";

    // Deep tones keep the island's white text and the album/agent colours readable; the one light theme
    // flips the text brushes (Ink/Muted/Track) to dark instead.
    // Glass is a translucent tint over a real blur of what is behind the island.
    public static readonly (string Name, Color Color)[] All =
    [
        (Glass, Color.FromArgb(0x66, 0x10, 0x10, 0x14)),
        (Light, Color.FromRgb(0xF2, 0xF2, 0xF7)),
        ("Obsidiana", Color.FromRgb(0x00, 0x00, 0x00)),
        ("Grafite", Color.FromRgb(0x1C, 0x1C, 0x1E)),
        ("Ardósia", Color.FromRgb(0x1E, 0x25, 0x33)),
        ("Oceano", Color.FromRgb(0x0A, 0x1F, 0x36)),
        ("Floresta", Color.FromRgb(0x0F, 0x26, 0x1C)),
        ("Café", Color.FromRgb(0x2B, 0x1D, 0x15)),
        ("Ameixa", Color.FromRgb(0x2A, 0x16, 0x33)),
        ("Cereja", Color.FromRgb(0x33, 0x0F, 0x18)),
    ];

    public static Color Of(string name)
    {
        foreach (var (n, c) in All)
            if (n == name) return c;
        return Colors.Black;
    }
}

public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "DynamicIsland";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
    }

    public static void Set(bool enabled, string? exe = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{exe ?? Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, false);
    }
}
