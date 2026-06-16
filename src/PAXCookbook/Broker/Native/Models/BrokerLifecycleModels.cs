namespace PAXCookbook.Broker.Native.Models;

// Stage 3i-A -- broker-lifecycle wire models.
//
// CloseIntentRequest mirrors the PowerShell broker body for
//   POST /api/v1/broker/close-intent
// (Routes/BrokerCloseIntent.ps1):
//   { "intent": "app-only-close" | "stop-server" }
//
// CloseIntentMarker mirrors the JSON written to
//   <workspace>/Runtime/app-close-intent.json
// by the same handler. Field order matters because the PS broker
// emits an ordered hashtable; the native writer reproduces that
// ordering verbatim so a chef inspecting both files side-by-side
// sees a byte-identical layout (modulo Crockford line-endings).
//
// ShutdownResponse is the literal 202 body the PowerShell broker
// returns from POST /api/v1/broker/shutdown
// (Routes/BrokerShutdown.ps1).
public sealed record CloseIntentRequest(string? Intent);

public sealed record CloseIntentMarker(
    int    SchemaVersion,
    string Intent,
    string CreatedUtc,
    string ExpiresUtc,
    string WrittenUtc);

public sealed record CloseIntentResult(
    bool                  Ok,
    int                   StatusCode,
    string?               Error,
    string?               Detail,
    CloseIntentMarker?    Marker);

public sealed record ShutdownAcceptedResponse(
    bool   Ok,
    string Status,
    string Reason,
    string Message);
