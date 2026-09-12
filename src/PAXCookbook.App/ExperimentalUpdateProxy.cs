// Experimental update-check proxy — GitHub Releases API reader (server-side).
//
// The experimental distribution channel discovers its newest build from the
// GitHub Releases API (the newest PRE-RELEASE, chosen by created_at) and that
// release's attached versions.json manifest. Historically the SPA fetched
// api.github.com DIRECTLY from the WebView2 renderer; that is the ONE place in
// the app that ever did so, and it was fragile in exactly the ways a browser
// context is: unauthenticated api.github.com is rate-limited to 60 req/hour per
// IP (trivially exhausted on shared corporate egress) and can be shaped by a
// corporate proxy differently than the raw CDN. Every OTHER GitHub read in the
// app (the Pantry) already goes through the broker for this reason.
//
// This route brings the experimental update check in line with that pattern:
// the BROKER makes the outbound calls (its own HttpClient — not the browser)
// and returns a compact result. Crucially it surfaces a SPECIFIC state
// (rate_limited / network_error / github_error / bad_response / no_prerelease /
// ok) so the UI can say something useful instead of a generic "make sure you're
// online" — the exact gap that made the field failure undiagnosable.
//
// Security posture (read-only, least privilege):
//   * Sits behind the same Bearer + broker-lock gates as every other /api/v1
//     route (no exemption).
//   * The releases-list URL is a FIXED constant (api.github.com, our repo). The
//     manifest asset URL comes from GitHub's own response for that fixed repo
//     and is validated to a GitHub https host before it is fetched, so no
//     caller-supplied host/scheme/path is ever contacted.
//   * Each outbound request is a single GET, no cookies, no credentials, an
//     explicit User-Agent, byte-capped and time-bounded. The caller's bearer
//     token is never forwarded upstream. Redirects are followed (bounded) only
//     so the release asset resolves to GitHub's own object CDN.
//   * It never runs PAX, never reads or writes appliance state, never touches a
//     recipe / cook / engine byte, and never reads a secret.

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace PAXCookbook.App;

internal static class ExperimentalUpdateProxy
{
    // Fixed releases-list endpoint for THIS repo. Never caller-influenced.
    internal const string ReleasesApiUrl =
        "https://api.github.com/repos/microsoft/PAX-Cookbook/releases?per_page=100";

    // Release asset name produced by tools\release\Build-Setup.ps1.
    private const string ManifestAssetName = "versions.json";

    // The releases list is a few KB; the manifest is well under 1 KB. Generous
    // caps that still reject a runaway body without buffering it whole.
    internal const int ReleasesMaxBytes = 2 * 1024 * 1024;
    internal const int ManifestMaxBytes = 256 * 1024;
    internal const int TimeoutSec = 15;

    internal enum SelectState
    {
        /// <summary>A newest pre-release with a versions.json asset was found.</summary>
        Ok,

        /// <summary>No pre-release exists yet — a valid empty state, not an error.</summary>
        NoPrerelease,

        /// <summary>Releases JSON was malformed, or the newest pre-release had no
        /// (GitHub-hosted) versions.json asset — an unexpected shape.</summary>
        BadResponse,
    }

    internal sealed record Selection(SelectState State, string? ManifestUrl);

    /// <summary>
    /// Pure, unit-tested selector: from the releases-list JSON, pick the newest
    /// PRE-RELEASE by created_at (descending — NOT by version parsing) and
    /// return that release's versions.json asset URL. Draft releases are
    /// ignored. Returns NoPrerelease when there is no pre-release at all, and
    /// BadResponse for malformed JSON or a pre-release missing its manifest.
    /// </summary>
    internal static Selection SelectNewestManifestUrl(string? releasesJson)
    {
        if (string.IsNullOrWhiteSpace(releasesJson))
        {
            return new Selection(SelectState.BadResponse, null);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(releasesJson);
        }
        catch (JsonException)
        {
            return new Selection(SelectState.BadResponse, null);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new Selection(SelectState.BadResponse, null);
            }

            JsonElement newest = default;
            DateTimeOffset newestCreated = DateTimeOffset.MinValue;
            bool found = false;

            foreach (JsonElement rel in doc.RootElement.EnumerateArray())
            {
                if (rel.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                // Pre-releases only; never a draft.
                if (!(rel.TryGetProperty("prerelease", out JsonElement pre)
                        && pre.ValueKind == JsonValueKind.True))
                {
                    continue;
                }
                if (rel.TryGetProperty("draft", out JsonElement draft)
                        && draft.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                DateTimeOffset created = DateTimeOffset.MinValue;
                if (rel.TryGetProperty("created_at", out JsonElement createdEl)
                        && createdEl.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(createdEl.GetString(), out DateTimeOffset parsed))
                {
                    created = parsed;
                }

                if (!found || created > newestCreated)
                {
                    newest = rel;
                    newestCreated = created;
                    found = true;
                }
            }

            if (!found)
            {
                // No pre-release published — a valid empty state, not an error.
                return new Selection(SelectState.NoPrerelease, null);
            }

            string? manifestUrl = SelectAssetUrl(newest, ManifestAssetName);
            if (manifestUrl is null)
            {
                // A pre-release exists but has no GitHub-hosted versions.json —
                // an unexpected shape we cannot compare against.
                return new Selection(SelectState.BadResponse, null);
            }

            return new Selection(SelectState.Ok, manifestUrl);
        }
    }

    private static string? SelectAssetUrl(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets)
                || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (!asset.TryGetProperty("name", out JsonElement nameEl)
                    || nameEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            if (!string.Equals(nameEl.GetString(), assetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (asset.TryGetProperty("browser_download_url", out JsonElement urlEl)
                    && urlEl.ValueKind == JsonValueKind.String)
            {
                string? url = urlEl.GetString();
                if (IsGitHubHost(url))
                {
                    return url;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True only for an https URL on a GitHub-owned host. The release asset
    /// browser_download_url is github.com (which 302-redirects to GitHub's
    /// object CDN); the releases API is api.github.com. Anything else is
    /// rejected so a spoofed release body can never point a fetch off GitHub.
    /// </summary>
    internal static bool IsGitHubHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? u))
        {
            return false;
        }
        if (!string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string h = u.Host.ToLowerInvariant();
        return h == "github.com"
            || h == "api.github.com"
            || h == "codeload.github.com"
            || h.EndsWith(".github.com", StringComparison.Ordinal)
            || h == "githubusercontent.com"
            || h.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    internal static async Task<IResult> HandleAsync(HttpContext ctx, VersionInfo version)
    {
        // Under active test isolation with update checks disabled, return a
        // bounded disabled state and make NO outbound network request. This is
        // the only place in the experimental channel that contacts the network
        // for update discovery, so gating it here guarantees an isolated run
        // never reaches api.github.com for an update check.
        if (TestIsolationRuntime.IsActive && !TestIsolationRuntime.Current!.UpdateChecksEnabled)
        {
            return Results.Json(new { ok = false, state = "disabled_in_test_isolation" });
        }

        string userAgent = "PAXCookbook/" + version.CookbookVersion + " (update-check)";

        // 1) Releases list (fixed URL, api.github.com). Returns JSON directly.
        FetchOutcome list = await FetchAsync(
            new Uri(ReleasesApiUrl),
            userAgent,
            "application/vnd.github+json",
            ReleasesMaxBytes,
            ctx.RequestAborted).ConfigureAwait(false);

        IResult? listError = MapFetchError(list);
        if (listError is not null)
        {
            return listError;
        }

        Selection sel = SelectNewestManifestUrl(Encoding.UTF8.GetString(list.Bytes!));
        if (sel.State == SelectState.NoPrerelease)
        {
            // Benign empty state — the UI shows "no experimental builds yet",
            // NOT an error.
            return Results.Json(new { ok = true, state = "no_prerelease" });
        }
        if (sel.State == SelectState.BadResponse || sel.ManifestUrl is null)
        {
            return Results.Json(new
            {
                ok = false,
                state = "bad_response",
                detail = "GitHub returned an unexpected releases response.",
            });
        }

        // 2) The newest pre-release's versions.json asset. browser_download_url
        //    302-redirects to GitHub's object CDN, so redirects are followed
        //    (the initial host was validated to GitHub in the selector).
        FetchOutcome manifest = await FetchAsync(
            new Uri(sel.ManifestUrl),
            userAgent,
            "application/octet-stream",
            ManifestMaxBytes,
            ctx.RequestAborted).ConfigureAwait(false);

        IResult? manifestError = MapFetchError(manifest);
        if (manifestError is not null)
        {
            return manifestError;
        }

        string manifestJson = Encoding.UTF8.GetString(manifest.Bytes!);
        // Validate it is a versions.json-shaped object before handing it back.
        try
        {
            using JsonDocument mdoc = JsonDocument.Parse(manifestJson);
            if (mdoc.RootElement.ValueKind != JsonValueKind.Object
                    || !mdoc.RootElement.TryGetProperty("current", out _))
            {
                return Results.Json(new
                {
                    ok = false,
                    state = "bad_response",
                    detail = "The pre-release update information was not in the expected format.",
                });
            }
        }
        catch (JsonException)
        {
            return Results.Json(new
            {
                ok = false,
                state = "bad_response",
                detail = "The pre-release update information was unreadable.",
            });
        }

        // Pass the raw manifest text back; the SPA parses it and runs the same
        // version/SHA compare it uses for the stable channel.
        return Results.Json(new { ok = true, state = "ok", manifestJson });
    }

    // Map a fetch outcome to a typed error response, or null when it succeeded.
    private static IResult? MapFetchError(FetchOutcome o)
    {
        if (o.NetworkError)
        {
            return Results.Json(new
            {
                ok = false,
                state = "network_error",
                detail = "Couldn't reach GitHub. Check your internet connection, then try again.",
            });
        }
        if (o.StatusCode is 403 or 429)
        {
            return Results.Json(new
            {
                ok = false,
                state = "rate_limited",
                detail = "GitHub's update service is rate-limiting requests right now "
                    + "(this can happen on shared corporate networks). Try again in a few minutes.",
            });
        }
        if (o.Bytes is null)
        {
            return Results.Json(new
            {
                ok = false,
                state = "github_error",
                detail = "GitHub returned an error (HTTP " + (o.StatusCode?.ToString() ?? "unknown") + ").",
            });
        }
        return null;
    }

    private readonly record struct FetchOutcome(bool NetworkError, int? StatusCode, byte[]? Bytes);

    // Single deterministic GET — no retry, no cookies, no credentials, bounded
    // redirect following (only so a release asset resolves to GitHub's CDN),
    // byte-capped and time-bounded.
    private static async Task<FetchOutcome> FetchAsync(
        Uri uri,
        string userAgent,
        string acceptHeader,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using HttpClientHandler handler = new()
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            UseCookies = false,
            UseDefaultCredentials = false,
        };
        using HttpClient http = new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSec),
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
        catch
        {
            return new FetchOutcome(NetworkError: true, StatusCode: null, Bytes: null);
        }

        using (resp)
        {
            int status = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                return new FetchOutcome(false, status, null);
            }

            long? contentLength = resp.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maxBytes)
            {
                // Oversize — treat as a non-usable body (surfaces as github_error).
                return new FetchOutcome(false, status, null);
            }

            try
            {
                await using Stream stream =
                    await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using MemoryStream buffer = new();
                byte[] chunk = new byte[64 * 1024];
                int read;
                int total = 0;
                while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                                           .ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        return new FetchOutcome(false, status, null);
                    }
                    buffer.Write(chunk, 0, read);
                }
                return new FetchOutcome(false, status, buffer.ToArray());
            }
            catch
            {
                return new FetchOutcome(NetworkError: true, StatusCode: status, Bytes: null);
            }
        }
    }
}
