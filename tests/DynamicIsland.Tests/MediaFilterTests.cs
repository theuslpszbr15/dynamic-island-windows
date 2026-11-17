using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class MediaFilterTests
{
    [Fact]
    public void NormalizesKeysCorrectly()
    {
        Assert.Equal("spotify", MediaFilter.NormalizeKey("Spotify.exe"));
        Assert.Equal("chrome", MediaFilter.NormalizeKey(@"C:\Program Files\Google\Chrome\chrome.exe"));
        Assert.Equal("mediaplayer", MediaFilter.NormalizeKey("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"));
        Assert.Equal("discord", MediaFilter.NormalizeKey("DiscordCanary.exe"));
        Assert.Equal("discord", MediaFilter.NormalizeKey("discordptb"));
        Assert.Equal("msedge", MediaFilter.NormalizeKey("msedge"));
    }

    [Fact]
    public void IdentifiesCategories()
    {
        Assert.Equal(MediaAppCategory.Music, MediaFilter.GetCategory("spotify"));
        Assert.Equal(MediaAppCategory.Music, MediaFilter.GetCategory("vlc"));
        Assert.Equal(MediaAppCategory.Music, MediaFilter.GetCategory("applemusic"));
        Assert.Equal(MediaAppCategory.Browser, MediaFilter.GetCategory("chrome"));
        Assert.Equal(MediaAppCategory.Browser, MediaFilter.GetCategory("firefox"));
        Assert.Equal(MediaAppCategory.Browser, MediaFilter.GetCategory("msedge"));
        Assert.Equal(MediaAppCategory.Call, MediaFilter.GetCategory("discord"));
        Assert.Equal(MediaAppCategory.Call, MediaFilter.GetCategory("teams"));
        Assert.Equal(MediaAppCategory.Call, MediaFilter.GetCategory("zoom"));
    }

    [Fact]
    public void MusicOnlyPreset_AllowsOnlyMusic()
    {
        var settings = new IslandSettings
        {
            ReadOnly = true,
            MediaFilterPreset = MediaFilterPreset.MusicOnly
        };

        // Music players allowed
        Assert.True(MediaFilter.IsAllowed("spotify", settings));
        Assert.True(MediaFilter.IsAllowed("vlc", settings));
        Assert.True(MediaFilter.IsAllowed("deezer", settings));

        // Browsers blocked (no TikTok or random web audio)
        Assert.False(MediaFilter.IsAllowed("chrome", settings));
        Assert.False(MediaFilter.IsAllowed("msedge", settings));
        Assert.False(MediaFilter.IsAllowed("firefox", settings));

        // Calls blocked (no Discord voice chatter)
        Assert.False(MediaFilter.IsAllowed("discord", settings));
        Assert.False(MediaFilter.IsAllowed("teams", settings));
    }

    [Fact]
    public void MusicAndWebPreset_AllowsMusicAndBrowsers()
    {
        var settings = new IslandSettings
        {
            ReadOnly = true,
            MediaFilterPreset = MediaFilterPreset.MusicAndWeb
        };

        Assert.True(MediaFilter.IsAllowed("spotify", settings));
        Assert.True(MediaFilter.IsAllowed("chrome", settings));
        Assert.True(MediaFilter.IsAllowed("msedge", settings));

        // Calls still blocked
        Assert.False(MediaFilter.IsAllowed("discord", settings));
        Assert.False(MediaFilter.IsAllowed("teams", settings));
    }

    [Fact]
    public void AllPreset_AllowsEverythingExceptCalls()
    {
        var settings = new IslandSettings
        {
            ReadOnly = true,
            MediaFilterPreset = MediaFilterPreset.All
        };

        Assert.True(MediaFilter.IsAllowed("spotify", settings));
        Assert.True(MediaFilter.IsAllowed("chrome", settings));
        Assert.True(MediaFilter.IsAllowed("game_executable", settings));

        // Calls protected
        Assert.False(MediaFilter.IsAllowed("discord", settings));
    }

    [Fact]
    public void CustomPreset_FollowsUserOverrides()
    {
        var settings = new IslandSettings
        {
            ReadOnly = true,
            MediaFilterPreset = MediaFilterPreset.Custom
        };

        settings.MediaAppFilter["chrome"] = true; // explicitly enabled browser
        settings.MediaAppFilter["spotify"] = false; // explicitly disabled Spotify

        Assert.True(MediaFilter.IsAllowed("chrome", settings));
        Assert.False(MediaFilter.IsAllowed("spotify", settings));
        // Unconfigured defaults to music allowed, others blocked
        Assert.True(MediaFilter.IsAllowed("vlc", settings));
        Assert.False(MediaFilter.IsAllowed("discord", settings));
    }

    [Fact]
    public void ExplicitBlock_OverridesNonCustomPresets()
    {
        var settings = new IslandSettings
        {
            ReadOnly = true,
            MediaFilterPreset = MediaFilterPreset.MusicOnly
        };

        settings.MediaAppFilter["spotify"] = false;

        // Even though Spotify is music, explicit user block takes precedence
        Assert.False(MediaFilter.IsAllowed("spotify", settings));
        Assert.True(MediaFilter.IsAllowed("deezer", settings));
    }
}
