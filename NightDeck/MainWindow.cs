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
    private static readonly Brush AccentBrush = Brush("#991B1B");
    private readonly DockPanel root = new();
    private readonly DockPanel toolbar = new() { LastChildFill = true };
    private readonly StackPanel toolbarLeft = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel toolbarRight = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid board = new() { Background = BackgroundBrush };
    private readonly Border[] panes = new Border[4];
    private readonly WebView2[] browsers = new WebView2[4];
    private readonly TextBlock[] zoomLabels = new TextBlock[4];
    private readonly Button[] accountButtons = new Button[4];
    private readonly bool[] muted = new bool[4];
    private readonly bool[] hidden = new bool[4];
    private readonly double[] zooms = { .6, .6, .6, .6 };
    private readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Night Deck", "settings.json");
    private readonly UpdateService updater = new(UpdateRepository());
    private readonly MenuItem updateCheckItem = new() { Header = "Verificar agora" };
    private bool checkingUpdates;
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
        Content = root;
        var menu = new Menu { Background = BarBrush, Foreground = Brushes.White, Height = 26 };
        var updates = new MenuItem { Header = "Atualizações" };
        updateCheckItem.Click += async (_, _) => await CheckUpdatesAsync(true);
        updates.Items.Add(updateCheckItem);
        menu.Items.Add(updates);
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
        toolbarLeft.Children.Add(Btn("Cores", "Personalização em desenvolvimento", () => MessageBox.Show(this, "Personalização de cores ainda não implementada.", "Night Deck")));
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            var button = Btn($"Conta {i + 1}", "Focar esta conta", () => { hidden[index] = false; focused = focused == index ? null : index; Rebuild(); });
            accountButtons[i] = button;
            toolbarLeft.Children.Add(button);
        }
        toolbarLeft.Children.Add(Btn("Todas", "Mostrar todas", () => { focused = null; Array.Fill(hidden, false); Rebuild(); }));
        toolbarRight.Children.Add(Btn("−", "Diminuir zoom de todas", () => SetGlobalZoom(-.1), width: 34));
        toolbarRight.Children.Add(Btn("60%", "Restaurar zoom", () => { for (int i = 0; i < 4; i++) SetZoom(i, .6); }));
        toolbarRight.Children.Add(Btn("+", "Aumentar zoom de todas", () => SetGlobalZoom(.1), width: 34));
        toolbarRight.Children.Add(Btn("⛶", "Tela cheia (F11)", ToggleFullscreen, width: 36));
        toolbarRight.Children.Add(Btn("⟳ Todas", "Recarregar as contas", () => { foreach (var b in browsers) b?.CoreWebView2?.Reload(); }));
        toolbarRight.Children.Add(Btn("Navegador", "Abrir PokeIdle no navegador", () => OpenUrl(GameUrl)));
        toolbarRight.Children.Add(Btn("Onde caçar", "Ferramenta de caça", () => OpenUrl(HuntUrl)));
        toolbarRight.Children.Add(Btn("Echo", "Em desenvolvimento", () => MessageBox.Show(this, "Echo ainda não implementado.", "Night Deck")));
        toolbarRight.Children.Add(Btn("♜", "Twitch (abrir no navegador)", () => OpenUrl("https://www.twitch.tv/"), width: 36));
        DockPanel.SetDock(toolbarLeft, Dock.Left);
        toolbar.Children.Add(toolbarLeft);
        toolbar.Children.Add(toolbarRight);
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
        var name = new TextBlock { Text = $"● Conta {i + 1}", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
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
        var window = new Window
        {
            Title = $"Restock — Conta {i + 1}",
            Owner = this, Width = 370, Height = 285,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = BarBrush, Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Reposição de recursos", FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Escolha a quantidade desejada para futura recompra:", TextWrapping = TextWrapping.Wrap });
        var balls = new TextBox { Text = "100", Margin = new Thickness(0, 7, 0, 7), ToolTip = "Quantidade alvo de Pokéballs" };
        var potions = new TextBox { Text = "50", Margin = new Thickness(0, 0, 0, 8), ToolTip = "Quantidade alvo de poções" };
        panel.Children.Add(new TextBlock { Text = "Pokéballs (quantidade alvo)" });
        panel.Children.Add(balls);
        panel.Children.Add(new TextBlock { Text = "Poções (quantidade alvo)" });
        panel.Children.Add(potions);
        var note = new TextBlock { Text = "A recompra automática ainda depende da integração com o mercado do jogo. Nenhuma compra será feita por este painel.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 2, 0, 10) };
        panel.Children.Add(note);
        panel.Children.Add(Btn("Abrir mercado desta conta", "Tentar abrir o mercado sem comprar", () => { window.Close(); _ = OpenMarketAsync(i); }));
        window.Content = panel;
        window.ShowDialog();
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

    private void SetZoom(int i, double value)
    {
        zooms[i] = Math.Round(Math.Clamp(value, .3, 1.5), 2);
        zoomLabels[i].Text = $"{zooms[i] * 100:0}%";
        if (browsers[i].CoreWebView2 != null) browsers[i].ZoomFactor = zooms[i];
        SaveSettings();
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
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!); File.WriteAllText(settingsFile, JsonSerializer.Serialize(new { layout, zooms })); }
        catch { }
    }
    private void LoadSettings()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(settingsFile));
            if (json.RootElement.TryGetProperty("layout", out var l)) layout = l.GetString() == "row" ? "row" : "grid";
            if (json.RootElement.TryGetProperty("zooms", out var z) && z.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < Math.Min(4, z.GetArrayLength()); i++) zooms[i] = Math.Clamp(z[i].GetDouble(), .3, 1.5);
        }
        catch { }
    }
}
