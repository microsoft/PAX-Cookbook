using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-C -- auth-profile mutation surface:
//   POST   /api/v1/auth/profiles        -> Invoke-AuthProfileCreate parity.
//   PUT    /api/v1/auth/profiles/{id}   -> Invoke-AuthProfileUpdate parity.
//                                          (Plan-row text lists only
//                                          POST+DELETE; PUT is added
//                                          for SPA parity because the
//                                          SPA's "Edit profile" page
//                                          depends on it -- the Stage
//                                          3i-C authorization explicitly
//                                          includes "update".)
//   DELETE /api/v1/auth/profiles/{id}   -> Invoke-AuthProfileDelete parity.
//                                          (Auto-removes the CredMan
//                                          secret for AppRegistration
//                                          Secret profiles; failure is
//                                          best-effort and surfaced in
//                                          the success envelope.)
//
// All three routes are re-auth gated (opClass = "profileMutation").
// Re-auth runs BEFORE id-shape and body parsing so the lock activity
// bump and verdict envelope are uniform across outcomes.
//
// Envelope contract:
//   * 401 reAuthRequired           when the verifier does not return Verified.
//   * 400 invalid_json             body is malformed or root not object.
//   * 422 auth_profile_invalid     validator returned errors.
//   * 409 auth_profile_name_in_use unique-by-name collision.
//   * 404 auth_profile_not_found   row missing (PUT/DELETE).
//   * 500 internal_error / write_failed.
//   * 200 success for PUT/DELETE; 201 success for POST.
public static class AuthProfileMutationRoutes
{
    private const string PromptMessage = "Authenticate to manage auth profiles.";

    public static void Register(
        IEndpointRouteBuilder app,
        Stage3iCServiceBundle bundle,
        AuthProfileMutationService service)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(service);

        // ---------- POST -----------------------------------------------------
        app.MapPost("/api/v1/auth/profiles", async (HttpContext ctx) =>
        {
            var verdict = await bundle.ReAuth.VerifyAsync(
                opClass:           AuthProfileOpClasses.ProfileMutation,
                message:           PromptMessage,
                cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            if (!verdict.IsVerified) return ReAuthRequired(verdict, AuthProfileOpClasses.ProfileMutation);
            bundle.LockService?.TouchActivity();

            var body = await ReadJsonAsync(ctx);
            if (body is null)
                return Results.Json(new { error = "invalid_json" }, statusCode: 400);

            try
            {
                var outcome = service.Create(body);
                return outcome switch
                {
                    AuthProfileMutationService.CreateOutcome.Invalid v => Results.Json(new
                    {
                        error  = "auth_profile_invalid",
                        errors = v.Errors.Select(SerializeError).ToArray(),
                    }, statusCode: 422),
                    AuthProfileMutationService.CreateOutcome.NameInUse n => Results.Json(new
                    {
                        error = "auth_profile_name_in_use",
                        name  = n.Name,
                    }, statusCode: 409),
                    AuthProfileMutationService.CreateOutcome.Created c => Results.Json(new
                    {
                        authProfileId = c.Row.AuthProfileId,
                        authProfile   = ToWire(c.Row),
                    }, statusCode: 201),
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

        // ---------- PUT ------------------------------------------------------
        app.MapPut("/api/v1/auth/profiles/{id}", async (HttpContext ctx, string id) =>
        {
            var verdict = await bundle.ReAuth.VerifyAsync(
                opClass:           AuthProfileOpClasses.ProfileMutation,
                message:           PromptMessage,
                cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            if (!verdict.IsVerified) return ReAuthRequired(verdict, AuthProfileOpClasses.ProfileMutation);
            bundle.LockService?.TouchActivity();

            var body = await ReadJsonAsync(ctx);
            if (body is null)
                return Results.Json(new { error = "invalid_json" }, statusCode: 400);

            try
            {
                var outcome = service.Update(id, body);
                return outcome switch
                {
                    AuthProfileMutationService.UpdateOutcome.NotFound nf => Results.Json(new
                    {
                        error         = "auth_profile_not_found",
                        authProfileId = nf.AuthProfileId,
                    }, statusCode: 404),
                    AuthProfileMutationService.UpdateOutcome.Invalid v => Results.Json(new
                    {
                        error  = "auth_profile_invalid",
                        errors = v.Errors.Select(SerializeError).ToArray(),
                    }, statusCode: 422),
                    AuthProfileMutationService.UpdateOutcome.NameInUse n => Results.Json(new
                    {
                        error = "auth_profile_name_in_use",
                        name  = n.Name,
                    }, statusCode: 409),
                    AuthProfileMutationService.UpdateOutcome.WriteFailed w => Results.Json(new
                    {
                        error         = "write_failed",
                        authProfileId = w.AuthProfileId,
                    }, statusCode: 500),
                    AuthProfileMutationService.UpdateOutcome.Updated u => Results.Json(new
                    {
                        authProfileId = u.Row.AuthProfileId,
                        authProfile   = ToWire(u.Row),
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

        // ---------- DELETE ---------------------------------------------------
        app.MapDelete("/api/v1/auth/profiles/{id}", async (HttpContext ctx, string id) =>
        {
            var verdict = await bundle.ReAuth.VerifyAsync(
                opClass:           AuthProfileOpClasses.ProfileMutation,
                message:           PromptMessage,
                cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            if (!verdict.IsVerified) return ReAuthRequired(verdict, AuthProfileOpClasses.ProfileMutation);
            bundle.LockService?.TouchActivity();

            try
            {
                var outcome = service.Delete(id);
                return outcome switch
                {
                    AuthProfileMutationService.DeleteOutcome.NotFound nf => Results.Json(new
                    {
                        error         = "auth_profile_not_found",
                        authProfileId = nf.AuthProfileId,
                    }, statusCode: 404),
                    AuthProfileMutationService.DeleteOutcome.WriteFailed w => Results.Json(new
                    {
                        error         = "write_failed",
                        authProfileId = w.AuthProfileId,
                    }, statusCode: 500),
                    AuthProfileMutationService.DeleteOutcome.Deleted d => Results.Json(new
                    {
                        authProfileId          = d.AuthProfileId,
                        deleted                = true,
                        credentialDeleteFailed = d.CredentialDeleteFailed,
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

    // ---- Helpers ----

    private static async Task<JsonNode?> ReadJsonAsync(HttpContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var text = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(text)) return null;
            var node = JsonNode.Parse(text);
            return node is JsonObject ? node : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
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

    private static object SerializeError(AuthProfileValidationError e) => new
    {
        instancePath = e.InstancePath,
        keyword      = e.Keyword,
        message      = e.Message,
        @params      = e.Params,
    };

    // Stage 3d AuthProfileReadRoutes envelope shape parity (camelCase
    // mapping with null-able cred_man_target / cert_thumbprint /
    // cert_store / description / last_verified_*).
    private static object ToWire(AuthProfileRow row) => new
    {
        authProfileId      = row.AuthProfileId,
        name               = row.Name,
        mode               = row.Mode,
        tenantId           = row.TenantId,
        clientId           = row.ClientId,
        credManTarget      = row.CredManTarget,
        certThumbprint     = row.CertThumbprint,
        certStore          = row.CertStore,
        description        = row.Description,
        lastVerifiedAt     = row.LastVerifiedAt,
        lastVerifiedResult = row.LastVerifiedResult,
        createdAt          = row.CreatedAt,
        updatedAt          = row.UpdatedAt,
    };
}
