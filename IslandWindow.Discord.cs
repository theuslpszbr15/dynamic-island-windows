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

    public void ApplyDiscord()
    {
        if (SnapshotMode) return;
        if (_settings.DiscordCalls) Discord.Start();
        else _discord?.Stop();
    }

    void OnDiscordVoice(DiscordVoiceEvent e)
    {
        if (!_settings.DiscordCalls) return;
        if (e.Joined)
            ShowCard(e.Avatar, e.Name, "Discord", $"Entrou em {e.Channel} · {e.Count} na call", round: true);
        else if (_settings.DiscordLeaves)
            ShowInfo("", $"{e.Name} saiu da call", e.Channel, thumb: e.Avatar, seconds: 2.6, round: true);
    }
}
