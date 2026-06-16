namespace PAXCookbook.Broker.Native.Models;

// Stage 3f -- cook-view live-tail WebSocket transport, native parity
// with app\broker\Routes\CookLogWs.ps1.
//
// The wire format is intentionally minimal: opaque UTF-8 TEXT frames
// carrying raw bytes appended to <cookFolder>\cook.log since the last
// frame. There is NO JSON envelope, NO type field, NO subscribe
// protocol -- this is a different transport from the /ws hub
// (Ws\WebSocketHub.ps1 in the PS broker) and the two systems
// DELIBERATELY do not share an abstraction. The PS broker's CookLogWs
// docstring is explicit about this; the native broker mirrors that
// design.
//
// The bytes 0..<EOF-at-upgrade-time> are NOT replayed over the socket.
// The browser hydrates that prefix via the Stage 3c whole-file route
// GET /api/v1/cooks/<id>/log; this socket only ships bytes appended
// after upgrade.

// Pre-upgrade gate outcomes. Mapped 1:1 to HTTP response status codes
// for parity with the PS broker's pre-upgrade gates -- those return
// HTTP status only (no body) because the connection has not been
// upgraded yet, so a JSON body would be useless to the browser layer.
public enum CookLogWebSocketGateResult
{
    // Upgrade succeeded; the tailer owns the socket from here.
    UpgradeAccepted = 0,

    // The request did not carry the WebSocket Upgrade headers. 400.
    NotAWebSocketRequest = 400,

    // The cook id segment is empty / malformed (does not match the
    // shared GUID-or-ULID pattern). 400.
    CookIdMalformed = 4001,

    // No row in the cooks table for the supplied id. 404.
    CookNotFound = 404,

    // Cook row exists but its status column is not 'running'. The
    // PS broker's contract: a terminal cook has no live tail; the
    // browser should call GET /api/v1/cooks/<id>/log to read the
    // final on-disk content instead. 409.
    CookIsTerminal = 409,

    // Cook row exists, status is 'running', but cook.log is not on
    // disk yet. The PS supervisor and the Stage 3e CookFolderService
    // both create cook.log on start, so this is a degenerate state
    // (corrupted workspace, manually deleted file). 409. Parity with
    // PS broker's "cook.log file does not exist" gate.
    CookLogMissing = 4091,

    // AcceptWebSocketAsync threw. The PS broker reports 400 here;
    // we mirror that. Logged but no body is returned because the
    // partial upgrade may have already written response bytes.
    UpgradeFailed = 4002,
}

// Close-status sentinels used by the per-socket tailer. These mirror
// the PS broker's CookLogTailScript:
//   NormalClosure (1000)        cook transitioned to a terminal status
//   InternalServerError (1011)  IO failure or send failure during tail
public static class CookLogStreamCloseReasons
{
    public const string CookTerminal = "cook_terminal";
    public const string IoError      = "io_error";
    public const string HostShutdown = "host_shutdown";
}

// Shared cook-id validation pattern. Matches the PS broker's
// $Script:CookIdPattern (Routes\Cooks.ps1 line 110): a canonical
// lowercase-or-uppercase 36-char GUID (8-4-4-4-12 hex with dashes) OR
// a 26-char Crockford-base32 ULID (0-9 plus A-Z minus I, L, O, U).
// Stage 3e cook starts always write GUIDs; the ULID branch exists for
// parity with the PS broker's scheduled path (Stage 3g territory).
public static class CookIdPattern
{
    // Compiled once at first use; safe to share across threads.
    public static readonly System.Text.RegularExpressions.Regex Regex =
        new(
            @"^(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9A-HJKMNP-TV-Z]{26})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
}
