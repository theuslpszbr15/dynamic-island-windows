using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>"DynamicIsland.exe send &lt;files&gt;" (a script, the Explorer, another app): the running island offers those files to
/// the paired phone, which asks before receiving. Only this user's processes can reach the pipe.</summary>
public sealed class SendPipe(Dispatcher ui) : IDisposable
{
    public static string PipeName => "DynamicIsland.Send." + Environment.UserName;
    const int MaxMessage = 64 * 1024;

    readonly CancellationTokenSource _cts = new();

    /// <summary>Raised on the UI thread with the existing files asked for.</summary>
    public event Action<IReadOnlyList<string>>? Files;

    public void Start() => _ = Task.Run(LoopAsync);

    async Task LoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = AgentHub.CreateOwnPipe(PipeName, 1);
                await server.WaitForConnectionAsync(token);
                var paths = await ReadAsync(server, token);
                if (paths.Count > 0) await ui.InvokeAsync(() => Files?.Invoke(paths));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Error("SendPipe", ex);
                try { await Task.Delay(500, token); } catch { break; }
            }
        }
    }

    static async Task<IReadOnlyList<string>> ReadAsync(Stream pipe, CancellationToken token)
    {
        var buffer = new byte[MaxMessage + 1];
        var total = 0;
        int read;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        while (total < buffer.Length && (read = await pipe.ReadAsync(buffer.AsMemory(total), timeout.Token)) > 0) total += read;
        if (total > MaxMessage) return [];
        return Parse(Encoding.UTF8.GetString(buffer, 0, total));
    }

    /// <summary>A JSON array of paths; only files that exist, at most 20 (what one offer to the phone takes).</summary>
    public static IReadOnlyList<string> Parse(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json) ?? [])
                .Where(p => p.Length is > 0 and < 1024 && File.Exists(p)).Select(Path.GetFullPath).Distinct().Take(20).ToList();
        }
        catch (JsonException) { return []; }
    }

    /// <summary>The command line side; exit code 0 when the island got it, 1 when it isn't running.</summary>
    public static int Run(string[] paths)
    {
        var full = paths.Select(Path.GetFullPath).Where(File.Exists).ToArray();
        if (full.Length == 0)
        {
            Console.Error.WriteLine("Nenhum arquivo encontrado.");
            return 1;
        }
        using var client = AgentHub.ConnectOwnPipe(PipeName, 1500);
        if (client == null)
        {
            Console.Error.WriteLine("A ilha não está aberta.");
            return 1;
        }
        client.Write(JsonSerializer.SerializeToUtf8Bytes(full));
        client.Flush();
        return 0;
    }

    public void Dispose() => _cts.Cancel();
}
