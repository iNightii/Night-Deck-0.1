using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// Atualizações apenas dos Releases publicados pelo repositório oficial deste deck.
/// Nunca mexe na pasta de perfis WebView2 nem instala algo sem confirmação.
/// </summary>
internal static class AppUpdater
{
    public const string ReleasesPage = "https://github.com/iNightii/Night-Deck-0.1/releases/latest";
    private const string LatestApi = "https://api.github.com/repos/iNightii/Night-Deck-0.1/releases/latest";
    private const long MaxInstallerBytes = 500L * 1024 * 1024;
    private static readonly HttpClient Api = CreateClient(TimeSpan.FromSeconds(20));
    private static readonly HttpClient Downloads = CreateClient(TimeSpan.FromMinutes(15));

    // O GitHub Actions publica com -p:Version=1.0.<run_number>.
    // Compilações locais sem esse parâmetro têm build 0.
    public static int CurrentBuild => Assembly.GetExecutingAssembly().GetName().Version?.Build ?? 0;

    internal sealed record Release(int Build, string Tag, Uri InstallerUrl, string? Sha256);

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NightDeck", "1.0"));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<Release?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync(LatestApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var release = json.RootElement;
        if (!release.TryGetProperty("tag_name", out var tagNode))
            return null;
        var tag = tagNode.GetString() ?? "";
        if (!tag.StartsWith("build-", StringComparison.Ordinal) ||
            !int.TryParse(tag.AsSpan("build-".Length), out var build) ||
            build <= CurrentBuild)
            return null;

        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) ||
                name.GetString() != "Night-Deck-Setup.exe" ||
                !asset.TryGetProperty("browser_download_url", out var download) ||
                !Uri.TryCreate(download.GetString(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
                !uri.AbsolutePath.StartsWith("/iNightii/Night-Deck-0.1/releases/download/", StringComparison.OrdinalIgnoreCase))
                continue;

            string? digest = null;
            if (asset.TryGetProperty("digest", out var hash) && hash.ValueKind == JsonValueKind.String)
            {
                var value = hash.GetString();
                if (value?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
                    digest = value["sha256:".Length..];
            }
            return new Release(build, tag, uri, digest);
        }
        return null;
    }

    public static async Task<string> DownloadAsync(Release release, IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        string folder = Path.Combine(Path.GetTempPath(), "NightDeck-Updates");
        Directory.CreateDirectory(folder);
        string installer = Path.Combine(folder, $"Night-Deck-Setup-{release.Build}.exe");
        string temporary = installer + ".part";
        try
        {
            using var response = await Downloads.GetAsync(release.InstallerUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? length = response.Content.Headers.ContentLength;
            if (length is > MaxInstallerBytes)
                throw new IOException("O instalador ultrapassou o limite de tamanho.");

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += read;
                    if (received > MaxInstallerBytes)
                        throw new IOException("O instalador ultrapassou o limite de tamanho.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (length is > 0)
                        progress.Report((int)Math.Min(100, received * 100 / length.Value));
                }
                if (received == 0 || (length.HasValue && received != length.Value))
                    throw new IOException("O download do instalador ficou incompleto.");
            }

            if (release.Sha256 is { Length: 64 } checksum)
            {
                await using var file = File.OpenRead(temporary);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
                if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A verificação de integridade do instalador falhou.");
            }

            File.Move(temporary, installer, overwrite: true);
            progress.Report(100);
            return installer;
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }
}
