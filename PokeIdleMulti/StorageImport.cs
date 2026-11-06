using System.Text.Json;
using System.Text.Json.Nodes;

namespace PokeIdleMulti;

/// <summary>Um lugar de onde dá para trazer as configurações (chrome.storage.local) de uma extensão.</summary>
internal sealed record StorageSource(string Label, string Folder, DateTime Changed, long Size);

/// <summary>Uma cópia pedida, aplicada quando o deck abrir (com a conta fechada o banco não está em uso).</summary>
internal sealed record PendingImport(string ExtensionId, string Source, string SourceLabel, string Profile);

/// <summary>
/// Traz as configurações de uma extensão de outro navegador (Chrome, IdleDeck ou outra conta do deck).
/// Elas ficam num banco LevelDB por extensão e por perfil, no mesmo formato em todos os navegadores
/// baseados no Chromium; como a extensão tem o mesmo ID, basta copiar a pasta. Só dá para escrever
/// com o deck fechado, então o pedido fica numa fila aplicada na próxima abertura.
/// </summary>
internal static class StorageImport
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string StorageFolder(string root, string profile, string extensionId) =>
        Path.Combine(root, "WebView2", "EBWebView", $"WV2Profile_{profile}", "Local Extension Settings", extensionId);

    /// <summary>Onde essa extensão tem configurações salvas: perfis do Chrome, contas do IdleDeck e as outras contas do deck.</summary>
    public static List<StorageSource> FindSources(string root, string extensionId, IReadOnlyList<(string Profile, string Name)> deckAccounts)
    {
        var sources = new List<StorageSource>();

        var chrome = Path.Combine(LocalAppData, @"Google\Chrome\User Data");
        var chromeNames = ChromeProfileNames(chrome);
        if (Directory.Exists(chrome))
        {
            foreach (var dir in Directory.GetDirectories(chrome))
            {
                var name = Path.GetFileName(dir);
                Add(sources, $"Chrome · {chromeNames.GetValueOrDefault(name, name)}",
                    Path.Combine(dir, "Local Extension Settings", extensionId));
            }
        }

        // IdleDeck vem da Microsoft Store: os dados ficam na pasta virtualizada do pacote.
        foreach (var package in SafeDirectories(Path.Combine(LocalAppData, "Packages"), "MacedoTI.IdleDeck_*"))
        {
            var data = Path.Combine(package, @"LocalCache\Roaming\IdleDeck");
            var names = IdleDeckSlotNames(Path.Combine(data, "config.json"));
            foreach (var slot in SafeDirectories(Path.Combine(data, "Partitions"), "slot-*"))
            {
                var id = Path.GetFileName(slot)["slot-".Length..];
                Add(sources, $"IdleDeck · {names.GetValueOrDefault(id, id[..8])}",
                    Path.Combine(slot, "Local Extension Settings", extensionId));
            }
        }

        foreach (var (profile, name) in deckAccounts)
            Add(sources, $"Deck · {name}", StorageFolder(root, profile, extensionId));

        return sources;
    }

    public static void Queue(string root, PendingImport import)
    {
        var pending = Load(root);
        pending.RemoveAll(p => p.ExtensionId == import.ExtensionId && p.Profile == import.Profile);
        pending.Add(import);
        Save(root, pending);
    }

    public static bool HasPending(string root) => Load(root).Count > 0;

    /// <summary>
    /// Aplica a fila. A configuração que a conta tinha vai para a pasta backups. O que não deu para
    /// aplicar (banco ainda aberto por um deck fechando) fica na fila para a próxima vez.
    /// </summary>
    public static List<(string Profile, string Result)> ApplyPending(string root)
    {
        var results = new List<(string, string)>();
        var pending = Load(root);
        var left = new List<PendingImport>();
        foreach (var import in pending)
        {
            var dest = StorageFolder(root, import.Profile, import.ExtensionId);
            try
            {
                if (!Directory.Exists(import.Source))
                    throw new DirectoryNotFoundException("a origem não existe mais");
                if (!WaitUntilClosed(dest, TimeSpan.FromSeconds(20)))
                {
                    left.Add(import);
                    results.Add((import.Profile, "ainda em uso, fica para a próxima vez que o deck abrir"));
                    continue;
                }
                if (Directory.Exists(dest))
                {
                    var backup = Path.Combine(root, "backups", $"{DateTime.Now:yyyyMMdd-HHmmss}_{import.Profile}_{import.ExtensionId}");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    Directory.Move(dest, backup);
                }
                CopyDatabase(import.Source, dest);
                results.Add((import.Profile, "configurações de " + import.SourceLabel));
            }
            catch (Exception ex)
            {
                results.Add((import.Profile, $"não deu ({ex.Message})"));
            }
        }
        Save(root, left);
        return results;
    }

    /// <summary>Copia o banco sem o LOCK e os LOG de texto, que são do navegador que abriu ele.</summary>
    public static void CopyDatabase(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (name == "LOCK" || name.StartsWith("LOG"))
                continue;
            // A origem pode estar aberta (Chrome ou IdleDeck rodando); ler compartilhado funciona.
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = File.Create(Path.Combine(dest, name));
            input.CopyTo(output);
        }
    }

    private static bool WaitUntilClosed(string database, TimeSpan timeout)
    {
        var lockFile = Path.Combine(database, "LOCK");
        var deadline = DateTime.Now + timeout;
        while (true)
        {
            if (!File.Exists(lockFile))
                return true;
            try
            {
                using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    return true;
            }
            catch (IOException) when (DateTime.Now < deadline)
            {
                Thread.Sleep(500);
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    private static void Add(List<StorageSource> sources, string label, string folder)
    {
        if (!Directory.Exists(folder))
            return;
        var files = new DirectoryInfo(folder).GetFiles();
        // Só LOCK/LOG: a extensão nunca guardou nada ali.
        if (!files.Any(f => f.Name.EndsWith(".ldb") || (f.Name.EndsWith(".log") && f.Name != "LOG")))
            return;
        sources.Add(new StorageSource(label, folder, files.Max(f => f.LastWriteTime), files.Sum(f => RealLength(f.FullName))));
    }

    // O tamanho da listagem fica desatualizado enquanto o navegador está com o arquivo aberto.
    private static long RealLength(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<string> SafeDirectories(string parent, string pattern)
    {
        try
        {
            return Directory.Exists(parent) ? Directory.GetDirectories(parent, pattern) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static Dictionary<string, string> ChromeProfileNames(string userData)
    {
        var names = new Dictionary<string, string>();
        try
        {
            var cache = JsonNode.Parse(File.ReadAllText(Path.Combine(userData, "Local State")))?["profile"]?["info_cache"] as JsonObject;
            foreach (var (dir, info) in cache ?? new JsonObject())
                if (info?["name"]?.GetValue<string>() is string name)
                    names[dir] = name;
        }
        catch
        {
            // Sem os nomes, mostra o nome da pasta (Default, Profile 1...).
        }
        return names;
    }

    /// <summary>Nome de cada conta do IdleDeck pelo id do slot, procurando no config.json dele.</summary>
    private static Dictionary<string, string> IdleDeckSlotNames(string configPath)
    {
        var names = new Dictionary<string, string>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["id"] is JsonValue id && id.TryGetValue<string>(out var slotId)
                        && obj["name"] is JsonValue name && name.TryGetValue<string>(out var slotName)
                        && obj.ContainsKey("url"))
                        names[slotId] = slotName;
                    foreach (var (_, child) in obj)
                        Walk(child);
                    break;
                case JsonArray array:
                    foreach (var child in array)
                        Walk(child);
                    break;
            }
        }
        try
        {
            Walk(JsonNode.Parse(File.ReadAllText(configPath)));
        }
        catch
        {
            // Sem o config, mostra o começo do id do slot.
        }
        return names;
    }

    private static string QueuePath(string root) => Path.Combine(root, "imports.json");

    private static List<PendingImport> Load(string root)
    {
        try
        {
            return JsonSerializer.Deserialize<List<PendingImport>>(File.ReadAllText(QueuePath(root))) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void Save(string root, List<PendingImport> pending)
    {
        var path = QueuePath(root);
        if (pending.Count == 0)
        {
            File.Delete(path);
            return;
        }
        Directory.CreateDirectory(root);
        File.WriteAllText(path, JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));
    }
}