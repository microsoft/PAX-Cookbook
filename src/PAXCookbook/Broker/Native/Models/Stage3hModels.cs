namespace PAXCookbook.Broker.Native.Models;

// Stage 3h -- shared types for the per-operation Windows re-auth
// verifier, the recipe-projection-hash composer, and the Credential
// Manager secret-store abstractions. All three abstractions are
// invoked from the native scheduled-task PUT/DELETE routes.
//
// Doctrine carried over from the PowerShell broker:
//   * Re-auth uses the Windows UserConsentVerifier (Hello / PIN /
//     biometric prompt). NOT WebAuthn. WebAuthn is reserved for the
//     SPA lock-overlay unlock flow only.
//   * Only the verdict literal "Verified" passes. Any other value
//     (DeviceNotPresent / NotConfiguredForUser / DisabledByPolicy /
//     DeviceBusy / RetriesExhausted / Canceled / ComInteropFailure /
//     Unknown) is fail-closed and surfaces as a 401 reAuthRequired
//     envelope to the caller.
//   * Verdict strings match the PowerShell broker verbatim --
//     Auth\WindowsReAuth.ps1 Invoke-WindowsReAuth return values.

// Verdict from Invoke-WindowsReAuth. The "Result" field is the raw
// string the PS function returns; IsVerified is true iff Result is
// exactly "Verified" (strict case-sensitive equality). FailureDetail
// is populated only when Result is "ComInteropFailure" -- it carries
// the underlying COM HRESULT / exception message captured by
// Get-WindowsReAuthLastFailureDetail.
public sealed record WindowsReAuthVerdict(
    string Result,
    bool IsVerified,
    string? FailureDetail);

// Result of computing the recipe projection hash for a given
// (recipe, authProfile, executionMode, paxScriptVersion) tuple via
// Get-RecipeProjectionHash. Sha256Hex is the 64-char lowercase hex
// digest. Error is null on success and a structured string
// otherwise (sidecar timeout / spawn failure / PS function threw /
// stdout did not match the expected hex format).
public sealed record RecipeProjectionHashResult(
    bool Ok,
    string? Sha256Hex,
    string? Error);
