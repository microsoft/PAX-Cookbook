# =====================================================================
# Routes\Setup.ps1
# =====================================================================
#
# Stage 3 (Phase 13) -- byte-preserving PAX script acquisition +
# activation under the §4.5 immutability contract at the top of
# Engine\Acquisition.psm1. download / cancel / state are real
# implementations. The upload route is intentionally split into two
# contracts:
#
#   - Content-Type: application/json with body
#         { "localFilePath": "<absolute .ps1 path>" }
#     is a narrowly-scoped JSON automation / smoke-test path. The
#     broker reads the file from disk, validates through the same
#     signed-manifest + approved-only + SHA-256 chain as download,
#     stages the bytes UNCHANGED, and activates via
#     Set-PaxScriptActivated. The operator's original local file is
#     NEVER mutated.
#
#   - Any other Content-Type (including multipart/form-data from a
#     real browser file-picker) returns HTTP 415 with the closed
#     token "multipart_upload_not_implemented" and a message saying
#     real upload is deferred until the SPA file-picker / file-
#     transfer stage. No multipart parser is implemented here.
#
# Stage 4B (Phase 13) -- customer-facing local-file UX. A sibling
# route /upload-bytes accepts Content-Type: application/octet-stream
# with the raw PAX script bytes as the body. The SPA first-run
# acquisition overlay reads the operator's selected file in the
# browser and POSTs the bytes here. The broker runs the same
# signed-manifest + approved-only + SHA-256 chain as the JSON-
# localFilePath path; the operator's source file is owned by the
# browser and the broker never opens it. X-PAX-Filename,
# X-PAX-File-Size, and X-PAX-Client-SHA256 request headers are
# advisory only -- the SHA-256 source of truth is the broker's
# SHA over the wire bytes, cross-checked against the signed
# manifest entry.
#
# Routes (all under /api/v1/setup/acquire-pax/):
#   POST .../download      -- HTTPS manifest fetch + script download + activate
#   POST .../upload        -- JSON-localFilePath automation path; multipart -> 415
#   POST .../upload-bytes  -- octet-stream raw-bytes customer path (Stage 4B)
#   POST .../cancel        -- mark current acquisition attempt cancelled
#   GET  .../state         -- read current acquisition state
#
# Loopback-bind + session-token auth is enforced by the broker's
# request pre-processor and not by individual route handlers.

Set-StrictMode -Version Latest

# ---------------------------------------------------------------------
# Stage 3 private helpers
# ---------------------------------------------------------------------

# Script-scope mirror of Engine\Acquisition.psm1's $Script:PaxScriptMaxBytes.
# Setup.ps1 is dot-sourced into the broker's script scope, which cannot
# reach into a module's script scope; without this mirror, any reference
# to $Script:PaxScriptMaxBytes from a route handler would raise a strict-
# mode "variable not set" error on the first byte-upload request.
$Script:PaxScriptMaxBytes = 4 * 1024 * 1024

function Get-SetupAcquisitionPrereqs {
    # Returns @{ ok=$true; versionPath; canonicalPax; cookbookVer;
    #            expectedSha; expectedVer; manifestUrl; trustAnchor;
    #            updatesDir } when every required broker-session
    # variable is populated, or @{ ok=$false; status; body } when the
    # call site should respond with the supplied status + body. Pure
    # read; never writes.
    $versionPath  = $Script:VersionFile
    $canonicalPax = $Script:PaxScriptPath
    $expectedVer  = $Script:PaxScriptVersion
    # Stage 4A: $Script:PaxScriptSha256 is null when the broker booted
    # into degraded mode (external policy + canonical script absent or
    # hash-mismatched). $Script:PaxScriptExpectedSha256 carries the
    # truthful VERSION.json paxScript.sha256 in all boot paths, so
    # prefer it. Download / upload acquisition flows need this value
    # to verify the freshly-fetched bytes against the expected hash;
    # they CANNOT work if expectedSha is null. Falling back to
    # $Script:PaxScriptSha256 keeps strict-embedded behavior unchanged
    # for any conceivable code path that pre-dates the expected-cache var.
    $expectedSha  = if ($null -ne $Script:PaxScriptExpectedSha256) { $Script:PaxScriptExpectedSha256 } else { $Script:PaxScriptSha256 }
    $cookbookVer  = $Script:CookbookVersion
    $manifestUrl  = $Script:PaxEngineManifestUrl
    $trustAnchor  = $Script:PaxEngineManifestTrustAnchorThumbprint
    $updatesDir   = $Script:UpdatesDir

    foreach ($pair in @(
        @{ name = 'VersionFile';     value = $versionPath },
        @{ name = 'PaxScriptPath';   value = $canonicalPax },
        @{ name = 'PaxScriptSha256'; value = $expectedSha },
        @{ name = 'PaxScriptVersion';value = $expectedVer },
        @{ name = 'CookbookVersion'; value = $cookbookVer },
        @{ name = 'UpdatesDir';      value = $updatesDir }
    )) {
        if ([string]::IsNullOrWhiteSpace([string]$pair.value)) {
            return @{
                ok     = $false
                status = 500
                body   = @{
                    error   = 'broker_state_missing'
                    message = ('Broker session variable $Script:' + $pair.name + ' is not populated.')
                    stage   = 'phase_13_stage_3'
                }
            }
        }
    }
    return @{
        ok              = $true
        versionPath     = $versionPath
        canonicalPax    = $canonicalPax
        cookbookVer     = $cookbookVer
        expectedSha     = $expectedSha
        expectedVer     = $expectedVer
        manifestUrl     = $manifestUrl
        trustAnchor     = $trustAnchor
        updatesDir      = $updatesDir
        signaturePolicy = if ([string]::IsNullOrWhiteSpace($Script:PaxScriptManifestSignaturePolicy)) { 'required' } else { [string]$Script:PaxScriptManifestSignaturePolicy }
    }
}

function Write-SetupAcquisitionFailure {
    # Merges { pending=$true; lastAttemptError={error,atUtc,...} } into
    # install-state without clearing successful prior acquisition
    # metadata, then responds with the supplied HTTP status +
    # structured body.
    param(
        [Parameter(Mandatory = $true)] $Context,
        [Parameter(Mandatory = $true)][int]$Status,
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [Parameter(Mandatory = $true)][string]$ErrorToken,
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $false)] $Details
    )
    $nowUtc = ([DateTimeOffset]::UtcNow).ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $lastErr = [ordered]@{
        error    = $ErrorToken
        atUtc    = $nowUtc
        endpoint = $Endpoint
        message  = $Message
    }
    if ($null -ne $Details -and $Details -is [System.Collections.IDictionary]) {
        foreach ($k in $Details.Keys) {
            $keyStr = [string]$k
            if (-not $lastErr.Contains($keyStr)) {
                $lastErr[$keyStr] = $Details[$k]
            }
        }
    }
    $stateWriteErr = $null
    try {
        [void](Write-PaxAcquisitionInstallState -Fields ([ordered]@{
            pending          = $true
            lastAttemptError = $lastErr
        }))
    } catch {
        $stateWriteErr = [string]$_.Exception.Message
    }
    $body = [ordered]@{
        error           = $ErrorToken
        endpoint        = $Endpoint
        stage           = 'phase_13_stage_3'
        message         = $Message
        atUtc           = $nowUtc
        signaturePolicy = if ([string]::IsNullOrWhiteSpace($Script:PaxScriptManifestSignaturePolicy)) { 'required' } else { [string]$Script:PaxScriptManifestSignaturePolicy }
    }
    if ($null -ne $Details -and $Details -is [System.Collections.IDictionary]) {
        foreach ($k in $Details.Keys) {
            $keyStr = [string]$k
            if (-not $body.Contains($keyStr)) {
                $body[$keyStr] = $Details[$k]
            }
        }
    }
    if ($null -ne $stateWriteErr) {
        $body['installStateWriteError'] = $stateWriteErr
    }
    Write-JsonResponse -Context $Context -Status $Status -Body $body
}

function New-SetupAcquisitionWorkDirectory {
    param([Parameter(Mandatory = $true)][string]$UpdatesDir)
    $ts   = ([DateTimeOffset]::UtcNow).ToString('yyyyMMddTHHmmssfffZ')
    $sub  = $ts + '-' + ([Guid]::NewGuid().ToString('N').Substring(0,8))
    $work = Join-Path (Join-Path $UpdatesDir 'engine') $sub
    if (-not (Test-Path -LiteralPath $work -PathType Container)) {
        New-Item -ItemType Directory -Path $work -Force | Out-Null
    }
    return $work
}

function Get-SetupRequestContentTypeBase {
    param([Parameter(Mandatory = $true)] $Context)
    $ct = $null
    try { $ct = [string]$Context.Request.ContentType } catch { $ct = $null }
    if ([string]::IsNullOrWhiteSpace($ct)) { return '' }
    $semi = $ct.IndexOf(';')
    if ($semi -ge 0) { $ct = $ct.Substring(0, $semi) }
    return $ct.Trim().ToLowerInvariant()
}

function Get-SetupRequestHeaderValue {
    # Bounded header reader for the upload-bytes route. Returns the
    # first value for the named header, trimmed, or $null when the
    # header is absent / blank. The X-PAX-* headers it reads are
    # ADVISORY ONLY -- the broker treats them as untrusted and uses
    # them solely for telemetry and fast-fail comparison against the
    # server-side byte computation. No header value is ever used for
    # path construction, hashing, or trust decisions.
    param(
        [Parameter(Mandatory = $true)] $Context,
        [Parameter(Mandatory = $true)][string]$Name
    )
    try {
        $headers = $Context.Request.Headers
        if ($null -eq $headers) { return $null }
        $val = [string]$headers[$Name]
        if ([string]::IsNullOrWhiteSpace($val)) { return $null }
        return $val.Trim()
    } catch {
        return $null
    }
}

function Read-SetupUploadBodyBytes {
    # Read the request body as raw bytes with a hard cap at
    # $Script:PaxScriptMaxBytes (4 MiB, mirroring the local-file
    # helper). The cap is enforced on raw bytes BEFORE any decode
    # or text parse so a malicious client cannot pad a tiny script
    # body with megabytes of trailing payload. Returns:
    #   @{ status = 'ok';        bytes = <byte[]>; advertisedSize = <long?> }
    #   @{ status = 'too_large'; bytes = $null;    advertisedSize = <long?> }
    #   @{ status = 'empty';     bytes = $null;    advertisedSize = $null }
    # Modeled on Read-RecipeTakeoutBodyBytes (Routes\RecipeTakeout.ps1).
    param($Context)
    $req = $Context.Request
    if (-not $req.HasEntityBody) {
        return @{ status = 'empty'; bytes = $null; advertisedSize = $null }
    }
    $cap = $Script:PaxScriptMaxBytes
    $advertised = $null
    try { $advertised = [long]$req.ContentLength64 } catch { $advertised = $null }
    if ($null -ne $advertised -and $advertised -gt $cap) {
        return @{ status = 'too_large'; bytes = $null; advertisedSize = $advertised }
    }
    $stream = $req.InputStream
    $ms     = New-Object System.IO.MemoryStream
    try {
        $buf  = New-Object byte[] 8192
        $read = 0
        while ($true) {
            $n = $stream.Read($buf, 0, $buf.Length)
            if ($n -le 0) { break }
            $read += $n
            if ($read -gt $cap) {
                return @{ status = 'too_large'; bytes = $null; advertisedSize = $advertised }
            }
            $ms.Write($buf, 0, $n)
        }
    } finally {
        try { $stream.Close() } catch {}
    }
    if ($ms.Length -eq 0) {
        return @{ status = 'empty'; bytes = $null; advertisedSize = $advertised }
    }
    return @{ status = 'ok'; bytes = $ms.ToArray(); advertisedSize = $advertised }
}

# ---------------------------------------------------------------------
# Acquisition handlers (Stage 3)
# ---------------------------------------------------------------------

function Get-SetupApprovedEntryForActivation {
    # Shared orchestration prefix used by both /download and the
    # JSON-localFilePath branch of /upload: prereq check, policy
    # gate, manifest fetch + verify, approved-only entry selection,
    # entry-vs-VERSION.json sha256 cross-check. On success returns
    # @{ ok=$true; prereq; workDir; manifest; manifestId; manifestHash;
    #    manifestVersion; entry; expectedSha }. On failure returns
    # @{ ok=$false; status; errorToken; message; details? } suitable
    # for piping into Write-SetupAcquisitionFailure.
    param(
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [Parameter(Mandatory = $false)][string]$TargetVersion,
        [Parameter(Mandatory = $false)][string]$TargetSha256
    )

    $prereq = Get-SetupAcquisitionPrereqs
    if (-not $prereq.ok) {
        return @{
            ok         = $false
            status     = [int]$prereq.status
            errorToken = [string]$prereq.body['error']
            message    = [string]$prereq.body['message']
        }
    }

    try {
        $policy = Resolve-PaxAcquisitionPolicy -VersionFilePath $prereq.versionPath
    } catch {
        return @{
            ok         = $false
            status     = 500
            errorToken = 'policy_resolution_failed'
            message    = [string]$_.Exception.Message
        }
    }
    if ($policy -ne 'external') {
        return @{
            ok         = $false
            status     = 409
            errorToken = 'policy_not_external'
            message    = ('PAX acquisition policy is "' + $policy + '". Mutating acquire endpoints require policy "external" in VERSION.json.')
            details    = @{ policy = $policy }
        }
    }
    if ([string]::IsNullOrWhiteSpace($prereq.manifestUrl)) {
        return @{
            ok         = $false
            status     = 409
            errorToken = 'engine_manifest_url_missing'
            message    = 'Broker session variable $Script:PaxEngineManifestUrl is not populated. VERSION.json paxScript.engineManifestUrl must be configured before acquisition.'
        }
    }
    if ([string]::IsNullOrWhiteSpace($prereq.trustAnchor) -and $prereq.signaturePolicy -ne 'internal-test-bypass') {
        return @{
            ok         = $false
            status     = 409
            errorToken = 'engine_manifest_trust_anchor_missing'
            message    = 'Broker session variable $Script:PaxEngineManifestTrustAnchorThumbprint is not populated. VERSION.json paxScript.engineManifestTrustAnchorThumbprint must be configured before acquisition.'
        }
    }

    $workDir = $null
    try {
        $workDir = New-SetupAcquisitionWorkDirectory -UpdatesDir $prereq.updatesDir
    } catch {
        return @{
            ok         = $false
            status     = 500
            errorToken = 'work_directory_create_failed'
            message    = ('Failed to create acquisition work directory: ' + $_.Exception.Message)
        }
    }

    $pkg = Get-ApprovedEngineManifestPackage `
        -ManifestUrl     $prereq.manifestUrl `
        -WorkDirectory   $workDir `
        -CookbookVersion $prereq.cookbookVer
    if ($null -eq $pkg -or -not $pkg.ok) {
        $pkgReason  = if ($null -ne $pkg -and $pkg.error)   { [string]$pkg.error }   else { 'manifest_fetch_failed' }
        $pkgMessage = if ($null -ne $pkg -and $pkg.message) { [string]$pkg.message } else { 'Approved-engine manifest fetch failed.' }
        return @{ ok = $false; status = 502; errorToken = $pkgReason; message = $pkgMessage }
    }

    $verify = Test-ApprovedEngineManifest `
        -ManifestBodyPath              $pkg.manifestBodyPath `
        -ManifestSignaturePath         $pkg.manifestSignaturePath `
        -ExpectedTrustAnchorThumbprint $prereq.trustAnchor `
        -SignaturePolicy               $prereq.signaturePolicy
    if ($null -eq $verify -or -not $verify.ok) {
        $verReason  = if ($null -ne $verify -and $verify.error)   { [string]$verify.error }   else { 'manifest_verify_failed' }
        $verMessage = if ($null -ne $verify -and $verify.message) { [string]$verify.message } else { 'Approved-engine manifest signature/schema verify failed.' }
        return @{ ok = $false; status = 502; errorToken = $verReason; message = $verMessage }
    }

    $selectArgs = @{ Manifest = $verify.manifest; CookbookVersion = $prereq.cookbookVer }
    if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) { $selectArgs['TargetVersion'] = $TargetVersion }
    if (-not [string]::IsNullOrWhiteSpace($TargetSha256))  { $selectArgs['TargetSha256']  = $TargetSha256 }
    $sel = Select-CompatibleEngineEntry @selectArgs
    if ($null -eq $sel -or -not $sel.ok) {
        $selReason  = if ($null -ne $sel -and $sel.error)   { [string]$sel.error }   else { 'no_compatible_engine' }
        $selMessage = if ($null -ne $sel -and $sel.message) { [string]$sel.message } else { 'No approved + compatible engine entry for this cookbook version.' }
        return @{ ok = $false; status = 409; errorToken = $selReason; message = $selMessage }
    }
    $entry = $sel.entry

    $entrySha = ([string]$entry['sha256']).ToUpperInvariant()
    $verSha   = $prereq.expectedSha.ToUpperInvariant()
    if ($entrySha -ne $verSha) {
        return @{
            ok         = $false
            status     = 409
            errorToken = 'version_hash_mismatch'
            message    = ('Approved entry sha256 ' + $entrySha + ' does not match VERSION.json paxScript.sha256 ' + $verSha + '.')
            details    = @{ entrySha256 = $entrySha; expectedSha256 = $verSha }
        }
    }

    return @{
        ok              = $true
        prereq          = $prereq
        workDir         = $workDir
        manifest        = $verify.manifest
        manifestId      = ([string]$verify.manifestId)
        manifestHash    = ([string]$verify.manifestHash)
        manifestVersion = ([string]$verify.manifestVersion)
        entry           = $entry
        expectedSha     = $verSha
        signaturePolicy = $prereq.signaturePolicy
    }
}

function Invoke-SetupAcquirePaxActivateStaged {
    # Activates a staged file via Set-PaxScriptActivated and emits
    # the success response.
    param(
        [Parameter(Mandatory = $true)] $Context,
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [Parameter(Mandatory = $true)] $StageHash,
        [Parameter(Mandatory = $true)] $Acquired,
        [Parameter(Mandatory = $true)][string]$SourceTag,
        [Parameter(Mandatory = $false)] $ExtraBody
    )
    $act = Set-PaxScriptActivated `
        -StagedFilePath      $Acquired.stagedPath `
        -ExpectedSha256      $StageHash.expectedSha `
        -Version             ([string]$Acquired.version) `
        -CanonicalScriptPath $StageHash.prereq.canonicalPax `
        -Source              $SourceTag `
        -ManifestId          $StageHash.manifestId `
        -ManifestHash        $StageHash.manifestHash `
        -ManifestVersion     $StageHash.manifestVersion
    if ($null -eq $act -or -not $act.ok) {
        $actReason  = if ($null -ne $act -and $act.error)   { [string]$act.error }   else { 'canonical_write_failed' }
        $actMessage = if ($null -ne $act -and $act.message) { [string]$act.message } else { 'Activation failed.' }
        Write-SetupAcquisitionFailure `
            -Context $Context -Status 502 -Endpoint $Endpoint `
            -ErrorToken $actReason -Message $actMessage
        return
    }

    # Stage 4A post-activation refresh of the broker's in-process
    # integrity cache. Set-PaxScriptActivated writes the canonical
    # PAX script bytes + install-state.json but does NOT touch the
    # broker's $Script:PaxScriptSha256 / $Script:PaxScriptVersion
    # variables (it lives in Engine\Acquisition.psm1 and has no
    # visibility into Start-Broker's script-scope). Routes\Cooks.ps1
    # reads $Script:PaxScriptSha256 to populate the cook context
    # block; if we don't refresh here, the FIRST cook spawn after a
    # successful first-run acquisition would either throw
    # 'Get-CookContextBlock: $Script:PaxScriptSha256 is not set' (when
    # the broker booted into Stage 4A degraded mode with the script
    # absent) or use a stale value from a prior bundle (when the
    # broker booted with a present-but-mismatched script). Clearing
    # $Script:BrokerStartupAcquisitionRequired here also flips the
    # /api/v1/setup/acquire-pax/state response brokerStartup.* fields
    # so the SPA's first-run overlay can dismiss itself without a
    # broker restart. The Stage 4 acquisition gate
    # (Routes\AcquisitionGate.ps1) is unaffected — it reads only the
    # install-state.json that Set-PaxScriptActivated just wrote.
    $Script:PaxScriptSha256                  = $StageHash.expectedSha
    $Script:PaxScriptVersion                 = [string]$Acquired.version
    $Script:BrokerStartupAcquisitionRequired = $false
    $Script:BrokerStartupAcquisitionReason   = $null

    $body = [ordered]@{
        endpoint        = $Endpoint
        stage           = 'phase_13_stage_3'
        result          = 'activated'
        source          = $SourceTag
        version         = ([string]$Acquired.version)
        sha256          = $StageHash.expectedSha
        canonicalScript = [ordered]@{ path = $act.canonicalPath }
        manifest        = [ordered]@{
            id      = $StageHash.manifestId
            hash    = $StageHash.manifestHash
            version = $StageHash.manifestVersion
        }
        timestamps      = [ordered]@{
            validatedAtUtc = $act.validatedAtUtc
            activatedAtUtc = $act.activatedAtUtc
        }
        signaturePolicy = if ($StageHash.Contains('signaturePolicy') -and -not [string]::IsNullOrWhiteSpace([string]$StageHash.signaturePolicy)) { [string]$StageHash.signaturePolicy } elseif (-not [string]::IsNullOrWhiteSpace($Script:PaxScriptManifestSignaturePolicy)) { [string]$Script:PaxScriptManifestSignaturePolicy } else { 'required' }
    }
    if ($null -ne $ExtraBody -and $ExtraBody -is [System.Collections.IDictionary]) {
        foreach ($k in $ExtraBody.Keys) {
            $body[[string]$k] = $ExtraBody[$k]
        }
    }
    Write-JsonResponse -Context $Context -Status 200 -Body $body
}

function Invoke-SetupAcquirePaxDownload {
    param($Context)
    $endpoint = 'POST /api/v1/setup/acquire-pax/download'

    $body = Read-RequestJson -Context $Context
    $targetVersion = $null
    $targetSha     = $null
    if ($null -ne $body -and $body -is [System.Collections.IDictionary]) {
        if ($body.Contains('targetVersion')) { $targetVersion = [string]$body['targetVersion'] }
        if ($body.Contains('targetSha256'))  { $targetSha     = [string]$body['targetSha256'] }
    }

    $stage = Get-SetupApprovedEntryForActivation `
        -Endpoint      $endpoint `
        -TargetVersion $targetVersion `
        -TargetSha256  $targetSha
    if (-not $stage.ok) {
        Write-SetupAcquisitionFailure `
            -Context $Context -Status $stage.status -Endpoint $endpoint `
            -ErrorToken $stage.errorToken -Message $stage.message -Details $stage['details']
        return
    }

    $acquired = Get-PaxScriptByDownload `
        -Entry                 $stage.entry `
        -WorkDirectory         $stage.workDir `
        -ExpectedVersionSha256 $stage.expectedSha `
        -CookbookVersion       $stage.prereq.cookbookVer
    if ($null -eq $acquired -or -not $acquired.ok) {
        $dlReason  = if ($null -ne $acquired -and $acquired.error)   { [string]$acquired.error }   else { 'script_fetch_failed' }
        $dlMessage = if ($null -ne $acquired -and $acquired.message) { [string]$acquired.message } else { 'PAX script byte download failed.' }
        Write-SetupAcquisitionFailure `
            -Context $Context -Status 502 -Endpoint $endpoint `
            -ErrorToken $dlReason -Message $dlMessage
        return
    }

    Invoke-SetupAcquirePaxActivateStaged `
        -Context $Context -Endpoint $endpoint -StageHash $stage `
        -Acquired $acquired -SourceTag 'download'
}

function Invoke-SetupAcquirePaxUpload {
    param($Context)
    $endpoint = 'POST /api/v1/setup/acquire-pax/upload'

    # The upload route is intentionally split. ONLY application/json
    # with body { "localFilePath": "<absolute .ps1 path>" } is wired.
    # Real browser multipart/form-data is DEFERRED until the SPA
    # file-picker / file-transfer stage and returns HTTP 415 with a
    # closed not-implemented token; we do NOT parse multipart here.
    $ctBase = Get-SetupRequestContentTypeBase -Context $Context
    if ($ctBase -ne 'application/json') {
        Write-JsonResponse -Context $Context -Status 415 -Body ([ordered]@{
            error    = 'multipart_upload_not_implemented'
            endpoint = $endpoint
            stage    = 'phase_13_stage_3'
            message  = 'Real multipart/form-data file upload is deferred until the SPA file-picker / file-transfer implementation stage. For automation and smoke tests, send Content-Type: application/json with body { "localFilePath": "<absolute path to .ps1>" } -- the broker reads the file from disk and validates through the same signed-manifest + approved-only + SHA-256 chain as the download route.'
            received = [ordered]@{
                contentTypeBase     = $ctBase
                acceptedContentType = 'application/json'
                acceptedBodyShape   = '{ "localFilePath": "<absolute path to .ps1>" }'
            }
        })
        return
    }

    $body = Read-RequestJson -Context $Context
    if ($null -eq $body -or -not ($body -is [System.Collections.IDictionary])) {
        Write-SetupAcquisitionFailure `
            -Context $Context -Status 400 -Endpoint $endpoint `
            -ErrorToken 'invalid_request_body' `
            -Message 'Request body must be JSON object: { "localFilePath": "<absolute path to .ps1>" }.'
        return
    }
    $localFilePath = $null
    if ($body.Contains('localFilePath')) { $localFilePath = [string]$body['localFilePath'] }
    if ([string]::IsNullOrWhiteSpace($localFilePath)) {
        Write-SetupAcquisitionFailure `
            -Context $Context -Status 400 -Endpoint $endpoint `
            -ErrorToken 'invalid_request_body' `
            -Message 'localFilePath is required and must be a non-empty string.'
        return
    }
    $targetVersion = $null
    $targetSha     = $null
    if ($body.Contains('targetVersion')) { $targetVersion = [string]$body['targetVersion'] }
    if ($body.Contains('targetSha256'))  { $targetSha     = [string]$body['targetSha256'] }

    $originalPreSha = $null
    try {
        if (Test-Path -LiteralPath $localFilePath -PathType Leaf) {
            $originalPreSha = ((Get-FileHash -LiteralPath $localFilePath -Algorithm SHA256).Hash).ToUpperInvariant()
        }
    } catch { $originalPreSha = $null }

    $stage = Get-SetupApprovedEntryForActivation `
        -Endpoint      $endpoint `
        -TargetVersion $targetVersion `
        -TargetSha256  $targetSha
    if (-not $stage.ok) {
        $stageDetails = $stage['details']
        if ($null -eq $stageDetails) { $stageDetails = [ordered]@{} }
        $stageDetails['localFilePath'] = $localFilePath
        if ($null -ne $originalPreSha) { $stageDetails['originalSha256BeforeAttempt'] = $originalPreSha }
        Write-SetupAcquisitionFailure `
            -Context $Context -Status $stage.status -Endpoint $endpoint `
            -ErrorToken $stage.errorToken -Message $stage.message -Details $stageDetails
        return
    }

    $acquired = Get-PaxScriptByLocalFile `
        -LocalFilePath         $localFilePath `
        -Manifest              $stage.manifest `
        -WorkDirectory         $stage.workDir `
        -CookbookVersion       $stage.prereq.cookbookVer `
        -TargetVersion         $targetVersion `
        -ExpectedVersionSha256 $stage.expectedSha
    if ($null -eq $acquired -or -not $acquired.ok) {
        $lfReason  = if ($null -ne $acquired -and $acquired.error)   { [string]$acquired.error }   else { 'read_failed' }
        $lfMessage = if ($null -ne $acquired -and $acquired.message) { [string]$acquired.message } else { 'PAX script local-file read failed.' }
        $lfDetails = [ordered]@{ localFilePath = $localFilePath }
        if ($null -ne $originalPreSha) { $lfDetails['originalSha256BeforeAttempt'] = $originalPreSha }
        Write-SetupAcquisitionFailure `
            -Context $Context -Status 502 -Endpoint $endpoint `
            -ErrorToken $lfReason -Message $lfMessage -Details $lfDetails
        return
    }

    $originalPostSha = $null
    try {
        if (Test-Path -LiteralPath $localFilePath -PathType Leaf) {
            $originalPostSha = ((Get-FileHash -LiteralPath $localFilePath -Algorithm SHA256).Hash).ToUpperInvariant()
        }
    } catch { $originalPostSha = $null }

    $extra = [ordered]@{
        original = [ordered]@{
            path               = $localFilePath
            sha256Before       = $originalPreSha
            sha256After        = $originalPostSha
            preservedUnchanged = ($null -ne $originalPreSha -and $originalPreSha -eq $originalPostSha)
        }
    }
    Invoke-SetupAcquirePaxActivateStaged `
        -Context $Context -Endpoint $endpoint -StageHash $stage `
        -Acquired $acquired -SourceTag 'local-file' -ExtraBody $extra
}

function Invoke-SetupAcquirePaxUploadBytes {
    # Stage 4B: customer-facing local-file PAX acquisition path. The
    # SPA's first-run acquisition overlay reads the selected file in
    # the browser, POSTs the raw bytes as application/octet-stream,
    # and this handler runs the same signed-manifest + approved-only
    # + SHA-256 chain as /upload (JSON-localFilePath) and /download.
    # The operator's source file is owned by the browser; the broker
    # never sees a filesystem path and never opens the operator file.
    # The X-PAX-* request headers are ADVISORY ONLY and are never
    # used for path construction, hashing, or trust decisions; they
    # appear in the response body only for telemetry and SPA echo.
    param($Context)
    $endpoint = 'POST /api/v1/setup/acquire-pax/upload-bytes'

    $ctBase = Get-SetupRequestContentTypeBase -Context $Context
    if ($ctBase -ne 'application/octet-stream') {
        Write-JsonResponse -Context $Context -Status 415 -Body ([ordered]@{
            error    = 'unsupported_content_type'
            endpoint = $endpoint
            stage    = 'phase_13_stage_4b'
            message  = 'Upload-bytes route requires Content-Type: application/octet-stream with the raw PAX script bytes as the request body. Multipart/form-data is not implemented; the JSON-localFilePath path remains at POST /api/v1/setup/acquire-pax/upload.'
            received = [ordered]@{
                contentTypeBase     = $ctBase
                acceptedContentType = 'application/octet-stream'
            }
        })
        return
    }

    $clientFilenameHeader = Get-SetupRequestHeaderValue -Context $Context -Name 'X-PAX-Filename'
    $clientSizeHeader     = Get-SetupRequestHeaderValue -Context $Context -Name 'X-PAX-File-Size'
    $clientShaHeader      = Get-SetupRequestHeaderValue -Context $Context -Name 'X-PAX-Client-SHA256'
    $clientTargetVersion  = Get-SetupRequestHeaderValue -Context $Context -Name 'X-PAX-Target-Version'
    $clientTargetSha      = Get-SetupRequestHeaderValue -Context $Context -Name 'X-PAX-Target-Sha256'

    $bodyRead = Read-SetupUploadBodyBytes -Context $Context
    $uploadEcho = [ordered]@{
        contentLengthHeader     = $bodyRead.advertisedSize
        receivedBytes           = if ($null -ne $bodyRead.bytes) { [long]$bodyRead.bytes.Length } else { 0 }
        clientFilenameHint      = $clientFilenameHeader
        clientReportedSize      = $clientSizeHeader
        clientReportedSha256    = $clientShaHeader
        sha256AcceptedByManifest= $false
    }
    if ($bodyRead.status -eq 'empty') {
        Write-SetupAcquisitionFailure `
            -Context $Context -Status 400 -Endpoint $endpoint `
            -ErrorToken 'empty_body' `
            -Message 'Request body must contain the raw PAX script bytes (Content-Type: application/octet-stream).' `
            -Details @{ upload = $uploadEcho }
        return
    }
    if ($bodyRead.status -eq 'too_large') {
        Write-SetupAcquisitionFailure `
            -Context $Context -Status 413 -Endpoint $endpoint `
            -ErrorToken 'payload_too_large' `
            -Message ('Uploaded body exceeds cap of ' + $Script:PaxScriptMaxBytes + ' bytes.') `
            -Details @{ upload = $uploadEcho; byteCap = $Script:PaxScriptMaxBytes }
        return
    }

    $stage = Get-SetupApprovedEntryForActivation `
        -Endpoint      $endpoint `
        -TargetVersion $clientTargetVersion `
        -TargetSha256  $clientTargetSha
    if (-not $stage.ok) {
        $stageDetails = $stage['details']
        if ($null -eq $stageDetails) { $stageDetails = [ordered]@{} }
        $stageDetails['upload'] = $uploadEcho
        Write-SetupAcquisitionFailure `
            -Context $Context -Status $stage.status -Endpoint $endpoint `
            -ErrorToken $stage.errorToken -Message $stage.message -Details $stageDetails
        return
    }

    $acquired = Get-PaxScriptByUploadBytes `
        -Bytes                  $bodyRead.bytes `
        -Manifest               $stage.manifest `
        -WorkDirectory          $stage.workDir `
        -CookbookVersion        $stage.prereq.cookbookVer `
        -TargetVersion          $clientTargetVersion `
        -ExpectedVersionSha256  $stage.expectedSha `
        -ClientReportedSha256   $clientShaHeader `
        -ClientReportedFilename $clientFilenameHeader
    if ($null -eq $acquired -or -not $acquired.ok) {
        $upReason  = if ($null -ne $acquired -and $acquired.error)   { [string]$acquired.error }   else { 'staging_write_failed' }
        $upMessage = if ($null -ne $acquired -and $acquired.message) { [string]$acquired.message } else { 'PAX script upload byte staging failed.' }
        $upDetails = [ordered]@{ upload = $uploadEcho }
        if ($null -ne $acquired -and $acquired.Contains('localSha256'))     { $upDetails['localSha256']     = $acquired.localSha256 }
        if ($null -ne $acquired -and $acquired.Contains('expectedSha256'))  { $upDetails['expectedSha256']  = $acquired.expectedSha256 }
        if ($null -ne $acquired -and $acquired.Contains('postWriteSha'))    { $upDetails['postWriteSha']    = $acquired.postWriteSha }
        Write-SetupAcquisitionFailure `
            -Context $Context -Status 502 -Endpoint $endpoint `
            -ErrorToken $upReason -Message $upMessage -Details $upDetails
        return
    }

    $uploadEcho['sha256AcceptedByManifest'] = $true
    $extra = [ordered]@{
        upload = $uploadEcho
        original = [ordered]@{
            path               = $null
            sha256Before       = $acquired.sha256
            sha256After        = $acquired.sha256
            preservedUnchanged = $true
        }
    }
    Invoke-SetupAcquirePaxActivateStaged `
        -Context $Context -Endpoint $endpoint -StageHash $stage `
        -Acquired $acquired -SourceTag 'local-file' -ExtraBody $extra
}

function Invoke-SetupAcquirePaxCancel {
    param($Context)
    $endpoint = 'POST /api/v1/setup/acquire-pax/cancel'
    $nowUtc = ([DateTimeOffset]::UtcNow).ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $lastErr = [ordered]@{
        error    = 'cancelled_by_operator'
        atUtc    = $nowUtc
        endpoint = $endpoint
        message  = 'Acquisition attempt cancelled by operator via POST /api/v1/setup/acquire-pax/cancel.'
    }
    $writeErr = $null
    try {
        [void](Write-PaxAcquisitionInstallState -Fields ([ordered]@{
            pending          = $true
            lastAttemptError = $lastErr
        }))
    } catch {
        $writeErr = [string]$_.Exception.Message
    }

    $stateBlock = $null
    try {
        $stateBlock = Get-PaxAcquisitionState `
            -VersionFilePath     $Script:VersionFile `
            -CanonicalScriptPath $Script:PaxScriptPath
    } catch { $stateBlock = $null }

    $body = [ordered]@{
        endpoint = $endpoint
        stage    = 'phase_13_stage_3'
        result   = 'cancelled'
        atUtc    = $nowUtc
    }
    if ($null -ne $stateBlock) {
        $body['state']            = $stateBlock.state
        $body['pending']          = [bool]$stateBlock.pending
        $body['source']           = $stateBlock.source
        $body['version']          = $stateBlock.version
        $body['sha256']           = $stateBlock.sha256
        $body['lastAttemptError'] = $stateBlock.lastAttemptError
    }
    if ($null -ne $writeErr) {
        $body['installStateWriteError'] = $writeErr
    }
    Write-JsonResponse -Context $Context -Status 200 -Body $body
}

function Invoke-SetupAcquirePaxState {
    param($Context)

    $versionPath  = $Script:VersionFile
    $canonicalPax = $Script:PaxScriptPath
    $expectedVer  = $Script:PaxScriptVersion
    # Stage 4A: same expected-SHA fallback as Get-SetupAcquisitionPrereqs.
    # The state route MUST surface the truthful canonical hash even
    # when the broker booted with the bundled script absent or
    # hash-mismatched, because the SPA renders this value in the
    # first-run acquisition overlay so the operator can compare it
    # against the hash of the script they are about to bring.
    $expectedSha  = if ($null -ne $Script:PaxScriptExpectedSha256) { $Script:PaxScriptExpectedSha256 } else { $Script:PaxScriptSha256 }
    $cookbookVer  = $Script:CookbookVersion

    if ([string]::IsNullOrWhiteSpace($versionPath)) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'broker_state_missing'
            message = 'Broker session variable $Script:VersionFile is not populated. Test-BundledPaxIntegrity should have set this at startup.'
            stage   = 'phase_13_stage_3'
        }
        return
    }

    try {
        $policy = Resolve-PaxAcquisitionPolicy -VersionFilePath $versionPath
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'policy_resolution_failed'
            message = [string]$_.Exception.Message
            stage   = 'phase_13_stage_3'
        }
        return
    }

    try {
        $state = Get-PaxAcquisitionState -VersionFilePath $versionPath -CanonicalScriptPath $canonicalPax
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error   = 'state_synthesis_failed'
            message = [string]$_.Exception.Message
            stage   = 'phase_13_stage_3'
        }
        return
    }

    $body = [ordered]@{
        endpoint        = 'GET /api/v1/setup/acquire-pax/state'
        stage           = 'phase_13_stage_3'
        policy          = $policy
        manifestSignaturePolicy = if ([string]::IsNullOrWhiteSpace($Script:PaxScriptManifestSignaturePolicy)) { 'required' } else { [string]$Script:PaxScriptManifestSignaturePolicy }
        state           = $state.state
        isAcquired      = [bool]$state.isAcquired
        pending         = [bool]$state.pending
        isLegacyEmbedded = ($policy -eq 'embedded')
        # Stage 4A: surface the broker's degraded-boot state. The SPA's
        # first-run acquisition overlay uses brokerStartup.acquisitionRequired
        # to decide whether to render the "broker booted without a
        # canonical PAX script — bring or download one" message. The
        # reason field is bounded to three known values:
        #   $null                       -> not degraded
        #   'pax_script_absent'         -> canonical script missing
        #   'pax_script_hash_mismatch'  -> canonical script present but on-disk SHA-256 != VERSION.json paxScript.sha256
        # The acquisition-gate predicate (Test-PaxAcquisitionGate) is
        # NOT a function of these fields — they are informational. The
        # gate is owned exclusively by the install-state.json snapshot
        # so that activation through ANY path (CLI, future installer,
        # SPA overlay) flips it uniformly.
        brokerStartup   = [ordered]@{
            acquisitionRequired = [bool]$Script:BrokerStartupAcquisitionRequired
            reason              = $Script:BrokerStartupAcquisitionReason
        }
        expected        = [ordered]@{
            paxScriptVersion = $expectedVer
            paxScriptSha256  = $expectedSha
            cookbookVersion  = $cookbookVer
        }
        canonicalScript = [ordered]@{
            path    = $state.canonicalScriptPath
            present = [bool]$state.canonicalScriptPresent
        }
        installState = [ordered]@{
            path             = $state.installStatePath
            source           = $state.source
            version          = $state.version
            sha256           = $state.sha256
            manifestId       = $state.manifestId
            manifestHash     = $state.manifestHash
            manifestVersion  = $state.manifestVersion
            validatedAtUtc   = $state.validatedAtUtc
            activatedAtUtc   = $state.activatedAtUtc
            lastAttemptError = $state.lastAttemptError
        }
        capabilities = [ordered]@{
            stateImplemented               = $true
            downloadImplemented            = $true
            uploadImplemented              = $false
            localFilePathUploadImplemented = $true
            byteUploadImplemented          = $true
            cancelImplemented              = $true
            manifestFetchImplemented       = $true
        }
    }

    Write-JsonResponse -Context $Context -Status 200 -Body $body
}

# ---------------------------------------------------------------------
# Route dispatcher
# ---------------------------------------------------------------------
function Invoke-SetupRoute {
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -eq '/api/v1/setup/acquire-pax/download') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-SetupAcquirePaxDownload -Context $Context
        return $true
    }

    if ($path -eq '/api/v1/setup/acquire-pax/upload') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-SetupAcquirePaxUpload -Context $Context
        return $true
    }

    if ($path -eq '/api/v1/setup/acquire-pax/upload-bytes') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-SetupAcquirePaxUploadBytes -Context $Context
        return $true
    }

    if ($path -eq '/api/v1/setup/acquire-pax/cancel') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-SetupAcquirePaxCancel -Context $Context
        return $true
    }

    if ($path -eq '/api/v1/setup/acquire-pax/state') {
        if ($method -ne 'GET') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-SetupAcquirePaxState -Context $Context
        return $true
    }

    return $false
}
