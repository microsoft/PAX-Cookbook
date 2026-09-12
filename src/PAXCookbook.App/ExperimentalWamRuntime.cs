using System;

namespace PAXCookbook.App;

// Live runtime owner of the experimental Work-account provider (Track 1 / T1-S3
// Phase 3 backend). Replaces the frozen startup classification with a
// thread-safe holder that:
//   * recomputes the bounded configuration state on demand from the durable
//     stores (config + verification), so Settings mutations reflect immediately;
//   * hot-activates the native endpoint + IPC pipe when — and only when — the
//     effective state is ready (compiled + fully configured + enabled +
//     verified), and hot-deactivates on disable, removal, or verification drift;
//   * exposes the ONE active endpoint the capability/initiate/status routes use.
//
// Endpoint/pipe ownership is guarded by a single lock and every transition is
// idempotent: repeated reconciles never create a second pipe, and a stale pipe
// is never left running after config removal, disable, or fingerprint drift
// (any identifier change invalidates the fingerprint-bound verification, which
// forces deactivation on the next reconcile).
internal sealed class ExperimentalWamRuntime : IDisposable
{
    // Reported wire code for the explicit local-disable state (configuration
    // retained but the operator turned the provider off locally). Distinct from
    // not_configured/invalid so the UI never conflates them.
    internal const string DisabledStateCode = "disabled";

    private readonly string? _localAppDataBase;
    private readonly string _pipeName;
    private readonly bool _compiled;
    private readonly Func<string, string?>? _readEnv;
    private readonly Func<DateTimeOffset> _now;

    private readonly object _gate = new();
    private ExperimentalWamDaemonEndpoint? _endpoint;
    private ExperimentalWamPipeServer? _pipeServer;
    private bool _disposed;

    internal ExperimentalWamRuntime(
        string? localAppDataBase,
        string pipeName,
        bool compiled,
        Func<string, string?>? readEnv = null,
        Func<DateTimeOffset>? now = null)
    {
        _localAppDataBase = localAppDataBase;
        _pipeName = pipeName;
        _compiled = compiled;
        _readEnv = readEnv;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    // The active endpoint, or null unless the provider is ready and activated.
    // The capability/initiate/status routes consult this (never a frozen value).
    internal ExperimentalWamDaemonEndpoint? Endpoint
    {
        get
        {
            lock (_gate)
            {
                return _endpoint;
            }
        }
    }

    // Recompute the live bounded state, reconciling native activation as a side
    // effect so capability tracks the state exactly.
    internal ExperimentalWamStateInfo GetState()
    {
        lock (_gate)
        {
            return ComputeAndReconcileLocked();
        }
    }

    // Import a helper setup-result: validate strictly, persist atomically, clear
    // any stale verification (drops to configured_unverified), and reconcile.
    internal ExperimentalWamActionResult ImportSetupResult(string? json)
    {
        lock (_gate)
        {
            if (!_compiled || string.IsNullOrWhiteSpace(_localAppDataBase))
            {
                return ExperimentalWamActionResult.Fail("unavailable_in_this_build", ComputeAndReconcileLocked());
            }

            ExperimentalWamSetupResultImport.ImportResult parsed =
                ExperimentalWamSetupResultImport.Parse(json, _compiled, _now());
            if (!parsed.Success || parsed.Input is null)
            {
                return ExperimentalWamActionResult.Fail(
                    ExperimentalWamSetupResultImport.ToReasonCode(parsed.Error),
                    ComputeAndReconcileLocked());
            }

            ExperimentalWamConfigStore.Save(_localAppDataBase!, parsed.Input);
            // A helper "verified" claim is never current consent proof: the
            // import transitions only to configured_unverified.
            ExperimentalWamVerificationStore.Clear(_localAppDataBase!);
            return ExperimentalWamActionResult.Ok(ComputeAndReconcileLocked());
        }
    }

    // Local enable/disable toggle. Retains all identifiers; only flips the
    // enabled flag. Disable hot-deactivates the provider on reconcile.
    internal ExperimentalWamActionResult SetLocallyEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (!_compiled || string.IsNullOrWhiteSpace(_localAppDataBase))
            {
                return ExperimentalWamActionResult.Fail("unavailable_in_this_build", ComputeAndReconcileLocked());
            }

            ExperimentalWamStoredConfig? stored = ExperimentalWamConfigStore.Load(_localAppDataBase!);
            if (stored is null || stored.IsMalformed)
            {
                return ExperimentalWamActionResult.Fail("not_configured", ComputeAndReconcileLocked());
            }

            ExperimentalWamConfigInput input = stored.ToInput();
            input = new ExperimentalWamConfigInput
            {
                Enabled = enabled,
                ProviderId = input.ProviderId,
                TenantId = input.TenantId,
                ClientId = input.ClientId,
            };
            ExperimentalWamConfigStore.Save(_localAppDataBase!, input);
            return ExperimentalWamActionResult.Ok(ComputeAndReconcileLocked());
        }
    }

    // Remove the local configuration (identifiers + verification). Does NOT touch
    // cloud objects. Reconcile deactivates the provider; returns to Hello-only.
    internal ExperimentalWamActionResult RemoveLocalConfiguration()
    {
        lock (_gate)
        {
            if (!_compiled || string.IsNullOrWhiteSpace(_localAppDataBase))
            {
                return ExperimentalWamActionResult.Fail("unavailable_in_this_build", ComputeAndReconcileLocked());
            }

            ExperimentalWamConfigStore.Delete(_localAppDataBase!);
            ExperimentalWamVerificationStore.Clear(_localAppDataBase!);
            return ExperimentalWamActionResult.Ok(ComputeAndReconcileLocked());
        }
    }

    // Record a successful independent read-only cloud verification for the
    // CURRENT configuration and reconcile to ready. The caller (Verify flow) is
    // responsible for having validated the helper result and fingerprint match.
    internal ExperimentalWamActionResult RecordVerified()
    {
        lock (_gate)
        {
            if (!_compiled || string.IsNullOrWhiteSpace(_localAppDataBase))
            {
                return ExperimentalWamActionResult.Fail("unavailable_in_this_build", ComputeAndReconcileLocked());
            }

            ExperimentalWamOptions options = CurrentStructuralOptionsLocked(out _);
            if (!options.IsFullyConfigured)
            {
                return ExperimentalWamActionResult.Fail("not_configured", ComputeAndReconcileLocked());
            }

            ExperimentalWamVerificationStore.RecordVerified(_localAppDataBase!, options);
            return ExperimentalWamActionResult.Ok(ComputeAndReconcileLocked());
        }
    }

    // Record that the most recent verification/sign-in failed due to missing or
    // revoked tenant consent. Reconcile drops capability.
    internal ExperimentalWamActionResult RecordConsentFailure()
    {
        lock (_gate)
        {
            if (!_compiled || string.IsNullOrWhiteSpace(_localAppDataBase))
            {
                return ExperimentalWamActionResult.Fail("unavailable_in_this_build", ComputeAndReconcileLocked());
            }

            ExperimentalWamOptions options = CurrentStructuralOptionsLocked(out _);
            if (!options.IsFullyConfigured)
            {
                return ExperimentalWamActionResult.Fail("not_configured", ComputeAndReconcileLocked());
            }

            ExperimentalWamVerificationStore.RecordConsentFailure(_localAppDataBase!, options);
            return ExperimentalWamActionResult.Ok(ComputeAndReconcileLocked());
        }
    }

    // Raw non-secret identifiers for the administrator-details disclosure. Only
    // returned to an Unlocked caller by the route; null when unconfigured.
    internal ExperimentalWamAdminDetails? GetAdminDetails()
    {
        lock (_gate)
        {
            ExperimentalWamOptions options = CurrentStructuralOptionsLocked(out _);
            if (!options.IsFullyConfigured)
            {
                return null;
            }

            return new ExperimentalWamAdminDetails(options.TenantId, options.ClientId);
        }
    }

    // ---- internals -------------------------------------------------------

    private ExperimentalWamStateInfo ComputeAndReconcileLocked()
    {
        ExperimentalWamResolution res = ExperimentalWamHost.ResolveFromSources(_localAppDataBase, _readEnv);
        ExperimentalWamConfigInput? attempted = res.Attempted;
        bool locallyEnabled = attempted?.Enabled ?? false;

        // Structural validity ignoring the local enable toggle (so a disabled
        // valid configuration is not mis-reported as invalid/not-configured).
        ExperimentalWamOptions structuralOptions = attempted is null
            ? ExperimentalWamOptions.Disabled
            : ExperimentalWamOptions.Create(WithEnabled(attempted, true));

        ExperimentalWamVerification verification = ExperimentalWamHost.ResolveEffectiveVerification(
            _localAppDataBase, structuralOptions, _compiled, _readEnv);

        // Desired activation uses the ACTUAL options (which respect the enable
        // toggle): a disabled config yields Disabled options -> not fully
        // configured -> deactivate.
        ExperimentalWamVerification actualVerification = ExperimentalWamHost.ResolveEffectiveVerification(
            _localAppDataBase, res.Options, _compiled, _readEnv);
        bool desiredActive = _compiled &&
            res.Options.IsFullyConfigured &&
            actualVerification == ExperimentalWamVerification.Verified;
        ReconcileActivationLocked(desiredActive, res.Options);

        bool activated = _endpoint is not null;
        ExperimentalWamConfigState baseState = ExperimentalWamStatus.Classify(
            new ExperimentalWamStatusInput(
                isCompiled: _compiled,
                configSourcePresent: res.ConfigSourcePresent,
                storedMalformed: res.StoredMalformed,
                attempted: attempted,
                isFullyConfigured: structuralOptions.IsFullyConfigured,
                providerActivated: activated,
                verification: verification));

        bool disabled = _compiled && structuralOptions.IsFullyConfigured && !locallyEnabled;
        string code = disabled ? DisabledStateCode : ExperimentalWamStatus.ToWireCode(baseState);

        string? verifiedUtc = (structuralOptions.IsFullyConfigured &&
                               verification == ExperimentalWamVerification.Verified &&
                               !string.IsNullOrWhiteSpace(_localAppDataBase))
            ? ExperimentalWamVerificationStore.GetVerifiedTimestamp(_localAppDataBase!, structuralOptions)
            : null;

        bool configured = structuralOptions.IsFullyConfigured;
        string? providerId = _compiled ? ExperimentalWamOptions.EntraWamProviderId : null;
        return new ExperimentalWamStateInfo(code, disabled, activated, configured, verifiedUtc, providerId);
    }

    private void ReconcileActivationLocked(bool desiredActive, ExperimentalWamOptions options)
    {
        if (_disposed)
        {
            return;
        }

        if (desiredActive)
        {
            if (_endpoint is not null)
            {
                return; // already active; idempotent.
            }

            ExperimentalWamDaemonEndpoint? endpoint = ExperimentalWamHost.TryCreateDaemonEndpoint(options);
            if (endpoint is null)
            {
                return;
            }

            ExperimentalWamDaemonEndpoint? activated = ExperimentalWamActivation.ActivateIfPipeReady(
                endpoint,
                () => ExperimentalWamPipeServer.TryCreate(_pipeName, endpoint),
                out ExperimentalWamPipeServer? server);

            if (activated is not null)
            {
                _endpoint = activated;
                _pipeServer = server;
                server?.Start();
            }
            else
            {
                server?.Dispose();
            }
        }
        else
        {
            if (_endpoint is null)
            {
                return; // already inactive; idempotent.
            }

            _pipeServer?.Dispose();
            _pipeServer = null;
            _endpoint = null;
        }
    }

    // Current structural options (toggle forced true) from the durable/env source.
    private ExperimentalWamOptions CurrentStructuralOptionsLocked(out bool locallyEnabled)
    {
        ExperimentalWamResolution res = ExperimentalWamHost.ResolveFromSources(_localAppDataBase, _readEnv);
        locallyEnabled = res.Attempted?.Enabled ?? false;
        return res.Attempted is null
            ? ExperimentalWamOptions.Disabled
            : ExperimentalWamOptions.Create(WithEnabled(res.Attempted, true));
    }

    private static ExperimentalWamConfigInput WithEnabled(ExperimentalWamConfigInput input, bool enabled) => new()
    {
        Enabled = enabled,
        ProviderId = input.ProviderId,
        TenantId = input.TenantId,
        ClientId = input.ClientId,
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pipeServer?.Dispose();
            _pipeServer = null;
            _endpoint = null;
        }
    }
}

// Immutable snapshot of the live provider state for the config-state route and
// the Settings UI. Carries NO tenant/client/resource identifier.
internal sealed class ExperimentalWamStateInfo
{
    internal ExperimentalWamStateInfo(
        string stateCode,
        bool disabled,
        bool capabilityAvailable,
        bool configured,
        string? verifiedUtc,
        string? providerId)
    {
        StateCode = stateCode;
        Disabled = disabled;
        CapabilityAvailable = capabilityAvailable;
        Configured = configured;
        VerifiedUtc = verifiedUtc;
        ProviderId = providerId;
    }

    internal string StateCode { get; }

    internal bool Disabled { get; }

    internal bool CapabilityAvailable { get; }

    internal bool Configured { get; }

    internal string? VerifiedUtc { get; }

    internal string? ProviderId { get; }
}

// Result of a Settings mutation: the bounded reason (on failure) plus the fresh
// state after the attempt.
internal sealed class ExperimentalWamActionResult
{
    private ExperimentalWamActionResult(bool success, string? reason, ExperimentalWamStateInfo state)
    {
        Success = success;
        Reason = reason;
        State = state;
    }

    internal bool Success { get; }

    internal string? Reason { get; }

    internal ExperimentalWamStateInfo State { get; }

    internal static ExperimentalWamActionResult Ok(ExperimentalWamStateInfo state) => new(true, null, state);

    internal static ExperimentalWamActionResult Fail(string reason, ExperimentalWamStateInfo state) =>
        new(false, reason, state);
}

// Non-secret administrator-details disclosure (Unlocked route only).
internal sealed class ExperimentalWamAdminDetails
{
    internal ExperimentalWamAdminDetails(string tenantId, string clientId)
    {
        TenantId = tenantId;
        ClientId = clientId;
    }

    internal string TenantId { get; }

    internal string ClientId { get; }
}
