namespace DynamicIsland.Services;

/// <summary>
/// Writes a settings/data file so a crash or power cut mid-write never leaves it half written: the text goes
/// to a temporary file, is flushed to disk, then replaces the old file (which is kept as .bak).
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(text));

    /// <param name="backup">Keep the previous version as .bak (off for secrets: an old token must not linger).</param>
    public static void WriteAllBytes(string path, byte[] bytes, bool backup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        if (File.Exists(path))
        {
            try { File.Replace(temp, path, backup ? path + ".bak" : null, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // e.g. a file system without ReplaceFile: plain move below
            }
        }
        File.Move(temp, path, true);
    }

    /// <summary>The file's text, or the last good copy (.bak) if the file is missing or <paramref name="valid"/>
    /// rejects it. A rejected file is kept aside as .corrupt so a later save doesn't erase it.</summary>
    public static string? ReadAllText(string path, Func<string, bool> valid)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            string text;
            try
            {
                if (!File.Exists(candidate)) continue;
                text = File.ReadAllText(candidate);
            }
            catch (Exception ex)
            {
                Log.Error($"AtomicFile.Read {Path.GetFileName(candidate)}", ex);
                continue;
            }
            if (valid(text)) return text;
            Log.Warn("AtomicFile.Read", $"{Path.GetFileName(candidate)} is unreadable, kept as .corrupt");
            try { File.Copy(candidate, $"{candidate}.{DateTime.Now:yyyyMMdd-HHmmss}.corrupt", true); } catch { }
        }
        return null;
    }
}
