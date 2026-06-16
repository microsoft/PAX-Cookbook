using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3f -- specialized cook-view live-tail WebSocket transport.
// Native parity with app\broker\Routes\CookLogWs.ps1.
//
//   GET (Upgrade: websocket) /api/v1/cooks/<cookId>/log/ws
//
// This is INTENTIONALLY SEPARATE from any future /ws hub port. The
// PS broker keeps these two systems separate; the native broker
// follows the same split:
//
//   /ws hub        : broker operational event transport. JSON
//                    envelope (type/cookId/data). Pub/sub. Not yet
//                    ported -- Stage 3f does not introduce it.
//   This file      : per-cook URL, one socket per mount, one
//                    FileStream per socket, opaque UTF-8 text frames
//                    carrying raw cook.log bytes. No JSON envelope,
//                    no replay, no buffering, no orchestration.
//
// Wire-format contract (FROZEN -- matches PS broker):
//
//   Server -> Client : opaque UTF-8 TEXT frames carrying bytes
//                      appended to cook.log since the last frame.
//                      Bytes 0..<EOF-at-upgrade-time> are NOT
//                      replayed; the client hydrates that prefix via
//                      GET /api/v1/cooks/<id>/log before connecting.
//   Client -> Server : no protocol. The client never sends.
//
// Pre-upgrade gate codes (parity with PS broker; HTTP status only,
// no body, because the connection has not been upgraded yet):
//
//   400  not a WebSocket upgrade request
//   400  cookId malformed
//   404  cook not found
//   409  cook is in a terminal status
//   409  cook.log file does not exist
//   400  AcceptWebSocketAsync threw
//
// Post-upgrade lifecycle is owned by CookLogTailer.
//
// DEFERRED in Stage 3f (vs. the PS broker's CookLogWs):
//
//   * Origin allow-list gate (PS returns 403). Native broker still
//     has no Test-OriginAllowed surface; Stage 3d declared bearer-
//     token + CSRF deferred, and the WS origin gate is the same
//     class of concern. The native host remains loopback-only.
//
//   * ?t=<sessionToken> query-token auth (PS returns 401). The
//     native broker's session-token surface is not yet implemented
//     (deferred to Stage 3i+ alongside the bearer-token + WebAuthn
//     work). Loopback-only mitigates the open surface for the
//     dormant-host window.
//
//   * Get-RedactedRequestUrl token-stripping logging helper. The
//     native broker does not log the request URL, so there is no
//     leak surface here yet -- when Stage 3i wires `?t=` we must
//     also wire the redactor.
//
// These deferrals are tracked verbatim in the Stage 3f record (§15
// "Security / no-secret notes").
public static class CookLogWebSocketRoutes
{
    public static void Register(
        IEndpointRouteBuilder app,
        SqliteWorkspaceReader reader,
        IHostApplicationLifetime lifetime)
    {
        if (app      is null) throw new ArgumentNullException(nameof(app));
        if (reader   is null) throw new ArgumentNullException(nameof(reader));
        if (lifetime is null) throw new ArgumentNullException(nameof(lifetime));

        var tailer = new CookLogTailer(reader);

        app.MapGet("/api/v1/cooks/{id}/log/ws", async (string id, HttpContext ctx) =>
        {
            // Pre-upgrade gates. Each returns the same HTTP status
            // code the PS broker uses -- no body, because the
            // connection is still HTTP and a JSON body cannot be
            // surfaced by the browser's WebSocket API on a failed
            // upgrade. The browser's onerror fires with whatever the
            // platform exposes; the canonical signal is the status.
            //
            // We do NOT set Cache-Control: no-store here because the
            // request is a WS upgrade and any caching layer that sees
            // it is misbehaving regardless of headers.

            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (string.IsNullOrWhiteSpace(id) || !CookIdPattern.Regex.IsMatch(id))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var row = reader.GetCookById(id);
            if (row is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!string.Equals(row.Status, "running", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }

            var logPath = ResolveCookLogPath(row.CookFolder, reader.Paths);
            if (logPath is null || !File.Exists(logPath))
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }

            // Open the cook.log handle BEFORE accepting the WebSocket
            // upgrade so the seek-to-EOF that establishes "no backlog"
            // happens before the client knows the connection is live.
            // This eliminates the otherwise unavoidable race window
            // between the client's 101 Switching Protocols arriving
            // and the tailer's first FileStream open -- in tests (and
            // in production for a fast-writing cook), bytes appended
            // in that window would be silently lost because both
            // sides treat them as "backlog the HTTP route already
            // hydrated". The PS broker tolerates this race because
            // the gap is microseconds in WinHttp; the native broker
            // closes the gap explicitly.
            FileStream fs;
            try
            {
                fs = new FileStream(
                    logPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 65536,
                    FileOptions.SequentialScan);
                fs.Position = fs.Length;
            }
            catch
            {
                // File was deleted/locked between the File.Exists
                // gate and our open. Surface the same 409 the gate
                // would have surfaced.
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }

            System.Net.WebSockets.WebSocket socket;
            try
            {
                socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            }
            catch
            {
                // Mirror PS broker: log nothing useful here (we have
                // no request-URL logger to redact through; this is
                // the only place the connection might surface a
                // token in a future stage). 400 keeps the upgrade-
                // failure shape consistent.
                try { fs.Dispose(); } catch { }
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                }
                return;
            }

            // Hand the socket + pre-opened FileStream to the tailer.
            // The host's application lifetime token cancels the loop
            // on shutdown so the task does not outlive Kestrel.
            // CookLogTailer owns both socket close and FileStream
            // dispose from this point.
            await tailer.RunAsync(socket, id, fs, lifetime.ApplicationStopping)
                .ConfigureAwait(false);
        });
    }

    // Duplicate of the private helper inside CookReadRoutes by
    // intent. Stage 3c documented the resolver shape (workspace-
    // relative "Cooks/<recipeId>/<cookId>", forward slashes, resolved
    // against WorkspaceFolderPath); the Stage 3f file-tail path needs
    // the same resolution but Stage 3f does not refactor Stage 3c.
    // The doctrine is tiny (4 lines of branching) and the duplication
    // is bounded -- if either branch ever changes, both must follow.
    private static string? ResolveCookLogPath(string cookFolder, WorkspacePaths paths)
    {
        if (string.IsNullOrWhiteSpace(cookFolder)) return null;
        string folderAbsolute;
        try
        {
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
