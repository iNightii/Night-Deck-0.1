using System.Text;
using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// Quem está ao vivo na Twitch, pela mesma API que o site usa (gql.twitch.tv, com o Client-Id público do site),
/// todos os canais numa pergunta só. Como na Kick, o deck abre só as primeiras da lista que estão no ar, até o
/// limite de Lives → Config… (<see cref="KickLive.Pick"/>), e live offline não fica aberta gastando.
/// </summary>
internal static class TwitchLive
{
    /// <summary>Quantas lives da Twitch abrir em cada conta, de fábrica (como na Kick).</summary>
    public const int DefaultSlots = 2;

    /// <summary>O teto do campo.</summary>
    public const int MaxSlots = 10;

    // O Client-Id do próprio site da Twitch (está no código da página, é público).
    private const string ClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// O player da live sozinho (o mesmo do "player em janela" do site). Ele entra com o login da Twitch do perfil
    /// (o cookie auth-token) e manda à Twitch o mesmo aviso de "assistindo" (minute-watched) que a página do
    /// canal, sem carregar o chat nem o resto do site.
    /// </summary>
    public static string PlayerUrl(string channel) =>
        $"https://player.twitch.tv/?channel={Uri.EscapeDataString(channel)}&parent=twitch.tv&player=popout";

    /// <summary>Cada canal: true = no ar, false = offline ou não existe, null = não deu para saber agora.</summary>
    public static async Task<Dictionary<string, bool?>> LiveAsync(IReadOnlyList<string> channels)
    {
        if (channels.Count == 0)
            return new();
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                query = "query($l:[String!]){users(logins:$l){login stream{id}}}",
                variables = new { l = channels },
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Client-Id", ClientId);
            using var response = await Http.SendAsync(request);
            if (response.IsSuccessStatusCode)
                return ParseLive(channels, await response.Content.ReadAsStringAsync());
        }
        catch
        {
            // Sem internet ou a Twitch não respondeu: fica tudo como "não deu para saber".
        }
        return channels.ToDictionary(c => c, _ => (bool?)null);
    }

    /// <summary>
    /// A resposta: "users" vem na ordem dos canais pedidos, null para o canal que não existe, e "stream" é null
    /// com o canal offline.
    /// </summary>
    public static Dictionary<string, bool?> ParseLive(IReadOnlyList<string> channels, string json)
    {
        var result = channels.ToDictionary(c => c, _ => (bool?)null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("users", out var users)
                || users.ValueKind != JsonValueKind.Array)
                return result;
            var found = new Dictionary<string, bool>();
            foreach (var user in users.EnumerateArray())
                if (user.ValueKind == JsonValueKind.Object && user.TryGetProperty("login", out var login) && login.GetString() is string name)
                    found[name.ToLowerInvariant()] = user.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.Object;
            foreach (var channel in channels)
                result[channel] = found.GetValueOrDefault(channel.ToLowerInvariant());
        }
        catch (JsonException)
        {
        }
        return result;
    }
}