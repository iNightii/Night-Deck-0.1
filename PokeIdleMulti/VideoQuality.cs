using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>
/// Deixa as lives (Kick e Twitch) na menor qualidade de vídeo. Quando o player pede a lista principal
/// do vídeo (.m3u8, uma linha por qualidade), o deck busca essa lista e entrega só a menor qualidade
/// com imagem: o player nem tem como subir, e a live segue tocando, que é o que conta para os pontos.
/// </summary>
internal static class VideoQuality
{
    // Listas principais: Kick (Amazon IVS) e Twitch. As listas de cada qualidade passam direto.
    private static readonly string[] MasterPlaylists =
    {
        "https://*.playback.live-video.net/api/video/v1/*",
        "https://usher.ttvnw.net/*",
    };

    private static readonly Regex Bandwidth = new(@"(?<![-A-Z])BANDWIDTH=(\d+)");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static void LimitToLowest(CoreWebView2Environment env, CoreWebView2 core)
    {
        // O player da Kick baixa o vídeo de dentro de um Web Worker; sem "All" esses pedidos não aparecem.
        foreach (var pattern in MasterPlaylists)
            core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += async (_, e) =>
        {
            if (e.Request.Method != "GET" || !IsMasterPlaylist(e.Request.Uri))
                return;
            var deferral = e.GetDeferral();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, e.Request.Uri);
                string? origin = null;
                foreach (var (name, value) in e.Request.Headers)
                {
                    if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                        origin = value;
                    if (name is "Origin" or "Referer" or "User-Agent" or "Accept" or "Accept-Language")
                        request.Headers.TryAddWithoutValidation(name, value);
                }
                using var response = await Http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                // Sem sucesso ou sem o que escolher: o pedido do player segue normal.
                if (!response.IsSuccessStatusCode || LowestOnly(body) is not string lowest)
                    return;
                var headers = "Content-Type: application/vnd.apple.mpegurl\r\nCache-Control: no-cache\r\n" +
                              $"Access-Control-Allow-Origin: {origin ?? "*"}\r\nVary: Origin";
                if (origin is not null)
                    headers += "\r\nAccess-Control-Allow-Credentials: true";
                e.Response = env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(lowest)), 200, "OK", headers);
            }
            catch
            {
                // Qualquer problema: deixa o player pegar a lista original.
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    private static bool IsMasterPlaylist(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.AbsolutePath.EndsWith(".m3u8")
        && ((parsed.Host.EndsWith(".playback.live-video.net") && parsed.AbsolutePath.StartsWith("/api/video/v1/"))
            || parsed.Host == "usher.ttvnw.net");

    /// <summary>
    /// A lista principal com uma só qualidade: a de menor banda que tem imagem (só áudio não serve, a
    /// live precisa estar tocando vídeo). Devolve null se não houver pelo menos duas para escolher.
    /// </summary>
    public static string? LowestOnly(string playlist)
    {
        var lines = playlist.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("#EXTM3U"))
            return null;
        var kept = new List<string>();
        var variants = new List<(long Bandwidth, bool HasVideo, string Info, string Uri)>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                if (line.Length > 0)
                    kept.Add(line);
                continue;
            }
            int next = i + 1;
            while (next < lines.Length && (lines[next].Length == 0 || lines[next].StartsWith('#')))
                next++;
            if (next >= lines.Length)
                break;
            var match = Bandwidth.Match(line);
            variants.Add((match.Success ? long.Parse(match.Groups[1].Value) : long.MaxValue, line.Contains("RESOLUTION="), line, lines[next]));
            i = next;
        }

        var withVideo = variants.Where(v => v.HasVideo).ToList();
        if (withVideo.Count < 2)
            return null;
        var lowest = withVideo.MinBy(v => v.Bandwidth);
        kept.Add(lowest.Info);
        kept.Add(lowest.Uri);
        return string.Join("\n", kept) + "\n";
    }
}