using System;

namespace PAXCookbook.App;

// Orchestrates the customer-facing "Verify setup" flow (Track 1 / T1-S3 Phase 2).
//
// PAX Cookbook never stores an administrator credential and never calls Microsoft
// Graph with a hidden product credential. Instead it launches the fixed, guarded
// helper, which uses the administrator's OWN transient Azure CLI session to
// perform a READ-ONLY live verification and emit a strict, redacted result. The
// product then independently validates that result — schema, provider/build,
// exact identifiers, exact configuration fingerprint, all structural checks, the
// AllPrincipals Microsoft Graph User.Read grant, and freshness — and ONLY then records
// verification and transitions to ready. A helper failure never erases the local
// configuration, and a proven missing/revoked consent maps to
// consent_missing_or_revoked (Hello recovery always preserved).
internal static class ExperimentalWamVerifyCoordinator
{
    internal static ExperimentalWamActionResult RunVerify(
        ExperimentalWamRuntime runtime,
        ExperimentalWamHelperRunner runner,
        bool isCompiled,
        DateTimeOffset nowUtc)
    {
        if (!isCompiled)
        {
            return ExperimentalWamActionResult.Fail("unavailable_in_this_build", runtime.GetState());
        }

        ExperimentalWamAdminDetails? details = runtime.GetAdminDetails();
        if (details is null)
        {
            // No complete configuration to verify.
            return ExperimentalWamActionResult.Fail("not_configured", runtime.GetState());
        }

        string expectedFingerprint = ExperimentalWamVerificationStore.ComputeFingerprint(
            details.TenantId, details.ClientId);

        ExperimentalWamHelperRunResult run = runner.Run(ExperimentalWamHelperOperation.Verify);
        if (!run.IsSuccess)
        {
            // Cloud/helper unavailable (pwsh/helper/timeout/nonzero): do NOT erase
            // local configuration and do NOT change the verification record — a
            // previously valid verification (if any) remains under the freshness
            // policy. This is distinct from a proven-invalid/revoked setup.
            return ExperimentalWamActionResult.Fail(
                ExperimentalWamHelperRunResult.ToReasonCode(run.Status),
                runtime.GetState());
        }

        ExperimentalWamVerifyResultImport.VerifyResult validated =
            ExperimentalWamVerifyResultImport.Validate(
                run.RawJson,
                isCompiled,
                details.TenantId,
                details.ClientId,
                expectedFingerprint,
                nowUtc);

        if (validated.Success)
        {
            // Independent read-only verification confirmed: record and reconcile
            // to ready (activates the endpoint/pipe).
            return runtime.RecordVerified();
        }

        if (validated.Error == ExperimentalWamVerifyResultImport.VerifyError.ConsentCheckFailed)
        {
            // Proven missing/revoked consent: record the consent failure so the
            // state becomes consent_missing_or_revoked (capability unavailable),
            // configuration retained, Hello preserved.
            ExperimentalWamActionResult consent = runtime.RecordConsentFailure();
            return ExperimentalWamActionResult.Fail("consent_missing_or_revoked", consent.State);
        }

        // Any other validation failure: preserve prior state; do not record a new
        // verification. Return the bounded reason.
        return ExperimentalWamActionResult.Fail(
            ExperimentalWamVerifyResultImport.ToReasonCode(validated.Error),
            runtime.GetState());
    }
}
