using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>
/// Teste de consumo das lives (<c>--consumo</c>): abre, com o mesmo código da janela das lives, players da Twitch e da
/// Kick ao vivo num perfil de teste à parte (sem login, fora das contas do deck), e mede a CPU e a RAM de todo o
/// navegador embutido em cada cenário: nenhuma live, 1, 2 e 4 players da Twitch, a página inteira do canal da
/// Twitch, um player sem os cortes de consumo (qualidade original, com propaganda e animações) e um da Kick. Cada
/// cenário espera o vídeo firmar e mede por 1 minuto. Grava o resultado em %TEMP%\PokeIdleDeck-consumo.txt e fecha.
/// </summary>
internal sealed class ConsumptionTest : Form
{
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Sample = TimeSpan.FromSeconds(60);

    public static string Report => Path.Combine(Path.GetTempPath(), "PokeIdleDeck-consumo.txt");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "PokeIdleDeck-consumo");
    private readonly List<string> _log = new();

    // O que tem no vídeo de cada página: a altura da imagem, se toca, onde está, e se a Twitch mostra propaganda.
    private const string VideoState =
        "(() => { const v = document.querySelector('video'); if (!v) return 'sem vídeo'; " +
        "const ad = !!document.querySelector('[data-a-target=\"video-ad-label\"], [data-a-target=\"video-ad-countdown\"]'); " +
        "return `${v.videoHeight}p ${v.paused ? 'pausado' : 'tocando'} t=${v.currentTime.toFixed(0)}${ad ? ' propaganda' : ''}`; })()";

    public ConsumptionTest()
    {
        Text = "PokeIdle Deck — teste de consumo";
        Icon = Theme.AppIcon;
        ShowInTaskbar = true;
        Size = new Size(420, 120);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Medindo o consumo das lives (uns 15 min). Fecha sozinho.", TextAlign = ContentAlignment.MiddleCenter });
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            _log.Add("ERRO " + ex);
        }
        finally
        {
            File.WriteAllText(Report, string.Join(Environment.NewLine, _log));
            Close();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch
        {
            // Sobra de um teste anterior ainda presa: usa como está.
        }
        var options = new CoreWebView2EnvironmentOptions(DeckForm.BrowserArguments);
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_root, "WebView2"), options);

        // As lives que estão no ar agora: as oficiais primeiro; se faltar, as maiores da Twitch.
        var official = new DeckConfig();
        var twitchLive = await TwitchLive.LiveAsync(official.TwitchChannels);
        var twitch = official.TwitchChannels.Where(c => twitchLive.GetValueOrDefault(c) == true).ToList();
        if (twitch.Count < 4)
            twitch.AddRange((await TopTwitchAsync()).Where(c => !twitch.Contains(c)));
        twitch = twitch.Take(4).ToList();
        string? kick = null;
        foreach (var channel in official.KickChannels)
            if (await KickLive.IsLiveAsync(channel) == true)
            {
                kick = channel;
                break;
            }
        _log.Add($"Teste de consumo das lives — {DateTime.Now:dd/MM/yyyy HH:mm}, {Environment.ProcessorCount} núcleos lógicos, " +
                 $"WebView2 {env.BrowserVersionString}");
        _log.Add($"Twitch no ar usadas: {string.Join(", ", twitch)}; Kick: {kick ?? "nenhuma oficial no ar"}");
        _log.Add($"Cada cenário: {Warmup.TotalSeconds:0} s para firmar, depois {Sample.TotalSeconds:0} s medindo. CPU em % de um núcleo " +
                 "(100% = um núcleo inteiro) e em % do PC; RAM = memória privada somada de todos os processos do navegador embutido.");
        _log.Add("");

        var scenarios = new List<(string Name, LiveSite Site, string[] Urls, bool Lean)>
        {
            ("nenhuma live (só o navegador e uma página em branco)", LiveSite.Twitch, Array.Empty<string>(), true),
        };
        if (twitch.Count > 0)
        {
            scenarios.Add(("Twitch: 1 player", LiveSite.Twitch, twitch.Take(1).Select(TwitchLive.PlayerUrl).ToArray(), true));
            scenarios.Add(("Twitch: 2 players", LiveSite.Twitch, twitch.Take(2).Select(TwitchLive.PlayerUrl).ToArray(), true));
            if (twitch.Count >= 4)
                scenarios.Add(("Twitch: 4 players", LiveSite.Twitch, twitch.Take(4).Select(TwitchLive.PlayerUrl).ToArray(), true));
            scenarios.Add(("Twitch: 1 página inteira do canal (com o chat)", LiveSite.Twitch, new[] { $"https://www.twitch.tv/{twitch[0]}" }, true));
            scenarios.Add(("Twitch: 1 player SEM os cortes (qualidade original, propaganda, animações)", LiveSite.Twitch,
                twitch.Take(1).Select(TwitchLive.PlayerUrl).ToArray(), false));
        }
        if (kick is not null)
            scenarios.Add(("Kick: 1 player (para comparar)", LiveSite.Kick, new[] { $"https://player.kick.com/{kick}" }, true));

        foreach (var scenario in scenarios)
            await MeasureAsync(env, scenario.Name, scenario.Site, scenario.Urls, scenario.Lean);
    }

    private async Task MeasureAsync(CoreWebView2Environment env, string name, LiveSite site, string[] urls, bool lean)
    {
        int slots = Math.Max(1, urls.Length);
        var window = await LivesWindow.CreateAsync(env, "consumo", "teste de consumo", Array.Empty<string>(),
            site == LiveSite.Twitch ? slots : 0, site == LiveSite.Kick ? slots : 0, (_, c) => c, null, lean);
        window.AllowClose = true;
        // Cada endereço numa vaga, como o deck faz com as lives no ar (o canal é só o nome da vaga).
        window.SetLives(site, urls.Select((url, i) => ($"live{i + 1}", url)).ToList());

        // Firmar: a conferência das lives (a mesma do deck) manda tocar a que estiver pausada.
        var until = DateTime.Now + Warmup;
        while (DateTime.Now < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            await window.WatchAsync();
        }

        var before = await CpuAsync(env);
        var watch = Stopwatch.StartNew();
        var ram = new List<long>();
        until = DateTime.Now + Sample;
        while (DateTime.Now < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            ram.Add(await RamAsync(env));
            await window.WatchAsync();
        }
        var after = await CpuAsync(env);
        double seconds = watch.Elapsed.TotalSeconds;

        // CPU por tipo de processo (só os que estavam vivos no começo e no fim da medição).
        var byKind = new Dictionary<string, double>();
        foreach (var (pid, (kind, time)) in after)
            if (pid != -1 && before.TryGetValue(pid, out var start))
                byKind[kind] = byKind.GetValueOrDefault(kind) + (time - start.Time).TotalSeconds;
        double deck = (after.GetValueOrDefault(-1).Time - before.GetValueOrDefault(-1).Time).TotalSeconds;
        byKind["deck (o .NET, que corta a qualidade)"] = deck;
        double total = byKind.Values.Sum();
        double core = total / seconds * 100;

        var videos = urls.Length == 0 ? new List<string>() : await window.EvalAsync(VideoState);
        _log.Add($"== {name}");
        _log.Add(string.Format(CultureInfo.InvariantCulture, "   CPU {0:0.0}% de um núcleo ({1:0.0}% do PC) · RAM {2:0} MB",
            core, core / Environment.ProcessorCount, ram.Count == 0 ? 0 : ram.Average() / 1024 / 1024));
        _log.Add("   CPU por parte: " + string.Join(", ", byKind.Where(k => k.Value > 0.05 * seconds / 100)
            .OrderByDescending(k => k.Value).Select(k => string.Format(CultureInfo.InvariantCulture, "{0} {1:0.0}%", k.Key, k.Value / seconds * 100))));
        if (videos.Count > 0)
            _log.Add("   vídeo: " + string.Join(" | ", videos.Select(v => JsonSerializer.Deserialize<string>(v))));
        _log.Add("   " + string.Join(" | ", window.Playback(site).Select(p => $"{p.Channel}: {(p.Playing == true ? "andando" : "parado")}, recarregou {p.Reloads}")));
        _log.Add("");
        File.WriteAllText(Report, string.Join(Environment.NewLine, _log));

        window.Close();
        // Os processos das páginas fecham; espera antes do próximo cenário.
        await Task.Delay(TimeSpan.FromSeconds(10));
    }

    /// <summary>O tempo de CPU de cada processo do navegador (e do próprio deck, no -1), com o tipo dele.</summary>
    private static async Task<Dictionary<int, (string Kind, TimeSpan Time)>> CpuAsync(CoreWebView2Environment env)
    {
        var result = new Dictionary<int, (string, TimeSpan)>();
        foreach (var info in await env.GetProcessExtendedInfosAsync())
        {
            try
            {
                using var process = Process.GetProcessById(info.ProcessInfo.ProcessId);
                result[info.ProcessInfo.ProcessId] = (Kind(info.ProcessInfo.Kind), process.TotalProcessorTime);
            }
            catch (ArgumentException)
            {
                // Fechou agora.
            }
        }
        using var self = Process.GetCurrentProcess();
        result[-1] = ("deck", self.TotalProcessorTime);
        return result;
    }

    private static async Task<long> RamAsync(CoreWebView2Environment env)
    {
        long bytes = 0;
        foreach (var info in await env.GetProcessExtendedInfosAsync())
        {
            try
            {
                using var process = Process.GetProcessById(info.ProcessInfo.ProcessId);
                bytes += process.PrivateMemorySize64;
            }
            catch (ArgumentException)
            {
            }
        }
        return bytes;
    }

    private static string Kind(CoreWebView2ProcessKind kind) => kind switch
    {
        CoreWebView2ProcessKind.Browser => "navegador",
        CoreWebView2ProcessKind.Renderer => "páginas",
        CoreWebView2ProcessKind.Gpu => "placa de vídeo",
        _ => "outros",
    };

    /// <summary>As lives com mais gente na Twitch agora (quando faltam oficiais no ar para o teste).</summary>
    private static async Task<List<string>> TopTwitchAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
            {
                Content = new StringContent("{\"query\":\"query{streams(first:8){edges{node{broadcaster{login}}}}}\"}", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Client-Id", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            using var response = await http.SendAsync(request);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("data").GetProperty("streams").GetProperty("edges").EnumerateArray()
                .Select(e => e.GetProperty("node").GetProperty("broadcaster").GetProperty("login").GetString()!).ToList();
        }
        catch
        {
            return new();
        }
    }
}