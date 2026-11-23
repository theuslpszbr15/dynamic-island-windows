using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace DynamicIsland.Services;

public sealed record PhoneSession(string Id, string Agent, string Project, string State, string Detail, long UpdatedAt);

public sealed record PhoneApproval(string Fingerprint, string Agent, string Project, string Command, long CreatedAt, bool Truncated);

/// <summary>A pinned tray item as the phone sees it: no path, just a name, a kind and a small icon.</summary>
public sealed record PhoneTrayItem(string Id, string Name, string Kind, string? IconPng);

/// <summary>
/// Opt-in link to the Android app over the local network: TLS with a self-signed certificate the phone pins
/// at pairing (QR code), a shared token, newline-delimited JSON. The phone can Allow/Deny only the exact
/// request the island is still waiting on. Off: nothing listens.
/// Away from home the same TLS stream can also run through a blind relay on the web (see relay/): the PC keeps
/// a control WebSocket in a room named after the token, and each phone that joins gets its own data WebSocket,
/// carrying the raw TLS bytes. The relay never sees the token or anything inside TLS.
/// Files and text go both ways only after the other side accepts (see PhoneLink.Files.cs): the offer travels on the
/// link, the bytes on a second connection the phone opens (same route, same pinned TLS, same token).
/// </summary>
public sealed partial class PhoneLink : IDisposable
{
    public const int Version = 1;
    public const int DefaultPort = 47900;

    /// <summary>The Android app built with this PC version (android/app/build.gradle.kts, read at build time).</summary>
    public static readonly int AndroidVersionCode = int.TryParse(Metadata("AndroidVersionCode"), out var code) ? code : 0;
    public static readonly string AndroidVersionName = Metadata("AndroidVersionName") ?? "";
    public static string AndroidApkUrl => $"https://github.com/{Updater.Repository}/releases/latest/download/DynamicIsland-Android.apk";

    static string? Metadata(string key) => typeof(PhoneLink).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>Raised on the UI thread when a phone connects with an app older than <see cref="AndroidVersionCode"/>.</summary>
    public event Action<string>? OldApp;
    // MaxPeers counts paired phones; connections still in the TLS/hello stage have their own small budget (and
    // per address), so someone on the network opening sockets can't lock the real phone out. Connections from the
    // relay have no address and a budget of their own.
    const int MaxLine = 64 * 1024, MaxPeers = 6, MaxPending = 4, MaxPendingPerAddress = 2, MaxPendingRelay = 4;
    static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10), IdleTimeout = TimeSpan.FromMinutes(20);
    // Keepalives on relay sockets are WebSocket pings (and the text "ping" on the control socket): the relay's runtime
    // answers both without waking the room, so an idle link costs nothing there.
    static readonly TimeSpan RelayPing = TimeSpan.FromSeconds(20), RelayMaxBackoff = TimeSpan.FromSeconds(60);
    static readonly TimeSpan RelayKeepAlive = TimeSpan.FromSeconds(30), RelayKeepAliveTimeout = TimeSpan.FromSeconds(30);
    /// <summary>A relay that never sends the challenge is one from before it: after this long the PC counts as in.</summary>
    static readonly TimeSpan RelayChallengeWait = TimeSpan.FromSeconds(5);

    /// <summary>The relay every new pairing uses unless the user picks their own (Configurações → Celular).</summary>
    public const string PublicRelay = "wss://dynamic-island-relay.brendoribeiron.workers.dev";
    /// <summary>The relay's close codes: the PC didn't prove the room's key, and a limit was hit.</summary>
    public const int RelayWrongKey = 4401, RelayLimit = 4429;
    public const string RelayLimitMessage = "Limite do repasse atingido. Tente mais tarde ou use seu próprio repasse.";
    public const string RelayWrongKeyMessage = "O repasse não reconheceu este PC. Use \"Parear de novo\" nas Configurações.";

    internal static string SecretFile => DemoMode
        ? Path.Combine(Path.GetTempPath(), $"dynamic-island-demo-{Environment.ProcessId}.bin")
        : Path.Combine(IslandSettings.Folder, "phone-link.bin");

    /// <summary>Screenshots and perf runs: a throwaway token and certificate and a made-up address, so a published
    /// screenshot's QR code never pairs with the PC that took it.</summary>
    internal static bool DemoMode;

    readonly Dispatcher _ui;
    readonly object _gate = new();
    readonly List<Peer> _peers = [];
    TcpListener? _listener;
    UdpClient? _discovery;
    CancellationTokenSource? _cts;
    X509Certificate2? _cert;
    byte[] _pfx = [];
    string _token = "";
    string _sessions = """{"type":"sessions","sessions":[]}""";
    string _tray = """{"type":"tray","items":[]}""";
    string? _pcState;
    readonly Dictionary<string, string> _approvals = []; // fingerprint -> JSON line

    public PhoneLink(Dispatcher ui) => _ui = ui;

    public bool Running => _listener != null;
    public int Port { get; private set; }
    public string Fingerprint => _cert == null ? "" : Convert.ToHexString(SHA256.HashData(_cert.RawData)).ToLowerInvariant();
    public int ConnectedPhones { get { lock (_gate) return _peers.Count(p => p.Authenticated); } }

    /// <summary>The relay origin (wss://host), or "" when off.</summary>
    public string Relay { get; private set; } = "";
    /// <summary>The control WebSocket to the relay is open.</summary>
    public bool RelayConnected { get; private set; }
    CancellationTokenSource? _relayCts;
    int _relayGeneration;

    /// <summary>Raised on the UI thread: (fingerprint, "allow" | "deny").</summary>
    public event Action<string, string>? Decision;
    /// <summary>Raised on the UI thread with a tray item id the phone wants opened.</summary>
    public event Action<string>? Open;
    public event Action? PhonesChanged;
    /// <summary>Raised on the UI thread with text the phone copied (clipboard sync).</summary>
    public event Action<string>? Clip;
    /// <summary>Text dictated on the phone, to be typed where the person is on the PC.</summary>
    public event Action<string>? Type;
    const int MaxTypeChars = 2000;
    /// <summary>Raised on the UI thread: (action, minutes) from the phone's remote control.</summary>
    public event Action<string, int>? Remote;
    /// <summary>Raised on the UI thread: ("ringing" | "offhook" | "idle", contact name, number) for the phone's calls.</summary>
    public event Action<string, string, string>? Call;
    /// <summary>A phone says which notices are switched off on its island (kinds, Unix ms of the change).</summary>
    public event Action<IReadOnlyList<string>, long>? FilterFromPhone;

    /// <summary>Clipboard text that travels between the PC and the phone (a line stays well under <see cref="MaxLine"/>).</summary>
    public const int MaxClipChars = 16_000;
    public static readonly string[] RemoteActions =
        ["play", "next", "prev", "volup", "voldown", "mute", "lock", "shutdown", "cancelShutdown"];

    public void Start()
    {
        if (Running) return;
        LoadSecrets();
        foreach (var port in new[] { DefaultPort, 0 })
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start(8);
                _listener = listener;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                break;
            }
            catch (SocketException) { }
        }
        if (_listener == null) throw new IOException("Nenhuma porta livre para a ligação com o celular.");
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_listener, _cts.Token);
        StartDiscovery(_cts.Token);
        RestartRelay();
    }

    /// <summary>Sets the relay (any form <see cref="NormalizeRelay"/> accepts; "" turns it off) and reconnects.
    /// <paramref name="own"/>: the user's own relay, which gets the relay key (<see cref="RelayKey"/>); the public
    /// relay never does.</summary>
    public void SetRelay(string? relay, bool own = true)
    {
        var normalized = NormalizeRelay(relay) ?? "";
        if (normalized == Relay && own == _relayOwn) return;
        Relay = normalized;
        _relayOwn = own;
        if (Running) RestartRelay();
    }

    bool _relayOwn = true;

    /// <summary>The key relay connections send ("" = none): only an own relay gets one.</summary>
    string RelayKeyInUse => _relayOwn ? _relayKey : "";

    /// <summary>Why the relay won't take this PC right now (a limit, an unknown key), or null. In words for the screen.</summary>
    public string? RelayProblem { get; private set; }
    /// <summary>Raised on the UI thread when <see cref="RelayProblem"/> appears (once per problem).</summary>
    public event Action<string>? RelayTrouble;

    void SetRelayProblem(string? problem)
    {
        if (RelayProblem == problem) return;
        RelayProblem = problem;
        _ = _ui.InvokeAsync(() =>
        {
            PhonesChanged?.Invoke();
            if (problem != null) RelayTrouble?.Invoke(problem);
        });
        if (problem == RelayLimitMessage) _ = ForgetProblemLater(problem);
    }

    // A limit passes (the minute, or the day): the next try finds out again.
    async Task ForgetProblemLater(string problem)
    {
        await Task.Delay(TimeSpan.FromMinutes(15));
        if (RelayProblem == problem) SetRelayProblem(null);
    }

    /// <summary>A relay close code in words for the island, or null when it isn't one the person can act on.</summary>
    public const string RelayRefusedPublicMessage =
        "O repasse público está recusando conexões agora. Crie o seu (grátis) em \"Criar meu repasse\", logo abaixo.";
    public const string RelayRefusedOwnMessage = "Seu repasse recusou a chave: confira a chave do repasse abaixo.";

    public static string? RelayCloseMessage(int? code) => code switch
    {
        RelayLimit => RelayLimitMessage,
        RelayWrongKey => RelayWrongKeyMessage,
        _ => null,
    };

    /// <summary>The relay's access key for an own relay, sent as "Authorization: Bearer …" on every relay connection: keeps
    /// strangers from using it as a free tunnel. It is not what protects the link (TLS and the token do); phones get it
    /// in the QR (rk=) or in the welcome, inside the authenticated TLS. Stored with the token (DPAPI).</summary>
    public string RelayKey
    {
        get { LoadSecrets(); return _relayKey; }
    }
    string _relayKey = "";

    /// <summary>Saves a new relay key ("" = none) and reconnects. False if it isn't a valid key.</summary>
    public bool SetRelayKey(string? key)
    {
        var k = key?.Trim() ?? "";
        if (k.Length > 0 && !ValidRelayKey(k)) return false;
        LoadSecrets();
        if (k == _relayKey) return true;
        _relayKey = k;
        SaveSecrets();
        if (Running) RestartRelay();
        return true;
    }

    /// <summary>A phone that finished the hello: its name, whether it came through the relay, and since when.</summary>
    public sealed record PhoneInfo(string Device, bool ViaRelay, DateTime Since);

    public IReadOnlyList<PhoneInfo> Phones
    {
        get { lock (_gate) return _peers.Where(p => p.Authenticated).Select(p => new PhoneInfo(p.Device, p.Relay, p.Since)).ToList(); }
    }

    /// <summary>Raised on the UI thread when a phone authenticates: (device name, came through the relay).</summary>
    public event Action<string, bool>? PhoneJoined;

    /// <summary>16-128 characters of base64url (A-Z a-z 0-9 - _).</summary>
    public static bool ValidRelayKey(string key) =>
        key.Length is >= 16 and <= 128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string NewRelayKey() => NewToken();

    public void Stop()
    {
        StopRelay();
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _discovery?.Dispose();
        _discovery = null;
        lock (_gate)
        {
            foreach (var peer in _peers) peer.Close();
            _peers.Clear();
        }
        ForgetOffers();
        PhonesChanged?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        _cert?.Dispose();
    }

    /// <summary>A new token: the paired phone is disconnected and has to scan again.</summary>
    public void PairAgain()
    {
        _token = NewToken();
        _relaySeed = Ed25519.NewSeed(); // a new room gets a new key on the relay too
        SetRelayProblem(null);
        SaveSecrets();
        ForgetOffers();
        lock (_gate)
        {
            foreach (var peer in _peers) peer.Close();
            _peers.Clear();
        }
        if (Running) RestartRelay(); // the room comes from the token: the old one is left
        PhonesChanged?.Invoke();
    }

    public string PairingLink() =>
        BuildPairingLink(LocalAddress() ?? "127.0.0.1", Port, Fingerprint, _token, DemoMode ? "MEU-PC" : Environment.MachineName, Relay, RelayKeyInUse);

    /// <summary>The QR code's link. <paramref name="relay"/> and <paramref name="relayKey"/> ("" = none) are
    /// optional parameters; the key only goes along with a relay (an own relay with a key: the public one has none).</summary>
    public static string BuildPairingLink(string host, int port, string fingerprint, string token, string name, string relay, string relayKey = "") =>
        $"dynamicisland://pair?v={Version}&host={host}&port={port}&fp={fingerprint}&token={token}" +
        $"&name={Uri.EscapeDataString(name)}" +
        (relay.Length > 0 ? $"&relay={Uri.EscapeDataString(relay)}" : "") +
        (relay.Length > 0 && relayKey.Length > 0 ? $"&rk={relayKey}" : "");

    /// <summary>The relay room for a pairing token: lowercase hex SHA-256 of "dynamicisland-room|" + token.
    /// Only someone with the token can find the room, and the room name doesn't reveal the token.</summary>
    public static string RoomId(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dynamicisland-room|" + token))).ToLowerInvariant();

    /// <summary>A relay as its origin "wss://hostname" (lowercase, no port, path, query or user info), or null if
    /// it isn't one. "relay.example.com" alone means wss://; ws:// and http(s):// are refused, a trailing "/" is fine.
    /// Empty input is null too.</summary>
    public static string? NormalizeRelay(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0 || text.Length > 253 + 6) return null;
        if (!text.Contains("://")) text = "wss://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "wss") return null;
        if (uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.AbsolutePath != "/" || uri.HostNameType != UriHostNameType.Dns) return null;
        if (text.TrimEnd('/').Count(c => c == '/') != 2) return null; // "wss://host/" at most
        return "wss://" + uri.IdnHost.ToLowerInvariant();
    }

    // ───────────────────────── state sent to phones ─────────────────────────

    public void PublishSessions(IEnumerable<PhoneSession> sessions)
    {
        var list = new JsonArray();
        foreach (var s in sessions)
            list.Add(new JsonObject
            {
                ["id"] = s.Id, ["agent"] = s.Agent, ["project"] = Cut(s.Project, 64), ["state"] = s.State,
                ["detail"] = Cut(s.Detail, 200), ["updatedAt"] = s.UpdatedAt,
            });
        _sessions = new JsonObject { ["type"] = "sessions", ["sessions"] = list }.ToJsonString();
        Broadcast(_sessions);
    }

    public void PublishTray(IEnumerable<PhoneTrayItem> items)
    {
        var list = new JsonArray();
        var budget = 40 * 1024; // the phone drops lines over 64 KiB; icons go first
        foreach (var item in items.Take(60))
        {
            var o = new JsonObject { ["id"] = item.Id, ["name"] = Cut(item.Name, 80), ["kind"] = item.Kind };
            if (item.IconPng != null && item.IconPng.Length < budget)
            {
                o["icon"] = item.IconPng;
                budget -= item.IconPng.Length;
            }
            list.Add(o);
        }
        _tray = new JsonObject { ["type"] = "tray", ["items"] = list }.ToJsonString();
        Broadcast(_tray);
    }

    public void PublishApproval(PhoneApproval a)
    {
        var line = new JsonObject
        {
            ["type"] = "approval", ["fingerprint"] = a.Fingerprint, ["agent"] = a.Agent, ["project"] = Cut(a.Project, 64),
            ["command"] = a.Command, ["createdAt"] = a.CreatedAt, ["truncated"] = a.Truncated,
        }.ToJsonString();
        lock (_gate) _approvals[a.Fingerprint] = line;
        Broadcast(line);
    }

    public void PublishResolved(string fingerprint)
    {
        lock (_gate)
            if (!_approvals.Remove(fingerprint)) return;
        Broadcast(new JsonObject { ["type"] = "approvalResolved", ["fingerprint"] = fingerprint }.ToJsonString());
    }

    /// <summary>Someone joined or left the Discord call: the phone's floating island shows it. Only names, the
    /// channel, how many are in it and a small PNG of the avatar, inside the same pinned TLS as everything else.</summary>
    public void PublishDiscord(bool joined, string name, string channel, int count, byte[]? avatarPng) =>
        Broadcast(DiscordLine(joined, name, channel, count, avatarPng));

    /// <summary>Avatars bigger than this go without the picture (the phone drops bigger ones too).</summary>
    public const int MaxDiscordAvatarBytes = 24 * 1024;

    public static string DiscordLine(bool joined, string name, string channel, int count, byte[]? avatarPng)
    {
        var o = new JsonObject
        {
            ["type"] = "discord", ["event"] = joined ? "join" : "leave", ["name"] = Cut(name, 64), ["channel"] = Cut(channel, 64),
            ["count"] = Math.Clamp(count, 0, 999),
        };
        if (avatarPng is { Length: > 0 and <= MaxDiscordAvatarBytes }) o["avatar"] = Convert.ToBase64String(avatarPng);
        return o.ToJsonString();
    }

    /// <summary>The notice kinds the phone's island knows (Pill.Kind in the app, minus the resting pill).</summary>
    internal static readonly string[] FilterKinds = ["Approval", "Claude", "Offer", "Transfer", "Discord", "Media", "Call", "Outgoing"];

    /// <summary>The "off" list of a {"type":"filter"} line: only known kinds, no repeats; null if it isn't a list of strings.</summary>
    internal static List<string>? CleanKinds(JsonElement root)
    {
        if (!root.TryGetProperty("off", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var kinds = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return null;
            var kind = item.GetString();
            if (kind != null && Array.IndexOf(FilterKinds, kind) >= 0 && !kinds.Contains(kind)) kinds.Add(kind);
        }
        return kinds;
    }

    /// <summary>The newest change wins; the same instant is no change (so a line bounced back stops there).</summary>
    internal static bool FilterIsNewer(long currentAt, long incomingAt) => incomingAt > currentAt;

    internal static string FilterLine(IEnumerable<string> off, long at) => new JsonObject
    {
        ["type"] = "filter", ["off"] = new JsonArray(off.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()), ["at"] = at,
    }.ToJsonString();

    string? _filter;

    /// <summary>The notice filter every phone should have: kept for phones that join later, sent to the ones connected now.</summary>
    public void PublishFilter(IEnumerable<string> off, long at)
    {
        var line = FilterLine(off, at);
        _filter = line;
        Broadcast(line);
    }

    void Broadcast(string line)
    {
        List<Peer> peers;
        lock (_gate) peers = _peers.Where(p => p.Authenticated).ToList();
        foreach (var peer in peers) peer.Enqueue(line);
    }

    /// <summary>Text copied on the PC goes to the phone's clipboard (too long: not sent).</summary>
    public void PublishClip(string text)
    {
        if (text.Length is 0 or > MaxClipChars) return;
        Broadcast(new JsonObject { ["type"] = "clip", ["text"] = text }.ToJsonString());
    }

    /// <summary>A notification shown on the PC's island, repeated on the phone (apps from 1.5.6 show it).</summary>
    public void PublishNotification(string app, string title, string body) =>
        Broadcast(new JsonObject { ["type"] = "pcNotify", ["app"] = Cut(app, 60), ["title"] = Cut(title, 120), ["body"] = Cut(body, 400) }.ToJsonString());

    /// <summary>What the phone's remote shows: the song, the volume and a pending shutdown (Unix ms, 0 = none).</summary>
    public void PublishPcState(string title, string artist, bool playing, int volume, bool muted, long shutdownAt)
    {
        var line = new JsonObject
        {
            ["type"] = "pc", ["title"] = Cut(title, 120), ["artist"] = Cut(artist, 120), ["playing"] = playing,
            ["volume"] = Math.Clamp(volume, 0, 100), ["muted"] = muted, ["shutdownAt"] = shutdownAt,
        }.ToJsonString();
        if (line == _pcState) return;
        _pcState = line;
        Broadcast(line);
    }

    static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    // ───────────────────────── connections ─────────────────────────

    async Task AcceptLoop(TcpListener listener, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancel); }
            catch (OperationCanceledException) { return; } // the island is closing: clean exit
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                // A transient network error must not kill the listener (model: AgentHub.LoopAsync).
                Log.Error("PhoneLink.Accept", ex);
                try { await Task.Delay(500, cancel); } catch { return; }
                continue;
            }
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            bool full;
            // The "local network only" rule is for this listener: relay connections come in through RelayLoop.
            Peer? peer = null;
            lock (_gate)
            {
                var pending = _peers.Where(p => !p.Authenticated && !p.Relay && !p.Fetching).ToList();
                full = _peers.Count(p => p.Authenticated) >= MaxPeers || pending.Count >= MaxPending
                       || (remote != null && pending.Count(p => remote.Equals(p.Address)) >= MaxPendingPerAddress);
                if (remote != null && IsLocal(remote) && !full)
                {
                    peer = new Peer(client.GetStream(), remote, client, relay: false);
                    _peers.Add(peer);
                }
            }
            if (peer == null)
            {
                client.Dispose();
                continue;
            }
            _ = Serve(peer, cancel);
        }
    }

    /// <summary>One phone, over the LAN or the relay: TLS as server, the hello with the token, then the session.
    /// The peer is already in _peers.</summary>
    async Task Serve(Peer peer, CancellationToken cancel)
    {
        try
        {
            using var hello = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            hello.CancelAfter(HelloTimeout);
            var ssl = new SslStream(peer.Raw, false);
            peer.Stream = ssl;
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _cert,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
            }, hello.Token);

            var first = await peer.ReadLineAsync(hello.Token);
            if (first == null) return;
            using (var doc = JsonDocument.Parse(first))
            {
                var root = doc.RootElement;
                var token = root.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
                var type = Str(root, "type");
                if (type is not ("hello" or "fetch" or "push") || !SameSecret(token, _token))
                {
                    await peer.SendAsync("""{"type":"error","code":"auth","message":"Pareamento inválido. Escaneie o código de novo."}""");
                    await Task.Delay(1000, cancel);
                    return;
                }
                if (type is "fetch" or "push")
                {
                    // A connection just for one accepted batch: it never becomes a phone in the list.
                    peer.Fetching = true;
                    if (type == "fetch") await ServeFiles(peer, Str(root, "id"), cancel);
                    else await ReceiveFiles(peer, Str(root, "id"), cancel);
                    return;
                }
                peer.Device = Cut(Str(root, "device") ?? "Android", 40);
                // apps before 1.3.4 don't say: 0
                peer.App = root.TryGetProperty("app", out var app) && app.TryGetInt32(out var appCode) ? appCode : 0;
            }
            peer.Since = DateTime.Now;
            peer.Authenticated = true;
            _ = _ui.InvokeAsync(() =>
            {
                PhonesChanged?.Invoke();
                PhoneJoined?.Invoke(peer.Device, peer.Relay);
                if (peer.App < AndroidVersionCode) OldApp?.Invoke(peer.Device);
            });
            await peer.SendAsync(new JsonObject
            {
                ["type"] = "welcome", ["v"] = Version, ["desktop"] = Environment.MachineName, ["os"] = "windows",
                ["relay"] = Relay, // phones paired before the relay learn it here, without a new QR code
                ["relayKey"] = Relay.Length > 0 ? RelayKeyInUse : "", // only ever sent here, after the token checked out
                // so the phone can switch this PC on later (Wake-on-LAN), when the link can't reach it
                ["wake"] = WakeOnLan.Wired() is { } wake ? new JsonObject { ["mac"] = wake.Mac, ["broadcast"] = wake.Broadcast } : null,
                // so an older app can offer its own update
                ["android"] = AndroidVersionCode > 0
                    ? new JsonObject { ["code"] = AndroidVersionCode, ["name"] = AndroidVersionName, ["url"] = AndroidApkUrl } : null,
            }.ToJsonString());
            await peer.SendAsync(_sessions);
            await peer.SendAsync(_tray);
            if (_pcState != null) await peer.SendAsync(_pcState);
            if (_filter is { } filter) await peer.SendAsync(filter);
            List<string> pending;
            lock (_gate) pending = _approvals.Values.ToList();
            foreach (var line in pending) await peer.SendAsync(line);
            foreach (var line in LiveOfferLines()) await peer.SendAsync(line);

            while (!cancel.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                idle.CancelAfter(IdleTimeout);
                var text = await peer.ReadLineAsync(idle.Token);
                if (text == null) return;
                using var doc = JsonDocument.Parse(text);
                var command = ParseCommand(doc.RootElement);
                switch (command.Action)
                {
                    case PhoneAction.Ping:
                        await peer.SendAsync("""{"type":"pong"}""");
                        break;
                    case PhoneAction.Decision:
                        _ = _ui.InvokeAsync(() => Decision?.Invoke(command.Fingerprint!, command.Decision!));
                        break;
                    case PhoneAction.Open:
                        _ = _ui.InvokeAsync(() => Open?.Invoke(command.Id!));
                        break;
                    case PhoneAction.Clip:
                        _ = _ui.InvokeAsync(() => Clip?.Invoke(command.Text!));
                        break;
                    case PhoneAction.Type:
                        _ = _ui.InvokeAsync(() => Type?.Invoke(command.Text!));
                        break;
                    case PhoneAction.Remote:
                        _ = _ui.InvokeAsync(() => Remote?.Invoke(command.Text!, command.Minutes));
                        break;
                    case PhoneAction.Call:
                        _ = _ui.InvokeAsync(() => Call?.Invoke(command.State!, command.Name ?? "", command.Number ?? ""));
                        break;
                    case PhoneAction.FileDecline:
                        EndOffer(command.Id!, FileOutcome.Declined);
                        break;
                    case PhoneAction.FileDone:
                        EndOffer(command.Id!, command.Ok ? FileOutcome.Received : FileOutcome.Failed);
                        break;
                    case PhoneAction.SendOffer:
                        OnPhoneOffer(peer, command.Offer);
                        break;
                    case PhoneAction.SendCancel:
                        CancelIncoming(command.Id!);
                        break;
                    case PhoneAction.Filter:
                        _ = _ui.InvokeAsync(() => FilterFromPhone?.Invoke(command.Kinds!, command.At));
                        break;
                    case PhoneAction.Bye:
                        return;
                }
            }
        }
        // Only the exception type is logged (never the payload): bad TLS, bad JSON, line too long, timeout: drop the connection.
        catch (Exception ex) { Log.Warn("PhoneLink.Serve", ex.GetType().Name); }
        finally
        {
            peer.Close();
            bool wasAuthenticated;
            lock (_gate) wasAuthenticated = _peers.Remove(peer) && peer.Authenticated;
            if (wasAuthenticated) _ = _ui.InvokeAsync(() => PhonesChanged?.Invoke());
        }
    }

    // ───────────────────────── discovery ─────────────────────────

    // When the PC's address changes (DHCP), the paired phone broadcasts {"type":"discover","fp":…} on UDP
    // DefaultPort and the PC with that certificate answers with its TCP port. Nothing secret is sent: the phone
    // still accepts only the pinned certificate, so a fake answer can at most send it to a dead end.
    void StartDiscovery(CancellationToken cancel)
    {
        try
        {
            // Exclusive bind (no SO_REUSEADDR), so no other program can take over the answers.
            var udp = new UdpClient(new IPEndPoint(IPAddress.Any, DefaultPort));
            _discovery = udp;
            _ = DiscoveryLoop(udp, cancel);
        }
        catch (SocketException ex) { Log.Error("PhoneLink.Discovery", ex); } // the link still works by address
    }

    async Task DiscoveryLoop(UdpClient udp, CancellationToken cancel)
    {
        var reply = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = "here", ["v"] = Version, ["fp"] = Fingerprint, ["port"] = Port,
        }.ToJsonString());
        while (!cancel.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(cancel); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // e.g. ICMP port unreachable from an earlier reply
            try
            {
                if (packet.Buffer.Length > 512 || !IsLocal(packet.RemoteEndPoint.Address)) continue;
                using var doc = JsonDocument.Parse(packet.Buffer);
                if (Str(doc.RootElement, "type") != "discover" || Str(doc.RootElement, "fp") is not { } fp
                    || !SameSecret(fp, Fingerprint)) continue;
                await udp.SendAsync(reply, packet.RemoteEndPoint, cancel);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { } // malformed packet from the network: ignore
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ───────────────────────── the phone's commands ─────────────────────────

    /// <summary>What the PC does with one JSON line from a phone. Serve's state machine, extracted so tests can
    /// drive it; <see cref="PhoneAction.None"/> means the line is fine but asks for nothing.</summary>
    internal enum PhoneAction { None, Ping, Decision, Open, Clip, Remote, Call, FileDecline, FileDone, SendOffer, SendCancel, Filter, Bye, Type }

    /// <summary>Text is the copied text (Clip) or the remote action (Remote); State/Name/Number describe a Call.</summary>
    internal sealed record PhoneCommand(PhoneAction Action, string? Fingerprint = null, string? Decision = null,
        string? Id = null, bool Ok = false, JsonElement Offer = default, string? Text = null, int Minutes = 0,
        string? State = null, string? Name = null, string? Number = null,
        IReadOnlyList<string>? Kinds = null, long At = 0);

    /// <summary>Lines that don't carry every field their action needs become <see cref="PhoneAction.None"/>
    /// (nothing happens), exactly like the guards they replace.</summary>
    internal static PhoneCommand ParseCommand(JsonElement root)
    {
        var id = Str(root, "id");
        switch (Str(root, "type"))
        {
            case "ping": return new PhoneCommand(PhoneAction.Ping);
            case "decision" when Str(root, "fingerprint") is { } fp && Str(root, "decision") is "allow" or "deny":
                return new PhoneCommand(PhoneAction.Decision, Fingerprint: fp, Decision: Str(root, "decision"));
            case "open" when id is { Length: > 0 and <= 64 }:
                return new PhoneCommand(PhoneAction.Open, Id: id);
            case "clip" when Str(root, "text") is { Length: > 0 and <= MaxClipChars } clip:
                return new PhoneCommand(PhoneAction.Clip, Text: clip);
            case "type" when Str(root, "text") is { Length: > 0 and <= MaxTypeChars } typed:
                return new PhoneCommand(PhoneAction.Type, Text: typed);
            case "remote" when Str(root, "action") is { } action && RemoteActions.Contains(action):
                return new PhoneCommand(PhoneAction.Remote, Text: action,
                    Minutes: root.TryGetProperty("minutes", out var m) && m.TryGetInt32(out var mi) ? Math.Clamp(mi, 1, 720) : 0);
            case "call" when Str(root, "state") is "ringing" or "offhook" or "idle":
                return new PhoneCommand(PhoneAction.Call, State: Str(root, "state"),
                    Name: Cut(Str(root, "name") ?? "", 64), Number: Cut(Str(root, "number") ?? "", 32));
            case "fileDecline" when id != null:
                return new PhoneCommand(PhoneAction.FileDecline, Id: id);
            case "fileDone" when id != null:
                return new PhoneCommand(PhoneAction.FileDone, Id: id,
                    Ok: root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True);
            case "sendOffer":
                return new PhoneCommand(PhoneAction.SendOffer, Offer: root);
            case "sendCancel" when id != null:
                return new PhoneCommand(PhoneAction.SendCancel, Id: id);
            case "filter" when root.TryGetProperty("at", out var at) && at.TryGetInt64(out var atMs) && atMs > 0
                && CleanKinds(root) is { } kinds:
                return new PhoneCommand(PhoneAction.Filter, Kinds: kinds, At: atMs);
            case "bye":
                return new PhoneCommand(PhoneAction.Bye);
            default:
                return new PhoneCommand(PhoneAction.None);
        }
    }

    static bool SameSecret(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    // Private, link-local and loopback only: the link never answers the internet.
    static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>The PC's private IPv4 on the network that has the default gateway (Wi-Fi or cable).</summary>
    public static string? LocalAddress()
    {
        if (DemoMode) return "192.168.0.12";
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up
                                 && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                     .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1))
        {
            var props = nic.GetIPProperties();
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
            var address = props.UnicastAddresses.Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsLocal(a) && !IPAddress.IsLoopback(a));
            if (address != null) return address.ToString();
        }
        return null;
    }

    // ───────────────────────── certificate and token ─────────────────────────

    static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Certificate and token live only in a DPAPI blob (this Windows user).
    void LoadSecrets()
    {
        if (_cert != null) return;
        try
        {
            if (File.Exists(SecretFile))
            {
                var data = JsonNode.Parse(Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(File.ReadAllBytes(SecretFile), null, DataProtectionScope.CurrentUser)))!;
                _token = data["token"]!.GetValue<string>();
                _relayKey = data["relayKey"]?.GetValue<string>() is { } rk && ValidRelayKey(rk) ? rk : "";
                _pfx = Convert.FromBase64String(data["pfx"]!.GetValue<string>());
                _cert = X509CertificateLoader.LoadPkcs12(_pfx, null, X509KeyStorageFlags.UserKeySet);
                // Pairings from before the relay's challenge get their key now; the relay keeps the first one it sees.
                var seed = data["relaySeed"]?.GetValue<string>() is { } s ? Convert.FromBase64String(s) : [];
                if (seed.Length == 32) _relaySeed = seed;
                else
                {
                    _relaySeed = Ed25519.NewSeed();
                    SaveSecrets();
                }
                return;
            }
        }
        catch (Exception ex) { Log.Error("PhoneLink.LoadSecrets (the phone will need to pair again)", ex); }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Dynamic Island", key, HashAlgorithmName.SHA256);
        using var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(20));
        _pfx = created.Export(X509ContentType.Pkcs12);
        _cert = X509CertificateLoader.LoadPkcs12(_pfx, null, X509KeyStorageFlags.UserKeySet);
        _token = NewToken();
        _relaySeed = Ed25519.NewSeed();
        SaveSecrets();
    }

    /// <summary>The Ed25519 private key (seed) that proves to the relay this PC owns the room; one per pairing.</summary>
    byte[] _relaySeed = [];

    void SaveSecrets()
    {
        if (_cert == null) return;
        var json = new JsonObject
        {
            ["token"] = _token, ["pfx"] = Convert.ToBase64String(_pfx), ["relayKey"] = _relayKey,
            ["relaySeed"] = Convert.ToBase64String(_relaySeed),
        }.ToJsonString();
        // A half-written secret would unpair the phone, so it is replaced atomically like settings.json.
        AtomicFile.WriteAllBytes(SecretFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser),
            backup: false);
    }

    // ───────────────────────── relay ─────────────────────────

    void RestartRelay()
    {
        StopRelay();
        if (Relay.Length == 0 || _token.Length == 0) return;
        _relayCts = new CancellationTokenSource();
        _ = RelayLoop(Relay, RoomId(_token), ++_relayGeneration, _relayCts.Token);
    }

    void StopRelay()
    {
        _relayCts?.Cancel();
        _relayCts?.Dispose();
        _relayCts = null;
        _relayGeneration++;
        SetRelayConnected(false);
        lock (_gate)
        {
            foreach (var peer in _peers.Where(p => p.Relay).ToList())
            {
                peer.Close();
                _peers.Remove(peer);
            }
        }
    }

    void SetRelayConnected(bool connected)
    {
        if (RelayConnected == connected) return;
        RelayConnected = connected;
        _ = _ui.InvokeAsync(() => PhonesChanged?.Invoke());
    }

    /// <summary>Keeps the control WebSocket (wss://host/pc/room) open, reconnecting with backoff. The relay
    /// sends {"type":"incoming","conn":id} for each phone that joins; each gets its own data socket.</summary>
    async Task RelayLoop(string relay, string room, int generation, CancellationToken cancel)
    {
        var backoff = TimeSpan.FromSeconds(2);
        var failures = 0;
        while (!cancel.IsCancellationRequested)
        {
            using (var ws = NewRelaySocket(relay))
            {
                try
                {
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                    {
                        connect.CancelAfter(TimeSpan.FromSeconds(15));
                        await ws.ConnectAsync(new Uri($"{relay}/pc/{room}"), connect.Token);
                    }
                    backoff = TimeSpan.FromSeconds(2);
                    failures = 0;
                    using var pinger = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    _ = PingLoop(ws, pinger.Token);
                    // The relay sends a challenge and lets the PC in once it is signed ("ready"); a relay from before the
                    // challenge never sends one, and then the PC is in as soon as it connects.
                    var challenged = false;
                    _ = Task.Delay(RelayChallengeWait, pinger.Token).ContinueWith(t =>
                    {
                        if (!t.IsCanceled && !challenged && generation == _relayGeneration) SetRelayConnected(true);
                    }, TaskScheduler.Default);
                    try
                    {
                        while (ws.State == WebSocketState.Open && !cancel.IsCancellationRequested)
                        {
                            var text = await ReceiveText(ws, cancel);
                            if (text == null) break;
                            if (text == "pong") continue;
                            try
                            {
                                using var doc = JsonDocument.Parse(text);
                                switch (Str(doc.RootElement, "type"))
                                {
                                    case "challenge" when !challenged && AnswerChallenge(text, room, _relaySeed) is { } answer:
                                        challenged = true;
                                        await ws.SendAsync(Encoding.UTF8.GetBytes(answer), WebSocketMessageType.Text, true, cancel);
                                        break;
                                    case "ready":
                                        if (generation == _relayGeneration) SetRelayConnected(true);
                                        SetRelayProblem(null);
                                        break;
                                    case "incoming" when Str(doc.RootElement, "conn") is { } conn && ValidConn(conn):
                                        _ = AcceptRelay(relay, room, conn, cancel);
                                        break;
                                }
                            }
                            catch (JsonException) { } // not for us
                        }
                    }
                    finally { pinger.Cancel(); }
                    if (RelayCloseMessage((int?)ws.CloseStatus) is { } problem) SetRelayProblem(problem);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (ws.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
                        SetRelayProblem(_relayOwn ? RelayRefusedOwnMessage : RelayRefusedPublicMessage);
                    // One line per outage, not one per retry.
                    if (failures++ == 0) Log.Error("PhoneLink.Relay", ex);
                }
            }
            if (generation == _relayGeneration) SetRelayConnected(false);
            try { await Task.Delay(backoff + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)), cancel); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, RelayMaxBackoff.Ticks));
        }
    }

    /// <summary>A socket for the relay: WebSocket pings every 30 s (dead links found in a minute, the room stays asleep),
    /// the relay key only for an own relay, and — for the public relay — the leaf certificate pinned by its
    /// SHA-256, on top of the normal validation (an own relay gets just the normal validation).</summary>
    ClientWebSocket NewRelaySocket(string relay)
    {
        var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.KeepAliveInterval = RelayKeepAlive;
        ws.Options.KeepAliveTimeout = RelayKeepAliveTimeout;
        if (relay == PublicRelay) ws.Options.RemoteCertificateValidationCallback = PublicRelayValidation;
        // corporate proxies (407) accept the signed-in Windows user, as browsers do
        var proxy = System.Net.WebRequest.GetSystemWebProxy();
        proxy.Credentials = System.Net.CredentialCache.DefaultNetworkCredentials;
        ws.Options.Proxy = proxy;
        if (RelayKeyInUse.Length > 0) ws.Options.SetRequestHeader("Authorization", "Bearer " + RelayKeyInUse);
        return ws;
    }

    /// <summary>SHA-256 of the public relay's leaf certificate and of its issuer, as a fallback while the
    /// Cloudflare rotation is in flight. Add the new one here when the relay presents a new certificate.</summary>
    public static readonly string[] PublicRelayPins =
    [
        "89e7eb57de209bd003fa0ce56aab4c036bcabe015b3e199aeb36b1ce30072529", // leaf CN=brendoribeiron.workers.dev (YE1)
        "a2372d06431e9716365eeed47ec020351497d182fcc038e457e58168a03cac07", // issuer CN=YE1, O=Let's Encrypt, C=US
    ];

    // Additive to the normal validation (never weaker): the chain must validate as usual and still carry a pin.
    static bool PublicRelayValidation(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is not X509Certificate2 cert || chain == null || errors != SslPolicyErrors.None) return false;
        return chain.ChainElements.Cast<X509ChainElement>().Any(e => PublicRelayPins.Contains(Sha256Hex(e.Certificate)));
    }

    static string Sha256Hex(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();

    /// <summary>The answer to the relay's {"type":"challenge","nonce":…} for <paramref name="room"/>: the pairing's
    /// Ed25519 public key and its signature on "dynamicisland-relay-auth|room|nonce". Null if it isn't a challenge.</summary>
    public static string? AnswerChallenge(string message, string room, byte[] seed)
    {
        string? nonce;
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (Str(doc.RootElement, "type") != "challenge") return null;
            nonce = Str(doc.RootElement, "nonce");
        }
        catch (JsonException) { return null; }
        if (nonce is not { Length: 64 } || !nonce.All(char.IsAsciiHexDigitLower) || seed.Length != 32) return null;
        var signature = Ed25519.Sign(seed, Encoding.UTF8.GetBytes($"dynamicisland-relay-auth|{room}|{nonce}"));
        return new JsonObject
        {
            ["type"] = "auth",
            ["key"] = Convert.ToHexString(Ed25519.PublicKey(seed)).ToLowerInvariant(),
            ["sig"] = Convert.ToHexString(signature).ToLowerInvariant(),
        }.ToJsonString();
    }

    // A text "ping" every 20 s: the relay answers "pong" without waking up, and idle NATs keep the socket.
    static async Task PingLoop(ClientWebSocket ws, CancellationToken cancel)
    {
        var ping = "ping"u8.ToArray();
        try
        {
            while (!cancel.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(RelayPing, cancel);
                await ws.SendAsync(ping, WebSocketMessageType.Text, true, cancel);
            }
        }
        catch { } // the receive loop notices the broken socket
    }

    static async Task<string?> ReceiveText(ClientWebSocket ws, CancellationToken cancel)
    {
        var buffer = new byte[4096];
        var total = 0;
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(total), cancel);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            total += result.Count;
            if (result.EndOfMessage)
                return result.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(buffer, 0, total) : "";
            if (total == buffer.Length) throw new InvalidDataException("relay control message too long");
        }
    }

    static bool ValidConn(string conn) => conn.Length is > 0 and <= 64 && conn.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>A phone joined the room: open its data socket (wss://host/pc/room/conn) and run the same TLS +
    /// hello path as a LAN connection over it. Binary frames only; closing either side closes the other.</summary>
    async Task AcceptRelay(string relay, string room, string conn, CancellationToken cancel)
    {
        Peer peer;
        var ws = NewRelaySocket(relay);
        lock (_gate)
        {
            if (_peers.Count(p => p.Authenticated) >= MaxPeers || _peers.Count(p => !p.Authenticated && p.Relay && !p.Fetching) >= MaxPendingRelay)
            {
                ws.Dispose();
                return; // the relay drops the phone once its partner never shows up
            }
            peer = new Peer(new RelayStream(ws), null, ws, relay: true);
            _peers.Add(peer);
        }
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            connect.CancelAfter(HelloTimeout);
            await ws.ConnectAsync(new Uri($"{relay}/pc/{room}/{conn}"), connect.Token);
        }
        catch
        {
            peer.Close();
            lock (_gate) _peers.Remove(peer);
            return;
        }
        await Serve(peer, cancel);
        // The relay cut the tunnel over a limit: say so once (the phone hears the same from its side).
        try { if (RelayCloseMessage((int?)ws.CloseStatus) is { } problem) SetRelayProblem(problem); } catch { }
    }

    /// <summary>The data WebSocket as a byte stream: each write is one binary frame, reads return frame data
    /// without boundaries. A text frame (not part of the protocol) or a close ends the stream.</summary>
    sealed class RelayStream(ClientWebSocket ws) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (ws.State is not (WebSocketState.Open or WebSocketState.CloseSent)) return 0;
                var result = await ws.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return 0;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ws.Abort();
                    return 0;
                }
                if (result.Count > 0 || buffer.Length == 0) return result.Count;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // TLS records are at most ~16 KB, well under the relay's 64 KB frame limit.
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ws.SendAsync(buffer, WebSocketMessageType.Binary, true, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) ws.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed class Peer(Stream raw, IPAddress? address, IDisposable owner, bool relay)
    {
        public Stream Raw => raw;
        public IPAddress? Address => address;
        public bool Relay => relay;
        readonly SemaphoreSlim _write = new(1, 1);
        readonly byte[] _buffer = new byte[8192];
        readonly MemoryStream _line = new();
        int _start, _end;
        public SslStream? Stream;
        public bool Authenticated;
        public string Device = "";
        /// <summary>The app's versionCode from the hello (0: an app too old to say).</summary>
        public int App;
        public DateTime Since;
        /// <summary>A connection that only carries a file (not a phone in the list, not a pending hello).</summary>
        public volatile bool Fetching;

        public async Task SendAsync(string line)
        {
            if (Stream == null) return;
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _write.WaitAsync();
            try { await Stream.WriteAsync(bytes); await Stream.FlushAsync(); }
            catch { Close(); }
            finally { _write.Release(); }
        }

        // Control lines from Broadcast: one bounded outbox per peer, so a long transfer on this connection
        // (or the phone reusing it to fetch) can't pile up unbounded waiting writes — the drain writes the
        // batch in one go, and SendRawAsync opens a window between chunks for it to flow.
        const int OutboxLimit = 64;
        readonly object _outboxGate = new();
        readonly Queue<string> _outbox = new();
        int _draining;

        public void Enqueue(string line)
        {
            if (Stream == null) return;
            lock (_outboxGate)
            {
                if (_outbox.Count >= OutboxLimit) _outbox.Dequeue(); // too far behind: the newest matters
                _outbox.Enqueue(line);
            }
            if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0) _ = DrainOutboxAsync();
        }

        async Task DrainOutboxAsync()
        {
            try
            {
                while (true)
                {
                    string[] batch;
                    lock (_outboxGate)
                    {
                        if (_outbox.Count == 0) { _draining = 0; return; }
                        batch = [.. _outbox];
                        _outbox.Clear();
                    }
                    await _write.WaitAsync();
                    try
                    {
                        foreach (var line in batch) await Stream!.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
                        await Stream!.FlushAsync();
                    }
                    catch { Close(); lock (_outboxGate) _outbox.Clear(); return; }
                    finally { _write.Release(); }
                }
            }
            catch { Close(); }
        }

        /// <summary>Exactly <paramref name="size"/> bytes after the last line read (what is left in the buffer first),
        /// into <paramref name="sink"/> and <paramref name="hash"/>. Throws if the link ends early.</summary>
        public async Task ReadRawAsync(Stream sink, long size, IncrementalHash hash, CancellationToken cancel)
        {
            for (var left = size; left > 0;)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await Stream!.ReadAsync(_buffer, cancel);
                    if (_end == 0) throw new EndOfStreamException("phone hung up mid-file");
                }
                var n = (int)Math.Min(left, _end - _start);
                hash.AppendData(_buffer, _start, n);
                await sink.WriteAsync(_buffer.AsMemory(_start, n), cancel);
                _start += n;
                left -= n;
            }
        }

        /// <summary>Exactly <paramref name="size"/> bytes of <paramref name="source"/>, after the lines already sent.
        /// The write semaphore is held per chunk (not for the whole file), so queued control lines flow between
        /// them instead of waiting for a transfer of minutes. Throws (and the connection is dropped) if the file
        /// ends early or the link breaks.</summary>
        public async Task SendRawAsync(Stream source, long size, CancellationToken cancel)
        {
            var chunk = new byte[64 * 1024];
            for (var left = size; left > 0;)
            {
                var n = await source.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, left)), cancel);
                if (n == 0) throw new EndOfStreamException("file shorter than offered");
                await _write.WaitAsync(cancel);
                try
                {
                    await Stream!.WriteAsync(chunk.AsMemory(0, n), cancel);
                    left -= n;
                }
                finally { _write.Release(); }
            }
            await _write.WaitAsync(cancel);
            try { await Stream!.FlushAsync(cancel); }
            finally { _write.Release(); }
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancel)
        {
            _line.SetLength(0);
            while (true)
            {
                for (var i = _start; i < _end; i++)
                {
                    if (_buffer[i] != (byte)'\n') continue;
                    _line.Write(_buffer, _start, i - _start);
                    _start = i + 1;
                    return Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
                }
                _line.Write(_buffer, _start, _end - _start);
                if (_line.Length > MaxLine) throw new InvalidDataException("line too long");
                _start = 0;
                _end = await Stream!.ReadAsync(_buffer, cancel);
                if (_end == 0) return null;
            }
        }

        public void Close()
        {
            try { Stream?.Dispose(); } catch { }
            try { owner.Dispose(); } catch { }
        }
    }
}
