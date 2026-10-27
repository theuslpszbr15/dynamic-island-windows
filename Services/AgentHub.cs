using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>One agent event. State: working | waiting | done | idle | end. Step: read | edit | run | search | web | think | task.
/// RequestId is set when the hook is waiting for an Allow/Deny answer from the island.</summary>
public sealed record AgentMessage(string Agent, string? Session, string State, string? Detail, string? Cwd,
    string? Step = null, string? File = null, string? Diff = null, string? RequestId = null);

/// <summary>Receives agent events from hook processes over a per-user named pipe.</summary>
public sealed class AgentHub(Dispatcher ui) : IDisposable
{
    public static string PipeName => "DynamicIsland.Agents." + Environment.UserName;
    public static string ReplyPipe(string requestId) => $"DynamicIsland.Reply.{Environment.UserName}.{requestId}";

    /// <summary>Answers a waiting hook ("allow" or "deny"); does nothing if it already gave up.</summary>
    public static Task Reply(string requestId, string decision) => Task.Run(() =>
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ReplyPipe(requestId), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1000);
            client.Write(Encoding.UTF8.GetBytes(decision));
            client.Flush();
        }
        catch { }
    });
    const int MaxMessage = 16 * 1024;

    readonly CancellationTokenSource _cts = new();

    /// <summary>Raised on the UI thread.</summary>
    public event Action<AgentMessage>? MessageReceived;

    public void Start() => _ = Task.Run(LoopAsync);

    async Task LoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);

                var buffer = new byte[MaxMessage];
                var total = 0;
                int read;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                while (total < buffer.Length && (read = await server.ReadAsync(buffer.AsMemory(total), timeout.Token)) > 0)
                    total += read;

                var message = JsonSerializer.Deserialize<AgentMessage>(Encoding.UTF8.GetString(buffer, 0, total), JsonSerializerOptions.Web);
                if (message is { Agent.Length: > 0, State.Length: > 0 })
                    await ui.InvokeAsync(() => MessageReceived?.Invoke(message));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch
            {
                try { await Task.Delay(100, token); } catch { break; }
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
