using System.IO.Compression;
using System.Net.Http;

namespace PAXCookbookSetup.Gui;

// Downloads the PAX Cookbook payload zip from GitHub Release at install time.
// Used when the Setup exe does not embed the payload (lightweight bootstrapper).
public sealed class PayloadDownloader
{
    public enum DownloadRoute
    {
        Stable,
        Experimental,
        Unavailable
    }

    public const string PayloadUrl =
        "https://github.com/microsoft/PAX-Cookbook/releases/latest/download/PAX_Cookbook_Payload.zip";

    // Shown to the user when an integrity check fails after every retry.
    public const string ManualDownloadUrl =
        "https://github.com/microsoft/PAX-Cookbook/releases";

    private const int MaxRetries = 3;
    private const int BufferSize = 81920;  // 80KB chunks for progress reporting
    
    private readonly SetupLogger _log;
    private readonly Action<string> _progress;
    private readonly string _tempPath;
    
    public PayloadDownloader(SetupLogger log, Action<string> progress, string? tempPath = null)
    {
        _log = log;
        _progress = progress;
        _tempPath = tempPath ?? Path.GetTempPath();
    }
    
    public sealed record DownloadResult(bool Success, string? ZipPath, string? Error)
    {
        public ManifestVerifier.PayloadExpectation? Expectation { get; init; }
        public string? SourceUrl { get; init; }
        internal bool IntegrityFailure { get; init; }
    }

    public static DownloadRoute ResolveDownloadRoute(string? channel)
        => SetupChannel.Normalize(channel) switch
        {
            SetupChannel.Experimental => DownloadRoute.Experimental,
            SetupChannel.Internal => DownloadRoute.Unavailable,
            _ => DownloadRoute.Stable
        };
    
    public async Task<DownloadResult> DownloadAsync(CancellationToken cancel = default)
    {
        var route = ResolveDownloadRoute(SetupChannel.Resolve());
        if (route == DownloadRoute.Unavailable)
        {
            return new DownloadResult(false, null,
                "Internal validation builds require an explicit local payload and do not download production releases.");
        }

        var destPath = Path.Combine(_tempPath, "PAXCookbook_Payload.zip");
        
        // Clean up any prior partial download
        if (File.Exists(destPath))
        {
            try { File.Delete(destPath); }
            catch { /* best-effort */ }
        }

        // Resolve the download source for THIS installer's channel. The channel
        // is stamped into the Setup assembly at build time (SetupChannel), so no
        // caller-supplied URL crosses the trust boundary — an experimental
        // installer discovers its own pre-release; a stable installer keeps the
        // fixed release/manifest URLs unchanged.
        ManifestVerifier.PayloadExpectation? expectation;

        if (route == DownloadRoute.Experimental)
        {
            using var gh = new HttpPrereqDownloader();
            var located = ExperimentalReleaseLocator.Locate(gh);
            if (located is null || !TryGetExperimentalVersion(located, out var selectedVersion))
            {
                _log.Write("experimental-release-locate-failed", "warning");
                return new DownloadResult(false, null,
                    SetupUiText.NoPreReleaseBuildFound +
                    $"You can download builds manually from {ManualDownloadUrl}");
            }

            _log.Write("experimental-release-located", "info",
                new Dictionary<string, object?>
                {
                    ["payloadUrl"] = located.PayloadUrl,
                    ["manifestUrl"] = located.ManifestUrl
                });

            // The pre-release's attached versions.json carries the payload SHA
            // gate. Fetch it through the host-validated downloader and parse it
            // with the same parser the stable path uses.
            string? manifestJson = gh.GetText(located.ManifestUrl, "application/json");
            expectation = ManifestVerifier.ParseReleaseMetadata(manifestJson, SetupChannel.Experimental, selectedVersion);
        }
        else
        {
            expectation = await new ManifestVerifier(_log).TryFetchAsync(cancel);
        }
        if (expectation is null)
        {
            _log.Write("payload-release-metadata-rejected", "error");
            return new DownloadResult(false, null, "The release information could not be verified.") { IntegrityFailure = true };
        }

        string payloadUrl = ManifestVerifier.ReleaseAssetUrl(expectation.Version!, ManifestVerifier.PayloadFileName);

        string? lastError = null;
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var result = await TryDownloadAsync(destPath, attempt, expectation, payloadUrl, cancel);
                if (result.Success || result.IntegrityFailure)
                    return result;

                lastError = result.Error;
                // Log the failure but continue to retry
                _log.Write("payload-download-attempt-failed", "warning",
                    new Dictionary<string, object?>
                    {
                        ["attempt"] = attempt,
                        ["error"] = result.Error
                    });
                    
                if (attempt < MaxRetries)
                {
                    _progress($"Download attempt {attempt} failed, retrying...");
                    await Task.Delay(1000 * attempt, cancel);  // Backoff
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _log.Write("payload-download-exception", "error",
                    new Dictionary<string, object?>
                    {
                        ["attempt"] = attempt,
                        ["exception"] = ex.GetType().Name,
                        ["message"] = ex.Message
                    });
                    
                if (attempt < MaxRetries)
                {
                    _progress($"Download attempt {attempt} failed, retrying...");
                    try { await Task.Delay(1000 * attempt, cancel); }
                    catch (OperationCanceledException) { throw; }
                }
            }
        }

        // Every attempt failed: surface the last error plus the manual-download
        // fallback so the user is never stranded.
        var finalMsg = string.IsNullOrEmpty(lastError)
            ? $"Download failed after {MaxRetries} attempts. You can download the files manually from {ManualDownloadUrl}"
            : $"{lastError} You can download the files manually from {ManualDownloadUrl}";
        return new DownloadResult(false, null, finalMsg);
    }
    
    internal static bool TryGetExperimentalVersion(ExperimentalReleaseLocator.Located release, out string? version)
    {
        version = release.Tag is { Length: > 1 } tag && tag[0] == 'v' ? tag[1..] : null;
        return ManifestVerifier.IsReleaseVersion(version, SetupChannel.Experimental) &&
            release.ManifestUrl == ManifestVerifier.ReleaseAssetUrl(version!, "versions.json") &&
            release.PayloadUrl == ManifestVerifier.ReleaseAssetUrl(version!, ManifestVerifier.PayloadFileName);
    }

    private async Task<DownloadResult> TryDownloadAsync(
        string destPath, int attempt, ManifestVerifier.PayloadExpectation expectation,
        string payloadUrl, CancellationToken cancel)
    {
        // Validate the URL is allowed
        if (!PrereqDownloadHosts.IsAllowed(payloadUrl))
        {
            return new DownloadResult(false, null,
                $"Payload URL not in allowed host list: {payloadUrl}");
        }
        
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false  // Manual redirect handling for host validation
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        
        var url = payloadUrl;
        int redirectCount = 0;
        const int maxRedirects = 5;
        
        while (true)
        {
            _progress($"Downloading PAX Cookbook... (connecting)");
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancel);
            
            // Handle redirects manually to validate each hop
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                redirectCount++;
                if (redirectCount > maxRedirects)
                {
                    return new DownloadResult(false, null, "Too many redirects.");
                }
                
                var location = response.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(location))
                {
                    return new DownloadResult(false, null, "Redirect without Location header.");
                }
                
                // Resolve relative URLs
                if (!Uri.TryCreate(location, UriKind.Absolute, out var nextUri))
                {
                    if (Uri.TryCreate(new Uri(url), location, out nextUri))
                        location = nextUri.ToString();
                }
                
                // Validate redirect target
                if (!PrereqDownloadHosts.IsAllowed(location))
                {
                    return new DownloadResult(false, null,
                        $"Redirect to disallowed host: {new Uri(location!).Host}");
                }
                
                url = location!;
                continue;
            }
            
            if (!response.IsSuccessStatusCode)
            {
                return new DownloadResult(false, null,
                    $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }
            
            // Download with progress
            var totalBytes = response.Content.Headers.ContentLength;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancel);
            using var fileStream = new FileStream(destPath, FileMode.Create,
                FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            
            var buffer = new byte[BufferSize];
            long downloaded = 0;
            int bytesRead;
            
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancel)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancel);
                downloaded += bytesRead;
                
                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    var pct = (int)(downloaded * 100 / totalBytes.Value);
                    var mb = downloaded / (1024.0 * 1024.0);
                    var totalMb = totalBytes.Value / (1024.0 * 1024.0);
                    _progress($"Downloading PAX Cookbook... {pct}% ({mb:F1} / {totalMb:F1} MB)");
                }
                else
                {
                    var mb = downloaded / (1024.0 * 1024.0);
                    _progress($"Downloading PAX Cookbook... {mb:F1} MB");
                }
            }
            
            break;
        }
        
        // Verify bytes against release metadata before inspecting the ZIP.
        // Order: cheap size sanity check first, then the SHA-256 comparison.
        if (expectation?.Size is long expectedSize)
        {
            var actualSize = new FileInfo(destPath).Length;
            if (actualSize != expectedSize)
            {
                _log.Write("payload-size-mismatch", "warning",
                    new Dictionary<string, object?>
                    {
                        ["expected"] = expectedSize,
                        ["actual"] = actualSize
                    });
                TryDelete(destPath);
                return new DownloadResult(false, null,
                    $"The downloaded file is the wrong size (expected {expectedSize:N0} bytes, " +
                    $"got {actualSize:N0}). The file may be incomplete or corrupted. Please try again.") { IntegrityFailure = true };
            }
        }

        if (!string.IsNullOrEmpty(expectation?.Sha256))
        {
            _progress("Checking the download...");
            var actualSha = ManifestVerifier.ComputeSha256(destPath);
            if (!string.Equals(actualSha, expectation!.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _log.Write("payload-sha-mismatch", "error",
                    new Dictionary<string, object?>
                    {
                        ["expected"] = expectation.Sha256,
                        ["actual"] = actualSha
                    });
                TryDelete(destPath);
                return new DownloadResult(false, null,
                    "The downloaded file failed integrity verification (SHA-256 mismatch). " +
                    "The file may be corrupted or tampered with. Please try again.") { IntegrityFailure = true };
            }
            _log.Write("payload-sha-verified", "info",
                new Dictionary<string, object?> { ["sha256"] = actualSha });
        }
        // Confirm that the verified artifact is a PAX Cookbook ZIP.
        _progress("Verifying download...");
        try
        {
            using var zip = ZipFile.OpenRead(destPath);
            // Check for manifest.json presence
            var hasManifest = zip.Entries.Any(e =>
                string.Equals(e.Name, "manifest.json", StringComparison.OrdinalIgnoreCase));
            if (!hasManifest)
            {
                return new DownloadResult(false, null,
                    "Downloaded file is not a valid PAX Cookbook payload (missing manifest.json).");
            }
        }
        catch (InvalidDataException)
        {
            return new DownloadResult(false, null,
                "The download appears to be corrupted. Please try again.");
        }
        catch (Exception ex)
        {
            return new DownloadResult(false, null,
                $"Could not verify download: {ex.Message}");
        }
        
        _log.Write("payload-download-complete", "info",
            new Dictionary<string, object?>
            {
                ["path"] = destPath,
                ["size"] = new FileInfo(destPath).Length,
                ["attempts"] = attempt
            });

        // Strip the Mark of the Web from the verified zip before it is extracted
        // so the internet-zone mark cannot propagate to the extracted payload
        // files. The installed tree is stripped again after copy as defence in
        // depth (see InstallVerb).
        MarkOfTheWeb.StripFile(destPath);

        return new DownloadResult(true, destPath, null)
        {
            Expectation = expectation,
            SourceUrl = payloadUrl
        };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
    
    // Cleanup the downloaded zip after extraction
    public static void Cleanup(string? zipPath)
    {
        if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
            return;
        try { File.Delete(zipPath); }
        catch { /* best-effort */ }
    }
}
