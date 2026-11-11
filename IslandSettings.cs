using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Microsoft.Win32;

namespace DynamicIsland;

public enum IslandShape { Pill, Notch }

public enum PhoneRelayMode { Public, Own, Off }

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
    public PetSpecies Mascot { get; set; } = PetSpecies.Bit;
    public string Theme { get; set; } = IslandThemes.Default;
    public PetOutfit Outfit { get; set; } = PetOutfit.Auto;
    public bool ShowLyrics { get; set; } = true;
    public bool ShowClipboard { get; set; } = true;
    public bool ShowLockKeys { get; set; } = true;
    /// <summary>A permission request first shows as a small pill; hovering or clicking it opens Allow / Deny.</summary>
    public bool QuietApprovals { get; set; } = true;
    /// <summary>Videos (YouTube, Netflix…) don't pop up or keep the pill open; still on the media page.</summary>
    public bool QuietVideos { get; set; } = true;
    /// <summary>Listen on the local network for the paired Android app (off by default).</summary>
    public bool PhoneLink { get; set; }
    /// <summary>How the phone reaches the PC away from home: the built-in public relay (default), the user's own relay
    /// (<see cref="PhoneRelay"/>) or not at all.</summary>
    public PhoneRelayMode PhoneRelayMode { get; set; } = PhoneRelayMode.Public;
    /// <summary>The user's own relay (wss://host, see relay/), used when <see cref="PhoneRelayMode"/> is Own.</summary>
    public string PhoneRelay { get; set; } = "";

    /// <summary>The relay the link should use right now ("" = none).</summary>
    [JsonIgnore]
    public string EffectivePhoneRelay => PhoneRelayMode switch
    {
        PhoneRelayMode.Public => Services.PhoneLink.PublicRelay,
        PhoneRelayMode.Own => PhoneRelay,
        _ => "",
    };
    /// <summary>The paired phone sees the file tray and can open its items on this PC.</summary>
    public bool PhoneTray { get; set; } = true;
    /// <summary>Files can be sent to the paired phone (dropped on the island's "celular" side, or from the tray).</summary>
    public bool PhoneFiles { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Shortcuts the user changed, by action ("hide", "media"…), e.g. "Ctrl+Shift+D". See Hotkeys.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = [];
    public bool DesktopPet { get; set; }
    public double? DesktopPetX { get; set; }
    public double? DesktopPetY { get; set; }
    /// <summary>Monday the weekly recap was last offered (yyyy-MM-dd).</summary>
    public string? RecapShown { get; set; }
    public int Version { get; set; }

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
        var settings = new IslandSettings();
        // A damaged settings.json falls back to settings.json.bak (the previous save) instead of the defaults.
        IslandSettings? Parse(string json)
        {
            try { return JsonSerializer.Deserialize<IslandSettings>(json, Options); }
            catch { return null; }
        }
        if (Services.AtomicFile.ReadAllText(FilePath, json => Parse(json) != null) is { } text)
            settings = Parse(text)!;
        // 2: Coco was only the old default, so files from before Bit move to Bit once.
        // 3: the theme is stored by id instead of its Portuguese name.
        // 4: the public relay is the default for new pairings (see MigrateRelay).
        if (settings.Version < 4)
        {
            if (settings.Version < 2 && settings.Mascot == PetSpecies.Coco) settings.Mascot = PetSpecies.Bit;
            settings.MigrateRelay(existing: settings.Version > 0);
            settings.Version = 4;
            settings.Theme = IslandThemes.Normalize(settings.Theme);
            settings.Save();
        }
        else settings.Theme = IslandThemes.Normalize(settings.Theme);
        return settings;
    }

    /// <summary>Before the public relay: a relay set by hand stays the user's own; someone who already used the phone
    /// link without a relay keeps it off (their traffic doesn't start going through a relay they never chose).
    /// Everyone else, and every new install, gets the public relay.</summary>
    internal void MigrateRelay(bool existing)
    {
        if (!existing) return;
        PhoneRelayMode = PhoneRelay.Length > 0 ? PhoneRelayMode.Own : PhoneLink ? PhoneRelayMode.Off : PhoneRelayMode.Public;
    }

    public void Save()
    {
        if (ReadOnly) return;
        try { Services.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options)); }
        catch (Exception ex) { Services.Log.Error("IslandSettings.Save", ex); }
    }
}

public static class IslandThemes
{
    // settings.json stores the Id, which never changes; Name is only what the user sees (and can be renamed or
    // translated freely). Older settings files stored the Name: IslandSettings.Load migrates them.
    public const string Default = "obsidian";
    public const string Glass = "glass";
    public const string Light = "porcelain";

    // Deep tones keep the island's white text and the album/agent colours readable; the one light theme
    // flips the text brushes (Ink/Muted/Track) to dark instead.
    // Glass is a translucent tint over a real blur of what is behind the island.
    public static readonly (string Id, string Name, Color Color)[] All =
    [
        (Glass, "Vidro (desfocado)", Color.FromArgb(0x66, 0x10, 0x10, 0x14)),
        (Light, "Porcelana (claro)", Color.FromRgb(0xF2, 0xF2, 0xF7)),
        (Default, "Obsidiana", Color.FromRgb(0x00, 0x00, 0x00)),
        ("graphite", "Grafite", Color.FromRgb(0x1C, 0x1C, 0x1E)),
        ("slate", "Ardósia", Color.FromRgb(0x1E, 0x25, 0x33)),
        ("ocean", "Oceano", Color.FromRgb(0x0A, 0x1F, 0x36)),
        ("forest", "Floresta", Color.FromRgb(0x0F, 0x26, 0x1C)),
        ("coffee", "Café", Color.FromRgb(0x2B, 0x1D, 0x15)),
        ("plum", "Ameixa", Color.FromRgb(0x2A, 0x16, 0x33)),
        ("cherry", "Cereja", Color.FromRgb(0x33, 0x0F, 0x18)),
    ];

    public static Color Of(string id)
    {
        foreach (var (i, _, c) in All)
            if (i == id) return c;
        return Colors.Black;
    }

    /// <summary>The id for a stored value that may be an old display name ("Vidro (desfocado)"); unknown → Default.</summary>
    public static string Normalize(string? stored)
    {
        foreach (var (id, name, _) in All)
            if (id == stored || name == stored) return id;
        return Default;
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
