namespace PokeIdleMulti;

/// <summary>Cores do deck (tema escuro) e o desenho das barras.</summary>
internal static class Theme
{
    public static readonly Color Back = Color.FromArgb(17, 17, 23);
    public static readonly Color Bar = Color.FromArgb(25, 25, 33);
    public static readonly Color Hover = Color.FromArgb(44, 44, 58);
    public static readonly Color Border = Color.FromArgb(48, 48, 62);
    public static readonly Color Accent = Color.FromArgb(124, 92, 255);
    public static readonly Color AccentDim = Color.FromArgb(58, 48, 110);
    public static readonly Color Text = Color.FromArgb(228, 228, 238);
    public static readonly Color Muted = Color.FromArgb(138, 138, 158);
    public static readonly Color Ok = Color.FromArgb(46, 204, 113);
    public static readonly Color Warn = Color.FromArgb(241, 196, 15);
    public static readonly Color Bad = Color.FromArgb(231, 76, 60);
    public static readonly Color Vote = Color.FromArgb(255, 152, 0);

    public static ToolStripRenderer Renderer { get; } = new DarkRenderer();

    /// <summary>
    /// Os ícones do próprio Windows (Segoe MDL2 Assets). Para o que a fonte normal não tem: o ⏻, por exemplo,
    /// vira um retângulo vazio no Windows 10.
    /// </summary>
    public static readonly Font Symbols = new("Segoe MDL2 Assets", 11);

    /// <summary>O ⏻ dos ícones do Windows (usar com <see cref="Symbols"/>).</summary>
    public const string PowerGlyph = "";

    /// <summary>Ampliar, recarregar e o som dos ícones do Windows: do mesmo tamanho do ⏻ (os da fonte normal saem miúdos).</summary>
    public const string FocusGlyph = "";
    public const string ReloadGlyph = "";
    public const string SoundGlyph = "";
    public const string MutedGlyph = "";

    /// <summary>O carrinho dos ícones do Windows (a recompra).</summary>
    public const string CartGlyph = "";

    /// <summary>O ícone do deck (o mesmo do exe), na barra de título e na barra de tarefas.</summary>
    public static Icon AppIcon { get; } = new(typeof(Theme).Assembly.GetManifestResourceStream("deck.ico")!);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Barra de título escura do Windows 10/11 (chamar depois que a janela tem handle).</summary>
    public static void DarkTitleBar(Form form)
    {
        int on = 1;
        // 20 nas versões atuais; 19 nas primeiras do Windows 10 que tinham o modo escuro.
        if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
    }

    public static void Style(ToolStrip strip)
    {
        strip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
        strip.GripStyle = ToolStripGripStyle.Hidden;
        strip.BackColor = Bar;
        strip.ForeColor = Text;
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Itens sem cor própria (inclusive os dos menus) usam o texto claro do tema.
            e.TextColor = !e.Item.Enabled ? Muted
                : e.Item.ForeColor == SystemColors.ControlText ? Text
                : e.Item.ForeColor;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Bar;
        public override Color ToolStripGradientMiddle => Bar;
        public override Color ToolStripGradientEnd => Bar;
        public override Color ToolStripBorder => Bar;
        public override Color ToolStripDropDownBackground => Bar;
        public override Color StatusStripGradientBegin => Bar;
        public override Color StatusStripGradientEnd => Bar;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Bar;
        public override Color ImageMarginGradientMiddle => Bar;
        public override Color ImageMarginGradientEnd => Bar;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Bar;
        public override Color ButtonSelectedBorder => Hover;
        public override Color ButtonSelectedHighlight => Hover;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientMiddle => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonPressedBorder => Accent;
        public override Color ButtonPressedHighlight => AccentDim;
        public override Color ButtonPressedGradientBegin => AccentDim;
        public override Color ButtonPressedGradientMiddle => AccentDim;
        public override Color ButtonPressedGradientEnd => AccentDim;
        public override Color ButtonCheckedHighlight => AccentDim;
        public override Color ButtonCheckedHighlightBorder => Accent;
        public override Color ButtonCheckedGradientBegin => AccentDim;
        public override Color ButtonCheckedGradientMiddle => AccentDim;
        public override Color ButtonCheckedGradientEnd => AccentDim;
        public override Color CheckBackground => AccentDim;
        public override Color CheckSelectedBackground => Accent;
        public override Color CheckPressedBackground => Accent;
    }
}