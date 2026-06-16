#requires -Version 7.4

# Updates.ps1 -- HTTP routes for the operator-driven update flow.
#
#   POST /api/v1/updates/check     -> 200 with the structured check result.
#                                     Fetches the configured manifest URL,
#                                     validates the manifest, and compares
#                                     versions. Single attempt; no retry.
#
#   POST /api/v1/updates/download  -> 200 with the staged-package result.
#                                     Requires a successful check
#                                     beforehand (the check populates the
#                                     in-memory manifest snapshot the
#                                     download path consumes).
#
#   GET  /api/v1/updates/state     -> 200 with the current in-memory state
#                                     plus the on-disk staged-package
#                                     inventory.
#
# The check and download endpoints are POST because they cause outbound
# network IO and update broker-side state; both already require the
# X-Cookbook-Request CSRF header via the main dispatcher.
#
# This file is the ONLY surface that drives the update flow at HTTP. The
# Update modules (Manifest.psm1, Package.psm1) are the only surfaces that
# perform outbound HTTPS. No other broker code reaches outside the
# loopback.
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - $Script:UpdateManifestUrl  (string or $null, from VERSION.json)
#   - $Script:CookbookVersion    (string)
#   - $Script:ReleaseChannel     (string)
#   - $Script:WorkspacePath      (string)
#   - Write-JsonResponse, Read-RequestJson, Get-UtcNowIso (broker helpers)
#   - Test-UpdateManifestUrl, Get-UpdateManifest, Test-UpdateManifestSchema,
#     Compare-CookbookVersion       (Update\Manifest.psm1)
#   - Save-UpdatePackage,
#     Get-UpdatePackageStagingDir,
#     Get-StagedPackageInventory    (Update\Package.psm1)

# ---------------------------------------------------------------------
# In-memory update state. Reset on every broker startup. The on-disk
# staged-package inventory is the source of truth across restarts; this
# in-memory state captures the most recent check and download attempt
# only.
# ---------------------------------------------------------------------
$Script:UpdateState = [hashtable]::Synchronized(@{
    lastCheck    = $null   # snapshot from Invoke-UpdatesCheck
    lastDownload = $null   # snapshot from Invoke-UpdatesDownload
    manifestSnapshot = $null  # validated manifest from the last successful check
})

# AI.C2.8 -- process-lifetime monotonic counter of observation-write
# INSERT failures. Internal to this broker process; resets to 0 on
# every broker restart. Never persisted, never sent off the appliance,
# never derived from a read of update_request_observations. The
# counter is informational only and MUST NOT influence /api/v1/health
# status derivation; the existing recent-errors ring continues to
# drive ok/degraded. See OPERATOR_GUIDE.md §17.14.
$Script:UpdateRequestObservationWriteFailureCount = 0

# AI.C2.10 -- process-lifetime monotonic counter of observation-write
# ATTEMPTS (every call to the writer, before the INSERT is tried).
# Paired with the AI.C2.8 failure counter as the denominator the chef
# needs to interpret the failure count. Same runtime-only invariants:
# internal to this broker process; resets to 0 on every broker restart;
# never persisted, never written to update_request_observations, never
# sent off the appliance, never derived from a read of the table.
# Informational only -- MUST NOT influence /api/v1/health status
# derivation. See OPERATOR_GUIDE.md §17.14.
$Script:UpdateRequestObservationWriteAttemptCount = 0

function Get-UpdateRequestObservationWriteFailureCount {
    # Pure read of the in-process counter. No DB touch, no clock
    # dependency, no aggregation. Returns an integer.
    return [int]$Script:UpdateRequestObservationWriteFailureCount
}

function Get-UpdateRequestObservationWriteAttemptCount {
    # Pure read of the in-process counter. No DB touch, no clock
    # dependency, no aggregation. Returns an integer.
    return [int]$Script:UpdateRequestObservationWriteAttemptCount
}

function Get-UpdateStateSnapshot {
    # Builds the public state body returned by GET /api/v1/updates/state
    # and embedded in the check / download responses.
    $configured = $false
    $urlCheckErr = $null
    if (-not [string]::IsNullOrWhiteSpace($Script:UpdateManifestUrl)) {
        $check = Test-UpdateManifestUrl -Url $Script:UpdateManifestUrl
        if ($check.ok) { $configured = $true } else { $urlCheckErr = $check.error }
    }
    $staged = @()
    try {
        $staged = Get-StagedPackageInventory -WorkspacePath $Script:WorkspacePath
    } catch {
        $staged = @()
    }

    # Trust readiness block. Surfaces -- truthfully -- whether the
    # operator has placed a trusted-signers.json allowlist under
    # <workspace>\Trust\. The block is intentionally light: it never
    # claims signature verification, only reports the configuration
    # state and signer count. Absence is the default and is reported
    # without alarm.
    $trustReadiness = [ordered]@{
        allowlistPresent = $false
        allowlistError   = $null
        signerCount      = 0
    }
    if (Get-Command -Name 'Read-TrustedSigners' -ErrorAction SilentlyContinue) {
        try {
            $ts = Read-TrustedSigners -WorkspacePath $Script:WorkspacePath
            $trustReadiness.allowlistPresent = [bool]$ts.present
            $trustReadiness.allowlistError   = $ts.error
            $trustReadiness.signerCount      = @($ts.signers).Count
        } catch {
            $trustReadiness.allowlistError = 'read_failed'
        }
    }

    return [ordered]@{
        manifestUrlConfigured  = $configured
        manifestUrlError       = $urlCheckErr
        currentCookbookVersion = $Script:CookbookVersion
        currentReleaseChannel  = $Script:ReleaseChannel
        lastCheck    = $Script:UpdateState.lastCheck
        lastDownload = $Script:UpdateState.lastDownload
        stagedPackages = @($staged)
        trustReadiness = $trustReadiness
    }
}

function Get-CheckResultState {
    # Maps a manifest-fetch / schema-validation / version-compare outcome
    # to the operator-readable "state" string surfaced in the response.
    param([hashtable]$Fetch, [hashtable]$Schema, [string]$LocalVersion)

    if (-not $Fetch.ok) {
        switch ($Fetch.error) {
            'not_configured'      { return 'notConfigured' }
            'placeholder_url'     { return 'notConfigured' }
            'malformed_url'       { return 'manifestUrlInvalid' }
            'forbidden_scheme'    { return 'manifestUrlInvalid' }
            default               { return 'fetchFailed' }
        }
    }
    if (-not $Schema.ok) { return 'manifestInvalid' }
    return $null   # caller fills in upToDate / updateAvailable based on version compare
}

# ---------------------------------------------------------------------
# POST /api/v1/updates/check
# ---------------------------------------------------------------------
function Invoke-UpdatesCheck {
    param($Context)

    $checkResult = [ordered]@{
        state          = $null
        checkedAtUtc   = (Get-UtcNowIso)
        manifestUrl    = $Script:UpdateManifestUrl
        currentVersion = $Script:CookbookVersion
        latestVersion  = $null
        bundledPaxChanges = $null
        compatibility  = $null
        manifest       = $null
        error          = $null
        message        = $null
    }

    # URL must be configured.
    if ([string]::IsNullOrWhiteSpace($Script:UpdateManifestUrl)) {
        $checkResult.state   = 'notConfigured'
        $checkResult.error   = 'not_configured'
        $checkResult.message = 'No update manifest URL is configured for this build.'
        $Script:UpdateState.lastCheck = $checkResult
        $Script:UpdateState.manifestSnapshot = $null
        Write-JsonResponse -Context $Context -Status 200 -Body @{ check = $checkResult; state = (Get-UpdateStateSnapshot) }
        return
    }

    # Fetch.
    $fetch = Get-UpdateManifest -Url $Script:UpdateManifestUrl -CookbookVersion $Script:CookbookVersion
    if (-not $fetch.ok) {
        $checkResult.state   = (Get-CheckResultState -Fetch $fetch -Schema @{ ok = $true } -LocalVersion $Script:CookbookVersion)
        $checkResult.error   = $fetch.error
        $checkResult.message = $fetch.message
        $Script:UpdateState.lastCheck = $checkResult
        $Script:UpdateState.manifestSnapshot = $null
        Write-JsonResponse -Context $Context -Status 200 -Body @{ check = $checkResult; state = (Get-UpdateStateSnapshot) }
        return
    }

    # Validate.
    $schema = Test-UpdateManifestSchema -Manifest $fetch.body
    if (-not $schema.ok) {
        $checkResult.state   = 'manifestInvalid'
        $checkResult.error   = $schema.error
        $checkResult.message = $schema.message
        $Script:UpdateState.lastCheck = $checkResult
        $Script:UpdateState.manifestSnapshot = $null
        Write-JsonResponse -Context $Context -Status 200 -Body @{ check = $checkResult; state = (Get-UpdateStateSnapshot) }
        return
    }

    # Compare versions.
    $latestVersion = [string]$fetch.body['latestCookbook']['version']
    $cmp = Compare-CookbookVersion -Left $latestVersion -Right $Script:CookbookVersion
    $checkResult.latestVersion = $latestVersion

    # PAX delta visibility: surface whether the bundled PAX script
    # version or SHA differs from what is currently installed.
    $remotePaxVer = [string]$fetch.body['includedPaxScript']['version']
    $remotePaxSha = [string]$fetch.body['includedPaxScript']['sha256']
    $checkResult.bundledPaxChanges = [ordered]@{
        currentVersion = $Script:PaxScriptVersion
        latestVersion  = $remotePaxVer
        currentSha256  = $Script:PaxScriptSha256
        latestSha256   = $remotePaxSha.ToUpperInvariant()
        changes        = (
            ($Script:PaxScriptVersion -ne $remotePaxVer) -or
            ($Script:PaxScriptSha256  -ne $remotePaxSha.ToUpperInvariant())
        )
    }

    # Compatibility visibility (operator-readable, no enforcement here).
    if ($fetch.body.Contains('compatibility') -and $fetch.body['compatibility']) {
        $cp = $fetch.body['compatibility']
        $checkResult.compatibility = [ordered]@{
            minCookbookVersionForPaxScript    = if ($cp.Contains('minCookbookVersionForPaxScript')) { $cp['minCookbookVersionForPaxScript'] } else { $null }
            minimumCompatibleInstallerVersion = if ($cp.Contains('minimumCompatibleInstallerVersion')) { $cp['minimumCompatibleInstallerVersion'] } else { $null }
        }
    }

    if ($cmp -gt 0) {
        $checkResult.state = 'updateAvailable'
    } elseif ($cmp -eq 0) {
        $checkResult.state = 'upToDate'
    } else {
        # Remote is OLDER than local. Treat as upToDate (a downgrade is
        # not an "update available"; the operator can still inspect the
        # surfaced versions).
        $checkResult.state = 'upToDate'
    }

    $checkResult.manifest = $fetch.body
    $Script:UpdateState.lastCheck = $checkResult
    $Script:UpdateState.manifestSnapshot = $fetch.body

    Write-JsonResponse -Context $Context -Status 200 -Body @{ check = $checkResult; state = (Get-UpdateStateSnapshot) }
}

# ---------------------------------------------------------------------
# POST /api/v1/updates/download
# ---------------------------------------------------------------------
function Invoke-UpdatesDownload {
    param($Context)

    $result = [ordered]@{
        state         = $null
        startedAtUtc  = (Get-UtcNowIso)
        finishedAtUtc = $null
        filename      = $null
        path          = $null
        sha256        = $null
        sizeBytes     = $null
        alreadyStaged = $false
        error         = $null
        message       = $null
    }

    $snapshot = $Script:UpdateState.manifestSnapshot
    if (-not $snapshot) {
        $result.state         = 'noManifestSnapshot'
        $result.finishedAtUtc = (Get-UtcNowIso)
        $result.error         = 'no_manifest_snapshot'
        $result.message       = 'Run a successful update check first; the download endpoint consumes the manifest captured by the last check.'
        $Script:UpdateState.lastDownload = $result
        Write-JsonResponse -Context $Context -Status 409 -Body @{ download = $result; state = (Get-UpdateStateSnapshot) }
        return
    }

    $lc = $snapshot['latestCookbook']
    $packageUrl    = [string]$lc['packageUrl']
    $expectedSha   = [string]$lc['sha256']
    $expectedVer   = [string]$lc['version']

    $save = Save-UpdatePackage `
        -WorkspacePath $Script:WorkspacePath `
        -PackageUrl $packageUrl `
        -ExpectedSha256 $expectedSha `
        -ExpectedCookbookVersion $expectedVer `
        -CookbookVersion $Script:CookbookVersion `
        -ManifestSnapshot $snapshot

    $result.finishedAtUtc = (Get-UtcNowIso)

    if (-not $save.ok) {
        $result.state   = 'downloadFailed'
        $result.error   = $save.error
        $result.message = $save.message
        $Script:UpdateState.lastDownload = $result
        Write-JsonResponse -Context $Context -Status 200 -Body @{ download = $result; state = (Get-UpdateStateSnapshot) }
        return
    }

    $result.state         = if ($save.alreadyStaged) { 'alreadyStaged' } else { 'staged' }
    $result.filename      = $save.filename
    $result.path          = $save.path
    $result.sha256        = $save.sha256
    $result.sizeBytes     = $save.sizeBytes
    $result.alreadyStaged = [bool]$save.alreadyStaged
    if ($save.PSObject.Properties.Match('warning') -and $save.warning) {
        $result.message = $save.warning
    }

    # AI.C3.2 G1 -- at-staging package-trust SHA-256 re-verification.
    # The Save-UpdatePackage call above writes the bytes to disk AND
    # has already done its own hash check at byte-receipt time; this
    # call is a SEPARATE, INDEPENDENT re-computation against the bytes
    # now at rest in the staging path, producing an append-only
    # package_trust_observations row tagged boundary='staging'. The
    # row is written for both the freshly-downloaded path and the
    # 'alreadyStaged' fast-path because the at-rest bytes can drift
    # between downloads (operator manipulation, external tooling).
    # The verifier increments its paired /health counters, never
    # mutates $result, and never short-circuits the response.
    try {
        $null = Invoke-PackageStagingVerification `
            -PackagePath    $save.path `
            -ExpectedSha256 $save.sha256
    } catch {
        Add-RecentError -Message ('Invoke-UpdatesDownload: at-staging verification threw: ' + $_.Exception.Message) -Source 'package_trust_observation'
    }

    $Script:UpdateState.lastDownload = $result
    Write-JsonResponse -Context $Context -Status 200 -Body @{ download = $result; state = (Get-UpdateStateSnapshot) }
}

# ---------------------------------------------------------------------
# GET /api/v1/updates/state
# ---------------------------------------------------------------------
function Invoke-UpdatesState {
    param($Context)
    Write-JsonResponse -Context $Context -Status 200 -Body (Get-UpdateStateSnapshot)
}

# =====================================================================
# Phase AG.C9 -- Update-apply runtime interaction.
# =====================================================================
#
# DOCTRINE: Update apply MUST NEVER kill an active cook to apply
# convenience. The appliance refuses (truthful 409) when any cook is
# running, and the operator's recourse is to wait for the cook to
# finish OR explicitly cancel it via the existing cook-cancel route --
# the apply path itself does NOT carry an override flag, by design.
#
# This is the central anti-"silent active-cook interruption" gate.
# Future apply machinery (package extraction, broker restart, etc.)
# will dock to this gate; no future caller may bypass it.
#
# As of V1.S01b the apply path performs the extract step and the
# broker self-exit step here; the launcher detached orchestrator
# performs the install and relaunch. The route still returns
# truthful refusals on every precondition failure: HTTP 401 on
# re-auth, HTTP 409 on active cooks or trust mismatch, HTTP 503
# on snapshot failure, HTTP 500 on extraction or handoff write
# failure. The "happy path" returns HTTP 202 with
# applyStatus = restart_initiated. Operators get an HONEST answer
# at every state.
# =====================================================================

function Test-UpdateApplyPreconditions {
    # Pure precondition evaluator. Used by both the live apply route
    # AND by dryRun previews. NEVER mutates state, NEVER calls
    # re-auth, NEVER touches the network. Always returns a structured
    # verdict whose top-level 'ok' field is the gate-pass signal.
    #
    # Returns hashtable with:
    #   ok               -- $true iff no precondition refuses.
    #   reason           -- short identifier on refusal; $null on ok.
    #   detail           -- operator-readable detail; $null on ok.
    #   activeCookCount  -- integer if snapshot succeeded, else $null.
    #   activeCooks      -- array of @{cookId;recipeId;pid;startedAt;ageSeconds}.
    #   stagedPackages   -- array passed through from update state.
    #   snapshotError    -- inner error from Get-ActiveCookSnapshot, or $null.
    #
    # Refusal reasons (NOT closure_reason vocabulary -- this is a
    # distinct precondition namespace):
    #   active_cooks_present
    #   active_cook_snapshot_failed
    $verdict = @{
        ok              = $false
        reason          = $null
        detail          = $null
        activeCookCount = $null
        activeCooks     = @()
        stagedPackages  = @()
        snapshotError   = $null
    }

    # 1. Determine active-cook population. If we cannot determine it,
    #    REFUSE -- we do NOT assume zero (silent interruption risk).
    $snapshot = Get-ActiveCookSnapshot
    if (-not $snapshot.ok) {
        $verdict.reason        = 'active_cook_snapshot_failed'
        $verdict.detail        = ('Cannot determine active cook population: ' + [string]$snapshot.detail + '. Refusing apply rather than risking silent interruption of a cook the broker cannot see.')
        $verdict.snapshotError = [string]$snapshot.error
        return $verdict
    }
    $verdict.activeCookCount = [int]$snapshot.count
    $verdict.activeCooks     = @($snapshot.cooks)

    # 2. Active cooks present -> refuse outright. No override flag.
    if ($snapshot.count -gt 0) {
        $verdict.reason = 'active_cooks_present'
        $verdict.detail = ("Refusing update apply: " + $snapshot.count + " cook(s) currently running. Wait for completion, or cancel the cook(s) explicitly via /api/v1/cooks/{cookId}/cancel. Cookbook never silently interrupts a running cook to apply an update.")
        return $verdict
    }

    # 3. Pass-through of staged-package inventory (preview convenience
    #    for the SPA, NOT a precondition).
    #
    #    Get-StagedPackageInventory emits its inventory array with the
    #    `,$items` non-unrolling idiom, so the call yields a single
    #    output unit whose value IS the inventory. Wrapping the call
    #    in `@(...)` would double-wrap -- the outer @() treats the
    #    single emitted Object[] as one item and produces a 1-element
    #    array whose sole member IS the inner inventory. The
    #    downstream apply loops (`foreach ($pkg in $pre.stagedPackages)`)
    #    would then iterate once with $pkg bound to that Object[],
    #    throwing PropertyNotFoundStrict on `$pkg.path` under
    #    Set-StrictMode -Version Latest -- the same defect class
    #    fixed in Invoke-PackageLaunchVerification. Bare assignment
    #    receives the inventory directly; the catch keeps the
    #    truthful empty-array fallback for any producer exception.
    try {
        $rawInventory = Get-StagedPackageInventory -WorkspacePath $Script:WorkspacePath
        if ($null -ne $rawInventory) {
            $verdict.stagedPackages = $rawInventory
        } else {
            $verdict.stagedPackages = @()
        }
    } catch {
        $verdict.stagedPackages = @()
    }

    # 4. All preconditions pass. The route is now authorized to
    #    proceed with apply initiation: extract the staged package,
    #    write the launcher handoff JSON, emit HTTP 202, and signal
    #    controlled broker shutdown so the launcher can hand off to
    #    the detached installer + relaunch orchestrator.
    #
    #    The route itself owns the decision of WHICH staged package
    #    to apply; this predicate only confirms that the chef has
    #    cleared every non-package precondition.
    $verdict.ok = $true
    return $verdict
}

# ---------------------------------------------------------------------
# POST /api/v1/updates/apply
# ---------------------------------------------------------------------

# AI.C2.3 -- update_request_observations writer.
#
# Append-only historical evidence writer. Inserts ONE row into
# update_request_observations per observed update-apply request that
# reached a lifecycle-phase-emitting branch. The function MUST NOT:
#
#   - update any existing observation row;
#   - delete any observation row;
#   - read any other observation row;
#   - infer continuity from prior rows;
#   - imply that the observed request is queued, pending, deferred,
#     scheduled, retried, replayed, resumed, or owned by the broker
#     for future execution.
#
# Returns the GUID it wrote on success, or $null on failure (with
# the failure surfaced via Add-RecentError). The caller embeds the
# returned value in the response body as 'observation_id'; a $null
# return surfaces to the chef as 'observation_id': null, which is
# the truthful "the broker received the request but could not
# record evidence" answer. The route still returns its normal
# wire-format -- truthful messiness is preferable to fabricated
# cleanliness, and refusing the request on observation-write
# failure would conflate evidence-recording with execution
# authority. The broker has neither.
function Add-UpdateRequestObservation {
    param(
        [Parameter(Mandatory=$true)][string]$RequestKind,
        [Parameter(Mandatory=$true)][string]$LifecyclePhase,
        [Parameter(Mandatory=$true)][string]$LifecyclePhaseSource,
        [Parameter(Mandatory=$true)][string]$EvidenceClassification,
        [Parameter(Mandatory=$true)][int]$HttpStatus
    )

    # AI.C2.10 -- attempt counter increment. MUST be the FIRST executable
    # statement of this function, BEFORE the SqliteConn null-check, the
    # outer try block, or any other work. Counts every call to the writer
    # (both successful and failing paths) so the AI.C2.8 failure counter
    # has a meaningful denominator. Wrapped in its own try/catch so a
    # pathological increment failure cannot escape the writer or
    # short-circuit the INSERT.
    try { $Script:UpdateRequestObservationWriteAttemptCount = [int]$Script:UpdateRequestObservationWriteAttemptCount + 1 } catch { }

    if ($null -eq $Script:SqliteConn) {
        Add-RecentError -Message 'Add-UpdateRequestObservation skipped: SQLite connection is null' -Source 'update_request_observation'
        return $null
    }

    $observationId = [Guid]::NewGuid().ToString()
    $observedAt    = Get-UtcNowIso
    $pidVal        = $PID
    $wsPath        = [string]$Script:WorkspacePath

    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = @'
INSERT INTO update_request_observations (
    observation_id, observed_at_utc, request_kind,
    lifecycle_phase, lifecycle_phase_source, evidence_classification,
    route, http_status, broker_pid, workspace_path
) VALUES (
    $oid, $obs, $rk,
    $lp, $lps, $ec,
    $rt, $st, $pid, $ws
);
'@
        $p = $cmd.CreateParameter(); $p.ParameterName = '$oid'; $p.Value = [string]$observationId;          [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$obs'; $p.Value = [string]$observedAt;             [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$rk';  $p.Value = [string]$RequestKind;            [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$lp';  $p.Value = [string]$LifecyclePhase;         [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$lps'; $p.Value = [string]$LifecyclePhaseSource;   [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$ec';  $p.Value = [string]$EvidenceClassification; [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$rt';  $p.Value = 'POST /api/v1/updates/apply';    [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$st';  $p.Value = [int]$HttpStatus;                [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$pid'; $p.Value = [int]$pidVal;                    [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$ws';  $p.Value = [string]$wsPath;                 [void]$cmd.Parameters.Add($p)
        try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
        return $observationId
    } catch {
        try { $Script:UpdateRequestObservationWriteFailureCount = [int]$Script:UpdateRequestObservationWriteFailureCount + 1 } catch { }
        Add-RecentError -Message ('Add-UpdateRequestObservation INSERT failed: ' + $_.Exception.Message) -Source 'update_request_observation'
        return $null
    }
}

function Invoke-UpdatesApply {
    param($Context)

    # Query-string parse for dryRun=true. Used by the SPA to preview
    # whether apply WOULD be refused without triggering re-auth or
    # any state-altering side effect.
    $dryRun = $false
    try {
        $qs = $Context.Request.Url.Query
        if ($qs) {
            $coll = [System.Web.HttpUtility]::ParseQueryString($qs)
            $v = $coll['dryRun']
            if ($v -and ($v -eq 'true' -or $v -eq '1')) { $dryRun = $true }
        }
    } catch {
        $dryRun = $false
    }

    # dryRun short-circuits BEFORE re-auth. The operator should be
    # able to preview the preconditions without being prompted for
    # Windows Hello / PIN. dryRun is read-only and side-effect-free
    # with respect to apply execution; it does, however, write ONE
    # append-only row of observational evidence per AI.C2.3 (see
    # below).
    #
    # AI.C2.2 -- DryRun is an EVALUATION request, not an apply
    # request. The operator explicitly signaled 'tell me about
    # applying' via ?dryRun=true; they did not signal 'apply'. The
    # lifecycle_phase value is sourced BY INDEX from the frozen
    # $Script:UpdateEvaluationPhases array (index 0 ==
    # 'update_apply_evaluation_requested') declared in
    # app/broker/Survivability/Vocabulary.ps1. It is NEVER
    # independently hard-coded here. This is a deliberate semantic
    # distinction from the live (non-dryRun) apply path, which
    # sources from $Script:UpdateLifecyclePhases[0]
    # ('update_apply_requested'). Conflating evaluation with apply
    # intent in a single phase would be a wire-format lie and would
    # inflate audit queries that count apply attempts. See
    # OPERATOR_GUIDE §17.8 for the operator-facing semantic decision
    # and §17.4 for the lifecycle-vs-evaluation categorical split.
    # evidence_classification is sourced BY INDEX from
    # $Script:SurvivabilityEvidenceClasses[1] ('observational')
    # because the operator's request has been observed, not acted
    # upon -- same reasoning as AI.C2.1's live-path use of the same
    # evidence class. The HTTP 200 status and the existing eight
    # body fields are unchanged; the three new AI.C2.2 fields are
    # additive transparency, not a change of behavior.
    #
    # AI.C2.3 -- One append-only row is written to
    # update_request_observations BEFORE the response. request_kind
    # is sourced by index from $Script:UpdateRequestKinds[1]
    # ('update_apply_evaluation_request'). The returned
    # observation_id is included verbatim in the response body. A
    # $null return surfaces in the wire as observation_id = $null,
    # which is the truthful answer when the broker received the
    # request but could not record evidence. The presence of an
    # observation_id does NOT mean apply was accepted, queued, or
    # owed; it means the broker recorded that the chef issued an
    # evaluation request at this time. Reading the row at any later
    # time (including across restart) yields the same forensic
    # truth -- no continuity, no resumption, no active state.
    if ($dryRun) {
        $pre = Test-UpdateApplyPreconditions
        $observationId = Add-UpdateRequestObservation `
            -RequestKind             $Script:UpdateRequestKinds[1] `
            -LifecyclePhase          $Script:UpdateEvaluationPhases[0] `
            -LifecyclePhaseSource    'UpdateEvaluationPhases[0]' `
            -EvidenceClassification  $Script:SurvivabilityEvidenceClasses[1] `
            -HttpStatus              200
        Write-JsonResponse -Context $Context -Status 200 -Body @{
            dryRun                  = $true
            wouldRefuse             = (-not $pre.ok)
            reason                  = $pre.reason
            detail                  = $pre.detail
            activeCookCount         = $pre.activeCookCount
            activeCooks             = $pre.activeCooks
            stagedPackages          = $pre.stagedPackages
            snapshotError           = $pre.snapshotError
            lifecycle_phase         = $Script:UpdateEvaluationPhases[0]
            lifecycle_phase_source  = 'UpdateEvaluationPhases[0]'
            evidence_classification = $Script:SurvivabilityEvidenceClasses[1]
            observation_id          = $observationId
        }
        return
    }

    # Live apply: re-auth gate first. Mirrors the manualCook pattern
    # in Routes/Cooks.ps1. The 'updateApply' OpClass is already
    # registered in $Script:BrokerLockReAuthOpClasses in BrokerLock.ps1.
    #
    # AI.C2.4 -- A failed re-auth verdict is a REFUSAL of the apply
    # request, not a mutation lifecycle progression. The chef
    # requested live apply but did not complete Windows Hello / PIN
    # verification, so apply NEVER began. The lifecycle_phase value
    # is sourced BY INDEX from $Script:UpdateRefusalPhases (index 0
    # == 'update_apply_refused_reauth_required'), declared in
    # app/broker/Survivability/Vocabulary.ps1. Refusal phases live
    # in a SEPARATE vocabulary from $Script:UpdateLifecyclePhases
    # because a refusal exits before the seven-step mutation
    # lifecycle begins; see OPERATOR_GUIDE §17.10 for the
    # operator-facing semantic decision. request_kind remains
    # $Script:UpdateRequestKinds[0] (update_apply_request) because
    # the chef issued an apply request (non-dryRun); only the
    # OUTCOME differs (refused). evidence_classification is
    # observational because the row witnesses a broker-observed
    # refusal event, not a future-execution commitment.
    # Persistence of the observation does NOT promote the request
    # into a queued, deferred, or retryable operation; the chef
    # must issue a new request to attempt apply again. The
    # existing HTTP 401 status, code 'reAuthRequired', and the
    # other body fields from New-BrokerLockReAuthRequiredResponse
    # are unchanged; the four AI.C2.4 fields are additive
    # transparency, not a change of behavior.
    $reAuthMsg = 'Verify to apply a Cookbook update. This is a privileged operation.'
    $verdict = Invoke-BrokerLockReAuthForOp -OpClass 'updateApply' -Message $reAuthMsg
    if ($verdict -ne 'Verified') {
        $resp = New-BrokerLockReAuthRequiredResponse -OpClass 'updateApply' -VerificationResult $verdict
        $observationId = Add-UpdateRequestObservation `
            -RequestKind             $Script:UpdateRequestKinds[0] `
            -LifecyclePhase          $Script:UpdateRefusalPhases[0] `
            -LifecyclePhaseSource    'UpdateRefusalPhases[0]' `
            -EvidenceClassification  $Script:SurvivabilityEvidenceClasses[1] `
            -HttpStatus              $resp.status
        $body = $resp.body
        $body['lifecycle_phase']         = $Script:UpdateRefusalPhases[0]
        $body['lifecycle_phase_source']  = 'UpdateRefusalPhases[0]'
        $body['evidence_classification'] = $Script:SurvivabilityEvidenceClasses[1]
        $body['observation_id']          = $observationId
        Write-JsonResponse -Context $Context -Status $resp.status -Body $body
        return
    }

    # Preconditions, post-re-auth. Order: snapshot probe, then active-
    # cook count, then apply-machinery presence.
    $pre = Test-UpdateApplyPreconditions

    if (-not $pre.ok -and $pre.reason -eq 'active_cook_snapshot_failed') {
        # We could not determine the active-cook population. Refuse
        # rather than proceed.
        #
        # AI.C2.4 -- An active-cook snapshot failure is a REFUSAL of
        # the apply request, not a mutation lifecycle progression.
        # The broker could not establish ground truth for the
        # precondition, so it refused to proceed. The
        # lifecycle_phase value is sourced BY INDEX from
        # $Script:UpdateRefusalPhases (index 2 ==
        # 'update_apply_refused_active_cook_snapshot_failed'). See
        # the AI.C2.4 doctrine block above the re-auth refusal for
        # the full semantic rationale; request_kind, evidence
        # classification, and existence-vs-acceptance discipline
        # are identical. The existing HTTP 503 status, error
        # 'active_cook_snapshot_failed', and the reason / detail /
        # snapshotError fields are unchanged; the four AI.C2.4
        # fields are additive transparency.
        Add-RecentError -Message ('Update apply refused: ' + $pre.detail) -Source 'update_apply_precheck'
        $observationId = Add-UpdateRequestObservation `
            -RequestKind             $Script:UpdateRequestKinds[0] `
            -LifecyclePhase          $Script:UpdateRefusalPhases[2] `
            -LifecyclePhaseSource    'UpdateRefusalPhases[2]' `
            -EvidenceClassification  $Script:SurvivabilityEvidenceClasses[1] `
            -HttpStatus              503
        Write-JsonResponse -Context $Context -Status 503 -Body @{
            error                   = 'active_cook_snapshot_failed'
            reason                  = $pre.reason
            detail                  = $pre.detail
            snapshotError           = $pre.snapshotError
            lifecycle_phase         = $Script:UpdateRefusalPhases[2]
            lifecycle_phase_source  = 'UpdateRefusalPhases[2]'
            evidence_classification = $Script:SurvivabilityEvidenceClasses[1]
            observation_id          = $observationId
        }
        return
    }

    if (-not $pre.ok -and $pre.reason -eq 'active_cooks_present') {
        # AI.C2.4 -- The presence of one or more active cooks is a
        # REFUSAL of the apply request, not a mutation lifecycle
        # progression. The chef requested live apply but the
        # precondition (zero active cooks) was not met, so apply
        # NEVER began. The lifecycle_phase value is sourced BY
        # INDEX from $Script:UpdateRefusalPhases (index 1 ==
        # 'update_apply_refused_active_cooks_present'). See the
        # AI.C2.4 doctrine block above the re-auth refusal for the
        # full semantic rationale. The existing HTTP 409 status,
        # error 'update_refused_active_cooks', and the reason /
        # detail / activeCookCount / activeCooks fields are
        # unchanged; the four AI.C2.4 fields are additive
        # transparency. Persistence of the refusal observation
        # does NOT imply the broker will reconsider apply once
        # the active cooks finish; the chef must issue a new
        # request.
        Add-RecentError -Message ('Update apply refused: ' + $pre.detail) -Source 'update_apply_precheck'
        $observationId = Add-UpdateRequestObservation `
            -RequestKind             $Script:UpdateRequestKinds[0] `
            -LifecyclePhase          $Script:UpdateRefusalPhases[1] `
            -LifecyclePhaseSource    'UpdateRefusalPhases[1]' `
            -EvidenceClassification  $Script:SurvivabilityEvidenceClasses[1] `
            -HttpStatus              409
        Write-JsonResponse -Context $Context -Status 409 -Body @{
            error                   = 'update_refused_active_cooks'
            reason                  = $pre.reason
            detail                  = $pre.detail
            activeCookCount         = $pre.activeCookCount
            activeCooks             = $pre.activeCooks
            lifecycle_phase         = $Script:UpdateRefusalPhases[1]
            lifecycle_phase_source  = 'UpdateRefusalPhases[1]'
            evidence_classification = $Script:SurvivabilityEvidenceClasses[1]
            observation_id          = $observationId
        }
        return
    }

    # Active-cook gate passed -- but apply machinery itself is not yet
    # implemented. Truthful 501 rather than fake success / fake start.
    #
    # AI.C3.2 G2 -- at-apply package-trust SHA-256 re-verification.
    # Runs after every precondition refusal branch has already
    # short-circuited (reauth, snapshot failure, active cooks) and
    # BEFORE the truthful 501 below. Iterates every staged package
    # surfaced by the apply preconditions snapshot; for each, re-
    # hashes the on-disk bytes from scratch via
    # Invoke-PackageApplyVerification and compares against the
    # caller-supplied expected hash from the .metadata.json sidecar.
    # The verifier writes one append-only row to
    # package_trust_observations per package with boundary='pre_apply'
    # AND increments its paired /health counters. On ANY mismatch the
    # apply request is REFUSED with HTTP 409, a structured error,
    # the new UpdateRefusalPhases[3] lifecycle phase, and the array of
    # package_trust_observation row IDs the chef can audit. The
    # refusal is also recorded as one update_request_observations row
    # so the two append-only tables tell the same story from two
    # angles (request-side and trust-side).
    $applyMismatchPackages   = @()
    $applyVerificationIds    = @()
    $applyVerificationByPath = [ordered]@{}
    foreach ($pkg in $pre.stagedPackages) {
        $pkgPath = $null
        $pkgExpected = $null
        if ($null -ne $pkg) {
            try { $pkgPath = [string]$pkg.path } catch { $pkgPath = $null }
            try {
                if ($null -ne $pkg.metadata -and $pkg.metadata.Contains('sha256')) {
                    $pkgExpected = [string]$pkg.metadata['sha256']
                }
            } catch { $pkgExpected = $null }
        }
        if ([string]::IsNullOrWhiteSpace($pkgPath)) { continue }
        if ([string]::IsNullOrWhiteSpace($pkgExpected)) {
            # No sidecar hash to compare against -- skip the at-apply
            # row entirely. The at-launch boundary will still re-derive
            # trust against the system trust store on next start.
            continue
        }
        $verdict = $null
        try {
            $verdict = Invoke-PackageApplyVerification `
                -PackagePath    $pkgPath `
                -ExpectedSha256 $pkgExpected
        } catch {
            Add-RecentError -Message ('Invoke-UpdatesApply: at-apply verification threw for ' + $pkgPath + ': ' + $_.Exception.Message) -Source 'package_trust_observation'
            $verdict = $null
        }
        if ($null -ne $verdict) {
            $applyVerificationIds += $verdict.observationId
            $applyVerificationByPath[$pkgPath] = [ordered]@{
                outcome        = [string]$verdict.outcome
                observationId  = $verdict.observationId
                expectedSha256 = [string]$verdict.expectedSha256
                observedSha256 = [string]$verdict.observedSha256
            }
            if ([string]$verdict.outcome -eq 'mismatch') {
                $applyMismatchPackages += $pkgPath
            }
        }
    }

    if ($applyMismatchPackages.Count -gt 0) {
        $observationId = Add-UpdateRequestObservation `
            -RequestKind             $Script:UpdateRequestKinds[0] `
            -LifecyclePhase          $Script:UpdateRefusalPhases[3] `
            -LifecyclePhaseSource    'UpdateRefusalPhases[3]' `
            -EvidenceClassification  $Script:SurvivabilityEvidenceClasses[1] `
            -HttpStatus              409
        Write-JsonResponse -Context $Context -Status 409 -Body @{
            error                            = 'package_trust_apply_mismatch'
            reason                           = 'package_trust_apply_mismatch'
            detail                           = ('At-apply SHA-256 re-verification refused ' + $applyMismatchPackages.Count + ' staged package(s). The bytes on disk no longer match the manifest-pinned digest captured at staging time. Re-download from a known-good source before retrying.')
            mismatchedPackages               = $applyMismatchPackages
            packageTrustObservationIds       = $applyVerificationIds
            packageTrustObservationsByPath   = $applyVerificationByPath
            lifecycle_phase                  = $Script:UpdateRefusalPhases[3]
            lifecycle_phase_source           = 'UpdateRefusalPhases[3]'
            evidence_classification          = $Script:SurvivabilityEvidenceClasses[1]
            observation_id                   = $observationId
        }
        return
    }

    # ----------------------------------------------------------------
    # V1.S01b -- apply initiation (HTTP 202 + controlled shutdown).
    #
    # All precondition refusals have already short-circuited above:
    #   - 401 on re-auth verdict != Verified
    #   - 503 on active_cook_snapshot_failed
    #   - 409 on active_cooks_present
    #   - 409 on at-apply package-trust mismatch
    # Test-UpdateApplyPreconditions now returns ok=$true; the apply
    # request is authorized. From this point the route returns
    # exactly one of:
    #
    #   - HTTP 500 with a structured error if the broker cannot
    #     fulfil the apply (extraction failed, malformed staged
    #     tree, handoff write failed). The broker stays alive so
    #     the operator can retry; ShutdownReason is NOT tagged.
    #
    #   - HTTP 202 with the apply-handoff payload AND a request
    #     for controlled shutdown. The handoff JSON tells the
    #     installer where the extracted bytes live; the
    #     'operator_update_apply' stop reason tells the launcher
    #     to spawn the detached orchestrator. The broker performs
    #     NO file swap, NO process restart, NO relaunch from
    #     inside its own running tree.
    #
    # No new production helper files (per slice rule). All apply
    # work is inline in this route. The installer is read-only.
    # ----------------------------------------------------------------

    # Pick the staged package to apply. Selection rule: the most
    # recently modified staged .zip that PASSED the at-apply trust
    # re-verification loop above. We never apply a package that
    # did not have a sidecar-pinned SHA-256 to compare against.
    $applyPackage        = $null
    $applyPackageHash    = $null
    $applyPackageVersion = $null
    foreach ($pkg in $pre.stagedPackages) {
        if ($null -eq $pkg) { continue }
        $thisPath = $null
        try { $thisPath = [string]$pkg.path } catch { $thisPath = $null }
        if ([string]::IsNullOrWhiteSpace($thisPath)) { continue }
        if (-not (Test-Path -LiteralPath $thisPath -PathType Leaf)) { continue }
        $thisVerdict = $null
        try { $thisVerdict = $applyVerificationByPath[$thisPath] } catch { $thisVerdict = $null }
        if ($null -eq $thisVerdict) { continue }
        $thisOutcome = $null
        try { $thisOutcome = [string]$thisVerdict['outcome'] } catch { $thisOutcome = $null }
        if ($thisOutcome -ne 'verified') { continue }
        if ($null -eq $applyPackage) { $applyPackage = $pkg; continue }
        $candWhen = [DateTime]::MinValue
        $bestWhen = [DateTime]::MinValue
        try { $candWhen = [DateTime]::Parse([string]$pkg.modifiedUtc).ToUniversalTime() } catch { }
        try { $bestWhen = [DateTime]::Parse([string]$applyPackage.modifiedUtc).ToUniversalTime() } catch { }
        if ($candWhen -gt $bestWhen) { $applyPackage = $pkg }
    }

    if ($null -eq $applyPackage) {
        Add-RecentError -Message 'Update apply refused: no verified staged package available for apply.' -Source 'update_apply'
        Write-JsonResponse -Context $Context -Status 409 -Body @{
            error  = 'no_verified_staged_package'
            reason = 'no_verified_staged_package'
            detail = 'No staged package with a sidecar-pinned SHA-256 is available for apply. Run Check and Download to stage a verified package first.'
        }
        return
    }

    try { $applyPackageHash    = [string]$applyPackage.metadata['sha256'] }                  catch { $applyPackageHash = $null }
    try { $applyPackageVersion = [string]$applyPackage.metadata['expectedCookbookVersion'] } catch { $applyPackageVersion = $null }
    if ([string]::IsNullOrWhiteSpace($applyPackageVersion)) { $applyPackageVersion = 'unknown' }

    # Extraction layout. The installer reads handoff.stagedExtractedPath
    # and joins 'VERSION.json' to it; that path must directly contain
    # VERSION.json. The release .zip top-level layout is { app\, launcher\ },
    # so:
    #
    #   <workspace>\Updates\<version>\extracted\               (Expand-Archive root)
    #   <workspace>\Updates\<version>\extracted\app\          (= stagedExtractedPath)
    #   <workspace>\Updates\<version>\extracted\app\VERSION.json
    #   <workspace>\Updates\<version>\extracted\app\install\Install-PAXCookbook.ps1
    #   <workspace>\Updates\<version>\extracted\launcher\
    #
    # See app\install\Install-PAXCookbook.ps1 Resolve-UpdateSourceRoot
    # for the consumed contract.
    $safeVersionForPath = ($applyPackageVersion -replace '[^0-9A-Za-z._-]', '_')
    $stagingDir         = Get-UpdatePackageStagingDir -WorkspacePath $Script:WorkspacePath
    $versionDir         = Join-Path $stagingDir $safeVersionForPath
    $extractionRoot     = Join-Path $versionDir 'extracted'
    $extractedAppDir    = Join-Path $extractionRoot 'app'
    $handoffJsonPath    = Join-Path $stagingDir 'handoff.json'
    $extractedVersionFile = Join-Path $extractedAppDir 'VERSION.json'

    # Idempotency: if extraction already exists AND the on-disk
    # VERSION.json's cookbookVersion matches the package metadata,
    # reuse it. Otherwise wipe and re-extract from the staged .zip.
    $extractionAlreadyValid = $false
    if (Test-Path -LiteralPath $extractedVersionFile -PathType Leaf) {
        try {
            $existingVer = (Get-Content -LiteralPath $extractedVersionFile -Raw | ConvertFrom-Json).cookbookVersion
            if ([string]$existingVer -eq [string]$applyPackageVersion) { $extractionAlreadyValid = $true }
        } catch { $extractionAlreadyValid = $false }
    }

    if (-not $extractionAlreadyValid) {
        try {
            if (Test-Path -LiteralPath $extractionRoot -PathType Container) {
                Remove-Item -LiteralPath $extractionRoot -Recurse -Force -ErrorAction Stop
            }
            New-Item -ItemType Directory -Path $extractionRoot -Force -ErrorAction Stop | Out-Null
            Expand-Archive -LiteralPath ([string]$applyPackage.path) -DestinationPath $extractionRoot -Force -ErrorAction Stop
        } catch {
            Add-RecentError -Message ('Update apply extraction failed: ' + $_.Exception.Message) -Source 'update_apply'
            Write-JsonResponse -Context $Context -Status 500 -Body @{
                error  = 'update_apply_extraction_failed'
                reason = 'update_apply_extraction_failed'
                detail = ('Could not extract staged package: ' + $_.Exception.Message)
            }
            return
        }
        if (-not (Test-Path -LiteralPath $extractedVersionFile -PathType Leaf)) {
            Add-RecentError -Message ('Update apply extraction produced no app\VERSION.json at ' + $extractedVersionFile) -Source 'update_apply'
            Write-JsonResponse -Context $Context -Status 500 -Body @{
                error  = 'update_apply_extracted_tree_malformed'
                reason = 'update_apply_extracted_tree_malformed'
                detail = ('The staged .zip did not produce app\VERSION.json under ' + $extractionRoot + '. Package layout does not match the installer handoff contract.')
            }
            return
        }
    }

    # Handoff JSON. The installer reads ONLY stagedExtractedPath;
    # every other field is forensic-evidence sidecar metadata for
    # operator audit of <workspace>\Updates\handoff.json. Contains
    # no secrets, no tenant data, no recipe data, no broker state.
    $handoffPayload = [ordered]@{
        stagedExtractedPath = $extractedAppDir
        version             = $applyPackageVersion
        packageSha256       = $applyPackageHash
        createdAtUtc        = Get-UtcNowIso
        brokerPid           = $PID
    }
    try {
        ($handoffPayload | ConvertTo-Json -Depth 6) |
            Set-Content -LiteralPath $handoffJsonPath -Encoding utf8 -ErrorAction Stop
    } catch {
        Add-RecentError -Message ('Update apply handoff write failed: ' + $_.Exception.Message) -Source 'update_apply'
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error  = 'update_apply_handoff_write_failed'
            reason = 'update_apply_handoff_write_failed'
            detail = ('Could not write handoff JSON: ' + $_.Exception.Message)
        }
        return
    }

    # ----------------------------------------------------------------
    # AI.C2 lifecycle phase emission.
    #
    # The broker has now: re-verified the staged package, extracted
    # it to the installer handoff layout, and written the handoff
    # JSON. From the apply-lifecycle vocabulary this is
    # update_apply_started (UpdateLifecyclePhases[1]) -- the broker
    # has begun the apply, but the file swap and process restart
    # are owned by the installer and the launcher orchestrator. We
    # write ONE append-only observation row with the started phase
    # and HTTP 202; no subsequent lifecycle phase is emitted from
    # the broker because it is about to exit.
    # ----------------------------------------------------------------
    $observationId = Add-UpdateRequestObservation `
        -RequestKind             $Script:UpdateRequestKinds[0] `
        -LifecyclePhase          $Script:UpdateLifecyclePhases[1] `
        -LifecyclePhaseSource    'UpdateLifecyclePhases[1]' `
        -EvidenceClassification  $Script:SurvivabilityEvidenceClasses[1] `
        -HttpStatus              202

    $applyResponseBody = @{
        applyStatus                    = 'restart_initiated'
        handoffPath                    = $handoffJsonPath
        stagedExtractedPath            = $extractedAppDir
        selectedPackagePath            = [string]$applyPackage.path
        selectedPackageSha256          = $applyPackageHash
        version                        = $applyPackageVersion
        expectedBehavior               = 'Cookbook is restarting against the new version. The broker will exit, the launcher will hand off to the installer, the installer will replace the App\ tree, and Cookbook will relaunch. Re-open the Cookbook window if it does not appear automatically.'
        packageTrustObservationIds     = $applyVerificationIds
        packageTrustObservationsByPath = $applyVerificationByPath
        lifecycle_phase                = $Script:UpdateLifecyclePhases[1]
        lifecycle_phase_source         = 'UpdateLifecyclePhases[1]'
        evidence_classification        = $Script:SurvivabilityEvidenceClasses[1]
        observation_id                 = $observationId
    }

    # Flush the 202 BEFORE signalling shutdown. Write-JsonResponse is
    # synchronous: it writes the body and closes the response stream
    # before returning. The chef's browser gets the apply-accepted
    # answer regardless of how fast the listener stops below.
    Write-JsonResponse -Context $Context -Status 202 -Body $applyResponseBody

    # Signal controlled shutdown. Earliest-writer-wins (same
    # discipline as the Ctrl+C and dispatch-loop-exception paths):
    # if some other branch already tagged $Script:ShutdownReason,
    # we leave that tag in place rather than overwriting it.
    # Setting $Script:ShuttingDown unblocks the dispatch loop's
    # while-header on its next iteration; calling
    # $Script:Listener.Stop() releases any GetContext() that would
    # otherwise still be blocking. The dispatch-loop finally then
    # invokes Invoke-Shutdown with the tagged reason, and the
    # terminal exit branch in Start-Broker.ps1 returns
    # $EXIT_OPERATOR_UPDATE_APPLY_REQUESTED so the launcher can
    # disambiguate this exit from every other clean exit.
    if ($null -eq $Script:ShutdownReason) {
        $Script:ShutdownReason = 'operator_update_apply'
    }
    $Script:ShuttingDown = $true
    try { $Script:Listener.Stop() } catch { }
}

# ---------------------------------------------------------------------
# Route dispatcher
# ---------------------------------------------------------------------
function Invoke-UpdatesRoute {
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -eq '/api/v1/updates/check') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-UpdatesCheck -Context $Context
        return $true
    }

    if ($path -eq '/api/v1/updates/download') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-UpdatesDownload -Context $Context
        return $true
    }

    if ($path -eq '/api/v1/updates/state') {
        if ($method -ne 'GET') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-UpdatesState -Context $Context
        return $true
    }

    if ($path -eq '/api/v1/updates/apply') {
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-UpdatesApply -Context $Context
        return $true
    }

    return $false
}
