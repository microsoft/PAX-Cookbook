using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-B1 -- recipe mutation surface:
//   POST   /api/v1/recipes           -> Invoke-RecipeCreate parity
//   PUT    /api/v1/recipes/{ulid}    -> Invoke-RecipeUpdate parity
//   DELETE /api/v1/recipes/{ulid}    -> Invoke-RecipeDelete parity
//
// All envelopes match the PowerShell broker error contract exactly:
//   * 400 invalid_json            (malformed body)
//   * 400 id_mismatch             (PUT body recipeId != URL ulid)
//   * 400 validation_failed       (server-stamp + Test-RecipeAll fail)
//   * 404 not_found               (row missing or soft-deleted)
//   * 422 recipe_file_missing
//   * 422 recipe_file_malformed
//   * 422 recipe_unsupported_schema_version
//   * 500 delete_failed           (row UPDATE...deleted_at affected != 1)
//
// The reader's SqliteWorkspaceReader handles the "cookbook.sqlite is
// missing/unreadable" case for the list/get routes; the mutation
// routes catch SqliteException explicitly and emit
// workspace_database_unavailable so a fresh workspace folder reports
// a structured error instead of a stack trace.
public static class RecipeMutationRoutes
{
    public static void Register(IEndpointRouteBuilder app, RecipeMutationService service)
    {
        // ---------- POST -----------------------------------------------------
        app.MapPost("/api/v1/recipes", async (HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);
            if (body is null)
                return Results.Json(new { error = "invalid_json" }, statusCode: 400);

            try
            {
                var outcome = service.Create(body);
                return outcome switch
                {
                    CreateOutcome.InvalidJsonResult        => Results.Json(new { error = "invalid_json" }, statusCode: 400),
                    CreateOutcome.ValidationFailedResult v => Results.Json(new
                    {
                        error  = "validation_failed",
                        errors = v.Errors.Select(SerializeError).ToArray(),
                    }, statusCode: 400),
                    CreateOutcome.CreatedResult c          => Results.Json(new
                    {
                        recipeId = c.RecipeId,
                        recipe   = c.Recipe,
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
        app.MapPut("/api/v1/recipes/{ulid}", async (HttpContext ctx, string ulid) =>
        {
            var body = await ReadJsonAsync(ctx);
            if (body is null)
                return Results.Json(new { error = "invalid_json" }, statusCode: 400);

            try
            {
                var outcome = service.Update(ulid, body);
                return outcome switch
                {
                    UpdateOutcome.NotFoundResult            => Results.Json(new { error = "not_found" }, statusCode: 404),
                    UpdateOutcome.InvalidJsonResult         => Results.Json(new { error = "invalid_json" }, statusCode: 400),
                    UpdateOutcome.IdMismatchResult m        => Results.Json(new
                    {
                        error        = "id_mismatch",
                        urlRecipeId  = m.UrlRecipeId,
                        bodyRecipeId = m.BodyRecipeId,
                    }, statusCode: 400),
                    UpdateOutcome.ValidationFailedResult v  => Results.Json(new
                    {
                        error  = "validation_failed",
                        errors = v.Errors.Select(SerializeError).ToArray(),
                    }, statusCode: 400),
                    UpdateOutcome.FileMissingResult f       => Results.Json(new
                    {
                        error    = "recipe_file_missing",
                        recipeId = f.RecipeId,
                    }, statusCode: 422),
                    UpdateOutcome.FileMalformedResult fm    => Results.Json(new
                    {
                        error    = "recipe_file_malformed",
                        recipeId = fm.RecipeId,
                        detail   = fm.Detail ?? "",
                    }, statusCode: 422),
                    UpdateOutcome.UnsupportedSchemaVersionResult u => Results.Json(new
                    {
                        error                  = "recipe_unsupported_schema_version",
                        recipeId               = u.RecipeId,
                        supportedSchemaVersion = RecipeValidator.SupportedSchemaVersion,
                        detail                 = u.Detail ?? "",
                    }, statusCode: 422),
                    UpdateOutcome.UpdatedResult upd         => Results.Json(new
                    {
                        recipeId = upd.RecipeId,
                        recipe   = upd.Recipe,
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
        app.MapDelete("/api/v1/recipes/{ulid}", (string ulid) =>
        {
            try
            {
                var outcome = service.Delete(ulid);
                return outcome switch
                {
                    DeleteOutcome.NotFoundResult           => Results.Json(new { error = "not_found" }, statusCode: 404),
                    DeleteOutcome.PersistFailureResult     => Results.Json(new { error = "delete_failed" }, statusCode: 500),
                    DeleteOutcome.DeletedResult d          => Results.Json(new
                    {
                        recipeId  = d.RecipeId,
                        deletedAt = d.DeletedAt,
                        trashPath = d.TrashPath,
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
            // PS Read-RequestJson returns null when root is not an
            // object. Mirror that so the route emits invalid_json
            // rather than crashing on body["foo"] access.
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

    private static object SerializeError(ValidationError e) => new
    {
        instancePath = e.InstancePath,
        keyword      = e.Keyword,
        message      = e.Message,
        @params      = e.Params,
    };
}
