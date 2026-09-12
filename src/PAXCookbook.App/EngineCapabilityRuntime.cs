using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PAXCookbook.App;

// Cycle 15 — RUNTIME ENGINE CAPABILITY CHECK.
//
// Answers exactly one question: does the ACQUIRED managed PAX engine on this
// machine attest a named capability, bound to the exact bytes that were
// verified at activation time?
//
// It is FAIL-CLOSED at every step: any unexpected condition collapses to
// `Unknown`, and only a fully consistent record yields `Available`.
//
// It performs NO network call, NO manifest fetch, NO repair, and NO write. It
// reuses EngineAcquisition.Resolve for policy detection and for the re-hash of
// the active engine file; it never duplicates hashing logic. The bounded state
// name is the ONLY value that may cross a boundary: no path, hash, version,
// manifest identity, or capability token is ever exposed.
internal enum EngineCapabilityState
{
    // Every check passed: the acquired engine attests the capability.
    Available,

    // The acquired engine is valid but does not attest this capability. This is
    // the state the CURRENT production engine produces.
    NotDeclared,

    // No approved managed engine has been acquired (including bundled/embedded
    // policy, which is not the acquired engine store).
    EngineNotAcquired,

    // The active engine file does not match the recorded approved hash.
    EngineHashMismatch,

    // The capability record is not bound to the recorded acquisition version.
    EngineVersionMismatch,

    // The persisted capability record is malformed, over-limit, unrecognized,
    // or duplicated.
    StateInvalid,

    // Unrepresentable / unreadable: fail closed.
    Unknown,
}

internal static class EngineCapabilityRuntime
{
    // The closed capability vocabulary is owned by the manifest validator; this
    // module reuses it so a token can never be recognized in only one place.
    internal const string OrganizationCertificateSha256Selector =
        ManifestSchemaValidator.OrganizationCertificateSha256SelectorV1;

    // Bounded wire/log token for a state. Never carries evidence.
    internal static string WireToken(EngineCapabilityState state) => state switch
    {
        EngineCapabilityState.Available => "available",
        EngineCapabilityState.NotDeclared => "not_declared",
        EngineCapabilityState.EngineNotAcquired => "engine_not_acquired",
        EngineCapabilityState.EngineHashMismatch => "engine_hash_mismatch",
        EngineCapabilityState.EngineVersionMismatch => "engine_version_mismatch",
        EngineCapabilityState.StateInvalid => "state_invalid",
        _ => "unknown",
    };

    // Production entry point. localAppDataBase is the explicit test override
    // seam, exactly like EngineAcquisition.Resolve, so a SYNTHETIC capable
    // engine can be constructed in a temp directory without a real acquisition.
    internal static EngineCapabilityState Evaluate(
        VersionInfo version, string localAppDataBase, string capabilityToken)
        => Evaluate(EngineAcquisition.Resolve(version, localAppDataBase), capabilityToken);

    // Evaluates against an acquisition result that has ALREADY re-hashed the
    // active engine file. A null acquisition fails closed.
    internal static EngineCapabilityState Evaluate(
        EngineAcquisitionResult? acquisition, string capabilityToken)
    {
        if (acquisition is null
            || string.IsNullOrWhiteSpace(capabilityToken)
            || !ManifestSchemaValidator.RecognizedCapabilities.Contains(capabilityToken))
        {
            return EngineCapabilityState.Unknown;
        }

        // 2. Bundled legacy mode is not the acquired engine store, which is why
        //    the CURRENT bundled engine can never attest a capability.
        if (string.Equals(acquisition.Policy, "embedded", StringComparison.Ordinal))
        {
            return EngineCapabilityState.EngineNotAcquired;
        }

        // 3. Only a fully valid acquisition may carry a capability.
        switch (acquisition.State)
        {
            case "acquired":
                break;
            case "invalid":
                return EngineCapabilityState.EngineHashMismatch;
            case "missing":
            case "acquisition_pending":
            case "failed":
                return EngineCapabilityState.EngineNotAcquired;
            default:
                return EngineCapabilityState.Unknown;
        }

        // 4. Read the persisted capability record.
        (CapabilityRecordStatus status, IReadOnlyList<string> tokens, string? recordedVersion) =
            ReadCapabilityRecord(acquisition.InstallStatePath);

        switch (status)
        {
            case CapabilityRecordStatus.Ok:
                break;
            case CapabilityRecordStatus.Absent:
                return EngineCapabilityState.NotDeclared;
            case CapabilityRecordStatus.Malformed:
                return EngineCapabilityState.StateInvalid;
            default:
                return EngineCapabilityState.Unknown;
        }

        // 5. The record is bound to the acquisition version recorded alongside
        //    the verified bytes in the same atomic write.
        if (string.IsNullOrWhiteSpace(recordedVersion)
            || string.IsNullOrWhiteSpace(acquisition.Version)
            || !string.Equals(recordedVersion, acquisition.Version, StringComparison.Ordinal))
        {
            return EngineCapabilityState.EngineVersionMismatch;
        }

        // 6-7.
        foreach (string token in tokens)
        {
            if (string.Equals(token, capabilityToken, StringComparison.Ordinal))
            {
                return EngineCapabilityState.Available;
            }
        }
        return EngineCapabilityState.NotDeclared;
    }

    private enum CapabilityRecordStatus
    {
        Ok,
        Absent,
        Malformed,
        Unreadable,
    }

    // Read-only parse of install-state.json -> paxAcquisition.capabilities plus
    // the sibling version the record is bound to. Never writes or repairs.
    private static (CapabilityRecordStatus Status, IReadOnlyList<string> Tokens, string? Version)
        ReadCapabilityRecord(string installStatePath)
    {
        try
        {
            if (!File.Exists(installStatePath))
            {
                return (CapabilityRecordStatus.Unreadable, Array.Empty<string>(), null);
            }

            using var fs = new FileStream(
                installStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument doc = JsonDocument.Parse(fs);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("paxAcquisition", out JsonElement pax)
                || pax.ValueKind != JsonValueKind.Object)
            {
                return (CapabilityRecordStatus.Unreadable, Array.Empty<string>(), null);
            }

            string? version = null;
            if (pax.TryGetProperty("version", out JsonElement verEl) &&
                verEl.ValueKind == JsonValueKind.String)
            {
                version = verEl.GetString();
            }

            if (!pax.TryGetProperty("capabilities", out JsonElement capEl) ||
                capEl.ValueKind == JsonValueKind.Null)
            {
                // Legacy record (written before this cycle) or an engine that
                // declares none: both mean "no capability".
                return (CapabilityRecordStatus.Absent, Array.Empty<string>(), version);
            }

            if (capEl.ValueKind != JsonValueKind.Array)
            {
                return (CapabilityRecordStatus.Malformed, Array.Empty<string>(), version);
            }

            var tokens = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement element in capEl.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    return (CapabilityRecordStatus.Malformed, Array.Empty<string>(), version);
                }
                string? token = element.GetString();
                if (string.IsNullOrWhiteSpace(token)
                    || tokens.Count >= ManifestSchemaValidator.MaxCapabilitiesPerEntry
                    || !ManifestSchemaValidator.RecognizedCapabilities.Contains(token)
                    || !seen.Add(token))
                {
                    // Fail closed on an unrecognized or duplicate token; never
                    // filter-and-continue.
                    return (CapabilityRecordStatus.Malformed, Array.Empty<string>(), version);
                }
                tokens.Add(token);
            }

            return (CapabilityRecordStatus.Ok, tokens, version);
        }
        catch
        {
            return (CapabilityRecordStatus.Unreadable, Array.Empty<string>(), null);
        }
    }
}
