using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NightDeck;

internal sealed class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string repo;
    public UpdateService(string repository) => repo = repository.Trim().Trim('/');
    public bool Configured => Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");

    public async Task<UpdateResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!Configured) return new(false, "Configure o repositório do GitHub em update-source.json.", null, null);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("NightDeck", "1.0"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(req, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new(false, "Ainda não existe uma versão publicada nos Releases.", null, null);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        string page = root.GetProperty("html_url").GetString() ?? $"https://github.com/{repo}/releases";
        var versionText = tag.TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var latest))
            return new(false, $"Release '{tag}' encontrado, mas não possui versão numérica.", page, null);
        var current = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
        if (latest <= current) return new(false, $"Night Deck atualizado (v{current.ToString(3)}).", page, null);
        string? installer = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("Night", StringComparison.OrdinalIgnoreCase))
                { installer = asset.GetProperty("browser_download_url").GetString(); break; }
            }
        return new(true, $"Nova versão disponível: v{latest} (instalada: v{current.ToString(3)}).", page, installer);
    }

    public async Task<string> DownloadInstallerAsync(string url, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Origem do instalador não permitida.");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Night Deck", "Updates");
        Directory.CreateDirectory(folder);
        var filename = Path.GetFileName(uri.AbsolutePath);
        if (!filename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Arquivo não é um instalador .exe.");
        var target = Path.Combine(folder, filename);
        var temp = target + ".download";
        using var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        res.EnsureSuccessStatusCode();
        await using (var input = await res.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(temp))
        {
            var buffer = new byte[81920]; long received = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                received += count;
                if (res.Content.Headers.ContentLength is long total && total > 0)
                    progress?.Report((int)Math.Min(100, received * 100 / total));
            }
        }
        File.Move(temp, target, true);
        return target;
    }
}

internal sealed record UpdateResult(bool Available, string Message, string? ReleasePage, string? InstallerUrl);
