using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>Someone joined or left the voice channel the user is in.</summary>
public sealed record DiscordVoiceEvent(bool Joined, string UserId, string Name, string Channel, int Count, BitmapSource? Avatar);

/// <summary>
/// Who joins and leaves the voice call the user is in, from the Discord app running on this PC (local RPC over the
/// discord-ipc named pipe; nothing goes through a server of ours). Needs the user's own Discord application (client id
/// and secret, see Configurações › Na ilha): Discord only lets an app's owner and its testers use RPC.
/// The app id, secret and tokens live in a DPAPI blob for this Windows user and are never logged.
/// Authorizing pops up Discord's own consent dialog, so it only happens when the user clicks "Conectar".
/// </summary>
public sealed class DiscordVoice(Dispatcher dispatcher) : IDisposable
{
    const string Scopes = "rpc rpc.voice.read";
    /// <summary>Registered under OAuth2 › Redirects of the user's app; the token exchange must name one.</summary>
    public const string RedirectUri = "http://localhost";
    const int MaxFrame = 1 << 20;
    static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);

    static string SecretFile => Path.Combine(IslandSettings.Folder, "discord.bin");
    static readonly byte[] Purpose = Encoding.UTF8.GetBytes("DynamicIsland.Discord");

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = MaxFrame };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland");
        return http;
    }

    sealed record Member(string Id, string Name, string? Avatar, string? Discriminator, bool Bot);

    sealed class Secrets
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTimeOffset Expires { get; set; }
    }

    Secrets _secrets = LoadSecrets();
    CancellationTokenSource? _cts;
    NamedPipeClientStream? _pipe;
    readonly SemaphoreSlim _write = new(1, 1);
    readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = [];
    readonly Dictionary<string, Member> _members = [];
    readonly Dictionary<string, BitmapSource?> _avatars = [];
    TaskCompletionSource? _authorizeAsked;
    /// <summary>"Conectar" was clicked and not yet acted on (so a reconnect doesn't make the user click twice).</summary>
    volatile bool _wantAuthorize;
    string? _channelId, _channelName, _me;
    DateTime _quietUntil;

    public event Action<DiscordVoiceEvent>? Changed;
    public event Action? StatusChanged;

    /// <summary>What Configurações shows (Portuguese).</summary>
    public string Status { get; private set; } = "Desligado";
    public bool Running => _cts != null;
    public bool Authenticated { get; private set; }
    public bool HasApp => _secrets.ClientId.Length > 0 && _secrets.ClientSecret.Length > 0;
    public bool HasToken => _secrets.RefreshToken.Length > 0;
    public string ClientId => _secrets.ClientId;
    /// <summary>The voice channel the user is in right now, if any.</summary>
    public string? Channel => _channelId != null ? _channelName : null;

    /// <summary>Discord application ids are snowflakes; secrets are 32 URL-safe characters (accept a little more).</summary>
    public static bool ValidClientId(string id) => id.Length is >= 15 and <= 22 && id.All(char.IsAsciiDigit);
    public static bool ValidSecret(string s) => s.Length is >= 16 and <= 128 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Saves the app's id and secret (a new app forgets the old tokens). False when either looks wrong.</summary>
    public bool SetApp(string clientId, string clientSecret)
    {
        clientId = clientId.Trim();
        clientSecret = clientSecret.Trim();
        if (!ValidClientId(clientId) || !ValidSecret(clientSecret)) return false;
        if (clientId != _secrets.ClientId) _secrets = new Secrets { ClientId = clientId };
        _secrets.ClientSecret = clientSecret;
        SaveSecrets();
        Restart();
        return true;
    }

    /// <summary>Forgets the app and its tokens (the user can also revoke it in Discord › Apps autorizados).</summary>
    public void Forget()
    {
        _secrets = new Secrets();
        try { File.Delete(SecretFile); } catch { }
        Restart();
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        ClosePipe();
        SetStatus("Desligado", false);
    }

    void Restart()
    {
        if (_cts == null) return;
        Stop();
        Start();
    }

    /// <summary>The user clicked "Conectar": the next (or current) connection asks Discord for consent.</summary>
    public void Authorize()
    {
        _wantAuthorize = true;
        _authorizeAsked?.TrySetResult();
        if (_cts == null) Start();
    }

    public void Dispose() => Stop();

    // ───────────────────────── connection ─────────────────────────

    async Task RunAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (!HasApp) { SetStatus("Coloque o Client ID e o Client Secret do seu app do Discord.", false); return; }
                if (await ConnectAsync(cancel) is not { } pipe)
                {
                    SetStatus("Discord fechado. A ilha tenta de novo sozinha.", false);
                }
                else
                {
                    _pipe = pipe;
                    await SessionAsync(pipe, cancel);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
            catch (DiscordError ex) when (ex.Fatal)
            {
                // Wrong secret, no access, or the user said no: don't retry on a loop, wait for "Conectar".
                ClosePipe();
                SetStatus(ex.Message, false);
                if (!_wantAuthorize)
                {
                    _authorizeAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    try { await _authorizeAsked.Task.WaitAsync(cancel); } catch (OperationCanceledException) { return; }
                    _authorizeAsked = null;
                }
                continue;
            }
            catch (DiscordError ex) { SetStatus(ex.Message, false); }
            catch (IOException)
            {
                SetStatus("O Discord foi fechado. A ilha reconecta quando ele abrir.", false); // normal: Discord quit or restarted
            }
            catch (Exception ex)
            {
                Log.Error("DiscordVoice", ex);
                SetStatus("A conexão com o Discord caiu. Tentando de novo…", false);
            }
            finally
            {
                ClosePipe();
            }
            try { await Task.Delay(Retry, cancel); } catch (OperationCanceledException) { return; }
        }
    }

    static async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken cancel)
    {
        // Discord takes the first free one of discord-ipc-0…9 (several Discord builds can run side by side).
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(300, cancel);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
            }
        }
        return null;
    }

    void ClosePipe()
    {
        var pipe = _pipe;
        _pipe = null;
        try { pipe?.Dispose(); } catch { }
        lock (_pending)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
        _channelId = null;
        lock (_members) _members.Clear();
    }

    async Task SessionAsync(NamedPipeClientStream pipe, CancellationToken cancel)
    {
        var ready = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending["READY"] = ready;
        var reader = ReadLoopAsync(pipe, cancel);
        _readerTask = reader;
        await WriteAsync(pipe, 0, new JsonObject { ["v"] = 1, ["client_id"] = _secrets.ClientId }, cancel);
        var hello = await Wait(ready.Task, reader);
        _me = Str(hello, "user", "id");

        if (!await AuthenticateAsync(pipe, reader, cancel))
        {
            SetStatus(HasToken ? "O Discord recusou o acesso salvo. Clique em Conectar de novo."
                : "Clique em Conectar e aceite no Discord.", false);
            if (!_wantAuthorize)
            {
                _authorizeAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await Wait(_authorizeAsked.Task, reader);
                _authorizeAsked = null;
            }
            _wantAuthorize = false;
            SetStatus("Aceite o pedido na janela do Discord…", false);
            var code = Str(await CommandAsync(pipe, reader, "AUTHORIZE", new JsonObject
            {
                ["client_id"] = _secrets.ClientId, ["scopes"] = new JsonArray(Scopes.Split(' ').Select(s => (JsonNode)s).ToArray()),
            }, cancel), "code") ?? throw new DiscordError("O Discord não devolveu a autorização.");
            await ExchangeAsync(new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri,
            }, cancel);
            if (!await AuthenticateAsync(pipe, reader, cancel)) throw new DiscordError("O Discord recusou o acesso. Confira o app e tente de novo.");
        }

        await CommandAsync(pipe, reader, "SUBSCRIBE", new JsonObject(), cancel, evt: "VOICE_CHANNEL_SELECT");
        var selected = await CommandAsync(pipe, reader, "GET_SELECTED_VOICE_CHANNEL", new JsonObject(), cancel);
        await EnterChannelAsync(pipe, reader, selected.ValueKind == JsonValueKind.Object ? selected : null, cancel);
        await reader;
    }

    /// <summary>Uses the saved token (refreshing it when it is old); false when there is none or Discord refuses it.</summary>
    async Task<bool> AuthenticateAsync(NamedPipeClientStream pipe, Task reader, CancellationToken cancel)
    {
        if (!HasToken) return false;
        try
        {
            if (_secrets.Expires < DateTimeOffset.UtcNow.AddDays(1))
                await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = _secrets.RefreshToken }, cancel);
            var auth = await CommandAsync(pipe, reader, "AUTHENTICATE", new JsonObject { ["access_token"] = _secrets.AccessToken }, cancel);
            _me = Str(auth, "user", "id") ?? _me;
            return true;
        }
        catch (DiscordError ex) when (!ex.Fatal)
        {
            // An expired or revoked token: drop it so the next attempt asks again instead of looping.
            _secrets.AccessToken = _secrets.RefreshToken = "";
            SaveSecrets();
            return false;
        }
    }

    /// <summary>The OAuth2 token endpoint (code → tokens, or refresh). The secret only ever goes to discord.com.</summary>
    async Task ExchangeAsync(Dictionary<string, string> form, CancellationToken cancel)
    {
        form["client_id"] = _secrets.ClientId;
        form["client_secret"] = _secrets.ClientSecret;
        using var response = await Http.PostAsync("https://discord.com/api/v10/oauth2/token", new FormUrlEncodedContent(form), cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
        {
            var error = "";
            try { error = JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : ""; } catch { }
            throw error switch
            {
                "invalid_client" => new DiscordError("Client ID ou Client Secret errado.", fatal: true),
                "invalid_grant" when form["grant_type"] == "authorization_code" =>
                    new DiscordError($"O Discord recusou o código. Em OAuth2 › Redirects do app, adicione {RedirectUri}.", fatal: true),
                _ => new DiscordError($"O Discord recusou o acesso ({(int)response.StatusCode}).", fatal: false),
            };
        }
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        _secrets.AccessToken = root.GetProperty("access_token").GetString() ?? "";
        if (root.TryGetProperty("refresh_token", out var refresh)) _secrets.RefreshToken = refresh.GetString() ?? "";
        _secrets.Expires = DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var e2) ? e2.GetInt32() : 3600);
        SaveSecrets();
    }

    // ───────────────────────── voice ─────────────────────────

    static readonly string[] VoiceEvents = ["VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE"];

    async Task EnterChannelAsync(NamedPipeClientStream pipe, Task reader, JsonElement? channel, CancellationToken cancel)
    {
        if (_channelId != null)
            foreach (var evt in VoiceEvents)
                try { await CommandAsync(pipe, reader, "UNSUBSCRIBE", new JsonObject { ["channel_id"] = _channelId }, cancel, evt: evt); }
                catch (DiscordError) { }
        lock (_members) _members.Clear();
        _channelId = channel is { } c ? Str(c, "id") : null;
        _channelName = channel is { } n ? Str(n, "name") : null;
        if (_channelId == null)
        {
            SetStatus("Conectado. Entre numa call para ver quem chega.", true);
            return;
        }
        if (channel is { } ch && ch.TryGetProperty("voice_states", out var states) && states.ValueKind == JsonValueKind.Array)
            foreach (var state in states.EnumerateArray())
                if (ToMember(state) is { } m) lock (_members) _members[m.Id] = m;
        // The people already there aren't news; only who comes and goes from now on.
        _quietUntil = DateTime.UtcNow.AddSeconds(1.5);
        foreach (var evt in VoiceEvents)
            await CommandAsync(pipe, reader, "SUBSCRIBE", new JsonObject { ["channel_id"] = _channelId }, cancel, evt: evt);
        int here;
        lock (_members) here = _members.Count + 1;
        SetStatus($"Conectado. Na call: {_channelName} ({here} pessoas).", true);
    }

    void OnEvent(NamedPipeClientStream pipe, Task reader, string evt, JsonElement data, CancellationToken cancel)
    {
        switch (evt)
        {
            case "VOICE_CHANNEL_SELECT":
                var id = Str(data, "channel_id");
                if (id == _channelId) return;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        JsonElement? channel = null;
                        if (id != null)
                            channel = await CommandAsync(pipe, reader, "GET_CHANNEL", new JsonObject { ["channel_id"] = id }, cancel);
                        await EnterChannelAsync(pipe, reader, channel, cancel);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Log.Error("DiscordVoice.Select", ex); }
                }, cancel);
                return;

            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                if (ToMember(data) is not { } m) return;
                bool isNew;
                lock (_members)
                {
                    isNew = !_members.ContainsKey(m.Id);
                    _members[m.Id] = m;
                }
                if (isNew && evt == "VOICE_STATE_CREATE") Raise(true, m);
                return;

            case "VOICE_STATE_DELETE":
                var userId = Str(data, "user", "id");
                Member? gone;
                lock (_members) { if (userId == null || !_members.Remove(userId, out gone)) return; }
                Raise(false, gone);
                return;
        }
    }

    Member? ToMember(JsonElement state)
    {
        if (!state.TryGetProperty("user", out var user) || Str(user, "id") is not { } id || id == _me) return null;
        // As Discord shows them in the call: server nickname, then display name, then username.
        var name = Str(state, "nick") is { Length: > 0 } nick ? nick
            : Str(user, "global_name") is { Length: > 0 } global ? global
            : Str(user, "username") ?? "Alguém";
        var bot = user.TryGetProperty("bot", out var b) && b.ValueKind == JsonValueKind.True;
        return new Member(id, name, Str(user, "avatar"), Str(user, "discriminator"), bot);
    }

    void Raise(bool joined, Member m)
    {
        if (DateTime.UtcNow < _quietUntil) return;
        var channel = _channelName ?? "";
        int count;
        lock (_members) count = _members.Count + 1;
        _ = Task.Run(async () =>
        {
            var avatar = await AvatarAsync(m);
            await dispatcher.InvokeAsync(() => Changed?.Invoke(new DiscordVoiceEvent(joined, m.Id, m.Name, channel, count, avatar)));
        });
    }

    /// <summary>The profile picture from Discord's CDN (or the default one), small and cached for the session.</summary>
    async Task<BitmapSource?> AvatarAsync(Member m)
    {
        var url = AvatarUrl(m.Id, m.Avatar, m.Discriminator);
        lock (_avatars) if (_avatars.TryGetValue(url, out var cached)) return cached;
        BitmapSource? image = null;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch { }
        lock (_avatars)
        {
            if (_avatars.Count > 200) _avatars.Clear();
            _avatars[url] = image;
        }
        return image;
    }

    internal static string AvatarUrl(string userId, string? avatar, string? discriminator)
    {
        if (!string.IsNullOrEmpty(avatar) && avatar.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && userId.All(char.IsAsciiDigit))
            return $"https://cdn.discordapp.com/avatars/{userId}/{avatar}.png?size=128";
        // Default avatars: by discriminator for old usernames, by id for new ones (discriminator "0").
        var index = int.TryParse(discriminator, out var d) && d > 0 ? d % 5
            : ulong.TryParse(userId, out var id) ? (int)((id >> 22) % 6) : 0;
        return $"https://cdn.discordapp.com/embed/avatars/{index}.png";
    }

    // ───────────────────────── framing ─────────────────────────

    sealed class DiscordError(string message, bool fatal = false) : Exception(message)
    {
        public bool Fatal { get; } = fatal;
    }

    async Task<JsonElement> CommandAsync(NamedPipeClientStream pipe, Task reader, string cmd, JsonObject args, CancellationToken cancel,
        string? evt = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[nonce] = tcs;
        var message = new JsonObject { ["cmd"] = cmd, ["args"] = args, ["nonce"] = nonce };
        if (evt != null) message["evt"] = evt;
        await WriteAsync(pipe, 1, message, cancel);
        // AUTHORIZE waits for the user to click in Discord; everything else answers right away.
        var timeout = cmd == "AUTHORIZE" ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(15);
        return await Wait(tcs.Task.WaitAsync(timeout, cancel), reader);
    }

    /// <summary>Waits for <paramref name="task"/>, but fails as soon as the reader stops (Discord closed).</summary>
    static async Task<T> Wait<T>(Task<T> task, Task reader)
    {
        if (await Task.WhenAny(task, reader) == reader)
        {
            await reader;
            throw new IOException("Discord closed the pipe");
        }
        return await task;
    }

    static async Task Wait(Task task, Task reader)
    {
        if (await Task.WhenAny(task, reader) == reader)
        {
            await reader;
            throw new IOException("Discord closed the pipe");
        }
        await task;
    }

    async Task WriteAsync(NamedPipeClientStream pipe, int op, JsonObject payload, CancellationToken cancel)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, 8);
        await _write.WaitAsync(cancel);
        try
        {
            await pipe.WriteAsync(frame, cancel);
            await pipe.FlushAsync(cancel);
        }
        finally { _write.Release(); }
    }

    async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken cancel)
    {
        await Task.Yield();
        var header = new byte[8];
        while (!cancel.IsCancellationRequested)
        {
            await pipe.ReadExactlyAsync(header, cancel);
            var op = BitConverter.ToInt32(header, 0);
            var length = BitConverter.ToInt32(header, 4);
            if (length is < 0 or > MaxFrame) throw new IOException("Discord frame too large");
            var body = new byte[length];
            await pipe.ReadExactlyAsync(body, cancel);
            if (op == 2) return; // CLOSE
            if (op == 3) { await WriteRawAsync(pipe, 4, body, cancel); continue; } // PING → PONG
            if (op != 1) continue;
            using var doc = JsonDocument.Parse(body);
            Dispatch(pipe, doc.RootElement.Clone(), cancel);
        }
    }

    /// <summary>The current connection's read loop (commands sent from events wait on it too).</summary>
    Task? _readerTask;

    void Dispatch(NamedPipeClientStream pipe, JsonElement root, CancellationToken cancel)
    {
        var cmd = Str(root, "cmd");
        var evt = Str(root, "evt");
        var data = root.TryGetProperty("data", out var d) ? d : default;
        if (cmd == "DISPATCH")
        {
            if (evt == "READY") Complete("READY", data);
            else if (evt != null && _readerTask is { } reader) OnEvent(pipe, reader, evt, data, cancel);
            return;
        }
        if (Str(root, "nonce") is not { } nonce) return;
        TaskCompletionSource<JsonElement>? tcs;
        lock (_pending) { _pending.Remove(nonce, out tcs); }
        if (tcs == null) return;
        if (evt == "ERROR")
        {
            var code = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
            tcs.TrySetException(new DiscordError(code switch
            {
                5000 or 4002 => "O Discord não liberou o acesso a este app. Você é dono ou tester dele no Developer Portal?",
                4006 or 4009 => "O acesso salvo expirou. Clique em Conectar de novo.",
                5003 => "Você recusou o pedido no Discord.",
                _ => $"Discord: {Str(data, "message") ?? "erro"} ({code})",
            }, fatal: code is 5000 or 4002 or 5003));
            return;
        }
        tcs.TrySetResult(data);
    }

    void Complete(string key, JsonElement data)
    {
        TaskCompletionSource<JsonElement>? tcs;
        lock (_pending) { _pending.Remove(key, out tcs); }
        tcs?.TrySetResult(data);
    }

    async Task WriteRawAsync(NamedPipeClientStream pipe, int op, byte[] body, CancellationToken cancel)
    {
        var frame = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, 8);
        await _write.WaitAsync(cancel);
        try { await pipe.WriteAsync(frame, cancel); }
        finally { _write.Release(); }
    }

    static string? Str(JsonElement e, params string[] path)
    {
        foreach (var name in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    void SetStatus(string text, bool authenticated)
    {
        if (text == Status && authenticated == Authenticated) return; // retries repeat themselves; Configurações re-renders on change
        Status = text;
        Authenticated = authenticated;
        dispatcher.InvokeAsync(() => StatusChanged?.Invoke());
    }

    // ───────────────────────── secrets ─────────────────────────

    static Secrets LoadSecrets()
    {
        try
        {
            if (File.Exists(SecretFile))
                return JsonSerializer.Deserialize<Secrets>(
                    ProtectedData.Unprotect(File.ReadAllBytes(SecretFile), Purpose, DataProtectionScope.CurrentUser)) ?? new();
        }
        catch (Exception ex) { Log.Error("DiscordVoice.LoadSecrets", ex); }
        return new();
    }

    void SaveSecrets()
    {
        try
        {
            AtomicFile.WriteAllBytes(SecretFile,
                ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_secrets), Purpose, DataProtectionScope.CurrentUser), backup: false);
        }
        catch (Exception ex) { Log.Error("DiscordVoice.SaveSecrets", ex); }
    }
}
