using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PAXCookbook.App;

// PHASE-1 Hello capability diagnostic capture host sink (cycle-01r-hello-
// capability-probe-repair). MEASUREMENT ONLY.
//
// Receives exactly one bounded, PII-free evidence envelope posted by the gated
// web-layer recorder and persists it to the launch-supplied evidence directory.
// It is only ever wired when the diagnostic seam is enabled (the build-gated
// test-isolation runtime AND PAXCB_HELLO_DIAG=1) and only after the caller has
// confirmed the WebMessage Source origin is exactly the loopback application
// origin.
//
// Defense-in-depth against identity/credential leakage: this sink NEVER writes
// the renderer's payload verbatim. It projects the incoming JSON through a
// STRICT allow-list — a fixed set of boolean / bounded-enum / short-string /
// timestamp fields. Any key not on the allow-list is dropped; any enum value
// outside its bounded set is coerced to a safe sentinel; any string over a hard
// length cap is rejected. So even a modified or hostile renderer cannot smuggle
// an account id, UPN, tenant, client id, token, claim, credential id,
// challenge, attestation, or certificate through this path.
internal static class HelloDiagCapture
{
    private const string EnvelopeType = "cookbook:hello-diag-capture";
    private const int MaxStringLength = 128;

    // Bounded enum domains. A value outside the domain is coerced to the
    // domain's safe sentinel rather than persisted, so the on-disk evidence is
    // always well-formed and free of renderer-authored free text.
    private static readonly HashSet<string> OriginRelationDomain = new(StringComparer.Ordinal)
    {
        "same_origin", "different_origin", "unavailable",
    };

    private static readonly HashSet<string> BackendSelectReasonDomain = new(StringComparer.Ordinal)
    {
        "switched",
        "windows_hello_enrollment_required",
        "windows_hello_platform_unavailable",
        "windows_hello_registration_repair_required",
        "request_failed",
        "transport_failure",
        "cancelled",
    };

    private static readonly HashSet<string> TerminalOutcomeDomain = new(StringComparer.Ordinal)
    {
        "affordance_opened_then_cancelled",
        "backend_switched",
        "platform_unavailable",
        "enrollment_required_no_affordance",
        "registration_repair_required",
        "request_failed",
        "transport_failure",
        "cancelled",
        "error",
        "incomplete",
    };

    // Returns true when the envelope was recognized as a diagnostic capture and
    // consumed (whether or not it could be persisted). Returns false when the
    // message is not a diagnostic-capture envelope so the caller can continue
    // its normal message dispatch.
    internal static bool TryHandle(string? rawJson, string? outDir)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            root = doc.RootElement.Clone();
        }
        catch
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out JsonElement typeEl) ||
            typeEl.ValueKind != JsonValueKind.String ||
            !string.Equals(typeEl.GetString(), EnvelopeType, StringComparison.Ordinal))
        {
            return false;
        }

        // From here the envelope is ours: consume it regardless of persistence
        // so it never falls through to another handler.
        if (!root.TryGetProperty("payload", out JsonElement payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(outDir))
        {
            // No evidence directory supplied by the launch — nothing to persist.
            return true;
        }

        var bounded = ProjectAllowList(payload);

        try
        {
            Directory.CreateDirectory(outDir);
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssffffZ", CultureInfo.InvariantCulture);
            string path = Path.Combine(outDir, $"diag_capability_probe_{stamp}.json");
            string json = JsonSerializer.Serialize(bounded, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Non-fatal: a persistence failure never affects the shell. The
            // envelope is still considered handled.
        }

        return true;
    }

    // Projects the renderer payload through the strict allow-list. Unknown keys
    // are dropped; enum values are validated against their bounded domain.
    private static Dictionary<string, object?> ProjectAllowList(JsonElement payload)
    {
        var o = new Dictionary<string, object?>(StringComparer.Ordinal);

        o["schemaVersion"] = BoundedString(payload, "schemaVersion");
        o["cycleId"] = BoundedString(payload, "cycleId");
        o["capturedUtc"] = BoundedString(payload, "capturedUtc");

        o["iframeSecureContext"] = OptionalBool(payload, "iframeSecureContext");
        o["topLevelSecureContext"] = OptionalBool(payload, "topLevelSecureContext");
        o["iframePlatformAuthenticatorAvailable"] = OptionalBool(payload, "iframePlatformAuthenticatorAvailable");
        o["topLevelPlatformAuthenticatorAvailable"] = OptionalBool(payload, "topLevelPlatformAuthenticatorAvailable");

        o["originRelation"] = BoundedEnum(payload, "originRelation", OriginRelationDomain, "unavailable");

        o["cookbookHelloEnrollPresent"] = OptionalBool(payload, "cookbookHelloEnrollPresent");
        o["cookbookHelloEnrollReadyAfterPrepare"] = OptionalBool(payload, "cookbookHelloEnrollReadyAfterPrepare");

        o["enrollmentRequestPosted"] = OptionalBool(payload, "enrollmentRequestPosted");
        o["enrollmentRequestReceived"] = OptionalBool(payload, "enrollmentRequestReceived");
        o["enrollmentAffordanceOpened"] = OptionalBool(payload, "enrollmentAffordanceOpened");

        o["backendSelectAttempted"] = OptionalBool(payload, "backendSelectAttempted");
        o["backendSelectReason"] = NullableBoundedEnum(payload, "backendSelectReason", BackendSelectReasonDomain);

        o["terminalOutcome"] = BoundedEnum(payload, "terminalOutcome", TerminalOutcomeDomain, "incomplete");

        o["selectedProviderStillWorkAccount"] = OptionalBool(payload, "selectedProviderStillWorkAccount");
        o["noCredentialCreated"] = OptionalBool(payload, "noCredentialCreated");
        o["credentialCeremonyInvoked"] = OptionalBool(payload, "credentialCeremonyInvoked");
        o["gestureDependentFieldsNotExercised"] = OptionalBool(payload, "gestureDependentFieldsNotExercised");

        return o;
    }

    private static bool? OptionalBool(JsonElement payload, string name)
    {
        if (payload.TryGetProperty(name, out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.True) { return true; }
            if (el.ValueKind == JsonValueKind.False) { return false; }
        }
        return null;
    }

    private static string? BoundedString(JsonElement payload, string name)
    {
        if (payload.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String)
        {
            string? s = el.GetString();
            if (s is not null && s.Length <= MaxStringLength)
            {
                return s;
            }
        }
        return null;
    }

    private static string BoundedEnum(JsonElement payload, string name, HashSet<string> domain, string sentinel)
    {
        if (payload.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String)
        {
            string? s = el.GetString();
            if (s is not null && domain.Contains(s))
            {
                return s;
            }
        }
        return sentinel;
    }

    private static string? NullableBoundedEnum(JsonElement payload, string name, HashSet<string> domain)
    {
        if (payload.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String)
        {
            string? s = el.GetString();
            if (s is not null && domain.Contains(s))
            {
                return s;
            }
        }
        return null;
    }
}
