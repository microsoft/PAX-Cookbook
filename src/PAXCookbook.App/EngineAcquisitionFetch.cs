// X14 HTTP fetchers. Pure I/O: no parsing of bodies beyond byte-count
// capping. Callers (Program.cs orchestrator) hand the bytes to the
// pure-logic ports in EngineAcquisitionValidation.cs.
//
// Oracle parity:
//   app\broker\Update\Manifest.psm1::Test-UpdateManifestUrl
//       HTTPS required; loopback HTTP allowed (for harness only).
//   app\broker\Engine\Acquisition.psm1
//       Manifest:  64 KB cap, 30 s timeout
//       Signature: 16 KB cap, 30 s timeout
//       Script:    4 MB cap, 60 s timeout
//   All requests: single GET, MaxAutomaticRedirections=0, no cookies,
//   no credentials, explicit User-Agent.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PAXCookbook.App;

internal static class FetchLimits
{
    internal const int ManifestMaxBytes = 64 * 1024;
    internal const int SignatureMaxBytes = 16 * 1024;
    internal const int PaxScriptMaxBytes = 4 * 1024 * 1024;
    internal const int ManifestTimeoutSec = 30;
    internal const int ScriptTimeoutSec = 60;
}

internal sealed record UrlValidationResult(bool Ok, string? Error, string? Message, Uri? Uri);

internal static class UrlValidator
{
    internal static UrlValidationResult Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new UrlValidationResult(false, "not_configured",
                "No URL is configured.", null);
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(url, "^<[A-Z0-9_]+>$"))
        {
            return new UrlValidationResult(false, "placeholder_url",
                "URL is a release placeholder.", null);
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return new UrlValidationResult(false, "malformed_url",
                "URL is not a well-formed absolute URI.", null);
        }
        string scheme = uri.Scheme.ToLowerInvariant();
        if (scheme == "https") { return new UrlValidationResult(true, null, null, uri); }
        if (scheme == "http" && uri.IsLoopback)
        {
            return new UrlValidationResult(true, null, null, uri);
        }
        return new UrlValidationResult(false, "forbidden_scheme",
            "URL must use HTTPS (got \"" + scheme + "\").", null);
    }
}

internal sealed record FetchResult(
    bool Ok,
    string? Error,
    string? Message,
    byte[]? Bytes,
    int? StatusCode,
    int? ByteCount,
    int? ByteCap);

internal static class HttpFetcher
{
    // Single deterministic GET — no retry, no cookies, no auto-redirect.
    // Caller specifies size cap and timeout. The same handler shape is
    // used for manifest, signature, and script fetches; the difference
    // is purely the cap + timeout + User-Agent.
    internal static async Task<FetchResult> FetchAsync(
        Uri uri,
        string userAgent,
        string acceptHeader,
        int maxBytes,
        int timeoutSec,
        string genericErrorTag,
        string oversizeErrorTag,
        CancellationToken cancellationToken)
    {
        using HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
        };
        using HttpClient http = new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSec),
        };

        using HttpRequestMessage req = new(HttpMethod.Get, uri);
        req.Headers.UserAgent.ParseAdd(userAgent);
        if (!string.IsNullOrEmpty(acceptHeader))
        {
            req.Headers.Accept.ParseAdd(acceptHeader);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                             .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new FetchResult(false, genericErrorTag,
                "GET " + uri + " failed: " + ex.Message, null, null, null, null);
        }

        using (resp)
        {
            int status = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                return new FetchResult(false, genericErrorTag,
                    "GET " + uri + " returned HTTP " + status + ".",
                    null, status, null, null);
            }

            // Server-declared content-length, when present, is enforced
            // early so an oversize body is rejected without copying all
            // bytes into memory.
            long? contentLength = resp.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maxBytes)
            {
                return new FetchResult(false, oversizeErrorTag,
                    "Body " + contentLength.Value + " bytes exceeds cap of " + maxBytes + " bytes.",
                    null, status, (int)contentLength.Value, maxBytes);
            }

            byte[] bytes;
            try
            {
                using Stream stream = await resp.Content.ReadAsStreamAsync(cancellationToken)
                                                        .ConfigureAwait(false);
                using MemoryStream ms = new();
                byte[] buffer = new byte[64 * 1024];
                int total = 0;
                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                                           .ConfigureAwait(false);
                    if (read <= 0) { break; }
                    total += read;
                    if (total > maxBytes)
                    {
                        return new FetchResult(false, oversizeErrorTag,
                            "Body exceeds cap of " + maxBytes + " bytes.",
                            null, status, total, maxBytes);
                    }
                    ms.Write(buffer, 0, read);
                }
                bytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                return new FetchResult(false, genericErrorTag,
                    "Reading response body from " + uri + " failed: " + ex.Message,
                    null, status, null, null);
            }

            if (bytes.Length == 0)
            {
                return new FetchResult(false, genericErrorTag,
                    "GET " + uri + " returned a zero-length body.", null, status, 0, null);
            }

            return new FetchResult(true, null, null, bytes, status, bytes.Length, maxBytes);
        }
    }
}

internal static class ManifestFetcher
{
    internal static Task<FetchResult> FetchManifestAsync(
        Uri uri, string cookbookVersion, CancellationToken cancellationToken)
        => HttpFetcher.FetchAsync(
            uri,
            userAgent: "PAXCookbook/" + cookbookVersion + " (engine-manifest)",
            acceptHeader: "application/json",
            maxBytes: FetchLimits.ManifestMaxBytes,
            timeoutSec: FetchLimits.ManifestTimeoutSec,
            genericErrorTag: "manifest_fetch_failed",
            oversizeErrorTag: "manifest_too_large",
            cancellationToken);

    internal static Task<FetchResult> FetchSignatureAsync(
        Uri uri, string cookbookVersion, CancellationToken cancellationToken)
        => HttpFetcher.FetchAsync(
            uri,
            userAgent: "PAXCookbook/" + cookbookVersion + " (engine-manifest-sig)",
            acceptHeader: "application/json",
            maxBytes: FetchLimits.SignatureMaxBytes,
            timeoutSec: FetchLimits.ManifestTimeoutSec,
            genericErrorTag: "signature_fetch_failed",
            oversizeErrorTag: "signature_too_large",
            cancellationToken);
}

internal static class ScriptFetcher
{
    internal static Task<FetchResult> FetchScriptAsync(
        Uri uri, string cookbookVersion, CancellationToken cancellationToken)
        => HttpFetcher.FetchAsync(
            uri,
            userAgent: "PAXCookbook/" + cookbookVersion + " (pax-script-download)",
            acceptHeader: "text/plain, application/octet-stream;q=0.8, */*;q=0.5",
            maxBytes: FetchLimits.PaxScriptMaxBytes,
            timeoutSec: FetchLimits.ScriptTimeoutSec,
            genericErrorTag: "script_fetch_failed",
            oversizeErrorTag: "script_too_large",
            cancellationToken);
}
