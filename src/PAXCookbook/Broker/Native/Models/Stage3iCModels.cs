namespace PAXCookbook.Broker.Native.Models;

// Stage 3i-C -- DTOs + outcome records for the auth-profile
// mutation surface, the credential-secret bind/remove surface, the
// auth-profile structural-test surface, and the cook stop/kill/
// resume surface.
//
// Single file (mirrors the lightweight model conventions used by
// Stage 3i-B1's RecipeMutationModels.cs) to keep route + service
// imports tight and to make the record shape diffable across the
// PowerShell broker (Routes/AuthProfiles.ps1, Routes/Cooks.ps1).
//
// The PS broker contract for every record below is documented inline.

// =====================================================================
// Auth-profile mutation request shapes
// =====================================================================

// POST /api/v1/auth/profiles -- create body.
//
// Mode is required and immutable after create. tenantId / clientId
// are required GUIDs (lowercase per Test-AuthProfileGuid). For
// AppRegistrationSecret mode credManTarget is server-stamped after
// the row INSERT (clients SHOULD NOT supply it; the create route
// derives it as PAXCookbook.AuthProfile.<authProfileId>.ClientSecret).
// For AppRegistrationCertificate mode certThumbprint is required;
// certStore defaults to "LocalMachine\\My".
public sealed record AuthProfileCreateRequest(
    string  Mode,
    string  Name,
    string  TenantId,
    string  ClientId,
    string? Description,
    string? CertThumbprint,
    string? CertStore);

// PUT /api/v1/auth/profiles/{id} -- update body.
//
// Mode is NOT accepted here: the PS broker enforces mode immutability
// (Routes/AuthProfiles.ps1 Invoke-AuthProfileUpdate). The native
// port returns 422 auth_profile_invalid with keyword "immutable" if
// the body carries a mode field that does not match the existing
// row. tenantId / clientId / description / certThumbprint /
// certStore are all mutable.
public sealed record AuthProfileUpdateRequest(
    string? Mode, // Validated for absence-or-match; never persisted via UPDATE.
    string? Name,
    string? TenantId,
    string? ClientId,
    string? Description,
    string? CertThumbprint,
    string? CertStore);

// POST /api/v1/auth/profiles/{id}/secret -- bind body.
//
// Plaintext over loopback (parity with the PS broker's POST handler
// in Routes/AuthProfiles.ps1). The bind service zeroes the buffer
// after handoff to ICredentialSecretStore.Write.
public sealed record AuthProfileSecretBindRequest(string ClientSecret);

// =====================================================================
// Validation error envelope (mirrors RecipeMutationModels.ValidationError)
// =====================================================================

public sealed record AuthProfileValidationError(
    string InstancePath,
    string Keyword,
    string Message,
    object Params);

// =====================================================================
// Auth-profile structural-test result (POST /api/v1/auth/profiles/{id}/test)
// =====================================================================

// The PS broker (Routes/AuthProfiles.ps1 Invoke-AuthProfileTest)
// returns 200 OK on EVERY outcome (including failure). The payload
// always carries validationKind="structural" and an ok flag. No
// HTTPS calls to Microsoft are made -- the test is bounded to
// confirming the credential is present (CredMan for Secret mode,
// X.509 store for Certificate mode).
//
// Result codes (string detail):
//   structural_ok               -- credential present, all required
//                                  metadata fields populated.
//   secret_missing              -- mode=AppRegistrationSecret but
//                                  CredMan reports the target absent.
//   cert_thumbprint_missing     -- mode=AppRegistrationCertificate
//                                  but cert_thumbprint is null/empty.
//   cert_not_found              -- thumbprint present but no matching
//                                  certificate found in cert_store.
//   probe_failed                -- ICertificateProbe.Locate threw
//                                  (e.g. CryptographicException).
//   mode_unsupported            -- mode is not one of the two
//                                  Stage 3i-C supported modes
//                                  (ManagedIdentity / WebLogin etc.
//                                  remain deferred).
public sealed record AuthProfileTestResult(
    string  AuthProfileId,
    string  Mode,
    string  ValidationKind, // always "structural"
    bool    Ok,
    string  Detail,
    string  LastVerifiedAt,
    string  LastVerifiedResult);

// =====================================================================
// Cook control models
// =====================================================================

// Cook resume spawn request handed to ICookResumeSpawner. The
// CookControlService composes this from the resume route inputs;
// production wires a deferred spawner (controlled 501 envelope --
// the route family still emits 401 reAuthRequired / 400
// invalid_cook_id / 404 not_found / 409 not_resumable / 410
// not_resumable_vanished / 412 recipe_invalid before reaching the
// spawner), tests wire a recording spawner that returns success.
//
// CheckpointFilePath is the absolute path captured from the parent
// cook's closure_evidence_json (key "checkpointPath"). The resume
// route asserts the file exists immediately before handing off.
public sealed record CookResumeSpawnRequest(
    string ParentCookId,
    string NewCookId,
    string RecipeId,
    string CookFolder,
    string CheckpointFilePath,
    string RecipeFilePath,
    string PaxScriptPath,
    string PaxScriptVersion);

// Result returned by ICookResumeSpawner. Outcome is "spawned" when
// the new process started; "deferred" when production wiring is
// not yet present (Stage 3i-C lands the route family but Stage 3j
// is responsible for wiring the spawner to the real CookExecution
// Service + PaxProcessRunner pipeline). Tests inject a recording
// spawner that returns Spawned for the success-path tests and a
// deferred spawner for the controlled-501 test.
public sealed record CookResumeSpawnResult(
    string  Outcome,        // "spawned" | "deferred" | "failed"
    string? FailureCode,    // when Outcome != "spawned"
    string? FailureDetail); // when Outcome != "spawned"

// =====================================================================
// Constants for native PAX broker parity
// =====================================================================

public static class AuthProfileModes
{
    public const string AppRegistrationSecret      = "AppRegistrationSecret";
    public const string AppRegistrationCertificate = "AppRegistrationCertificate";

    public static readonly string[] SupportedForMutation = new[]
    {
        AppRegistrationSecret,
        AppRegistrationCertificate,
    };

    public const string DefaultCertStore = @"LocalMachine\My";
}

public static class AuthProfileOpClasses
{
    public const string ProfileMutation = "profileMutation";
    public const string SecretBind      = "secretBind";
    public const string SecretRemove    = "secretRemove";
    public const string ProfileTest     = "profileTest";
}

public static class CookControlOpClasses
{
    public const string ManualCook = "manualCook";
}

public static class CookClosureReasons
{
    // Cook rows must carry one of these closure_reason values to be
    // resumable. cancel_kill is NOT in the list -- a force-killed
    // cook cannot be safely resumed (its checkpoint may be partial
    // or corrupt).
    public static readonly string[] Resumable = new[]
    {
        "cancel_stop",
        "cancel_stop_escalated_kill",
        "broker_restart",
        "broker_restart_manual",
        "broker_restart_scheduled",
    };

    public const string CancelStop          = "cancel_stop";
    public const string CancelStopEscalated = "cancel_stop_escalated_kill";
    public const string CancelKill          = "cancel_kill";
}
