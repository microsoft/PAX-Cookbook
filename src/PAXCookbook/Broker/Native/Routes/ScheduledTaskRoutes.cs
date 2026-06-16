using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3g + 3h -- scheduled-task surface.
//
//   GET    /api/v1/scheduled-tasks                  -- list (full port).
//   GET    /api/v1/recipes/{ulid}/scheduled-task    -- single. Hash
//                                                      recompute happens
//                                                      when the
//                                                      Stage3hServiceBundle
//                                                      is configured;
//                                                      otherwise the
//                                                      Stage 3g honesty
//                                                      payload is
//                                                      surfaced.
//   PUT    /api/v1/recipes/{ulid}/scheduled-task    -- live when the
//                                                      Stage3hServiceBundle
//                                                      is configured;
//                                                      controlled 501
//                                                      otherwise.
//   DELETE /api/v1/recipes/{ulid}/scheduled-task    -- same gating.
//
// All responses set Cache-Control: no-store.
//
// Doctrine (Stage 3h):
//   * Re-auth uses the Windows UserConsentVerifier (Hello/PIN), NOT
//     WebAuthn. The PowerShell broker calls Invoke-WindowsReAuth
//     (Auth\WindowsReAuth.ps1); the native broker delegates to a
//     hidden one-shot pwsh sidecar that invokes the exact same PS
//     function via WindowsReAuthSidecarVerifier. The 401 envelope
//     is `{ code:'reAuthRequired', opClass:'scheduleConfig',
//     verificationResult:<verdict>, message:<verdict-default> }`,
//     bit-identical to New-BrokerLockReAuthRequiredResponse.
//   * The PUT order of checks mirrors Invoke-ScheduledTaskPut:
//     re-auth -> resolve recipe -> reject not local-manual ->
//     reject missing auth.mode -> reject auth.mode not in whitelist
//     -> reject missing auth profile -> parse body -> validate
//     recurrence -> for AppRegistrationSecret require clientSecret
//     -> CredMan write -> projection hash -> ScheduledTaskId
//     preserve/mint -> ConvertTo-Json -Compress recurrence ->
//     registrar register -> Set-ScheduledTaskRow -> 200.
//   * The DELETE order mirrors Invoke-ScheduledTaskDelete: re-auth
//     -> taskRow lookup -> 404 if missing -> registrar unregister
//     -> 502 on failure -> delete row -> 200.
//   * Recurrence JSON: ConvertTo-Json -Depth 6 -Compress produces a
//     compact JSON token; the C# serializer does the same when
//     WriteIndented=false (default).
//   * scheduledTaskId is a 26-char Crockford base32 ULID minted from
//     RNGCryptoServiceProvider, matching New-ScheduledTaskId. The
//     PS implementation reuses the recipe-id alphabet '0123456789AB
//     CDEFGHJKMNPQRSTVWXYZ'.
//   * windowsTaskName is 'PAXCookbook_' + recipeId (Get-WindowsTaskNameForRecipe).
public static class ScheduledTaskRoutes
{
    // Same ULID alphabet the PS broker uses ($Script:RecipeIdPattern
    // in Routes/Recipes.ps1 line 933).
    private static readonly Regex UlidPattern = new(
        @"^[0-9A-HJKMNP-TV-Z]{26}$",
        RegexOptions.Compiled);

    // Crockford base32 alphabet, 26-char ULID; mirrors
    // New-ScheduledTaskId in Routes\ScheduledTasks.ps1.
    private const string CrockfordBase32Alphabet =
        "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    // Mirrors $Script:ScheduledTaskPermittedAuthModes in the PS
    // broker (Routes/ScheduledTasks.ps1 line 129).
    private static readonly string[] PermittedAuthModes = new[]
    {
        "AppRegistrationSecret",
        "AppRegistrationCertificate",
    };

    // Mirrors $Script:ScheduledTaskRecurrenceKinds.
    private static readonly string[] RecurrenceKinds = new[] { "daily", "weekly" };

    public const string DeferredStalenessReason =
        "projection_hash_unavailable_in_native_broker";

    // Stage 3g registration overload -- preserves the original
    // signature so callers that have not opted into Stage 3h keep
    // their 501-fallback behavior verbatim.
    public static void Register(
        IEndpointRouteBuilder app,
        SqliteWorkspaceReader reader,
        ScheduledTaskStore store)
        => Register(app, reader, store, stage3hBundle: null);

    // Stage 3h registration overload -- when stage3hBundle is non-
    // null the route runs PUT + DELETE live and recomputes the
    // projection hash on GET. When null the Stage 3g 501 fallback
    // and projection-hash-honesty payload are preserved.
    public static void Register(
        IEndpointRouteBuilder app,
        SqliteWorkspaceReader reader,
        ScheduledTaskStore store,
        Stage3hServiceBundle? stage3hBundle)
    {
        // ---------------- GET /api/v1/scheduled-tasks ----------------
        app.MapGet("/api/v1/scheduled-tasks", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var rows = store.TryGetAll();
            if (rows is null)
            {
                return Results.Json(
                    new { error = "workspace_database_unavailable" },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Json(new
            {
                scheduledTasks = rows.Select(r => new
                {
                    scheduledTaskId      = r.ScheduledTaskId,
                    recipeId             = r.RecipeId,
                    recipeName           = r.RecipeName,
                    recipeDeletedAt      = r.RecipeDeletedAt,
                    windowsTaskName      = r.WindowsTaskName,
                    windowsTaskPath      = r.WindowsTaskPath,
                    recipeProjectionHash = r.RecipeProjectionHash,
                    paxScriptVersion     = r.PaxScriptVersion,
                    registeredAt         = r.RegisteredAt,
                    registeredByUser     = r.RegisteredByUser,
                    lastImportedCookId   = r.LastImportedCookId,
                    lastImportedAt       = r.LastImportedAt,
                    lastStaleCheckAt     = r.LastStaleCheckAt,
                    status               = r.Status,
                    createdAt            = r.CreatedAt,
                    updatedAt            = r.UpdatedAt,
                }).ToArray(),
            });
        });

        // ---------------- GET /api/v1/recipes/{id}/scheduled-task ----------------
        app.MapGet("/api/v1/recipes/{id}/scheduled-task", async (string id, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";

            if (string.IsNullOrWhiteSpace(id) || !UlidPattern.IsMatch(id))
            {
                return Results.Json(
                    new { error = "recipe_id_invalid", recipeId = id },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var recipeMeta = reader.GetRecipeById(id);
            if (recipeMeta is null)
            {
                return Results.Json(
                    new { error = "recipe_not_found", recipeId = id },
                    statusCode: StatusCodes.Status404NotFound);
            }
            if (!string.IsNullOrEmpty(recipeMeta.DeletedAt))
            {
                return Results.Json(
                    new { error = "recipe_trashed", recipeId = id },
                    statusCode: StatusCodes.Status404NotFound);
            }

            var taskRow = store.GetByRecipeId(id);
            if (taskRow is null)
            {
                var health = ScheduledTaskHealthComposer.Compose(
                    taskRow:        null,
                    currentHash:    null,
                    hashRecomputed: false,
                    staleCheckedAt: null,
                    lastTerminal:   null,
                    hasRunning:     false);
                return Results.Json(new
                {
                    registered = false,
                    scheduledTask = (object?)null,
                    health = ToWire(health),
                    staleReason = stage3hBundle is null
                        ? DeferredStalenessReason
                        : null,
                });
            }

            // V1.S06d parity: stamp a fresh last_stale_check_at on
            // every single-recipe GET. Done unconditionally because
            // the editor surfaces the timestamp even when hash
            // recompute is deferred.
            var nowIso = DateTime.UtcNow.ToString("o");
            store.UpdateStaleCheck(id, nowIso);

            string? currentHash    = null;
            bool    hashRecomputed = false;
            string? hashError      = null;
            if (stage3hBundle is not null)
            {
                var hashAttempt = await TryComposeHashAsync(
                    stage3hBundle, reader, id, ctx.RequestAborted)
                    .ConfigureAwait(false);
                hashRecomputed = hashAttempt.Recomputed;
                currentHash    = hashAttempt.Hash;
                hashError      = hashAttempt.Error;
            }

            var lastTerminal = store.GetLastTerminalCook(taskRow.ScheduledTaskId);
            var hasRunning   = store.HasRunningCook(taskRow.ScheduledTaskId);

            var healthRegistered = ScheduledTaskHealthComposer.Compose(
                taskRow:        taskRow,
                currentHash:    currentHash,
                hashRecomputed: hashRecomputed,
                staleCheckedAt: nowIso,
                lastTerminal:   lastTerminal,
                hasRunning:     hasRunning);

            string? staleReason = null;
            if (stage3hBundle is null)
            {
                staleReason = DeferredStalenessReason;
            }
            else if (hashError is not null)
            {
                staleReason = "projection_hash_recompute_failed";
            }
            else if (hashRecomputed && currentHash is not null
                && !string.Equals(currentHash, taskRow.RecipeProjectionHash,
                    StringComparison.Ordinal))
            {
                staleReason = "projection_changed";
            }
            else if (hashRecomputed
                && !string.Equals(taskRow.PaxScriptVersion,
                    stage3hBundle.PaxScriptVersion,
                    StringComparison.Ordinal))
            {
                staleReason = "pax_version_changed";
            }

            return Results.Json(new
            {
                registered = true,
                scheduledTask = new
                {
                    scheduledTaskId      = taskRow.ScheduledTaskId,
                    recipeId             = taskRow.RecipeId,
                    windowsTaskName      = taskRow.WindowsTaskName,
                    windowsTaskPath      = taskRow.WindowsTaskPath,
                    recipeProjectionHash = taskRow.RecipeProjectionHash,
                    paxScriptVersion     = taskRow.PaxScriptVersion,
                    registeredAt         = taskRow.RegisteredAt,
                    registeredByUser     = taskRow.RegisteredByUser,
                    lastImportedCookId   = taskRow.LastImportedCookId,
                    lastImportedAt       = taskRow.LastImportedAt,
                    lastStaleCheckAt     = nowIso,
                    status               = taskRow.Status,
                    createdAt            = taskRow.CreatedAt,
                    updatedAt            = taskRow.UpdatedAt,
                },
                health      = ToWire(healthRegistered),
                staleReason = staleReason,
            });
        });

        // ---------------- PUT /api/v1/recipes/{id}/scheduled-task ----------------
        app.MapMethods("/api/v1/recipes/{id}/scheduled-task", new[] { "PUT" },
            async (string id, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";

            if (stage3hBundle is null)
            {
                return Results.Json(new
                {
                    error    = "scheduled_task_put_deferred",
                    recipeId = id,
                    message  = "Scheduled-task PUT is deferred in the native broker until Stage 3h lands the per-operation Windows re-auth verifier, the projection-hash composer, and the Windows Credential Manager writer.",
                    deferred = new[]
                    {
                        "windows_reauth",
                        "projection_hash",
                        "credential_manager_write",
                    },
                    plannedStage = "3h",
                },
                statusCode: StatusCodes.Status501NotImplemented);
            }

            return await HandlePutAsync(
                ctx, id, reader, store, stage3hBundle).ConfigureAwait(false);
        });

        // ---------------- DELETE /api/v1/recipes/{id}/scheduled-task ----------------
        app.MapMethods("/api/v1/recipes/{id}/scheduled-task", new[] { "DELETE" },
            async (string id, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";

            if (stage3hBundle is null)
            {
                return Results.Json(new
                {
                    error    = "scheduled_task_delete_deferred",
                    recipeId = id,
                    message  = "Scheduled-task DELETE is deferred in the native broker until Stage 3h lands the per-operation Windows re-auth verifier.",
                    deferred = new[]
                    {
                        "windows_reauth",
                    },
                    plannedStage = "3h",
                },
                statusCode: StatusCodes.Status501NotImplemented);
            }

            return await HandleDeleteAsync(
                ctx, id, store, stage3hBundle).ConfigureAwait(false);
        });
    }

    // ============================================================
    //  PUT handler (live, gated by Stage3hServiceBundle).
    // ============================================================
    private static async Task<IResult> HandlePutAsync(
        HttpContext ctx,
        string recipeId,
        SqliteWorkspaceReader reader,
        ScheduledTaskStore store,
        Stage3hServiceBundle bundle)
    {
        const string PromptMessage =
            "Verify to register or update the Windows Scheduled Task for this recipe.";

        // ---- 1. Re-auth FIRST (mirrors PS Invoke-ScheduledTaskPut). ----
        if (string.IsNullOrWhiteSpace(recipeId) || !UlidPattern.IsMatch(recipeId))
        {
            // Even bogus ids are re-auth-gated by the PS broker; we
            // match by performing the re-auth before any id-shape
            // check would surface. The router matches all 1+ char
            // values here, so a malformed id WILL reach this branch
            // -- enforce verification first.
        }

        var verdict = await bundle.ReAuth.VerifyAsync(
            opClass:    "scheduleConfig",
            message:    PromptMessage,
            cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
        if (!verdict.IsVerified)
        {
            return ReAuthRequired(verdict);
        }
        bundle.LockService?.TouchActivity();

        // ---- 2. recipe id shape ----
        if (string.IsNullOrWhiteSpace(recipeId) || !UlidPattern.IsMatch(recipeId))
        {
            return Results.Json(
                new { error = "recipe_id_invalid", recipeId },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // ---- 3. recipe row + file load (parity with Resolve-ScheduledTaskRecipeContext). ----
        var recipeMeta = reader.GetRecipeById(recipeId);
        if (recipeMeta is null)
        {
            return Results.Json(
                new { error = "recipe_not_found", recipeId,
                    detail = new { recipeId } },
                statusCode: StatusCodes.Status404NotFound);
        }
        if (!string.IsNullOrEmpty(recipeMeta.DeletedAt))
        {
            return Results.Json(
                new { error = "recipe_trashed", recipeId,
                    detail = new { recipeId } },
                statusCode: StatusCodes.Status404NotFound);
        }

        var loaded = bundle.RecipeReader.Load(recipeId);
        if (loaded.Status != RecipeFileReadStatus.Ok)
        {
            return Results.Json(
                new { error = "recipe_invalid", recipeId,
                    detail = new { loaderStatus = LoaderStatusToken(loaded.Status), detail = loaded.Detail } },
                statusCode: StatusCodes.Status412PreconditionFailed);
        }

        if (loaded.RawJson is null)
        {
            return Results.Json(
                new { error = "recipe_invalid", recipeId,
                    detail = new { loaderStatus = "missing_recipe_payload" } },
                statusCode: StatusCodes.Status412PreconditionFailed);
        }

        JsonDocument recipeDoc;
        try
        {
            recipeDoc = JsonDocument.Parse(loaded.RawJson);
        }
        catch (JsonException ex)
        {
            return Results.Json(
                new { error = "recipe_invalid", recipeId,
                    detail = new { loaderStatus = "malformed", detail = ex.Message } },
                statusCode: StatusCodes.Status412PreconditionFailed);
        }

        using (recipeDoc)
        {
            var recipeJson = recipeDoc.RootElement;

            // ---- 4. executionMode gate. ----
            var execMode = loaded.ExecutionMode;
            if (string.IsNullOrWhiteSpace(execMode)) execMode = "local-manual";
            if (!string.Equals(execMode, "local-manual", StringComparison.Ordinal))
            {
                return Results.Json(new
                {
                    error          = "recipe_not_local_manual",
                    recipeId,
                    executionMode  = execMode,
                },
                statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            // ---- 5. recipe.auth.mode gate. ----
            JsonElement authObj = default;
            var hasAuthObj = TryReadObject(recipeJson, "auth", out authObj)
                && authObj.ValueKind == JsonValueKind.Object;
            var authMode = loaded.AuthMode;
            if (!hasAuthObj || string.IsNullOrWhiteSpace(authMode))
            {
                return Results.Json(new
                {
                    error    = "recipe_invalid",
                    recipeId,
                    detail   = "recipe is missing auth.mode",
                },
                statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            if (Array.IndexOf(PermittedAuthModes, authMode) < 0)
            {
                return Results.Json(new
                {
                    error    = "recipe_auth_unsupported",
                    recipeId,
                    authMode,
                    allowed  = PermittedAuthModes,
                },
                statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            // ---- 6. auth profile resolution. ----
            var authProfileId = TryReadString(authObj, "authProfileId");
            AuthProfileRow? authProfile = null;
            if (!string.IsNullOrWhiteSpace(authProfileId))
            {
                authProfile = reader.GetAuthProfileById(authProfileId!);
            }
            if (authProfile is null)
            {
                return Results.Json(new
                {
                    error         = "auth_profile_missing",
                    recipeId,
                    authProfileId = authProfileId,
                },
                statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            return await HandlePutBodyAsync(
                ctx, recipeId, store, bundle,
                authMode!, authProfile)
                .ConfigureAwait(false);
        }
    }

    private static async Task<IResult> HandlePutBodyAsync(
        HttpContext ctx,
        string recipeId,
        ScheduledTaskStore store,
        Stage3hServiceBundle bundle,
        string authMode,
        AuthProfileRow authProfile)
    {
        // ---- 7. parse body. ----
        JsonDocument? bodyDoc;
        try
        {
            bodyDoc = await JsonDocument.ParseAsync(
                ctx.Request.Body, default, ctx.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.Json(new
            {
                error  = "invalid_json",
                detail = "request body must be { recurrence: { kind, hour, minute, daysOfWeek? }, clientSecret? }",
            },
            statusCode: StatusCodes.Status400BadRequest);
        }

        using (bodyDoc)
        {
            var bodyRoot = bodyDoc.RootElement;
            if (bodyRoot.ValueKind != JsonValueKind.Object)
            {
                return Results.Json(new
                {
                    error  = "invalid_json",
                    detail = "request body must be a JSON object",
                },
                statusCode: StatusCodes.Status400BadRequest);
            }

            var bv = ValidatePutBody(bodyRoot);
            if (!bv.Ok)
            {
                return Results.Json(new
                {
                    error  = "invalid_recurrence",
                    errors = bv.Errors,
                },
                statusCode: StatusCodes.Status400BadRequest);
            }

            // ---- 8. SEC-A: AppRegistrationSecret requires clientSecret every PUT. ----
            if (string.Equals(authMode, "AppRegistrationSecret", StringComparison.Ordinal)
                && !bv.SecretPresent)
            {
                return Results.Json(new
                {
                    error          = "auth_profile_secret_missing",
                    recipeId,
                    authProfileId  = authProfile.AuthProfileId,
                    detail         = "AppRegistrationSecret scheduled-task PUT requires 'clientSecret' in the request body (one-shot, not stored). Cookbook rebinds the secret to Windows Credential Manager on every PUT.",
                },
                statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            // ---- 9. CredMan write. ----
            var secretRebound = false;
            if (string.Equals(authMode, "AppRegistrationSecret", StringComparison.Ordinal))
            {
                var clientSecret = bv.ClientSecretValue ?? string.Empty;
                try
                {
                    bundle.CredStore.Write(authProfile.AuthProfileId, clientSecret);
                    secretRebound = true;
                }
                catch (Exception ex)
                {
                    return Results.Json(new
                    {
                        error  = "secret_write_failed",
                        detail = ex.Message,
                    },
                    statusCode: StatusCodes.Status500InternalServerError);
                }
            }

            // ---- 10. projection hash. ----
            var recipeFilePath = bundle.RecipeReader.ResolvePath(recipeId);
            var hashResult = await bundle.HashComposer.ComposeAsync(
                recipeFilePath:   recipeFilePath,
                paxScriptPath:    bundle.PaxScriptPath,
                authProfile:      authProfile,
                executionMode:    bundle.ExecutionMode,
                paxScriptVersion: bundle.PaxScriptVersion,
                cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            if (!hashResult.Ok || hashResult.Sha256Hex is null)
            {
                return Results.Json(new
                {
                    error  = "projection_failed",
                    detail = hashResult.Error ?? "unknown_projection_failure",
                },
                statusCode: StatusCodes.Status500InternalServerError);
            }
            var projectionHash = hashResult.Sha256Hex;

            // ---- 11. ScheduledTaskId: preserve existing or mint new. ----
            var existing = store.GetByRecipeId(recipeId);
            var scheduledTaskId = existing?.ScheduledTaskId ?? NewScheduledTaskId();
            var windowsTaskName = "PAXCookbook_" + recipeId;

            // ---- 12. registrar -> register. ----
            var recurrenceJson = JsonSerializer.Serialize(bv.NormalizedRecurrence);
            var registrarReq = new ScheduledTaskRegistrarRequest(
                Action:          "register",
                RecipeId:        recipeId,
                ScheduledTaskId: scheduledTaskId,
                WorkspacePath:   bundle.WorkspacePath,
                RecurrenceJson:  recurrenceJson);

            var registrarResult = await bundle.Registrar.InvokeAsync(
                registrarReq, ctx.RequestAborted).ConfigureAwait(false);

            if (registrarResult.ExitCode != 0)
            {
                return Results.Json(new
                {
                    error            = "registrar_failed",
                    recipeId,
                    scheduledTaskId,
                    exitCode         = registrarResult.ExitCode,
                    durationMs       = registrarResult.DurationMs,
                    stdout           = registrarResult.Stdout,
                    stderr           = registrarResult.Stderr,
                    logPath          = registrarResult.LogPath,
                },
                statusCode: StatusCodes.Status502BadGateway);
            }

            // ---- 13. DB upsert. ----
            var nowIso = DateTime.UtcNow.ToString("o");
            try
            {
                store.Upsert(
                    scheduledTaskId:      scheduledTaskId,
                    recipeId:             recipeId,
                    windowsTaskName:      windowsTaskName,
                    windowsTaskPath:      bundle.ScheduledTaskFolderPath,
                    recipeProjectionHash: projectionHash,
                    paxScriptVersion:     bundle.PaxScriptVersion,
                    nowIso:               nowIso,
                    registeredByUser:     bundle.RegisteredByUser);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error            = "db_write_failed",
                    recipeId,
                    scheduledTaskId,
                    detail           = ex.Message,
                },
                statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Json(new
            {
                ok                    = true,
                recipeId,
                scheduledTaskId,
                windowsTaskName,
                windowsTaskPath       = bundle.ScheduledTaskFolderPath,
                recipeProjectionHash  = projectionHash,
                paxScriptVersion      = bundle.PaxScriptVersion,
                registeredAt          = nowIso,
                secretRebound,
                registrarDurationMs   = registrarResult.DurationMs,
            });
        }
    }

    // ============================================================
    //  DELETE handler (live, gated by Stage3hServiceBundle).
    // ============================================================
    private static async Task<IResult> HandleDeleteAsync(
        HttpContext ctx,
        string recipeId,
        ScheduledTaskStore store,
        Stage3hServiceBundle bundle)
    {
        const string PromptMessage =
            "Verify to unregister the Windows Scheduled Task for this recipe.";

        var verdict = await bundle.ReAuth.VerifyAsync(
            opClass:    "scheduleConfig",
            message:    PromptMessage,
            cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
        if (!verdict.IsVerified)
        {
            return ReAuthRequired(verdict);
        }
        bundle.LockService?.TouchActivity();

        if (string.IsNullOrWhiteSpace(recipeId) || !UlidPattern.IsMatch(recipeId))
        {
            return Results.Json(
                new { error = "recipe_id_invalid", recipeId },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // DELETE deliberately allows trashed-recipe cleanup, parity
        // with the PS broker. The 404 fires only when there is no
        // scheduled_tasks row at all (taskRow, NOT recipe row).
        var taskRow = store.GetByRecipeId(recipeId);
        if (taskRow is null)
        {
            return Results.Json(
                new { error = "task_not_found", recipeId },
                statusCode: StatusCodes.Status404NotFound);
        }

        var registrarReq = new ScheduledTaskRegistrarRequest(
            Action:          "unregister",
            RecipeId:        recipeId,
            ScheduledTaskId: taskRow.ScheduledTaskId,
            WorkspacePath:   bundle.WorkspacePath,
            RecurrenceJson:  null);

        var registrarResult = await bundle.Registrar.InvokeAsync(
            registrarReq, ctx.RequestAborted).ConfigureAwait(false);

        if (registrarResult.ExitCode != 0)
        {
            return Results.Json(new
            {
                error            = "registrar_failed",
                recipeId,
                scheduledTaskId  = taskRow.ScheduledTaskId,
                exitCode         = registrarResult.ExitCode,
                durationMs       = registrarResult.DurationMs,
                stdout           = registrarResult.Stdout,
                stderr           = registrarResult.Stderr,
                logPath          = registrarResult.LogPath,
            },
            statusCode: StatusCodes.Status502BadGateway);
        }

        try
        {
            store.DeleteByRecipeId(recipeId);
        }
        catch (Exception ex)
        {
            return Results.Json(new
            {
                error            = "db_delete_failed",
                recipeId,
                scheduledTaskId  = taskRow.ScheduledTaskId,
                detail           = ex.Message,
            },
            statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Json(new
        {
            ok                  = true,
            recipeId,
            scheduledTaskId     = taskRow.ScheduledTaskId,
            registrarDurationMs = registrarResult.DurationMs,
        });
    }

    // ============================================================
    //  Helpers
    // ============================================================

    // Hash-recompute attempt for the GET single-recipe handler.
    // Failure paths are swallowed and surfaced via the staleReason
    // field on the response so the editor can render the correct
    // state without raising 500.
    private static async Task<(bool Recomputed, string? Hash, string? Error)>
        TryComposeHashAsync(
            Stage3hServiceBundle bundle,
            SqliteWorkspaceReader reader,
            string recipeId,
            CancellationToken cancellationToken)
    {
        var loaded = bundle.RecipeReader.Load(recipeId);
        if (loaded.Status != RecipeFileReadStatus.Ok || loaded.RawJson is null)
        {
            return (Recomputed: false, Hash: null,
                Error: "recipe_file_unavailable: " + LoaderStatusToken(loaded.Status));
        }

        AuthProfileRow? authProfile = null;
        try
        {
            using var doc = JsonDocument.Parse(loaded.RawJson);
            if (TryReadObject(doc.RootElement, "auth", out var authObj)
                && authObj.ValueKind == JsonValueKind.Object)
            {
                var authProfileId = TryReadString(authObj, "authProfileId");
                if (!string.IsNullOrWhiteSpace(authProfileId))
                {
                    authProfile = reader.GetAuthProfileById(authProfileId!);
                }
            }
        }
        catch (JsonException ex)
        {
            return (Recomputed: false, Hash: null,
                Error: "recipe_json_parse_failed: " + ex.Message);
        }

        var recipeFilePath = bundle.RecipeReader.ResolvePath(recipeId);
        var result = await bundle.HashComposer.ComposeAsync(
            recipeFilePath:    recipeFilePath,
            paxScriptPath:     bundle.PaxScriptPath,
            authProfile:       authProfile,
            executionMode:     bundle.ExecutionMode,
            paxScriptVersion:  bundle.PaxScriptVersion,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Ok && result.Sha256Hex is not null)
        {
            return (Recomputed: true, Hash: result.Sha256Hex, Error: null);
        }
        return (Recomputed: false, Hash: null, Error: result.Error);
    }

    // Crockford base32 26-char ULID minted from a CSPRNG. Mirrors
    // New-ScheduledTaskId in Routes\ScheduledTasks.ps1.
    internal static string NewScheduledTaskId()
    {
        var buf = new byte[26];
        RandomNumberGenerator.Fill(buf);
        var sb = new StringBuilder(26);
        for (int i = 0; i < buf.Length; i++)
        {
            sb.Append(CrockfordBase32Alphabet[buf[i] & 0x1F]);
        }
        return sb.ToString();
    }

    // ----- Re-auth 401 envelope (mirrors New-BrokerLockReAuthRequiredResponse) -----

    private static IResult ReAuthRequired(WindowsReAuthVerdict verdict) =>
        Results.Json(new
        {
            code               = "reAuthRequired",
            opClass            = "scheduleConfig",
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

    // ----- PUT body validator (mirrors Test-ScheduledTaskPutBody) -----

    private sealed record AjvError(string instancePath, string keyword, string message, object @params);

    private sealed record PutBodyValidation(
        bool Ok,
        IReadOnlyList<AjvError> Errors,
        Dictionary<string, object>? NormalizedRecurrence,
        bool SecretPresent,
        string? ClientSecretValue);

    private static PutBodyValidation ValidatePutBody(JsonElement body)
    {
        var errors = new List<AjvError>();
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new AjvError("", "type", "request body must be a JSON object", new { }));
            return new PutBodyValidation(false, errors, null, false, null);
        }

        if (!body.TryGetProperty("recurrence", out var recEl)
            || recEl.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new AjvError("/recurrence", "required",
                "must have property 'recurrence'", new { }));
            return new PutBodyValidation(false, errors, null, false, null);
        }
        if (recEl.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new AjvError("/recurrence", "type",
                "recurrence must be an object", new { }));
            return new PutBodyValidation(false, errors, null, false, null);
        }

        string? kind = null;
        if (!recEl.TryGetProperty("kind", out var kindEl))
        {
            errors.Add(new AjvError("/recurrence/kind", "required",
                "recurrence must have property 'kind'", new { }));
        }
        else
        {
            kind = kindEl.ValueKind == JsonValueKind.String
                ? kindEl.GetString() : null;
            if (kind is null || Array.IndexOf(RecurrenceKinds, kind) < 0)
            {
                errors.Add(new AjvError("/recurrence/kind", "enum",
                    "recurrence.kind must be one of: "
                        + string.Join(", ", RecurrenceKinds),
                    new { allowed = RecurrenceKinds }));
                kind = null;
            }
        }

        int? hour = null;
        if (!recEl.TryGetProperty("hour", out var hourEl))
        {
            errors.Add(new AjvError("/recurrence/hour", "required",
                "recurrence must have property 'hour'", new { }));
        }
        else if (!TryGetInt(hourEl, out var h) || h < 0 || h > 23)
        {
            errors.Add(new AjvError("/recurrence/hour", "range",
                "recurrence.hour must be an integer in [0, 23]", new { }));
        }
        else
        {
            hour = h;
        }

        int? minute = null;
        if (!recEl.TryGetProperty("minute", out var minEl))
        {
            errors.Add(new AjvError("/recurrence/minute", "required",
                "recurrence must have property 'minute'", new { }));
        }
        else if (!TryGetInt(minEl, out var m) || m < 0 || m > 59)
        {
            errors.Add(new AjvError("/recurrence/minute", "range",
                "recurrence.minute must be an integer in [0, 59]", new { }));
        }
        else
        {
            minute = m;
        }

        int[]? daysOfWeek = null;
        if (string.Equals(kind, "weekly", StringComparison.Ordinal))
        {
            if (!recEl.TryGetProperty("daysOfWeek", out var dowEl))
            {
                errors.Add(new AjvError("/recurrence/daysOfWeek", "required",
                    "weekly recurrence must specify daysOfWeek", new { }));
            }
            else
            {
                if (dowEl.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new AjvError("/recurrence/daysOfWeek", "length",
                        "daysOfWeek must contain 1..7 entries", new { }));
                }
                else
                {
                    var len = dowEl.GetArrayLength();
                    if (len < 1 || len > 7)
                    {
                        errors.Add(new AjvError("/recurrence/daysOfWeek", "length",
                            "daysOfWeek must contain 1..7 entries", new { }));
                    }
                    else
                    {
                        var dows = new List<int>(len);
                        var bad  = false;
                        foreach (var dEl in dowEl.EnumerateArray())
                        {
                            if (!TryGetInt(dEl, out var di) || di < 0 || di > 6)
                            {
                                errors.Add(new AjvError(
                                    "/recurrence/daysOfWeek", "range",
                                    "daysOfWeek entries must be integers in [0, 6] (0 = Sunday)",
                                    new { }));
                                bad = true;
                                break;
                            }
                            if (!dows.Contains(di)) dows.Add(di);
                        }
                        if (!bad) daysOfWeek = dows.ToArray();
                    }
                }
            }
        }

        var secretPresent = false;
        string? secretValue = null;
        if (body.TryGetProperty("clientSecret", out var csEl)
            && csEl.ValueKind == JsonValueKind.String)
        {
            var s = csEl.GetString();
            if (!string.IsNullOrEmpty(s))
            {
                secretPresent = true;
                secretValue   = s;
            }
        }

        if (errors.Count != 0)
        {
            return new PutBodyValidation(false, errors, null,
                secretPresent, secretValue);
        }

        var normalized = new Dictionary<string, object>
        {
            ["kind"]   = kind!,
            ["hour"]   = hour!.Value,
            ["minute"] = minute!.Value,
        };
        if (string.Equals(kind, "weekly", StringComparison.Ordinal))
        {
            normalized["daysOfWeek"] = daysOfWeek!;
        }
        return new PutBodyValidation(true, errors, normalized,
            secretPresent, secretValue);
    }

    // Mirrors the PS broker's loaderStatus string in the
    // recipe_invalid response detail.
    private static string LoaderStatusToken(RecipeFileReadStatus status) =>
        status switch
        {
            RecipeFileReadStatus.Ok                       => "ok",
            RecipeFileReadStatus.Missing                  => "missing",
            RecipeFileReadStatus.Malformed                => "malformed",
            RecipeFileReadStatus.UnsupportedSchemaVersion => "unsupported_schema_version",
            _                                              => "unknown",
        };

    private static bool TryGetInt(JsonElement el, out int value)
    {
        value = 0;
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                if (el.TryGetInt32(out var n)) { value = n; return true; }
                return false;
            case JsonValueKind.String:
                return int.TryParse(el.GetString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
            default:
                return false;
        }
    }

    // ----- recipe JSON helpers (the recipe payload is JsonElement) -----

    private static string? TryReadString(JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(property, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static bool TryReadObject(JsonElement obj, string property,
        out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (!obj.TryGetProperty(property, out var el)) return false;
        value = el;
        return el.ValueKind == JsonValueKind.Object;
    }

    private static object ToWire(Models.ScheduledTaskHealth h) => new
    {
        status                   = h.Status,
        stale                    = h.Stale,
        projectionHashCurrent    = h.ProjectionHashCurrent,
        projectionHashRegistered = h.ProjectionHashRegistered,
        staleProjectionCheckedAt = h.StaleProjectionCheckedAt,
        lastImportedCookId       = h.LastImportedCookId,
        lastImportedAt           = h.LastImportedAt,
        lastTerminalCookId       = h.LastTerminalCookId,
        lastTerminalStatus       = h.LastTerminalStatus,
        lastTerminalErrorClass   = h.LastTerminalErrorClass,
        lastTerminalAt           = h.LastTerminalAt,
        message                  = h.Message,
    };
}
