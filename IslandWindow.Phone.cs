using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

// The link to the Android app: sessions and permission requests go out, Allow/Deny comes back.
public partial class IslandWindow
{
    const int PhoneCommandLimit = 500;
    PhoneLink? _phone;

    public PhoneLink? Phone => _phone;

    TransferHistory? _history;
    /// <summary>Recebidos / Enviados of the phone window (kept on disk, encrypted; in memory only in snapshot mode).</summary>
    public TransferHistory History => _history ??= new TransferHistory(SnapshotMode ? null : TransferHistory.DefaultFile);
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
            _phone.RelayTrouble += OnRelayTrouble;
            _phone.PendingChanged += OnPhonePending;
            _phone.History = History;
        }
        _phone.SetRelay(_settings.EffectivePhoneRelay, own: _settings.PhoneRelayMode == PhoneRelayMode.Own);
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

    // The relay refused this PC (a limit, or a key it doesn't know): once per problem, not on every retry.
    void OnRelayTrouble(string message)
    {
        if (SnapshotMode) return;
        ShowInfo("", "Repasse do celular", message, glyphColor: Meter.Red,
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

    const string NoPhone = "Nenhum celular conectado";

    public void SendToPhone(IReadOnlyList<string> paths) => _ = SendFilesToPhoneAsync(paths);

    /// <summary>Offers the files to the phone as one batch (one Aceitar there); the island says what happened.
    /// Folders and big files are refused here (and again on the phone). Null when offered, otherwise why not.</summary>
    public async Task<string?> SendFilesToPhoneAsync(IReadOnlyList<string> paths)
    {
        if (_phone == null || !CanSendToPhone)
        {
            ShowInfo("\uE8EA", NoPhone, "", glyphColor: Meter.Red, seconds: 3);
            return NoPhone;
        }
        var label = paths.Count == 1 ? System.IO.Path.GetFileName(paths[0].TrimEnd('\\', '/')) : $"{paths.Count} arquivos";
        var error = await _phone.OfferFilesAsync(paths);
        if (error != null)
            ShowInfo("\uE8EA", label, error, glyphColor: Meter.Red, seconds: 3);
        else
            ShowInfo("\uE8EA", label, "Aceite no celular", seconds: 3);
        return error;
    }

    /// <summary>Null when sent, otherwise why not.</summary>
    public string? SendTextToPhone(string text)
    {
        if (_phone == null || !CanSendToPhone)
        {
            ShowInfo("\uE8EA", NoPhone, "", glyphColor: Meter.Red, seconds: 3);
            return NoPhone;
        }
        var error = _phone.SendText(text);
        if (error != null) ShowInfo("\uE8C8", "Texto", error, glyphColor: Meter.Red, seconds: 3);
        else ShowInfo("\uE8C8", Preview(text), "No celular", glyphColor: AgentSession.Done);
        return error;
    }

    public void SendClipboardToPhone() => _ = SendClipboardToPhoneAsync();

    /// <summary>Whatever is in the clipboard: files, an image (as a PNG) or text. Null when sent, otherwise why not.</summary>
    public async Task<string?> SendClipboardToPhoneAsync()
    {
        try
        {
            if (System.Windows.Clipboard.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: > 0 } files)
                return await SendFilesToPhoneAsync(files);
            if (System.Windows.Clipboard.ContainsImage() && System.Windows.Clipboard.GetImage() is { } image)
                return await SendFilesToPhoneAsync([SaveClipboardImage(image)]);
            if (System.Windows.Clipboard.ContainsText())
                return SendTextToPhone(System.Windows.Clipboard.GetText());
            ShowInfo("\uE8C8", "Nada copiado para enviar", "", seconds: 2);
            return "Nada copiado para enviar";
        }
        catch (Exception ex)
        {
            Log.Error("SendClipboardToPhone", ex);
            ShowInfo("\uE8C8", "Não deu para ler o que foi copiado", "", glyphColor: Meter.Red, seconds: 3);
            return "Não deu para ler o que foi copiado";
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

    // The phone wants to send something: one click on the pill accepts; ignored, it moves to Pendentes after 45 s.
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

    // ───────────────────────── Pendentes ─────────────────────────

    int _pendingCount;

    /// <summary>What the phone wants to send and nobody answered yet (the island's Pendentes page).</summary>
    public IReadOnlyList<PhoneIncoming> PhonePending => _phone?.PendingIncoming ?? [];

    /// <summary>Raised when <see cref="PhonePending"/> changes (the settings window listens too).</summary>
    public event Action? PhonePendingChanged;

    void OnPhonePending()
    {
        var count = PhonePending.Count;
        // a new one moved here: a short note, clicking it opens the page
        if (count > _pendingCount && !SnapshotMode)
            ShowInfo("\uE823", "Ficou nos pendentes", count == 1 ? "1 pedido" : $"{count} pedidos",
                action: OpenPending, seconds: 4);
        _pendingCount = count;
        if (_expanded && _page == PagePending) UpdatePendingUi();
        PhonePendingChanged?.Invoke();
    }

    public void OpenPending() => SetExpanded(true, PagePending);

    /// <summary>Accepts a pending batch (the phone sends it now, or as soon as it connects again).</summary>
    public void AcceptPending(PhoneIncoming offer)
    {
        if (_phone?.AcceptIncoming(offer.Id) != true) return;
        if (offer.Text != null) CopyFromPhone(offer.Text);
        else ShowInfo("\uE896", What(offer), _phone.ConnectedPhones > 0 ? "Recebendo…" : "Chega quando o celular conectar", seconds: 3);
    }

    public void DeclinePending(PhoneIncoming offer) => _phone?.DeclineIncoming(offer.Id);

    static string What(PhoneIncoming offer) =>
        offer.Text != null ? "Texto: " + Preview(offer.Text)
        : offer.Items.Count == 1 ? offer.Items[0].IsText ? "Um texto longo" : offer.Items[0].Name
        : $"{offer.Items.Count} arquivos";

    void UpdatePendingUi()
    {
        var list = PhonePending;
        PendingEmpty.Visibility = Vis(list.Count == 0);
        PendingList.Children.Clear();
        foreach (var offer in list)
        {
            var row = new Grid { Margin = new System.Windows.Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });
            var kind = offer.Text != null || offer.Items.Any(i => i.IsText) ? "\uE8C8"
                : offer.Items.All(i => MediaFiles.IsImage(i.Name)) ? "\uEB9F"
                : offer.Items.All(i => MediaFiles.IsVideo(i.Name)) ? "\uE714" : "\uE8A5";
            var icon = new TextBlock
            {
                Text = kind, FontFamily = (System.Windows.Media.FontFamily)FindResource("Icons"), FontSize = 20, Width = 34,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            row.Children.Add(icon);
            var info = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new System.Windows.Thickness(0, 0, 8, 0) };
            info.Children.Add(new TextBlock { Text = What(offer), FontSize = 13, FontWeight = System.Windows.FontWeights.SemiBold, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis });
            var left = offer.At + PhoneLink.PendingLifetime - DateTime.Now;
            var detail = new TextBlock
            {
                FontSize = 11.5, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
                Text = $"{offer.Device} · {(offer.Text != null ? $"{offer.Text.Length} caracteres" : SizeText(offer.TotalBytes))} · " +
                       $"{(offer.At.Date == DateTime.Today ? $"às {offer.At:HH:mm}" : $"{offer.At:dd/MM HH:mm}")} · vale mais {Math.Max(1, (int)left.TotalHours)} h",
            };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            info.Children.Add(detail);
            Grid.SetColumn(info, 1);
            row.Children.Add(info);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            var decline = new System.Windows.Controls.Button { Content = "Recusar", Style = (System.Windows.Style)FindResource("Chip"), Height = 30 };
            decline.Click += (_, _) => DeclinePending(offer);
            var accept = new System.Windows.Controls.Button
            {
                Content = offer.Text != null ? "Copiar" : "Aceitar", Style = (System.Windows.Style)FindResource("Chip"), Height = 30,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF)),
            };
            accept.Click += (_, _) => AcceptPending(offer);
            buttons.Children.Add(decline);
            buttons.Children.Add(accept);
            Grid.SetColumn(buttons, 2);
            row.Children.Add(buttons);
            PendingList.Children.Add(row);
        }
    }

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
