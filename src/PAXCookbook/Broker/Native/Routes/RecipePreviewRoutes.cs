using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-B2 -- POST /api/v1/recipes/preview.
//
// Two-branch entry point (lookup-by-id vs full draft body); the
// service handles the discriminator. The route layer only translates
// outcomes into the PS-parity envelopes:
//
//   * 200 ok                                  -> { recipeId, command, argv, extraArguments, spawn:{command,argv} }
//   * 400 invalid_json
//   * 400 validation_failed { errors: [...] }
//   * 404 not_found                           (recipe row missing or soft-deleted)
//   * 404 recipe_file_missing                 (row exists, file gone)
//   * 422 recipe_file_malformed               (file unreadable / non-object)
//   * 422 recipe_unsupported_schema_version
//   * 500 recipe_load_unknown_status          (defensive)
//
// SqliteException is mapped to 500 workspace_database_unavailable so
// a fresh workspace folder without cookbook.sqlite emits the
// established Stage 3c shape rather than a stack trace.
public static class RecipePreviewRoutes
{
    public static void Register(IEndpointRouteBuilder app, RecipePreviewService service)
    {
        app.MapPost("/api/v1/recipes/preview", async (HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);
            if (body is null)
                return Results.Json(new { error = "invalid_json" }, statusCode: 400);

            try
            {
                var outcome = service.Preview(body);
                return outcome switch
                {
                    RecipePreviewOutcome.InvalidJsonResult              =>
                        Results.Json(new { error = "invalid_json" }, statusCode: 400),

                    RecipePreviewOutcome.NotFoundResult n               =>
                        Results.Json(new { error = "not_found", recipeId = n.RecipeId }, statusCode: 404),

                    RecipePreviewOutcome.FileMissingResult f            =>
                        Results.Json(new { error = "recipe_file_missing", recipeId = f.RecipeId }, statusCode: 404),

                    RecipePreviewOutcome.FileMalformedResult m          =>
                        Results.Json(new
                        {
                            error    = "recipe_file_malformed",
                            recipeId = m.RecipeId,
                            detail   = m.Detail ?? "",
                        }, statusCode: 422),

                    RecipePreviewOutcome.UnsupportedSchemaVersionResult u =>
                        Results.Json(new
                        {
                            error                  = "recipe_unsupported_schema_version",
                            recipeId               = u.RecipeId,
                            supportedSchemaVersion = RecipeValidator.SupportedSchemaVersion,
                            detail                 = u.Detail ?? "",
                        }, statusCode: 422),

                    RecipePreviewOutcome.LoadUnknownStatusResult lus    =>
                        Results.Json(new
                        {
                            error    = "recipe_load_unknown_status",
                            recipeId = lus.RecipeId,
                            status   = lus.Status,
                        }, statusCode: 500),

                    RecipePreviewOutcome.ValidationFailedResult v       =>
                        Results.Json(new
                        {
                            error  = "validation_failed",
                            errors = v.Errors.Select(SerializeError).ToArray(),
                        }, statusCode: 400),

                    RecipePreviewOutcome.OkResult ok                    =>
                        Results.Json(new
                        {
                            recipeId       = ok.RecipeId,
                            command        = ok.Command,
                            argv           = ok.Argv,
                            extraArguments = ok.ExtraArguments,
                            spawn          = new
                            {
                                command = ok.SpawnCommand,
                                argv    = ok.SpawnArgv,
                            },
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
            // object. Mirror exactly so the route emits invalid_json
            // rather than crashing on body["foo"] access.
            return node is JsonObject ? node : null;
        }
        catch (JsonException) { return null; }
        catch (IOException)   { return null; }
    }

    private static object SerializeError(ValidationError e) => new
    {
        instancePath = e.InstancePath,
        keyword      = e.Keyword,
        message      = e.Message,
        @params      = e.Params,
    };
}
