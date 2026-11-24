using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Microsoft.Win32;

namespace DynamicIsland;

public enum IslandShape { Pill, Notch }

public enum PhoneRelayMode { Public, Own, Off }

/// <summary>Where "who joins the Discord call" comes from: the Discord window (no setup), or Discord's local RPC with the
/// island's app (testers only) or the user's own app.</summary>
public enum DiscordSource { Window, IslandApp, OwnApp }

/// <summary>What the island does while an app is in full screen (a game, a video, slides): stay hidden and show only
/// for a shortcut, also for alerts that can't wait (the default), or never hide.</summary>
public enum FullscreenMode { Shortcuts, Alerts, Always }

/// <summary>Preset for filtering which applications can appear as playing media.</summary>
public enum MediaFilterPreset { MusicOnly, MusicAndWeb, All, Custom }

public sealed record TaskItem(string Id, string Text, bool Done, string? Due = null);
public sealed record QuickSnippet(string Label, string Value);

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
    /// <summary>The colour of the "Personalizado" theme.</summary>
    public string CustomThemeColor { get; set; } = "#1E2533";
    /// <summary>The imported mascot (PetSpecies.Custom), copied into the settings folder.</summary>
    [JsonIgnore]
    public static string CustomMascotFile => System.IO.Path.Combine(Folder, "mascot.png");
    public PetOutfit Outfit { get; set; } = PetOutfit.Auto;
    public bool ShowLyrics { get; set; } = true;
    public bool ShowClipboard { get; set; } = true;
    public bool ShowLockKeys { get; set; } = true;
    /// <summary>A permission request first shows as a small pill; hovering or clicking it opens Allow / Deny.</summary>
    public bool QuietApprovals { get; set; } = true;
    /// <summary>Videos (YouTube, Netflix…) don't pop up or keep the pill open; still on the media page.</summary>
    public bool QuietVideos { get; set; } = true;
    /// <summary>Preset controlling which apps can show on the media card.</summary>
    public MediaFilterPreset MediaFilterPreset { get; set; } = MediaFilterPreset.MusicOnly;
    /// <summary>Explicit allow/block overrides per application key.</summary>
    public Dictionary<string, bool> MediaAppFilter { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsMediaAppAllowed(string key) =>
        Services.MediaFilter.IsAllowed(key, this);

    public void SetMediaAppAllowed(string key, bool allowed)
    {
        var norm = Services.MediaFilter.NormalizeKey(key);
        if (!string.IsNullOrEmpty(norm))
        {
            MediaAppFilter[norm] = allowed;
            Save();
        }
    }
    /// <summary>The soft orange glow behind the settings page and around the open island.</summary>
    public bool GlowLook { get; set; } = true;
    /// <summary>Animated aurora light rim and glow around the island.</summary>
    public bool AuroraLook { get; set; }
    /// <summary>Hides the island when the focused window's top bar sits right under it (e.g. maximized browser tabs).</summary>
    public bool SmartHideUnderTopBar { get; set; }
    /// <summary>Drag the island with Ctrl+LeftClick (or Alt+LeftClick) to another monitor and remember it.</summary>
    public bool DragMonitor { get; set; } = true;
    /// <summary>Playful idle animations on the mascot (chasing a fly, bouncing a ball, looking around).</summary>
    public bool PlayfulPet { get; set; } = true;
    public List<TaskItem> Tasks { get; set; } = [];
    public int WaterGoal { get; set; } = 8;
    public int WaterGlasses { get; set; }
    public string? WaterDate { get; set; }
    public bool WaterReminder { get; set; }
    public List<QuickSnippet> Snippets { get; set; } = [];
    public string QuickNote { get; set; } = "";
    /// <summary>Secret iCal links (Google Calendar, Outlook on the web, iCloud…) for "Reunião em 5 min".</summary>
    public List<string> CalendarFeeds { get; set; } = [];
    /// <summary>Also read the classic Outlook on this PC (only while it is open).</summary>
    public bool OutlookCalendar { get; set; } = true;
    /// <summary>While the timer runs, Windows notifications wait and the minutes count as focus.</summary>
    public bool FocusMode { get; set; } = true;
    /// <summary>Focused minutes per day (yyyy-MM-dd), the last 30 days.</summary>
    public Dictionary<string, int> FocusLog { get; set; } = [];
    /// <summary>Jarvis's shortcut: "hold" = hold to talk (a tap opens the chat box), "text" = always the chat box.</summary>
    public string JarvisVoiceMode { get; set; } = "hold";
    /// <summary>Jarvis says its answers with the Piper voice (when downloaded).</summary>
    public bool JarvisSpeak { get; set; } = true;
    /// <summary>Colors of the voice glow while Jarvis listens, thinks or speaks (VoiceGlow.Variants) or "off".</summary>
    public string JarvisGlow { get; set; } = "colorful";
    /// <summary>How Jarvis calls the person (the Groq brain's prompt); empty = "o usuário".</summary>
    public string JarvisUserName { get; set; } = "";
    /// <summary>Hold the dictation shortcut, speak, let go: the words are pasted where you type.</summary>
    public bool Dictation { get; set; } = true;
    /// <summary>"said", "clean" or "formal" (Dictation.Styles); the last two need the Groq key.</summary>
    public string DictationStyle { get; set; } = "clean";
    /// <summary>Select text, press the shortcut: the translation shows on the island (needs the Groq key).</summary>
    public bool QuickTranslate { get; set; } = true;
    /// <summary>"auto" (Portuguese ↔ English) or a language from Translator.Targets.</summary>
    public string TranslateTo { get; set; } = "auto";
    /// <summary>Copilot, Claude and Codex plan usage next to the clock.</summary>
    public bool PlanUsage { get; set; } = true;
    /// <summary>CI failures, pull requests and releases of <see cref="GitHubRepos"/> on the island.</summary>
    public bool GitHubWatch { get; set; }
    public List<string> GitHubRepos { get; set; } = [Services.Updater.Repository];
    public bool GitHubCi { get; set; } = true;
    public bool GitHubPulls { get; set; } = true;
    public bool GitHubReleases { get; set; } = true;
    /// <summary>The last 20 copied texts on the island's "Copiados" page (kept in memory; pinned ones encrypted on disk).</summary>
    public bool ClipboardHistory { get; set; } = true;
    /// <summary>A card when the internet drops and when it comes back.</summary>
    public bool NetAlerts { get; set; } = true;
    /// <summary>A browser download in progress shows on the compact island.</summary>
    public bool DownloadProgress { get; set; } = true;
    /// <summary>"Ei Jarvis" opens Jarvis by voice (Windows' offline recognition; the microphone shows as in use).</summary>
    public bool WakeWord { get; set; }
    /// <summary>MCP servers whose tools Jarvis can use (Playwright's browser comes in the list, off).</summary>
    public List<Services.McpPlugin> Plugins { get; set; } = [Services.McpPlugin.Playwright()];
    /// <summary>Jarvis may move the mouse and type on this PC (usar_computador). Off until the person turns it on.</summary>
    public bool ComputerUse { get; set; }
    /// <summary>"cada" asks before every action; "perigosas" only before typing, Enter/Delete and clicks on buy, send, delete…</summary>
    public string ComputerUseApproval { get; set; } = "cada";
    /// <summary>A new install offers the demo once.</summary>
    public bool DemoPending { get; set; }
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
    /// <summary>Text copied on one side lands on the other's clipboard.</summary>
    public bool PhoneClipboard { get; set; } = true;
    /// <summary>The phone's incoming calls show on the island.</summary>
    public bool PhoneCalls { get; set; } = true;
    /// <summary>The phone can control music, volume, lock and shutdown of this PC.</summary>
    public bool PhoneRemote { get; set; } = true;
    /// <summary>Text dictated on the phone is typed where you are on the PC.</summary>
    public bool PhoneTyping { get; set; } = true;
    /// <summary>The notifications the island shows also go to the paired phone.</summary>
    public bool PhoneNotifications { get; set; } = true;
    /// <summary>Who joins the Discord call the user is in shows on the island (local RPC, see Services/DiscordVoice).</summary>
    public bool DiscordCalls { get; set; }
    /// <summary>Also a small pill when someone leaves that call.</summary>
    public bool DiscordLeaves { get; set; } = true;
    /// <summary>Who joins and leaves also goes to the paired phone's floating island (over the same pinned link).</summary>
    public bool DiscordToPhone { get; set; } = true;
    /// <summary>Notices switched off on the phone's island ("Avisos da ilha"), shared with the phone over the link.</summary>
    public List<string> IslandOffKinds { get; set; } = [];
    /// <summary>When that list last changed (Unix ms, 0 = never): the newest side wins when PC and phone disagree.</summary>
    public long IslandOffAt { get; set; }
    public DiscordSource DiscordSource { get; set; } = DiscordSource.Window;
    public FullscreenMode Fullscreen { get; set; } = FullscreenMode.Alerts;
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Shortcuts the user changed, by action ("hide", "media"…), e.g. "Ctrl+Shift+D". See Hotkeys.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = [];
    public bool DesktopPet { get; set; }
    public double? DesktopPetX { get; set; }
    public double? DesktopPetY { get; set; }
    /// <summary>Monday the weekly recap was last offered (yyyy-MM-dd).</summary>
    public string? RecapShown { get; set; }
    /// <summary>Recently used Spotify playlists (name + optional URI id).</summary>
    public List<PlaylistSetting>? SpotifyPlaylists { get; set; }
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
        else settings.DemoPending = true;
        // 2: Coco was only the old default, so files from before Bit move to Bit once.
        // 3: the theme is stored by id instead of its Portuguese name.
        // 4: the public relay is the default for new pairings (see MigrateRelay).
        // 5: Spotify playlists list added.
        if (settings.Version < 5)
        {
            if (settings.Version < 2 && settings.Mascot == PetSpecies.Coco) settings.Mascot = PetSpecies.Bit;
            if (settings.Version < 4) settings.MigrateRelay(existing: settings.Version > 0);
            settings.Version = 5;
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
    /// <summary>The person's own colour (<see cref="CustomColor"/>, kept in IslandSettings.CustomThemeColor).</summary>
    public const string Custom = "custom";

    public static Color CustomColor { get; set; } = Color.FromRgb(0x1E, 0x25, 0x33);

    /// <summary>Themes that want dark text: Porcelana, and a light custom colour.</summary>
    public static bool IsLight(string id) =>
        id == Light || (id == Custom && (0.299 * CustomColor.R + 0.587 * CustomColor.G + 0.114 * CustomColor.B) / 255 > 0.6);

    /// <summary>"#1E2533" or "1E2533" → colour, or null.</summary>
    public static Color? ParseHex(string? text)
    {
        var t = (text ?? "").Trim().TrimStart('#');
        if (t.Length != 6 || !uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

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
        (Custom, "Personalizado", Color.FromRgb(0x1E, 0x25, 0x33)),
    ];

    public static Color Of(string id)
    {
        if (id == Custom) return CustomColor;
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
