using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PAXCookbook.App;

// Native port of the PowerShell parity oracle
// app\broker\Engine\Acquisition.psm1::Get-PaxAcquisitionState. This module
// detects whether an approved managed PAX engine is present and valid. It is
// strictly READ-ONLY with respect to acquisition: it never creates, repairs,
// copies, downloads, packages, mutates, or invokes the managed engine file or
// the install-state metadata. The oracle validates presence only; the native
// runtime additionally re-hashes the managed engine file when it exists and
// compares against the recorded approved hash so a tampered or stale engine is
// surfaced as an explicit "invalid" state rather than silently trusted.
internal static class EngineAcquisition
{
    private const string ProductFolder = "PAXCookbook";
    private const string InstallStateFile = "install-state.json";
    private const string EngineFolder = "Engine";
    private const string ManagedEngineFile = "PAX_Purview_Audit_Log_Processor.ps1";

    // Resolves the LocalApplicationData base directory that anchors the managed
    // engine tree. Explicit test override wins; otherwise honor the LOCALAPPDATA
    // environment variable first (oracle parity — the installer and broker share
    // this anchor), then fall back to the platform LocalApplicationData folder.
    public static string ResolveLocalAppDataBase(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        string? env = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public static string GetInstallStatePath(string localAppDataBase)
        => Path.Combine(localAppDataBase, ProductFolder, InstallStateFile);

    public static string GetManagedEnginePath(string localAppDataBase)
        => Path.Combine(localAppDataBase, ProductFolder, EngineFolder, ManagedEngineFile);

    // Computes the current acquisition state. Pure read: opens the managed
    // engine file and install-state.json for reading only, never writing.
    public static EngineAcquisitionResult Resolve(VersionInfo version, string localAppDataBase)
    {
        string installStatePath = GetInstallStatePath(localAppDataBase);
        string enginePath = GetManagedEnginePath(localAppDataBase);
        bool present = File.Exists(enginePath);
        string policy = NormalizePolicy(version.PaxAcquisitionPolicy);

        if (policy == "embedded")
        {
            // Bundled-engine legacy mode. Byte integrity for the bundled engine
            // is validated separately at broker startup; here presence is the
            // acquisition signal, matching the oracle's embedded-legacy branch.
            return new EngineAcquisitionResult
            {
                Policy = "embedded",
                State = "embedded-legacy",
                IsAcquired = present,
                AcquisitionRequired = !present,
                ManagedEnginePathPresent = present,
                RecordedSha256 = version.PaxSha256,
                Version = version.PaxVersion,
                Source = "embedded",
                LastAttemptError = null,
                Message = present ? "Bundled engine present." : "Bundled engine missing.",
                InstallStatePath = installStatePath,
                ManagedEnginePath = enginePath,
            };
        }

        PaxAcquisitionBlock? block = ReadAcquisitionBlock(installStatePath);

        if (block is null)
        {
            // No install-state metadata block: the engine was never acquired.
            return Pending(policy, present, null, installStatePath, enginePath,
                "PAX engine has not been acquired yet.");
        }

        if (block.Pending == true)
        {
            if (!string.IsNullOrEmpty(block.LastAttemptError))
            {
                return new EngineAcquisitionResult
                {
                    Policy = policy,
                    State = "failed",
                    IsAcquired = false,
                    AcquisitionRequired = true,
                    ManagedEnginePathPresent = present,
                    RecordedSha256 = block.Sha256,
                    Version = block.Version,
                    Source = block.Source,
                    LastAttemptError = block.LastAttemptError,
                    Message = "The last PAX engine acquisition attempt failed.",
                    InstallStatePath = installStatePath,
                    ManagedEnginePath = enginePath,
                };
            }

            return new EngineAcquisitionResult
            {
                Policy = policy,
                State = "acquisition_pending",
                IsAcquired = false,
                AcquisitionRequired = true,
                ManagedEnginePathPresent = present,
                RecordedSha256 = block.Sha256,
                Version = block.Version,
                Source = block.Source,
                LastAttemptError = block.LastAttemptError,
                Message = "PAX engine acquisition is in flight.",
                InstallStatePath = installStatePath,
                ManagedEnginePath = enginePath,
            };
        }

        // Not pending. The metadata must claim a completed acquisition (an
        // activation timestamp plus a recorded approved hash) before the engine
        // file is trusted at all.
        bool claimsAcquired = block.HasActivatedAt && !string.IsNullOrEmpty(block.Sha256);
        if (!claimsAcquired)
        {
            return Pending(policy, present, block, installStatePath, enginePath,
                "PAX engine acquisition is incomplete.");
        }

        if (!present)
        {
            // Metadata records a completed acquisition but the managed engine
            // file is gone.
            return new EngineAcquisitionResult
            {
                Policy = policy,
                State = "missing",
                IsAcquired = false,
                AcquisitionRequired = true,
                ManagedEnginePathPresent = false,
                RecordedSha256 = block.Sha256,
                Version = block.Version,
                Source = block.Source,
                LastAttemptError = block.LastAttemptError,
                Message = "Recorded PAX engine metadata exists but the managed engine file is missing.",
                InstallStatePath = installStatePath,
                ManagedEnginePath = enginePath,
            };
        }

        // File present and a recorded approved hash exists: re-hash the file
        // (read-only) and compare. A mismatch is an explicit invalid state.
        string? actual = TryComputeSha256(enginePath);
        bool match = actual is not null && HexEquals(actual, block.Sha256!);
        if (!match)
        {
            return new EngineAcquisitionResult
            {
                Policy = policy,
                State = "invalid",
                IsAcquired = false,
                AcquisitionRequired = true,
                ManagedEnginePathPresent = true,
                RecordedSha256 = block.Sha256,
                Version = block.Version,
                Source = block.Source,
                LastAttemptError = block.LastAttemptError,
                Message = "The managed PAX engine file does not match the recorded approved hash.",
                InstallStatePath = installStatePath,
                ManagedEnginePath = enginePath,
            };
        }

        return new EngineAcquisitionResult
        {
            Policy = policy,
            State = "acquired",
            IsAcquired = true,
            AcquisitionRequired = false,
            ManagedEnginePathPresent = true,
            RecordedSha256 = block.Sha256,
            Version = block.Version,
            Source = block.Source,
            LastAttemptError = block.LastAttemptError,
            Message = "An approved managed PAX engine is present and valid.",
            InstallStatePath = installStatePath,
            ManagedEnginePath = enginePath,
        };
    }

    private static EngineAcquisitionResult Pending(
        string policy, bool present, PaxAcquisitionBlock? block,
        string installStatePath, string enginePath, string message)
    {
        return new EngineAcquisitionResult
        {
            Policy = policy,
            State = "acquisition_pending",
            IsAcquired = false,
            AcquisitionRequired = true,
            ManagedEnginePathPresent = present,
            RecordedSha256 = block?.Sha256,
            Version = block?.Version,
            Source = block?.Source,
            LastAttemptError = block?.LastAttemptError,
            Message = message,
            InstallStatePath = installStatePath,
            ManagedEnginePath = enginePath,
        };
    }

    // Read-only parse of install-state.json -> the paxAcquisition block.
    // Returns null when the file is absent, unparseable, has no object root, or
    // has no paxAcquisition object. Never writes or repairs the file.
    private static PaxAcquisitionBlock? ReadAcquisitionBlock(string installStatePath)
    {
        try
        {
            if (!File.Exists(installStatePath))
            {
                return null;
            }

            using var fs = new FileStream(
                installStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument doc = JsonDocument.Parse(fs);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("paxAcquisition", out JsonElement pax) ||
                pax.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var block = new PaxAcquisitionBlock();

            if (pax.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String)
            {
                block.Sha256 = sha.GetString();
            }
            if (pax.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String)
            {
                block.Version = ver.GetString();
            }
            if (pax.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String)
            {
                block.Source = src.GetString();
            }
            if (pax.TryGetProperty("pending", out var pending))
            {
                if (pending.ValueKind == JsonValueKind.True) { block.Pending = true; }
                else if (pending.ValueKind == JsonValueKind.False) { block.Pending = false; }
            }
            if (pax.TryGetProperty("activatedAtUtc", out var activated) &&
                activated.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(activated.GetString()))
            {
                block.HasActivatedAt = true;
            }
            if (pax.TryGetProperty("lastAttemptError", out var lastErr))
            {
                block.LastAttemptError = SummarizeError(lastErr);
            }

            return block;
        }
        catch
        {
            // Absent, locked, or malformed install-state metadata is treated as
            // "no recorded acquisition". Read-only behavior: nothing is created
            // or repaired.
            return null;
        }
    }

    private static string? SummarizeError(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string? s = element.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            string? code = null;
            string? message = null;
            if (element.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            {
                code = e.GetString();
            }
            if (element.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                message = m.GetString();
            }

            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(message))
            {
                return code + ": " + message;
            }
            if (!string.IsNullOrWhiteSpace(code)) { return code; }
            if (!string.IsNullOrWhiteSpace(message)) { return message; }
        }

        return null;
    }

    private static string? TryComputeSha256(string path)
    {
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] hash = SHA256.HashData(fs);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null;
        }
    }

    private static bool HexEquals(string a, string b)
        => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePolicy(string? policy)
    {
        string p = (policy ?? string.Empty).Trim().ToLowerInvariant();
        return p == "external" ? "external" : "embedded";
    }

    private sealed class PaxAcquisitionBlock
    {
        public string? Sha256;
        public string? Version;
        public string? Source;
        public bool? Pending;
        public bool HasActivatedAt;
        public string? LastAttemptError;
    }
}

// Immutable result of an engine acquisition-state evaluation, plus the two
// serialization shapes the runtime exposes: the read-only state payload (for
// GET /api/v1/setup/acquire-pax/state, consumed by the SPA engine overlay) and
// the 409 acquisitionRequired gate body (for blocked cook/scheduler mutations).
internal sealed class EngineAcquisitionResult
{
    public required string Policy { get; init; }
    public required string State { get; init; }
    public required bool IsAcquired { get; init; }
    public required bool AcquisitionRequired { get; init; }
    public required bool ManagedEnginePathPresent { get; init; }
    public string? RecordedSha256 { get; init; }
    public string? Version { get; init; }
    public string? Source { get; init; }
    public string? LastAttemptError { get; init; }
    public required string Message { get; init; }
    public required string InstallStatePath { get; init; }
    public required string ManagedEnginePath { get; init; }

    // Read-only state payload. Field names preserve SPA engine-overlay
    // compatibility: pax-engine-overlay.js::isAcquiredBody reads policy, state,
    // and isAcquired. No local source paths, no actual recomputed hash, no
    // secrets, and no raw script content are exposed.
    public object ToStatePayload()
    {
        return new
        {
            endpoint = "GET /api/v1/setup/acquire-pax/state",
            stage = "v1_office_grade_x13",
            policy = Policy,
            state = State,
            isAcquired = IsAcquired,
            acquisitionRequired = AcquisitionRequired,
            managedEnginePathPresent = ManagedEnginePathPresent,
            sha256 = RecordedSha256,
            version = Version,
            source = Source,
            lastAttemptError = LastAttemptError,
            message = Message,
            capabilities = new
            {
                stateImplemented = true,
                downloadImplemented = false,
                uploadImplemented = false,
                localFilePathUploadImplemented = false,
                byteUploadImplemented = false,
                cancelImplemented = false,
                manifestFetchImplemented = false,
            },
            x13 = new
            {
                note = "Engine acquisition STATE detection and the Engine Required gate are implemented read-only. No engine bytes are read for acquisition, copied, downloaded, packaged, mutated, or invoked, and install-state metadata is never created or repaired by this runtime.",
            },
        };
    }

    // 409 acquisitionRequired gate body. Shape matches api.js, which dispatches
    // cookbook:acquisitionRequired for HTTP 409 with body.code ===
    // 'acquisitionRequired' and reads endpoint, state, isLegacyEmbedded,
    // message, and details. details carries only the safe state snapshot.
    public object ToGate409Body(string method, string path)
    {
        return new
        {
            code = "acquisitionRequired",
            error = "acquisitionRequired",
            endpoint = method + " " + path,
            state = State,
            isLegacyEmbedded = false,
            acquisitionRequired = true,
            message = "An approved managed PAX engine must be acquired before this operation can run.",
            details = new
            {
                policy = Policy,
                state = State,
                isAcquired = IsAcquired,
                managedEnginePathPresent = ManagedEnginePathPresent,
                sha256 = RecordedSha256,
                version = Version,
                source = Source,
                lastAttemptError = LastAttemptError,
            },
        };
    }
}
