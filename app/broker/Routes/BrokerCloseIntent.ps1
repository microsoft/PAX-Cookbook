# =====================================================================
# /api/v1/broker/close-intent route.
#
# Written to by close-app.js BEFORE calling window.close() or the
# /api/v1/broker/shutdown route. The app-window watchdog
# (launcher\Watch-PAXCookbookAppWindow.ps1) reads the resulting
# marker file <Workspace>\Runtime\app-close-intent.json to decide
# whether the disappearance of the Edge --app= window was operator-
# initiated (suppress prompt) or a native OS X / taskbar close
# (show prompt).
#
# Auth posture (all enforced by Invoke-RequestHandler BEFORE this
# function runs, see app\broker\Start-Broker.ps1):
#   - Origin loopback only (http://127.0.0.1:<port> or
#     http://localhost:<port>).
#   - Bearer token must match $Script:SessionToken.
#   - X-Cookbook-Request CSRF header (POST is a state-changing verb).
#   - This route is dispatched ABOVE the Locked-state gate so the
#     SPA can record intent even when the broker is Locked (the
#     Close App modal is reachable from the lock overlay via the
#     "Close app and stop server" tertiary button).
#
# Request body:
#   { "intent": "app-only-close" | "stop-server" }
#
# Marker file payload:
#   { "schemaVersion": 1,
#     "intent":        "<intent>",
#     "writtenUtc":    "<iso 8601>" }
#
# The marker is informational; the watcher treats markers older than
# ~10s as expired and falls back to the prompt path. Stop-PAXCookbook
# removes the marker file as part of Remove-RuntimeSidecars.
# =====================================================================

function Invoke-BrokerCloseIntent {
    param($Context)

    $req     = $Context.Request
    $allowed = @('app-only-close', 'stop-server')

    # Read body. Bounded to a small ceiling -- the payload is a tiny
    # JSON object, anything larger is malformed by definition.
    $body = $null
    try {
        $reader = New-Object System.IO.StreamReader($req.InputStream, [System.Text.Encoding]::UTF8)
        $raw    = $reader.ReadToEnd()
        $reader.Close()
        if ($raw.Length -gt 1024) {
            Write-JsonResponse -Context $Context -Status 413 -Body @{ error = 'payload_too_large' }
            return
        }
        if ($raw) { $body = $raw | ConvertFrom-Json -ErrorAction Stop }
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    $intent = $null
    if ($body -and $body.PSObject.Properties['intent']) {
        $intent = [string]$body.intent
    }
    if (-not $intent -or ($allowed -notcontains $intent)) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_intent'; allowed = $allowed }
        return
    }

    # Resolve the marker path relative to the broker's runtime
    # directory (the same Runtime\ folder that holds workspace.lock
    # and browser.window.json). Falling back to $Script:RuntimeDir if
    # defined; Start-Broker.ps1 sets this at startup.
    $runtimeDir = $null
    try { $runtimeDir = $Script:RuntimeDir } catch { $runtimeDir = $null }
    if (-not $runtimeDir) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'runtime_dir_unavailable' }
        return
    }
    if (-not (Test-Path -LiteralPath $runtimeDir -PathType Container)) {
        try { $null = New-Item -ItemType Directory -Path $runtimeDir -Force } catch { }
    }

    $markerPath = Join-Path $runtimeDir 'app-close-intent.json'
    $nowUtc  = [datetime]::UtcNow
    $createdIso = $nowUtc.ToString('o')
    $expiresIso = $nowUtc.AddSeconds(10).ToString('o')
    $payload = [ordered]@{
        schemaVersion = 1
        intent        = $intent
        createdUtc    = $createdIso
        expiresUtc    = $expiresIso
        # Kept for backwards-compatibility with the initial draft of
        # the watcher; new readers should prefer createdUtc + the
        # watcher's own cap.
        writtenUtc    = $createdIso
    }
    try {
        $json = $payload | ConvertTo-Json -Depth 3
        $tmp  = $markerPath + '.tmp'
        Set-Content -LiteralPath $tmp -Value $json -Encoding utf8 -NoNewline -Force
        Move-Item -LiteralPath $tmp -Destination $markerPath -Force
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'marker_write_failed'; detail = $_.Exception.Message }
        return
    }

    # Forensic logging: record every accepted intent post into the
    # appliance-wide install.log so the watcher's marker decisions
    # can be correlated with the SPA's POSTs after the fact.
    try {
        $installLog = Join-Path ([System.IO.Path]::GetDirectoryName($runtimeDir)) '..\..\install.log'
        $installLog = [System.IO.Path]::GetFullPath($installLog)
        # Walk up from <Workspace>\Runtime\ to <InstallRoot>\install.log:
        # <Workspace>\Runtime  -> parent = <Workspace>  -> parent = <LocalAppData>\PAXCookbook
        $localApp = [Environment]::GetFolderPath('LocalApplicationData')
        if ($localApp) {
            $installLog = Join-Path $localApp 'PAXCookbook\install.log'
        }
        $stamp = (Get-Date).ToUniversalTime().ToString('o')
        $line  = '[' + $stamp + '] [close-intent route pid=' + $PID + '] Accepted intent=' + $intent + ' createdUtc=' + $createdIso
        Add-Content -LiteralPath $installLog -Value $line -ErrorAction SilentlyContinue
    } catch { }

    Write-JsonResponse -Context $Context -Status 202 -Body @{ ok = $true; intent = $intent }
}

function Invoke-BrokerCloseIntentRoute {
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -ne '/api/v1/broker/close-intent') { return $false }

    if ($method -ne 'POST') {
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return $true
    }

    Invoke-BrokerCloseIntent -Context $Context
    return $true
}
