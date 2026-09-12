#if EXPERIMENTAL_WAM
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PAXCookbook.App;

// Minimal, purpose-built owner window for the one-shot provider native test.
//
// It exists only to give the native-WAM broker a real, non-zero parent HWND on
// the STA UI thread. It is NOT the product shell: no WebView2, no broker, no
// tray, no menu. It shows a neutral "signing in" surface, runs exactly one
// acquisition, and is disposed immediately afterward. Compiled only under the
// EXPERIMENTAL_WAM gate.
internal sealed class ProviderNativeTestWindow : Form
{
    private WamInteractiveResult _result =
        WamInteractiveResult.Failure(WamAcquireStatus.BrokerFailure);

    internal ProviderNativeTestWindow()
    {
        Text = "PAX Cookbook";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 120);
        BackColor = Color.White;

        var label = new Label
        {
            Text = "Signing in with your work account\u2026",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10F),
        };
        Controls.Add(label);

        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Icon = Icon.ExtractAssociatedIcon(exe);
            }
        }
        catch
        {
            // Best-effort; a missing icon never blocks the test.
        }
    }

    // Forces native handle creation and returns the real HWND before the message
    // loop runs, so the acquisition can parent to it.
    internal IntPtr EnsureHandle() => Handle;

    // Runs the STA message loop, performs exactly one acquisition on the UI
    // thread, then closes and returns the bounded result. Never throws.
    internal WamInteractiveResult RunAcquisition(Func<Task<WamInteractiveResult>> acquire)
    {
        Shown += async (_, _) =>
        {
            try
            {
                _result = await acquire();
            }
            catch (OperationCanceledException)
            {
                _result = WamInteractiveResult.Failure(WamAcquireStatus.UserCancelled);
            }
            catch
            {
                _result = WamInteractiveResult.Failure(WamAcquireStatus.BrokerFailure);
            }
            finally
            {
                try { Close(); } catch { /* already closing */ }
            }
        };

        Application.Run(this);
        return _result;
    }
}
#endif
