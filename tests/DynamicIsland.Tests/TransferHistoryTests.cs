using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class TransferHistoryTests
{
    static TransferEntry File(string id, bool sent = true, string status = "Esperando o celular") =>
        new(id, DateTime.Now, sent, id + ".jpg", 1234, 1, status, [@"C:\fotos\" + id + ".jpg"], null);

    [Fact]
    public void NewestFirstAndCapped()
    {
        var history = new TransferHistory(null);
        for (var i = 0; i < TransferHistory.MaxEntries + 5; i++) history.Add(File("a" + i));
        Assert.Equal(TransferHistory.MaxEntries, history.Entries.Count);
        Assert.Equal("a" + (TransferHistory.MaxEntries + 4), history.Entries[0].Id);
    }

    [Fact]
    public void StatusRemoveAndClearPerSide()
    {
        var history = new TransferHistory(null);
        var changes = 0;
        history.Changed += () => changes++;
        history.Add(File("x"));
        history.Add(File("x", sent: false, status: "Recebido"));
        history.SetStatus("x", true, "No celular");
        Assert.Equal("No celular", history.Entries.Single(e => e.Sent).Status);
        Assert.Equal("Recebido", history.Entries.Single(e => !e.Sent).Status);
        history.SetStatus("missing", true, "No celular"); // nothing happens
        history.Remove("x", true);
        Assert.Single(history.Entries);
        history.Clear(false);
        Assert.Empty(history.Entries);
        Assert.Equal(5, changes);
    }

    [Fact]
    public void LongTextsAreCut()
    {
        var history = new TransferHistory(null);
        history.Add(new TransferEntry("t", DateTime.Now, false, "Texto", 0, 1, "Copiado", [], new string('a', TransferHistory.MaxTextChars + 10)));
        Assert.Equal(TransferHistory.MaxTextChars, history.Entries[0].Text!.Length);
    }

    [Fact]
    public void KeptEncryptedOnDisk()
    {
        var folder = Path.Combine(Path.GetTempPath(), "di-history-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(folder, "transfers.bin");
        try
        {
            var history = new TransferHistory(path);
            history.Add(new TransferEntry("t", DateTime.Now, true, "Texto", 5, 1, "No celular", [], "segredo123"));
            history.Add(File("foto"));
            Assert.DoesNotContain("segredo123", System.Text.Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(path)));

            var again = new TransferHistory(path);
            Assert.Equal(["foto", "t"], again.Entries.Select(e => e.Id));
            Assert.Equal("segredo123", again.Entries[1].Text);
            Assert.Equal([@"C:\fotos\foto.jpg"], again.Entries[0].Paths);

            System.IO.File.WriteAllBytes(path, [1, 2, 3]); // damaged: starts over instead of failing
            Assert.Empty(new TransferHistory(path).Entries);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }
}
