namespace PAXCookbook.Broker.Native.Models;

// Honest readiness snapshot for GET /api/v1/broker/webauthn/status.
//
// The PowerShell broker's Get-WebAuthnRegistrationSummary reads
// <WorkspacePath>\Auth\webauthn-credentials.json and returns:
//   { registered: bool, credentialIds: string[] }
// plus the broker emits acceptedOrigins/rpId/supportedAlgs/userVerification
// from its bound port and a static config block.
//
// Stage 3d ports the read-only / file-discovery half of this surface
// only. The verification half (challenge issuance, signature check,
// ECDSA P-256, COSE key parsing) is documented as deferred and the
// corresponding POST routes return controlled 501 not_implemented.
public sealed record WebAuthnStatusInfo(
    bool Registered,
    IReadOnlyList<string> CredentialIds,
    IReadOnlyList<string> AcceptedOrigins,
    string RpId,
    IReadOnlyList<int> SupportedAlgs,
    string UserVerification);
