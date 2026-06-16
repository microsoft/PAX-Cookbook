using System.Collections.Concurrent;
using System.Diagnostics;

namespace PAXCookbook.App;

// X6 — Stop / Cancel process-lifecycle control. The single home for the
// in-process cancellation registry and the kill mechanism behind
// POST /api/v1/cooks/{id}/stop and POST /api/v1/cooks/{id}/kill (both routes map
// to the same lifecycle handler — there is no separate, divergent kill path).
//
// Identity safety (doctrine, see RecipeReadModel.CookRead.ReconcileOneStaleCook):
// a running cook may only be killed through the LIVE System.Diagnostics.Process
// held by THIS broker's supervisor — NEVER by a stored pid. A pid recorded in a
// cook row is untrustworthy after a restart (PID reuse), so it is never used to
// target a kill. The registry is populated the instant a supervised child is
// spawned and cleared when its supervisor finalizes, so a hit here is, by
// construction, the exact live child of a cook this broker is actively
// supervising. A miss means "not supervised here" and NOTHING is killed.
//
// This type never reads the workspace database, never touches the managed PAX
// engine bytes, never reads a credential, and never logs. It only holds live
// process handles plus a per-cook cancel flag, and kills a process tree on
// request. It spawns nothing: cancellation is lifecycle control, not a second
// way to run PAX (constraints 8 and 9).
internal static class CookCancellation
{
    // The disposition of a cancel request.
    internal enum CancelOutcome
    {
        // No live handle for this cook in this broker — nothing was killed.
        NotSupervised,

        // A live handle was found: the cancel flag was set and a tree-kill was
        // attempted. KillThrew on the result records whether the kill call
        // itself threw (which happens benignly when the child had already
        // exited); either way the supervisor still finalizes the cook.
        Requested,
    }

    // Result of RequestCancel: the outcome plus, for diagnostics/tests, whether
    // the kill call threw. KillThrew is never surfaced in a route body.
    internal readonly record struct CancelResult(CancelOutcome Outcome, bool KillThrew);

    private sealed class CookControlHandle
    {
        internal CookControlHandle(Process process, DateTime startedUtc)
        {
            Process = process;
            StartedUtc = startedUtc;
        }

        // The LIVE child held by the supervisor — the only trustworthy kill
        // target for this cook.
        internal Process Process { get; }

        internal DateTime StartedUtc { get; }

        // Set true the moment a user-initiated cancel is requested. The
        // supervisor reads this (volatile) flag after WaitForExit to record a
        // "canceled" terminal state instead of "errored".
        internal volatile bool CancelRequested;
    }

    private static readonly ConcurrentDictionary<string, CookControlHandle> Handles =
        new(StringComparer.Ordinal);

    // Register the live supervised child for a cook. Called by the supervisor
    // immediately after a successful spawn, BEFORE the supervisor thread starts,
    // so a near-instant Stop can find it. Replaces any stale handle for the id.
    internal static void Register(string cookId, Process process, DateTime startedUtc)
    {
        if (string.IsNullOrEmpty(cookId) || process is null)
        {
            return;
        }
        Handles[cookId] = new CookControlHandle(process, startedUtc);
    }

    // Remove the handle for a cook. Called by the supervisor in its finally,
    // after the live Process has been disposed. Safe to call when absent.
    internal static void Unregister(string cookId)
    {
        if (string.IsNullOrEmpty(cookId))
        {
            return;
        }
        Handles.TryRemove(cookId, out _);
    }

    // Whether a user-initiated cancel was requested for a cook. False when the
    // cook is not (or no longer) registered.
    internal static bool IsCancelRequested(string cookId)
    {
        if (string.IsNullOrEmpty(cookId))
        {
            return false;
        }
        return Handles.TryGetValue(cookId, out CookControlHandle? handle) && handle.CancelRequested;
    }

    // Request cancellation of a supervised cook. When a live handle exists, sets
    // the cancel flag and kills the ENTIRE child process tree (the pwsh child
    // plus any grandchildren the engine spawned) through that live Process. All
    // process access is defensive: a race where the child exits between lookup
    // and Kill must never throw out of this method (an already-exited process
    // throws InvalidOperationException; the OS can throw Win32Exception
    // mid-teardown). Returns NotSupervised when there is no live handle — in
    // which case NOTHING is killed (never a pid-based kill).
    internal static CancelResult RequestCancel(string cookId)
    {
        if (string.IsNullOrEmpty(cookId) ||
            !Handles.TryGetValue(cookId, out CookControlHandle? handle))
        {
            return new CancelResult(CancelOutcome.NotSupervised, KillThrew: false);
        }

        // Mark first so the supervisor records "canceled" even if the child is
        // exiting at this very instant.
        handle.CancelRequested = true;

        bool killThrew = false;
        try
        {
            // entireProcessTree:true reaps the pwsh child and any grandchildren
            // (for example a PAX-spawned helper) in one call.
            handle.Process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The child likely already exited, or the OS refused mid-teardown.
            // Either way the supervisor will still finalize the cook; the cancel
            // degrades gracefully and the cancel flag is already set.
            killThrew = true;
        }

        return new CancelResult(CancelOutcome.Requested, killThrew);
    }
}
