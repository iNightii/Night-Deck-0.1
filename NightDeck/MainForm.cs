using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NightDeck;

public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(8, 12, 20);
    private static readonly Color Bar = Color.FromArgb(17, 24, 39);
    private static readonly Color ButtonColor = Color.FromArgb(30, 41, 59);
    private static readonly Color Accent = Color.FromArgb(153, 27, 27);
    private const string GameUrl = "https://pokeidle.io/";
    private readonly TableLayoutPanel root = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly FlowLayoutPanel toolbar = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, BackColor = Bar, Padding = new Padding(5, 4, 5, 2) };
    private readonly TableLayoutPanel board = new() { Dock = DockStyle.Fill, BackColor = Background, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly Panel toolbarSpacer = new() { Width = 0, Height = 29, Margin = Padding.Empty };
    private readonly Label ram = new() { AutoSize = true, ForeColor = Color.Silver, Margin = new Padding(10, 8, 8, 0) };
    private readonly Panel[] panes = new Panel[4];
    private readonly WebView2[] browsers = new WebView2[4];
    private readonly Label[] zoomLabels = new Label[4];
    private readonly bool[] muted = new bool[4];
    private readonly bool[] hidden = new bool[4];
    private readonly double[] zooms = { .6, .6, .6, .6 };
    private readonly System.Windows.Forms.Timer ramTimer = new() { Interval = 3000 };
    private readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Night Deck", "settings.json");
    private readonly UpdateService updater = new(UpdateRepository());
    private readonly ToolStripMenuItem updatesMenu = new("Atualizações");
    private readonly ToolStripMenuItem updateCheckItem = new("Verificar agora");
    private bool checkingUpdates;
    private string layout = "grid";
    private int? focused;
    private bool fullScreen;
    private FormBorderStyle previousBorder;
    private FormWindowState previousState;

    public MainForm()
    {
        Text = "Night Deck";
        MinimumSize = new Size(900, 520);
        WindowState = FormWindowState.Maximized;
        BackColor = Background;
        ForeColor = Color.White;
        LoadSettings();
        root.RowCount = 2;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(board, 0, 1);
        Controls.Add(root);
        BuildMainMenu();
        BuildToolbar();
        toolbar.Resize += (_, _) => AlignToolbar();
        for (int i = 0; i < 4; i++) BuildPane(i);
        Rebuild();
        ramTimer.Tick += (_, _) => RefreshRam();
        ramTimer.Start();
        RefreshRam();
        Shown += async (_, _) => { await Task.Delay(4000); if (!IsDisposed) await CheckUpdatesAsync(false); };
        FormClosing += (_, _) => SaveSettings();
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F11) ToggleFullscreen(); };
    }

    private static string UpdateRepository()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "update-source.json");
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            return json.RootElement.GetProperty("repository").GetString() ?? "";
        }
        catch { return ""; }
    }

    private void BuildMainMenu()
    {
        var menu = new MenuStrip { BackColor = Bar, ForeColor = Color.White, Dock = DockStyle.Top };
        updateCheckItem.Click += async (_, _) => await CheckUpdatesAsync(true);
        updatesMenu.DropDownItems.Add(updateCheckItem);
        menu.Items.Add(updatesMenu);
        menu.Items.Add(new ToolStripMenuItem("Ajuda", null, (_, _) =>
            MessageBox.Show("Night Deck — quatro contas PokeIdle", "Ajuda")));
        MainMenuStrip = menu;
        root.RowCount = 3;
        root.RowStyles.Clear();
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Remove(toolbar);
        root.Controls.Remove(board);
        root.Controls.Add(menu, 0, 0);
        root.Controls.Add(toolbar, 0, 1);
        root.Controls.Add(board, 0, 2);
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (checkingUpdates) return;
        checkingUpdates = true;
        updateCheckItem.Enabled = false;
        try
        {
            var result = await updater.CheckAsync();
            if (IsDisposed) return;
            if (!result.Available)
            {
                if (manual) MessageBox.Show(this, result.Message, "Atualizações", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var choice = MessageBox.Show(this, result.Message + "\n\nDeseja baixar a atualização agora?",
                "Atualização disponível", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (choice != DialogResult.Yes) return;
            if (result.InstallerUrl is null)
            {
                if (result.ReleasePage is not null) OpenUrl(result.ReleasePage);
                return;
            }
            updateCheckItem.Text = "Baixando atualização...";
            var installer = await updater.DownloadInstallerAsync(result.InstallerUrl);
            if (IsDisposed) return;
            if (MessageBox.Show(this, "Download concluído. Deseja executar o instalador agora?\nAs contas serão fechadas.",
                "Instalar atualização", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                Close();
            }
        }
        catch (Exception ex)
        {
            if (manual && !IsDisposed) MessageBox.Show(this, "Não foi possível verificar atualizações.\n" + ex.Message,
                "Atualizações", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            checkingUpdates = false;
            if (!IsDisposed) { updateCheckItem.Enabled = true; updateCheckItem.Text = "Verificar agora"; }
        }
    }

    private static Button Btn(string text, string tip, Action action, bool selected = false, int width = 0)
    {
        var b = text == "TWITCH_ICON" ? new TwitchIconButton() : new RoundedButton();
        b.Text = text == "TWITCH_ICON" ? "" : text;
        b.BackColor = selected ? Accent : ButtonColor;
        b.ForeColor = Color.White;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(60, 75, 95);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 65, 85);
        b.AutoSize = width == 0;
        if (width != 0) b.Width = width;
        b.Height = 29;
        b.Margin = new Padding(2, 2, 2, 0);
        b.Padding = new Padding(7, 0, 7, 0);
        b.Cursor = Cursors.Hand;
        b.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        b.FlatAppearance.BorderColor = Color.FromArgb(60, 75, 95);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 65, 85);
        b.Click += (_, _) => action();
        new ToolTip().SetToolTip(b, tip);
        return b;
    }

    private class RoundedButton : Button
    {
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width < 10 || Height < 10) return;
            using var path = new GraphicsPath();
            const int radius = 7;
            int d = radius * 2;
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(Width - d - 1, 0, d, d, 270, 90);
            path.AddArc(Width - d - 1, Height - d - 1, d, d, 0, 90);
            path.AddArc(0, Height - d - 1, d, d, 90, 90);
            path.CloseFigure();
            var old = Region;
            Region = new Region(path);
            old?.Dispose();
        }
    }

    private sealed class TwitchIconButton : RoundedButton
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.White, 1.7f);
            float x = Width / 2f - 8, y = Height / 2f - 9;
            PointF[] outer = {
                new(x, y), new(x + 17, y), new(x + 17, y + 12),
                new(x + 12, y + 17), new(x + 8, y + 17),
                new(x + 5, y + 20), new(x + 5, y + 17),
                new(x, y + 17)
            };
            g.DrawPolygon(pen, outer);
            g.DrawLine(pen, x + 7, y + 5, x + 7, y + 11);
            g.DrawLine(pen, x + 12, y + 5, x + 12, y + 11);
        }
    }

    private void AlignToolbar()
    {
        if (toolbarSpacer.IsDisposed || toolbar.IsDisposed) return;
        int other = toolbar.Controls.Cast<Control>().Where(c => c != toolbarSpacer && c.Visible)
            .Sum(c => c.Width + c.Margin.Horizontal);
        int width = Math.Max(0, toolbar.ClientSize.Width - toolbar.Padding.Horizontal - other - 6);
        if (toolbarSpacer.Width != width) toolbarSpacer.Width = width;
    }

    private void BuildToolbar()
    {
        toolbar.Controls.Clear();
        toolbar.Controls.Add(Btn("Grade 2×2", "Quatro contas em grade", () => { layout = "grid"; focused = null; Rebuild(); SaveSettings(); }, layout == "grid"));
        toolbar.Controls.Add(Btn("Vertical 1×4", "Quatro contas lado a lado", () => { layout = "row"; focused = null; Rebuild(); SaveSettings(); }, layout == "row"));
        toolbar.Controls.Add(Btn("Cores", "Personalização será adicionada na próxima etapa", () => MessageBox.Show("Personalização de cores será adicionada em uma próxima etapa.", "Night Deck")));
        for (int i = 0; i < 4; i++)
        {
            int n = i;
            toolbar.Controls.Add(Btn($"{i + 1} Conta {i + 1}", "Focar esta conta (clique novamente para voltar)", () => { hidden[n] = false; focused = focused == n ? null : n; Rebuild(); }, focused == i));
        }
        toolbar.Controls.Add(Btn("Todas", "Mostrar todas as contas", () => { focused = null; Array.Fill(hidden, false); Rebuild(); }));
        toolbar.Controls.Add(ram);
        toolbar.Controls.Add(toolbarSpacer);
        toolbar.Controls.Add(Btn("−", "Diminuir zoom de todas", () => SetGlobalZoom(-.1)));
        toolbar.Controls.Add(Btn("60%", "Restaurar zoom para 60%", () => { for (int i = 0; i < 4; i++) SetZoom(i, .6); }));
        toolbar.Controls.Add(Btn("+", "Aumentar zoom de todas", () => SetGlobalZoom(.1)));
        toolbar.Controls.Add(Btn("⛶", "Tela cheia (F11)", ToggleFullscreen));
        toolbar.Controls.Add(Btn("⟳ Todas", "Recarregar as quatro contas", () => { foreach (var b in browsers) b?.CoreWebView2?.Reload(); }));
        toolbar.Controls.Add(Btn("Navegador", "Abrir PokeIdle no navegador padrão", () => OpenUrl(GameUrl)));
        toolbar.Controls.Add(Btn("Onde caçar", "Abrir ferramenta de caça", () => OpenUrl("https://pokeidle-ferramentas.pages.dev/#hunt")));
        toolbar.Controls.Add(Btn("Echo", "Sincronização de cliques será adicionada em uma próxima etapa", () => MessageBox.Show("Echo ainda não está implementado nesta base nova.", "Night Deck")));
        toolbar.Controls.Add(Btn("TWITCH_ICON", "Abrir Twitch no navegador com acesso ao chat", () => OpenUrl("https://www.twitch.tv/"), width: 32));
        AlignToolbar();
    }

    private void BuildPane(int i)
    {
        var pane = new Panel { Dock = DockStyle.Fill, Margin = new Padding(2), BackColor = Color.FromArgb(12, 19, 31) };
        var header = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Bar, Padding = Padding.Empty };
        var name = new Label { Text = $"🟢 Conta {i + 1}", AutoSize = true, ForeColor = Color.White, Margin = new Padding(6, 7, 10, 0), Location = new Point(6, 7), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
        header.Controls.Add(name);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 350, Height = 28,
            WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Padding = Padding.Empty };
        actions.Controls.Add(Btn("−", "Diminuir zoom", () => SetZoom(i, zooms[i] - .1), width: 30));
        var zoom = new Label { Text = "60%", ForeColor = Color.White, Width = 38, Height = 25, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 2, 0, 0) };
        zoomLabels[i] = zoom;
        actions.Controls.Add(zoom);
        actions.Controls.Add(Btn("+", "Aumentar zoom", () => SetZoom(i, zooms[i] + .1), width: 30));
        actions.Controls.Add(Btn("◖))", "Ativar ou desativar som", () => { muted[i] = !muted[i]; if (browsers[i].CoreWebView2 is not null) browsers[i].CoreWebView2.IsMuted = muted[i]; }, width: 43));
        actions.Controls.Add(Btn("⟳", "Recarregar", () => browsers[i].CoreWebView2?.Reload(), width: 30));
        actions.Controls.Add(Btn("⛶", "Expandir conta", () => { focused = focused == i ? null : i; Rebuild(); }, width: 30));
        actions.Controls.Add(Btn("◉", "Ocultar conta", () => { hidden[i] = true; focused = null; Rebuild(); }, width: 30));
        actions.Controls.Add(Btn("🛒", "Abrir mercado do PokeIdle nesta conta", () => OpenMarket(i), width: 36));
        header.Controls.Add(actions);
        var browser = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(12, 19, 31) };
        browsers[i] = browser;
        pane.Controls.Add(browser);
        pane.Controls.Add(header);
        panes[i] = pane;
        _ = InitBrowser(i);
    }

    private async void OpenMarket(int i)
    {
        var browser = browsers[i];
        if (browser.CoreWebView2 is null) return;
        // Nao compra itens automaticamente: apenas abre o mercado dentro da conta selecionada.
        const string script = @"(() => {
          const links = [...document.querySelectorAll('a, button, [role=button]')];
          const target = links.find(el => /^(market|mercado)$/i.test((el.textContent || '').trim()));
          if (target) { target.click(); return true; }
          return false;
        })()";
        try
        {
            var found = await browser.CoreWebView2.ExecuteScriptAsync(script);
            if (found != "true") MessageBox.Show(this,
                "Não encontrei o botão Mercado nesta página. Abra o mercado manualmente.\nA recompra automática ainda não está implementada.",
                "Restock", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Restock"); }
    }

    private async Task InitBrowser(int i)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Night Deck", "Accounts", $"Account{i + 1}");
            Directory.CreateDirectory(folder);
            var env = await CoreWebView2Environment.CreateAsync(null, folder);
            if (IsDisposed || browsers[i].IsDisposed) return;
            await browsers[i].EnsureCoreWebView2Async(env);
            browsers[i].CoreWebView2.Settings.AreDevToolsEnabled = false;
            browsers[i].CoreWebView2.IsMuted = muted[i];
            browsers[i].ZoomFactor = zooms[i];
            browsers[i].CoreWebView2.Navigate(GameUrl);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) MessageBox.Show($"Não foi possível iniciar a Conta {i + 1}: {ex.Message}", "Night Deck");
        }
    }

    private void SetZoom(int i, double value)
    {
        zooms[i] = Math.Round(Math.Clamp(value, .3, 1.5), 2);
        zoomLabels[i].Text = $"{zooms[i] * 100:0}%";
        if (browsers[i]?.CoreWebView2 != null) browsers[i].ZoomFactor = zooms[i];
        SaveSettings();
    }
    private void SetGlobalZoom(double delta) { for (int i = 0; i < 4; i++) SetZoom(i, zooms[i] + delta); }

    private void Rebuild()
    {
        board.SuspendLayout();
        board.Controls.Clear();
        board.ColumnStyles.Clear(); board.RowStyles.Clear();
        var indices = focused is int f ? new[] { f } : Enumerable.Range(0, 4).Where(i => !hidden[i]).ToArray();
        if (indices.Length == 0) { Array.Fill(hidden, false); indices = Enumerable.Range(0, 4).ToArray(); }
        int cols = focused.HasValue ? 1 : layout == "row" ? indices.Length : Math.Min(2, indices.Length);
        int rows = (int)Math.Ceiling(indices.Length / (double)cols);
        board.ColumnCount = cols; board.RowCount = rows;
        for (int c = 0; c < cols; c++) board.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
        for (int r = 0; r < rows; r++) board.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        for (int k = 0; k < indices.Length; k++)
        {
            var pane = panes[indices[k]];
            if (pane.Parent != null) pane.Parent.Controls.Remove(pane);
            board.Controls.Add(pane, k % cols, k / cols);
        }
        board.ResumeLayout(true);
        BuildToolbar();
    }

    private void RefreshRam()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            long bytes = process.WorkingSet64;
            ram.Text = $"● RAM {bytes / 1024d / 1024d:0} MB (processo principal)";
        }
        catch { ram.Text = "RAM —"; }
    }

    private void ToggleFullscreen()
    {
        if (!fullScreen)
        {
            previousBorder = FormBorderStyle; previousState = WindowState;
            WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Maximized;
        }
        else { WindowState = FormWindowState.Normal; FormBorderStyle = previousBorder; WindowState = previousState; }
        fullScreen = !fullScreen;
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
