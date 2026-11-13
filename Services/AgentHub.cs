using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>One agent event. State: working | waiting | done | idle | end. Step: read | edit | run | search | web | think | task.
/// RequestId is set when the hook is waiting for an Allow/Deny answer from the island.</summary>
public sealed record AgentMessage(string Agent, string? Session, string State, string? Detail, string? Cwd,
    string? Step = null, string? File = null, string? Diff = null, string? RequestId = null, string? Command = null);

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
            using var client = ConnectOwnPipe(ReplyPipe(requestId), 1000);
            if (client == null) return;
            client.Write(Encoding.UTF8.GetBytes(decision));
            client.Flush();
        }
        catch { }
    });

    // PipeOptions.CurrentUserOnly compares the pipe's owner with the token's *default owner*, which is not the
    // same everywhere for one user: an elevated process owns what it creates as "Administrators", while Git Bash
    // (where Claude Code runs its hooks) sets the owner to the user. The island and its hooks then refused each
    // other and every hook message arrived empty. So both ends use the user's SID explicitly instead.
    static SecurityIdentifier CurrentUser => WindowsIdentity.GetCurrent().User!;

    /// <summary>A pipe server owned by, and only open to, the current user, whatever the process's default owner.</summary>
    public static NamedPipeServerStream CreateOwnPipe(string name, int maxInstances)
    {
        var user = CurrentUser;
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.In, maxInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>Connects to a pipe made by <see cref="CreateOwnPipe"/>; null if it isn't there or someone else owns it.</summary>
    public static NamedPipeClientStream? ConnectOwnPipe(string name, int timeoutMs)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
        try
        {
            client.Connect(timeoutMs);
            if (client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && owner == CurrentUser)
                return client;
        }
        catch { }
        client.Dispose();
        return null;
    }
    // Diffs and full commands are clipped by AgentCli (4000 chars each), so this is ample; a longer message
    // is dropped (and logged) rather than parsed half.
    const int MaxMessage = 64 * 1024;

    readonly CancellationTokenSource _cts = new();

    /// <summary>Raised on the UI thread.</summary>
    public event Action<AgentMessage>? MessageReceived;

    public void Start() => _ = Task.Run(LoopAsync);

    // A fresh server instance is listening again as soon as a hook connects: several agents (or a tool's
    // Pre/PostToolUse in quick succession) are read in parallel instead of queuing behind one slow client.
    // They are still handed to the UI in the order they connected, so "done" never lands before the last step.
    async Task LoopAsync()
    {
        var token = _cts.Token;
        var previous = Task.CompletedTask;
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateOwnPipe(PipeName, NamedPipeServerStream.MaxAllowedServerInstances);
                await server.WaitForConnectionAsync(token);
                var connected = server;
                server = null;
                var before = previous;
                previous = Task.Run(() => ServeAsync(connected, before, token), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Error("AgentHub.Listen", ex);
                try { await Task.Delay(500, token); } catch { break; }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    async Task ServeAsync(NamedPipeServerStream server, Task before, CancellationToken token)
    {
        AgentMessage? message = null;
        await using (server)
        {
            try
            {
                var buffer = new byte[MaxMessage + 1];
                var total = 0;
                int read;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                while (total < buffer.Length && (read = await server.ReadAsync(buffer.AsMemory(total), timeout.Token)) > 0)
                    total += read;
                if (total > MaxMessage) Log.Warn("AgentHub", $"message over {MaxMessage} bytes dropped");
                else message = JsonSerializer.Deserialize<AgentMessage>(Encoding.UTF8.GetString(buffer, 0, total), JsonSerializerOptions.Web);
            }
            catch (OperationCanceledException) { } // client never finished writing, or the island is closing
            catch (JsonException ex) { Log.Error("AgentHub.Parse", ex); }
            catch (Exception ex) { Log.Error("AgentHub.Read", ex); }
        }

        // Always waits for the previous connection, even when this one failed, so the order chain never breaks.
        try { await before; } catch { }
        if (message is not { Agent.Length: > 0, State.Length: > 0 } || token.IsCancellationRequested) return;
        try { await ui.InvokeAsync(() => MessageReceived?.Invoke(message)); }
        catch (Exception ex) { Log.Error("AgentHub.Dispatch", ex); }
    }

    public void Dispose() => _cts.Cancel();
}
