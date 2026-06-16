#requires -Version 7.4

# WebAuthnVerify.ps1
#
# Phase UX-1H -- browser-owned Windows Hello via WebAuthn / platform
# authenticator. The browser invokes Windows Hello through the
# navigator.credentials API; the browser process becomes the natural
# owner of the Windows Security prompt, so the prompt appears
# associated with the browser window (correct monitor, no terminal
# taskbar flash). The broker only verifies the cryptographic
# assertion.
#
# Doctrine (verbatim, in force):
#   - Cookbook NEVER collects, hashes, compares, proxies, or stores
#     any Windows password, PIN, or biometric material. WebAuthn
#     surfaces ONLY a public key and a signature; the private key
#     stays inside Windows Hello / TPM, never leaves the platform
#     authenticator. The broker observes only the cryptographic
#     verdict.
#   - Browser-owned WebAuthn is the PRIMARY unlock path from the
#     FIRST user-facing unlock onwards, whenever the browser
#     supports the platform authenticator. The very first unlock
#     on a fresh workspace takes the LOCK-BYPASS bootstrap path
#     ('unlock_register'): navigator.credentials.create() invokes
#     Windows Hello in the browser process, the broker verifies
#     the resulting clientDataJSON + authData (UP and UV flags
#     required) and persists the public credential atomically
#     with the Locked -> Unlocked transition. Every subsequent
#     unlock takes the steady-state 'unlock' path
#     (navigator.credentials.get). The legacy broker-owned WinRT
#     path (Invoke-WindowsReAuth) is reserved as fallback for
#     browsers without WebAuthn support and as a recovery hatch
#     when the browser-owned path fails for technical reasons
#     (NOT user cancellation -- a cancel keeps the operator on the
#     overlay so they can retry without surprise).
#   - Verification != Graph permission. A WebAuthn assertion does
#     NOT imply Graph authorization. Per-op gated operations still
#     run their own re-auth (Invoke-BrokerLockReAuthForOp), which
#     stays on the broker-owned path because per-op verification
#     happens during in-flight HTTP requests, not at the lock
#     overlay.
#
# Local-only model:
#   - RP ID = effective domain of the page origin (resolved by the
#     browser; the broker accepts both '127.0.0.1' and 'localhost').
#   - Origin = http://127.0.0.1:<brokerPort> or http://localhost:<brokerPort>.
#     Verified by string match against $Script:BrokerPort.
#   - Challenge = 32 cryptographically random bytes, single-use,
#     in-process (Hashtable), 5-minute TTL. Never written to disk.
#   - Credential storage = <WorkspacePath>\Auth\webauthn-credentials.json.
#     One entry per registered (browser-profile, Windows-user) pair.
#     Schema: { schemaVersion, credentials: [ { credentialId,
#     publicKeySpkiBase64, alg, createdUtc, lastUsedUtc, signCount } ] }
#
# What is NOT verified:
#   - Attestation. We trust the local browser process to honestly
#     report the public key returned by the platform authenticator.
#     The bootstrap registration path that runs while Locked
#     ('unlock_register') is gated on:
#       (a) the broker-issued single-use challenge with that
#           specific purpose,
#       (b) the clientDataJSON origin matching 127.0.0.1 or
#           localhost on the broker's bound port,
#       (c) the authData UP+UV flags set (proving the browser
#           actually drove the platform authenticator to a
#           user-verified verdict before producing the credential),
#       (d) the SPKI parsing as a valid P-256 public key,
#       (e) the broker's HTTP socket being bound to 127.0.0.1 with
#           the SPA's bearer session token enforced at the
#           middleware layer.
#     These together are equivalent to the security gate of the
#     legacy broker-owned Hello path (which itself trusts WinRT
#     IUserConsentVerifier without an attestation chain), so
#     allowing bootstrap registration while Locked does not
#     weaken the threat model.

# ---------------------------------------------------------------------
# State
# ---------------------------------------------------------------------

# Pending challenges keyed by the challenge's base64url string. Each
# value is a hashtable: { purpose: 'register'|'unlock';
# createdUtc: DateTime }. Single-use: Confirm-WebAuthnChallenge
# removes the entry on success. A 5-minute TTL sweep runs lazily on
# every Confirm-* call.
$Script:WebAuthnPendingChallenges = @{}

# Maximum lifetime of a pending challenge before the lazy sweep
# evicts it. Long enough for an inattentive operator to dismiss
# their Windows Hello prompt and try again; short enough that a
# leaked challenge cannot be replayed indefinitely.
$Script:WebAuthnChallengeTtlSeconds = 300

# Default credential algorithm: ES256 (ECDSA over the NIST P-256
# curve with SHA-256). This is the COSE algorithm identifier -7,
# which is the most widely supported on Windows Hello + browsers.
# RS256 (-257) would also be acceptable but adds RSA-handling
# complexity to the broker; we restrict to ES256 to keep the
# verifier surface minimal.
$Script:WebAuthnSupportedAlgs = @(-7)

# ---------------------------------------------------------------------
# Storage helpers
# ---------------------------------------------------------------------

function Get-WebAuthnCredentialsPath {
    # Returns the absolute path of the JSON file that stores all
    # registered WebAuthn credentials for this workspace. Lives
    # under $Script:WorkspacePath\Auth\, following the same pattern
    # as Runtime/, Database/, Cooks/, Recipes/.
    if ([string]::IsNullOrWhiteSpace($Script:WorkspacePath)) {
        throw 'Get-WebAuthnCredentialsPath: $Script:WorkspacePath is not set; the broker must have initialized the workspace before WebAuthn calls.'
    }
    $authDir = Join-Path $Script:WorkspacePath 'Auth'
    return (Join-Path $authDir 'webauthn-credentials.json')
}

function Get-WebAuthnState {
    # Reads the credentials JSON file. Returns a hashtable
    # @{ schemaVersion = 1; credentials = @() } when the file does
    # not exist (first run). Caller MUST NOT mutate the returned
    # object's nested arrays without calling Save-WebAuthnState to
    # persist the changes.
    $path = Get-WebAuthnCredentialsPath
    if (-not (Test-Path -LiteralPath $path)) {
        return @{
            schemaVersion = 1
            credentials   = @()
        }
    }
    try {
        $raw  = Get-Content -LiteralPath $path -Raw -ErrorAction Stop
        $json = $raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop
        if ($null -eq $json.credentials) { $json.credentials = @() }
        return $json
    } catch {
        # If the file is corrupt, surface a clear error rather than
        # silently treating the workspace as having no credentials
        # (which would mask the bootstrap-vs-rotation distinction
        # in the SPA).
        throw ('WebAuthn credentials store is unreadable at ' + $path + ': ' + $_.Exception.Message)
    }
}

function Save-WebAuthnState {
    # Persists the credentials JSON atomically: write to a temp file
    # in the same directory, then rename over the live file. This
    # avoids partial writes if the broker is killed mid-save.
    param([Parameter(Mandatory)][hashtable]$State)
    $path = Get-WebAuthnCredentialsPath
    $dir  = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $tmp = $path + '.tmp'
    $json = $State | ConvertTo-Json -Depth 8 -Compress:$false
    Set-Content -LiteralPath $tmp -Value $json -Encoding utf8 -NoNewline
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    Rename-Item -LiteralPath $tmp -NewName (Split-Path -Leaf $path) -Force
}

function Find-WebAuthnCredential {
    # Looks up a credential by its base64url credentialId. Returns
    # $null when not found. Callers compare by reference for update,
    # but should pass through Save-WebAuthnState after mutating.
    param(
        [Parameter(Mandatory)][hashtable]$State,
        [Parameter(Mandatory)][string]$CredentialIdBase64Url
    )
    foreach ($c in $State.credentials) {
        if ($c.credentialId -eq $CredentialIdBase64Url) { return $c }
    }
    return $null
}

function Get-WebAuthnCredentialLabel {
    # Safe optional-label reader for any object that may or may not
    # carry a 'label' field. Returns the trimmed string value when
    # the field is present and non-blank, or $null when the field
    # is absent, blank, or the source object itself is $null.
    #
    # Under Set-StrictMode -Version Latest (which the broker runs
    # under, per Environment.ps1) any naked '.label' access on an
    # object that lacks the field throws PropertyNotFoundException
    # with the message
    #   "The property 'label' cannot be found on this object."
    # The two object shapes that flow through the bootstrap and
    # register routes both hit this trap:
    #   - request bodies parsed by Read-RequestJson, which uses
    #     ConvertFrom-Json -AsHashtable and therefore returns
    #     [OrderedHashtable]. PSObject.Properties does NOT enumerate
    #     hashtable keys, so PSObject-style probes silently report
    #     "absent" for keys that are in fact present; the only
    #     reliable check is IDictionary.Contains(key).
    #   - stored credential entries read by Get-WebAuthnState, which
    #     also uses -AsHashtable and so are hashtables today, but
    #     might in the future be PSCustomObject if the persistence
    #     layer changes. PSObject.Properties[name] is the reliable
    #     check there.
    # Both shapes are accepted; callers do not need to know which
    # they have.
    #
    # The 'label' field is fully optional. Older credential records
    # written before label support was added do not carry it, and
    # the SPA's current bootstrap-register-unlock POST body does
    # not include it either. Missing-label MUST NOT be treated as
    # an error -- this helper exists precisely so callers can
    # request the value without converting a missing field into a
    # 500-class failure.
    param([object]$Source)
    if ($null -eq $Source) { return $null }
    if ($Source -is [System.Collections.IDictionary]) {
        if ($Source.Contains('label')) {
            $v = [string]$Source['label']
            if (-not [string]::IsNullOrWhiteSpace($v)) { return $v }
        }
        return $null
    }
    $po = $Source.PSObject
    if ($null -ne $po) {
        $prop = $po.Properties['label']
        if ($null -ne $prop -and -not [string]::IsNullOrWhiteSpace([string]$prop.Value)) {
            return [string]$prop.Value
        }
    }
    return $null
}

# ---------------------------------------------------------------------
# Base64URL helpers
# ---------------------------------------------------------------------

function ConvertTo-Base64Url {
    # base64url without padding, as required by WebAuthn JSON.
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $b64 = [Convert]::ToBase64String($Bytes)
    return $b64.TrimEnd('=').Replace('+','-').Replace('/','_')
}

function ConvertFrom-Base64Url {
    # Accepts base64url with or without padding; returns a byte[].
    # Throws on invalid input -- callers should treat that as a
    # protocol violation (HTTP 400).
    param([Parameter(Mandatory)][string]$Encoded)
    $s = $Encoded.Replace('-','+').Replace('_','/')
    switch ($s.Length % 4) {
        2 { $s += '==' }
        3 { $s += '=' }
        0 { }
        default { throw 'ConvertFrom-Base64Url: invalid base64url input length.' }
    }
    return [Convert]::FromBase64String($s)
}

# ---------------------------------------------------------------------
# Challenge issuance + consumption
# ---------------------------------------------------------------------

function Invoke-WebAuthnChallengeSweep {
    # Lazy TTL sweep. Called from New-WebAuthnChallenge and
    # Confirm-WebAuthnChallenge so we never accumulate stale
    # entries when the operator walks away. Cheap: the table is
    # tiny in practice (one active challenge per browser tab).
    $cutoff = (Get-Date).ToUniversalTime().AddSeconds(-1 * $Script:WebAuthnChallengeTtlSeconds)
    $stale  = @()
    foreach ($key in $Script:WebAuthnPendingChallenges.Keys) {
        $entry = $Script:WebAuthnPendingChallenges[$key]
        if ($entry.createdUtc -lt $cutoff) { $stale += $key }
    }
    foreach ($key in $stale) {
        $null = $Script:WebAuthnPendingChallenges.Remove($key)
    }
}

function New-WebAuthnChallenge {
    # Issues a fresh challenge for the given purpose. Returns a
    # hashtable @{ challenge = '<base64url>' } that the SPA passes
    # straight to navigator.credentials.create/get. The same
    # base64url string MUST be returned by the browser in the
    # subsequent register/unlock POST so the broker can find the
    # pending entry.
    #
    # Purposes:
    #   'register'        -- enroll a credential while already Unlocked.
    #   'unlock'          -- verify an assertion against an existing
    #                        credential to transition Locked -> Unlocked.
    #   'unlock_register' -- enroll a credential FROM A LOCKED BROKER
    #                        AND simultaneously transition Locked ->
    #                        Unlocked. Used only by the lock-bypass
    #                        bootstrap path: the very first unlock on
    #                        a fresh workspace, before any credential
    #                        exists. The security gate is the platform
    #                        authenticator's user-verified
    #                        navigator.credentials.create call, whose
    #                        authData.UV+UP flags are checked on the
    #                        broker side.
    param(
        [Parameter(Mandatory)][ValidateSet('register','unlock','unlock_register')]
        [string]$Purpose
    )
    Invoke-WebAuthnChallengeSweep

    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $b64u = ConvertTo-Base64Url -Bytes $bytes

    $Script:WebAuthnPendingChallenges[$b64u] = @{
        purpose    = $Purpose
        createdUtc = (Get-Date).ToUniversalTime()
    }
    return @{ challenge = $b64u }
}

function New-WebAuthnUserId {
    # Issues a fresh 32-byte random base64url string suitable for
    # PublicKeyCredentialUserEntity.id. The value is NOT linked to
    # a Graph identity, NOT persisted, and NOT correlated with the
    # registration challenge. Decoupling user.id from the challenge
    # gives every bootstrap-register-unlock attempt an independent
    # per-credential identifier so a platform authenticator that
    # caches by (rp.id, user.id) does not collide with stale
    # orphans created by earlier failed attempts on this same
    # appliance. Returns a hashtable @{ userId = '<base64url>' }
    # to match the New-WebAuthnChallenge return shape.
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $b64u = ConvertTo-Base64Url -Bytes $bytes
    return @{ userId = $b64u }
}

function Confirm-WebAuthnChallenge {
    # Looks up a pending challenge, verifies its purpose matches,
    # and removes it (single-use). Returns $true on success and
    # $false when the challenge is unknown, expired, or registered
    # under a different purpose.
    param(
        [Parameter(Mandatory)][string]$ChallengeBase64Url,
        [Parameter(Mandatory)][ValidateSet('register','unlock','unlock_register')]
        [string]$ExpectedPurpose
    )
    Invoke-WebAuthnChallengeSweep
    if (-not $Script:WebAuthnPendingChallenges.ContainsKey($ChallengeBase64Url)) {
        return $false
    }
    $entry = $Script:WebAuthnPendingChallenges[$ChallengeBase64Url]
    if ($entry.purpose -ne $ExpectedPurpose) {
        # Drop the entry anyway -- a cross-purpose hit is either a
        # client bug or a misuse attempt; either way we should not
        # leave it to be consumed by a legitimate call.
        $null = $Script:WebAuthnPendingChallenges.Remove($ChallengeBase64Url)
        return $false
    }
    $null = $Script:WebAuthnPendingChallenges.Remove($ChallengeBase64Url)
    return $true
}

function Test-WebAuthnAttestationAuthData {
    # Verifies the authenticatorData blob returned alongside a
    # navigator.credentials.create() result satisfies the broker's
    # bootstrap requirements:
    #   - length is at least 37 (rpIdHash[32] || flags[1] || signCount[4]),
    #   - User Presence (UP, 0x01) flag is set,
    #   - User Verified (UV, 0x04) flag is set.
    # We do NOT verify the rpIdHash here -- the registration
    # clientDataJSON.origin acceptability check is what enforces
    # local-origin binding; the rpIdHash binds the credential to the
    # RP, and on Windows Hello with a locally-bound RP this is
    # derivable but not cryptographically meaningful in the
    # local-only threat model.
    #
    # Returns @{ ok = $true } on success or
    # @{ ok = $false; reason = '<tag>' } on failure.
    param([Parameter(Mandatory)][byte[]]$AuthenticatorData)
    if ($AuthenticatorData.Length -lt 37) {
        return @{ ok = $false; reason = 'authenticator_data_too_short' }
    }
    $flags = $AuthenticatorData[32]
    $upSet = (($flags -band 0x01) -ne 0)
    $uvSet = (($flags -band 0x04) -ne 0)
    if (-not $upSet) { return @{ ok = $false; reason = 'user_presence_not_asserted' } }
    if (-not $uvSet) { return @{ ok = $false; reason = 'user_verification_not_asserted' } }
    return @{ ok = $true }
}

# ---------------------------------------------------------------------
# Origin verification
# ---------------------------------------------------------------------

function Test-WebAuthnOriginAcceptable {
    # The browser's clientDataJSON includes the origin it observed.
    # We accept exactly two forms: http://127.0.0.1:<brokerPort>
    # and http://localhost:<brokerPort>. Anything else (a different
    # port, a different scheme, an injected host header) is
    # rejected. The broker port is read from $Script:BrokerPort
    # which was set during the HttpListener bind in Start-Broker.
    param([Parameter(Mandatory)][string]$Origin)
    if ([string]::IsNullOrWhiteSpace($Origin)) { return $false }
    $port = [int]$Script:BrokerPort
    if ($port -le 0) { return $false }
    $allowed = @(
        ('http://127.0.0.1:' + $port),
        ('http://localhost:' + $port)
    )
    return ($Origin -in $allowed)
}

# ---------------------------------------------------------------------
# clientDataJSON parsing + verification
# ---------------------------------------------------------------------

function Test-WebAuthnClientData {
    # Verifies the standard clientDataJSON invariants for a given
    # operation type ('webauthn.create' or 'webauthn.get'). Returns
    # @{ ok = $true } on success and @{ ok = $false; reason = '...' }
    # on failure with a stable reason tag the SPA can surface.
    param(
        [Parameter(Mandatory)][byte[]]$ClientDataBytes,
        [Parameter(Mandatory)][ValidateSet('webauthn.create','webauthn.get')]
        [string]$ExpectedType,
        [Parameter(Mandatory)][string]$ExpectedChallenge
    )
    try {
        $json = [System.Text.Encoding]::UTF8.GetString($ClientDataBytes)
        $obj  = $json | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    } catch {
        return @{ ok = $false; reason = 'client_data_parse_failed' }
    }
    if ($obj.type -ne $ExpectedType) {
        return @{ ok = $false; reason = 'client_data_type_mismatch' }
    }
    if ($obj.challenge -ne $ExpectedChallenge) {
        return @{ ok = $false; reason = 'client_data_challenge_mismatch' }
    }
    if (-not (Test-WebAuthnOriginAcceptable -Origin $obj.origin)) {
        return @{ ok = $false; reason = 'client_data_origin_unacceptable' }
    }
    return @{ ok = $true }
}

# ---------------------------------------------------------------------
# Registration
# ---------------------------------------------------------------------

function Add-WebAuthnCredential {
    # Persists a new credential. Called from the
    # /api/v1/broker/webauthn/register route after Confirm-* and
    # Test-WebAuthnClientData both succeed. Replaces any existing
    # entry with the same credentialId (which can happen if the
    # browser re-registers because the previous record was
    # cleared from the broker).
    param(
        [Parameter(Mandatory)][string]$CredentialIdBase64Url,
        [Parameter(Mandatory)][string]$PublicKeySpkiBase64,
        [Parameter(Mandatory)][int]$Alg,
        [string]$Label
    )
    if ($Alg -notin $Script:WebAuthnSupportedAlgs) {
        throw ('Add-WebAuthnCredential: unsupported COSE alg ' + $Alg + '; broker accepts only ' + ($Script:WebAuthnSupportedAlgs -join ','))
    }
    $state = Get-WebAuthnState
    $existing = Find-WebAuthnCredential -State $state -CredentialIdBase64Url $CredentialIdBase64Url
    $nowIso = (Get-Date).ToUniversalTime().ToString('o')
    if ($null -ne $existing) {
        $existing.publicKeySpkiBase64 = $PublicKeySpkiBase64
        $existing.alg                 = $Alg
        $existing.lastUsedUtc         = $nowIso
        if ($Label) { $existing.label = $Label }
    } else {
        $entry = @{
            credentialId        = $CredentialIdBase64Url
            publicKeySpkiBase64 = $PublicKeySpkiBase64
            alg                 = $Alg
            createdUtc          = $nowIso
            lastUsedUtc         = $nowIso
            signCount           = 0
        }
        if ($Label) { $entry.label = $Label }
        $state.credentials = @($state.credentials) + $entry
    }
    Save-WebAuthnState -State $state
}

function Remove-WebAuthnCredential {
    # Optional housekeeping surface. Not currently exposed via
    # HTTP, but kept here so smoke tests / future operator tooling
    # can reset a stale credential without hand-editing JSON.
    param([Parameter(Mandatory)][string]$CredentialIdBase64Url)
    $state = Get-WebAuthnState
    $state.credentials = @($state.credentials | Where-Object { $_.credentialId -ne $CredentialIdBase64Url })
    Save-WebAuthnState -State $state
}

function Get-WebAuthnRegistrationSummary {
    # Returns @{ registered = $bool; credentialIds = @(...) } for
    # the /api/v1/broker/webauthn/status endpoint. The SPA uses
    # this to decide whether to enter the bootstrap (broker-owned)
    # path or the steady-state (browser-owned) path. We never
    # surface the public key on this endpoint; only the
    # base64url credential IDs the browser needs in
    # allowCredentials.
    $state = Get-WebAuthnState
    $ids = @()
    foreach ($c in $state.credentials) {
        if ($c.credentialId) { $ids += [string]$c.credentialId }
    }
    return @{
        registered    = ($ids.Count -gt 0)
        credentialIds = $ids
    }
}

# ---------------------------------------------------------------------
# Assertion verification (ECDSA P-256 / SHA-256)
# ---------------------------------------------------------------------

function Test-WebAuthnAssertion {
    # Verifies a WebAuthn assertion produced by
    # navigator.credentials.get() against a stored credential.
    # Returns @{ ok = $true } on success or
    # @{ ok = $false; reason = '<tag>' } on any failure. The
    # caller MUST treat any non-ok result as fail-closed and
    # leave the broker locked.
    #
    # Inputs are exactly what the browser hands back, base64url-
    # encoded:
    #   - CredentialIdBase64Url  : credential.rawId
    #   - ClientDataBase64Url    : credential.response.clientDataJSON
    #   - AuthenticatorDataBase64Url : credential.response.authenticatorData
    #   - SignatureBase64Url     : credential.response.signature
    #
    # ExpectedChallenge is the base64url challenge the broker
    # issued via New-WebAuthnChallenge -Purpose 'unlock'. The
    # caller has already removed the challenge from the pending
    # table via Confirm-WebAuthnChallenge.
    param(
        [Parameter(Mandatory)][string]$CredentialIdBase64Url,
        [Parameter(Mandatory)][string]$ClientDataBase64Url,
        [Parameter(Mandatory)][string]$AuthenticatorDataBase64Url,
        [Parameter(Mandatory)][string]$SignatureBase64Url,
        [Parameter(Mandatory)][string]$ExpectedChallenge
    )

    # Look up the stored credential.
    $state = Get-WebAuthnState
    $cred  = Find-WebAuthnCredential -State $state -CredentialIdBase64Url $CredentialIdBase64Url
    if ($null -eq $cred) {
        return @{ ok = $false; reason = 'credential_unknown' }
    }
    if ($cred.alg -notin $Script:WebAuthnSupportedAlgs) {
        return @{ ok = $false; reason = 'credential_alg_unsupported' }
    }

    # Decode the four byte blobs. The [byte[]] casts are load-
    # bearing -- ConvertFrom-Base64Url returns a byte[] but
    # PowerShell's function-return semantics can re-wrap it as a
    # System.Object[] before assignment, which would silently break
    # the [Buffer]::BlockCopy call below ("Object must be an array
    # of primitives"). Casting on assignment pins the primitive
    # array type so the BCL crypto APIs accept it.
    try {
        $clientData        = [byte[]](ConvertFrom-Base64Url -Encoded $ClientDataBase64Url)
        $authenticatorData = [byte[]](ConvertFrom-Base64Url -Encoded $AuthenticatorDataBase64Url)
        $signature         = [byte[]](ConvertFrom-Base64Url -Encoded $SignatureBase64Url)
        $publicKeySpki     = [byte[]][Convert]::FromBase64String($cred.publicKeySpkiBase64)
    } catch {
        return @{ ok = $false; reason = 'b64_decode_failed' }
    }

    # clientDataJSON sanity (type, challenge, origin).
    $cd = Test-WebAuthnClientData `
            -ClientDataBytes $clientData `
            -ExpectedType    'webauthn.get' `
            -ExpectedChallenge $ExpectedChallenge
    if (-not $cd.ok) { return @{ ok = $false; reason = $cd.reason } }

    # authenticatorData layout: rpIdHash[32] || flags[1] || signCount[4] || ...
    # We don't verify the rpIdHash (the browser computed it from the
    # effective origin and we already verified origin acceptability
    # above), but we DO require the User Verified flag (UV = 0x04)
    # and the User Present flag (UP = 0x01) to be set -- that is
    # what proves Windows Hello actually challenged the operator
    # for this assertion rather than the browser cached a session.
    if ($authenticatorData.Length -lt 37) {
        return @{ ok = $false; reason = 'authenticator_data_too_short' }
    }
    $flags = $authenticatorData[32]
    $upSet = (($flags -band 0x01) -ne 0)
    $uvSet = (($flags -band 0x04) -ne 0)
    if (-not $upSet) { return @{ ok = $false; reason = 'user_presence_not_asserted' } }
    if (-not $uvSet) { return @{ ok = $false; reason = 'user_verification_not_asserted' } }

    # Build the bytes that were actually signed:
    #   signedBytes = authenticatorData || SHA-256(clientDataJSON)
    # ECDsa.VerifyData with HashAlgorithmName.SHA256 will hash
    # signedBytes for us once more, which matches the WebAuthn
    # signature contract.
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $cdHash = $sha.ComputeHash($clientData)
    } finally {
        $sha.Dispose()
    }
    $signed = New-Object byte[] ($authenticatorData.Length + $cdHash.Length)
    [Buffer]::BlockCopy($authenticatorData, 0, $signed, 0, $authenticatorData.Length)
    [Buffer]::BlockCopy($cdHash, 0, $signed, $authenticatorData.Length, $cdHash.Length)

    # ECDSA verification. The signature is ASN.1 / DER encoded (the
    # WebAuthn spec mandates this for ECDSA), so we request
    # Rfc3279DerSequence rather than the raw r||s IeeeP1363 form.
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    try {
        try {
            $bytesRead = 0
            $ecdsa.ImportSubjectPublicKeyInfo($publicKeySpki, [ref]$bytesRead)
        } catch {
            return @{ ok = $false; reason = 'public_key_import_failed' }
        }
        try {
            $valid = $ecdsa.VerifyData(
                $signed,
                $signature,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
        } catch {
            return @{ ok = $false; reason = 'signature_verify_threw' }
        }
        if (-not $valid) {
            return @{ ok = $false; reason = 'signature_invalid' }
        }
    } finally {
        $ecdsa.Dispose()
    }

    # Update lastUsedUtc and signCount. We do NOT enforce that
    # signCount is strictly increasing -- some platform
    # authenticators (Windows Hello included) return a constant 0
    # for hardware-backed credentials, so a strict-increase rule
    # would lock the operator out. We still record what the
    # browser sent so future diagnostic tooling can flag rollback
    # if one ever appears.
    if ($authenticatorData.Length -ge 37) {
        # signCount is a big-endian 32-bit unsigned int at offset 33.
        $sc = ([int]$authenticatorData[33] -shl 24) -bor `
              ([int]$authenticatorData[34] -shl 16) -bor `
              ([int]$authenticatorData[35] -shl  8) -bor `
              ([int]$authenticatorData[36])
        $cred.signCount = $sc
    }
    $cred.lastUsedUtc = (Get-Date).ToUniversalTime().ToString('o')
    Save-WebAuthnState -State $state

    return @{ ok = $true }
}

# ---------------------------------------------------------------------
# Self-test helpers (used by the verification harness, not by HTTP)
# ---------------------------------------------------------------------

function Get-WebAuthnPendingChallengeCount {
    # Diagnostic surface for smoke / harness. Returns the count of
    # pending challenges currently in the in-process table. Not
    # exposed via HTTP.
    return $Script:WebAuthnPendingChallenges.Count
}

function Clear-WebAuthnPendingChallenges {
    # Diagnostic surface for smoke / harness. Drops all pending
    # challenges (e.g. between test scenarios). Not exposed via
    # HTTP.
    $Script:WebAuthnPendingChallenges.Clear()
}
