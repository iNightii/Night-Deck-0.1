using System.Net;
using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// Quem está ao vivo na Kick, pela mesma API que o site usa (kick.com/api/v2/channels/{canal}). O deck
/// abre só as primeiras da lista que estão no ar, até o limite escolhido em Lives → Config… (2 de fábrica,
/// as que a Kick conta para os pontos); live offline não fica aberta gastando.
/// </summary>
internal static class KickLive
{
    /// <summary>Quantas lives da Kick abrir em cada conta, de fábrica: as que rendem pontos ao mesmo tempo.</summary>
    public const int DefaultSlots = 2;

    /// <summary>O teto do campo (cada live custa uns 260 MB de RAM).</summary>
    public const int MaxSlots = 10;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>true = no ar, false = offline, null = não deu para saber agora.</summary>
    public static async Task<bool?> IsLiveAsync(string channel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel)}");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return false;
            return response.IsSuccessStatusCode ? ParseLive(await response.Content.ReadAsStringAsync()) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A resposta da API: "livestream" é null com o canal offline.</summary>
    public static bool? ParseLive(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("livestream", out var live))
                return null;
            return live.ValueKind == JsonValueKind.Object
                && !(live.TryGetProperty("is_live", out var isLive) && isLive.ValueKind == JsonValueKind.False);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// As lives para abrir: as primeiras da lista que estão no ar, até <paramref name="slots"/> (a primeira da
    /// lista tem prioridade). Canal que não deu para conferir conta como no ar, para não perder pontos.
    /// </summary>
    public static List<string> Pick(IEnumerable<string> channels, IReadOnlyDictionary<string, bool?> live, int slots) =>
        channels.Where(c => live.GetValueOrDefault(c) != false).Take(slots).ToList();
}