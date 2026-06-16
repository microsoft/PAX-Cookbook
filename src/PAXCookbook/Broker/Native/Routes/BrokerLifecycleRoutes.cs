using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-A -- broker-lifecycle surface.
//
//   POST /api/v1/broker/close-intent  -- Mirrors
//        Routes/BrokerCloseIntent.ps1. Writes
//        <Runtime>/app-close-intent.json. Dispatched ABOVE the lock
//        gate (the operator may close the app even when the broker
//        is locked).
//   POST /api/v1/broker/shutdown      -- Mirrors
//        Routes/BrokerShutdown.ps1. Returns 202 BEFORE signalling
//        the host so the SPA receives the acceptance envelope.
//        Requires Unlocked broker state (gate enforced upstream).
public static class BrokerLifecycleRoutes
{
    public static void Register(
        IEndpointRouteBuilder        app,
        BrokerCloseIntentWriter      closeIntent,
        IBrokerShutdownCoordinator   shutdown)
    {
        RegisterCloseIntent(app, closeIntent);
        RegisterShutdown(app, shutdown);
    }

    // Stage 3i-A -- /broker/shutdown is mappable even when the
    // workspace runtime directory is unavailable (it does not write
    // to disk). Use this variant when /broker/close-intent must
    // remain unregistered (returns 404 for the unmatched path).
    public static void RegisterShutdownOnly(
        IEndpointRouteBuilder        app,
        IBrokerShutdownCoordinator   shutdown)
    {
        RegisterShutdown(app, shutdown);
    }

    private static void RegisterCloseIntent(
        IEndpointRouteBuilder    app,
        BrokerCloseIntentWriter  closeIntent)
    {
        app.MapPost("/api/v1/broker/close-intent", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";

            // Read at most MaxRequestBytes+1 bytes so the >1024 case
            // can be detected without buffering an arbitrarily large
            // payload.
            var max = closeIntent.MaxRequestBytes;
            byte[] body;
            try
            {
                using var ms = new MemoryStream();
                var buffer = new byte[1024];
                int read;
                long total = 0;
                while ((read = await ctx.Request.Body.ReadAsync(buffer, 0, buffer.Length, ctx.RequestAborted)) > 0)
                {
                    total += read;
                    if (total > max)
                    {
                        await WriteErrorAsync(ctx, 413, "payload_too_large",
                            "Request body exceeds " + max + " bytes.");
                        return;
                    }
                    ms.Write(buffer, 0, read);
                }
                body = ms.ToArray();
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(ctx, 500, "request_read_failed", ex.Message);
                return;
            }

            string? intent;
            try
            {
                if (body.Length == 0)
                {
                    await WriteErrorAsync(ctx, 400, "invalid_json",
                        "Request body is empty.");
                    return;
                }
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    await WriteErrorAsync(ctx, 400, "invalid_json",
                        "Request body must be a JSON object.");
                    return;
                }
                intent = doc.RootElement.TryGetProperty("intent", out var i) &&
                         i.ValueKind == JsonValueKind.String
                            ? i.GetString()
                            : null;
            }
            catch (JsonException)
            {
                await WriteErrorAsync(ctx, 400, "invalid_json",
                    "Request body is not well-formed JSON.");
                return;
            }

            var outcome = closeIntent.Write(intent);
            if (!outcome.Ok)
            {
                if (outcome.Error == "invalid_intent")
                {
                    // Parity envelope: include allowed[] so the SPA
                    // can render the canonical allowlist.
                    ctx.Response.StatusCode  = outcome.StatusCode;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.Headers.CacheControl = "no-store";
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        error   = outcome.Error,
                        message = outcome.Detail,
                        allowed = BrokerCloseIntentWriter.AllowedIntents,
                    });
                    return;
                }
                await WriteErrorAsync(ctx, outcome.StatusCode,
                    outcome.Error ?? "marker_write_failed",
                    outcome.Detail ?? string.Empty);
                return;
            }

            ctx.Response.StatusCode  = 202;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.WriteAsJsonAsync(new
            {
                ok     = true,
                intent = outcome.Marker!.Intent,
            });
        });
    }

    private static void RegisterShutdown(
        IEndpointRouteBuilder      app,
        IBrokerShutdownCoordinator shutdown)
    {
        app.MapPost("/api/v1/broker/shutdown", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var payload = new ShutdownAcceptedResponse(
                Ok:      true,
                Status:  "shutdown_initiated",
                Reason:  "operator_close_app",
                Message: "PAX Cookbook server is shutting down.");
            ctx.Response.StatusCode  = 202;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsJsonAsync(new
            {
                ok      = payload.Ok,
                status  = payload.Status,
                reason  = payload.Reason,
                message = payload.Message,
            });
            // Flush body to the wire BEFORE signalling shutdown so
            // the SPA receives the acceptance envelope. Parity with
            // BrokerShutdown.ps1 which writes the response then
            // sets $Script:ShuttingDown / stops the listener.
            await ctx.Response.Body.FlushAsync();
            shutdown.Signal("operator_close_app");
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
