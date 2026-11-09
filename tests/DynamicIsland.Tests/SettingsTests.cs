using System.Windows.Input;
using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class HotkeyTests
{
    [Fact]
    public void ParsesAndFormatsShortcuts()
    {
        Assert.Equal((Hotkeys.MOD_CONTROL | Hotkeys.MOD_ALT, (uint)0x44), Hotkeys.Parse("Ctrl+Alt+D"));
        Assert.Equal((Hotkeys.MOD_CONTROL | Hotkeys.MOD_SHIFT, (uint)0x78), Hotkeys.Parse("Ctrl+Shift+F9"));
        Assert.Equal((Hotkeys.MOD_WIN, (uint)0x35), Hotkeys.Parse("Win+5"));
        Assert.Equal("Ctrl+Alt+5", Hotkeys.Format(ModifierKeys.Control | ModifierKeys.Alt, Key.D5));
    }

    [Theory]
    [InlineData("D")]             // no modifier
    [InlineData("Shift+D")]       // Shift alone would steal normal typing
    [InlineData("Ctrl+Alt")]      // no key
    [InlineData("Ctrl+Alt+D+E")]  // two keys
    [InlineData("Ctrl+Space")]    // not a letter, digit or F-key
    public void RejectsUnusableShortcuts(string text) => Assert.Null(Hotkeys.Parse(text));

    [Fact]
    public void FallsBackToTheDefaultWhenTheStoredOneIsBroken()
    {
        var settings = new IslandSettings { ReadOnly = true };
        settings.Hotkeys["agents"] = "nonsense";
        Assert.Equal("Ctrl+Alt+A", Hotkeys.Keys(settings, "agents"));
        settings.Hotkeys["agents"] = "Ctrl+Shift+A";
        Assert.Equal("Ctrl+Shift+A", Hotkeys.Keys(settings, "agents"));
    }
}

public class ThemeTests
{
    [Theory]
    [InlineData("Vidro (desfocado)", IslandThemes.Glass)]  // older settings files stored the display name
    [InlineData("Porcelana (claro)", IslandThemes.Light)]
    [InlineData("Ameixa", "plum")]
    [InlineData("ocean", "ocean")]
    [InlineData("tema que não existe", IslandThemes.Default)]
    [InlineData(null, IslandThemes.Default)]
    public void OldNamesMoveToStableIds(string? stored, string id) => Assert.Equal(id, IslandThemes.Normalize(stored));
}

public class AtomicFileTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "di-tests-" + Guid.NewGuid().ToString("N"));
    string File1 => Path.Combine(_folder, "settings.json");

    [Fact]
    public void KeepsThePreviousVersionAsBackup()
    {
        AtomicFile.WriteAllText(File1, "one");
        AtomicFile.WriteAllText(File1, "two");
        Assert.Equal("two", File.ReadAllText(File1));
        Assert.Equal("one", File.ReadAllText(File1 + ".bak"));
        Assert.False(File.Exists(File1 + ".tmp"));
    }

    [Fact]
    public void ACorruptFileFallsBackToTheBackupAndIsKeptAside()
    {
        AtomicFile.WriteAllText(File1, "{\"ok\":1}");
        AtomicFile.WriteAllText(File1, "{\"ok\":2}");
        File.WriteAllText(File1, "{\"ok\":"); // power cut mid-write (what the old File.WriteAllText could leave)
        var text = AtomicFile.ReadAllText(File1, t => t.EndsWith('}'));
        Assert.Equal("{\"ok\":1}", text);
        Assert.Single(Directory.GetFiles(_folder, "*.corrupt"));
    }

    [Fact]
    public void SecretsLeaveNoBackup()
    {
        AtomicFile.WriteAllBytes(File1, [1], backup: false);
        AtomicFile.WriteAllBytes(File1, [2], backup: false);
        Assert.False(File.Exists(File1 + ".bak"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { }
    }
}
