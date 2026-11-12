using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicIsland.Services;

/// <summary>A batch the phone wants to send: files (name, size, hash) or one text. Nothing is read before Accept.</summary>
public sealed record PhoneIncoming(string Id, string Device, IReadOnlyList<PhoneLink.IncomingItem> Items, string? Text)
{
    public long TotalBytes => Items.Sum(i => i.Size);
    /// <summary>When the phone first asked.</summary>
    public DateTime At { get; init; } = DateTime.Now;
}

// Files and text between the PC and the phone, in both directions, always after the receiving side accepts.
//
//   PC -> phone   link: fileOffer {id, items:[{name,size,sha256}]}   phone: fileDecline / fileDone {ok}
//                 phone opens a connection: {"type":"fetch","token","id"} -> per item {"type":"file",index,size,sha256} + bytes
//                 link: text {id, text} (the phone shows it with "Copiar"; nothing to fetch)
//   phone -> PC   link: sendOffer {id, items:[…]} or {id, text}       PC: sendAccept / sendDecline {id}
//                 phone opens a connection: {"type":"push","token","id"} -> PC "ready" -> per item header + bytes -> PC "saved" {ok}
//
// Pending: what nobody answers right away waits up to a day. A batch from the PC stays offered (and is offered again
// to a phone that connects); one from the phone moves to "Pendentes" on the island after the prompt (PC: sendPending
// {id}), the phone keeps its copies and offers it again on every connect with "pending":true, and an Accept given
// while the phone was away is repeated then.
//
// Every byte goes inside the same pinned TLS with the pairing token (LAN or relay, the relay only sees TLS).
// Sizes and SHA-256 are checked by whoever receives; received files land in Downloads\Dynamic Island with the
// "came from the internet" mark, so Windows warns before running anything. Nothing received is ever opened by itself.
public sealed partial class PhoneLink
{
    /// <summary>The biggest single file either way (videos get more): a quick way to hand things over, not a sync.</summary>
    public const long MaxFileBytes = 30L * 1024 * 1024, MaxVideoBytes = 60L * 1024 * 1024;
    /// <summary>Texts up to <see cref="InlineTextChars"/> ride on the link; longer ones (up to <see cref="MaxTextChars"/>)
    /// travel like a file, as UTF-8 of at most <see cref="MaxTextBytes"/>, and end up in the clipboard.</summary>
    public const int MaxBatchItems = 20, InlineTextChars = 8000, MaxTextChars = 100_000, MaxTextBytes = 512 * 1024;

    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".mkv", ".webm", ".3gp", ".avi" };

    /// <summary>60 MB for videos, 30 MB for everything else (the phone uses the same rule).</summary>
    public static long MaxBytesFor(string name) => VideoExtensions.Contains(Path.GetExtension(name)) ? MaxVideoBytes : MaxFileBytes;
    const int MaxOffers = 20, MaxIncoming = 20, MaxTransfers = 2;
    /// <summary>Unanswered things wait this long either way (the PC's own offers: while the island stays open).</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(24);
    static readonly TimeSpan OfferLifetime = PendingLifetime, TransferTimeout = TimeSpan.FromMinutes(5);
    /// <summary>How long a phone's offer shows as a prompt on the island before it moves to Pendentes.</summary>
    static readonly TimeSpan IncomingAnswerWindow = TimeSpan.FromSeconds(45);

    public enum FileOutcome { Sending, Received, Declined, Failed, Expired }

    /// <summary>A file to send, or (<see cref="Text"/> set) a long text sent the same way.</summary>
    sealed record FileEntry(string Path, string Name, long Size, string Sha256, DateTime LastWriteUtc, byte[]? Text = null);
    /// <summary>A batch waiting for the phone. Only names, sizes and hashes go out (<paramref name="Line"/>, sent
    /// again to a phone that connects later); paths stay here.</summary>
    sealed record FileOffer(string Id, string Label, IReadOnlyList<FileEntry> Items, string Line = "");

    /// <summary>One thing in a batch from the phone; <paramref name="IsText"/>: a long text, for the clipboard.</summary>
    public sealed record IncomingItem(string Name, long Size, string Sha256, bool IsText = false);

    sealed class Incoming(PhoneIncoming offer, Peer from)
    {
        public PhoneIncoming Offer => offer;
        /// <summary>The phone's link it last came on (it may have reconnected since).</summary>
        public Peer From = from;
        public bool Accepted;
        /// <summary>Nobody clicked the prompt: it waits in Pendentes.</summary>
        public bool Pending;
    }

    readonly Dictionary<string, FileOffer> _offers = [];
    readonly Dictionary<string, Incoming> _incoming = [];
    int _transfers;

    /// <summary>Raised on the UI thread as a batch offered to the phone moves along: (label, what happened).</summary>
    public event Action<string, FileOutcome>? FileStatus;
    /// <summary>Raised on the UI thread when the phone wants to send something; answer with Accept / Decline.</summary>
    public event Action<PhoneIncoming>? IncomingOffer;
    /// <summary>Raised on the UI thread when files from the phone are saved: (label, the first saved file, ok).</summary>
    public event Action<string, string?, bool>? IncomingDone;
    /// <summary>Raised on the UI thread with a long text from the phone the person accepted (for the clipboard).</summary>
    public event Action<string>? IncomingText;
    /// <summary>Raised on the UI thread when the list of <see cref="PendingIncoming"/> changes.</summary>
    public event Action? PendingChanged;

    /// <summary>What the phone wants to send and nobody answered yet, oldest first.</summary>
    public IReadOnlyList<PhoneIncoming> PendingIncoming
    {
        get { lock (_gate) return [.. _incoming.Values.Where(i => i.Pending && !i.Accepted).Select(i => i.Offer).OrderBy(o => o.At)]; }
    }

    void RaisePending() => _ = _ui.InvokeAsync(() => PendingChanged?.Invoke());

    /// <summary>Where sent and received things are listed (the phone window's Recebidos / Enviados); null: nowhere.</summary>
    public TransferHistory? History { get; set; }

    /// <summary>Downloads\Dynamic Island for this Windows user.</summary>
    public static string ReceiveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Dynamic Island");

    // ───────────────────────── PC -> phone ─────────────────────────

    /// <summary>Hashes the files and offers them to the connected phones as one batch; null when offered, otherwise
    /// why not (short, for the island). The phone asks the person, and only then fetches them.</summary>
    public async Task<string?> OfferFilesAsync(IReadOnlyList<string> paths)
    {
        if (!Running) return "Ligação desligada";
        if (ConnectedPhones == 0) return "Celular desligado";
        if (paths.Count == 0) return "Nada para enviar";
        if (paths.Count > MaxBatchItems) return $"Máx. {MaxBatchItems} de uma vez";
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) return "Só arquivos";
            var info = new FileInfo(path);
            if (!info.Exists) return "Não encontrado";
            if (info.Length > MaxBytesFor(path)) return MaxBytesFor(path) == MaxVideoBytes ? "Vídeo: máx. 60 MB" : "Máx. 30 MB cada";
        }
        lock (_gate)
            if (_offers.Count >= MaxOffers) return "Espere os outros";

        List<FileEntry> items;
        try
        {
            items = await Task.Run(() => paths.Select(path =>
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
                var after = new FileInfo(path);
                if (file.Length > MaxBytesFor(path) || after.Length != file.Length) throw new IOException("changed while hashing");
                return new FileEntry(path, Cut(after.Name, 120), file.Length, hash, after.LastWriteTimeUtc);
            }).ToList());
        }
        catch (Exception ex)
        {
            Log.Error("PhoneLink.OfferFiles", ex);
            return "Não deu para ler";
        }
        return Offer(items.Count == 1 ? items[0].Name : $"{items.Count} arquivos", items);
    }

    string? Offer(string label, List<FileEntry> items, string? text = null)
    {
        var id = NewOfferId();
        var list = new JsonArray();
        foreach (var item in items)
        {
            var o = new JsonObject { ["name"] = item.Name, ["size"] = item.Size, ["sha256"] = item.Sha256 };
            if (item.Text != null) o["text"] = true;
            list.Add(o);
        }
        var offer = new FileOffer(id, label, items, new JsonObject { ["type"] = "fileOffer", ["id"] = id, ["items"] = list }.ToJsonString());
        lock (_gate)
        {
            if (_offers.Count >= MaxOffers) return "Espere os outros";
            _offers[offer.Id] = offer;
        }
        Broadcast(offer.Line);
        History?.Add(new TransferEntry(offer.Id, DateTime.Now, true, text != null ? "Texto" : label, items.Sum(i => i.Size), items.Count,
            "Esperando o celular", text != null ? [] : [.. items.Select(i => i.Path)], text));
        _ = ExpireLater(offer.Id);
        return null;
    }

    /// <summary>Text for the phone; null when sent, otherwise why not. A short one goes on the link and the phone
    /// shows it with "Copiar"; a long one is offered like a file and lands in the phone's clipboard.</summary>
    public string? SendText(string text)
    {
        if (!Running) return "Ligação desligada";
        if (ConnectedPhones == 0) return "Celular desligado";
        if (string.IsNullOrWhiteSpace(text)) return "Texto vazio";
        if (text.Length > MaxTextChars) return "Máx. 100 mil caracteres";
        if (text.Length <= InlineTextChars)
        {
            var id = NewOfferId();
            Broadcast(new JsonObject { ["type"] = "text", ["id"] = id, ["text"] = text }.ToJsonString());
            History?.Add(new TransferEntry(id, DateTime.Now, true, "Texto", Encoding.UTF8.GetByteCount(text), 1, "No celular", [], text));
            return null;
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxTextBytes) return "Texto grande demais";
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return Offer("Texto", [new FileEntry("", "texto.txt", bytes.Length, sha, default, bytes)], text);
    }

    async Task ExpireLater(string id)
    {
        await Task.Delay(OfferLifetime);
        EndOffer(id, FileOutcome.Expired);
    }

    /// <summary>Takes the offer away (the phones drop their prompt) and tells the island how it ended.</summary>
    void EndOffer(string id, FileOutcome outcome)
    {
        FileOffer? offer;
        lock (_gate)
            if (!_offers.Remove(id, out offer)) return;
        Broadcast(new JsonObject { ["type"] = "fileCancel", ["id"] = id }.ToJsonString());
        History?.SetStatus(id, true, outcome switch
        {
            FileOutcome.Received => "No celular",
            FileOutcome.Declined => "Recusado",
            FileOutcome.Expired => "Não aceito",
            _ => "Falhou",
        });
        _ = _ui.InvokeAsync(() => FileStatus?.Invoke(offer.Label, outcome));
    }

    /// <summary>Offers still waiting, for a phone that just connected (it may have been away when they went out).</summary>
    List<string> LiveOfferLines()
    {
        lock (_gate) return [.. _offers.Values.Select(o => o.Line)];
    }

    void ForgetOffers()
    {
        lock (_gate)
        {
            _offers.Clear();
            _incoming.Clear();
        }
    }

    /// <summary>128 random bits as lowercase hex: only phones that heard the offer (inside TLS) know it.</summary>
    static string NewOfferId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    static bool ValidOfferId(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A phone with the token fetches an accepted batch: per item one JSON header line, then exactly
    /// <c>size</c> raw bytes. The phone checks each size and SHA-256 before it keeps anything.</summary>
    async Task ServeFiles(Peer peer, string? id, CancellationToken cancel)
    {
        FileOffer? offer = null;
        lock (_gate)
            if (id != null) _offers.TryGetValue(id, out offer);
        if (offer == null)
        {
            await peer.SendAsync("""{"type":"error","code":"file","message":"Esse envio não vale mais. Mande de novo pelo PC."}""");
            return;
        }
        if (!TakeTransfer())
        {
            await peer.SendAsync("""{"type":"error","code":"file","message":"O PC já está enviando outros arquivos. Tente de novo daqui a pouco."}""");
            return;
        }
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TransferTimeout);
            History?.SetStatus(offer.Id, true, "Enviando");
            _ = _ui.InvokeAsync(() => FileStatus?.Invoke(offer.Label, FileOutcome.Sending));
            for (var i = 0; i < offer.Items.Count; i++)
            {
                var item = offer.Items[i];
                await using Stream file = item.Text != null ? new MemoryStream(item.Text, false)
                    : new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                // The hash the phone checks is the one taken when the batch was offered.
                if (item.Text == null && (file.Length != item.Size || File.GetLastWriteTimeUtc(item.Path) != item.LastWriteUtc))
                {
                    await peer.SendAsync("""{"type":"error","code":"file","message":"Um arquivo mudou no PC depois de oferecido. Mande de novo."}""");
                    return;
                }
                await peer.SendAsync(new JsonObject
                {
                    ["type"] = "file", ["id"] = offer.Id, ["index"] = i, ["size"] = item.Size, ["sha256"] = item.Sha256,
                }.ToJsonString());
                await peer.SendRawAsync(file, item.Size, limit.Token);
            }
            // The phone hangs up once it has every byte; the result comes back as fileDone on the link.
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            drain.CancelAfter(TimeSpan.FromSeconds(30));
            try { await peer.ReadLineAsync(drain.Token); } catch { }
        }
        finally { Interlocked.Decrement(ref _transfers); }
    }

    bool TakeTransfer()
    {
        if (Interlocked.Increment(ref _transfers) <= MaxTransfers) return true;
        Interlocked.Decrement(ref _transfers);
        return false;
    }

    // ───────────────────────── phone -> PC ─────────────────────────

    /// <summary>sendOffer on the link: checked here (names, sizes, counts), then shown on the island.</summary>
    void OnPhoneOffer(Peer peer, JsonElement root)
    {
        var id = Str(root, "id");
        if (!ValidOfferId(id)) return;
        string? text = null;
        var items = new List<IncomingItem>();
        if (Str(root, "text") is { } t)
        {
            if (t.Length == 0 || t.Length > InlineTextChars) { Answer(peer, id!, false); return; }
            text = t;
        }
        else if (root.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                var size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : -1;
                var sha = Str(e, "sha256") ?? "";
                var isText = e.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.True;
                var name = isText ? "texto.txt" : SafeFileName(Str(e, "name") ?? "");
                if (items.Count >= MaxBatchItems || size < 0 || size > (isText ? MaxTextBytes : MaxBytesFor(name)) || sha.Length != 64
                    || !sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                {
                    Answer(peer, id!, false);
                    return;
                }
                items.Add(new IncomingItem(name, size, sha, isText));
            }
            // a long text travels alone
            if (items.Count == 0 || (items.Any(i => i.IsText) && items.Count > 1)) { Answer(peer, id!, false); return; }
        }
        else return;

        // The phone offers what is still waiting again each time it connects ("pending": it already asked before).
        var again = root.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.True;
        Incoming? known;
        var offer = new PhoneIncoming(id!, peer.Device, items, text);
        lock (_gate)
        {
            if (_incoming.TryGetValue(id!, out known)) known.From = peer;
            else if (_incoming.Count >= MaxIncoming) { Answer(peer, id!, false); return; }
            else _incoming[id!] = new Incoming(offer, peer) { Pending = again };
        }
        if (known != null)
        {
            if (known.Accepted) Answer(peer, id!, true);
            else if (known.Pending) Tell(peer, "sendPending", id!);
            return;
        }
        _ = ExpireIncomingLater(id!);
        if (again)
        {
            Tell(peer, "sendPending", id!);
            RaisePending();
            return;
        }
        _ = PendLater(id!);
        _ = _ui.InvokeAsync(() => IncomingOffer?.Invoke(offer));
    }

    /// <summary>The prompt went unanswered: the offer waits in Pendentes and the phone keeps its copies.</summary>
    async Task PendLater(string id)
    {
        await Task.Delay(IncomingAnswerWindow);
        Incoming? waiting;
        lock (_gate)
        {
            if (!_incoming.TryGetValue(id, out waiting) || waiting.Accepted || waiting.Pending) return;
            waiting.Pending = true;
        }
        Tell(waiting.From, "sendPending", id);
        RaisePending();
    }

    /// <summary>A day later whatever is still here goes: not answered, or accepted but the bytes never came.</summary>
    async Task ExpireIncomingLater(string id)
    {
        await Task.Delay(PendingLifetime);
        Incoming? gone;
        lock (_gate)
            if (!_incoming.Remove(id, out gone)) return;
        if (!gone.Accepted) Broadcast(AnswerLine("sendDecline", id));
        RaisePending();
    }

    /// <summary>The person clicked the island: the phone may send it now. For text there is nothing more to wait for
    /// (the island already has it). False if the offer is gone.</summary>
    public bool AcceptIncoming(string id)
    {
        Incoming? waiting;
        lock (_gate)
        {
            if (!_incoming.TryGetValue(id, out waiting) || waiting.Accepted) return false;
            // (a text stays listed as accepted, so a phone that was away and offers it again just hears "accepted")
            waiting.Accepted = true;
        }
        // to every connected phone: the one that asked may have reconnected (if it is away, it hears it on its way back)
        Broadcast(AnswerLine("sendAccept", id));
        if (waiting.Pending) RaisePending();
        if (waiting.Offer.Text is { } text)
            History?.Add(new TransferEntry(id, DateTime.Now, false, "Texto", Encoding.UTF8.GetByteCount(text), 1, "Copiado", [], text));
        return true;
    }

    public void DeclineIncoming(string id)
    {
        Incoming? waiting;
        lock (_gate)
            if (!_incoming.Remove(id, out waiting)) return;
        Broadcast(AnswerLine("sendDecline", id));
        if (waiting.Pending) RaisePending();
    }

    /// <summary>The phone took its offer back (Cancelar in its Pendentes): it is no longer here to accept.</summary>
    void CancelIncoming(string id)
    {
        Incoming? gone;
        lock (_gate)
            if (!_incoming.Remove(id, out gone)) return;
        // (a pill still asking just finds it gone when clicked)
        if (gone.Pending) RaisePending();
    }

    static string AnswerLine(string type, string id) => new JsonObject { ["type"] = type, ["id"] = id }.ToJsonString();

    static void Answer(Peer peer, string id, bool accept) => Tell(peer, accept ? "sendAccept" : "sendDecline", id);

    static void Tell(Peer peer, string type, string id) => _ = peer.SendAsync(AnswerLine(type, id));

    /// <summary>The phone sends an accepted batch: per item a header, then exactly the bytes. Each one goes to a
    /// hidden temporary file first and is kept only if its size and SHA-256 are the ones the person accepted.</summary>
    async Task ReceiveFiles(Peer peer, string? id, CancellationToken cancel)
    {
        Incoming? batch = null;
        lock (_gate)
            if (id != null && _incoming.TryGetValue(id, out var b) && b.Accepted && b.Offer.Text == null && _incoming.Remove(id))
                batch = b;
        if (batch == null)
        {
            await peer.SendAsync("""{"type":"error","code":"file","message":"O PC não aceitou esse envio (ou ele expirou)."}""");
            return;
        }
        if (!TakeTransfer())
        {
            await peer.SendAsync("""{"type":"error","code":"file","message":"O PC já está recebendo outros arquivos. Tente de novo daqui a pouco."}""");
            return;
        }
        var offer = batch.Offer;
        var label = offer.Items.Count == 1 ? (offer.Items[0].IsText ? "Texto" : offer.Items[0].Name) : $"{offer.Items.Count} arquivos";
        var saved = new List<string>();
        string? received = null;
        var ok = false;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TransferTimeout);
            Directory.CreateDirectory(ReceiveFolder);
            await peer.SendAsync("""{"type":"ready"}""");
            for (var i = 0; i < offer.Items.Count; i++)
            {
                var item = offer.Items[i];
                var header = await peer.ReadLineAsync(limit.Token) ?? throw new EndOfStreamException();
                using (var doc = JsonDocument.Parse(header))
                {
                    var h = doc.RootElement;
                    if (Str(h, "type") != "file" || !h.TryGetProperty("index", out var ix) || !ix.TryGetInt32(out var index) || index != i
                        || !h.TryGetProperty("size", out var sz) || !sz.TryGetInt64(out var size) || size != item.Size
                        || Str(h, "sha256") != item.Sha256)
                        throw new InvalidDataException("header doesn't match the accepted offer");
                }
                if (item.IsText)
                {
                    var text = received = await ReceiveText(peer, item, limit.Token);
                    _ = _ui.InvokeAsync(() => IncomingText?.Invoke(text));
                }
                else saved.Add(await ReceiveOne(peer, item, limit.Token));
            }
            ok = true;
        }
        catch (Exception ex)
        {
            Log.Error("PhoneLink.ReceiveFiles", ex);
        }
        finally { Interlocked.Decrement(ref _transfers); }
        try { await peer.SendAsync(new JsonObject { ["type"] = "saved", ["ok"] = ok, ["count"] = saved.Count }.ToJsonString()); } catch { }
        History?.Add(new TransferEntry(offer.Id, DateTime.Now, false, label, offer.TotalBytes, offer.Items.Count,
            !ok ? (saved.Count > 0 ? $"Incompleto ({saved.Count} de {offer.Items.Count})" : "Falhou") : received != null ? "Copiado" : "Recebido",
            saved, received));
        var first = saved.FirstOrDefault();
        if (!ok || saved.Count > 0) _ = _ui.InvokeAsync(() => IncomingDone?.Invoke(label, first, ok));
    }

    static async Task<string> ReceiveText(Peer peer, IncomingItem item, CancellationToken cancel)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var bytes = new MemoryStream();
        await peer.ReadRawAsync(bytes, item.Size, hash, cancel);
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (actual != item.Sha256) throw new InvalidDataException("received text doesn't match the hash");
        var text = new UTF8Encoding(false, true).GetString(bytes.ToArray()); // invalid UTF-8 throws
        return text.Length > MaxTextChars ? throw new InvalidDataException("text too long") : text;
    }

    static async Task<string> ReceiveOne(Peer peer, IncomingItem item, CancellationToken cancel)
    {
        var temp = Path.Combine(ReceiveFolder, "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".part");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                File.SetAttributes(temp, FileAttributes.Hidden);
                await peer.ReadRawAsync(file, item.Size, hash, cancel);
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(item.Sha256)))
                throw new InvalidDataException("received bytes don't match the hash");
            var target = UniquePath(ReceiveFolder, item.Name);
            File.Move(temp, target);
            File.SetAttributes(target, FileAttributes.Normal);
            MarkFromInternet(target);
            return target;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>The same mark a browser download gets (Zone.Identifier, Internet zone): Windows SmartScreen and Office
    /// warn before running or editing it.</summary>
    static void MarkFromInternet(string path)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n"); }
        catch (Exception ex) { Log.Error("PhoneLink.MarkFromInternet", ex); }
    }

    static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}){ext}");
        return path;
    }

    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Just a file name Windows can hold: no folders, no reserved or invisible direction characters (which
    /// can make "gpj.exe" read as "exe.jpg"), no trailing dots or spaces, no device names, at most 100 characters.</summary>
    public static string SafeFileName(string raw)
    {
        var name = raw.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Where(c => c >= ' ' && c != '\u007f' && !invalid.Contains(c) && c is not ('<' or '>' or ':' or '"' or '|' or '?' or '*')
            && c is not ('‎' or '‏') && c is not (>= '‪' and <= '‮') && c is not (>= '⁦' and <= '⁩')).ToArray());
        name = name.Trim().TrimStart('.').TrimEnd('.', ' ');
        if (name.Length > 100)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > 10) ext = "";
            name = name[..(100 - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        var stem = name.Split('.')[0];
        if (ReservedNames.Contains(stem)) name = "_" + name;
        return name.Length == 0 ? "arquivo" : name;
    }
}
