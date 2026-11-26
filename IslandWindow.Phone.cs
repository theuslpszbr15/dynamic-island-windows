using System.Security.Cryptography;
using System.Text;
using System.Windows;
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
    public TransferHistory History
    {
        get
        {
            if (_history != null) return _history;
            _history = new TransferHistory(SnapshotMode ? null : TransferHistory.DefaultFile);
            _history.Changed += () => Dispatcher.InvokeAsync(() => { if (_expanded && _page == PagePending) UpdatePendingUi(); });
            return _history;
        }
    }
    public string? PhoneError { get; private set; }

    UsbBridge? _usb;
    public UsbBridge? Usb => _usb;

    /// <summary>Starts or stops keeping the USB tunnel up, to match the settings and the link.</summary>
    public void ApplyPhoneUsb()
    {
        if (!_settings.PhoneLink || !_settings.PhoneUsb || _phone is not { Running: true })
        {
            _usb?.Stop();
            return;
        }
        _usb ??= new UsbBridge();
        _usb.Start(() => _phone.Port);
    }

    public void ApplyPhoneLink()
    {
        if (!_settings.PhoneLink)
        {
            _phone?.Stop();
            UpdatePhoneSend();
            ApplyPhoneUsb();
            return;
        }
        if (_phone == null)
        {
            _phone = new PhoneLink(Dispatcher);
            _phone.Decision += OnPhoneDecision;
            _phone.Open += OnPhoneOpen;
            _phone.PhoneJoined += OnPhoneJoined;
            _phone.OldApp += OnOldApp;
            _phone.PhonesChanged += UpdatePhoneSend;
            _phone.FileStatus += OnPhoneFile;
            _phone.IncomingOffer += OnPhoneIncoming;
            _phone.IncomingText += OnPhoneText;
            _phone.IncomingDone += OnPhoneIncomingDone;
            _phone.RelayTrouble += OnRelayTrouble;
            _phone.PendingChanged += OnPhonePending;
            _phone.Clip += OnPhoneClip;
            _phone.Type += OnPhoneType;
            _phone.AskJarvis += OnPhoneJarvis;
            _phone.Remote += OnPhoneRemote;
            _phone.Call += OnPhoneCall;
            _phone.FilterFromPhone += OnPhoneFilter;
            _phone.PhonesChanged += PublishPhonePc;
            _phone.History = History;
        }
        _phone.SetRelay(_settings.EffectivePhoneRelay, own: _settings.PhoneRelayMode == PhoneRelayMode.Own);
        UpdatePhoneSend();
        if (_phone.Running) return;
        try { _phone.Start(); PhoneError = null; }
        catch (Exception ex)
        {
            PhoneError = ex.Message;
            ShowInfo("\uE8EA", "Ligação com o celular não iniciou", ex.Message, glyphColor: Meter.Red, seconds: 5);
            return;
        }
        if (_settings.IslandOffAt > 0) _phone.PublishFilter(_settings.IslandOffKinds, _settings.IslandOffAt);
        PublishPhoneSessions();
        PublishPhoneTray();
        ApplyPhoneUsb();
#if DEBUG
        // End-to-end tests of the phone app read the pairing link from a file instead of scanning the QR code.
        if (Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_TEST_LINK_FILE") is { Length: > 0 } linkFile)
            File.WriteAllText(linkFile, _phone.PairingLink(true));
#endif
        foreach (var approval in _approvals) PublishApproval(approval);
    }

    // Coming in over the internet is new, so it's worth a glance: if it wasn't you, "Parear de novo" cuts it off.
    readonly HashSet<string> _oldAppWarned = [];

    // Once per phone while the island runs. With file sending on, the update goes straight to the phone (it shows there
    // as a file to accept, then Android installs it); otherwise the QR code to download it.
    void OnOldApp(string device)
    {
        if (SnapshotMode || !_oldAppWarned.Add(device)) return;
        var direct = CanSendToPhone;
        ShowInfo("\uE8EA", "App do celular desatualizado", direct ? "Enviar atualização" : $"Atualize para {PhoneLink.AndroidVersionName}",
            glyphColor: System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x0A), seconds: 12,
            action: direct ? () => _ = SendAppUpdateToPhoneAsync() : () => ((App)System.Windows.Application.Current).ShowAppDownload());
    }

    /// <summary>Downloads the phone app from the latest release (checked against its SHA-256) and offers it to the phone.</summary>
    public async Task SendAppUpdateToPhoneAsync()
    {
        if (!CanSendToPhone)
        {
            ((App)System.Windows.Application.Current).ShowAppDownload();
            return;
        }
        ShowInfo("\uE896", "Baixando o app do celular…", "", seconds: 30);
        if (await Updater.DownloadAndroidAppAsync() is not { } apk)
        {
            ShowInfo("\uE8EA", "Não consegui baixar o app agora", "Ver QR", glyphColor: Meter.Red, seconds: 6,
                action: () => ((App)System.Windows.Application.Current).ShowAppDownload());
            return;
        }
        // the phone shows it as a file to accept; tapping it opens Android's installer
        await SendFilesToPhoneAsync([apk]);
    }

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
        if (_phone?.Running != true || _demo) return;
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
        // The phone can't Allow what the island itself would refuse (a second line could hide what would run).
        if (decision == "allow" && !ApprovalRules.IslandCanAllow(approval.Command)) return;
        Answer(approval, decision);
        PublishPhoneSessions();
    }

    static long Millis(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();

    // ───────────────────────── files to the phone ─────────────────────────

    /// <summary>A phone is connected and sending is on: the drop hint gets its "celular" side and tray items a button.</summary>
    bool CanSendToPhone => _settings.PhoneLink && _settings.PhoneFiles && _phone?.Running == true && _phone.ConnectedPhones > 0;

    public bool PhoneReady => CanSendToPhone;

    public void UpdatePhoneSend()
    {
        Resources["PhoneSendVisibility"] = CanSendToPhone ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        Resources["PhoneActionsVisibility"] = _settings.PhoneLink && _settings.PhoneFiles
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    void PhonePick_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Mandar pro celular" };
        if (dialog.ShowDialog(this) == true) SendToPhone(dialog.FileNames);
    }

    void PhoneClipboard_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        SendClipboardToPhone();
    }

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
        if (_settings.FitVideos != VideoFitMode.Never) paths = await FitVideosAsync(paths);
        var error = await _phone.OfferFilesAsync(paths);
        if (error != null)
            ShowInfo("\uE8EA", label, error, glyphColor: Meter.Red, seconds: 3);
        else
            ShowInfo("\uE8EA", label, "Aceite no celular", seconds: 3);
        return error;
    }

    /// <summary>Videos a phone may not play (1080p60, 4K, HEVC…) converted first: asked on the island (the card running
    /// out sends them as they are) or always, per the setting. A video that fails to convert goes as it is.</summary>
    async Task<IReadOnlyList<string>> FitVideosAsync(IReadOnlyList<string> paths)
    {
        var heavy = new List<(string Path, VideoFit.Info Info, string Problem)>();
        foreach (var path in paths.Where(VideoFit.IsVideo))
            if (await VideoFit.ProbeAsync(path) is { } info && VideoFit.Problem(info) is { } problem) heavy.Add((path, info, problem));
        if (heavy.Count == 0) return paths;
        if (_settings.FitVideos == VideoFitMode.Ask)
        {
            var choice = new TaskCompletionSource<bool>();
            var name = heavy.Count == 1 ? System.IO.Path.GetFileName(heavy[0].Path) : $"{heavy.Count} vídeos";
            var card = ShowActions(heavy.Count == 1 ? "Esse vídeo pode travar no celular" : "Esses vídeos podem travar no celular",
                $"{heavy[0].Problem} · {name}",
                [new("\uE895", "Converter", () => choice.TrySetResult(true), Primary: true), new("\uE724", "Mandar assim", () => choice.TrySetResult(false))],
                glyph: "\uE714", seconds: 20, important: true);
            while (!choice.Task.IsCompleted && _transient == Transient.Action && ReferenceEquals(_actionCard, card))
                await Task.WhenAny(choice.Task, Task.Delay(300));
            if (!choice.Task.IsCompleted || !choice.Task.Result) return paths;
        }
        var result = paths.ToList();
        foreach (var (path, info, _) in heavy)
        {
            var label = System.IO.Path.GetFileName(path);
            var shown = -1;
            var progress = new Progress<double>(p =>
            {
                var percent = (int)(p * 100);
                if (percent == shown) return;
                shown = percent;
                ShowInfo("\uE714", $"Convertendo {label}", $"{percent}%", seconds: 30);
            });
            ShowInfo("\uE714", $"Convertendo {label}", "0%", seconds: 30);
            if (await VideoFit.ConvertAsync(path, info, progress) is { } fitted) result[result.IndexOf(path)] = fitted;
            else ShowInfo("\uE714", $"Não deu para converter {label}", "vai assim", glyphColor: Meter.Red, seconds: 3);
        }
        return result;
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

    const int PhoneRows = 3;

    void UpdatePendingUi()
    {
        var list = PhonePending;
        var received = History.Entries.Where(e => !e.Sent).Take(Math.Max(0, PhoneRows - list.Count)).ToList();
        PendingEmpty.Visibility = Vis(list.Count == 0 && received.Count == 0);
        PendingList.Children.Clear();
        foreach (var offer in list)
        {
            var kind = offer.Text != null || offer.Items.Any(i => i.IsText) ? "\uE8C8"
                : offer.Items.All(i => MediaFiles.IsImage(i.Name)) ? "\uEB9F"
                : offer.Items.All(i => MediaFiles.IsVideo(i.Name)) ? "\uE714" : "\uE8A5";
            var left = offer.At + PhoneLink.PendingLifetime - DateTime.Now;
            PendingList.Children.Add(PhoneRow(kind, What(offer),
                $"{offer.Device} · {(offer.Text != null ? $"{offer.Text.Length} caracteres" : SizeText(offer.TotalBytes))} · " +
                $"{When(offer.At)} · vale mais {Math.Max(1, (int)left.TotalHours)} h",
                ("Recusar", false, () => DeclinePending(offer)),
                (offer.Text != null ? "Copiar" : "Aceitar", true, () => AcceptPending(offer))));
        }
        foreach (var entry in received)
        {
            var image = entry.Paths.Count == 1 && MediaFiles.IsImage(entry.Paths[0]) && File.Exists(entry.Paths[0]);
            var buttons = new List<(string, bool, Action)>();
            if (entry.Text is { } text) buttons.Add(("Copiar", true, () => CopyFromPhone(text)));
            else
            {
                if (image) buttons.Add(("Copiar", true, () => CopyImage(entry.Paths[0])));
                if (entry.Paths.FirstOrDefault(File.Exists) is { } first) buttons.Add(("Mostrar", !image, () => ShowInFolder(first)));
            }
            PendingList.Children.Add(PhoneRow(entry.Text != null ? "\uE8C8" : image ? "\uEB9F" : "\uE896",
                entry.Text is { } t ? Preview(t) : entry.Title,
                $"Recebido · {(entry.Text != null ? $"{entry.Text.Length} caracteres" : SizeText(entry.Bytes))} · {When(entry.When)}",
                [.. buttons]));
        }
    }

    static string When(DateTime at) => at.Date == DateTime.Today ? $"às {at:HH:mm}" : $"{at:dd/MM HH:mm}";

    FrameworkElement PhoneRow(string glyph, string title, string line, params (string Label, bool Main, Action Click)[] buttons)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 20, Width = 34, VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        row.Children.Add(icon);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        info.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Text = line };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        info.Children.Add(detail);
        Grid.SetColumn(info, 1);
        row.Children.Add(info);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, main, click) in buttons)
        {
            var b = new Button { Content = label, Style = (Style)FindResource("Chip"), Height = 30, Margin = new Thickness(6, 0, 0, 0) };
            if (main) b.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            b.Click += (_, e) => { e.Handled = true; click(); };
            panel.Children.Add(b);
        }
        Grid.SetColumn(panel, 2);
        row.Children.Add(panel);
        return row;
    }

    void CopyImage(string path)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            Clipboard.SetImage(image);
            ShowInfo("\uEB9F", Path.GetFileName(path), "Imagem copiada", glyphColor: AgentSession.Done);
        }
        catch { ShowInfo("\uEB9F", "Não deu para copiar a imagem", "", glyphColor: Meter.Red, seconds: 3); }
    }

    static void ShowInFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch { }
    }

    void PhoneAll_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ((App)Application.Current).ShowPhoneWindow("received");
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

    /// <summary>A phone's "Avisos da ilha" changed (or it just connected). The newest change wins and goes to every phone;
    /// an older one is answered with what the PC has, so the phone catches up.</summary>
    void OnPhoneFilter(IReadOnlyList<string> off, long at)
    {
        if (PhoneLink.FilterIsNewer(_settings.IslandOffAt, at))
        {
            _settings.IslandOffKinds = off.ToList();
            _settings.IslandOffAt = at;
            _settings.Save();
            _phone?.PublishFilter(off, at);
        }
        else if (_settings.IslandOffAt > at)
            _phone?.PublishFilter(_settings.IslandOffKinds, _settings.IslandOffAt);
    }

    /// <summary>Turns one notice on or off on the phone's island from the PC's settings; goes to every phone.</summary>
    public void SetNoticeShown(string kind, bool shown)
    {
        var next = _settings.IslandOffKinds.Where(k => k != kind).ToList();
        if (!shown) next.Add(kind);
        if (next.Count == _settings.IslandOffKinds.Count && next.All(_settings.IslandOffKinds.Contains)) return;
        _settings.IslandOffKinds = next;
        _settings.IslandOffAt = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _settings.IslandOffAt + 1);
        _settings.Save();
        _phone?.PublishFilter(next, _settings.IslandOffAt);
    }

    public bool NoticeShown(string kind) => !NoticeOff(kind);

    bool NoticeOff(string kind) => _settings.IslandOffKinds.Contains(kind);
}
