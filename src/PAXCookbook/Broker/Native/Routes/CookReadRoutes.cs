using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3c -- read-only Cooks surface.
//
//   GET /api/v1/cooks            -- DB-row list. The PS broker
//                                   (Invoke-CooksList) enriches each
//                                   row with recipe-snapshot.json,
//                                   cook-context.json, artifact
//                                   rollup, and a Test-CookResumable
//                                   probe. All of that is deferred --
//                                   see Stage 3c record.
//   GET /api/v1/cooks/{id}       -- DB-row metadata. PS broker
//                                   (Invoke-CookGet) returns a large
//                                   enriched payload (snapshot,
//                                   context, command, sentinels,
//                                   artifacts, paxSummary, metrics,
//                                   etc.). Deferred. Native broker
//                                   surfaces only the SQLite row plus
//                                   sentinel `cookEnrichment`
//                                   indicating deferral.
//   GET /api/v1/cooks/{id}/log   -- Whole-file read of
//                                   <cookFolder>\cook.log with
//                                   shared read open and no-store
//                                   cache header. Cook folder is
//                                   taken from the SQLite cook row
//                                   verbatim; the PS broker's
//                                   Resolve-CookFolder relocation
//                                   logic is deferred.
public static class CookReadRoutes
{
    public static void Register(IEndpointRouteBuilder app, SqliteWorkspaceReader reader)
    {
        app.MapGet("/api/v1/cooks", () =>
        {
            var rows = reader.TryListCooks();
            if (rows is null)
            {
                return Results.Json(new
                {
                    error = "workspace_database_unavailable",
                },
                statusCode: StatusCodes.Status500InternalServerError);
            }
            return Results.Json(new
            {
                cooks = rows.Select(ToWire).ToArray(),
            });
        });

        app.MapGet("/api/v1/cooks/{id}", (string id) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.Json(new { error = "cook_id_required" },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var row = reader.GetCookById(id);
            if (row is null)
            {
                return Results.Json(new { error = "cook_not_found", cookId = id },
                    statusCode: StatusCodes.Status404NotFound);
            }
            return Results.Json(new
            {
                cook = ToWire(row),
                cookEnrichment = new
                {
                    deferred = true,
                    deferredReason = "stage_3c_metadata_only",
                    plannedStage = "3d_or_later",
                },
            });
        });

        app.MapGet("/api/v1/cooks/{id}/log", async (string id, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-store";
                await ctx.Response.WriteAsync("{\"error\":\"cook_id_required\"}");
                return;
            }

            var row = reader.GetCookById(id);
            if (row is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-store";
                await ctx.Response.WriteAsync("{\"error\":\"cook_not_found\"}");
                return;
            }

            var logPath = ResolveCookLogPath(row.CookFolder, reader.Paths);
            if (logPath is null || !File.Exists(logPath))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-store";
                await ctx.Response.WriteAsync("{\"error\":\"cook_log_not_found\"}");
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";

            // Shared-read open mirrors the PowerShell broker:
            // FileMode.Open + FileAccess.Read + FileShare.ReadWrite
            // so an in-flight cook process holding the log open for
            // append does not block the request.
            await using var fs = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            await fs.CopyToAsync(ctx.Response.Body);
        });
    }

    private static object ToWire(CookRow r) => new
    {
        cookId           = r.CookId,
        recipeId         = r.RecipeId,
        status           = r.Status,
        exitCode         = r.ExitCode,
        pid              = r.Pid,
        cookFolder       = r.CookFolder,
        paxScriptPath    = r.PaxScriptPath,
        paxScriptVersion = r.PaxScriptVersion,
        trigger          = r.Trigger,
        startedAt        = r.StartedAt,
        finishedAt       = r.FinishedAt,
        durationSeconds  = r.DurationSeconds,
        errorClass       = r.ErrorClass,
        errorMessage     = r.ErrorMessage,
        createdAt        = r.CreatedAt,
        updatedAt        = r.UpdatedAt,
        summaryPath      = r.SummaryPath,
        parentCookId     = r.ParentCookId,
    };

    // Resolve <cookFolder>\cook.log. If the stored cook_folder is
    // relative (the historical layout under earlier broker versions),
    // resolve it against the workspace's Cooks/ directory. The PS
    // broker's Resolve-CookFolder also handles foreign-prefix
    // relocation (workspace moved or restored); that branch is
    // deferred -- if the stored absolute path doesn't exist on this
    // machine, the route returns cook_log_not_found.
    private static string? ResolveCookLogPath(string cookFolder, WorkspacePaths paths)
    {
        if (string.IsNullOrWhiteSpace(cookFolder)) return null;
        string folderAbsolute;
        try
        {
            // Stage 3e parity with CookFolderService.ToWorkspaceRelative:
            // the stored relative form is "Cooks/<recipeId>/<cookId>"
            // (workspace-relative, forward slashes), so resolve it
            // against the workspace root, not CooksDir.
            folderAbsolute = Path.IsPathFullyQualified(cookFolder)
                ? cookFolder
                : Path.Combine(paths.WorkspaceFolderPath, cookFolder);
        }
        catch
        {
            return null;
        }
        return Path.Combine(folderAbsolute, "cook.log");
    }
}
