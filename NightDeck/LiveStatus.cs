using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NightDeck;

// Adapted from TwitchLive.cs and KickLive.cs in PokeIdleMulti 2026.11.05.2.
// Unknown status is NOT treated as live, to avoid opening offline channels.
internal static class LiveStatus
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private const string TwitchClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    public static async Task<HashSet<string>> TwitchOnlineAsync(string[] channels)
    {
        var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (channels.Length == 0) return online;
        var requestBody = JsonSerializer.Serialize(new
        {
            query = "query($l:[String!]){users(logins:$l){login stream{id}}}",
            variables = new { l = channels }
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql");
        req.Headers.TryAddWithoutValidation("Client-Id", TwitchClientId);
        req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(req);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
        {
            foreach (var user in users.EnumerateArray())
            {
                if (user.ValueKind != JsonValueKind.Object) continue;
                if (user.TryGetProperty("login", out var login) && user.TryGetProperty("stream", out var stream)
                    && stream.ValueKind == JsonValueKind.Object)
                {
                    var name = login.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) online.Add(name);
                }
            }
        }
        return online;
    }

    public static async Task<HashSet<string>> KickOnlineAsync(string[] channels)
    {
        var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://kick.com/api/v2/channels/" + Uri.EscapeDataString(channel));
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var response = await Http.SendAsync(req);
                if (!response.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("livestream", out var live) &&
                    live.ValueKind == JsonValueKind.Object &&
                    (!live.TryGetProperty("is_live", out var status) || status.ValueKind != JsonValueKind.False))
                    online.Add(channel);
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            catch (JsonException) { }
        }
        return online;
    }
}
