using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-B2 -- POST /api/v1/templates/{id}/materialize.
//
// Six envelopes per Invoke-TemplateMaterialize:
//
//   * 201 created                       -> { recipeId, recipe }
//   * 404 template_not_found
//   * 412 template_incompatible         -> includes bundledPaxVersion,
//                                          minPaxScriptVersion, and a
//                                          one-element details array
//                                          carrying the AJV-shape
//                                          paxIncompatible error.
//   * 400 invalid_json
//   * 400 materialize_body_invalid      -> AJV-shape body errors.
//   * 400 materialize_recipe_invalid    -> AJV-shape recipe errors +
//                                          templateId + the new recipeId.
//
// The route layer never re-runs the materializer's validation; the
// service is the single source of truth.
public static class TemplateMaterializeRoutes
{
    public static void Register(IEndpointRouteBuilder app, TemplateMaterializerService service)
    {
        app.MapPost("/api/v1/templates/{templateId}/materialize", async (string templateId, HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);

            try
            {
                var outcome = service.Materialize(templateId, body);
                return outcome switch
                {
                    TemplateMaterializeOutcome.NotFoundResult nf        =>
                        Results.Json(new { error = "template_not_found", templateId = nf.TemplateId }, statusCode: 404),

                    TemplateMaterializeOutcome.IncompatibleResult inc   =>
                        Results.Json(new
                        {
                            error               = "template_incompatible",
                            templateId          = inc.TemplateId,
                            bundledPaxVersion   = inc.BundledPaxVersion,
                            minPaxScriptVersion = inc.MinPaxScriptVersion,
                            details             = new[] { SerializeError(inc.Detail) },
                        }, statusCode: 412),

                    TemplateMaterializeOutcome.InvalidJsonResult        =>
                        Results.Json(new { error = "invalid_json" }, statusCode: 400),

                    TemplateMaterializeOutcome.BodyInvalidResult bv     =>
                        Results.Json(new
                        {
                            error  = "materialize_body_invalid",
                            errors = bv.Errors.Select(SerializeError).ToArray(),
                        }, statusCode: 400),

                    TemplateMaterializeOutcome.RecipeInvalidResult rv   =>
                        Results.Json(new
                        {
                            error      = "materialize_recipe_invalid",
                            templateId = rv.TemplateId,
                            recipeId   = rv.RecipeId,
                            errors     = rv.Errors.Select(SerializeError).ToArray(),
                        }, statusCode: 400),

                    TemplateMaterializeOutcome.CreatedResult ok         =>
                        Results.Json(new
                        {
                            recipeId = ok.RecipeId,
                            recipe   = ok.Recipe,
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
