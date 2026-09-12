using System;
using System.Text.Json;

namespace PAXCookbook.App;

// Parsing/dispatch for the experimental Entra WAM daemon routes (T1-S2A
// authorization-boundary repair).
//
// These are the ONLY renderer-reachable experimental endpoints, and NEITHER is
// on the security-critical result path:
//   * initiate: the renderer asks the daemon to start a request; the daemon
//     mints the challenge ITSELF, and returns only an OPAQUE requestId.
//   * status: the renderer polls a bounded state (pending/approved/denied).
// The bounded RESULT is delivered to the daemon only over the native IPC channel
// (ExperimentalWamPipe); there is no HTTP result handler here, so a renderer can
// never POST an Approved outcome. Kept off the HTTP pipeline so it is
// deterministic and unit-testable; the Program.cs routes are thin adapters that
// gate on the endpoint being non-null (fully configured) and delegate here.
internal static class ExperimentalWamRoutes
{
    // GET /experimental/wam/capability — bounded capability probe (T1-S2B). The
    // renderer uses it to decide whether to render the experimental work-account
    // action. It returns ONLY { available, providerId } (or { available:false });
    // never a tenant id, client id, pipe name, salt, registration detail, account
    // identity, claim, or release-gate value. `available` is true only when the
    // caller passes an active endpoint (which the host wires only when the real
    // authenticator was compiled, the runtime is fully configured, and the native
    // pipe was created successfully). A default build always passes null here.
    internal static (int Status, object Body) HandleCapability(ExperimentalWamDaemonEndpoint? endpoint)
    {
        return endpoint is not null
            ? (200, new { available = true, providerId = "entra-wam" })
            : (200, new { available = false });
    }

    // POST /experimental/wam/initiate — start a request; returns an opaque
    // requestId. The renderer supplies only purpose; it does not and cannot
    // choose the challenge, provider, or any outcome field.
    internal static (int Status, object Body) HandleInitiate(ExperimentalWamDaemonEndpoint endpoint, JsonElement? body)
    {
        if (body is null || !TryParsePurpose(body.Value, out WamAuthPurpose purpose))
        {
            return (400, new { error = "invalid_request" });
        }

        string? requestId = endpoint.Initiate(purpose);
        if (requestId is null)
        {
            return (400, new { error = "request_not_initiated" });
        }

        return (200, new { requestId });
    }

    // GET/POST /experimental/wam/status — poll a bounded state PLUS a bounded,
    // customer-safe rejection reason. Both fields are bounded enums rendered to
    // fixed strings: NO token, raw claim, tenant/client/object id, UPN, account
    // handle, scope array, challenge, or generation can appear here. An unknown or
    // absent request fails closed (Unknown state + the fail-closed "denied" reason,
    // never a permissive one).
    internal static (int Status, object Body) HandleStatus(ExperimentalWamDaemonEndpoint endpoint, string? requestId)
    {
        ExperimentalWamStatusReport report = endpoint.GetStatusReport(requestId);
        return (200, new
        {
            state = report.State.ToString(),
            reason = WamRejectionReasonMap.ToStatusString(report.Reason),
        });
    }

    private static bool TryParsePurpose(JsonElement body, out WamAuthPurpose purpose)
    {
        purpose = WamAuthPurpose.SessionUnlock;
        string? raw = GetString(body, "purpose");
        if (string.Equals(raw, "session", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "session_unlock", StringComparison.OrdinalIgnoreCase))
        {
            purpose = WamAuthPurpose.SessionUnlock;
            return true;
        }

        return false;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
