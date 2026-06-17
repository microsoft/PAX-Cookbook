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
    // The running daemon's hidden pump form and shutdown callback, captured while
    // Run() is active so RequestExit() (called from a broker request thread) can
    // marshal the SAME teardown the "Exit PAX Cookbook" menu item performs.
    // Both are UI-thread state set at the start of Run() and cleared when its
    // message loop ends; a null pump form means no daemon tray loop is running.
    private static Form? _pumpForm;
    private static Action? _onExit;

    // Run the tray message loop until the user chooses Exit. onExit is invoked
    // once, on the UI thread, when Exit is chosen (before the loop ends), so the
    // caller can stop the broker host and release the port file. statusText is a
    // short informational line shown (disabled) in the menu.
    internal static void Run(string iconPath, string statusText, Action onExit)
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

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PAX Cookbook", null, (_, _) => OpenUi());
        ToolStripItem status = menu.Items.Add(statusText);
        status.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit PAX Cookbook", null, (_, _) =>
        {
            try { onExit(); }
            catch { /* Non-fatal: shutdown is best-effort; the loop still ends. */ }
            try { pumpForm.Close(); }
            catch { /* Non-fatal: the form may already be closing. */ }
        });

        var tray = new NotifyIcon
        {
            Text = "PAX Cookbook (running in the background)",
            Icon = trayIcon ?? SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => OpenUi();

        // Publish this loop's pump form + shutdown callback so the broker's
        // /api/v1/shutdown endpoint (running on a request thread) can end the
        // daemon the same way the tray Exit item does. Cleared when the loop ends.
        _pumpForm = pumpForm;
        _onExit = onExit;

        try
        {
            Application.Run(pumpForm);
        }
        finally
        {
            _pumpForm = null;
            _onExit = null;
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
    // broker's /api/v1/shutdown handler). Returns true when a tray loop is
    // running and the exit was marshaled to it; false when no daemon tray loop
    // exists (a combined window or the --no-window smoke host), so the caller
    // can fall back to stopping the host directly. Best-effort: any failure
    // simply returns false.
    internal static bool RequestExit()
    {
        Form? form = _pumpForm;
        Action? onExit = _onExit;
        if (form is null || form.IsDisposed || !form.IsHandleCreated)
        {
            return false;
        }
        try
        {
            // Marshal to the tray's UI thread and run the SAME teardown as the
            // "Exit PAX Cookbook" menu item: invoke the shutdown callback (stop
            // the broker + release the port file) then close the pump form so
            // Application.Run ends and the daemon process exits.
            form.BeginInvoke(new Action(() =>
            {
                try { onExit?.Invoke(); }
                catch { /* Non-fatal: shutdown is best-effort; the loop still ends. */ }
                try { form.Close(); }
                catch { /* Non-fatal: the form may already be closing. */ }
            }));
            return true;
        }
        catch
        {
            // Non-fatal: if marshaling fails the caller stops the host directly.
            return false;
        }
    }
}
