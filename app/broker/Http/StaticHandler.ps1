#requires -Version 7.4

# Static file handler for the SPA shell.
#
# Serves files under $Script:WebRoot. Returns:
#   200  GET of a file with a recognized extension under WebRoot
#   403  any path that resolves outside WebRoot (traversal attempt)
#   404  unknown extension, missing file, or non-GET method
#
# No Bearer / no CSRF check: the SPA shell must load BEFORE the
# session token is captured into sessionStorage, so the static
# surface is unauthenticated by design. The only other unauthenticated
# surface is GET /api/v1/health. All other /api/v1/* routes still
# require Authorization: Bearer and (for state-changing verbs) the
# X-Cookbook-Request CSRF header.
#
# Token bootstrap: when serving index.html, the handler injects a tiny
# inline script that exposes $Script:SessionToken as
# window.__cookbookBootstrapToken. boot.js consumes that variable and
# persists it to sessionStorage. This server-side hand-off is the
# authoritative path because it does not depend on URL-fragment
# survival across the launcher -> Edge --app= -> Chromium boundary.
# The URL-fragment hand-off remains in place as a defense-in-depth
# fallback when the broker has not yet minted a token.

$Script:StaticMimeMap = @{
    '.html'        = 'text/html; charset=utf-8'
    '.css'         = 'text/css; charset=utf-8'
    '.js'          = 'application/javascript; charset=utf-8'
    '.json'        = 'application/json; charset=utf-8'
    '.svg'         = 'image/svg+xml'
    '.png'         = 'image/png'
    '.ico'         = 'image/x-icon'
    '.woff2'       = 'font/woff2'
    '.webmanifest' = 'application/manifest+json; charset=utf-8'
}

function Write-StaticErrorResponse {
    param($Context, [int]$Status, [string]$Message)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Message)
    $Context.Response.StatusCode      = $Status
    $Context.Response.ContentType     = 'text/plain; charset=utf-8'
    $Context.Response.ContentLength64 = $bytes.LongLength
    $Context.Response.Headers['Cache-Control'] = 'no-store'
    try {
        $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    } finally {
        try { $Context.Response.OutputStream.Close() } catch {}
        try { $Context.Response.Close() } catch {}
    }
}

function Invoke-StaticHandler {
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()

    if ($method -ne 'GET') {
        Write-StaticErrorResponse -Context $Context -Status 404 -Message 'not_found'
        return
    }

    # HttpListener URL-decodes the path before exposing AbsolutePath.
    $rel = $req.Url.AbsolutePath
    if ([string]::IsNullOrEmpty($rel) -or $rel -eq '/') {
        $rel = '/index.html'
    }
    $rel = $rel.TrimStart('/')
    # Translate forward slashes to native separators for path combination.
    $rel = $rel.Replace('/', [System.IO.Path]::DirectorySeparatorChar)

    # Canonicalize the web root once per request. GetFullPath normalizes
    # any leftover separators and resolves '.'/'..' segments.
    $rootCanonical = [System.IO.Path]::GetFullPath($Script:WebRoot)
    $rootBoundary  = $rootCanonical + [System.IO.Path]::DirectorySeparatorChar

    try {
        $candidate = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($rootCanonical, $rel))
    } catch {
        Write-StaticErrorResponse -Context $Context -Status 403 -Message 'forbidden'
        return
    }

    # The canonicalized path must be a strict descendant of the web root.
    # OrdinalIgnoreCase covers Windows case-insensitive filesystems.
    if (-not $candidate.StartsWith($rootBoundary, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-StaticErrorResponse -Context $Context -Status 403 -Message 'forbidden'
        return
    }

    $ext = [System.IO.Path]::GetExtension($candidate).ToLowerInvariant()
    if (-not $Script:StaticMimeMap.ContainsKey($ext)) {
        Write-StaticErrorResponse -Context $Context -Status 404 -Message 'not_found'
        return
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        Write-StaticErrorResponse -Context $Context -Status 404 -Message 'not_found'
        return
    }

    # Server-side token bootstrap for index.html. The token is written
    # into an inline <script> in <head> so boot.js can adopt it into
    # sessionStorage without depending on a URL fragment surviving
    # Chromium's --app= mode rewrites. The injected script is removed
    # by boot.js once consumed. We inject only when the broker has a
    # SessionToken in memory and the file being served is index.html.
    if ($ext -eq '.html' -and (Split-Path -Leaf $candidate) -ieq 'index.html' -and
        $null -ne $Script:SessionToken -and $Script:SessionToken.Length -gt 0) {

        $html = [System.IO.File]::ReadAllText($candidate, [System.Text.UTF8Encoding]::new($false))

        # Anchor: the boot.js script tag. The injection lands
        # immediately before this tag so boot.js sees the bootstrap
        # variable on its very first statement, before any other
        # asset script runs (all other scripts are deferred).
        $anchor   = '<script src="assets/boot.js'
        $injected = '<script id="cookbook-token-bootstrap">window.__cookbookBootstrapToken=' +
                    "'" + $Script:SessionToken + "'" + ';</script>' + "`n    "

        $anchorIdx = $html.IndexOf($anchor, [System.StringComparison]::Ordinal)
        if ($anchorIdx -ge 0) {
            $html = $html.Substring(0, $anchorIdx) + $injected + $html.Substring($anchorIdx)
        }

        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($html)
        $Context.Response.StatusCode      = 200
        $Context.Response.ContentType     = $Script:StaticMimeMap[$ext]
        $Context.Response.ContentLength64 = $bytes.LongLength
        $Context.Response.Headers['Cache-Control'] = 'no-store'
        try {
            $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        } finally {
            try { $Context.Response.OutputStream.Close() } catch {}
            try { $Context.Response.Close() } catch {}
        }
        return
    }

    $bytes = [System.IO.File]::ReadAllBytes($candidate)
    $Context.Response.StatusCode      = 200
    $Context.Response.ContentType     = $Script:StaticMimeMap[$ext]
    $Context.Response.ContentLength64 = $bytes.LongLength
    $Context.Response.Headers['Cache-Control'] = 'no-store'
    try {
        $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    } finally {
        try { $Context.Response.OutputStream.Close() } catch {}
        try { $Context.Response.Close() } catch {}
    }
}
