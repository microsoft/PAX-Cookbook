namespace PAXCookbook.Service;

/// <summary>
/// CYCLE 64. The two-strikes rule, made explicit.
///
/// The rule itself never changed and is NOT widened here: zero strikes after a
/// success; ONE tolerated consecutive failure; the SECOND consecutive failure is
/// terminal. It was previously an implicit boolean threaded through the heartbeat
/// loop, which made it impossible to test without a real clock and a real
/// filesystem. It is now a pure value transition with no clock, no filesystem, no
/// exception, no logging and no caller-supplied tolerance.
/// </summary>
internal readonly struct HeartbeatStrikeState : IEquatable<HeartbeatStrikeState>
{
    private HeartbeatStrikeState(int consecutiveFailures, bool isTerminal)
    {
        ConsecutiveFailures = consecutiveFailures;
        IsTerminal = isTerminal;
    }

    /// <summary>The starting state, and the state after any success. Zero strikes.</summary>
    internal static HeartbeatStrikeState None => new(0, false);

    internal int ConsecutiveFailures { get; }

    /// <summary>True once the tolerated allowance is exhausted. The worker must stop writing.</summary>
    internal bool IsTerminal { get; }

    public bool Equals(HeartbeatStrikeState other) =>
        ConsecutiveFailures == other.ConsecutiveFailures && IsTerminal == other.IsTerminal;

    public override bool Equals(object? obj) => obj is HeartbeatStrikeState other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(ConsecutiveFailures, IsTerminal);

    public static bool operator ==(HeartbeatStrikeState left, HeartbeatStrikeState right) => left.Equals(right);

    public static bool operator !=(HeartbeatStrikeState left, HeartbeatStrikeState right) => !left.Equals(right);

    internal static HeartbeatStrikeState Create(int consecutiveFailures, bool isTerminal) =>
        new(consecutiveFailures, isTerminal);
}

/// <summary>
/// The pure transition function. The worker is its only production caller.
/// </summary>
internal static class HeartbeatStrikeRule
{
    /// <summary>
    /// Exactly ONE consecutive failure is tolerated. This is a compile-time constant
    /// on purpose: no configuration, no environment value and no caller may raise it.
    /// </summary>
    internal const int ToleratedConsecutiveFailures = 1;

    /// <summary>A success always resets the count. It can never produce a terminal state.</summary>
    internal static HeartbeatStrikeState AfterSuccess(HeartbeatStrikeState current) =>
        HeartbeatStrikeState.None;

    /// <summary>
    /// A failure increments the consecutive count. The state becomes terminal only
    /// once the count exceeds the tolerated allowance.
    /// </summary>
    internal static HeartbeatStrikeState AfterFailure(HeartbeatStrikeState current)
    {
        var consecutiveFailures = current.ConsecutiveFailures + 1;

        return HeartbeatStrikeState.Create(
            consecutiveFailures,
            consecutiveFailures > ToleratedConsecutiveFailures);
    }
}
