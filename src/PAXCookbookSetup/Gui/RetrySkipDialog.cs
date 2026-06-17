using System.Drawing;
using System.Windows.Forms;

namespace PAXCookbookSetup.Gui;

// A modal prompt with exactly two buttons — "Retry" and "Exit Setup" — shown
// when a required prerequisite install fails or is declined. All prerequisites
// are mandatory; the user must either retry or exit Setup entirely.
internal static class RetryExitDialog
{
    public static RetryExitDecision Show(IWin32Window owner, string title, string message)
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
            Location = new Point(212, 126)
        };
        var exit = new Button
        {
            Text = "Exit Setup",
            DialogResult = DialogResult.Cancel,
            Size = new Size(110, 30),
            Location = new Point(316, 126)
        };

        form.Controls.Add(label);
        form.Controls.Add(retry);
        form.Controls.Add(exit);
        form.AcceptButton = retry;
        form.CancelButton = exit;

        var result = form.ShowDialog(owner);
        return result == DialogResult.Retry ? RetryExitDecision.Retry : RetryExitDecision.ExitSetup;
    }
}
