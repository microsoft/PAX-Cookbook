using System;
using System.Threading;
using System.Threading.Tasks;

namespace PAXCookbook.App;

// Close-app lifecycle decision for the WebView2 shell (Track 1 / T1-S2B repair).
//
// The 3-choice close modal's "Exit" posts cookbook:close-app to the native shell.
// What that must do depends on who owns the broker:
//   * Standalone window (window started its own in-process broker): closing the
//     window tears the broker down with the process, so the shell just closes.
//   * Attached window (a SEPARATE headless daemon owns the broker): closing the
//     window alone would ORPHAN the daemon — contradicting the "Exit stops the
//     background broker" contract. The shell must ask the daemon to stop over the
//     authenticated loopback shutdown route and AWAIT the result BEFORE closing.
//     A stop that is refused (e.g. 423 while Locked) or fails must surface a
//     truthful bounded failure instead of silently closing and claiming both are
//     gone.
//
// This type is the deterministic decision core, isolated from WinForms/HTTP so it
// can be unit-tested. It performs no window teardown and no HTTP itself: the
// daemon-shutdown attempt is an injected delegate that returns true only when the
// daemon is confirmed stopped (HTTP 200 accepted, or already not serving).
internal enum CloseAppMode
{
    // The window owns its in-process broker (no separate daemon).
    Standalone = 0,

    // The window is attached to a separate headless broker daemon.
    Attached = 1,
}

internal enum CloseAppAction
{
    // Proceed to tear the window down (Standalone, or Attached after the daemon
    // was confirmed stopped).
    CloseWindow = 0,

    // Attached close-both could not stop the daemon: keep the window open and
    // surface a truthful bounded failure rather than orphaning the daemon.
    SurfaceDaemonFailure = 1,
}

internal sealed class AttachedWindowCloseCoordinator
{
    private readonly CloseAppMode _mode;
    private readonly Func<CancellationToken, Task<bool>>? _requestDaemonShutdown;

    // requestDaemonShutdown MUST be non-null for Attached mode and returns true
    // only when the daemon is confirmed stopped. It is ignored in Standalone mode.
    internal AttachedWindowCloseCoordinator(
        CloseAppMode mode,
        Func<CancellationToken, Task<bool>>? requestDaemonShutdown)
    {
        _mode = mode;
        _requestDaemonShutdown = requestDaemonShutdown;
    }

    // Decide what the shell should do for a cookbook:close-app request. In
    // Attached mode the daemon shutdown is awaited so the request is guaranteed
    // to have been delivered (and resolved) before the window is torn down.
    internal async Task<CloseAppAction> DecideCloseAppAsync(CancellationToken cancellationToken)
    {
        if (_mode == CloseAppMode.Standalone || _requestDaemonShutdown is null)
        {
            // Window owns the in-process broker: closing the window stops it. No
            // separate daemon to contact.
            return CloseAppAction.CloseWindow;
        }

        bool stopped;
        try
        {
            stopped = await _requestDaemonShutdown(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Any failure to obtain a confirmed stop is treated as a truthful
            // failure — never a silent "both closed".
            stopped = false;
        }

        return stopped ? CloseAppAction.CloseWindow : CloseAppAction.SurfaceDaemonFailure;
    }
}
