using System;
using System.Threading;
using System.Threading.Tasks;

namespace PAXCookbook.App;

// Shared idempotent daemon-shutdown coordinator (T1-S2B TASK B live-lifecycle
// repair).
//
// The headless broker daemon can be asked to stop from several places — the
// tray "Exit PAX Cookbook" item, the authenticated /api/v1/shutdown route (used
// by the attached-window "close both" flow), and process teardown. The original
// code ran the Kestrel stop (app.StopAsync().GetAwaiter().GetResult()) SYNCHRONOUSLY
// on the WinForms UI thread inside the tray message loop. StopAsync's completion
// continuation needs that same UI thread, so it deadlocked: the tray context
// menu never repainted/disposed (it stayed painted on the desktop) and the
// process never exited.
//
// This coordinator owns shutdown regardless of trigger and is deadlock-free:
//   1. It disposes the tray icon + context menu and ends the message loop on the
//      tray UI thread (immediate, visible dismissal — NO blocking work there).
//   2. It stops Kestrel OFF the UI thread and then force-exits within a bounded
//      interval, so a slow or stuck host stop can never hold the process (and the
//      exe write-lock) open.
// The first call performs shutdown exactly once; later calls are safe no-ops, so
// overlapping triggers (tray Exit + a /shutdown request) cannot double-stop or
// race each other.
internal sealed class DaemonShutdownCoordinator
{
    private int _started;
    private readonly Action _teardownTrayUi;
    private readonly Action<Action> _postToUi;
    private readonly Func<CancellationToken, Task> _stopHost;
    private readonly Action _forceExit;
    private readonly TimeSpan _budget;

    // teardownTrayUi : dispose the NotifyIcon + ContextMenuStrip and end the tray
    //                  message loop (runs on the UI thread via postToUi).
    // postToUi       : marshal an action onto the tray UI thread (BeginInvoke), or
    //                  run it inline when already on that thread.
    // stopHost       : stop the in-process Kestrel host (app.StopAsync). Invoked
    //                  OFF the UI thread; a hung stop is bounded by 'budget'.
    // forceExit      : deterministic process exit (Environment.Exit) — the final
    //                  watchdog after graceful cleanup or the budget elapses.
    internal DaemonShutdownCoordinator(
        Action teardownTrayUi,
        Action<Action> postToUi,
        Func<CancellationToken, Task> stopHost,
        Action forceExit,
        TimeSpan? budget = null)
    {
        _teardownTrayUi = teardownTrayUi;
        _postToUi = postToUi;
        _stopHost = stopHost;
        _forceExit = forceExit;
        _budget = budget ?? TimeSpan.FromSeconds(5);
    }

    // Atomically accept the FIRST shutdown request; ignore duplicates. Returns
    // true only for the call that actually initiated shutdown. Never blocks: the
    // tray teardown is marshaled and the host stop runs on a background task, so
    // this cannot deadlock on the UI thread or wait on itself.
    internal bool Shutdown()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return false;
        }

        // 1. Immediate, visible dismissal on the tray UI thread: dispose the tray
        //    icon + menu and end the message loop. No host-stop work runs here.
        try
        {
            _postToUi(_teardownTrayUi);
        }
        catch
        {
            // Non-fatal: the force-exit below still guarantees termination.
        }

        // 2. Off-thread host stop + bounded, deterministic force-exit. Task.WhenAny
        //    against the budget guarantees the exit fires even if the host stop
        //    ignores cancellation and hangs.
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(_budget);
                await Task.WhenAny(_stopHost(cts.Token), Task.Delay(_budget)).ConfigureAwait(false);
            }
            catch
            {
                // Bounded: a failed or hung stop must never hold the process open.
            }

            try
            {
                _forceExit();
            }
            catch
            {
                // Nothing more can be done; the OS reclaims the process on exit.
            }
        });

        return true;
    }
}
