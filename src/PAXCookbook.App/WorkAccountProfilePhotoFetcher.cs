// Native Work-account profile-photo fetcher — EXPERIMENTAL, gated.
//
// This entire file compiles ONLY under the EXPERIMENTAL_WAM constant (set by the
// ExperimentalWam=true build property). Stable / customer builds define no such
// constant and contain none of this code, so the stable payload has no Graph
// photo path at all.
//
// Bounded token / photo doctrine (binding): the Work-account User.Read access
// token is used ONLY for the single exact request
//   GET https://graph.microsoft.com/v1.0/me/photo/$value
// performed INSIDE the HWND-owning native window process. The token is attached
// as the HTTPS Authorization header here and NOWHERE else; it never leaves this
// process, is never logged, is never placed in an exception, and is never used
// for audit, directory, managed-key, permission-profile, PAX/Bake, or any other
// tenant-data query. The request is built entirely from constant components — no
// caller-supplied URL, host, path, method, query, or body is ever accepted — so
// a wrong scheme/host/path/method/query is rejected BY CONSTRUCTION and by an
// explicit guard BEFORE any network call. Any failure falls back cleanly and
// never blocks authorization.

#if EXPERIMENTAL_WAM
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace PAXCookbook.App;

// The bounded outcome of a single profile-photo fetch. No value ever carries a
// Graph error body, a response-header dump, the URL, the token, an account
// value, an exception message, or a stack trace.
internal enum WorkAccountPhotoOutcome
{
    Available = 0,
    NotFound = 1,
    Forbidden = 2,
    Timeout = 3,
    Redirected = 4,
    WrongContentType = 5,
    TooLarge = 6,
    TransportFailure = 7,
    MalformedResponse = 8,
}

// The bounded result of a photo fetch. On Available it carries the accepted
// image bytes plus the exact accepted content type (held in-process only); on
// every other outcome it carries no payload. It exposes NO token, URL, header
// dump, account value, or error text — only the bounded outcome and, on success,
// the image bytes + content type.
internal sealed class WorkAccountPhotoFetchResult
{
    private WorkAccountPhotoFetchResult(WorkAccountPhotoOutcome outcome, byte[]? imageBytes, string? contentType)
    {
        Outcome = outcome;
        ImageBytes = imageBytes;
        ContentType = contentType;
    }

    internal WorkAccountPhotoOutcome Outcome { get; }

    // Accepted image bytes — non-null ONLY when Outcome == Available.
    internal byte[]? ImageBytes { get; }

    // Exact accepted content type — non-null ONLY when Outcome == Available.
    internal string? ContentType { get; }

    internal static WorkAccountPhotoFetchResult Available(byte[] imageBytes, string contentType) =>
        new(WorkAccountPhotoOutcome.Available, imageBytes, contentType);

    internal static WorkAccountPhotoFetchResult Failure(WorkAccountPhotoOutcome outcome) =>
        new(outcome, imageBytes: null, contentType: null);
}

// The immutable, constant-only request policy for the profile-photo GET. Nothing
// here is caller-supplied; every value is a compile-time constant, and the guard
// proves an off-policy request is rejected before any network call.
internal static class WorkAccountGraphPhotoRequest
{
    internal const string Method = "GET";
    internal const string Scheme = "https";
    internal const string Host = "graph.microsoft.com";
    internal const string Path = "/v1.0/me/photo/$value";

    // The single, exact, constant endpoint. Built once from the constants above.
    internal static Uri Endpoint { get; } = new Uri("https://graph.microsoft.com/v1.0/me/photo/$value");

    // True ONLY for the exact GET https://graph.microsoft.com/v1.0/me/photo/$value
    // request with no query and no body. A wrong method, non-HTTPS scheme, wrong
    // host, wrong path, any query, or any body is rejected. Pure and testable.
    internal static bool IsAllowed(string method, Uri? uri, bool hasBody)
    {
        if (hasBody)
        {
            return false;
        }

        if (!string.Equals(method, Method, StringComparison.Ordinal))
        {
            return false;
        }

        if (uri is null)
        {
            return false;
        }

        return string.Equals(uri.Scheme, Scheme, StringComparison.Ordinal) &&
               string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.AbsolutePath, Path, StringComparison.Ordinal) &&
               string.IsNullOrEmpty(uri.Query);
    }

    // Builds the one and only request message from constants: HTTP GET, the exact
    // endpoint, no body. GET is impossible to change (there is no method
    // parameter). Returns null if — impossibly — the constants failed the guard,
    // so the fetcher can fail closed without a network call.
    internal static HttpRequestMessage? Build()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        if (!IsAllowed(request.Method.Method, request.RequestUri, hasBody: request.Content is not null))
        {
            request.Dispose();
            return null;
        }

        return request;
    }
}

// The photo fetcher port. Injected into the authenticator only in the HWND-
// owning window process; the default (stable) build never constructs it.
internal interface IWorkAccountProfilePhotoFetcher
{
    // Perform the single bounded profile-photo GET, attaching the supplied access
    // token as the HTTPS Authorization header. Returns a bounded outcome only;
    // never throws request details, never logs, never surfaces the token or URL.
    Task<WorkAccountPhotoFetchResult> FetchAsync(string accessToken, CancellationToken cancellationToken);
}

internal sealed class WorkAccountProfilePhotoFetcher : IWorkAccountProfilePhotoFetcher, IDisposable
{
    // HARD ceiling enforced DURING streaming: 2 MiB. The body is read in bounded
    // chunks and rejected the instant the cumulative size would exceed the
    // ceiling — the whole body is never buffered first.
    internal const int MaxPhotoBytes = 2 * 1024 * 1024;

    private const int ReadChunkBytes = 64 * 1024;
    private const int RequestTimeoutSeconds = 10;

    // Only the exact profile-photo media type is accepted. Microsoft Graph
    // returns the /me/photo/$value binary as JPEG; a broad image/* is never
    // accepted and arbitrary bytes are never sniffed as an image.
    private const string AcceptedContentType = "image/jpeg";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    // Production constructor: builds the hardened handler + client. No logging or
    // tracing DelegatingHandler is ever attached.
    internal WorkAccountProfilePhotoFetcher()
    {
        _client = new HttpClient(CreateHardenedHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
        };
        _ownsClient = true;
    }

    // Test seam: inject a synthetic transport. The streaming cap, content-type,
    // redirect, and validation logic are identical; only the wire is synthetic.
    internal WorkAccountProfilePhotoFetcher(HttpMessageHandler transport)
    {
        if (transport is null)
        {
            throw new ArgumentNullException(nameof(transport));
        }

        _client = new HttpClient(transport, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
        };
        _ownsClient = true;
    }

    // The exact hardened handler: no auto-redirect, no cookies, no default
    // credentials, no pre-auth. A redirect is surfaced as an outcome, never
    // followed.
    internal static HttpClientHandler CreateHardenedHandler() =>
        new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            Credentials = null,
            PreAuthenticate = false,
        };

    public async Task<WorkAccountPhotoFetchResult> FetchAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(accessToken))
        {
            return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.TransportFailure);
        }

        // Built from constants; a null here means the constant policy failed its
        // own guard — fail closed with no network call.
        HttpRequestMessage? request = WorkAccountGraphPhotoRequest.Build();
        if (request is null)
        {
            return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.TransportFailure);
        }

        try
        {
            // The bearer token is attached ONLY here, as the exact HTTPS
            // Authorization header, immediately before the send.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using HttpResponseMessage response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            int status = (int)response.StatusCode;

            if (status is >= 300 and <= 399)
            {
                return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.Redirected);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.NotFound);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.Forbidden);
            }

            if (!response.IsSuccessStatusCode)
            {
                return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.TransportFailure);
            }

            // Accept ONLY the exact profile-photo media type. Never image/*, never
            // an error body, never sniffed bytes.
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null ||
                !string.Equals(mediaType, AcceptedContentType, StringComparison.OrdinalIgnoreCase))
            {
                return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.WrongContentType);
            }

            return await ReadBoundedJpegAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A photo fetch never blocks authorization; a cancel/timeout is a
            // bounded fallback, never a rethrow that could surface request detail.
            return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.Timeout);
        }
        catch (HttpRequestException)
        {
            return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.TransportFailure);
        }
        catch (IOException)
        {
            return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.TransportFailure);
        }
        finally
        {
            request.Dispose();
        }
    }

    // Streams the response body in bounded chunks, enforcing the 2 MiB ceiling
    // DURING streaming: the instant the next chunk would push the cumulative size
    // past the ceiling, the read stops and TooLarge is returned WITHOUT buffering
    // the offending chunk and without ever buffering the whole body first.
    private static async Task<WorkAccountPhotoFetchResult> ReadBoundedJpegAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        byte[] chunk = new byte[ReadChunkBytes];
        using var accumulator = new MemoryStream();

        while (true)
        {
            int read = await body
                .ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                .ConfigureAwait(false);

            if (read <= 0)
            {
                break;
            }

            if (accumulator.Length + read > MaxPhotoBytes)
            {
                // Reject before over-buffering: the chunk is NOT appended.
                return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.TooLarge);
            }

            accumulator.Write(chunk, 0, read);
        }

        byte[] bytes = accumulator.ToArray();
        Array.Clear(chunk, 0, chunk.Length);

        if (!IsWellFormedJpeg(bytes))
        {
            Array.Clear(bytes, 0, bytes.Length);
            return WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.MalformedResponse);
        }

        return WorkAccountPhotoFetchResult.Available(bytes, AcceptedContentType);
    }

    // Minimal, bounded structural check: a JPEG begins with the SOI marker
    // (FF D8 FF) and ends with the EOI marker (FF D9). A truncated or non-JPEG
    // body fails this check and falls back to initials. This is NOT a decoder —
    // it only rejects obviously-malformed/truncated content; it never decodes or
    // executes the bytes.
    private static bool IsWellFormedJpeg(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 4)
        {
            return false;
        }

        bool soi = bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        bool eoi = bytes[^2] == 0xFF && bytes[^1] == 0xD9;
        return soi && eoi;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
#endif
