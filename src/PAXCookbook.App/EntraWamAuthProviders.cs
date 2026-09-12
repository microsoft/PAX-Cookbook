namespace PAXCookbook.App;

// Experimental Entra WAM adapter for the provider-neutral session-unlock seam
// (T1-S2A).
//
// This implements the EXISTING synchronous ISessionUnlockProvider contract
// (Windows Hello is untouched). It consumes a sanitized, token-free
// WamInteractiveResult that was produced in the HWND-owning process and returns
// ONLY a neutral verdict. It fails closed whenever the provider is not fully
// configured, the in-process authorization challenge did not validate, the
// acquisition did not succeed, or exact scope / identity validation fails. There
// is no fallback to Windows Hello.
//
// A WAM verdict is never treated as Windows Hello evidence and never claims
// Hello-equivalent assurance (POL-1).

// Bounded, redacted provider outcome reason for diagnostics. Never exposes a
// tenant/client/object identifier, token, or raw claim.
internal enum EntraWamProviderReason
{
    None = 0,
    Approved = 1,
    NotConfigured = 2,
    ChallengeRejected = 3,
    AcquireFailed = 4,
    ScopeOrIdentityRejected = 5,
}

internal sealed class EntraWamSessionUnlockProvider : ISessionUnlockProvider
{
    private readonly ExperimentalWamOptions _options;
    private readonly WamInteractiveResult _result;
    private readonly bool _challengeValidated;

    internal EntraWamSessionUnlockProvider(
        ExperimentalWamOptions options,
        WamInteractiveResult result,
        bool challengeValidated)
    {
        _options = options;
        _result = result;
        _challengeValidated = challengeValidated;
    }

    public string ProviderId => AuthProviderIds.EntraWam;

    // Bounded diagnostic reason from the last Authorize() call.
    internal EntraWamProviderReason LastReason { get; private set; } = EntraWamProviderReason.None;

    public SessionUnlockOutcome Authorize()
    {
        if (_options is null || !_options.IsFullyConfigured)
        {
            LastReason = EntraWamProviderReason.NotConfigured;
            return SessionUnlockOutcome.Deny;
        }

        if (!_challengeValidated)
        {
            LastReason = EntraWamProviderReason.ChallengeRejected;
            return SessionUnlockOutcome.Deny;
        }

        if (_result is null || !_result.Succeeded)
        {
            LastReason = EntraWamProviderReason.AcquireFailed;
            return SessionUnlockOutcome.Deny;
        }

        WamValidation validation = WamScopeIdentityValidator.Validate(_result, _options);
        if (validation != WamValidation.Valid)
        {
            LastReason = EntraWamProviderReason.ScopeOrIdentityRejected;
            return SessionUnlockOutcome.Deny;
        }

        LastReason = EntraWamProviderReason.Approved;
        return SessionUnlockOutcome.Grant;
    }
}
