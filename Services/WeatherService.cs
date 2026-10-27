using System.Net.Http;
using System.Text.Json;

namespace DynamicIsland.Services;

public sealed record WeatherNow(string Place, int TempC, int FeelsC, int Humidity, int WindKmh, string Description, int Code,
    int MinC, int MaxC)
{
    public bool Sunny => Code == 113;
    public bool Rainy => Code is >= 176 and not (248 or 260);
}

/// <summary>Current weather for the IP's city from wttr.in (free, no key).</summary>
public static class WeatherService
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland");
        return http;
    }

    public static async Task<WeatherNow?> GetAsync()
    {
        var text = await Http.GetStringAsync("https://wttr.in/?format=j1&lang=pt");
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        var now = root.GetProperty("current_condition")[0];

        static int Int(JsonElement e, string name) => int.TryParse(e.GetProperty(name).GetString(), out var v) ? v : 0;
        static string First(JsonElement e, string name) =>
            e.TryGetProperty(name, out var list) && list.GetArrayLength() > 0 ? list[0].GetProperty("value").GetString() ?? "" : "";

        var description = First(now, "lang_pt");
        if (description.Length == 0) description = First(now, "weatherDesc");
        var place = root.TryGetProperty("nearest_area", out var areas) && areas.GetArrayLength() > 0 ? First(areas[0], "areaName") : "";
        var today = root.GetProperty("weather")[0];

        return new WeatherNow(place, Int(now, "temp_C"), Int(now, "FeelsLikeC"), Int(now, "humidity"), Int(now, "windspeedKmph"),
            description.Trim(), Int(now, "weatherCode"), Int(today, "mintempC"), Int(today, "maxtempC"));
    }
}
