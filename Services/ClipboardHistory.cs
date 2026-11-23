using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DynamicIsland.Services;

public sealed record ClipEntry(string Text, DateTime At, bool Pinned);

/// <summary>
/// The last texts copied on this PC (password managers and other private sources are already filtered out by the
/// caller). Only text, at most 20 recent ones kept in memory and never written to disk; pinned ones are saved encrypted
/// for this Windows user (DPAPI) so they survive a restart.
/// </summary>
public sealed class ClipboardHistory
{
    public const int Recent = 20, MaxPinned = 20, MaxLength = 10_000;
    static readonly byte[] Purpose = "DynamicIsland.Clipboard"u8.ToArray();

    readonly List<ClipEntry> _items = [];
    readonly string _pinnedFile;

    public event Action? Changed;

    public ClipboardHistory(string? folder = null)
    {
        _pinnedFile = Path.Combine(folder ?? IslandSettings.Folder, "clipboard-pinned.bin");
        try
        {
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_pinnedFile), Purpose, DataProtectionScope.CurrentUser));
            foreach (var text in JsonSerializer.Deserialize<List<string>>(json) ?? []) _items.Add(new ClipEntry(text, DateTime.MinValue, true));
        }
        catch { } // none yet, or from another Windows user
    }

    /// <summary>Pinned first (in the order they were pinned), then the newest.</summary>
    public IReadOnlyList<ClipEntry> Items => [.. _items.Where(i => i.Pinned), .. _items.Where(i => !i.Pinned).OrderByDescending(i => i.At)];

    public IReadOnlyList<ClipEntry> Search(string query) =>
        string.IsNullOrWhiteSpace(query) ? Items : Items.Where(i => i.Text.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxLength) text = text[..MaxLength];
        var existing = _items.FindIndex(i => i.Text == text);
        if (existing >= 0)
        {
            // copied again: it moves to the top (a pinned one just stays pinned)
            if (_items[existing].Pinned) return;
            _items.RemoveAt(existing);
        }
        _items.Add(new ClipEntry(text, DateTime.Now, false));
        var recent = _items.Where(i => !i.Pinned).OrderBy(i => i.At).ToList();
        foreach (var old in recent.Take(Math.Max(0, recent.Count - Recent))) _items.Remove(old);
        Changed?.Invoke();
    }

    public void TogglePin(ClipEntry entry)
    {
        var index = _items.FindIndex(i => i.Text == entry.Text);
        if (index < 0) return;
        if (!entry.Pinned && _items.Count(i => i.Pinned) >= MaxPinned) return;
        _items[index] = _items[index] with { Pinned = !entry.Pinned, At = DateTime.Now };
        SavePinned();
        Changed?.Invoke();
    }

    public void Remove(ClipEntry entry)
    {
        if (_items.RemoveAll(i => i.Text == entry.Text) == 0) return;
        if (entry.Pinned) SavePinned();
        Changed?.Invoke();
    }

    /// <summary>Forgets the recent ones; pinned ones stay.</summary>
    public void Clear()
    {
        _items.RemoveAll(i => !i.Pinned);
        Changed?.Invoke();
    }

    void SavePinned()
    {
        try
        {
            var pinned = _items.Where(i => i.Pinned).Select(i => i.Text).ToList();
            if (pinned.Count == 0)
            {
                File.Delete(_pinnedFile);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_pinnedFile)!);
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pinned)), Purpose, DataProtectionScope.CurrentUser);
            AtomicFile.WriteAllBytes(_pinnedFile, bytes, backup: false);
        }
        catch (Exception ex) { Log.Error("ClipboardHistory", ex); }
    }
}
