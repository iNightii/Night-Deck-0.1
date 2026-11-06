namespace PokeIdleMulti;

/// <summary>Caixinha para pedir um texto (nome da conta, link da extensão).</summary>
internal static class Prompt
{
    public static string? Ask(IWin32Window owner, string title, string label, string value = "")
    {
        using var form = new Form
        {
            Text = title,
            Icon = Theme.AppIcon,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(460, 120),
            ShowInTaskbar = false,
        };
        var text = new Label { Text = label, AutoSize = true, Location = new Point(12, 12), MaximumSize = new Size(436, 0) };
        var box = new TextBox { Text = value, Location = new Point(12, 48), Width = 436 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(292, 82), Width = 75 };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(373, 82), Width = 75 };
        form.Controls.AddRange(new Control[] { text, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}