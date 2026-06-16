# =====================================================================
# /api/v1/broker/shutdown route.
#
# Cooperative shutdown endpoint invoked by the SPA's Close App ->
# Close app and stop server button. Returns 202 (accepted) and then
# signals the dispatch loop to stop, identical to the pattern used by
# Routes\Updates.ps1 -> Invoke-UpdatesApply.
#
# Auth posture (all enforced by Invoke-RequestHandler BEFORE this
# function runs, see app\broker\Start-Broker.ps1):
#   - Origin loopback only (http://127.0.0.1:<port> or
#     http://localhost:<port>).
#   - Bearer token must match $Script:SessionToken.
#   - X-Cookbook-Request CSRF header (POST is a state-changing verb).
#   - Broker must be Unlocked (route is dispatched AFTER the
#     lock-state middleware in Invoke-RequestHandler).
#
# Why cooperative rather than process-kill:
#   - The broker's own Invoke-Shutdown removes workspace.lock and
#     workspace.lock.acquire on the way out, so a cooperative
#     shutdown leaves the runtime sidecars clean for the next
#     launch. The Stop-PAXCookbook.ps1 helper's Force-kill path is
#     the recovery escape hatch for unhealthy brokers, not the
#     happy-path Close App shutdown.
#   - Closing the Cookbook Edge app-window is the SPA's
#     responsibility (window.close() in close-app.js) AFTER this
#     endpoint accepts -- the browser does not need a server
#     command to close its own window. Unrelated Edge windows are
#     therefore untouched by this endpoint.
# =====================================================================

function Invoke-BrokerShutdown {
    param($Context)

    $responseBody = @{
        ok          = $true
        status      = 'shutdown_initiated'
        reason      = 'operator_close_app'
        message     = 'PAX Cookbook server is shutting down.'
    }

    # Flush 202 BEFORE signalling shutdown. Write-JsonResponse is
    # synchronous: it writes the body and closes the response stream
    # before returning. The chef's browser gets the answer regardless
    # of how fast the listener stops below.
    Write-JsonResponse -Context $Context -Status 202 -Body $responseBody

    # Earliest-writer-wins (same discipline as the Ctrl+C and
    # update-apply paths): if some other branch already tagged
    # $Script:ShutdownReason, leave that tag in place rather than
    # overwriting it.
    if ($null -eq $Script:ShutdownReason) {
        $Script:ShutdownReason = 'operator_close_app'
    }
    $Script:ShuttingDown = $true
    try { $Script:Listener.Stop() } catch { }
}

function Invoke-BrokerShutdownRoute {
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -ne '/api/v1/broker/shutdown') { return $false }

    if ($method -ne 'POST') {
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return $true
    }

    Invoke-BrokerShutdown -Context $Context
    return $true
}
