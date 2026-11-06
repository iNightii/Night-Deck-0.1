using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PokeIdleMulti;

internal enum PaneStatus { Starting, Loading, Ok, Failed }

/// <summary>
/// Uma conta dentro do deck: um cabeçalho fino (status, tempo rodando, extensões, botões) e a página
/// do jogo ocupando o resto, sem barra de navegador. Cada conta tem um perfil próprio do WebView2,
/// com cookies, login e extensões separados.
/// </summary>
internal sealed class AccountPane : UserControl
{
    // F11 dentro da página não chega na janela do deck; o script avisa por mensagem.
    private const string KeyScript =
        "addEventListener('keydown', e => { if (e.key === 'F11') { e.preventDefault(); chrome.webview.postMessage('F11'); } }, true);";

    private readonly ToolStrip _header = new ClickThroughToolStrip();
    private readonly ToolStripLabel _dot = new("●");
    private readonly ToolStripLabel _name = new();
    private readonly ToolStripLabel _info = new();
    private readonly ToolStripButton _focus = new(Theme.FocusGlyph) { Font = Theme.Symbols };
    private readonly ToolStripButton _reload = new(Theme.ReloadGlyph) { Font = Theme.Symbols };
    private readonly ToolStripButton _mute = new(Theme.SoundGlyph) { Font = Theme.Symbols };
    private readonly ToolStripButton _vote = new("🗳 Votar") { Visible = false };
    private readonly ToolStripDropDownButton _lives = new("lives") { Enabled = false };
    private readonly ToolStripButton _power = new(Theme.PowerGlyph) { Font = Theme.Symbols };
    private readonly ToolStripButton _restock = new(Theme.CartGlyph) { Font = Theme.Symbols };
    private readonly Label _offNote = new()
    {
        Dock = DockStyle.Fill,
        Visible = false,
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Theme.Back,
        ForeColor = Theme.Muted,
    };
    private readonly Font _voteBold;
    private readonly List<ToolStripItem> _extensionItems = new();
    private PaneStatus _status = PaneStatus.Starting;
    private string? _statusText = "abrindo…";
    private string? _note;
    private DateTime _noteUntil;
    // O que o deck disse do botão para cada site (o botão "lives" junta os dois).
    private (bool Enabled, bool Closed, string Tooltip) _kick = (false, false, "");
    private (bool Enabled, bool Closed, string Tooltip) _twitch = (false, false, "");
    private bool _loginScreen;
    private string _voteText = "🗳 Votar";
    private bool _voteShort;

    // A tela de login do jogo está à vista (a mesma conta que o jogo faz para desenhar o captcha).
    private const string LoginScript =
        "(() => { const p = document.querySelector('#portal'), l = document.querySelector('#login');" +
        " return !!p && !!l && !p.classList.contains('hidden') && !l.classList.contains('hidden'); })()";

    public AccountPane(int index)
    {
        Index = index;
        BackColor = Theme.Border;
        Padding = new Padding(1);

        Theme.Style(_header);
        _header.Dock = DockStyle.Top;
        _header.Padding = new Padding(6, 2, 4, 2);
        _header.CanOverflow = false;
        // Letra e ícones maiores que o padrão de 9 pt: com 4 contas na tela, os botões ficavam pequenos demais.
        _header.Font = new Font("Segoe UI", 10f);
        _header.ImageScalingSize = new Size(20, 20);
        _dot.ForeColor = Theme.Muted;
        _name.Font = new Font(_header.Font, FontStyle.Bold);
        _info.ForeColor = Theme.Muted;

        // Itens à direita entram da direita para a esquerda.
        _power.Alignment = _focus.Alignment = _reload.Alignment = _mute.Alignment = _lives.Alignment =
            _vote.Alignment = _restock.Alignment = ToolStripItemAlignment.Right;
        _restock.Click += (_, _) => RestockRequested?.Invoke(this);
        ShowRestock(null);
        _power.ToolTipText = "Desligar esta conta: o jogo, as extensões, as lives e o voto dela param (ela continua aqui no deck)";
        _power.Click += (_, _) => PowerRequested?.Invoke(this);
        _focus.ToolTipText = "Ampliar esta conta / voltar (duplo clique no cabeçalho faz o mesmo)";
        _reload.ToolTipText = "Recarregar a página";
        _mute.ToolTipText = "Ligar / desligar o som desta conta";
        _voteBold = new Font(_header.Font, FontStyle.Bold);
        _focus.Click += (_, _) => FocusRequested?.Invoke(this);
        _reload.Click += (_, _) => ReloadRequested?.Invoke(this);
        _mute.Click += (_, _) => SetMuted(!Muted);
        _vote.Click += (_, _) => VoteRequested?.Invoke(this);
        _lives.DropDownOpening += (_, _) => LivesMenuOpening?.Invoke(this, _lives.DropDownItems);
        _lives.DropDownItems.Add("…");
        _header.DoubleClick += (_, _) => FocusRequested?.Invoke(this);
        _header.SizeChanged += (_, _) => FitHeader();
        _header.Items.AddRange(new ToolStripItem[] { _dot, _name, _info, _power, _focus, _reload, _mute, _lives, _restock, _vote });
        foreach (var item in new ToolStripItem[] { _power, _focus, _reload, _mute, _lives, _restock, _vote })
            Roomy(item);

        _offNote.Font = new Font(Font.FontFamily, 11f);
        _offNote.DoubleClick += (_, _) => FocusRequested?.Invoke(this);
        Controls.Add(WebView);
        Controls.Add(_offNote);
        Controls.Add(_header);
    }

    /// <summary>Botão do cabeçalho com folga: área de clique maior e espaço entre um e outro (apertado, sem a folga).</summary>
    private static void Roomy(ToolStripItem item, bool tight = false)
    {
        item.Margin = tight ? new Padding(0, 1, 0, 1) : new Padding(2, 1, 2, 1);
        item.Padding = tight ? new Padding(1) : new Padding(5, 1, 5, 1);
    }

    public event Action<AccountPane>? FocusRequested;
    public event Action<AccountPane>? ReloadRequested;
    public event Action<AccountPane>? FullscreenRequested;
    public event Action<AccountPane>? SettingsChanged;
    public event Action<AccountPane, ExtensionInfo, Point>? PopupRequested;
    public event Action<AccountPane>? VoteRequested;

    /// <summary>Um clique de verdade na página (para o eco repetir nas outras contas).</summary>
    public event Action<AccountPane, Echo.Click>? Clicked;

    /// <summary>O botão ⏻: ligar ou desligar a conta.</summary>
    public event Action<AccountPane>? PowerRequested;

    /// <summary>O carrinho do cabeçalho: abrir a recompra desta conta.</summary>
    public event Action<AccountPane>? RestockRequested;

    /// <summary>O menu do botão lives abrindo: o deck põe nele as lives da Kick e da Twitch para fechar ou abrir de novo.</summary>
    public event Action<AccountPane, ToolStripItemCollection>? LivesMenuOpening;

    /// <summary>Desligada: fica no deck, mas sem o jogo (nem as extensões, que só rodam nele), lives ou voto.</summary>
    public bool Off { get; private set; }

    /// <summary>A página do jogo abriu um link do TopIdle (o "votar" de dentro do jogo).</summary>
    public event Action<AccountPane, string>? TopIdleLinkRequested;

    public int Index { get; }
    public string ProfileName => $"conta{Index + 1}";
    public string AccountName { get; set; } = "";
    public WebView2 WebView { get; } = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Back };
    public CoreWebView2? Core => WebView.CoreWebView2;
    public CoreWebView2Profile? Profile => WebView.CoreWebView2?.Profile;
    public PaneStatus Status => _status;
    public DateTime? StartedAt { get; private set; }
    public bool Muted => Core?.IsMuted ?? false;
    public int ExtensionButtonCount => _extensionItems.Count;
    public string? VoteText => _vote.Visible ? _voteText : null;

    /// <summary>Cria o navegador da conta e abre o jogo; <paramref name="prepare"/> roda antes de abrir (o conserto das extensões).</summary>
    public async Task InitializeAsync(CoreWebView2Environment env, string url, double zoom, bool muted, Action<CoreWebView2>? setup,
        Func<CoreWebView2, Task>? prepare = null)
    {
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName;
        _ = WebView.Handle;
        await WebView.EnsureCoreWebView2Async(env, options);

        var core = WebView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.IsMuted = muted;
        UpdateMuteButton();
        WebView.ZoomFactor = zoom;
        WebView.ZoomFactorChanged += (_, _) => SettingsChanged?.Invoke(this);

        core.NavigationStarting += (_, e) =>
        {
            if (e.IsRedirected || Off)
                return;
            SetStatus(PaneStatus.Loading, "carregando…");
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (Off)
                return;
            if (e.IsSuccess)
            {
                StartedAt ??= DateTime.Now;
                SetStatus(PaneStatus.Ok, null);
                // O ULTRA do deck vale para todas: a conta que recarrega volta a ele, mesmo se tinha saído no
                // "Voltar ao jogo" da caixa.
                if (Ultra)
                    _ = SetUltraAsync(true);
                // A página nova (inclusive a que o próprio jogo recarrega numa versão nova) começa sem configuração.
                _ = PushRestockAsync();
            }
            else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                SetStatus(PaneStatus.Failed, "não carregou: sem internet ou o site caiu");
            }
        };
        core.ProcessFailed += (_, e) => OnProcessFailed(e);
        core.NewWindowRequested += (_, e) =>
        {
            if (TopIdleLinkRequested is not null && TopIdle.IsTopIdleLink(e.Uri))
            {
                // O voto de dentro do jogo abre na janela de voto do deck, que percebe quando o voto entra.
                e.Handled = true;
                TopIdleLinkRequested(this, e.Uri);
                return;
            }
            PopupWindow.HandleNewWindow(env, ProfileName, FindForm(), e);
        };
        core.WebMessageReceived += (_, e) =>
        {
            string? message;
            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return;
            }
            if (message == "F11")
                FullscreenRequested?.Invoke(this);
            else if (message is not null && Echo.Parse(message) is Echo.Click click)
                Clicked?.Invoke(this, click);
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(KeyScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Echo.CaptureScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Restock.Script);
        await ApplyReduceMotionAsync();

        setup?.Invoke(core);
        if (prepare is not null)
            await prepare(core);
        core.Navigate(url);
    }

    public void Reload()
    {
        StartedAt = null;
        Core?.Reload();
    }

    /// <summary>Menos animações (lido ao criar o navegador da conta; mudou depois, chame <see cref="ApplyReduceMotionAsync"/>).</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>
    /// A página passa a ver o "reduzir movimento" do sistema ligado (ou desligado), e o próprio jogo
    /// desliga ~100 grupos de animação que rodam sem parar: selos pulsando, halos, cartas, o portal do
    /// login. Medido na tela de login: metade da CPU da página e da placa de vídeo. Vale até fechar o deck,
    /// inclusive depois de recarregar.
    /// </summary>
    public async Task ApplyReduceMotionAsync()
    {
        if (Core is null)
            return;
        var features = ReduceMotion ? "[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]" : "[]";
        try
        {
            await Core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", $"{{\"features\":{features}}}");
        }
        catch
        {
            // A página está fechando: na próxima abertura ela volta a ler a escolha.
        }
    }

    // Modo ULTRA Economia do jogo ("ULTRA Battery Saver"): a escolha fica no localStorage ("cfg-ultra"),
    // e o jogo acende a classe "modo-ultra" no <html> quando entra com ela ligada.
    // ligado: na tela; escolhido: liga quando entrar no jogo (ou quando a conta tiver equipe); normal; fora.
    private const string UltraStateScript =
        "(() => document.documentElement.classList.contains('modo-ultra') ? 'ligado'" +
        " : localStorage.getItem('cfg-ultra') === '1' ? 'escolhido'" +
        " : document.querySelector('#app:not(.hidden)') ? 'normal' : 'fora')()";

    // Liga pelo mesmo caminho do jogador (Configurações → interruptor do ULTRA, que fecha a janela sozinho)
    // e desliga pelo "Voltar ao jogo" da caixa. Fora do jogo, ou com a conta ainda sem equipe, só fica a
    // escolha: o próprio jogo liga ao entrar.
    private const string SetUltraScript =
        "(on => { const ultra = () => document.documentElement.classList.contains('modo-ultra');" +
        " if (!on) { document.querySelector('#ultra-sair')?.click(); localStorage.setItem('cfg-ultra', '0'); return !ultra(); }" +
        " if (ultra()) return true;" +
        " const app = document.querySelector('#app');" +
        " if (!app || app.classList.contains('hidden') || localStorage.getItem('cfg-ultra') === '1') { localStorage.setItem('cfg-ultra', '1'); return true; }" +
        " document.querySelector('#btn-config')?.click(); document.querySelector('#cfg-ultra')?.click(); return ultra(); })";

    /// <summary>O ULTRA do deck ligado: a cada carregamento do jogo a conta volta a ele.</summary>
    public bool Ultra { get; set; }

    /// <summary>A página é o jogo (deck.test: o jogo do autoteste).</summary>
    private bool OnGame => Core is not null && Uri.TryCreate(Core.Source, UriKind.Absolute, out var uri)
        && uri.Host is "pokeidle.io" or "deck.test";

    /// <summary>"ligado", "escolhido", "normal" ou "fora"; null se a página não é o jogo ou não respondeu.</summary>
    public async Task<string?> GetUltraAsync()
    {
        if (Off || !OnGame)
            return null;
        try
        {
            return JsonSerializer.Deserialize<string>(await WebView.ExecuteScriptAsync(UltraStateScript));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Liga ou desliga o Modo ULTRA Economia do jogo nesta conta, na hora (sem recarregar).</summary>
    public async Task<bool> SetUltraAsync(bool on)
    {
        if (Off || !OnGame)
            return false;
        try
        {
            return await WebView.ExecuteScriptAsync($"{SetUltraScript}({(on ? "true" : "false")})") == "true";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A recompra desta conta (a mesma instância do deck.json: a janela da recompra muda ela).</summary>
    public RestockConfig RestockConfig { get; set; } = new();

    /// <summary>Manda a configuração da recompra para a página (o script dela já está lá desde o carregamento).</summary>
    public async Task PushRestockAsync()
    {
        if (Off || !OnGame)
            return;
        try
        {
            await WebView.ExecuteScriptAsync(Restock.ConfigureScript(RestockConfig));
        }
        catch
        {
            // A página está fechando: a próxima chega configurada no carregamento.
        }
    }

    /// <summary>O estoque, o ouro e o registro da recompra; null se a página não é o jogo ou não respondeu.</summary>
    public async Task<RestockStatus?> ReadRestockAsync()
    {
        if (Off || !OnGame)
            return null;
        RestockStatus? status;
        try
        {
            status = Restock.Parse(await WebView.ExecuteScriptAsync(Restock.SummaryScript));
        }
        catch
        {
            return null;
        }
        // Página que perdeu a configuração (carregou sem o aviso de pronta): manda de novo.
        if (status is { Configured: false })
            await PushRestockAsync();
        return status;
    }

    /// <summary>
    /// O carrinho do cabeçalho: verde com a recompra ligada e o jogo conectado, amarelo ligada esperando o jogo
    /// (ou em pausa), apagado desligada. A dica tem o estoque e a última linha do registro.
    /// </summary>
    public void ShowRestock(RestockStatus? status)
    {
        bool on = RestockConfig.On;
        bool working = on && status is { Connected: true, Paused: false };
        _restock.ForeColor = !on ? Theme.Muted : working ? Theme.Ok : Theme.Warn;
        RestockState = !on ? "desligada" : working ? "ligada" : "esperando";
        var lines = new List<string>
        {
            !on ? "Recompra desligada: clique para configurar e ligar (pokebolas, revives e poções no Market do NPC)"
            : status is null or { Connected: false } ? "Recompra ligada: esperando o jogo conectar"
            : status.Paused ? "Recompra ligada, em pausa (veja o registro)"
            : "Recompra ligada",
        };
        if (status is { Connected: true })
        {
            var ball = status.Balls?.FirstOrDefault(b => b.Id == RestockConfig.Balls.Id);
            lines.Add($"Ouro {Restock.N(status.Gold)} · {ball?.Name ?? "pokebola"} {Restock.N(ball?.Have ?? 0)} · " +
                      $"revives {Restock.N(status.ReviveHave)} · poções {Restock.N(status.PotionHave)}");
        }
        if (status?.Log is { Count: > 0 } log)
            lines.Add(log[^1]);
        _restock.ToolTipText = string.Join("\n", lines);
    }

    /// <summary>"desligada", "ligada" ou "esperando" (a cor do carrinho), para o autoteste.</summary>
    public string RestockState { get; private set; } = "desligada";

    /// <summary>
    /// Confere se a conta está parada na tela de login. Ali o jogo mantém o captcha do Cloudflare, que
    /// refaz o desafio sozinho e vai juntando memória (medido: +60 MB a cada 30 s no começo).
    /// </summary>
    public async Task CheckLoginScreenAsync()
    {
        bool login = false;
        if (Core is not null && !Off && _status == PaneStatus.Ok)
        {
            try
            {
                login = await WebView.ExecuteScriptAsync(LoginScript) == "true";
            }
            catch
            {
                return;
            }
        }
        if (login == _loginScreen)
            return;
        _loginScreen = login;
        _info.ToolTipText = login
            ? "Na tela de login o jogo mantém o captcha do Cloudflare, que refaz o desafio sozinho e vai juntando " +
              "memória (centenas de MB em poucas horas). Entre na conta ou desligue-a no botão de desligar, no canto direito do cabeçalho."
            : null;
        RefreshHeader();
    }

    /// <summary>O texto ao lado do nome no cabeçalho, para o autoteste.</summary>
    public string HeaderInfo => _info.Text ?? "";

    public void SetMuted(bool muted)
    {
        if (Core is null)
            return;
        Core.IsMuted = muted;
        UpdateMuteButton();
        SettingsChanged?.Invoke(this);
    }

    /// <summary>O nick salvo pelo jogo nesta conta, para a recompensa do voto ir para o personagem certo.</summary>
    public async Task<string?> ReadNickAsync()
    {
        if (Core is null || !Core.Source.StartsWith("https://pokeidle.io", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return TopIdle.ParseNick(await WebView.ExecuteScriptAsync("localStorage.getItem('nick')"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Botão de voto do cabeçalho: laranja quando o voto está liberado, com o tempo que falta quando não.</summary>
    public void SetVote(string text, Color color, bool highlight, string tooltip)
    {
        if (Off)
        {
            HideVote();
            return;
        }
        _voteText = text;
        _vote.Text = _voteShort ? VoteIcon(text) : text;
        FitHeader();
        _vote.ForeColor = color;
        _vote.Font = highlight ? _voteBold : _header.Font;
        _vote.ToolTipText = tooltip;
        _vote.Visible = true;
    }

    public void HideVote()
    {
        _vote.Visible = false;
        FitHeader();
    }

    /// <summary>Só o 🗳 do texto do voto (ele vem antes de tudo e ocupa dois chars).</summary>
    private static string VoteIcon(string text) => text.Length > 2 ? text[..2] : text;

    /// <summary>
    /// A parte da Kick do botão lives do cabeçalho: sem o que fechar (o motivo fica na dica), ou com live da Kick
    /// fechada nele. O botão fica apagado quando nenhum dos dois sites tem o que fechar, e marcado quando algum tem
    /// live fechada.
    /// </summary>
    public void SetKick(bool enabled, bool closed, string tooltip)
    {
        _kick = (enabled, closed, tooltip);
        ShowLives();
    }

    /// <summary>A parte da Twitch do botão lives do cabeçalho (a mesma da Kick).</summary>
    public void SetTwitch(bool enabled, bool closed, string tooltip)
    {
        _twitch = (enabled, closed, tooltip);
        ShowLives();
    }

    private void ShowLives()
    {
        _lives.Enabled = _kick.Enabled || _twitch.Enabled;
        _lives.ForeColor = _kick.Closed || _twitch.Closed ? Theme.Accent : Theme.Text;
        _lives.ToolTipText = $"Kick: {_kick.Tooltip}\nTwitch: {_twitch.Tooltip}";
    }

    /// <summary>A parte de cada site do botão lives ("ligado", "apagado" ou "fechada"), para o autoteste.</summary>
    public string KickState => _kick.Closed ? "fechada" : _kick.Enabled ? "ligado" : "apagado";

    public string TwitchState => _twitch.Closed ? "fechada" : _twitch.Enabled ? "ligado" : "apagado";

    public string? NoteText => DateTime.Now < _noteUntil ? _note : null;

    /// <summary>
    /// Desliga (a página do jogo vira em branco e o aviso toma o lugar dela) ou religa a conta. Religar só
    /// arruma o painel; quem abre o jogo de novo é o deck.
    /// </summary>
    public void SetOff(bool off)
    {
        Off = off;
        _offNote.Text = $"{AccountName} desligada\n\nNada roda nela: jogo, extensões, lives e voto ficam parados.\n" +
                        "Clique no botão de ligar, no canto direito do cabeçalho, para ligar.";
        _offNote.Visible = off;
        WebView.Visible = !off;
        _reload.Visible = _mute.Visible = _lives.Visible = _restock.Visible = !off;
        foreach (var item in _extensionItems)
            item.Visible = !off;
        _power.ToolTipText = off
            ? "Ligar esta conta: o jogo abre de novo, com as extensões e as lives dela"
            : "Desligar esta conta: o jogo, as extensões, as lives e o voto dela param (ela continua aqui no deck)";
        _power.ForeColor = off ? Theme.Accent : Theme.Text;
        if (off)
        {
            HideVote();
            StartedAt = null;
            SetStatus(PaneStatus.Starting, "desligada");
            Core?.Navigate("about:blank");
        }
        else
        {
            SetStatus(PaneStatus.Starting, "ligando…");
        }
    }

    /// <summary>O texto de status do cabeçalho ("desligada", "carregando…"), para o autoteste.</summary>
    public string? StatusText => _statusText;

    /// <summary>Um aviso rápido no cabeçalho (some sozinho em alguns segundos).</summary>
    public void Note(string text)
    {
        _note = text;
        _noteUntil = DateTime.Now.AddSeconds(4);
        RefreshHeader();
    }

    /// <summary>Um botão no cabeçalho para cada extensão desta conta que tem popup.</summary>
    public void SetExtensions(IEnumerable<ExtensionInfo> extensions)
    {
        foreach (var item in _extensionItems)
        {
            _header.Items.Remove(item);
            item.Dispose();
        }
        _extensionItems.Clear();

        foreach (var extension in extensions)
        {
            var button = new ToolStripButton
            {
                Alignment = ToolStripItemAlignment.Right,
                ToolTipText = extension.Name,
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Visible = !Off,
            };
            var icon = LoadIcon(extension);
            if (icon is null)
            {
                button.DisplayStyle = ToolStripItemDisplayStyle.Text;
                button.Text = "🧩";
            }
            else
            {
                button.Image = icon;
            }
            Roomy(button);
            button.Click += (_, _) =>
            {
                var bounds = button.Bounds;
                PopupRequested?.Invoke(this, extension, _header.PointToScreen(new Point(bounds.Right, bounds.Bottom)));
            };
            _extensionItems.Add(button);
            _header.Items.Add(button);
        }
        FitHeader();
    }

    /// <summary>Atualiza nome, bolinha de status e o tempo rodando (chamado a cada segundo).</summary>
    public void RefreshHeader()
    {
        _name.Text = AccountName;
        _dot.ForeColor = _status switch
        {
            PaneStatus.Ok => Theme.Ok,
            PaneStatus.Loading => Theme.Warn,
            PaneStatus.Failed => Theme.Bad,
            _ => Theme.Muted,
        };
        var info = DateTime.Now < _noteUntil ? "· " + _note
            : _statusText is not null ? "· " + _statusText
            : _loginScreen ? "· na tela de login: entre ou desligue a conta"
            : StartedAt is DateTime started ? "· rodando há " + FormatUptime(DateTime.Now - started)
            : "";
        if (_info.Text != info)
        {
            _info.Text = info;
            FitHeader();
        }
    }

    private void SetStatus(PaneStatus status, string? text)
    {
        _status = status;
        _statusText = text;
        RefreshHeader();
    }

    private void OnProcessFailed(CoreWebView2ProcessFailedEventArgs e)
    {
        if (Off)
            return;
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                // A aba morreu de qualquer jeito; recarregar já põe a conta de volta no jogo.
                SetStatus(PaneStatus.Failed, "a página caiu, recarregando…");
                Reload();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                SetStatus(PaneStatus.Loading, "a página travou, esperando ela voltar…");
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                SetStatus(PaneStatus.Failed, "o navegador interno fechou: feche e abra o deck");
                break;
        }
    }

    private void UpdateMuteButton() => _mute.Text = Muted ? Theme.MutedGlyph : Theme.SoundGlyph;

    /// <summary>
    /// Conta estreita (em colunas): primeiro o voto fica só com o 🗳 (o tempo continua na dica e na barra de baixo),
    /// depois sai o "rodando há", e por fim os botões perdem a folga, para nenhum sumir por falta de espaço. Com
    /// espaço de novo, tudo volta.
    /// </summary>
    private void FitHeader()
    {
        int room = _header.DisplayRectangle.Width - 8;
        int Needed() => _header.Items.Cast<ToolStripItem>()
            .Where(i => i.Available)
            .Sum(i => i.GetPreferredSize(Size.Empty).Width + i.Margin.Horizontal);
        var buttons = _header.Items.Cast<ToolStripItem>().Where(i => i.Alignment == ToolStripItemAlignment.Right).ToList();
        _header.SuspendLayout();
        buttons.ForEach(b => Roomy(b));
        SetShortVote(false);
        _info.Available = true;
        if (Needed() > room)
            SetShortVote(true);
        if (Needed() > room)
            _info.Available = false;
        if (Needed() > room)
            buttons.ForEach(b => Roomy(b, tight: true));
        _header.ResumeLayout();
    }

    private void SetShortVote(bool shortVote)
    {
        _voteShort = shortVote;
        var text = shortVote ? VoteIcon(_voteText) : _voteText;
        if (_vote.Text != text)
            _vote.Text = text;
    }

    private static string FormatUptime(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes:00} min" : $"{time.Minutes} min";

    private static Image? LoadIcon(ExtensionInfo extension)
    {
        if (extension.Icon is null)
            return null;
        try
        {
            using var stream = File.OpenRead(Path.Combine(extension.Folder, extension.Icon));
            using var image = Image.FromStream(stream);
            return new Bitmap(image, new Size(20, 20));
        }
        catch
        {
            // Ícone em SVG ou faltando: o botão fica com a peça de quebra-cabeça.
            return null;
        }
    }
}