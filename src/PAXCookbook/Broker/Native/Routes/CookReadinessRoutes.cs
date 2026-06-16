using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-A -- cook readiness probe.
//
//   POST /api/v1/cooks/readiness  -- accepts {recipeId, cookId?}
//                                    JSON body. Returns the full
//                                    readiness envelope (status,
//                                    summary, checks[]).
//
// PS-source parity: app/broker/Routes/Cooks.ps1 dispatches
// POST /api/v1/cooks/readiness only (any other method => 405).
// The Stage 3i discovery doc originally listed GET; the broker
// source is the source of truth and POST is what the native broker
// must implement. The Stage 3i-A record explicitly notes this
// discovery-doc correction.
public static class CookReadinessRoutes
{
    public static void Register(IEndpointRouteBuilder app, CookReadinessProbe probe)
    {
        app.MapPost("/api/v1/cooks/readiness", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";

            string?  recipeId = null;
            string?  cookId   = null;
            try
            {
                using var ms = new System.IO.MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                if (ms.Length == 0)
                {
                    await WriteErrorAsync(ctx, 400, "invalid_json",
                        "Request body is empty.");
                    return;
                }
                using var doc = JsonDocument.Parse(ms.ToArray());
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    await WriteErrorAsync(ctx, 400, "invalid_json",
                        "Request body must be a JSON object.");
                    return;
                }
                if (doc.RootElement.TryGetProperty("recipeId", out var rid) &&
                    rid.ValueKind == JsonValueKind.String)
                {
                    recipeId = rid.GetString();
                }
                if (doc.RootElement.TryGetProperty("cookId", out var cid) &&
                    cid.ValueKind == JsonValueKind.String)
                {
                    cookId = cid.GetString();
                }
            }
            catch (JsonException)
            {
                await WriteErrorAsync(ctx, 400, "invalid_json",
                    "Request body is not well-formed JSON.");
                return;
            }

            var result = probe.Probe(recipeId, cookId);
            ctx.Response.StatusCode  = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsJsonAsync(result);
        });
    }

    private static async Task WriteErrorAsync(
        HttpContext ctx, int statusCode, string code, string message)
    {
        ctx.Response.StatusCode  = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsJsonAsync(new
        {
            error   = code,
            message,
        });
    }
}
