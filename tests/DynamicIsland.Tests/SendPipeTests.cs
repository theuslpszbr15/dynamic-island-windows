using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class SendPipeTests
{
    [Fact]
    public void KeepsOnlyExistingFiles()
    {
        var file = Path.GetTempFileName();
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new[] { file, file, @"C:\nao\existe.mp4", "" });
            Assert.Equal([Path.GetFullPath(file)], SendPipe.Parse(json));
            Assert.Empty(SendPipe.Parse("isto não é json"));
            Assert.Empty(SendPipe.Parse("{\"a\":1}"));
        }
        finally { File.Delete(file); }
    }
}
