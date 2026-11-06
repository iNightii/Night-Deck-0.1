using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PokeIdleMulti;

/// <summary>
/// Janela solta com uma página no mesmo perfil de uma conta: popups que o site abre (login com
/// Discord/Google, links) e os popups das extensões.
/// </summary>
internal sealed class PopupWindow : Form
{
    private readonly bool _closeOnDeactivate;
    private bool _closing;

    private PopupWindow(Size size, bool closeOnDeactivate)
    {
        _closeOnDeactivate = closeOnDeactivate;
        Text = "PokeIdle Deck";
        Icon = Theme.AppIcon;
        BackColor = Theme.Back;
        StartPosition = FormStartPosition.Manual;
        ClientSize = size;
        if (closeOnDeactivate)
        {
            // Como o popup de extensão do Chrome: some quando você clica fora.
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
        }
        Controls.Add(WebView);
    }

    public WebView2 WebView { get; } = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Back };

    public static async Task<PopupWindow> CreateAsync(
        CoreWebView2Environment env, string profile, Size size, bool closeOnDeactivate)
    {
        var popup = new PopupWindow(size, closeOnDeactivate);
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = profile;
        _ = popup.WebView.Handle;
        await popup.WebView.EnsureCoreWebView2Async(env, options);

        var core = popup.WebView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.DocumentTitleChanged += (_, _) => popup.Text = core.DocumentTitle;
        core.WindowCloseRequested += (_, _) => popup.Close();
        core.NewWindowRequested += (_, e) => HandleNewWindow(env, profile, popup, e);
        return popup;
    }

    /// <summary>
    /// Abre numa <see cref="PopupWindow"/> a janela que uma página pediu (window.open), no mesmo perfil,
    /// para o login por popup (Discord, Google, Twitch) devolver a sessão para quem abriu.
    /// </summary>
    public static async void HandleNewWindow(
        CoreWebView2Environment env, string profile, Form? owner, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var features = e.WindowFeatures;
            var size = features.HasSize ? new Size((int)features.Width, (int)features.Height) : new Size(1000, 720);
            var popup = await CreateAsync(env, profile, size, closeOnDeactivate: false);
            var area = owner is null ? Screen.PrimaryScreen!.WorkingArea : Screen.FromControl(owner).WorkingArea;
            popup.Location = new Point(
                area.X + Math.Max(0, (area.Width - popup.Width) / 2),
                area.Y + Math.Max(0, (area.Height - popup.Height) / 2));
            e.NewWindow = popup.WebView.CoreWebView2;
            e.Handled = true;
            popup.Show(owner);
        }
        catch
        {
            // Sem a janela própria, o WebView2 abre a dele.
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Encosta a janela logo abaixo de <paramref name="anchor"/> (canto direito), sem sair da tela.</summary>
    public void PlaceBelow(Point anchor)
    {
        var area = Screen.FromPoint(anchor).WorkingArea;
        Location = new Point(
            Math.Clamp(anchor.X - Width, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(anchor.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_closeOnDeactivate && !_closing)
            BeginInvoke(Close);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        WebView.Dispose();
        base.OnFormClosed(e);
    }
}