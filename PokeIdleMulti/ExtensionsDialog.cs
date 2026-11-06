using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>Instalar, ligar/desligar e remover as extensões de cada conta.</summary>
internal sealed class ExtensionsDialog : Form
{
    private readonly IReadOnlyList<AccountPane> _panes;
    private readonly ExtensionStore _store;
    private readonly CoreWebView2Environment _env;
    private readonly string _root;
    private readonly Action _changed;
    private readonly ComboBox _account = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly CheckBox _allAccounts = new() { Text = "Instalar em todas as contas", AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
    private bool _filling;

    public ExtensionsDialog(IReadOnlyList<AccountPane> panes, ExtensionStore store, CoreWebView2Environment env, string root, Action changed)
    {
        _panes = panes;
        _store = store;
        _env = env;
        _root = root;
        _changed = changed;

        Text = "Extensões das contas";
        Icon = Theme.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 440);
        MinimumSize = new Size(600, 320);
        ShowInTaskbar = false;
        Padding = new Padding(10);

        _list.Columns.Add("Extensão (marcada = ligada)", 420);
        _list.Columns.Add("ID", 250);
        _list.ItemChecked += async (_, e) => await SetEnabledAsync(e.Item);

        foreach (var pane in panes)
            _account.Items.Add(pane.AccountName);
        _account.SelectedIndexChanged += async (_, _) => await RefreshListAsync();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36 };
        top.Controls.Add(new Label { Text = "Conta:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        top.Controls.Add(_account);
        top.Controls.Add(new Label
        {
            Text = "Extensão nova só entra na página depois de recarregar a conta (botão ⟳ no cabeçalho dela).",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(12, 7, 0, 0),
        });

        var add = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 6, 0, 0) };
        add.Controls.Add(NewButton("Importar do meu Chrome…", async () => await ImportFromChromeAsync()));
        add.Controls.Add(NewButton("Da Chrome Web Store…", async () => await FromWebStoreAsync()));
        add.Controls.Add(NewButton("Pasta descompactada…", async () => await FromFolderAsync()));
        add.Controls.Add(_allAccounts);

        var manage = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
        manage.Controls.Add(NewButton("Fechar", () => { Close(); return Task.CompletedTask; }));
        manage.Controls.Add(NewButton("Remover", async () => await RemoveAsync()));
        manage.Controls.Add(NewButton("Opções", async () => await OpenOptionsAsync()));
        manage.Controls.Add(NewButton("Trazer configurações…", () => { ImportSettings(); return Task.CompletedTask; }));

        Controls.Add(_list);
        Controls.Add(manage);
        Controls.Add(add);
        Controls.Add(top);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_account.Items.Count > 0)
            _account.SelectedIndex = 0;
    }

    private AccountPane? Selected => _account.SelectedIndex >= 0 ? _panes[_account.SelectedIndex] : null;

    private Button NewButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    /// <summary>Roda uma ação com o cursor de espera e mostra o erro, se der.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        UseWaitCursor = true;
        Enabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Explain(ex), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Enabled = true;
            UseWaitCursor = false;
        }
    }

    private static string Explain(Exception ex) => ex switch
    {
        HttpRequestException => "Não consegui baixar da Chrome Web Store: " + ex.Message,
        COMException => "O motor do navegador recusou a extensão: " + ex.Message,
        _ => ex.Message,
    };

    private async Task RefreshListAsync()
    {
        _filling = true;
        _list.Items.Clear();
        try
        {
            if (Selected?.Profile is not CoreWebView2Profile profile)
                return;
            foreach (var extension in await profile.GetBrowserExtensionsAsync())
            {
                var item = new ListViewItem(extension.Name) { Checked = extension.IsEnabled, Tag = extension };
                item.SubItems.Add(extension.Id);
                _list.Items.Add(item);
            }
        }
        finally
        {
            _filling = false;
        }
    }

    private async Task SetEnabledAsync(ListViewItem item)
    {
        if (_filling || item.Tag is not CoreWebView2BrowserExtension extension)
            return;
        await RunAsync(async () =>
        {
            await extension.EnableAsync(item.Checked);
            _changed();
        });
    }

    private async Task InstallAsync(string folder)
    {
        var targets = _allAccounts.Checked ? _panes.ToList() : Selected is { } selected ? new List<AccountPane> { selected } : new();
        ExtensionInfo? info = null;
        foreach (var pane in targets.Where(p => p.Profile is not null))
            info = await _store.InstallAsync(pane.Profile!, folder);
        await RefreshListAsync();
        _changed();
        if (info is not null)
        {
            MessageBox.Show(this,
                $"Instalada: {info.Name}\n\nEla entra na página quando a conta for recarregada (botão ⟳ no cabeçalho dela).",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task ImportFromChromeAsync()
    {
        var available = ExtensionStore.FindChromeExtensions();
        if (available.Count == 0)
        {
            MessageBox.Show(this, "Não achei extensões no seu Google Chrome.", Text);
            return;
        }

        using var pick = new Form
        {
            Text = "Importar do Chrome",
            Icon = Theme.AppIcon,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(420, 360),
            ShowInTaskbar = false,
            Padding = new Padding(10),
        };
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        foreach (var extension in available)
            list.Items.Add(extension.Name);
        var ok = new Button { Text = "Importar", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 32 };
        pick.Controls.Add(list);
        pick.Controls.Add(ok);
        pick.AcceptButton = ok;
        if (pick.ShowDialog(this) != DialogResult.OK)
            return;

        foreach (int index in list.CheckedIndices)
            await InstallAsync(_store.ImportFromChrome(available[index]));
    }

    private async Task FromWebStoreAsync()
    {
        var link = Prompt.Ask(this, "Da Chrome Web Store",
            "Cole o link da extensão na Chrome Web Store (ou só o ID dela):");
        if (string.IsNullOrWhiteSpace(link))
            return;
        await InstallAsync(await _store.DownloadFromWebStoreAsync(link));
    }

    private async Task FromFolderAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "Pasta da extensão descompactada (a que tem o manifest.json)" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        await InstallAsync(_store.CopyUnpacked(dialog.SelectedPath));
    }

    private async Task RemoveAsync()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not CoreWebView2BrowserExtension extension)
            return;
        var answer = MessageBox.Show(this, $"Remover \"{extension.Name}\" de {Selected?.AccountName}?", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return;
        await extension.RemoveAsync();
        await _store.CleanupAsync(_panes.Select(p => p.Profile).OfType<CoreWebView2Profile>());
        await RefreshListAsync();
        _changed();
    }

    /// <summary>Copia as configurações da extensão de outro lugar (Chrome, IdleDeck, outra conta) para a conta escolhida.</summary>
    private void ImportSettings()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not CoreWebView2BrowserExtension extension || Selected is not { } target)
        {
            MessageBox.Show(this, "Escolha a conta lá em cima e clique na extensão na lista.", Text);
            return;
        }
        var others = _panes.Where(p => p != target).Select(p => (p.ProfileName, p.AccountName)).ToList();
        var sources = StorageImport.FindSources(_root, extension.Id, others);
        if (sources.Count == 0)
        {
            MessageBox.Show(this, $"Não achei configurações de \"{extension.Name}\" em outro lugar (Chrome, IdleDeck ou outra conta do deck).", Text);
            return;
        }

        using var pick = new Form
        {
            Text = $"Trazer configurações para {target.AccountName}",
            Icon = Theme.AppIcon,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(520, 300),
            ShowInTaskbar = false,
            Padding = new Padding(10),
        };
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        foreach (var source in sources)
            list.Items.Add($"{source.Label}  —  mudou em {source.Changed:dd/MM HH:mm}, {Math.Max(1, source.Size / 1024)} KB");
        list.SelectedIndex = 0;
        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            Text = $"De onde vêm as configurações de \"{extension.Name}\"? As que {target.AccountName} tem hoje ficam guardadas num backup.",
        };
        var ok = new Button { Text = "Trazer", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 32 };
        pick.Controls.Add(list);
        pick.Controls.Add(ok);
        pick.Controls.Add(hint);
        pick.AcceptButton = ok;
        if (pick.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0)
            return;

        var chosen = sources[list.SelectedIndex];
        StorageImport.Queue(_root, new PendingImport(extension.Id, chosen.Folder, chosen.Label, target.ProfileName));
        MessageBox.Show(this,
            $"Combinado: as configurações de \"{extension.Name}\" vindas de {chosen.Label} entram em {target.AccountName} " +
            "na próxima vez que o deck abrir (com ele aberto, o navegador está usando esses arquivos).\n\n" +
            "Feche o deck e abra de novo.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task OpenOptionsAsync()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not CoreWebView2BrowserExtension extension || Selected is null)
            return;
        if (_store.Find(extension.Id)?.Options is not string options)
        {
            MessageBox.Show(this, "Essa extensão não tem página de opções.", Text);
            return;
        }
        var window = await PopupWindow.CreateAsync(_env, Selected.ProfileName, new Size(900, 680), closeOnDeactivate: false);
        window.Location = new Point(Left + 40, Top + 40);
        window.WebView.CoreWebView2.Navigate($"chrome-extension://{extension.Id}/{options}");
        window.Show(Owner);
    }
}