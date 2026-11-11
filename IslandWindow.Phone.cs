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
            return;
        }
        if (_phone == null)
        {
            _phone = new PhoneLink(Dispatcher);
            _phone.Decision += OnPhoneDecision;
            _phone.Open += OnPhoneOpen;
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
}
