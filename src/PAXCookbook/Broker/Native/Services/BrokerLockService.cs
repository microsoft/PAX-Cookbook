using System.Text.RegularExpressions;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// In-process broker lock state, owned by NativeBrokerHost. Mirrors
// the PowerShell broker's $Script:BrokerLockState plus the lazy
// inactivity sweep performed in Auth\BrokerLock.ps1
// Invoke-BrokerLockInactivitySweep.
//
// Doctrine carried over from the PS broker (verbatim where it
// applies to Stage 3d):
//   - Boot state is ALWAYS Locked. There is NO persisted "remember
//     me" flag; closing the SPA does not lock; only an explicit
//     lock, inactivity timeout, or broker restart locks.
//   - The lock-state endpoint does NOT bump activity -- otherwise
//     SPA polling would keep the broker unlocked indefinitely.
//   - Process-scoped: state lives in this in-memory service and
//     resets on every host restart.
//
// Stage 3d intentionally drops the wall-vs-monotonic time-anomaly
// classification (the PS broker's Phase AG.C10 work). The wire
// shape preserves the timeAnomaly field as null so the SPA's
// renderer is not surprised; populating it is deferred.
public sealed class BrokerLockService
{
    public const int DefaultInactivityTimeoutMinutes = 15;

    private static readonly TimeSpan PollerActivityBumpWindow = TimeSpan.FromSeconds(0);

    // Lock-bypass allowlist. Mirrors $Script:BrokerLockAllowedWhenLockedRoutes
    // in app/broker/Auth/BrokerLock.ps1. The PowerShell broker also
    // bypasses /api/v1/health BEFORE the lock middleware runs -- the
    // native broker handles that the same way (the health route is
    // registered separately and is reachable regardless of lock
    // state). Anything matching here is reachable while Locked.
    private static readonly IReadOnlyList<(string Method, Regex PathRegex)> AllowedWhenLocked =
        new (string, Regex)[]
        {
            ("GET",  Compile("/api/v1/broker/lock-state")),
            ("POST", Compile("/api/v1/broker/unlock")),
            ("POST", Compile("/api/v1/broker/lock")),
            ("GET",  Compile("/api/v1/broker/webauthn/status")),
            ("POST", Compile("/api/v1/broker/webauthn/unlock-challenge")),
            ("POST", Compile("/api/v1/broker/webauthn/unlock")),
            ("POST", Compile("/api/v1/broker/webauthn/bootstrap-register-challenge")),
            ("POST", Compile("/api/v1/broker/webauthn/bootstrap-register-unlock")),
        };

    private static Regex Compile(string pathLiteral) =>
        new("^" + Regex.Escape(pathLiteral) + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly int _inactivityTimeoutMinutes;
    private readonly Func<DateTimeOffset> _utcNow;

    private readonly object _gate = new();
    private BrokerLockStateKind _state;
    private DateTimeOffset _lastActivityUtc;

    public BrokerLockService(
        int inactivityTimeoutMinutes = DefaultInactivityTimeoutMinutes,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (inactivityTimeoutMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inactivityTimeoutMinutes),
                "Inactivity timeout must be positive.");
        }
        _inactivityTimeoutMinutes = inactivityTimeoutMinutes;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _state = BrokerLockStateKind.Locked;
        _lastActivityUtc = _utcNow();
    }

    public int InactivityTimeoutMinutes => _inactivityTimeoutMinutes;

    // Reads current state with the lazy inactivity sweep applied.
    // The sweep re-locks the broker if it has been idle past the
    // configured threshold. Pure side-effect, no allocation on the
    // hot path.
    public BrokerLockStateKind GetState()
    {
        lock (_gate)
        {
            ApplyInactivitySweep_NoLock();
            return _state;
        }
    }

    public BrokerLockSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            ApplyInactivitySweep_NoLock();
            var totalSeconds = _inactivityTimeoutMinutes * 60;
            var remaining = 0;
            if (_state == BrokerLockStateKind.Unlocked)
            {
                var elapsed = (int)Math.Floor((_utcNow() - _lastActivityUtc).TotalSeconds);
                remaining = Math.Max(0, totalSeconds - elapsed);
            }
            return new BrokerLockSnapshot(
                State: ToWireString(_state),
                LastActivityUtc: _lastActivityUtc.ToUniversalTime().ToString("o"),
                InactivityTimeoutMinutes: _inactivityTimeoutMinutes,
                InactivityRemainingSeconds: remaining,
                TimeAnomaly: null);
        }
    }

    // Idempotent explicit relock. Returns the post-state snapshot.
    public BrokerLockSnapshot TransitionToLocked()
    {
        lock (_gate)
        {
            _state = BrokerLockStateKind.Locked;
            // lastActivityUtc intentionally not reset (informational
            // once Locked, parity with Set-BrokerLockLocked).
            return GetSnapshotNoLock();
        }
    }

    // Verified-unlock entry point. Stage 3d does NOT call this from a
    // route (the unlock route returns 501 not_implemented because the
    // WinRT IUserConsentVerifier path is not yet portable in-process);
    // it exists so unit tests can drive the transition deterministically
    // and so later stages can wire WebAuthn or a sidecar verifier.
    public BrokerLockSnapshot TransitionToUnlocked()
    {
        lock (_gate)
        {
            _state = BrokerLockStateKind.Unlocked;
            _lastActivityUtc = _utcNow();
            return GetSnapshotNoLock();
        }
    }

    // Bumps lastActivityUtc to "now". Called from the lock-bypass
    // middleware on every successful non-lock-state authenticated
    // request. Poller-style endpoints (lock-state) MUST NOT call this
    // -- otherwise SPA polling keeps the broker unlocked indefinitely.
    public void TouchActivity()
    {
        lock (_gate)
        {
            _lastActivityUtc = _utcNow();
        }
    }

    public static bool IsRouteAllowedWhenLocked(string method, string path)
    {
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(path)) return false;
        foreach (var (allowedMethod, regex) in AllowedWhenLocked)
        {
            if (!string.Equals(allowedMethod, method, StringComparison.OrdinalIgnoreCase)) continue;
            if (regex.IsMatch(path)) return true;
        }
        return false;
    }

    private void ApplyInactivitySweep_NoLock()
    {
        if (_state != BrokerLockStateKind.Unlocked) return;
        var elapsed = _utcNow() - _lastActivityUtc;
        if (elapsed.TotalMinutes >= _inactivityTimeoutMinutes)
        {
            _state = BrokerLockStateKind.Locked;
            // Do not reset _lastActivityUtc -- informational only.
        }
    }

    private BrokerLockSnapshot GetSnapshotNoLock()
    {
        var totalSeconds = _inactivityTimeoutMinutes * 60;
        var remaining = 0;
        if (_state == BrokerLockStateKind.Unlocked)
        {
            var elapsed = (int)Math.Floor((_utcNow() - _lastActivityUtc).TotalSeconds);
            remaining = Math.Max(0, totalSeconds - elapsed);
        }
        return new BrokerLockSnapshot(
            State: ToWireString(_state),
            LastActivityUtc: _lastActivityUtc.ToUniversalTime().ToString("o"),
            InactivityTimeoutMinutes: _inactivityTimeoutMinutes,
            InactivityRemainingSeconds: remaining,
            TimeAnomaly: null);
    }

    private static string ToWireString(BrokerLockStateKind state) =>
        state == BrokerLockStateKind.Unlocked ? "Unlocked" : "Locked";

    // Hint to silence unused-readonly-field warning if PollerActivityBumpWindow
    // is removed later. The field is named for the doctrine -- poller endpoints
    // do not bump activity, so the "window" is intentionally zero.
    internal TimeSpan PollerWindow => PollerActivityBumpWindow;
}
