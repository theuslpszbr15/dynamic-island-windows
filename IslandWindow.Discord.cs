using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Services;

namespace DynamicIsland;

// Who joins and leaves the Discord call the user is in: the card with their profile picture, a small pill on leaving.
public partial class IslandWindow
{
    DiscordVoice? _discord;

    /// <summary>Created on first use (Configurações needs it to save the app even before the feature is on).</summary>
    public DiscordVoice Discord
    {
        get
        {
            if (_discord != null) return _discord;
            _discord = new DiscordVoice(Dispatcher);
            _discord.Changed += OnDiscordVoice;
            return _discord;
        }
    }

    DiscordWindow? _discordWindow;

    public void ApplyDiscord()
    {
        if (SnapshotMode) return;
        var source = _settings.DiscordSource;
        var rpc = _settings.DiscordCalls && source != DiscordSource.Window;
        if (rpc)
        {
            Discord.OwnApp = source == DiscordSource.OwnApp;
            Discord.Start();
        }
        else _discord?.Stop();

        if (_settings.DiscordCalls && source == DiscordSource.Window && _discordWindow == null)
        {
            _discordWindow = new DiscordWindow(Dispatcher);
            _discordWindow.Changed += OnDiscordVoice;
            _discordWindow.ListHidden += OnDiscordListHidden;
        }
        if (_discordWindow != null)
        {
            _discordWindow.Enabled = _settings.DiscordCalls && source == DiscordSource.Window;
            _discordWindow.SetInCall(_privacy.Microphone.Contains("Discord"));
        }
    }

    // In a call, but the call's server isn't open in Discord: say once why nothing will show.
    void OnDiscordListHidden()
    {
        if (!_settings.DiscordCalls || _settings.DiscordSource != DiscordSource.Window) return;
        ShowInfo("\uE7BA", "Discord: abra o servidor da call", "para ver quem entra", seconds: 4.5);
    }

    void OnDiscordVoice(DiscordVoiceEvent e)
    {
        if (!_settings.DiscordCalls) return;
        if (_settings.DiscordToPhone && _phone?.Running == true && _phone.ConnectedPhones > 0)
            _phone.PublishDiscord(e.Joined, e.Name, e.Channel, e.Count, SmallPng(e.Avatar));
        // The name as shown in the call, plus the @username (unique, so a nickname can't pass for someone else).
        // (Read from the window there is no @username: only the name as shown.)
        var handle = e.Username.Length > 0 ? "@" + e.Username : "";
        var same = handle.Length == 0 || e.Name.Equals(e.Username, StringComparison.OrdinalIgnoreCase);
        if (e.Joined)
            ShowCard(e.Avatar, same ? e.Name : $"{e.Name}  {handle}", "Discord",
                $"Entrou em {e.Channel} · {e.Count} na call", round: true);
        else if (_settings.DiscordLeaves)
            ShowInfo("\uE77B", same ? $"{e.Name} saiu da call" : $"{e.Name} ({handle}) saiu da call", e.Channel,
                thumb: e.Avatar, seconds: 2.6, round: true);
    }

    /// <summary>The avatar as a 64 px PNG for the phone (null without one or if it can't be encoded).</summary>
    static byte[]? SmallPng(BitmapSource? avatar)
    {
        if (avatar == null) return null;
        try
        {
            var scale = 64.0 / Math.Max(avatar.PixelWidth, avatar.PixelHeight);
            BitmapSource small = scale < 1 ? new TransformedBitmap(avatar, new ScaleTransform(scale, scale)) : avatar;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(small));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.Length <= PhoneLink.MaxDiscordAvatarBytes ? stream.ToArray() : null;
        }
        catch (Exception ex)
        {
            Log.Error("Discord.SmallPng", ex);
            return null;
        }
    }
}
