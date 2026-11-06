using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// O resgate automático do bônus do PokeIdle com os pontos de cada canal de um site (Kick ou Twitch), numa conta.
/// Usa os mesmos pedidos que o site faz ao clicar em Resgatar, com o login da conta, de dentro do player de cada live
/// aberta (que já usa esse login). A cada conferência, no máximo 1 resgate por canal. Sem o site vinculado no jogo o
/// PokeIdle devolve os pontos: o deck vê o saldo voltar e para de resgatar nesta conta. Na Twitch também pega o baú
/// de bônus (+50 a cada ~15 min), que só vem para quem clica.
/// </summary>
internal sealed class PointsRedeemer
{
    /// <summary>De quanto em quanto tempo confere o saldo na Kick (assistindo, ela dá 10 pontos a cada 5 min).</summary>
    public static readonly TimeSpan KickInterval = TimeSpan.FromMinutes(5);

    /// <summary>Na Twitch, mais seguido: o baú de bônus aparece a cada ~15 min e espera o clique.</summary>
    public static readonly TimeSpan TwitchInterval = TimeSpan.FromMinutes(2);

    // Resgate (ou baú) recusado: espera antes de tentar de novo naquele canal.
    private static readonly TimeSpan RetryWait = TimeSpan.FromMinutes(30);

    // Saldo que sobe até esse tempo depois de um resgate, bem mais do que assistir dá, é o bônus devolvido.
    private static readonly TimeSpan RefundWindow = TimeSpan.FromMinutes(30);

    // O Client-Id do próprio site da Twitch, o mesmo do player.
    internal const string TwitchClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    /// <summary>
    /// O começo dos scripts da Twitch (dentro de "async a => {"): gql(query, variables) chama a API do site com o login
    /// da conta (a.token, a.device, a.clientId, a.api); quando a Twitch desafia o pedido com a checagem anti-robô, pede o
    /// token dela de dentro da página e manda de novo. fail(resposta, resultado) diz o que deu errado.
    /// </summary>
    internal const string TwitchGql =
        " const base = { 'Client-Id': a.clientId, Authorization: 'OAuth ' + a.token, 'Content-Type': 'text/plain;charset=UTF-8'," +
        "  ...(a.device ? { 'X-Device-Id': a.device } : {}) };" +
        " let integrity = null;" +
        " const gql = async (query, variables) => {" +
        "  for (let i = 0; i < 2; i++) {" +
        "   const r = await fetch(a.api + '/gql', { method: 'POST', cache: 'no-cache'," +
        "    headers: integrity ? { ...base, 'Client-Integrity': integrity } : base, body: JSON.stringify({ query, variables }) });" +
        "   if (!r.ok) return { failed: 'erro ' + r.status };" +
        "   const j = await r.json();" +
        "   const challenged = !!j?.extensions?.challenge || (j?.errors || []).some(e => e?.extensions?.code === 'IntegrityCheckFailed');" +
        "   if (!challenged) return j;" +
        "   if (integrity) return { failed: 'checagem anti-robô da Twitch' };" +
        "   const t = await fetch(a.api + '/integrity', { method: 'POST', cache: 'no-cache', headers: base });" +
        "   const tj = t.ok ? await t.json() : null;" +
        "   if (!tj?.token) return { failed: 'checagem anti-robô da Twitch (' + t.status + ')' };" +
        "   integrity = tj.token;" +
        "  }" +
        " };" +
        " const fail = (j, x) => j?.failed || x?.error?.code || j?.errors?.[0]?.message || 'sem resposta';";

    private readonly Dictionary<string, Channel> _channels = new();

    public PointsRedeemer(LiveSite site) => Site = site;

    public LiveSite Site { get; }

    private sealed class Channel
    {
        /// <summary>O saldo na última conferência, já sem o que foi resgatado nela.</summary>
        public int? Points { get; set; }
        public int? Cost { get; set; }
        public DateTime? RedeemedAt { get; set; }
        public DateTime RetryAt { get; set; }

        /// <summary>O baú de bônus da Twitch: quando pegou o último, e o que deu errado no último que tentou.</summary>
        public DateTime? ClaimedAt { get; set; }
        public string? ClaimError { get; set; }
        public DateTime ClaimRetryAt { get; set; }

        public string Text { get; set; } = "";
    }

    /// <summary>Por que parou de resgatar nesta conta (null: resgatando).</summary>
    public string? Stopped { get; private set; }

    /// <summary>Sem o login do site no perfil da conta (as lives tocam, mas não dá para resgatar).</summary>
    public bool NoLogin { get; set; }

    /// <summary>Para o registro dos resgates: cada resgate, baú, recusa e pontos que voltaram ("canal: o que houve").</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Volta a resgatar do zero (ao salvar Lives → Config…, depois de vincular o site no jogo).</summary>
    public void Restart()
    {
        Stopped = null;
        NoLogin = false;
        _channels.Clear();
    }

    /// <summary>O saldo do canal e o último resgate, para o menu Lives ("230/300 pontos, resgatou 14:05").</summary>
    public string Text(string channel) => _channels.TryGetValue(channel, out var c) ? c.Text : "";

    // A partir desse saldo os pontos do último resgate voltaram (metade do custo: assistir dá 10 a cada 5 min, e o
    // baú da Twitch, 50 a cada ~15 min).
    private static int? RefundAt(Channel c) =>
        c.Points is int points && c.Cost is int cost && c.RedeemedAt is DateTime at && DateTime.Now - at < RefundWindow
            ? points + cost / 2
            : null;

    /// <summary>Se este canal pode gastar agora, e a partir de que saldo é devolução (não resgata em cima dela).</summary>
    private (bool Spend, int? Refund) Plan(string channel)
    {
        _channels.TryGetValue(channel, out var c);
        return (Stopped is null && (c is null || DateTime.Now >= c.RetryAt), c is null ? null : RefundAt(c));
    }

    // O nome do bônus em cada canal: "Bônus +15% XP PokeIdle", "Bônus de +15% Capture Boost", "… Secret Lure".
    internal static string Word(KickReward reward) => reward switch
    {
        KickReward.Capture => "captur",
        KickReward.Lure => "lure|shiny",
        _ => "\\bxp\\b",
    };

    /// <summary>
    /// O script que roda no player da live de <paramref name="channel"/> da Kick: lê o saldo e o bônus escolhido e, se
    /// der e estiver liberado, resgata 1. Devolve (quando a promessa acaba) o JSON que <see cref="Apply"/> lê.
    /// </summary>
    public string KickScript(string api, string channel, string token, KickReward reward)
    {
        var (spend, refund) = Plan(channel);
        var args = JsonSerializer.Serialize(new { api, channel, token, word = Word(reward), spend, refund });
        return "(async a => {" +
               " const call = (path, init) => fetch(a.api + '/api/v2/channels/' + encodeURIComponent(a.channel) + path," +
               "  { cache: 'no-cache', credentials: 'include', ...init, headers: { Accept: 'application/json', 'x-app-platform': 'web'," +
               "   Authorization: 'Bearer ' + a.token, ...(init && init.body ? { 'Content-Type': 'application/json' } : {}) } });" +
               " try {" +
               "  let r = await call('/points');" +
               "  if (!r.ok) return { error: 'saldo: erro ' + r.status };" +
               "  const points = (await r.json())?.data?.points;" +
               "  if (typeof points !== 'number') return { error: 'saldo: sem os pontos na resposta' };" +
               "  r = await call('/rewards');" +
               "  if (!r.ok) return { points, error: 'bônus: erro ' + r.status };" +
               "  const word = new RegExp(a.word, 'i');" +
               "  const reward = ((await r.json())?.data || []).find(x => x.is_enabled && word.test(x.title || '')" +
               "   && /pokeidle/i.test((x.title || '') + ' ' + (x.description || '')));" +
               "  if (!reward) return { points, error: 'canal sem o bônus' };" +
               "  const out = { points, cost: reward.cost, title: reward.title, paused: !!reward.is_paused };" +
               "  if (!a.spend || reward.is_paused || points < reward.cost || (a.refund !== null && points >= a.refund)) return out;" +
               // O transaction_id é um ULID novo, como o site gera: o tempo em base 32 e mais 16 letras ao acaso.
               "  const abc = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'; let t = Date.now(), id = '';" +
               "  for (let i = 0; i < 10; i++) { id = abc[t % 32] + id; t = Math.floor(t / 32); }" +
               "  for (let i = 0; i < 16; i++) id += abc[Math.floor(Math.random() * 32)];" +
               "  r = await call('/rewards/' + reward.id + '/redeem', { method: 'POST', body: JSON.stringify({ transaction_id: id, message: '' }) });" +
               "  return { ...out, redeemed: r.ok, status: r.status };" +
               " } catch (e) { return { error: 'sem resposta da Kick: ' + (e && e.message || e) }; }" +
               $"}})({args}).then(o => JSON.stringify(o))";
    }

    /// <summary>
    /// O script que roda no player da live de <paramref name="channel"/> da Twitch, pela API do site (gql.twitch.tv),
    /// com o login da conta (<paramref name="token"/>, o cookie auth-token) e o aparelho dela (<paramref name="device"/>,
    /// o cookie unique_id): lê o saldo, pega o baú de bônus se tiver um (<paramref name="claim"/>) e, com
    /// <paramref name="reward"/>, resgata 1 bônus se der. Quando a Twitch desafia o pedido com a checagem anti-robô
    /// (como no baú), pede o token dela de dentro do player, como o próprio site faz (o Kasada do player entra no
    /// pedido), e manda de novo.
    /// </summary>
    public string TwitchScript(string api, string channel, string token, string? device, KickReward? reward, bool claim)
    {
        var (spend, refund) = Plan(channel);
        _channels.TryGetValue(channel, out var c);
        claim = claim && (c is null || DateTime.Now >= c.ClaimRetryAt);
        var args = JsonSerializer.Serialize(new
        {
            api, channel, token, device, clientId = TwitchClientId, word = reward is KickReward r ? Word(r) : null, spend, refund, claim,
        });
        return "(async a => {" + TwitchGql +
               " try {" +
               "  const j = await gql('query($login:String!){user(login:$login){id channel{self{communityPoints{balance availableClaim{id}}}' +" +
               "   ' communityPointsSettings{customRewards{id title prompt cost isEnabled isPaused isInStock cooldownExpiresAt}}}}}', { login: a.channel });" +
               "  if (j?.failed) return { error: 'saldo: ' + j.failed };" +
               "  const user = j?.data?.user;" +
               "  if (!user?.channel) return { error: 'canal não encontrado' };" +
               "  const cp = user.channel.self?.communityPoints;" +
               "  if (typeof cp?.balance !== 'number') return { error: 'saldo: sem os pontos na resposta (login da Twitch?)' };" +
               "  let points = cp.balance; const out = { points };" +
               "  if (a.claim && cp.availableClaim?.id) {" +
               "   const k = await gql('mutation($i:ClaimCommunityPointsInput!){claimCommunityPoints(input:$i){claim{id} currentPoints error{code}}}'," +
               "    { i: { channelID: user.id, claimID: cp.availableClaim.id } });" +
               "   const p = k?.data?.claimCommunityPoints;" +
               "   if (p && !p.error && !k.errors) { out.claimed = true; points = typeof p.currentPoints === 'number' ? p.currentPoints : points + 50; }" +
               "   else out.claimError = fail(k, p);" +
               "   out.points = points;" +
               "  }" +
               "  if (!a.word) return out;" +
               "  const word = new RegExp(a.word, 'i');" +
               "  const reward = (user.channel.communityPointsSettings?.customRewards || []).find(x => x.isEnabled && word.test(x.title || '')" +
               "   && /pokeidle/i.test((x.title || '') + ' ' + (x.prompt || '')));" +
               "  if (!reward) return { ...out, error: 'canal sem o bônus' };" +
               "  Object.assign(out, { cost: reward.cost, title: reward.title, paused: reward.isPaused || !reward.isInStock || !!reward.cooldownExpiresAt });" +
               "  if (!a.spend || out.paused || points < reward.cost || (a.refund !== null && points >= a.refund)) return out;" +
               // O transactionID é um id novo a cada resgate (32 letras hexadecimais). O custo, o título e a descrição
               // (prompt) vão iguais aos do bônus no canal: a Twitch confere e, se não baterem, recusa (PROPERTIES_MISMATCH).
               "  const id = [...crypto.getRandomValues(new Uint8Array(16))].map(b => b.toString(16).padStart(2, '0')).join('');" +
               "  const r = await gql('mutation($i:RedeemCommunityPointsCustomRewardInput!){redeemCommunityPointsCustomReward(input:$i){redemption{id} error{code}}}'," +
               "   { i: { channelID: user.id, cost: reward.cost, prompt: reward.prompt ?? null, rewardID: reward.id, title: reward.title, transactionID: id } });" +
               "  const p = r?.data?.redeemCommunityPointsCustomReward;" +
               "  if (p && !p.error && !r.errors) return { ...out, redeemed: true };" +
               "  return { ...out, refused: fail(r, p) };" +
               " } catch (e) { return { error: 'sem resposta da Twitch: ' + (e && e.message || e) }; }" +
               $"}})({args}).then(o => JSON.stringify(o))";
    }

    /// <summary>
    /// Anota o que o script de um canal devolveu. Devolve o aviso para o cabeçalho da conta quando resgatou, quando
    /// o site recusou ou quando parou por causa dos pontos que voltaram.
    /// </summary>
    public string? Apply(string channel, string json)
    {
        if (!_channels.TryGetValue(channel, out var c))
            _channels[channel] = c = new Channel();
        int? points = null, cost = null, status = null;
        string? error = null, title = null, refused = null, claimError = null;
        bool redeemed = false, paused = false, claimed = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            points = root.TryGetProperty("points", out var p) && p.TryGetInt32(out int pv) ? pv : null;
            cost = root.TryGetProperty("cost", out var k) && k.TryGetInt32(out int kv) ? kv : null;
            status = root.TryGetProperty("status", out var s) && s.TryGetInt32(out int sv) ? sv : null;
            error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            refused = root.TryGetProperty("refused", out var f) ? f.GetString() : null;
            claimError = root.TryGetProperty("claimError", out var ce) ? ce.GetString() : null;
            redeemed = root.TryGetProperty("redeemed", out var r) && r.ValueKind == JsonValueKind.True;
            paused = root.TryGetProperty("paused", out var z) && z.ValueKind == JsonValueKind.True;
            claimed = root.TryGetProperty("claimed", out var b) && b.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            error = "a live não respondeu";
        }

        // O baú (só na Twitch): pegou, ou deu errado e espera 30 min para tentar de novo naquele canal.
        if (claimed)
        {
            c.ClaimedAt = DateTime.Now;
            c.ClaimError = null;
            Log?.Invoke($"{channel}: pegou o baú (+50" + (points is int now ? $", saldo {now})" : ")"));
        }
        else if (claimError is not null)
        {
            c.ClaimError = claimError;
            c.ClaimRetryAt = DateTime.Now + RetryWait;
            Log?.Invoke($"{channel}: o baú não passou ({claimError}); tento de novo em 30 min");
        }
        string bonus = c.ClaimError is string why ? $", baú: {why}" : c.ClaimedAt is DateTime got ? $", baú {got:HH:mm}" : "";

        if (points is not int balance)
        {
            c.Text = error ?? "a live não respondeu";
            return null;
        }
        if (cost is not int price)
        {
            // Sem bônus escolhido (só o baú) ou o canal sem o bônus: só o saldo.
            c.Points = balance;
            c.Text = $"{balance} pontos{bonus}" + (error is not null ? $", {error}" : "");
            return null;
        }

        string? note = null;
        if (RefundAt(c) is int limit && balance >= limit && Stopped is null)
        {
            Stopped = Site == LiveSite.Kick
                ? "os pontos voltaram (vincule a Kick no jogo, em Bônus na Kick, e salve Lives → Config… de novo)"
                : "os pontos voltaram (vincule a Twitch no jogo, em Bônus Twitch, e salve Lives → Config… de novo)";
            note = $"{Site}: os pontos do resgate voltaram ({Site} não vinculada no jogo?); parei de resgatar nesta conta";
            Log?.Invoke($"{channel}: os pontos do último resgate voltaram (saldo {balance}; {Site} não vinculada no jogo?); " +
                        "parei de resgatar nesta conta");
        }
        c.Cost = price;
        c.Points = balance;
        string? refusal = status?.ToString() ?? refused;
        if (redeemed)
        {
            c.Points = balance - price;
            c.RedeemedAt = DateTime.Now;
            note = $"{Site}: resgatei {title} em {channel}";
            Log?.Invoke($"{channel}: resgatou {title} ({price} pontos; sobraram {balance - price})");
        }
        else if (refusal is not null)
        {
            c.RetryAt = DateTime.Now + RetryWait;
            note = $"{Site}: o resgate em {channel} foi recusado (erro {refusal}); tento de novo em 30 min";
            Log?.Invoke($"{channel}: o resgate de {title} foi recusado (erro {refusal}; saldo {balance}); tento de novo em 30 min");
        }
        c.Text = $"{c.Points}/{price} pontos"
                 + (c.RedeemedAt is DateTime at ? $", resgatou {at:HH:mm}" : "")
                 + (refusal is not null && !redeemed ? $", resgate recusado ({refusal})" : "")
                 + (paused ? ", bônus pausado no canal" : "")
                 + bonus;
        return note;
    }
}