namespace DynamicIsland.Services;

/// <summary>
/// Appends failures to %APPDATA%\DynamicIsland\error.log (kept under 1 MB, one older copy). Only for errors
/// worth a trace: expected misses (no island running, device unplugged) stay silent.
/// </summary>
public static class Log
{
    const long MaxBytes = 1024 * 1024;
    static readonly object Gate = new();

    static string FilePath => Path.Combine(IslandSettings.Folder, "error.log");

    public static void Error(string where, Exception ex) => Write($"{where}: {ex}");

    public static void Warn(string where, string message) => Write($"{where}: {message}");

    static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(IslandSettings.Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"[{DateTime.Now:O}] {text}{Environment.NewLine}");
            }
        }
        catch { } // logging must never take the island down
    }
}
