// Pantry repository viewer — GitHub REST API reader.
//
// The Pantry surfaces a small, curated set of public GitHub repositories. To
// show one inside the appliance window the broker calls GitHub's public REST
// API server-side for (1) the repo's rendered README HTML and (2) the repo
// metadata, then returns a single combined JSON document. The SPA renders that
// natively (the README in a markdown container, the metadata as a header).
//
// This replaces the earlier full-page HTML proxy: GitHub is a client-rendered
// SPA whose proxied HTML rendered broken, and the API approach is both cleaner
// and far more constrained — the only host ever contacted is api.github.com,
// parameterized solely by a validated {owner}/{repo}, so there is no arbitrary
// URL fetch surface at all.
//
// Security posture (read-only, least privilege):
//   * Sits behind the same Bearer + broker-lock gates as every other
//     /api/v1 route — there is NO authentication exemption.
//   * owner/repo are validated to a strict charset with no ".." traversal,
//     then interpolated into a fixed https://api.github.com/repos/... template.
//     No caller-supplied host, scheme, or path escape is possible.
//   * Each outbound request is a single GET with no auto-redirect, no cookies,
//     and no credentials (HttpFetcher), byte-capped and time-bounded. The
//     caller's bearer token is never forwarded upstream.
//   * The README HTML returned by GitHub's html+json media type is already
//     server-sanitized by GitHub's markup pipeline (no <script>, no on*
//     handlers, no javascript: URLs) — the same HTML shown on github.com.
//   * It never runs PAX, never reads or writes appliance state, never touches
//     a recipe / cook / engine byte, and never reads a secret.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace PAXCookbook.App;

internal static class PantryProxy
{
    // Metadata is a small JSON document; the rendered README can be larger.
    // Both are bounded well under these caps. 15 s covers a slow public fetch
    // without hanging the request.
    internal const int MetaMaxBytes = 1 * 1024 * 1024;
    internal const int ReadmeMaxBytes = 5 * 1024 * 1024;
    internal const int TimeoutSec = 15;

    // GitHub owner / repo path segments. Owners are alphanumeric + hyphen;
    // repos also allow "." and "_". We accept the superset, require the first
    // character to be alphanumeric (rejects a leading dot, "." and ".."), cap
    // the length, and explicitly reject any ".." sequence. With "/" excluded by
    // the charset and ".." rejected, neither segment can traverse the fixed API
    // path.
    private const int MaxSegmentLength = 100;

    internal static bool IsValidSegment([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxSegmentLength)
        {
            return false;
        }
        if (value.IndexOf("..", StringComparison.Ordinal) >= 0)
        {
            return false;
        }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (i > 0)
            {
                ok = ok || c == '.' || c == '_' || c == '-';
            }
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    internal static async Task<IResult> HandleAsync(HttpContext ctx, VersionInfo version)
    {
        string? owner = ctx.Request.Query["owner"];
        string? repo = ctx.Request.Query["repo"];

        if (!IsValidSegment(owner) || !IsValidSegment(repo))
        {
            return Results.Json(
                new { ok = false, error = "Invalid repository reference." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        string userAgent = "PAXCookbook/" + version.CookbookVersion + " (pantry-api)";

        // 1) Repo metadata. This is the authoritative existence check: a 404
        //    here means the repository is not visible.
        Uri metaUri = new("https://api.github.com/repos/" + owner + "/" + repo);
        FetchResult meta = await HttpFetcher.FetchAsync(
            metaUri,
            userAgent: userAgent,
            acceptHeader: "application/vnd.github+json",
            maxBytes: MetaMaxBytes,
            timeoutSec: TimeoutSec,
            genericErrorTag: "github_fetch_failed",
            oversizeErrorTag: "github_too_large",
            cancellationToken: ctx.RequestAborted).ConfigureAwait(false);

        if (!meta.Ok || meta.Bytes is null)
        {
            return Results.Json(new { ok = false, error = DescribeFetchError(meta) });
        }

        RepoMeta parsed;
        try
        {
            parsed = ParseMeta(meta.Bytes);
        }
        catch (JsonException)
        {
            return Results.Json(new { ok = false, error = "GitHub returned an unexpected response." });
        }

        // 2) Rendered README HTML (best-effort). A repo with no README still
        //    yields a useful metadata view, so a README failure is not fatal.
        //    GitHub's html+json media type returns HTML its markup pipeline has
        //    already sanitized (no <script>, no on* handlers, no javascript:).
        string readmeHtml = string.Empty;
        Uri readmeUri = new("https://api.github.com/repos/" + owner + "/" + repo + "/readme");
        FetchResult readme = await HttpFetcher.FetchAsync(
            readmeUri,
            userAgent: userAgent,
            acceptHeader: "application/vnd.github.html+json",
            maxBytes: ReadmeMaxBytes,
            timeoutSec: TimeoutSec,
            genericErrorTag: "github_fetch_failed",
            oversizeErrorTag: "github_too_large",
            cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
        if (readme.Ok && readme.Bytes is not null)
        {
            readmeHtml = Encoding.UTF8.GetString(readme.Bytes);
            // GitHub's html+json README absolutizes most image src to camo /
            // raw URLs, but some relative paths (e.g. ./docs/x.png) survive and
            // can't resolve from the appliance's localhost origin. Rewrite any
            // remaining relative <img src> to its raw.githubusercontent.com URL
            // so screenshots render; absolute URLs are left untouched. The
            // branch (a GitHub-supplied JSON field) is constrained to a safe ref
            // charset before it is concatenated into the src URL so it cannot
            // break out of the attribute; anything unexpected falls back to HEAD.
            string branch = IsSafeBranch(parsed.DefaultBranch) ? parsed.DefaultBranch : "HEAD";
            readmeHtml = RewriteRelativeImageUrls(readmeHtml, owner, repo, branch);
        }

        return Results.Json(new
        {
            ok = true,
            owner,
            repo,
            description = parsed.Description,
            stars = parsed.Stars,
            forks = parsed.Forks,
            language = parsed.Language,
            license = parsed.License,
            updatedAt = parsed.UpdatedAt,
            topics = parsed.Topics,
            defaultBranch = parsed.DefaultBranch,
            readmeHtml,
            htmlUrl = parsed.HtmlUrl,
        });
    }

    // A git branch/ref that is safe to interpolate into a raw.githubusercontent
    // URL path. Refs legitimately contain letters, digits, '.', '_', '-', and
    // '/'; anything else (notably '"', '<', '>', '=', spaces, backticks) is
    // rejected so the value can never terminate the img src attribute or inject
    // markup. '..' is rejected to avoid path traversal in the raw URL.
    internal static bool IsSafeBranch([NotNullWhen(true)] string? branch)
    {
        if (string.IsNullOrEmpty(branch) || branch.Length > 255)
        {
            return false;
        }
        if (branch.IndexOf("..", StringComparison.Ordinal) >= 0)
        {
            return false;
        }
        foreach (char c in branch)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '.' || c == '_' || c == '-' || c == '/';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    // Rewrite relative <img src> values in GitHub-rendered README HTML to their
    // absolute raw.githubusercontent.com URL so screenshots referenced by a
    // repo-relative path resolve from the appliance origin. Only src values that
    // are NOT already absolute (http/https), protocol-relative (//), a data:
    // URI, or a fragment (#) are rewritten; everything else is left as-is. A
    // MatchEvaluator builds the replacement from the captured groups plus a
    // trusted base URL, so no interpolated value is ever parsed as a regex
    // substitution token. The rewrite only ever changes an image's src
    // attribute value; it cannot introduce a <script>, an event handler, or any
    // new markup, so the GitHub-sanitized HTML stays sanitized.
    private static readonly Regex RelativeImgSrc = new(
        @"(<img\b[^>]*?\ssrc="")(?!https?://|//|data:|#)(\.?/?)([^""]+)("")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string RewriteRelativeImageUrls(string html, string owner, string repo, string branch)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }
        string baseUrl = "https://raw.githubusercontent.com/" + owner + "/" + repo + "/" + branch + "/";
        return RelativeImgSrc.Replace(
            html,
            m => m.Groups[1].Value + baseUrl + m.Groups[3].Value + m.Groups[4].Value);
    }

    // Map a failed GitHub fetch to a short, user-facing message. A null status
    // code means the request never completed (DNS / connect / timeout); a 404
    // means the repo is not visible; a 403 / 429 from api.github.com on an
    // unauthenticated public read is the rate limiter.
    private static string DescribeFetchError(FetchResult result)
    {
        if (result.StatusCode is null)
        {
            return "Unable to reach GitHub. Check your internet connection.";
        }
        return result.StatusCode switch
        {
            404 => "Repository not found.",
            403 or 429 => "GitHub API rate limit reached. Try again in a few minutes.",
            _ => "GitHub returned an error (HTTP " + result.StatusCode + ").",
        };
    }

    internal sealed record RepoMeta(
        string? Description,
        int Stars,
        int Forks,
        string? Language,
        string? License,
        string? UpdatedAt,
        IReadOnlyList<string> Topics,
        string? DefaultBranch,
        string? HtmlUrl);

    internal static RepoMeta ParseMeta(byte[] json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string? license = null;
        if (root.TryGetProperty("license", out JsonElement lic) && lic.ValueKind == JsonValueKind.Object)
        {
            license = GetString(lic, "spdx_id");
            if (string.IsNullOrEmpty(license) || license == "NOASSERTION")
            {
                license = GetString(lic, "name");
            }
        }

        List<string> topics = new();
        if (root.TryGetProperty("topics", out JsonElement tp) && tp.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement t in tp.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    string? s = t.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        topics.Add(s);
                    }
                }
            }
        }

        return new RepoMeta(
            GetString(root, "description"),
            GetInt(root, "stargazers_count"),
            GetInt(root, "forks_count"),
            GetString(root, "language"),
            license,
            GetString(root, "updated_at"),
            topics,
            GetString(root, "default_branch"),
            GetString(root, "html_url"));
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }
        return null;
    }

    private static int GetInt(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out int v))
        {
            return v;
        }
        return 0;
    }

    // ---- Repo contents (file explorer) -------------------------------------
    //
    // GET /api/v1/pantry/repo-contents?owner=&repo=&path= returns a single
    // directory listing from GitHub's Contents API so the SPA can render a
    // lazily-expanded file-explorer tree. Read-only against api.github.com:
    // owner/repo are charset-validated, the optional path is validated to a safe
    // relative form (no "..", no leading "/", no breakout/shell/control
    // characters) and then percent-encoded per segment before it is interpolated
    // into a fixed .../contents/{path} URL — no host, scheme, or path escape is
    // possible. Each file's download URL is validated to a trusted GitHub https
    // host before it is returned (an unsafe value becomes null), so the SPA only
    // ever renders a safe <a href> the shell hands to the OS browser. It never
    // runs PAX, mutates state, or reads a secret.

    internal static async Task<IResult> HandleContentsAsync(HttpContext ctx, VersionInfo version)
    {
        string? owner = ctx.Request.Query["owner"];
        string? repo = ctx.Request.Query["repo"];
        string? pathRaw = ctx.Request.Query["path"];
        string path = pathRaw ?? string.Empty;

        if (!IsValidSegment(owner) || !IsValidSegment(repo))
        {
            return Results.Json(
                new { ok = false, error = "Invalid repository reference." },
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (!IsValidPath(path))
        {
            return Results.Json(
                new { ok = false, error = "Invalid path." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        string userAgent = "PAXCookbook/" + version.CookbookVersion + " (pantry-api)";
        // The path is validated above (no traversal / breakout characters) but
        // may still contain spaces, parentheses, and other characters that must
        // be percent-encoded for a well-formed request line; encode each segment
        // (preserving the "/" separators) so e.g. "4. Rollup (Manual)" reaches
        // GitHub as "4.%20Rollup%20%28Manual%29".
        Uri uri = new("https://api.github.com/repos/" + owner + "/" + repo + "/contents/" + EncodeContentsPath(path));
        FetchResult res = await HttpFetcher.FetchAsync(
            uri,
            userAgent: userAgent,
            acceptHeader: "application/vnd.github+json",
            maxBytes: ContentsMaxBytes,
            timeoutSec: TimeoutSec,
            genericErrorTag: "github_fetch_failed",
            oversizeErrorTag: "github_too_large",
            cancellationToken: ctx.RequestAborted).ConfigureAwait(false);

        if (!res.Ok || res.Bytes is null)
        {
            return Results.Json(new { ok = false, error = DescribeContentsError(res) });
        }

        List<PantryContentItem> items;
        try
        {
            items = ParseContents(res.Bytes);
        }
        catch (JsonException)
        {
            return Results.Json(new { ok = false, error = "GitHub returned an unexpected response." });
        }

        return Results.Json(new
        {
            ok = true,
            owner,
            repo,
            path,
            items,
        });
    }

    // Map a failed contents fetch to a short, user-facing message. A 404 here
    // means the path (not the repository) is not visible.
    private static string DescribeContentsError(FetchResult result)
    {
        if (result.StatusCode is null)
        {
            return "Unable to reach GitHub. Check your internet connection.";
        }
        return result.StatusCode switch
        {
            404 => "Path not found.",
            403 or 429 => "GitHub API rate limit reached. Try again in a few minutes.",
            _ => "GitHub returned an error (HTTP " + result.StatusCode + ").",
        };
    }

    // A repo-relative content path safe to interpolate into the fixed
    // .../contents/{path} URL. Empty means the repo root. Rather than an
    // allow-list (which would reject the spaces, parentheses, accents, CJK, and
    // other characters that legitimately appear in GitHub folder names), this is
    // a BLOCK-list: it rejects only the security-relevant characters and lets
    // GitHub answer 404 for a path that is merely non-existent. Blocked:
    //   * ".." anywhere (path traversal)
    //   * a leading "/" (absolute path)
    //   * "\" (Windows separator), "|" and "`" (shell-injection surface)
    //   * '"', "<", ">" (attribute / HTML breakout — same rationale as the branch
    //     sanitization; defense-in-depth, since the path is also echoed back as
    //     JSON the SPA renders as text)
    //   * any control character (< 0x20, including NUL) and DEL (0x7f)
    // Whatever survives is percent-encoded per segment before it reaches the
    // GitHub URL (see EncodeContentsPath), so a surviving special character can
    // never alter the host, scheme, query, or fragment of the fixed
    // api.github.com URL.
    internal static bool IsValidPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return true;
        }
        if (path.Length > 500 || path[0] == '/')
        {
            return false;
        }
        if (path.IndexOf("..", StringComparison.Ordinal) >= 0)
        {
            return false;
        }
        foreach (char c in path)
        {
            if (c == '\\' || c == '"' || c == '<' || c == '>' || c == '|' || c == '`')
            {
                return false;
            }
            if (c < ' ' || c == '\u007f')
            {
                return false;
            }
        }
        return true;
    }

    // Percent-encode each "/"-separated segment of a validated repo path so the
    // GitHub contents URL is well-formed (spaces -> %20, "(" -> %28, etc.) while
    // the "/" separators are preserved. Uri.EscapeDataString encodes every
    // character that is not an RFC 3986 unreserved character, so any character
    // the IsValidPath block-list let through is neutralized for the URL
    // structure (it cannot become a query, fragment, or authority delimiter).
    internal static string EncodeContentsPath(string path)
    {
        if (path.Length == 0)
        {
            return string.Empty;
        }
        string[] segments = path.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }
        return string.Join("/", segments);
    }

    internal sealed record PantryContentItem(
        string Name,
        string Path,
        string Type,
        long Size,
        string? DownloadUrl,
        string? Sha);

    internal static List<PantryContentItem> ParseContents(byte[] json)
    {
        List<PantryContentItem> items = new();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        // The Contents API returns an array for a directory. A file path returns
        // a single object; the tree only ever requests directories, so a
        // non-array response yields an empty listing.
        if (root.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (JsonElement entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string? name = GetString(entry, "name");
            string? itemPath = GetString(entry, "path");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(itemPath))
            {
                continue;
            }
            bool isDir = GetString(entry, "type") == "dir";
            // A directory has no download URL; a file's GitHub download_url is
            // kept only when it passes the trusted-GitHub-host gate (an unsafe
            // value becomes null, so the row renders but is not clickable).
            string? downloadUrl = null;
            if (!isDir)
            {
                string? raw = GetString(entry, "download_url");
                if (IsSafeDownloadUrl(raw))
                {
                    downloadUrl = raw;
                }
            }
            items.Add(new PantryContentItem(
                name,
                itemPath,
                isDir ? "dir" : "file",
                GetLong(entry, "size"),
                downloadUrl,
                GetString(entry, "sha")));
        }
        return items;
    }

    // ---- File download proxy (in-app preview + Save As) --------------------
    //
    // GET /api/v1/pantry/download?url={raw-github-url} fetches a single file
    // from GitHub and streams it straight back to the SPA so a file can be
    // previewed or saved without leaving the appliance window. Same read-only,
    // Bearer + lock-gated posture as the other Pantry routes, with one
    // deliberate difference: the body is NOT byte-capped -- a large dashboard /
    // template is streamed through without ever being buffered in memory,
    // bounded only by a 5-minute timeout. This can never become an open relay:
    // the url is validated server-side to a trusted GitHub https host
    // (IsSafeDownloadUrl) before any outbound request is made, the caller's
    // bearer token is never forwarded upstream, redirects are not followed, and
    // no cookies / credentials are sent. It never runs PAX, mutates appliance
    // state, or reads a secret.
    internal static async Task<IResult> HandleDownloadAsync(HttpContext ctx, VersionInfo version)
    {
        string? url = ctx.Request.Query["url"];
        if (string.IsNullOrEmpty(url))
        {
            return Results.Json(
                new { ok = false, error = "Missing download URL." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The query value is already URL-decoded by ASP.NET. Only an absolute
        // https URL whose host is github.com / githubusercontent.com (or a
        // subdomain) is allowed; anything else is rejected before any request.
        if (!IsSafeDownloadUrl(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? target) || target is null)
        {
            return Results.Json(
                new { ok = false, error = "Forbidden download URL." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        string userAgent = "PAXCookbook/" + version.CookbookVersion + " (pantry-download)";

        // A dedicated streaming client: no auto-redirect, no cookies, no
        // credentials, an explicit User-Agent, and a 5-minute ceiling for a
        // large file. The bearer token is never attached, so this is a plain
        // unauthenticated GET to GitHub's public file CDN.
        using HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
        };
        using HttpClient http = new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        using HttpRequestMessage req = new(HttpMethod.Get, target);
        req.Headers.UserAgent.ParseAdd(userAgent);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted)
                             .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Results.Json(
                new { ok = false, error = "Unable to download the file from GitHub." },
                statusCode: StatusCodes.Status502BadGateway);
        }

        using (resp)
        {
            int status = (int)resp.StatusCode;
            if (status == StatusCodes.Status404NotFound)
            {
                return Results.Json(
                    new { ok = false, error = "File not found." },
                    statusCode: StatusCodes.Status404NotFound);
            }
            if (!resp.IsSuccessStatusCode)
            {
                return Results.Json(
                    new { ok = false, error = "GitHub returned an error (HTTP " + status + ")." },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            // Pass the upstream content type through (the in-app preview relies
            // on it to choose a renderer) and the content length when GitHub
            // declares it; attach a sanitized attachment filename and an inert
            // permissive CORS header for the same-origin SPA fetch.
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            string? contentType = resp.Content.Headers.ContentType?.ToString();
            ctx.Response.ContentType = string.IsNullOrEmpty(contentType)
                ? "application/octet-stream"
                : contentType;
            long? contentLength = resp.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value >= 0)
            {
                ctx.Response.ContentLength = contentLength.Value;
            }
            ctx.Response.Headers["Content-Disposition"] =
                "attachment; filename=\"" + SanitizeDownloadFilename(target) + "\"";
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";

            try
            {
                using Stream upstream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted)
                                                          .ConfigureAwait(false);
                await upstream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The response has already begun streaming, so the status and
                // headers are committed and cannot be changed -- stop writing and
                // let the client observe a truncated body.
            }
            return Results.Empty;
        }
    }

    // Extract a safe download filename from a URL's last non-empty path segment:
    // URL-decode it, then drop the characters that are invalid in a Windows
    // filename (\ / : * ? " < > |), any control character, and any non-ASCII
    // character (so the Content-Disposition header value stays well-formed and
    // can never inject a header). Falls back to "download" when nothing usable
    // remains.
    private static string SanitizeDownloadFilename(Uri target)
    {
        string segment = string.Empty;
        string[] parts = target.AbsolutePath.Split('/');
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrEmpty(parts[i]))
            {
                segment = parts[i];
                break;
            }
        }
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(segment);
        }
        catch (Exception)
        {
            decoded = segment;
        }
        StringBuilder sb = new(decoded.Length);
        foreach (char c in decoded)
        {
            if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?'
                || c == '"' || c == '<' || c == '>' || c == '|')
            {
                continue;
            }
            if (c < ' ' || c > '\u007e')
            {
                continue;
            }
            sb.Append(c);
        }
        string clean = sb.ToString().Trim();
        return clean.Length == 0 ? "download" : clean;
    }

    // ---- Shared GitHub-contents helpers -----------------------------------
    //
    // Retained for the Pantry repo-contents route (the file explorer): a byte
    // cap for directory listings, a URL safety gate that keeps only trusted
    // GitHub https file-download links, and a long-int JSON reader for sizes.

    internal const int ContentsMaxBytes = 2 * 1024 * 1024;

    // A download URL safe to return to the SPA: an absolute https URL whose host
    // is github.com / githubusercontent.com (or a subdomain). The SPA renders it
    // only as an <a href> the shell hands to the OS browser; validating it here
    // guarantees a non-https / non-GitHub / javascript: / data: value can never
    // reach the rendered link.
    private static bool IsSafeDownloadUrl([NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u) || u is null)
        {
            return false;
        }
        if (!string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string host = u.Host.ToLowerInvariant();
        return host == "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal)
            || host == "githubusercontent.com" || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    private static long GetLong(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt64(out long v))
        {
            return v;
        }
        return 0;
    }
}
