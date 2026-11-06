namespace PokeIdleMulti;

/// <summary>
/// A recompra de uma conta (o carrinho no cabeçalho dela): ligar, quanto manter de pokebola, revive e poção,
/// qual poção e o mínimo de ouro. Vale na hora, sem botão de salvar; o estoque e o registro das compras se
/// atualizam enquanto a janela está aberta.
/// </summary>
internal sealed class RestockWindow : Form
{
    private const int InfoWidth = 520;

    private readonly RestockConfig _config;
    private readonly CheckBox _on = new() { Text = "Recompra ligada nesta conta", AutoSize = true };
    private readonly Label _state = Info();
    private readonly CheckBox _ballsOn = Check("Recomprar");
    private readonly ComboBox _ball = Combo(220);
    private readonly NumericUpDown _ballKeep = Number(Restock.MaxStock);
    private readonly NumericUpDown _ballBelow = Number(Restock.MaxStock);
    private readonly Label _ballInfo = Info();
    private readonly CheckBox _revivesOn = Check("Recomprar (sempre o revive mais barato)");
    private readonly NumericUpDown _reviveKeep = Number(Restock.MaxStock);
    private readonly NumericUpDown _reviveBelow = Number(Restock.MaxStock);
    private readonly Label _reviveInfo = Info();
    private readonly CheckBox _potionsOn = Check("Recomprar");
    private readonly ComboBox _potion = Combo(330);
    private readonly NumericUpDown _potionKeep = Number(Restock.MaxStock);
    private readonly NumericUpDown _potionBelow = Number(Restock.MaxStock);
    private readonly CheckBox _potionFirst = Check("Pôr a poção escolhida em 1º na fila da automação \"Usar Poções\"");
    private readonly Label _potionInfo = Info();
    private readonly NumericUpDown _goldFloor = Number(1_000_000_000_000);
    private readonly Label _gold = Info();
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = InfoWidth, Height = 120,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };
    private bool _loading;

    /// <summary>A configuração mudou (o deck salva e manda para a página).</summary>
    public event Action? Changed;

    private sealed record Choice(int Id, string Text)
    {
        public override string ToString() => Text;
    }

    public RestockWindow(string account, RestockConfig config)
    {
        _config = config;
        Text = $"Recompra · {account}";
        Icon = Theme.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(0, 0, 12, 12);

        _on.Font = new Font(Font, FontStyle.Bold);
        var close = new Button { Text = "Fechar", DialogResult = DialogResult.Cancel, Width = 90, Anchor = AnchorStyles.Right };
        close.Click += (_, _) => Close();
        CancelButton = close;

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(12, 12),
        };
        stack.Controls.Add(_on);
        stack.Controls.Add(_state);
        stack.Controls.Add(Group("Pokébola",
            Row(_ballsOn, Caption("Qual:"), _ball),
            Row(Caption("Manter"), _ballKeep, Caption("Recomprar abaixo de"), _ballBelow),
            _ballInfo));
        stack.Controls.Add(Group("Revives",
            Row(_revivesOn),
            Row(Caption("Manter (todos os revives)"), _reviveKeep, Caption("Recomprar abaixo de"), _reviveBelow),
            _reviveInfo));
        stack.Controls.Add(Group("Poções",
            Row(_potionsOn, Caption("Qual comprar:"), _potion),
            Row(Caption("Manter (todas as poções)"), _potionKeep, Caption("Recomprar abaixo de"), _potionBelow),
            _potionFirst,
            _potionInfo));
        stack.Controls.Add(Row(Caption("Nunca deixar o ouro abaixo de"), _goldFloor, _gold));
        stack.Controls.Add(Group("Registro", _log));
        var note = Info();
        note.Text = "Compra no Market do NPC, até 9.999 por vez, uma compra a cada 4 s, com a conta ligada no deck. " +
                    "Se o ouro não der para tudo: poções, depois revives, depois a pokebola. A Golden Potion fica de fora.";
        stack.Controls.Add(note);
        stack.Controls.Add(close);
        Controls.Add(stack);

        _loading = true;
        _on.Checked = config.On;
        _ballsOn.Checked = config.Balls.On;
        _ballKeep.Value = config.Balls.Keep;
        _ballBelow.Value = config.Balls.Below;
        _revivesOn.Checked = config.Revives.On;
        _reviveKeep.Value = config.Revives.Keep;
        _reviveBelow.Value = config.Revives.Below;
        _potionsOn.Checked = config.Potions.On;
        _potionKeep.Value = config.Potions.Keep;
        _potionBelow.Value = config.Potions.Below;
        _potionFirst.Checked = config.PotionFirst;
        _goldFloor.Value = Math.Min(config.GoldFloor, _goldFloor.Maximum);
        _loading = false;
        ShowStatus(null);

        foreach (var box in new[] { _on, _ballsOn, _revivesOn, _potionsOn, _potionFirst })
            box.CheckedChanged += (_, _) => Apply();
        foreach (var number in new[] { _ballKeep, _ballBelow, _reviveKeep, _reviveBelow, _potionKeep, _potionBelow, _goldFloor })
            number.ValueChanged += (_, _) => Apply();
        _ball.SelectedIndexChanged += (_, _) => Apply();
        _potion.SelectedIndexChanged += (_, _) => Apply();
    }

    /// <summary>O texto do estado (no alto da janela), para o autoteste.</summary>
    public string StateText => _state.Text;

    /// <summary>A poção escolhida na lista, para o autoteste.</summary>
    public string PotionText => _potion.Text;

    /// <summary>O mesmo que clicar em "Recompra ligada nesta conta".</summary>
    public void SetOn(bool on) => _on.Checked = on;

    /// <summary>Como digitar no "Manter" da pokebola: o valor lido e o texto que fica no campo (para o autoteste).</summary>
    public (decimal Value, string Text) TypeBallKeep(string text)
    {
        _ballKeep.Text = text;
        var value = _ballKeep.Value;
        return (value, _ballKeep.Text);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Mostrada sem ser modal, a janela não centraliza sozinha no deck.
        var area = Owner?.Bounds ?? Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    /// <summary>O estoque, as listas (com os nomes e preços do jogo) e o registro que a página contou.</summary>
    public void ShowStatus(RestockStatus? status)
    {
        bool connected = status is { Connected: true };
        _state.ForeColor = !_config.On ? SystemColors.GrayText : connected && !status!.Paused ? Color.SeaGreen : Color.DarkGoldenrod;
        _state.Text = !_config.On ? "Desligada. Ligada, ela confere o estoque a cada segundo e compra o que faltar."
            : !connected ? "Ligada, esperando o jogo conectar (a conta precisa estar no jogo, não na tela de login)."
            : status!.Paused ? "Ligada, em pausa: 3 compras seguidas não deram certo (volta sozinha em 2 min; veja o registro)."
            : $"Ligada{(status.Nick is null ? "" : $" na conta {status.Nick}")}.";

        var balls = status?.Balls is { Count: > 0 } live ? live : Restock.KnownBalls.ToList();
        Fill(_ball, balls.Select(b => new Choice(b.Id, b.Price > 0 ? $"{b.Name} · {Restock.N(b.Price)} ouro" : b.Name ?? $"bola {b.Id}")),
            _config.Balls.Id);
        var potions = status?.Potions is { Count: > 0 } livePotions ? livePotions : Restock.KnownPotions.ToList();
        Fill(_potion, potions.Select(p => new Choice(p.Id, $"{p.Name} · {Restock.N(p.Price)} ouro · {Restock.N(p.Heal)} HP"))
            .Prepend(new Choice(0, "Melhor custo-benefício (automático)")), _config.Potions.Id);

        if (!connected)
        {
            _ballInfo.Text = _reviveInfo.Text = "Na mochila: —";
            _potionInfo.Text = "Na mochila: —";
            _gold.Text = "";
        }
        else
        {
            var ball = status!.Balls?.FirstOrDefault(b => b.Id == _config.Balls.Id);
            _ballInfo.Text = ball is null ? "Essa pokebola não está à venda no NPC." : $"Na mochila: {Restock.N(ball.Have)}";
            var revives = status.Revives ?? new();
            _reviveInfo.Text = $"Na mochila: {Restock.N(status.ReviveHave)}" +
                               (revives.Count > 0 ? $" ({string.Join(" · ", revives.Select(r => $"{r.Name} {Restock.N(r.Have)}"))})" : "") +
                               (revives.Count > 0 ? $"\nCompra: {revives[0].Name}, {Restock.N(revives[0].Price)} ouro" : "");
            _potionInfo.Text = PotionInfo(status);
            _gold.Text = $"ouro agora: {Restock.N(status.Gold)}";
        }

        var log = string.Join(Environment.NewLine, status?.Log ?? new());
        if (_log.Text != log)
        {
            _log.Text = log;
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
    }

    private string PotionInfo(RestockStatus status)
    {
        var potions = status.Potions ?? new();
        var lines = new List<string> { $"Na mochila (todas): {Restock.N(status.PotionHave)}" };
        if (status.MaxHp is double hp && status.Threshold is double threshold)
        {
            var costs = potions.Where(p => p.Cost is not null).OrderBy(p => p.Cost)
                .Select(p => $"{p.Name} {Restock.Cost(p.Cost!.Value)}");
            lines.Add($"Ouro por HP útil no pokémon ativo ({Restock.N(hp)} HP, poção abaixo de {threshold * 100:0}%): " +
                      string.Join(" · ", costs));
        }
        else
        {
            lines.Add("Custo-benefício: esperando o jogo mandar o pokémon ativo.");
        }
        var chosen = potions.FirstOrDefault(p => p.Id == status.PotionId);
        if (chosen is not null)
            lines.Add($"Compra: {chosen.Name}" + (status.QueueFirst == chosen.Id ? " (1ª na fila do \"Usar Poções\")" : ""));
        return string.Join("\n", lines);
    }

    private void Apply()
    {
        if (_loading)
            return;
        _config.On = _on.Checked;
        _config.Balls.On = _ballsOn.Checked;
        _config.Balls.Id = (_ball.SelectedItem as Choice)?.Id ?? _config.Balls.Id;
        _config.Balls.Keep = (long)_ballKeep.Value;
        _config.Balls.Below = (long)_ballBelow.Value;
        _config.Revives.On = _revivesOn.Checked;
        _config.Revives.Keep = (long)_reviveKeep.Value;
        _config.Revives.Below = (long)_reviveBelow.Value;
        _config.Potions.On = _potionsOn.Checked;
        _config.Potions.Id = (_potion.SelectedItem as Choice)?.Id ?? _config.Potions.Id;
        _config.Potions.Keep = (long)_potionKeep.Value;
        _config.Potions.Below = (long)_potionBelow.Value;
        _config.PotionFirst = _potionFirst.Checked;
        _config.GoldFloor = (long)_goldFloor.Value;
        Changed?.Invoke();
    }

    /// <summary>Troca as opções da lista só quando mudaram, e nunca com ela aberta (a escolha não pula sozinha).</summary>
    private void Fill(ComboBox box, IEnumerable<Choice> choices, int selected)
    {
        var list = choices.ToList();
        if (list.All(c => c.Id != selected))
            list.Add(new Choice(selected, $"item {selected}"));
        if (box.DroppedDown || box.Items.Cast<Choice>().SequenceEqual(list))
            return;
        _loading = true;
        box.Items.Clear();
        box.Items.AddRange(list.ToArray<object>());
        box.SelectedItem = list.First(c => c.Id == selected);
        _loading = false;
    }

    private static Control Group(string title, params Control[] rows)
    {
        var inner = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(8, 20),
        };
        inner.Controls.AddRange(rows);
        // Todos com a mesma largura, a do texto mais comprido (as linhas das poções).
        var group = new GroupBox
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 6),
            MinimumSize = new Size(InfoWidth + 30, 0),
        };
        group.Controls.Add(inner);
        return group;
    }

    private static Control Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 0) };

    private static Label Info() => new() { AutoSize = true, MaximumSize = new Size(InfoWidth, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 4) };

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 4, 3, 0) };

    private static ComboBox Combo(int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };

    private static NumericUpDown Number(decimal max) => new Count { Maximum = max, Width = 110, TextAlign = HorizontalAlignment.Right };

    /// <summary>
    /// Quantidade inteira escrita como no resto da janela e no registro (30.000), seja qual for o formato de
    /// números do Windows. Aceita digitar com ponto, vírgula ou sem nada.
    /// </summary>
    private sealed class Count : NumericUpDown
    {
        // Como na classe base: com texto digitado (UserEdit), ler o Value valida o texto, então a marca sai antes;
        // e o texto escrito aqui (ChangingText) não conta como digitado.
        protected override void UpdateEditText()
        {
            if (UserEdit)
            {
                ValidateEditText();
                return;
            }
            ChangingText = true;
            Text = Restock.N((double)Value);
        }

        protected override void ValidateEditText()
        {
            UserEdit = false;
            if (decimal.TryParse(new string(Text.Where(char.IsDigit).ToArray()), out var value))
                Value = Math.Clamp(value, Minimum, Maximum);
            UpdateEditText();
        }
    }
}