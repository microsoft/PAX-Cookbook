using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-A -- bundled PAX script export.
//
//   GET /api/v1/runtime/pax-script/download
//
// Mirrors Routes/Runtime.ps1:Invoke-RuntimeExportPaxScriptGet:
//   * Streams the bundled PAX script bytes as application/octet-stream.
//   * Content-Disposition: attachment; filename="PAX_Purview_Audit_Log_Processor_v<ver>.ps1"
//     (version sanitised against ^[A-Za-z0-9.\-]{1,40}$; else "unknown").
//   * Cache-Control: no-store.
//   * Access-Control-Expose-Headers: Content-Disposition so the SPA's
//     fetch() can read the filename.
//   * NO SHA-256 header. The PowerShell broker intentionally omits a
//     download-side hash header; the SPA verifies the bundled hash
//     against VERSION.json after the download completes.
public static class RuntimeDownloadRoutes
{
    public static void Register(IEndpointRouteBuilder app, PaxScriptExportReader reader)
    {
        app.MapGet("/api/v1/runtime/pax-script/download", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";

            var outcome = reader.Read();
            if (!outcome.Ok)
            {
                ctx.Response.StatusCode  = 500;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error   = outcome.Error,
                    message = outcome.Detail,
                });
                return;
            }

            ctx.Response.StatusCode  = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.Headers["Content-Disposition"] =
                "attachment; filename=\"" + outcome.Filename + "\"";
            ctx.Response.Headers["Access-Control-Expose-Headers"] = "Content-Disposition";
            ctx.Response.ContentLength = outcome.Bytes!.LongLength;
            await ctx.Response.Body.WriteAsync(outcome.Bytes);
        });
    }
}
