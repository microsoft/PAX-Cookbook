using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PAXCookbook.App;

// Strict parser for the helper's VERIFY result (Track 1 / T1-S3 Phase 2/4).
//
// The Verify result belongs to the same schema family as the Provision/import
// result (kind == "pax-cookbook-wam-setup-result") but carries resultKind ==
// "verify" plus the live consent/grant outcome and a configuration fingerprint.
// Unlike an imported Provision result — which the product never trusts as proof
// of current consent — a Verify result is the product's OWN independent read-only
// confirmation, so on success the product records verification and transitions to
// ready. This parser binds the result to the EXACT local configuration: every
// identifier and the fingerprint must match, the structural and grant checks must
// all pass, and the timestamp must be fresh.
internal static class ExperimentalWamVerifyResultImport
{
    internal const int SupportedSchemaVersion = 1;
    internal const string ExpectedKind = "pax-cookbook-wam-setup-result";
    internal const string ExpectedResultKind = "verify";
    internal const string ExpectedProviderId = "entra-wam";

    internal static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(30);

    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "kind",
        "resultKind",
        "providerId",
        "providerVersion",
        // Accepted (not required) so a picker-compatible helper result that
        // carries the single-configured-tenant marker is not rejected as an
        // unknown field. The product's authorization is unchanged.
        "authorizationModel",
        "tenantId",
        "clientAppId",
        "structuralVerification",
        "grantResult",
        "configFingerprint",
        "verifiedUtc",
        "helper",
        "labels",
    };

    internal enum VerifyError
    {
        None = 0,
        InvalidJson,
        UnknownField,
        UnsupportedSchemaVersion,
        UnsupportedKind,
        UnsupportedResultKind,
        UnsupportedProvider,
        UnsupportedBuild,
        IdentifierMismatch,
        FingerprintMismatch,
        StructuralCheckFailed,
        ConsentCheckFailed,
        StaleOrIncompleteVerification,
    }

    internal readonly struct VerifyResult
    {
        private VerifyResult(bool success, VerifyError error)
        {
            Success = success;
            Error = error;
        }

        internal bool Success { get; }

        internal VerifyError Error { get; }

        internal static VerifyResult Ok() => new(true, VerifyError.None);

        internal static VerifyResult Fail(VerifyError e) => new(false, e);
    }

    // Validates a Verify result against the CURRENT configuration. expected*
    // values come from the durable configuration the product already holds; the
    // helper result must match them exactly.
    internal static VerifyResult Validate(
        string? json,
        bool isCompiled,
        string expectedTenantId,
        string expectedClientId,
        string expectedFingerprint,
        DateTimeOffset nowUtc,
        TimeSpan? maxAge = null)
    {
        TimeSpan max = maxAge ?? DefaultMaxAge;

        if (!isCompiled)
        {
            return VerifyResult.Fail(VerifyError.UnsupportedBuild);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return VerifyResult.Fail(VerifyError.InvalidJson);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return VerifyResult.Fail(VerifyError.InvalidJson);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return VerifyResult.Fail(VerifyError.InvalidJson);
            }

            foreach (JsonProperty p in root.EnumerateObject())
            {
                if (!AllowedFields.Contains(p.Name))
                {
                    return VerifyResult.Fail(VerifyError.UnknownField);
                }
            }

            if (!TryGetInt(root, "schemaVersion", out int sv) || sv != SupportedSchemaVersion)
            {
                return VerifyResult.Fail(VerifyError.UnsupportedSchemaVersion);
            }

            if (!string.Equals(GetString(root, "kind"), ExpectedKind, StringComparison.Ordinal))
            {
                return VerifyResult.Fail(VerifyError.UnsupportedKind);
            }

            if (!string.Equals(GetString(root, "resultKind"), ExpectedResultKind, StringComparison.Ordinal))
            {
                return VerifyResult.Fail(VerifyError.UnsupportedResultKind);
            }

            if (!string.Equals(GetString(root, "providerId"), ExpectedProviderId, StringComparison.Ordinal))
            {
                return VerifyResult.Fail(VerifyError.UnsupportedProvider);
            }

            // Exact identifier match to the local configuration.
            string tenant = (GetString(root, "tenantId") ?? string.Empty).Trim();
            string client = (GetString(root, "clientAppId") ?? string.Empty).Trim();
            if (!string.Equals(tenant, expectedTenantId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(client, expectedClientId, StringComparison.OrdinalIgnoreCase))
            {
                return VerifyResult.Fail(VerifyError.IdentifierMismatch);
            }

            // Exact configuration fingerprint match.
            string fingerprint = (GetString(root, "configFingerprint") ?? string.Empty).Trim();
            if (!string.Equals(fingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return VerifyResult.Fail(VerifyError.FingerprintMismatch);
            }

            // Every structural check must pass.
            if (!root.TryGetProperty("structuralVerification", out JsonElement sve) ||
                sve.ValueKind != JsonValueKind.Object ||
                !sve.TryGetProperty("success", out JsonElement sveOk) ||
                sveOk.ValueKind != JsonValueKind.True)
            {
                return VerifyResult.Fail(VerifyError.StructuralCheckFailed);
            }

            // Exact tenant-wide AllPrincipals Microsoft Graph User.Read grant
            // must be present.
            if (!root.TryGetProperty("grantResult", out JsonElement gr) ||
                gr.ValueKind != JsonValueKind.Object ||
                !gr.TryGetProperty("allPrincipalsUserRead", out JsonElement grOk) ||
                grOk.ValueKind != JsonValueKind.True)
            {
                return VerifyResult.Fail(VerifyError.ConsentCheckFailed);
            }

            // Freshness.
            string? verifiedRaw = GetString(root, "verifiedUtc");
            if (string.IsNullOrWhiteSpace(verifiedRaw) ||
                !DateTimeOffset.TryParse(
                    verifiedRaw,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset verifiedUtc))
            {
                return VerifyResult.Fail(VerifyError.StaleOrIncompleteVerification);
            }

            if (verifiedUtc > nowUtc.AddMinutes(5) || (nowUtc - verifiedUtc) > max)
            {
                return VerifyResult.Fail(VerifyError.StaleOrIncompleteVerification);
            }

            return VerifyResult.Ok();
        }
    }

    internal static string ToReasonCode(VerifyError error) => error switch
    {
        VerifyError.InvalidJson => "invalid_json",
        VerifyError.UnknownField => "unknown_field",
        VerifyError.UnsupportedSchemaVersion => "unsupported_schema_version",
        VerifyError.UnsupportedKind => "unsupported_kind",
        VerifyError.UnsupportedResultKind => "unsupported_result_kind",
        VerifyError.UnsupportedProvider => "unsupported_provider",
        VerifyError.UnsupportedBuild => "unsupported_build",
        VerifyError.IdentifierMismatch => "configuration_mismatch",
        VerifyError.FingerprintMismatch => "configuration_fingerprint_mismatch",
        VerifyError.StructuralCheckFailed => "structural_verification_failed",
        VerifyError.ConsentCheckFailed => "consent_missing_or_revoked",
        VerifyError.StaleOrIncompleteVerification => "stale_or_incomplete_verification",
        _ => "verification_failed",
    };

    private static bool TryGetInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        return obj.TryGetProperty(name, out JsonElement el) &&
               el.ValueKind == JsonValueKind.Number &&
               el.TryGetInt32(out value);
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
