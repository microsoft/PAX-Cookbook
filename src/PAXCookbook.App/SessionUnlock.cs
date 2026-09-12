namespace PAXCookbook.App;

// Provider-neutral session-unlock contract (Track 1 / T1-S1).
//
// A session-unlock provider verifies its own provider-specific evidence out of
// band (Windows Hello today; a future alternate later) and reports ONLY a
// neutral verdict. This contract deliberately carries no transport or trust
// mechanics: no HTTP request/response type, serialization type, provider
// credential, or identity-transport detail appears on it. Provider-specific
// detail lives only in the concrete adapters. Session unlock is the sole
// authorization concern this contract expresses.
internal enum SessionUnlockDecision
{
    Denied = 0,
    Approved = 1,
}

// Neutral outcome of a session-unlock authorization. Carries the verdict only.
// An approved verdict is what maps to the broker session-unlock side effect,
// but it does NOT itself redefine broker lock semantics.
internal readonly record struct SessionUnlockOutcome(SessionUnlockDecision Decision)
{
    internal bool Approved => Decision == SessionUnlockDecision.Approved;

    internal static SessionUnlockOutcome Grant { get; } = new(SessionUnlockDecision.Approved);
    internal static SessionUnlockOutcome Deny { get; } = new(SessionUnlockDecision.Denied);
}

// A provider that can authorize lifting the broker session lock.
internal interface ISessionUnlockProvider
{
    // Stable, neutral provider identity for diagnostics (e.g. "windows-hello").
    string ProviderId { get; }

    // Verify the provider's own evidence and return a neutral verdict. The
    // provider performs NO broker state transition; applying the verdict is the
    // coordinator's responsibility.
    SessionUnlockOutcome Authorize();
}

// Applies a provider's verified session-unlock verdict to the broker lock. This
// is the ONLY place a session-unlock verdict becomes a broker session-state
// transition. A denied verdict changes nothing.
//
// The broker side effect is injected (defaulting to BrokerLock.SetUnlocked) so
// orchestration can be exercised with a fake provider and a spy side effect,
// without a live Windows Hello ceremony.
internal sealed class SessionUnlockCoordinator
{
    private readonly Action _applyUnlock;

    internal SessionUnlockCoordinator(Action? applyUnlock = null)
    {
        _applyUnlock = applyUnlock ?? BrokerLock.SetUnlocked;
    }

    // Ask the provider for its verdict; on approval, apply the session-unlock
    // side effect exactly once. Returns the neutral outcome.
    internal SessionUnlockOutcome Unlock(ISessionUnlockProvider provider)
    {
        SessionUnlockOutcome outcome = provider.Authorize();
        if (outcome.Approved)
        {
            _applyUnlock();
        }

        return outcome;
    }
}
