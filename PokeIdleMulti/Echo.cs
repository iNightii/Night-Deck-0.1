using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// Eco: um clique numa conta se repete nas outras. O que se repete é o botão, não a posição na tela:
/// a página da conta clicada conta qual elemento foi (o caminho dele na página e o texto dele), e nas
/// outras o deck clica o elemento no mesmo caminho com o mesmo texto, ou o único com esse texto. Se a
/// outra conta está numa tela sem esse botão, nada é clicado nela. Digitação não se repete: o chat do
/// jogo viraria a mesma mensagem mandada pelas quatro contas.
/// </summary>
internal static class Echo
{
    // Igual nos dois scripts: o texto que identifica o botão (o rótulo, senão o que está escrito nele).
    private const string TextOf =
        "const textOf = el => String(el.getAttribute('aria-label') || el.getAttribute('title') || el.innerText || el.value || '')" +
        ".trim().replace(/\\s+/g, ' ').slice(0, 80);";

    /// <summary>
    /// Roda em toda página das contas: cada clique de verdade vira uma mensagem para o deck. Os cliques
    /// que o próprio deck repete não são "de verdade" (isTrusted falso), então não voltam em eco.
    /// </summary>
    public const string CaptureScript = "if (window.top === window) {" + TextOf + """
          const clickable = 'button, a, input, select, textarea, label, summary, [role=button], [role=tab], [role=menuitem], [role=option], [onclick], [tabindex]';
          const pathOf = el => {
            const parts = [];
            for (; el && el !== document.body && el.parentElement; el = el.parentElement) {
              const same = [...el.parentElement.children].filter(c => c.tagName === el.tagName);
              parts.unshift(el.tagName.toLowerCase() + (same.length > 1 ? `:nth-of-type(${same.indexOf(el) + 1})` : ''));
            }
            return parts.length ? 'body > ' + parts.join(' > ') : 'body';
          };
          addEventListener('click', e => {
            if (!e.isTrusted || !(e.target instanceof Element)) return;
            const el = e.target.closest(clickable) || e.target;
            const r = el.getBoundingClientRect();
            chrome.webview.postMessage(JSON.stringify({
              eco: 'clique', path: pathOf(el), tag: el.tagName.toLowerCase(), text: textOf(el),
              fx: r.width ? (e.clientX - r.left) / r.width : 0.5, fy: r.height ? (e.clientY - r.top) / r.height : 0.5,
            }));
          }, true);
        }
        """;

    /// <summary>Um clique vindo de uma página (já conferido e reescrito pelo deck).</summary>
    public sealed record Click(string Path, string Tag, string Text, double Fx, double Fy);

    /// <summary>A mensagem da página, se for um clique do eco. Os campos são relidos um a um: a página não escolhe o script.</summary>
    public static Click? Parse(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("eco", out var kind) || kind.GetString() != "clique")
                return null;
            return new Click(root.GetProperty("path").GetString() ?? "", root.GetProperty("tag").GetString() ?? "",
                root.GetProperty("text").GetString() ?? "",
                Math.Clamp(root.GetProperty("fx").GetDouble(), 0, 1), Math.Clamp(root.GetProperty("fy").GetDouble(), 0, 1));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Script que repete o clique noutra conta; devolve true se achou o mesmo botão e clicou.</summary>
    public static string ReplayScript(Click click) => "((m) => {" + TextOf + """
          let el = null;
          try { el = document.querySelector(m.Path); } catch (e) { }
          if (el && textOf(el) !== m.Text) el = null;
          if (!el && m.Text) {
            let same = [];
            try { same = [...document.querySelectorAll(m.Tag)].filter(c => textOf(c) === m.Text); } catch (e) { }
            if (same.length === 1) el = same[0];
          }
          if (!el) return false;
          const r = el.getBoundingClientRect();
          const base = { bubbles: true, cancelable: true, composed: true, view: window, button: 0,
                         clientX: r.left + r.width * m.Fx, clientY: r.top + r.height * m.Fy };
          const pointer = { ...base, pointerId: 1, pointerType: 'mouse', isPrimary: true };
          el.dispatchEvent(new PointerEvent('pointerdown', { ...pointer, buttons: 1 }));
          el.dispatchEvent(new MouseEvent('mousedown', { ...base, buttons: 1 }));
          el.dispatchEvent(new PointerEvent('pointerup', pointer));
          el.dispatchEvent(new MouseEvent('mouseup', base));
          el.click();
          return true;
        })(
        """ + JsonSerializer.Serialize(click) + ")";
}