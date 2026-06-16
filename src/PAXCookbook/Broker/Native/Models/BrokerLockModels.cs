namespace PAXCookbook.Broker.Native.Models;

// In-process broker lock state. Mirrors the PowerShell broker's
// $Script:BrokerLockState string. Locked is the boot state; the
// operator must unlock explicitly once per process lifetime. There
// is no persisted "remember me" flag and no across-restart unlock.
// The string-typed wire form is preserved by emitting ToWireString().
public enum BrokerLockStateKind
{
    Locked,
    Unlocked,
}

// Snapshot suitable for direct JSON serialisation on
// GET /api/v1/broker/lock-state. Shape parity with the PowerShell
// broker's Get-BrokerLockStateSnapshot output: state (string),
// lastActivityUtc (ISO-8601), inactivityTimeoutMinutes (int),
// inactivityRemainingSeconds (int), timeAnomaly (null in the native
// broker's Stage 3d slice -- the wall-vs-monotonic anomaly
// classification is not ported yet).
public sealed record BrokerLockSnapshot(
    string State,
    string LastActivityUtc,
    int InactivityTimeoutMinutes,
    int InactivityRemainingSeconds,
    object? TimeAnomaly);
