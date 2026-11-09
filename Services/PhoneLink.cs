using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
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
/// </summary>
public sealed class PhoneLink : IDisposable
{
    public const int Version = 1;
    public const int DefaultPort = 47900;
    // MaxPeers counts paired phones; connections still in the TLS/hello stage have their own small budget (and
    // per address), so someone on the network opening sockets can't lock the real phone out.
    const int MaxLine = 64 * 1024, MaxPeers = 6, MaxPending = 4, MaxPendingPerAddress = 2;
    static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10), IdleTimeout = TimeSpan.FromMinutes(20);

    static string SecretFile => Path.Combine(IslandSettings.Folder, "phone-link.bin");

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
    readonly Dictionary<string, string> _approvals = []; // fingerprint -> JSON line

    public PhoneLink(Dispatcher ui) => _ui = ui;

    public bool Running => _listener != null;
    public int Port { get; private set; }
    public string Fingerprint => _cert == null ? "" : Convert.ToHexString(SHA256.HashData(_cert.RawData)).ToLowerInvariant();
    public int ConnectedPhones { get { lock (_gate) return _peers.Count(p => p.Authenticated); } }

    /// <summary>Raised on the UI thread: (fingerprint, "allow" | "deny").</summary>
    public event Action<string, string>? Decision;
    /// <summary>Raised on the UI thread with a tray item id the phone wants opened.</summary>
    public event Action<string>? Open;
    public event Action? PhonesChanged;

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
    }

    public void Stop()
    {
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
        SaveSecrets();
        lock (_gate)
        {
            foreach (var peer in _peers) peer.Close();
            _peers.Clear();
        }
        PhonesChanged?.Invoke();
    }

    public string PairingLink()
    {
        var host = LocalAddress() ?? "127.0.0.1";
        return $"dynamicisland://pair?v={Version}&host={host}&port={Port}&fp={Fingerprint}&token={_token}" +
               $"&name={Uri.EscapeDataString(Environment.MachineName)}";
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

    void Broadcast(string line)
    {
        List<Peer> peers;
        lock (_gate) peers = _peers.Where(p => p.Authenticated).ToList();
        foreach (var peer in peers) _ = peer.SendAsync(line);
    }

    static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    // ───────────────────────── connections ─────────────────────────

    async Task AcceptLoop(TcpListener listener, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancel); }
            catch { return; }
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            bool full;
            lock (_gate)
            {
                var pending = _peers.Where(p => !p.Authenticated).ToList();
                full = _peers.Count(p => p.Authenticated) >= MaxPeers || pending.Count >= MaxPending
                       || (remote != null && pending.Count(p => remote.Equals(p.Address)) >= MaxPendingPerAddress);
            }
            if (remote == null || !IsLocal(remote) || full)
            {
                client.Dispose();
                continue;
            }
            _ = Serve(client, cancel);
        }
    }

    async Task Serve(TcpClient client, CancellationToken cancel)
    {
        var peer = new Peer(client);
        lock (_gate) _peers.Add(peer);
        try
        {
            using var hello = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            hello.CancelAfter(HelloTimeout);
            var ssl = new SslStream(client.GetStream(), false);
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
                if (Str(root, "type") != "hello" || !SameSecret(token, _token))
                {
                    await peer.SendAsync("""{"type":"error","code":"auth","message":"Pareamento inválido. Escaneie o código de novo."}""");
                    await Task.Delay(1000, cancel);
                    return;
                }
                peer.Device = Cut(Str(root, "device") ?? "Android", 40);
            }
            peer.Authenticated = true;
            _ = _ui.InvokeAsync(() => PhonesChanged?.Invoke());
            await peer.SendAsync(new JsonObject
            {
                ["type"] = "welcome", ["v"] = Version, ["desktop"] = Environment.MachineName, ["os"] = "windows",
            }.ToJsonString());
            await peer.SendAsync(_sessions);
            await peer.SendAsync(_tray);
            List<string> pending;
            lock (_gate) pending = _approvals.Values.ToList();
            foreach (var line in pending) await peer.SendAsync(line);

            while (!cancel.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                idle.CancelAfter(IdleTimeout);
                var text = await peer.ReadLineAsync(idle.Token);
                if (text == null) return;
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                switch (Str(root, "type"))
                {
                    case "ping":
                        await peer.SendAsync("""{"type":"pong"}""");
                        break;
                    case "decision" when Str(root, "fingerprint") is { } fp && Str(root, "decision") is "allow" or "deny":
                        var decision = Str(root, "decision")!;
                        _ = _ui.InvokeAsync(() => Decision?.Invoke(fp, decision));
                        break;
                    case "open" when Str(root, "id") is { Length: > 0 and <= 64 } id:
                        _ = _ui.InvokeAsync(() => Open?.Invoke(id));
                        break;
                    case "bye":
                        return;
                }
            }
        }
        catch { } // bad TLS, bad JSON, line too long, timeout: drop the connection
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
                _pfx = Convert.FromBase64String(data["pfx"]!.GetValue<string>());
                _cert = X509CertificateLoader.LoadPkcs12(_pfx, null, X509KeyStorageFlags.UserKeySet);
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
        SaveSecrets();
    }

    void SaveSecrets()
    {
        if (_cert == null) return;
        var json = new JsonObject { ["token"] = _token, ["pfx"] = Convert.ToBase64String(_pfx) }.ToJsonString();
        // A half-written secret would unpair the phone, so it is replaced atomically like settings.json.
        AtomicFile.WriteAllBytes(SecretFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser),
            backup: false);
    }

    sealed class Peer(TcpClient client)
    {
        public IPAddress? Address { get; } = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        readonly SemaphoreSlim _write = new(1, 1);
        readonly byte[] _buffer = new byte[8192];
        readonly MemoryStream _line = new();
        int _start, _end;
        public SslStream? Stream;
        public bool Authenticated;
        public string Device = "";

        public async Task SendAsync(string line)
        {
            if (Stream == null) return;
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _write.WaitAsync();
            try { await Stream.WriteAsync(bytes); await Stream.FlushAsync(); }
            catch { Close(); }
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
            client.Dispose();
        }
    }
}
