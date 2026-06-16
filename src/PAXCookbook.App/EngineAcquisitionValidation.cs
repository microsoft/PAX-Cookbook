// X14 pure-logic ports of the PowerShell oracle:
//   app\broker\Engine\ManifestSchema.psm1
//   app\broker\Engine\Acquisition.psm1::Select-CompatibleEngineEntry
//   app\broker\Update\Signature.psm1::Test-PackageSignature
//
// These types perform NO network I/O and NO filesystem mutation. They
// only parse JSON, hash bytes, validate shapes, and (in the signature
// verifier) read a package file's bytes to recompute its hash. They
// never touch the canonical PAX script or install-state.json.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.App;

internal static class Sha256Hex
{
    internal static string OfBytes(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        if (!SHA256.TryHashData(bytes, hash, out _))
        {
            throw new InvalidOperationException("SHA256.TryHashData failed.");
        }
        return Convert.ToHexString(hash);
    }

    internal static string OfFile(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> hash = stackalloc byte[32];
        using var sha = SHA256.Create();
        byte[] computed = sha.ComputeHash(fs);
        return Convert.ToHexString(computed);
    }

    internal static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return false; }
        if (value.Length != 64) { return false; }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) { return false; }
        }
        return true;
    }

    internal static string? Normalize(string? value)
    {
        return IsValid(value) ? value!.ToUpperInvariant() : null;
    }
}

internal sealed record ApprovedEngineEntry(
    string Name,
    string Version,
    string Sha256Upper,
    string DownloadUrl,
    string Status,
    string MinCookbookVersion,
    string MaxCookbookVersion,
    string? ReleaseNotesUrl);

internal sealed record ManifestValidationResult
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public string? SchemaVersion { get; init; }
    public string? ManifestVersion { get; init; }
    public string? Channel { get; init; }
    public string? GeneratedAtUtc { get; init; }
    public string? SigningKeyId { get; init; }
    public IReadOnlyList<ApprovedEngineEntry> Entries { get; init; } = Array.Empty<ApprovedEngineEntry>();

    internal string ManifestId =>
        (SigningKeyId ?? "unknown") + ":" + (ManifestVersion ?? "unknown");
}

internal static class ManifestSchemaValidator
{
    // Oracle Test-ApprovedEngineManifestSchema parity. Top-level and
    // per-entry allow-lists are CLOSED — any unknown field is a hard reject.
    private static readonly HashSet<string> AllowedTopLevelKeys = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "manifestVersion",
        "channel",
        "generatedAtUtc",
        "signingKeyId",
        "scripts",
    };

    private static readonly HashSet<string> AllowedEntryKeys = new(StringComparer.Ordinal)
    {
        "name",
        "version",
        "sha256",
        "downloadUrl",
        "status",
        "minCookbookVersion",
        "maxCookbookVersion",
        "releaseNotesUrl",
    };

    private static readonly string[] RequiredEntryKeys = new[]
    {
        "name", "version", "sha256", "downloadUrl", "status",
        "minCookbookVersion", "maxCookbookVersion",
    };

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "approved", "deprecated", "withdrawn",
    };

    private static readonly int[] SupportedSchemaVersions = new[] { 1 };

    internal static ManifestValidationResult Validate(JsonElement root)
        => Validate(root, allowLoopbackHttpDownloadUrl: false);

    // allowLoopbackHttpDownloadUrl is the runtime parity equivalent of the
    // PowerShell oracle's verify_stage3 trick of overriding Get-PaxScriptByDownload.
    // It is only ever set true when VERSION.json paxScript.manifestSignaturePolicy
    // = 'internal-test-bypass' (i.e. the same test-only signature seam the X14
    // route handler already honors). Production deployments use 'required',
    // so this branch is never taken outside smoke runs.
    internal static ManifestValidationResult Validate(JsonElement root, bool allowLoopbackHttpDownloadUrl)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ManifestValidationResult
            {
                Ok = false,
                Error = "type_mismatch",
                Message = "Approved-engine manifest must be a JSON object.",
            };
        }

        foreach (JsonProperty p in root.EnumerateObject())
        {
            if (!AllowedTopLevelKeys.Contains(p.Name))
            {
                return new ManifestValidationResult
                {
                    Ok = false,
                    Error = "unknown_field",
                    Message = "Top-level key \"" + p.Name + "\" is not allowed.",
                };
            }
        }

        if (!root.TryGetProperty("schemaVersion", out JsonElement schemaEl))
        {
            return new ManifestValidationResult
            {
                Ok = false, Error = "missing_field", Message = "\"schemaVersion\" is required.",
            };
        }
        if (schemaEl.ValueKind != JsonValueKind.Number || !schemaEl.TryGetInt32(out int schemaVer))
        {
            return new ManifestValidationResult
            {
                Ok = false, Error = "type_mismatch", Message = "\"schemaVersion\" must be an integer.",
            };
        }
        if (Array.IndexOf(SupportedSchemaVersions, schemaVer) < 0)
        {
            return new ManifestValidationResult
            {
                Ok = false,
                Error = "unsupported_schema_version",
                Message = "\"schemaVersion\" = " + schemaVer + " is not supported. Supported: " +
                    string.Join(", ", SupportedSchemaVersions),
            };
        }

        if (!root.TryGetProperty("manifestVersion", out JsonElement mvEl))
        {
            return MissingField("manifestVersion");
        }
        string manifestVersion;
        if (mvEl.ValueKind == JsonValueKind.Number)
        {
            manifestVersion = mvEl.GetRawText();
        }
        else if (mvEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mvEl.GetString()))
        {
            manifestVersion = mvEl.GetString()!;
        }
        else
        {
            return new ManifestValidationResult
            {
                Ok = false,
                Error = "missing_field",
                Message = "\"manifestVersion\" is required (integer or non-empty string).",
            };
        }

        if (!TryRequiredString(root, "channel", out string? channel))
        {
            return MissingField("channel");
        }
        if (!TryRequiredString(root, "generatedAtUtc", out string? generatedAtUtc))
        {
            return MissingField("generatedAtUtc");
        }
        if (!TryRequiredString(root, "signingKeyId", out string? signingKeyId))
        {
            return MissingField("signingKeyId");
        }

        if (!root.TryGetProperty("scripts", out JsonElement scriptsEl))
        {
            return MissingField("scripts");
        }
        if (scriptsEl.ValueKind != JsonValueKind.Array)
        {
            return new ManifestValidationResult
            {
                Ok = false, Error = "type_mismatch", Message = "\"scripts\" must be an array.",
            };
        }

        List<ApprovedEngineEntry> entries = new();
        int idx = -1;
        foreach (JsonElement entryEl in scriptsEl.EnumerateArray())
        {
            idx++;
            if (entryEl.ValueKind != JsonValueKind.Object)
            {
                return new ManifestValidationResult
                {
                    Ok = false, Error = "type_mismatch",
                    Message = "scripts[" + idx + "] must be a JSON object.",
                };
            }

            foreach (JsonProperty p in entryEl.EnumerateObject())
            {
                if (!AllowedEntryKeys.Contains(p.Name))
                {
                    return new ManifestValidationResult
                    {
                        Ok = false, Error = "unknown_field",
                        Message = "scripts[" + idx + "] has unknown field \"" + p.Name + "\".",
                    };
                }
            }

            foreach (string req in RequiredEntryKeys)
            {
                if (!entryEl.TryGetProperty(req, out _))
                {
                    return new ManifestValidationResult
                    {
                        Ok = false, Error = "missing_field",
                        Message = "scripts[" + idx + "] is missing required field \"" + req + "\".",
                    };
                }
            }

            if (!TryRequiredString(entryEl, "name", out string? entryName))
            {
                return EntryTypeMismatch(idx, "name");
            }
            if (!TryRequiredString(entryEl, "version", out string? entryVersion))
            {
                return EntryTypeMismatch(idx, "version");
            }

            string? entrySha = TryGetString(entryEl, "sha256");
            if (!Sha256Hex.IsValid(entrySha))
            {
                return new ManifestValidationResult
                {
                    Ok = false, Error = "invalid_sha256",
                    Message = "scripts[" + idx + "].sha256 must be a 64-character hex string.",
                };
            }

            string? downloadUrl = TryGetString(entryEl, "downloadUrl");
            if (!IsAcceptableEntryDownloadUrl(downloadUrl, allowLoopbackHttpDownloadUrl))
            {
                return new ManifestValidationResult
                {
                    Ok = false, Error = "invalid_download_url",
                    Message = "scripts[" + idx + "].downloadUrl must be an https:// URL.",
                };
            }

            string? status = TryGetString(entryEl, "status");
            if (status is null || !AllowedStatuses.Contains(status))
            {
                return new ManifestValidationResult
                {
                    Ok = false, Error = "invalid_status",
                    Message = "scripts[" + idx + "].status must be one of: " +
                        string.Join(", ", AllowedStatuses),
                };
            }

            if (!TryRequiredString(entryEl, "minCookbookVersion", out string? minVer))
            {
                return EntryTypeMismatch(idx, "minCookbookVersion");
            }
            if (!TryRequiredString(entryEl, "maxCookbookVersion", out string? maxVer))
            {
                return EntryTypeMismatch(idx, "maxCookbookVersion");
            }

            string? releaseNotesUrl = null;
            if (entryEl.TryGetProperty("releaseNotesUrl", out JsonElement rnEl) &&
                rnEl.ValueKind != JsonValueKind.Null)
            {
                releaseNotesUrl = rnEl.ValueKind == JsonValueKind.String ? rnEl.GetString() : null;
                if (!IsHttpsUrl(releaseNotesUrl))
                {
                    return new ManifestValidationResult
                    {
                        Ok = false, Error = "invalid_release_notes_url",
                        Message = "scripts[" + idx + "].releaseNotesUrl must be an https:// URL when present.",
                    };
                }
            }

            entries.Add(new ApprovedEngineEntry(
                Name: entryName!,
                Version: entryVersion!,
                Sha256Upper: entrySha!.ToUpperInvariant(),
                DownloadUrl: downloadUrl!,
                Status: status,
                MinCookbookVersion: minVer!,
                MaxCookbookVersion: maxVer!,
                ReleaseNotesUrl: releaseNotesUrl));
        }

        return new ManifestValidationResult
        {
            Ok = true,
            SchemaVersion = schemaVer.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ManifestVersion = manifestVersion,
            Channel = channel,
            GeneratedAtUtc = generatedAtUtc,
            SigningKeyId = signingKeyId,
            Entries = entries,
        };
    }

    private static ManifestValidationResult MissingField(string name) => new()
    {
        Ok = false, Error = "missing_field", Message = "\"" + name + "\" is required.",
    };

    private static ManifestValidationResult EntryTypeMismatch(int idx, string field) => new()
    {
        Ok = false, Error = "type_mismatch",
        Message = "scripts[" + idx + "]." + field + " must be a non-empty string.",
    };

    private static bool TryRequiredString(JsonElement parent, string name, out string? value)
    {
        if (parent.TryGetProperty(name, out JsonElement el) &&
            el.ValueKind == JsonValueKind.String)
        {
            string? s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                value = s;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static string? TryGetString(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }
        return null;
    }

    private static bool IsHttpsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return false; }
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) { return false; }
        return string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAcceptableEntryDownloadUrl(string? value, bool allowLoopbackHttp)
    {
        if (string.IsNullOrWhiteSpace(value)) { return false; }
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) { return false; }
        if (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) { return true; }
        if (allowLoopbackHttp &&
            string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            uri.IsLoopback)
        {
            return true;
        }
        return false;
    }
}

internal static class ManifestSelector
{
    // Oracle Select-CompatibleEngineEntry parity: approved-only selection,
    // version range gating, highest version among approved candidates wins.
    internal static (ApprovedEngineEntry? Entry, string? Error, string? Message, int Evaluated) Select(
        IReadOnlyList<ApprovedEngineEntry> entries,
        string cookbookVersion,
        string? targetVersion,
        string? targetSha256)
    {
        if (!Version.TryParse(cookbookVersion, out Version? cookbookVer))
        {
            return (null, "invalid_cookbook_version",
                "CookbookVersion \"" + cookbookVersion + "\" is not a parseable version.", 0);
        }

        string? targetShaUpper = Sha256Hex.Normalize(targetSha256);

        int evaluated = 0;
        ApprovedEngineEntry? best = null;
        Version? bestVer = null;

        foreach (ApprovedEngineEntry entry in entries)
        {
            evaluated++;
            if (entry.Status != "approved") { continue; }

            if (!string.IsNullOrWhiteSpace(targetVersion) && entry.Version != targetVersion)
            {
                continue;
            }
            if (targetShaUpper is not null && entry.Sha256Upper != targetShaUpper)
            {
                continue;
            }

            if (!Version.TryParse(entry.MinCookbookVersion, out Version? minV)) { continue; }
            if (!Version.TryParse(entry.MaxCookbookVersion, out Version? maxV)) { continue; }
            if (cookbookVer < minV) { continue; }
            if (cookbookVer > maxV) { continue; }

            if (!Version.TryParse(entry.Version, out Version? entryVer)) { continue; }

            if (best is null || entryVer > bestVer)
            {
                best = entry;
                bestVer = entryVer;
            }
        }

        if (best is null)
        {
            return (null, "no_compatible_engine",
                "No approved script entry is compatible with cookbook version " + cookbookVersion + ".",
                evaluated);
        }

        return (best, null, null, evaluated);
    }
}

internal sealed record SignatureVerifyResult
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public string? CertThumbprint { get; init; }
    public string? CertSubject { get; init; }
    public string? PackageSha256 { get; init; }
}

internal static class SignatureVerifier
{
    // Oracle Update\Signature.psm1::Test-PackageSignature parity.
    // Detached .sig envelope: closed schema (every key required, no unknowns),
    // embedded cert SHA-1 thumbprint must self-match the envelope field, the
    // recomputed SHA-256 of the package must equal envelope.packageSha256, and
    // the cert's RSA public key must verify the raw signature against the
    // SHA-256 hash with PKCS#1 v1.5 padding. Never throws for documented
    // failure modes — returns a tagged result. Trust-anchor pinning is the
    // caller's responsibility (compare CertThumbprint to the pinned value).

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "schemaVersion", "packageFile", "packageSha256", "hashAlgorithm",
        "signatureAlgorithm", "signatureBase64", "signerCertBase64",
        "signerCertThumbprint", "signedAtUtc",
    };

    internal static SignatureVerifyResult Verify(string packagePath, string envelopePath)
    {
        if (!File.Exists(packagePath))
        {
            return Fail("package_missing", "Package file does not exist on disk.");
        }
        if (!File.Exists(envelopePath))
        {
            return Fail("envelope_missing", "Signature envelope file does not exist on disk.");
        }

        JsonElement envelope;
        try
        {
            using FileStream fs = File.OpenRead(envelopePath);
            using JsonDocument doc = JsonDocument.Parse(fs);
            envelope = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            return Fail("envelope_parse_failed",
                "Could not parse signature envelope JSON: " + ex.Message);
        }

        if (envelope.ValueKind != JsonValueKind.Object)
        {
            return Fail("envelope_not_object", "Signature envelope is not a JSON object.");
        }

        foreach (JsonProperty p in envelope.EnumerateObject())
        {
            if (!AllowedKeys.Contains(p.Name))
            {
                return Fail("unknown_field",
                    "Signature envelope has an unknown field: \"" + p.Name + "\".");
            }
        }
        foreach (string req in AllowedKeys)
        {
            if (!envelope.TryGetProperty(req, out _))
            {
                return Fail("missing_field",
                    "Signature envelope is missing required field: \"" + req + "\".");
            }
        }

        JsonElement svEl = envelope.GetProperty("schemaVersion");
        if (svEl.ValueKind != JsonValueKind.Number || !svEl.TryGetInt32(out int sv))
        {
            return Fail("type_mismatch", "\"schemaVersion\" must be an integer.");
        }
        if (sv != 1)
        {
            return Fail("unsupported_schema_version",
                "Signature envelope schemaVersion " + sv + " is not supported.");
        }

        foreach (string field in new[] { "packageFile", "signedAtUtc", "signatureBase64", "signerCertBase64" })
        {
            JsonElement el = envelope.GetProperty(field);
            if (el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString()))
            {
                return Fail("missing_field",
                    "\"" + field + "\" is required and must be a non-empty string.");
            }
        }

        JsonElement hashAlgEl = envelope.GetProperty("hashAlgorithm");
        string hashAlg = hashAlgEl.ValueKind == JsonValueKind.String ? (hashAlgEl.GetString() ?? "") : "";
        if (hashAlg != "SHA256")
        {
            return Fail("unsupported_hash_algorithm",
                "hashAlgorithm \"" + hashAlg + "\" is not supported.");
        }

        JsonElement sigAlgEl = envelope.GetProperty("signatureAlgorithm");
        string sigAlg = sigAlgEl.ValueKind == JsonValueKind.String ? (sigAlgEl.GetString() ?? "") : "";
        if (sigAlg != "RSA-PKCS1v15-SHA256")
        {
            return Fail("unsupported_signature_algorithm",
                "signatureAlgorithm \"" + sigAlg + "\" is not supported.");
        }

        string envelopeThumb = HexClean(envelope.GetProperty("signerCertThumbprint").GetString());
        if (envelopeThumb.Length != 40)
        {
            return Fail("invalid_thumbprint",
                "\"signerCertThumbprint\" must be a 40-character SHA-1 hex thumbprint.");
        }

        string envelopeHash = HexClean(envelope.GetProperty("packageSha256").GetString());
        if (envelopeHash.Length != 64)
        {
            return Fail("invalid_package_hash",
                "\"packageSha256\" must be a 64-character SHA-256 hex digest.");
        }

        X509Certificate2? cert = null;
        try
        {
            byte[] certBytes = Convert.FromBase64String(envelope.GetProperty("signerCertBase64").GetString() ?? "");
            cert = new X509Certificate2(certBytes);
        }
        catch (Exception ex)
        {
            return Fail("cert_decode_failed",
                "Could not decode signer certificate from base64: " + ex.Message);
        }

        try
        {
            string certThumb = cert.Thumbprint?.ToUpperInvariant() ?? string.Empty;
            string certSubject = cert.Subject ?? string.Empty;

            if (!string.Equals(certThumb, envelopeThumb, StringComparison.Ordinal))
            {
                return new SignatureVerifyResult
                {
                    Ok = false,
                    Error = "thumbprint_mismatch",
                    Message = "Embedded cert thumbprint (" + certThumb +
                        ") does not match envelope signerCertThumbprint (" + envelopeThumb + ").",
                    CertThumbprint = certThumb,
                    CertSubject = certSubject,
                };
            }

            string packageHash = Sha256Hex.OfFile(packagePath);
            if (!string.Equals(packageHash, envelopeHash, StringComparison.Ordinal))
            {
                return new SignatureVerifyResult
                {
                    Ok = false,
                    Error = "hash_mismatch",
                    Message = "Package SHA-256 (" + packageHash +
                        ") does not match envelope packageSha256 (" + envelopeHash + ").",
                    CertThumbprint = certThumb,
                    CertSubject = certSubject,
                    PackageSha256 = packageHash,
                };
            }

            using RSA? rsa = cert.GetRSAPublicKey();
            if (rsa is null)
            {
                return new SignatureVerifyResult
                {
                    Ok = false,
                    Error = "no_rsa_public_key",
                    Message = "Signer certificate does not expose an RSA public key.",
                    CertThumbprint = certThumb,
                    CertSubject = certSubject,
                    PackageSha256 = packageHash,
                };
            }

            byte[] sigBytes = Convert.FromBase64String(envelope.GetProperty("signatureBase64").GetString() ?? "");
            byte[] hashBytes = Convert.FromHexString(packageHash);
            bool verified = rsa.VerifyHash(hashBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!verified)
            {
                return new SignatureVerifyResult
                {
                    Ok = false,
                    Error = "signature_invalid",
                    Message = "RSA public-key verification of the signature failed.",
                    CertThumbprint = certThumb,
                    CertSubject = certSubject,
                    PackageSha256 = packageHash,
                };
            }

            return new SignatureVerifyResult
            {
                Ok = true,
                CertThumbprint = certThumb,
                CertSubject = certSubject,
                PackageSha256 = packageHash,
            };
        }
        catch (Exception ex)
        {
            return Fail("verification_threw", "Verification raised an exception: " + ex.Message);
        }
        finally
        {
            cert.Dispose();
        }
    }

    private static SignatureVerifyResult Fail(string error, string message) => new()
    {
        Ok = false, Error = error, Message = message,
    };

    private static string HexClean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return string.Empty; }
        StringBuilder sb = new(value.Length);
        foreach (char c in value)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (ok) { sb.Append(char.ToUpperInvariant(c)); }
        }
        return sb.ToString();
    }
}
