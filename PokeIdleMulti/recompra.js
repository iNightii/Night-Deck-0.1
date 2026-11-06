// A recompra do deck: confere o estoque da conta (a pokebola escolhida, os revives e as poções) e compra
// no Market do NPC o que ficou abaixo do pedido, pelo mesmo pedido do botão Comprar do jogo (`shop.buy`).
//
// O jogo guarda o estado da conta dentro do módulo dele, fora do alcance da página. Então este script
// acompanha o mesmo socket: pega o socket do jogo quando ele nasce, lê o `welcome` (o quadro inteiro) e
// os `estado` (só as chaves que mudaram, remontadas como no `shared/estado-delta.mjs` do jogo), e compra
// por ele. O deck só configura (`__deckRecompra.configure`) e lê o resumo (`__deckRecompra.summary`).
(() => {
  if (window.top !== window || window.__deckRecompra) return;

  const GAP = 4000; // uma compra a cada 4 s
  const MAX_QTY = 9999; // o teto do jogo por pedido (`maxCompraPorOuro`)
  const TIMEOUT = 15000; // compra sem resposta do jogo depois disso conta como falha
  const GOLDEN_POTION = 70070; // a única que cura por % do HP: fica de fora (nem conta, nem compra)
  const KEYS = ['gold', 'balls', 'items', 'automation', 'activeId', 'nick'];
  const WANTED = new Set(['welcome', 'estado', 'batalha', 'erro']);

  let socket = null;
  let account = null; // as chaves de KEYS da conta, remontadas dos deltas
  let maxHp = new Map(); // id do pokemon → HP máximo (o custo-benefício das poções é pelo ativo)
  let ballCatalog = []; // catalogoBolas do welcome
  let ours = []; // itensNossos do welcome: os itens do jogo que não estão no espelho
  let mirror = null; // poções e revives do /assets/items.json
  let mirrorAt = 0;
  const paid = new Map(); // item → preço por unidade cobrado na última compra (vale mais que o catálogo)
  let config = null;
  const log = [];
  const once = new Set();
  let pending = null; // a compra no ar
  let nextAt = 0;
  let pauseUntil = 0;
  let failures = 0;
  let queueAt = 0;
  const filling = { balls: false, revives: false, potions: false };

  const fmt = (n) => Math.round(n).toLocaleString('pt-BR');
  const note = (text) => {
    log.push(`${new Date().toLocaleTimeString('pt-BR')} ${text}`);
    if (log.length > 40) log.shift();
  };
  const noteOnce = (key, text) => {
    if (once.has(key)) return;
    once.add(key);
    note(text);
  };

  // O socket do jogo é o que abre para o próprio site (`wss://pokeidle.io`). O Proxy mantém o resto igual
  // para quem vier depois (a classe, o prototype, o instanceof): o Rotom, por exemplo, também olha esse socket.
  const Native = window.WebSocket;
  if (typeof Native === 'function') {
    window.WebSocket = new Proxy(Native, {
      construct(target, args, newTarget) {
        const ws = Reflect.construct(target, args, newTarget);
        try {
          if (new URL(String(args[0]), location.href).host === location.host) attach(ws);
        } catch { }
        return ws;
      },
    });
  }

  function attach(ws) {
    socket = ws;
    account = null;
    pending = null;
    ws.addEventListener('message', (e) => {
      if (ws === socket) receive(e.data);
    });
  }

  function receive(data) {
    if (typeof data !== 'string') return;
    // A batalha chega o tempo todo: só vale ler a que traz uma compra.
    const head = /^\{"t":"([^"]*)"/.exec(data);
    if (head && (!WANTED.has(head[1]) || (head[1] === 'batalha' && !data.includes('"k":"compra"')))) return;
    let m;
    try {
      m = JSON.parse(data);
    } catch {
      return;
    }
    if (m.t === 'welcome') {
      ballCatalog = Array.isArray(m.catalogoBolas) ? m.catalogoBolas : [];
      ours = Array.isArray(m.itensNossos) ? m.itensNossos : [];
      merge(m.estado, true);
      loadMirror();
    } else if (m.t === 'estado') {
      merge(m.estado, false);
    } else if (m.t === 'batalha') {
      for (const e of m.ev ?? []) if (e?.k === 'compra') bought(e);
    } else if (m.t === 'erro') {
      refused(m);
    }
  }

  // Chave ausente é "não mudou"; `cheio` (welcome, reconexão) recomeça do zero. A coleção vem por
  // `pokemons` (inteira), `pkMud` e `pkFora`.
  function merge(state, full) {
    if (!state || typeof state !== 'object') return;
    if (full || state.cheio || !account) {
      account = {};
      maxHp = new Map();
    }
    for (const key of KEYS) if (key in state) account[key] = state[key];
    if (Array.isArray(state.pokemons)) maxHp = new Map(state.pokemons.map((p) => [p.id, p.maxHp]));
    for (const p of state.pkMud ?? []) maxHp.set(p.id, p.maxHp);
    for (const id of state.pkFora ?? []) maxHp.delete(id);
  }

  function loadMirror() {
    if (mirror || Date.now() < mirrorAt) return;
    mirrorAt = Date.now() + 30000;
    fetch('/assets/items.json', { cache: 'no-store' })
      .then((r) => r.json())
      .then((j) => {
        mirror = (j.items ?? []).filter((i) => i.category === 'heal' || i.category === 'revive');
      })
      .catch(() => noteOnce('itens', 'não consegui ler a lista de itens do jogo; tento de novo em 30 s'));
  }

  /** As poções (heal) ou os revives que o NPC vende, do mais barato ao mais caro. */
  function shop(category) {
    const items = new Map();
    for (const i of [...(mirror ?? []), ...ours]) {
      if (i?.category !== category || !(i.npcPrice > 0) || i.id === GOLDEN_POTION) continue;
      items.set(i.id, {
        id: i.id, name: i.name, price: paid.get(i.id) ?? i.npcPrice,
        heal: Number(i.healAmount) || 0, healPct: Number(i.healPct) || 0,
      });
    }
    return [...items.values()].sort((a, b) => a.price - b.price);
  }

  const have = (item) => Number(account?.items?.[item.id]) || 0;
  const total = (items) => items.reduce((sum, item) => sum + have(item), 0);

  /**
   * O custo-benefício: ouro por HP que a poção cura DE VERDADE no pokemon ativo. A automação do jogo usa a
   * poção quando a vida cai abaixo do limiar (30% de fábrica), então falta pelo menos (100% − limiar) do HP
   * máximo; o que a poção curar além disso pode se perder.
   */
  function rate(potions) {
    const hp = maxHp.get(account?.activeId);
    if (!(hp > 0)) return null;
    const threshold = Math.min(1, Math.max(0.1, Number(account.automation?.hpLimiar) || 0.3));
    const room = Math.max(0.1, 1 - threshold) * hp;
    for (const p of potions) {
      p.useful = Math.min(p.healPct > 0 ? p.healPct * hp : p.heal, room);
      p.cost = p.useful > 0 ? p.price / p.useful : Infinity;
    }
    return { hp, threshold };
  }

  function pickPotion(potions) {
    if (config?.potions?.id > 0) return potions.find((p) => p.id === config.potions.id) ?? null;
    if (!rate(potions)) return null;
    return potions.filter((p) => p.cost < Infinity).sort((a, b) => a.cost - b.cost || b.useful - a.useful)[0] ?? null;
  }

  /** O que comprar de um tipo: abaixo de `below` começa, e compra até `keep` (em várias compras, se for o caso). */
  function plan(kind, rule, count, item) {
    if (!rule?.on) {
      filling[kind] = false;
      return null;
    }
    const target = Math.max(rule.keep, rule.below);
    if (count >= target) {
      filling[kind] = false;
      return null;
    }
    if (!filling[kind] && count >= rule.below) return null;
    filling[kind] = true;
    if (!item || !(item.price > 0)) return null;
    const floor = config.goldFloor || 0;
    const qty = Math.min(MAX_QTY, target - count, Math.floor(((Number(account.gold) || 0) - floor) / item.price));
    if (qty < 1) {
      noteOnce(`ouro:${kind}`, `${item.name}: o ouro está no mínimo (${fmt(floor)}); compra quando passar de ${fmt(floor + item.price)}`);
      return null;
    }
    once.delete(`ouro:${kind}`);
    return { kind, item, qty, count };
  }

  function tick() {
    if (!config?.on || !account || socket?.readyState !== 1) return;
    const now = Date.now();
    if (!mirror) {
      loadMirror();
      return;
    }
    if (pending) {
      // Sem o aviso da compra, o estoque que subiu também confirma.
      const count = pending.kind === 'balls' ? Number(account.balls?.[pending.item.id]) || 0 : have(pending.item);
      if (count - pending.count >= pending.qty / 2) done(`comprou ${pending.item.name} ×${fmt(pending.qty)}`);
      else if (now - pending.at < TIMEOUT) return;
      else failed(`${pending.item.name}: o jogo não respondeu à compra`);
    }
    if (now < nextAt || now < pauseUntil) return;

    const potions = shop('heal');
    const revives = shop('revive');
    const potion = pickPotion(potions);
    fixQueue(potion, potions, now);
    const b = ballCatalog.find((x) => x.id === config.balls?.id && x.compravel && x.priceGold > 0);
    const ball = b && { id: b.id, name: b.nome, price: paid.get(`bola${b.id}`) ?? b.priceGold };
    // Primeiro o que mantém o pokemon de pé, se o ouro não der para tudo.
    const todo = plan('potions', config.potions, total(potions), potion)
      ?? plan('revives', config.revives, total(revives), revives[0])
      ?? plan('balls', config.balls, Number(account.balls?.[config.balls?.id]) || 0, ball);
    if (!todo) return;
    pending = { ...todo, at: now };
    nextAt = now + GAP;
    socket.send(JSON.stringify({ t: 'shop.buy', kind: todo.kind === 'balls' ? 'ball' : 'item', id: todo.item.id, qty: todo.qty }));
  }

  /** A poção escolhida em 1º na fila do "Usar Poções" (a ordem da fila é a ordem de gasto). */
  function fixQueue(potion, potions, now) {
    const auto = account.automation;
    if (!config.potions?.on || !config.potionFirst || !potion || !auto || typeof auto !== 'object' || now < queueAt) return;
    const queue = Array.isArray(auto.potionIds) ? auto.potionIds : [];
    if (queue[0] === potion.id) return;
    queueAt = now + 30000;
    // Fila vazia é "qualquer uma, da mais cara para a mais barata": as outras continuam valendo, nessa ordem.
    const rest = queue.length ? queue.filter((id) => id !== potion.id)
      : potions.filter((p) => p.id !== potion.id).sort((a, b) => b.price - a.price).map((p) => p.id);
    socket.send(JSON.stringify({ t: 'auto.set', ...auto, potionIds: [potion.id, ...rest] }));
    note(`${potion.name} em 1º na fila do "Usar Poções"`);
  }

  function bought(e) {
    if (!pending || Number(e.qtd) !== pending.qty) return; // compra feita na mão: o próprio jogo avisa
    const spent = Number(e.gasto);
    if (spent > 0) paid.set(pending.kind === 'balls' ? `bola${pending.item.id}` : pending.item.id, spent / pending.qty);
    done(`comprou ${pending.item.name} ×${fmt(pending.qty)}${spent > 0 ? ` (−${fmt(spent)} ouro)` : ''}`);
  }

  function refused(m) {
    if (!pending || Date.now() - pending.at > 5000) return;
    failed(`${pending.item.name}: o jogo recusou a compra (${m.msg || m.chave || 'sem motivo'})`);
    nextAt = Date.now() + 30000;
  }

  function done(text) {
    note(text);
    pending = null;
    failures = 0;
  }

  function failed(text) {
    note(text);
    pending = null;
    if (++failures >= 3) {
      failures = 0;
      pauseUntil = Date.now() + 120000;
      note('3 compras seguidas sem dar certo: pausa de 2 min');
    }
  }

  // Relido campo a campo: número que não é número vira 0 (e 0 em "manter" e "abaixo de" não compra nada).
  const rule = (r) => ({
    on: !!r?.on, id: Math.trunc(Number(r?.id) || 0),
    keep: Math.max(0, Math.trunc(Number(r?.keep) || 0)), below: Math.max(0, Math.trunc(Number(r?.below) || 0)),
  });

  function configure(c) {
    if (!c || typeof c !== 'object') return false;
    if (!!c.on !== !!config?.on) note(c.on ? 'recompra ligada' : 'recompra desligada');
    config = {
      on: !!c.on, balls: rule(c.balls), revives: rule(c.revives), potions: rule(c.potions),
      potionFirst: !!c.potionFirst, goldFloor: Math.max(0, Number(c.goldFloor) || 0),
    };
    for (const kind of Object.keys(filling)) if (!config[kind].on) filling[kind] = false;
    once.clear();
    pauseUntil = 0;
    failures = 0;
    return true;
  }

  /** O que o deck mostra: o estoque, o ouro, a poção escolhida e o registro. */
  function summary() {
    const s = { configured: !!config, on: !!config?.on, connected: socket?.readyState === 1 && !!account, log: log.slice(-30) };
    if (!account) return s;
    const potions = shop('heal');
    const revives = shop('revive');
    const fit = rate(potions);
    const potion = pickPotion(potions);
    return Object.assign(s, {
      nick: account.nick ?? null,
      gold: Number(account.gold) || 0,
      balls: ballCatalog.filter((b) => b.compravel && b.priceGold > 0)
        .map((b) => ({ id: b.id, name: b.nome, price: paid.get(`bola${b.id}`) ?? b.priceGold, have: Number(account.balls?.[b.id]) || 0 })),
      revives: revives.map((r) => ({ id: r.id, name: r.name, price: r.price, have: have(r) })),
      potions: potions.map((p) => ({
        id: p.id, name: p.name, price: p.price, have: have(p), heal: p.healPct > 0 && fit ? Math.round(p.healPct * fit.hp) : p.heal,
        cost: fit && Number.isFinite(p.cost) ? p.cost : null,
      })),
      potionId: potion?.id ?? null,
      maxHp: fit?.hp ?? null,
      threshold: fit?.threshold ?? null,
      queueFirst: Array.isArray(account.automation?.potionIds) ? account.automation.potionIds[0] ?? null : null,
      paused: pauseUntil > Date.now(),
    });
  }

  Object.defineProperty(window, '__deckRecompra', { value: Object.freeze({ configure, summary }) });
  setInterval(tick, 1000);
})();