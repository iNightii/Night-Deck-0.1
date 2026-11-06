using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>
/// Uma extensão instalada pelo deck: onde está a pasta e o que o cabeçalho da conta precisa dela.
/// <paramref name="OpensWindow"/>: o botão abre <paramref name="Popup"/> numa janela própria, para
/// extensões cujo ícone abre uma página em vez de popup (o Cockpit do Rotom Sniper, por exemplo).
/// </summary>
internal sealed record ExtensionInfo(string Id, string Folder, string Name, string? Popup, string? Icon, string? Options,
    bool OpensWindow = false);

/// <summary>Uma extensão instalada no seu Chrome, que dá para trazer para o deck.</summary>
internal sealed record ChromeExtension(string ChromeId, string Name, string Folder);

/// <summary>
/// Guarda as extensões do deck. O WebView2 só carrega extensões descompactadas e não copia a pasta:
/// ela precisa continuar existindo sem mudanças (se mudar, ele desinstala). Por isso cada extensão
/// fica numa pasta própria do deck, uma por versão, que nunca é alterada depois de instalada.
/// </summary>
internal sealed class ExtensionStore
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _root;
    private readonly string _registryPath;
    private readonly Dictionary<string, ExtensionInfo> _known = new();

    public ExtensionStore(string dataRoot)
    {
        _root = Path.Combine(dataRoot, "extensions");
        _registryPath = Path.Combine(_root, "extensions.json");
        try
        {
            var saved = JsonSerializer.Deserialize<List<ExtensionInfo>>(File.ReadAllText(_registryPath));
            foreach (var info in saved ?? new())
                _known[info.Id] = info with { Folder = Rebase(info.Folder) ?? info.Folder };
        }
        catch
        {
            // Sem registro ainda: nenhuma extensão instalada pelo deck.
        }
    }

    public ExtensionInfo? Find(string id) => _known.GetValueOrDefault(id);

    /// <summary>
    /// Os dados do deck vieram de outro PC (ou de outro usuário do Windows): as pastas das extensões
    /// estão aqui, mas o perfil e o registro ainda guardam o caminho de lá. Devolve a pasta daqui de uma
    /// extensão cujo caminho antigo não existe mais (null se ela não mudou de lugar).
    /// </summary>
    private string? Rebase(string folder)
    {
        if (Directory.Exists(folder))
            return null;
        var here = Path.Combine(_root, Path.GetFileName(folder.TrimEnd('\\', '/')));
        return Directory.Exists(here) ? here : null;
    }

    /// <summary>
    /// Extensões descompactadas do perfil cuja pasta mudou de lugar (dados trazidos de outro PC): o id e
    /// a pasta daqui. Lê o "Secure Preferences" do perfil, onde o navegador guarda o caminho de cada uma.
    /// </summary>
    public List<(string Id, string Folder)> MovedExtensions(string profilePath)
    {
        var moved = new List<(string, string)>();
        try
        {
            using var prefs = JsonDocument.Parse(File.ReadAllText(Path.Combine(profilePath, "Secure Preferences")), Lenient);
            if (!prefs.RootElement.TryGetProperty("extensions", out var extensions) || !extensions.TryGetProperty("settings", out var settings))
                return moved;
            foreach (var entry in settings.EnumerateObject())
            {
                // 4 = descompactada (as do deck); as que vêm com o navegador ficam na pasta dele.
                if (entry.Value.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.Number && location.GetInt32() == 4
                    && entry.Value.TryGetProperty("path", out var path) && path.GetString() is string old && Rebase(old) is string here)
                    moved.Add((entry.Name, here));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Perfil novo (sem o arquivo ainda) ou ilegível: nada para consertar.
        }
        return moved;
    }

    /// <summary>
    /// Reinstala, da pasta daqui, as extensões do perfil que vieram de outro PC (com o mesmo id, o
    /// armazenamento delas, como as regras do Rotom, continua o mesmo). Devolve quantas reinstalou.
    /// </summary>
    public async Task<int> RepairAsync(CoreWebView2Profile profile)
    {
        int repaired = 0;
        foreach (var (id, folder) in MovedExtensions(profile.ProfilePath))
        {
            try
            {
                var extension = await profile.AddBrowserExtensionAsync(folder);
                _known[extension.Id] = Describe(folder) with { Id = extension.Id };
                repaired++;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or System.Runtime.InteropServices.COMException)
            {
                // Pasta estragada: fica sem a extensão, que dá para instalar de novo em Extensões.
            }
        }
        if (repaired > 0)
            Save();
        return repaired;
    }

    public async Task<ExtensionInfo> InstallAsync(CoreWebView2Profile profile, string folder)
    {
        var extension = await profile.AddBrowserExtensionAsync(folder);
        var info = Describe(folder) with { Id = extension.Id };
        _known[info.Id] = info;
        Save();
        return info;
    }

    /// <summary>Apaga as pastas de extensões que nenhuma conta usa mais.</summary>
    public async Task CleanupAsync(IEnumerable<CoreWebView2Profile> profiles)
    {
        var inUse = new HashSet<string>();
        foreach (var profile in profiles)
            foreach (var extension in await profile.GetBrowserExtensionsAsync())
                inUse.Add(extension.Id);

        foreach (var info in _known.Values.Where(i => !inUse.Contains(i.Id)).ToList())
        {
            _known.Remove(info.Id);
            try
            {
                Directory.Delete(info.Folder, recursive: true);
            }
            catch
            {
                // Se algo ainda segura a pasta, ela fica; não atrapalha nada.
            }
        }
        Save();
    }

    public static IReadOnlyList<ChromeExtension> FindChromeExtensions()
    {
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Google\Chrome\User Data");
        var found = new Dictionary<string, ChromeExtension>();
        if (!Directory.Exists(userData))
            return Array.Empty<ChromeExtension>();

        var profiles = Directory.GetDirectories(userData)
            .Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile "));
        foreach (var extensionsDir in profiles.Select(p => Path.Combine(p, "Extensions")).Where(Directory.Exists))
        {
            foreach (var idDir in Directory.GetDirectories(extensionsDir))
            {
                var chromeId = Path.GetFileName(idDir);
                // Componente interno do Chrome, não é uma extensão de verdade.
                if (found.ContainsKey(chromeId) || chromeId == "nmmhkkegccagdldgiimedpiccmgmieda")
                    continue;
                var latest = Directory.GetDirectories(idDir)
                    .Where(v => File.Exists(Path.Combine(v, "manifest.json")))
                    .OrderBy(v => Version.TryParse(Path.GetFileName(v).Split('_')[0], out var ver) ? ver : new Version())
                    .LastOrDefault();
                if (latest is null)
                    continue;
                try
                {
                    found[chromeId] = new ChromeExtension(chromeId, Describe(latest).Name, latest);
                }
                catch
                {
                    // Manifesto ilegível: deixa essa de fora.
                }
            }
        }
        return found.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public string ImportFromChrome(ChromeExtension extension) =>
        CopyIn(extension.Folder, $"{extension.ChromeId}_{Path.GetFileName(extension.Folder)}");

    public string CopyUnpacked(string folder)
    {
        if (!File.Exists(Path.Combine(folder, "manifest.json")))
            throw new InvalidOperationException("Essa pasta não tem manifest.json: escolha a pasta da extensão descompactada.");
        return CopyIn(folder, $"{SafeName(Path.GetFileName(folder.TrimEnd('\\')))}_{DateTime.Now:yyyyMMddHHmmss}");
    }

    /// <summary>Baixa a extensão da Chrome Web Store pelo link (ou ID) e descompacta numa pasta do deck.</summary>
    public async Task<string> DownloadFromWebStoreAsync(string linkOrId)
    {
        var match = Regex.Match(linkOrId, "[a-p]{32}");
        if (!match.Success)
            throw new InvalidOperationException("Não achei o ID da extensão nesse link (são 32 letras de a até p, no fim do endereço).");
        var id = match.Value;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        var crx = await http.GetByteArrayAsync(
            $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=131.0.0.0" +
            $"&acceptformat=crx2,crx3&x=id%3D{id}%26installsource%3Dondemand%26uc");
        var (zipStart, publicKey) = ParseCrx(crx, id);

        var temp = Path.Combine(_root, "_baixando_" + id);
        if (Directory.Exists(temp))
            Directory.Delete(temp, recursive: true);
        using (var zip = new ZipArchive(new MemoryStream(crx, zipStart, crx.Length - zipStart)))
            zip.ExtractToDirectory(temp);
        RemoveMetadata(temp);

        var manifestPath = Path.Combine(temp, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath), documentOptions: Lenient)!.AsObject();
        // Com a chave pública no manifesto a extensão mantém o mesmo ID da loja.
        if (publicKey is not null && !manifest.ContainsKey("key"))
        {
            manifest["key"] = Convert.ToBase64String(publicKey);
            File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        var dest = Path.Combine(_root, $"{id}_{SafeName(Text(manifest["version"]) ?? "0")}");
        if (Directory.Exists(dest))
        {
            Directory.Delete(temp, recursive: true);
            return dest;
        }
        Directory.Move(temp, dest);
        return dest;
    }

    /// <summary>Nome, popup, ícone e página de opções a partir do manifest.json.</summary>
    public static ExtensionInfo Describe(string folder)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")), documentOptions: Lenient)!.AsObject();
        var action = manifest["action"] ?? manifest["browser_action"] ?? manifest["page_action"];
        var popup = Text(action?["default_popup"])?.TrimStart('/');
        var options = (Text(manifest["options_ui"]?["page"]) ?? Text(manifest["options_page"]))?.TrimStart('/');

        // Ícone sem popup: no Chrome o clique vai para a própria extensão, que costuma abrir uma página dela.
        // Aqui não dá para mandar esse clique; se houver uma única página assim, o botão abre ela direto.
        string? page = null;
        if (popup is null && action is not null)
        {
            var background = Text(manifest["background"]?["page"])?.TrimStart('/');
            var pages = Directory.GetFiles(folder, "*.html")
                .Select(Path.GetFileName)
                .Where(f => f != options && f != background)
                .ToList();
            if (pages.Count == 1)
                page = pages[0];
        }

        return new ExtensionInfo(
            Id: "",
            Folder: folder,
            Name: Localize(manifest, folder, Text(manifest["name"])) is { Length: > 0 } name ? name : Path.GetFileName(folder),
            Popup: popup ?? page,
            Icon: PickIcon(action?["default_icon"]) ?? PickIcon(manifest["icons"]),
            Options: options,
            OpensWindow: popup is null && page is not null);
    }

    private string CopyIn(string source, string name)
    {
        var dest = Path.Combine(_root, name);
        // Pasta já instalada nunca é reescrita: o WebView2 desinstala a extensão se o conteúdo mudar.
        if (Directory.Exists(dest))
            return dest;
        var temp = dest + ".copiando";
        if (Directory.Exists(temp))
            Directory.Delete(temp, recursive: true);
        CopyDirectory(source, temp);
        Directory.Move(temp, dest);
        return dest;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        // _metadata é do Chrome (assinatura da loja); extensão descompactada com ela não carrega.
        foreach (var dir in Directory.GetDirectories(source).Where(d => Path.GetFileName(d) != "_metadata"))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static void RemoveMetadata(string folder)
    {
        var metadata = Path.Combine(folder, "_metadata");
        if (Directory.Exists(metadata))
            Directory.Delete(metadata, recursive: true);
    }

    private void Save()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_registryPath, JsonSerializer.Serialize(_known.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? PickIcon(JsonNode? icons)
    {
        if (Text(icons) is string single)
            return single.TrimStart('/');
        if (icons is not JsonObject sizes)
            return null;
        var options = sizes
            .Select(p => (Size: int.TryParse(p.Key, out var s) ? s : 0, Path: Text(p.Value)))
            .Where(o => o.Path is not null)
            .OrderBy(o => o.Size)
            .ToList();
        var best = options.FirstOrDefault(o => o.Size >= 16);
        return (best.Path ?? options.LastOrDefault().Path)?.TrimStart('/');
    }

    /// <summary>Resolve nomes "__MSG_chave__" pelos arquivos de tradução da extensão.</summary>
    private static string Localize(JsonObject manifest, string folder, string? value)
    {
        if (value is null || !value.StartsWith("__MSG_") || !value.EndsWith("__"))
            return value ?? "";
        var key = value[6..^2];
        var locales = new[] { "pt_BR", "pt", Text(manifest["default_locale"]), "en" }.OfType<string>().Distinct();
        foreach (var locale in locales)
        {
            var path = Path.Combine(folder, "_locales", locale, "messages.json");
            if (!File.Exists(path))
                continue;
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) is not JsonObject messages)
                continue;
            var entry = messages.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
            if (Text(entry?["message"]) is { Length: > 0 } message)
                return message;
        }
        return key;
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

    /// <summary>Onde começa o zip dentro do .crx e a chave pública da extensão (para manter o ID).</summary>
    private static (int ZipStart, byte[]? PublicKey) ParseCrx(byte[] crx, string id)
    {
        if (crx.Length < 16 || Encoding.ASCII.GetString(crx, 0, 4) != "Cr24")
            throw new InvalidDataException("A loja não devolveu uma extensão (.crx). Confira o link.");
        uint version = BitConverter.ToUInt32(crx, 4);
        if (version == 2)
        {
            int keyLength = (int)BitConverter.ToUInt32(crx, 8);
            int signatureLength = (int)BitConverter.ToUInt32(crx, 12);
            return (16 + keyLength + signatureLength, crx[16..(16 + keyLength)]);
        }
        if (version == 3)
        {
            int headerLength = (int)BitConverter.ToUInt32(crx, 8);
            var header = crx[12..(12 + headerLength)];
            // CrxFileHeader (protobuf): campos 2 e 3 são as provas de chave; dentro delas, o campo 1 é a chave pública.
            var key = ProtoFields(header)
                .Where(f => f.Field is 2 or 3)
                .SelectMany(f => ProtoFields(f.Data))
                .Where(f => f.Field == 1)
                .Select(f => f.Data)
                .FirstOrDefault(k => IdFromKey(k) == id);
            return (12 + headerLength, key);
        }
        throw new InvalidDataException($"Formato de .crx desconhecido (versão {version}).");
    }

    private static List<(int Field, byte[] Data)> ProtoFields(byte[] buffer)
    {
        var fields = new List<(int, byte[])>();
        int pos = 0;
        while (pos < buffer.Length)
        {
            ulong tag = ReadVarint(buffer, ref pos);
            int field = (int)(tag >> 3);
            switch ((int)(tag & 7))
            {
                case 0: ReadVarint(buffer, ref pos); break;
                case 1: pos += 8; break;
                case 2:
                    int length = (int)ReadVarint(buffer, ref pos);
                    fields.Add((field, buffer[pos..(pos + length)]));
                    pos += length;
                    break;
                case 5: pos += 4; break;
                default: return fields;
            }
        }
        return fields;
    }

    private static ulong ReadVarint(byte[] buffer, ref int pos)
    {
        ulong result = 0;
        for (int shift = 0; pos < buffer.Length; shift += 7)
        {
            byte b = buffer[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
        }
        return result;
    }

    private static string IdFromKey(byte[] key)
    {
        var hash = SHA256.HashData(key);
        var id = new StringBuilder(32);
        foreach (var b in hash.AsSpan(0, 16))
        {
            id.Append((char)('a' + (b >> 4)));
            id.Append((char)('a' + (b & 0xF)));
        }
        return id.ToString();
    }
}