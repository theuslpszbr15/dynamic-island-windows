using System.Collections.Concurrent;

namespace DynamicIsland.Services;

public enum MediaAppCategory
{
    Music,
    Browser,
    Call,
    Other,
}

public sealed record MediaAppInfo(string Key, string DisplayName, MediaAppCategory Category);

/// <summary>
/// Controls which applications are allowed to show as playing media in the island.
/// Filters out voice call sounds (Discord, Teams) and unwanted browser tabs (TikTok, Shorts)
/// according to presets or individual app choices.
/// </summary>
public static class MediaFilter
{
    static readonly HashSet<string> MusicPlayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "spotify", "applemusic", "tidal", "deezer", "cider", "vlc", "foobar2000",
        "zunemusic", "mediaplayer", "wmplayer", "musicbee", "aimp", "winamp",
        "groove", "amazonmusic", "qobuz", "itunes"
    };

    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "operagx", "vivaldi",
        "arc", "waterfox", "librewolf", "zen"
    };

    static readonly HashSet<string> Calls = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord", "discordptb", "discordcanary", "teams", "ms-teams", "zoom",
        "slack", "ciscocollabhost", "webexhost", "skype", "whatsapp", "telegram",
        "msedgewebview2"
    };

    static readonly Dictionary<string, (string Name, MediaAppCategory Category)> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spotify"] = ("Spotify", MediaAppCategory.Music),
        ["applemusic"] = ("Apple Music", MediaAppCategory.Music),
        ["deezer"] = ("Deezer", MediaAppCategory.Music),
        ["tidal"] = ("Tidal", MediaAppCategory.Music),
        ["vlc"] = ("VLC Media Player", MediaAppCategory.Music),
        ["foobar2000"] = ("Foobar2000", MediaAppCategory.Music),
        ["mediaplayer"] = ("Windows Media Player", MediaAppCategory.Music),
        ["cider"] = ("Cider", MediaAppCategory.Music),
        ["musicbee"] = ("MusicBee", MediaAppCategory.Music),
        ["aimp"] = ("AIMP", MediaAppCategory.Music),
        ["winamp"] = ("Winamp", MediaAppCategory.Music),

        ["chrome"] = ("Google Chrome", MediaAppCategory.Browser),
        ["msedge"] = ("Microsoft Edge", MediaAppCategory.Browser),
        ["firefox"] = ("Mozilla Firefox", MediaAppCategory.Browser),
        ["brave"] = ("Brave Browser", MediaAppCategory.Browser),
        ["opera"] = ("Opera", MediaAppCategory.Browser),
        ["operagx"] = ("Opera GX", MediaAppCategory.Browser),
        ["vivaldi"] = ("Vivaldi", MediaAppCategory.Browser),
        ["arc"] = ("Arc Browser", MediaAppCategory.Browser),

        ["discord"] = ("Discord", MediaAppCategory.Call),
        ["teams"] = ("Microsoft Teams", MediaAppCategory.Call),
        ["zoom"] = ("Zoom", MediaAppCategory.Call),
        ["slack"] = ("Slack", MediaAppCategory.Call),
        ["whatsapp"] = ("WhatsApp", MediaAppCategory.Call),
        ["telegram"] = ("Telegram", MediaAppCategory.Call),
        ["skype"] = ("Skype", MediaAppCategory.Call),
    };

    static readonly ConcurrentDictionary<string, MediaAppInfo> Discovered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cleans process names or AppUserModelIds into a canonical lowercase key.</summary>
    public static string NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var name = raw.Trim();
        if (name.Contains('!')) name = name.Split('!')[0];
        if (name.Contains('_')) name = name.Split('_')[0];
        name = name.Split('.', '\\').LastOrDefault(p => p.Length > 0 && !p.Equals("exe", StringComparison.OrdinalIgnoreCase)) ?? name;

        return name.ToLowerInvariant() switch
        {
            "zunemusic" or "mediaplayer" or "wmplayer" => "mediaplayer",
            "msedge" => "msedge",
            "chrome" => "chrome",
            "spotify" => "spotify",
            "firefox" => "firefox",
            "discordptb" or "discordcanary" => "discord",
            "operagx" => "operagx",
            "ms-teams" => "teams",
            _ => name.ToLowerInvariant()
        };
    }

    public static MediaAppCategory GetCategory(string key)
    {
        var norm = NormalizeKey(key);
        if (Catalog.TryGetValue(norm, out var info)) return info.Category;
        if (Calls.Contains(norm)) return MediaAppCategory.Call;
        if (Browsers.Contains(norm)) return MediaAppCategory.Browser;
        if (MusicPlayers.Contains(norm)) return MediaAppCategory.Music;
        return MediaAppCategory.Other;
    }

    public static string GetDisplayName(string key)
    {
        var norm = NormalizeKey(key);
        if (Catalog.TryGetValue(norm, out var info)) return info.Name;
        if (Discovered.TryGetValue(norm, out var disc)) return disc.DisplayName;
        if (norm.Length == 0) return "Desconhecido";
        return char.ToUpperInvariant(norm[0]) + norm[1..];
    }

    /// <summary>Registers an application seen producing audio or holding a media session.</summary>
    public static void RecordSeen(string key, string? friendlyName = null)
    {
        var norm = NormalizeKey(key);
        if (string.IsNullOrEmpty(norm)) return;
        var name = friendlyName ?? GetDisplayName(norm);
        var category = GetCategory(norm);
        Discovered.TryAdd(norm, new MediaAppInfo(norm, name, category));
    }

    /// <summary>Returns catalog presets and any runtime-discovered apps combined.</summary>
    public static IReadOnlyList<MediaAppInfo> GetAllKnownApps()
    {
        var list = new Dictionary<string, MediaAppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Catalog)
            list[k] = new MediaAppInfo(k, v.Name, v.Category);
        foreach (var (k, v) in Discovered)
            list[k] = v;
        return [.. list.Values];
    }

    /// <summary>Determines whether a media source is allowed to appear on the island.</summary>
    public static bool IsAllowed(string? rawAppId, IslandSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(rawAppId)) return false;
        var key = NormalizeKey(rawAppId);
        if (string.IsNullOrEmpty(key)) return false;

        RecordSeen(key);

        var preset = settings?.MediaFilterPreset ?? MediaFilterPreset.MusicOnly;
        var filter = settings?.MediaAppFilter;

        // In Custom mode, check explicit map
        if (preset == MediaFilterPreset.Custom)
        {
            if (filter != null && filter.TryGetValue(key, out var allowed))
                return allowed;
            return GetCategory(key) == MediaAppCategory.Music;
        }

        // If user explicitly blocked this app in overrides
        if (filter != null && filter.TryGetValue(key, out var isAllowed) && !isAllowed)
            return false;

        return preset switch
        {
            MediaFilterPreset.MusicOnly => GetCategory(key) == MediaAppCategory.Music,
            MediaFilterPreset.MusicAndWeb => GetCategory(key) is MediaAppCategory.Music or MediaAppCategory.Browser,
            MediaFilterPreset.All => GetCategory(key) != MediaAppCategory.Call,
            _ => GetCategory(key) == MediaAppCategory.Music
        };
    }
}
