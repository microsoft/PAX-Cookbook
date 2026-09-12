using System.Diagnostics;

namespace PAXCookbook.App;

// Native broker lock state machine — parity port of the PowerShell oracle
// (app\broker\Auth\BrokerLock.ps1). The PowerShell broker remains the
// immutable parity oracle; this is a behavior-equivalent native
// reimplementation, not a wrapper around it.
//
// Doctrine (preserved verbatim from the oracle):
//   - Boot state is ALWAYS Locked. There is NO persisted "remember me"
//     flag; closing the SPA does not lock; only an explicit lock,
//     inactivity timeout, or runtime restart locks.
//   - The lock is broker-scoped (per-process), not browser-scoped. Two
//     browser tabs on the same runtime see the same lock state.
//   - Unlocked state is NOT operation approval. Per-op gated operations
//     perform a fresh verification regardless of lock state (not ported
//     in X3 — those mutable routes are out of scope).
//   - Monotonic time (Stopwatch.GetTimestamp) is the ELAPSED-RUNTIME
//     OPERATIONAL authority; wall-clock is HISTORICAL EVIDENCE only.
//     The inactivity sweep is lazy (no background timer thread).
internal static class BrokerLock
{
    internal const int InactivityTimeoutMinutes = 15;

    // Wall-vs-monotonic classification threshold (seconds). Frozen oracle
    // value (Get-BrokerTimeSkewSnapshot: $threshold = 60).
    private const double AnomalyThresholdSec = 60.0;

    private static readonly object Gate = new();

    // Boot default is ALWAYS Locked.
    private static string _state = "Locked";
    private static DateTime _lastActivityUtc = DateTime.UtcNow;
    private static long _lastActivityMonoTicks = Stopwatch.GetTimestamp();
    private static TimeAnomaly? _timeAnomaly;

    // Provenance of the CURRENT unlocked session (T1-S3): how the session was
    // unlocked. One of "windows_hello", "work_account", or null when Locked.
    // NEVER persisted — it is process-scoped in-memory state, so a broker
    // restart, shutdown, explicit Lock, or failed unlock all clear it. Bearer
    // possession is NOT provenance; provenance is set only after a verified
    // unlock ceremony completes. It records HOW the current session unlocked
    // ("windows_hello" or "work_account") so provider-switching in Settings can
    // reason about the active provider. It is NOT used to gate OAuth
    // administration — either selected provider's Unlocked session may
    // administer configuration (OAuth is the Hello replacement).
    private static string? _unlockProvenance;

    // Monotonically increasing counter bumped on every transition into the
    // Locked state (explicit lock, inactivity timeout, or time-anomaly
    // re-lock). It is exposed as a bounded, non-secret indicator that the lock
    // state machine advanced.
    private static long _lockGeneration;

    // Exact lock-bypass allow-list (oracle $Script:BrokerLockAllowedWhenLockedRoutes).
    // These are the ONLY HTTP routes reachable while Locked (static asset
    // GETs and /api/v1/health are short-circuited upstream). Method + path
    // are matched exactly; the oracle patterns are literal (no wildcards).
    private static readonly (string Method, string Path)[] AllowedWhenLocked =
    {
        ("GET",  "/api/v1/broker/lock-state"),
        ("POST", "/api/v1/broker/unlock"),
        ("POST", "/api/v1/broker/lock"),
        ("GET",  "/api/v1/broker/webauthn/status"),
        ("POST", "/api/v1/broker/webauthn/unlock-challenge"),
        ("POST", "/api/v1/broker/webauthn/unlock"),
        ("POST", "/api/v1/broker/webauthn/bootstrap-register-challenge"),
        ("POST", "/api/v1/broker/webauthn/bootstrap-register-unlock"),

        // Experimental Entra WAM session unlock must be able to run its ceremony
        // while Locked: the renderer initiates a request and polls a bounded
        // status, and a valid NATIVE result unlocks once. Only these exact paths
        // are lock-bypass (no prefix wildcard); the security-critical result
        // arrives over native IPC, never an HTTP route. Bearer + CSRF still apply
        // (the capability probe is a safe GET, so no CSRF). The capability GET is
        // reachable while Locked so the lock overlay can decide whether to offer
        // the experimental work-account action before an unlock.
        ("GET",  "/api/v1/broker/experimental/wam/capability"),
        ("POST", "/api/v1/broker/experimental/wam/initiate"),
        ("POST", "/api/v1/broker/experimental/wam/status"),
        ("GET",  "/api/v1/broker/experimental/wam/config-state"),

        // The selected-session-provider status is a safe, bounded GET the lock
        // overlay reads while Locked to render EXACTLY the selected provider's
        // experience (Windows Hello OR work account, never both). It returns no
        // identifiers — only the selected provider id, a usable flag, a bounded
        // health/recovery code, and whether recovery is required.
        ("GET",  "/api/v1/broker/session-provider"),
    };

    // Returns "Locked" or "Unlocked" after applying the lazy inactivity sweep.
    internal static string GetState()
    {
        lock (Gate)
        {
            Sweep();
            return _state;
        }
    }

    // Current lock generation after applying the lazy inactivity sweep (so a
    // pending sweep-driven re-lock is reflected before the value is read).
    internal static long CurrentLockGeneration
    {
        get
        {
            lock (Gate)
            {
                Sweep();
                return _lockGeneration;
            }
        }
    }

    // JSON-serializable snapshot for GET /api/v1/broker/lock-state. Applies
    // the lazy sweep first. inactivityRemainingSeconds is monotonic-derived
    // and is 0 whenever the broker is not Unlocked.
    internal static LockSnapshot GetSnapshot()
    {
        lock (Gate)
        {
            Sweep();
            int remaining = 0;
            if (_state == "Unlocked")
            {
                var skew = Skew(_lastActivityUtc, _lastActivityMonoTicks);
                double totalSec = InactivityTimeoutMinutes * 60;
                remaining = (int)Math.Max(0, totalSec - skew.MonoElapsedSec);
            }

            return new LockSnapshot(
                _state,
                _lastActivityUtc.ToString("o"),
                InactivityTimeoutMinutes,
                remaining,
                _timeAnomaly);
        }
    }

    // Transition any state -> Unlocked. Callers MUST have already obtained a
    // verified verdict; this method does NOT independently verify (parity
    // with Set-BrokerLockUnlocked). Bumps both anchors and clears any anomaly.
    internal static void SetUnlocked() => SetUnlocked(null);

    // Provenance-carrying unlock. provenance is the verified unlock method
    // ("windows_hello" or "work_account"); null records an unspecified unlock
    // (test seams). Provenance is never persisted.
    internal static void SetUnlocked(string? provenance)
    {
        lock (Gate)
        {
            _state = "Unlocked";
            _lastActivityUtc = DateTime.UtcNow;
            _lastActivityMonoTicks = Stopwatch.GetTimestamp();
            _timeAnomaly = null;
            _unlockProvenance = provenance;
        }
    }

    // Provenance of the current unlocked session, or null when Locked. Never
    // persisted; cleared on Lock/restart/shutdown.
    internal static string? GetUnlockProvenance()
    {
        lock (Gate)
        {
            Sweep();
            return _state == "Unlocked" ? _unlockProvenance : null;
        }
    }

    // Transition any state -> Locked. Idempotent. Does NOT reset
    // lastActivityUtc (informational once Locked). Bumps the lock generation
    // only on a real transition so a stray double-lock does not churn it.
    internal static void SetLocked()
    {
        lock (Gate)
        {
            if (_state != "Locked")
            {
                _lockGeneration++;
            }
            _state = "Locked";
            _unlockProvenance = null;
        }
    }

    // Bump activity anchors on a successful authenticated request that is NOT
    // a lock-state poll. Clears any recorded anomaly (fresh activity proves
    // auth freshness survived whatever discontinuity a prior sweep saw).
    internal static void BumpActivity()
    {
        lock (Gate)
        {
            _lastActivityUtc = DateTime.UtcNow;
            _lastActivityMonoTicks = Stopwatch.GetTimestamp();
            _timeAnomaly = null;
        }
    }

    // Lock-bypass predicate: can this request proceed while Locked?
    internal static bool IsRouteAllowedWhenLocked(string method, string path)
    {
        foreach (var (m, p) in AllowedWhenLocked)
        {
            if (string.Equals(m, method, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Lazy inactivity sweep (caller holds Gate). Re-locks the broker on any
    // of: (a) monotonic idle >= inactivity threshold, (b) wall_clock_rollback,
    // (c) sleep_or_pause_gap, (d) wall_clock_forward_jump. Normal idle re-locks
    // leave _timeAnomaly null; anomaly re-locks record the classification so
    // the next snapshot surfaces WHY.
    private static void Sweep()
    {
        if (_state != "Unlocked")
        {
            return;
        }

        var skew = Skew(_lastActivityUtc, _lastActivityMonoTicks);
        TimeAnomaly? forceRelock = null;

        if (skew.AnomalyKind is not null)
        {
            forceRelock = new TimeAnomaly(
                skew.AnomalyKind,
                DateTime.UtcNow.ToString("o"),
                Math.Round(skew.WallElapsedSec, 3),
                Math.Round(skew.MonoElapsedSec, 3),
                Math.Round(skew.SkewSec, 3),
                (int)AnomalyThresholdSec);
        }

        // The inactivity auto-lock has been removed: an open app never locks on
        // idle (active, idle, or minimized to the taskbar). The broker still
        // re-locks on a detected time anomaly (clock rollback / sleep gap /
        // forward jump) so a runtime-continuity break never leaves stale auth in
        // place. Windows Hello is still required at startup and before each
        // manual bake (the step-up gate is independent of this sweep).
        if (forceRelock is not null)
        {
            if (_state != "Locked")
            {
                _lockGeneration++;
            }
            _state = "Locked";
            _timeAnomaly = forceRelock;
        }
    }

    // Dual-clock skew classification — parity with Get-BrokerTimeSkewSnapshot.
    // CLASSIFIES, does not repair. Monotonic elapsed is >= 0 by construction;
    // wall elapsed can be negative (truthful evidence of a rollback).
    private static SkewResult Skew(DateTime refWallUtc, long refMonoTicks)
    {
        DateTime nowWall = DateTime.UtcNow;
        long nowTicks = Stopwatch.GetTimestamp();
        double wallElapsedSec = (nowWall - refWallUtc).TotalSeconds;
        double monoFreq = Stopwatch.Frequency;
        if (monoFreq <= 0)
        {
            monoFreq = 1.0;
        }

        double monoElapsedSec = (nowTicks - refMonoTicks) / monoFreq;
        double skewSec = wallElapsedSec - monoElapsedSec;

        string? anomalyKind = null;
        if (wallElapsedSec < -AnomalyThresholdSec)
        {
            anomalyKind = "wall_clock_rollback";
        }
        else if (skewSec > AnomalyThresholdSec && monoElapsedSec < 1.0)
        {
            anomalyKind = "sleep_or_pause_gap";
        }
        else if (skewSec > AnomalyThresholdSec)
        {
            anomalyKind = "wall_clock_forward_jump";
        }

        return new SkewResult(wallElapsedSec, monoElapsedSec, skewSec, anomalyKind);
    }

    private readonly record struct SkewResult(
        double WallElapsedSec,
        double MonoElapsedSec,
        double SkewSec,
        string? AnomalyKind);
}

// Lock-state snapshot serialized on GET /api/v1/broker/lock-state. Field
// names are camelCase to match the oracle JSON contract consumed by
// app\web\assets\broker-status.js and lock-overlay.js.
internal sealed record LockSnapshot(
    string state,
    string lastActivityUtc,
    int inactivityTimeoutMinutes,
    int inactivityRemainingSeconds,
    TimeAnomaly? timeAnomaly);

// Structured time-anomaly payload (null on the happy path). Surfaced, never
// smoothed (oracle AG.C10 doctrine).
internal sealed record TimeAnomaly(
    string kind,
    string observedAtUtc,
    double wallElapsedSec,
    double monoElapsedSec,
    double skewSec,
    int anomalyThresholdSec);
