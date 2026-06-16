using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-A -- update manifest fetcher.
//
// Parity with app/broker/Update/Manifest.psm1:
//   * Single deterministic GET. No retry. No redirect-follow
//     (HttpClientHandler with AllowAutoRedirect=false).
//   * Hard ceiling on response body (64 KiB).
//   * 30 s timeout.
//   * User-Agent format: 'PAXCookbook/<ver> (manifest-check)'.
//   * URL validation: HTTPS-only; loopback HTTP permitted for
//     test harnesses (parity with Test-UpdateManifestUrl).
//   * Failure modes return structured tokens that map 1:1 to the
//     PS error vocabulary so the SPA state machine sees the same
//     code in both broker implementations.
//
// The HttpMessageHandler is injectable so tests run against a
// FakeHttpMessageHandler without ever touching the network.
public interface IUpdateManifestProbe
{
    UrlCheckOutcome           ValidateUrl(string? url);
    Task<ManifestFetchOutcome> FetchAsync(string url, string cookbookVersion, CancellationToken ct);
}

public sealed record UrlCheckOutcome(
    bool    Ok,
    string? Error,
    string? Message,
    Uri?    NormalizedUri);

public sealed class UpdateManifestProbe : IUpdateManifestProbe, IDisposable
{
    private const int    MaxManifestBytes      = 64 * 1024;
    private const int    ManifestTimeoutSeconds = 30;

    private readonly string         _cookbookVersionHint;
    private readonly HttpClient     _client;
    private readonly bool           _ownsClient;

    public UpdateManifestProbe(string cookbookVersionHint, HttpMessageHandler? handler = null)
    {
        _cookbookVersionHint = cookbookVersionHint ?? string.Empty;
        // AllowAutoRedirect=false is the actual flag that disables
        // 30x following; MaxAutomaticRedirections is only consulted
        // when redirects are enabled. .NET 8's SocketsHttpHandler
        // rejects MaxAutomaticRedirections=0 with ArgumentOutOfRange,
        // so we leave the property at its default.
        var resolvedHandler = handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies        = false,
        };
        _client = new HttpClient(resolvedHandler, disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(ManifestTimeoutSeconds),
        };
        _ownsClient = true;
    }

    public UrlCheckOutcome ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new UrlCheckOutcome(false, "not_configured",
                "No update manifest URL is configured.", null);
        }
        // Reject placeholder template tokens that ship in repo
        // manifests (e.g. "<UPDATE_MANIFEST_URL>"). Matches the
        // PS Test-UpdateManifestUrl regex.
        if (System.Text.RegularExpressions.Regex.IsMatch(url, "^<[A-Z0-9_]+>$"))
        {
            return new UrlCheckOutcome(false, "placeholder_url",
                "Update manifest URL is a release placeholder.", null);
        }
        Uri? uri;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
        {
            return new UrlCheckOutcome(false, "malformed_url",
                "Update manifest URL is not a well-formed absolute URI.", null);
        }
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme == "https")
        {
            return new UrlCheckOutcome(true, null, null, uri);
        }
        if (scheme == "http" && uri.IsLoopback)
        {
            return new UrlCheckOutcome(true, null, null, uri);
        }
        return new UrlCheckOutcome(false, "forbidden_scheme",
            "Update manifest URL must use HTTPS (got \"" + scheme + "\").",
            null);
    }

    public async Task<ManifestFetchOutcome> FetchAsync(
        string url, string cookbookVersion, CancellationToken ct)
    {
        var validation = ValidateUrl(url);
        if (!validation.Ok || validation.NormalizedUri is null)
        {
            return new ManifestFetchOutcome(
                Ok: false,
                Error: validation.Error,
                Message: validation.Message,
                RawText: null,
                SourceUrl: null);
        }

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, validation.NormalizedUri);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
                new ProductHeaderValue("PAXCookbook",
                    string.IsNullOrWhiteSpace(cookbookVersion)
                        ? "0.0.0" : cookbookVersion)));
            request.Headers.UserAgent.Add(
                new ProductInfoHeaderValue("(manifest-check)"));
            response = await _client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ManifestFetchOutcome(
                Ok: false,
                Error: "fetch_failed",
                Message: "Manifest fetch failed: " + ex.Message,
                RawText: null,
                SourceUrl: validation.NormalizedUri.AbsoluteUri);
        }

        using (response)
        {
            if (response.Content.Headers.ContentLength is long cl && cl > MaxManifestBytes)
            {
                return new ManifestFetchOutcome(
                    Ok: false,
                    Error: "manifest_too_large",
                    Message: "Manifest exceeds " + MaxManifestBytes + " bytes.",
                    RawText: null,
                    SourceUrl: validation.NormalizedUri.AbsoluteUri);
            }
            byte[] payload;
            try
            {
                payload = await response.Content.ReadAsByteArrayAsync(ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ManifestFetchOutcome(
                    Ok: false,
                    Error: "fetch_failed",
                    Message: "Manifest body read failed: " + ex.Message,
                    RawText: null,
                    SourceUrl: validation.NormalizedUri.AbsoluteUri);
            }
            if (payload.LongLength > MaxManifestBytes)
            {
                return new ManifestFetchOutcome(
                    Ok: false,
                    Error: "manifest_too_large",
                    Message: "Manifest exceeds " + MaxManifestBytes + " bytes.",
                    RawText: null,
                    SourceUrl: validation.NormalizedUri.AbsoluteUri);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new ManifestFetchOutcome(
                    Ok: false,
                    Error: "fetch_failed",
                    Message: "Manifest fetch returned HTTP " + (int)response.StatusCode + ".",
                    RawText: null,
                    SourceUrl: validation.NormalizedUri.AbsoluteUri);
            }
            var text = System.Text.Encoding.UTF8.GetString(payload);
            return new ManifestFetchOutcome(
                Ok: true,
                Error: null,
                Message: null,
                RawText: text,
                SourceUrl: validation.NormalizedUri.AbsoluteUri);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
