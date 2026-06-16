namespace PAXCookbook.Broker.Native.Services;

// Stage 3h -- Windows Credential Manager secret-store abstraction.
// The PowerShell broker uses Set-AuthProfileSecret /
// Test-AuthProfileSecretPresent / Remove-AuthProfileSecret in
// Auth\CredentialManager.ps1 to write the AppRegistrationSecret
// auth.mode client secret to CredMan. The native broker uses this
// abstraction so:
//   * Production wires WindowsCredentialSecretStore (advapi32
//     P/Invoke; identical TargetName / Type=GENERIC / Persist=
//     LOCAL_MACHINE / UTF-16 LE blob to the PS impl).
//   * Tests wire a fake that records (target, bytes) writes for
//     assertion -- the real CredMan is NEVER touched in unit
//     tests.
//
// Target naming MUST match the PS broker verbatim:
//   PAXCookbook.AuthProfile.<authProfileId>.ClientSecret
//
// SECURITY:
//   * Read is intentionally NOT on this interface. The scheduled-
//     task wrapper at fire-time pulls the secret directly via
//     Read-AuthProfileSecret (supervisor scope); the broker
//     process never reads the secret back. The PUT route's only
//     CredMan operations are Write + Exists.
//   * ComposeTarget is a pure string operation (no FS access).
public interface ICredentialSecretStore
{
    // Writes the client secret to Windows Credential Manager under
    // the canonical target name. Replaces an existing value
    // idempotently (the PS impl performs the same). Throws on
    // failure -- callers MUST handle the exception path (the PUT
    // route maps it to 500 secret_write_failed).
    void Write(string authProfileId, string secret);

    // Returns true iff a credential with the canonical target name
    // already exists. Used by the PUT route's secret-rebind
    // verification.
    bool Exists(string authProfileId);

    // Deletes the credential under the canonical target name. Does
    // NOT throw if the credential is absent (best-effort cleanup
    // semantics, parity with Remove-AuthProfileSecret).
    void Delete(string authProfileId);

    // Pure naming function (Get-AuthProfileCredManTarget). Exposed
    // so tests can assert the exact target the route will write to
    // without having to mock the entire write path.
    string ComposeTarget(string authProfileId);
}
