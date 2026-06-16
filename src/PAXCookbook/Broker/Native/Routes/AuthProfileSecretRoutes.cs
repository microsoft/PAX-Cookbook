using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-C -- auth-profile credential bind/remove surface:
//   POST   /api/v1/auth/profiles/{id}/secret   -> Bind-AuthProfileSecret parity.
//   DELETE /api/v1/auth/profiles/{id}/secret   -> Remove-AuthProfileSecret parity.
//
// Both routes are re-auth gated (opClass = "secretBind" for POST,
// "secretRemove" for DELETE) -- mirrors the PS broker's separation
// of policy buckets so a Windows Hello prompt history can distinguish
// the two operations.
//
// Mode invariant: both routes require mode = AppRegistrationSecret on
// the target row. Cert-mode profiles get 422 auth_profile_mode_
// mismatch with the row's current mode in the envelope.
//
// Plaintext semantics: the bind body carries a `clientSecret` string
// (the PS broker accepts the same shape because the transport is
// loopback HTTPS). The client secret is NEVER persisted in the
// auth_profiles table -- only the CredMan target name is.
public static class AuthProfileSecretRoutes
{
    private const string BindPromptMessage   = "Authenticate to bind the auth-profile client secret.";
    private const string RemovePromptMessage = "Authenticate to remove the auth-profile client secret.";

    public static void Register(
        IEndpointRouteBuilder app,
        Stage3iCServiceBundle bundle,
        AuthProfileSecretService service)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(service);

        // ---------- POST -----------------------------------------------------
        app.MapPost("/api/v1/auth/profiles/{id}/secret", async (HttpContext ctx, string id) =>
        {
            var verdict = await bundle.ReAuth.VerifyAsync(
                opClass:           AuthProfileOpClasses.SecretBind,
                message:           BindPromptMessage,
                cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            if (!verdict.IsVerified) return ReAuthRequired(verdict, AuthProfileOpClasses.SecretBind);
            bundle.LockService?.TouchActivity();

            var body = await ReadJsonAsync(ctx);

            try
            {
                var outcome = service.Bind(id, body);
                return outcome switch
                {
                    AuthProfileSecretService.BindOutcome.NotFound nf => Results.Json(new
                    {
                        error         = "auth_profile_not_found",
                        authProfileId = nf.AuthProfileId,
                    }, statusCode: 404),
                    AuthProfileSecretService.BindOutcome.ModeMismatch m => Results.Json(new
                    {
                        error         = "auth_profile_mode_mismatch",
                        authProfileId = m.AuthProfileId,
                        currentMode   = m.CurrentMode,
                        requiredMode  = AuthProfileModes.AppRegistrationSecret,
                    }, statusCode: 422),
                    AuthProfileSecretService.BindOutcome.InvalidJson => Results.Json(new
                    {
                        error = "invalid_json",
                    }, statusCode: 400),
                    AuthProfileSecretService.BindOutcome.SecretRequired => Results.Json(new
                    {
                        error = "client_secret_required",
                    }, statusCode: 400),
                    AuthProfileSecretService.BindOutcome.SecretTooLong t => Results.Json(new
                    {
                        error     = "client_secret_too_long",
                        length    = t.Length,
                        maxLength = AuthProfileSecretService.MaxSecretLength,
                    }, statusCode: 400),
                    AuthProfileSecretService.BindOutcome.WriteFailed w => Results.Json(new
                    {
                        error  = "secret_write_failed",
                        detail = w.Detail,
                    }, statusCode: 500),
                    AuthProfileSecretService.BindOutcome.Bound b => Results.Json(new
                    {
                        authProfileId = b.AuthProfileId,
                        credManTarget = b.CredManTarget,
                        bound         = true,
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
        app.MapDelete("/api/v1/auth/profiles/{id}/secret", async (HttpContext ctx, string id) =>
        {
            var verdict = await bundle.ReAuth.VerifyAsync(
                opClass:           AuthProfileOpClasses.SecretRemove,
                message:           RemovePromptMessage,
                cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            if (!verdict.IsVerified) return ReAuthRequired(verdict, AuthProfileOpClasses.SecretRemove);
            bundle.LockService?.TouchActivity();

            try
            {
                var outcome = service.Remove(id);
                return outcome switch
                {
                    AuthProfileSecretService.RemoveOutcome.NotFound nf => Results.Json(new
                    {
                        error         = "auth_profile_not_found",
                        authProfileId = nf.AuthProfileId,
                    }, statusCode: 404),
                    AuthProfileSecretService.RemoveOutcome.ModeMismatch m => Results.Json(new
                    {
                        error         = "auth_profile_mode_mismatch",
                        authProfileId = m.AuthProfileId,
                        currentMode   = m.CurrentMode,
                        requiredMode  = AuthProfileModes.AppRegistrationSecret,
                    }, statusCode: 422),
                    AuthProfileSecretService.RemoveOutcome.WriteFailed w => Results.Json(new
                    {
                        error  = "secret_delete_failed",
                        detail = w.Detail,
                    }, statusCode: 500),
                    AuthProfileSecretService.RemoveOutcome.Removed r => Results.Json(new
                    {
                        authProfileId = r.AuthProfileId,
                        removed       = r.Existed ? "present" : "absent",
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
}
