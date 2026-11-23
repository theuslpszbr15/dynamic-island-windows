using System.Windows;

namespace DynamicIsland.Services;

/// <summary>Types text where the person is typing by pasting it, then gives the clipboard back what it held.
/// Neither clipboard watchers (the island included) nor Windows' clipboard history keep the pasted text.</summary>
public static class TextPaster
{
    const uint VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    /// <summary>Until when the island's clipboard watcher should look away (the old content coming back).</summary>
    public static DateTime IgnoreUntil { get; private set; }

    public static bool ModifiersDown() =>
        Native.IsKeyDown(VK_CONTROL) || Native.IsKeyDown(VK_MENU) || Native.IsKeyDown(VK_SHIFT) || Native.IsKeyDown(VK_LWIN) || Native.IsKeyDown(VK_RWIN);

    /// <summary>Must run on an STA thread (the UI thread). False when the clipboard stayed busy.</summary>
    public static async Task<bool> PasteAsync(string text)
    {
        // a shortcut's Ctrl/Alt still down would turn Ctrl+V into something else
        for (var i = 0; i < 60 && ModifiersDown(); i++) await Task.Delay(50);
        var saved = Copy();
        var data = new DataObject(DataFormats.UnicodeText, text);
        Private(data);
        IgnoreUntil = DateTime.Now.AddSeconds(3);
        if (!await SetAsync(data)) return false;
        Native.PressCtrlV();
        // the app reads the clipboard after the keys arrive; give it a moment before putting the old content back
        await Task.Delay(600);
        if (saved == null) return true;
        IgnoreUntil = DateTime.Now.AddSeconds(2);
        Private(saved);
        await SetAsync(saved);
        return true;
    }

    /// <summary>The text selected in the app in front (a Ctrl+C), with the clipboard given back afterwards. Null when
    /// nothing is selected. Must run on an STA thread.</summary>
    public static async Task<string?> CopySelectionAsync()
    {
        for (var i = 0; i < 60 && ModifiersDown(); i++) await Task.Delay(50);
        var saved = Copy();
        var before = Native.GetClipboardSequenceNumber();
        IgnoreUntil = DateTime.Now.AddSeconds(3);
        Native.PressCtrl(0x43);
        // the app copies when it gets to the keys; nothing selected = the clipboard never changes
        for (var i = 0; i < 16 && Native.GetClipboardSequenceNumber() == before; i++) await Task.Delay(40);
        if (Native.GetClipboardSequenceNumber() == before) return null;
        string? text = null;
        for (var attempt = 0; attempt < 5 && text == null; attempt++)
        {
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch (System.Runtime.InteropServices.COMException) { await Task.Delay(60); }
        }
        if (saved != null)
        {
            IgnoreUntil = DateTime.Now.AddSeconds(2);
            Private(saved);
            await SetAsync(saved);
        }
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    static void Private(DataObject data)
    {
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
    }

    static DataObject? Copy()
    {
        try
        {
            if (Clipboard.GetDataObject() is not { } source) return null;
            var copy = new DataObject();
            foreach (var format in source.GetFormats(false))
                try
                {
                    if (source.GetData(format, false) is { } value) copy.SetData(format, value, false);
                }
                catch { } // a format another app renders on demand and won't give
            return copy.GetFormats(false).Length > 0 ? copy : null;
        }
        catch { return null; }
    }

    static async Task<bool> SetAsync(DataObject data)
    {
        // another app may hold the clipboard for a moment
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException) { await Task.Delay(60); }
        }
        return false;
    }
}
