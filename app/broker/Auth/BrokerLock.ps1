#requires -Version 7.4

# BrokerLock.ps1
#
# Phase AF -- Broker lock-state machine and per-operation fresh
# re-authentication gate. This is the policy layer that sits on top
# of WindowsReAuth.ps1 (the primitive) and CredentialManager.ps1 (the
# secret store).
#
# Doctrine (verbatim, in force):
#   - The broker is process-scoped; the lock state lives in this
#     PowerShell process and resets on every restart. Cookbook does
#     NOT persist "unlocked" state across restarts. Boot state is
#     ALWAYS Locked.
#   - Unlock requires a fresh Windows-native verification verdict of
#     'Verified'. There is no token, no remembered password, no
#     "trust this device" toggle.
#   - The unlock is BROKER-scoped, not browser-scoped. Two browser
#     tabs on the same broker see the same lock state. Closing the
#     SPA does not lock; only an explicit /api/v1/broker/lock POST,
#     a broker restart, or an inactivity timeout locks.
#   - Per-operation fresh re-auth is REQUIRED for the operations
#     enumerated in $Script:BrokerLockReAuthOpClasses, even when the
#     broker is already Unlocked. The single Unlock gate is the
#     "the operator is at the keyboard" check; the per-op gate is
#     the "the operator INTENDED this specific privileged action"
#     check. THESE ARE TWO DISTINCT GATES:
#     **Unlocked state is NOT operation approval.** A broker can be
#     Unlocked and still demand a fresh Windows Hello verdict before
#     every gated operation. There is NO setting, no toggle, no chef-
#     facing option to disable this per-op gate -- it is structural.
#     The implementation of Invoke-BrokerLockReAuthForOp ALWAYS calls
#     Invoke-WindowsReAuth regardless of $Script:BrokerLockState; the
#     state is never short-circuited.
#   - Local Windows-user verification does NOT imply Graph workload
#     authorization. A 'Verified' verdict permits Cookbook to PROCEED
#     with the protected operation; the operation's own authorization
#     (e.g. Graph permissions on an AppRegistration) is independent.
#
# This module exposes:
#   Get-BrokerLockState                  -- {Locked|Unlocked} with lazy
#                                           inactivity-timeout sweep.
#   Get-BrokerLockStateSnapshot          -- {state, lastActivityUtc,
#                                           inactivityTimeoutMinutes,
#                                           remainingSeconds} payload
#                                           suitable for SPA JSON.
#   Set-BrokerLockUnlocked               -- mutates state to Unlocked
#                                           (callers MUST verify first).
#   Set-BrokerLockLocked                 -- explicit relock.
#   Update-BrokerLockActivity            -- bumps lastActivityUtc.
#   Test-BrokerLockOperationGated        -- predicate: does this OpClass
#                                           require fresh re-auth?
#   Test-BrokerLockRouteAllowedWhenLocked -- predicate: does this
#                                           route bypass the lock?
#   Invoke-BrokerLockReAuthForOp         -- the fresh re-auth gate
#                                           for protected operations.
#                                           Returns 'Verified' or the
#                                           non-Verified verdict; never
#                                           throws.

# ---------------------------------------------------------------------
# State
# ---------------------------------------------------------------------

# The lock state. 'Locked' is the boot state; the operator must unlock
# explicitly once. The value is intentionally a plain string (not an
# enum) to keep the wire shape and the in-memory shape identical -- the
# SPA-facing JSON payload is just { "state": "Locked" } or
# { "state": "Unlocked" }.
$Script:BrokerLockState = 'Locked'

# UTC timestamp of the last activity that counts toward the inactivity
# timeout. Updated by Update-BrokerLockActivity from the request
# dispatch loop and from successful Invoke-BrokerLockReAuthForOp calls.
# Polls of /broker/lock-state do NOT bump this -- otherwise the SPA
# would keep the broker unlocked indefinitely by polling.
$Script:BrokerLockLastActivityUtc = [datetime]::UtcNow

# Phase AG.C10 -- MONOTONIC anchor paired with $BrokerLockLastActivityUtc.
#
# The wall-clock timestamp above is HISTORICAL EVIDENCE authority (used
# for /broker/lock-state's lastActivityUtc field that the operator sees).
# The monotonic ticks below are ELAPSED-RUNTIME OPERATIONAL authority,
# used by the inactivity sweep to compute idle time WITHOUT being fooled
# by NTP step corrections, DST adjustments, manual clock changes, S4
# hibernate, or VM pauses.
#
# Both anchors are bumped together by Update-BrokerLockActivity and
# Set-BrokerLockUnlocked. They are NEVER bumped independently -- they
# are a single conceptual anchor with two clock projections.
#
# When the wall-vs-mono delta disagrees past the threshold defined in
# Get-BrokerTimeSkewSnapshot, the sweep re-locks the broker AND records
# the classified anomaly in $Script:BrokerLockTimeAnomaly so the next
# /broker/lock-state poll can surface it. Truthful re-lock > optimistic
# continuity forgiveness when auth freshness becomes ambiguous.
$Script:BrokerLockLastActivityMonoTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()

# Phase AG.C10 -- time-anomaly attribution surface.
#
# Set to a hashtable @{ kind; observedAtUtc; wallElapsedSec; monoElapsedSec;
# skewSec; anomalyThresholdSec } by Invoke-BrokerLockInactivitySweep when a
# time discontinuity caused the sweep to force a re-lock. Surfaced via
# Get-BrokerLockStateSnapshot so the operator sees WHY the broker re-locked.
# Cleared by Update-BrokerLockActivity and Set-BrokerLockUnlocked on the
# next successful operator activity (i.e. re-auth survives the anomaly).
# $null means "no time-anomaly attribution active" -- the normal case.
$Script:BrokerLockTimeAnomaly = $null

# Inactivity timeout. After this much idle time, the broker auto-locks.
# 15 minutes is the v1 default; the value is intentionally NOT operator-
# configurable in v1 (changing it requires a release).
$Script:BrokerLockInactivityTimeoutMinutes = 15

# OpClasses that require fresh re-auth even when the broker is unlocked.
# This list is the canonical enumeration; any new privileged route must
# be added here AND to the harness's contract scan.
$Script:BrokerLockReAuthOpClasses = @(
    'manualCook',         # POST /api/v1/cooks  (start a cook run on demand)
    'profileMutation',    # POST/PUT/DELETE on /api/v1/auth/profiles[/{id}]
    'secretBind',         # POST /api/v1/auth/profiles/{id}/secret
    'secretRemove',       # DELETE /api/v1/auth/profiles/{id}/secret
    'profileTest',        # POST /api/v1/auth/profiles/{id}/test
    'scheduleConfig',     # future scheduled-cook config mutation
    'updateApply'         # POST /api/v1/updates/apply
)

# Routes allowed when the broker is locked. These are the only HTTP
# surfaces the SPA can reach during a locked session; the dispatch
# loop returns 423 for everything else. Static asset GETs (HTML/JS/CSS)
# are matched by prefix outside this list, and the unauthenticated
# /api/v1/health route short-circuits before the lock middleware ever
# runs.
#
# Match shape: { Method = '<METHOD>|*'; PathPattern = '<regex>' }
# The PathPattern is anchored ^...$ by the matcher.
$Script:BrokerLockAllowedWhenLockedRoutes = @(
    @{ Method = 'GET';  PathPattern = '/api/v1/broker/lock-state' }
    @{ Method = 'POST'; PathPattern = '/api/v1/broker/unlock' }
    @{ Method = 'POST'; PathPattern = '/api/v1/broker/lock' }           # explicit lock (idempotent)
    # Browser-owned Windows Hello (WebAuthn) entry points. These MUST
    # be reachable while the broker is Locked because they ARE how
    # the operator unlocks via the platform authenticator from the
    # browser process.
    #
    # Steady-state path (a credential is already registered):
    #   status -> unlock-challenge -> unlock
    #
    # Bootstrap path (no credential yet -- the very first unlock on
    # a fresh workspace):
    #   status -> bootstrap-register-challenge -> bootstrap-register-unlock
    # The bootstrap path is LOCK-BYPASS specifically so the FIRST
    # unlock can be browser-owned, eliminating the terminal-flash /
    # wrong-monitor problem caused by the legacy broker-owned Hello
    # prompt. The security gate for bootstrap is the platform
    # authenticator's UV ceremony performed inside the browser
    # (proven by the UP+UV flags on the authData blob that the
    # bootstrap-register-unlock handler verifies) plus the
    # purpose-tagged single-use challenge -- NOT a prior Locked->
    # Unlocked transition. See Auth/WebAuthnVerify.ps1 doctrine and
    # Routes/BrokerWebAuthn.ps1 partition table.
    #
    # The non-bootstrap registration endpoints
    # (/webauthn/register-challenge, /webauthn/register) are
    # deliberately NOT listed here -- they handle operator-initiated
    # credential management (add a second browser, rotate a
    # credential) and that flow requires the broker to be already
    # Unlocked.
    @{ Method = 'GET';  PathPattern = '/api/v1/broker/webauthn/status' }
    @{ Method = 'POST'; PathPattern = '/api/v1/broker/webauthn/unlock-challenge' }
    @{ Method = 'POST'; PathPattern = '/api/v1/broker/webauthn/unlock' }
    @{ Method = 'POST'; PathPattern = '/api/v1/broker/webauthn/bootstrap-register-challenge' }
    @{ Method = 'POST'; PathPattern = '/api/v1/broker/webauthn/bootstrap-register-unlock' }
)

# Exception kind tag used by Invoke-BrokerLockReAuthForOp callers and
# the dispatch loop. We do not subclass [Exception] in PowerShell to
# avoid Add-Type churn; instead we throw a PSCustomObject wrapped in
# a string identifier the dispatch loop pattern-matches.
$Script:BrokerLockReAuthErrorTag = 'PAXCookbook.ReAuthRequired'

# ---------------------------------------------------------------------
# Inactivity sweep
# ---------------------------------------------------------------------

function Invoke-BrokerLockInactivitySweep {
    # Internal: if the broker is Unlocked, decide whether to re-lock.
    # Called as a lazy side-effect from Get-BrokerLockState; the broker
    # has no background timer thread, by design.
    #
    # Phase AG.C10 -- the sweep is now TIME-ANOMALY-AWARE. It re-locks
    # the broker on ANY of:
    #   (a) Normal idle timeout (monotonic-elapsed >= inactivity threshold).
    #       Monotonic is the authority here, NOT wall-clock, so a 4-hour
    #       laptop sleep does NOT silently elapse the 15-minute window
    #       without the operator's runtime actually idling that long.
    #   (b) wall_clock_rollback     -- wall moved backward materially. Auth
    #                                  freshness becomes ambiguous; re-lock.
    #   (c) sleep_or_pause_gap      -- monotonic stalled; OS itself was paused.
    #                                  We cannot tell what happened externally
    #                                  during the pause; re-lock.
    #   (d) wall_clock_forward_jump -- wall jumped forward without OS pausing.
    #                                  NTP step or manual clock change; we
    #                                  cannot reconcile, re-lock.
    #
    # All four conditions are "truthful refusal" -- if runtime continuity
    # becomes ambiguous in ANY direction, auth freshness becomes ambiguous,
    # and the correct security posture is to require fresh re-auth.
    #
    # When (b/c/d) trigger the re-lock, $Script:BrokerLockTimeAnomaly is
    # populated with the classified anomaly so Get-BrokerLockStateSnapshot
    # can surface it. Normal idle-timeout re-locks leave
    # $Script:BrokerLockTimeAnomaly $null -- they are not anomalies.
    if ($Script:BrokerLockState -ne 'Unlocked') { return }

    $skew = Get-BrokerTimeSkewSnapshot `
        -RefWallUtc $Script:BrokerLockLastActivityUtc `
        -RefMonoTicks $Script:BrokerLockLastActivityMonoTicks

    $monoIdleMinutes = $skew.monoElapsedSec / 60.0
    $forceRelockAnomaly = $null

    if ($skew.anomalyKind) {
        # Time-anomaly path: any non-null classification forces a re-lock
        # regardless of how long mono-elapsed claims to be. Capture the
        # classification verbatim for snapshot surfacing.
        $forceRelockAnomaly = @{
            kind                = $skew.anomalyKind
            observedAtUtc       = ([datetime]::UtcNow).ToString('o')
            wallElapsedSec      = [Math]::Round($skew.wallElapsedSec, 3)
            monoElapsedSec      = [Math]::Round($skew.monoElapsedSec, 3)
            skewSec             = [Math]::Round($skew.skewSec, 3)
            anomalyThresholdSec = $skew.anomalyThresholdSec
        }
    }

    if ($forceRelockAnomaly -or ($monoIdleMinutes -ge $Script:BrokerLockInactivityTimeoutMinutes)) {
        $Script:BrokerLockState = 'Locked'
        # We intentionally do NOT reset lastActivityUtc here -- it's
        # informational only once Locked. We DO record the anomaly when
        # one is present, so the next snapshot poll surfaces it.
        $Script:BrokerLockTimeAnomaly = $forceRelockAnomaly
    }
}

# ---------------------------------------------------------------------
# Public state API
# ---------------------------------------------------------------------

function Get-BrokerLockState {
    # Returns 'Locked' or 'Unlocked'. Always-correct: applies the
    # inactivity sweep as a lazy side-effect before reading.
    Invoke-BrokerLockInactivitySweep
    return $Script:BrokerLockState
}

function Get-BrokerLockStateSnapshot {
    # Snapshot suitable for JSON serialization on /broker/lock-state.
    # Includes the remaining-seconds countdown so the SPA can render a
    # "auto-lock in N min" hint without making policy decisions of its
    # own (the broker is authoritative).
    #
    # Phase AG.C10 -- inactivity countdown is now MONOTONIC-derived so
    # NTP corrections and wall-clock jumps don't visually skip the
    # countdown. The wall-clock lastActivityUtc is still surfaced for
    # operator-visible historical evidence ("you were last active at this
    # wall time"). When a time anomaly forced a re-lock, the optional
    # timeAnomaly field tells the operator WHY -- attribution, not
    # smoothing.
    Invoke-BrokerLockInactivitySweep
    $remaining = 0
    if ($Script:BrokerLockState -eq 'Unlocked') {
        $skew = Get-BrokerTimeSkewSnapshot `
            -RefWallUtc $Script:BrokerLockLastActivityUtc `
            -RefMonoTicks $Script:BrokerLockLastActivityMonoTicks
        $totalSec = $Script:BrokerLockInactivityTimeoutMinutes * 60
        $remaining = [int]([math]::Max(0, $totalSec - $skew.monoElapsedSec))
    }
    return [pscustomobject]@{
        state                      = $Script:BrokerLockState
        lastActivityUtc            = $Script:BrokerLockLastActivityUtc.ToString('o')
        inactivityTimeoutMinutes   = $Script:BrokerLockInactivityTimeoutMinutes
        inactivityRemainingSeconds = $remaining
        timeAnomaly                = $Script:BrokerLockTimeAnomaly
        # The SPA renders these enum values from /broker/lock-state into
        # the broker-status pill. The pill state 'locked' is added by
        # broker-status.js when state == 'Locked'. The timeAnomaly field
        # is $null on the happy path and a structured payload when the
        # last sweep classified a wall-vs-monotonic discontinuity. Phase
        # AG.C10 doctrine: surface it, never smooth it.
    }
}

function Update-BrokerLockActivity {
    # Bump lastActivityUtc to "now". The dispatch loop calls this on
    # every successful authenticated request that is NOT a poll of
    # /broker/lock-state. Re-auth wrappers call it after a Verified
    # verdict.
    #
    # Phase AG.C10 -- bumps BOTH the wall and monotonic anchors in a
    # single conceptual step. Clears $Script:BrokerLockTimeAnomaly
    # because successful operator activity proves auth freshness
    # survived whatever discontinuity the previous sweep may have
    # observed. The anomaly stays in /api/v1/health's recent-errors
    # only if the caller chose to record it there (we do NOT auto-
    # forward classifications to the error ring; that is the caller's
    # decision).
    $Script:BrokerLockLastActivityUtc        = [datetime]::UtcNow
    $Script:BrokerLockLastActivityMonoTicks  = [System.Diagnostics.Stopwatch]::GetTimestamp()
    $Script:BrokerLockTimeAnomaly            = $null
}

function Set-BrokerLockUnlocked {
    # Transition Locked -> Unlocked. Callers MUST have already obtained
    # a 'Verified' verdict from Invoke-WindowsReAuth; this function does
    # NOT independently verify (the verification is the route's
    # responsibility so that the broker can record which OpClass was
    # the unlock trigger).
    #
    # Phase AG.C10 -- bumps both wall and monotonic anchors, and clears
    # any previously-recorded time anomaly. A fresh re-auth proves the
    # operator is present right now; whatever the clock did during the
    # locked interval is no longer operationally relevant.
    $Script:BrokerLockState                  = 'Unlocked'
    $Script:BrokerLockLastActivityUtc        = [datetime]::UtcNow
    $Script:BrokerLockLastActivityMonoTicks  = [System.Diagnostics.Stopwatch]::GetTimestamp()
    $Script:BrokerLockTimeAnomaly            = $null
}

function Set-BrokerLockLocked {
    # Transition any state -> Locked. Idempotent. Callable from the
    # /api/v1/broker/lock route and from inside the broker's shutdown
    # path. Does NOT reset lastActivityUtc (informational).
    $Script:BrokerLockState = 'Locked'
}

# ---------------------------------------------------------------------
# Per-op gate
# ---------------------------------------------------------------------

function Test-BrokerLockOperationGated {
    # Returns $true if the named OpClass requires fresh re-auth even
    # when the broker is Unlocked. The list is the canonical Cookbook
    # enumeration (see $Script:BrokerLockReAuthOpClasses).
    param([Parameter(Mandatory)][string]$OpClass)
    return $Script:BrokerLockReAuthOpClasses -contains $OpClass
}

function Test-BrokerLockRouteAllowedWhenLocked {
    # Returns $true if the request can proceed while the broker is
    # locked (e.g. lock-state polls, unlock action, version probe).
    # Matched by method + path against the allowlist. Static asset
    # routes are handled separately by the dispatch loop's
    # static-handler short-circuit.
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path
    )
    foreach ($entry in $Script:BrokerLockAllowedWhenLockedRoutes) {
        if ($entry.Method -ne '*' -and $entry.Method -ne $Method) { continue }
        if ($Path -match ('^' + $entry.PathPattern + '$')) {
            return $true
        }
    }
    return $false
}

function Invoke-BrokerLockReAuthForOp {
    # The fresh re-auth gate. Pattern:
    #
    #   $verdict = Invoke-BrokerLockReAuthForOp `
    #       -OpClass 'manualCook' `
    #       -Message 'Verify to start a cook run for recipe "Weekly Refresh".'
    #   if ($verdict -ne 'Verified') {
    #       # The route emits HTTP 401 with code=reAuthRequired and
    #       # surfaces $verdict as verificationResult.
    #       return @{ status = 401; ... }
    #   }
    #
    # On 'Verified': bumps lock activity AND returns 'Verified'.
    # On any other verdict: does NOT mutate state, returns the verdict
    # string verbatim (one of DeviceNotPresent / NotConfiguredForUser /
    # DisabledByPolicy / DeviceBusy / RetriesExhausted / Canceled /
    # ComInteropFailure / Unknown).
    #
    # This function never throws on verification failure -- it returns
    # the verdict so the route handler can shape the HTTP response.
    # It may throw if -OpClass is unknown (a bug, not a runtime error).
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$OpClass,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not (Test-BrokerLockOperationGated -OpClass $OpClass)) {
        throw "Invoke-BrokerLockReAuthForOp: '$OpClass' is not a gated OpClass. Update `$Script:BrokerLockReAuthOpClasses if a new operation needs re-auth."
    }
    if ([string]::IsNullOrWhiteSpace($Message)) {
        throw "Invoke-BrokerLockReAuthForOp: -Message is required (operator must see WHY they are being prompted)."
    }
    $verdict = Invoke-WindowsReAuth -Message $Message
    if (Test-WindowsReAuthResultIsVerified -Result $verdict) {
        Update-BrokerLockActivity
        return 'Verified'
    }
    return $verdict
}

function New-BrokerLockReAuthRequiredResponse {
    # Helper used by route handlers to produce the standard 401
    # payload when a fresh re-auth fails. The wire shape is stable
    # across all gated operations so the SPA can route on it
    # uniformly.
    param(
        [Parameter(Mandatory)][string]$OpClass,
        [Parameter(Mandatory)][string]$VerificationResult,
        [string]$OperatorMessage = ''
    )
    if ([string]::IsNullOrWhiteSpace($OperatorMessage)) {
        $OperatorMessage = switch ($VerificationResult) {
            'Canceled'             { 'Verification was canceled. Please try the operation again.' }
            'NotConfiguredForUser' { 'Windows Hello / PIN is not configured for your account. Set it up in Windows Settings before performing this operation.' }
            'DisabledByPolicy'     { 'Windows Hello is disabled by policy on this machine. Contact your administrator.' }
            'DeviceNotPresent'     { 'No verification device is available. This appliance requires Windows Hello, PIN, or a fallback credential prompt.' }
            'DeviceBusy'           { 'The verification device is busy. Please try again in a moment.' }
            'RetriesExhausted'     { 'Too many failed verification attempts. Please wait and try again.' }
            'ComInteropFailure'    { 'Windows verification surface is unavailable. Restart the appliance and try again; if the problem persists, see TROUBLESHOOTING §13b.' }
            default                { 'Verification did not succeed. Please try the operation again.' }
        }
    }
    return @{
        status = 401
        body   = @{
            code               = 'reAuthRequired'
            opClass            = $OpClass
            verificationResult = $VerificationResult
            message            = $OperatorMessage
        }
    }
}

function New-BrokerLockLockedResponse {
    # Helper used by the dispatch-loop middleware when a request is
    # rejected because the broker is locked. The SPA's lock-overlay
    # component listens for this shape on every 423 response.
    param(
        [string]$AttemptedMethod = '',
        [string]$AttemptedPath   = ''
    )
    return @{
        status = 423
        body   = @{
            code            = 'brokerLocked'
            message         = 'The appliance is locked. Verify with Windows Hello / PIN to unlock.'
            attemptedMethod = $AttemptedMethod
            attemptedPath   = $AttemptedPath
        }
    }
}
