using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3c -- read-only Templates surface backed by the static
// catalog loaded once at startup.
//
//   GET /api/v1/templates       -- list of TemplateSummary records.
//   GET /api/v1/templates/{id}  -- raw template JSON document.
//
// Templates ship inside app/templates/*.template.json. They are not
// stored in the workspace SQLite DB. The PowerShell broker loads them
// once at startup; the native broker matches that contract.
public static class TemplateReadRoutes
{
    public static void Register(IEndpointRouteBuilder app, TemplateCatalogReader catalog)
    {
        app.MapGet("/api/v1/templates", () =>
        {
            return Results.Json(new
            {
                templates = catalog.ListSummaries().Select(s => new
                {
                    templateId            = s.TemplateId,
                    templateVersion       = s.TemplateVersion,
                    templateSchemaVersion = s.TemplateSchemaVersion,
                    displayName           = s.DisplayName,
                    shortDescription      = s.ShortDescription,
                    category              = s.Category,
                    minPaxScriptVersion   = s.MinPaxScriptVersion,
                    minCookbookVersion    = s.MinCookbookVersion,
                    manualGuidanceCount   = s.ManualGuidanceCount,
                }).ToArray(),
                loadWarnings = catalog.LoadWarnings,
            });
        });

        app.MapGet("/api/v1/templates/{id}", async (string id, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-store";
                await ctx.Response.WriteAsync("{\"error\":\"template_id_required\"}");
                return;
            }
            if (!catalog.TryGetDocument(id, out var doc))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-store";
                await ctx.Response.WriteAsync("{\"error\":\"template_not_found\"}");
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            // Stream the raw root element so fields beyond the summary
            // projection (manualGuidance, inputSchema, recipeDefaults,
            // etc.) round-trip without transformation.
            await using var writer = new Utf8JsonWriter(ctx.Response.Body);
            doc.WriteTo(writer);
            await writer.FlushAsync();
        });
    }
}
