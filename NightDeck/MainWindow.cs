using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace NightDeck;

public sealed class MainWindow : Window
{
    private const string GameUrl = "https://pokeidle.io/";
    private const string HuntUrl = "https://pokeidle-gg.pages.dev/#hunt";
    private static readonly Brush BackgroundBrush = Brush("#080C14");
    private static readonly Brush BarBrush = Brush("#111827");
    private static readonly Brush ButtonBrush = Brush("#1E293B");
    private static Brush AccentBrush = Brush("#991B1B");
    private string accentColor = "#991B1B";
    private string twitchChannels = "";
    private string kickChannels = "";
    private readonly DockPanel root = new();
    private readonly DockPanel toolbar = new() { LastChildFill = true };
    private readonly StackPanel toolbarLeft = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel toolbarRight = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid board = new() { Background = BackgroundBrush };
    private readonly Border[] panes = new Border[4];
    private readonly WebView2[] browsers = new WebView2[4];
    private readonly TextBlock[] zoomLabels = new TextBlock[4];
    private Button? globalZoomButton;
    private readonly Button[] accountButtons = new Button[4];
    private readonly bool[] muted = new bool[4];
    private readonly bool[] hidden = new bool[4];
    private readonly double[] zooms = { .6, .6, .6, .6 };
    private readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Night Deck", "settings.json");
    private readonly UpdateService updater = new(UpdateRepository());
    private readonly MenuItem updateCheckItem = new() { Header = "Verificar agora" };
    private bool checkingUpdates;
    private bool echoEnabled;
    private bool reduceMotion = true;
    private readonly string[] accountNames = { "Conta 1", "Conta 2", "Conta 3", "Conta 4" };
    private Button? echoButton;
    private readonly DateTime[] lastEcho = new DateTime[4];
    private string layout = "grid";
    private int? focused;
    private bool fullScreen;
    private WindowStyle previousStyle;
    private WindowState previousState;
    private ResizeMode previousResize;

    public MainWindow()
    {
        Title = "Night Deck";
        Width = 1400; Height = 850;
        MinWidth = 900; MinHeight = 520;
        WindowState = WindowState.Maximized;
        Background = BackgroundBrush;
        Foreground = Brushes.White;
        LoadSettings();
        AccentBrush = Brush(accentColor);
        Content = root;
        var menu = new Menu { Background = BarBrush, Foreground = Brushes.White, Height = 26 };
        var updates = new MenuItem { Header = "Atualizações" };
        updateCheckItem.Click += async (_, _) => await CheckUpdatesAsync(true);
        updates.Items.Add(updateCheckItem);
        menu.Items.Add(updates);
        var preferences = new MenuItem { Header = "Preferências" };
        var rename = new MenuItem { Header = "Renomear contas" };
        rename.Click += (_, _) => ShowAccountNames();
        preferences.Items.Add(rename);
        var motion = new MenuItem { Header = "Reduzir animações do jogo", IsCheckable = true, IsChecked = reduceMotion };
        motion.Click += async (_, _) => { reduceMotion = motion.IsChecked; SaveSettings(); await ApplyMotionToAllAsync(); };
        preferences.Items.Add(motion);
        menu.Items.Add(preferences);
        var help = new MenuItem { Header = "Ajuda" };
        help.Click += (_, _) => MessageBox.Show(this, "Night Deck — quatro contas PokeIdle", "Ajuda");
        menu.Items.Add(help);
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        var top = new Border { Background = BarBrush, Padding = new Thickness(5, 3, 5, 3), Child = toolbar };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(board);
        BuildToolbar();
        for (int i = 0; i < 4; i++) BuildPane(i);
        Rebuild();
        Loaded += async (_, _) =>
        {
            // WPF WebView2 must be attached to a loaded visual tree before initialization.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            for (int i = 0; i < browsers.Length; i++) _ = InitBrowserAsync(i);
            await Task.Delay(4000);
            if (IsLoaded) await CheckUpdatesAsync(false);
        };
        Closing += (_, _) => SaveSettings();
        KeyDown += (_, e) => { if (e.Key == Key.F11) ToggleFullscreen(); };
    }

    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;

    private static Button Btn(string label, string tooltip, Action click, bool selected = false, double width = double.NaN)
    {
        var button = new Button
        {
            Content = label, ToolTip = tooltip, MinHeight = 28,
            Width = width, Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(2), Background = selected ? AccentBrush : ButtonBrush,
            Foreground = Brushes.White, BorderThickness = new Thickness(1),
            BorderBrush = Brush("#3C4B5F"), Cursor = Cursors.Hand,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12
        };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Button.PaddingProperty));
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Button.BackgroundProperty, Brush("#374151")));
        template.Triggers.Add(hover);
        button.Template = template;
        button.Click += (_, _) => click();
        return button;
    }

    private void BuildToolbar()
    {
        toolbarLeft.Children.Add(Btn("Grade 2×2", "Quatro contas em grade", () => ChangeLayout("grid"), layout == "grid"));
        toolbarLeft.Children.Add(Btn("Vertical 1×4", "Quatro contas lado a lado", () => ChangeLayout("row"), layout == "row"));
        toolbarLeft.Children.Add(Btn("Cores", "Alterar cor de destaque", ShowAccentColors));
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            var button = Btn(accountNames[i], "Focar esta conta", () => { hidden[index] = false; focused = focused == index ? null : index; Rebuild(); });
            accountButtons[i] = button;
            toolbarLeft.Children.Add(button);
        }
        toolbarLeft.Children.Add(Btn("Todas", "Mostrar todas", () => { focused = null; Array.Fill(hidden, false); Rebuild(); }));
        toolbarRight.Children.Add(Btn("−", "Diminuir zoom de todas", () => SetGlobalZoom(-.1), width: 34));
        globalZoomButton = Btn("60%", "Restaurar zoom de todas para 60%", () => { for (int i = 0; i < 4; i++) SetZoom(i, .6); });
        toolbarRight.Children.Add(globalZoomButton);
        RefreshGlobalZoomLabel();
        toolbarRight.Children.Add(Btn("+", "Aumentar zoom de todas", () => SetGlobalZoom(.1), width: 34));
        toolbarRight.Children.Add(Btn("⛶", "Tela cheia (F11)", ToggleFullscreen, width: 36));
        toolbarRight.Children.Add(Btn("⟳ Todas", "Recarregar as contas", () => { foreach (var b in browsers) b?.CoreWebView2?.Reload(); }));
        toolbarRight.Children.Add(Btn("Navegador", "Abrir PokeIdle no navegador", () => OpenUrl(GameUrl)));
        toolbarRight.Children.Add(Btn("Onde caçar", "Ferramenta de caça", () => OpenUrl(HuntUrl)));
        echoButton = Btn("Echo OFF", "Sincronizar cliques em botões entre as contas visíveis", ToggleEcho);
        toolbarRight.Children.Add(echoButton);
        toolbarRight.Children.Add(Btn("Lives", "Abrir somente lives online da Twitch ou Kick", ShowLives));
        DockPanel.SetDock(toolbarLeft, Dock.Left);
        toolbar.Children.Add(toolbarLeft);
        toolbar.Children.Add(toolbarRight);
    }

    private void ShowLives()
    {
        var dialog = new Window
        {
            Title = "Lives Twitch / Kick", Owner = this, Width = 470, Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = BarBrush, Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Canais Twitch (separados por vírgula)" });
        var twitch = new TextBox { Text = twitchChannels, MinHeight = 28, Margin = new Thickness(0, 5, 0, 12) };
        panel.Children.Add(twitch);
        panel.Children.Add(new TextBlock { Text = "Canais Kick (separados por vírgula)" });
        var kick = new TextBox { Text = kickChannels, MinHeight = 28, Margin = new Thickness(0, 5, 0, 12) };
        panel.Children.Add(kick);
        var status = new TextBlock { Text = "Somente canais confirmados ao vivo serão abertos.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 10) };
        panel.Children.Add(status);
        panel.Children.Add(Btn("Verificar e abrir lives", "Consultar status atual", () => _ = LaunchLivesAsync()));
        dialog.Content = panel;
        dialog.ShowDialog();

        async Task LaunchLivesAsync()
        {
            string[] Parse(string value) => value.Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.TrimStart('@'))
                .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"^[A-Za-z0-9_]{3,25}$"))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
            twitchChannels = twitch.Text;
            kickChannels = kick.Text;
            SaveSettings();
            status.Text = "Verificando transmissões...";
            try
            {
                var tw = await LiveStatus.TwitchOnlineAsync(Parse(twitchChannels));
                var ki = await LiveStatus.KickOnlineAsync(Parse(kickChannels));
                foreach (var name in tw) OpenUrl("https://www.twitch.tv/" + Uri.EscapeDataString(name));
                foreach (var name in ki) OpenUrl("https://kick.com/" + Uri.EscapeDataString(name));
                status.Text = $"Abertas: {tw.Count} Twitch, {ki.Count} Kick. Canais offline não foram abertos.";
            }
            catch (Exception ex) { status.Text = "Não foi possível verificar o status: " + ex.Message; }
        }
    }

    private void ShowAccentColors()
    {
        var dialog = new Window
        {
            Title = "Cores — Night Deck", Owner = this, Width = 370, Height = 190,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = BarBrush, Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Cor de destaque (hexadecimal)", Margin = new Thickness(0, 0, 0, 8) });
        var field = new TextBox { Text = accentColor, MinHeight = 28 };
        panel.Children.Add(field);
        panel.Children.Add(Btn("Aplicar", "Atualizar destaque dos botões", () =>
        {
            var color = field.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$"))
            {
                MessageBox.Show(dialog, "Use o formato #RRGGBB.", "Cores"); return;
            }
            accentColor = color;
            AccentBrush = Brush(color);
            if (echoButton != null && echoEnabled) echoButton.Background = AccentBrush;
            if (toolbarLeft.Children.Count > 1)
            {
                if (toolbarLeft.Children[0] is Button grid) grid.Background = layout == "grid" ? AccentBrush : ButtonBrush;
                if (toolbarLeft.Children[1] is Button row) row.Background = layout == "row" ? AccentBrush : ButtonBrush;
            }
            for (int i = 0; i < 4; i++) accountButtons[i].Background = focused == i ? AccentBrush : ButtonBrush;
            SaveSettings(); dialog.Close();
        }));
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private void ShowAccountNames()
    {
        var dialog = new Window
        {
            Title = "Nomes das contas", Owner = this, Width = 380, Height = 320,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = BarBrush, Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        var inputs = new TextBox[4];
        for (int i = 0; i < 4; i++)
        {
            panel.Children.Add(new TextBlock { Text = $"Conta {i + 1}" });
            inputs[i] = new TextBox { Text = accountNames[i], MaxLength = 28, Margin = new Thickness(0, 3, 0, 9) };
            panel.Children.Add(inputs[i]);
        }
        panel.Children.Add(Btn("Salvar nomes", "Aplicar nomes personalizados", () =>
        {
            for (int i = 0; i < 4; i++)
            {
                accountNames[i] = string.IsNullOrWhiteSpace(inputs[i].Text) ? $"Conta {i + 1}" : inputs[i].Text.Trim();
                accountButtons[i].Content = accountNames[i];
                if (panes[i].Child is DockPanel dock && dock.Children.OfType<DockPanel>().FirstOrDefault() is DockPanel header
                    && header.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock title)
                    title.Text = "● " + accountNames[i];
            }
            SaveSettings(); dialog.Close();
        }));
        dialog.Content = new ScrollViewer { Content = panel };
        dialog.ShowDialog();
    }

    private void ToggleEcho()
    {
        echoEnabled = !echoEnabled;
        if (echoButton != null)
        {
            echoButton.Content = echoEnabled ? "Echo ON" : "Echo OFF";
            echoButton.Background = echoEnabled ? AccentBrush : ButtonBrush;
        }
    }

    // Only mirror explicit button-like clicks, not game movement or typing.
    private const string EchoCaptureScript = """
        (() => {
          if (window.__nightDeckEcho) return;
          window.__nightDeckEcho = true;
          document.addEventListener('click', event => {
            const el = event.target?.closest?.('button,a,summary,[role="button"],[role="tab"]');
            if (!el || el.closest('input,textarea,[contenteditable="true"]')) return;
            const tag = el.tagName.toLowerCase();
            const text = (el.getAttribute('aria-label') || el.title || el.innerText || '').trim().replace(/\\s+/g,' ').slice(0,100);
            const path = (() => {
              const parts = [];
              for (let n = el; n && n !== document.body && parts.length < 10; n = n.parentElement) {
                const siblings = [...(n.parentElement?.children || [])].filter(x => x.tagName === n.tagName);
                parts.unshift(n.tagName.toLowerCase() + ':nth-of-type(' + (siblings.indexOf(n) + 1) + ')');
              }
              return parts.join(' > ');
            })();
            if (path) window.chrome?.webview?.postMessage({ kind:'nightdeck-echo', path, tag, text });
          }, true);
        })();
        """;

    private async void RelayEcho(int source, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!echoEnabled || hidden[source] || focused.HasValue) return;
        if ((DateTime.UtcNow - lastEcho[source]).TotalMilliseconds < 180) return;
        try
        {
            using var data = JsonDocument.Parse(args.WebMessageAsJson);
            var root = data.RootElement;
            if (root.GetProperty("kind").GetString() != "nightdeck-echo") return;
            var path = root.GetProperty("path").GetString() ?? "";
            var tag = root.GetProperty("tag").GetString() ?? "";
            var label = root.GetProperty("text").GetString() ?? "";
            if (path.Length is < 1 or > 1200 || label.Length > 100 ||
                !new[] { "button", "a", "summary" }.Contains(tag) && tag != "div" && tag != "span") return;
            lastEcho[source] = DateTime.UtcNow;
            var payload = JsonSerializer.Serialize(new { path, tag, label });
            var script = """
                (() => {
                  const d = PAYLOAD;
                  const norm = x => String(x || '').trim().replace(/\\s+/g,' ').slice(0,100);
                  let el;
                  try { el = document.querySelector(d.path); } catch {}
                  if (!el || (d.label && norm(el.getAttribute('aria-label') || el.title || el.innerText) !== d.label)) return;
                  if (!el.matches('button,a,summary,[role="button"],[role="tab"]')) return;
                  el.click();
                })()
                """.Replace("PAYLOAD", payload);
            for (int i = 0; i < browsers.Length; i++)
            {
                if (i == source || hidden[i] || browsers[i].CoreWebView2 == null) continue;
                try { await browsers[i].CoreWebView2.ExecuteScriptAsync(script); }
                catch { }
            }
        }
        catch { }
    }

    private void ChangeLayout(string next)
    {
        layout = next; focused = null; Rebuild(); SaveSettings();
        // Update selection without rebuilding the toolbar or its controls.
        if (toolbarLeft.Children[0] is Button grid) grid.Background = layout == "grid" ? AccentBrush : ButtonBrush;
        if (toolbarLeft.Children[1] is Button row) row.Background = layout == "row" ? AccentBrush : ButtonBrush;
    }

    private void BuildPane(int i)
    {
        var dock = new DockPanel { Background = Brush("#0C131F") };
        var header = new DockPanel { Background = BarBrush, Height = 31, LastChildFill = false };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        var name = new TextBlock { Text = $"● {accountNames[i]}", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        actions.Children.Add(Btn("−", "Diminuir zoom", () => SetZoom(i, zooms[i] - .1), width: 30));
        var zoom = new TextBlock { Text = $"{zooms[i] * 100:0}%", Foreground = Brushes.White, Width = 40, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        zoomLabels[i] = zoom;
        actions.Children.Add(zoom);
        actions.Children.Add(Btn("+", "Aumentar zoom", () => SetZoom(i, zooms[i] + .1), width: 30));
        actions.Children.Add(Btn("♪", "Ativar/desativar som", () => { muted[i] = !muted[i]; if (browsers[i].CoreWebView2 != null) browsers[i].CoreWebView2.IsMuted = muted[i]; }, width: 30));
        actions.Children.Add(Btn("⟳", "Recarregar", () => browsers[i].CoreWebView2?.Reload(), width: 30));
        actions.Children.Add(Btn("⛶", "Expandir conta", () => { focused = focused == i ? null : i; Rebuild(); }, width: 30));
        actions.Children.Add(Btn("◉", "Ocultar conta", () => { hidden[i] = true; focused = null; Rebuild(); }, width: 30));
        actions.Children.Add(Btn("▣", "Restock: configurar compras", () => ShowRestock(i), width: 30));
        var browser = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(12, 19, 31) };
        browsers[i] = browser;
        dock.Children.Add(browser);
        panes[i] = new Border { Child = dock, Margin = new Thickness(2), Background = Brush("#0C131F"), CornerRadius = new CornerRadius(5), ClipToBounds = true };
    }

    private async Task InitBrowserAsync(int i)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Night Deck", "Accounts", $"Account{i + 1}");
            Directory.CreateDirectory(folder);
            var env = await CoreWebView2Environment.CreateAsync(null, folder);
            await browsers[i].EnsureCoreWebView2Async(env);
            int source = i;
            browsers[i].CoreWebView2.WebMessageReceived += (_, args) => RelayEcho(source, args);
            await browsers[i].CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(EchoCaptureScript);
            await browsers[i].CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(MotionScript);
            browsers[i].NavigationCompleted += async (_, _) => await ApplyMotionAsync(i);
            browsers[i].CoreWebView2.Settings.AreDevToolsEnabled = false;
            browsers[i].CoreWebView2.IsMuted = muted[i];
            browsers[i].ZoomFactor = zooms[i];
            browsers[i].CoreWebView2.Navigate(GameUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível iniciar a Conta {i + 1}.\n\n{ex.Message}", "Erro WebView2", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowRestock(int i)
    {
        // Settings modeled on restock-ui.js v1.45.2. Actual market execution is separate.
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Night Deck", $"restock-account-{i + 1}.json");
        RestockSettings cfg;
        try { cfg = JsonSerializer.Deserialize<RestockSettings>(File.ReadAllText(file)) ?? new(); }
        catch { cfg = new(); }
        var window = new Window
        {
            Title = $"Restock — Conta {i + 1}", Owner = this, Width = 520, Height = 680,
            MinWidth = 440, MinHeight = 540, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = BarBrush, Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = $"Recompra — Conta {i + 1}", FontSize = 18, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Configurações adaptadas da versão 1.45.2. O motor de compra ainda não está conectado.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 12) });
        CheckBox Check(string label, bool value)
        {
            var c = new CheckBox { Content = label, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 4) };
            panel.Children.Add(c); return c;
        }
        TextBox Field(string label, long value)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 2) });
            var t = new TextBox { Text = value.ToString(), Margin = new Thickness(0, 0, 0, 5), MinHeight = 24 };
            panel.Children.Add(t); return t;
        }
        var ballsOn = Check("Repor Pokéballs", cfg.BallsOn);
        var ballId = Field("ID da Pokéball", cfg.BallId);
        var ballKeep = Field("Estoque alvo de Pokéballs", cfg.BallKeep);
        var ballBelow = Field("Repor quando abaixo de", cfg.BallBelow);
        var revivesOn = Check("Repor Revives", cfg.RevivesOn);
        var reviveKeep = Field("Estoque alvo de Revives", cfg.ReviveKeep);
        var reviveBelow = Field("Repor Revives quando abaixo de", cfg.ReviveBelow);
        var potionsOn = Check("Repor poções", cfg.PotionsOn);
        var potionId = Field("ID da poção", cfg.PotionId);
        var potionKeep = Field("Estoque alvo de poções", cfg.PotionKeep);
        var potionBelow = Field("Repor poções quando abaixo de", cfg.PotionBelow);
        var potionFirst = Check("Priorizar poções", cfg.PotionFirst);
        var goldFloor = Field("Reserva mínima de ouro", cfg.GoldFloor);
        panel.Children.Add(Btn("Salvar configuração", "Salva apenas os valores, sem realizar compras", () =>
        {
            bool Parse(TextBox field, long max, out long value) =>
                long.TryParse(field.Text, out value) && value >= 0 && value <= max;
            if (!Parse(ballId, 10000000, out var bi) || !Parse(ballKeep, 10000000, out var bk) ||
                !Parse(ballBelow, 10000000, out var bb) || !Parse(reviveKeep, 10000000, out var rk) ||
                !Parse(reviveBelow, 10000000, out var rb) || !Parse(potionId, 10000000, out var pi) ||
                !Parse(potionKeep, 10000000, out var pk) || !Parse(potionBelow, 10000000, out var pb) ||
                !Parse(goldFloor, 1000000000000, out var gf))
            {
                MessageBox.Show(window, "Preencha os campos com números válidos e não negativos.", "Restock");
                return;
            }
            var settings = new RestockSettings
            {
                BallsOn = ballsOn.IsChecked == true, BallId = bi, BallKeep = bk, BallBelow = bb,
                RevivesOn = revivesOn.IsChecked == true, ReviveKeep = rk, ReviveBelow = rb,
                PotionsOn = potionsOn.IsChecked == true, PotionId = pi, PotionKeep = pk, PotionBelow = pb,
                PotionFirst = potionFirst.IsChecked == true, GoldFloor = gf
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, JsonSerializer.Serialize(settings));
                MessageBox.Show(window, "Configuração salva. A recompra automática ainda não está ativa.", "Restock");
                window.Close();
            }
            catch (Exception ex) { MessageBox.Show(window, ex.Message, "Restock"); }
        }));
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.ShowDialog();
    }

    private sealed class RestockSettings
    {
        public bool BallsOn { get; set; } = true;
        public long BallId { get; set; } = 4;
        public long BallKeep { get; set; } = 30000;
        public long BallBelow { get; set; } = 5000;
        public bool RevivesOn { get; set; } = true;
        public long ReviveKeep { get; set; } = 500;
        public long ReviveBelow { get; set; } = 100;
        public bool PotionsOn { get; set; } = true;
        public long PotionId { get; set; } = 204;
        public long PotionKeep { get; set; } = 5000;
        public long PotionBelow { get; set; } = 2000;
        public bool PotionFirst { get; set; } = true;
        public long GoldFloor { get; set; } = 1000000;
    }

    private async Task OpenMarketAsync(int i)
    {
        var core = browsers[i].CoreWebView2;
        if (core == null) return;
        try
        {
            const string script = "(() => { const els = [...document.querySelectorAll('a,button,[role=button]')]; const el = els.find(e => /^(market|mercado)$/i.test((e.textContent || '').trim())); if (el) { el.click(); return true; } return false; })()";
            if (await core.ExecuteScriptAsync(script) != "true")
                MessageBox.Show(this, "Não encontrei Mercado. A recompra automática ainda não foi implementada.", "Restock");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Restock"); }
    }

    // Adapted from the ReduceMotion preference in PokeIdleMulti 2026.11.05.2.
    private const string MotionScript = """
        (() => {
          const style = document.createElement('style');
          style.id = 'nightdeck-motion';
          style.textContent = '@media (prefers-reduced-motion: reduce) { *, *::before, *::after { scroll-behavior: auto !important; } }';
          (document.head || document.documentElement).appendChild(style);
        })();
        """;

    private async Task ApplyMotionAsync(int i)
    {
        if (browsers[i].CoreWebView2 == null) return;
        var js = reduceMotion
            ? "document.documentElement.style.setProperty('scroll-behavior','auto'); document.documentElement.dataset.nightDeckReduceMotion='1';"
            : "document.documentElement.style.removeProperty('scroll-behavior'); delete document.documentElement.dataset.nightDeckReduceMotion;";
        try { await browsers[i].CoreWebView2.ExecuteScriptAsync(js); }
        catch { }
    }

    private async Task ApplyMotionToAllAsync()
    {
        for (int i = 0; i < 4; i++) await ApplyMotionAsync(i);
    }

    private void SetZoom(int i, double value)
    {
        zooms[i] = Math.Round(Math.Clamp(value, .3, 1.5), 2);
        zoomLabels[i].Text = $"{zooms[i] * 100:0}%";
        RefreshGlobalZoomLabel();
        if (browsers[i].CoreWebView2 != null) browsers[i].ZoomFactor = zooms[i];
        SaveSettings();
    }
    private void RefreshGlobalZoomLabel()
    {
        if (globalZoomButton == null) return;
        var first = zooms[0];
        globalZoomButton.Content = zooms.All(z => Math.Abs(z - first) < 0.001)
            ? $"{first * 100:0}%"
            : "Misto";
    }

    private void SetGlobalZoom(double delta) { for (int i = 0; i < 4; i++) SetZoom(i, zooms[i] + delta); }

    private void Rebuild()
    {
        var indices = focused is int f ? new[] { f } : Enumerable.Range(0, 4).Where(i => !hidden[i]).ToArray();
        if (indices.Length == 0) { Array.Fill(hidden, false); indices = Enumerable.Range(0, 4).ToArray(); }
        var cols = focused.HasValue ? 1 : layout == "row" ? indices.Length : Math.Min(2, indices.Length);
        var rows = (int)Math.Ceiling(indices.Length / (double)cols);
        // Reparent existing WebView2 controls. Do not recreate browser sessions.
        foreach (var pane in panes) if (pane != null && pane.Parent is Panel parent) parent.Children.Remove(pane);
        board.Children.Clear();
        board.RowDefinitions.Clear(); board.ColumnDefinitions.Clear();
        for (int c = 0; c < cols; c++) board.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++) board.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int k = 0; k < indices.Length; k++)
        {
            var pane = panes[indices[k]];
            Grid.SetColumn(pane, k % cols);
            Grid.SetRow(pane, k / cols);
            board.Children.Add(pane);
        }
        for (int i = 0; i < 4; i++) accountButtons[i].Background = focused == i ? AccentBrush : ButtonBrush;
    }

    private void ToggleFullscreen()
    {
        if (!fullScreen)
        {
            previousStyle = WindowStyle; previousState = WindowState; previousResize = ResizeMode;
            WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
        }
        else { WindowState = WindowState.Normal; WindowStyle = previousStyle; ResizeMode = previousResize; WindowState = previousState; }
        fullScreen = !fullScreen;
    }

    private static string UpdateRepository()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "update-source.json")));
            return json.RootElement.GetProperty("repository").GetString() ?? "";
        }
        catch { return ""; }
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (checkingUpdates) return;
        checkingUpdates = true; updateCheckItem.IsEnabled = false;
        try
        {
            var result = await updater.CheckAsync();
            if (!result.Available)
            {
                if (manual) MessageBox.Show(this, result.Message, "Atualizações");
                return;
            }
            if (MessageBox.Show(this, result.Message + "\n\nDeseja baixar agora?", "Atualização", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            if (result.InstallerUrl is null) { if (result.ReleasePage != null) OpenUrl(result.ReleasePage); return; }
            updateCheckItem.Header = "Baixando...";
            var installer = await updater.DownloadInstallerAsync(result.InstallerUrl);
            if (MessageBox.Show(this, "Download concluído. Executar o instalador e fechar as contas?", "Instalar", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                Close();
            }
        }
        catch (Exception ex) { if (manual) MessageBox.Show(this, ex.Message, "Atualizações"); }
        finally { checkingUpdates = false; updateCheckItem.IsEnabled = true; updateCheckItem.Header = "Verificar agora"; }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Night Deck"); }
    }
    private void SaveSettings()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!); File.WriteAllText(settingsFile, JsonSerializer.Serialize(new { layout, zooms, reduceMotion, accountNames, accentColor, twitchChannels, kickChannels })); }
        catch { }
    }
    private void LoadSettings()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(settingsFile));
            if (json.RootElement.TryGetProperty("twitchChannels", out var tc) && tc.ValueKind == JsonValueKind.String) twitchChannels = tc.GetString() ?? "";
            if (json.RootElement.TryGetProperty("kickChannels", out var kc) && kc.ValueKind == JsonValueKind.String) kickChannels = kc.GetString() ?? "";
            if (json.RootElement.TryGetProperty("accentColor", out var ac) && ac.ValueKind == JsonValueKind.String &&
                System.Text.RegularExpressions.Regex.IsMatch(ac.GetString() ?? "", "^#[0-9a-fA-F]{6}$"))
                accentColor = ac.GetString()!;
            if (json.RootElement.TryGetProperty("reduceMotion", out var m) && (m.ValueKind == JsonValueKind.True || m.ValueKind == JsonValueKind.False)) reduceMotion = m.GetBoolean();
            if (json.RootElement.TryGetProperty("accountNames", out var names) && names.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < Math.Min(4, names.GetArrayLength()); i++)
                    if (names[i].ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(names[i].GetString())) accountNames[i] = names[i].GetString()!;
            if (json.RootElement.TryGetProperty("layout", out var l)) layout = l.GetString() == "row" ? "row" : "grid";
            if (json.RootElement.TryGetProperty("zooms", out var z) && z.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < Math.Min(4, z.GetArrayLength()); i++) zooms[i] = Math.Clamp(z[i].GetDouble(), .3, 1.5);
        }
        catch { }
    }
}
