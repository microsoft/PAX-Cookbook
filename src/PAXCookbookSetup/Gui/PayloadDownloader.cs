using System.IO.Compression;
using System.Net.Http;

namespace PAXCookbookSetup.Gui;

// Downloads the PAX Cookbook payload zip from GitHub Release at install time.
// Used when the Setup exe does not embed the payload (lightweight bootstrapper).
public sealed class PayloadDownloader
{
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
    
    public sealed record DownloadResult(bool Success, string? ZipPath, string? Error);
    
    public async Task<DownloadResult> DownloadAsync(CancellationToken cancel = default)
    {
        var destPath = Path.Combine(_tempPath, "PAXCookbook_Payload.zip");
        
        // Clean up any prior partial download
        if (File.Exists(destPath))
        {
            try { File.Delete(destPath); }
            catch { /* best-effort */ }
        }

        // Fetch the versions manifest once up front. If it can't be reached or
        // parsed, we degrade to zip-integrity-only checking (offline / proxy /
        // air-gapped scenarios still install) rather than blocking the install.
        var expectation = await new ManifestVerifier(_log).TryFetchAsync(cancel);
        if (expectation is null)
        {
            _progress("Version manifest unavailable — verifying download integrity only.");
            _log.Write("payload-sha-skipped-no-manifest", "warning");
        }

        string? lastError = null;
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var result = await TryDownloadAsync(destPath, attempt, expectation, cancel);
                if (result.Success)
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
    
    private async Task<DownloadResult> TryDownloadAsync(
        string destPath, int attempt, ManifestVerifier.PayloadExpectation? expectation,
        CancellationToken cancel)
    {
        // Validate the URL is allowed
        if (!PrereqDownloadHosts.IsAllowed(PayloadUrl))
        {
            return new DownloadResult(false, null,
                $"Payload URL not in allowed host list: {PayloadUrl}");
        }
        
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false  // Manual redirect handling for host validation
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        
        var url = PayloadUrl;
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
        
        // Integrity verification against the versions manifest (when available).
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
                    $"got {actualSize:N0}). The file may be incomplete or corrupted. Please try again.");
            }
        }

        if (!string.IsNullOrEmpty(expectation?.Sha256))
        {
            _progress("Verifying download integrity (SHA-256)...");
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
                    "The file may be corrupted or tampered with. Please try again.");
            }
            _log.Write("payload-sha-verified", "info",
                new Dictionary<string, object?> { ["sha256"] = actualSha });
        }
        else
        {
            _log.Write("payload-sha-skipped", "warning");
        }

        // Structural fallback check: confirm it is a valid PAX Cookbook zip.
        // This is the sole integrity gate when the manifest was unavailable.
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

        return new DownloadResult(true, destPath, null);
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
