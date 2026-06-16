#requires -Version 7.4

# Routes/BrokerLock.ps1
#
# Phase AF -- HTTP surface for the broker lock state machine. Three
# endpoints, all under /api/v1/broker/:
#
#   GET    /api/v1/broker/lock-state   -- read-only state snapshot;
#                                          allowed even when Locked
#                                          (the SPA needs to know the
#                                          state to render the overlay).
#   POST   /api/v1/broker/unlock       -- transition Locked -> Unlocked
#                                          after a 'Verified' Windows
#                                          re-auth verdict. Allowed
#                                          even when Locked (this IS
#                                          how the operator unlocks).
#   POST   /api/v1/broker/lock         -- explicit relock. Allowed
#                                          when Unlocked; harmless
#                                          (idempotent) when already
#                                          Locked.
#
# Doctrine (verbatim, in force):
#   - The unlock is BROKER-scoped, not browser-scoped. Two browser
#     tabs on the same broker see the same lock state.
#   - Boot state is ALWAYS Locked. There is NO persisted "remember me"
#     flag; closing the SPA does not lock; only an explicit lock,
#     inactivity timeout, or broker restart locks.
#   - Unlocked state is NOT operation approval. Per-op gated
#     operations ALWAYS perform a fresh Windows verification
#     regardless of lock state. See Auth/BrokerLock.ps1's
#     Invoke-BrokerLockReAuthForOp.
#   - The lock-state endpoint does NOT bump activity -- otherwise
#     SPA polling would keep the broker unlocked indefinitely.
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - Write-JsonResponse, Read-RequestJson (helpers from broker)
#   - Get-BrokerLockState, Get-BrokerLockStateSnapshot,
#     Set-BrokerLockUnlocked, Set-BrokerLockLocked,
#     Test-WindowsReAuthResultIsVerified, Invoke-WindowsReAuth,
#     New-BrokerLockReAuthRequiredResponse (Auth/BrokerLock.ps1,
#     Auth/WindowsReAuth.ps1)

function Invoke-BrokerLockStateGet {
    param($Context)
    # The snapshot is computed fresh each call (it triggers the lazy
    # inactivity sweep via Get-BrokerLockState inside the snapshot
    # helper). The shape is intentionally stable: state +
    # lastActivityUtc + inactivityTimeoutMinutes +
    # inactivityRemainingSeconds. The SPA renders the locked overlay
    # off the 'state' field alone.
    $snap = Get-BrokerLockStateSnapshot
    Write-JsonResponse -Context $Context -Status 200 -Body $snap
}

function Invoke-BrokerUnlock {
    param($Context)
    # Optional body for telemetry / message override; absent is fine.
    $body = $null
    try { $body = Read-RequestJson -Context $Context } catch { $body = $null }

    # UX-1H3 -- audit marker. /api/v1/broker/unlock is the LEGACY
    # broker-owned terminal Windows Hello path. Under UX-1H/UX-1H2
    # the SPA is supposed to drive the browser-owned WebAuthn
    # ceremony (/api/v1/broker/webauthn/{unlock-challenge,unlock,
    # bootstrap-register-challenge,bootstrap-register-unlock})
    # instead; UX-1H3 forbids the SPA's primary unlock button from
    # silently routing here. This Write-Host line is the canonical
    # signal that this route was hit -- if an operator reports
    # "Windows Hello still appears on the terminal monitor", the
    # presence of this line in the broker terminal stdout
    # identifies that the legacy path was invoked (either by an
    # explicit operator click of the "Use legacy Windows Hello
    # fallback" button inside the SPA Support details disclosure
    # OR by an out-of-band caller such as curl). Cross-reference
    # the attemptId query parameter with the SPA console line
    # under '[PAX Cookbook Unlock]' to confirm whether the call
    # was explicit.
    $attemptId = $null
    try { $attemptId = [string]$Context.Request.QueryString['attempt'] } catch { $attemptId = $null }
    $tsLog = (Get-Date).ToUniversalTime().ToString('o')
    $msgLog = '[' + $tsLog + '] LEGACY BROKER-OWNED WINDOWS HELLO PATH INVOKED'
    if ($attemptId) { $msgLog = $msgLog + ' attempt=' + $attemptId }
    Write-Host $msgLog -ForegroundColor Yellow

    # Always prompt with the canonical message. Operators see WHY
    # they are being asked to verify (per the doctrine in
    # Auth/BrokerLock.ps1). We do NOT honor a client-supplied
    # message override here -- the SPA can supply context via the
    # operation that subsequently runs, but the unlock prompt
    # itself is broker-controlled.
    $message = 'Unlock PAX Cookbook'
    $verdict = Invoke-WindowsReAuth -Message $message

    if (Test-WindowsReAuthResultIsVerified -Result $verdict) {
        Set-BrokerLockUnlocked
        $snap = Get-BrokerLockStateSnapshot
        Write-JsonResponse -Context $Context -Status 200 -Body @{
            ok    = $true
            state = $snap.state
            lastActivityUtc            = $snap.lastActivityUtc
            inactivityTimeoutMinutes   = $snap.inactivityTimeoutMinutes
            inactivityRemainingSeconds = $snap.inactivityRemainingSeconds
            verificationResult         = 'Verified'
        }
        return
    }

    # Non-Verified verdicts surface verbatim so the SPA can choose
    # the right operator message. We deliberately do NOT mutate the
    # lock state on failure.
    #
    # CP5C: when the verdict is ComInteropFailure, the broker also
    # records a diagnostic tag identifying WHICH of the seven WinRT
    # interop paths failed (see WindowsReAuth.ps1
    # Get-WindowsReAuthLastFailureDetail). Without this, the operator
    # only sees "verification surface is unavailable" with no clue
    # which native call broke. The detail is appended to broker
    # RecentErrors so it appears on the /api/v1/health response and
    # in the operator console.
    if ($verdict -eq 'ComInteropFailure') {
        $detail = Get-WindowsReAuthLastFailureDetail
        if (-not [string]::IsNullOrWhiteSpace($detail)) {
            Add-RecentError ('Unlock: Windows Hello returned ComInteropFailure. detail=' + $detail) -Source 'windows_hello_interop'
        } else {
            Add-RecentError 'Unlock: Windows Hello returned ComInteropFailure with no diagnostic detail captured.' -Source 'windows_hello_interop'
        }
    }
    $resp = New-BrokerLockReAuthRequiredResponse `
        -OpClass 'unlock' `
        -VerificationResult $verdict
    Write-JsonResponse -Context $Context -Status $resp.status -Body $resp.body
}

function Invoke-BrokerLock {
    param($Context)
    # Explicit relock. Always succeeds (idempotent).
    Set-BrokerLockLocked
    $snap = Get-BrokerLockStateSnapshot
    Write-JsonResponse -Context $Context -Status 200 -Body @{
        ok    = $true
        state = $snap.state
        lastActivityUtc            = $snap.lastActivityUtc
        inactivityTimeoutMinutes   = $snap.inactivityTimeoutMinutes
        inactivityRemainingSeconds = $snap.inactivityRemainingSeconds
    }
}

function Invoke-BrokerLockRoute {
    # Returns $true if the request was consumed by this handler.
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -eq '/api/v1/broker/lock-state' -and $method -eq 'GET') {
        Invoke-BrokerLockStateGet -Context $Context
        return $true
    }
    if ($path -eq '/api/v1/broker/unlock' -and $method -eq 'POST') {
        Invoke-BrokerUnlock -Context $Context
        return $true
    }
    if ($path -eq '/api/v1/broker/lock' -and $method -eq 'POST') {
        Invoke-BrokerLock -Context $Context
        return $true
    }

    # Reject any other /api/v1/broker/<lock>* method/verb combination
    # with 405 (vs. falling through to 404) so the SPA gets a precise
    # error when it ships a bad client.
    if ($path -eq '/api/v1/broker/lock-state' -or
        $path -eq '/api/v1/broker/unlock' -or
        $path -eq '/api/v1/broker/lock') {
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return $true
    }

    # Phase UX-1H -- LOCK-BYPASS WebAuthn endpoints (status,
    # unlock-challenge, unlock). These MUST work when the broker is
    # Locked because they ARE the browser-owned unlock flow. The
    # WebAuthn module is loaded later in the dot-source order than
    # this file, so we guard the call with a function-existence
    # check; on a fresh boot, Get-Command lookups are cheap and the
    # answer never changes once the broker is up.
    if ($path.StartsWith('/api/v1/broker/webauthn/')) {
        if (Get-Command -Name 'Invoke-BrokerWebAuthnLockBypassRoute' -ErrorAction SilentlyContinue) {
            $consumed = Invoke-BrokerWebAuthnLockBypassRoute -Context $Context
            if ($consumed) { return $true }
        }
        # The remaining /webauthn/* routes (register-challenge,
        # register) are LOCK-GATED and flow through the post-lock
        # dispatcher in Start-Broker.ps1. Returning $false here
        # lets that path run.
    }

    return $false
}
