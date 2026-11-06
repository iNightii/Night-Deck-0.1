using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>Situação do voto de uma conta no TopIdle.</summary>
internal sealed record VoteState(bool LoggedIn, bool CanVote, DateTime? NextVoteAt, string? User)
{
    public bool Ready => LoggedIn && (CanVote || NextVoteAt is DateTime at && at <= DateTime.Now);
}

/// <summary>
/// Voto no TopIdle (https://topidle.com), o ranking de jogos idle; o pokeidle dá recompensa por voto
/// para o personagem informado. Quem vota é você, na página do TopIdle, aberta no navegador da conta
/// (é lá que fica o login do TopIdle dela). O deck só descobre quando o próximo voto libera e percebe
/// quando o voto foi registrado.
/// </summary>
internal static class TopIdle
{
    public const string Base = "https://topidle.com";

    // O mesmo formato que o TopIdle aceita como nome do personagem.
    private static readonly Regex PlayerPattern = new("^[A-Za-z0-9_-]{1,64}$");

    // Os cookies vão à mão, os da conta certa em cada pedido.
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>O id do jogo no TopIdle pelo domínio (pokeidle.io → pokeidle-io-5f9969).</summary>
    public static async Task<string?> FindGameIdAsync(string domain)
    {
        using var doc = await GetJsonAsync("/api/games", null);
        foreach (var game in Games(doc.RootElement))
        {
            var gameDomain = Text(game, "domain") ?? HostOf(Text(game, "url"));
            if (string.Equals(gameDomain?.Replace("www.", ""), domain, StringComparison.OrdinalIgnoreCase))
                return Text(game, "id");
        }
        return null;
    }

    /// <summary>Pergunta ao TopIdle, com o login do TopIdle salvo no perfil da conta, se ela já pode votar.</summary>
    public static async Task<VoteState> GetStateAsync(CoreWebView2CookieManager cookies, string gameId)
    {
        var list = await cookies.GetCookiesAsync(Base);
        var header = string.Join("; ", list.Select(c => $"{c.Name}={c.Value}"));
        if (header.Length == 0)
            return new VoteState(false, true, null, null);

        using var me = await GetJsonAsync("/api/me", header);
        if (!me.RootElement.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            return new VoteState(false, true, null, null);
        var name = Text(user, "name") ?? Text(user, "email");

        using var doc = await GetJsonAsync("/api/games", header);
        foreach (var game in Games(doc.RootElement).Where(g => Text(g, "id") == gameId))
        {
            bool canVote = !(game.TryGetProperty("canVote", out var can) && can.ValueKind == JsonValueKind.False);
            DateTime? next = game.TryGetProperty("nextVoteAt", out var at) && at.ValueKind == JsonValueKind.Number && at.GetDouble() > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(at.GetDouble() * 1000)).LocalDateTime
                : null;
            return new VoteState(true, canVote, next, name);
        }
        return new VoteState(true, true, null, name);
    }

    /// <summary>A página de voto do jogo, já com o personagem que recebe a recompensa.</summary>
    public static string VoteUrl(string gameId, string? player) =>
        $"{Base}/jogo/{Uri.EscapeDataString(gameId)}?votar=1"
        + (player is not null && PlayerPattern.IsMatch(player) ? $"&playerIdentifier={Uri.EscapeDataString(player)}" : "");

    /// <summary>A página do TopIdle registrou o voto (POST /api/games/{id}/vote deu certo).</summary>
    public static bool IsVoteRegistered(string method, string uri, int status, string gameId) =>
        method == "POST" && status is >= 200 and < 300
        && Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && IsTopIdleHost(parsed.Host)
        && parsed.AbsolutePath == $"/api/games/{Uri.EscapeDataString(gameId)}/vote";

    /// <summary>Entrou ou saiu do TopIdle: vale conferir o voto de novo.</summary>
    public static bool IsLoginChange(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && IsTopIdleHost(parsed.Host)
        && (parsed.AbsolutePath.StartsWith("/api/auth/") || parsed.AbsolutePath == "/api/logout");

    public static bool IsTopIdleLink(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == "https" && IsTopIdleHost(parsed.Host);

    /// <summary>O nick que o jogo guarda no localStorage ("nick"), a partir do resultado de ExecuteScriptAsync.</summary>
    public static string? ParseNick(string scriptResult)
    {
        try
        {
            var value = JsonSerializer.Deserialize<string?>(scriptResult);
            // O jogo pode guardar o nick já em JSON ("\"fulano\"").
            if (value is not null && value.StartsWith('"'))
                value = JsonSerializer.Deserialize<string?>(value);
            value = value?.Trim();
            return value is not null && PlayerPattern.IsMatch(value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTopIdleHost(string host) => host is "topidle.com" or "www.topidle.com";

    private static async Task<JsonDocument> GetJsonAsync(string path, string? cookieHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Base + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) PokeIdleDeck");
        if (cookieHeader is not null)
            request.Headers.Add("Cookie", cookieHeader);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static IEnumerable<JsonElement> Games(JsonElement root)
    {
        var list = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array ? games
            : default;
        return list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Host : null;
}