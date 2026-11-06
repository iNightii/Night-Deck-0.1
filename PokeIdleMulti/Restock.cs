using System.Globalization;
using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// Recompra de uma conta: a pokebola escolhida, os revives e as poções. Abaixo de "recomprar abaixo de", o
/// script da página (recompra.js) compra no Market do NPC até "manter", sem deixar o ouro abaixo do mínimo.
/// </summary>
internal sealed class RestockConfig
{
    public bool On { get; set; }

    /// <summary>A pokebola (Id do catálogo do jogo; 4 é a Ultra Ball).</summary>
    public RestockRule Balls { get; set; } = new() { On = true, Id = 4, Keep = 30000, Below = 5000 };

    /// <summary>Os revives, contados juntos; compra sempre o mais barato.</summary>
    public RestockRule Revives { get; set; } = new() { On = true, Keep = 500, Below = 100 };

    /// <summary>As poções, contadas juntas; Id 0 é o melhor custo-benefício no pokemon ativo, 204 a Ultimate Potion.</summary>
    public RestockRule Potions { get; set; } = new() { On = true, Id = Restock.UltimatePotion, Keep = 5000, Below = 2000 };

    /// <summary>Pôr a poção escolhida em 1º na fila da automação "Usar Poções" (a ordem dela é a ordem de gasto).</summary>
    public bool PotionFirst { get; set; } = true;

    /// <summary>Nunca comprar a ponto de o ouro ficar abaixo disto.</summary>
    public long GoldFloor { get; set; } = 1_000_000;

    /// <summary>Um deck.json editado à mão (ou de uma versão sem a recompra) não pode deixar regra vazia.</summary>
    public RestockConfig Normalized()
    {
        var fresh = new RestockConfig();
        Balls ??= fresh.Balls;
        Revives ??= fresh.Revives;
        Potions ??= fresh.Potions;
        foreach (var rule in new[] { Balls, Revives, Potions })
        {
            rule.Keep = Math.Clamp(rule.Keep, 0, Restock.MaxStock);
            rule.Below = Math.Clamp(rule.Below, 0, Restock.MaxStock);
        }
        GoldFloor = Math.Max(0, GoldFloor);
        return this;
    }
}

internal sealed class RestockRule
{
    public bool On { get; set; }
    public int Id { get; set; }
    public long Keep { get; set; }
    public long Below { get; set; }
}

/// <summary>O que a página conta: estoque, ouro, a poção escolhida e o registro das compras.</summary>
internal sealed record RestockStatus(
    bool Configured, bool On, bool Connected, string? Nick, double Gold,
    List<RestockOffer>? Balls, List<RestockOffer>? Revives, List<RestockOffer>? Potions,
    int? PotionId, double? MaxHp, double? Threshold, int? QueueFirst, bool Paused, List<string>? Log)
{
    public double ReviveHave => Revives?.Sum(r => r.Have) ?? 0;
    public double PotionHave => Potions?.Sum(p => p.Have) ?? 0;
}

/// <summary>Uma bola, poção ou revive do balcão; <see cref="Cost"/> é o ouro por HP útil (só nas poções).</summary>
internal sealed record RestockOffer(int Id, string? Name, double Price, double Have, double Heal, double? Cost);

internal static class Restock
{
    public const int UltimatePotion = 204;

    /// <summary>O teto dos campos de quantidade.</summary>
    public const long MaxStock = 10_000_000;

    private static readonly CultureInfo Br = new("pt-BR");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>O script que roda em toda página do jogo (embutido no exe).</summary>
    public static string Script { get; } = ReadScript();

    /// <summary>Antes de a página contar o que tem: os nomes do catálogo do jogo, para a janela já abrir preenchida.</summary>
    public static readonly RestockOffer[] KnownBalls =
    {
        new(1, "Poke Ball", 0, 0, 0, null), new(2, "Great Ball", 0, 0, 0, null),
        new(3, "Super Ball", 0, 0, 0, null), new(4, "Ultra Ball", 0, 0, 0, null),
    };

    public static readonly RestockOffer[] KnownPotions =
    {
        new(200, "Small Potion", 50, 0, 60, null), new(201, "Great Potion", 150, 0, 150, null),
        new(202, "Ultra Potion", 400, 0, 400, null), new(203, "Hyper Potion", 800, 0, 1000, null),
        new(UltimatePotion, "Ultimate Potion", 1500, 0, 3000, null),
    };

    public static string ConfigureScript(RestockConfig config) =>
        $"window.__deckRecompra ? window.__deckRecompra.configure({JsonSerializer.Serialize(config, Json)}) : false";

    public const string SummaryScript = "window.__deckRecompra ? window.__deckRecompra.summary() : null";

    public static RestockStatus? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RestockStatus>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string N(double value) => Math.Round(value).ToString("N0", Br);

    /// <summary>Ouro por HP com duas casas ("0,80").</summary>
    public static string Cost(double value) => value.ToString("0.00", Br);

    private static string ReadScript()
    {
        using var stream = typeof(Restock).Assembly.GetManifestResourceStream("recompra.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}