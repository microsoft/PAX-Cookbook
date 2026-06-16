using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-C -- auth-profile structural-test surface:
//   POST   /api/v1/auth/profiles/{id}/test   -> Invoke-AuthProfileTest parity.
//
// Re-auth gated (opClass = "profileTest"). Empty body. The route
// returns 200 OK for EVERY outcome (parity with the PS broker --
// the ok flag inside the envelope is the success signal, not the
// HTTP status).
//
// Outcomes (Detail field):
//   structural_ok            -- credential present + metadata valid.
//   secret_missing           -- CredMan reports the target absent.
//   cert_thumbprint_missing  -- row has no cert_thumbprint.
//   cert_not_found           -- thumbprint present but no store match.
//   probe_failed             -- ICertificateProbe threw (e.g. ACL denial).
//   mode_unsupported         -- mode is neither Secret nor Cert.
public static class AuthProfileTestRoutes
{
    private const string PromptMessage = "Authenticate to run a structural test against this auth profile.";

    public static void Register(
        IEndpointRouteBuilder app,
        Stage3iCServiceBundle bundle,
        AuthProfileTestService service)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(service);

        app.MapPost("/api/v1/auth/profiles/{id}/test", async (HttpContext ctx, string id) =>
        {
            var verdict = await bundle.ReAuth.VerifyAsync(
                opClass:           AuthProfileOpClasses.ProfileTest,
                message:           PromptMessage,
                cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            if (!verdict.IsVerified) return ReAuthRequired(verdict, AuthProfileOpClasses.ProfileTest);
            bundle.LockService?.TouchActivity();

            try
            {
                var outcome = service.Test(id);
                return outcome switch
                {
                    AuthProfileTestService.TestOutcome.NotFound nf => Results.Json(new
                    {
                        error         = "auth_profile_not_found",
                        authProfileId = nf.AuthProfileId,
                    }, statusCode: 404),
                    AuthProfileTestService.TestOutcome.Tested t => Results.Json(new
                    {
                        authProfileId      = t.Result.AuthProfileId,
                        mode               = t.Result.Mode,
                        validationKind     = t.Result.ValidationKind,
                        ok                 = t.Result.Ok,
                        detail             = t.Result.Detail,
                        lastVerifiedAt     = t.Result.LastVerifiedAt,
                        lastVerifiedResult = t.Result.LastVerifiedResult,
                    }, statusCode: 200),
                    _ => Results.Json(new { error = "internal_error" }, statusCode: 500),
                };
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                return Results.Json(new
                {
                    error  = "workspace_database_unavailable",
                    detail = ex.Message,
                }, statusCode: 500);
            }
        });
    }

    private static IResult ReAuthRequired(WindowsReAuthVerdict verdict, string opClass) =>
        Results.Json(new
        {
            code               = "reAuthRequired",
            opClass            = opClass,
            verificationResult = verdict.Result,
            message            = DefaultMessageFor(verdict.Result),
        },
        statusCode: StatusCodes.Status401Unauthorized);

    private static string DefaultMessageFor(string verdict) => verdict switch
    {
        "Canceled"             => "Verification was canceled. Please try the operation again.",
        "NotConfiguredForUser" => "Windows Hello / PIN is not configured for your account. Set it up in Windows Settings before performing this operation.",
        "DisabledByPolicy"     => "Windows Hello is disabled by policy on this machine. Contact your administrator.",
        "DeviceNotPresent"     => "No verification device is available. This appliance requires Windows Hello, PIN, or a fallback credential prompt.",
        "DeviceBusy"           => "The verification device is busy. Please try again in a moment.",
        "RetriesExhausted"     => "Too many failed verification attempts. Please wait and try again.",
        "ComInteropFailure"    => "Windows verification surface is unavailable. Restart the appliance and try again; if the problem persists, see TROUBLESHOOTING \u00a713b.",
        _                      => "Verification did not succeed. Please try the operation again.",
    };
}
