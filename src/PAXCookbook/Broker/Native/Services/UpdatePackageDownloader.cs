using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-A -- package downloader for staged update bundles.
//
// Parity with app/broker/Update/Package.psm1:
//   * Stages to <workspace>/Updates/<filename>.partial then atomic
//     File.Move(overwrite=true) to <filename>.
//   * Validates the filename against the allow regex
//     ^[A-Za-z0-9._-]{1,160}\.zip$ -- rejects ../ \ / etc.
//   * Re-validates the URL via UpdateManifestProbe.ValidateUrl
//     so HTTP-non-loopback URLs are blocked even if the manifest
//     parser missed them.
//   * SHA-256 streamed during download (no second pass over the
//     bytes). On mismatch the .partial file is deleted and
//     sha256_mismatch is returned.
//   * Idempotent: if the final file already exists with a matching
//     SHA-256 the route reports alreadyStaged=true and does NOT
//     re-download.
//   * Sidecar metadata: <filename>.metadata.json captures
//     cookbookVersionAtDownload, downloadedAtUtc, sourceUrl,
//     filename, sizeBytes, sha256, expectedCookbookVersion,
//     manifestSnapshot.
//   * Max package bytes: 256 MiB (matches PS MaxPackageBytes).
//   * HttpMessageHandler is injectable for tests.
public interface IUpdatePackageDownloader
{
    Task<UpdateDownloadResult> DownloadAsync(
        IDictionary<string, object?> manifestSnapshot,
        string                       currentCookbookVersion,
        CancellationToken            ct);

    IReadOnlyList<StagedPackageInfo> GetStagedInventory();
}

public sealed class UpdatePackageDownloader : IUpdatePackageDownloader, IDisposable
{
    private const long MaxPackageBytes      = 256L * 1024L * 1024L;
    private const int  PackageTimeoutSeconds = 600;
    private static readonly Regex FilenameAllow = new(
        @"^[A-Za-z0-9._-]{1,160}\.zip$", RegexOptions.Compiled);

    private readonly string                _workspacePath;
    private readonly string                _cookbookVersionHint;
    private readonly UpdateManifestProbe   _urlValidator;
    private readonly HttpClient            _client;
    private readonly bool                  _ownsClient;
    private readonly Func<DateTimeOffset>  _clock;

    public UpdatePackageDownloader(
        string                workspacePath,
        string                cookbookVersionHint,
        HttpMessageHandler?   handler = null,
        Func<DateTimeOffset>? clock   = null)
    {
        _workspacePath       = workspacePath;
        _cookbookVersionHint = cookbookVersionHint ?? string.Empty;
        _urlValidator        = new UpdateManifestProbe(cookbookVersionHint, handler: null);
        // AllowAutoRedirect=false is sufficient; .NET 8
        // SocketsHttpHandler rejects MaxAutomaticRedirections=0.
        var resolvedHandler = handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies        = false,
        };
        _client = new HttpClient(resolvedHandler, disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(PackageTimeoutSeconds),
        };
        _ownsClient = true;
        _clock      = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        IDictionary<string, object?> manifestSnapshot,
        string                       currentCookbookVersion,
        CancellationToken            ct)
    {
        var startedAtUtc = _clock().ToUniversalTime().ToString("o");
        // Pull latestCookbook block.
        if (!manifestSnapshot.TryGetValue("latestCookbook", out var lcObj) ||
            lcObj is not IDictionary<string, object?> lc)
        {
            return Failure("no_manifest_snapshot",
                "Manifest snapshot does not include latestCookbook.",
                startedAtUtc);
        }
        var packageUrl = lc.TryGetValue("packageUrl", out var p) ? p as string : null;
        var sha256     = lc.TryGetValue("sha256",     out var s) ? s as string : null;
        var version    = lc.TryGetValue("version",    out var v) ? v as string : null;

        if (string.IsNullOrWhiteSpace(packageUrl))
        {
            return Failure("package_url_missing",
                "latestCookbook.packageUrl is missing.", startedAtUtc);
        }
        if (string.IsNullOrEmpty(sha256) ||
            !Regex.IsMatch(sha256, "^[0-9A-Fa-f]{64}$"))
        {
            return Failure("invalid_sha256",
                "latestCookbook.sha256 is not 64-char hex.", startedAtUtc);
        }
        var urlOutcome = _urlValidator.ValidateUrl(packageUrl);
        if (!urlOutcome.Ok || urlOutcome.NormalizedUri is null)
        {
            return Failure("package_url_" + (urlOutcome.Error ?? "invalid"),
                urlOutcome.Message ?? "Package URL rejected.", startedAtUtc);
        }
        string? filename;
        try
        {
            var segs = urlOutcome.NormalizedUri.Segments;
            filename = Uri.UnescapeDataString(segs[^1]);
        }
        catch (Exception ex)
        {
            return Failure("malformed_filename",
                "Could not derive filename from URL: " + ex.Message,
                startedAtUtc);
        }
        if (string.IsNullOrEmpty(filename) ||
            filename.Contains("..", StringComparison.Ordinal) ||
            filename.Contains('/') ||
            filename.Contains('\\') ||
            !FilenameAllow.IsMatch(filename))
        {
            return Failure("malformed_filename",
                "Filename failed the staging allowlist: \"" + filename + "\".",
                startedAtUtc);
        }

        var stagingDir = Path.Combine(_workspacePath, "Updates");
        try
        {
            Directory.CreateDirectory(stagingDir);
        }
        catch (Exception ex)
        {
            return Failure("download_failed",
                "Could not create staging directory: " + ex.Message,
                startedAtUtc);
        }
        var finalPath   = Path.Combine(stagingDir, filename);
        var partialPath = finalPath + ".partial";
        var metaPath    = finalPath + ".metadata.json";

        // Idempotency: hash any existing final and compare.
        if (File.Exists(finalPath))
        {
            string existingHash;
            try
            {
                existingHash = ComputeSha256(finalPath);
            }
            catch (Exception ex)
            {
                return Failure("download_failed",
                    "Existing staged file hash failed: " + ex.Message,
                    startedAtUtc);
            }
            if (string.Equals(existingHash, sha256, StringComparison.OrdinalIgnoreCase))
            {
                var info = new FileInfo(finalPath);
                WriteSidecar(metaPath, manifestSnapshot, packageUrl!, filename,
                    info.Length, sha256!, currentCookbookVersion, version, startedAtUtc);
                return new UpdateDownloadResult(
                    State:         "alreadyStaged",
                    StartedAtUtc:  startedAtUtc,
                    FinishedAtUtc: _clock().ToUniversalTime().ToString("o"),
                    Filename:      filename,
                    Path:          finalPath,
                    Sha256:        sha256,
                    SizeBytes:     info.Length,
                    AlreadyStaged: true,
                    Error:         null,
                    Message:       "Package already staged with matching hash.");
            }
            return Failure("staging_slot_occupied",
                "Staging slot is occupied by a package with a different hash.",
                startedAtUtc);
        }

        // Fresh download.
        try
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, urlOutcome.NormalizedUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
                new ProductHeaderValue("PAXCookbook",
                    string.IsNullOrWhiteSpace(_cookbookVersionHint) ? "0.0.0" : _cookbookVersionHint)));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(package-download)"));
            using var response = await _client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Failure("download_failed",
                    "Package fetch returned HTTP " + (int)response.StatusCode + ".",
                    startedAtUtc);
            }
            if (response.Content.Headers.ContentLength is long cl && cl > MaxPackageBytes)
            {
                return Failure("package_too_large",
                    "Package size " + cl + " exceeds limit " + MaxPackageBytes + ".",
                    startedAtUtc);
            }

            long totalBytes = 0;
            using (var sha = SHA256.Create())
            using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var fs = new FileStream(partialPath, FileMode.Create, FileAccess.Write,
                                            FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                                          .ConfigureAwait(false)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > MaxPackageBytes)
                    {
                        try { fs.Dispose(); } catch { }
                        try { File.Delete(partialPath); } catch { }
                        return Failure("package_too_large",
                            "Package exceeded " + MaxPackageBytes + " bytes mid-stream.",
                            startedAtUtc);
                    }
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                if (totalBytes == 0)
                {
                    try { fs.Dispose(); } catch { }
                    try { File.Delete(partialPath); } catch { }
                    return Failure("empty_download",
                        "Package download produced zero bytes.", startedAtUtc);
                }

                var computed = HexLower(sha.Hash!);
                if (!string.Equals(computed, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { fs.Dispose(); } catch { }
                    try { File.Delete(partialPath); } catch { }
                    return Failure("sha256_mismatch",
                        "Computed SHA-256 \"" + computed
                         + "\" does not match expected \"" + sha256 + "\".",
                        startedAtUtc);
                }
            }
            try
            {
                File.Move(partialPath, finalPath, overwrite: true);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
                return Failure("stage_rename_failed",
                    "Could not promote staged file: " + ex.Message,
                    startedAtUtc);
            }

            WriteSidecar(metaPath, manifestSnapshot, packageUrl!, filename,
                totalBytes, sha256!, currentCookbookVersion, version, startedAtUtc);

            return new UpdateDownloadResult(
                State:         "staged",
                StartedAtUtc:  startedAtUtc,
                FinishedAtUtc: _clock().ToUniversalTime().ToString("o"),
                Filename:      filename,
                Path:          finalPath,
                Sha256:        sha256,
                SizeBytes:     totalBytes,
                AlreadyStaged: false,
                Error:         null,
                Message:       null);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
            return Failure("download_failed",
                "Package download failed: " + ex.Message, startedAtUtc);
        }
    }

    public IReadOnlyList<StagedPackageInfo> GetStagedInventory()
    {
        var dir = Path.Combine(_workspacePath, "Updates");
        if (!Directory.Exists(dir))
        {
            return Array.Empty<StagedPackageInfo>();
        }
        var result = new List<StagedPackageInfo>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (!FilenameAllow.IsMatch(name)) continue;
            var info = new FileInfo(file);
            var metaPath = file + ".metadata.json";
            StagedPackageMetadata? meta = null;
            if (File.Exists(metaPath))
            {
                try
                {
                    var text = File.ReadAllText(metaPath);
                    meta = JsonSerializer.Deserialize<StagedPackageMetadata>(text,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                        });
                }
                catch
                {
                    meta = null;
                }
            }
            result.Add(new StagedPackageInfo(
                Filename:     name,
                Path:         file,
                SizeBytes:    info.Length,
                ModifiedUtc:  info.LastWriteTimeUtc.ToString("o"),
                MetadataPath: File.Exists(metaPath) ? metaPath : null,
                Metadata:     meta,
                Trust:        null));
        }
        return result;
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(path);
        var bytes = sha.ComputeHash(fs);
        return HexLower(bytes);
    }

    private static string HexLower(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private void WriteSidecar(
        string                       metaPath,
        IDictionary<string, object?> manifestSnapshot,
        string                       sourceUrl,
        string                       filename,
        long                         sizeBytes,
        string                       sha256,
        string                       currentCookbookVersion,
        string?                      expectedCookbookVersion,
        string                       startedAtUtc)
    {
        try
        {
            var sidecar = new Dictionary<string, object?>
            {
                ["cookbookVersionAtDownload"] = currentCookbookVersion,
                ["downloadedAtUtc"]           = startedAtUtc,
                ["sourceUrl"]                 = sourceUrl,
                ["filename"]                  = filename,
                ["sizeBytes"]                 = sizeBytes,
                ["sha256"]                    = sha256,
                ["expectedCookbookVersion"]   = expectedCookbookVersion,
                ["manifestSnapshot"]          = manifestSnapshot,
            };
            var json = JsonSerializer.Serialize(sidecar,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, json);
        }
        catch
        {
            // Sidecar write is best-effort; the package itself is the
            // primary artefact. PS Package.psm1 also tolerates sidecar
            // failures.
        }
    }

    private static UpdateDownloadResult Failure(string code, string message, string startedAtUtc)
    {
        return new UpdateDownloadResult(
            State:         "downloadFailed",
            StartedAtUtc:  startedAtUtc,
            FinishedAtUtc: DateTimeOffset.UtcNow.ToString("o"),
            Filename:      null,
            Path:          null,
            Sha256:        null,
            SizeBytes:     null,
            AlreadyStaged: false,
            Error:         code,
            Message:       message);
    }

    public void Dispose()
    {
        try { _urlValidator.Dispose(); } catch { }
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
