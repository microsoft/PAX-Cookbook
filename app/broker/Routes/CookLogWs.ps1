#requires -Version 7.4

# CookLogWs.ps1 — specialized cook-view live-tail WebSocket transport.
#
#   GET (Upgrade: websocket) /api/v1/cooks/<cookId>/log/ws?t=<token>
#
# This is INTENTIONALLY SEPARATE from the /ws hub
# (Ws/WebSocketHub.ps1). The two systems solve different problems:
#
#   /ws hub            : broker operational event transport.
#                        Single endpoint, pub/sub registry, typed JSON
#                        envelope frames (stdout/stderr/started/finished
#                        /interrupted/ack/error), multi-subscriber fan-
#                        out. FROZEN — do not modify.
#
#   This file          : cook-view live-tail. Per-cook URL, one socket
#                        per mount, one FileStream per socket, one poll
#                        loop per socket, opaque UTF-8 text frames
#                        carrying raw cook.log bytes. No subscribe
#                        message protocol, no JSON envelopes, no replay,
#                        no buffering, no orchestration, no pub/sub.
#
# Do NOT attempt to unify these two systems. Do NOT introduce shared
# transport abstractions. Duplication is acceptable; the responsibilities
# are different.
#
# Wire-format contract:
#   Server -> Client : opaque UTF-8 text frames, each carrying a chunk of
#                      raw cook.log bytes appended since the last frame.
#                      Bytes 0..<EOF-at-upgrade-time> are NOT replayed
#                      over the socket; the client hydrates that prefix
#                      via GET /api/v1/cooks/<id>/log.
#   Client -> Server : no protocol messages. The client never sends.
#
# Auth model:
#   Same ?t=<sessionToken> query auth used by /ws. Browsers cannot set
#   custom headers on WS upgrades, so Bearer is not available. The token
#   is constant-time compared via Test-WsQueryToken. Any code path that
#   logs the request URL MUST use Get-RedactedRequestUrl.
#
# Pre-upgrade gates (HTTP status only, no body — connection has not been
# upgraded yet so a JSON body would be useless to the browser):
#   400  not a WebSocket upgrade request
#   403  origin not allowed
#   401  ?t= missing or wrong
#   400  cookId malformed
#   404  cook not found
#   409  cook is in a terminal status (no live tail possible)
#   409  cook.log file does not exist
#   400  AcceptWebSocketAsync threw
#
# Post-upgrade lifecycle:
#   NormalClosure (1000)        cook transitioned to a terminal status
#   InternalServerError (1011)  IO failure or send failure during tail
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - Test-OriginAllowed                                   (Start-Broker.ps1)
#   - Add-RecentError                                      (Start-Broker.ps1)
#   - Get-SqliteConnectionString                           (Start-Broker.ps1)
#   - Test-WsQueryToken, Get-RedactedRequestUrl            (Ws/WebSocketHub.ps1)
#   - Get-CookRow, $Script:CookIdPattern                   (Routes/Cooks.ps1)


# ---------------------------------------------------------------------
# Per-socket tail loop (ThreadJob scriptblock)
# ---------------------------------------------------------------------
#
# Runs in a ThreadJob runspace. ThreadJob runspaces do NOT inherit dot-
# sourced functions from the parent, so every helper this loop needs is
# inlined below.

$Script:CookLogTailScript = {
    param(
        [System.Net.WebSockets.WebSocket]$Socket,
        [string]$CookId,
        [string]$LogPath,
        [string]$ConnectionString
    )

    $ErrorActionPreference = 'Continue'

    $fs          = $null
    $decoder     = [System.Text.UTF8Encoding]::new($false).GetDecoder()
    $readBuf     = [byte[]]::new(65536)
    $charBuf     = [char[]]::new(65536)
    $position    = [long]0
    $cycle       = 0
    $closeStatus = [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure
    $closeReason = 'cook_terminal'

    # Inline helper: read status from the cooks table using a NEW
    # connection per call. SqliteConnection is not thread-safe and the
    # main runspace's $Script:SqliteConn is not visible here.
    $isTerminal = {
        $conn = [Microsoft.Data.Sqlite.SqliteConnection]::new($ConnectionString)
        try {
            $conn.Open()
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = 'SELECT status FROM cooks WHERE cook_id = $id;'
            $p = $cmd.CreateParameter()
            $p.ParameterName = '$id'
            $p.Value         = $CookId
            [void]$cmd.Parameters.Add($p)
            $reader = $cmd.ExecuteReader()
            try {
                if (-not $reader.Read()) { return $true }   # row vanished -> treat as terminal
                $s = $reader.GetString(0)
                return ($s -ne 'running')
            } finally {
                $reader.Dispose()
                $cmd.Dispose()
            }
        } catch {
            # Transient DB read failure: do NOT terminate. Next cycle will
            # retry. We never want a momentary lock contention to kick a
            # browser off the live tail.
            return $false
        } finally {
            try { $conn.Close()   } catch {}
            try { $conn.Dispose() } catch {}
        }
    }

    # Inline helper: read and ship any bytes between $position and the
    # current file length. $flush controls whether the UTF-8 decoder
    # emits any pending state (last cycle of a terminal cook).
    $drain = {
        param([bool]$Flush)
        $currentLength = $fs.Length
        if ($currentLength -le $position) { return $true }
        $remaining = $currentLength - $position
        while ($remaining -gt 0) {
            $toRead = [int][Math]::Min($readBuf.Length, [long]$remaining)
            $n = $fs.Read($readBuf, 0, $toRead)
            if ($n -le 0) { break }
            $isLastChunk = $Flush -and (($remaining - $n) -le 0)
            $charCount = $decoder.GetChars(
                $readBuf, 0, $n,
                $charBuf, 0,
                $isLastChunk)
            if ($charCount -gt 0) {
                $text  = [string]::new($charBuf, 0, $charCount)
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
                $seg   = [System.ArraySegment[byte]]::new($bytes)
                $sendTask = $Socket.SendAsync(
                    $seg,
                    [System.Net.WebSockets.WebSocketMessageType]::Text,
                    $true,
                    [System.Threading.CancellationToken]::None)
                [void]$sendTask.GetAwaiter().GetResult()
            }
            $position  += [long]$n
            $remaining -= $n
        }
        return $true
    }

    try {
        $fs = [System.IO.File]::Open(
            $LogPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite)
        # Start at current EOF. The pre-EOF prefix is hydrated by the
        # browser via GET /api/v1/cooks/<id>/log; this socket only ships
        # bytes appended after upgrade.
        $position    = $fs.Length
        $fs.Position = $position

        while ($Socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            [void](& $drain $false)

            $cycle++
            if (($cycle % 4) -eq 0) {
                if (& $isTerminal) {
                    [void](& $drain $true)
                    break
                }
            }

            Start-Sleep -Milliseconds 250
        }
    } catch {
        $closeStatus = [System.Net.WebSockets.WebSocketCloseStatus]::InternalServerError
        $closeReason = 'io_error'
    } finally {
        try { if ($fs) { $fs.Dispose() } } catch {}
        try {
            if ($Socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
                $closeTask = $Socket.CloseAsync(
                    $closeStatus,
                    $closeReason,
                    [System.Threading.CancellationToken]::None)
                [void]$closeTask.GetAwaiter().GetResult()
            }
        } catch {}
        try { $Socket.Dispose() } catch {}
    }
}


# ---------------------------------------------------------------------
# Upgrade entry point (called from Invoke-RequestHandler)
# ---------------------------------------------------------------------

function Invoke-CookLogWs {
    # Pre-upgrade gates run synchronously on the dispatch thread. On
    # success, AcceptWebSocketAsync upgrades the connection and the per-
    # socket tail loop is handed off to a ThreadJob. The dispatch thread
    # returns immediately.
    param($Context, [string]$CookId)
    $req = $Context.Request

    if (-not $req.IsWebSocketRequest) {
        try { $Context.Response.StatusCode = 400; $Context.Response.Close() } catch {}
        return
    }
    if (-not (Test-OriginAllowed -Request $req)) {
        try { $Context.Response.StatusCode = 403; $Context.Response.Close() } catch {}
        return
    }
    $t = $req.QueryString['t']
    if (-not (Test-WsQueryToken -Provided $t)) {
        try { $Context.Response.StatusCode = 401; $Context.Response.Close() } catch {}
        return
    }
    if ($CookId -notmatch $Script:CookIdPattern) {
        try { $Context.Response.StatusCode = 400; $Context.Response.Close() } catch {}
        return
    }
    $row = Get-CookRow -CookId $CookId
    if (-not $row) {
        try { $Context.Response.StatusCode = 404; $Context.Response.Close() } catch {}
        return
    }
    if ($row.status -ne 'running') {
        try { $Context.Response.StatusCode = 409; $Context.Response.Close() } catch {}
        return
    }
    $logPath = Join-Path $row.cookFolder 'cook.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        try { $Context.Response.StatusCode = 409; $Context.Response.Close() } catch {}
        return
    }

    $wsCtx = $null
    try {
        # [NullString]::Value forces a true null reference for the sub-
        # protocol argument; PowerShell converts $null to "" when binding
        # to a String parameter, and AcceptWebSocketAsync rejects "".
        $wsCtx = $Context.AcceptWebSocketAsync([NullString]::Value).GetAwaiter().GetResult()
    } catch {
        # Never log the raw URL or Exception.Message verbatim; the session
        # token may appear in either. Structural metadata only, plus the
        # redacted URL form for context.
        $ex = $_.Exception
        $chain = New-Object System.Collections.Generic.List[string]
        $cur = $ex
        while ($cur) {
            $chain.Add($cur.GetType().FullName + ' (HResult=0x' + ('{0:X8}' -f $cur.HResult) + ')')
            $cur = $cur.InnerException
        }
        $redacted = Get-RedactedRequestUrl -Request $req
        Add-RecentError ('cook-log WS upgrade failed at ' + $redacted + ': ' + ($chain -join ' -> '))
        try { $Context.Response.StatusCode = 400; $Context.Response.Close() } catch {}
        return
    }

    [void](Start-ThreadJob -ScriptBlock $Script:CookLogTailScript -ArgumentList @(
        $wsCtx.WebSocket,
        $CookId,
        $logPath,
        (Get-SqliteConnectionString)
    ))
}
