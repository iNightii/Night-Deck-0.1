using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>
/// Corta propaganda e rastreadores nas páginas das lives. A página da Kick abre ~40 sites de fora
/// (anúncios do Google, OpenX, Criteo, análises do TikTok/Facebook…), vários em iframes com processo
/// próprio: medido, 2 lives da Kick caem de ~650 MB para ~440 MB, e o vídeo segue tocando. O vídeo,
/// a própria Kick/Twitch e o Cloudflare (a checagem anti-robô) passam normal.
/// </summary>
internal static class AdBlock
{
    private static readonly string[] Domains =
    {
        // Anúncios
        "googlesyndication.com", "doubleclick.net", "googleadservices.com", "adtrafficquality.google",
        "imasdk.googleapis.com", "openx.net", "openxcdn.net", "criteo.com", "criteo.net", "dnacdn.net",
        "creativecdn.com", "id5-sync.com", "amazon-adsystem.com",
        // Análises e rastreadores
        "googletagmanager.com", "google-analytics.com", "tiktok.com", "tiktokw.us", "facebook.net", "facebook.com",
        "analytics.yahoo.com", "quora.com", "litix.io", "datazoom.io", "datadoghq-browser-agent.com",
        "cloudflareinsights.com", "scorecardresearch.com",
        // Aviso de cookies e pagamento (as lives não assinam nada)
        "onetrust.com", "stripe.com", "stripe.network",
    };

    public static void Apply(CoreWebView2Environment env, CoreWebView2 core)
    {
        foreach (var domain in Domains)
        {
            core.AddWebResourceRequestedFilter($"*://{domain}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.AddWebResourceRequestedFilter($"*://*.{domain}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }
        core.WebResourceRequested += (_, e) =>
        {
            // Um "não" que o script da página consegue ler (e o autoteste também).
            if (Blocked(e.Request.Uri))
                e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "Access-Control-Allow-Origin: *");
        };
    }

    public static bool Blocked(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && Domains.Any(d => parsed.Host.Equals(d, StringComparison.OrdinalIgnoreCase)
                            || parsed.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
}