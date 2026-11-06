using System.Text.Json;
using System.Text.Json.Serialization;

namespace PokeIdleMulti;

internal enum DeckLayout { Grid, Columns, Focus }

/// <summary>O bônus do PokeIdle que o deck resgata com os pontos de cada canal da Kick ou da Twitch (1 h cada, as horas acumulam).</summary>
internal enum KickReward { Xp, Capture, Lure }

/// <summary>Preferências do deck salvas entre uma execução e outra.</summary>
internal sealed class DeckConfig
{
    private const int Accounts = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string[] AccountNames { get; set; } = { "Conta 1", "Conta 2", "Conta 3", "Conta 4" };

    public DeckLayout Layout { get; set; } = DeckLayout.Grid;

    /// <summary>A conta ampliada no layout de foco.</summary>
    public int Focused { get; set; }

    /// <summary>Zoom da página de cada conta (Ctrl + roda do mouse).</summary>
    public double[] Zoom { get; set; } = { 1, 1, 1, 1 };

    /// <summary>
    /// O zoom da grade e das colunas, guardado ao ampliar uma conta (o foco põe tudo em 100%); voltar do foco
    /// devolve este. Nulo enquanto não entrou no foco.
    /// </summary>
    public double? ZoomOutsideFocus { get; set; }

    public bool[] Muted { get; set; } = new bool[Accounts];

    /// <summary>Contas desligadas no botão ⏻: ficam no deck, mas não abrem o jogo (nem extensões, lives e voto).</summary>
    public bool[] Off { get; set; } = new bool[Accounts];

    /// <summary>
    /// Menos animações no jogo (o "reduzir movimento" do sistema, que o próprio jogo respeita). Gasta bem
    /// menos CPU e placa de vídeo com várias contas à vista.
    /// </summary>
    public bool ReduceMotion { get; set; } = true;

    /// <summary>
    /// O Modo ULTRA Economia do jogo em todas as contas (o jogo inteiro fora da tela, só uma caixa de números).
    /// Ligado, a conta que recarrega ou é ligada volta a ele sozinha.
    /// </summary>
    public bool Ultra { get; set; }

    /// <summary>Posição e tamanho da janela quando não está maximizada: x, y, largura, altura.</summary>
    public int[]? WindowBounds { get; set; }

    public bool Maximized { get; set; } = true;

    /// <summary>
    /// Contas (1 a 4) que mantêm as lives abertas. O bônus só vale para a conta do jogo vinculada
    /// à Twitch/Kick logada nas lives dela, então normalmente é só a main.
    /// </summary>
    public List<int> LivesAccounts { get; set; } = new() { 1 };

    /// <summary>
    /// Streamers oficiais na Twitch (a lista do painel de bônus do jogo), em ordem de prioridade. Desde 04/11/2026 o
    /// bônus da Twitch sai dos pontos do canal, que só vêm com a live tocando (estar no chat não conta mais): o
    /// deck abre o player das primeiras que estiverem no ar, até <see cref="TwitchLives"/> (<see cref="TwitchLive"/>).
    /// </summary>
    public List<string> TwitchChannels { get; set; } = new()
    {
        "tftoddy", "clovaobengala", "axtlol", "patopapao", "matta", "guaxilol1", "rodil",
        "cogu", "luccatv", "faasii", "biscoitao", "techzz", "dersin2k", "denissnidertv",
    };

    /// <summary>Quantas lives da Twitch abrir em cada conta (Lives → Config…). De fábrica 2, como na Kick.</summary>
    public int TwitchLives { get; set; } = TwitchLive.DefaultSlots;

    /// <summary>
    /// O bônus que cada conta resgata sozinha com os pontos das lives da Twitch abertas nela (Lives → Config…);
    /// null: a conta não resgata.
    /// </summary>
    public KickReward?[] RedeemTwitch { get; set; } = new KickReward?[Accounts];

    /// <summary>Pegar sozinho o baú de bônus da Twitch (+50 a cada ~15 min, que só vem para quem clica).</summary>
    public bool ClaimTwitchBonus { get; set; } = true;

    /// <summary>
    /// Streamers oficiais na Kick, em ordem de prioridade. Abre a live inteira, que é o que rende pontos
    /// do canal, mas só das primeiras que estiverem no ar, até <see cref="KickLives"/> (<see cref="KickLive"/>).
    /// </summary>
    public List<string> KickChannels { get; set; } = new() { "biscoitao", "yetz", "surskity" };

    /// <summary>Quantas lives da Kick abrir em cada conta (Lives → Config…). De fábrica 2, as que a Kick conta para os pontos.</summary>
    public int KickLives { get; set; } = KickLive.DefaultSlots;

    /// <summary>Altura da janela dos pontos da Kick, em pixels a 100% de escala (muda arrastando a borda dela).</summary>
    public int KickPointsHeight { get; set; } = 640;

    /// <summary>
    /// O bônus que cada conta resgata sozinha com os pontos das lives da Kick abertas nela (Lives → Config…);
    /// null: a conta não resgata.
    /// </summary>
    public KickReward?[] RedeemKick { get; set; } = new KickReward?[Accounts];

    /// <summary>Da versão em que o resgate valia para todas as contas: lidos uma vez e convertidos em <see cref="RedeemKick"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? KickRedeem { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KickReward? KickReward { get; set; }

    /// <summary>O que o botão kick fechou em cada conta.</summary>
    public LiveLimit[] Kick { get; set; } = NewLimits();

    /// <summary>O que o botão twitch fechou em cada conta.</summary>
    public LiveLimit[] Twitch { get; set; } = NewLimits();

    /// <summary>O que os botões kick e twitch fecharam em cada conta, por site.</summary>
    public LiveLimit[] Limits(LiveSite site) => site == LiveSite.Kick ? Kick : Twitch;

    /// <summary>Quantas lives de <paramref name="site"/> abrir em cada conta.</summary>
    public int Lives(LiveSite site) => site == LiveSite.Kick ? KickLives : TwitchLives;

    /// <summary>Os canais de <paramref name="site"/>, em ordem de prioridade.</summary>
    public List<string> Channels(LiveSite site) => site == LiveSite.Kick ? KickChannels : TwitchChannels;

    /// <summary>A recompra de pokebolas, revives e poções de cada conta (o carrinho no cabeçalho dela).</summary>
    public RestockConfig[] Restock { get; set; } = Enumerable.Range(0, Accounts).Select(_ => new RestockConfig()).ToArray();

    /// <summary>Da versão em que o kick só fechava a 2ª live: lido uma vez e convertido em <see cref="Kick"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool[]? KickSingle { get; set; }

    public static DeckConfig Load(string path)
    {
        DeckConfig config;
        try
        {
            config = JsonSerializer.Deserialize<DeckConfig>(File.ReadAllText(path), JsonOptions) ?? new DeckConfig();
        }
        catch
        {
            config = new DeckConfig();
        }

        // Um arquivo editado à mão não pode deixar listas mais curtas que as 4 contas.
        config.AccountNames = Fill(config.AccountNames, i => $"Conta {i + 1}");
        config.Zoom = Fill(config.Zoom, _ => 1.0);
        config.Muted = Fill(config.Muted, _ => false);
        config.Off = Fill(config.Off, _ => false);
        config.Kick = Fill(config.Kick, _ => new LiveLimit())
            .Select(k => k is { Closed: not null } ? k : new LiveLimit()).ToArray();
        config.KickLives = Math.Clamp(config.KickLives, 0, KickLive.MaxSlots);
        config.TwitchLives = Math.Clamp(config.TwitchLives, 0, TwitchLive.MaxSlots);
        for (int i = 0; i < Accounts && config.KickSingle is not null; i++)
            if (i < config.KickSingle.Length && config.KickSingle[i])
                config.Kick[i].Slots = Math.Min(config.Kick[i].Slots ?? config.KickLives, 1);
        config.KickSingle = null;
        foreach (var kick in config.Kick)
            kick.Fit(config.KickLives);
        config.Twitch = Fill(config.Twitch, _ => new LiveLimit())
            .Select(t => t is { Closed: not null } ? t : new LiveLimit()).ToArray();
        foreach (var twitch in config.Twitch)
            twitch.Fit(config.TwitchLives);
        config.Restock = Fill(config.Restock, _ => new RestockConfig()).Select(r => (r ?? new RestockConfig()).Normalized()).ToArray();
        config.Focused = Math.Clamp(config.Focused, 0, Accounts - 1);
        config.KickPointsHeight = Math.Clamp(config.KickPointsHeight, 300, 3000);
        config.RedeemKick = Fill(config.RedeemKick, _ => null);
        if (config.KickRedeem == true)
            config.RedeemKick = config.RedeemKick.Select(r => (PokeIdleMulti.KickReward?)(r ?? config.KickReward ?? PokeIdleMulti.KickReward.Xp)).ToArray();
        config.KickRedeem = null;
        config.KickReward = null;
        // Um número fora da lista (deck.json editado à mão) vira o XP, sem desligar o resgate da conta.
        config.RedeemKick = config.RedeemKick.Select(r => r is KickReward reward && !Enum.IsDefined(reward) ? PokeIdleMulti.KickReward.Xp : r).ToArray();
        config.RedeemTwitch = Fill(config.RedeemTwitch, _ => null)
            .Select(r => r is KickReward reward && !Enum.IsDefined(reward) ? PokeIdleMulti.KickReward.Xp : r).ToArray();
        return config;
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Não conseguir salvar a preferência não pode derrubar o deck.
        }
    }

    private static LiveLimit[] NewLimits() => Enumerable.Range(0, Accounts).Select(_ => new LiveLimit()).ToArray();

    private static T[] Fill<T>(T[]? values, Func<int, T> fallback) =>
        Enumerable.Range(0, Accounts).Select(i => values is not null && i < values.Length ? values[i] : fallback(i)).ToArray();
}

/// <summary>As lives de um site (Kick ou Twitch) que o botão dele (kick ou twitch) fechou numa conta.</summary>
internal sealed class LiveLimit
{
    /// <summary>
    /// Quantas lives do site abrem nesta conta; null é o limite do deck (<see cref="DeckConfig.KickLives"/>,
    /// <see cref="DeckConfig.TwitchLives"/>). Cada live fechada leva a vaga dela junto, para a conferência de 2 em 2
    /// min não abrir outra no lugar; 0 é "fechar todas".
    /// </summary>
    public int? Slots { get; set; }

    /// <summary>Os canais fechados: não abrem nesta conta até abrir de novo no botão.</summary>
    public List<string> Closed { get; set; } = new();

    /// <summary>Alguma live fechada no botão (ele fica marcado).</summary>
    [JsonIgnore]
    public bool Limited => Slots is not null || Closed.Count > 0;

    /// <summary>As vagas desta conta com o limite do deck em <paramref name="max"/>.</summary>
    public int SlotsOf(int max) => Math.Clamp(Slots ?? max, 0, max);

    /// <summary>
    /// Acerta as vagas a um limite novo: chegar ao limite é ter todas (null), e um deck.json de antes (com o 2
    /// fixo escrito em cada conta) também cai aqui.
    /// </summary>
    public void Fit(int max)
    {
        if (Slots is int slots && slots >= max)
            Slots = null;
        else if (Slots < 0)
            Slots = 0;
    }
}