using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PAXCookbook.App;

// Strict, deterministic importer for the machine-readable Work-account setup
// result produced by the provisioning helper (Track 1 / T1-S3 Phase 4).
//
// The setup result is the ONLY channel by which an administrator hands the app
// the three non-secret registration identifiers plus the helper's structural
// verification. It is treated as UNTRUSTED input: parsing is strict (unknown
// fields rejected), every value is revalidated, and — critically — a helper's
// "verified" claim is NEVER accepted as proof of current tenant consent. A
// successful import transitions the provider only to configured_unverified; the
// app independently performs its own read-only cloud verification (Phase 5)
// before the provider can become ready.
//
// Allowed top-level fields ONLY (any other key fails the import):
//   schemaVersion, kind, providerId, providerVersion, authorizationModel,
//   tenantId, clientAppId, structuralVerification, verifiedUtc, helper, labels.
// Forbidden anywhere at the top level: tokens, secrets, certificates, claims,
// account/user identity, correlation IDs, raw directory responses, customer or
// organization names. These are rejected structurally by the allow-list.
internal static class ExperimentalWamSetupResultImport
{
    internal const int SupportedSchemaVersion = 1;
    internal const string ExpectedKind = "pax-cookbook-wam-setup-result";
    internal const string ExpectedProviderId = "entra-wam";

    // Required authorization-model marker. The provisioning helper's registration
    // audience is picker-compatible (broad), but PAX Cookbook authorizes exactly
    // the ONE configured tenant. A conforming result must positively assert this
    // model; an older single-tenant-model result that lacks the marker is
    // rejected so the app never silently imports an out-of-model registration.
    internal const string ExpectedAuthorizationModel = "single-configured-tenant";

    // Default maximum age of the helper's structural verification. Older results
    // are rejected as stale so an administrator cannot import a long-obsolete
    // provisioning snapshot.
    internal static readonly TimeSpan DefaultMaxVerificationAge = TimeSpan.FromDays(90);

    private static readonly HashSet<string> AllowedTopLevelFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "kind",
        "resultKind",
        "providerId",
        "providerVersion",
        "authorizationModel",
        "tenantId",
        "clientAppId",
        "structuralVerification",
        "verifiedUtc",
        "helper",
        "labels",
    };

    // Bounded, business-safe failure reasons. No dynamic identifier is ever
    // included in the reason.
    internal enum ImportError
    {
        None = 0,
        InvalidJson,
        UnknownField,
        UnsupportedSchemaVersion,
        UnsupportedKind,
        UnsupportedResultKind,
        UnsupportedAuthorizationModel,
        UnsupportedProvider,
        UnsupportedBuild,
        InvalidTenantId,
        InvalidClientId,
        StructuralVerificationFailed,
        StaleOrIncompleteVerification,
    }

    internal readonly struct ImportResult
    {
        private ImportResult(bool success, ImportError error, ExperimentalWamConfigInput? input)
        {
            Success = success;
            Error = error;
            Input = input;
        }

        internal bool Success { get; }

        internal ImportError Error { get; }

        // The validated, importable configuration (non-secret identifiers only).
        // Non-null only on success.
        internal ExperimentalWamConfigInput? Input { get; }

        internal static ImportResult Ok(ExperimentalWamConfigInput input) => new(true, ImportError.None, input);

        internal static ImportResult Fail(ImportError error) => new(false, error, null);
    }

    // Parses and validates a setup-result document. Pure: no filesystem or clock
    // ambient state (nowUtc injected). Applies steps 1-9 of the import contract;
    // persistence + the configured_unverified transition (steps 10-11) are the
    // caller's responsibility.
    internal static ImportResult Parse(
        string? json,
        bool isCompiled,
        DateTimeOffset nowUtc,
        TimeSpan? maxVerificationAge = null)
    {
        TimeSpan maxAge = maxVerificationAge ?? DefaultMaxVerificationAge;

        if (string.IsNullOrWhiteSpace(json))
        {
            return ImportResult.Fail(ImportError.InvalidJson);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return ImportResult.Fail(ImportError.InvalidJson);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ImportResult.Fail(ImportError.InvalidJson);
            }

            // (2) Reject unknown top-level fields (strict allow-list).
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (!AllowedTopLevelFields.Contains(prop.Name))
                {
                    return ImportResult.Fail(ImportError.UnknownField);
                }
            }

            // (3) Schema version.
            if (!TryGetInt(root, "schemaVersion", out int schemaVersion) ||
                schemaVersion != SupportedSchemaVersion)
            {
                return ImportResult.Fail(ImportError.UnsupportedSchemaVersion);
            }

            // Kind marker (positive identification of the document type).
            string? kind = GetString(root, "kind");
            if (!string.Equals(kind, ExpectedKind, StringComparison.Ordinal))
            {
                return ImportResult.Fail(ImportError.UnsupportedKind);
            }

            // Result kind: an imported configuration comes from a Provision
            // result. A Verify result is not imported here.
            string? resultKind = GetString(root, "resultKind");
            if (!string.Equals(resultKind, "provision", StringComparison.Ordinal))
            {
                return ImportResult.Fail(ImportError.UnsupportedResultKind);
            }

            // Authorization-model marker: the picker-compatible registration
            // audience is broad, but PAX Cookbook authorizes exactly the one
            // configured tenant. Require the marker so an older single-tenant-
            // model result that lacks it is rejected rather than silently
            // imported.
            string? authorizationModel = GetString(root, "authorizationModel");
            if (!string.Equals(authorizationModel, ExpectedAuthorizationModel, StringComparison.Ordinal))
            {
                return ImportResult.Fail(ImportError.UnsupportedAuthorizationModel);
            }

            // (9a) Provider support.
            string? providerId = GetString(root, "providerId");
            if (!string.Equals(providerId, ExpectedProviderId, StringComparison.Ordinal))
            {
                return ImportResult.Fail(ImportError.UnsupportedProvider);
            }

            // (9b) Build support: only an experimental build can host the
            // provider, so a stable build rejects the import outright.
            if (!isCompiled)
            {
                return ImportResult.Fail(ImportError.UnsupportedBuild);
            }

            // (4) Tenant / client GUIDs (canonical hyphenated form).
            string tenantId = (GetString(root, "tenantId") ?? string.Empty).Trim();
            if (!Guid.TryParseExact(tenantId, "D", out _))
            {
                return ImportResult.Fail(ImportError.InvalidTenantId);
            }

            string clientAppId = (GetString(root, "clientAppId") ?? string.Empty).Trim();
            if (!Guid.TryParseExact(clientAppId, "D", out _))
            {
                return ImportResult.Fail(ImportError.InvalidClientId);
            }

            // (7) Require structural verification success.
            if (!root.TryGetProperty("structuralVerification", out JsonElement sv) ||
                sv.ValueKind != JsonValueKind.Object ||
                !sv.TryGetProperty("success", out JsonElement svSuccess) ||
                svSuccess.ValueKind != JsonValueKind.True)
            {
                return ImportResult.Fail(ImportError.StructuralVerificationFailed);
            }

            // (8) Reject stale or incomplete verification timestamp.
            string? verifiedUtcRaw = GetString(root, "verifiedUtc");
            if (string.IsNullOrWhiteSpace(verifiedUtcRaw) ||
                !DateTimeOffset.TryParse(
                    verifiedUtcRaw,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset verifiedUtc))
            {
                return ImportResult.Fail(ImportError.StaleOrIncompleteVerification);
            }

            // Reject a future timestamp (clock tampering / bad snapshot) and one
            // older than the allowed window.
            if (verifiedUtc > nowUtc.AddMinutes(5) || (nowUtc - verifiedUtc) > maxAge)
            {
                return ImportResult.Fail(ImportError.StaleOrIncompleteVerification);
            }

            // Success: build the importable configuration (non-secret only). The
            // caller saves it and transitions to configured_unverified — never
            // ready — because the helper's claim is not proof of current consent.
            var input = new ExperimentalWamConfigInput
            {
                Enabled = true,
                ProviderId = ExpectedProviderId,
                TenantId = tenantId,
                ClientId = clientAppId,
            };
            return ImportResult.Ok(input);
        }
    }

    // Bounded business-readable reason string for a failure (no identifiers).
    internal static string ToReasonCode(ImportError error) => error switch
    {
        ImportError.InvalidJson => "invalid_json",
        ImportError.UnknownField => "unknown_field",
        ImportError.UnsupportedSchemaVersion => "unsupported_schema_version",
        ImportError.UnsupportedKind => "unsupported_kind",
        ImportError.UnsupportedResultKind => "unsupported_result_kind",
        ImportError.UnsupportedAuthorizationModel => "unsupported_authorization_model",
        ImportError.UnsupportedProvider => "unsupported_provider",
        ImportError.UnsupportedBuild => "unsupported_build",
        ImportError.InvalidTenantId => "invalid_tenant_id",
        ImportError.InvalidClientId => "invalid_client_id",
        ImportError.StructuralVerificationFailed => "structural_verification_failed",
        ImportError.StaleOrIncompleteVerification => "stale_or_incomplete_verification",
        _ => "invalid_setup_result",
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
