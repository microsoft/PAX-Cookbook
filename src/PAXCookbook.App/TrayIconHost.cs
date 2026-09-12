using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PAXCookbook.App;

// System-tray host for the headless broker daemon (V2).
//
// When PAX Cookbook is launched with --headless (e.g. by the HKCU Run key at
// login) it runs the in-process Kestrel broker with NO WebView2 window so
// scheduled bakes can fire in the background. This class gives that windowless
// process a system-tray presence so the user can open the Cookbook UI or stop
// the broker. It owns a hidden WinForms message-pump form (a tray NotifyIcon
// needs a Win32 message loop) and blocks in Application.Run until the user
// chooses Exit, at which point the caller stops the broker and releases the
// port file.
//
// Tray behavior (mirrors the window-mode tray in WebViewShell):
//   * Double-click / "Open PAX Cookbook" — launch a normal (windowed) instance
//     of this same executable with no arguments. That instance detects THIS
//     running broker (broker.port + health probe) and opens a window attached
//     to it instead of starting a second broker.
//   * "Status: ..." — a disabled, informational item naming the current state.
//   * "Exit PAX Cookbook" — invoke the supplied shutdown callback (stop the
//     broker, release the port file) and end the message loop.
//
// It never runs PAX, never reads a secret, and starts no process other than a
// fresh UI instance of its own signed executable.
internal static class TrayIconHost
{
    // The running daemon's shutdown coordinator, captured while Run() is active so
    // RequestExit() (called from a broker request thread) can trigger the SAME
    // idempotent teardown the "Exit PAX Cookbook" menu item performs. Set at the
    // start of Run() and cleared when its message loop ends; null means no daemon
    // tray loop is running.
    private static DaemonShutdownCoordinator? _coordinator;

    // Run the tray message loop until shutdown is requested (the "Exit" menu item,
    // or the /shutdown route via RequestExit). stopHost stops the in-process
    // Kestrel host; the shared coordinator runs it OFF the UI thread and force-
    // exits within a bounded interval, so shutdown can never deadlock the message
    // pump. statusText is a short informational line shown (disabled) in the menu.
    // trayExitAfterMs > 0 is a test-only seam: it drives the REAL tray "Exit"
    // action (coordinator.Shutdown on the UI thread, through the live message
    // loop) after that delay, so the automated lifecycle smoke can prove the
    // daemon exits and disposes its tray/menu without a human clicking. The
    // desktop launcher never passes it.
    internal static void Run(string iconPath, string statusText, Func<CancellationToken, Task> stopHost, int trayExitAfterMs = 0)
    {
        ApplicationConfiguration.Initialize();

        // A zero-size, never-shown form purely to host the NotifyIcon and pump
        // Win32 messages. It is never made visible and stays off the taskbar.
        using var pumpForm = new Form
        {
            ShowInTaskbar = false,
            WindowState = FormWindowState.Minimized,
            FormBorderStyle = FormBorderStyle.None,
            Opacity = 0,
            Size = new Size(0, 0),
        };
        pumpForm.Load += (_, _) =>
        {
            pumpForm.Visible = false;
            pumpForm.Hide();
        };

        Icon? trayIcon = null;
        try
        {
            if (File.Exists(iconPath))
            {
                Size traySize = SystemInformation.SmallIconSize;
                trayIcon = new Icon(iconPath, traySize.Width, traySize.Height);
            }
        }
        catch
        {
            trayIcon = null;
        }

        void OpenUi()
        {
            // Launch a windowed instance with no app args; it detects this broker
            // and attaches a window to it. WDAC-safe: run the Microsoft-signed
            // dotnet.exe host with our DLL. (Environment.ProcessPath is dotnet.exe
            // when we were launched that way, and relaunching it with no DLL would
            // not start the app.)
            try
            {
                var dotnet = DotNetLaunch.DotNetExePath();
                var dll = DotNetLaunch.OwnDllPath();
                if (File.Exists(dll))
                {
                    // CreateNoWindow suppresses dotnet.exe's console window.
                    var psi = new ProcessStartInfo(dotnet)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add(dll);
                    Process.Start(psi);
                }
            }
            catch
            {
                // Best-effort: a failed launch leaves the click a no-op.
            }
        }

        DaemonShutdownCoordinator? coordinator = null;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PAX Cookbook", null, (_, _) => OpenUi());
        ToolStripItem status = menu.Items.Add(statusText);
        status.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit PAX Cookbook", null, (_, _) => coordinator?.Shutdown());

        var tray = new NotifyIcon
        {
            Text = "PAX Cookbook (running in the background)",
            Icon = trayIcon ?? SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => OpenUi();

        // Fast, idempotent tray-UI teardown: hide + dispose the NotifyIcon and
        // ContextMenuStrip (so the menu disappears immediately) and end the message
        // loop. No host-stop work runs here, so the UI thread never blocks.
        void TeardownTrayUi()
        {
            try { tray.Visible = false; tray.Dispose(); } catch { /* idempotent */ }
            try { menu.Dispose(); } catch { /* idempotent */ }
            try { pumpForm.Close(); } catch { /* the form may already be closing */ }
        }

        // Marshal an action onto the tray UI thread (or run inline when already on
        // it), so the teardown always runs on the message-pump thread.
        void PostToUi(Action action)
        {
            try
            {
                if (pumpForm.IsHandleCreated && !pumpForm.IsDisposed && pumpForm.InvokeRequired)
                {
                    pumpForm.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch
            {
                try { action(); } catch { /* best-effort; force-exit still terminates */ }
            }
        }

        // Publish the shared coordinator so the broker's /api/v1/shutdown endpoint
        // (running on a request thread) ends the daemon the same idempotent way the
        // tray Exit item does. Cleared when the loop ends.
        coordinator = new DaemonShutdownCoordinator(
            teardownTrayUi: TeardownTrayUi,
            postToUi: PostToUi,
            stopHost: stopHost,
            forceExit: () => Environment.Exit(0));
        _coordinator = coordinator;

        // Test-only: drive the REAL tray Exit path on the UI thread after a delay.
        System.Windows.Forms.Timer? exitSeamTimer = null;
        if (trayExitAfterMs > 0)
        {
            exitSeamTimer = new System.Windows.Forms.Timer { Interval = trayExitAfterMs };
            exitSeamTimer.Tick += (_, _) =>
            {
                exitSeamTimer.Stop();
                coordinator?.Shutdown();
            };
            exitSeamTimer.Start();
        }

        try
        {
            Application.Run(pumpForm);
        }
        finally
        {
            _coordinator = null;
            try { exitSeamTimer?.Stop(); exitSeamTimer?.Dispose(); } catch { /* idempotent */ }
            try
            {
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
            }
            catch
            {
                // Non-fatal: process is exiting anyway.
            }
            trayIcon?.Dispose();
        }
    }

    // Request a graceful daemon shutdown from outside the tray UI thread (the
    // broker's /api/v1/shutdown handler, used by the attached-window close-both
    // flow). Delegates to the shared idempotent coordinator, which disposes the
    // tray UI on the message-pump thread and stops Kestrel off it. Returns true
    // when a daemon tray loop is running and this call initiated shutdown; false
    // when no tray loop exists (a combined window or the --no-window smoke host),
    // so the caller can fall back to stopping the host directly. A duplicate
    // request after shutdown already began also returns false and is a safe no-op.
    internal static bool RequestExit()
    {
        return _coordinator?.Shutdown() ?? false;
    }
}
