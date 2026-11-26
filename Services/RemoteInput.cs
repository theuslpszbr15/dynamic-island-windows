using System.Runtime.InteropServices;

namespace DynamicIsland.Services;

/// <summary>The phone as touchpad and keyboard: moves the pointer, clicks, scrolls, presses a known key or types text
/// (as Unicode keystrokes, so it works in any app and leaves the clipboard alone). Only the keys in <see cref="Keys"/>.</summary>
public static class RemoteInput
{
    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyInput Key;
    }

    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    const uint KeyUp = 0x2, KeyUnicode = 0x4, KeyExtended = 0x1;
    const int MaxMove = 2000, MaxScroll = 2400;

    /// <summary>Name → (virtual key, extended). Navigation keys are "extended" for Windows, or they act like the numpad's.</summary>
    public static readonly IReadOnlyDictionary<string, (byte Vk, bool Extended)> Keys = new Dictionary<string, (byte, bool)>
    {
        ["enter"] = (0x0D, false), ["backspace"] = (0x08, false), ["delete"] = (0x2E, true), ["tab"] = (0x09, false),
        ["esc"] = (0x1B, false), ["space"] = (0x20, false), ["up"] = (0x26, true), ["down"] = (0x28, true),
        ["left"] = (0x25, true), ["right"] = (0x27, true), ["home"] = (0x24, true), ["end"] = (0x23, true),
        ["pageup"] = (0x21, true), ["pagedown"] = (0x22, true), ["f5"] = (0x74, false), ["f11"] = (0x7A, false),
        ["b"] = (0x42, false), ["f"] = (0x46, false), ["j"] = (0x4A, false), ["k"] = (0x4B, false), ["l"] = (0x4C, false),
        ["m"] = (0x4D, false), ["win"] = (0x5B, true),
    };

    /// <summary>Two-key shortcuts the remote offers: switch window, start the slideshow from here.</summary>
    public static readonly IReadOnlyDictionary<string, (byte Modifier, byte Vk)> Shortcuts = new Dictionary<string, (byte, byte)>
    {
        ["alt+tab"] = (0x12, 0x09), ["shift+f5"] = (0x10, 0x74),
    };

    public static bool KnownKey(string name) => Keys.ContainsKey(name) || Shortcuts.ContainsKey(name);

    static readonly HashSet<string> Held = [];

    /// <summary>Lets go of a button the phone held down (dragging) when its link drops in the middle.</summary>
    public static void ReleaseAll()
    {
        string[] held;
        lock (Held) held = Held.ToArray();
        foreach (var button in held) Button(button, "up");
    }

    /// <summary>Moves the pointer by (dx, dy) screen pixels, kept on the screens (no Windows acceleration on top of the phone's).</summary>
    public static void Move(int dx, int dy)
    {
        if (!GetCursorPos(out var p)) return;
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        var x = Math.Clamp(p.X + Math.Clamp(dx, -MaxMove, MaxMove), left, left + Math.Max(1, width) - 1);
        var y = Math.Clamp(p.Y + Math.Clamp(dy, -MaxMove, MaxMove), top, top + Math.Max(1, height) - 1);
        SetCursorPos(x, y);
    }

    /// <summary>"left" | "right" | "middle", and "click" | "double" | "down" | "up".</summary>
    public static void Button(string button, string action)
    {
        lock (Held)
        {
            if (action == "down") Held.Add(button);
            else Held.Remove(button);
        }
        var (down, up) = button switch
        {
            "right" => (0x0008u, 0x0010u),
            "middle" => (0x0020u, 0x0040u),
            _ => (0x0002u, 0x0004u),
        };
        var flags = action switch
        {
            "down" => new[] { down },
            "up" => new[] { up },
            "double" => new[] { down, up, down, up },
            _ => new[] { down, up },
        };
        Send(flags.Select(f => new Input { Type = 0, Mouse = new MouseInput { Flags = f } }).ToArray());
    }

    /// <summary>The wheel: positive scrolls up, 120 = one notch (smaller amounts scroll smoothly where apps allow).</summary>
    public static void Scroll(int amount) =>
        Send([new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0800, Data = unchecked((uint)Math.Clamp(amount, -MaxScroll, MaxScroll)) } }]);

    public static void Key(string name)
    {
        if (Shortcuts.TryGetValue(name, out var combo))
        {
            Send([KeyEvent(combo.Modifier, false, false), KeyEvent(combo.Vk, false, false), KeyEvent(combo.Vk, false, true), KeyEvent(combo.Modifier, false, true)]);
            return;
        }
        if (!Keys.TryGetValue(name, out var key)) return;
        Send([KeyEvent(key.Vk, key.Extended, false), KeyEvent(key.Vk, key.Extended, true)]);
    }

    /// <summary>A modifier held with one key (Jarvis's Ctrl+Z and the like).</summary>
    public static void Combo(byte modifier, byte vk) =>
        Send([KeyEvent(modifier, false, false), KeyEvent(vk, false, false), KeyEvent(vk, false, true), KeyEvent(modifier, false, true)]);

    /// <summary>Types the text where the keyboard focus is; a line break is Enter.</summary>
    public static void Text(string text)
    {
        var inputs = new List<Input>(text.Length * 2);
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            if (c == '\n')
            {
                inputs.Add(KeyEvent(0x0D, false, false));
                inputs.Add(KeyEvent(0x0D, false, true));
                continue;
            }
            if (char.IsControl(c)) continue;
            inputs.Add(new Input { Type = 1, Key = new KeyInput { Scan = c, Flags = KeyUnicode } });
            inputs.Add(new Input { Type = 1, Key = new KeyInput { Scan = c, Flags = KeyUnicode | KeyUp } });
        }
        if (inputs.Count > 0) Send(inputs.ToArray());
    }

    static Input KeyEvent(byte vk, bool extended, bool up) =>
        new() { Type = 1, Key = new KeyInput { Vk = vk, Flags = (extended ? KeyExtended : 0) | (up ? KeyUp : 0) } };

    static void Send(Input[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
}
