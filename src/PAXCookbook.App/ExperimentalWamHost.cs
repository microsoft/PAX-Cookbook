using System;

namespace PAXCookbook.App;

// Experimental Entra WAM host wiring (Track 1 / T1-S2A repair).
//
// Maps the explicit experimental RUNTIME configuration (environment-injected,
// never hardcoded) onto typed ExperimentalWamOptions, and exposes the concrete
// selection used at the daemon and window call sites. It is the single host
// mapping that makes the provider selectable; when the environment gate is
// absent/false/partial, it resolves to Disabled and every call site falls back
// to the unchanged Windows Hello path (a fail-closed default, NOT an automatic
// fallback after an explicit entra-wam request).
//
// No customer-facing Settings text and no stable route depends on this; a
// stable/default build never sets these variables, so it cannot activate the
// provider.
internal static class ExperimentalWamHost
{
    internal const string EnabledEnvVar = "PAXCOOKBOOK_EXPERIMENTAL_WAM_ENABLED";
    internal const string TenantEnvVar = "PAXCOOKBOOK_EXPERIMENTAL_WAM_TENANT";
    internal const string ClientEnvVar = "PAXCOOKBOOK_EXPERIMENTAL_WAM_CLIENT";
    internal const string ProviderEnvVar = "PAXCOOKBOOK_EXPERIMENTAL_WAM_PROVIDER";

    // Explicit developer/test override that bypasses read-only cloud verification
    // so a smoke or dev run can reach the ready state without a live tenant. It
    // is honored ONLY in an experimental build (IsExperimentalAuthenticatorCompiled)
    // and ONLY when the configuration is otherwise fully structurally valid; a
    // stable/customer build never compiles the authenticator, so the flag is inert
    // there and can never grant customer-ready capability. Mere presence of the
    // configuration environment variables is NOT sufficient — only this explicit
    // flag substitutes for verification.
    internal const string AssumeVerifiedEnvVar = "PAXCOOKBOOK_EXPERIMENTAL_WAM_ASSUME_VERIFIED_DEV_ONLY";

    // Resolve options from an explicit environment reader. The reader is
    // injectable so the mapping is deterministic and unit-testable with no
    // ambient process state. A null reader uses the process environment.
    //
    // Environment-only path (dev/back-compat). Production launches use
    // ResolveFromSources, which prefers the durable per-user configuration and
    // only falls back to the environment.
    internal static ExperimentalWamOptions Resolve(Func<string, string?>? readEnv = null)
        => ExperimentalWamOptions.Create(ReadEnvInput(readEnv, out _));

    // Full production resolution with durable-first precedence (T1-S3 Phase 2).
    //
    // A durable per-user configuration (written by the Settings experience or
    // the onboarding import) is preferred so the product needs NO environment
    // variables or developer tooling. Only when no durable file is present does
    // the environment gate apply, preserving the experimental dev workflow. The
    // returned resolution also carries the presence/validity signals the status
    // classifier needs, so the caller derives the bounded lifecycle state
    // without re-reading either source.
    internal static ExperimentalWamResolution ResolveFromSources(
        string? localAppDataBase,
        Func<string, string?>? readEnv = null)
    {
        // Durable configuration wins when present.
        if (!string.IsNullOrWhiteSpace(localAppDataBase))
        {
            ExperimentalWamStoredConfig? stored = ExperimentalWamConfigStore.Load(localAppDataBase!);
            if (stored is not null)
            {
                if (stored.IsMalformed)
                {
                    return new ExperimentalWamResolution(
                        ExperimentalWamOptions.Disabled,
                        configSourcePresent: true,
                        storedMalformed: true,
                        attempted: null);
                }

                ExperimentalWamConfigInput durableInput = stored.ToInput();
                return new ExperimentalWamResolution(
                    ExperimentalWamOptions.Create(durableInput),
                    configSourcePresent: true,
                    storedMalformed: false,
                    attempted: durableInput);
            }
        }

        // No durable file: fall back to the environment gate.
        ExperimentalWamConfigInput envInput = ReadEnvInput(readEnv, out bool envPresent);
        return new ExperimentalWamResolution(
            ExperimentalWamOptions.Create(envInput),
            configSourcePresent: envPresent,
            storedMalformed: false,
            attempted: envPresent ? envInput : null);
    }

    // Maps the environment gate onto the untyped validation input. envPresent is
    // true when ANY of the experimental variables is set (even to a disabling
    // value), so the status classifier can tell "no source" from "a source that
    // is present but incomplete".
    private static ExperimentalWamConfigInput ReadEnvInput(Func<string, string?>? readEnv, out bool envPresent)
    {
        Func<string, string?> env = readEnv ?? (name => Environment.GetEnvironmentVariable(name));

        string? enabledRaw = env(EnabledEnvVar);
        string? providerRaw = env(ProviderEnvVar);
        string? tenantRaw = env(TenantEnvVar);
        string? clientRaw = env(ClientEnvVar);

        envPresent =
            enabledRaw is not null ||
            providerRaw is not null ||
            tenantRaw is not null ||
            clientRaw is not null;

        bool enabled =
            string.Equals(enabledRaw, "1", StringComparison.Ordinal) ||
            string.Equals(enabledRaw, "true", StringComparison.OrdinalIgnoreCase);

        // The provider id, when the gate is on, must be exactly "entra-wam". An
        // unset provider variable defaults to the closed id only when enabled.
        string providerId = providerRaw ?? (enabled ? ExperimentalWamOptions.EntraWamProviderId : string.Empty);

        return new ExperimentalWamConfigInput
        {
            Enabled = enabled,
            ProviderId = string.IsNullOrEmpty(providerId) ? null : providerId,
            TenantId = tenantRaw,
            ClientId = clientRaw,
        };
    }

    // Daemon-side factory: builds the endpoint that mints challenges and applies
    // neutral results through the existing coordinators. Returns null when the
    // real experimental authenticator was NOT compiled in (a default/stable
    // build, regardless of environment configuration) or when the provider is
    // not fully configured, so the daemon advertises no active endpoint and the
    // capability route reports unavailable.
    internal static ExperimentalWamDaemonEndpoint? TryCreateDaemonEndpoint(ExperimentalWamOptions options)
    {
        if (!ExperimentalWamCapability.IsExperimentalAuthenticatorCompiled)
        {
            return null;
        }

        if (options is null || !options.IsFullyConfigured)
        {
            return null;
        }

        return new ExperimentalWamDaemonEndpoint(options);
    }

    // Resolves the effective verification for the current options. Customer-ready
    // capability requires this to be Verified. Precedence:
    //   1. Not fully configured  -> None (never verified).
    //   2. Experimental build AND explicit dev-only override flag -> Verified
    //      (a build-gated test seam; inert in stable builds).
    //   3. Durable verification record whose fingerprint matches the current
    //      identifiers -> its recorded outcome (Verified/ConsentFailed).
    //   4. Otherwise -> None (fail closed to configured_unverified).
    internal static ExperimentalWamVerification ResolveEffectiveVerification(
        string? localAppDataBase,
        ExperimentalWamOptions options,
        bool isCompiled,
        Func<string, string?>? readEnv = null)
    {
        if (options is null || !options.IsFullyConfigured)
        {
            return ExperimentalWamVerification.None;
        }

        if (isCompiled)
        {
            Func<string, string?> env = readEnv ?? (name => Environment.GetEnvironmentVariable(name));
            string? assumeRaw = env(AssumeVerifiedEnvVar);
            if (string.Equals(assumeRaw, "1", StringComparison.Ordinal) ||
                string.Equals(assumeRaw, "true", StringComparison.OrdinalIgnoreCase))
            {
                return ExperimentalWamVerification.Verified;
            }
        }

        if (!string.IsNullOrWhiteSpace(localAppDataBase))
        {
            return ExperimentalWamVerificationStore.Resolve(localAppDataBase!, options);
        }

        return ExperimentalWamVerification.None;
    }
}

// Combined result of durable-first configuration resolution. Carries the typed
// options plus the presence/validity signals the status classifier needs, so a
// single read of the winning source yields both the runtime options and the
// bounded lifecycle state.
internal sealed class ExperimentalWamResolution
{
    internal ExperimentalWamResolution(
        ExperimentalWamOptions options,
        bool configSourcePresent,
        bool storedMalformed,
        ExperimentalWamConfigInput? attempted)
    {
        Options = options;
        ConfigSourcePresent = configSourcePresent;
        StoredMalformed = storedMalformed;
        Attempted = attempted;
    }

    // The validated runtime options (Disabled when no valid configuration won).
    internal ExperimentalWamOptions Options { get; }

    // A durable config file or an environment gate is present.
    internal bool ConfigSourcePresent { get; }

    // A durable config file existed but could not be parsed / had an unknown
    // schema version.
    internal bool StoredMalformed { get; }

    // The raw values from the winning source, before validation. Null when no
    // source is present.
    internal ExperimentalWamConfigInput? Attempted { get; }
}
