using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-C -- cook control surface:
//   POST   /api/v1/cooks/{id}/stop     -> Invoke-CookStop parity. 202 Accepted.
//   POST   /api/v1/cooks/{id}/kill     -> Invoke-CookKill parity. 202 Accepted.
//   POST   /api/v1/cooks/{id}/resume   -> Invoke-CookResume parity. 201 Created.
//
// Gate matrix:
//   * STOP and KILL are NOT re-auth gated. The PS broker permits an
//     authenticated SPA session to stop/kill its own cooks without
//     re-prompting; the broker-lock state machine STILL gates these
//     operations (BrokerLockService.IsRouteAllowedWhenLocked returns
//     false for POST when locked) so a locked broker emits 423
//     lock_state_locked from the upstream middleware before this
//     route is reached.
//   * RESUME IS re-auth gated (opClass = "manualCook") because it
//     spawns a NEW cook process.
//
// Production status:
//   * STOP / KILL: the production InMemoryCookProcessRegistry is
//     empty until Stage 3j wires CookExecutionService into the
//     registry, so production returns 404 cook_not_active for every
//     real cookId. Tests inject a pre-populated FakeCookProcess
//     Registry to exercise the active path.
//   * RESUME: production returns a controlled 501 envelope via
//     DeferredCookResumeSpawner (cook_resume_spawn_deferred_native_
//     stage3i) AFTER passing all pre-spawn validation. Tests inject
//     a FakeCookResumeSpawner that returns Outcome="spawned".
public static class CookControlRoutes
{
    private const string ResumePromptMessage = "Authenticate to resume an interrupted cook.";

    public static void Register(
        IEndpointRouteBuilder app,
        Stage3iCServiceBundle bundle,
        CookControlService    service)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(service);

        // ---------- STOP -----------------------------------------------------
        app.MapPost("/api/v1/cooks/{id}/stop", (string id) =>
        {
            var outcome = service.Stop(id);
            return outcome switch
            {
                CookControlService.StopOutcome.InvalidCookId ic => Results.Json(new
                {
                    error  = "invalid_cook_id",
                    cookId = ic.CookId,
                }, statusCode: 400),
                CookControlService.StopOutcome.NotActive na => Results.Json(new
                {
                    error  = "cook_not_active",
                    cookId = na.CookId,
                }, statusCode: 404),
                CookControlService.StopOutcome.SignalFailed sf => Results.Json(new
                {
                    error  = "cook_signal_failed",
                    cookId = sf.CookId,
                }, statusCode: 500),
                CookControlService.StopOutcome.Accepted a => Results.Json(new
                {
                    cookId   = a.CookId,
                    accepted = "stop",
                }, statusCode: 202),
                _ => Results.Json(new { error = "internal_error" }, statusCode: 500),
            };
        });

        // ---------- KILL -----------------------------------------------------
        app.MapPost("/api/v1/cooks/{id}/kill", (string id) =>
        {
            var outcome = service.Kill(id);
            return outcome switch
            {
                CookControlService.KillOutcome.InvalidCookId ic => Results.Json(new
                {
                    error  = "invalid_cook_id",
                    cookId = ic.CookId,
                }, statusCode: 400),
                CookControlService.KillOutcome.NotActive na => Results.Json(new
                {
                    error  = "cook_not_active",
                    cookId = na.CookId,
                }, statusCode: 404),
                CookControlService.KillOutcome.SignalFailed sf => Results.Json(new
                {
                    error  = "cook_signal_failed",
                    cookId = sf.CookId,
                }, statusCode: 500),
                CookControlService.KillOutcome.Accepted a => Results.Json(new
                {
                    cookId   = a.CookId,
                    accepted = "kill",
                }, statusCode: 202),
                _ => Results.Json(new { error = "internal_error" }, statusCode: 500),
            };
        });

        // ---------- RESUME ---------------------------------------------------
        app.MapPost("/api/v1/cooks/{id}/resume", async (HttpContext ctx, string id) =>
        {
            var verdict = await bundle.ReAuth.VerifyAsync(
                opClass:           CookControlOpClasses.ManualCook,
                message:           ResumePromptMessage,
                cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            if (!verdict.IsVerified) return ReAuthRequired(verdict, CookControlOpClasses.ManualCook);
            bundle.LockService?.TouchActivity();

            try
            {
                var outcome = service.Resume(id);
                return outcome switch
                {
                    CookControlService.ResumeOutcome.InvalidCookId ic => Results.Json(new
                    {
                        error  = "invalid_cook_id",
                        cookId = ic.CookId,
                    }, statusCode: 400),
                    CookControlService.ResumeOutcome.NotFound nf => Results.Json(new
                    {
                        error  = "cook_not_found",
                        cookId = nf.CookId,
                    }, statusCode: 404),
                    CookControlService.ResumeOutcome.NotResumable nr => Results.Json(new
                    {
                        error  = "cook_not_resumable",
                        cookId = nr.CookId,
                        reason = nr.Reason,
                        detail = nr.Detail,
                    }, statusCode: 409),
                    CookControlService.ResumeOutcome.RecipeInvalid ri => Results.Json(new
                    {
                        error    = "recipe_invalid",
                        cookId   = ri.CookId,
                        recipeId = ri.RecipeId,
                        reason   = ri.Reason,
                    }, statusCode: 412),
                    CookControlService.ResumeOutcome.CheckpointVanished cv => Results.Json(new
                    {
                        error          = "cook_resume_checkpoint_vanished",
                        cookId         = cv.CookId,
                        checkpointPath = cv.CheckpointPath,
                    }, statusCode: 410),
                    CookControlService.ResumeOutcome.Deferred d => Results.Json(new
                    {
                        error        = d.FailureCode,
                        parentCookId = d.ParentCookId,
                        detail       = d.FailureDetail,
                    }, statusCode: 501),
                    CookControlService.ResumeOutcome.SpawnFailed sf => Results.Json(new
                    {
                        error        = sf.FailureCode,
                        parentCookId = sf.ParentCookId,
                        detail       = sf.FailureDetail,
                    }, statusCode: 500),
                    CookControlService.ResumeOutcome.Spawned sp => Results.Json(new
                    {
                        parentCookId = sp.ParentCookId,
                        cookId       = sp.NewCookId,
                        recipeId     = sp.RecipeId,
                        cookFolder   = sp.CookFolder,
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
