using System.Drawing;
using System.Windows.Forms;

namespace PAXCookbook.WebView2;

// Owned-WinForms-Form close dialog per native-close-lifecycle-contract.md
// (Stage 3k two-button revision). The prompt is a modal Form owned by
// the WebView2 host window, with two ordinary push buttons:
//   1. "Close PAX Cookbook"  -> ClosePaxCookbook
//   2. "Cancel"              -> Cancel
// Default = Cancel. Esc, dialog X, and any unexpected exit path all
// resolve to Cancel via the form's initial Choice value. The form does
// not depend on comctl32 v6 TaskDialogIndirect; it uses only standard
// WinForms controls that are part of the same UI surface the WebView2
// host already runs on.
public sealed class Win32CloseDialogService : ICloseDialogService
{
    public CloseChoice Prompt(IntPtr ownerHwnd)
    {
        using var form = new ClosePromptForm();
        IWin32Window? owner = ownerHwnd != IntPtr.Zero ? new OwnerWindow(ownerHwnd) : null;
        if (owner is not null)
        {
            form.ShowDialog(owner);
        }
        else
        {
            form.ShowDialog();
        }
        return form.Choice;
    }

    private sealed class OwnerWindow : IWin32Window
    {
        public OwnerWindow(IntPtr handle) { Handle = handle; }
        public IntPtr Handle { get; }
    }

    private sealed class ClosePromptForm : Form
    {
        public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;

        public ClosePromptForm()
        {
            Text = "PAX Cookbook";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ControlBox = true;
            ClientSize = new Size(480, 200);
            AutoScaleMode = AutoScaleMode.Dpi;

            var lblHeader = new Label
            {
                Text = "Close PAX Cookbook?",
                Location = new Point(20, 18),
                AutoSize = true,
                Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            };

            var lblBody = new Label
            {
                Text = "PAX Cookbook will stop the local broker and close the app. " +
                       "If a bake is running, let it finish before closing unless you " +
                       "are sure you want to stop the app.",
                Location = new Point(20, 52),
                Size = new Size(440, 80),
                AutoSize = false,
            };

            var btnClose = new Button
            {
                Name = "btnClose",
                Text = "Close PAX Cookbook",
                Location = new Point(20, 150),
                Size = new Size(180, 32),
                DialogResult = DialogResult.OK,
            };
            btnClose.Click += (s, e) => { Choice = CloseChoice.ClosePaxCookbook; Close(); };

            var btnCancel = new Button
            {
                Name = "btnCancel",
                Text = "Cancel",
                Location = new Point(370, 150),
                Size = new Size(90, 32),
                DialogResult = DialogResult.Cancel,
            };
            btnCancel.Click += (s, e) => { Choice = CloseChoice.Cancel; Close(); };

            AcceptButton = btnClose;
            CancelButton = btnCancel;

            Controls.Add(lblHeader);
            Controls.Add(lblBody);
            Controls.Add(btnClose);
            Controls.Add(btnCancel);
        }
    }
}
