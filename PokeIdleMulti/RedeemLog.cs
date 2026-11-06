using System.Diagnostics;

namespace PokeIdleMulti;

/// <summary>
/// O registro dos resgates (Lives → Registro dos resgates…): cada resgate, cada baú da Twitch, cada recusa e cada vez
/// que os pontos voltaram, com a hora, a conta, o site e o canal. Fica num arquivo de texto na pasta do deck
/// (resgates.log), de uma execução para outra, com as últimas <see cref="Keep"/> linhas.
/// </summary>
internal sealed class RedeemLog
{
    private const int Keep = 2000;

    private readonly List<string> _lines = new();

    public RedeemLog(string path)
    {
        FilePath = path;
        try
        {
            if (File.Exists(path))
                _lines.AddRange(File.ReadAllLines(path).Where(l => l.Length > 0).TakeLast(Keep));
        }
        catch
        {
            // Arquivo preso ou estragado: começa vazio, sem derrubar o deck.
        }
    }

    public string FilePath { get; }

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Entrou uma linha nova (na thread da interface).</summary>
    public event Action? Changed;

    public void Add(string account, LiveSite site, string text)
    {
        var line = $"{DateTime.Now:dd/MM/yy HH:mm}  {account} · {site} · {text}";
        _lines.Add(line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (_lines.Count > Keep * 2)
            {
                _lines.RemoveRange(0, _lines.Count - Keep);
                File.WriteAllLines(FilePath, _lines);
            }
            else
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Não conseguir gravar o registro não pode parar o resgate.
        }
        Changed?.Invoke();
    }
}

/// <summary>A janela do registro dos resgates: o mais novo em cima, atualizando sozinha enquanto está aberta.</summary>
internal sealed class RedeemLogWindow : Form
{
    private readonly RedeemLog _log;
    private readonly TextBox _text = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    public RedeemLogWindow(RedeemLog log)
    {
        _log = log;
        Text = "Registro dos resgates";
        Icon = Theme.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 460);
        Padding = new Padding(8);

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 40,
            Text = "Cada resgate, baú da Twitch, recusa e pontos que voltaram, o mais novo em cima. A conferência do saldo " +
                   "(Kick a cada 5 min, Twitch a cada 2) só aparece aqui quando algo acontece; o saldo de agora fica no menu Lives.",
        };
        var open = new Button { Text = "Abrir o arquivo", Width = 120 };
        open.Click += (_, _) =>
        {
            try
            {
                if (File.Exists(_log.FilePath))
                    Process.Start(new ProcessStartInfo(_log.FilePath) { UseShellExecute = true });
            }
            catch
            {
                // Sem programa para abrir .log: fica só a janela.
            }
        };
        var close = new Button { Text = "Fechar", Width = 90, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        CancelButton = close;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 38 };
        buttons.Controls.Add(close);
        buttons.Controls.Add(open);

        Controls.Add(_text);
        Controls.Add(buttons);
        Controls.Add(hint);
        _log.Changed += Fill;
        Fill();
    }

    /// <summary>O que a janela mostra agora (autoteste).</summary>
    public string ShownText => _text.Text;

    private void Fill()
    {
        if (IsDisposed)
            return;
        _text.Text = _log.Lines.Count == 0
            ? "Nenhum resgate ainda. Escolha o bônus de cada conta em Lives → Config…; o baú da Twitch vem ligado de fábrica."
            : string.Join(Environment.NewLine, _log.Lines.Reverse());
        _text.SelectionStart = 0;
        _text.ScrollToCaret();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _log.Changed -= Fill;
        base.OnFormClosed(e);
    }
}