namespace PokeIdleMulti;

/// <summary>
/// Barra de botões que aceita o primeiro clique com a janela sem foco. A ToolStrip normal usa esse
/// clique só para ativar a janela e o descarta, e aí o botão pede um segundo clique.
/// </summary>
internal sealed class ClickThroughToolStrip : ToolStrip
{
    public const int WmMouseActivate = 0x21;
    public const int MaActivate = 1;
    public const int MaActivateAndEat = 2;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmMouseActivate && m.Result == MaActivateAndEat)
            m.Result = MaActivate;
    }
}