// X14 activation, staging, and install-state writer. Native port of:
//   app\broker\Engine\Acquisition.psm1::Get-PaxScriptByLocalFile
//   app\broker\Engine\Acquisition.psm1::Get-PaxScriptByUploadBytes
//   app\broker\Engine\Acquisition.psm1::Set-PaxScriptActivated
//   app\broker\Engine\Acquisition.psm1::Write-PaxAcquisitionInstallState
//   app\broker\Routes\Setup.ps1::Write-SetupAcquisitionFailure
//
// This module is the ONLY surface in the native runtime that writes to
// the canonical PAX engine path or install-state.json. It enforces the
// byte-preservation chain (pre-write hash, copy to temp, post-write
// hash, atomic File.Move-with-overwrite, post-move hash) and the
// install-state atomic write (temp + Move).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PAXCookbook.App;

internal static class AcquisitionSources
{
    internal const string Download = "download";
    internal const string LocalFile = "local-file";
    internal const string Automation = "automation";

    internal static bool IsValid(string? token)
        => token == Download || token == LocalFile || token == Automation;
}

internal sealed record StageResult
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public string? StagedPath { get; init; }
    public string? Sha256 { get; init; }
    public int? ByteCount { get; init; }
    public int? ByteCap { get; init; }
}

internal static class StagingArea
{
    // Creates a fresh per-acquisition work directory under
    // <updatesDir>\engine\<utc-stamp>-<rand>. Mirrors the oracle's
    // New-SetupAcquisitionWorkDirectory.
    internal static string CreateWorkDirectory(string updatesDir)
    {
        if (string.IsNullOrWhiteSpace(updatesDir))
        {
            throw new ArgumentException("updatesDir is required.", nameof(updatesDir));
        }
        string ts = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        string rand = Guid.NewGuid().ToString("N").Substring(0, 8);
        string work = Path.Combine(updatesDir, "engine", ts + "-" + rand);
        Directory.CreateDirectory(work);
        return work;
    }

    // Writes the supplied bytes to a fresh .ps1 file under workDir, then
    // re-hashes the on-disk file and verifies it equals the input hash.
    // Caller has already enforced the size cap and may have already
    // verified the bytes against the expected pin; this just preserves
    // them onto staging.
    internal static StageResult StageBytes(string workDir, byte[] bytes, string prefix)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return new StageResult { Ok = false, Error = "empty_body",
                Message = "Bytes argument is null or empty." };
        }
        if (bytes.Length > FetchLimits.PaxScriptMaxBytes)
        {
            return new StageResult { Ok = false, Error = "script_too_large",
                Message = "Bytes length " + bytes.Length + " exceeds cap of " +
                    FetchLimits.PaxScriptMaxBytes + " bytes.",
                ByteCount = bytes.Length, ByteCap = FetchLimits.PaxScriptMaxBytes };
        }

        string preHash = Sha256Hex.OfBytes(bytes);

        string stagedName = "staged-" + prefix + "-" + Guid.NewGuid().ToString("N") + ".ps1";
        string stagedPath = Path.Combine(workDir, stagedName);

        try
        {
            Directory.CreateDirectory(workDir);
            File.WriteAllBytes(stagedPath, bytes);
        }
        catch (Exception ex)
        {
            return new StageResult { Ok = false, Error = "staging_write_failed",
                Message = "Failed to write staged file \"" + stagedPath + "\": " + ex.Message };
        }

        string reHash;
        try
        {
            reHash = Sha256Hex.OfFile(stagedPath);
        }
        catch (Exception ex)
        {
            try { File.Delete(stagedPath); } catch { /* best effort */ }
            return new StageResult { Ok = false, Error = "post_write_hash_mismatch",
                Message = "Failed to re-hash staged file: " + ex.Message };
        }

        if (!string.Equals(reHash, preHash, StringComparison.Ordinal))
        {
            try { File.Delete(stagedPath); } catch { /* best effort */ }
            return new StageResult { Ok = false, Error = "post_write_hash_mismatch",
                Message = "Staged file SHA-256 " + reHash + " does not match pre-write hash " + preHash + "." };
        }

        return new StageResult
        {
            Ok = true,
            StagedPath = stagedPath,
            Sha256 = preHash,
            ByteCount = bytes.Length,
        };
    }
}

internal sealed record ActivationResult
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public string? CanonicalPath { get; init; }
    public string? Sha256 { get; init; }
    public string? Version { get; init; }
    public string? Source { get; init; }
    public string? ValidatedAtUtc { get; init; }
    public string? ActivatedAtUtc { get; init; }
    public string? StatePath { get; init; }
}

internal sealed record ActivationRequest
{
    public required string StagedFilePath { get; init; }
    public required string ExpectedSha256 { get; init; }
    public required string Version { get; init; }
    public required string CanonicalScriptPath { get; init; }
    public required string Source { get; init; }
    public string? ManifestId { get; init; }
    public string? ManifestHash { get; init; }
    public string? ManifestVersion { get; init; }
    public required string StatePath { get; init; }
}

internal static class ScriptActivator
{
    internal static ActivationResult Activate(ActivationRequest req)
    {
        if (!AcquisitionSources.IsValid(req.Source))
        {
            return Fail("invalid_source",
                "Source \"" + req.Source + "\" is not in the allowed set: download, local-file, automation.");
        }
        if (string.IsNullOrWhiteSpace(req.Version))
        {
            return Fail("invalid_version", "Version is required.");
        }
        string expected = (req.ExpectedSha256 ?? string.Empty).ToUpperInvariant();
        if (expected.Length != 64 || !IsHex(expected))
        {
            return Fail("invalid_expected_sha",
                "ExpectedSha256 \"" + req.ExpectedSha256 + "\" is not a 64-character hex string.");
        }
        if (string.IsNullOrWhiteSpace(req.CanonicalScriptPath))
        {
            return Fail("canonical_write_failed", "CanonicalScriptPath is empty.");
        }
        if (!File.Exists(req.StagedFilePath))
        {
            return Fail("staged_file_missing", "Staged file does not exist: " + req.StagedFilePath);
        }

        string stagedHash;
        try
        {
            stagedHash = Sha256Hex.OfFile(req.StagedFilePath);
        }
        catch (Exception ex)
        {
            return Fail("staged_hash_mismatch",
                "Failed to hash staged file \"" + req.StagedFilePath + "\": " + ex.Message);
        }
        if (!string.Equals(stagedHash, expected, StringComparison.Ordinal))
        {
            return Fail("staged_hash_mismatch",
                "Staged file SHA-256 " + stagedHash + " does not match ExpectedSha256 " + expected + ".");
        }

        string? canonDir = Path.GetDirectoryName(req.CanonicalScriptPath);
        if (!string.IsNullOrEmpty(canonDir))
        {
            try { Directory.CreateDirectory(canonDir); }
            catch (Exception ex)
            {
                return Fail("canonical_dir_create_failed",
                    "Failed to create canonical directory \"" + canonDir + "\": " + ex.Message);
            }
        }

        string tmpPath = req.CanonicalScriptPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(req.StagedFilePath, tmpPath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            return Fail("canonical_write_failed",
                "Failed to copy staged file to canonical temp \"" + tmpPath + "\": " + ex.Message);
        }

        string tmpHash;
        try
        {
            tmpHash = Sha256Hex.OfFile(tmpPath);
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            return Fail("post_write_hash_mismatch",
                "Failed to hash canonical temp \"" + tmpPath + "\": " + ex.Message);
        }
        if (!string.Equals(tmpHash, expected, StringComparison.Ordinal))
        {
            TryDelete(tmpPath);
            return Fail("post_write_hash_mismatch",
                "Canonical temp SHA-256 " + tmpHash + " does not match ExpectedSha256 " + expected + ".");
        }

        try
        {
            File.Move(tmpPath, req.CanonicalScriptPath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            return Fail("canonical_write_failed",
                "Failed to move canonical temp onto canonical path \"" + req.CanonicalScriptPath + "\": " + ex.Message);
        }

        string postHash;
        try
        {
            postHash = Sha256Hex.OfFile(req.CanonicalScriptPath);
        }
        catch (Exception ex)
        {
            return Fail("post_write_hash_mismatch",
                "Failed to hash post-move canonical file \"" + req.CanonicalScriptPath + "\": " + ex.Message);
        }
        if (!string.Equals(postHash, expected, StringComparison.Ordinal))
        {
            return Fail("post_write_hash_mismatch",
                "Canonical SHA-256 " + postHash + " does not match ExpectedSha256 " + expected + ".");
        }

        string nowUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        try
        {
            InstallStateWriter.WriteSuccess(req.StatePath, new SuccessFields
            {
                Pending = false,
                Source = req.Source,
                Version = req.Version,
                Sha256 = expected,
                ManifestId = req.ManifestId,
                ManifestHash = req.ManifestHash,
                ManifestVersion = req.ManifestVersion,
                ValidatedAtUtc = nowUtc,
                ActivatedAtUtc = nowUtc,
            });
        }
        catch (Exception ex)
        {
            return new ActivationResult
            {
                Ok = false,
                Error = "install_state_write_failed",
                Message = "Activation copied canonical bytes but install-state write failed: " + ex.Message,
                CanonicalPath = req.CanonicalScriptPath,
                Sha256 = expected,
            };
        }

        return new ActivationResult
        {
            Ok = true,
            CanonicalPath = req.CanonicalScriptPath,
            Sha256 = expected,
            Version = req.Version,
            Source = req.Source,
            ValidatedAtUtc = nowUtc,
            ActivatedAtUtc = nowUtc,
            StatePath = req.StatePath,
        };
    }

    private static ActivationResult Fail(string error, string message)
        => new() { Ok = false, Error = error, Message = message };

    private static bool IsHex(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            if (!ok) { return false; }
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { /* best effort */ }
    }
}

internal sealed record SuccessFields
{
    public required bool Pending { get; init; }
    public required string Source { get; init; }
    public required string Version { get; init; }
    public required string Sha256 { get; init; }
    public string? ManifestId { get; init; }
    public string? ManifestHash { get; init; }
    public string? ManifestVersion { get; init; }
    public required string ValidatedAtUtc { get; init; }
    public required string ActivatedAtUtc { get; init; }
}

internal sealed record FailureDetails
{
    public required string Error { get; init; }
    public required string Endpoint { get; init; }
    public required string Message { get; init; }
    public IReadOnlyDictionary<string, object?>? Extras { get; init; }
}

internal static class InstallStateWriter
{
    // All writes to install-state.json funnel through this module so the
    // sibling-preserve / merge-into-paxAcquisition / atomic-temp-rename
    // contract is enforced exactly once.

    internal static IReadOnlyDictionary<string, object?> WriteSuccess(string statePath, SuccessFields fields)
    {
        Dictionary<string, object?> incoming = new(StringComparer.Ordinal)
        {
            ["pending"] = fields.Pending,
            ["source"] = fields.Source,
            ["version"] = fields.Version,
            ["sha256"] = fields.Sha256,
            ["manifestId"] = fields.ManifestId,
            ["manifestHash"] = fields.ManifestHash,
            ["manifestVersion"] = fields.ManifestVersion,
            ["validatedAtUtc"] = fields.ValidatedAtUtc,
            ["activatedAtUtc"] = fields.ActivatedAtUtc,
            ["lastAttemptError"] = null,
        };
        return MergeAndWrite(statePath, incoming);
    }

    internal static IReadOnlyDictionary<string, object?> WriteFailure(string statePath, FailureDetails details)
    {
        string nowUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
        Dictionary<string, object?> lastErr = new(StringComparer.Ordinal)
        {
            ["error"] = details.Error,
            ["atUtc"] = nowUtc,
            ["endpoint"] = details.Endpoint,
            ["message"] = details.Message,
        };
        if (details.Extras is not null)
        {
            foreach (KeyValuePair<string, object?> kv in details.Extras)
            {
                if (!lastErr.ContainsKey(kv.Key))
                {
                    lastErr[kv.Key] = kv.Value;
                }
            }
        }
        Dictionary<string, object?> incoming = new(StringComparer.Ordinal)
        {
            ["pending"] = true,
            ["lastAttemptError"] = lastErr,
        };
        return MergeAndWrite(statePath, incoming);
    }

    internal static IReadOnlyDictionary<string, object?> WriteCancel(string statePath, string endpoint)
    {
        return WriteFailure(statePath, new FailureDetails
        {
            Error = "cancelled_by_operator",
            Endpoint = endpoint,
            Message = "Acquisition attempt cancelled by operator via " + endpoint + ".",
        });
    }

    // Reads the existing install-state document (treating absent or
    // corrupt as empty), preserves every top-level sibling key, merges
    // the incoming fields into the existing paxAcquisition block
    // (so non-mentioned fields survive), and writes atomically via a
    // same-directory temp file plus File.Move(overwrite).
    private static Dictionary<string, object?> MergeAndWrite(
        string statePath, Dictionary<string, object?> incoming)
    {
        if (string.IsNullOrWhiteSpace(statePath))
        {
            throw new ArgumentException("statePath is required.", nameof(statePath));
        }
        string? stateDir = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrEmpty(stateDir))
        {
            Directory.CreateDirectory(stateDir);
        }

        Dictionary<string, object?> doc = ReadExistingOrEmpty(statePath);

        Dictionary<string, object?> existingPax;
        if (doc.TryGetValue("paxAcquisition", out object? rawPax) &&
            rawPax is Dictionary<string, object?> existing)
        {
            existingPax = new Dictionary<string, object?>(existing, StringComparer.Ordinal);
        }
        else
        {
            existingPax = new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        foreach (KeyValuePair<string, object?> kv in incoming)
        {
            existingPax[kv.Key] = kv.Value;
        }
        doc["paxAcquisition"] = existingPax;

        JsonSerializerOptions opts = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        string json = JsonSerializer.Serialize(doc, opts);

        string tmp = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(tmp, Encoding.UTF8.GetBytes(json));
        try
        {
            File.Move(tmp, statePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) { File.Delete(tmp); } } catch { /* best effort */ }
            throw;
        }

        return existingPax;
    }

    private static Dictionary<string, object?> ReadExistingOrEmpty(string statePath)
    {
        Dictionary<string, object?> doc = new(StringComparer.Ordinal);
        if (!File.Exists(statePath))
        {
            return doc;
        }
        try
        {
            using FileStream fs = File.OpenRead(statePath);
            using JsonDocument parsed = JsonDocument.Parse(fs);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in parsed.RootElement.EnumerateObject())
                {
                    doc[p.Name] = ConvertJsonElement(p.Value);
                }
            }
        }
        catch
        {
            // Corrupt existing document — preserve nothing, start fresh.
            doc.Clear();
        }
        return doc;
    }

    // Recursive JsonElement -> CLR-object translator that round-trips
    // through JsonSerializer.Serialize cleanly. We avoid keeping any
    // JsonElement references after the parsed document is disposed.
    private static object? ConvertJsonElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, object?> obj = new(StringComparer.Ordinal);
                foreach (JsonProperty p in el.EnumerateObject())
                {
                    obj[p.Name] = ConvertJsonElement(p.Value);
                }
                return obj;
            case JsonValueKind.Array:
                List<object?> arr = new();
                foreach (JsonElement item in el.EnumerateArray())
                {
                    arr.Add(ConvertJsonElement(item));
                }
                return arr;
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out long l)) { return l; }
                if (el.TryGetDouble(out double d)) { return d; }
                return el.GetRawText();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }
}
