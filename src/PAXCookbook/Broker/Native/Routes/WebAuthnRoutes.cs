using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3d -- WebAuthn readiness surface.
//
//   GET  /api/v1/broker/webauthn/status -- honest readiness probe.
//                                          Reads
//                                          <WorkspacePath>\Auth\webauthn-credentials.json
//                                          and surfaces
//                                          registered + credentialIds
//                                          + acceptedOrigins + rpId +
//                                          supportedAlgs + userVerification.
//                                          NEVER surfaces public keys
//                                          (the broker is the verifier;
//                                          the SPA does not need them).
//
//   The following six POST routes are intentionally registered with
//   controlled 501 not_implemented envelopes:
//     POST /api/v1/broker/webauthn/unlock-challenge
//     POST /api/v1/broker/webauthn/unlock
//     POST /api/v1/broker/webauthn/bootstrap-register-challenge
//     POST /api/v1/broker/webauthn/bootstrap-register-unlock
//     POST /api/v1/broker/webauthn/register-challenge
//     POST /api/v1/broker/webauthn/register
//
//   The verification half (single-use challenge map, ECDSA P-256
//   signature verification, COSE key parsing, attestation auth-data
//   flag inspection, credential persistence under the workspace)
//   is a large port that requires its own dedicated slice. Stage 3d
//   intentionally returns honest "not implemented" so the SPA can
//   pick a path without being tricked into believing the native
//   broker will accept an assertion -- the PowerShell broker remains
//   the only verifier until that slice lands.
public static class WebAuthnRoutes
{
    public static void Register(
        IEndpointRouteBuilder app,
        WebAuthnReadinessReader readiness,
        int port)
    {
        app.MapGet("/api/v1/broker/webauthn/status", () =>
        {
            var info = readiness.BuildStatus(port);
            return Results.Json(new
            {
                registered       = info.Registered,
                credentialIds    = info.CredentialIds,
                acceptedOrigins  = info.AcceptedOrigins,
                rpId             = info.RpId,
                supportedAlgs    = info.SupportedAlgs,
                userVerification = info.UserVerification,
            });
        });

        // Six deferred POST endpoints. All return the same stable
        // not_implemented envelope so the SPA can render a single
        // "WebAuthn verification not available on native broker"
        // message regardless of which sub-endpoint it called.
        var deferredPaths = new[]
        {
            "/api/v1/broker/webauthn/unlock-challenge",
            "/api/v1/broker/webauthn/unlock",
            "/api/v1/broker/webauthn/bootstrap-register-challenge",
            "/api/v1/broker/webauthn/bootstrap-register-unlock",
            "/api/v1/broker/webauthn/register-challenge",
            "/api/v1/broker/webauthn/register",
        };

        foreach (var path in deferredPaths)
        {
            // Capture loop variable into a local so each handler
            // closes over its own copy (it would not matter for the
            // identical response, but it makes the intent obvious).
            var endpoint = path;
            app.MapPost(endpoint, () =>
            {
                return Results.Json(new
                {
                    error              = "not_implemented",
                    code               = "webauthnVerifierUnavailable",
                    verificationResult = "NotPortedNative",
                    endpoint           = endpoint,
                    detail             = "WebAuthn verification (single-use challenge, ECDSA P-256 "
                                       + "signature check, COSE key parse, credential persistence) "
                                       + "is not yet ported to the native broker. The PowerShell "
                                       + "broker remains the production WebAuthn path until a "
                                       + "later stage.",
                },
                statusCode: StatusCodes.Status501NotImplemented);
            });
        }
    }
}
