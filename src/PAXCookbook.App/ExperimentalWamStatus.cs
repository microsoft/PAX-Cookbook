using System;

namespace PAXCookbook.App;

// The bounded lifecycle state of the experimental Entra WAM Work-account
// provider (Track 1 / T1-S3 Phase 2).
//
// Exactly one of these states describes the provider at any moment. The Settings
// experience, the session/lock overlay, and the onboarding import all render
// from this single classification instead of re-deriving readiness from raw
// identifiers. The state is computed by a pure function (ExperimentalWamStatus.
// Classify) from build capability, durable/environment configuration presence,
// deterministic validity, native provider activation, and a recorded
// verification outcome — so it is trivially unit-testable with no ambient state.
internal enum ExperimentalWamConfigState
{
    // The running build was not compiled with the experimental authenticator
    // (every stable/customer build). The provider can never be offered.
    UnavailableInThisBuild = 0,

    // Experimental build, but no durable configuration and no environment
    // configuration exist. Nothing to offer yet.
    NotConfigured = 1,

    // A configuration source exists but is incomplete: not enabled, or one or
    // more required identifiers are blank. The administrator started but has not
    // finished supplying the work-account configuration.
    AdministratorSetupRequired = 2,

    // A configuration source is present and is attempting to be complete
    // (enabled, all identifiers supplied) but the values are malformed,
    // self-referential (client == resource), or otherwise fail deterministic
    // validation.
    InvalidConfiguration = 3,

    // The configuration is complete and well-formed, but the native provider
    // (daemon endpoint + same-user IPC pipe) could not activate on this run, so
    // no sign-in can currently be attempted.
    ProviderUnavailable = 4,

    // The configuration is complete, well-formed, and the native provider is
    // active, but no successful work-account sign-in has been recorded yet.
    ConfiguredUnverified = 5,

    // The last work-account sign-in failed specifically because tenant-wide
    // administrator consent for the single Microsoft Graph User.Read permission is missing
    // or has been revoked. The configuration itself is valid.
    ConsentMissingOrRevoked = 6,

    // The configuration is complete, well-formed, the provider is active, and a
    // successful work-account sign-in has been recorded. Fully usable.
    Ready = 7,
}

// The recorded outcome of the most recent work-account sign-in attempt. Persisted
// separately from the admin-authored configuration so a runtime result never
// mutates the configuration file.
internal enum ExperimentalWamVerification
{
    // No sign-in has been recorded (fresh configuration).
    None = 0,

    // A work-account sign-in completed successfully.
    Verified = 1,

    // The most recent attempt failed due to missing/revoked tenant consent.
    ConsentFailed = 2,
}

// Immutable inputs to the classifier. Assembled by the host from the winning
// configuration source; carries no persisted identifier beyond the raw attempted
// values needed to distinguish "incomplete" from "invalid".
internal readonly struct ExperimentalWamStatusInput
{
    internal ExperimentalWamStatusInput(
        bool isCompiled,
        bool configSourcePresent,
        bool storedMalformed,
        ExperimentalWamConfigInput? attempted,
        bool isFullyConfigured,
        bool providerActivated,
        ExperimentalWamVerification verification)
    {
        IsCompiled = isCompiled;
        ConfigSourcePresent = configSourcePresent;
        StoredMalformed = storedMalformed;
        Attempted = attempted;
        IsFullyConfigured = isFullyConfigured;
        ProviderActivated = providerActivated;
        Verification = verification;
    }

    // Build was compiled with the experimental authenticator.
    internal bool IsCompiled { get; }

    // A durable config file or an environment configuration gate is present.
    internal bool ConfigSourcePresent { get; }

    // A durable config file existed but could not be parsed / had an unknown
    // schema version.
    internal bool StoredMalformed { get; }

    // The raw values from the winning source (durable file preferred over
    // environment), before validation. Null when no source is present.
    internal ExperimentalWamConfigInput? Attempted { get; }

    // The resolved options passed deterministic validation.
    internal bool IsFullyConfigured { get; }

    // The native daemon endpoint + IPC pipe activated on this run.
    internal bool ProviderActivated { get; }

    // Recorded outcome of the most recent sign-in attempt.
    internal ExperimentalWamVerification Verification { get; }
}

internal static class ExperimentalWamStatus
{
    // Pure classification into exactly one bounded state. Order of the guards is
    // load-bearing: build capability first (a stable build can never leave
    // UnavailableInThisBuild), then presence, then validity, then activation,
    // then verification.
    internal static ExperimentalWamConfigState Classify(ExperimentalWamStatusInput input)
    {
        if (!input.IsCompiled)
        {
            return ExperimentalWamConfigState.UnavailableInThisBuild;
        }

        // A durable file that exists but cannot be read is an explicit invalid
        // configuration, never a silent "not configured".
        if (input.StoredMalformed)
        {
            return ExperimentalWamConfigState.InvalidConfiguration;
        }

        if (!input.ConfigSourcePresent)
        {
            return ExperimentalWamConfigState.NotConfigured;
        }

        if (input.IsFullyConfigured)
        {
            // Verification — not mere structural validity — drives usability. A
            // structurally complete configuration is only configured_unverified
            // until an independent read-only cloud verification records success;
            // activation is attempted ONLY in the Verified case, so it merely
            // distinguishes ready from provider_unavailable there.
            return input.Verification switch
            {
                ExperimentalWamVerification.ConsentFailed => ExperimentalWamConfigState.ConsentMissingOrRevoked,
                ExperimentalWamVerification.Verified =>
                    input.ProviderActivated
                        ? ExperimentalWamConfigState.Ready
                        : ExperimentalWamConfigState.ProviderUnavailable,
                _ => ExperimentalWamConfigState.ConfiguredUnverified,
            };
        }

        // Present but not fully configured: distinguish an administrator who has
        // not finished (incomplete) from supplied-but-invalid values.
        bool attemptingComplete =
            input.Attempted is ExperimentalWamConfigInput a &&
            a.Enabled &&
            !string.IsNullOrWhiteSpace(a.TenantId) &&
            !string.IsNullOrWhiteSpace(a.ClientId);

        return attemptingComplete
            ? ExperimentalWamConfigState.InvalidConfiguration
            : ExperimentalWamConfigState.AdministratorSetupRequired;
    }

    // Stable snake_case wire code for a state. This is the ONLY string a route,
    // the Settings UX, or the onboarding import keys on; the enum names are
    // internal.
    internal static string ToWireCode(ExperimentalWamConfigState state) => state switch
    {
        ExperimentalWamConfigState.UnavailableInThisBuild => "unavailable_in_this_build",
        ExperimentalWamConfigState.NotConfigured => "not_configured",
        ExperimentalWamConfigState.AdministratorSetupRequired => "administrator_setup_required",
        ExperimentalWamConfigState.InvalidConfiguration => "invalid_configuration",
        ExperimentalWamConfigState.ProviderUnavailable => "provider_unavailable",
        ExperimentalWamConfigState.ConfiguredUnverified => "configured_unverified",
        ExperimentalWamConfigState.ConsentMissingOrRevoked => "consent_missing_or_revoked",
        ExperimentalWamConfigState.Ready => "ready",
        _ => "unavailable_in_this_build",
    };
}
