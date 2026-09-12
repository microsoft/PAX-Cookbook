using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PAXCookbook.App;

// Window-side experimental WAM message routing (Track 1 / T1-S2A native
// request-binding + restart-continuity repair).
//
// The renderer message is requestId-ONLY. It may carry only the message type and
// the opaque daemon-issued requestId; if it also carries any security-relevant
// field (purpose, recipe, generation, provider, or acquisition mode) the
// envelope is REJECTED (strict, so a stale or hostile caller fails visibly).
// Flow:
//   1. The authenticated SPA calls the daemon initiate route and receives an
//      opaque requestId (no security fields).
//   2. The SPA hands ONLY {type, requestId} to the native window.
//   3. The window resolves the requestId over the native pipe (Stage 1 lookup)
//      and receives the daemon-authored descriptor. It runs WAM strictly per
//      that descriptor and submits the bounded neutral result over the native
//      pipe (Stage 2). The renderer only polls the daemon status route and never
//      sees a security field.
// This helper is deterministic and unit-testable (no live WebView2, no live
// pipe) via the IExperimentalWamNativeChannel seam.
internal static class ExperimentalWamWindowMessage
{
    internal const string RequestType = "cookbook:experimental-wam-request";

    // Renderer-authored fields that MUST NOT influence acquisition. Their mere
    // presence rejects the envelope: purpose, recipe, generation, provider, and
    // acquisition mode are all daemon-owned.
    private static readonly string[] ForbiddenFields =
    {
        "purpose", "recipeId", "recipe", "generation", "lockGeneration",
        "provider", "providerId", "mode", "acquisitionMode",
    };

    // Returns true when the message is a well-formed requestId-only experimental
    // WAM request (and starts the async two-stage acquisition, delivering the
    // bounded result to the daemon over the native channel).
    internal static bool TryHandle(string? webMessageJson, ExperimentalWamWindowBridge bridge, IExperimentalWamNativeChannel channel)
        => TryBeginHandle(webMessageJson, bridge, channel) is not null;

    // Awaitable variant used by tests: returns the in-flight acquisition Task, or
    // null when the message was not a well-formed requestId-only request.
    internal static Task? TryBeginHandle(string? webMessageJson, ExperimentalWamWindowBridge bridge, IExperimentalWamNativeChannel channel)
    {
        string? requestId = TryParseRequestId(webMessageJson);
        if (requestId is null)
        {
            return null;
        }

        return RunAsync(requestId, bridge, channel);
    }

    // Parses ONLY the type and opaque requestId. Returns null (not handled) when
    // the message is not the experimental request type, has no requestId, or
    // carries any forbidden renderer-authored security field.
    internal static string? TryParseRequestId(string? webMessageJson)
    {
        if (string.IsNullOrWhiteSpace(webMessageJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(webMessageJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("type", out JsonElement typeEl) ||
                typeEl.ValueKind != JsonValueKind.String ||
                !string.Equals(typeEl.GetString(), RequestType, StringComparison.Ordinal))
            {
                return null;
            }

            // Strict rejection: any renderer-authored security field disqualifies
            // the envelope so a hostile or stale caller cannot smuggle intent.
            foreach (string forbidden in ForbiddenFields)
            {
                if (root.TryGetProperty(forbidden, out _))
                {
                    return null;
                }
            }

            string? requestId = GetString(root, "requestId");
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return null;
            }

            return requestId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task RunAsync(string requestId, ExperimentalWamWindowBridge bridge, IExperimentalWamNativeChannel channel)
    {
        try
        {
            // Stage 1: resolve the daemon-authored descriptor. A NotFound result
            // (unknown/expired/terminal/consumed) does nothing; the renderer
            // observes the daemon status instead.
            if (!channel.TryLookup(requestId, out WamNativeDescriptor descriptor) || !descriptor.Found)
            {
                return;
            }

            NeutralWamResult result = await bridge.AcquireAsync(requestId, descriptor, CancellationToken.None).ConfigureAwait(false);

            // Stage 2: submit the bounded result over the native pipe.
            channel.TrySubmit(result);
        }
        catch
        {
            // Never let an experimental acquisition failure escape the message
            // pump. Deliver a bounded broker-failure result over native IPC.
            channel.TrySubmit(NeutralWamResult.Rejected(requestId, NeutralWamCategory.BrokerFailure));
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
