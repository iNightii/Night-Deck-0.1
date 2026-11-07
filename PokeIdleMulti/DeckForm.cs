using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>
/// A janela do deck: as 4 contas dentro dela, cada uma num perfil próprio do navegador embutido
/// (WebView2), sem barra de navegador. Dá para mover, redimensionar, maximizar e pôr em tela cheia;
/// as contas ficam em grade 2×2, em colunas ou uma ampliada. As lives da Twitch/Kick rodam escondidas
/// numa janela à parte, no perfil da conta vinculada.
/// </summary>
internal sealed class DeckForm : Form
{
    private const string GameUrl = "https://pokeidle.io/app";
    private const int Accounts = 4;
    private const int Gap = 4;

    // Sem isso o navegador desacelera as contas escondidas, minimizadas ou atrás de outras janelas;
    // e o autoplay deixa as lives da Kick tocarem (sem som) sem precisar de clique.
    // --process-per-site: as páginas do mesmo site num perfil dividem um processo. As lives da Kick de uma
    // conta ficam num só, sem carregar o site inteiro uma vez por live: medido, 3 lives caem de ~655 MB
    // para ~560 MB. Cada conta é um perfil, e perfis nunca dividem processo: o jogo fica separado.
    internal const string BrowserArguments =
        "--disable-background-timer-throttling --disable-backgrounding-occluded-windows " +
        "--disable-renderer-backgrounding --autoplay-policy=no-user-gesture-required --process-per-site";

    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokeIdleMulti");

    private readonly bool _selfTest;
    private readonly string _root;
    private readonly string _configPath;
    private readonly DeckConfig _config;
    private readonly ExtensionStore _store;
    private readonly AccountPane[] _panes = new AccountPane[Accounts];
    private readonly LivesWindow?[] _lives = new LivesWindow?[Accounts];
    private readonly string?[] _twitchSeen = new string?[Accounts];

    /// <summary>A conta tem o login da Twitch no perfil (null: ainda não conferiu). Sem ele os players nem abrem.</summary>
    private readonly bool?[] _twitchLogin = new bool?[Accounts];

    private readonly Dictionary<string, bool?> _kickLive = new();
    private readonly Dictionary<string, bool?> _twitchLive = new();
    private DateTime _liveCheckAt;
    private bool _liveChecking;
    private bool _watching;
    private readonly PointsRedeemer[] _redeemKick = Enumerable.Range(0, Accounts).Select(_ => new PointsRedeemer(LiveSite.Kick)).ToArray();
    private readonly PointsRedeemer[] _redeemTwitch = Enumerable.Range(0, Accounts).Select(_ => new PointsRedeemer(LiveSite.Twitch)).ToArray();
    private DateTime _twitchPointsAt;
    private bool _twitchPointsBusy;
    private readonly RedeemLog _redeemLog;
    private RedeemLogWindow? _redeemLogWindow;
    private BulkRedeemDialog? _bulkWindow;
    private DateTime _kickRedeemAt;
    private bool _kickRedeeming;
    private readonly Dictionary<(int Pane, string Extension), PopupWindow> _extensionWindows = new();
    private readonly ToolStrip _bar = new ClickThroughToolStrip();
    private readonly StatusStrip _status = new();
    private readonly Panel _grid = new() { Dock = DockStyle.Fill, BackColor = Theme.Back };
    private readonly ToolStripButton _gridButton = new("Grade 2×2");
    private readonly ToolStripButton _columnsButton = new("Colunas");
    private readonly ToolStripButton _focusButton = new("Foco");
    private readonly ToolStripButton[] _accountButtons = new ToolStripButton[Accounts];
    // ⇄ e ⚡, e não 🔁 e 🔋: na barra o Windows desenha esses dois emojis como quadrados.
    private readonly ToolStripButton _echoButton = new("⇄ Eco");
    private bool _echo;
    private readonly ToolStripButton _zoomOutButton = new("−");
    private readonly ToolStripDropDownButton _zoomButton = new("Zoom 100%");
    private readonly ToolStripButton _zoomInButton = new("+");
    private readonly ToolStripDropDownButton _livesButton = new("Lives");
    private readonly ToolStripDropDownButton _kickButton = new("💰 Pontos da Kick") { Visible = false };
    private readonly ToolStripDropDownButton _twitchButton = new("💰 Pontos da Twitch") { Visible = false };
    private readonly ToolStripButton _extensionsButton = new("🧩 Extensões");
    private readonly ToolStripButton _updateButton = new("Atualizações");
    private readonly ToolStripDropDownButton _sniperButton = new("🎯 Sniper") { Visible = false };
    private readonly bool[] _hasSniper = new bool[Accounts];
    private readonly ToolStripButton _fullscreenButton = new("⛶ Tela cheia");
    private readonly ToolStripSplitButton _ultraButton = new("⚡ ULTRA");
    private readonly ToolStripStatusLabel _ram = new();
    private readonly ToolStripStatusLabel _livesStatus = new();
    private readonly ToolStripStatusLabel _voteStatus = new();
    private readonly VoteState?[] _votes = new VoteState?[Accounts];
    private readonly DateTime[] _voteRefreshAt = new DateTime[Accounts];
    private readonly bool[] _voteBusy = new bool[Accounts];
    private readonly PopupWindow?[] _voteWindows = new PopupWindow?[Accounts];
    private readonly RestockWindow?[] _restockWindows = new RestockWindow?[Accounts];
    private readonly bool[] _restockBusy = new bool[Accounts];
    private string? _topIdleGameId;
    private DateTime _topIdleRetryAt;
    private bool _topIdleLookup;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private CoreWebView2Environment? _env;
    private DeckLayout _layoutBeforeFocus = DeckLayout.Grid;
    private bool _fullscreen;
    private FormWindowState _restoreState;
    private Rectangle _restoreBounds;
    private int _seconds;
    private bool _memoryBusy;
    private bool _updateBusy;
    private AppUpdater.Release? _availableUpdate;
    private CancellationTokenSource? _updateCancellation;

    public DeckForm(bool selfTest)
    {
        _selfTest = selfTest;
        // O autoteste usa perfis e páginas de teste separados, para não mexer nas suas contas.
        _root = selfTest ? Path.Combine(DataRoot, "selftest") : DataRoot;
        _configPath = Path.Combine(_root, "deck.json");
        _config = selfTest ? new DeckConfig() : DeckConfig.Load(_configPath);
        // O autoteste começa com a conta 4 desligada, como ficaria salvo do uso anterior, e com 1 live da Twitch
        // (mais as 2 da Kick).
        if (selfTest)
        {
            _config.Off[3] = true;
            _config.TwitchLives = 1;
        }
        // O registro dos resgates (o do autoteste começa vazio a cada vez).
        var logPath = Path.Combine(_root, "resgates.log");
        if (selfTest && File.Exists(logPath))
            File.Delete(logPath);
        _redeemLog = new RedeemLog(logPath);
        for (int i = 0; i < Accounts; i++)
        {
            int index = i;
            _redeemKick[i].Log = text => _redeemLog.Add(_config.AccountNames[index], LiveSite.Kick, text);
            _redeemTwitch[i].Log = text => _redeemLog.Add(_config.AccountNames[index], LiveSite.Twitch, text);
        }
        _store = new ExtensionStore(_root);
        if (_config.Layout != DeckLayout.Focus)
            _layoutBeforeFocus = _config.Layout;
        // O zoom é um só para todas as contas. Do tempo em que cada uma tinha o seu, fica o da primeira ligada.
        var zoom = _config.Zoom[Enumerable.Range(0, Accounts).FirstOrDefault(i => !_config.Off[i])];
        Array.Fill(_config.Zoom, zoom);

        Text = "PokeIdle Deck";
        Icon = Theme.AppIcon;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        KeyPreview = true;
        MinimumSize = new Size(900, 600);

        BuildBar();
        BuildStatus();
        for (int i = 0; i < Accounts; i++)
        {
            var pane = new AccountPane(i)
            {
                AccountName = _config.AccountNames[i], ReduceMotion = _config.ReduceMotion, Ultra = _config.Ultra,
                RestockConfig = _config.Restock[i],
            };
            pane.ShowRestock(null);
            pane.RestockRequested += p => ShowRestockWindow(p.Index);
            pane.FocusRequested += p => ToggleFocus(p.Index);
            pane.ReloadRequested += p => p.Reload();
            pane.FullscreenRequested += _ => ToggleFullscreen();
            pane.SettingsChanged += OnPaneSettingsChanged;
            pane.Clicked += (p, click) => _ = EchoAsync(p, click);
            pane.PowerRequested += p => _ = SetPaneOffAsync(p.Index, !p.Off);
            pane.PopupRequested += (p, extension, anchor) => _ = OpenExtensionPopupAsync(p, extension, anchor);
            pane.VoteRequested += p => _ = OpenVoteAsync(p, null);
            // O botão lives do cabeçalho: as lives da Kick e as da Twitch da conta, para fechar ou abrir de novo.
            pane.LivesMenuOpening += (p, items) =>
            {
                items.Clear();
                foreach (var site in new[] { LiveSite.Kick, LiveSite.Twitch })
                {
                    if (items.Count > 0)
                        items.Add(new ToolStripSeparator());
                    items.Add(new ToolStripMenuItem($"Lives da {site}:") { Enabled = false });
                    items.AddRange(LiveMenuItems(p.Index, site).ToArray());
                }
            };
            pane.TopIdleLinkRequested += (p, uri) => _ = OpenVoteAsync(p, uri);
            _panes[i] = pane;
            _grid.Controls.Add(pane);
        }
        _grid.Resize += (_, _) => ArrangePanes();

        // Encaixe: o que preenche entra primeiro, as barras depois.
        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(_bar);

        RestoreWindowBounds();
        _clock.Tick += (_, _) => OnClock();
    }

    private void BuildBar()
    {
        Theme.Style(_bar);
        _bar.Padding = new Padding(6, 3, 6, 3);
        _bar.Items.Add(new ToolStripLabel("PokeIdle Deck") { Font = new Font(_bar.Font, FontStyle.Bold), ForeColor = Theme.Accent });
        _bar.Items.Add(new ToolStripSeparator());

        _gridButton.ToolTipText = "As 4 contas em grade";
        _columnsButton.ToolTipText = "As 4 contas lado a lado";
        _focusButton.ToolTipText = "Uma conta ocupando o deck (as outras continuam rodando escondidas)";
        _gridButton.Click += (_, _) => SetLayout(DeckLayout.Grid);
        _columnsButton.Click += (_, _) => SetLayout(DeckLayout.Columns);
        _focusButton.Click += (_, _) => FocusPane(_config.Focused);
        _bar.Items.AddRange(new ToolStripItem[] { _gridButton, _columnsButton, _focusButton, new ToolStripSeparator() });

        for (int i = 0; i < Accounts; i++)
        {
            int index = i;
            var button = new ToolStripButton
            {
                ToolTipText = "Clique: ampliar esta conta (de novo: voltar). Botão direito: mais opções",
            };
            button.Click += (_, _) => ToggleFocus(index);
            button.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Right)
                    ShowAccountMenu(index);
            };
            _accountButtons[i] = button;
            _bar.Items.Add(button);
        }

        _echoButton.ToolTipText = "Eco: um clique numa conta se repete nas outras (no mesmo botão)";
        _echoButton.Click += (_, _) => SetEcho(!_echo);
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(_echoButton);

        _zoomOutButton.ToolTipText = "Diminuir o zoom de todas as contas";
        _zoomInButton.ToolTipText = "Aumentar o zoom de todas as contas";
        _zoomButton.ToolTipText = "Zoom de todas as contas";
        _zoomOutButton.Font = _zoomInButton.Font = new Font(_bar.Font.FontFamily, 11f, FontStyle.Bold);
        _zoomOutButton.Click += (_, _) => StepZoom(-1);
        _zoomInButton.Click += (_, _) => StepZoom(1);
        _zoomButton.DropDownOpening += (_, _) => FillZoomMenu();
        _zoomButton.DropDownItems.Add("…");
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.AddRange(new ToolStripItem[] { _zoomOutButton, _zoomButton, _zoomInButton });
        ShowZoom();

        // Itens à direita entram da direita para a esquerda.
        _fullscreenButton.Alignment = _extensionsButton.Alignment = _livesButton.Alignment = ToolStripItemAlignment.Right;
        _fullscreenButton.ToolTipText = "Tela cheia (F11)";
        _fullscreenButton.Click += (_, _) => ToggleFullscreen();
        _extensionsButton.ToolTipText = "Instalar e gerenciar as extensões de cada conta";
        _extensionsButton.Click += (_, _) => OpenExtensions();
        _updateButton.Alignment = ToolStripItemAlignment.Right;
        _updateButton.ToolTipText = "Consultar e instalar a versão mais recente do Night Deck";
        _updateButton.Click += async (_, _) => await CheckForUpdatesAsync(true);
        _livesButton.ToolTipText = "Lives da Twitch e da Kick tocando em segundo plano, juntando os pontos dos canais (o bônus das lives no jogo)";
        // Com o Shift apertado aparece também o resgate em lote, escondido.
        _livesButton.DropDownOpening += (_, _) => FillLivesMenu((ModifierKeys & Keys.Shift) == Keys.Shift);
        _livesButton.DropDownItems.Add("…");
        _kickButton.Alignment = ToolStripItemAlignment.Right;
        _kickButton.ToolTipText = "Abre os chats da Kick da conta, para resgatar os pontos do canal (300 pontos = 1 h de +15% de XP)";
        _kickButton.DropDownOpening += (_, _) => FillPointsMenu(LiveSite.Kick);
        _kickButton.DropDownItems.Add("…");
        _twitchButton.Alignment = ToolStripItemAlignment.Right;
        _twitchButton.ToolTipText = "Abre os chats da Twitch da conta, com o botão dos pontos e o baú, para resgatar na mão " +
                                    "(300 pontos = 1 h de +15% de XP)";
        _twitchButton.DropDownOpening += (_, _) => FillPointsMenu(LiveSite.Twitch);
        _twitchButton.DropDownItems.Add("…");
        _sniperButton.Alignment = ToolStripItemAlignment.Right;
        _sniperButton.ToolTipText = "Liga ou desliga o Rotom Sniper (compras automáticas no mercado) em todas as contas";
        _sniperButton.DropDownOpening += (_, _) => FillSniperMenu();
        _sniperButton.DropDownItems.Add("…");
        _ultraButton.Alignment = ToolStripItemAlignment.Right;
        _ultraButton.ToolTipText = "ULTRA em todas as contas: o Modo ULTRA Economia do jogo (ULTRA Battery Saver). O jogo inteiro " +
                                   "sai da tela e fica só uma caixa com o XP, o Gold, os drops e os shinies da sessão; a caça continua. " +
                                   "Na setinha, o menos animações";
        _ultraButton.ButtonClick += async (_, _) => await SetUltraAsync(!_config.Ultra);
        _ultraButton.DropDownOpening += (_, _) => FillUltraMenu();
        ShowUltra();
        _ultraButton.DropDownItems.Add("…");
        _bar.Items.Add(_fullscreenButton);
        _bar.Items.Add(_ultraButton);
        _bar.Items.Add(_extensionsButton);
        _bar.Items.Add(_updateButton);
        _bar.Items.Add(_sniperButton);
        _bar.Items.Add(_livesButton);
        _bar.Items.Add(_kickButton);
        _bar.Items.Add(_twitchButton);
    }

    // ---------- Atualizador pelo release oficial ----------
    // O download só inicia depois da confirmação: nada é instalado silenciosamente.
    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_selfTest || _updateBusy || IsDisposed || Disposing)
            return;

        _updateBusy = true;
        _updateButton.Enabled = false;
        _updateButton.Text = "Verificando…";
        using var cancellation = new CancellationTokenSource();
        _updateCancellation = cancellation;
        try
        {
            var latest = await AppUpdater.CheckAsync(cancellation.Token);
            if (IsDisposed || Disposing)
                return;

            _availableUpdate = latest;
            _updateButton.Text = latest is null ? "Atualizações" : $"Atualizar #{latest.Build}";
            _updateButton.ToolTipText = latest is null
                ? "Sem versão nova. Clique para verificar novamente"
                : $"Nova versão disponível: {latest.Tag}. Clique para instalar.";

            if (!interactive)
                return;

            if (latest is null)
            {
                MessageBox.Show(this,
                    "Não foi encontrada uma versão mais recente nos Releases oficiais.\n\n" +
                    $"Build instalado: {AppUpdater.CurrentBuild}.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var choice = MessageBox.Show(this,
                $"Nova versão disponível: {latest.Tag}.\n\n" +
                "Deseja baixar o instalador oficial e executá-lo?\n" +
                "O aplicativo será fechado depois que o instalador abrir.\n\n" +
                "Se estiver usando a versão portátil, o instalador fará uma instalação separada.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes)
                return;

            var progress = new Progress<int>(percent =>
            {
                if (!IsDisposed && !Disposing)
                    _updateButton.Text = $"Baixando {percent}%";
            });

            string installer = await AppUpdater.DownloadAsync(latest, progress, cancellation.Token);
            if (IsDisposed || Disposing)
                return;

            Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
            Close(); // O instalador pode substituir os arquivos sem manter o deck aberto.
        }
        catch (OperationCanceledException)
        {
            // A janela foi fechada durante a verificação ou o download.
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing && interactive)
                MessageBox.Show(this, "Não foi possível atualizar o Night Deck:\n" + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (ReferenceEquals(_updateCancellation, cancellation))
                _updateCancellation = null;
            _updateBusy = false;
            if (!IsDisposed && !Disposing)
            {
                _updateButton.Enabled = true;
                _updateButton.Text = _availableUpdate is { } available
                    ? $"Atualizar #{available.Build}" : "Atualizações";
            }
        }
    }

    private void BuildStatus()
    {
        Theme.Style(_status);
        _status.SizingGrip = false;
        _status.ShowItemToolTips = true;
        _ram.ForeColor = Theme.Muted;
        _livesStatus.ForeColor = Theme.Muted;
        _status.Items.Add(_ram);
        _status.Items.Add(new ToolStripStatusLabel("·") { ForeColor = Theme.Muted });
        _status.Items.Add(_livesStatus);
        _status.Items.Add(new ToolStripStatusLabel("·") { ForeColor = Theme.Muted });
        _voteStatus.ForeColor = Theme.Muted;
        _status.Items.Add(_voteStatus);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ArrangePanes();
        UpdateBar();
        try
        {
            await InitializeAsync();
            if (!_selfTest)
                _ = CheckForUpdatesAsync(false);
        }
        catch (Exception ex)
        {
            if (_selfTest)
            {
                File.WriteAllText(SelfTestReport, "ERRO ao iniciar " + ex);
                Close();
                return;
            }
            MessageBox.Show(this, "Não consegui abrir o navegador embutido:\n\n" + ex.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task InitializeAsync()
    {
        if (_selfTest)
            PrepareSelfTestFiles();

        // Configurações de extensão pedidas em "Trazer configurações…": só dá para gravar antes do navegador abrir.
        var imported = await Task.Run(() => StorageImport.ApplyPending(_root));

        var options = new CoreWebView2EnvironmentOptions(BrowserArguments) { AreBrowserExtensionsEnabled = true };
        _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_root, "WebView2"), options);

        // Conta desligada nem cria o navegador: só abre quando for ligada.
        foreach (var pane in _panes.Where(p => _config.Off[p.Index]))
            pane.SetOff(true);
        await Task.WhenAll(_panes.Where(p => !p.Off).Select(p =>
            p.InitializeAsync(_env, GameUrlForDeck, _config.Zoom[p.Index], _config.Muted[p.Index], SelfTestSetup, RepairExtensions)));
        await RefreshExtensionButtonsAsync();
        foreach (int account in _config.LivesAccounts.Where(a => a is >= 1 and <= Accounts).ToList())
            await OpenLivesAsync(account - 1);

        _clock.Start();
        OnClock();
        if (_selfTest)
        {
            _ = RunSelfTestAsync();
            return;
        }
        if (imported.Count > 0)
        {
            var lines = imported.Select(r =>
                $"{_panes.FirstOrDefault(p => p.ProfileName == r.Profile)?.AccountName ?? r.Profile}: {r.Result}");
            MessageBox.Show(this, "Configurações de extensão trazidas:\n\n" + string.Join("\n", lines), Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private string GameUrlForDeck => _selfTest ? "https://deck.test/game.html" : GameUrl;

    /// <summary>Dados trazidos de outro PC: reinstala da pasta daqui as extensões cujo caminho antigo sumiu.</summary>
    private Task RepairExtensions(CoreWebView2 core) => _store.RepairAsync(core.Profile);

    // As lives do autoteste vêm de outros sites (lives.test e kick.test), como a Kick e o jogo são sites diferentes:
    // assim dividem processo entre elas, mas não com o jogo. A Kick de teste (o player e a API) quem responde é o
    // próprio deck, porque o navegador não deixa interceptar os pedidos de um site mapeado para uma pasta.
    private Action<CoreWebView2>? SelfTestSetup => _selfTest
        ? core =>
        {
            core.SetVirtualHostNameToFolderMapping("deck.test", Path.Combine(_root, "site"), CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("lives.test", Path.Combine(_root, "site"), CoreWebView2HostResourceAccessKind.Allow);
            core.AddWebResourceRequestedFilter("https://kick.test/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.AddWebResourceRequestedFilter("https://twitch.test/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                FakeKick(e);
                FakeTwitch(e);
            };
        }
        : null;

    // A Kick de mentira do autoteste: o saldo de cada canal e cada resgate pedido ("canal bônus login corpo").
    private readonly Dictionary<string, int> _fakePoints = new();
    private readonly List<string> _fakeRedeems = new();

    // Quem está logado na Kick de mentira: no canal com o mesmo nome é o dono (pontos infinitos).
    private string _fakeKickMe = "fulano_kick";

    // O id de cada usuário na Kick de mentira.
    private static int FakeKickId(string name) => name.Sum(c => (int)c);

    /// <summary>
    /// A Kick do autoteste (kick.test): as páginas da pasta do site de teste (o player é o kick.html; sem o arquivo,
    /// fica sem resposta, como sem internet) e a API, com quem está logado, o canal (e o usuário dele), o saldo, os
    /// bônus do canal e o resgate. O /me do canal ela não tem (404): o dono do canal sai pelo id do usuário.
    /// </summary>
    private void FakeKick(CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        if (_env is null || uri.Host != "kick.test")
            return;
        if (!uri.AbsolutePath.StartsWith("/api/"))
        {
            var file = Path.Combine(_root, "site", uri.AbsolutePath.TrimStart('/'));
            if (File.Exists(file))
                e.Response = _env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(file)), 200, "OK", "Content-Type: text/html");
            return;
        }
        // api/v1/user (quem está logado), api/v2/channels/{canal}, …/me, …/points, …/rewards e …/rewards/{id}/redeem
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool logged = e.Request.Headers.Contains("Authorization");
        void Reply(int code, string body) => e.Response = _env.CreateWebResourceResponse(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)), code, code == 200 ? "OK" : "Error", "Content-Type: application/json");
        if (path is [_, "v1", "user"])
        {
            Reply(logged ? 200 : 401, logged ? JsonSerializer.Serialize(new { id = FakeKickId(_fakeKickMe), username = _fakeKickMe }) : "{\"message\":\"Unauthenticated.\"}");
            return;
        }
        if (path.Length < 4 || path[2] != "channels")
            return;
        string channel = path[3];
        bool owner = channel == _fakeKickMe;
        if (path.Length == 4)
        {
            Reply(200, JsonSerializer.Serialize(new
            {
                id = 1000 + FakeKickId(channel), user_id = FakeKickId(channel), slug = channel, user = new { id = FakeKickId(channel), username = channel },
            }));
            return;
        }
        if (path[4] == "me")
        {
            Reply(404, "{\"message\":\"Not Found\"}");
            return;
        }
        // Os 3 do PokeIdle e um "XP" de outro tipo, que não é o do jogo.
        var rewards = new[]
        {
            (Id: "xp", Title: "Bônus +15% XP PokeIdle", Cost: 300, Description: "1 hora de +15% de XP no PokeIdle."),
            (Id: "cap", Title: "Bônus de +15% Capture Boost", Cost: 500, Description: "1 hora de +15% na chance de captura no PokeIdle."),
            (Id: "lure", Title: "Bônus de +15% Secret Lure", Cost: 1000, Description: "1 hora de +15% na chance de shiny no PokeIdle."),
            (Id: "chat", Title: "Dobro de XP no chat", Cost: 100, Description: ""),
        };
        int status = 200;
        string json;
        if (path[4] == "points")
        {
            json = $"{{\"data\":{{\"points\":{_fakePoints.GetValueOrDefault(channel)}}},\"message\":\"OK\"}}";
        }
        else if (path.Length == 5 && path[4] == "rewards")
        {
            json = JsonSerializer.Serialize(new
            {
                data = rewards.Select(r => new { id = r.Id, title = r.Title, cost = r.Cost, description = r.Description, is_enabled = true, is_paused = false }),
            });
        }
        else if (path.Length == 7 && path[6] == "redeem" && e.Request.Method == "POST" && rewards.Any(r => r.Id == path[5]))
        {
            string body = e.Request.Content is null ? "" : new StreamReader(e.Request.Content).ReadToEnd();
            string auth = e.Request.Headers.Contains("Authorization") ? e.Request.Headers.GetHeader("Authorization") : "sem login";
            _fakeRedeems.Add($"{channel} {path[5]} {auth} {body}");
            // O dono do canal (pontos infinitos) resgata sem gastar.
            int cost = rewards.First(r => r.Id == path[5]).Cost;
            if (owner)
                status = 200;
            else if (_fakePoints.GetValueOrDefault(channel) >= cost)
                _fakePoints[channel] -= cost;
            else
                status = 400;
            json = status == 200 ? "{\"message\":\"OK\"}" : "{\"message\":\"Not enough points\"}";
        }
        else
        {
            return;
        }
        e.Response = _env.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)), status,
            status == 200 ? "OK" : "Bad Request", "Content-Type: application/json");
    }

    // A Twitch de mentira do autoteste: o saldo de cada canal, os canais com baú esperando e cada pedido feito.
    private readonly Dictionary<string, int> _fakeTwitchPoints = new();
    private readonly HashSet<string> _fakeTwitchClaims = new();
    private readonly List<string> _fakeTwitch = new();

    // Roda depois de cada resgate que passou (o autoteste gasta os pontos por fora, ou cancela, no meio do lote).
    private Action<string>? _fakeTwitchAfterRedeem;

    // Quem está logado na Twitch de mentira: no canal com o mesmo nome é o dono (pontos infinitos; o saldo nem vem).
    private string _fakeTwitchMe = "fulano_twitch";

    /// <summary>
    /// A Twitch do autoteste (twitch.test): as páginas da pasta do site de teste (o player é o twitch.html), a API
    /// (/gql: o saldo com os bônus do canal, o baú, que só passa com o token da checagem anti-robô, e o resgate) e o
    /// token da checagem (/integrity).
    /// </summary>
    private void FakeTwitch(CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        if (_env is null || uri.Host != "twitch.test")
            return;
        if (uri.AbsolutePath is not ("/gql" or "/integrity"))
        {
            var file = Path.Combine(_root, "site", uri.AbsolutePath.TrimStart('/'));
            if (File.Exists(file))
                e.Response = _env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(file)), 200, "OK", "Content-Type: text/html");
            return;
        }
        string Header(string name) => e.Request.Headers.Contains(name) ? e.Request.Headers.GetHeader(name) : "-";
        var rewards = new[]
        {
            new { id = "xp", title = "Bônus +15% XP PokeIdle", prompt = "1 hora de +15% de XP no PokeIdle.", cost = 300 },
            new { id = "cap", title = "Bônus de +15% Capture Boost", prompt = "1 hora de +15% na chance de captura no PokeIdle.", cost = 500 },
            new { id = "lure", title = "Bônus de +15% Secret Lure", prompt = "1 hora de +15% na chance de shiny no PokeIdle.", cost = 1000 },
            new { id = "chat", title = "Capture o streamer", prompt = "", cost = 100 },
        };
        string json;
        if (uri.AbsolutePath == "/integrity")
        {
            _fakeTwitch.Add($"checagem {Header("Authorization")} {Header("X-Device-Id")}");
            json = "{\"token\":\"tok-checagem\",\"expiration\":0}";
        }
        else
        {
            using var doc = JsonDocument.Parse(e.Request.Content is null ? "{}" : new StreamReader(e.Request.Content).ReadToEnd());
            string query = doc.RootElement.GetProperty("query").GetString() ?? "";
            var variables = doc.RootElement.GetProperty("variables");
            if (query.Contains("claimCommunityPoints"))
            {
                string claim = variables.GetProperty("i").GetProperty("claimID").GetString() ?? "";
                string channel = claim.Replace("bau-", "");
                if (Header("Client-Integrity") != "tok-checagem")
                {
                    _fakeTwitch.Add("baú sem a checagem");
                    json = "{\"errors\":[{\"message\":\"failed integrity check\",\"extensions\":{\"code\":\"IntegrityCheckFailed\"}}]," +
                           "\"data\":{\"claimCommunityPoints\":null},\"extensions\":{\"challenge\":{\"type\":\"integrity\"}}}";
                }
                else
                {
                    _fakeTwitch.Add($"baú {claim} {Header("Authorization")}");
                    _fakeTwitchClaims.Remove(channel);
                    _fakeTwitchPoints[channel] = _fakeTwitchPoints.GetValueOrDefault(channel) + 50;
                    json = $"{{\"data\":{{\"claimCommunityPoints\":{{\"claim\":{{\"id\":\"{claim}\"}},\"currentPoints\":{_fakeTwitchPoints[channel]},\"error\":null}}}}}}";
                }
            }
            else if (query.Contains("redeemCommunityPointsCustomReward"))
            {
                var input = variables.GetProperty("i");
                string channel = input.GetProperty("channelID").GetString()!.Replace("id-", "");
                int cost = input.GetProperty("cost").GetInt32();
                _fakeTwitch.Add($"resgate {channel} {input.GetProperty("rewardID").GetString()} {cost} {Header("Authorization")} " +
                                input.GetProperty("transactionID").GetString());
                // Como a Twitch: o custo, o título e a descrição (prompt) têm de ser os do bônus no canal.
                var reward = rewards.FirstOrDefault(r => r.id == input.GetProperty("rewardID").GetString());
                string? prompt = input.TryGetProperty("prompt", out var sent) && sent.ValueKind == JsonValueKind.String ? sent.GetString() : null;
                bool matches = reward is not null && reward.cost == cost && reward.title == input.GetProperty("title").GetString()
                               && reward.prompt == (prompt ?? "");
                // O dono do canal (pontos infinitos) resgata sem gastar.
                bool owner = channel == _fakeTwitchMe;
                bool enough = owner || _fakeTwitchPoints.GetValueOrDefault(channel) >= cost;
                if (matches && enough)
                {
                    if (!owner)
                        _fakeTwitchPoints[channel] -= cost;
                    _fakeTwitchAfterRedeem?.Invoke(channel);
                }
                json = "{\"data\":{\"redeemCommunityPointsCustomReward\":" +
                       (!matches ? "{\"redemption\":null,\"error\":{\"code\":\"PROPERTIES_MISMATCH\"}}"
                           : enough ? "{\"redemption\":{\"id\":\"r1\"},\"error\":null}"
                           : "{\"redemption\":null,\"error\":{\"code\":\"NOT_ENOUGH_POINTS\"}}") + "}}";
            }
            else
            {
                string channel = variables.GetProperty("login").GetString() ?? "";
                _fakeTwitch.Add($"saldo {channel} {Header("Authorization")} {Header("X-Device-Id")}");
                object? communityPoints = channel == _fakeTwitchMe ? null : new
                {
                    balance = _fakeTwitchPoints.GetValueOrDefault(channel),
                    availableClaim = _fakeTwitchClaims.Contains(channel) ? new { id = "bau-" + channel } : null,
                };
                json = JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        currentUser = new { id = "id-" + _fakeTwitchMe, login = _fakeTwitchMe },
                        user = new
                        {
                            id = "id-" + channel,
                            channel = new
                            {
                                self = new { communityPoints },
                                communityPointsSettings = new
                                {
                                    customRewards = rewards.Select(r => new
                                    {
                                        r.id, r.title, r.prompt, r.cost, isEnabled = true, isPaused = false, isInStock = true,
                                        cooldownExpiresAt = (string?)null,
                                    }),
                                },
                            },
                        },
                    },
                });
            }
        }
        e.Response = _env.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)), 200, "OK",
            "Content-Type: application/json");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _clock.Stop();
        _updateCancellation?.Cancel();
        SaveWindowBounds();
        SaveConfig();
        for (int i = 0; i < Accounts; i++)
            CloseLives(i);
        base.OnFormClosing(e);
    }

    // ---------- Janela: posição, tamanho e tela cheia ----------

    private void RestoreWindowBounds()
    {
        var saved = _config.WindowBounds is { Length: 4 } b ? new Rectangle(b[0], b[1], b[2], b[3]) : Rectangle.Empty;
        if (_selfTest)
            saved = new Rectangle(80, 60, 1500, 900);
        if (saved.Width >= MinimumSize.Width && saved.Height >= MinimumSize.Height
            && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = saved;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            var work = Screen.PrimaryScreen!.WorkingArea;
            Size = new Size(Math.Min(1600, work.Width), Math.Min(950, work.Height));
        }
        if (_config.Maximized && !_selfTest)
            WindowState = FormWindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var state = _fullscreen ? _restoreState : WindowState;
        var bounds = _fullscreen ? _restoreBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _config.WindowBounds = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height };
        if (state != FormWindowState.Minimized)
            _config.Maximized = state == FormWindowState.Maximized;
    }

    private void ToggleFullscreen()
    {
        SuspendLayout();
        if (!_fullscreen)
        {
            _restoreState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var screen = Screen.FromControl(this).Bounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen;
            _status.Visible = false;
            _fullscreen = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _restoreBounds;
            WindowState = _restoreState;
            _status.Visible = true;
            _fullscreen = false;
        }
        _fullscreenButton.Checked = _fullscreen;
        _fullscreenButton.Text = _fullscreen ? "⛶ Sair da tela cheia" : "⛶ Tela cheia";
        ResumeLayout();
    }

    // ---------- Layout das contas ----------

    private void ArrangePanes()
    {
        var area = _grid.ClientRectangle;
        if (area.Width <= 0 || area.Height <= 0)
            return;
        _grid.SuspendLayout();
        foreach (var pane in _panes)
        {
            var cell = CellFor(pane.Index, area);
            pane.Visible = cell is not null;
            if (cell is Rectangle bounds)
                pane.Bounds = bounds;
        }
        _grid.ResumeLayout();
    }

    private Rectangle? CellFor(int index, Rectangle area) => _config.Layout switch
    {
        DeckLayout.Columns => Cell(area, index, 0, Accounts, 1),
        DeckLayout.Focus => index == _config.Focused ? Cell(area, 0, 0, 1, 1) : null,
        _ => Cell(area, index % 2, index / 2, 2, 2),
    };

    private static Rectangle Cell(Rectangle area, int col, int row, int cols, int rows) => Rectangle.FromLTRB(
        area.X + Gap + col * (area.Width - Gap) / cols,
        area.Y + Gap + row * (area.Height - Gap) / rows,
        area.X + (col + 1) * (area.Width - Gap) / cols,
        area.Y + (row + 1) * (area.Height - Gap) / rows);

    private void SetLayout(DeckLayout layout)
    {
        bool entering = layout == DeckLayout.Focus && _config.Layout != DeckLayout.Focus;
        bool leaving = layout != DeckLayout.Focus && _config.Layout == DeckLayout.Focus;
        _config.Layout = layout;
        if (layout != DeckLayout.Focus)
            _layoutBeforeFocus = layout;
        // A conta ampliada fica em 100%; voltar para a grade ou as colunas devolve o zoom de antes (o que deixa
        // as 4 contas cabendo na tela, como 50%).
        if (entering)
        {
            _config.ZoomOutsideFocus = _config.Zoom[0];
            SetZoomAll(1);
        }
        else if (leaving && _config.ZoomOutsideFocus is double zoom)
        {
            SetZoomAll(zoom);
        }
        ArrangePanes();
        UpdateBar();
    }

    private void FocusPane(int index)
    {
        _config.Focused = index;
        SetLayout(DeckLayout.Focus);
        _panes[index].WebView.Focus();
    }

    private void ToggleFocus(int index)
    {
        if (_config.Layout == DeckLayout.Focus && _config.Focused == index)
            SetLayout(_layoutBeforeFocus);
        else
            FocusPane(index);
    }

    private void UpdateBar()
    {
        _gridButton.Checked = _config.Layout == DeckLayout.Grid;
        _columnsButton.Checked = _config.Layout == DeckLayout.Columns;
        _focusButton.Checked = _config.Layout == DeckLayout.Focus;
        for (int i = 0; i < Accounts; i++)
        {
            _accountButtons[i].Text = _config.AccountNames[i];
            _accountButtons[i].Checked = _config.Layout == DeckLayout.Focus && _config.Focused == i;
        }

        _livesButton.Text = _config.LivesAccounts.Count == 0 ? "Lives: desligadas" : "Lives: ligadas";
        var parts = Enumerable.Range(0, Accounts)
            .Where(i => _lives[i] is not null)
            .Select(i => $"{_config.AccountNames[i]}: {LivesSummary(i)}")
            .ToList();
        _livesStatus.Text = parts.Count == 0 ? "Lives desligadas" : "Lives · " + string.Join(" · ", parts);
        _kickButton.Visible = _lives.Any(l => l is not null && l.Tabs(LiveSite.Kick).Any());
        _twitchButton.Visible = _lives.Any(l => l is not null && l.Tabs(LiveSite.Twitch).Any());
        RenderLiveButtons();
    }

    /// <summary>Os botões kick e twitch de cada conta: o que dá para fechar ou abrir de novo, ou por que não tem nada.</summary>
    private void RenderLiveButtons()
    {
        for (int i = 0; i < Accounts; i++)
        {
            var (enabled, closed, tip) = LiveButton(i, LiveSite.Kick);
            _panes[i].SetKick(enabled, closed, tip);
            (enabled, closed, tip) = LiveButton(i, LiveSite.Twitch);
            _panes[i].SetTwitch(enabled, closed, tip);
        }
    }

    private (bool Enabled, bool Closed, string Tooltip) LiveButton(int index, LiveSite site)
    {
        var open = LivesOpen(index, site);
        var limit = _config.Limits(site)[index];
        if (_lives[index] is null)
            return (false, false, !_config.LivesAccounts.Contains(index + 1)
                ? "As lives estão desligadas nesta conta (ligue em Lives, na barra de cima)"
                : $"Nenhuma live da {site} para abrir (Lives → Config…)");
        if (limit.Limited)
            return (true, true, $"Lives da {site} fechadas nesta conta: " + (limit.Slots == 0 ? "todas" : ShutText(index, site)) +
                (open.Count > 0 ? $" (abertas: {string.Join(", ", open)})" : "") + ". Clique para fechar outra ou abrir de novo");
        if (open.Count > 0)
            return (true, false, $"Escolher qual live da {site} fechar nesta conta (abertas: {string.Join(", ", open)}). " +
                "A vaga de uma live fechada não abre outra sozinha");
        bool checking = !_selfTest && (site == LiveSite.Kick ? _kickLive : _twitchLive).Count == 0;
        return (false, false, site == LiveSite.Twitch && _twitchLogin[index] == false ? "Entre na Twitch nesta conta (Lives → Mostrar)"
            : checking ? $"Conferindo quem está no ar na {site}…" : $"Nenhuma live da {site} aberta nesta conta agora");    }

    /// <summary>As lives de <paramref name="site"/> abertas na conta, na ordem da lista.</summary>
    private List<string> LivesOpen(int index, LiveSite site) => (_lives[index]?.Channels(site) ?? Enumerable.Empty<string>())
        .OrderBy(c => _config.Channels(site).IndexOf(c)).ToList();

    /// <summary>Os canais da lista que o botão do site fechou na conta.</summary>
    private List<string> LivesShut(int index, LiveSite site) => _config.Channels(site).Where(_config.Limits(site)[index].Closed.Contains).ToList();

    /// <summary>As fechadas pelo nome, ou quantas vagas a conta perdeu (do tempo em que o kick só fechava a 2ª).</summary>
    private string ShutText(int index, LiveSite site)
    {
        var shut = LivesShut(index, site);
        int max = _config.Lives(site);
        int missing = max - _config.Limits(site)[index].SlotsOf(max);
        return shut.Count > 0 ? string.Join(", ", shut) : missing == 1 ? "1 vaga" : $"{missing} vagas";
    }

    /// <summary>O menu do kick ou do twitch: fechar cada live aberta ou todas; abrir de novo cada fechada ou todas.</summary>
    private List<ToolStripItem> LiveMenuItems(int index, LiveSite site)
    {
        var items = new List<ToolStripItem>();
        var open = LivesOpen(index, site);
        var shut = LivesShut(index, site);
        foreach (var channel in open)
            items.Add(new ToolStripMenuItem($"Fechar a live de {channel}", null, (_, _) => CloseLive(index, site, channel)));
        if (open.Count > 0)
            items.Add(new ToolStripMenuItem($"Fechar todas (nenhuma live da {site} nesta conta)", null, (_, _) => CloseLive(index, site, null)));
        if (_config.Limits(site)[index].Limited)
        {
            if (items.Count > 0)
                items.Add(new ToolStripSeparator());
            foreach (var channel in shut)
                items.Add(new ToolStripMenuItem($"Abrir de novo a live de {channel}", null, (_, _) => ReopenLive(index, site, channel)));
            items.Add(new ToolStripMenuItem($"Abrir todas de novo (volta às {_config.Lives(site)} primeiras da lista no ar)", null,
                (_, _) => ReopenLive(index, site, null)));
        }
        if (items.Count == 0)
            items.Add(new ToolStripMenuItem($"Nenhuma live da {site} aberta nesta conta agora") { Enabled = false });
        return items;
    }

    /// <summary>Uma linha por conta com live do site: abre a janela das lives já nela, com os chats (e o botão dos pontos).</summary>
    private void FillPointsMenu(LiveSite site)
    {
        var items = (site == LiveSite.Kick ? _kickButton : _twitchButton).DropDownItems;
        items.Clear();
        for (int i = 0; i < Accounts; i++)
        {
            int index = i;
            if (_lives[i] is LivesWindow window && window.Tabs(site).Any())
                items.Add($"{_config.AccountNames[i]} · {string.Join(", ", window.Channels(site))}", null, (_, _) => ShowPoints(index, site));
        }
        if (items.Count == 0)
            items.Add(new ToolStripMenuItem($"Nenhuma live da {site} no ar agora") { Enabled = false });
    }

    /// <summary>"Twitch no ar: cogu ▶, Kick no ar: biscoitao ▶" ou "Twitch: entre na Twitch…".</summary>
    private string LivesSummary(int index)
    {
        var parts = new List<string>();
        if (_lives[index] is LivesWindow window)
        {
            var kick = window.KickChannels.ToList();
            // ▶ tocando, ‖ parada (o deck manda tocar e, se continuar, recarrega: ⟳ quantas vezes). O ‖ e não o
            // ⏸: esse só existe nas fontes de emoji, que o desenham como um botão quadrado.
            // Com o resgate ligado, o saldo de cada uma entre parênteses: (230/300 pontos, resgatou 14:05).
            var redeem = _config.RedeemKick[index] is not null ? _redeemKick[index] : null;
            // Na Twitch o saldo aparece também só com o baú ligado.
            var twitchRedeem = _config.RedeemTwitch[index] is not null || _config.ClaimTwitchBonus ? _redeemTwitch[index] : null;
            IEnumerable<string> Played(LiveSite site) => window.Playback(site).Select(k => k.Channel
                + (k.Playing == true ? " ▶" : k.Playing == false ? " ‖" : "") + (k.Reloads > 0 ? $" ⟳{k.Reloads}" : "")
                + ((site == LiveSite.Kick ? redeem : twitchRedeem)?.Text(k.Channel) is { Length: > 0 } points ? $" ({points})" : ""));
            if (_config.TwitchChannels.Count > 0 && _config.TwitchLives > 0)
                parts.Add(_twitchLogin[index] == false ? "Twitch: entre na Twitch (Lives → Mostrar)"
                        + (_twitchSeen[index] is string seen ? $" [{seen}]" : "")
                    : window.TwitchChannels.Any() ? "Twitch no ar: " + string.Join(", ", Played(LiveSite.Twitch))
                    : _config.Twitch[index].Slots == 0 ? "Twitch: todas fechadas no botão lives"
                    : _twitchLive.Count == 0 ? "Twitch: conferindo quem está no ar…"
                    : "Twitch: nenhuma live da lista no ar");
            if (_config.Twitch[index].Limited && _config.Twitch[index].SlotsOf(_config.TwitchLives) > 0)
                parts.Add("Twitch fechadas no botão lives: " + ShutText(index, LiveSite.Twitch));
            if (twitchRedeem?.Stopped is string twitchStopped)
                parts.Add("resgate da Twitch parado: " + twitchStopped);
            else if (twitchRedeem is { NoLogin: true } && window.TwitchChannels.Any())
                parts.Add("pontos da Twitch: entre na Twitch nas lives desta conta");
            var playback = Played(LiveSite.Kick);
            if (_config.KickChannels.Count > 0 && _config.KickLives > 0)
                parts.Add(kick.Count > 0 ? "Kick no ar: " + string.Join(", ", playback)
                    : _config.Kick[index].Slots == 0 ? "Kick: todas fechadas no botão lives"
                    : _kickLive.Count == 0 ? "Kick: conferindo quem está no ar…"
                    : "Kick: nenhuma live da lista no ar");
            if (redeem?.Stopped is string stopped)
                parts.Add("resgate parado: " + stopped);
            else if (redeem is { NoLogin: true } && kick.Count > 0)
                parts.Add("resgate: entre na Kick nas lives desta conta");
            if (_config.Kick[index].Limited && _config.Kick[index].SlotsOf(_config.KickLives) > 0)
                parts.Add("Kick fechadas no botão lives: " + ShutText(index, LiveSite.Kick));
            if (window.Loaded < window.Count)
                parts.Add("carregando…");
        }
        return string.Join(", ", parts);
    }

    private void OnClock()
    {
        // Confere periodicamente, inclusive se o deck ficar aberto por dias.
        if (!_selfTest && _seconds > 0 && _seconds % 21600 == 0)
            _ = CheckForUpdatesAsync(false);
        foreach (var pane in _panes)
            pane.RefreshHeader();
        if (_seconds++ % 3 == 0)
            _ = UpdateMemoryAsync();
        if (_seconds % 5 == 0)
        {
            foreach (var window in _lives.OfType<LivesWindow>())
                window.KeepParked();
            UpdateBar();
        }
        if (_seconds % 30 == 0)
            foreach (var pane in _panes)
                _ = pane.CheckLoginScreenAsync();
        if (_seconds % 2 == 0)
            foreach (var pane in _panes.Where(p => p.RestockConfig.On || _restockWindows[p.Index] is not null))
                _ = RefreshRestockAsync(pane.Index);
        if (!_selfTest && !_liveChecking && DateTime.Now >= _liveCheckAt && _lives.Any(l => l is not null))
            _ = CheckLivesAsync();
        if (!_selfTest && _seconds % 30 == 15)
            _ = WatchLivesAsync();
        if (!_selfTest && !_kickRedeeming && DateTime.Now >= _kickRedeemAt
            && Enumerable.Range(0, Accounts).Any(i => _config.RedeemKick[i] is not null && _lives[i] is not null))
            _ = RedeemKickAsync();
        if (!_selfTest && !_twitchPointsBusy && DateTime.Now >= _twitchPointsAt
            && Enumerable.Range(0, Accounts).Any(i => (_config.RedeemTwitch[i] is not null || _config.ClaimTwitchBonus)
                                                     && _lives[i]?.TwitchChannels.Any() == true))
            _ = TwitchPointsAsync();
        UpdateVotes();
    }

    /// <summary>
    /// A RAM do navegador embutido, separada em o jogo das contas, as lives e o resto (o próprio navegador,
    /// a placa de vídeo, as extensões). É a memória privada de cada processo, a que pesa no PC.
    /// </summary>
    private async Task UpdateMemoryAsync()
    {
        if (_env is null || _memoryBusy)
            return;
        _memoryBusy = true;
        try
        {
            long game = 0, lives = 0, other = 0;
            int count = 0;
            foreach (var info in await _env.GetProcessExtendedInfosAsync())
            {
                long bytes;
                try
                {
                    using var process = Process.GetProcessById(info.ProcessInfo.ProcessId);
                    bytes = process.PrivateMemorySize64;
                }
                catch (ArgumentException)
                {
                    // O processo acabou de fechar.
                    continue;
                }
                count++;
                // Cada página roda num processo, e um iframe conta para a página que o abriu: o captcha do
                // Cloudflare na tela de login é do jogo; o da Kick, das lives.
                var hosts = info.AssociatedFrameInfos.Select(PageHost).ToList();
                if (hosts.Any(h => h.EndsWith("pokeidle.io")))
                    game += bytes;
                else if (hosts.Any(h => h.EndsWith("kick.com") || h.EndsWith("twitch.tv")))
                    lives += bytes;
                else
                    other += bytes;
            }
            _ram.Text = $"RAM ~{Gb(game + lives + other)} GB: jogo {Gb(game)} · lives {Gb(lives)} · navegador {Gb(other)}";
            _ram.ToolTipText = $"{count} processos do navegador embutido (memória privada, a que pesa no PC).\n" +
                               "Para aliviar: feche lives no botão lives do cabeçalho de cada conta, abra menos lives (Lives → Config…), " +
                               "ou desligue (no canto direito do cabeçalho) as contas que não está usando.";
        }
        catch
        {
            // O deck está fechando ou o navegador caiu: fica o último valor.
        }
        finally
        {
            _memoryBusy = false;
        }
    }

    private static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0");

    /// <summary>O site da página onde o quadro está (sobe dos iframes até a página).</summary>
    private static string PageHost(CoreWebView2FrameInfo frame)
    {
        while (frame.ParentFrameInfo is CoreWebView2FrameInfo parent)
            frame = parent;
        return Uri.TryCreate(frame.Source, UriKind.Absolute, out var uri) ? uri.Host : "";
    }

    /// <summary>Liga ou desliga o "menos animações" em todas as contas, na hora (sem recarregar).</summary>
    private async Task SetReduceMotionAsync(bool reduce)
    {
        _config.ReduceMotion = reduce;
        SaveConfig();
        foreach (var pane in _panes)
        {
            pane.ReduceMotion = reduce;
            await pane.ApplyReduceMotionAsync();
        }
    }

    /// <summary>O ⚡ ULTRA na barra: laranja com "ligado", como o eco.</summary>
    private void ShowUltra()
    {
        _ultraButton.Text = _config.Ultra ? "⚡ ULTRA ligado" : "⚡ ULTRA";
        _ultraButton.ForeColor = _config.Ultra ? Theme.Vote : Theme.Text;
    }

    /// <summary>O ULTRA (o mesmo do botão) e, embaixo, o menos animações.</summary>
    private void FillUltraMenu()
    {
        var items = _ultraButton.DropDownItems;
        items.Clear();
        items.Add(_config.Ultra ? "Desligar o ULTRA" : "Ligar o ULTRA (em todas as contas)", null,
            async (_, _) => await SetUltraAsync(!_config.Ultra));
        items.Add(new ToolStripSeparator());
        var motion = new ToolStripMenuItem("🐢 Menos animações (no jogo normal e na tela de login)")
        {
            Checked = _config.ReduceMotion,
            ToolTipText = "O próprio jogo desliga as animações que rodam sem parar (selos pulsando, halos, cartas). " +
                          "Gasta bem menos CPU e placa de vídeo. Desmarque para ver todas",
        };
        motion.Click += async (_, _) => await SetReduceMotionAsync(!_config.ReduceMotion);
        items.Add(motion);
    }

    /// <summary>
    /// Liga ou desliga o ULTRA em todas as contas, na hora. Ligado, vale também para a conta que recarrega ou
    /// é ligada depois (ela volta ao ULTRA sozinha).
    /// </summary>
    private async Task SetUltraAsync(bool on)
    {
        _config.Ultra = on;
        ShowUltra();
        SaveConfig();
        var failed = new List<string>();
        foreach (var pane in _panes)
        {
            pane.Ultra = on;
            if (!pane.Off && !await pane.SetUltraAsync(on))
                failed.Add(pane.AccountName);
        }
        if (failed.Count > 0)
            MessageBox.Show(this,
                $"Não consegui {(on ? "ligar" : "desligar")} o ULTRA em: {string.Join(", ", failed)}.\n\n" +
                "O deck usa o interruptor do próprio jogo (Configurações → Desempenho → Modo ULTRA Economia): " +
                "recarregue a conta (⟳ no cabeçalho) e tente de novo, ou ligue por lá.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ---------- Ações das contas ----------

    private void ShowAccountMenu(int index)
    {
        var pane = _panes[index];
        var menu = new ContextMenuStrip();
        menu.Items.Add(_config.Layout == DeckLayout.Focus && _config.Focused == index ? "Voltar para a grade" : "Ampliar no deck",
            null, (_, _) => ToggleFocus(index));
        menu.Items.Add("Recarregar a página", null, (_, _) => pane.Reload());
        menu.Items.Add(pane.Muted ? "Ligar o som" : "Desligar o som", null, (_, _) => pane.SetMuted(!pane.Muted));
        menu.Items.Add("Renomear…", null, (_, _) => RenameAccount(index));
        menu.Items.Add(pane.Off ? "Ligar esta conta" : "Desligar esta conta (fica no deck, sem rodar nada)",
            null, async (_, _) => await SetPaneOffAsync(index, !pane.Off));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Lives da Twitch/Kick nesta conta", null, async (_, _) => await ToggleLivesAsync(index))
        {
            Checked = _config.LivesAccounts.Contains(index + 1),
        });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(Cursor.Position);
    }

    /// <summary>
    /// Desliga ou religa uma conta. Desligada, ela fica no deck, mas a página do jogo fecha (e com ela
    /// as extensões, que só rodam no jogo), e as lives, as janelas dela e a conferência do voto param.
    /// </summary>
    private async Task SetPaneOffAsync(int index, bool off)
    {
        var pane = _panes[index];
        _config.Off[index] = off;
        SaveConfig();
        if (off)
        {
            CloseLives(index);
            _voteWindows[index]?.Close();
            foreach (var (key, window) in _extensionWindows.Where(w => w.Key.Pane == index).ToList())
            {
                window.Close();
                _extensionWindows.Remove(key);
            }
            _votes[index] = null;
            _restockWindows[index]?.Close();
            pane.SetOff(true);
        }
        else
        {
            pane.SetOff(false);
            if (pane.Core is CoreWebView2 core)
                core.Navigate(GameUrlForDeck);
            else if (_env is not null)
            {
                // Estava desligada desde que o deck abriu: o navegador dela é criado agora.
                await pane.InitializeAsync(_env, GameUrlForDeck, _config.Zoom[index], _config.Muted[index], SelfTestSetup, RepairExtensions);
                await RefreshExtensionButtonsAsync();
            }
            _voteRefreshAt[index] = DateTime.Now;
            if (_config.LivesAccounts.Contains(index + 1))
                await OpenLivesAsync(index);
        }
        UpdateBar();
    }

    // ---------- Recompra ----------

    /// <summary>A janela da recompra da conta (o carrinho no cabeçalho); aberta de novo, só vem para a frente.</summary>
    private void ShowRestockWindow(int index)
    {
        if (_restockWindows[index] is RestockWindow open)
        {
            open.Activate();
            return;
        }
        var pane = _panes[index];
        var window = new RestockWindow(pane.AccountName, pane.RestockConfig);
        window.Changed += () =>
        {
            SaveConfig();
            _ = ApplyRestockAsync(index);
        };
        window.FormClosed += (_, _) => _restockWindows[index] = null;
        _restockWindows[index] = window;
        window.Show(this);
        _ = RefreshRestockAsync(index);
    }

    /// <summary>A configuração nova vale na hora: vai para a página e o carrinho e a janela mostram o resultado.</summary>
    private async Task ApplyRestockAsync(int index)
    {
        await _panes[index].PushRestockAsync();
        await RefreshRestockAsync(index);
    }

    /// <summary>Lê da página o estoque e o registro, para o carrinho do cabeçalho e a janela aberta.</summary>
    private async Task RefreshRestockAsync(int index)
    {
        if (_restockBusy[index])
            return;
        _restockBusy[index] = true;
        try
        {
            var status = await _panes[index].ReadRestockAsync();
            _panes[index].ShowRestock(status);
            _restockWindows[index]?.ShowStatus(status);
        }
        finally
        {
            _restockBusy[index] = false;
        }
    }

    private void RenameAccount(int index)
    {
        var name = Prompt.Ask(this, "Renomear conta", "Nome que aparece no deck (por exemplo, o nick do jogo):", _config.AccountNames[index]);
        if (string.IsNullOrWhiteSpace(name))
            return;
        _config.AccountNames[index] = name;
        _panes[index].AccountName = name;
        _panes[index].RefreshHeader();
        SaveConfig();
        UpdateBar();
    }

    // ---------- Extensões ----------

    private void OpenExtensions()
    {
        if (_env is null)
            return;
        using var dialog = new ExtensionsDialog(_panes, _store, _env, _root, () => _ = RefreshExtensionButtonsAsync());
        dialog.ShowDialog(this);
    }

    private async Task RefreshExtensionButtonsAsync()
    {
        foreach (var pane in _panes)
        {
            if (pane.Profile is not CoreWebView2Profile profile)
                continue;
            var extensions = await profile.GetBrowserExtensionsAsync();
            _hasSniper[pane.Index] = extensions.Any(e => e.Id == RotomSniper.Id && e.IsEnabled);
            _sniperButton.Visible = _hasSniper.Any(h => h);
            pane.SetExtensions(extensions
                .Where(e => e.IsEnabled)
                .Select(e => _store.Find(e.Id))
                .OfType<ExtensionInfo>()
                .Where(info => info.Popup is not null));
        }
    }

    private async Task<PopupWindow?> OpenExtensionPopupAsync(AccountPane pane, ExtensionInfo extension, Point anchor)
    {
        if (_env is null || extension.Popup is null)
            return null;

        var key = (pane.Index, extension.Id);
        if (extension.OpensWindow && _extensionWindows.TryGetValue(key, out var open) && !open.IsDisposed)
        {
            if (open.WindowState == FormWindowState.Minimized)
                open.WindowState = FormWindowState.Normal;
            open.Activate();
            return open;
        }

        var popup = await PopupWindow.CreateAsync(_env, pane.ProfileName,
            extension.OpensWindow ? new Size(1160, 760) : new Size(420, 580), closeOnDeactivate: !extension.OpensWindow);
        if (extension.OpensWindow)
        {
            var area = Screen.FromControl(this).WorkingArea;
            popup.Location = new Point(area.X + Math.Max(0, (area.Width - popup.Width) / 2), area.Y + Math.Max(0, (area.Height - popup.Height) / 2));
            _extensionWindows[key] = popup;
        }
        else
        {
            popup.PlaceBelow(anchor);
        }
        popup.WebView.CoreWebView2.Navigate($"chrome-extension://{extension.Id}/{extension.Popup}");
        popup.Show(this);
        return popup;
    }

    // ---------- Eco e zoom ----------

    private static readonly double[] ZoomLevels = { 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5 };

    /// <summary>O zoom (o mesmo em todas as contas) no botão; − e + apagam nas pontas.</summary>
    private void ShowZoom()
    {
        double zoom = _config.Zoom[0];
        _zoomButton.Text = $"Zoom {zoom * 100:0}%";
        _zoomOutButton.Enabled = zoom > ZoomLevels[0] + 0.001;
        _zoomInButton.Enabled = zoom < ZoomLevels[^1] - 0.001;
    }

    /// <summary>O − e o +: o nível de zoom anterior ou o seguinte da lista.</summary>
    private void StepZoom(int direction)
    {
        double zoom = _config.Zoom[0];
        SetZoomAll(direction < 0
            ? ZoomLevels.LastOrDefault(level => level < zoom - 0.001, ZoomLevels[0])
            : ZoomLevels.FirstOrDefault(level => level > zoom + 0.001, ZoomLevels[^1]));
    }

    private void FillZoomMenu()
    {
        var items = _zoomButton.DropDownItems;
        items.Clear();
        foreach (var level in ZoomLevels)
        {
            var item = new ToolStripMenuItem($"{level * 100:0}%") { Checked = Math.Abs(_config.Zoom[0] - level) < 0.01 };
            item.Click += (_, _) => SetZoomAll(level);
            items.Add(item);
        }
    }

    /// <summary>O eco só repete cliques; o zoom vale para todas as contas com ele ligado ou não.</summary>
    private void SetEcho(bool on)
    {
        _echo = on;
        _echoButton.Text = on ? "⇄ Eco ligado" : "⇄ Eco";
        _echoButton.ForeColor = on ? Theme.Vote : Theme.Text;
    }

    /// <summary>
    /// Zoom (Ctrl + roda, Ctrl + 0) ou som de uma conta mudou. O zoom vale sempre para todas as contas. O WebView2
    /// só avisa zoom mudado pela pessoa, nunca o que o deck põe, então espalhar o zoom não volta aqui.
    /// </summary>
    private void OnPaneSettingsChanged(AccountPane pane)
    {
        _config.Muted[pane.Index] = pane.Muted;
        SetZoomAll(pane.WebView.ZoomFactor);
    }

    private void SetZoomAll(double zoom)
    {
        foreach (var pane in _panes)
        {
            // Só quem está diferente: assim a mudança não fica voltando de uma conta para outra.
            if (Math.Abs(pane.WebView.ZoomFactor - zoom) > 0.001)
                pane.WebView.ZoomFactor = zoom;
            _config.Zoom[pane.Index] = zoom;
        }
        ShowZoom();
    }

    /// <summary>Repete nas outras contas o clique feito numa; avisa no cabeçalho de quem não tem esse botão.</summary>
    private async Task EchoAsync(AccountPane source, Echo.Click click)
    {
        if (!_echo)
            return;
        var script = Echo.ReplayScript(click);
        await Task.WhenAll(_panes.Where(p => p != source && p.Core is not null && !p.Off).Select(async pane =>
        {
            string result;
            try
            {
                result = await pane.WebView.ExecuteScriptAsync(script);
            }
            catch
            {
                result = "false";
            }
            if (result != "true")
                pane.Note($"eco: \"{(click.Text.Length > 0 ? click.Text : "esse botão")}\" não está nesta tela");
        }));
    }

    // ---------- Rotom Sniper ----------

    /// <summary>Ligar/desligar em todas as contas, e uma linha por conta com o estado dela (clicar troca só ela).</summary>
    private async void FillSniperMenu()
    {
        var items = _sniperButton.DropDownItems;
        items.Clear();
        items.Add("Ligar o sniper em todas as contas", null, async (_, _) => await SetSniperAsync(null, true));
        items.Add("Desligar o sniper em todas as contas", null, async (_, _) => await SetSniperAsync(null, false));
        items.Add(new ToolStripSeparator());
        var lines = new List<(int Index, ToolStripMenuItem Item)>();
        for (int i = 0; i < Accounts; i++)
        {
            if (!_hasSniper[i])
                continue;
            if (_panes[i].Off)
            {
                items.Add(new ToolStripMenuItem($"{_config.AccountNames[i]}: conta desligada") { Enabled = false });
                continue;
            }
            var item = new ToolStripMenuItem($"{_config.AccountNames[i]}: conferindo…") { Enabled = false };
            items.Add(item);
            lines.Add((i, item));
        }
        foreach (var (index, item) in lines)
        {
            bool? on = _panes[index].Core is CoreWebView2 core ? await RotomSniper.GetEnabledAsync(core) : null;
            if (on is not bool enabled)
            {
                item.Text = $"{_config.AccountNames[index]}: o Rotom não está na página (recarregue a conta)";
                continue;
            }
            item.Text = $"{_config.AccountNames[index]}: {(enabled ? "ligado" : "desligado")}";
            item.Checked = enabled;
            item.Enabled = true;
            item.Click += async (_, _) => await SetSniperAsync(index, !enabled);
        }
    }

    /// <summary>Liga ou desliga o sniper numa conta (ou em todas, com <paramref name="only"/> nulo).</summary>
    private async Task SetSniperAsync(int? only, bool enabled)
    {
        var failed = new List<string>();
        for (int i = 0; i < Accounts; i++)
        {
            if (!_hasSniper[i] || _panes[i].Off || only is int one && one != i)
                continue;
            if (_panes[i].Core is not CoreWebView2 core || !await RotomSniper.SetEnabledAsync(core, enabled))
                failed.Add(_config.AccountNames[i]);
        }
        // Onde deu certo, o próprio Rotom avisa na página do jogo ("AUTO-SNIPE ATIVADO/PAUSADO").
        if (failed.Count > 0)
            MessageBox.Show(this,
                $"Não consegui {(enabled ? "ligar" : "desligar")} o sniper em: {string.Join(", ", failed)}.\n\n" +
                "O deck mexe no Rotom pela página do jogo: recarregue a conta (⟳ no cabeçalho) e tente de novo.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ---------- Voto no TopIdle ----------

    /// <summary>A cada segundo: atualiza os botões de voto e confere no TopIdle as contas cuja hora chegou.</summary>
    private void UpdateVotes()
    {
        // O autoteste não sai para a internet; ele confere o voto por partes.
        if (_env is null || _selfTest)
            return;
        if (_topIdleGameId is null)
        {
            if (!_topIdleLookup && DateTime.Now >= _topIdleRetryAt)
                _ = FindTopIdleGameAsync();
            return;
        }
        for (int i = 0; i < Accounts; i++)
        {
            if (!_voteBusy[i] && DateTime.Now >= _voteRefreshAt[i] && _panes[i].Core is not null && !_panes[i].Off)
                _ = RefreshVoteAsync(i);
            RenderVote(i);
        }
        RenderVoteStatus();
    }

    private async Task<string?> FindTopIdleGameAsync()
    {
        _topIdleLookup = true;
        _topIdleRetryAt = DateTime.Now.AddMinutes(10);
        try
        {
            _topIdleGameId = await TopIdle.FindGameIdAsync("pokeidle.io");
        }
        catch
        {
            // Sem internet ou TopIdle fora do ar: tenta de novo daqui a pouco.
        }
        finally
        {
            _topIdleLookup = false;
        }
        return _topIdleGameId;
    }

    private async Task RefreshVoteAsync(int index)
    {
        if (_topIdleGameId is not string gameId || _panes[index].Core is not CoreWebView2 core)
            return;
        _voteBusy[index] = true;
        try
        {
            var state = await TopIdle.GetStateAsync(core.CookieManager, gameId);
            _votes[index] = state;
            // Confere de novo a cada 30 min, ou logo depois que o próximo voto liberar
            // (já votou mas o TopIdle ainda não deu a hora do próximo: de novo em 2 min).
            var next = DateTime.Now.AddMinutes(30);
            if (state.LoggedIn && !state.CanVote)
                next = state.NextVoteAt is not DateTime at ? DateTime.Now.AddMinutes(2)
                    : at > DateTime.Now ? (at.AddSeconds(3) < next ? at.AddSeconds(3) : next) : DateTime.Now.AddMinutes(1);
            _voteRefreshAt[index] = next;
        }
        catch
        {
            _voteRefreshAt[index] = DateTime.Now.AddMinutes(5);
        }
        finally
        {
            _voteBusy[index] = false;
        }
        RenderVote(index);
        RenderVoteStatus();
    }

    private void RenderVote(int index)
    {
        var pane = _panes[index];
        if (_topIdleGameId is null)
        {
            pane.HideVote();
            return;
        }
        var state = _votes[index];
        if (state is null)
            pane.SetVote("🗳 TopIdle", Theme.Muted, false, "Conferindo o voto desta conta no TopIdle…");
        else if (!state.LoggedIn)
            pane.SetVote("🗳 Votar", Theme.Text, false,
                "Votar no pokeidle pelo TopIdle (o jogo dá recompensa por voto). Na primeira vez, entre no TopIdle " +
                "com o Discord ou o Google na janela que abre: o login fica salvo nesta conta.");
        else if (state.Ready)
            pane.SetVote("🗳 Votar", Theme.Vote, true,
                $"Voto liberado no TopIdle ({state.User}). Clique para votar: quem vota é você, na página do TopIdle.");
        else if (state.NextVoteAt is DateTime at)
            pane.SetVote("🗳 " + FormatLeft(at - DateTime.Now), Theme.Muted, false,
                $"Esta conta já votou. O próximo voto libera {FormatWhen(at)}.");
        else
            // Acabou de votar: o TopIdle ainda não disse quando libera o próximo.
            pane.SetVote("🗳 ✓ Votou", Theme.Muted, false, "Esta conta já votou. Conferindo no TopIdle quando libera o próximo voto…");
    }

    /// <summary>A janela de voto viu o voto entrar: o botão vira "votou" e o TopIdle é consultado logo em seguida.</summary>
    private void MarkVoted(int index)
    {
        _votes[index] = (_votes[index] ?? new VoteState(true, true, null, null)) with { LoggedIn = true, CanVote = false, NextVoteAt = null };
        _voteRefreshAt[index] = DateTime.Now.AddSeconds(2);
        RenderVote(index);
        RenderVoteStatus();
    }

    private void RenderVoteStatus()
    {
        if (_topIdleGameId is null)
        {
            _voteStatus.Text = "";
            return;
        }
        var on = Enumerable.Range(0, Accounts).Where(i => !_panes[i].Off).ToList();
        var ready = on.Where(i => _votes[i]?.Ready == true).Select(i => _config.AccountNames[i]).ToList();
        var next = on.Select(i => _votes[i]).Where(v => v is { LoggedIn: true, Ready: false, NextVoteAt: not null }).Select(v => v!.NextVoteAt!.Value)
            .DefaultIfEmpty().Min();
        _voteStatus.ForeColor = ready.Count > 0 ? Theme.Vote : Theme.Muted;
        _voteStatus.Text = ready.Count > 0 ? "🗳 Voto liberado: " + string.Join(", ", ready)
            : next > DateTime.Now ? "🗳 Próximo voto em " + FormatLeft(next - DateTime.Now)
            : "🗳 Vote no TopIdle pelo botão de cada conta";
    }

    private static string FormatLeft(TimeSpan left) =>
        left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes:00} min" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";

    private static string FormatWhen(DateTime at) =>
        at.Date == DateTime.Today ? $"hoje às {at:HH:mm}" : at.Date == DateTime.Today.AddDays(1) ? $"amanhã às {at:HH:mm}" : $"em {at:dd/MM} às {at:HH:mm}";

    /// <summary>
    /// Abre a página de voto do TopIdle no navegador da conta (onde fica o login do TopIdle dela), já com
    /// o nick do personagem. Quem vota é você; o deck percebe o voto registrado e atualiza o botão.
    /// </summary>
    private async Task OpenVoteAsync(AccountPane pane, string? url)
    {
        if (_env is null)
            return;
        int index = pane.Index;
        if (_voteWindows[index] is { IsDisposed: false } open)
        {
            if (url is not null)
                open.WebView.CoreWebView2?.Navigate(url);
            if (open.WindowState == FormWindowState.Minimized)
                open.WindowState = FormWindowState.Normal;
            open.Activate();
            return;
        }

        if ((_topIdleGameId ?? await FindTopIdleGameAsync()) is not string gameId)
        {
            MessageBox.Show(this, "Não consegui falar com o TopIdle agora. Confira a internet e tente de novo; as contas seguem rodando.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        url ??= TopIdle.VoteUrl(gameId, await pane.ReadNickAsync());

        var window = await PopupWindow.CreateAsync(_env, pane.ProfileName, new Size(1100, 820), closeOnDeactivate: false);
        var area = Screen.FromControl(this).WorkingArea;
        window.Location = new Point(area.X + Math.Max(0, (area.Width - window.Width) / 2), area.Y + Math.Max(0, (area.Height - window.Height) / 2));
        string prefix = $"Votar com {pane.AccountName} · quem vota é você, nesta página do TopIdle";
        window.Text = prefix;
        var core = window.WebView.CoreWebView2;
        core.DocumentTitleChanged += (_, _) => window.Text = prefix;
        core.WebResourceResponseReceived += (_, e) =>
        {
            var uri = e.Request.Uri;
            if (TopIdle.IsVoteRegistered(e.Request.Method, uri, e.Response?.StatusCode ?? 0, gameId))
            {
                prefix = $"✓ Voto registrado com {pane.AccountName}! Pode fechar esta janela";
                window.Text = prefix;
                MarkVoted(index);
            }
            else if (TopIdle.IsLoginChange(uri))
            {
                _voteRefreshAt[index] = DateTime.Now.AddSeconds(2);
            }
        };
        window.FormClosed += (_, _) =>
        {
            _voteWindows[index] = null;
            _voteRefreshAt[index] = DateTime.Now.AddSeconds(1);
        };
        _voteWindows[index] = window;
        core.Navigate(url);
        window.Show(this);
    }

    // ---------- Lives ----------

    // A live da Twitch toca só o player (o "player em janela" do site), como na Kick.
    private string TwitchUrl(string channel) => _selfTest
        ? $"https://twitch.test/twitch.html?canal={Uri.EscapeDataString(channel)}"
        : TwitchLive.PlayerUrl(channel);

    // A live da Kick toca só o player: ele avisa a Kick que a conta está assistindo, como a página do canal
    // (testado: os pontos sobem igual), gastando uns 40% menos. A página do canal só abre para o chat e os
    // pontos, com a janela das lives na tela.
    private string KickUrl(string channel) => _selfTest
        ? $"https://kick.test/kick.html?canal={Uri.EscapeDataString(channel)}"
        : $"https://player.kick.com/{channel}";

    // A página do chat, com o botão dos pontos, que abre por cima do player só no 💰: na Kick a do canal, na Twitch o
    // chat em janela (bem mais leve que a página do canal, e com o botão dos pontos e o baú).
    private string ChatUrl(LiveSite site, string channel) => (site, _selfTest) switch
    {
        (LiveSite.Kick, true) => $"https://kick.test/kick.html?canal={Uri.EscapeDataString(channel)}&chat=1",
        (LiveSite.Kick, false) => $"https://kick.com/{channel}",
        (_, true) => $"https://twitch.test/twitch.html?canal={Uri.EscapeDataString(channel)}&chat=1",
        _ => $"https://www.twitch.tv/popout/{Uri.EscapeDataString(channel)}/chat?popout=",
    };

    private async Task OpenLivesAsync(int index)
    {
        if (_env is null || _lives[index] is not null || _panes[index].Off)
            return;
        var urls = new List<string>();
        int twitchSlots = Math.Min(_config.TwitchLives, _config.TwitchChannels.Count);        if (twitchSlots > 0)
        {
            // Sem login da Twitch nesta conta ainda: uma aba para entrar, e os players só abrem depois (sem login não
            // rendem pontos). O que vale é o token: o cookie com o nick nem sempre existe.
            _twitchLogin[index] = _selfTest || (await ReadTwitchLoginAsync(index)).Token is not null;
            if (_twitchLogin[index] == false)
                urls.Add("https://www.twitch.tv/login");
        }

        int kickSlots = Math.Min(_config.KickLives, _config.KickChannels.Count);
        if (urls.Count > 0 || twitchSlots > 0 || kickSlots > 0)
        {
            _lives[index] = await LivesWindow.CreateAsync(_env, _panes[index].ProfileName, _config.AccountNames[index], urls,
                twitchSlots, kickSlots, ChatUrl, SelfTestSetup);
            var window = _lives[index]!;
            window.NextRequested += site => ShowNextPoints(index, site);
            window.PointsHeight = _config.KickPointsHeight;
            window.PointsHeightChanged += height =>
            {
                _config.KickPointsHeight = height;
                SaveConfig();
                foreach (var other in _lives.OfType<LivesWindow>())
                    other.PointsHeight = height;
            };
            ApplyLives();
            // Confere logo quem está no ar (o autoteste não sai para a internet: abre as primeiras da lista), e
            // o saldo dos pontos 1 min depois, com os players já carregados.
            _liveCheckAt = DateTime.Now;
            _kickRedeemAt = _twitchPointsAt = DateTime.Now.AddMinutes(1);
        }
        UpdateBar();
    }

    /// <summary>
    /// Pergunta à Twitch e à Kick quais canais da lista estão no ar (a cada 2 min, enquanto houver lives abertas).
    /// A conta que entrou na Twitch desde a última conferência fecha a página de entrar e passa a abrir os players.
    /// </summary>
    private async Task CheckLivesAsync()
    {
        _liveChecking = true;
        _liveCheckAt = DateTime.Now.AddMinutes(2);
        try
        {
            var kick = _config.KickLives > 0 ? _config.KickChannels.ToList() : new List<string>();
            var twitch = _config.TwitchLives > 0 ? _config.TwitchChannels.ToList() : new List<string>();
            var kickLive = Task.WhenAll(kick.Select(KickLive.IsLiveAsync));
            var twitchLive = TwitchLive.LiveAsync(twitch);
            var kickNow = await kickLive;
            for (int i = 0; i < kick.Count; i++)
                _kickLive[kick[i]] = kickNow[i];
            foreach (var (channel, live) in await twitchLive)
                _twitchLive[channel] = live;
            for (int i = 0; i < Accounts; i++)
                if (_lives[i] is LivesWindow window && _twitchLogin[i] == false && (await ReadTwitchLoginAsync(i)).Token is not null)
                {
                    _twitchLogin[i] = true;
                    window.CloseFixed();
                }
        }
        finally
        {
            _liveChecking = false;
        }
        ApplyLives();
    }

    /// <summary>
    /// A cada 30 s, se as lives abertas estão mesmo tocando (vídeo parado não rende pontos); a que caiu e não
    /// voltou sozinha é recarregada.
    /// </summary>
    private async Task WatchLivesAsync()
    {
        if (_watching)
            return;
        _watching = true;
        try
        {
            foreach (var window in _lives.OfType<LivesWindow>().ToList())
                if (!window.IsDisposed)
                    await window.WatchAsync();
        }
        finally
        {
            _watching = false;
        }
        UpdateBar();
    }

    // A API da Kick (no autoteste, a de mentira em kick.test).
    private string KickApi => _selfTest ? "https://kick.test" : "https://kick.com";

    /// <summary>
    /// A cada 5 min, nas contas com o resgate marcado em Lives → Config…: o saldo de cada live da Kick aberta na conta
    /// e, se der, 1 resgate do bônus escolhido para ela em cada uma (as horas acumulam no jogo).
    /// </summary>
    private async Task RedeemKickAsync()
    {
        _kickRedeeming = true;
        _kickRedeemAt = DateTime.Now + PointsRedeemer.KickInterval;
        try
        {
            for (int i = 0; i < Accounts; i++)
            {
                if (_config.RedeemKick[i] is not KickReward reward || _lives[i] is not LivesWindow window || !window.KickChannels.Any())
                    continue;
                var redeemer = _redeemKick[i];
                var token = await ReadKickTokenAsync(i);
                redeemer.NoLogin = token is null;
                if (token is null)
                    continue;
                foreach (var (channel, result) in await window.EvalLivesAsync(LiveSite.Kick, c => redeemer.KickScript(KickApi, c, token, reward)))
                    if (redeemer.Apply(channel, result) is string note)
                        _panes[i].Note(note);
            }
        }
        finally
        {
            _kickRedeeming = false;
        }
        UpdateBar();
    }

    // A API da Twitch (no autoteste, a de mentira em twitch.test).
    private string TwitchApi => _selfTest ? "https://twitch.test" : "https://gql.twitch.tv";

    /// <summary>
    /// A cada 2 min, nas contas com live da Twitch aberta: o saldo de cada uma, o baú de bônus se tiver (com o baú ligado
    /// em Lives → Config…) e, nas contas com o resgate marcado, 1 resgate do bônus escolhido em cada live, se der.
    /// </summary>
    private async Task TwitchPointsAsync()
    {
        _twitchPointsBusy = true;
        _twitchPointsAt = DateTime.Now + PointsRedeemer.TwitchInterval;
        try
        {
            for (int i = 0; i < Accounts; i++)
            {
                var reward = _config.RedeemTwitch[i];
                bool claim = _config.ClaimTwitchBonus;
                if ((reward is null && !claim) || _lives[i] is not LivesWindow window || !window.TwitchChannels.Any())
                    continue;
                var redeemer = _redeemTwitch[i];
                var (token, device) = await ReadTwitchLoginAsync(i);
                redeemer.NoLogin = token is null;
                if (token is null)
                    continue;
                foreach (var (channel, result) in await window.EvalLivesAsync(LiveSite.Twitch,
                             c => redeemer.TwitchScript(TwitchApi, c, token, device, reward, claim)))
                    if (redeemer.Apply(channel, result) is string note)
                        _panes[i].Note(note);
            }
        }
        finally
        {
            _twitchPointsBusy = false;
        }
        UpdateBar();
    }

    /// <summary>O login da Kick salvo no perfil da conta: o cookie session_token, que o site manda como "Bearer".</summary>
    private async Task<string?> ReadKickTokenAsync(int index)
    {
        if (_panes[index].Core is not CoreWebView2 core)
            return null;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync("https://kick.com");
            var token = cookies.FirstOrDefault(c => c.Name == "session_token")?.Value;
            return string.IsNullOrEmpty(token) ? null : Uri.UnescapeDataString(token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deixa abertas, em cada janela de lives, só as lives da Twitch e da Kick que estão no ar (até o limite do deck).</summary>
    private void ApplyLives()
    {
        for (int i = 0; i < Accounts; i++)
        {
            if (_lives[i] is not LivesWindow window)
                continue;
            // Antes da primeira resposta de cada site não abre nada: a live pode estar offline. Na Twitch, sem o login
            // na conta também não: o player sem login não rende pontos.
            if (_selfTest || _twitchLive.Count > 0 || _twitchLogin[i] == false)
            {
                // Sem as fechadas no botão twitch da conta, e cada uma levou a vaga dela junto.
                var twitch = _config.Twitch[i];
                var open = _config.TwitchChannels.Where(c => !twitch.Closed.Contains(c));
                window.SetLives(LiveSite.Twitch, _twitchLogin[i] == false ? new()
                    : KickLive.Pick(open, _twitchLive, twitch.SlotsOf(_config.TwitchLives)).Select(c => (c, TwitchUrl(c))).ToList());
            }
            if (_selfTest || _kickLive.Count > 0)
            {
                // Sem as fechadas no botão lives da conta, e cada uma levou a vaga dela junto.
                var kick = _config.Kick[i];
                var channels = _config.KickChannels.Where(c => !kick.Closed.Contains(c));
                window.SetLives(LiveSite.Kick, KickLive.Pick(channels, _kickLive, kick.SlotsOf(_config.KickLives)).Select(c => (c, KickUrl(c))).ToList());
            }
        }
        UpdateBar();
    }

    /// <summary>
    /// Fecha no botão do site (kick ou twitch) a live de <paramref name="channel"/> nesta conta (ou todas, com nulo). A
    /// vaga fica em branco: a conferência de 2 em 2 min não abre outra live nela até abrir de novo.
    /// </summary>
    private void CloseLive(int index, LiveSite site, string? channel)
    {
        var limit = _config.Limits(site)[index];
        foreach (var shut in channel is null ? LivesOpen(index, site) : new List<string> { channel })
            if (!limit.Closed.Contains(shut))
                limit.Closed.Add(shut);
        limit.Slots = channel is null ? 0 : Math.Max(0, limit.SlotsOf(_config.Lives(site)) - 1);
        SaveConfig();
        ApplyLives();
        _panes[index].Note(channel is null ? $"{site}: fechei todas as lives" : $"{site}: fechei a live de {channel}");
    }

    /// <summary>Abre de novo a live de <paramref name="channel"/> fechada no botão do site (ou todas, com nulo), com a vaga dela.</summary>
    private void ReopenLive(int index, LiveSite site, string? channel)
    {
        var limit = _config.Limits(site)[index];
        if (channel is null)
        {
            limit.Closed.Clear();
            limit.Slots = null;
        }
        else if (limit.Closed.Remove(channel))
        {
            limit.Slots = limit.SlotsOf(_config.Lives(site)) + 1;
            limit.Fit(_config.Lives(site));
        }
        SaveConfig();
        ApplyLives();
        _panes[index].Note(channel is null ? $"{site}: as lives voltam a abrir" : $"{site}: {channel} volta a abrir (se estiver no ar)");
    }

    private void CloseLives(int index)
    {
        _twitchLogin[index] = null;
        if (_lives[index] is LivesWindow window)
        {
            window.AllowClose = true;
            window.Close();
            _lives[index] = null;
        }
        UpdateBar();
    }

    /// <summary>
    /// O login da Twitch salvo no perfil da conta (o cookie auth-token, que o site manda como "OAuth") e o aparelho (o
    /// cookie unique_id, que o site manda como X-Device-Id). Pode ser chamado de qualquer thread; o navegador embutido
    /// só é usado na thread da interface.
    /// </summary>
    private Task<(string? Token, string? Device)> ReadTwitchLoginAsync(int index)
    {
        var result = new TaskCompletionSource<(string?, string?)>();
        async void Read()
        {
            try
            {
                if (_panes[index].Core is not CoreWebView2 core)
                {
                    result.TrySetResult((null, null));
                    return;
                }
                var cookies = await core.CookieManager.GetCookiesAsync("https://www.twitch.tv");
                var token = cookies.FirstOrDefault(c => c.Name == "auth-token")?.Value;
                var device = cookies.FirstOrDefault(c => c.Name == "unique_id")?.Value;
                // O que foi visto, para o menu explicar quando o login não aparece.
                _twitchSeen[index] = $"{cookies.Count} cookies da Twitch{(string.IsNullOrEmpty(token) ? ", sem o auth-token do login" : ", com o auth-token")}";
                result.TrySetResult(string.IsNullOrEmpty(token) ? (null, null) : (token, string.IsNullOrEmpty(device) ? null : device));
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        }
        try
        {
            if (InvokeRequired)
                BeginInvoke(Read);
            else
                Read();
        }
        catch (InvalidOperationException)
        {
            // O deck está fechando.
            result.TrySetResult((null, null));
        }
        return result.Task;
    }

    private async Task ToggleLivesAsync(int index)
    {
        int account = index + 1;
        if (_config.LivesAccounts.RemoveAll(a => a == account) > 0)
        {
            CloseLives(index);
        }
        else
        {
            _config.LivesAccounts.Add(account);
            _config.LivesAccounts.Sort();
            await OpenLivesAsync(index);
        }
        SaveConfig();
        UpdateBar();
    }

    /// <summary>O menu Lives; <paramref name="hidden"/> (Shift apertado ao abrir) põe também o resgate em lote.</summary>
    private void FillLivesMenu(bool hidden)
    {
        var items = _livesButton.DropDownItems;
        items.Clear();
        items.Add(new ToolStripMenuItem("Marque a conta vinculada à sua Twitch/Kick (a main):") { Enabled = false });
        for (int i = 0; i < Accounts; i++)
        {
            int index = i;
            string state = _panes[i].Off ? " — conta desligada (as lives voltam ao ligar)"
                : _lives[i] is not null ? " — " + LivesSummary(i) : "";
            var item = new ToolStripMenuItem(_config.AccountNames[i] + state) { Checked = _config.LivesAccounts.Contains(i + 1) };
            item.Click += async (_, _) => await ToggleLivesAsync(index);
            items.Add(item);
        }
        items.Add(new ToolStripSeparator());
        items.Add("Mostrar as lives (para entrar na Twitch/Kick ou resgatar pontos)", null, (_, _) => ShowLives())
            .Enabled = _lives.Any(l => l is not null);
        int today = _redeemLog.Lines.Count(l => l.StartsWith(DateTime.Now.ToString("dd/MM/yy")));
        items.Add($"Registro dos resgates… ({today} hoje)", null, (_, _) => ShowRedeemLog());
        if (hidden)
            items.Add("Resgate em lote… (pontos da Twitch e da Kick)", null, (_, _) => ShowBulkRedeem());
        items.Add("Config…", null, async (_, _) => await EditChannelsAsync());
    }

    /// <summary>
    /// O resgate em lote (uma janela só; clicar de novo traz ela para a frente). Vem escolhida a 1ª conta com lives, e
    /// os pedidos rodam num player do site (Twitch ou Kick) escondido no perfil da conta (no autoteste, o de mentira).
    /// </summary>
    private void ShowBulkRedeem()
    {
        if (_env is null)
            return;
        if (_bulkWindow is { IsDisposed: false })
        {
            _bulkWindow.Activate();
            return;
        }
        int account = _config.LivesAccounts.Where(a => a >= 1 && a <= Accounts).Select(a => a - 1).DefaultIfEmpty(0).First();
        var accounts = Enumerable.Range(0, Accounts)
            .Select(i => (_config.AccountNames[i] + (_panes[i].Off ? " (desligada)" : ""), _panes[i].ProfileName)).ToList();
        _bulkWindow = new BulkRedeemDialog(_env, accounts, account,
            site => site == LiveSite.Kick ? KickApi : TwitchApi,
            (site, channel) => site == LiveSite.Kick ? KickUrl(channel) : TwitchUrl(channel), SelfTestSetup,
            (i, site, text) => _redeemLog.Add(_config.AccountNames[i], site, text));
        _bulkWindow.Show(this);
    }

    /// <summary>A janela do registro dos resgates (uma só; clicar de novo traz ela para a frente).</summary>
    private void ShowRedeemLog()
    {
        if (_redeemLogWindow is { IsDisposed: false })
        {
            _redeemLogWindow.Activate();
            return;
        }
        _redeemLogWindow = new RedeemLogWindow(_redeemLog);
        _redeemLogWindow.Show(this);
    }

    private void ShowLives()
    {
        int order = 0;
        foreach (var window in _lives.OfType<LivesWindow>())
            window.Reveal(Screen.FromControl(this).WorkingArea, order: order++);
    }

    /// <summary>
    /// Os chats de <paramref name="site"/> da conta, com o botão dos pontos, para resgatar; "Próxima conta" na janela
    /// passa para a seguinte que tem live daquele site aberta.
    /// </summary>
    private void ShowPoints(int index, LiveSite site)
    {
        var with = Enumerable.Range(0, Accounts).Where(i => _lives[i]?.Tabs(site).Any() == true).ToList();
        if (_lives[index] is not LivesWindow window || !with.Contains(index))
            return;
        foreach (int i in with)
            _lives[i]!.HasNext = with.Count > 1;
        window.Reveal(Screen.FromControl(this).WorkingArea, window.Tabs(site).First().Tab);
    }

    private void ShowNextPoints(int index, LiveSite site)
    {
        for (int step = 1; step < Accounts; step++)
        {
            int next = (index + step) % Accounts;
            if (_lives[next]?.Tabs(site).Any() != true)
                continue;
            _lives[index]?.Park();
            ShowPoints(next, site);
            return;
        }
    }

    private async Task EditChannelsAsync()
    {
        using var dialog = new ChannelsDialog(_config.TwitchChannels, _config.KickChannels, _config.TwitchLives, _config.KickLives,
            _config.AccountNames, _config.RedeemKick, _config.RedeemTwitch, _config.ClaimTwitchBonus);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _config.TwitchChannels = dialog.TwitchChannels;
        _config.KickChannels = dialog.KickChannels;
        _config.TwitchLives = Math.Clamp(dialog.TwitchLives, 0, TwitchLive.MaxSlots);
        foreach (var twitch in _config.Twitch)
            twitch.Fit(_config.TwitchLives);
        SetKickLives(dialog.KickLives);
        // Salvar de novo também volta a resgatar na conta que parou (depois de vincular a Kick ou a Twitch no jogo).
        _config.RedeemKick = dialog.RedeemKick;
        _config.RedeemTwitch = dialog.RedeemTwitch;
        _config.ClaimTwitchBonus = dialog.ClaimTwitchBonus;
        foreach (var redeemer in _redeemKick.Concat(_redeemTwitch))
            redeemer.Restart();
        SaveConfig();
        _kickLive.Clear();
        _twitchLive.Clear();
        // Reabre as lives de cada conta marcada (também a que estava sem nenhuma, se agora tem o que abrir).
        for (int i = 0; i < Accounts; i++)
        {
            if (!_config.LivesAccounts.Contains(i + 1) || _panes[i].Off)
                continue;
            CloseLives(i);
            await OpenLivesAsync(i);
        }
    }

    /// <summary>O limite de lives da Kick por conta; vale quando as lives reabrem (salvar os canais já reabre).</summary>
    private void SetKickLives(int lives)
    {
        _config.KickLives = Math.Clamp(lives, 0, KickLive.MaxSlots);
        foreach (var kick in _config.Kick)
            kick.Fit(_config.KickLives);
    }

    private void SaveConfig()
    {
        if (!_selfTest)
            _config.Save(_configPath);
    }

    // ---------- Autoteste ----------

    private static string SelfTestReport => Path.Combine(Path.GetTempPath(), "PokeIdleDeck-selftest.txt");

    /// <summary>Páginas de teste (servidas como https://deck.test/) e uma extensão de teste.</summary>
    private void PrepareSelfTestFiles()
    {
        var site = Path.Combine(_root, "site");
        Directory.CreateDirectory(site);
        File.WriteAllText(Path.Combine(site, "game.html"),
            "<!doctype html><title>Jogo-teste</title><body style='background:#234;color:#fff'>jogo<nav>" +
            "<button onclick='window.__mapa = (window.__mapa || 0) + 1'>Mapa</button> " +
            "<button onclick='window.__outro = (window.__outro || 0) + 1'>Outro</button></nav></body>");
        // O player da Twitch de teste, com o vídeo "tocando" (o tempo anda).
        File.WriteAllText(Path.Combine(site, "twitch.html"),
            "<!doctype html><title>Twitch-teste</title><video muted></video><script>const v = document.querySelector('video');" +
            " Object.defineProperty(v, 'paused', { get: () => false });" +
            " Object.defineProperty(v, 'currentTime', { get: () => performance.now() / 1000 });</script>");
        // As poções e os revives do espelho de itens do jogo (/assets/items.json), que a recompra lê.
        Directory.CreateDirectory(Path.Combine(site, "assets"));
        File.WriteAllText(Path.Combine(site, "assets", "items.json"), """
            { "items": [
              { "id": 200, "name": "Small Potion", "category": "heal", "npcPrice": 50, "healAmount": 60 },
              { "id": 203, "name": "Hyper Potion", "category": "heal", "npcPrice": 800, "healAmount": 1000 },
              { "id": 204, "name": "Ultimate Potion", "category": "heal", "npcPrice": 1500, "healAmount": 3000 },
              { "id": 205, "name": "Revive", "category": "revive", "npcPrice": 600, "revivePct": 0.5 },
              { "id": 206, "name": "Max Revive", "category": "revive", "npcPrice": 2500, "revivePct": 1 },
              { "id": 207, "name": "Medicine", "category": "heal", "npcPrice": 0, "healAmount": 20 },
              { "id": 1, "name": "Pedra", "category": "stone", "npcPrice": 5000 }
            ] }
            """);
        // Live de teste com vídeo "tocando" (o tempo anda); a do yetz fica parada, sem vídeo para tocar.
        File.WriteAllText(Path.Combine(site, "kick.html"),
            "<!doctype html><title>Kick-teste</title><video muted></video><div id=channel-chatroom><div id=chatroom-messages>msgs</div>botão dos pontos</div><script>" +
            "if (!location.search.includes('yetz')) { const v = document.querySelector('video');" +
            " Object.defineProperty(v, 'paused', { get: () => false });" +
            " Object.defineProperty(v, 'currentTime', { get: () => performance.now() / 1000 }); }</script>");

        var extension = Path.Combine(_root, "ext-teste");
        Directory.CreateDirectory(extension);
        File.WriteAllText(Path.Combine(extension, "manifest.json"),
            """
            {
              "manifest_version": 3,
              "name": "Extensao teste",
              "version": "1.0",
              "permissions": [ "storage" ],
              "action": { "default_popup": "popup.html" },
              "content_scripts": [ { "matches": [ "https://deck.test/*" ], "js": [ "cs.js" ] } ]
            }
            """);
        File.WriteAllText(Path.Combine(extension, "cs.js"), "document.documentElement.dataset.ext = 'ok';");
        File.WriteAllText(Path.Combine(extension, "popup.html"), "<!doctype html><title>Popup-teste</title><body>popup</body>");

        // Como o Rotom Sniper: ícone sem popup, que abre uma página da própria extensão.
        var withPage = Path.Combine(_root, "ext-pagina");
        Directory.CreateDirectory(withPage);
        File.WriteAllText(Path.Combine(withPage, "manifest.json"),
            """
            { "manifest_version": 3, "name": "Extensao pagina", "version": "1.0",
              "action": { "default_title": "abrir" }, "background": { "service_worker": "bg.js" } }
            """);
        File.WriteAllText(Path.Combine(withPage, "bg.js"), "");
        File.WriteAllText(Path.Combine(withPage, "cockpit.html"), "<!doctype html><title>Cockpit-teste</title>");
    }

    /// <summary>
    /// Autoteste (<c>--self-test</c>): abre 4 contas de teste e confere sessões separadas, layouts,
    /// conta escondida rodando no ritmo normal, extensão por conta e popup dela, lives escondidas,
    /// minimizar e tela cheia. Grava o resultado em %TEMP%\PokeIdleDeck-selftest.txt e fecha.
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        var log = new List<string>();
        void Check(string name, bool ok, string detail = "") => log.Add($"{(ok ? "OK  " : "FALHOU")} {name} {detail}");
        async Task<bool> WaitFor(Func<bool> condition, int seconds)
        {
            var deadline = DateTime.Now.AddSeconds(seconds);
            while (!condition() && DateTime.Now < deadline)
                await Task.Delay(250);
            return condition();
        }
        Task<string> Eval(AccountPane pane, string script) => pane.WebView.ExecuteScriptAsync(script);
        bool Shape(int cols, int rows) => _panes.All(p => p.Visible && p.WebView.Height > 100
            && p.Bounds == Cell(_grid.ClientRectangle, p.Index % cols, p.Index / cols, cols, rows));
        string Detail() => string.Join(" | ", _panes.Select(p => $"{p.Index + 1}:{(p.Visible ? p.Bounds.ToString() : "escondida")}"));

        try
        {
            bool loaded = await WaitFor(() => _panes.Take(3).All(p => p.Status == PaneStatus.Ok), 40);
            Check("conta desligada ao abrir o deck não carrega nada",
                loaded && _panes[3].Off && _panes[3].Core is null && _panes[3].StatusText == "desligada",
                $"conta4: desligada={_panes[3].Off} navegador={(_panes[3].Core is null ? "não criado" : "criado")} status='{_panes[3].StatusText}'");
            await SetPaneOffAsync(3, false);
            loaded = await WaitFor(() => _panes.All(p => p.Status == PaneStatus.Ok), 40);
            Check("4 contas abriram (a 4ª ligada no ⏻)", loaded && _panes.All(p => p.Core?.DocumentTitle == "Jogo-teste"),
                string.Join(", ", _panes.Select(p => p.Status)));

            // Menos animações: o jogo vê o "reduzir movimento" do sistema ligado; o botão desliga e religa na hora.
            const string ReducedMotion = "matchMedia('(prefers-reduced-motion: reduce)').matches";
            async Task<string> Motion() => string.Join(",", await Task.WhenAll(_panes.Select(p => Eval(p, ReducedMotion))));
            var motionStart = await Motion();
            await SetReduceMotionAsync(false);
            var motionOff = await Motion();
            await SetReduceMotionAsync(true);
            var motionOn = await Motion();
            Check("menos animações no jogo (e o botão liga e desliga na hora)",
                motionStart == "true,true,true,true" && motionOff == "false,false,false,false" && motionOn == "true,true,true,true"
                && _config.ReduceMotion,
                $"ao abrir={motionStart} desligado={motionOff} religado={motionOn}");

            // ULTRA: o botão liga em todas pelo interruptor das Configurações do jogo e desliga pelo "Voltar ao
            // jogo" da caixa, na hora; fora do jogo (a 4ª, no portal) só guarda a escolha. Ligado, a conta que
            // recarrega volta a ele mesmo se tinha saído. O jogo-teste ganha as peças do jogo.
            const string FakeUltra = """
                (() => {
                  localStorage.removeItem('cfg-ultra');
                  const ultra = on => {
                    localStorage.setItem('cfg-ultra', on ? '1' : '0');
                    document.documentElement.classList.toggle('modo-ultra', on);
                    document.querySelector('#ultra').innerHTML = on ? '<button id=ultra-sair>Voltar ao jogo</button>' : '';
                    if (on) document.querySelector('#ultra-sair').onclick = () => ultra(false);
                  };
                  document.body.insertAdjacentHTML('beforeend', '<div id=app></div><button id=btn-config>Config</button><div id=modal></div><div id=ultra></div>');
                  document.querySelector('#btn-config').onclick = () => {
                    document.querySelector('#modal').innerHTML = '<button id=cfg-ultra>ULTRA</button>';
                    document.querySelector('#cfg-ultra').onclick = () => {
                      ultra(localStorage.getItem('cfg-ultra') !== '1');
                      document.querySelector('#modal').innerHTML = '';
                    };
                  };
                  return 1;
                })()
                """;
            foreach (var pane in _panes)
                await Eval(pane, FakeUltra);
            await Eval(_panes[3], "document.querySelector('#app').classList.add('hidden'); 1");
            async Task<string> Ultra() => string.Join(",", await Task.WhenAll(_panes.Select(p => p.GetUltraAsync())));
            var ultraStart = await Ultra();
            await SetUltraAsync(true);
            var ultraOn = await Ultra();
            string buttonOn = _ultraButton.Text;
            await Eval(_panes[2], "localStorage.setItem('cfg-ultra', '0'); 1");
            _panes[2].Reload();
            await Task.Delay(500);
            await WaitFor(() => _panes[2].Status == PaneStatus.Ok, 15);
            string? reloaded = null;
            for (int i = 0; i < 20 && reloaded != "escolhido"; i++)
            {
                await Task.Delay(250);
                reloaded = await _panes[2].GetUltraAsync();
            }
            await SetUltraAsync(false);
            var ultraOff = await Ultra();
            Check("ULTRA do jogo liga e desliga em todas na hora, e a conta que recarrega volta a ele",
                ultraStart == "normal,normal,normal,fora" && ultraOn == "ligado,ligado,ligado,escolhido" && reloaded == "escolhido"
                && ultraOff == "normal,normal,fora,fora" && buttonOn == "⚡ ULTRA ligado" && _ultraButton.Text == "⚡ ULTRA" && !_config.Ultra,
                $"antes={ultraStart} ligado={ultraOn} 3ª recarregada={reloaded} desligado={ultraOff} botão='{buttonOn}'→'{_ultraButton.Text}'");
            foreach (var pane in _panes)
                await Eval(pane, "document.querySelectorAll('#app, #btn-config, #modal, #ultra').forEach(e => e.remove()); 1");

            // Conta parada na tela de login: o cabeçalho avisa (o captcha de lá vai juntando memória).
            await Eval(_panes[1], "document.body.insertAdjacentHTML('beforeend', '<div id=portal></div><div id=login></div>'); 1");
            await _panes[1].CheckLoginScreenAsync();
            string loginInfo = _panes[1].HeaderInfo;
            await Eval(_panes[1], "document.querySelector('#portal').remove(); document.querySelector('#login').remove(); 1");
            await _panes[1].CheckLoginScreenAsync();
            Check("aviso de conta parada na tela de login",
                loginInfo.Contains("tela de login") && !_panes[1].HeaderInfo.Contains("tela de login") && !_panes[0].HeaderInfo.Contains("tela de login"),
                $"com login='{loginInfo}' sem='{_panes[1].HeaderInfo}'");

            // Recompra: o script do deck pega o socket que o jogo abre para o próprio site. No jogo-teste ele não
            // conecta, então o socket finge estar aberto, guarda o que seria mandado e recebe o welcome à mão.
            // Ligada na conta 1 (Ultimate Potion, os valores de fábrica), ela arruma a fila do "Usar Poções" e
            // compra o que falta, uma compra por vez; a conta 2, desligada, não compra nada.
            const string FakeSocket = """
                (() => {
                  const ws = new WebSocket('wss://deck.test/');
                  Object.defineProperty(ws, 'readyState', { get: () => 1 });
                  window.__enviados = [];
                  ws.send = d => window.__enviados.push(JSON.parse(d));
                  window.__msg = o => ws.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(o) }));
                  window.__msg({ t: 'welcome',
                    catalogoBolas: [{ id: 1, nome: 'Poke Ball', priceGold: 20, compravel: true }, { id: 4, nome: 'Ultra Ball', priceGold: 300, compravel: true }],
                    itensNossos: [{ id: 70070, name: 'Golden Potion', category: 'heal', npcPrice: 5000, healPct: 0.5 }],
                    estado: { cheio: true, nick: 'teste', gold: 50000000, balls: { 4: 4000 }, items: { 204: 1000, 205: 50, 70070: 9999 },
                      activeId: 7, automation: { autoPotion: true, potionIds: [200, 204], hpLimiar: 0.3 }, pokemons: [{ id: 7, maxHp: 2000 }] } });
                  return 1;
                })()
                """;
            const string SentScript = "JSON.stringify(window.__enviados.map(m => m.t === 'shop.buy' ? `${m.kind}:${m.id}x${m.qty}` : `fila:${m.potionIds}`))";
            // O ExecuteScript devolve o texto em JSON: o JSON.stringify da página chega entre aspas.
            async Task<string> Sent(AccountPane pane) => JsonSerializer.Deserialize<string>(await Eval(pane, SentScript)) ?? "";
            await Eval(_panes[0], FakeSocket);
            await Eval(_panes[1], FakeSocket);
            _config.Restock[0].On = true;
            await ApplyRestockAsync(0);
            await Task.Delay(2500);
            var firstBuy = await Sent(_panes[0]);
            // O jogo confirma (o aviso da compra e o estoque novo): 4 s depois vêm os revives, depois as Ultra Balls.
            await Eval(_panes[0], "__msg({ t: 'batalha', ev: [{ k: 'compra', nome: 'Ultimate Potion', qtd: 4000, gasto: 6000000 }] });" +
                                  " __msg({ t: 'estado', estado: { gold: 44000000, items: { 204: 5000, 205: 50 } } }); 1");
            await Task.Delay(4500);
            await Eval(_panes[0], "__msg({ t: 'estado', estado: { items: { 204: 5000, 205: 500 } } }); 1");
            await Task.Delay(4500);
            var allBuys = await Sent(_panes[0]);
            var notBought = await Sent(_panes[1]);
            await RefreshRestockAsync(0);
            await RefreshRestockAsync(1);
            Check("recompra: arruma a fila das poções e compra poção, revive e pokebola, uma por vez (desligada não compra)",
                firstBuy == "[\"fila:204,200\",\"item:204x4000\"]" && allBuys == "[\"fila:204,200\",\"item:204x4000\",\"item:205x450\",\"ball:4x9999\"]"
                && notBought == "[]" && _panes[0].RestockState == "ligada" && _panes[1].RestockState == "desligada",
                $"conta1={allBuys} (1ª={firstBuy}) conta2={notBought} carrinhos={_panes[0].RestockState},{_panes[1].RestockState}");

            // A janela do carrinho: mostra a conta e a poção escolhida; desligar nela vale na hora, e a página que
            // recarrega recebe a configuração sozinha.
            ShowRestockWindow(0);
            var restockWindow = _restockWindows[0]!;
            await WaitFor(() => restockWindow.StateText.Contains("na conta"), 5);
            string windowState = restockWindow.StateText, windowPotion = restockWindow.PotionText;
            var picture = Path.Combine(Path.GetTempPath(), "PokeIdleDeck-recompra.png");
            using (var bitmap = new Bitmap(restockWindow.Width, restockWindow.Height))
            {
                restockWindow.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(picture);
            }
            var typed = restockWindow.TypeBallKeep("12.345");
            var typedComma = restockWindow.TypeBallKeep("30,000");
            Check("recompra: campo de quantidade aceita 12.345 e 30,000 e escreve como o registro",
                typed == (12345m, "12.345") && typedComma == (30000m, "30.000") && _config.Restock[0].Balls.Keep == 30000,
                $"12.345 → {typed} · 30,000 → {typedComma} · config={_config.Restock[0].Balls.Keep}");
            restockWindow.SetOn(false);
            await Task.Delay(500);
            var pageOff = await Eval(_panes[0], "window.__deckRecompra.summary().on");
            _panes[0].Reload();
            await Task.Delay(500);
            await WaitFor(() => _panes[0].Status == PaneStatus.Ok, 15);
            await Task.Delay(500);
            var afterReload = await Eval(_panes[0], "[window.__deckRecompra.summary().configured, window.__deckRecompra.summary().on]");
            restockWindow.Close();
            Check("recompra: janela do carrinho mostra a conta e desliga na hora; recarregada, a página recebe a configuração",
                windowState.Contains("na conta teste") && windowPotion.StartsWith("Ultimate Potion") && pageOff == "false" && !_config.Restock[0].On
                && afterReload == "[true,false]" && _panes[0].RestockState == "desligada" && _restockWindows[0] is null,
                $"estado='{windowState}' poção='{windowPotion}' página desligada={pageOff} recarregada={afterReload} carrinho={_panes[0].RestockState} (imagem em {picture})");

            await Eval(_panes[0], "localStorage.setItem('quem', 'conta1')");
            var own = await Eval(_panes[0], "localStorage.getItem('quem')");
            var other = await Eval(_panes[1], "localStorage.getItem('quem')");
            Check("cada conta com sessão própria", own == "\"conta1\"" && other == "null", $"conta1={own} conta2={other}");

            SetLayout(DeckLayout.Grid);
            await Task.Delay(500);
            Check("grade 2x2", Shape(2, 2), Detail());

            SetLayout(DeckLayout.Columns);
            await Task.Delay(500);
            Check("colunas", Shape(4, 1), Detail());
            SetLayout(DeckLayout.Grid);

            await Eval(_panes[0], "window.__t = 0; setInterval(() => window.__t++, 100); 1");
            double zoomBefore = _config.Zoom[0];
            bool AtZoom(double zoom) => _panes.All(p => Math.Abs(p.WebView.ZoomFactor - zoom) < 0.01);
            SetZoomAll(0.5);
            FocusPane(1);
            bool zoomFocused = AtZoom(1) && _zoomButton.Text == "Zoom 100%";
            await Task.Delay(3000);
            bool focused = _panes[1].Visible && _panes[1].Bounds == Cell(_grid.ClientRectangle, 0, 0, 1, 1)
                && _panes.Where(p => p.Index != 1).All(p => !p.Visible);
            Check("ampliar conta 2", focused, Detail());
            int.TryParse(await Eval(_panes[0], "window.__t"), out int ticks);
            Check("conta escondida continua no ritmo normal", ticks >= 20, $"{ticks} ticks em 3 s (normal ~30, desacelerada ~3)");
            ToggleFocus(1);
            await Task.Delay(500);
            Check("voltar para a grade", _config.Layout == DeckLayout.Grid && Shape(2, 2), Detail());
            // Ampliar põe o zoom em 100%; voltar pelo mesmo botão ou pela grade devolve o de antes (50%).
            bool zoomBack = AtZoom(0.5) && _zoomButton.Text == "Zoom 50%";
            FocusPane(2);
            bool zoomAgain = AtZoom(1);
            SetLayout(DeckLayout.Grid);
            bool zoomGrid = AtZoom(0.5) && _config.ZoomOutsideFocus is 0.5;
            Check("ampliar põe o zoom em 100%, e voltar (pelo botão ou pela grade) devolve o de antes",
                zoomFocused && zoomBack && zoomAgain && zoomGrid,
                $"ampliou={zoomFocused} voltou={zoomBack} ampliou de novo={zoomAgain} pela grade={zoomGrid} ({_zoomButton.Text})");
            SetZoomAll(zoomBefore);

            // Eco: um clique de verdade (pelo DevTools, como o mouse) na conta 1 se repete nas outras;
            // a conta 3 está "noutra tela" (sem o botão Mapa) e não clica nada.
            async Task RealClick(AccountPane pane, string selector)
            {
                var xy = JsonSerializer.Deserialize<double[]>(await Eval(pane,
                    $"(() => {{ const r = document.querySelector('{selector}').getBoundingClientRect(); return [r.left + r.width / 2, r.top + r.height / 2]; }})()"))!;
                foreach (var type in new[] { "mousePressed", "mouseReleased" })
                    await pane.Core!.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                        JsonSerializer.Serialize(new { type, x = xy[0], y = xy[1], button = "left", clickCount = 1 }));
            }
            async Task<string> Clicks() => string.Join(",", await Task.WhenAll(_panes.Select(p => Eval(p, "window.__mapa || 0"))));
            await Eval(_panes[2], "document.querySelector('nav button').remove(); 1");
            SetEcho(true);
            await RealClick(_panes[0], "nav button");
            await Task.Delay(1500);
            var echoed = await Clicks();
            Check("eco: clique na conta 1 se repete nas outras (e não volta)",
                echoed == "1,1,0,1" && _panes[2].NoteText?.Contains("Mapa") == true && _panes[1].NoteText is null,
                $"cliques por conta={echoed} aviso conta3='{_panes[2].NoteText}'");

            SetEcho(false);
            await RealClick(_panes[0], "nav button");
            await Task.Delay(1000);
            var alone = await Clicks();
            Check("eco desligado: o clique fica só na conta", alone == "2,1,0,1", $"cliques por conta={alone}");

            // O zoom não depende do eco (desligado aqui). Zoom posto pelo programa não gera o aviso do WebView2
            // (só o Ctrl + roda da pessoa): avisa à mão.
            _panes[1].WebView.ZoomFactor = 0.8;
            OnPaneSettingsChanged(_panes[1]);
            bool zoomSynced = await WaitFor(() => _panes.All(p => Math.Abs(p.WebView.ZoomFactor - 0.8) < 0.01), 5);
            Check("zoom de uma conta vale para todas, com o eco desligado", zoomSynced && !_echo && _config.Zoom.All(z => Math.Abs(z - 0.8) < 0.01),
                string.Join(", ", _panes.Select(p => $"{p.WebView.ZoomFactor:0.00}")));

            // Os botões de zoom da barra: + e − andam um nível em todas as contas, e o botão do meio mostra o zoom.
            bool AllAt(double zoom) => _panes.All(p => Math.Abs(p.WebView.ZoomFactor - zoom) < 0.01);
            SetZoomAll(1);
            StepZoom(1);
            bool zoomedIn = AllAt(1.1) && _zoomButton.Text == "Zoom 110%";
            StepZoom(-1);
            StepZoom(-1);
            bool zoomedOut = AllAt(0.9) && _zoomButton.Text == "Zoom 90%";
            SetZoomAll(1.5);
            bool top = !_zoomInButton.Enabled && _zoomOutButton.Enabled;
            Check("botões de zoom: + e − mudam todas as contas", zoomedIn && zoomedOut && top,
                $"+ {zoomedIn} · −− {zoomedOut} · no máximo o + apaga {top} · {_zoomButton.Text}");
            SetZoomAll(1);

            var folder = _store.CopyUnpacked(Path.Combine(_root, "ext-teste"));
            var info = await _store.InstallAsync(_panes[0].Profile!, folder);
            await RefreshExtensionButtonsAsync();
            _panes[0].Reload();
            _panes[1].Reload();
            await Task.Delay(500);
            await WaitFor(() => _panes[0].Status == PaneStatus.Ok && _panes[1].Status == PaneStatus.Ok, 15);
            await Task.Delay(500);
            var ext0 = await Eval(_panes[0], "document.documentElement.dataset.ext || null");
            var ext1 = await Eval(_panes[1], "document.documentElement.dataset.ext || null");
            Check("extensão roda na conta 1 e só nela", ext0 == "\"ok\"" && ext1 == "null", $"conta1={ext0} conta2={ext1} id={info.Id}");
            Check("menos animações continua depois de recarregar",
                await Eval(_panes[0], ReducedMotion) == "true" && await Eval(_panes[1], ReducedMotion) == "true");
            Check("botão da extensão no cabeçalho da conta 1", _panes[0].ExtensionButtonCount == 1 && _panes[1].ExtensionButtonCount == 0);

            // O liga/desliga do sniper, com a extensão de teste no lugar do Rotom: grava no storage dela            // de dentro da página, lê de volta, e a conta 2 (sem a extensão) responde "não carregado".
            bool? sniperBefore = await RotomSniper.GetEnabledAsync(_panes[0].Core!, info.Id);
            bool sniperOff = await RotomSniper.SetEnabledAsync(_panes[0].Core!, false, info.Id);
            bool? sniperAfterOff = await RotomSniper.GetEnabledAsync(_panes[0].Core!, info.Id);
            bool sniperOn = await RotomSniper.SetEnabledAsync(_panes[0].Core!, true, info.Id);
            bool? sniperAfterOn = await RotomSniper.GetEnabledAsync(_panes[0].Core!, info.Id);
            bool? sniperNone = await RotomSniper.GetEnabledAsync(_panes[1].Core!, info.Id);
            Check("liga/desliga do sniper pelo deck",
                sniperBefore == true && sniperOff && sniperAfterOff == false && sniperOn && sniperAfterOn == true && sniperNone is null,
                $"antes={sniperBefore} desligar={sniperOff}->{sniperAfterOff} ligar={sniperOn}->{sniperAfterOn} conta2={sniperNone?.ToString() ?? "sem extensão"}");

            var popup = await OpenExtensionPopupAsync(_panes[0], info, new Point(Right - 40, Top + 120));
            bool popupOk = popup is not null && await WaitFor(() => popup.WebView.CoreWebView2?.DocumentTitle == "Popup-teste", 10);
            Check("popup da extensão abre", popupOk, $"titulo '{popup?.WebView.CoreWebView2?.DocumentTitle}'");
            popup?.Close();

            // Trazer configurações: grava na storage da extensão na conta 1 e copia para a conta 4, como faria
            // ao abrir o deck (a conta 4 ainda não abriu o banco dessa extensão, então dá para gravar agora).
            var writer = await OpenExtensionPopupAsync(_panes[0], info, new Point(Right - 40, Top + 120));
            await WaitFor(() => writer?.WebView.CoreWebView2?.DocumentTitle == "Popup-teste", 10);
            await writer!.WebView.ExecuteScriptAsync("chrome.storage.local.set({ regra: 'da conta 1' }); 1");
            await Task.Delay(1000);
            writer.Close();
            var source = StorageImport.FindSources(_root, info.Id, new[] { (_panes[0].ProfileName, _panes[0].AccountName) })
                .FirstOrDefault(s => s.Label.StartsWith("Deck"));
            if (source is not null)
                StorageImport.Queue(_root, new PendingImport(info.Id, source.Folder, source.Label, _panes[3].ProfileName));
            var applied = StorageImport.ApplyPending(_root);
            await _store.InstallAsync(_panes[3].Profile!, folder);
            var reader = await OpenExtensionPopupAsync(_panes[3], info, new Point(Right - 40, Top + 120));
            await WaitFor(() => reader?.WebView.CoreWebView2?.DocumentTitle == "Popup-teste", 10);
            await reader!.WebView.ExecuteScriptAsync("window.__r = 'pendente'; chrome.storage.local.get('regra', v => window.__r = v.regra || 'vazio'); 1");
            string copied = "\"pendente\"";
            for (int i = 0; i < 20 && copied.Contains("pendente"); i++)
            {
                await Task.Delay(250);
                copied = await reader.WebView.ExecuteScriptAsync("window.__r");
            }
            reader.Close();
            Check("trazer configurações da extensão (conta 1 para a conta 4)", copied == "\"da conta 1\"",
                $"leu {copied}; origem {(source is null ? "não achada" : source.Label)}; {string.Join("; ", applied.Select(a => a.Result))}");

            // Dados trazidos de outro PC: o perfil ainda aponta para a pasta da extensão de lá.
            var fakeProfile = Path.Combine(_root, "perfil-de-outro-pc");
            Directory.CreateDirectory(fakeProfile);
            File.WriteAllText(Path.Combine(fakeProfile, "Secure Preferences"), JsonSerializer.Serialize(new
            {
                extensions = new
                {
                    settings = new Dictionary<string, object>
                    {
                        [info.Id] = new { location = 4, path = @"C:\Users\OutroUsuario\AppData\Local\PokeIdleMulti\extensions\" + Path.GetFileName(folder) },
                        ["daquimesmo"] = new { location = 4, path = folder },
                        ["donavegador"] = new { location = 5, path = @"C:\nao\existe" },
                    },
                },
            }));
            var moved = _store.MovedExtensions(fakeProfile);
            Check("dados de outro PC: acha a extensão que mudou de pasta",
                moved.Count == 1 && moved[0].Id == info.Id && moved[0].Folder == folder,
                string.Join(", ", moved.Select(m => $"{m.Id} -> {m.Folder}")));

            var page = ExtensionStore.Describe(Path.Combine(_root, "ext-pagina"));
            Check("extensão sem popup ganha botão que abre a página dela", page.OpensWindow && page.Popup == "cockpit.html",
                $"popup={page.Popup} janela={page.OpensWindow}");

            var before = Application.OpenForms.OfType<PopupWindow>().ToList();
            await Eval(_panes[2], "window.open('https://deck.test/kick.html', '_blank', 'width=500,height=400'); 1");
            bool opened = await WaitFor(() => Application.OpenForms.OfType<PopupWindow>().Except(before).Any(), 10);
            Check("janela aberta pelo site (login por popup) fica no deck", opened);
            foreach (var window in Application.OpenForms.OfType<PopupWindow>().Except(before).ToList())
                window.Close();

            // Twitch-teste + as 2 primeiras da Kick (o autoteste não pergunta à Kick quem está no ar).
            bool livesOk = await WaitFor(() => _lives[0] is LivesWindow w && w.Count == 3 && w.Loaded == w.Count, 20);
            var lives0 = _lives[0];
            bool parked = lives0 is not null && lives0.Visible && lives0.Parked
                && !Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(lives0.Bounds));
            Check("lives da conta 1 carregadas, rodando fora da tela",
                livesOk && parked && _lives.Skip(1).All(l => l is null),
                lives0 is null ? "sem janela" : $"{lives0.Loaded}/{lives0.Count} Kick={string.Join(",", lives0.KickChannels)} {lives0.Bounds}");
            var visibility = lives0 is null ? new List<string>() : await lives0.EvalAsync("document.visibilityState");
            Check("todas as páginas das lives visíveis para o navegador (vídeo de trás toca)",
                visibility.Count == 3 && visibility.All(v => v == "\"visible\""), string.Join(", ", visibility));

            // Fora da tela só tocam os players, sem página do chat. Nos pontos abre por cima de cada player a
            // página do canal, só com o rodapé do chat (sem as mensagens); fechar fecha essas páginas.
            const string ChatShown = "(() => { const c = document.querySelector('#channel-chatroom'); return c ? getComputedStyle(c).display + ' ' " +
                "+ getComputedStyle(document.querySelector('#chatroom-messages')).visibility : 'sem chat'; })()";
            int chatsParked = lives0?.ChatsOpen ?? -1;
            // 💰 Pontos da Kick: só as colunas de chat lado a lado (a página do canal fica na largura de
            // computador, encostada à direita), com a altura dos pontos (a tela baixa limita), no meio da tela.
            ShowPoints(0, LiveSite.Kick);
            bool chatsUp = await WaitFor(() => lives0 is not null && lives0.ChatsLoaded == 2, 15);
            await Task.Delay(300);
            string chatShown = lives0 is null ? "" : string.Join(",", await lives0.EvalChatsAsync(ChatShown));
            var screenArea = lives0 is null ? Rectangle.Empty : Screen.FromControl(lives0).WorkingArea;
            var columns = lives0?.KickLayout.ToList() ?? new();
            int chatWidth = lives0?.LogicalToDeviceUnits(340) ?? 0, pageWidth = lives0?.LogicalToDeviceUnits(1100) ?? 0;
            bool fits = lives0 is not null && lives0.Bounds.Height == Math.Min(screenArea.Height, lives0.LogicalToDeviceUnits(640))
                && screenArea.Contains(lives0.Bounds) && Math.Abs(lives0.Bounds.Top - screenArea.Top - (screenArea.Bottom - lives0.Bounds.Bottom)) <= 1;
            bool sideBySide = columns.Count == 2 && columns.All(c => c.Frame.Width == chatWidth && c.View.Width == pageWidth
                && c.View.Right == c.Frame.Width && c.View.Height == c.Frame.Height) && columns[0].Frame.Right == columns[1].Frame.Left;
            string layout = lives0 is null ? "" : $"janela={lives0.Bounds} área={screenArea} colunas={string.Join(" ", columns.Select(c => $"{c.Frame}/{c.View}"))}";
            lives0?.Close();
            await Task.Delay(300);
            int chatsAfter = lives0?.ChatsOpen ?? -1;
            Activate();
            Check("chat da Kick só abre com os pontos na tela (só o rodapé, sem as mensagens), e fecha ao sair",
                chatsParked == 0 && chatsUp && chatShown == "\"block hidden\",\"block hidden\"" && chatsAfter == 0
                && lives0 is { Parked: true, Visible: true },
                $"fora={chatsParked} páginas de chat; mostrando={chatShown}; depois de fechar={chatsAfter}");
            Check("pontos da Kick: os 2 chats lado a lado, com a altura dos pontos", fits && sideBySide, layout);

            // 💰 Pontos da Twitch: o chat em janela de cada live da Twitch da conta (com o botão dos pontos), só com o
            // 💰 aberto, numa coluna do tamanho dela; fechar fecha essa página.
            bool twitchPointsButton = _twitchButton.Available;
            ShowPoints(0, LiveSite.Twitch);
            bool twitchChatUp = await WaitFor(() => lives0 is not null && lives0.ChatsLoaded == 1, 15);
            await Task.Delay(300);
            var twitchChats = lives0 is null ? new List<string>() : await lives0.EvalChatsAsync("location.search");
            var twitchColumn = lives0?.ColumnsOf(LiveSite.Twitch).ToList() ?? new();
            lives0?.Close();
            await Task.Delay(300);
            Activate();
            Check("💰 Pontos da Twitch: abre o chat em janela de cada live da Twitch da conta, e fecha ao sair",
                twitchPointsButton && twitchChatUp && twitchChats.SequenceEqual(new[] { "\"?canal=tftoddy&chat=1\"" })
                && twitchColumn.Count == 1 && twitchColumn[0].View.Size == twitchColumn[0].Frame.Size && lives0?.ChatsOpen == 0,
                $"botão={twitchPointsButton} chats={string.Join(",", twitchChats)} coluna={string.Join(" ", twitchColumn)} depois={lives0?.ChatsOpen}");

            // Propaganda cortada nas lives: o pedido a um site de anúncio volta 403 do próprio deck.
            if (lives0 is not null)
                await lives0.EvalAsync("window.__ads = 'pendente'; fetch('https://pagead2.googlesyndication.com/deck-teste')" +
                                       ".then(r => window.__ads = r.status, () => window.__ads = 'erro'); 1");
            await Task.Delay(1500);
            var ads = lives0 is null ? new List<string>() : await lives0.EvalAsync("window.__ads");
            Check("lives sem propaganda (anúncio bloqueado; vídeo, Kick e Cloudflare passam)",
                ads.Count == 3 && ads.All(a => a == "403")
                && AdBlock.Blocked("https://pagead2.googlesyndication.com/x") && AdBlock.Blocked("https://js.stripe.com/v3")
                && !AdBlock.Blocked("https://kick.com/biscoitao") && !AdBlock.Blocked("https://abc.playback.live-video.net/api/video/v1/x.m3u8")
                && !AdBlock.Blocked("https://challenges.cloudflare.com/x") && !AdBlock.Blocked("https://notgooglesyndication.com/"),
                string.Join(",", ads));

            Check("Kick: abre só as primeiras da lista que estão no ar, até o limite (2 de fábrica, ou o escolhido)",
                KickLive.Pick(new[] { "biscoitao", "axt", "clovao", "outro" },
                    new Dictionary<string, bool?> { ["biscoitao"] = false, ["axt"] = true, ["clovao"] = true, ["outro"] = true }, 2)
                    .SequenceEqual(new[] { "axt", "clovao" })
                && KickLive.Pick(new[] { "biscoitao", "axt", "clovao" },
                    new Dictionary<string, bool?> { ["biscoitao"] = true, ["axt"] = false, ["clovao"] = true }, 2)
                    .SequenceEqual(new[] { "biscoitao", "clovao" })
                && KickLive.Pick(new[] { "biscoitao", "axt", "clovao" }, new Dictionary<string, bool?>(), 2)
                    .SequenceEqual(new[] { "biscoitao", "axt" })
                && KickLive.Pick(new[] { "biscoitao", "axt", "clovao", "outro" },
                    new Dictionary<string, bool?> { ["axt"] = false }, 3)
                    .SequenceEqual(new[] { "biscoitao", "clovao", "outro" })
                && new DeckConfig().KickLives == 2);
            Check("Kick: resposta da API (no ar / offline)",
                KickLive.ParseLive("{\"livestream\":null}") == false && KickLive.ParseLive("{\"livestream\":{\"is_live\":true}}") == true
                && KickLive.ParseLive("{}") is null && KickLive.ParseLive("<html>") is null);
            var twitchParsed = TwitchLive.ParseLive(new[] { "cogu", "rodil", "nao" },
                "{\"data\":{\"users\":[{\"login\":\"cogu\",\"stream\":{\"id\":\"1\"}},{\"login\":\"rodil\",\"stream\":null},null]}}");
            Check("Twitch: resposta da API (no ar / offline / não existe) e o endereço do player",
                twitchParsed["cogu"] == true && twitchParsed["rodil"] == false && twitchParsed["nao"] == false
                && TwitchLive.ParseLive(new[] { "x" }, "<html>")["x"] is null
                && TwitchLive.PlayerUrl("cogu") == "https://player.twitch.tv/?channel=cogu&parent=twitch.tv&player=popout"
                && new DeckConfig().TwitchLives == 2,
                string.Join(", ", twitchParsed.Select(t => $"{t.Key}={t.Value?.ToString() ?? "?"}")));

            // Twitch (1 vaga no autoteste): abre o player da 1ª da lista no ar, troca a que sai do ar pela próxima, e sem o
            // login da Twitch na conta não abre nenhuma (o player sem login não rende pontos).
            string TwitchNow() => string.Join(",", lives0?.TwitchChannels ?? Enumerable.Empty<string>());
            string twitchFirst = TwitchNow();
            _twitchLive["tftoddy"] = false;
            _twitchLive["clovaobengala"] = true;
            ApplyLives();
            bool twitchSwapped = await WaitFor(() => lives0 is not null && lives0.Loaded == lives0.Count && TwitchNow() == "clovaobengala", 10);
            _twitchLogin[0] = false;
            ApplyLives();
            bool twitchClosed = await WaitFor(() => TwitchNow() == "", 5);
            string noTwitchLogin = LivesSummary(0);
            _twitchLogin[0] = true;
            _twitchLive.Clear();
            ApplyLives();
            bool twitchBack = await WaitFor(() => lives0 is not null && lives0.Loaded == lives0.Count && TwitchNow() == "tftoddy", 10);
            if (lives0 is not null)
                await lives0.WatchAsync();
            string twitchSummary = LivesSummary(0);
            Check("Twitch: abre o player da 1ª no ar, troca a que sai do ar, e sem login na Twitch não abre",
                twitchFirst == "tftoddy" && twitchSwapped && twitchClosed && noTwitchLogin.Contains("Twitch: entre na Twitch")
                && twitchBack && twitchSummary.Contains("Twitch no ar: tftoddy ▶"),
                $"antes={twitchFirst} trocou={twitchSwapped} sem login={twitchClosed} ({noTwitchLogin}) voltou={twitchBack} ({twitchSummary})");

            // A 2ª da lista sai do ar: a vaga dela vai para a próxima no ar, e a 1ª fica sem recarregar.
            if (lives0 is not null)
                await lives0.EvalAsync("window.__marca = 'mesma'; 1");
            _kickLive["biscoitao"] = true;
            _kickLive["yetz"] = false;
            _kickLive["surskity"] = true;
            ApplyLives();
            bool swapped = await WaitFor(() => lives0 is not null && lives0.Loaded == lives0.Count
                && lives0.KickChannels.SequenceEqual(new[] { "biscoitao", "surskity" }), 15);
            var kept = lives0 is null ? new List<string>() : await lives0.EvalAsync("window.__marca || 'recarregou'");
            Check("Kick: live que sai do ar fecha e a próxima no ar entra",
                swapped && kept.Count == 3 && kept[1] == "\"mesma\"" && kept[2] == "\"recarregou\"",
                lives0 is null ? "sem janela" : $"Kick={string.Join(",", lives0.KickChannels)} marca={string.Join(",", kept)}");

            // Botão kick na conta 1: escolhe qual fechar, até a 1ª da lista (biscoitao); a outra fica sem
            // recarregar, e a conferência de 2 em 2 min não põe outra na vaga, nem com mais uma no ar.
            string KickNow() => string.Join(",", lives0?.KickChannels ?? Enumerable.Empty<string>());
            if (lives0 is not null)
                await lives0.EvalAsync("window.__marca = 'mesma'; 1");
            string kickBefore = string.Join(",", _panes.Select(p => p.KickState));
            var menuBefore = LiveMenuItems(0, LiveSite.Kick).Select(i => i.Text).ToList();
            CloseLive(0, LiveSite.Kick, "biscoitao");
            bool chosen = await WaitFor(() => lives0 is not null && lives0.Count == 2 && lives0.Loaded == lives0.Count
                && lives0.KickChannels.SequenceEqual(new[] { "surskity" }), 10);
            var stayed = lives0 is null ? new List<string>() : await lives0.EvalAsync("window.__marca || 'recarregou'");
            var menuAfter = LiveMenuItems(0, LiveSite.Kick).Select(i => i.Text).ToList();
            // Todas no ar: a vaga do biscoitao não abre outra (fica 1 live, a mais de cima da lista sem as fechadas).
            _kickLive["yetz"] = true;
            ApplyLives();
            bool noRefill = await WaitFor(() => lives0 is not null && lives0.Count == 2 && lives0.Loaded == lives0.Count
                && lives0.KickChannels.SequenceEqual(new[] { "yetz" }), 10);
            Check("kick: escolhe qual live fechar (até a 1ª da lista) e a vaga não abre outra",
                kickBefore == "ligado,apagado,apagado,apagado"
                && menuBefore.Take(3).SequenceEqual(new[] { "Fechar a live de biscoitao", "Fechar a live de surskity", "Fechar todas (nenhuma live da Kick nesta conta)" })
                && chosen && stayed.Count == 2 && stayed[1] == "\"mesma\"" && noRefill && _panes[0].KickState == "fechada"
                && menuAfter.Contains("Fechar a live de surskity") && menuAfter.Contains("Abrir de novo a live de biscoitao"),
                $"botões antes={kickBefore} menu antes={string.Join("|", menuBefore)} marca={string.Join(",", stayed)} sem repor={noRefill} " +
                $"Kick={KickNow()} botão={_panes[0].KickState} menu depois={string.Join("|", menuAfter)}");
            CloseLive(0, LiveSite.Kick, null);
            ApplyLives();
            bool none = await WaitFor(() => lives0 is not null && lives0.Count == 1 && !lives0.KickChannels.Any(), 10);
            Check("kick: fechar todas", none && _config.Kick[0].Slots == 0 && _panes[0].KickState == "fechada", $"Kick={KickNow()}");
            ReopenLive(0, LiveSite.Kick, null);
            bool reopened = await WaitFor(() => lives0 is not null && lives0.Count == 3 && lives0.Loaded == lives0.Count
                && lives0.KickChannels.Order().SequenceEqual(new[] { "biscoitao", "yetz" }), 15);
            Check("kick: abrir todas de novo volta às 2 primeiras da lista no ar",
                reopened && _panes[0].KickState == "ligado" && !_config.Kick[0].Limited,
                $"Kick={KickNow()} botão={_panes[0].KickState}");

            // Botão twitch na conta 1: o mesmo do kick, para a live da Twitch (1 vaga no autoteste). Fechada, a vaga não
            // abre outra da lista; abrir de novo volta.
            string twitchBefore = _panes[0].TwitchState;
            var twitchMenuBefore = LiveMenuItems(0, LiveSite.Twitch).Select(i => i.Text).ToList();
            CloseLive(0, LiveSite.Twitch, "tftoddy");
            bool twitchShut = await WaitFor(() => lives0 is not null && !lives0.TwitchChannels.Any(), 10);
            UpdateBar();
            string twitchShutState = _panes[0].TwitchState;
            var twitchMenuAfter = LiveMenuItems(0, LiveSite.Twitch).Select(i => i.Text).ToList();
            string twitchShutSummary = LivesSummary(0);
            ReopenLive(0, LiveSite.Twitch, null);
            bool twitchReopened = await WaitFor(() => lives0 is not null && lives0.Loaded == lives0.Count
                && lives0.TwitchChannels.SequenceEqual(new[] { "tftoddy" }), 15);
            UpdateBar();
            Check("twitch: o botão da conta fecha a live da Twitch (a vaga não abre outra) e abre de novo",
                twitchBefore == "ligado" && twitchMenuBefore.Take(2).SequenceEqual(new[] { "Fechar a live de tftoddy", "Fechar todas (nenhuma live da Twitch nesta conta)" })
                && twitchShut && twitchShutState == "fechada" && twitchMenuAfter.Contains("Abrir de novo a live de tftoddy")
                && twitchShutSummary.Contains("Twitch: todas fechadas no botão lives") && twitchReopened && _panes[0].TwitchState == "ligado"
                && !_config.Twitch[0].Limited,
                $"antes={twitchBefore} menu={string.Join("|", twitchMenuBefore)} fechou={twitchShut} botão={twitchShutState} " +
                $"menu depois={string.Join("|", twitchMenuAfter)} ({twitchShutSummary}) voltou={twitchReopened}");

            // As lives da Kick da conta dividem um processo (--process-per-site), e o jogo fica em outro.
            var liveProcesses = _env is null ? new List<CoreWebView2ProcessExtendedInfo>()
                : (await _env.GetProcessExtendedInfosAsync())
                    .Where(p => p.AssociatedFrameInfos.Any(f => f.Source.Contains("/kick.html?canal="))).ToList();
            var livePages = liveProcesses.SelectMany(p => p.AssociatedFrameInfos).Select(f => f.Source).ToList();
            Check("lives da Kick da conta num processo só, separado do jogo",
                liveProcesses.Count == 1 && livePages.Count(s => s.Contains("/kick.html?canal=") && !s.Contains("chat=1")) == 2
                && !livePages.Any(s => s.Contains("game.html")),
                $"processos com live={liveProcesses.Count} páginas={string.Join(", ", livePages)}");

            // Conferência das lives tocando: a do biscoitao anda (▶); a do yetz fica parada (a 1ª conferência
            // só anota onde o vídeo está) e, depois de duas paradas seguidas, é recarregada (⟳1).
            if (lives0 is not null)
                for (int i = 0; i < 3; i++)
                    await lives0.WatchAsync();
            var playback = lives0?.KickPlayback.ToDictionary(k => k.Channel) ?? new();
            string summary = LivesSummary(0);
            Check("lives da Kick: confere se o vídeo toca e recarrega a que travou",
                playback.TryGetValue("biscoitao", out var bisc) && bisc.Playing == true && bisc.Reloads == 0
                && playback.TryGetValue("yetz", out var yetz) && yetz.Reloads == 1
                && summary.Contains("biscoitao ▶") && summary.Contains("yetz ⟳1"),
                summary);

            // A página da live morre (como por falta de memória no notebook) e abre de novo na hora. Sem
            // internet ela nem carrega: a conferência tenta de novo depois de duas paradas, e o vídeo volta.
            var kickFile = Path.Combine(_root, "site", "kick.html");
            File.Move(kickFile, kickFile + ".fora");
            int killed = 0;
            if (_env is not null)
                foreach (var renderer in await _env.GetProcessExtendedInfosAsync())
                    if (renderer.AssociatedFrameInfos.Any(f => f.Source.Contains("kick.html?canal=biscoitao")))
                    {
                        using var process = Process.GetProcessById(renderer.ProcessInfo.ProcessId);
                        process.Kill();
                        killed++;
                    }
            (bool? Playing, int Reloads) Biscoitao() => lives0?.KickPlayback.Where(k => k.Channel == "biscoitao")
                .Select(k => (k.Playing, k.Reloads)).FirstOrDefault() ?? default;
            bool reopenedNow = await WaitFor(() => Biscoitao().Reloads == 1 && lives0!.Loaded < lives0.Count, 15);
            File.Move(kickFile + ".fora", kickFile);
            if (lives0 is not null)
                for (int i = 0; i < 2; i++)
                    await lives0.WatchAsync();
            bool loadedAgain = await WaitFor(() => lives0 is not null && lives0.Loaded == lives0.Count, 15);
            if (lives0 is not null)
                await lives0.WatchAsync();
            // As lives também ficam sem animações, e continuam assim na página que abriu de novo.
            var livesMotion = lives0 is null ? new List<string>() : await lives0.EvalAsync(ReducedMotion);
            Check("lives da Kick: página que morreu abre de novo, e a que não carregou é recarregada",
                killed > 0 && reopenedNow && loadedAgain && Biscoitao() is (true, 2) && livesMotion.Count == 3 && livesMotion.All(m => m == "true"),
                $"processos fechados={killed} reabriu na hora={reopenedNow} carregou depois={loadedAgain} sem animações={string.Join(",", livesMotion)} {LivesSummary(0)}");

            // Resgate dos pontos (Lives → Config…, cada conta com o seu): a conta 1 resgata Captura (500), com o login
            // dela: o cookie session_token vai como Bearer. O biscoitao tem 1210: 1 resgate por conferência, até não
            // dar mais; o yetz, com 120, não resgata.
            var kickCookies = _panes[0].Core!.CookieManager;
            kickCookies.AddOrUpdateCookie(kickCookies.CreateCookie("session_token", "123%7Cabc", "kick.com", "/"));
            using (var redeemDialog = new ChannelsDialog(_config.TwitchChannels, _config.KickChannels, _config.TwitchLives, 2,
                _config.AccountNames, new KickReward?[] { KickReward.Capture, null, KickReward.Lure, null },
                new KickReward?[] { KickReward.Capture, KickReward.Xp, null, null }, true))
            {
                _config.RedeemKick = redeemDialog.RedeemKick;
                _config.RedeemTwitch = redeemDialog.RedeemTwitch;
                _config.ClaimTwitchBonus = redeemDialog.ClaimTwitchBonus;
                // Como a janela fica, para conferir de olho (aparece um instante).
                redeemDialog.Show(this);
                await Task.Delay(500);
                using var dialogPicture = new Bitmap(redeemDialog.Width, redeemDialog.Height);
                redeemDialog.DrawToBitmap(dialogPicture, new Rectangle(Point.Empty, dialogPicture.Size));
                dialogPicture.Save(Path.Combine(Path.GetTempPath(), "PokeIdleDeck-canais.png"));
            }
            _fakePoints["biscoitao"] = 1210;
            _fakePoints["yetz"] = 120;
            await RedeemKickAsync();
            var firstRedeem = _fakeRedeems.ToList();
            await RedeemKickAsync();
            await RedeemKickAsync();
            string redeemSummary = LivesSummary(0);
            bool ulid = firstRedeem.Count == 1 && System.Text.RegularExpressions.Regex.IsMatch(firstRedeem[0],
                "^biscoitao cap Bearer 123\\|abc \\{\"transaction_id\":\"[0-9A-HJKMNP-TV-Z]{26}\",\"message\":\"\"\\}$");
            Check("pontos da Kick: resgata o bônus escolhido na conta, com o login dela, 1 por canal a cada conferência, só com saldo",
                _config.RedeemKick.SequenceEqual(new KickReward?[] { KickReward.Capture, null, KickReward.Lure, null })
                && ulid && _fakeRedeems.Count == 2 && _fakePoints["biscoitao"] == 210 && _fakePoints["yetz"] == 120
                && redeemSummary.Contains("(210/500 pontos, resgatou") && redeemSummary.Contains("(120/500 pontos)"),
                $"pedidos={string.Join(" | ", _fakeRedeems)} saldo={_fakePoints["biscoitao"]}/{_fakePoints["yetz"]} {redeemSummary}");

            // Sem a Kick vinculada no jogo o PokeIdle devolve os pontos: o deck não resgata de novo e para nesta conta.
            // Salvar Lives → Config… volta a resgatar; sem o login da Kick no perfil, avisa no menu.
            _fakePoints["biscoitao"] += 500;
            await RedeemKickAsync();
            await RedeemKickAsync();
            string stoppedSummary = LivesSummary(0);
            bool stopped = _redeemKick[0].Stopped is not null && _fakeRedeems.Count == 2;
            for (int i = 0; i < 20; i++)
            {
                var login = (await kickCookies.GetCookiesAsync("https://kick.com")).Where(c => c.Name == "session_token").ToList();
                if (login.Count == 0)
                    break;
                login.ForEach(kickCookies.DeleteCookie);
                await Task.Delay(250);
            }
            _redeemKick[0].Restart();
            await RedeemKickAsync();
            string noLoginSummary = LivesSummary(0);
            Check("pontos da Kick: os pontos voltaram (sem vínculo no jogo) para na conta; sem login avisa",
                stopped && stoppedSummary.Contains("resgate parado: os pontos voltaram") && _fakeRedeems.Count == 2
                && _redeemKick[0].NoLogin && noLoginSummary.Contains("resgate: entre na Kick"),
                $"parou={stopped} pedidos={_fakeRedeems.Count} | {stoppedSummary} | {noLoginSummary}");

            // Conta desmarcada não resgata nem confere o saldo, mesmo logada e com pontos.
            kickCookies.AddOrUpdateCookie(kickCookies.CreateCookie("session_token", "123%7Cabc", "kick.com", "/"));
            _config.RedeemKick[0] = null;
            _redeemKick[0].Restart();
            await RedeemKickAsync();
            string offSummary = LivesSummary(0);
            Check("pontos da Kick: conta desmarcada não resgata nem confere o saldo",
                _fakeRedeems.Count == 2 && _redeemKick[0].Text("biscoitao") == "" && !offSummary.Contains("pontos"), offSummary);
            _config.RedeemKick = new KickReward?[Accounts];

            // Pontos da Twitch: a conta 1 resgata Captura (500) com o login dela (o cookie auth-token vai como OAuth e o
            // unique_id como X-Device-Id). O tftoddy tem 460 e um baú esperando: o baú é desafiado pela checagem anti-robô,
            // o deck busca o token dela e manda de novo (510), e aí resgata a Captura (10). Na conferência seguinte, sem
            // baú e sem saldo, só lê o saldo.
            var twitchCookies = _panes[0].Core!.CookieManager;
            twitchCookies.AddOrUpdateCookie(twitchCookies.CreateCookie("auth-token", "tok123", ".twitch.tv", "/"));
            twitchCookies.AddOrUpdateCookie(twitchCookies.CreateCookie("unique_id", "dev123", ".twitch.tv", "/"));
            _fakeTwitchPoints["tftoddy"] = 460;
            _fakeTwitchClaims.Add("tftoddy");
            await TwitchPointsAsync();
            var twitchFirstRun = _fakeTwitch.ToList();
            await TwitchPointsAsync();
            string twitchPointsSummary = LivesSummary(0);
            bool twitchFlow = twitchFirstRun.Count == 5
                && twitchFirstRun[0] == "saldo tftoddy OAuth tok123 dev123" && twitchFirstRun[1] == "baú sem a checagem"
                && twitchFirstRun[2] == "checagem OAuth tok123 dev123" && twitchFirstRun[3] == "baú bau-tftoddy OAuth tok123"
                && System.Text.RegularExpressions.Regex.IsMatch(twitchFirstRun[4], "^resgate tftoddy cap 500 OAuth tok123 [0-9a-f]{32}$");
            Check("pontos da Twitch: pega o baú (passando pela checagem anti-robô) e resgata o bônus da conta, com o login dela",
                twitchFlow && _fakeTwitch.Count == 6 && _fakeTwitch[5].StartsWith("saldo tftoddy") && _fakeTwitchPoints["tftoddy"] == 10
                && twitchPointsSummary.Contains("tftoddy ▶ (10/500 pontos, resgatou") && twitchPointsSummary.Contains(", baú "),
                $"pedidos={string.Join(" | ", _fakeTwitch)} saldo={_fakeTwitchPoints["tftoddy"]} {twitchPointsSummary}");

            // Sem bônus marcado na conta, só o baú: confere o saldo e pega o baú, sem resgatar; sem o login, avisa.
            _config.RedeemTwitch[0] = null;
            _redeemTwitch[0].Restart();
            _fakeTwitchClaims.Add("tftoddy");
            _fakeTwitch.Clear();
            await TwitchPointsAsync();
            string claimOnly = LivesSummary(0);
            bool claimOnlyOk = _fakeTwitchPoints["tftoddy"] == 60 && !_fakeTwitch.Any(r => r.StartsWith("resgate")) && claimOnly.Contains("(60 pontos, baú ");
            for (int i = 0; i < 20; i++)
            {
                var login = (await twitchCookies.GetCookiesAsync("https://www.twitch.tv")).Where(c => c.Name == "auth-token").ToList();
                if (login.Count == 0)
                    break;
                login.ForEach(twitchCookies.DeleteCookie);
                await Task.Delay(250);
            }
            await TwitchPointsAsync();
            string twitchNoLogin = LivesSummary(0);
            Check("pontos da Twitch: só o baú, sem resgatar; sem o login da Twitch, avisa",
                claimOnlyOk && _redeemTwitch[0].NoLogin && twitchNoLogin.Contains("pontos da Twitch: entre na Twitch"),
                $"pedidos={string.Join(" | ", _fakeTwitch)} | {claimOnly} | {twitchNoLogin}");
            _config.RedeemTwitch = new KickReward?[Accounts];
            _redeemTwitch[0].Restart();

            // Registro dos resgates: cada resgate, baú e pontos que voltaram, com a conta, o site e o canal, no
            // resgates.log; a janela mostra o mais novo em cima.
            var logLines = _redeemLog.Lines.ToList();
            string account1 = _config.AccountNames[0];
            bool Logged(string text) => logLines.Any(l => l.Contains($"  {account1} · {text}"));
            ShowRedeemLog();
            await Task.Delay(300);
            string shownLog = _redeemLogWindow?.ShownText ?? "";
            _redeemLogWindow?.Close();
            Activate();
            Check("registro dos resgates: grava resgates, baús e pontos que voltaram no resgates.log, e mostra o mais novo em cima",
                Logged("Kick · biscoitao: resgatou Bônus de +15% Capture Boost (500 pontos; sobraram 710)")
                && Logged("Kick · biscoitao: resgatou Bônus de +15% Capture Boost (500 pontos; sobraram 210)")
                && Logged("Kick · biscoitao: os pontos do último resgate voltaram")
                && Logged("Twitch · tftoddy: pegou o baú (+50, saldo 510)")
                && Logged("Twitch · tftoddy: resgatou Bônus de +15% Capture Boost (500 pontos; sobraram 10)")
                && Logged("Twitch · tftoddy: pegou o baú (+50, saldo 60)")
                && File.ReadAllLines(_redeemLog.FilePath).SequenceEqual(logLines)
                && logLines.Count > 0 && shownLog.StartsWith(logLines[^1]),
                string.Join(" | ", logLines));

            // Resgate em lote: o item só aparece no menu Lives com o Shift. Com a conta 1 (o login dela de novo) no
            // biscoitao, 2 XP + 1 Captura com 1500 pontos são 3 resgates (XP, Captura, XP), cada um com a descrição do
            // bônus (senão a Twitch de mentira recusa) e um transactionID novo; saem 1100 e o saldo do fim é lido de novo.
            twitchCookies.AddOrUpdateCookie(twitchCookies.CreateCookie("auth-token", "tok123", ".twitch.tv", "/"));
            twitchCookies.AddOrUpdateCookie(twitchCookies.CreateCookie("unique_id", "dev123", ".twitch.tv", "/"));
            _fakeTwitchPoints["biscoitao"] = 1500;
            bool HasBulk() => _livesButton.DropDownItems.Cast<ToolStripItem>().Any(x => x.Text?.StartsWith("Resgate em lote") == true);
            FillLivesMenu(false);
            bool bulkHidden = !HasBulk();
            FillLivesMenu(true);
            bool bulkShown = HasBulk();
            _livesButton.DropDownItems.Cast<ToolStripItem>().FirstOrDefault(x => x.Text?.StartsWith("Resgate em lote") == true)?.PerformClick();
            FillLivesMenu(false);
            var bulk = _bulkWindow;
            string bulkBefore = "", bulkEnd = "";
            bool bulkRead = false, bulkReady = false;
            var bulkRedeems = new List<string>();
            if (bulk is not null)
            {
                bulk.PauseMs = (100, 200);
                bulk.Account = 0;
                bulk.Channel = "biscoitao";
                bulk.SetCounts(2, 1, 0);
                bulkRead = await bulk.CheckAsync();
                bulkBefore = bulk.StatusText;
                bulkReady = bulk.CanRedeem;
                _fakeTwitch.Clear();
                await bulk.RunAsync();
                bulkRedeems = _fakeTwitch.Where(r => r.StartsWith("resgate")).ToList();
                bulkEnd = string.Join(" / ", bulk.ProgressLines);            }
            bool bulkOrder = bulkRedeems.Count == 3
                && System.Text.RegularExpressions.Regex.IsMatch(bulkRedeems[0], "^resgate biscoitao xp 300 OAuth tok123 [0-9a-f]{32}$")
                && System.Text.RegularExpressions.Regex.IsMatch(bulkRedeems[1], "^resgate biscoitao cap 500 OAuth tok123 [0-9a-f]{32}$")
                && System.Text.RegularExpressions.Regex.IsMatch(bulkRedeems[2], "^resgate biscoitao xp 300 OAuth tok123 [0-9a-f]{32}$")
                && bulkRedeems.Select(r => r.Split(' ')[^1]).Distinct().Count() == 3;
            Check("resgate em lote: só aparece no Lives com o Shift; 2 XP + 1 Captura são 3 resgates com a descrição do bônus, e lê o saldo no fim",
                bulkHidden && bulkShown && bulk is not null && bulkRead && bulkReady && bulkOrder
                && bulkBefore.Contains("fulano_twitch tem 1.500 pontos em biscoitao") && bulkBefore.Contains("Total: 1.100 pontos (sobram 400)")
                && bulk.Done == 3 && bulk.Stop is null && _fakeTwitchPoints["biscoitao"] == 400 && _fakeTwitch[^1].StartsWith("saldo biscoitao")
                && bulkEnd.EndsWith("Fim: 3 de 3. Saldo agora: 400."),
                $"menu={bulkHidden}/{bulkShown} leu={bulkRead} pronto={bulkReady} | {bulkBefore.Replace(Environment.NewLine, " ")} | " +
                $"pedidos={string.Join(" | ", _fakeTwitch)} | {bulkEnd} saldo={_fakeTwitchPoints["biscoitao"]}");

            // Sem saldo para o lote, não começa (nenhum resgate). Com saldo, mas os pontos gastos por fora depois do 1º
            // (outro resgate no meio), para no NOT_ENOUGH_POINTS da Twitch e mostra o saldo do fim; o Cancelar para depois
            // do resgate da vez. Tudo no registro dos resgates.
            string bulkShort = "", bulkStopped = "", bulkCancelled = "";
            bool bulkRefused = false, bulkNotEnough = false, bulkCancel = false;
            if (bulk is not null)
            {
                bulk.SetCounts(2, 0, 0);
                await bulk.CheckAsync();
                bulkShort = bulk.StatusText;
                _fakeTwitch.Clear();
                await bulk.RunAsync();
                bulkRefused = !bulk.CanRedeem && bulkShort.Contains("Saldo insuficiente: precisa de 600 pontos e a conta tem 400.")
                              && bulk.Done == 0 && bulk.Stop?.StartsWith("Saldo insuficiente") == true && !_fakeTwitch.Any(r => r.StartsWith("resgate"));

                _fakeTwitchPoints["biscoitao"] = 1000;
                bulk.SetCounts(3, 0, 0);
                _fakeTwitchAfterRedeem = channel => _fakeTwitchPoints[channel] = 100;
                _fakeTwitch.Clear();
                await bulk.RunAsync();
                bulkStopped = string.Join(" / ", bulk.ProgressLines);
                bulkNotEnough = bulk.Done == 1 && bulk.Stop == "erro NOT_ENOUGH_POINTS" && _fakeTwitch.Count(r => r.StartsWith("resgate")) == 2
                                && bulkStopped.Contains("2/3 +15% XP: recusado, erro NOT_ENOUGH_POINTS. Parei aqui.")
                                && bulkStopped.EndsWith("Fim: 1 de 3 (erro NOT_ENOUGH_POINTS). Saldo agora: 100.");

                _fakeTwitchPoints["biscoitao"] = 1000;
                _fakeTwitchAfterRedeem = _ => bulk.Cancel();
                _fakeTwitch.Clear();
                await bulk.RunAsync();
                _fakeTwitchAfterRedeem = null;
                bulkCancelled = string.Join(" / ", bulk.ProgressLines);
                bulkCancel = bulk.Done == 1 && bulk.Stop == "cancelado" && _fakeTwitch.Count(r => r.StartsWith("resgate")) == 1
                             && _fakeTwitchPoints["biscoitao"] == 700 && bulkCancelled.EndsWith("Fim: 1 de 3 (cancelado). Saldo agora: 700.");
            }
            var bulkLog = _redeemLog.Lines.Where(l => l.Contains($"  {account1} · Twitch · biscoitao: ")).ToList();
            bool BulkLogged(string text) => bulkLog.Any(l => l.EndsWith($"  {account1} · Twitch · biscoitao: {text}"));
            Check("resgate em lote: sem saldo não começa; para no 1º erro da Twitch (NOT_ENOUGH_POINTS) e no Cancelar; tudo no registro",
                bulkRefused && bulkNotEnough && bulkCancel
                && BulkLogged("resgate em lote de 2 XP + 1 Captura (1100 pontos; saldo 1500)")
                && BulkLogged("resgatou Bônus +15% XP PokeIdle (em lote, 1 de 3; 300 pontos; sobraram 1200)")
                && BulkLogged("resgatou Bônus de +15% Capture Boost (em lote, 2 de 3; 500 pontos; sobraram 700)")
                && BulkLogged("resgate em lote terminou: 3 de 3; saldo 400")
                && BulkLogged("o resgate em lote de Bônus +15% XP PokeIdle foi recusado (erro NOT_ENOUGH_POINTS; saldo 700); parei no 2 de 3")
                && BulkLogged("resgate em lote terminou: 1 de 3 (erro NOT_ENOUGH_POINTS); saldo 100")
                && BulkLogged("resgate em lote terminou: 1 de 3 (cancelado); saldo 700"),
                $"{bulkShort.Replace(Environment.NewLine, " ")} | {bulkStopped} | {bulkCancelled} | {string.Join(" | ", bulkLog)}");

            // Resgate em lote na Kick, com o login da conta 1 (o session_token vai como Bearer): 1 XP + 1 Captura com 1000
            // pontos são 2 resgates (XP, Captura), cada um com um transaction_id novo; sobram 200, lidos de novo no fim.
            string kickBulkBefore = "", kickEnd = "";
            bool kickRead = false, kickReady = false;
            int kickFrom = _fakeRedeems.Count;
            if (bulk is not null)
            {
                _fakePoints["biscoitao"] = 1000;
                bulk.SetCounts(1, 1, 0);
                bulk.Platform = LiveSite.Kick;
                kickRead = await bulk.CheckAsync();
                kickBulkBefore = bulk.StatusText;
                kickReady = bulk.CanRedeem;
                await bulk.RunAsync();
                kickEnd = string.Join(" / ", bulk.ProgressLines);
            }
            var kickBulk = _fakeRedeems.Skip(kickFrom).ToList();
            const string kickBody = " \\{\"transaction_id\":\"[0-9A-HJKMNP-TV-Z]{26}\",\"message\":\"\"\\}$";
            bool kickOrder = kickBulk.Count == 2
                && System.Text.RegularExpressions.Regex.IsMatch(kickBulk[0], "^biscoitao xp Bearer 123\\|abc" + kickBody)
                && System.Text.RegularExpressions.Regex.IsMatch(kickBulk[1], "^biscoitao cap Bearer 123\\|abc" + kickBody);
            Check("resgate em lote na Kick: com o login da conta, 1 XP + 1 Captura são 2 resgates, e lê o saldo no fim",
                kickRead && kickReady && kickOrder && kickBulkBefore.Contains("fulano_kick tem 1.000 pontos em biscoitao na Kick")
                && kickBulkBefore.Contains("Total: 800 pontos (sobram 200)") && bulk?.Done == 2 && bulk.Stop is null
                && _fakePoints["biscoitao"] == 200 && kickEnd.EndsWith("Fim: 2 de 2. Saldo agora: 200."),
                $"leu={kickRead} pronto={kickReady} | {kickBulkBefore.Replace(Environment.NewLine, " ")} | pedidos={string.Join(" | ", kickBulk)} | " +
                $"{kickEnd} saldo={_fakePoints["biscoitao"]}");

            // O dono do canal tem pontos infinitos: na Twitch o saldo nem vem, na Kick vem 0. O lote reconhece o dono (a
            // conta logada é o usuário do canal; a Kick de mentira nem tem o /me do canal), mostra ∞, não exige saldo e
            // resgata 2 Shiny (2000 pontos) nos dois sites, sem gastar nada. Tudo no registro.
            string ownerTwitch = "", ownerKick = "", ownerTwitchEnd = "", ownerKickEnd = "";
            bool ownerTwitchReady = false, ownerKickReady = false;
            int ownerTwitchDone = 0, ownerKickDone = 0, ownerKickFrom = _fakeRedeems.Count;
            if (bulk is not null)
            {
                _fakeTwitchMe = _fakeKickMe = "biscoitao";
                _fakeTwitchPoints["biscoitao"] = 0;
                _fakePoints["biscoitao"] = 0;
                bulk.SetCounts(0, 0, 2);
                bulk.Platform = LiveSite.Twitch;
                await bulk.CheckAsync();
                ownerTwitch = bulk.StatusText;
                ownerTwitchReady = bulk.CanRedeem;
                _fakeTwitch.Clear();
                await bulk.RunAsync();
                ownerTwitchDone = bulk.Done;
                ownerTwitchEnd = string.Join(" / ", bulk.ProgressLines);
                bulk.Platform = LiveSite.Kick;
                await bulk.CheckAsync();
                ownerKick = bulk.StatusText;
                ownerKickReady = bulk.CanRedeem;
                await bulk.RunAsync();
                ownerKickDone = bulk.Done;
                ownerKickEnd = string.Join(" / ", bulk.ProgressLines);
                _fakeTwitchMe = "fulano_twitch";
                _fakeKickMe = "fulano_kick";
                bulk.Close();
                Activate();
            }
            int ownerTwitchRedeems = _fakeTwitch.Count(r => r.StartsWith("resgate biscoitao lure 1000 OAuth tok123 "));
            int ownerKickRedeems = _fakeRedeems.Skip(ownerKickFrom).Count(r => r.StartsWith("biscoitao lure Bearer 123|abc "));
            var ownerLog = _redeemLog.Lines.Where(l => l.Contains($"  {account1} · ") && l.Contains(" · biscoitao: ")).ToList();
            bool OwnerLogged(LiveSite site, string text) => ownerLog.Any(l => l.EndsWith($"  {account1} · {site} · biscoitao: {text}"));
            bool ownerLogs = new[] { LiveSite.Twitch, LiveSite.Kick }.All(site =>
                OwnerLogged(site, "resgate em lote de 2 Shiny (2000 pontos; dono do canal, pontos infinitos)")
                && OwnerLogged(site, "resgatou Bônus de +15% Secret Lure (em lote, 2 de 2; 1000 pontos; dono do canal)")
                && OwnerLogged(site, "resgate em lote terminou: 2 de 2; saldo ∞ (dono do canal)"))
                && OwnerLogged(LiveSite.Kick, "resgatou Bônus de +15% Capture Boost (em lote, 2 de 2; 500 pontos; sobraram 200)");
            Check("resgate em lote: o dono do canal (pontos infinitos) resgata sem saldo, na Twitch e na Kick; tudo no registro",
                ownerTwitchReady && ownerKickReady
                && ownerTwitch.Contains("biscoitao é o dono do canal biscoitao na Twitch: pontos infinitos (∞).")
                && ownerTwitch.Contains("Total: 2.000 pontos (dono do canal: não precisa de saldo)")
                && ownerKick.Contains("biscoitao é o dono do canal biscoitao na Kick: pontos infinitos (∞).")
                && ownerTwitchDone == 2 && ownerKickDone == 2 && ownerTwitchRedeems == 2 && ownerKickRedeems == 2
                && ownerTwitchEnd.EndsWith("Fim: 2 de 2. Saldo agora: ∞ (dono do canal).")
                && ownerKickEnd.EndsWith("Fim: 2 de 2. Saldo agora: ∞ (dono do canal).")
                && _fakeTwitchPoints["biscoitao"] == 0 && _fakePoints["biscoitao"] == 0 && ownerLogs && bulk?.IsDisposed == true,
                $"{ownerTwitch.Replace(Environment.NewLine, " ")} | {ownerTwitchEnd} | {ownerKick.Replace(Environment.NewLine, " ")} | {ownerKickEnd} | " +
                $"twitch={ownerTwitchRedeems} kick={ownerKickRedeems} | {string.Join(" | ", ownerLog.TakeLast(8))}");

            // deck.json: o de antes (um resgate para todas as contas) vira o mesmo bônus em cada conta; um número fora
            // da lista vira o XP; de fábrica nenhuma conta resgata.
            var redeemPath = Path.Combine(_root, "deck-resgate.json");
            File.WriteAllText(redeemPath, "{ \"KickRedeem\": true, \"KickReward\": \"Lure\" }");
            var redeemConfig = DeckConfig.Load(redeemPath);
            redeemConfig.Save(redeemPath);
            string redeemSaved = File.ReadAllText(redeemPath);
            File.WriteAllText(redeemPath, "{ \"RedeemKick\": [ \"Capture\", null, 7 ] }");
            var perAccount = DeckConfig.Load(redeemPath);
            Check("pontos da Kick: o resgate de cada conta fica no deck.json (nenhuma de fábrica; o de antes vale para todas)",
                redeemConfig.RedeemKick.All(r => r == KickReward.Lure) && !redeemSaved.Contains("\"KickRedeem\"") && !redeemSaved.Contains("\"KickReward\"")
                && perAccount.RedeemKick.SequenceEqual(new KickReward?[] { KickReward.Capture, null, KickReward.Xp, null })
                && new DeckConfig().RedeemKick.All(r => r is null),
                $"antes→{string.Join(",", redeemConfig.RedeemKick)} por conta→{string.Join(",", perAccount.RedeemKick.Select(r => r?.ToString() ?? "não"))}");
            await WaitFor(() => _panes.All(p => p.Status == PaneStatus.Ok), 20);
            _kickLive.Clear();

            // deck.json da versão em que o kick só fechava a 2ª: vira 1 vaga, e o campo velho some ao salvar.
            var oldConfig = Path.Combine(_root, "deck-antigo.json");
            File.WriteAllText(oldConfig, "{ \"KickSingle\": [false, false, false, true] }");
            var migrated = DeckConfig.Load(oldConfig);
            migrated.Save(oldConfig);
            var saved = File.ReadAllText(oldConfig);
            Check("kick: deck.json antigo (só a 2ª fechada) continua com a 2ª fechada",
                migrated.Kick[3].Slots == 1 && migrated.Kick.Take(3).All(k => !k.Limited)
                && !saved.Contains("KickSingle") && !saved.Contains("Limited") && saved.Contains("\"Slots\": 1"),
                string.Join(",", migrated.Kick.Select(k => k.Slots)));

            // deck.json de antes do limite: o 2 fixo escrito em cada conta vira "todas"; com o limite em 3, uma conta
            // que fechou uma live fica com 2, e um limite fora da faixa é corrigido.
            File.WriteAllText(oldConfig, "{ \"Kick\": [ { \"Slots\": 2, \"Closed\": [] }, { \"Slots\": 1, \"Closed\": [\"yetz\"] } ] }");
            var legacy = DeckConfig.Load(oldConfig);
            File.WriteAllText(oldConfig, "{ \"KickLives\": 3, \"Kick\": [ { \"Slots\": 2, \"Closed\": [\"yetz\"] }, { \"Slots\": 3, \"Closed\": [] } ] }");
            var three = DeckConfig.Load(oldConfig);
            File.WriteAllText(oldConfig, "{ \"KickLives\": 99 }");
            var tooMany = DeckConfig.Load(oldConfig);
            Check("kick: limite de lives no deck.json (o de antes vira todas; fora da faixa é corrigido)",
                legacy.KickLives == 2 && legacy.Kick[0].Slots is null && !legacy.Kick[0].Limited && legacy.Kick[1].Slots == 1 && legacy.Kick[1].Limited
                && three.KickLives == 3 && three.Kick[0].SlotsOf(3) == 2 && three.Kick[0].Limited && three.Kick[1].Slots is null && three.Kick[2].SlotsOf(3) == 3
                && tooMany.KickLives == KickLive.MaxSlots,
                $"antes={legacy.KickLives}:{string.Join(",", legacy.Kick.Select(k => k.Slots?.ToString() ?? "todas"))} " +
                $"com 3={string.Join(",", three.Kick.Select(k => k.SlotsOf(3)))} 99→{tooMany.KickLives}");

            // deck.json do tempo da presença no chat: o modo da Twitch some ao salvar, e o número de lives fica na faixa.
            File.WriteAllText(oldConfig, "{ \"TwitchMode\": \"Presence\", \"TwitchLives\": 99 }");
            var presenceConfig = DeckConfig.Load(oldConfig);
            presenceConfig.Save(oldConfig);
            var presenceSaved = File.ReadAllText(oldConfig);
            File.WriteAllText(oldConfig, "{ \"TwitchMode\": \"Live\" }");
            Check("Twitch: deck.json antigo (presença no chat) vira 2 lives, e o número fica na faixa",
                presenceConfig.TwitchLives == TwitchLive.MaxSlots && !presenceSaved.Contains("TwitchMode")
                && DeckConfig.Load(oldConfig).TwitchLives == TwitchLive.DefaultSlots,
                $"99→{presenceConfig.TwitchLives}");

            // O campo em Lives → Config…: com 3, a conta abre as 3 primeiras da lista (o autoteste não pergunta à Kick
            // quem está no ar, então conta todas como no ar), e a janela dos pontos põe as 3 colunas lado a lado.
            // O da Twitch, em 2: abre as 2 primeiras da lista dela.
            using (var channelsDialog = new ChannelsDialog(_config.TwitchChannels, _config.KickChannels, 2, 3,
                _config.AccountNames, _config.RedeemKick, _config.RedeemTwitch, _config.ClaimTwitchBonus))
            {
                SetKickLives(channelsDialog.KickLives);
                _config.TwitchLives = channelsDialog.TwitchLives;
            }
            CloseLives(0);
            await OpenLivesAsync(0);
            var lives3 = _lives[0];
            bool opened3 = await WaitFor(() => lives3 is not null && lives3.Loaded == lives3.Count
                && lives3.KickChannels.SequenceEqual(new[] { "biscoitao", "yetz", "surskity" })
                && lives3.TwitchChannels.SequenceEqual(new[] { "tftoddy", "clovaobengala" }), 20);
            _config.TwitchLives = 1;
            ShowPoints(0, LiveSite.Kick);
            await Task.Delay(300);
            // Lado a lado: cada coluna começa num lugar diferente da janela.
            int columns3 = lives3?.KickLayout.Select(c => c.Frame.Left).Distinct().Count() ?? 0;
            lives3?.Close();
            Activate();
            SetKickLives(2);
            CloseLives(0);
            await OpenLivesAsync(0);
            bool back2 = await WaitFor(() => _lives[0] is LivesWindow w && w.Loaded == w.Count && w.KickChannels.Count() == 2, 20);
            Check("kick: com o limite em 3 a conta abre 3 lives (3 colunas nos pontos), e volta a 2; Twitch em 2 abre 2",
                opened3 && columns3 == 3 && back2,
                $"Kick={string.Join(",", lives3?.KickChannels ?? Enumerable.Empty<string>())} " +
                $"Twitch={string.Join(",", lives3?.TwitchChannels ?? Enumerable.Empty<string>())} colunas={columns3} " +
                $"de volta a 2={back2} ({string.Join(",", _lives[0]?.KickChannels ?? Enumerable.Empty<string>())})");

            // ⏻ na conta 1 (que tem lives): o jogo vira página em branco e as lives fecham; ligar traz tudo de volta.
            await SetPaneOffAsync(0, true);
            bool closed = await WaitFor(() => _panes[0].Core?.Source == "about:blank", 10);
            Check("desligar a conta 1: o jogo fecha, as lives param e o cabeçalho mostra 'desligada'",
                closed && _panes[0].Off && _lives[0] is null && _panes[0].StatusText == "desligada"
                && _panes[0].VoteText is null && _config.LivesAccounts.Contains(1),
                $"página={_panes[0].Core?.Source} lives={(_lives[0] is null ? "fechadas" : "abertas")} status='{_panes[0].StatusText}'");
            await SetPaneOffAsync(0, false);
            bool back = await WaitFor(() => _panes[0].Status == PaneStatus.Ok && _panes[0].Core?.DocumentTitle == "Jogo-teste"
                && _lives[0] is LivesWindow w && w.Count > 0 && w.Loaded == w.Count, 20);
            Check("ligar a conta 1 de novo: o jogo e as lives voltam", back,
                $"status={_panes[0].Status} título='{_panes[0].Core?.DocumentTitle}' lives={(_lives[0] is LivesWindow lw ? $"{lw.Loaded}/{lw.Count}" : "fechadas")}");
            Check("menos animações continua depois de desligar e ligar a conta", await Eval(_panes[0], ReducedMotion) == "true");

            WindowState = FormWindowState.Minimized;
            await Task.Delay(1500);
            var alive = await Eval(_panes[2], "1 + 1");
            WindowState = FormWindowState.Normal;
            await Task.Delay(1000);
            Check("minimizar e restaurar com as contas vivas", alive == "2" && Shape(2, 2), Detail());

            // Voto no TopIdle, por partes (o autoteste não sai para a internet).
            Check("link de voto com o nick do personagem",
                TopIdle.VoteUrl("pokeidle-io-5f9969", "fulano") == "https://topidle.com/jogo/pokeidle-io-5f9969?votar=1&playerIdentifier=fulano"
                && TopIdle.VoteUrl("pokeidle-io-5f9969", "nick com espaço") == "https://topidle.com/jogo/pokeidle-io-5f9969?votar=1");
            Check("nick lido do jogo", TopIdle.ParseNick("\"fulano\"") == "fulano"
                && TopIdle.ParseNick("\"\\\"ciclano\\\"\"") == "ciclano" && TopIdle.ParseNick("null") is null);
            Check("voto registrado é percebido",
                TopIdle.IsVoteRegistered("POST", "https://topidle.com/api/games/pokeidle-io-5f9969/vote", 200, "pokeidle-io-5f9969")
                && !TopIdle.IsVoteRegistered("POST", "https://topidle.com/api/games/pokeidle-io-5f9969/vote", 429, "pokeidle-io-5f9969")
                && !TopIdle.IsVoteRegistered("GET", "https://topidle.com/api/games/pokeidle-io-5f9969/vote", 200, "pokeidle-io-5f9969")
                && !TopIdle.IsVoteRegistered("POST", "https://outro.com/api/games/pokeidle-io-5f9969/vote", 200, "pokeidle-io-5f9969"));
            _topIdleGameId = "pokeidle-io-5f9969";
            _votes[0] = new VoteState(true, false, DateTime.Now.AddHours(2).AddMinutes(5).AddSeconds(30), "teste");
            _votes[1] = new VoteState(true, true, null, "teste");
            _votes[2] = new VoteState(false, true, null, null);
            for (int i = 0; i < Accounts; i++)
                RenderVote(i);
            RenderVoteStatus();
            Check("botões de voto nos cabeçalhos",
                _panes[0].VoteText == "🗳 2 h 05 min" && _panes[1].VoteText == "🗳 Votar" && _panes[2].VoteText == "🗳 Votar"
                && _panes[3].VoteText == "🗳 TopIdle" && _voteStatus.Text == "🗳 Voto liberado: Conta 2",
                string.Join(" | ", _panes.Select(p => p.VoteText)) + " | " + _voteStatus.Text);
            MarkVoted(1);
            Check("depois de votar o botão vira 'votou' (sem erro)", _panes[1].VoteText == "🗳 ✓ Votou" && _voteStatus.Text?.StartsWith("🗳 Próximo voto em") == true,
                _panes[1].VoteText + " | " + _voteStatus.Text);

            // O cabeçalho da conta 1 (com extensão, kick, carrinho e voto) na grade e em colunas, a mais apertada:
            // a imagem para conferir o tamanho dos botões, e o que não coube.
            var headerPicture = Path.Combine(Path.GetTempPath(), "PokeIdleDeck-cabecalho.png");
            var shots = new List<Bitmap>();
            var cut = new Dictionary<DeckLayout, string>();
            foreach (var shape in new[] { DeckLayout.Grid, DeckLayout.Columns })
            {
                SetLayout(shape);
                await Task.Delay(500);
                var header = _panes[0].Controls.OfType<ToolStrip>().First();
                var shot = new Bitmap(header.Width, header.Height);
                header.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size));
                shots.Add(shot);
                cut[shape] = string.Join(" ", header.Items.Cast<ToolStripItem>()
                    .Where(i => i.Available && i.Placement == ToolStripItemPlacement.None).Select(i => i.Text ?? i.ToolTipText));
            }
            SetLayout(DeckLayout.Grid);
            using (var both = new Bitmap(shots.Max(b => b.Width), shots.Sum(b => b.Height) + 8))
            {
                using (var g = Graphics.FromImage(both))
                {
                    g.Clear(Color.Black);
                    g.DrawImage(shots[0], 0, 0);
                    g.DrawImage(shots[1], 0, shots[0].Height + 8);
                }
                both.Save(headerPicture);
            }
            shots.ForEach(b => b.Dispose());
            Check("cabeçalho: botões maiores, e nenhum some (em colunas sai o \"rodando há\" e o voto fica só 🗳)",
                cut[DeckLayout.Grid].Length == 0 && cut[DeckLayout.Columns].Length == 0 && _panes[0].VoteText == "🗳 2 h 05 min",
                $"cortados na grade='{cut[DeckLayout.Grid]}' em colunas='{cut[DeckLayout.Columns]}' (imagem em {headerPicture})");
            try
            {
                Check("TopIdle acha o pokeidle (precisa de internet)",
                    await TopIdle.FindGameIdAsync("pokeidle.io") == "pokeidle-io-5f9969");
                var anonymous = await TopIdle.GetStateAsync(_panes[3].Core!.CookieManager, "pokeidle-io-5f9969");
                Check("conta sem login no TopIdle aparece como 'Votar' (precisa de internet)", !anonymous.LoggedIn);
                Check("Kick responde se a live está no ar (precisa de internet)", await KickLive.IsLiveAsync("biscoitao") is not null);
            }
            catch (Exception ex)
            {
                Check("TopIdle respondeu (precisa de internet)", false, ex.Message);
            }

            const string master =
                "#EXTM3U\n#EXT-X-TWITCH-INFO:NODE=\"x\"\n#EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID=\"720p60\",NAME=\"720p60\"\n" +
                "#EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=100,BANDWIDTH=3000000,RESOLUTION=1280x720,VIDEO=\"720p60\"\nhttps://x/720.m3u8\n" +                "#EXT-X-STREAM-INF:BANDWIDTH=230000,RESOLUTION=284x160,VIDEO=\"160p30\"\nhttps://x/160.m3u8\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=160000,CODECS=\"mp4a.40.2\",VIDEO=\"audio_only\"\nhttps://x/audio.m3u8\n";
            var lowestOnly = VideoQuality.LowestOnly(master) ?? "";
            Check("lives na menor qualidade de vídeo (lista com só a 160p)",
                lowestOnly.Contains("160.m3u8") && !lowestOnly.Contains("720.m3u8") && !lowestOnly.Contains("audio.m3u8")
                && lowestOnly.Contains("#EXT-X-MEDIA") && VideoQuality.LowestOnly("#EXTM3U\n#EXTINF:2,\nseg.ts\n") is null);

            // A Twitch de verdade responde quem está no ar (canal que não existe vem como offline).
            var twitchNow = await TwitchLive.LiveAsync(new[] { "rodil", "tftoddy", "naoexiste_deck_teste_0" });
            Check("Twitch responde se a live está no ar (precisa de internet)",
                twitchNow["rodil"] is not null && twitchNow["tftoddy"] is not null && twitchNow["naoexiste_deck_teste_0"] == false,
                string.Join(", ", twitchNow.Select(t => $"{t.Key}={t.Value?.ToString() ?? "?"}")));

            var windowed = Bounds;
            ToggleFullscreen();
            await Task.Delay(800);
            var screen = Screen.FromControl(this).Bounds;
            Check("tela cheia", Bounds == screen && FormBorderStyle == FormBorderStyle.None && Shape(2, 2), $"{Bounds} tela {screen}");
            ToggleFullscreen();
            await Task.Delay(800);
            Check("sair da tela cheia volta como estava", Bounds == windowed && FormBorderStyle == FormBorderStyle.Sizable, $"{Bounds} antes {windowed}");
            Check("RAM na barra separada em jogo, lives e navegador",
                _ram.Text?.StartsWith("RAM ~") == true && _ram.Text.Contains(" jogo ") && _ram.Text.Contains(" lives "), _ram.Text ?? "");
        }
        catch (Exception ex)
        {
            log.Add("ERRO " + ex);
        }
        finally
        {
            File.WriteAllText(SelfTestReport, string.Join(Environment.NewLine, log));
            Close();
        }
    }
}