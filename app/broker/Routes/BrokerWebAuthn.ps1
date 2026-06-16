#requires -Version 7.4

# Routes/BrokerWebAuthn.ps1
#
# Phase UX-1H -- HTTP surface for the browser-owned Windows Hello path.
# All endpoints are under /api/v1/broker/webauthn/. The browser drives
# Windows Hello via navigator.credentials.create / get, then forwards
# the resulting credential / assertion to the broker, which verifies
# (Auth/WebAuthnVerify.ps1) and transitions the broker lock state.
#
# Endpoint partition (LOCK-BYPASS vs LOCK-GATED):
#
#   GET    /api/v1/broker/webauthn/status              LOCK-BYPASS
#       Returns whether any WebAuthn credential is registered, plus
#       the credentialIds the browser needs to populate allowCredentials.
#       Allowed when Locked because the SPA needs this answer BEFORE
#       it can choose between the bootstrap path
#       (browser-owned create + atomic unlock) and the steady-state
#       path (browser-owned get).
#
#   POST   /api/v1/broker/webauthn/unlock-challenge    LOCK-BYPASS
#       Issues a fresh single-use challenge bound to purpose='unlock'.
#       The SPA passes the challenge straight to
#       navigator.credentials.get(). Allowed when Locked (this is how
#       the operator BEGINS an unlock; the broker lock-state cannot
#       transition without it).
#
#   POST   /api/v1/broker/webauthn/unlock              LOCK-BYPASS
#       Verifies an assertion produced by
#       navigator.credentials.get(). On success: consumes the
#       challenge, verifies the signature via ECDSA P-256, and calls
#       Set-BrokerLockUnlocked. Returns the same lock-state snapshot
#       shape as /api/v1/broker/unlock. Allowed when Locked (this IS
#       the unlock).
#
#   POST   /api/v1/broker/webauthn/bootstrap-register-challenge   LOCK-BYPASS
#       Issues a single-use challenge bound to purpose='unlock_register'.
#       Allowed when Locked. The SPA uses this for the very first
#       unlock on a fresh workspace (no credential registered yet).
#       The challenge purpose is distinct from both 'register' and
#       'unlock' so it cannot be cross-consumed.
#
#   POST   /api/v1/broker/webauthn/bootstrap-register-unlock      LOCK-BYPASS
#       Verifies a credential created by
#       navigator.credentials.create() (browser-owned Windows Hello),
#       persists it, AND transitions Locked -> Unlocked in the same
#       request. Allowed when Locked, but ONLY when:
#         - the challenge was issued with purpose='unlock_register',
#         - the clientDataJSON.type is 'webauthn.create',
#         - the clientDataJSON.origin is the broker's bound origin,
#         - the authenticatorData UP and UV flags are both set,
#         - the public-key SPKI parses as ES256,
#         - the credentialId is non-empty and not already registered.
#       The security gate is the browser-owned UV check the platform
#       authenticator performed before returning the credential
#       (proven by the authData flags), NOT a prior Locked-state
#       transition. See doctrine in Auth/WebAuthnVerify.ps1.
#
#   POST   /api/v1/broker/webauthn/register-challenge  LOCK-GATED
#       Issues a single-use challenge bound to purpose='register'.
#       Allowed ONLY when the broker is already Unlocked. Used by
#       operator-initiated credential management (add a second
#       browser, rotate a credential) AFTER the initial bootstrap.
#
#   POST   /api/v1/broker/webauthn/register            LOCK-GATED
#       Persists a credential created by
#       navigator.credentials.create(). Allowed ONLY when the broker
#       is already Unlocked. Used together with /register-challenge
#       for non-bootstrap credential management.
#
# Doctrine (verbatim, in force):
#   - Cookbook NEVER collects, hashes, compares, proxies, or stores
#     any Windows password, PIN, or biometric material. WebAuthn
#     surfaces ONLY a public key and a signature; the private key
#     stays inside Windows Hello / TPM.
#   - Browser-owned WebAuthn is the PRIMARY path from the FIRST
#     user-facing unlock onwards. The legacy broker-owned WinRT
#     path (Invoke-WindowsReAuth, called via /api/v1/broker/unlock)
#     is reserved as fallback for browsers without WebAuthn support
#     and as a recovery hatch when the browser-owned path fails for
#     a technical reason. Both paths converge in
#     Set-BrokerLockUnlocked.
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - Write-JsonResponse, Read-RequestJson (broker helpers)
#   - Get-BrokerLockState, Set-BrokerLockUnlocked,
#     Get-BrokerLockStateSnapshot (Auth/BrokerLock.ps1)
#   - New-WebAuthnChallenge, Confirm-WebAuthnChallenge,
#     Test-WebAuthnClientData, Test-WebAuthnAssertion,
#     Test-WebAuthnAttestationAuthData,
#     Add-WebAuthnCredential, Find-WebAuthnCredential,
#     Get-WebAuthnState, Get-WebAuthnRegistrationSummary,
#     ConvertFrom-Base64Url (Auth/WebAuthnVerify.ps1)

# ---------------------------------------------------------------------
# Lock-bypass handlers (allowed when Locked)
# ---------------------------------------------------------------------

function Write-WebAuthnAttemptLog {
    # UX-1H3 audit helper. Emits a single Write-Host line per
    # WebAuthn HTTP hit with the operator-correlated attemptId
    # (when the SPA appended one via ?attempt=...) so the broker
    # stdout transcript pairs 1:1 with the browser-side
    # '[PAX Cookbook Unlock]' console lines. The helper takes no
    # response payload and never raises; logging must not affect
    # the request lifecycle.
    param(
        [Parameter(Mandatory)][string]$Endpoint,
        $Context
    )
    try {
        $attemptId = $null
        try { $attemptId = [string]$Context.Request.QueryString['attempt'] } catch { $attemptId = $null }
        $ts = (Get-Date).ToUniversalTime().ToString('o')
        $msg = '[' + $ts + '] BROWSER-OWNED WEBAUTHN ' + $Endpoint
        if ($attemptId) { $msg = $msg + ' attempt=' + $attemptId }
        Write-Host $msg -ForegroundColor Cyan
    } catch {
        # Never let a logging failure propagate.
    }
}

function Get-WebAuthnAttemptIdFromContext {
    # Pulls the operator-correlated attemptId off the request so
    # every structured response and audit line on the broker side
    # pairs 1:1 with a '[PAX Cookbook Unlock]' line in the browser
    # console. Tolerates a missing query string and never raises.
    param($Context)
    try { $a = [string]$Context.Request.QueryString['attempt'] } catch { $a = $null }
    if ([string]::IsNullOrWhiteSpace($a)) { return $null }
    return $a
}

function Write-BootstrapRejection {
    # Single point through which every rejection from
    # /webauthn/bootstrap-register-unlock emits its structured JSON
    # response AND its audit log line. The JSON body shape is
    # contract-stable so the SPA can render brokerRejectStage /
    # brokerRejectCode / brokerRejectMessage in copied
    # diagnostics: { ok:false, code, message, stage, attemptId }.
    # The audit line is grep-stable so smokes and operator triage
    # can join browser logs to broker logs by attempt id.
    param(
        [Parameter(Mandatory)]$Context,
        [string]$AttemptId,
        [Parameter(Mandatory)][int]$Status,
        [Parameter(Mandatory)]
        [ValidateSet(
            'parse_request',
            'client_data',
            'challenge',
            'origin',
            'attestation_parse',
            'auth_data',
            'flags',
            'credential_id',
            'cose_key',
            'public_key',
            'alg',
            'credential_store',
            'lock_transition',
            'unknown'
        )]
        [string]$Stage,
        [Parameter(Mandatory)][string]$Code,
        [Parameter(Mandatory)][string]$Message
    )
    try {
        $ts = (Get-Date).ToUniversalTime().ToString('o')
        $line = '[' + $ts + '] BROWSER-OWNED WEBAUTHN bootstrap-register-unlock REJECTED'
        if ($AttemptId) { $line = $line + ' attempt=' + $AttemptId }
        $line = $line + ' stage=' + $Stage + ' code=' + $Code
        Write-Host $line -ForegroundColor Yellow
    } catch {
        # Logging must never break the response.
    }
    Write-JsonResponse -Context $Context -Status $Status -Body @{
        ok        = $false
        code      = $Code
        message   = $Message
        stage     = $Stage
        attemptId = $AttemptId
    }
}

function Write-BootstrapAcceptance {
    # Counterpart to Write-BootstrapRejection. Emitted from the
    # success path immediately before the 200 response so the
    # broker transcript contains an ACCEPTED line that pairs with
    # the browser-side 'browser_ceremony_success' diagnostic.
    param(
        [Parameter(Mandatory)]$Context,
        [string]$AttemptId
    )
    try {
        $ts = (Get-Date).ToUniversalTime().ToString('o')
        $line = '[' + $ts + '] BROWSER-OWNED WEBAUTHN bootstrap-register-unlock ACCEPTED'
        if ($AttemptId) { $line = $line + ' attempt=' + $AttemptId }
        Write-Host $line -ForegroundColor Green
    } catch {
        # Logging must never break the response.
    }
}

function Write-BootstrapException {
    # Last-resort exception responder for
    # /webauthn/bootstrap-register-unlock. Called from the top-
    # level catch in Invoke-BrokerWebAuthnBootstrapRegisterUnlock
    # when ANY unexpected exception escapes the per-gate handlers.
    # Emits a structured HTTP 500 JSON of shape:
    #   { ok:false, code:'internal_exception', stage:'unknown',
    #     message, attemptId, exceptionType, exceptionMessage,
    #     checkpoint }
    # and an audit line:
    #   BROWSER-OWNED WEBAUTHN bootstrap-register-unlock EXCEPTION
    #   attempt=<id> checkpoint=<ck> type=<t>
    # Never logs request body, credentialId, public key bytes,
    # challenge, or any caller-supplied raw material. Wrapped end-
    # to-end so a failure inside this helper still results in a
    # status-code-only 500 rather than a hung connection.
    param(
        $Context,
        $AttemptId,
        $Checkpoint,
        $Exception
    )
    $excType = ''
    $excMsg  = ''
    try {
        if ($null -ne $Exception) {
            if ($Exception.Exception) {
                if ($Exception.Exception.GetType) {
                    $excType = [string]$Exception.Exception.GetType().FullName
                }
                if ($Exception.Exception.Message) {
                    $excMsg = [string]$Exception.Exception.Message
                }
            } elseif ($Exception.GetType) {
                $excType = [string]$Exception.GetType().FullName
                if ($Exception.Message) { $excMsg = [string]$Exception.Message }
            }
        }
    } catch {
        $excType = 'unknown'
        $excMsg  = '<exception details unavailable>'
    }
    if ([string]::IsNullOrEmpty($excType)) { $excType = 'unknown' }
    if ([string]::IsNullOrEmpty($excMsg))  { $excMsg  = '<no message>' }
    $checkpointStr = ''
    if ($null -ne $Checkpoint) { $checkpointStr = [string]$Checkpoint }
    if ([string]::IsNullOrEmpty($checkpointStr)) { $checkpointStr = 'unknown' }
    $attemptStr = ''
    if ($AttemptId) { $attemptStr = [string]$AttemptId }
    try {
        $ts = (Get-Date).ToUniversalTime().ToString('o')
        $line = '[' + $ts + '] BROWSER-OWNED WEBAUTHN bootstrap-register-unlock EXCEPTION'
        if ($attemptStr) { $line = $line + ' attempt=' + $attemptStr }
        $line = $line + ' checkpoint=' + $checkpointStr + ' type=' + $excType
        Write-Host $line -ForegroundColor Red
    } catch {
        # Logging must never break the response.
    }
    try {
        Add-RecentError -Message ('WebAuthn bootstrap exception at checkpoint=' + $checkpointStr + ' type=' + $excType + ' message=' + $excMsg) -Source 'webauthn_bootstrap'
    } catch {
        # Bookkeeping must never break the response.
    }
    try {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            ok               = $false
            code             = 'internal_exception'
            stage            = 'unknown'
            message          = 'Cookbook hit an internal error while verifying the Windows Hello passkey.'
            attemptId        = $attemptStr
            exceptionType    = $excType
            exceptionMessage = $excMsg
            checkpoint       = $checkpointStr
        }
    } catch {
        # Even JSON write failed -- last resort, try to close the
        # response with a minimal 500 so the client at least gets
        # a status code instead of a hung connection.
        try {
            $Context.Response.StatusCode = 500
            $Context.Response.Close()
        } catch {
            # Truly nothing more we can do here.
        }
    }
}

function Invoke-BrokerWebAuthnStatus {
    param($Context)
    Write-WebAuthnAttemptLog -Endpoint 'status' -Context $Context
    # Reads the credentials store and surfaces just enough for the
    # SPA to pick a flow. We never surface the public key (the
    # browser does not need it; the broker is the verifier). The
    # 'origin' field tells the SPA which origin string the browser
    # MUST send in clientDataJSON.origin; if the SPA's
    # window.location.origin does not match, the SPA must NOT
    # attempt WebAuthn (instead falls through to legacy unlock).
    $summary = Get-WebAuthnRegistrationSummary
    $port = [int]$Script:BrokerPort
    Write-JsonResponse -Context $Context -Status 200 -Body @{
        registered      = $summary.registered
        credentialIds   = $summary.credentialIds
        acceptedOrigins = @(
            ('http://127.0.0.1:' + $port),
            ('http://localhost:' + $port)
        )
        rpId            = 'auto'   # SPA must omit rp.id (let the
                                   # browser default to the effective
                                   # origin); the broker accepts either
                                   # 127.0.0.1 or localhost.
        supportedAlgs   = @(-7)    # ES256 only.
        userVerification = 'required'
    }
}

function Invoke-BrokerWebAuthnUnlockChallenge {
    param($Context)
    Write-WebAuthnAttemptLog -Endpoint 'unlock-challenge' -Context $Context
    # Issues an unlock challenge. The SPA places this in
    # PublicKeyCredentialRequestOptions.challenge after base64url
    # decoding, and Windows Hello signs over it.
    $ch = New-WebAuthnChallenge -Purpose 'unlock'
    Write-JsonResponse -Context $Context -Status 200 -Body @{
        challenge        = $ch.challenge
        timeoutMs        = 60000
        userVerification = 'required'
    }
}

function Invoke-BrokerWebAuthnUnlock {
    param($Context)
    Write-WebAuthnAttemptLog -Endpoint 'unlock' -Context $Context
    # Verifies a WebAuthn assertion and (on success) transitions
    # the broker lock state. Body shape:
    #   {
    #     credentialId       : '<base64url>',
    #     clientDataJSON     : '<base64url>',
    #     authenticatorData  : '<base64url>',
    #     signature          : '<base64url>',
    #     challenge          : '<base64url>'
    #   }
    # The challenge is the EXACT base64url the broker issued via
    # /webauthn/unlock-challenge; we use it to look up the pending
    # entry, NOT to trust the client (the clientDataJSON.challenge
    # field is re-verified inside Test-WebAuthnAssertion).
    try {
        $body = Read-RequestJson -Context $Context
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }
    if ($null -eq $body) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    $missing = @()
    foreach ($f in 'credentialId','clientDataJSON','authenticatorData','signature','challenge') {
        if ([string]::IsNullOrWhiteSpace([string]$body.$f)) { $missing += $f }
    }
    if ($missing.Count -gt 0) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'missing_fields'
            fields = $missing
        }
        return
    }

    # Consume the challenge (single-use). If it was never issued,
    # already used, expired, or was issued for the wrong purpose,
    # fail closed.
    if (-not (Confirm-WebAuthnChallenge -ChallengeBase64Url ([string]$body.challenge) -ExpectedPurpose 'unlock')) {
        Add-RecentError 'WebAuthn unlock: challenge unknown, expired, or already consumed.' -Source 'webauthn_unlock'
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'challenge_invalid'
            reason = 'challenge_unknown_or_expired'
        }
        return
    }

    $verdict = Test-WebAuthnAssertion `
        -CredentialIdBase64Url        ([string]$body.credentialId) `
        -ClientDataBase64Url          ([string]$body.clientDataJSON) `
        -AuthenticatorDataBase64Url   ([string]$body.authenticatorData) `
        -SignatureBase64Url           ([string]$body.signature) `
        -ExpectedChallenge            ([string]$body.challenge)

    if (-not $verdict.ok) {
        Add-RecentError ('WebAuthn unlock: verification failed; reason=' + $verdict.reason) -Source 'webauthn_unlock'
        Write-JsonResponse -Context $Context -Status 401 -Body @{
            error  = 'webauthn_verification_failed'
            reason = $verdict.reason
        }
        return
    }

    Set-BrokerLockUnlocked
    $snap = Get-BrokerLockStateSnapshot
    Write-JsonResponse -Context $Context -Status 200 -Body @{
        ok    = $true
        state = $snap.state
        lastActivityUtc            = $snap.lastActivityUtc
        inactivityTimeoutMinutes   = $snap.inactivityTimeoutMinutes
        inactivityRemainingSeconds = $snap.inactivityRemainingSeconds
        verificationResult         = 'Verified'
        verificationPath           = 'webauthn'
    }
}

function Invoke-BrokerWebAuthnBootstrapRegisterChallenge {
    param($Context)
    Write-WebAuthnAttemptLog -Endpoint 'bootstrap-register-challenge' -Context $Context
    # LOCK-BYPASS. Issues a bootstrap challenge whose purpose is
    # specifically 'unlock_register'. The challenge purpose tag is
    # what isolates this flow from the LOCK-GATED 'register' flow:
    # a challenge issued here can ONLY be consumed at
    # /webauthn/bootstrap-register-unlock, and a challenge issued by
    # the LOCK-GATED /webauthn/register-challenge will be rejected
    # if a malicious caller tries to redeem it here. The endpoint
    # is intended for the very first unlock on a fresh workspace,
    # before any WebAuthn credential exists.
    $ch = New-WebAuthnChallenge -Purpose 'unlock_register'
    $uid = New-WebAuthnUserId
    $port = [int]$Script:BrokerPort
    Write-JsonResponse -Context $Context -Status 200 -Body @{
        challenge        = $ch.challenge
        timeoutMs        = 60000
        userVerification = 'required'
        rpName           = 'PAX Cookbook'
        userIdBase64Url  = $uid.userId
        userName         = ('cookbook-' + [Environment]::UserName)
        userDisplayName  = [Environment]::UserName
        pubKeyCredParams = @(@{ type = 'public-key'; alg = -7 })
        acceptedOrigins  = @(
            ('http://127.0.0.1:' + $port),
            ('http://localhost:' + $port)
        )
    }
}

function Invoke-BrokerWebAuthnBootstrapRegisterUnlock {
    param($Context)
    # LOCK-BYPASS. Verifies a credential created by
    # navigator.credentials.create() (browser-owned Windows Hello),
    # persists it, AND transitions Locked -> Unlocked atomically.
    #
    # Body shape:
    #   {
    #     credentialId      : '<base64url>',
    #     publicKeySpki     : '<base64>',        (SPKI DER, standard b64)
    #     alg               : -7,                 (ES256 only)
    #     clientDataJSON    : '<base64url>',
    #     authenticatorData : '<base64url>',     (from
    #                                             attestationResponse.getAuthenticatorData())
    #     challenge         : '<base64url>',
    #     label             : '<optional friendly name>'
    #   }
    #
    # Gates, in order:
    #   1. challenge consumed with purpose='unlock_register' (single-use, TTL'd).
    #   2. clientDataJSON: type='webauthn.create', challenge matches,
    #      origin is 127.0.0.1 or localhost on the bound port.
    #   3. authenticatorData: UP (0x01) and UV (0x04) flags set.
    #   4. SPKI imports as a valid P-256 public key.
    #   5. alg == -7 (ES256).
    #   6. credentialId is non-empty and NOT already registered
    #      (a duplicate here means a confused client; we fail
    #      closed rather than silently overwrite during bootstrap).
    # All six gates must pass before Add-WebAuthnCredential persists
    # and Set-BrokerLockUnlocked transitions. Any failure leaves the
    # broker Locked with no credential added.
    #
    # Every structured rejection path emits a response of shape
    # { ok:false, code, message, stage, attemptId } via
    # Write-BootstrapRejection, and a paired audit line
    # 'BROWSER-OWNED WEBAUTHN bootstrap-register-unlock REJECTED
    # attempt=<id> stage=<stage> code=<code>'. The 14 stage values
    # are listed in Write-BootstrapRejection's ValidateSet.
    #
    # Top-level exception boundary: EVERY code path below the
    # outer try is wrapped so an unhandled exception NEVER reaches
    # the dispatcher (which would respond HTTP 500 with no body
    # and no headers -- the exact opaque-500 symptom UX-1H8 fixes).
    # The catch invokes Write-BootstrapException, which always
    # emits a structured HTTP 500 JSON of shape:
    #   { ok:false, code:'internal_exception', stage:'unknown',
    #     message, attemptId, exceptionType, exceptionMessage,
    #     checkpoint }
    # $checkpoint is updated before each gate so the catch can
    # surface which gate was executing when the exception fired.
    $attemptId = $null
    $checkpoint = 'enter'
    try {
        $checkpoint = 'attempt_log'
        Write-WebAuthnAttemptLog -Endpoint 'bootstrap-register-unlock' -Context $Context

        $checkpoint = 'attempt_id'
        $attemptId = Get-WebAuthnAttemptIdFromContext -Context $Context

        $checkpoint = 'parse_request'
        try {
            $body = Read-RequestJson -Context $Context
        } catch {
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 400 -Stage 'parse_request' -Code 'invalid_json' `
                -Message 'Request body could not be parsed as JSON.'
            return
        }
        if ($null -eq $body) {
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 400 -Stage 'parse_request' -Code 'invalid_json' `
                -Message 'Request body was empty or null after JSON parse.'
            return
        }

        $missing = @()
        foreach ($f in 'credentialId','publicKeySpki','clientDataJSON','authenticatorData','challenge') {
            if ([string]::IsNullOrWhiteSpace([string]$body.$f)) { $missing += $f }
        }
        if ($missing.Count -gt 0) {
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 400 -Stage 'parse_request' -Code 'missing_fields' `
                -Message ('Required field(s) missing or empty: ' + ($missing -join ', '))
            return
        }

        $checkpoint = 'alg'
        $alg = -7
        if ($null -ne $body.alg) {
            try { $alg = [int]$body.alg } catch { $alg = -7 }
        }
        if ($alg -ne -7) {
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 400 -Stage 'alg' -Code 'unsupported_alg' `
                -Message ('Only ES256 (alg=-7) is supported; received alg=' + $alg + '.')
            return
        }

        # Gate 1: consume the challenge under purpose 'unlock_register'.
        # A challenge issued for 'register' or 'unlock' is rejected
        # here and dropped from the pending table (handled inside
        # Confirm-WebAuthnChallenge) so it cannot be reused later for
        # its original purpose.
        $checkpoint = 'challenge'
        if (-not (Confirm-WebAuthnChallenge -ChallengeBase64Url ([string]$body.challenge) -ExpectedPurpose 'unlock_register')) {
            Add-RecentError 'WebAuthn bootstrap: challenge unknown, expired, already consumed, or wrong purpose.' -Source 'webauthn_bootstrap'
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 400 -Stage 'challenge' -Code 'challenge_unknown_or_expired' `
                -Message 'Challenge was not issued by this broker, has expired, was already consumed, or was issued for a different purpose.'
            return
        }

        # Gate 2: clientDataJSON parses + invariants hold.
        $checkpoint = 'client_data'
        try {
            $cdBytes = [byte[]](ConvertFrom-Base64Url -Encoded ([string]$body.clientDataJSON))
        } catch {
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 400 -Stage 'client_data' -Code 'client_data_b64_decode_failed' `
                -Message 'clientDataJSON could not be base64url-decoded.'
            return
        }
        $cd = Test-WebAuthnClientData `
                -ClientDataBytes   $cdBytes `
                -ExpectedType      'webauthn.create' `
                -ExpectedChallenge ([string]$body.challenge)
        if (-not $cd.ok) {
            Add-RecentError ('WebAuthn bootstrap: clientDataJSON rejected; reason=' + $cd.reason) -Source 'webauthn_bootstrap'
            $cdStage = 'client_data'
            if ($cd.reason -eq 'origin_unacceptable') { $cdStage = 'origin' }
            $cdMsg = switch ($cd.reason) {
                'invalid_json'        { 'clientDataJSON did not parse as JSON.' }
                'type_mismatch'       { 'clientDataJSON.type was not "webauthn.create".' }
                'challenge_mismatch'  { 'clientDataJSON.challenge did not match the challenge consumed on this attempt.' }
                'origin_unacceptable' { 'clientDataJSON.origin is not in the broker accepted-origins list (http://127.0.0.1:<port> or http://localhost:<port>).' }
                default               { 'clientDataJSON was rejected.' }
            }
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 400 -Stage $cdStage -Code ('client_data_' + $cd.reason) -Message $cdMsg
            return
        }

        # Gate 3: authenticatorData UP+UV flags. This is the
        # cryptographic proof that the platform authenticator (Windows
        # Hello) actually completed a user-verified ceremony before
        # producing the credential. Without these flags set, the
        # browser is claiming a credential exists without proving
        # Hello was satisfied -- we MUST reject.
        $checkpoint = 'auth_data'
        try {
            $authData = [byte[]](ConvertFrom-Base64Url -Encoded ([string]$body.authenticatorData))
        } catch {
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 400 -Stage 'auth_data' -Code 'auth_data_b64_decode_failed' `
                -Message 'authenticatorData could not be base64url-decoded.'
            return
        }
        $authVerdict = Test-WebAuthnAttestationAuthData -AuthenticatorData $authData
        if (-not $authVerdict.ok) {
            Add-RecentError ('WebAuthn bootstrap: authenticatorData rejected; reason=' + $authVerdict.reason) -Source 'webauthn_bootstrap'
            $adStage = 'auth_data'
            if ($authVerdict.reason -eq 'up_flag_unset' -or $authVerdict.reason -eq 'uv_flag_unset') {
                $adStage = 'flags'
            }
            $adMsg = switch ($authVerdict.reason) {
                'authenticator_data_too_short' { 'authenticatorData is shorter than the WebAuthn minimum of 37 bytes.' }
                'up_flag_unset'                { 'authenticatorData UP flag (0x01) is not set; the platform authenticator did not report a user-present ceremony.' }
                'uv_flag_unset'                { 'authenticatorData UV flag (0x04) is not set; Windows Hello did not assert user verification.' }
                default                        { 'authenticatorData was rejected.' }
            }
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 400 -Stage $adStage -Code $authVerdict.reason -Message $adMsg
            return
        }

        # Gate 4: SPKI parses as a valid P-256 public key.
        $checkpoint = 'public_key'
        try {
            $spkiBytes = [byte[]][Convert]::FromBase64String([string]$body.publicKeySpki)
        } catch {
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 400 -Stage 'public_key' -Code 'spki_b64_decode_failed' `
                -Message 'publicKeySpki could not be base64-decoded.'
            return
        }
        $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
        try {
            try {
                $bytesRead = 0
                $ecdsa.ImportSubjectPublicKeyInfo($spkiBytes, [ref]$bytesRead)
            } catch {
                Add-RecentError ('WebAuthn bootstrap: public key import failed; ' + $_.Exception.Message) -Source 'webauthn_bootstrap'
                Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                    -Status 400 -Stage 'public_key' -Code 'spki_import_failed' `
                    -Message ('SubjectPublicKeyInfo import failed: ' + [string]$_.Exception.Message)
                return
            }
        } finally {
            $ecdsa.Dispose()
        }

        # Gate 6: credentialId must not already exist. A duplicate at
        # bootstrap means either a misbehaving client or a replay of an
        # already-bootstrapped credential; either way we should not
        # silently overwrite during a Locked-state transition.
        $checkpoint = 'credential_id'
        $credIdStr = [string]$body.credentialId
        $state     = Get-WebAuthnState
        $dup       = Find-WebAuthnCredential -State $state -CredentialIdBase64Url $credIdStr
        if ($null -ne $dup) {
            Add-RecentError 'WebAuthn bootstrap: credentialId already registered.' -Source 'webauthn_bootstrap'
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 409 -Stage 'credential_id' -Code 'duplicate_credential_id' `
                -Message 'A credential with this credentialId is already registered on this appliance.'
            return
        }

        # 'label' is an optional field. The SPA does not currently
        # include it in the bootstrap-register-unlock POST body, and
        # older credential records do not carry it either. Reading
        # the field MUST go through Get-WebAuthnCredentialLabel
        # because the broker runs under Set-StrictMode -Version
        # Latest, and a naked '$body.label' access on a hashtable
        # body that lacks the key throws PropertyNotFoundException
        # ("The property 'label' cannot be found on this object.").
        # That is the exact exception UX-1H8's structured boundary
        # surfaced and the bug UX-1H9 closes.
        $label = Get-WebAuthnCredentialLabel -Source $body

        $checkpoint = 'credential_store'
        try {
            Add-WebAuthnCredential `
                -CredentialIdBase64Url $credIdStr `
                -PublicKeySpkiBase64   ([string]$body.publicKeySpki) `
                -Alg                   $alg `
                -Label                 $label
        } catch {
            Add-RecentError ('WebAuthn bootstrap: persistence failed; ' + $_.Exception.Message) -Source 'webauthn_bootstrap'
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 500 -Stage 'credential_store' -Code 'persist_failed' `
                -Message ('Credential could not be persisted: ' + [string]$_.Exception.Message)
            return
        }

        # All gates passed and the credential is persisted. Transition
        # the broker out of the Locked state. The browser-owned UV
        # ceremony that the platform authenticator just completed IS
        # the Hello verdict; we do not need a second prompt.
        $checkpoint = 'lock_transition'
        try {
            Set-BrokerLockUnlocked
        } catch {
            Add-RecentError ('WebAuthn bootstrap: lock transition failed; ' + $_.Exception.Message) -Source 'webauthn_bootstrap'
            Write-BootstrapRejection -Context $Context -AttemptId $attemptId `
                -Status 500 -Stage 'lock_transition' -Code 'lock_transition_failed' `
                -Message ('Credential was persisted, but the broker could not transition to Unlocked: ' + [string]$_.Exception.Message)
            return
        }
        $checkpoint = 'success_response'
        $snap = Get-BrokerLockStateSnapshot
        Write-BootstrapAcceptance -Context $Context -AttemptId $attemptId
        Write-JsonResponse -Context $Context -Status 200 -Body @{
            ok    = $true
            state = $snap.state
            lastActivityUtc            = $snap.lastActivityUtc
            inactivityTimeoutMinutes   = $snap.inactivityTimeoutMinutes
            inactivityRemainingSeconds = $snap.inactivityRemainingSeconds
            verificationResult         = 'Verified'
            verificationPath           = 'webauthn_bootstrap'
            registered                 = $true
            credentialId               = $credIdStr
            attemptId                  = $attemptId
        }
    } catch {
        Write-BootstrapException -Context $Context -AttemptId $attemptId -Checkpoint $checkpoint -Exception $_
    }
}

# ---------------------------------------------------------------------
# Lock-gated handlers (only allowed when Unlocked)
# ---------------------------------------------------------------------

function Invoke-BrokerWebAuthnRegisterChallenge {
    param($Context)
    # Issues a registration challenge. Caller must have unlocked
    # the broker via the legacy /api/v1/broker/unlock path first --
    # the dispatch loop's lock middleware enforces this, NOT this
    # handler.
    $ch = New-WebAuthnChallenge -Purpose 'register'
    $uid = New-WebAuthnUserId
    $port = [int]$Script:BrokerPort
    Write-JsonResponse -Context $Context -Status 200 -Body @{
        challenge        = $ch.challenge
        timeoutMs        = 60000
        userVerification = 'required'
        rpName           = 'PAX Cookbook'
        userIdBase64Url  = $uid.userId   # Fresh 32 random bytes,
                                         # decoupled from the
                                         # registration challenge.
                                         # Not linked to a Graph
                                         # identity; not persisted.
                                         # See New-WebAuthnUserId
                                         # in WebAuthnVerify.ps1.
        userName         = ('cookbook-' + [Environment]::UserName)
        userDisplayName  = [Environment]::UserName
        pubKeyCredParams = @(@{ type = 'public-key'; alg = -7 })
        acceptedOrigins  = @(
            ('http://127.0.0.1:' + $port),
            ('http://localhost:' + $port)
        )
    }
}

function Invoke-BrokerWebAuthnRegister {
    param($Context)
    # Persists a credential created by navigator.credentials.create.
    # Body shape:
    #   {
    #     credentialId    : '<base64url>',
    #     publicKeySpki   : '<base64>',       (PEM-bare base64; SPKI)
    #     alg             : -7,                (must be ES256 today)
    #     clientDataJSON  : '<base64url>',
    #     challenge       : '<base64url>',
    #     label           : '<optional friendly name>'
    #   }
    try {
        $body = Read-RequestJson -Context $Context
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }
    if ($null -eq $body) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    $missing = @()
    foreach ($f in 'credentialId','publicKeySpki','clientDataJSON','challenge') {
        if ([string]::IsNullOrWhiteSpace([string]$body.$f)) { $missing += $f }
    }
    if ($missing.Count -gt 0) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'missing_fields'
            fields = $missing
        }
        return
    }

    $alg = -7
    if ($null -ne $body.alg) {
        try { $alg = [int]$body.alg } catch { $alg = -7 }
    }
    if ($alg -ne -7) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error = 'unsupported_alg'
            alg   = $alg
        }
        return
    }

    # Consume the challenge (single-use). The clientDataJSON
    # parse below independently re-verifies the same challenge,
    # but consuming up-front prevents replay even if the JSON
    # parse fails.
    if (-not (Confirm-WebAuthnChallenge -ChallengeBase64Url ([string]$body.challenge) -ExpectedPurpose 'register')) {
        Add-RecentError 'WebAuthn register: challenge unknown, expired, or already consumed.' -Source 'webauthn_register'
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'challenge_invalid'
            reason = 'challenge_unknown_or_expired'
        }
        return
    }

    # Verify the clientDataJSON sanity (type / challenge / origin).
    try {
        $cdBytes = ConvertFrom-Base64Url -Encoded ([string]$body.clientDataJSON)
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'b64_decode_failed'
            field  = 'clientDataJSON'
        }
        return
    }
    $cd = Test-WebAuthnClientData `
            -ClientDataBytes   $cdBytes `
            -ExpectedType      'webauthn.create' `
            -ExpectedChallenge ([string]$body.challenge)
    if (-not $cd.ok) {
        Add-RecentError ('WebAuthn register: clientDataJSON rejected; reason=' + $cd.reason) -Source 'webauthn_register'
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'client_data_rejected'
            reason = $cd.reason
        }
        return
    }

    # Sanity-check the SPKI by attempting an import. We reject the
    # registration if the public key cannot be parsed -- that
    # surfaces a malformed payload immediately rather than at the
    # first unlock attempt later.
    try {
        $spkiBytes = [Convert]::FromBase64String([string]$body.publicKeySpki)
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error = 'b64_decode_failed'
            field = 'publicKeySpki'
        }
        return
    }
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    try {
        try {
            $bytesRead = 0
            $ecdsa.ImportSubjectPublicKeyInfo($spkiBytes, [ref]$bytesRead)
        } catch {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error  = 'public_key_import_failed'
                detail = [string]$_.Exception.Message
            }
            return
        }
    } finally {
        $ecdsa.Dispose()
    }

    $label = Get-WebAuthnCredentialLabel -Source $body

    try {
        Add-WebAuthnCredential `
            -CredentialIdBase64Url ([string]$body.credentialId) `
            -PublicKeySpkiBase64   ([string]$body.publicKeySpki) `
            -Alg                   $alg `
            -Label                 $label
    } catch {
        Add-RecentError ('WebAuthn register: persistence failed; ' + $_.Exception.Message) -Source 'webauthn_register'
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error  = 'register_persist_failed'
            detail = [string]$_.Exception.Message
        }
        return
    }

    Write-JsonResponse -Context $Context -Status 201 -Body @{
        ok          = $true
        registered  = $true
        credentialId = [string]$body.credentialId
    }
}

# ---------------------------------------------------------------------
# Dispatchers
# ---------------------------------------------------------------------

function Invoke-BrokerWebAuthnLockBypassRoute {
    # Routes that MUST work while the broker is Locked. Called from
    # Invoke-BrokerLockRoute (Routes/BrokerLock.ps1) so the lock
    # middleware lets them through before checking Locked state.
    # Returns $true if consumed; $false otherwise.
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -eq '/api/v1/broker/webauthn/status' -and $method -eq 'GET') {
        Invoke-BrokerWebAuthnStatus -Context $Context
        return $true
    }
    if ($path -eq '/api/v1/broker/webauthn/unlock-challenge' -and $method -eq 'POST') {
        Invoke-BrokerWebAuthnUnlockChallenge -Context $Context
        return $true
    }
    if ($path -eq '/api/v1/broker/webauthn/unlock' -and $method -eq 'POST') {
        Invoke-BrokerWebAuthnUnlock -Context $Context
        return $true
    }
    if ($path -eq '/api/v1/broker/webauthn/bootstrap-register-challenge' -and $method -eq 'POST') {
        Invoke-BrokerWebAuthnBootstrapRegisterChallenge -Context $Context
        return $true
    }
    if ($path -eq '/api/v1/broker/webauthn/bootstrap-register-unlock' -and $method -eq 'POST') {
        Invoke-BrokerWebAuthnBootstrapRegisterUnlock -Context $Context
        return $true
    }

    # Method-not-allowed on these LOCK-BYPASS paths still lives
    # here so a wrong-verb request gets a precise error (vs. a 423
    # from the lock middleware OR a 404 from the post-lock
    # dispatcher).
    if ($path -eq '/api/v1/broker/webauthn/status' -or
        $path -eq '/api/v1/broker/webauthn/unlock-challenge' -or
        $path -eq '/api/v1/broker/webauthn/unlock' -or
        $path -eq '/api/v1/broker/webauthn/bootstrap-register-challenge' -or
        $path -eq '/api/v1/broker/webauthn/bootstrap-register-unlock') {
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return $true
    }

    return $false
}

function Invoke-BrokerWebAuthnRoute {
    # Routes that require the broker to be Unlocked first (the
    # dispatch loop has already gated this via the lock
    # middleware). Called from the post-lock dispatch path in
    # Start-Broker.ps1. Returns $true if consumed; $false otherwise.
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -eq '/api/v1/broker/webauthn/register-challenge' -and $method -eq 'POST') {
        Invoke-BrokerWebAuthnRegisterChallenge -Context $Context
        return $true
    }
    if ($path -eq '/api/v1/broker/webauthn/register' -and $method -eq 'POST') {
        Invoke-BrokerWebAuthnRegister -Context $Context
        return $true
    }

    if ($path -eq '/api/v1/broker/webauthn/register-challenge' -or
        $path -eq '/api/v1/broker/webauthn/register') {
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return $true
    }

    return $false
}
