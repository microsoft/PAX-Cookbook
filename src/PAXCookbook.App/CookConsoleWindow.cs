using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PAXCookbook.App;

// Best-effort console-window hider for the single interactive cook case.
//
// A WebLogin cook must allocate a real console window so MSAL/WAM has a parent
// window handle (a headless child fails with "A window handle must be
// configured"). But the user must never see a terminal during a bake. The
// supervisor sets CreateNoWindow=false (so a console + HWND is allocated) and
// WindowStyle=Hidden to express the intent, but .NET only honors WindowStyle on
// the ShellExecuteEx path (UseShellExecute=true). The cook spawn keeps
// UseShellExecute=false (required for stdout redirection and child-only GRAPH_*
// injection), so the console is created VISIBLE despite WindowStyle=Hidden.
//
// This helper closes that gap by hiding the child's console window AFTER the
// spawn via ShowWindow(SW_HIDE). Hiding a window does not destroy it: the HWND
// remains valid, so MSAL/WAM can still parent its sign-in dialog to it while the
// console stays invisible. Acquiring a console application's window handle from
// the parent is inherently imperfect (the classic console window is owned by a
// conhost host process, not by pwsh), so this is a BEST-EFFORT cosmetic hide:
// it never throws, never blocks the supervisor's hot path or the stdout->cook.log
// tee, and is time-bounded. A fully reliable no-window guarantee is confirmed by
// the operator's real re-test.
//
// It touches NO PAX bytes, reads NO secret, writes NOTHING to cook.log, and is
// only ever invoked for the interactive (WebLogin) cook.
internal static class CookConsoleWindow
{
    private const int SW_HIDE = 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    // Single-shot hide of a window by handle. Never throws. Returns false for a
    // zero handle or any failure.
    internal static bool TryHideWindowSafe(nint hWnd)
    {
        if (hWnd == 0)
        {
            return false;
        }
        try
        {
            return ShowWindow(hWnd, SW_HIDE);
        }
        catch
        {
            return false;
        }
    }

    // Bounded poll: repeatedly ask handleProvider for a window handle; the first
    // non-zero handle is hidden. Returns true if a window was hidden, false if
    // the budget elapsed without one. Never throws and never blocks longer than
    // budgetMs (plus one stepMs slice).
    internal static bool PollAndHideBounded(Func<nint> handleProvider, int budgetMs, int stepMs = 50)
    {
        if (handleProvider is null || budgetMs <= 0)
        {
            return false;
        }
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < budgetMs)
            {
                nint handle = 0;
                try { handle = handleProvider(); }
                catch { handle = 0; }

                if (handle != 0)
                {
                    return TryHideWindowSafe(handle);
                }
                Thread.Sleep(stepMs);
            }
        }
        catch
        {
            // best-effort: a failed hide never affects the cook.
        }
        return false;
    }

    // Fire-and-forget hide of a child process's console window. Queued to the
    // thread pool so it never blocks the supervisor (which proceeds to record the
    // pid and hand off to the finalizer). Best-effort: a console application's
    // main window handle may resolve late or not at all, so the poll is bounded
    // and a miss is silently tolerated.
    internal static void QueueHideChildConsole(Process proc, int budgetMs = 2000)
    {
        if (proc is null)
        {
            return;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            PollAndHideBounded(
                () =>
                {
                    try
                    {
                        proc.Refresh();
                        return proc.HasExited ? 0 : proc.MainWindowHandle;
                    }
                    catch
                    {
                        return 0;
                    }
                },
                budgetMs);
        });
    }
}
