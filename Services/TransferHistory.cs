using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DynamicIsland.Services;

/// <summary>One thing sent to or received from the phone. <see cref="Paths"/>: the files on this PC (sent: where they
/// came from; received: where they were saved). <see cref="Text"/>: a text that went either way.</summary>
public sealed record TransferEntry(string Id, DateTime When, bool Sent, string Title, long Bytes, int Count,
    string Status, IReadOnlyList<string> Paths, string? Text)
{
    public bool IsText => Text != null;
}

/// <summary>
/// The Recebidos / Enviados lists of the phone window: the last <see cref="MaxEntries"/> transfers, newest first.
/// Kept in the island's folder encrypted for this Windows user (DPAPI), since texts can be private; long texts are
/// cut to <see cref="MaxTextChars"/>. A null file keeps it in memory only (tests).
/// </summary>
public sealed class TransferHistory(string? file)
{
    public const int MaxEntries = 100, MaxTextChars = 20_000;
    static readonly byte[] Purpose = Encoding.UTF8.GetBytes("dynamicisland-transfers");

    readonly object _gate = new();
    List<TransferEntry>? _entries;

    public static string DefaultFile => Path.Combine(IslandSettings.Folder, "transfers.bin");

    /// <summary>Raised (on whatever thread made the change) when an entry is added, changed or the list cleared.</summary>
    public event Action? Changed;

    public IReadOnlyList<TransferEntry> Entries
    {
        get { lock (_gate) return [.. Load()]; }
    }

    public void Add(TransferEntry entry)
    {
        if (entry.Text is { Length: > MaxTextChars } text) entry = entry with { Text = text[..MaxTextChars] };
        lock (_gate)
        {
            var list = Load();
            list.RemoveAll(e => e.Id == entry.Id && e.Sent == entry.Sent);
            list.Insert(0, entry);
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            Save(list);
        }
        Changed?.Invoke();
    }

    /// <summary>Sets the status of an entry already in the list (e.g. "No celular" once the phone confirms).</summary>
    public void SetStatus(string id, bool sent, string status)
    {
        lock (_gate)
        {
            var list = Load();
            var i = list.FindIndex(e => e.Id == id && e.Sent == sent);
            if (i < 0 || list[i].Status == status) return;
            list[i] = list[i] with { Status = status };
            Save(list);
        }
        Changed?.Invoke();
    }

    public void Remove(string id, bool sent)
    {
        lock (_gate)
        {
            if (Load().RemoveAll(e => e.Id == id && e.Sent == sent) == 0) return;
            Save(_entries!);
        }
        Changed?.Invoke();
    }

    public void Clear(bool sent)
    {
        lock (_gate)
        {
            if (Load().RemoveAll(e => e.Sent == sent) == 0) return;
            Save(_entries!);
        }
        Changed?.Invoke();
    }

    List<TransferEntry> Load()
    {
        if (_entries != null) return _entries;
        _entries = [];
        if (file == null || !File.Exists(file)) return _entries;
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(file), Purpose, DataProtectionScope.CurrentUser);
            _entries = JsonSerializer.Deserialize<List<TransferEntry>>(json) ?? [];
            _entries.RemoveAll(e => e == null! || e.Id == null || e.Title == null || e.Status == null);
            _entries = [.. _entries.Select(e => e with { Paths = e.Paths ?? [] }).Take(MaxEntries)];
        }
        catch (Exception ex)
        {
            Log.Error("TransferHistory.Load", ex); // unreadable (another user, damaged): start over
            _entries = [];
        }
        return _entries;
    }

    void Save(List<TransferEntry> list)
    {
        if (file == null) return;
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(list);
            AtomicFile.WriteAllBytes(file, ProtectedData.Protect(json, Purpose, DataProtectionScope.CurrentUser), backup: false);
        }
        catch (Exception ex) { Log.Error("TransferHistory.Save", ex); }
    }
}
