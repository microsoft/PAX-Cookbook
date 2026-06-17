using System.Drawing;
using System.Windows.Forms;

namespace PAXCookbookSetup.Gui;

// A tiny modal prompt with exactly two buttons — "Retry" and "Skip" — shown
// when a prerequisite install fails or is declined (the wizard's Progress
// screen offers Retry/Skip per the spec). WinForms' MessageBox has no
// Retry/Skip button pair, so this minimal dialog provides the exact labels.
internal static class RetrySkipDialog
{
    public static RetrySkipDecision Show(IWin32Window owner, string title, string message)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(440, 170),
            Font = new Font("Segoe UI", 9F)
        };

        var label = new Label
        {
            Text = message,
            Location = new Point(20, 20),
            Size = new Size(400, 96),
            AutoSize = false
        };

        var retry = new Button
        {
            Text = "Retry",
            DialogResult = DialogResult.Retry,
            Size = new Size(96, 30),
            Location = new Point(224, 126)
        };
        var skip = new Button
        {
            Text = "Skip",
            DialogResult = DialogResult.Ignore,
            Size = new Size(96, 30),
            Location = new Point(328, 126)
        };

        form.Controls.Add(label);
        form.Controls.Add(retry);
        form.Controls.Add(skip);
        form.AcceptButton = retry;
        form.CancelButton = skip;

        var result = form.ShowDialog(owner);
        return result == DialogResult.Retry ? RetrySkipDecision.Retry : RetrySkipDecision.Skip;
    }
}
