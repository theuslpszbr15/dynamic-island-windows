using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using DynamicIsland.Services;

namespace DynamicIsland;

// What the phone does to this PC: clipboard sync, remote control (music, volume, lock, shutdown) and its calls.
public partial class IslandWindow
{
    [DllImport("user32.dll")] static extern bool LockWorkStation();

    const byte VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;

    /// <summary>Text that just came from the phone: copying it here must not send it straight back.</summary>
    string? _clipFromPhone;
    DateTime? _shutdownAt;
    float _volumeLevel;
    bool _volumeMuted, _callShowing;

    void OnPhoneClip(string text)
    {
        if (!_settings.PhoneClipboard) return;
        _clipFromPhone = text;
        try { Clipboard.SetText(text); }
        catch { return; } // another app holds the clipboard
        ShowInfo("\uE8EA", Preview(text), "Copiado do celular", glyphColor: AgentSession.Done);
    }

    /// <summary>Text dictated on the phone: typed where the person is on the PC (pasted, clipboard given back).</summary>
    async void OnPhoneType(string text)
    {
        if (!_settings.PhoneTyping) return;
        if (await TextPaster.PasteAsync(text)) ShowInfo("\uE8EA", Preview(text), "Ditado do celular", glyphColor: AgentSession.Done);
    }

    /// <summary>Text copied on the PC (password managers already filtered out by OnClipboard).</summary>
    void SendClipToPhone(string text)
    {
        if (!_settings.PhoneClipboard || _phone?.Running != true || _phone.ConnectedPhones == 0) return;
        if (text == _clipFromPhone) return;
        _clipFromPhone = null;
        _phone.PublishClip(text);
    }

    void OnPhoneRemote(string action, int minutes)
    {
        if (!_settings.PhoneRemote) return;
        switch (action)
        {
            case "play": _media.PlayPause(); break;
            case "next": _media.Next(); break;
            case "prev": _media.Previous(); break;
            case "volup": if (!_spotifyVolume.Nudge(0.02f)) Native.PressKey(VK_VOLUME_UP); break;
            case "voldown": if (!_spotifyVolume.Nudge(-0.02f)) Native.PressKey(VK_VOLUME_DOWN); break;
            case "mute": if (!_spotifyVolume.ToggleMute()) Native.PressKey(VK_VOLUME_MUTE); break;
            case "lock":
                if (!SnapshotMode) LockWorkStation();
                break;
            case "shutdown" when minutes > 0:
                if (!SnapshotMode && !RunShutdown($"/s /t {minutes * 60}")) return;
                _shutdownAt = DateTime.Now.AddMinutes(minutes);
                ShowInfo("\uE7E8", $"O PC desliga às {_shutdownAt:HH:mm} (pedido do celular)", "Cancelar",
                    glyphColor: AgentSession.Waiting, action: CancelShutdown, seconds: 8);
                break;
            case "cancelShutdown":
                CancelShutdown();
                break;
        }
        PublishPhonePc();
    }

    void CancelShutdown()
    {
        if (_shutdownAt == null) return;
        if (!SnapshotMode) RunShutdown("/a");
        _shutdownAt = null;
        ShowInfo("\uE7E8", "Desligamento cancelado", "", glyphColor: AgentSession.Done);
        PublishPhonePc();
    }

    static bool RunShutdown(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { CreateNoWindow = true, UseShellExecute = false });
            return process != null;
        }
        catch { return false; }
    }

    void OnPhoneCall(string state, string name, string number)
    {
        if (!_settings.PhoneCalls) return;
        if (state == "ringing")
        {
            var who = name.Length > 0 ? name : number.Length > 0 ? number : "Número desconhecido";
            _callShowing = true;
            ShowCard(null, who, "Celular", name.Length > 0 && number.Length > 0 ? $"Ligação chegando · {number}" : "Ligação chegando",
                seconds: 30, glyph: "\uE717");
            return;
        }
        if (!_callShowing) return;
        _callShowing = false;
        if (_transient == Transient.Notification) TransientEnded();
    }

    /// <summary>The phone's remote shows what is playing, the volume and a pending shutdown.</summary>
    void PublishPhonePc()
    {
        if (_phone?.Running != true || _demo) return;
        if (_shutdownAt is { } at && at < DateTime.Now) _shutdownAt = null;
        var m = _mediaSnap;
        _phone.PublishPcState(m?.Title ?? "", m?.Artist ?? "", m?.IsPlaying == true, (int)Math.Round(_volumeLevel * 100), _volumeMuted,
            _shutdownAt is { } when ? new DateTimeOffset(when).ToUnixTimeMilliseconds() : 0);
    }
}
