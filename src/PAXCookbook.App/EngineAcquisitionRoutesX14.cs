// X14 route handlers. Native port of:
//   app\broker\Routes\Setup.ps1::Invoke-SetupAcquirePaxDownload
//   app\broker\Routes\Setup.ps1::Invoke-SetupAcquirePaxUpload (JSON branch)
//   app\broker\Routes\Setup.ps1::Invoke-SetupAcquirePaxUploadBytes
//   app\broker\Routes\Setup.ps1::Invoke-SetupAcquirePaxCancel
//   app\broker\Routes\Setup.ps1::Invoke-SetupAcquirePaxState (X14 body shape)
//
// These handlers are the ONLY callers that write to the canonical PAX engine
// path or install-state.json. They funnel ALL failures through
// InstallStateWriter.WriteFailure so the install-state lastAttemptError
// block stays in sync with the HTTP response body, and every success path
// goes through ScriptActivator.Activate.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace PAXCookbook.App;

internal static class EngineAcquisitionRoutesX14
{
    // Oracle parity: app/broker/Routes/Setup.ps1 Invoke-SetupAcquirePaxState
    // emits stage='phase_13_stage_3' for the GET /state body and all sibling
    // failure / success envelopes from the acquisition routes. The native
    // runtime mirrors that literal value verbatim so the SPA's stage-aware
    // logic does not need branching for native vs broker.
    internal const string Stage = "phase_13_stage_3";

    // -----------------------------------------------------------------
    // GET /api/v1/setup/acquire-pax/state -- X14 body shape
    // -----------------------------------------------------------------
    internal static object BuildStatePayload(VersionInfo version, string engineLocalAppDataBase)
    {
        EngineAcquisitionResult engine = EngineAcquisition.Resolve(version, engineLocalAppDataBase);
        string installStatePath = EngineAcquisition.GetInstallStatePath(engineLocalAppDataBase);
        string enginePath = EngineAcquisition.GetManagedEnginePath(engineLocalAppDataBase);
        PaxAcquisitionSnapshot snap = PaxAcquisitionSnapshot.Read(installStatePath);

        return new
        {
            endpoint = "GET /api/v1/setup/acquire-pax/state",
            stage = Stage,
            policy = engine.Policy,
            manifestSignaturePolicy = string.IsNullOrWhiteSpace(version.ManifestSignaturePolicy)
                ? "required"
                : version.ManifestSignaturePolicy,
            state = engine.State,
            isAcquired = engine.IsAcquired,
            pending = snap.Pending,
            isLegacyEmbedded = string.Equals(engine.Policy, "embedded", StringComparison.Ordinal),
            brokerStartup = new
            {
                acquisitionRequired = !engine.IsAcquired,
                reason = engine.IsAcquired ? null :
                    (engine.ManagedEnginePathPresent ? "pax_script_hash_mismatch" : "pax_script_absent"),
            },
            expected = new
            {
                paxScriptVersion = version.PaxVersion,
                paxScriptSha256 = (version.PaxSha256 ?? string.Empty).ToUpperInvariant(),
                cookbookVersion = version.CookbookVersion,
            },
            canonicalScript = new
            {
                path = enginePath,
                present = engine.ManagedEnginePathPresent,
            },
            installState = new
            {
                path = installStatePath,
                source = snap.Source,
                version = snap.Version,
                sha256 = snap.Sha256,
                manifestId = snap.ManifestId,
                manifestHash = snap.ManifestHash,
                manifestVersion = snap.ManifestVersion,
                validatedAtUtc = snap.ValidatedAtUtc,
                activatedAtUtc = snap.ActivatedAtUtc,
                lastAttemptError = snap.LastAttemptError,
            },
            capabilities = new
            {
                stateImplemented = true,
                downloadImplemented = true,
                uploadImplemented = false,
                localFilePathUploadImplemented = true,
                byteUploadImplemented = true,
                cancelImplemented = true,
                manifestFetchImplemented = true,
            },
        };
    }

    // -----------------------------------------------------------------
    // POST /api/v1/setup/acquire-pax/download
    // -----------------------------------------------------------------
    internal static async Task<IResult> HandleDownloadAsync(
        HttpContext ctx, VersionInfo version, string engineLocalAppDataBase)
    {
        const string endpoint = "POST /api/v1/setup/acquire-pax/download";

        (string? targetVersion, string? targetSha) = await ReadTargetsFromJsonBodyAsync(ctx);

        StageContext? stage = await PrepareStageAsync(
            ctx, endpoint, version, engineLocalAppDataBase, targetVersion, targetSha);
        if (stage is null) { return Results.Empty; } // PrepareStageAsync already wrote the response

        // Fetch the script bytes from the entry's downloadUrl.
        UrlValidationResult urlCheck = UrlValidator.Validate(stage.Entry.DownloadUrl);
        if (!urlCheck.Ok)
        {
            return WriteFailure(stage.StatePath, endpoint, 502,
                urlCheck.Error ?? "invalid_download_url",
                urlCheck.Message ?? "Entry downloadUrl is not acceptable.");
        }
        FetchResult fetch = await ScriptFetcher.FetchScriptAsync(
            urlCheck.Uri!, version.CookbookVersion, ctx.RequestAborted);
        if (!fetch.Ok || fetch.Bytes is null)
        {
            return WriteFailure(stage.StatePath, endpoint, 502,
                fetch.Error ?? "script_fetch_failed",
                fetch.Message ?? "Script fetch failed.");
        }

        // Verify the downloaded bytes match the entry sha256.
        string downloadedSha = Sha256Hex.OfBytes(fetch.Bytes);
        if (!string.Equals(downloadedSha, stage.Entry.Sha256Upper, StringComparison.Ordinal))
        {
            return WriteFailure(stage.StatePath, endpoint, 502, "script_hash_mismatch",
                "Downloaded script SHA-256 " + downloadedSha + " does not match approved entry sha256 " +
                stage.Entry.Sha256Upper + ".",
                extras: new Dictionary<string, object?> {
                    ["downloadedSha256"] = downloadedSha,
                    ["expectedSha256"]   = stage.Entry.Sha256Upper,
                });
        }

        // Stage the bytes.
        StageResult staged = StagingArea.StageBytes(stage.WorkDir, fetch.Bytes, "download");
        if (!staged.Ok)
        {
            return WriteFailure(stage.StatePath, endpoint, 502,
                staged.Error ?? "staging_write_failed",
                staged.Message ?? "Failed to stage downloaded bytes.");
        }

        // Activate.
        ActivationResult act = ScriptActivator.Activate(new ActivationRequest
        {
            StagedFilePath = staged.StagedPath!,
            ExpectedSha256 = stage.Entry.Sha256Upper,
            Version = stage.Entry.Version,
            CanonicalScriptPath = stage.CanonicalScriptPath,
            Source = AcquisitionSources.Download,
            ManifestId = stage.ManifestId,
            ManifestHash = stage.ManifestHash,
            ManifestVersion = stage.ManifestVersion,
            StatePath = stage.StatePath,
        });
        if (!act.Ok)
        {
            return WriteFailure(stage.StatePath, endpoint, 502,
                act.Error ?? "canonical_write_failed",
                act.Message ?? "Activation failed.");
        }

        return WriteActivationSuccess(endpoint, AcquisitionSources.Download, stage, act, extras: null);
    }

    // -----------------------------------------------------------------
    // POST /api/v1/setup/acquire-pax/upload (JSON localFilePath only)
    // -----------------------------------------------------------------
    internal static async Task<IResult> HandleUploadAsync(
        HttpContext ctx, VersionInfo version, string engineLocalAppDataBase)
    {
        const string endpoint = "POST /api/v1/setup/acquire-pax/upload";

        string contentTypeBase = GetContentTypeBase(ctx);
        if (contentTypeBase != "application/json")
        {
            return Results.Json(new
            {
                error = "multipart_upload_not_implemented",
                endpoint,
                stage = Stage,
                message = "Real multipart/form-data file upload is deferred. Send Content-Type: application/json with body { \"localFilePath\": \"<absolute path to .ps1>\" }.",
                received = new
                {
                    contentTypeBase,
                    acceptedContentType = "application/json",
                    acceptedBodyShape = "{ \"localFilePath\": \"<absolute path to .ps1>\" }",
                },
            }, statusCode: 415);
        }

        string statePath = EngineAcquisition.GetInstallStatePath(engineLocalAppDataBase);

        JsonElement? body = await TryReadJsonBodyAsync(ctx);
        if (body is null || body.Value.ValueKind != JsonValueKind.Object)
        {
            return WriteFailure(statePath, endpoint, 400, "invalid_request_body",
                "Request body must be JSON object: { \"localFilePath\": \"<absolute path to .ps1>\" }.");
        }
        string? localFilePath = TryString(body.Value, "localFilePath");
        if (string.IsNullOrWhiteSpace(localFilePath))
        {
            return WriteFailure(statePath, endpoint, 400, "invalid_request_body",
                "localFilePath is required and must be a non-empty string.");
        }
        string? targetVersion = TryString(body.Value, "targetVersion");
        string? targetSha = TryString(body.Value, "targetSha256");

        if (!File.Exists(localFilePath))
        {
            return WriteFailure(statePath, endpoint, 502, "file_missing",
                "Local file does not exist: " + localFilePath,
                extras: new Dictionary<string, object?> {
                    ["localFilePath"] = Path.GetFileName(localFilePath),
                });
        }
        if (!localFilePath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return WriteFailure(statePath, endpoint, 502, "invalid_extension",
                "Local file extension is not .ps1.",
                extras: new Dictionary<string, object?> {
                    ["localFilePath"] = Path.GetFileName(localFilePath),
                });
        }

        // Read bytes (immutability: never mutate original file).
        byte[] bytes;
        try { bytes = File.ReadAllBytes(localFilePath); }
        catch (Exception ex)
        {
            return WriteFailure(statePath, endpoint, 502, "read_failed",
                "Failed to read local file: " + ex.Message,
                extras: new Dictionary<string, object?> {
                    ["localFilePath"] = Path.GetFileName(localFilePath),
                });
        }
        if (bytes.Length == 0)
        {
            return WriteFailure(statePath, endpoint, 502, "read_failed",
                "Local file is empty.",
                extras: new Dictionary<string, object?> {
                    ["localFilePath"] = Path.GetFileName(localFilePath),
                });
        }
        if (bytes.Length > FetchLimits.PaxScriptMaxBytes)
        {
            return WriteFailure(statePath, endpoint, 413, "payload_too_large",
                "Local file " + bytes.Length + " bytes exceeds cap of " + FetchLimits.PaxScriptMaxBytes + ".",
                extras: new Dictionary<string, object?> {
                    ["localFilePath"] = Path.GetFileName(localFilePath),
                    ["byteCount"] = bytes.Length,
                    ["byteCap"] = FetchLimits.PaxScriptMaxBytes,
                });
        }

        string preSha = Sha256Hex.OfBytes(bytes);

        StageContext? stage = await PrepareStageAsync(
            ctx, endpoint, version, engineLocalAppDataBase, targetVersion, targetSha);
        if (stage is null) { return Results.Empty; }

        if (!string.Equals(preSha, stage.ExpectedSha, StringComparison.Ordinal))
        {
            return WriteFailure(stage.StatePath, endpoint, 502, "hash_not_approved",
                "Local file SHA-256 " + preSha + " does not match VERSION.json paxScript.sha256 " +
                stage.ExpectedSha + ".",
                extras: new Dictionary<string, object?> {
                    ["localSha256"] = preSha,
                    ["expectedSha256"] = stage.ExpectedSha,
                    ["localFilePath"] = Path.GetFileName(localFilePath),
                });
        }

        StageResult staged = StagingArea.StageBytes(stage.WorkDir, bytes, "localfile");
        if (!staged.Ok)
        {
            return WriteFailure(stage.StatePath, endpoint, 502,
                staged.Error ?? "staging_write_failed",
                staged.Message ?? "Failed to stage local-file bytes.");
        }

        ActivationResult act = ScriptActivator.Activate(new ActivationRequest
        {
            StagedFilePath = staged.StagedPath!,
            ExpectedSha256 = stage.ExpectedSha,
            Version = stage.Entry.Version,
            CanonicalScriptPath = stage.CanonicalScriptPath,
            Source = AcquisitionSources.LocalFile,
            ManifestId = stage.ManifestId,
            ManifestHash = stage.ManifestHash,
            ManifestVersion = stage.ManifestVersion,
            StatePath = stage.StatePath,
        });
        if (!act.Ok)
        {
            return WriteFailure(stage.StatePath, endpoint, 502,
                act.Error ?? "canonical_write_failed",
                act.Message ?? "Activation failed.");
        }

        // ENG-5: only the basename of the operator's source file is
        // exposed, never the full local path.
        Dictionary<string, object?> extras = new(StringComparer.Ordinal)
        {
            ["original"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["filename"] = Path.GetFileName(localFilePath),
                ["sha256"] = preSha,
                ["preservedUnchanged"] = SafePostReadHashMatches(localFilePath, preSha),
            },
        };
        return WriteActivationSuccess(endpoint, AcquisitionSources.LocalFile, stage, act, extras);
    }

    // -----------------------------------------------------------------
    // POST /api/v1/setup/acquire-pax/upload-bytes
    // -----------------------------------------------------------------
    internal static async Task<IResult> HandleUploadBytesAsync(
        HttpContext ctx, VersionInfo version, string engineLocalAppDataBase)
    {
        const string endpoint = "POST /api/v1/setup/acquire-pax/upload-bytes";

        string contentTypeBase = GetContentTypeBase(ctx);
        if (contentTypeBase != "application/octet-stream")
        {
            return Results.Json(new
            {
                error = "unsupported_content_type",
                endpoint,
                stage = Stage,
                message = "Upload-bytes route requires Content-Type: application/octet-stream with the raw PAX script bytes as the request body.",
                received = new
                {
                    contentTypeBase,
                    acceptedContentType = "application/octet-stream",
                },
            }, statusCode: 415);
        }

        string statePath = EngineAcquisition.GetInstallStatePath(engineLocalAppDataBase);

        string? clientFilenameHint = GetHeaderOrNull(ctx, "X-PAX-Filename");
        string? clientReportedSize = GetHeaderOrNull(ctx, "X-PAX-File-Size");
        string? clientReportedSha = GetHeaderOrNull(ctx, "X-PAX-Client-SHA256");
        string? clientTargetVersion = GetHeaderOrNull(ctx, "X-PAX-Target-Version");
        string? clientTargetSha = GetHeaderOrNull(ctx, "X-PAX-Target-Sha256");

        long? advertisedSize = ctx.Request.ContentLength;
        if (advertisedSize.HasValue && advertisedSize.Value > FetchLimits.PaxScriptMaxBytes)
        {
            return WriteFailure(statePath, endpoint, 413, "payload_too_large",
                "Uploaded body exceeds cap of " + FetchLimits.PaxScriptMaxBytes + " bytes.",
                extras: new Dictionary<string, object?> {
                    ["upload"] = BuildUploadEcho(advertisedSize, 0, clientFilenameHint,
                        clientReportedSize, clientReportedSha, false),
                    ["byteCap"] = FetchLimits.PaxScriptMaxBytes,
                });
        }

        byte[] bytes;
        try
        {
            bytes = await ReadCappedBodyAsync(ctx, FetchLimits.PaxScriptMaxBytes,
                ctx.RequestAborted);
        }
        catch (PayloadTooLargeException)
        {
            return WriteFailure(statePath, endpoint, 413, "payload_too_large",
                "Uploaded body exceeds cap of " + FetchLimits.PaxScriptMaxBytes + " bytes.",
                extras: new Dictionary<string, object?> {
                    ["upload"] = BuildUploadEcho(advertisedSize, 0, clientFilenameHint,
                        clientReportedSize, clientReportedSha, false),
                    ["byteCap"] = FetchLimits.PaxScriptMaxBytes,
                });
        }
        catch (Exception ex)
        {
            return WriteFailure(statePath, endpoint, 500, "read_failed",
                "Failed to read request body: " + ex.Message);
        }
        if (bytes.Length == 0)
        {
            return WriteFailure(statePath, endpoint, 400, "empty_body",
                "Request body must contain the raw PAX script bytes (Content-Type: application/octet-stream).",
                extras: new Dictionary<string, object?> {
                    ["upload"] = BuildUploadEcho(advertisedSize, 0, clientFilenameHint,
                        clientReportedSize, clientReportedSha, false),
                });
        }

        string preSha = Sha256Hex.OfBytes(bytes);

        StageContext? stage = await PrepareStageAsync(
            ctx, endpoint, version, engineLocalAppDataBase,
            clientTargetVersion, clientTargetSha);
        if (stage is null) { return Results.Empty; }

        if (!string.Equals(preSha, stage.ExpectedSha, StringComparison.Ordinal))
        {
            return WriteFailure(stage.StatePath, endpoint, 502, "hash_not_approved",
                "Uploaded bytes SHA-256 " + preSha + " does not match VERSION.json paxScript.sha256 " +
                stage.ExpectedSha + ".",
                extras: new Dictionary<string, object?> {
                    ["upload"] = BuildUploadEcho(advertisedSize, bytes.Length, clientFilenameHint,
                        clientReportedSize, clientReportedSha, false),
                    ["localSha256"] = preSha,
                    ["expectedSha256"] = stage.ExpectedSha,
                });
        }

        StageResult staged = StagingArea.StageBytes(stage.WorkDir, bytes, "uploadbytes");
        if (!staged.Ok)
        {
            return WriteFailure(stage.StatePath, endpoint, 502,
                staged.Error ?? "staging_write_failed",
                staged.Message ?? "Failed to stage upload bytes.");
        }

        ActivationResult act = ScriptActivator.Activate(new ActivationRequest
        {
            StagedFilePath = staged.StagedPath!,
            ExpectedSha256 = stage.ExpectedSha,
            Version = stage.Entry.Version,
            CanonicalScriptPath = stage.CanonicalScriptPath,
            Source = AcquisitionSources.LocalFile,
            ManifestId = stage.ManifestId,
            ManifestHash = stage.ManifestHash,
            ManifestVersion = stage.ManifestVersion,
            StatePath = stage.StatePath,
        });
        if (!act.Ok)
        {
            return WriteFailure(stage.StatePath, endpoint, 502,
                act.Error ?? "canonical_write_failed",
                act.Message ?? "Activation failed.");
        }

        Dictionary<string, object?> extras = new(StringComparer.Ordinal)
        {
            ["upload"] = BuildUploadEcho(advertisedSize, bytes.Length, clientFilenameHint,
                clientReportedSize, clientReportedSha, true),
            ["original"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["filename"] = clientFilenameHint,
                ["sha256"] = preSha,
                ["preservedUnchanged"] = true,
            },
        };
        return WriteActivationSuccess(endpoint, AcquisitionSources.LocalFile, stage, act, extras);
    }

    // -----------------------------------------------------------------
    // POST /api/v1/setup/acquire-pax/cancel
    // -----------------------------------------------------------------
    internal static IResult HandleCancel(VersionInfo version, string engineLocalAppDataBase)
    {
        const string endpoint = "POST /api/v1/setup/acquire-pax/cancel";
        string statePath = EngineAcquisition.GetInstallStatePath(engineLocalAppDataBase);
        try
        {
            InstallStateWriter.WriteCancel(statePath, endpoint);
        }
        catch (Exception ex)
        {
            return Results.Json(new
            {
                error = "install_state_write_failed",
                endpoint,
                stage = Stage,
                message = "Failed to write cancellation to install-state.json: " + ex.Message,
            }, statusCode: 500);
        }
        return Results.Json(new
        {
            endpoint,
            stage = Stage,
            result = "cancelled",
            message = "Acquisition attempt cancelled by operator.",
            atUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        }, statusCode: 200);
    }

    // -----------------------------------------------------------------
    // Shared stage orchestration: prereq + policy + manifest fetch +
    // verify + entry select + entry-vs-pin cross-check. Returns null
    // when it has already written the response.
    // -----------------------------------------------------------------
    private sealed class StageContext
    {
        public required string WorkDir { get; init; }
        public required string CanonicalScriptPath { get; init; }
        public required string StatePath { get; init; }
        public required string ExpectedSha { get; init; }
        public required ApprovedEngineEntry Entry { get; init; }
        public required string ManifestId { get; init; }
        public required string ManifestHash { get; init; }
        public required string ManifestVersion { get; init; }
    }

    private static async Task<StageContext?> PrepareStageAsync(
        HttpContext ctx, string endpoint, VersionInfo version, string engineBase,
        string? targetVersion, string? targetSha)
    {
        string statePath = EngineAcquisition.GetInstallStatePath(engineBase);
        string canonicalScriptPath = EngineAcquisition.GetManagedEnginePath(engineBase);

        // Policy gate. Only "external" mutates.
        string policy = (version.PaxAcquisitionPolicy ?? "embedded").Trim().ToLowerInvariant();
        if (policy != "external")
        {
            await WriteFailureAsync(ctx, statePath, endpoint, 409, "policy_not_external",
                "PAX acquisition policy is \"" + policy + "\". Mutating acquire endpoints require policy \"external\" in VERSION.json.",
                extras: new Dictionary<string, object?> { ["policy"] = policy });
            return null;
        }

        string signaturePolicy = string.IsNullOrWhiteSpace(version.ManifestSignaturePolicy)
            ? "required" : version.ManifestSignaturePolicy.Trim();

        if (string.IsNullOrWhiteSpace(version.EngineManifestUrl))
        {
            await WriteFailureAsync(ctx, statePath, endpoint, 409, "engine_manifest_url_missing",
                "VERSION.json paxScript.engineManifestUrl must be configured before acquisition.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(version.EngineManifestTrustAnchorThumbprint) &&
            signaturePolicy != "internal-test-bypass")
        {
            await WriteFailureAsync(ctx, statePath, endpoint, 409,
                "engine_manifest_trust_anchor_missing",
                "VERSION.json paxScript.engineManifestTrustAnchorThumbprint must be configured before acquisition.");
            return null;
        }

        UrlValidationResult urlCheck = UrlValidator.Validate(version.EngineManifestUrl);
        if (!urlCheck.Ok)
        {
            await WriteFailureAsync(ctx, statePath, endpoint, 502,
                urlCheck.Error ?? "manifest_url_invalid",
                urlCheck.Message ?? "engineManifestUrl is not acceptable.");
            return null;
        }

        string updatesDir = ResolveUpdatesDir(engineBase);
        string workDir;
        try { workDir = StagingArea.CreateWorkDirectory(updatesDir); }
        catch (Exception ex)
        {
            await WriteFailureAsync(ctx, statePath, endpoint, 500,
                "work_directory_create_failed",
                "Failed to create acquisition work directory: " + ex.Message);
            return null;
        }

        // Fetch the manifest.
        FetchResult manifestFetch = await ManifestFetcher.FetchManifestAsync(
            urlCheck.Uri!, version.CookbookVersion, ctx.RequestAborted);
        if (!manifestFetch.Ok || manifestFetch.Bytes is null)
        {
            await WriteFailureAsync(ctx, statePath, endpoint, 502,
                manifestFetch.Error ?? "manifest_fetch_failed",
                manifestFetch.Message ?? "Manifest fetch failed.");
            return null;
        }
        string manifestBodyPath = Path.Combine(workDir, "approved-engine-manifest.json");
        try { File.WriteAllBytes(manifestBodyPath, manifestFetch.Bytes); }
        catch (Exception ex)
        {
            await WriteFailureAsync(ctx, statePath, endpoint, 500,
                "manifest_persist_failed",
                "Failed to persist manifest body to work dir: " + ex.Message);
            return null;
        }

        // Parse + schema-validate.
        ManifestValidationResult validated;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(manifestFetch.Bytes);
            validated = ManifestSchemaValidator.Validate(doc.RootElement,
                allowLoopbackHttpDownloadUrl: signaturePolicy == "internal-test-bypass");
        }
        catch (Exception ex)
        {
            await WriteFailureAsync(ctx, statePath, endpoint, 502, "manifest_parse_failed",
                "Manifest JSON parse failed: " + ex.Message);
            return null;
        }
        if (!validated.Ok)
        {
            await WriteFailureAsync(ctx, statePath, endpoint, 502,
                validated.Error ?? "manifest_schema_invalid",
                validated.Message ?? "Approved-engine manifest failed schema validation.");
            return null;
        }

        // Signature verification (unless internal-test-bypass).
        string manifestHash = Sha256Hex.OfBytes(manifestFetch.Bytes);
        if (signaturePolicy != "internal-test-bypass")
        {
            Uri sigUri = new(urlCheck.Uri!.ToString() + ".sig");
            FetchResult sigFetch = await ManifestFetcher.FetchSignatureAsync(
                sigUri, version.CookbookVersion, ctx.RequestAborted);
            if (!sigFetch.Ok || sigFetch.Bytes is null)
            {
                await WriteFailureAsync(ctx, statePath, endpoint, 502,
                    sigFetch.Error ?? "signature_fetch_failed",
                    sigFetch.Message ?? "Signature fetch failed.");
                return null;
            }
            string sigPath = Path.Combine(workDir, "approved-engine-manifest.json.sig");
            try { File.WriteAllBytes(sigPath, sigFetch.Bytes); }
            catch (Exception ex)
            {
                await WriteFailureAsync(ctx, statePath, endpoint, 500,
                    "signature_persist_failed",
                    "Failed to persist signature to work dir: " + ex.Message);
                return null;
            }

            SignatureVerifyResult sig = SignatureVerifier.Verify(manifestBodyPath, sigPath);
            if (!sig.Ok)
            {
                await WriteFailureAsync(ctx, statePath, endpoint, 502,
                    sig.Error ?? "signature_verify_failed",
                    sig.Message ?? "Manifest signature verification failed.");
                return null;
            }
            string expectedThumb = version.EngineManifestTrustAnchorThumbprint!
                .Replace(":", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
            string actualThumb = (sig.CertThumbprint ?? string.Empty)
                .Replace(":", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
            if (!string.Equals(actualThumb, expectedThumb, StringComparison.Ordinal))
            {
                await WriteFailureAsync(ctx, statePath, endpoint, 502,
                    "manifest_trust_anchor_mismatch",
                    "Manifest signing cert thumbprint " + actualThumb +
                    " does not match pinned trust anchor " + expectedThumb + ".");
                return null;
            }
        }

        // Select approved + compatible entry.
        var sel = ManifestSelector.Select(validated.Entries, version.CookbookVersion,
            targetVersion, targetSha);
        if (sel.Entry is null)
        {
            await WriteFailureAsync(ctx, statePath, endpoint, 409,
                sel.Error ?? "no_compatible_engine",
                sel.Message ?? "No approved + compatible engine entry.");
            return null;
        }

        // Cross-check entry sha256 against VERSION.json pin.
        string expectedSha = (version.PaxSha256 ?? string.Empty).ToUpperInvariant();
        if (!string.Equals(sel.Entry.Sha256Upper, expectedSha, StringComparison.Ordinal))
        {
            await WriteFailureAsync(ctx, statePath, endpoint, 409, "version_hash_mismatch",
                "Approved entry sha256 " + sel.Entry.Sha256Upper +
                " does not match VERSION.json paxScript.sha256 " + expectedSha + ".",
                extras: new Dictionary<string, object?> {
                    ["entrySha256"] = sel.Entry.Sha256Upper,
                    ["expectedSha256"] = expectedSha,
                });
            return null;
        }

        return new StageContext
        {
            WorkDir = workDir,
            CanonicalScriptPath = canonicalScriptPath,
            StatePath = statePath,
            ExpectedSha = expectedSha,
            Entry = sel.Entry,
            ManifestId = validated.ManifestId,
            ManifestHash = manifestHash,
            ManifestVersion = validated.ManifestVersion ?? string.Empty,
        };
    }

    // -----------------------------------------------------------------
    // Response helpers
    // -----------------------------------------------------------------
    private static IResult WriteActivationSuccess(
        string endpoint, string sourceTag, StageContext stage, ActivationResult act,
        Dictionary<string, object?>? extras)
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["endpoint"] = endpoint,
            ["stage"] = Stage,
            ["result"] = "activated",
            ["source"] = sourceTag,
            ["version"] = act.Version,
            ["sha256"] = act.Sha256,
            ["canonicalScript"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = act.CanonicalPath,
            },
            ["manifest"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = stage.ManifestId,
                ["hash"] = stage.ManifestHash,
                ["version"] = stage.ManifestVersion,
            },
            ["timestamps"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["validatedAtUtc"] = act.ValidatedAtUtc,
                ["activatedAtUtc"] = act.ActivatedAtUtc,
            },
        };
        if (extras is not null)
        {
            foreach (var kv in extras) { body[kv.Key] = kv.Value; }
        }
        return Results.Json(body, statusCode: 200);
    }

    private static IResult WriteFailure(string statePath, string endpoint, int status,
        string errorToken, string message, Dictionary<string, object?>? extras = null)
    {
        string? stateWriteErr = null;
        try
        {
            InstallStateWriter.WriteFailure(statePath, new FailureDetails
            {
                Error = errorToken,
                Endpoint = endpoint,
                Message = message,
                Extras = extras,
            });
        }
        catch (Exception ex) { stateWriteErr = ex.Message; }

        string atUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["error"] = errorToken,
            ["endpoint"] = endpoint,
            ["stage"] = Stage,
            ["message"] = message,
            ["atUtc"] = atUtc,
        };
        if (extras is not null)
        {
            foreach (var kv in extras) { if (!body.ContainsKey(kv.Key)) { body[kv.Key] = kv.Value; } }
        }
        if (stateWriteErr is not null)
        {
            body["installStateWriteError"] = stateWriteErr;
        }
        return Results.Json(body, statusCode: status);
    }

    private static async Task WriteFailureAsync(
        HttpContext ctx, string statePath, string endpoint, int status,
        string errorToken, string message, Dictionary<string, object?>? extras = null)
    {
        IResult r = WriteFailure(statePath, endpoint, status, errorToken, message, extras);
        await r.ExecuteAsync(ctx);
    }

    // -----------------------------------------------------------------
    // Body / header helpers
    // -----------------------------------------------------------------
    private static string GetContentTypeBase(HttpContext ctx)
    {
        string raw = ctx.Request.ContentType ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) { return string.Empty; }
        int semi = raw.IndexOf(';');
        if (semi >= 0) { raw = raw.Substring(0, semi); }
        return raw.Trim().ToLowerInvariant();
    }

    private static string? GetHeaderOrNull(HttpContext ctx, string name)
    {
        if (!ctx.Request.Headers.TryGetValue(name, out var values)) { return null; }
        string? v = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static async Task<JsonElement?> TryReadJsonBodyAsync(HttpContext ctx)
    {
        if (ctx.Request.ContentLength == 0) { return null; }
        try
        {
            using MemoryStream ms = new();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            if (ms.Length == 0) { return null; }
            using JsonDocument doc = JsonDocument.Parse(ms.ToArray());
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static async Task<(string?, string?)> ReadTargetsFromJsonBodyAsync(HttpContext ctx)
    {
        JsonElement? body = await TryReadJsonBodyAsync(ctx);
        if (body is null || body.Value.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }
        return (TryString(body.Value, "targetVersion"), TryString(body.Value, "targetSha256"));
    }

    private static string? TryString(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String)
        {
            string? s = el.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }

    private static async Task<byte[]> ReadCappedBodyAsync(
        HttpContext ctx, int cap, CancellationToken ct)
    {
        using MemoryStream ms = new();
        byte[] buf = new byte[64 * 1024];
        int total = 0;
        while (true)
        {
            int n = await ctx.Request.Body.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n <= 0) { break; }
            total += n;
            if (total > cap) { throw new PayloadTooLargeException(); }
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private sealed class PayloadTooLargeException : Exception { }

    private static object BuildUploadEcho(long? advertised, int receivedBytes,
        string? clientFilenameHint, string? clientReportedSize, string? clientReportedSha,
        bool sha256AcceptedByManifest)
    {
        return new
        {
            contentLengthHeader = advertised,
            receivedBytes,
            clientFilenameHint,
            clientReportedSize,
            clientReportedSha256 = clientReportedSha,
            sha256AcceptedByManifest,
        };
    }

    private static bool SafePostReadHashMatches(string path, string preSha)
    {
        try
        {
            string post = Sha256Hex.OfFile(path);
            return string.Equals(post, preSha, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    internal static string ResolveUpdatesDir(string engineLocalAppDataBase)
    {
        string? ov = _updatesDirOverride;
        if (!string.IsNullOrWhiteSpace(ov)) { return ov; }
        return Path.Combine(engineLocalAppDataBase, "PAXCookbook", "Updates");
    }

    private static string? _updatesDirOverride;

    // Test-only: smoke harness sets an isolated work-dir root so acquisition
    // attempts never write under the operator's real profile. Setting null
    // restores default <engineBase>\PAXCookbook\Updates resolution.
    internal static void SetUpdatesDirOverride(string? path)
    {
        _updatesDirOverride = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }
}

// Snapshot of the paxAcquisition block read from install-state.json. Returns
// nulls/defaults for absent or unparseable files so the state endpoint never
// crashes.
internal sealed class PaxAcquisitionSnapshot
{
    public bool Pending { get; init; }
    public string? Source { get; init; }
    public string? Version { get; init; }
    public string? Sha256 { get; init; }
    public string? ManifestId { get; init; }
    public string? ManifestHash { get; init; }
    public string? ManifestVersion { get; init; }
    public string? ValidatedAtUtc { get; init; }
    public string? ActivatedAtUtc { get; init; }
    public object? LastAttemptError { get; init; }

    internal static PaxAcquisitionSnapshot Read(string installStatePath)
    {
        if (!File.Exists(installStatePath)) { return new PaxAcquisitionSnapshot(); }
        try
        {
            using FileStream fs = File.OpenRead(installStatePath);
            using JsonDocument doc = JsonDocument.Parse(fs);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { return new PaxAcquisitionSnapshot(); }
            if (!doc.RootElement.TryGetProperty("paxAcquisition", out JsonElement pax) ||
                pax.ValueKind != JsonValueKind.Object)
            {
                return new PaxAcquisitionSnapshot();
            }
            bool pending = false;
            if (pax.TryGetProperty("pending", out JsonElement p) && p.ValueKind == JsonValueKind.True) { pending = true; }
            object? lastErr = null;
            if (pax.TryGetProperty("lastAttemptError", out JsonElement le) && le.ValueKind != JsonValueKind.Null)
            {
                lastErr = JsonElementToObject(le);
            }
            return new PaxAcquisitionSnapshot
            {
                Pending = pending,
                Source = ReadStr(pax, "source"),
                Version = ReadStr(pax, "version"),
                Sha256 = ReadStr(pax, "sha256"),
                ManifestId = ReadStr(pax, "manifestId"),
                ManifestHash = ReadStr(pax, "manifestHash"),
                ManifestVersion = ReadStr(pax, "manifestVersion"),
                ValidatedAtUtc = ReadStr(pax, "validatedAtUtc"),
                ActivatedAtUtc = ReadStr(pax, "activatedAtUtc"),
                LastAttemptError = lastErr,
            };
        }
        catch { return new PaxAcquisitionSnapshot(); }
    }

    private static string? ReadStr(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }
        return null;
    }

    private static object? JsonElementToObject(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, object?> o = new(StringComparer.Ordinal);
                foreach (JsonProperty p in el.EnumerateObject()) { o[p.Name] = JsonElementToObject(p.Value); }
                return o;
            case JsonValueKind.Array:
                List<object?> a = new();
                foreach (JsonElement i in el.EnumerateArray()) { a.Add(JsonElementToObject(i)); }
                return a;
            case JsonValueKind.String: return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out long l)) { return l; }
                if (el.TryGetDouble(out double d)) { return d; }
                return el.GetRawText();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null;
        }
    }
}
