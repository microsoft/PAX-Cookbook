using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PAXCookbook.App;

// PHASE-2 attended Hello capability diagnostic capture host sink (cycle-01r-
// hello-capability-probe-repair). MEASUREMENT ONLY.
//
// Companion to HelloDiagCapture. Where the PHASE-1 sink captured the UNATTENDED
// auto-drive probe (which auto-cancels the enroll affordance and never runs the
// create() ceremony), this sink captures an ATTENDED, human-driven switch: a
// real operator clicks "Use Windows Hello", drives the top-level affordance, and
// performs the gesture-dependent navigator.credentials.create() ceremony. The
// residual unreproduced PHASE-1 failure was exactly that gesture-dependent
// ceremony, so this sink records the bounded fact of whether the ceremony was
// invoked and, if it rejected, the DOMException NAME mapped to a bounded class —
// never the credential, challenge, attestation, or any identity material.
//
// Compile-time gate: this sink is only ever wired when the build-gated test-
// isolation runtime is active (TestIsolationRuntime.IsActive). A stable/customer
// build has no isolation activation path, so IsActive is always false there and
// the seam can NEVER run — no attended marker is injected and this handler is
// never called. It also requires the WebMessage Source origin to be exactly the
// loopback application origin (enforced by the caller).
//
// Defense-in-depth against identity/credential leakage: like HelloDiagCapture,
// this sink NEVER writes the renderer's payload verbatim. It projects the
// incoming JSON through a STRICT allow-list — a fixed set of nullable-boolean /
// bounded-enum / short-string fields. Any key not on the allow-list is dropped;
// any enum value outside its bounded set is coerced to a safe sentinel (or
// dropped to null); any string over a hard length cap is rejected. So even a
// modified or hostile renderer cannot smuggle an account id, UPN, tenant, client
// id, token, claim, credential id, challenge, attestation, or certificate
// through this path.
internal static class HelloAttendedDiagCapture
{
    private const string EnvelopeType = "cookbook:hello-attended-diag-capture";
    private const int MaxStringLength = 128;

    // Bounded enum domains. A value outside the domain is coerced to the
    // domain's safe sentinel (or dropped to null for nullable enums) rather than
    // persisted, so the on-disk evidence is always well-formed and free of
    // renderer-authored free text.
    private static readonly HashSet<string> OriginRelationDomain = new(StringComparer.Ordinal)
    {
        "same_origin", "different_origin", "unavailable",
    };

    private static readonly HashSet<string> FinalProviderDomain = new(StringComparer.Ordinal)
    {
        "work_account", "windows_hello", "unavailable",
    };

    // Reuses the PHASE-1 backend-select reason domain (the same bounded set the
    // select-windows-hello route can surface).
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

    private static readonly HashSet<string> EnrollmentResultDomain = new(StringComparer.Ordinal)
    {
        "enrolled", "cancelled", "timeout", "failed", "unavailable", "transport_failure",
    };

    // The bounded classification of a create() ceremony rejection, derived by the
    // shell from the DOMException NAME only (never the message/stack). This is the
    // decisive attended fact the unattended probe could not reach.
    private static readonly HashSet<string> CreateFailureClassDomain = new(StringComparer.Ordinal)
    {
        "not_allowed", "security", "abort", "timeout", "constraint", "unknown",
    };

    // Returns true when the envelope was recognized as an attended diagnostic
    // capture and consumed (whether or not it could be persisted). Returns false
    // when the message is not an attended-capture envelope so the caller can
    // continue its normal message dispatch.
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

        // From here the envelope is ours: consume it regardless of persistence so
        // it never falls through to another handler.
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

        Dictionary<string, object?> bounded = ProjectAllowList(payload);

        try
        {
            Directory.CreateDirectory(outDir);
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssffffZ", CultureInfo.InvariantCulture);
            string path = Path.Combine(outDir, $"diag_attended_{stamp}.json");
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
    // Exposed as internal (not private) so the allow-list can be unit-tested
    // directly through InternalsVisibleTo, proving no unlisted key survives.
    internal static Dictionary<string, object?> ProjectAllowList(JsonElement payload)
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

        o["enrollmentRequestPosted"] = OptionalBool(payload, "enrollmentRequestPosted");
        o["enrollmentRequestReceived"] = OptionalBool(payload, "enrollmentRequestReceived");
        o["enrollmentAffordanceOpened"] = OptionalBool(payload, "enrollmentAffordanceOpened");
        o["enrollmentGestureStarted"] = OptionalBool(payload, "enrollmentGestureStarted");

        // The decisive attended facts the unattended probe could not reach.
        o["credentialCeremonyInvoked"] = OptionalBool(payload, "credentialCeremonyInvoked");
        o["createFailureClass"] = NullableBoundedEnum(payload, "createFailureClass", CreateFailureClassDomain);
        o["enrollmentResultOutcome"] = NullableBoundedEnum(payload, "enrollmentResultOutcome", EnrollmentResultDomain);

        o["backendSelectAttempted"] = OptionalBool(payload, "backendSelectAttempted");
        o["backendSelectReason"] = NullableBoundedEnum(payload, "backendSelectReason", BackendSelectReasonDomain);
        o["backendSelectPersisted"] = OptionalBool(payload, "backendSelectPersisted");

        o["finalSelectedProvider"] = BoundedEnum(payload, "finalSelectedProvider", FinalProviderDomain, "unavailable");

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
