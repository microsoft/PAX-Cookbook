using System.Windows.Forms;
using PAXCookbook.Shared.Platform;

namespace PAXCookbook;

// Friendly, dedicated notice shown when the app is launched on a pre-Windows-10
// OS. Kept deliberately minimal: a single information dialog with the plain
// requirement and no technical explanation. Falls back to stderr if a message
// box cannot be shown (e.g. a non-interactive session).
internal static class UnsupportedOsNotice
{
    public static void Show()
    {
        try
        {
            MessageBox.Show(
                WindowsVersionGate.UnsupportedMessage,
                WindowsVersionGate.ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch
        {
            try { Console.Error.WriteLine(WindowsVersionGate.UnsupportedMessage); }
            catch { /* nothing further we can do */ }
        }
    }
}
