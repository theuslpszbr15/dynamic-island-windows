using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace DynamicIsland.Services;

public sealed record UpdateInfo(Version Version, string Url, string? Sha256, string Notes);

/// <summary>Looks for a newer GitHub release and downloads its single-file exe (checked against the release digest).</summary>
public static class Updater
{
    const string LatestApi = "https://api.github.com/repos/theuslpszbr15/dynamic-island-windows/releases/latest";
    const string AssetName = "DynamicIsland.exe";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DynamicIsland/{Current}");
        return http;
    }

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
        if (latest <= Current) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (url == null || !url.StartsWith("https://github.com/", StringComparison.Ordinal)) return null;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var sha = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : null;
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new UpdateInfo(latest, url, sha, notes);
        }
        return null;
    }

    /// <summary>Downloads to %TEMP%; throws if the file doesn't match the published SHA-256.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info)
    {
        var path = Path.Combine(Path.GetTempPath(), $"DynamicIsland-{info.Version}.exe");
        var bytes = await Http.GetByteArrayAsync(info.Url);
        if (info.Sha256 != null)
        {
            var actual = Convert.ToHexString(SHA256.HashData(bytes));
            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O arquivo baixado não confere com o da release.");
        }
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}
