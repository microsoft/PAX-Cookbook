#requires -Version 7.4

# WebSocketHub.ps1
#
# Single WebSocket endpoint: /ws
#
#   Auth   : ?t=<sessionToken> on the upgrade URL (browsers cannot set
#            custom headers on WS upgrades, so the corpus mandates query
#            token). Constant-time compare.
#   Origin : Same Test-OriginAllowed gate as HTTP routes.
#   CSRF   : N/A (WS handshake is a same-origin browser GET).
#
# Frame model (M1, FROZEN — do NOT add new types):
#   client -> server:  { "subscribe":   "cook:<cookId>" }
#                      { "unsubscribe": "cook:<cookId>" }
#                      { "ping": 1 }
#   server -> client:  { "type": "stdout"|"stderr"|"started"|"finished"|"interrupted"|"ack"|"error",
#                        "cookId": "<cookId>", "data": "<string>" }
#
# No seq, no ring buffer, no catch-up, no metrics, no progress %, no
# throughput. The WS is a live operational tap; on-disk `cook.log` is
# authoritative for replay.
#
# Phase AG -- stream-state taxonomy (broker contributions vs. client
# responsibilities). The user-facing live tail must distinguish:
#
#   live              -- WS open, frames arriving recently.
#                        Broker contribution: stdout/stderr/started
#                        frames flowing. Client derives by observing
#                        time-since-last-frame.
#
#   stalled           -- WS open, cook still running, but the child
#                        has not emitted output for >= stallSeconds.
#                        Broker contribution: a one-shot 'stderr'
#                        frame with payload '[BROKER] stall detected:
#                        no stdout/stderr for Ns'. The supervisor
#                        also writes closure_evidence_json.stallDetected
#                        so post-mortem inspection sees the signal.
#
#   completed         -- WS open, terminal 'finished' frame received.
#                        Cook ended cleanly (exit_code=0).
#
#   interrupted       -- WS open, terminal 'interrupted' frame
#                        received. Cook was canceled, killed, or
#                        spawn-failed.
#
#   broker_restarted  -- The broker died while the cook was running.
#                        No frame is pushed (the socket is gone).
#                        The client detects this on reconnect: GET
#                        /cooks/<id> reports closure_reason ==
#                        broker_restart_* and the cook is no longer
#                        in 'running' state.
#
#   transport_lost    -- WS connection itself dropped (network glitch,
#                        sleep, etc.). Broker cannot signal across a
#                        closed transport; the client owns this state.
#                        On reconnect the client polls GET /cooks/<id>
#                        to learn current cook status.
#
#   replay_incomplete -- A bounded buffer overflowed; some frames
#                        were dropped. Broker contribution: the
#                        per-subscriber droppedFrames counter (see
#                        New-WsSubscriber). NOT echoed as a frame to
#                        the client in M1 (the frame model is frozen)
#                        -- the counter is forensic only and surfaces
#                        via verifier scripts and cook.log markers.
#                        The authoritative full record stays in
#                        cook.log on disk; the WS is a live tap.
#
# These names map onto the cook closure_reason vocabulary defined in
# Start-Broker.ps1's $Script:CookClosureReasons block. They are stream
# state, not cook state -- a cook can be 'completed' while the WS is
# 'transport_lost', for example.
#
# Concurrency model:
#   - One supervisor ThreadJob per cook owns the child process and
#     publishes events into Publish-CookEvent.
#   - One reader ThreadJob + one sender ThreadJob per WS connection.
#   - $Script:WsRegistry is a synchronized hashtable
#         cookId -> synchronized List of subscriber state objects
#     accessed from all of: dispatch thread (route handlers), supervisor
#     ThreadJobs (publishers), and WS reader ThreadJobs (subscribe /
#     unsubscribe).
#
# Token redaction:
#   The session token appears in the WS upgrade URL as ?t=<token>. It is
#   ABSOLUTELY FORBIDDEN to log the raw URL or any Exception.Message that
#   may contain it. Get-RedactedRequestUrl is the only sanctioned way to
#   render a WS request for logging. Add-RecentError calls in this file
#   MUST use it.

# ---------------------------------------------------------------------
# Redaction
# ---------------------------------------------------------------------

function Get-RedactedRequestUrl {
    # Returns the request URL with the WS session token ?t=... redacted.
    # Safe to log. Never call $Request.Url.AbsoluteUri directly in any
    # logger because the raw URL carries the session token.
    param($Request)
    try {
        $u     = $Request.Url
        $query = $u.Query   # "" or "?k=v&k2=v2"
        $newQ  = ''
        if ($query) {
            $parts = $query.TrimStart('?').Split('&')
            $rendered = @()
            foreach ($p in $parts) {
                $eq = $p.IndexOf('=')
                if ($eq -lt 0) {
                    $rendered += $p
                } else {
                    $k = $p.Substring(0, $eq)
                    if ($k -ieq 't') {
                        $rendered += ($k + '=<redacted>')
                    } else {
                        $rendered += $p
                    }
                }
            }
            $newQ = '?' + ($rendered -join '&')
        }
        return ($u.Scheme + '://' + $u.Authority + $u.AbsolutePath + $newQ)
    } catch {
        # If anything goes wrong building the redacted form, return a
        # conservative path-only string rather than risking a leak.
        return ('<unloggable-url path=' + $Request.Url.AbsolutePath + '>')
    }
}

# ---------------------------------------------------------------------
# Query-string token auth (WS-only)
# ---------------------------------------------------------------------

function Test-WsQueryToken {
    param([string]$Provided)
    if ([string]::IsNullOrEmpty($Provided)) { return $false }
    $expected = $Script:SessionToken
    if ($Provided.Length -ne $expected.Length) { return $false }
    $a = [System.Text.Encoding]::ASCII.GetBytes($Provided)
    $b = [System.Text.Encoding]::ASCII.GetBytes($expected)
    return [System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals($a, $b)
}

# ---------------------------------------------------------------------
# Subscriber state + registry plumbing
# ---------------------------------------------------------------------

function New-WsSubscriber {
    # The per-connection state object. Shared (by reference) between the
    # WS reader job, the WS sender job, and any supervisor jobs that
    # broadcast to it.
    #
    # Phase AG -- sendQueueCap + droppedFrames bound the per-subscriber
    # backlog. A slow / stuck / disconnected client cannot cause
    # unbounded broker memory growth. Drop policy is OLDEST-first so
    # the most recent (most diagnostically valuable) frames survive.
    # droppedFrames is a single-element int array used as a mutable
    # counter cell -- PowerShell hashtables don't support [ref] on
    # value-typed members directly, so we use array-element indirection.
    #
    # Phase AG.C7 -- lastReportedDrops is the bookkeeping value the WS
    # sender ThreadJob uses to surface NEW drops to the chef as a
    # bounded notice frame. Whenever droppedFrames[0] > lastReportedDrops[0]
    # the sender emits ONE 'stderr' frame ("[BROKER] live-tail dropped
    # N frame(s) since last notice; cook.log on disk is authoritative.")
    # and advances the bookmark. Frequency is naturally bounded by the
    # sender's 250 ms wake cycle -- worst case 4 notice frames/sec
    # regardless of how many publish-time drops occurred. The notice
    # is sent VIA THE SAME socket as the dropped frames so a working
    # subscriber sees the truth and a dead subscriber sees nothing
    # (its closed flag suppresses both real frames and notices).
    param([System.Net.WebSockets.WebSocket]$WebSocket)
    return [hashtable]::Synchronized(@{
        subscriberId       = [guid]::NewGuid().ToString()
        socket             = $WebSocket
        sendQueue          = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
        sendSignal         = [System.Threading.ManualResetEventSlim]::new($false)
        closed             = $false
        subscribedCookIds  = [System.Collections.Concurrent.ConcurrentDictionary[string,bool]]::new()
        sendQueueCap       = 50000
        droppedFrames      = [int[]]@(0)
        lastReportedDrops  = [int[]]@(0)
    })
}

function Get-OrCreateCookSubscriberList {
    # Atomically fetches the synchronized List for $CookId, creating it
    # on first subscribe. The List itself is synchronized so subsequent
    # Add/Remove is thread-safe without further locking on $Registry.
    param([hashtable]$Registry, [string]$CookId)
    [System.Threading.Monitor]::Enter($Registry.SyncRoot)
    try {
        if (-not $Registry.ContainsKey($CookId)) {
            $Registry[$CookId] = [System.Collections.ArrayList]::Synchronized(([System.Collections.ArrayList]::new()))
        }
        return $Registry[$CookId]
    } finally {
        [System.Threading.Monitor]::Exit($Registry.SyncRoot)
    }
}

function Add-WsSubscription {
    param([hashtable]$Registry, [string]$CookId, [hashtable]$Subscriber)
    $list = Get-OrCreateCookSubscriberList -Registry $Registry -CookId $CookId
    [void]$list.Add($Subscriber)
    [void]$Subscriber.subscribedCookIds.TryAdd($CookId, $true)
}

function Remove-WsSubscription {
    param([hashtable]$Registry, [string]$CookId, [hashtable]$Subscriber)
    [void]$Subscriber.subscribedCookIds.TryRemove($CookId, [ref]$null)
    if ($Registry.ContainsKey($CookId)) {
        $list = $Registry[$CookId]
        [void]$list.Remove($Subscriber)
    }
}

function Remove-WsSubscriberFromAll {
    # Called when a WS connection closes. Strips this subscriber from
    # every cook's subscriber list.
    param([hashtable]$Registry, [hashtable]$Subscriber)
    foreach ($cookId in @($Subscriber.subscribedCookIds.Keys)) {
        Remove-WsSubscription -Registry $Registry -CookId $cookId -Subscriber $Subscriber
    }
}

# ---------------------------------------------------------------------
# Publish entry point (called from supervisor ThreadJobs)
# ---------------------------------------------------------------------

function Publish-CookEvent {
    # Enqueues a single frame for every subscriber to $CookId. Safe to
    # call from any runspace.
    param(
        [hashtable]$Registry,
        [string]$CookId,
        [ValidateSet('stdout','stderr','started','finished','interrupted','ack','error')]
        [string]$Type,
        [string]$Data
    )
    if (-not $Registry.ContainsKey($CookId)) { return }
    $frame = @{
        type   = $Type
        cookId = $CookId
        data   = $Data
    } | ConvertTo-Json -Compress -Depth 4

    $list = $Registry[$CookId]
    # Snapshot under the synchronized list's own lock so enumeration is
    # safe even if subscribers are concurrently added/removed.
    $snapshot = @()
    [System.Threading.Monitor]::Enter($list.SyncRoot)
    try { $snapshot = $list.ToArray() } finally { [System.Threading.Monitor]::Exit($list.SyncRoot) }

    foreach ($sub in $snapshot) {
        if ($sub.closed) { continue }
        # Phase AG -- bounded enqueue, drop-OLDEST. The cap protects
        # the broker from unbounded growth when a subscriber's socket
        # is slow or stuck. cook.log on disk is the authoritative full
        # record; this queue is the live tap.
        $junk = $null
        while ($sub.sendQueue.Count -ge $sub.sendQueueCap -and $sub.sendQueue.TryDequeue([ref]$junk)) {
            $sub.droppedFrames[0]++
        }
        [void]$sub.sendQueue.Enqueue($frame)
        try { $sub.sendSignal.Set() } catch {}
    }
}

# ---------------------------------------------------------------------
# Reader ThreadJob scriptblock (per connection)
# ---------------------------------------------------------------------

$Script:WsReaderScript = {
    param($Subscriber, $Registry, $CookRegistry)

    $cancel = [System.Threading.CancellationTokenSource]::new()
    $ws     = $Subscriber.socket
    $buf    = [byte[]]::new(4096)
    $segment = [System.ArraySegment[byte]]::new($buf)

    try {
        while ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open -and -not $Subscriber.closed) {
            $task = $null
            try {
                $task = $ws.ReceiveAsync($segment, $cancel.Token)
                $result = $task.GetAwaiter().GetResult()
            } catch {
                break
            }

            if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                try {
                    $closeTask = $ws.CloseAsync(
                        [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                        'client-close', $cancel.Token)
                    [void]$closeTask.GetAwaiter().GetResult()
                } catch {}
                break
            }

            if ($result.MessageType -ne [System.Net.WebSockets.WebSocketMessageType]::Text) {
                # Binary frames are not part of the M1 protocol; ignore.
                continue
            }

            $text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $result.Count)
            $msg  = $null
            try { $msg = $text | ConvertFrom-Json -AsHashtable -Depth 4 } catch { $msg = $null }
            if ($null -eq $msg) { continue }

            if ($msg.ContainsKey('ping')) {
                # Tiny liveness probe; respond with ack/'pong' and move on.
                $pong = @{ type='ack'; cookId=''; data='pong' } | ConvertTo-Json -Compress -Depth 3
                [void]$Subscriber.sendQueue.Enqueue($pong)
                try { $Subscriber.sendSignal.Set() } catch {}
                continue
            }

            if ($msg.ContainsKey('subscribe')) {
                $topic = [string]$msg.subscribe
                if ($topic -match '^cook:([0-9a-fA-F\-]{36})$') {
                    $cid = $matches[1].ToLowerInvariant()
                    # If cook is no longer running (or never existed), reply
                    # with a single 'ack' marking terminal and DON'T add a
                    # subscription. The client should hit GET /api/v1/cooks
                    # /<id> for terminal state. No replay, no catch-up.
                    if ($CookRegistry.ContainsKey($cid)) {
                        # Use the per-registry helper from the importing
                        # session — it lives in this same script-scope
                        # only if dot-sourced before Start-ThreadJob.
                        # Instead we inline the add to avoid runspace
                        # function-visibility issues.
                        [System.Threading.Monitor]::Enter($Registry.SyncRoot)
                        try {
                            if (-not $Registry.ContainsKey($cid)) {
                                $Registry[$cid] = [System.Collections.ArrayList]::Synchronized(([System.Collections.ArrayList]::new()))
                            }
                            $list = $Registry[$cid]
                        } finally {
                            [System.Threading.Monitor]::Exit($Registry.SyncRoot)
                        }
                        [void]$list.Add($Subscriber)
                        [void]$Subscriber.subscribedCookIds.TryAdd($cid, $true)
                        $ack = @{ type='ack'; cookId=$cid; data='subscribed' } | ConvertTo-Json -Compress -Depth 3
                    } else {
                        $ack = @{ type='ack'; cookId=$cid; data='terminal' } | ConvertTo-Json -Compress -Depth 3
                    }
                    [void]$Subscriber.sendQueue.Enqueue($ack)
                    try { $Subscriber.sendSignal.Set() } catch {}
                } else {
                    $err = @{ type='error'; cookId=''; data='invalid_subscribe_topic' } | ConvertTo-Json -Compress -Depth 3
                    [void]$Subscriber.sendQueue.Enqueue($err)
                    try { $Subscriber.sendSignal.Set() } catch {}
                }
                continue
            }

            if ($msg.ContainsKey('unsubscribe')) {
                $topic = [string]$msg.unsubscribe
                if ($topic -match '^cook:([0-9a-fA-F\-]{36})$') {
                    $cid = $matches[1].ToLowerInvariant()
                    [void]$Subscriber.subscribedCookIds.TryRemove($cid, [ref]$null)
                    if ($Registry.ContainsKey($cid)) {
                        $list = $Registry[$cid]
                        [void]$list.Remove($Subscriber)
                    }
                    $ack = @{ type='ack'; cookId=$cid; data='unsubscribed' } | ConvertTo-Json -Compress -Depth 3
                    [void]$Subscriber.sendQueue.Enqueue($ack)
                    try { $Subscriber.sendSignal.Set() } catch {}
                }
                continue
            }
        }
    } finally {
        $Subscriber.closed = $true
        try { $Subscriber.sendSignal.Set() } catch {}
        # Strip from every cook's subscriber list.
        foreach ($cookId in @($Subscriber.subscribedCookIds.Keys)) {
            [void]$Subscriber.subscribedCookIds.TryRemove($cookId, [ref]$null)
            if ($Registry.ContainsKey($cookId)) {
                $list = $Registry[$cookId]
                [void]$list.Remove($Subscriber)
            }
        }
        try { $cancel.Dispose() } catch {}
    }
}

# ---------------------------------------------------------------------
# Sender ThreadJob scriptblock (per connection)
# ---------------------------------------------------------------------

$Script:WsSenderScript = {
    param($Subscriber)
    $ws = $Subscriber.socket
    try {
        while (-not $Subscriber.closed -and $ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            # Wait up to 250ms for either a queued message or the close
            # signal; bounded wait keeps shutdown responsive.
            [void]$Subscriber.sendSignal.Wait(250)
            $Subscriber.sendSignal.Reset()

            # Phase AG.C7 -- bounded-overflow truthfulness. If
            # Publish-CookEvent has dropped frames since our last
            # notice, surface the count to the chef as ONE 'stderr'
            # frame ("[BROKER] live-tail dropped N frame(s) ...")
            # before draining the queue. Bounded by the 250 ms wake
            # cycle (worst case ~4 notices/sec). cook.log on disk is
            # the authoritative full record; this is the live-tap's
            # honesty channel. Inlined inside the sender (not via
            # Publish-CookEvent) so the notice cannot itself be
            # dropped by its own backlog.
            $curDrops  = [int]$Subscriber.droppedFrames[0]
            $lastDrops = [int]$Subscriber.lastReportedDrops[0]
            if ($curDrops -gt $lastDrops -and -not $Subscriber.closed `
                    -and $ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
                $deltaDrops = $curDrops - $lastDrops
                $Subscriber.lastReportedDrops[0] = $curDrops
                $noticeStr = @{
                    type   = 'stderr'
                    cookId = ''
                    data   = '[BROKER] live-tail dropped ' + $deltaDrops + ' frame(s) since last notice; cook.log on disk is authoritative.'
                } | ConvertTo-Json -Compress -Depth 3
                try {
                    $nb = [System.Text.Encoding]::UTF8.GetBytes($noticeStr)
                    $nseg = [System.ArraySegment[byte]]::new($nb)
                    $ntask = $ws.SendAsync(
                        $nseg,
                        [System.Net.WebSockets.WebSocketMessageType]::Text,
                        $true,
                        [System.Threading.CancellationToken]::None)
                    [void]$ntask.GetAwaiter().GetResult()
                } catch {
                    $Subscriber.closed = $true
                }
            }

            $msg = $null
            while ($Subscriber.sendQueue.TryDequeue([ref]$msg)) {
                if ($Subscriber.closed) { break }
                if ($ws.State -ne [System.Net.WebSockets.WebSocketState]::Open) { break }
                try {
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
                    $segment = [System.ArraySegment[byte]]::new($bytes)
                    $sendTask = $ws.SendAsync(
                        $segment,
                        [System.Net.WebSockets.WebSocketMessageType]::Text,
                        $true,
                        [System.Threading.CancellationToken]::None)
                    [void]$sendTask.GetAwaiter().GetResult()
                } catch {
                    # Send failure ends the connection; the reader will
                    # observe and clean up.
                    $Subscriber.closed = $true
                    break
                }
            }
        }
    } finally {
        try {
            if ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
                $closeTask = $ws.CloseAsync(
                    [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                    'server-close', [System.Threading.CancellationToken]::None)
                [void]$closeTask.GetAwaiter().GetResult()
            }
        } catch {}
        try { $ws.Dispose() } catch {}
    }
}

# ---------------------------------------------------------------------
# Upgrade entry point (called from dispatch loop)
# ---------------------------------------------------------------------

function Invoke-WsUpgrade {
    # Performs the HTTP -> WS upgrade, gates on Origin + ?t= token, and
    # spins up reader + sender ThreadJobs for the connection. Returns
    # immediately; the connection lives for its own lifetime in the
    # ThreadPool.
    param($Context)
    $req = $Context.Request

    if (-not (Test-OriginAllowed -Request $req)) {
        try { $Context.Response.StatusCode = 403; $Context.Response.Close() } catch {}
        return
    }

    $t = $req.QueryString['t']
    if (-not (Test-WsQueryToken -Provided $t)) {
        try { $Context.Response.StatusCode = 401; $Context.Response.Close() } catch {}
        return
    }

    $wsCtx = $null
    try {
        # PowerShell converts $null to "" when binding to a String
        # parameter. AcceptWebSocketAsync rejects an empty subProtocol
        # with ArgumentException. [NullString]::Value forces a true
        # null reference, which signals "no sub-protocol negotiation".
        $wsCtx = $Context.AcceptWebSocketAsync([NullString]::Value).GetAwaiter().GetResult()
    } catch {
        # Never log Exception.Message verbatim (the WS URL with ?t= may
        # appear in some socket-layer exception strings). Type name and
        # structural metadata only.
        $ex = $_.Exception
        $chain = New-Object System.Collections.Generic.List[string]
        $cur = $ex
        while ($cur) {
            $chain.Add($cur.GetType().FullName + ' (HResult=0x' + ('{0:X8}' -f $cur.HResult) + ')')
            $cur = $cur.InnerException
        }
        $detail = 'WS upgrade failed: ' + ($chain -join ' -> ')
        Add-RecentError $detail
        try {
            [Console]::Error.WriteLine($detail)
            if ($ex.StackTrace) {
                [Console]::Error.WriteLine($ex.StackTrace)
            }
        } catch {}
        try { $Context.Response.StatusCode = 400; $Context.Response.Close() } catch {}
        return
    }

    $subscriber = New-WsSubscriber -WebSocket $wsCtx.WebSocket

    [void](Start-ThreadJob -ScriptBlock $Script:WsReaderScript `
        -ArgumentList $subscriber, $Script:WsRegistry, $Script:CookRegistry)
    [void](Start-ThreadJob -ScriptBlock $Script:WsSenderScript `
        -ArgumentList $subscriber)
}
