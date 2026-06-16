using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using PAXCookbook.Logging;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace PAXCookbook.WebView2;

// Real WinForms + WebView2 host.
//
// Per webview2-host-contract + native-close-lifecycle-contract:
//   - title "PAX Cookbook"; window opens maximized
//   - app-owned CoreWebView2Environment using the explicit user-data
//     folder (no Edge profile sharing)
//   - NavigationStarting enforces the localhost+selected-port allowlist
//   - NewWindowRequested cancelled (keeps user inside host)
//   - FormClosing triggers the three-button close dialog through the
//     coordinator unless a silent close was requested
//   - exposes IUiWindowController to the supplied sink for IPC/stop
//     orchestration from outside the UI thread
public sealed class WebView2WinFormsHost : IUiHost
{
    public UiHostResult Launch(UiHostLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                ApplicationConfiguration.Initialize();
                Directory.CreateDirectory(request.UserDataFolder);
                using var form = new HostForm(request);
                request.ControllerSink?.Set(new WinFormsController(form));
                Application.Run(form);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return failure is null
            ? new UiHostResult(UiHostOutcome.Launched, null)
            : new UiHostResult(UiHostOutcome.LaunchFailed, failure.Message);
    }

    internal sealed class HostForm : Form
    {
        private readonly UiHostLaunchRequest _req;
        private readonly NavigationAllowlist _allow;
        private readonly WebView2Control _web;
        private bool _silentClose;
        private bool _dialogOpen;
        // Stage 5 AB: set once the user has picked ClosePaxCookbook
        // and the async shutdown has started. Any further FormClosing
        // event (rapid X-clicks, Alt+F4) is cancelled until the
        // shutdown task calls Close() programmatically with the
        // silent-close bypass.
        private bool _shutdownInProgress;
        private int  _webDisposed;

        public HostForm(UiHostLaunchRequest req)
        {
            _req = req;
            _allow = new NavigationAllowlist(req.BrokerPort);
            Text = req.WindowTitle;
            WindowState = FormWindowState.Maximized;
            TryApplyEmbeddedIcon();
            _web = new WebView2Control { Dock = DockStyle.Fill };
            Controls.Add(_web);
            Load += async (_, _) => await InitializeAsync();
            FormClosing += OnFormClosing;
        }

        // Loads the multi-resolution PAX Cookbook icon embedded in the
        // assembly and applies it to the window/taskbar. Best-effort:
        // if the resource is missing, the form falls back to the default
        // WinForms icon and the host still runs.
        private void TryApplyEmbeddedIcon()
        {
            try
            {
                var asm = typeof(HostForm).Assembly;
                using var s = asm.GetManifestResourceStream("PAXCookbook.Resources.PAXCookbook.ico");
                if (s is not null)
                {
                    Icon = new System.Drawing.Icon(s);
                }
            }
            catch
            {
                // ignored — icon is non-essential to launch.
            }
        }

        private async Task InitializeAsync()
        {
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _req.UserDataFolder);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                var d = _allow.Evaluate(e.Uri);
                if (d.Decision == NavigationDecision.Block) e.Cancel = true;
            };
            _web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; };
            _web.CoreWebView2.Navigate(_req.BrokerUrl);
        }

        public void RequestSilentClose()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RequestSilentClose)); return; }
            _silentClose = true;
            Close();
        }

        public void FocusToFront()
        {
            if (InvokeRequired) { BeginInvoke(new Action(FocusToFront)); return; }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
            Activate();
            BringToFront();
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (_silentClose || _req.CloseCoordinator is null) return;
            // Stage 5 AB: if we already started the async shutdown,
            // cancel any further close request so the form stays
            // alive until the shutdown task triggers a silent close.
            // Without this guard, a second X-click or an Alt+F4 would
            // re-enter OnFormClosing while the broker stop task is
            // still running and either re-prompt the user or trigger
            // a partial dispose mid-shutdown.
            if (_shutdownInProgress) { e.Cancel = true; return; }
            if (_dialogOpen) { e.Cancel = true; return; }
            _dialogOpen = true;
            try
            {
                var trigger = e.CloseReason switch
                {
                    CloseReason.UserClosing => CloseTrigger.TitleBarX,
                    CloseReason.WindowsShutDown => CloseTrigger.WmClose,
                    CloseReason.TaskManagerClosing => CloseTrigger.WmClose,
                    CloseReason.ApplicationExitCall => CloseTrigger.Stop,
                    _ => CloseTrigger.TitleBarX
                };
                PromptResult prompt;
                try
                {
                    prompt = _req.CloseCoordinator.PromptForChoice(trigger, Handle);
                }
                catch (Exception ex)
                {
                    // PromptForChoice already wraps the dialog call in
                    // a catch that returns Cancel on any exception, so
                    // anything reaching here is an unexpected fault in
                    // the coordinator itself. Keep the window open and
                    // log loud so the regression is visible in app log.
                    e.Cancel = true;
                    _req.CloseCoordinator.Log?.Write("App", "close-prompt-handler-exception", "error", new Dictionary<string, object?>
                    {
                        ["trigger"] = trigger.ToString(),
                        ["exception"] = ex.GetType().FullName,
                        ["message"] = ex.Message,
                        ["stack"] = ex.ToString()
                    });
                    return;
                }
                if (prompt.Choice == CloseChoice.Cancel)
                {
                    // Cancel path: keep window open, broker untouched.
                    // No async shutdown started.
                    e.Cancel = true;
                    return;
                }
                // prompt.Choice == ClosePaxCookbook: hand off to the
                // async shutdown so FormClosing returns immediately.
                // The shutdown task disposes WebView2 to drop its
                // loopback HTTP/HTTP2 sockets (breaking the Kestrel
                // drain deadlock), drives the broker stop off the UI
                // thread, then calls Close() programmatically with
                // the silent-close bypass.
                e.Cancel = true;
                _shutdownInProgress = true;
                BeginAsyncShutdown(_req.CloseCoordinator);
            }
            finally
            {
                _dialogOpen = false;
            }
        }

        // Stage 5 AB: bounded, deterministic, async close orchestration.
        // Sequence:
        //   1. Hide the maximized chrome immediately so the user sees
        //      visible progress and not a frozen window.
        //   2. Dispose the WebView2 control ON THE UI THREAD. This is
        //      the deadlock breaker: WebView2 keeps loopback HTTP/HTTP2
        //      sockets open to the in-process Kestrel broker, and
        //      Kestrel.StopAsync waits for those sockets to drain. If
        //      we left them open the broker stop would never complete
        //      and the form would never close.
        //   3. Run RunBrokerStop on Task.Run so the UI thread is free
        //      to service whatever message-loop work the dispose / drain
        //      itself requires.
        //   4. Outer watchdog: wait at most BrokerStopTimeout + 5s for
        //      the stop task to complete. Whether it completes or the
        //      watchdog fires, marshal a Close() back to the UI thread
        //      with _silentClose so the form actually closes. The
        //      process is GUARANTEED to be able to exit within the
        //      bounded window.
        private void BeginAsyncShutdown(CloseGestureCoordinator coord)
        {
            try { Hide(); } catch { /* not critical */ }
            DisposeWebView2Quietly();

            var hardTimeout = coord.BrokerStopTimeout + TimeSpan.FromSeconds(5);

            var stopTask = Task.Run(() =>
            {
                try
                {
                    coord.RunBrokerStop();
                }
                catch (Exception ex)
                {
                    coord.Log?.Write("App", "broker-stop-failed", "error", new Dictionary<string, object?>
                    {
                        ["phase"]     = "async-shutdown",
                        ["exception"] = ex.GetType().FullName,
                        ["message"]   = ex.Message
                    });
                }
            });

            Task.Run(async () =>
            {
                var watchdog = Task.Delay(hardTimeout);
                var winner   = await Task.WhenAny(stopTask, watchdog).ConfigureAwait(false);
                if (winner == watchdog)
                {
                    coord.Log?.Write("App", "shutdown-watchdog-fired", "warn", new Dictionary<string, object?>
                    {
                        ["hardTimeoutMs"] = (int)hardTimeout.TotalMilliseconds
                    });
                }
                else
                {
                    coord.Log?.Write("App", "shutdown-complete", "info", new Dictionary<string, object?>
                    {
                        ["hardTimeoutMs"] = (int)hardTimeout.TotalMilliseconds
                    });
                }
                CloseProgrammaticallyFromBackground(coord);
            });
        }

        private void DisposeWebView2Quietly()
        {
            // Idempotency: WinForms will also try to dispose the
            // control when the form disposes. Once we have disposed
            // explicitly we remove it from the Controls collection so
            // form.Dispose does not double-dispose.
            if (Interlocked.Exchange(ref _webDisposed, 1) != 0) return;
            try
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() =>
                    {
                        try { Controls.Remove(_web); } catch { }
                        try { _web.Dispose(); }       catch { }
                    }));
                }
                else
                {
                    try { Controls.Remove(_web); } catch { }
                    try { _web.Dispose(); }       catch { }
                }
            }
            catch (Exception ex)
            {
                _req.CloseCoordinator?.Log?.Write("App", "webview2-dispose-failed", "warn", new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().FullName,
                    ["message"]   = ex.Message
                });
            }
        }

        private void CloseProgrammaticallyFromBackground(CloseGestureCoordinator coord)
        {
            try
            {
                if (IsDisposed) return;
                if (!IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    _silentClose = true;
                    try { Close(); } catch { /* form may be torn down */ }
                }));
            }
            catch (Exception ex)
            {
                coord.Log?.Write("App", "shutdown-close-failed", "error", new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().FullName,
                    ["message"]   = ex.Message
                });
            }
        }
    }

    internal sealed class WinFormsController : IUiWindowController
    {
        private readonly HostForm _form;
        public WinFormsController(HostForm form) => _form = form;
        public void FocusWindow() => _form.FocusToFront();
        public void CloseWindowSilently() => _form.RequestSilentClose();
    }
}
