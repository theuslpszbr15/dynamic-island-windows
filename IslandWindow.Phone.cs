using System.Security.Cryptography;
using System.Text;
using DynamicIsland.Services;

namespace DynamicIsland;

// The link to the Android app: sessions and permission requests go out, Allow/Deny comes back.
public partial class IslandWindow
{
    const int PhoneCommandLimit = 500;
    PhoneLink? _phone;

    public PhoneLink? Phone => _phone;
    public string? PhoneError { get; private set; }

    public void ApplyPhoneLink()
    {
        if (!_settings.PhoneLink)
        {
            _phone?.Stop();
            UpdatePhoneSend();
            return;
        }
        if (_phone == null)
        {
            _phone = new PhoneLink(Dispatcher);
            _phone.Decision += OnPhoneDecision;
            _phone.Open += OnPhoneOpen;
            _phone.PhoneJoined += OnPhoneJoined;
            _phone.PhonesChanged += UpdatePhoneSend;
            _phone.FileStatus += OnPhoneFile;
            _phone.IncomingOffer += OnPhoneIncoming;
            _phone.IncomingText += OnPhoneText;
            _phone.IncomingDone += OnPhoneIncomingDone;
        }
        _phone.SetRelay(_settings.PhoneRelay);
        if (_phone.Running) return;
        try { _phone.Start(); PhoneError = null; }
        catch (Exception ex)
        {
            PhoneError = ex.Message;
            ShowInfo("\uE8EA", "Ligação com o celular não iniciou", ex.Message, glyphColor: Meter.Red, seconds: 5);
            return;
        }
        PublishPhoneSessions();
        PublishPhoneTray();
        foreach (var approval in _approvals) PublishApproval(approval);
    }

    // Coming in over the internet is new, so it's worth a glance: if it wasn't you, "Parear de novo" cuts it off.
    void OnPhoneJoined(string device, bool viaRelay)
    {
        if (!viaRelay || SnapshotMode) return;
        ShowInfo("", "Celular conectado pela internet", device.Length > 0 ? device : "Android",
            action: () => ((App)System.Windows.Application.Current).ShowSettings("phone"), seconds: 6);
    }

    static string TrayId(FileItem item) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Path.ToLowerInvariant())))[..16].ToLowerInvariant();

    public void PublishPhoneTray()
    {
        if (_phone?.Running != true) return;
        if (!_settings.PhoneTray)
        {
            _phone.PublishTray([]);
            return;
        }
        _phone.PublishTray(Files.Select(f => new PhoneTrayItem(TrayId(f), f.Name, TrayKind(f.Path), IconPng(f.Icon))));
    }

    static string TrayKind(string path) =>
        Directory.Exists(path) ? "folder"
        : System.IO.Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk" or ".url" or ".appref-ms" or ".bat" or ".cmd" ? "app"
        : "file";

    static string? IconPng(System.Windows.Media.ImageSource? icon)
    {
        if (icon is not System.Windows.Media.Imaging.BitmapSource bitmap) return null;
        try
        {
            var scaled = new System.Windows.Media.Imaging.TransformedBitmap(bitmap,
                new System.Windows.Media.ScaleTransform(48.0 / bitmap.PixelWidth, 48.0 / bitmap.PixelHeight));
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Convert.ToBase64String(stream.ToArray());
        }
        catch { return null; }
    }

    /// <summary>Items opened from the phone in snapshot mode (nothing is launched there).</summary>
    public List<string> PhoneOpened { get; } = [];

    // Only something already pinned in the tray, found by its id; the phone never sends a path.
    void OnPhoneOpen(string id)
    {
        if (!_settings.PhoneTray) return;
        var item = Files.FirstOrDefault(f => TrayId(f) == id);
        if (item == null) return;
        if (SnapshotMode)
        {
            PhoneOpened.Add(item.Name);
            return;
        }
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Path) { UseShellExecute = true }); }
        catch { return; }
        ShowInfo("\uE8EA", "Aberto pelo celular", item.Name, glyphColor: AgentSession.Done);
    }

    void PublishApproval(Approval a)
    {
        if (_phone?.Running != true) return;
        // A command the phone can't show whole can only be denied there.
        var truncated = a.Command.Length > PhoneCommandLimit;
        _phone.PublishApproval(new PhoneApproval(a.Fingerprint, a.Agent, a.Project,
            truncated ? a.Command[..PhoneCommandLimit] : a.Command, Millis(a.Since), truncated));
    }

    void PublishPhoneSessions()
    {
        if (_phone?.Running != true) return;
        _phone.PublishSessions(Agents.Select(s => new PhoneSession(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Key)))[..16].ToLowerInvariant(),
            s.Agent, s.Project,
            _approvals.Any(a => a.SessionKey == s.Key) ? "approval" : s.State,
            s.Detail, Millis(s.UpdatedAt))));
    }

    // Applied only to the request still pending with that exact fingerprint.
    void OnPhoneDecision(string fingerprint, string decision)
    {
        var approval = _approvals.FirstOrDefault(a => a.Fingerprint == fingerprint);
        if (approval == null || DateTime.Now - approval.Since > ApprovalWait) return;
        if (decision == "allow" && approval.Command.Length > PhoneCommandLimit) return;
        Answer(approval, decision);
        PublishPhoneSessions();
    }

    static long Millis(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();

    // ───────────────────────── files to the phone ─────────────────────────

    /// <summary>A phone is connected and sending is on: the drop hint gets its "celular" side and tray items a button.</summary>
    bool CanSendToPhone => _settings.PhoneLink && _settings.PhoneFiles && _phone?.Running == true && _phone.ConnectedPhones > 0;

    public bool PhoneReady => CanSendToPhone;

    public void UpdatePhoneSend() =>
        Resources["PhoneSendVisibility"] = CanSendToPhone ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>Offers the files to the phone as one batch (one Aceitar there); the island says what happened.
    /// Folders and big files are refused here (and again on the phone).</summary>
    public async void SendToPhone(IReadOnlyList<string> paths)
    {
        if (_phone == null || !CanSendToPhone)
        {
            ShowInfo("\uE8EA", "Nenhum celular conectado", "", glyphColor: Meter.Red, seconds: 3);
            return;
        }
        var label = paths.Count == 1 ? System.IO.Path.GetFileName(paths[0].TrimEnd('\\', '/')) : $"{paths.Count} arquivos";
        if (await _phone.OfferFilesAsync(paths) is { } error)
            ShowInfo("\uE8EA", label, error, glyphColor: Meter.Red, seconds: 3);
        else
            ShowInfo("\uE8EA", label, "Aceite no celular", seconds: 3);
    }

    public void SendTextToPhone(string text)
    {
        if (_phone == null || !CanSendToPhone)
        {
            ShowInfo("\uE8EA", "Nenhum celular conectado", "", glyphColor: Meter.Red, seconds: 3);
            return;
        }
        if (_phone.SendText(text) is { } error) ShowInfo("\uE8C8", "Texto", error, glyphColor: Meter.Red, seconds: 3);
        else ShowInfo("\uE8C8", Preview(text), "No celular", glyphColor: AgentSession.Done);
    }

    /// <summary>Whatever is in the clipboard: files, an image (as a PNG) or text.</summary>
    public void SendClipboardToPhone()
    {
        try
        {
            if (System.Windows.Clipboard.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: > 0 } files)
                SendToPhone(files);
            else if (System.Windows.Clipboard.ContainsImage() && System.Windows.Clipboard.GetImage() is { } image)
                SendToPhone([SaveClipboardImage(image)]);
            else if (System.Windows.Clipboard.ContainsText())
                SendTextToPhone(System.Windows.Clipboard.GetText());
            else
                ShowInfo("\uE8C8", "Nada copiado para enviar", "", seconds: 2);
        }
        catch (Exception ex)
        {
            Log.Error("SendClipboardToPhone", ex);
            ShowInfo("\uE8C8", "Não deu para ler o que foi copiado", "", glyphColor: Meter.Red, seconds: 3);
        }
    }

    // A screenshot in the clipboard goes as a PNG from a temp folder only this user can read.
    static string SaveClipboardImage(System.Windows.Media.Imaging.BitmapSource image)
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Dynamic Island");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder, "imagem-*.png"))
            if (File.GetLastWriteTime(old) < DateTime.Now.AddHours(-1)) try { File.Delete(old); } catch { }
        var path = System.IO.Path.Combine(folder, $"imagem-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);
        return path;
    }

    static string Preview(string text)
    {
        var line = string.Join(' ', text.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    void OnPhoneFile(string label, PhoneLink.FileOutcome outcome)
    {
        switch (outcome)
        {
            case PhoneLink.FileOutcome.Sending: ShowInfo("\uE8EA", label, "Enviando…", seconds: 3); break;
            case PhoneLink.FileOutcome.Received: ShowInfo("\uE73E", label, "No celular", glyphColor: AgentSession.Done); break;
            case PhoneLink.FileOutcome.Declined: ShowInfo("\uE8EA", label, "Recusado"); break;
            case PhoneLink.FileOutcome.Failed: ShowInfo("\uE8EA", label, "Falhou", glyphColor: Meter.Red, seconds: 3); break;
            case PhoneLink.FileOutcome.Expired: ShowInfo("\uE8EA", label, "Não aceito"); break;
        }
    }

    // The phone wants to send something: one click on the pill accepts; ignoring it declines (after 45 s).
    void OnPhoneIncoming(PhoneIncoming offer)
    {
        if (_phone == null || SnapshotMode) return;
        var phone = _phone;
        if (!_settings.PhoneFiles)
        {
            phone.DeclineIncoming(offer.Id);
            return;
        }
        if (offer.Text != null)
        {
            ShowInfo("\uE8C8", "Texto do celular: " + Preview(offer.Text), "Clique p/ copiar", action: () =>
            {
                if (!phone.AcceptIncoming(offer.Id)) return;
                CopyFromPhone(offer.Text);
            }, seconds: 30);
            return;
        }
        var what = offer.Items.Count == 1 ? offer.Items[0].IsText ? "um texto longo" : offer.Items[0].Name : $"{offer.Items.Count} arquivos";
        ShowInfo("\uE896", $"{offer.Device} quer mandar {what} ({SizeText(offer.TotalBytes)})", "Clique p/ aceitar",
            action: () => phone.AcceptIncoming(offer.Id), seconds: 30);
    }

    void OnPhoneText(string text) => CopyFromPhone(text);

    void CopyFromPhone(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            ShowInfo("\uE8C8", Preview(text), "Copiado do celular", glyphColor: AgentSession.Done);
        }
        catch { ShowInfo("\uE8C8", "Não deu para copiar o texto", "", glyphColor: Meter.Red, seconds: 3); }
    }

    // Saved in Downloads\Dynamic Island; the click shows it in Explorer (it is never opened by itself).
    void OnPhoneIncomingDone(string label, string? first, bool ok)
    {
        if (!ok)
        {
            ShowInfo("\uE896", label, "Falhou", glyphColor: Meter.Red, seconds: 3);
            return;
        }
        ShowInfo("\uE896", label, "Recebido", glyphColor: AgentSession.Done, action: () =>
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", first != null ? $"/select,\"{first}\"" : $"\"{PhoneLink.ReceiveFolder}\"");
            }
            catch { }
        }, seconds: 5);
    }

    static string SizeText(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / (1024.0 * 1024.0):0.#} MB";
}
