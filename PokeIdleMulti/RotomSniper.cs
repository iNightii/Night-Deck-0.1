using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>
/// Liga e desliga o Rotom Sniper (a extensão do mercado) de uma conta pelo deck. A chave fica no
/// chrome.storage da extensão; o deck grava ali de dentro do content script dela na página do jogo
/// (pelo protocolo do DevTools), e o Rotom aplica na hora, como se fosse pelo cockpit.
/// </summary>
internal static class RotomSniper
{
    /// <summary>O id da loja (fixo pela chave do manifest).</summary>
    public const string Id = "olccbpdjdkbfgondiiaicmlifhbcggmk";

    private const string Key = "rotom_sniper_enabled";

    // Runtime.enable/disable de duas chamadas ao mesmo tempo se atropelariam.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Ligado ou desligado; null se o Rotom não está carregado na página (recarregar a conta resolve).</summary>
    public static async Task<bool?> GetEnabledAsync(CoreWebView2 core, string extensionId = Id)
    {
        // Como o Rotom lê: nunca mexido conta como ligado.
        var value = await EvalAsync(core, extensionId,
            $"new Promise(r => chrome.storage.local.get('{Key}', v => r(v['{Key}'] === undefined ? true : Boolean(v['{Key}']))))");
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public static async Task<bool> SetEnabledAsync(CoreWebView2 core, bool enabled, string extensionId = Id)
    {
        var value = await EvalAsync(core, extensionId,
            $"new Promise(r => chrome.storage.local.set({{ '{Key}': {(enabled ? "true" : "false")} }}, () => r(!chrome.runtime.lastError)))");
        return value?.ValueKind == JsonValueKind.True;
    }

    /// <summary>Roda <paramref name="expression"/> no mundo isolado da extensão na página, onde o content script tem chrome.storage.</summary>
    private static async Task<JsonElement?> EvalAsync(CoreWebView2 core, string extensionId, string expression)
    {
        await Gate.WaitAsync();
        var contexts = new List<int>();
        void OnCreated(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var context = doc.RootElement.GetProperty("context");
            if (context.TryGetProperty("origin", out var origin) && origin.GetString() == $"chrome-extension://{extensionId}")
                contexts.Add(context.GetProperty("id").GetInt32());
        }
        CoreWebView2DevToolsProtocolEventReceiver? receiver = null;
        try
        {
            receiver = core.GetDevToolsProtocolEventReceiver("Runtime.executionContextCreated");
            receiver.DevToolsProtocolEventReceived += OnCreated;
            // Ao ligar, o DevTools conta todos os contextos que já existem na página.
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            for (int i = 0; i < 20 && contexts.Count == 0; i++)
                await Task.Delay(50);
            if (contexts.Count == 0)
                return null;
            var json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            {
                expression,
                contextId = contexts[0],
                awaitPromise = true,
                returnByValue = true,
            }));
            using var result = JsonDocument.Parse(json);
            return result.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var value) ? value.Clone() : null;
        }
        catch
        {
            // Página recarregando ou fechando: fica como "não carregado".
            return null;
        }
        finally
        {
            if (receiver is not null)
                receiver.DevToolsProtocolEventReceived -= OnCreated;
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Runtime.disable", "{}");
            }
            catch
            {
                // A página já foi embora.
            }
            Gate.Release();
        }
    }
}