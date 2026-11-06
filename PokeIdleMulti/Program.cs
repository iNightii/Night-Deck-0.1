using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        ToolStripManager.Renderer = Theme.Renderer;

        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "Falta o WebView2 Runtime da Microsoft (o motor de navegador que o deck usa).\n\n" +
                "Baixe em https://go.microsoft.com/fwlink/p/?LinkId=2124703 e abra o deck de novo.",
                "PokeIdle Deck", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Teste de consumo das lives: perfil de teste à parte, roda junto com o deck aberto.
        if (args.Contains("--consumo"))
        {
            using var measuring = new Mutex(true, "PokeIdleDeck-consumo", out bool alone);
            if (alone)
                Application.Run(new ConsumptionTest());
            return;
        }

        bool selfTest = args.Contains("--self-test");
        // Dois decks abertos com as mesmas contas ficam se derrubando ("Account opened elsewhere").
        using var single = new Mutex(true, selfTest ? "PokeIdleDeck-selftest" : "PokeIdleDeck", out bool first);
        if (!first)
        {
            MessageBox.Show("O PokeIdle Deck já está aberto.", "PokeIdle Deck",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new DeckForm(selfTest));
    }
}