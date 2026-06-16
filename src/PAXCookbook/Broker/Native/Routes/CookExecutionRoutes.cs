using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3e -- native cook-start surface.
//
//   POST /api/v1/recipes/{ulid}/cook
//
// Mirrors Invoke-CookStart (app/broker/Routes/Cooks.ps1 ~line 1262)
// limited to the Stage 3e scope (see CookExecutionService for the
// per-step parity / defer matrix). The route layer is intentionally
// thin: it parses the URL parameter, delegates to the orchestrator,
// and translates the CookStartOutcome into the canonical envelope.
//
// Doctrine:
//   - No-store on every response so a stale 409 / 412 envelope does
//     not get cached by the SPA.
//   - The 201 success body is verbatim `{ cookId, recipeId, cookFolder }`
//     (parity with the PS broker, where cookFolder is absolute on
//     the wire and workspace-relative in the DB).
//   - Errors share one writer (WriteErrorAsync) so the envelope is
//     uniform across every Stage 3e failure mode.
public static class CookExecutionRoutes
{
    public static void Register(IEndpointRouteBuilder app, CookExecutionService service)
    {
        app.MapPost("/api/v1/recipes/{ulid}/cook", async (string ulid, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";

            CookStartOutcome outcome;
            try
            {
                outcome = service.StartCook(ulid);
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(ctx, new CookStartError(
                    StatusCode: StatusCodes.Status500InternalServerError,
                    Code:       "unhandled_exception",
                    Message:    "An unexpected error occurred.",
                    Details:    new { detail = ex.Message }));
                return;
            }

            if (outcome.IsSuccess)
            {
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(outcome.Response);
                return;
            }

            await WriteErrorAsync(ctx, outcome.ErrorEnvelope!);
        });
    }

    private static async Task WriteErrorAsync(HttpContext ctx, CookStartError err)
    {
        ctx.Response.StatusCode = err.StatusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsJsonAsync(new
        {
            error   = err.Code,
            message = err.Message,
            details = err.Details,
        });
    }
}
