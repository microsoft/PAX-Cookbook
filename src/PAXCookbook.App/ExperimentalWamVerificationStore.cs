using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PAXCookbook.App;

// Durable record of the most recent READ-ONLY cloud verification of the
// experimental Entra WAM setup (Track 1 / T1-S3 security gate + Phase 5).
//
// Purpose: the customer-ready capability MUST NOT be granted on structural
// configuration alone. A configuration only becomes usable (state == ready)
// after the app independently confirms — read-only, against the live tenant —
// that the two-registration setup is exactly correct. This store persists that
// confirmation so the ready state survives restarts, and BINDS it to the exact
// configured identifiers via a fingerprint: if the administrator later imports a
// different tenant/client/resource, the stale verification no longer matches and
// the provider falls back to configured_unverified (never silently ready).
//
// Doctrine (binding):
//   - Stores ONLY a bounded outcome, a timestamp, and a non-reversible
//     fingerprint of the three non-secret identifiers. No token, claim, account,
//     grant, correlation id, or raw directory response is ever written.
//   - A missing/unreadable/mismatched record yields verification None (fail
//     closed to configured_unverified, never ready).
//   - The fingerprint is a SHA-256 of the lowercased identifier tuple; it exists
//     only to detect configuration drift, not to reveal the identifiers.
internal static class ExperimentalWamVerificationStore
{
    private const string ProductFolder = "PAXCookbook";
    private const string ConfigFolder = "Config";
    private const string VerificationFileName = "experimental-wam-verification.json";
    internal const int CurrentSchemaVersion = 1;

    private const string OutcomeVerified = "verified";
    private const string OutcomeConsentFailed = "consent_failed";
    private const string OutcomeNone = "none";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    internal static string ResolveVerificationPath(string localAppDataBase)
        => Path.Combine(localAppDataBase, ProductFolder, ConfigFolder, VerificationFileName);

    // Non-reversible fingerprint that binds the verification to the exact
    // one-registration configuration: the tenant id, the public-client id, and
    // the provider + schema version. Byte-identical to the helper's
    // Get-ConfigFingerprint. Used only to detect configuration drift.
    internal static string ComputeFingerprint(string? tenantId, string? clientId)
    {
        string material = string.Join(
            '|',
            (tenantId ?? string.Empty).Trim().ToLowerInvariant(),
            (clientId ?? string.Empty).Trim().ToLowerInvariant(),
            ExperimentalWamOptions.EntraWamProviderId,
            ExperimentalWamOptions.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Resolves the effective verification for the CURRENT options. Returns
    // Verified/ConsentFailed only when a stored record exists AND its fingerprint
    // matches the current identifiers; otherwise None (fail closed). A record for
    // a not-fully-configured option set is never trusted.
    internal static ExperimentalWamVerification Resolve(string localAppDataBase, ExperimentalWamOptions options)
    {
        if (options is null || !options.IsFullyConfigured)
        {
            return ExperimentalWamVerification.None;
        }

        VerificationRecord? record = LoadRecord(localAppDataBase);
        if (record is null)
        {
            return ExperimentalWamVerification.None;
        }

        string expected = ComputeFingerprint(options.TenantId, options.ClientId);
        if (!string.Equals(record.ConfigFingerprint, expected, StringComparison.OrdinalIgnoreCase))
        {
            // The verified configuration no longer matches the current one.
            return ExperimentalWamVerification.None;
        }

        return record.Outcome switch
        {
            OutcomeVerified => ExperimentalWamVerification.Verified,
            OutcomeConsentFailed => ExperimentalWamVerification.ConsentFailed,
            _ => ExperimentalWamVerification.None,
        };
    }

    // Records a successful read-only cloud verification for the given options.
    internal static void RecordVerified(string localAppDataBase, ExperimentalWamOptions options)
        => Write(localAppDataBase, options, OutcomeVerified);

    // Records that the most recent attempt failed due to missing/revoked consent.
    internal static void RecordConsentFailure(string localAppDataBase, ExperimentalWamOptions options)
        => Write(localAppDataBase, options, OutcomeConsentFailed);

    // Clears any stored verification (used when configuration is removed or an
    // administrator explicitly resets it).
    internal static void Clear(string localAppDataBase)
    {
        string path = ResolveVerificationPath(localAppDataBase);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    private static void Write(string localAppDataBase, ExperimentalWamOptions options, string outcome)
    {
        if (options is null || !options.IsFullyConfigured)
        {
            throw new InvalidOperationException("Cannot record verification for an unconfigured provider.");
        }

        string path = ResolveVerificationPath(localAppDataBase);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var record = new VerificationRecord
        {
            SchemaVersion = CurrentSchemaVersion,
            Outcome = outcome,
            VerifiedUtc = DateTimeOffset.UtcNow.ToString("O"),
            ConfigFingerprint = ComputeFingerprint(options.TenantId, options.ClientId),
        };

        string json = JsonSerializer.Serialize(record, SerializerOptions);
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    // Returns the verified timestamp (ISO 8601) for the current options when a
    // matching Verified record exists; null otherwise. For business-readable
    // Settings display only (no identifiers).
    internal static string? GetVerifiedTimestamp(string localAppDataBase, ExperimentalWamOptions options)
    {
        if (options is null || !options.IsFullyConfigured)
        {
            return null;
        }

        VerificationRecord? record = LoadRecord(localAppDataBase);
        if (record is null || record.Outcome != OutcomeVerified)
        {
            return null;
        }

        string expected = ComputeFingerprint(options.TenantId, options.ClientId);
        return string.Equals(record.ConfigFingerprint, expected, StringComparison.OrdinalIgnoreCase)
            ? record.VerifiedUtc
            : null;
    }

    private static VerificationRecord? LoadRecord(string localAppDataBase)
    {
        string path = ResolveVerificationPath(localAppDataBase);
        if (!File.Exists(path))
        {
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        VerificationRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<VerificationRecord>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (record is null || record.SchemaVersion > CurrentSchemaVersion)
        {
            return null;
        }

        return record;
    }

    private sealed class VerificationRecord
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("outcome")]
        public string Outcome { get; set; } = OutcomeNone;

        [JsonPropertyName("verifiedUtc")]
        public string? VerifiedUtc { get; set; }

        [JsonPropertyName("configFingerprint")]
        public string? ConfigFingerprint { get; set; }
    }
}
