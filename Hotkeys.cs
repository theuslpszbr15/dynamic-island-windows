using System.Windows.Input;

namespace DynamicIsland;

/// <summary>
/// The island's global shortcuts: what each one does, its default keys and the "Ctrl+Alt+D" text format kept in
/// settings.json (IslandSettings.Hotkeys holds only the ones the user changed).
/// </summary>
public static class Hotkeys
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;

    public sealed record Shortcut(int Id, string Key, string Default, string Label);

    // Ids are the RegisterHotKey ids, so they stay stable.
    public static readonly Shortcut[] All =
    [
        new(1, "hide", "Ctrl+Alt+D", "esconder / mostrar a ilha"),
        new(2, "media", "Ctrl+Alt+M", "abrir mídia"),
        new(3, "timer", "Ctrl+Alt+T", "abrir o timer"),
        new(4, "agents", "Ctrl+Alt+A", "abrir agentes"),
        new(5, "calendar", "Ctrl+Alt+C", "abrir a agenda"),
    ];

    public static Shortcut? ById(int id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>The keys in use for an action (the user's choice or the default), e.g. "Ctrl+Alt+D".</summary>
    public static string Keys(IslandSettings settings, string action)
    {
        var fallback = All.First(a => a.Key == action).Default;
        return settings.Hotkeys.TryGetValue(action, out var keys) && Parse(keys) != null ? keys : fallback;
    }

    /// <summary>"Ctrl+Shift+F9" → (modifiers, virtual key), or null when it isn't a usable shortcut
    /// (needs Ctrl, Alt or Win plus one letter, digit or F-key).</summary>
    public static (uint Modifiers, uint Key)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint modifiers = 0;
        Key? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default:
                    if (key != null || !Enum.TryParse<Key>(part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part, true, out var k)
                        || !Usable(k)) return null;
                    key = k;
                    break;
            }
        }
        if (key is not { } found || (modifiers & (MOD_CONTROL | MOD_ALT | MOD_WIN)) == 0) return null;
        return (modifiers, (uint)KeyInterop.VirtualKeyFromKey(found));
    }

    /// <summary>The text for a key pressed in the settings box, or null while only modifiers are down.</summary>
    public static string? Format(ModifierKeys modifiers, Key key)
    {
        if (!Usable(key)) return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString());
        var text = string.Join('+', parts);
        return Parse(text) != null ? text : null;
    }

    static bool Usable(Key key) => key is (>= Key.A and <= Key.Z) or (>= Key.D0 and <= Key.D9) or (>= Key.F1 and <= Key.F24);
}
