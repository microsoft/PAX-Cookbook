using System.Windows.Forms;
using PAXCookbook.Shared.Platform;

namespace PAXCookbookSetup;

// Friendly, dedicated notice shown when Setup is launched on a pre-Windows-10
// OS. Shows an information dialog for interactive (double-click / GUI) runs and
// always writes the same message to stderr so scripted/silent runs surface it
// too. No technical explanation -- just the plain requirement.
internal static class UnsupportedOsNotice
{
    public static void Show()
    {
        if (Environment.UserInteractive)
        {
            try
            {
                MessageBox.Show(
                    WindowsVersionGate.UnsupportedMessage,
                    WindowsVersionGate.ProductName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch { /* fall through to stderr */ }
        }

        try { Console.Error.WriteLine(WindowsVersionGate.UnsupportedMessage); }
        catch { /* nothing further we can do */ }
    }
}
