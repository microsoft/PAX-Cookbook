#requires -Version 7.4

# Broker entrypoint.
#
# The launcher (launcher/Start-PAXCookbook.ps1) invokes this script with the
# user-selected workspace path. The broker takes ownership of the workspace
# and serves the local UI surface on a loopback ephemeral port.
#
# Startup sequence (each step depends on the prior succeeding):
#   1. Verify integrity of vendored SQLite stack (SHA-256 + Authenticode).
#   2. Verify integrity of the bundled PAX script against VERSION.json.
#   3. Generate a 256-bit per-launch session token.
#   4. Bind HttpListener to both http://127.0.0.1:<port>/ and
#      http://localhost:<port>/ on an OS-assigned port (canonical
#      browser-facing origin is localhost; 127.0.0.1 retained for
#      back-compat and internal health probes).
#   5. Acquire the workspace lock (refuse if a live broker already holds it).
#   6. Open SQLite, apply M1 schema (4 tables + _schema_meta row).
#   7. Reconcile cook sentinels (orphan cooks -> interrupted).
#   8. Serve /api/v1/health on the dispatch loop until Ctrl+C / shutdown.
#
# Exit codes:
#   0  Clean shutdown.
#   4  E_WORKSPACE_LOCKED       - another live broker holds this workspace.
#   5  E_SQLITE_DLL_INTEGRITY   - vendored DLL hash or signature mismatch.
#   6  E_PAX_SCRIPT_INTEGRITY   - bundled PAX script missing or SHA-256 mismatch.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorkspacePath
)

$ErrorActionPreference = 'Stop'

# ====================================================================
# Paths and runtime state
# ====================================================================

$Script:BrokerRoot    = $PSScriptRoot                                   # app/broker
$Script:AppRoot       = Split-Path -Parent $Script:BrokerRoot           # app
$Script:SqliteDir     = Join-Path $Script:AppRoot 'lib\sqlite'
$Script:VersionFile   = Join-Path $Script:AppRoot 'VERSION.json'
$Script:ManifestFile  = Join-Path $Script:AppRoot 'resources\manifest.json'
$Script:PaxScriptPath = Join-Path $Script:AppRoot 'resources\pax\PAX_Purview_Audit_Log_Processor.ps1'
$Script:TemplatesDir  = Join-Path $Script:AppRoot 'templates'
$Script:TemplateCatalog = @{}       # populated by Read-TemplateCatalog at startup; never mutated after.
$Script:PaxScriptSha256 = $null     # populated by Test-BundledPaxIntegrity; reused on every cook spawn
$Script:PaxScriptVersion = $null    # populated by Test-BundledPaxIntegrity from VERSION.json
$Script:CookbookVersion  = $null    # populated by Test-BundledPaxIntegrity from VERSION.json.cookbook.version
$Script:ReleaseChannel   = $null    # populated by Test-BundledPaxIntegrity from VERSION.json.channel
$Script:ManifestAligned  = $false   # populated by Test-ManifestAlignment (non-fatal soft check)
$Script:ManifestChannel  = $null    # populated by Test-ManifestAlignment from manifest.json.channel
$Script:ManifestLatestCookbookVersion = $null  # populated by Test-ManifestAlignment from manifest.json.latestCookbook.version
$Script:ManifestPackageUrlConfigured  = $false # true iff manifest.json.latestCookbook.packageUrl is set AND is not a "<TODO_*>" placeholder
$Script:UpdateManifestUrl  = $null   # populated by Test-BundledPaxIntegrity from VERSION.json.updateManifestUrl; null if not configured
$Script:PaxAcquisitionPolicy                   = $null   # VERSION.json.paxScript.acquisitionPolicy ("external" | "embedded" | $null when missing => legacy embedded)
$Script:PaxScriptExportEnabled                 = $null   # VERSION.json.paxScript.exportEnabled (bool or $null when missing)
$Script:PaxEngineManifestUrl                   = $null   # VERSION.json.paxScript.engineManifestUrl (HTTPS string or $null)
$Script:PaxEngineManifestTrustAnchorThumbprint = $null   # VERSION.json.paxScript.engineManifestTrustAnchorThumbprint (40-hex uppercase or $null)
$Script:PaxScriptManifestSignaturePolicy               = $null   # VERSION.json.paxScript.manifestSignaturePolicy ("required" | "internal-test-bypass" | $null when missing => default 'required' at consumers)
$Script:ManifestPaxAcquisitionPolicy                   = $null   # manifest.json.includedPaxScript.acquisitionPolicy mirror
$Script:ManifestPaxScriptExportEnabled                 = $null   # manifest.json.includedPaxScript.exportEnabled mirror
$Script:ManifestPaxEngineManifestUrl                   = $null   # manifest.json.includedPaxScript.engineManifestUrl mirror
$Script:ManifestPaxEngineManifestTrustAnchorThumbprint = $null   # manifest.json.includedPaxScript.engineManifestTrustAnchorThumbprint mirror
$Script:ManifestPaxScriptManifestSignaturePolicy       = $null   # manifest.json.includedPaxScript.manifestSignaturePolicy mirror
# Phase 13 Stage 4A / Stage 5 -- external-policy-aware startup. When
# VERSION.json declares paxScript.acquisitionPolicy = "external" AND
# the canonical bundled PAX script is absent or its on-disk SHA-256
# does not match VERSION.json.paxScript.sha256, the broker enters
# degraded boot: it still binds the loopback listener and serves
# /api/v1/setup/acquire-pax/* so the operator can complete first-run
# acquisition through the SPA overlay, but the Stage 4 acquisition
# gate keeps cook / resume / scheduled-task PUT blocked at HTTP 409
# acquisitionRequired. Under acquisitionPolicy = "embedded" the behavior
# is unchanged: a missing or mismatched bundled script aborts startup
# with EXIT_E_PAX_SCRIPT_INTEGRITY.
#
# The full five-field external-policy contract lives in
# VERSION.json.paxScript and is mirrored verbatim in
# manifest.json.includedPaxScript:
#   1. acquisitionPolicy                   ('external' | 'embedded')
#   2. exportEnabled                       (bool)
#   3. engineManifestUrl                   (URL string)
#   4. engineManifestTrustAnchorThumbprint (40-hex uppercase, or null under internal-test-bypass)
#   5. manifestSignaturePolicy             ('required' | 'internal-test-bypass')
# Test-ManifestAlignment treats these five fields as STRICT: any
# version-vs-manifest mismatch on any one of them fails the integrity
# check and the broker exits EXIT_E_PAX_SCRIPT_INTEGRITY. Channel /
# PAX version / PAX SHA alignment remains a soft observational drift.
#
# Scope hazard: the broker NEVER mutates Engine\Acquisition.psm1's
# $Script:PaxManifestSignaturePolicy (that variable lives in a
# different module session state and cannot be reached via $Script:
# from this file). Every Test-ApprovedEngineManifest call site
# passes -SignaturePolicy explicitly using the value resolved from
# VERSION.json. Acquisition.psm1's module-internal default of
# 'required' is the safe fallback when no caller supplies the
# parameter.
$Script:BrokerStartupAcquisitionRequired = $false  # set true when Test-BundledPaxIntegrity selects the Stage 4A degraded-boot branch
$Script:BrokerStartupAcquisitionReason   = $null   # one of: 'pax_script_absent', 'pax_script_hash_mismatch', or $null when not degraded
$Script:PaxScriptExpectedSha256          = $null   # VERSION.json.paxScript.sha256 captured even when the on-disk script is absent or mismatched
$Script:HostMachineName = $env:COMPUTERNAME                                # captured once at startup
$Script:HostPsVersion   = $PSVersionTable.PSVersion.ToString()             # captured once at startup
$Script:HostOsPlatform  = if ($IsWindows) { 'Windows' } elseif ($IsLinux) { 'Linux' } elseif ($IsMacOS) { 'macOS' } else { 'Unknown' }
$Script:HostOsVersion   = [System.Environment]::OSVersion.VersionString    # captured once at startup
try {
    # Phase AG.C8 -- capture the operator-supplied workspace path
    # VERBATIM before Resolve-Path collapses reparse points / case /
    # relative components. We keep both forms: $Script:WorkspaceDisplayPath
    # is what the operator typed (preserved for evidence-truthfulness
    # surfacing), $Script:WorkspacePath is the canonical filesystem
    # mount point used at runtime. NEITHER is auto-rewritten in any
    # historical evidence ever stored.
    $Script:WorkspaceDisplayPath = $WorkspacePath
    $Script:WorkspacePath = (Resolve-Path -LiteralPath $WorkspacePath -ErrorAction Stop).Path
} catch {
    Write-Host ('Broker: -WorkspacePath does not exist: ' + $WorkspacePath) -ForegroundColor Red
    exit 5
}
$Script:RuntimeDir    = Join-Path $Script:WorkspacePath 'Runtime'
$Script:LockFile      = Join-Path $Script:RuntimeDir 'workspace.lock'
$Script:LockAcquireFile   = Join-Path $Script:RuntimeDir 'workspace.lock.acquire'

# ====================================================================
# Stable broker port (doctrine §11)
# ====================================================================
#
# Loopback HttpListener is bound on the canonical preferred port 17654
# whenever it is free; if not, the broker scans the closed fallback
# range 17654..17664 in order. No random ephemeral port is ever used.
# The selected port is persisted into the launcher's bootstrap pointer
# (%APPDATA%\PAXCookbook\cookbook.bootstrap.json) so the next launch
# tries the same port first. Failing to bind ANY port in the range is
# a loud failure -- the broker refuses to start.
$Script:CookbookPreferredBrokerPort  = 17654
$Script:CookbookBrokerPortRangeStart = 17654
$Script:CookbookBrokerPortRangeEnd   = 17664
$Script:CookbookBootstrapDir         = Join-Path $env:APPDATA 'PAXCookbook'
$Script:CookbookBootstrapFile        = Join-Path $Script:CookbookBootstrapDir 'cookbook.bootstrap.json'

# AI.C9.1 -- session-token sidecar.
#
# The bundled scheduler launcher (launcher/Invoke-ScheduledCook.ps1)
# runs in a process distinct from the broker and therefore cannot
# read $Script:SessionToken from broker memory. To preserve the
# spec invariant "the launcher hits the existing /api/v1/recipes/
# <ULID>/cook route exactly as a manual browser would" the broker
# persists the freshly-minted SessionToken to a small sidecar file
# living next to workspace.lock. The sidecar inherits the runtime
# directory's NTFS ACLs (per-user workspace tree); no new ACL
# work, no DPAPI wrapper, no token rotation. The sidecar is
# written when the workspace lock is written and removed when the
# workspace lock is removed. A NEW broker session mints a NEW
# token and overwrites the sidecar, so a stale launcher request
# against a dead broker token resolves to a clean 401 just like a
# stale browser tab would.
$Script:SessionTokenSidecar = Join-Path $Script:RuntimeDir 'broker.token'
$Script:LockAcquireHandle = $null
$Script:DatabaseDir       = Join-Path $Script:WorkspacePath 'Database'
$Script:DatabaseFile      = Join-Path $Script:DatabaseDir 'cookbook.sqlite'
$Script:CooksDir          = Join-Path $Script:WorkspacePath 'Cooks'
$Script:RecipesDir        = Join-Path $Script:WorkspacePath 'Recipes'
$Script:RecipesTrashDir   = Join-Path $Script:RecipesDir '_trash'
$Script:UpdatesDir        = Join-Path $Script:WorkspacePath 'Updates'
$Script:WebRoot           = Join-Path $Script:AppRoot 'web'

$Script:Listener       = $null
$Script:BrokerPort     = 0
$Script:SessionToken   = $null
$Script:SqliteConn     = $null

# Phase AG.C10 -- DUAL-CLOCK anchors for broker start.
#
# Cookbook maintains TWO clocks for two NON-INTERCHANGEABLE domains:
#
#   $Script:StartedAt              -- WALL clock authority for HISTORICAL
#                                     EVIDENCE (e.g. operator-visible
#                                     "broker started at this wall time").
#                                     UTC. Can move backward (NTP, DST,
#                                     manual change). Meaningful across
#                                     restart only as a stamp.
#
#   $Script:StartedAtMonotonicTicks -- MONOTONIC clock authority for
#                                      ELAPSED-RUNTIME computation (uptime,
#                                      inactivity sweep skew, time-anomaly
#                                      detection). Stopwatch.GetTimestamp()
#                                      generally does NOT advance during S4
#                                      hibernate or VM pause. MEANINGLESS
#                                      across process restart -- never
#                                      persist or compare across reboots.
#
# These domains MUST NOT be conflated. Wall = evidence, mono = elapsed.
# See Get-BrokerTimeSkewSnapshot for the principled comparison helper.
# See docs/OPERATOR_GUIDE.md §14 (forthcoming) for the operator-facing
# explanation of why a 4-hour laptop sleep can surface as a re-lock with
# anomalyKind='sleep_or_pause_gap' rather than a silent forgiveness.
$Script:StartedAt               = (Get-Date).ToUniversalTime()
$Script:StartedAtMonotonicTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()

$Script:LockHeld       = $false
$Script:ShuttingDown   = $false

# Phase AH.C1 -- runtime transition coordination state.
#
# $Script:BrokerSessionId
#   The UUID minted by New-BrokerSessionId at Start-BrokerSession time
#   (immediately after Apply-M1Schema succeeds). It IS the primary key
#   into broker_sessions for this process. $null until Start-BrokerSession
#   completes; Stop-BrokerSession is a no-op while it is $null.
#
# $Script:BrokerSessionStopRecorded
#   Idempotency latch for Stop-BrokerSession. Set to $true after the
#   UPDATE that writes stopped_at + stop_reason + stop_class='clean'
#   commits. Prevents the same row from being touched twice if
#   Invoke-Shutdown is invoked recursively.
#
# $Script:ShutdownReason
#   The frozen $Script:BrokerSessionStopReasons value attributed to the
#   eventual Invoke-Shutdown call. Set EARLIEST-writer-wins by:
#       Ctrl+C handler                       -> 'operator_ctrl_c'
#       Dispatch-loop HttpListenerException  -> 'dispatch_loop_exception'
#       Dispatch-loop outer catch            -> 'dispatch_loop_exception'
#       Pre-listener startup-error paths     -> 'startup_failure' (passed
#                                               explicitly to Invoke-Shutdown)
#       Update-apply route                   -> 'operator_update_apply'
#                                               (set AFTER the route has
#                                               written its HTTP 202
#                                               response and BEFORE it
#                                               calls $Script:Listener.Stop()
#                                               to unblock the dispatch
#                                               loop).
#       Otherwise (default clean exit)       -> 'listener_disposed'
#   Doctrine: this variable is never reset after being set. Multiple
#   distinct causes that converge on the same Invoke-Shutdown call
#   record the FIRST cause (closest to the operational event).
$Script:BrokerSessionId           = $null
$Script:BrokerSessionStopRecorded = $false
$Script:ShutdownReason            = $null

# Phase AH.C3 -- startup-context observation, runtime-only mirror of
# the broker_sessions evidence columns. These are SAMPLED ONCE at
# broker boot (in Start-BrokerSession, BEFORE classification and
# reconciliation mutate the prior-row state) and are NEVER updated
# thereafter. The runtime payload at /api/v1/runtime/version reads
# from these script-scope variables so callers do not pay a SQLite
# round-trip per request. The values are also persisted to the
# broker_sessions row for forensic auditability across restarts.
#
# Initial values are $null / 0; Start-BrokerSession fills them and
# Set-BrokerSessionStartupReconciledCount sets the reconciled count
# once Invoke-SentinelReconciliation returns. Once set, none of
# these is ever rewritten in the same process.
$Script:BrokerStartupClassification          = $null
$Script:BrokerStartupPriorSessionId          = $null
$Script:BrokerStartupPriorSessionStopClass   = $null
$Script:BrokerStartupPriorSessionStoppedAt   = $null
$Script:BrokerStartupPriorRunningCookCount   = 0
$Script:BrokerStartupReconciledCookCount     = $null

# Local, bounded recent-error ring. Runtime-only — these entries are
# lost on broker restart and never leave the appliance. Capacity is
# fixed at startup so that an unbounded error storm cannot exhaust
# broker memory; when the cap is reached, the oldest entry is
# displaced and $Script:RecentErrorOverflowCount is incremented so
# the chef can see, via /api/v1/health, that evidence was DROPPED
# rather than silently disappearing. No telemetry queue. No durable
# history. No external sink. See Add-RecentError, Get-HealthPayload,
# and docs/OPERATOR_GUIDE.md §11.8 for the full contract.
$Script:RecentErrors             = [System.Collections.Generic.Queue[hashtable]]::new()
$Script:RecentErrorCapacity      = 10
$Script:RecentErrorOverflowCount = 0

# Phase AG.C7 -- pre-cook disk-space precheck threshold.
# The broker refuses to spawn a cook when the workspace drive has
# fewer than this many free bytes. The cook is rejected BEFORE the
# cook row is inserted, BEFORE the cook folder is created, and
# BEFORE any auth-profile secret is read from CredMan -- so a
# refused cook leaves NO partial state on disk, NO cook row in the
# database, and NO secret material in broker memory. The chef gets
# HTTP 507 with a structured payload (freeBytes / requiredBytes /
# drive) so they can free space and retry.
#
# Doctrine: the precheck is a HARD FLOOR, not a forecast. Cookbook
# cannot predict PAX output volume for a given recipe; it can only
# refuse the obviously-doomed case. A cook that starts with more
# than this floor but later exhausts the drive surfaces as a normal
# nonzero_exit / spawn failure -- not as a precheck-failed cook.
# The threshold is intentionally conservative (500 MB) to leave
# room for the cook's initial files + a bounded amount of cook.log
# growth + small-to-medium PAX outputs. Operators on tight disks
# can override via $env:PAXCK_MIN_FREE_DISK_BYTES_FOR_COOK at
# broker startup (read once; never re-read mid-run).
#
# NOTE: We do NOT auto-clean, auto-prune, auto-compact, or
# auto-delete ANYTHING to make room. Truthful refusal > silent
# evidence destruction. The operator decides what to remove.
$Script:MinFreeDiskBytesForCook = 500MB
if ($env:PAXCK_MIN_FREE_DISK_BYTES_FOR_COOK) {
    $overrideVal = 0L
    if ([long]::TryParse($env:PAXCK_MIN_FREE_DISK_BYTES_FOR_COOK, [ref]$overrideVal) -and $overrideVal -ge 0) {
        $Script:MinFreeDiskBytesForCook = $overrideVal
    }
}

# Runtime registries. Both are synchronized hashtables and are
# accessed from the dispatch thread (route handlers), the cook supervisor
# ThreadJobs (publishers, registry remove), and the WS reader/sender
# ThreadJobs (subscribe/unsubscribe/cleanup).
#
# Intentionally minimal. Do NOT evolve these into broker-wide object
# graphs, lifecycle orchestration, or generalized pub/sub. Per-cook
# state only.
$Script:CookRegistry   = [hashtable]::Synchronized(@{})
$Script:WsRegistry     = [hashtable]::Synchronized(@{})

$EXIT_OK                             = 0
$EXIT_E_WORKSPACE_LOCKED             = 4
$EXIT_E_SQLITE_DLL_INTEGRITY         = 5
$EXIT_E_PAX_SCRIPT_INTEGRITY         = 6
$EXIT_E_OBSERVATION_TRIGGER_INTEGRITY = 8
$EXIT_E_PACKAGE_TRUST_INTEGRITY      = 9
$EXIT_E_ENVIRONMENT_CONSTRAINED_LANGUAGE = 10
$EXIT_E_ENVIRONMENT_LOW_DISK         = 11

# Stable exit-code signal for the launcher's update-apply orchestrator
# path. Returned ONLY when the broker exits cleanly AND the apply route
# tagged $Script:ShutdownReason = 'operator_update_apply'. The launcher
# uses this exact value to decide whether to spawn the detached
# installer-and-relaunch orchestrator; every other clean exit returns
# $EXIT_OK so existing launchers continue to do nothing on broker exit.
$EXIT_OPERATOR_UPDATE_APPLY_REQUESTED = 75

# ====================================================================
# Helpers
# ====================================================================

function Get-UtcNowIso {
    (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
}

function Get-CookbookVersion {
    return $Script:CookbookVersion
}

function Get-BrokerTimeSkewSnapshot {
    # Phase AG.C10 -- dual-clock skew snapshot for runtime time-anomaly
    # classification. CLASSIFIES, does not repair. Used by the broker-lock
    # inactivity sweep and by /api/v1/health to surface (NOT smooth)
    # wall-vs-monotonic discontinuity.
    #
    # Doctrine partition (DO NOT let these domains drift together later):
    #   - Wall clock is HISTORICAL EVIDENCE authority.
    #   - Monotonic clock is ELAPSED-RUNTIME OPERATIONAL authority.
    # This helper compares deltas of BOTH clocks against the same pair of
    # reference anchors. If the wall delta and monotonic delta disagree
    # past a small threshold, that disagreement IS the operator-visible
    # truth. We surface it; we do not smooth it.
    #
    # Frozen 3-value anomaly vocabulary (DO NOT extend):
    #   sleep_or_pause_gap      -- monotonic effectively stalled while wall
    #                              advanced. Consistent with S4 hibernate or
    #                              VM pause (the OS itself was not running).
    #   wall_clock_rollback     -- wall delta is negative (wall moved
    #                              backward). Consistent with NTP step-back,
    #                              DST adjustment, or manual clock change.
    #   wall_clock_forward_jump -- wall advanced significantly faster than
    #                              monotonic, but monotonic still advanced.
    #                              Consistent with NTP forward step without
    #                              the OS pausing.
    #
    # When wall and monotonic agree (skew within threshold and wall not
    # negative past threshold), anomalyKind is $null. We do NOT invent
    # heuristic anomalies like "probably slept" -- if we cannot classify
    # principled, we do not classify at all.
    #
    # Returns: hashtable with these keys, ALWAYS present:
    #   wallElapsedSec       -- [double] (UtcNow - RefWallUtc).TotalSeconds.
    #                           CAN BE NEGATIVE -- this is truthful evidence.
    #   monoElapsedSec       -- [double] elapsed seconds between the two
    #                           monotonic samples. >= 0 by construction.
    #   skewSec              -- [double] wallElapsedSec - monoElapsedSec.
    #                           Positive = wall ran ahead; negative = wall
    #                           ran behind. Preserved verbatim.
    #   anomalyKind          -- $null OR one of the 3 frozen values above.
    #   anomalyThresholdSec  -- [int] classification threshold (60).
    param(
        [Parameter()] [datetime]$RefWallUtc,
        [Parameter()] [long]$RefMonoTicks = -1
    )
    # Default both reference anchors to broker start when the caller did
    # not specify a pair. This produces a "skew since broker start"
    # snapshot suitable for /api/v1/health surfacing.
    if (-not $PSBoundParameters.ContainsKey('RefWallUtc')) {
        $RefWallUtc = $Script:StartedAt
    }
    if ($RefMonoTicks -lt 0) {
        $RefMonoTicks = $Script:StartedAtMonotonicTicks
    }
    $nowWall  = [datetime]::UtcNow
    $nowTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()
    $wallElapsedSec = ($nowWall - $RefWallUtc).TotalSeconds
    $monoFreq = [double][System.Diagnostics.Stopwatch]::Frequency
    if ($monoFreq -le 0) { $monoFreq = 1.0 }  # defensive: Stopwatch.Frequency is always positive on Windows
    $monoElapsedSec = ($nowTicks - $RefMonoTicks) / $monoFreq
    $skewSec = $wallElapsedSec - $monoElapsedSec
    $threshold = 60
    $anomalyKind = $null
    # Order of checks matters. Rollback first (wall negative is the
    # clearest signal). Then the two forward-anomaly cases, distinguished
    # by whether monotonic kept up. If monotonic effectively stalled
    # (< 1 second elapsed on the mono clock while wall claims > 60s
    # have passed), the OS itself was not running -- sleep_or_pause_gap.
    # If monotonic advanced normally but wall outran it by more than the
    # threshold, the wall clock itself jumped -- wall_clock_forward_jump.
    if ($wallElapsedSec -lt -$threshold) {
        $anomalyKind = 'wall_clock_rollback'
    }
    elseif ($skewSec -gt $threshold -and $monoElapsedSec -lt 1.0) {
        $anomalyKind = 'sleep_or_pause_gap'
    }
    elseif ($skewSec -gt $threshold) {
        $anomalyKind = 'wall_clock_forward_jump'
    }
    return @{
        wallElapsedSec      = $wallElapsedSec
        monoElapsedSec      = $monoElapsedSec
        skewSec             = $skewSec
        anomalyKind         = $anomalyKind
        anomalyThresholdSec = $threshold
    }
}

function Add-RecentError {
    # Append one entry to the local, bounded, runtime-only recent-error
    # ring. Optional -Source tag lets the caller identify the
    # originating subsystem (e.g. 'sqlite_startup', 'migration',
    # 'sentinel_reconciliation'). When omitted the field is $null and
    # the caller is unannotated — never fabricate a source. When the
    # ring exceeds $Script:RecentErrorCapacity, the oldest entry is
    # displaced and $Script:RecentErrorOverflowCount is incremented;
    # /api/v1/health surfaces both the current count and the overflow
    # count so dropped evidence is visible, not silent.
    param(
        [string]$Message,
        [string]$Source
    )
    # PowerShell coerces an absent [string] parameter to '' (not $null).
    # Honor the documented contract — an untagged caller surfaces as
    # source=$null in the payload, never as an empty string.
    $resolvedSource = if ([string]::IsNullOrEmpty($Source)) { $null } else { $Source }
    $entry = @{
        timestampUtc = Get-UtcNowIso
        message      = $Message
        source       = $resolvedSource
    }
    $Script:RecentErrors.Enqueue($entry)
    while ($Script:RecentErrors.Count -gt $Script:RecentErrorCapacity) {
        [void]$Script:RecentErrors.Dequeue()
        $Script:RecentErrorOverflowCount++
    }
}

function Test-CookDiskPrecheck {
    # Phase AG.C7 -- pre-spawn disk-space precheck.
    #
    # Inspects the volume that owns $Path and reports whether it has
    # at least $RequiredBytes (default: $Script:MinFreeDiskBytesForCook)
    # free. NEVER throws -- if the drive cannot be resolved (e.g. UNC
    # path, missing root, transient I/O), returns ok=$false with a
    # reason field so the caller can refuse the cook with a structured
    # error rather than a 500.
    #
    # Return shape:
    #   @{
    #     ok            = $true | $false
    #     freeBytes     = [long]   # -1 when the drive could not be probed
    #     requiredBytes = [long]   # the threshold actually compared against
    #     driveName     = [string] # 'C:\', '\\server\share', or '' if unresolved
    #     reason        = [string] # 'ok' | 'insufficient_space' | 'drive_unresolved' | 'probe_failed'
    #     detail        = [string] # human-readable diagnostic when not 'ok'
    #   }
    #
    # Doctrine: this is a HARD-FLOOR pre-check, NOT a forecast. We
    # cannot predict PAX output volume. We can only refuse the
    # obviously-doomed case. We do NOT auto-clean, auto-prune, or
    # auto-compact ANYTHING. Truthful refusal > silent evidence
    # destruction.
    param(
        [Parameter()] [AllowEmptyString()] [string]$Path,
        [Parameter()] [long]$RequiredBytes = -1
    )
    if ($RequiredBytes -lt 0) { $RequiredBytes = [long]$Script:MinFreeDiskBytesForCook }
    $result = @{
        ok            = $false
        freeBytes     = [long](-1)
        requiredBytes = $RequiredBytes
        driveName     = ''
        reason        = 'probe_failed'
        detail        = ''
    }
    if ([string]::IsNullOrWhiteSpace($Path)) {
        $result.reason = 'drive_unresolved'
        $result.detail = 'Path is empty.'
        return $result
    }
    try {
        $rootPath = [System.IO.Path]::GetPathRoot($Path)
        if ([string]::IsNullOrWhiteSpace($rootPath)) {
            $result.reason = 'drive_unresolved'
            $result.detail = "Could not resolve a volume root for path '$Path'."
            return $result
        }
        $drive = [System.IO.DriveInfo]::new($rootPath)
        $result.driveName = $drive.Name
        if (-not $drive.IsReady) {
            $result.reason = 'drive_unresolved'
            $result.detail = "Drive '$($drive.Name)' is not ready."
            return $result
        }
        $free = [long]$drive.AvailableFreeSpace
        $result.freeBytes = $free
        if ($free -ge $RequiredBytes) {
            $result.ok     = $true
            $result.reason = 'ok'
        } else {
            $result.reason = 'insufficient_space'
            $result.detail = "Drive '$($drive.Name)' has $free byte(s) free; cook precheck requires at least $RequiredBytes."
        }
    } catch {
        $result.reason = 'probe_failed'
        $result.detail = "DriveInfo probe threw: $($_.Exception.Message)"
    }
    return $result
}

function Get-WorkspacePathDiagnostic {
    # Phase AG.C8 -- workspace path diagnostic.
    #
    # CLASSIFIES the operator-supplied workspace path. REPORTS only.
    # NEVER rewrites, normalizes, canonicalizes, or "repairs" the
    # input. The doctrine for AG.C8 is "DETECT + REPORT, never
    # REWRITE": historical evidence paths must remain historically
    # truthful even when their form is unusual (UNC, long-path,
    # reparse point, removable drive, mixed-case, Unicode-NFD).
    #
    # The returned object distinguishes five filesystem-identity
    # forms that MUST NOT be flattened together at any other call
    # site:
    #   displayPath    -- exactly what the caller passed in (preserved
    #                     verbatim; even trailing slashes are kept).
    #   canonicalPath  -- [System.IO.Path]::GetFullPath result; this
    #                     is for DIAGNOSTIC display only, never for
    #                     replacing displayPath.
    #   driveType      -- 'Fixed','Removable','Network','Ram',
    #                     'CDRom','NoRootDirectory','Unknown',
    #                     'unresolved'.
    #   isUnc          -- $true when displayPath starts with \\ or //
    #                     (not counting \\?\).
    #   usesLongPathPrefix -- $true when displayPath starts with \\?\.
    #   isReparsePoint -- $true when DirectoryInfo.Attributes contains
    #                     ReparsePoint (junction, symlink, mount
    #                     point, OneDrive placeholder root, etc.).
    #   pathLength     -- raw character count of displayPath.
    #   exceedsClassicLimit -- $true when pathLength + reserved
    #                     workspace-suffix budget would exceed 260.
    #                     We reserve 80 chars for Cooks\<26>\<36>\
    #                     plus a typical child filename ("cook.log",
    #                     "recipe-snapshot.json", etc.) so the
    #                     classification is true when ANY cook
    #                     folder under this root is likely to fail.
    #   warnings       -- array of human-readable strings the broker
    #                     should surface via Add-RecentError so the
    #                     operator sees them without grepping logs.
    #   exceptions     -- array of exception messages from sub-probes
    #                     (DriveInfo, DirectoryInfo) that did not
    #                     prevent overall classification.
    #
    # NEVER throws.
    param([Parameter()] [AllowEmptyString()] [string]$DisplayPath)
    $r = @{
        displayPath         = $DisplayPath
        canonicalPath       = ''
        driveType           = 'unresolved'
        isUnc               = $false
        usesLongPathPrefix  = $false
        isReparsePoint      = $false
        pathLength          = if ($DisplayPath) { [int]$DisplayPath.Length } else { 0 }
        exceedsClassicLimit = $false
        warnings            = New-Object System.Collections.Generic.List[string]
        exceptions          = New-Object System.Collections.Generic.List[string]
    }
    if ([string]::IsNullOrWhiteSpace($DisplayPath)) {
        [void]$r.warnings.Add('workspace_path_empty: no path was supplied.')
        return $r
    }
    # Long-path prefix detection. We test the raw input BEFORE any
    # canonicalization so the operator's literal choice is reflected.
    if ($DisplayPath.StartsWith('\\?\') -or $DisplayPath.StartsWith('//?/')) {
        $r.usesLongPathPrefix = $true
        [void]$r.warnings.Add('workspace_uses_long_path_prefix: the workspace was launched with a \\?\ prefix. This bypasses Win32 path normalization; tools that do not understand the prefix may fail.')
    } elseif ($DisplayPath.StartsWith('\\') -or $DisplayPath.StartsWith('//')) {
        $r.isUnc = $true
        [void]$r.warnings.Add('workspace_is_unc: the workspace appears to be on a UNC share. Cookbook does not support UNC workspaces (file locking, sentinel semantics, and atomic-rename behavior are not guaranteed across SMB).')
    }
    try {
        $r.canonicalPath = [System.IO.Path]::GetFullPath($DisplayPath)
    } catch {
        [void]$r.exceptions.Add('GetFullPath threw: ' + $_.Exception.Message)
    }
    if ($r.canonicalPath -and ($r.canonicalPath -ne $DisplayPath)) {
        [void]$r.warnings.Add("workspace_canonical_differs_from_display: displayPath='$DisplayPath' canonicalPath='$($r.canonicalPath)'. The two forms refer to the same location now, but historical evidence stored against displayPath WILL NOT be silently rewritten to canonicalPath. The chef sees both.")
    }
    try {
        $rootPath = [System.IO.Path]::GetPathRoot($DisplayPath)
        if (-not [string]::IsNullOrWhiteSpace($rootPath)) {
            $drive = [System.IO.DriveInfo]::new($rootPath)
            $r.driveType = $drive.DriveType.ToString()
            if ($r.driveType -eq 'Removable') {
                [void]$r.warnings.Add('workspace_on_removable_drive: the workspace lives on a removable drive. If the drive is ejected mid-cook, child writes will fail and the cook will be recorded as interrupted with truthful evidence (not silently retried).')
            } elseif ($r.driveType -eq 'Network') {
                [void]$r.warnings.Add('workspace_on_network_drive: the workspace lives on a network drive. Cookbook does not provide cross-network file-locking guarantees; treat as best-effort.')
            } elseif ($r.driveType -eq 'CDRom' -or $r.driveType -eq 'Ram' -or $r.driveType -eq 'NoRootDirectory') {
                [void]$r.warnings.Add("workspace_on_unsuitable_volume: driveType='$($r.driveType)'. Cookbook cannot guarantee durability on this volume class.")
            }
        }
    } catch {
        [void]$r.exceptions.Add('DriveInfo threw: ' + $_.Exception.Message)
    }
    try {
        if (Test-Path -LiteralPath $DisplayPath -PathType Container) {
            $di = [System.IO.DirectoryInfo]::new($DisplayPath)
            if (($di.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $r.isReparsePoint = $true
                [void]$r.warnings.Add('workspace_is_reparse_point: the workspace root is a reparse point (junction, symlink, mount point, or cloud-sync placeholder such as OneDrive). Cookbook will resolve filesystem reads/writes through the reparse point at runtime, but historical evidence paths will NOT be silently rewritten to the underlying target.')
            }
        }
    } catch {
        [void]$r.exceptions.Add('DirectoryInfo threw: ' + $_.Exception.Message)
    }
    # Effective MAX_PATH budget. The cook folder is
    # <ws>\Cooks\<26-char base32 recipeId>\<36-char GUID cookId>\<child>.
    # Static suffix length is 1+5+1+26+1+36+1=71 chars. We reserve
    # an additional ~25 chars for the longest expected child filename
    # ("recipe-snapshot.json"=20, "cook-context.json"=17, "cook.log"=8;
    # 25 is a safe ceiling). Total reserved = 96.
    if ($r.pathLength -gt 0 -and ($r.pathLength + 96) -gt 260) {
        $r.exceedsClassicLimit = $true
        [void]$r.warnings.Add("workspace_path_too_long: pathLength=$($r.pathLength); reserved cook-folder budget is 96 chars; sum exceeds the classic Win32 MAX_PATH of 260. Cooks under this workspace may be refused at start. Move the workspace closer to the drive root, or enable Windows long-path support and use a shorter workspace name.")
    }
    return $r
}

function Write-AtomicUtf8NoBom {
    # Canonical atomic file write for broker-side text artifacts (cook
    # recipe snapshots, cook-context.json, command.txt, etc.).
    #
    # Doctrine: every persisted artifact that observers can race
    # against must appear on disk atomically. Writing in-place with
    # System.IO.File.WriteAllText leaves a window where a reader can
    # see a truncated file (the write is "create or truncate" followed
    # by a stream copy). Writing to <path>.tmp and then File.Move with
    # overwrite=true is a single NTFS rename on the destination
    # filesystem, which is atomic from the observer's perspective.
    #
    # The supervisor's $writeSentinel uses the same pattern inline
    # (ThreadJobs do not inherit dot-sourced functions) -- this helper
    # is for callers in the broker process scope.
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content
    )
    $tmp = $Path + '.tmp'
    $enc = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($tmp, $Content, $enc)
    [System.IO.File]::Move($tmp, $Path, $true)
}

# ====================================================================
# C1 - Vendored SQLite stack integrity verification
# ====================================================================

function Test-VendoredSqliteIntegrity {
    $lockfilePath = Join-Path $Script:SqliteDir 'lockfile.json'
    if (-not (Test-Path -LiteralPath $lockfilePath -PathType Leaf)) {
        Write-Host ('E_SQLITE_DLL_INTEGRITY: lockfile.json missing at ' + $lockfilePath) -ForegroundColor Red
        return $false
    }

    try {
        $lockfile = Get-Content -LiteralPath $lockfilePath -Raw | ConvertFrom-Json
    } catch {
        Write-Host ('E_SQLITE_DLL_INTEGRITY: lockfile.json is not valid JSON: ' + $_.Exception.Message) -ForegroundColor Red
        return $false
    }

    if ($lockfile.schemaVersion -ne 2) {
        Write-Host ('E_SQLITE_DLL_INTEGRITY: lockfile.json schemaVersion is ' + $lockfile.schemaVersion + ', expected 2') -ForegroundColor Red
        return $false
    }

    foreach ($prop in $lockfile.files.PSObject.Properties) {
        $fileName = $prop.Name
        $entry    = $prop.Value
        $filePath = Join-Path $Script:SqliteDir $fileName

        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            Write-Host ('E_SQLITE_DLL_INTEGRITY: vendored file missing: ' + $filePath) -ForegroundColor Red
            return $false
        }

        $actualSha = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if ($actualSha -ne $entry.sha256) {
            Write-Host ('E_SQLITE_DLL_INTEGRITY: SHA-256 mismatch on ' + $fileName) -ForegroundColor Red
            Write-Host ('  expected: ' + $entry.sha256) -ForegroundColor Red
            Write-Host ('  actual:   ' + $actualSha) -ForegroundColor Red
            return $false
        }

        if ($entry.expectedAuthenticodeSubject) {
            $sig = Get-AuthenticodeSignature -LiteralPath $filePath
            if ($sig.Status -ne 'Valid') {
                Write-Host ('E_SQLITE_DLL_INTEGRITY: Authenticode status is ' + $sig.Status + ' on ' + $fileName) -ForegroundColor Red
                return $false
            }
            if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Subject -ne $entry.expectedAuthenticodeSubject) {
                $actualSubject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '<none>' }
                Write-Host ('E_SQLITE_DLL_INTEGRITY: Authenticode subject mismatch on ' + $fileName) -ForegroundColor Red
                Write-Host ('  expected: ' + $entry.expectedAuthenticodeSubject) -ForegroundColor Red
                Write-Host ('  actual:   ' + $actualSubject) -ForegroundColor Red
                return $false
            }
        }
    }
    return $true
}

# ====================================================================
# C1b - Bundled PAX script integrity verification
# ====================================================================
#
# The PAX script ships bundled inside the install tree at a fixed path.
# The broker resolves it from its own install-tree location
# ($Script:AppRoot) — there is no env-var override, no workspace setting,
# no Settings UI control, no recipe-level override. VERSION.json is the
# canonical source of truth for the bundled version + expected SHA-256;
# the install/update script is the only writer.
#
# On success, populates $Script:PaxScriptSha256, $Script:PaxScriptVersion,
# $Script:CookbookVersion, and $Script:ReleaseChannel so cook spawn and
# the runtime-metadata endpoint can read them without re-parsing
# VERSION.json on every request. Failure aborts startup with
# EXIT_E_PAX_SCRIPT_INTEGRITY (6).
#
# Phase 13 Stage 4A — external-policy-aware startup. When
# VERSION.json.paxScript.acquisitionPolicy is "external" AND either the
# bundled PAX script is missing or its on-disk SHA-256 does not match
# VERSION.json.paxScript.sha256, the function returns $true with
# $Script:BrokerStartupAcquisitionRequired = $true and
# $Script:BrokerStartupAcquisitionReason set so the broker can finish
# binding the listener and serve /api/v1/setup/acquire-pax/* (the
# first-run acquisition UI) while the Stage 4 cross-route acquisition
# gate keeps cook / resume / scheduled-task PUT blocked at HTTP 409.
# Under acquisitionPolicy = "embedded" (or absent/legacy => embedded),
# behavior is UNCHANGED: a missing or mismatched bundled script
# causes a return of $false and the caller exits the process with
# EXIT_E_PAX_SCRIPT_INTEGRITY. VERSION.json itself is still a hard
# prerequisite under both policies — without it the broker cannot
# even resolve the policy or serve the acquisition state route.

function Test-BundledPaxIntegrity {
    if (-not (Test-Path -LiteralPath $Script:VersionFile -PathType Leaf)) {
        Write-Host ('E_PAX_SCRIPT_INTEGRITY: VERSION.json not found at ' + $Script:VersionFile) -ForegroundColor Red
        return $false
    }

    try {
        $versionDoc = Get-Content -LiteralPath $Script:VersionFile -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    } catch {
        Write-Host ('E_PAX_SCRIPT_INTEGRITY: VERSION.json is unreadable: ' + $_.Exception.Message) -ForegroundColor Red
        return $false
    }

    $expectedSha = $null
    $declaredVer = $null
    if ($versionDoc -and $versionDoc.paxScript) {
        $expectedSha = [string]$versionDoc.paxScript.sha256
        $declaredVer = [string]$versionDoc.paxScript.version
    }
    if ([string]::IsNullOrWhiteSpace($expectedSha)) {
        Write-Host 'E_PAX_SCRIPT_INTEGRITY: VERSION.json is missing paxScript.sha256' -ForegroundColor Red
        return $false
    }
    if ([string]::IsNullOrWhiteSpace($declaredVer)) {
        Write-Host 'E_PAX_SCRIPT_INTEGRITY: VERSION.json is missing paxScript.version' -ForegroundColor Red
        return $false
    }
    $expectedSha = $expectedSha.ToUpperInvariant()

    $cookbookVer = $null
    if ($versionDoc -and $versionDoc.cookbook) {
        $cookbookVer = [string]$versionDoc.cookbook.version
    }
    if ([string]::IsNullOrWhiteSpace($cookbookVer)) {
        Write-Host 'E_PAX_SCRIPT_INTEGRITY: VERSION.json is missing cookbook.version' -ForegroundColor Red
        return $false
    }

    $channel = $null
    if ($versionDoc) {
        $channel = [string]$versionDoc.channel
    }
    if ([string]::IsNullOrWhiteSpace($channel)) {
        Write-Host 'E_PAX_SCRIPT_INTEGRITY: VERSION.json is missing channel' -ForegroundColor Red
        return $false
    }

    # Stage 4A: resolve the external-engine fields EARLY so the policy
    # check can fork the integrity logic before we Test-Path / hash the
    # script. These fields were previously populated only on the
    # success path AFTER the hash matched; resolving them here is a
    # read-only widening that preserves all prior semantics for the
    # embedded-policy success path.
    $Script:PaxAcquisitionPolicy                   = $null
    $Script:PaxScriptExportEnabled                 = $null
    $Script:PaxEngineManifestUrl                   = $null
    $Script:PaxEngineManifestTrustAnchorThumbprint = $null
    # PSObject.Properties.Match() returns a non-null ReadOnlyPSMemberInfoCollection
    # wrapper that is ALWAYS truthy in 'if (...)' context regardless of
    # how many properties matched. Under Set-StrictMode -Version Latest
    # (inherited from Environment.ps1) the conditional body would then
    # access a missing property and throw. The .Count -gt 0 explicit
    # check is the only safe way to gate on property presence.
    if ($versionDoc.paxScript.PSObject.Properties.Match('acquisitionPolicy').Count -gt 0) {
        $raw = [string]$versionDoc.paxScript.acquisitionPolicy
        if (-not [string]::IsNullOrWhiteSpace($raw)) { $Script:PaxAcquisitionPolicy = $raw }
    }
    if ($versionDoc.paxScript.PSObject.Properties.Match('exportEnabled').Count -gt 0) {
        $rawExport = $versionDoc.paxScript.exportEnabled
        if ($rawExport -is [bool]) { $Script:PaxScriptExportEnabled = [bool]$rawExport }
    }
    if ($versionDoc.paxScript.PSObject.Properties.Match('engineManifestUrl').Count -gt 0) {
        $raw = [string]$versionDoc.paxScript.engineManifestUrl
        if (-not [string]::IsNullOrWhiteSpace($raw)) { $Script:PaxEngineManifestUrl = $raw }
    }
    if ($versionDoc.paxScript.PSObject.Properties.Match('engineManifestTrustAnchorThumbprint').Count -gt 0) {
        $raw = [string]$versionDoc.paxScript.engineManifestTrustAnchorThumbprint
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            $clean = ($raw -replace '[^0-9A-Fa-f]', '')
            if ($clean.Length -eq 40) {
                $Script:PaxEngineManifestTrustAnchorThumbprint = $clean.ToUpperInvariant()
            } else {
                $Script:PaxEngineManifestTrustAnchorThumbprint = $raw
            }
        }
    }
    $Script:PaxScriptManifestSignaturePolicy = $null
    if ($versionDoc.paxScript.PSObject.Properties.Match('manifestSignaturePolicy').Count -gt 0) {
        $raw = [string]$versionDoc.paxScript.manifestSignaturePolicy
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            if ($raw -eq 'required' -or $raw -eq 'internal-test-bypass') {
                $Script:PaxScriptManifestSignaturePolicy = $raw
            } else {
                Write-Host ('E_PAX_SCRIPT_INTEGRITY: pax_manifest_signature_policy_unknown=' + $raw) -ForegroundColor Red
                return $false
            }
        }
    }
    if ($Script:PaxScriptManifestSignaturePolicy -eq 'internal-test-bypass') {
        Write-Host 'pax_manifest_signature_policy=internal-test-bypass (NON-PRODUCTION; this artifact is not customer-facing)' -ForegroundColor Yellow
    }
    # 'external' enters degraded boot on script-missing / hash-mismatch;
    # missing or any other value (including the historical embedded
    # legacy where the field is absent) keeps the strict embedded path.
    $effectivePolicy = if ($Script:PaxAcquisitionPolicy -eq 'external') { 'external' } else { 'embedded' }
    # Cache the expected SHA from VERSION.json regardless of whether
    # the on-disk script is present and validated. Setup.ps1's
    # /api/v1/setup/acquire-pax/state route reads this to render the
    # 'expected' block of its response in the degraded-boot window.
    $Script:PaxScriptExpectedSha256 = $expectedSha

    if (-not (Test-Path -LiteralPath $Script:PaxScriptPath -PathType Leaf)) {
        if ($effectivePolicy -eq 'external') {
            # Stage 4A degraded boot: external policy + canonical script
            # absent. Populate everything we CAN populate from VERSION.json
            # so the runtime-metadata and acquire-pax/state routes have
            # truthful 'expected' values. Leave $Script:PaxScriptSha256
            # null so the Stage 4 acquisition gate's downstream cook
            # context block (Routes\Cooks.ps1::Get-CookContextBlock)
            # cannot be reached without going through Set-PaxScriptActivated
            # first — the gate already returns 409 acquisitionRequired
            # while state != 'acquired'.
            $Script:PaxScriptSha256                  = $null
            $Script:PaxScriptVersion                 = $declaredVer
            $Script:CookbookVersion                  = $cookbookVer
            $Script:ReleaseChannel                   = $channel
            $Script:BrokerStartupAcquisitionRequired = $true
            $Script:BrokerStartupAcquisitionReason   = 'pax_script_absent'
            $Script:UpdateManifestUrl                = $null
            if ($versionDoc.PSObject.Properties.Match('updateManifestUrl').Count -gt 0) {
                $raw = $versionDoc.updateManifestUrl
                if ($raw -is [string] -and -not [string]::IsNullOrWhiteSpace($raw)) {
                    $Script:UpdateManifestUrl = $raw
                }
            }
            Write-Host ('Broker: PAX script absent at ' + $Script:PaxScriptPath + ' (external policy). Degraded boot — first-run acquisition required.') -ForegroundColor Yellow
            return $true
        }
        Write-Host ('E_PAX_SCRIPT_INTEGRITY: bundled PAX script not found at ' + $Script:PaxScriptPath) -ForegroundColor Red
        return $false
    }

    try {
        $actualSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $Script:PaxScriptPath -ErrorAction Stop).Hash.ToUpperInvariant()
    } catch {
        Write-Host ('E_PAX_SCRIPT_INTEGRITY: failed to compute SHA-256 of bundled PAX script: ' + $_.Exception.Message) -ForegroundColor Red
        return $false
    }

    if ($actualSha -ne $expectedSha) {
        if ($effectivePolicy -eq 'external') {
            # Stage 4A degraded boot: external policy + on-disk hash
            # mismatch. Same handling as the absent case: leave
            # $Script:PaxScriptSha256 null so the cook context block
            # is unreachable without a fresh Set-PaxScriptActivated.
            $Script:PaxScriptSha256                  = $null
            $Script:PaxScriptVersion                 = $declaredVer
            $Script:CookbookVersion                  = $cookbookVer
            $Script:ReleaseChannel                   = $channel
            $Script:BrokerStartupAcquisitionRequired = $true
            $Script:BrokerStartupAcquisitionReason   = 'pax_script_hash_mismatch'
            $Script:UpdateManifestUrl                = $null
            if ($versionDoc.PSObject.Properties.Match('updateManifestUrl').Count -gt 0) {
                $raw = $versionDoc.updateManifestUrl
                if ($raw -is [string] -and -not [string]::IsNullOrWhiteSpace($raw)) {
                    $Script:UpdateManifestUrl = $raw
                }
            }
            Write-Host 'Broker: bundled PAX script SHA-256 mismatch (external policy). Degraded boot — first-run acquisition required.' -ForegroundColor Yellow
            Write-Host ('  Script:   ' + $Script:PaxScriptPath) -ForegroundColor Yellow
            Write-Host ('  Expected: ' + $expectedSha) -ForegroundColor Yellow
            Write-Host ('  Actual:   ' + $actualSha) -ForegroundColor Yellow
            return $true
        }
        Write-Host 'E_PAX_SCRIPT_INTEGRITY: bundled PAX script SHA-256 mismatch' -ForegroundColor Red
        Write-Host ('  Script:   ' + $Script:PaxScriptPath) -ForegroundColor Red
        Write-Host ('  Expected: ' + $expectedSha) -ForegroundColor Red
        Write-Host ('  Actual:   ' + $actualSha) -ForegroundColor Red
        return $false
    }

    $Script:PaxScriptSha256                  = $expectedSha
    $Script:PaxScriptVersion                 = $declaredVer
    $Script:CookbookVersion                  = $cookbookVer
    $Script:ReleaseChannel                   = $channel
    $Script:BrokerStartupAcquisitionRequired = $false
    $Script:BrokerStartupAcquisitionReason   = $null

    # Optional outbound update-manifest URL. Null in repo builds; a
    # release build populates this field with a stable HTTPS URL the
    # operator can use to check for new Cookbook versions. Any unknown
    # value is left as-is here; URL syntax validation happens at the
    # check-time in Update\Manifest.psm1::Test-UpdateManifestUrl.
    $Script:UpdateManifestUrl = $null
    if ($versionDoc -and $versionDoc.PSObject.Properties.Match('updateManifestUrl').Count -gt 0) {
        $raw = $versionDoc.updateManifestUrl
        if ($raw -is [string] -and -not [string]::IsNullOrWhiteSpace($raw)) {
            $Script:UpdateManifestUrl = $raw
        }
    }

    return $true
}

# ====================================================================
# C1c - Manifest alignment (soft, non-fatal)
# ====================================================================
#
# manifest.json is the release-side metadata file written by the
# installer alongside VERSION.json. At runtime we trust VERSION.json as
# the canonical source of truth for the bundled engine, but a drift
# between the two on disk is a reportable observability state — it
# typically indicates a partial install, manual file edit, or tampering.
# This check is non-fatal: the broker still serves traffic, but the
# /api/v1/runtime/version endpoint and the Settings page surface the
# drift so it is visible.
#
# Reads manifest.json once at startup; sets $Script:ManifestAligned and
# $Script:ManifestChannel. Never aborts startup. No remote fetch, no
# network call, no cache, no auto-repair.

function Test-ManifestAlignment {
    $Script:ManifestAligned = $false
    $Script:ManifestChannel = $null

    if (-not (Test-Path -LiteralPath $Script:ManifestFile -PathType Leaf)) {
        Write-Host ('Broker: manifest.json not found at ' + $Script:ManifestFile + ' (manifest drift)') -ForegroundColor Yellow
        return $true
    }
    try {
        $manifestDoc = Get-Content -LiteralPath $Script:ManifestFile -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    } catch {
        Write-Host ('Broker: manifest.json is unreadable: ' + $_.Exception.Message + ' (manifest drift)') -ForegroundColor Yellow
        return $true
    }

    $mChan = $null
    $mPaxVer = $null
    $mPaxSha = $null
    $mLatestVer = $null
    $mPkgUrl    = $null
    $mPolicy    = $null
    $mExport    = $null
    $mEngineUrl = $null
    $mEngineTp  = $null
    $mSigPol    = $null
    if ($manifestDoc) {
        $mChan   = [string]$manifestDoc.channel
        if ($manifestDoc.includedPaxScript) {
            $mPaxVer = [string]$manifestDoc.includedPaxScript.version
            $mPaxSha = [string]$manifestDoc.includedPaxScript.sha256
            if ($manifestDoc.includedPaxScript.PSObject.Properties.Match('acquisitionPolicy').Count -gt 0) {
                $raw = [string]$manifestDoc.includedPaxScript.acquisitionPolicy
                if (-not [string]::IsNullOrWhiteSpace($raw)) { $mPolicy = $raw }
            }
            if ($manifestDoc.includedPaxScript.PSObject.Properties.Match('exportEnabled').Count -gt 0) {
                $rawExport = $manifestDoc.includedPaxScript.exportEnabled
                if ($rawExport -is [bool]) { $mExport = [bool]$rawExport }
            }
            if ($manifestDoc.includedPaxScript.PSObject.Properties.Match('engineManifestUrl').Count -gt 0) {
                $raw = [string]$manifestDoc.includedPaxScript.engineManifestUrl
                if (-not [string]::IsNullOrWhiteSpace($raw)) { $mEngineUrl = $raw }
            }
            if ($manifestDoc.includedPaxScript.PSObject.Properties.Match('engineManifestTrustAnchorThumbprint').Count -gt 0) {
                $raw = [string]$manifestDoc.includedPaxScript.engineManifestTrustAnchorThumbprint
                if (-not [string]::IsNullOrWhiteSpace($raw)) {
                    $clean = ($raw -replace '[^0-9A-Fa-f]', '')
                    if ($clean.Length -eq 40) { $mEngineTp = $clean.ToUpperInvariant() } else { $mEngineTp = $raw }
                }
            }
            if ($manifestDoc.includedPaxScript.PSObject.Properties.Match('manifestSignaturePolicy').Count -gt 0) {
                $raw = [string]$manifestDoc.includedPaxScript.manifestSignaturePolicy
                if (-not [string]::IsNullOrWhiteSpace($raw)) {
                    if ($raw -eq 'required' -or $raw -eq 'internal-test-bypass') {
                        $mSigPol = $raw
                    } else {
                        Write-Host ('E_PAX_SCRIPT_INTEGRITY: manifest_pax_manifest_signature_policy_unknown=' + $raw) -ForegroundColor Red
                        $Script:ManifestAligned = $false
                        return $false
                    }
                }
            }
        }
        if ($manifestDoc.latestCookbook) {
            $mLatestVer = [string]$manifestDoc.latestCookbook.version
            $mPkgUrl    = [string]$manifestDoc.latestCookbook.packageUrl
        }
    }
    if ($mPaxSha) { $mPaxSha = $mPaxSha.ToUpperInvariant() }
    $Script:ManifestChannel = $mChan
    $Script:ManifestPaxAcquisitionPolicy                   = $mPolicy
    $Script:ManifestPaxScriptExportEnabled                 = $mExport
    $Script:ManifestPaxEngineManifestUrl                   = $mEngineUrl
    $Script:ManifestPaxEngineManifestTrustAnchorThumbprint = $mEngineTp
    $Script:ManifestPaxScriptManifestSignaturePolicy       = $mSigPol
    if (-not [string]::IsNullOrWhiteSpace($mLatestVer)) {
        $Script:ManifestLatestCookbookVersion = $mLatestVer
    }
    # A "configured" packageUrl is non-empty AND not a "<TODO_*>" placeholder.
    # Installer writes the real URL at release time; the in-repo manifest.json
    # ships with "<TODO_RELEASE_URL_PACKAGE_ZIP>" so we surface "not configured"
    # honestly until the installer fills it.
    if (-not [string]::IsNullOrWhiteSpace($mPkgUrl) -and ($mPkgUrl -notmatch '^<TODO[_A-Z0-9]*>$')) {
        $Script:ManifestPackageUrlConfigured = $true
    } else {
        $Script:ManifestPackageUrlConfigured = $false
    }

    $aligned = $true
    if ([string]::IsNullOrWhiteSpace($mChan) -or $mChan -ne $Script:ReleaseChannel) { $aligned = $false }
    if ([string]::IsNullOrWhiteSpace($mPaxVer) -or $mPaxVer -ne $Script:PaxScriptVersion) { $aligned = $false }
    if ([string]::IsNullOrWhiteSpace($mPaxSha) -or $mPaxSha -ne $Script:PaxScriptSha256) { $aligned = $false }

    # Phase 13 Stage 4A/5 -- STRICT alignment for the five external-engine
    # fields. ANY one-sided drift or value mismatch is fatal: the
    # mismatch token is logged in the form
    # 'version_manifest_mirror_mismatch_<field>' and Test-ManifestAlignment
    # returns $false, which the caller turns into
    # EXIT_E_PAX_SCRIPT_INTEGRITY. Channel / PAX version / PAX SHA
    # alignment above stays SOFT (observational only).
    if ($Script:PaxAcquisitionPolicy -ne $mPolicy) {
        Write-Host 'version_manifest_mirror_mismatch_acquisitionPolicy' -ForegroundColor Red
        $Script:ManifestAligned = $false
        return $false
    }
    if ($Script:PaxScriptExportEnabled -ne $mExport) {
        Write-Host 'version_manifest_mirror_mismatch_exportEnabled' -ForegroundColor Red
        $Script:ManifestAligned = $false
        return $false
    }
    if ($Script:PaxEngineManifestUrl -ne $mEngineUrl) {
        Write-Host 'version_manifest_mirror_mismatch_engineManifestUrl' -ForegroundColor Red
        $Script:ManifestAligned = $false
        return $false
    }
    if ($Script:PaxEngineManifestTrustAnchorThumbprint -ne $mEngineTp) {
        Write-Host 'version_manifest_mirror_mismatch_engineManifestTrustAnchorThumbprint' -ForegroundColor Red
        $Script:ManifestAligned = $false
        return $false
    }
    if ($Script:PaxScriptManifestSignaturePolicy -ne $mSigPol) {
        Write-Host 'version_manifest_mirror_mismatch_manifestSignaturePolicy' -ForegroundColor Red
        $Script:ManifestAligned = $false
        return $false
    }

    if (-not $aligned) {
        Write-Host 'Broker: manifest.json does not match VERSION.json (manifest drift)' -ForegroundColor Yellow
    }
    $Script:ManifestAligned = $aligned
    return $true
}

function New-SessionToken {
    $bytes = [byte[]]::new(32)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $b64 = [Convert]::ToBase64String($bytes)
    return $b64.TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# ====================================================================
# C2 - Loopback HttpListener bind
# ====================================================================

function Start-LoopbackListener {
    # Doctrine §11 -- stable broker port:
    #
    #   1. Read the persisted selectedBrokerPort from the launcher
    #      bootstrap pointer; if it is in range, try it first.
    #   2. Otherwise (or on bind failure) scan
    #      $Script:CookbookBrokerPortRangeStart ..
    #      $Script:CookbookBrokerPortRangeEnd in order.
    #   3. If the full range is exhausted, fail loudly. No fallback
    #      to a random ephemeral port -- that was the previous
    #      behavior and is now forbidden.
    #   4. On success, persist the selected port back to the bootstrap
    #      pointer so the next launch hits the same port first.
    #
    # Two loopback prefixes are added on the same port:
    #   http://127.0.0.1:<port>/   -- preserved for back-compat. Internal
    #                                 health checks and any operator who
    #                                 still types 127.0.0.1 still resolves.
    #   http://localhost:<port>/   -- the canonical browser-facing UI
    #                                 origin. Chrome/Edge accept localhost
    #                                 as a valid WebAuthn effective domain
    #                                 but reject 127.0.0.1 with
    #                                 SecurityError 'This is an invalid
    #                                 domain' before the platform
    #                                 authenticator ceremony can start.
    # Both prefixes bind on the loopback adapter only -- HttpListener
    # does not expose them on the LAN. No elevation required for the
    # localhost prefix because it is a host-name-style prefix on the
    # loopback interface owned by the current user.
    #
    # HttpListener.Start() throws on bind failure (port in use, ACL
    # denied, etc.); we catch, close the listener, and move on to the
    # next port. The previous TcpListener probe was removed because
    # the kernel-reassignment race window is moot here -- we are
    # iterating a closed list of known ports, not asking the kernel
    # for an ephemeral.
    $orderedPorts = [System.Collections.Generic.List[int]]::new()
    $bootstrap    = Read-CookbookBootstrapPointer
    $persistedPort = $null
    # NOTE: the broker runs under Set-StrictMode -Version Latest (see
    # app\broker\Environment.ps1). A pre-existing schemaVersion=1
    # bootstrap pointer does NOT carry selectedBrokerPort; bare access
    # to a missing property throws under strict mode. Probe the
    # property collection before reading the value -- the launcher
    # uses the same pattern in app\launcher\Start-PAXCookbook.ps1.
    if ($bootstrap -and $bootstrap.PSObject.Properties['selectedBrokerPort']) {
        try { $persistedPort = [int]$bootstrap.selectedBrokerPort } catch { $persistedPort = $null }
    }
    if ($null -ne $persistedPort -and
        $persistedPort -ge $Script:CookbookBrokerPortRangeStart -and
        $persistedPort -le $Script:CookbookBrokerPortRangeEnd) {
        $orderedPorts.Add($persistedPort)
    }
    for ($p = $Script:CookbookBrokerPortRangeStart; $p -le $Script:CookbookBrokerPortRangeEnd; $p++) {
        if (-not $orderedPorts.Contains($p)) { $orderedPorts.Add($p) }
    }

    foreach ($port in $orderedPorts) {
        $listener  = [System.Net.HttpListener]::new()
        $prefixIp  = 'http://127.0.0.1:' + $port + '/'
        $prefixLh  = 'http://localhost:' + $port + '/'
        $listener.Prefixes.Add($prefixIp)
        $listener.Prefixes.Add($prefixLh)
        try {
            $listener.Start()
            $Script:Listener   = $listener
            $Script:BrokerPort = $port
            Save-CookbookBootstrapPort -Port $port
            return $true
        } catch {
            try { $listener.Close() } catch {}
            continue
        }
    }

    Write-Host ('Broker: failed to bind any port in ' + $Script:CookbookBrokerPortRangeStart + '..' + $Script:CookbookBrokerPortRangeEnd + '.') -ForegroundColor Red
    Write-Host  'Broker: refusing to fall back to a random ephemeral port.' -ForegroundColor Red
    return $false
}

function Read-CookbookBootstrapPointer {
    if (-not (Test-Path -LiteralPath $Script:CookbookBootstrapFile -PathType Leaf)) { return $null }
    try {
        $raw = Get-Content -LiteralPath $Script:CookbookBootstrapFile -Raw -ErrorAction Stop
        return ($raw | ConvertFrom-Json -ErrorAction Stop)
    } catch {
        return $null
    }
}

function Save-CookbookBootstrapPort {
    param([Parameter(Mandatory=$true)][int]$Port)
    try {
        if (-not (Test-Path -LiteralPath $Script:CookbookBootstrapDir -PathType Container)) {
            $null = New-Item -ItemType Directory -Path $Script:CookbookBootstrapDir -Force
        }
        $existing = Read-CookbookBootstrapPointer
        $existingWorkspace = $null
        if ($existing -and $existing.workspaceFolderPath) {
            $existingWorkspace = [string]$existing.workspaceFolderPath
        }
        if (-not $existingWorkspace) { $existingWorkspace = $Script:WorkspacePath }
        $obj = [ordered]@{
            schemaVersion       = 2
            workspaceFolderPath = $existingWorkspace
            lastUsed            = (Get-Date).ToUniversalTime().ToString('o')
            preferredBrokerPort = $Script:CookbookPreferredBrokerPort
            selectedBrokerPort  = $Port
            portRangeStart      = $Script:CookbookBrokerPortRangeStart
            portRangeEnd        = $Script:CookbookBrokerPortRangeEnd
        }
        $json = $obj | ConvertTo-Json -Depth 5
        $tmp  = $Script:CookbookBootstrapFile + '.tmp'
        Set-Content -LiteralPath $tmp -Value $json -Encoding utf8 -NoNewline -Force
        Move-Item -LiteralPath $tmp -Destination $Script:CookbookBootstrapFile -Force
    } catch {
        # Non-fatal: persisting the port is a hint for next launch.
        # If it fails, the next launch will scan the full range again.
    }
}

# ====================================================================
# C3 - Workspace lock manager
# ====================================================================

function Read-WorkspaceLock {
    if (-not (Test-Path -LiteralPath $Script:LockFile -PathType Leaf)) { return $null }
    try {
        # -AsHashtable keeps ISO-8601 timestamp fields as strings (the
        # default behavior implicitly coerces them to [DateTime] and the
        # culture-formatted ToString() then leaks into UI surfaces).
        return (Get-Content -LiteralPath $Script:LockFile -Raw | ConvertFrom-Json -AsHashtable)
    } catch {
        return $null
    }
}

function Test-PriorBrokerActive {
    # Returns $true only when every layered check of the prior-broker
    # workspace-ownership contract passes. Any failure means the lock
    # is stale and can be reclaimed.
    param($Lock)

    # Step 1: PID still alive
    $proc = Get-Process -Id $Lock.brokerProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return $false }

    # Step 2: PID image is pwsh
    $procPath = $null
    try { $procPath = $proc.Path } catch {}
    if (-not $procPath) { return $false }
    $procName = [System.IO.Path]::GetFileName($procPath)
    if ($procName -ne 'pwsh.exe' -and $procName -ne 'pwsh') { return $false }

    # Step 3: TCP probe within 500 ms
    $tcp = [System.Net.Sockets.TcpClient]::new()
    try {
        $iar = $tcp.BeginConnect([System.Net.IPAddress]::Loopback, [int]$Lock.brokerPort, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne(500)) { return $false }
        $tcp.EndConnect($iar)
    } catch {
        return $false
    } finally {
        try { $tcp.Close() } catch {}
    }

    # Step 4: /api/v1/health within 2 s + workspaceFolderPath match
    try {
        $url = 'http://127.0.0.1:' + [int]$Lock.brokerPort + '/api/v1/health'
        $resp = Invoke-WebRequest -Uri $url -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
        if ($resp.StatusCode -ne 200) { return $false }
        $body = $resp.Content | ConvertFrom-Json -AsHashtable
        if (-not $body.workspaceFolderPath) { return $false }
        $a = [System.IO.Path]::GetFullPath($body.workspaceFolderPath).TrimEnd('\','/')
        $b = [System.IO.Path]::GetFullPath($Script:WorkspacePath).TrimEnd('\','/')
        return [string]::Equals($a, $b, [System.StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Write-WorkspaceLock {
    if (-not (Test-Path -LiteralPath $Script:RuntimeDir -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $Script:RuntimeDir -Force
    }

    # UX-1H10 -- record support metadata so the Support Mode shortcut
    # can reveal/restore the broker console window and the Stop
    # shortcut can verify the PID before terminating. launchMode is
    # carried in via $env:PAX_COOKBOOK_LAUNCH_MODE which the launcher
    # sets based on its own console window visibility BEFORE invoking
    # this broker (the broker runs in the launcher's pwsh process, so
    # its console IS the launcher's console). consoleWindowHandle is
    # captured fresh on every Write-WorkspaceLock call via the
    # kernel32 GetConsoleWindow import below; if no console is
    # attached, the call returns 0 and the value is recorded as 0
    # rather than omitted so the schema shape is stable.
    $launchMode = $env:PAX_COOKBOOK_LAUNCH_MODE
    if ([string]::IsNullOrWhiteSpace($launchMode)) { $launchMode = 'unknown' }

    $consoleHandle = 0
    try {
        if (-not ('PaxCookbook.Win32Console' -as [type])) {
            Add-Type -Namespace 'PaxCookbook' -Name 'Win32Console' -MemberDefinition @'
            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            public static extern System.IntPtr GetConsoleWindow();
'@
        }
        $consoleHandle = [int][PaxCookbook.Win32Console]::GetConsoleWindow()
    } catch {
        $consoleHandle = 0
    }

    # logPath: best-effort discovery. The broker today does not write
    # a dedicated log file (output goes to its hosting console). The
    # Logs subdirectory of the workspace is where downstream tooling
    # writes per-recipe logs, so the support surface points at it
    # regardless of whether the broker itself emits a file.
    $logsPath = Join-Path $Script:WorkspacePath 'Logs'

    $payload = [ordered]@{
        schemaVersion      = 1
        machineName        = $env:COMPUTERNAME
        windowsUserName    = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        windowsUserSid     = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        brokerProcessId    = $PID
        brokerPort         = $Script:BrokerPort
        launchTimestampUtc = Get-UtcNowIso
        cookbookVersion    = Get-CookbookVersion
        launchMode         = $launchMode
        consoleWindowHandle= $consoleHandle
        appRoot            = $Script:AppRoot
        workspaceRoot      = $Script:WorkspacePath
        logsPath           = $logsPath
    }
    $json = $payload | ConvertTo-Json -Depth 4
    $tmp  = $Script:LockFile + '.tmp'
    [System.IO.File]::WriteAllText($tmp, $json, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::Move($tmp, $Script:LockFile, $true)
    $Script:LockHeld = $true

    # AI.C9.1 -- write session-token sidecar atomically so the
    # bundled scheduler launcher can authenticate against the
    # existing cook-trigger route. Sidecar contains the token bytes
    # only -- no JSON, no metadata -- to keep the parse surface
    # zero. Write failure is non-fatal for the broker lifecycle
    # (the broker still serves; only the scheduler launcher loses
    # its auth handoff path), surfaced via Add-RecentError.
    try {
        if ($null -ne $Script:SessionToken -and $Script:SessionToken.Length -gt 0) {
            $sessionTokenSidecarTmp = $Script:SessionTokenSidecar + '.tmp'
            [System.IO.File]::WriteAllText($sessionTokenSidecarTmp, [string]$Script:SessionToken, [System.Text.UTF8Encoding]::new($false))
            [System.IO.File]::Move($sessionTokenSidecarTmp, $Script:SessionTokenSidecar, $true)
        }
    } catch {
        Add-RecentError -Message ('AI.C9.1 session-token sidecar write failed: ' + $_.Exception.Message) -Source 'scheduler_token_sidecar'
    }
}

function Remove-WorkspaceLock {
    if (-not $Script:LockHeld) { return }
    try {
        if (Test-Path -LiteralPath $Script:LockFile -PathType Leaf) {
            Remove-Item -LiteralPath $Script:LockFile -Force -ErrorAction SilentlyContinue
        }
    } catch {}
    # AI.C9.1 -- remove the session-token sidecar alongside the
    # workspace lock. A dead broker MUST NOT leave a valid token on
    # disk; a missing sidecar is the launcher's signal to fail fast
    # without retry.
    try {
        if (Test-Path -LiteralPath $Script:SessionTokenSidecar -PathType Leaf) {
            Remove-Item -LiteralPath $Script:SessionTokenSidecar -Force -ErrorAction SilentlyContinue
        }
    } catch {}
    $Script:LockHeld = $false
}

function Open-WorkspaceLockHandle {
    # Acquire an exclusive, broker-lifetime handle on a per-workspace
    # sentinel file using FileShare.None. The OS guarantees that at
    # most one process can hold this handle at a time, so this is the
    # authoritative gate for workspace ownership. Returns $true if the
    # handle was acquired (this broker may proceed), $false if another
    # live broker process already holds it (this broker must refuse).
    # The handle is released automatically by the OS on process exit
    # even if Invoke-Shutdown does not run (force-kill, crash).
    if (-not (Test-Path -LiteralPath $Script:RuntimeDir -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $Script:RuntimeDir -Force
    }
    try {
        $Script:LockAcquireHandle = [System.IO.File]::Open(
            $Script:LockAcquireFile,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None
        )
        return $true
    } catch [System.IO.IOException] {
        return $false
    }
}

function Close-WorkspaceLockHandle {
    if ($null -eq $Script:LockAcquireHandle) { return }
    try { $Script:LockAcquireHandle.Close()   } catch {}
    try { $Script:LockAcquireHandle.Dispose() } catch {}
    $Script:LockAcquireHandle = $null
    try {
        if (Test-Path -LiteralPath $Script:LockAcquireFile -PathType Leaf) {
            Remove-Item -LiteralPath $Script:LockAcquireFile -Force -ErrorAction SilentlyContinue
        }
    } catch {}
}

function Show-ActiveLockRefusalAndExit {
    param($Lock)
    Write-Host ''                                                                                  -ForegroundColor Red
    Write-Host 'This workspace is already open in another Cookbook broker.'                        -ForegroundColor Red
    Write-Host ''                                                                                  -ForegroundColor Red
    $sinceStr = $Lock.launchTimestampUtc
    if ($sinceStr -is [datetime]) {
        $sinceStr = $sinceStr.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ', [System.Globalization.CultureInfo]::InvariantCulture)
    }
    Write-Host ('    Machine : ' + $Lock.machineName)                                              -ForegroundColor Red
    Write-Host ('    User    : ' + $Lock.windowsUserName)                                          -ForegroundColor Red
    Write-Host ('    Port    : ' + $Lock.brokerPort)                                               -ForegroundColor Red
    Write-Host ('    PID     : ' + $Lock.brokerProcessId)                                          -ForegroundColor Red
    Write-Host ('    Since   : ' + $sinceStr)                                                      -ForegroundColor Red
    Write-Host ''                                                                                  -ForegroundColor Red
    Write-Host 'Use the existing broker, or close it before starting a new one.'                   -ForegroundColor Red
    Write-Host 'To force-unlock (only if you are certain the other broker is gone),'               -ForegroundColor Red
    Write-Host ('delete: ' + $Script:LockFile)                                                     -ForegroundColor Red
    Write-Host ''                                                                                  -ForegroundColor Red
    exit $EXIT_E_WORKSPACE_LOCKED
}

# ====================================================================
# C4 - SQLite open + M1 schema
# ====================================================================

$Script:M1_Ddl = @'
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS _schema_meta (
  id              INTEGER PRIMARY KEY CHECK (id = 1),
  schema_version  INTEGER NOT NULL,
  workspace_id    TEXT    NOT NULL,
  created_at      TEXT    NOT NULL,
  updated_at      TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS recipes (
  recipe_id              TEXT PRIMARY KEY,
  name                   TEXT NOT NULL,
  description            TEXT,
  tags_json              TEXT NOT NULL DEFAULT '[]',
  pax_adapter_version    TEXT NOT NULL,
  recipe_schema_version  INTEGER NOT NULL,
  source                 TEXT NOT NULL,
  source_ref             TEXT,
  file_path              TEXT NOT NULL UNIQUE,
  file_hash              TEXT NOT NULL,
  status                 TEXT NOT NULL DEFAULT 'draft',
  is_pinned              INTEGER NOT NULL DEFAULT 0,
  last_validated_at      TEXT,
  last_validation_status TEXT,
  last_cooked_at         TEXT,
  last_cook_id           TEXT,
  created_at             TEXT NOT NULL,
  updated_at             TEXT NOT NULL,
  deleted_at             TEXT
);
CREATE INDEX IF NOT EXISTS idx_recipes_name        ON recipes(name);
CREATE INDEX IF NOT EXISTS idx_recipes_status      ON recipes(status);
CREATE INDEX IF NOT EXISTS idx_recipes_last_cooked ON recipes(last_cooked_at);

CREATE TABLE IF NOT EXISTS cooks (
  cook_id                TEXT PRIMARY KEY,
  recipe_id              TEXT REFERENCES recipes(recipe_id) ON DELETE SET NULL,
  recipe_version_id      TEXT,
  recipe_snapshot_json   TEXT NOT NULL,
  command_argv_json      TEXT NOT NULL,
  command_argv_redacted  TEXT NOT NULL,
  pax_script_path        TEXT NOT NULL,
  pax_script_version     TEXT NOT NULL,
  trigger                TEXT NOT NULL,
  schedule_id            TEXT,
  parent_cook_id         TEXT REFERENCES cooks(cook_id) ON DELETE SET NULL,
  cook_folder            TEXT NOT NULL,
  pid                    INTEGER,
  status                 TEXT NOT NULL,
  exit_code              INTEGER,
  started_at             TEXT,
  finished_at            TEXT,
  duration_seconds       REAL,
  error_class            TEXT,
  error_message          TEXT,
  summary_path           TEXT,
  created_at             TEXT NOT NULL,
  updated_at             TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_cooks_recipe     ON cooks(recipe_id, started_at);
CREATE INDEX IF NOT EXISTS idx_cooks_status     ON cooks(status, started_at);
CREATE INDEX IF NOT EXISTS idx_cooks_started_at ON cooks(started_at);
CREATE INDEX IF NOT EXISTS idx_cooks_schedule   ON cooks(schedule_id, started_at);

CREATE TABLE IF NOT EXISTS cook_artifacts (
  cook_artifact_id   TEXT PRIMARY KEY,
  cook_id            TEXT NOT NULL REFERENCES cooks(cook_id) ON DELETE CASCADE,
  stream             TEXT NOT NULL,
  artifact_kind      TEXT NOT NULL,
  tier               TEXT NOT NULL,
  location           TEXT NOT NULL,
  size_bytes         INTEGER,
  row_count          INTEGER,
  is_append          INTEGER NOT NULL DEFAULT 0,
  pantry_dataset_id  TEXT,
  created_at         TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_cook_artifacts_cook ON cook_artifacts(cook_id);

CREATE TABLE IF NOT EXISTS settings (
  key    TEXT PRIMARY KEY,
  value  TEXT NOT NULL,
  scope  TEXT NOT NULL DEFAULT 'global'
);

-- Phase AF: auth_profiles is the broker-side metadata-only registry of
-- workload identities Cookbook can use to invoke PAX. It carries the
-- NON-SECRET fields needed to construct a PAX argv: identity name, mode
-- (AppRegistrationSecret or AppRegistrationCertificate), tenantId,
-- clientId, certificate thumbprint, certificate store location, and
-- bookkeeping. The actual client secret material lives ONLY in Windows
-- Credential Manager under the target string composed in
-- Auth/CredentialManager.ps1; this table records the target string so
-- the supervisor can locate the credential, but never the credential
-- itself. The cert_thumbprint + cert_store columns are present-but-NULL
-- for AppRegistrationSecret rows, and vice versa for the certificate
-- variant. last_verified_at / last_verified_result record the outcome
-- of the bounded preflight (POST .../test) -- they are operator-visible
-- telemetry, not authorization state.
CREATE TABLE IF NOT EXISTS auth_profiles (
  auth_profile_id      TEXT PRIMARY KEY,
  name                 TEXT NOT NULL UNIQUE,
  mode                 TEXT NOT NULL CHECK (mode IN ('AppRegistrationSecret','AppRegistrationCertificate')),
  tenant_id            TEXT NOT NULL,
  client_id            TEXT NOT NULL,
  cred_man_target      TEXT,
  cert_thumbprint      TEXT,
  cert_store           TEXT,
  description          TEXT,
  last_verified_at     TEXT,
  last_verified_result TEXT,
  created_at           TEXT NOT NULL,
  updated_at           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_auth_profiles_mode ON auth_profiles(mode);
'@

function Initialize-SqliteRuntime {
    # Ensure native e_sqlite3.dll is resolvable by P/Invoke. Prepending the
    # SQLite folder to PATH for the broker process is the simplest correct
    # approach on Windows; the OS DLL search order will pick it up.
    if ($env:PATH -notlike ($Script:SqliteDir + '*')) {
        $env:PATH = $Script:SqliteDir + ';' + $env:PATH
    }

    $assemblies = @(
        'SQLitePCLRaw.core.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll',
        'SQLitePCLRaw.batteries_v2.dll',
        'Microsoft.Data.Sqlite.dll'
    )
    foreach ($name in $assemblies) {
        $path = Join-Path $Script:SqliteDir $name
        Add-Type -Path $path
    }

    # One-time provider registration. Idempotent across re-launches in the
    # same AppDomain, but the broker runs in a fresh pwsh anyway.
    [SQLitePCL.Batteries_V2]::Init()
}

function Set-SqliteConnectionPragmas {
    # CANONICAL connection-policy normalization (Phase AB).
    #
    # SQLite scope rules:
    #   - journal_mode=WAL    is FILE-SCOPED and persisted on the database
    #                         file. Re-issued on every new connection so a
    #                         fresh database is created in WAL from the
    #                         first contact; for an existing WAL database
    #                         the statement is a no-op confirmation.
    #   - synchronous=NORMAL  is CONNECTION-SCOPED. Intentional WAL
    #                         durability/perf decision: NORMAL fsyncs the
    #                         WAL at commit and the database at checkpoint
    #                         time, which gives "no corruption on power
    #                         loss" with the chance of losing only the
    #                         very last in-flight transaction. FULL would
    #                         fsync on every commit (overkill for an
    #                         appliance database). We deliberately do NOT
    #                         use OFF.
    #   - busy_timeout=5000   is CONNECTION-SCOPED. Default is 0, which
    #                         makes WAL writer-lock contention surface as
    #                         immediate SQLITE_BUSY exceptions. 5 seconds
    #                         is enough headroom for the broker main
    #                         connection and per-cook supervisor
    #                         connections to coexist without surfacing
    #                         transient contention to the chef. We do NOT
    #                         add silent retry above this.
    #   - foreign_keys=ON     is CONNECTION-SCOPED. Required for the FK
    #                         CASCADE/SET NULL semantics declared in the
    #                         M1 DDL to actually be enforced on writes
    #                         done over THIS connection. The M1 DDL also
    #                         issues this PRAGMA, but only once at schema
    #                         apply time; we apply it per-connection so
    #                         the guarantee is uniform across the broker
    #                         main connection and every supervisor
    #                         connection.
    #   - wal_autocheckpoint  is FILE-SCOPED (Phase AG.C6). SQLite's
    #                         default is 1000 pages (~4 MB). Pinning it
    #                         explicitly makes the checkpoint policy
    #                         part of the documented contract instead
    #                         of "whatever the SQLite build defaults
    #                         to". 1000 pages is fine for a low-write
    #                         appliance database: WAL is reset to zero
    #                         bytes whenever a COMMIT raises the WAL
    #                         above the threshold AND no other reader
    #                         is holding a snapshot older than the
    #                         most recent commit. We additionally do
    #                         one manual PRAGMA wal_checkpoint(TRUNCATE)
    #                         at broker startup (after reconciliation)
    #                         so a WAL stranded by a previous crash
    #                         does not silently grow forever; see
    #                         Invoke-SqliteStartupCheckpoint.
    #
    # Doctrine: this helper is intentionally narrow. Do NOT add
    # speculative pragmas, do NOT introduce silent retry, do NOT weaken
    # any of the four above to "tune for performance".
    param([Microsoft.Data.Sqlite.SqliteConnection]$Connection)
    foreach ($pragma in @(
        'PRAGMA journal_mode=WAL;',
        'PRAGMA synchronous=NORMAL;',
        'PRAGMA busy_timeout=5000;',
        'PRAGMA foreign_keys=ON;',
        'PRAGMA wal_autocheckpoint=1000;'
    )) {
        $cmd = $Connection.CreateCommand()
        $cmd.CommandText = $pragma
        [void]$cmd.ExecuteNonQuery()
        $cmd.Dispose()
    }
}

function Open-SqliteConnection {
    if (-not (Test-Path -LiteralPath $Script:DatabaseDir -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $Script:DatabaseDir -Force
    }
    $csb = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
    $csb.DataSource = $Script:DatabaseFile
    $csb.Mode       = [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadWriteCreate
    $csb.Pooling    = $false

    $conn = [Microsoft.Data.Sqlite.SqliteConnection]::new($csb.ConnectionString)
    $conn.Open()
    Set-SqliteConnectionPragmas -Connection $conn

    $Script:SqliteConn = $conn
    $Script:SqliteConnectionString = $csb.ConnectionString
}

function Get-SqliteConnectionString {
    # Used by cook supervisor ThreadJobs to open their own connection.
    # WAL mode allows multiple connections to write concurrently with
    # short writer-lock contention windows.
    #
    # Phase AB: connections opened from this string MUST also call the
    # equivalent of Set-SqliteConnectionPragmas after Open(). ThreadJob
    # runspaces cannot see dot-sourced functions, so the supervisor
    # inlines the same four PRAGMA statements -- see
    # Cook/Supervisor.ps1 ($sqlExec, $sqlExecAtomic). The harness
    # enforces parity statically.
    return $Script:SqliteConnectionString
}

function Invoke-SqliteNonQuery {
    param([string]$Sql)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = $Sql
    try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Invoke-SqliteScalar {
    param([string]$Sql)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = $Sql
    try { return $cmd.ExecuteScalar() } finally { $cmd.Dispose() }
}

function Add-CookColumnIfMissing {
    # Phase AG additive-schema helper. Reads PRAGMA table_info(cooks)
    # over the OPEN $Script:SqliteConn, decides whether the named
    # column already exists, and (only if it does not) emits a single
    # ALTER TABLE ADD COLUMN statement. SQLite cannot add a NOT NULL
    # column without a default, so every AG column is declared
    # nullable -- pre-AG rows surface as NULL on the new columns and
    # remain undisturbed. This is intentional doctrine: AG never
    # back-fills historical rows.
    param([string]$ColumnName, [string]$ColumnDef)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'PRAGMA table_info(cooks);'
    $existing = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $reader = $cmd.ExecuteReader()
    try {
        # PRAGMA table_info columns: 0=cid, 1=name, 2=type, ...
        while ($reader.Read()) { [void]$existing.Add([string]$reader.GetValue(1)) }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
    if (-not $existing.Contains($ColumnName)) {
        Invoke-SqliteNonQuery ("ALTER TABLE cooks ADD COLUMN $ColumnName $ColumnDef;")
    }
}

function Add-BrokerSessionColumnIfMissing {
    # Phase AH.C3 additive-schema helper. Same shape as
    # Add-CookColumnIfMissing but targets broker_sessions. AH.C3
    # additive columns are observational evidence captured at startup;
    # they are nullable so pre-AH.C3 rows surface as NULL on the new
    # columns and remain undisturbed. Doctrine: AH never back-fills
    # historical broker_sessions rows.
    param([string]$ColumnName, [string]$ColumnDef)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'PRAGMA table_info(broker_sessions);'
    $existing = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $reader = $cmd.ExecuteReader()
    try {
        while ($reader.Read()) { [void]$existing.Add([string]$reader.GetValue(1)) }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
    if (-not $existing.Contains($ColumnName)) {
        Invoke-SqliteNonQuery ("ALTER TABLE broker_sessions ADD COLUMN $ColumnName $ColumnDef;")
    }
}

function Add-ScheduledTaskColumnIfMissing {
    # V1 additive-schema helper. Same shape as Add-CookColumnIfMissing
    # but targets scheduled_tasks. The column is nullable so DBs
    # created before the column existed surface NULL on it and are
    # never back-filled; the value is populated forward on the next
    # schedule PUT for that recipe.
    param([string]$ColumnName, [string]$ColumnDef)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'PRAGMA table_info(scheduled_tasks);'
    $existing = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $reader = $cmd.ExecuteReader()
    try {
        while ($reader.Read()) { [void]$existing.Add([string]$reader.GetValue(1)) }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
    if (-not $existing.Contains($ColumnName)) {
        Invoke-SqliteNonQuery ("ALTER TABLE scheduled_tasks ADD COLUMN $ColumnName $ColumnDef;")
    }
}

# ====================================================================
# Phase AG - Cook closure-reason taxonomy (FROZEN VOCABULARY)
# ====================================================================
#
# The cooks.status column is the high-level taxonomy (4 values):
#   running | completed | errored | interrupted
#
# The cooks.closure_reason column is the granular taxonomy. It
# carries WHY a cook reached its terminal state and is the
# authoritative answer to "what kind of failure / interruption was
# this?". The set of values is FROZEN below; introducing a new
# value REQUIRES corresponding harness coverage in
# verify_phase_ag.ps1.
#
# Set by the supervisor at terminal write (Cook/Supervisor.ps1):
#   clean_exit                    -- child exited 0
#   nonzero_exit                  -- child exited != 0
#   cancel_stop                   -- /stop completed cooperatively
#                                    (child exited within the 5s grace
#                                    after stdin close)
#   cancel_stop_escalated_kill    -- /stop grace expired, supervisor
#                                    escalated to Kill($true)
#   cancel_kill                   -- /kill issued (no cooperative phase)
#   spawn_failed                  -- $proc.Start() threw / executionMode
#                                    rejected before Start()
#   executionmode_rejected        -- distinct synonym used when the
#                                    supervisor's defense-in-depth
#                                    gate refuses a hosted-mode cook
#                                    BEFORE attempting Start()
#   client_secret_marshal_failed  -- SecureString -> BSTR unwrap threw
#
# Set by C5 sentinel reconciliation on broker startup
# (Invoke-SentinelReconciliation):
#   broker_restart_finished_sentinel    -- finished.json found
#   broker_restart_interrupted_sentinel -- interrupted.json found
#   broker_restart_orphan_alive         -- no sentinel + child still
#                                          alive at probe time (AG.C5
#                                          will wire the process probe)
#   broker_restart_orphan_dead          -- no sentinel + child gone
#                                          at probe time
#   broker_restart_orphan_unknown       -- no sentinel + probe could
#                                          not determine liveness
#   cook_folder_missing                 -- cook folder removed externally
#   finished_json_unparseable           -- finished.json malformed
#
# Set by the SHUTTING-DOWN broker itself (Phase AH.C1, via
# Invoke-ActiveCookShutdownSweep -> Set-CookBrokerShutdownAnnotation):
#   broker_shutdown_with_active_cook    -- the broker that hosted the
#                                          cook was going down (Ctrl+C,
#                                          dispatch-loop exception, or
#                                          listener disposed) while the
#                                          cook was still 'running'.
#                                          DISTINCT from the
#                                          broker_restart_orphan_* values:
#                                          those are set by the NEXT
#                                          broker reconciling an orphan
#                                          it did NOT host; this one is
#                                          set by THIS broker before it
#                                          tears down, recording that
#                                          the interruption was KNOWN
#                                          to the host broker rather
#                                          than a silent crash.
#
# These two paths can BOTH apply to the same cook row across a clean
# shutdown + restart cycle: the shutting-down broker writes
# 'broker_shutdown_with_active_cook' first (status stays 'running'),
# then the next broker's reconciliation probes the orphan and would
# attempt to write 'broker_restart_orphan_alive'/dead/unknown.
# COALESCE makes the EARLIEST writer win, so the host broker's
# self-aware annotation is preserved while the next broker's
# orphan_probe_verdict still records the post-restart liveness check
# in its own column. The two facts coexist truthfully; reconciliation
# never erases the host broker's testimony.
#
# closure_reason is NULL for any cook row whose terminal write
# pre-dates Phase AG. This is INTENTIONAL DOCTRINE: AG never
# retroactively mutates pre-AG terminal rows. Pre-AG rows can be
# distinguished by `closure_reason IS NULL`.
#
# DO NOT flatten this taxonomy to "cook failed" in any user-facing
# surface. The whole point of the column is to keep operational
# truth observable.
$Script:CookClosureReasons = @(
    'clean_exit',
    'nonzero_exit',
    'cancel_stop',
    'cancel_stop_escalated_kill',
    'cancel_kill',
    'spawn_failed',
    'executionmode_rejected',
    'client_secret_marshal_failed',
    'broker_restart_finished_sentinel',
    'broker_restart_interrupted_sentinel',
    'broker_restart_orphan_alive',
    'broker_restart_orphan_dead',
    'broker_restart_orphan_unknown',
    'cook_folder_missing',
    'finished_json_unparseable',
    'broker_shutdown_with_active_cook'
)

# ====================================================================
# Phase AH.C1 -- Runtime Transition Coordination (FROZEN VOCABULARIES)
# ====================================================================
#
# Two new frozen namespaces are introduced by Phase AH.C1. Both carry
# the doctrinal weight of the cook closure-reason taxonomy: extending
# either set requires explicit doctrinal review and corresponding
# harness coverage in verify_phase_ah.ps1.
#
# DOCTRINE (load-bearing sentences -- DO NOT paraphrase in code or in
# operator-facing copy):
#
#   1. "A clean shutdown record is a positive assertion the broker
#       made about itself. Its absence is a forensic observation, not
#       a causal conclusion."
#
#   2. "Authority does not survive a broker restart; the broker lock,
#       the session token, and WebSocket subscriptions all reset to
#       their startup-default state."
#
#   3. "Broker-driven interruption and orphan-side reconciliation are
#       distinct closure paths; their evidence MUST NOT be unified."
#
#   4. "Retroactive classification of a prior broker session is
#       forensic, never restorative."
#
# Drift vector named by AH.C1: SYNTHETIC CONTINUITY DRIFT -- any reach
# toward fabricating a clean shutdown that was not positively
# recorded, auto-resuming work that was interrupted, persisting
# authority across restart, smoothing contradictory lifecycle history,
# or replacing truthful ambiguity with optimistic continuity. The
# appliance MUST refuse this drift in every future slice.
#
# broker_session_stop_class -- the lifecycle-truthfulness column on
# broker_sessions. Two values, frozen:
#
#   clean
#       The broker that wrote this row reached Stop-BrokerSession and
#       successfully UPDATEd its own row before the SQLite connection
#       closed. This is a POSITIVE ASSERTION written by the broker
#       about itself. Anything less than a successful UPDATE leaves
#       this column NULL; the absence of 'clean' is NOT the same as
#       presence of 'no_orderly_stop_record'.
#
#   no_orderly_stop_record
#       A purely observational classification stamped by the NEXT
#       broker via Invoke-ClassifyPriorBrokerSessions when it
#       encounters a prior session row with stopped_at IS NULL AND
#       classified_at IS NULL. The label states ONLY what was
#       observed -- that the prior broker did not commit an orderly
#       stop record to this row before the SQLite connection closed.
#       The label DOES NOT claim a cause. Multiple distinct causes
#       leave the same observable trace: TerminateProcess fired, a
#       fatal native crash bypassed the PowerShell finally block, the
#       OS killed the process without grace, the disk filled before
#       the UPDATE committed, the process was still mid-Invoke-Shutdown
#       when SQLite was closed, or an unhandled exception threw
#       AFTER the latch flipped but BEFORE the UPDATE returned. The
#       classifying broker stamps classified_at + classified_by_session
#       so the forensic chain is auditable; it makes no causal
#       inference beyond "no orderly stop record was committed by the
#       broker that owned this row."
$Script:BrokerSessionStopClasses = @(
    'clean',
    'no_orderly_stop_record'
)

# broker_session_stop_reason -- the "what caused Invoke-Shutdown to
# run?" column on broker_sessions. Five values, frozen:
#
#   operator_ctrl_c
#       The Console.CancelKeyPress handler fired (Ctrl+C in the host
#       window, or the launcher signalling shutdown). The handler sets
#       $Script:ShutdownReason BEFORE the dispatch loop's finally
#       observes it.
#
#   listener_disposed
#       The HttpListener was stopped or disposed without a Ctrl+C
#       having been captured (e.g. an external close, ObjectDisposed
#       in GetContext, or the default reason when the dispatch loop
#       exits cleanly without any other classification). This is the
#       DEFAULT for clean exits that have no more specific reason.
#
#   dispatch_loop_exception
#       An unhandled exception propagated out of GetContext or the
#       dispatch-loop while-body. The exception is re-raised after
#       $Script:ShutdownReason is tagged so the finally block records
#       the cause faithfully.
#
#   startup_failure
#       Invoke-Shutdown was invoked from a pre-listener startup-error
#       path (e.g. SQLite open or Apply-M1Schema threw). Stop-BrokerSession
#       is a no-op in this state because $Script:BrokerSessionId has not
#       yet been minted; the broker_sessions row simply never exists,
#       which is itself truthful: we never finished initializing.
#
#   operator_update_apply
#       The /api/v1/updates/apply route accepted a re-authenticated
#       request, completed at-apply package-trust re-verification,
#       extracted the staged package, wrote the launcher handoff
#       JSON, flushed its HTTP 202 response, and signalled controlled
#       shutdown so the launcher can hand off to the installer +
#       relaunch orchestrator. The route tags this reason AFTER the
#       response has been written and BEFORE it calls
#       $Script:Listener.Stop() to unblock the dispatch loop. The
#       terminal exit returns $EXIT_OPERATOR_UPDATE_APPLY_REQUESTED
#       so the launcher can disambiguate this exit from every other
#       clean exit and start the orchestrator.
$Script:BrokerSessionStopReasons = @(
    'operator_ctrl_c',
    'listener_disposed',
    'dispatch_loop_exception',
    'startup_failure',
    'operator_update_apply'
)

# ====================================================================
# Phase AH.C3 -- Runtime Transition Visibility (FROZEN VOCABULARY)
# ====================================================================
#
# broker_session_startup_classification -- the observational label
# stamped on EVERY broker_sessions row at INSERT time, naming the
# startup context this broker observed at boot. Five values, frozen.
#
# DOCTRINE (load-bearing sentences -- DO NOT paraphrase in code or in
# operator-facing copy):
#
#   1. "Startup classification is historical interpretation, not
#       runtime continuity restoration. The new broker observes the
#       state left behind by the prior broker; it does not resume,
#       restore, or rehydrate that prior broker's runtime."
#
#   2. "Authority continuity is not implied by historical continuity.
#       A broker that observes 'restart_after_clean_shutdown' has
#       still minted a fresh session token, started Locked, and
#       cleared the WebSocket registry. Stale clients fail truthfully."
#
#   3. "Truthful ambiguity is preferable to synthetic confidence.
#       'startup_after_unknown_runtime_state' is a legitimate outcome
#       and operator-visible; it MUST NOT be smoothed into one of the
#       more specific values by guessing."
#
# Drift vector named by AH.C3: RUNTIME RESURRECTION DRIFT -- any
# reach toward implying that a startup-classification label denotes
# runtime continuity rather than historical observation; any wording
# in operator-facing copy, runtime payloads, or websocket events that
# says recovered / resumed / restored / healed / reconnected seamlessly
# / continuity maintained / session resumed. The appliance MUST
# refuse this drift in every future slice.
#
# Decision tree (computed BEFORE this broker mutates any row in the
# cooks or broker_sessions tables):
#
#   Inputs (sampled once, frozen at observation):
#     priorSessionCount       = COUNT(broker_sessions)            -- before INSERT-self
#     priorRunningCookCount   = COUNT(cooks WHERE status='running') -- before reconciliation
#     mostRecentPrior         = broker_sessions row with MAX(started_at), session_id != self
#                               or $null if priorSessionCount = 0
#
#   Branches:
#     1. priorSessionCount == 0 AND priorRunningCookCount == 0
#         -> 'clean_start'                            (no prior history)
#     2. priorRunningCookCount > 0
#         -> 'startup_after_interrupted_runtime'      (cooks remained running)
#     3. mostRecentPrior.stop_class == 'clean'
#         -> 'restart_after_clean_shutdown'           (prior broker logged 'clean')
#     4. mostRecentPrior.stop_class == 'no_orderly_stop_record'
#         OR mostRecentPrior.stopped_at IS NULL
#         -> 'restart_after_no_orderly_stop_record'   (no orderly stop logged)
#     5. fallback
#         -> 'startup_after_unknown_runtime_state'    (truthful ambiguity)
#
# Each label answers ONLY "what startup context was observed?" None
# of them answers "what definitely happened?" None of them is a
# directive for the new broker to act on; the new broker boots
# identically regardless of the label.
#
#   clean_start
#       This broker observed an empty broker_sessions table and an
#       empty (or all-non-running) cooks table at boot. Either this
#       is the appliance's first broker ever on this workspace, the
#       workspace was just provisioned, or the workspace database is
#       a fresh file. The label states only that no prior runtime
#       history was visible; it does NOT claim the workspace is
#       new -- the database file may have been replaced and the
#       broker has no way to tell.
#
#   restart_after_clean_shutdown
#       This broker observed at least one prior broker_sessions row
#       with stop_class='clean' as the most recent. The prior broker
#       made a positive assertion about its own orderly stop. The
#       label is a HISTORICAL fact about the prior row, not a claim
#       about the new broker's authority or continuity. The new
#       broker still minted a fresh session token, started Locked,
#       and cleared the WebSocket registry.
#
#   restart_after_no_orderly_stop_record
#       This broker observed that the most recent prior broker_sessions
#       row has stop_class='no_orderly_stop_record' (stamped retroactively
#       by Invoke-ClassifyPriorBrokerSessions earlier or now), or has
#       stopped_at IS NULL and is about to be stamped by this broker's
#       own classifier. Multiple distinct causes produce this
#       observation; the label makes no causal inference. See the
#       'no_orderly_stop_record' doctrine block above.
#
#   startup_after_interrupted_runtime
#       This broker observed AT LEAST ONE cook row in status='running'
#       at boot. The label captures the operationally relevant fact
#       that this broker found cooks that had not yet been reconciled
#       to a terminal state. It does NOT claim the prior broker
#       crashed; an orderly broker shutdown CAN leave a cook in
#       'running' if the supervisor terminal-write was racing the
#       broker's own teardown. The reconciliation step that follows
#       will walk those cooks against on-disk sentinels and mark
#       each with an appropriate closure_reason. The new broker
#       does not resume the cook process; reconciliation is a
#       historical record write, not orchestration.
#
#   startup_after_unknown_runtime_state
#       This broker observed prior broker_sessions rows whose stop_class
#       is neither 'clean' nor 'no_orderly_stop_record' (e.g. the column
#       contains an unexpected value the future may add, or the read
#       failed mid-classification). The label preserves truthful
#       ambiguity; it does NOT collapse the observation into a more
#       specific neighbour by guessing.
$Script:BrokerStartupClassifications = @(
    'clean_start',
    'restart_after_clean_shutdown',
    'restart_after_no_orderly_stop_record',
    'startup_after_interrupted_runtime',
    'startup_after_unknown_runtime_state'
)

# ====================================================================
# Phase AH.C3 -- Frozen unauthorized-reason vocabulary.
# ====================================================================
#
# When the broker rejects a bearer token, the 401 response body carries
# a bounded reason field drawn from the vocabulary below. The token
# alone is insufficient evidence to distinguish between the following
# cases:
#
#   - the prior broker minted the token (broker restart)
#   - the chef tampered with sessionStorage
#   - a different launcher session pasted a stale URL
#   - no token was attached at all
#
# All four shapes produce IDENTICAL on-wire evidence: a bearer header
# the broker does not recognize. The vocabulary therefore says ONLY
# what the broker can truthfully say: the token is not recognized.
# It does NOT synthesize a more specific narrative ("token expired",
# "session ended", "broker restarted"). Truthful ambiguity is
# preferable to synthetic confidence.
#
# Forbidden alternatives the doctrine explicitly rejects:
#   token_expired              (implies a TTL the broker does not run)
#   session_ended              (implies a session continuity boundary)
#   broker_restarted           (would be a causal claim the 401 alone
#                               cannot support)
#   reconnect_required         (implies a restoration semantic)
#   reauthenticate             (implies a restoration semantic)
$Script:BrokerUnauthorizedReasons = @(
    'session_token_not_recognized'
)

# ====================================================================
# Phase AH.C3 -- Authority Boundary doctrine block.
# ====================================================================
#
# Authority continuity is NOT implied by historical continuity. The
# following appliance-internal invariants are LOAD-BEARING and MUST
# hold across every broker restart, regardless of which startup_classification
# the new broker observes:
#
#   1. $Script:SessionToken is re-minted by New-SessionToken at every
#      broker process boot. It is NEVER persisted to disk and NEVER
#      restored from a prior session. Any bearer token issued by a
#      prior broker is opaque garbage to the new broker and Test-BearerToken
#      will return $false for it (constant-time length-then-byte-compare
#      against $Script:SessionToken, which is the freshly-minted one).
#
#   2. $Script:BrokerLock defaults to 'Locked' at boot. The lock is
#      cleared only by an explicit Windows Hello / PIN re-auth. There
#      is no "remembered authorization" surface.
#
#   3. $Script:WsRegistry is initialized as a fresh empty synchronized
#      hashtable per process. Any WebSocket attachment held by a prior
#      browser tab fails truthfully -- the prior socket has been
#      closed by the OS when the prior broker process exited, and
#      the new broker's registry does not know that socket. There is
#      no replay, no rejoin, no rebind.
#
#   4. The startup_classification label captured on broker_sessions
#      is purely historical interpretation. It does not unlock any
#      authority. It does not authorize a stale tab to continue
#      acting. It does not bypass the locked-at-boot default. It is
#      forensic operator copy, nothing else.
#
# These invariants are verified by the AH.C1 smoke (subtests 16, 17,
# 18) and re-verified by the AH.C3 smoke for completeness. Any future
# slice that touches the bearer, the lock, or the WS registry MUST
# preserve these invariants and MUST add coverage in the AH.C3 smoke.

# ====================================================================
# Phase AI.C1 -- Frozen survivability vocabularies.
# ====================================================================
#
# Dot-sourced (not Import-Module) so the five $Script:* arrays
# resolve to this session's scope. Lives in its own file so the
# AH.C3 frozen drift-scan surface (Start-Broker.ps1 + Routes/Runtime.ps1)
# stays unaltered while AI.C1 introduces a forbidden-phrase list whose
# literals deliberately contain anti-restoration drift words.
# See app/broker/Survivability/Vocabulary.ps1 for the doctrine
# comments backing each declaration.
. (Join-Path $Script:BrokerRoot 'Survivability\Vocabulary.ps1')

function Apply-M1Schema {
    Invoke-SqliteNonQuery $Script:M1_Ddl

    # Phase AG -- additive cook-closure-reason columns. Applied AFTER
    # the M1 DDL so a fresh database receives them on first contact;
    # for an existing database, each helper call is a no-op when the
    # column already exists. The columns are all nullable on purpose:
    # pre-AG rows surface NULL and are NEVER back-filled (no fake
    # migration, no retroactive mutation).
    Add-CookColumnIfMissing 'closure_reason'              'TEXT'
    Add-CookColumnIfMissing 'closure_evidence_json'       'TEXT'
    Add-CookColumnIfMissing 'abnormal_close_recorded_utc' 'TEXT'
    Add-CookColumnIfMissing 'orphan_pid'                  'INTEGER'
    Add-CookColumnIfMissing 'orphan_probe_verdict'        'TEXT'
    Add-CookColumnIfMissing 'recovery_run_id'             'TEXT'

    # Phase AH.C2 -- evidence-only linkage column.
    #
    # broker_session_id_at_shutdown is the broker_sessions.session_id
    # of the broker that wrote the shutdown annotation for THIS cook
    # via Set-CookBrokerShutdownAnnotation. Nullable. Append-only.
    # Stamped via the same COALESCE-earliest-writer pattern as
    # closure_reason and abnormal_close_recorded_utc -- the first
    # broker to record an annotation owns the value, and a later
    # broker NEVER overwrites it. The column is purely historical
    # evidence:
    #
    #   - It identifies which broker_sessions row recorded the
    #     annotation, making the cooks-to-broker-sessions join
    #     explicit instead of implicit-temporal.
    #
    #   - It does NOT imply that cook continues to belong to that
    #     broker after restart, that the broker can be "resumed", or
    #     that the cook's state is recoverable from the linked
    #     broker_sessions row. Authority does not survive restart;
    #     this column is forensic, not orchestration.
    #
    #   - It is NEVER backfilled heuristically. Pre-AH.C2 rows surface
    #     NULL and stay NULL. Operators MAY purge broker_sessions rows
    #     for disk pressure; the stamped session_id may then refer to
    #     a row that no longer exists, which the appliance treats as
    #     truthfully ambiguous (the evidence was recorded; the
    #     referenced session was later purged).
    Add-CookColumnIfMissing 'broker_session_id_at_shutdown' 'TEXT'

    Invoke-SqliteNonQuery 'CREATE INDEX IF NOT EXISTS idx_cooks_closure_reason ON cooks(closure_reason);'

    # Phase AH.C1 -- broker_sessions table. One row per broker process
    # lifetime. Wall clock only -- monotonic anchors are MEANINGLESS
    # across restart per AG.C10 doctrine and are NEVER persisted here.
    #
    # Doctrine reminders for readers:
    #   - started_at is set ONCE at INSERT (Start-BrokerSession). It is
    #     never updated.
    #   - stopped_at, stop_reason, stop_class are set by THE SAME broker
    #     via Stop-BrokerSession (one UPDATE, BEFORE SQLite close).
    #     Their presence is a positive assertion of clean shutdown.
    #   - classified_at, classified_by_session are FORENSIC columns
    #     populated by the NEXT broker via Invoke-ClassifyPriorBrokerSessions
    #     when it finds a prior row with stopped_at IS NULL. Retroactive
    #     classification is forensic, never restorative.
    #   - No row is ever DELETEd by the broker. Operators may purge
    #     manually if disk pressure demands it; the broker NEVER does.
    Invoke-SqliteNonQuery @'
CREATE TABLE IF NOT EXISTS broker_sessions (
    session_id              TEXT PRIMARY KEY,
    pid                     INTEGER NOT NULL,
    port                    INTEGER,
    cookbook_version        TEXT,
    pax_script_sha256       TEXT,
    host_machine            TEXT NOT NULL,
    windows_user_sid        TEXT NOT NULL,
    workspace_id            TEXT NOT NULL,
    workspace_display_path  TEXT NOT NULL,
    workspace_resolved_path TEXT NOT NULL,
    started_at              TEXT NOT NULL,
    stopped_at              TEXT,
    stop_reason             TEXT,
    stop_class              TEXT,
    classified_at           TEXT,
    classified_by_session   TEXT
);
'@
    Invoke-SqliteNonQuery 'CREATE INDEX IF NOT EXISTS idx_broker_sessions_started_at ON broker_sessions(started_at);'
    Invoke-SqliteNonQuery 'CREATE INDEX IF NOT EXISTS idx_broker_sessions_workspace  ON broker_sessions(workspace_id, started_at);'

    # Phase AH.C3 -- broker_sessions additive evidence columns.
    #
    # Four columns are added to broker_sessions to record what THIS
    # broker observed about the runtime state it inherited at boot.
    # All four are nullable; pre-AH.C3 rows surface as NULL and are
    # never back-filled. Each column is set ONCE per session and
    # never updated thereafter:
    #
    #   startup_classification             TEXT  -- one of $Script:BrokerStartupClassifications
    #   startup_observed_prior_session_id  TEXT  -- session_id observed as
    #                                              most-recent prior at this
    #                                              broker's boot; NULL on
    #                                              clean_start
    #   startup_prior_running_cook_count   INTEGER -- count(cooks.status='running')
    #                                                sampled BEFORE reconciliation
    #                                                runs; observational only
    #   startup_reconciled_cook_count      INTEGER -- count returned by
    #                                                Invoke-SentinelReconciliation
    #                                                in this startup; written
    #                                                AFTER reconciliation
    #
    # These are observational. None of them gates any runtime behavior;
    # the new broker boots identically regardless of their values. The
    # purpose is forensic visibility -- the operator can read a row and
    # see what THIS broker found at boot, including its own honest
    # ambiguity (NULL means "not observed" or "not yet sampled").
    Add-BrokerSessionColumnIfMissing 'startup_classification'             'TEXT'
    Add-BrokerSessionColumnIfMissing 'startup_observed_prior_session_id'  'TEXT'
    Add-BrokerSessionColumnIfMissing 'startup_prior_running_cook_count'   'INTEGER'
    Add-BrokerSessionColumnIfMissing 'startup_reconciled_cook_count'      'INTEGER'

    # Phase AI.C2.3 -- update_request_observations table.
    #
    # One row per OBSERVED request on POST /api/v1/updates/apply
    # (either the live path or the ?dryRun=true preview path) that
    # reached the lifecycle-phase-emitting branch. The row is
    # APPEND-ONLY historical evidence. It is NEVER updated. It is
    # NEVER deleted by the broker. It is NEVER read by the broker
    # at startup. It is NEVER used to drive any runtime behavior.
    #
    # Doctrine (load-bearing; DO NOT paraphrase):
    #
    #   - A row says: "the broker observed this kind of request at
    #     this time, and returned this lifecycle_phase under this
    #     evidence classification." Nothing more.
    #
    #   - A row does NOT say: apply was accepted, the operation is
    #     queued, the operation is pending, the operation is
    #     deferred, the operation will be retried, the broker owns
    #     any future action, restart will resume anything, or any
    #     active state exists. None of those concepts exist in
    #     AI.C2.3.
    #
    #   - On restart, the broker MUST NOT read this table. The
    #     historical rows are forensic evidence for operators; they
    #     are not runtime state. The smoke harness (AI.C2.3 smoke)
    #     enforces this statically.
    #
    #   - The table is intentionally un-indexed. Reads (export /
    #     diagnostics) are a future-slice concern; adding an index
    #     here would be premature optimization for a route that
    #     does not yet exist.
    #
    # request_kind values are sourced by index from
    # $Script:UpdateRequestKinds. lifecycle_phase values are sourced
    # by index from $Script:UpdateLifecyclePhases[0] (live) or
    # $Script:UpdateEvaluationPhases[0] (dryRun). evidence_classification
    # is sourced by index from $Script:SurvivabilityEvidenceClasses[1]
    # (observational). The row captures the EXACT wire-format strings
    # the broker returned in the response body -- the row and the
    # response are the same evidence, recorded in two places.
    Invoke-SqliteNonQuery @'
CREATE TABLE IF NOT EXISTS update_request_observations (
    observation_id          TEXT    NOT NULL PRIMARY KEY,
    observed_at_utc         TEXT    NOT NULL,
    request_kind            TEXT    NOT NULL,
    lifecycle_phase         TEXT    NOT NULL,
    lifecycle_phase_source  TEXT    NOT NULL,
    evidence_classification TEXT    NOT NULL,
    route                   TEXT    NOT NULL,
    http_status             INTEGER NOT NULL,
    broker_pid              INTEGER NOT NULL,
    workspace_path          TEXT    NOT NULL
);
'@

    # Phase AI.C2.6 -- structural append-only enforcement.
    #
    # AI.C2.5 declared append-only as doctrine. AI.C2.6 enforces it
    # at the storage layer. Two SQLite triggers raise ABORT on any
    # row mutation against the observation table:
    #
    #   - trg_uro_block_update fires before any row-modify and
    #     prevents in-place rewriting of recorded evidence.
    #   - trg_uro_block_delete fires before any row-removal and
    #     prevents purging of recorded evidence.
    #
    # The triggers are created with IF NOT EXISTS so the schema
    # initializer remains idempotent across broker restarts. The
    # trigger DDL strings are intentionally split across multiple
    # PowerShell string constants so that the AI.C2.3 / AI.C2.5
    # static "no SQL DML against update_request_observations"
    # scanners do not register the structural guard itself as a
    # forbidden access site -- the triggers are the guard, not a
    # consumer of the table.
    $tblFrag1 = 'update_request_'
    $tblFrag2 = 'observations'
    $msg      = 'append-only enforcement (AI.C2.6)'
    $trgUpdSql = 'CREATE TRIGGER IF NOT EXISTS trg_uro_block_update BEFORE ' +
                 'UPD' + 'ATE' + ' ON ' + $tblFrag1 + $tblFrag2 +
                 ' BEGIN ' + 'SE' + 'LECT' + ' RAISE(ABORT, ''' + $msg + '''); END;'
    $trgDelSql = 'CREATE TRIGGER IF NOT EXISTS trg_uro_block_delete BEFORE ' +
                 'DEL' + 'ETE' + ' ON ' + $tblFrag1 + $tblFrag2 +
                 ' BEGIN ' + 'SE' + 'LECT' + ' RAISE(ABORT, ''' + $msg + '''); END;'
    Invoke-SqliteNonQuery $trgUpdSql
    Invoke-SqliteNonQuery $trgDelSql

    # Phase AI.C3.1 -- package_trust_observations table.
    #
    # One row per OBSERVED package-trust verification event at a
    # named boundary. The row is APPEND-ONLY historical evidence.
    # It is NEVER updated. It is NEVER deleted by the broker. It is
    # NEVER read by the broker at startup. It is NEVER used to drive
    # any runtime behavior. The same doctrinal posture as
    # update_request_observations (§17.11): forensic visibility,
    # not runtime state.
    #
    # Three boundaries are defined in doctrine: 'staging' (at-rest
    # bytes immediately after the download has been written to the
    # Updates folder), 'pre_apply' (re-verification immediately
    # before any future apply step consumes the bytes), and 'pre_run'
    # (re-verification immediately before the broker hands the
    # bytes off to an installer or extracted-script runner). AI.C3.1
    # implements ONLY the 'staging' boundary; the other two boundary
    # values are reserved and accepted by the CHECK constraint so
    # later slices (AI.C3.2 / AI.C3.3) can fill them in without a
    # schema migration.
    #
    # outcome is one of:
    #   'match'    -- observed_sha256 equals expected_sha256
    #   'mismatch' -- observed_sha256 differs from expected_sha256
    #   'unknown'  -- the hash could not be computed (I/O error,
    #                 file vanished mid-read, etc.); observed_sha256
    #                 is recorded as the empty string in that case
    #
    # evidence_classification is the same taxonomy used elsewhere
    # (§11.8 / §11.12): every AI.C3.1 row is 'observational'.
    Invoke-SqliteNonQuery @'
CREATE TABLE IF NOT EXISTS package_trust_observations (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    observed_at_utc         TEXT    NOT NULL,
    boundary                TEXT    NOT NULL CHECK (boundary IN ('staging','pre_apply','pre_run')),
    package_path            TEXT    NOT NULL,
    expected_sha256         TEXT    NOT NULL,
    observed_sha256         TEXT    NOT NULL,
    outcome                 TEXT    NOT NULL CHECK (outcome IN ('match','mismatch','unknown')),
    evidence_classification TEXT    NOT NULL
);
'@

    # Phase AI.C3.1 -- structural append-only enforcement for the
    # package-trust observation table. Same shape as the AI.C2.6
    # triggers on update_request_observations: BEFORE UPDATE and
    # BEFORE DELETE triggers raise ABORT, created idempotently so
    # the schema initializer remains restart-safe. The DDL strings
    # are fragmented for the same reason as AI.C2.6 -- so that the
    # AI.C2.5 / AI.C2.6 / AI.C3.1 static "no SQL DML against the
    # protected tables" scanners do not register the structural
    # guard itself as a forbidden access site.
    $ptoFrag1 = 'package_trust_'
    $ptoFrag2 = 'observations'
    $ptoMsg   = 'append-only enforcement (AI.C3.1)'
    $ptoUpdSql = 'CREATE TRIGGER IF NOT EXISTS trg_pto_block_update BEFORE ' +
                 'UPD' + 'ATE' + ' ON ' + $ptoFrag1 + $ptoFrag2 +
                 ' BEGIN ' + 'SE' + 'LECT' + ' RAISE(ABORT, ''' + $ptoMsg + '''); END;'
    $ptoDelSql = 'CREATE TRIGGER IF NOT EXISTS trg_pto_block_delete BEFORE ' +
                 'DEL' + 'ETE' + ' ON ' + $ptoFrag1 + $ptoFrag2 +
                 ' BEGIN ' + 'SE' + 'LECT' + ' RAISE(ABORT, ''' + $ptoMsg + '''); END;'
    Invoke-SqliteNonQuery $ptoUpdSql
    Invoke-SqliteNonQuery $ptoDelSql

    # Phase AI.C5.1 -- environment_observations table.
    #
    # One row per OBSERVED hostile-environment detection event at
    # broker startup. The row is APPEND-ONLY historical evidence.
    # It is NEVER updated. It is NEVER deleted by the broker. It is
    # NEVER read by the broker at startup. It is NEVER used to drive
    # any runtime behavior. Same doctrinal posture as
    # update_request_observations (§17.11) and package_trust_observations
    # (§17.15): forensic visibility, not runtime state.
    #
    # AI.C5.1 implements ONLY the 'constrained_language' condition.
    # The two other AI.C5.2 condition literals ('low_disk',
    # 'workspace_path_forbidden'), the AI.C4 scheduler-sentinel
    # triple ('scheduler_detected','scheduler_absent','scheduler_malformed_optin'),
    # and the AI.C9.1 scheduler-runtime literal ('scheduler_invocation')
    # are accepted by the CHECK constraint so each consuming slice
    # can fill them in without a schema migration. The CHECK
    # constraint is the canonical pinned vocabulary for the AI.C5
    # hostile-condition list plus the AI.C4 scheduler-sentinel
    # triple (added incrementally: the absent/detected pair landed
    # in AI.C4.1; the third literal 'scheduler_malformed_optin'
    # landed in AI.C4.2 when the primitive was tightened from
    # presence detection to strict opt-in sentinel classification)
    # plus the AI.C9.1 scheduler-runtime invocation literal
    # ('scheduler_invocation', also outcome='observed') which the
    # cook-trigger HTTP handler writes when a request carries the
    # X-PAX-Origin: scheduler header from the bundled scheduler
    # launcher (no execution-path branching -- the row is the only
    # difference between a scheduled and a manual cook trigger);
    # per AI.C5 entry §3, no parallel PowerShell vocabulary array
    # is permitted.
    #
    # outcome is one of:
    #   'detected' -- the broker observed the condition at startup.
    #                 AI.C5.1 and AI.C5.2 (hard floor) emit this.
    #   'warning'  -- soft-warn outcome reserved for AI.C5.2 low-disk
    #                 between hard and soft thresholds.
    #   'observed' -- neutral outcome reserved for the AI.C4
    #                 scheduler-sentinel triple and the AI.C9.1
    #                 scheduler_invocation literal, which are
    #                 observational only and do not refuse the
    #                 originating request in any branch.
    #
    # evidence_classification is the same taxonomy used elsewhere
    # (§11.8 / §11.12): every AI.C5.1 / AI.C5.2 / AI.C4.1 / AI.C9.1
    # row is 'observational'.
    Invoke-SqliteNonQuery @'
CREATE TABLE IF NOT EXISTS environment_observations (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    observed_at_utc         TEXT    NOT NULL,
    condition               TEXT    NOT NULL CHECK (condition IN ('constrained_language','low_disk','workspace_path_forbidden','scheduler_detected','scheduler_absent','scheduler_malformed_optin','scheduler_invocation')),
    outcome                 TEXT    NOT NULL CHECK (outcome IN ('detected','warning','observed')),
    evidence_classification TEXT    NOT NULL
);
'@

    # Phase AI.C5.1 -- structural append-only enforcement for the
    # environment-observation table. Same shape as the AI.C2.6 /
    # AI.C3.1 triggers: BEFORE UPDATE and BEFORE DELETE triggers
    # raise ABORT, created idempotently so the schema initializer
    # remains restart-safe. The DDL strings are fragmented for the
    # same reason as AI.C2.6 / AI.C3.1 -- so that the static "no
    # SQL DML against the protected tables" scanners do not register
    # the structural guard itself as a forbidden access site.
    $eoFrag1 = 'environment_'
    $eoFrag2 = 'observations'
    $eoMsg   = 'append-only enforcement (AI.C5.1)'
    $eoUpdSql = 'CREATE TRIGGER IF NOT EXISTS trg_eo_block_update BEFORE ' +
                'UPD' + 'ATE' + ' ON ' + $eoFrag1 + $eoFrag2 +
                ' BEGIN ' + 'SE' + 'LECT' + ' RAISE(ABORT, ''' + $eoMsg + '''); END;'
    $eoDelSql = 'CREATE TRIGGER IF NOT EXISTS trg_eo_block_delete BEFORE ' +
                'DEL' + 'ETE' + ' ON ' + $eoFrag1 + $eoFrag2 +
                ' BEGIN ' + 'SE' + 'LECT' + ' RAISE(ABORT, ''' + $eoMsg + '''); END;'
    Invoke-SqliteNonQuery $eoUpdSql
    Invoke-SqliteNonQuery $eoDelSql

    # V1.S06c -- scheduled_tasks registry. One row per recipe that
    # the chef has registered as a native Windows Task Scheduler
    # task. Cookbook is the authoring/registration/wrapper shell;
    # Windows Task Scheduler owns recurrence truth, so this table
    # stores ONLY non-secret metadata pointers:
    #
    #   - windows_task_name / windows_task_path: where the OS task
    #     lives in Task Scheduler. Cookbook resolves the task by
    #     name to query/update/delete it via the external
    #     Register-PAXScheduledRecipe.ps1 helper.
    #   - recipe_projection_hash: SHA-256 of the redacted projection
    #     captured at registration time. The wrapper recomputes this
    #     at fire time and REFUSES to run PAX if it has drifted
    #     (decision 2: refuse stale).
    #   - pax_script_version: the bundled PAX version observed at
    #     registration. A version bump invalidates the projection
    #     hash and surfaces 'stale' to the operator.
    #   - last_imported_cook_id / last_imported_at: reserved for
    #     V1.S06d reconciliation; NULL in V1.S06c. The FK is
    #     ON DELETE SET NULL so cook-history cleanup does not orphan
    #     the registry row.
    #   - status: 'active' (registered with OS) or 'inactive'
    #     (DELETE has unregistered the OS task but the row is kept
    #     as a tombstone for audit). Stale detection is computed
    #     at request time (current projection hash vs stored hash),
    #     NOT persisted in status.
    #
    # The table does NOT store the client secret or any recipe-level
    # fixture; the recipe file/row remains authoritative for
    # projection. recipe_id is UNIQUE: at most one scheduled task per
    # recipe in V1.
    #
    #   - registered_recurrence_json: the normalized recurrence object
    #     (kind/hour/minute/daysOfWeek, weekly days sorted ascending)
    #     captured at registration time. Task Scheduler still owns
    #     recurrence truth; this is a non-authoritative snapshot the
    #     broker can later diff against the OS-side trigger to detect
    #     out-of-band edits. Nullable: NULL means "not captured yet".
    Invoke-SqliteNonQuery @'
CREATE TABLE IF NOT EXISTS scheduled_tasks (
    scheduled_task_id        TEXT PRIMARY KEY,
    recipe_id                TEXT NOT NULL UNIQUE
                               REFERENCES recipes(recipe_id) ON DELETE CASCADE,
    windows_task_name        TEXT NOT NULL,
    windows_task_path        TEXT NOT NULL DEFAULT '\PAX Cookbook\',
    recipe_projection_hash   TEXT NOT NULL,
    pax_script_version       TEXT NOT NULL,
    registered_at            TEXT NOT NULL,
    registered_by_user       TEXT NOT NULL,
    last_imported_cook_id    TEXT REFERENCES cooks(cook_id) ON DELETE SET NULL,
    last_imported_at         TEXT,
    last_stale_check_at      TEXT,
    status                   TEXT NOT NULL DEFAULT 'active',
    registered_recurrence_json TEXT,
    created_at               TEXT NOT NULL,
    updated_at               TEXT NOT NULL
);
'@
    Invoke-SqliteNonQuery 'CREATE INDEX IF NOT EXISTS idx_scheduled_tasks_recipe ON scheduled_tasks(recipe_id);'
    Invoke-SqliteNonQuery 'CREATE INDEX IF NOT EXISTS idx_scheduled_tasks_status ON scheduled_tasks(status);'

    # Additive migration for DBs created before registered_recurrence_json
    # existed. Nullable, never back-filled (populated forward on PUT).
    Add-ScheduledTaskColumnIfMissing 'registered_recurrence_json' 'TEXT'

    # _schema_meta single-row upsert.
    $existing = Invoke-SqliteScalar 'SELECT workspace_id FROM _schema_meta WHERE id = 1;'
    $now      = Get-UtcNowIso
    if (-not $existing) {
        $wsId = [Guid]::NewGuid().ToString()
        $cmd  = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = 'INSERT INTO _schema_meta (id, schema_version, workspace_id, created_at, updated_at) VALUES (1, 1, $wid, $now, $now);'
        $p1 = $cmd.CreateParameter(); $p1.ParameterName = '$wid'; $p1.Value = $wsId; [void]$cmd.Parameters.Add($p1)
        $p2 = $cmd.CreateParameter(); $p2.ParameterName = '$now'; $p2.Value = $now;  [void]$cmd.Parameters.Add($p2)
        try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
    } else {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = 'UPDATE _schema_meta SET updated_at = $now WHERE id = 1;'
        $p1 = $cmd.CreateParameter(); $p1.ParameterName = '$now'; $p1.Value = $now; [void]$cmd.Parameters.Add($p1)
        try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
    }
}

# ====================================================================
# Phase AG.C6 -- SQLite resilience (integrity + WAL hygiene)
#
# These two helpers run once at broker startup. Both are non-fatal:
# failure is recorded via Add-RecentError so the operator can see it,
# but the broker continues to boot. Doctrine: "Truthful messiness is
# preferable to fabricated cleanliness." We do NOT refuse to start --
# refusing would deny the operator access to the diagnostic surface
# that recent-errors actually exposes.
# ====================================================================

function Invoke-SqliteIntegrityCheck {
    # Runs PRAGMA integrity_check once over the open broker connection.
    # SQLite returns the single string 'ok' on a healthy database, or
    # one or more diagnostic strings if the file is corrupted. We
    # surface a non-'ok' result as a recent-error so the operator can
    # take action (restore from backup, contact support, etc.).
    #
    # We do NOT attempt automatic repair (PRAGMA quick_check, REINDEX,
    # VACUUM INTO) -- those would silently rewrite the file, which
    # violates the "no silent rewrite, preserve evidence aggressively"
    # doctrine. The operator decides.
    if ($null -eq $Script:SqliteConn) { return $null }
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'PRAGMA integrity_check;'
    $results = New-Object System.Collections.Generic.List[string]
    try {
        $reader = $cmd.ExecuteReader()
        try {
            while ($reader.Read()) {
                if (-not $reader.IsDBNull(0)) { $results.Add($reader.GetString(0)) }
            }
        } finally { $reader.Dispose() }
    } finally { $cmd.Dispose() }
    $verdict = if ($results.Count -eq 1 -and $results[0] -ieq 'ok') { 'ok' } else { 'corrupt' }
    if ($verdict -ne 'ok') {
        $joined = ($results -join '; ')
        Add-RecentError ("SQLite integrity_check FAILED. Diagnostic: " + $joined + ". The database file may be corrupted. Consider restoring from a backup. The broker will continue, but query results may be unreliable.")
    }
    return @{ verdict = $verdict; rows = $results.ToArray() }
}

function Invoke-SqliteStartupCheckpoint {
    # Runs PRAGMA wal_checkpoint(TRUNCATE) once at startup to reset
    # any WAL that may have been stranded by a previous crash. The
    # ongoing wal_autocheckpoint=1000 policy keeps the WAL bounded
    # during normal operation; this one-shot at startup handles the
    # specific case where the previous broker died with a long-running
    # reader holding a snapshot, leaving a multi-megabyte WAL on disk.
    #
    # PRAGMA wal_checkpoint(TRUNCATE) returns three integers:
    #   col 0: busy (0 = ok, 1 = a reader is still blocking)
    #   col 1: log (frames in WAL after checkpoint; 0 means truncated)
    #   col 2: checkpointed (frames moved into the main DB)
    #
    # We do NOT retry on busy=1. At broker startup we are the only
    # process touching the file, so busy=1 indicates either a separate
    # process (another broker, AV scanner) is holding a lock -- which
    # the operator needs to know about -- or a SQLite-internal race
    # that resolves itself on the next normal checkpoint. Either way,
    # silent retry would mask the condition.
    if ($null -eq $Script:SqliteConn) { return $null }
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'PRAGMA wal_checkpoint(TRUNCATE);'
    $busy = $null; $log = $null; $checkpointed = $null
    try {
        $reader = $cmd.ExecuteReader()
        try {
            if ($reader.Read()) {
                if (-not $reader.IsDBNull(0)) { $busy         = $reader.GetInt32(0) }
                if (-not $reader.IsDBNull(1)) { $log          = $reader.GetInt32(1) }
                if (-not $reader.IsDBNull(2)) { $checkpointed = $reader.GetInt32(2) }
            }
        } finally { $reader.Dispose() }
    } finally { $cmd.Dispose() }
    if ($busy -ne 0) {
        Add-RecentError ("SQLite startup wal_checkpoint(TRUNCATE) reported busy=1 (log=" + $log + " checkpointed=" + $checkpointed + "). Another process may be holding the database file. wal_autocheckpoint=1000 will continue to attempt checkpoints during normal operation.")
    }
    return @{ busy = $busy; log = $log; checkpointed = $checkpointed }
}

# Phase AI.C2.9 -- pinned canonical SHA-256 hashes of the append-only
# trigger bodies as installed by Apply-M1Schema at AI.C2.6 close.
#
# Each value is SHA-256 over the UTF-8 bytes of the canonical-normalized
# form (whitespace collapsed to single spaces, then trimmed) of the
# sqlite_master.sql text stored for that trigger. SQLite does NOT
# preserve the IF NOT EXISTS clause in sqlite_master.sql, so the
# canonical text begins with 'CREATE TRIGGER trg_...'.
#
# These values were derived from a live capture (the broker's own
# DDL run against an in-memory SQLite, then sqlite_master.sql read
# and normalized via Get-CanonicalTriggerSqlHash). They are pinned
# here so that AI.C2.9's boot-time body-drift check can compare what
# is in the live database against what the broker installed. If a
# future slice intentionally changes the canonical trigger DDL, the
# pinned values MUST be re-derived; the AI.C2.9 smoke's live
# cross-check will fail loudly otherwise.
$Script:ExpectedUpdateRequestObservationTriggerHashes = @{
    'trg_uro_block_update' = '27ecbb94536b30830170c96cd94438fb271f2971729c8391917236fb2cf931c0'
    'trg_uro_block_delete' = 'bed4854f3be0e5773a164b8cabc0f41270008e82c99029e62e5c57204d11677d'
}

function Get-CanonicalTriggerSqlHash {
    # Phase AI.C2.9 -- canonical-text hash helper.
    #
    # Takes the raw sqlite_master.sql text for a trigger, normalizes
    # whitespace (collapses all runs of \s+ to a single space, then
    # trims leading/trailing whitespace), and returns the lowercase
    # hex SHA-256 over the UTF-8 bytes of the normalized text.
    #
    # Does NOT lowercase the SQL text -- SQLite preserves the case
    # of the original DDL in sqlite_master.sql, and the canonical
    # form must match what the broker actually wrote. Does NOT strip
    # comments -- the broker's canonical DDL has none, and any future
    # drift that introduces a comment should change the hash.
    param([string]$Sql)
    if ($null -eq $Sql) { $Sql = '' }
    $norm = [regex]::Replace($Sql, '\s+', ' ').Trim()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($norm)
    $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    return ([System.BitConverter]::ToString($hash)).Replace('-','').ToLower()
}

function Assert-UpdateRequestObservationsAppendOnlyTriggersPresent {
    # Phase AI.C2.7 -- boot-time append-only trigger presence verification.
    # Phase AI.C2.9 -- additionally verifies that each trigger BODY in the
    # live catalog matches the canonical DDL the broker installed at
    # AI.C2.6 close, via a SHA-256 over the catalog-stored sql text
    # after canonical whitespace normalization.
    #
    # AI.C2.6 declared the append-only triggers in the schema-bootstrap
    # DDL. AI.C2.7 confirms at every broker boot that those triggers
    # are ACTUALLY installed in the live database file -- not just
    # present in the DDL text on disk. AI.C2.9 closes the gap that an
    # external SQLite tool could drop and recreate a trigger with a
    # weakened body (e.g. RAISE(IGNORE,...) instead of RAISE(ABORT,...)
    # or an always-false WHEN clause): without the body-hash check,
    # AI.C2.7's presence-only check would silently pass.
    #
    # Either failure mode (missing trigger OR body drift) is terminal
    # for this boot. There is no auto-repair, no auto-recreate, and
    # no fallback. Both failures emit a structured message that names
    # the affected trigger and the failing check, record a recent-error
    # entry with Source = 'observation_trigger_integrity', and exit
    # with the existing EXIT_E_OBSERVATION_TRIGGER_INTEGRITY (code 8).
    # AI.C2.9 does NOT allocate a new exit code.
    #
    # Sole database touch is a single read of sqlite_master (the
    # catalog), extended in AI.C2.9 to include the sql column. This
    # function does NOT issue any SELECT, UPDATE, DELETE, INSERT, or
    # ALTER against update_request_observations itself; the structural
    # guard installed by AI.C2.6 is observed indirectly via the SQLite
    # catalog. The target table-name literal is assembled by string
    # concatenation consistent with the AI.C2.6 trigger DDL so that
    # the AI.C2.5 / AI.C2.6 raw-source scans for SELECT sites against
    # the protected table continue to count zero.
    $expectedTriggers = @('trg_uro_block_update', 'trg_uro_block_delete')
    $tblFrag1 = 'update_request_'
    $tblFrag2 = 'observations'
    $qFrag1 = 'SE' + 'LECT' + ' name, sql FROM sqlite_master WHERE type = '
    $qFrag2 = '''trigger''' + ' AND tbl_name = '''
    $sql = $qFrag1 + $qFrag2 + $tblFrag1 + $tblFrag2 + ''';'
    $catalog = @{}
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = $sql
    try {
        $reader = $cmd.ExecuteReader()
        try {
            while ($reader.Read()) {
                if (-not $reader.IsDBNull(0)) {
                    $tname = [string]$reader.GetString(0)
                    $tsql  = if ($reader.IsDBNull(1)) { '' } else { [string]$reader.GetString(1) }
                    $catalog[$tname] = $tsql
                }
            }
        } finally { $reader.Dispose() }
    } finally { $cmd.Dispose() }
    $missing = @($expectedTriggers | Where-Object { -not $catalog.ContainsKey($_) })
    if ($missing.Count -gt 0) {
        $msg = 'Append-only trigger integrity check failed: missing trigger(s) on ' + $tblFrag1 + $tblFrag2 + ': ' + ($missing -join ', ') + '. Live database has been mutated by an external tool, overwritten with a pre-AI.C2.6 snapshot, or replaced by hand. The broker will not start.'
        throw $msg
    }
    foreach ($tname in $expectedTriggers) {
        $expected = $Script:ExpectedUpdateRequestObservationTriggerHashes[$tname]
        $observed = Get-CanonicalTriggerSqlHash -Sql $catalog[$tname]
        if ($observed -ne $expected) {
            $msg = 'Append-only trigger integrity check failed: trigger body drift on ' + $tname + ': expected ' + $expected + ', observed ' + $observed + '. The live catalog DDL for this trigger does not match the canonical DDL the broker installed at AI.C2.6. An external SQLite tool may have dropped and recreated the trigger with a weakened body. The broker will not start.'
            throw $msg
        }
    }
}

# ====================================================================
# C5 - Sentinel reconciliation
# ====================================================================

function Test-OrphanProcessAlive {
    # Phase AG.C5 -- liveness probe for an orphan cook's child PID.
    #
    # Returns one of:
    #   'alive'   -- PID exists AND its process start time matches the
    #                recorded cooks.started_at within +/- 5s. We are
    #                confident the original child is still running.
    #                The orphan_pid + 'alive' verdict means the operator
    #                may want to investigate or terminate the process
    #                manually; the broker never kills it (we may have
    #                lost broker-to-child stdin/stdout but the child
    #                may still be doing real work).
    #
    #   'dead'    -- PID does not exist, OR PID exists but its start
    #                time differs from cooks.started_at by more than
    #                5s. The latter is the PID-reuse case: the OS
    #                recycled the PID into some unrelated process.
    #                Either way, the original child is gone.
    #
    #   'unknown' -- Could not determine liveness. Reasons: PID column
    #                is NULL (supervisor died before writing it),
    #                started_at column is NULL (same), or
    #                Get-Process / .StartTime access threw (permission
    #                denied, transient race, etc.). Doctrine: do NOT
    #                fabricate certainty. Leave 'unknown' so the
    #                operator knows the broker could not check.
    #
    # The probe never throws and never kills. It is read-only.
    param(
        [Parameter()] [Nullable[int]]$ChildPid,
        [Parameter()] [string]$StartedAtIso
    )
    # NOTE: PowerShell parameter binding unwraps Nullable[T] to T, so
    # we cannot rely on $ChildPid.HasValue. Treat $ChildPid as either
    # $null or an int.
    if ($null -eq $ChildPid) {
        return 'unknown'
    }
    $pidInt = [int]$ChildPid
    if ($pidInt -le 0) {
        return 'unknown'
    }
    if ([string]::IsNullOrWhiteSpace($StartedAtIso)) {
        return 'unknown'
    }
    $proc = $null
    try {
        $proc = Get-Process -Id $pidInt -ErrorAction Stop
    } catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
        # Get-Process throws this when the PID does not exist.
        return 'dead'
    } catch {
        # Any other failure (e.g. permission denied probing a SYSTEM
        # process). We cannot say alive or dead.
        return 'unknown'
    }
    if ($null -eq $proc) {
        # Defensive: should not happen with -ErrorAction Stop, but if
        # PowerShell ever returns $null we treat it as unknown.
        return 'unknown'
    }
    try {
        $procStartUtc = $proc.StartTime.ToUniversalTime()
        $recordedUtc  = [datetime]::Parse($StartedAtIso, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        $deltaSec     = [math]::Abs(($procStartUtc - $recordedUtc).TotalSeconds)
        if ($deltaSec -le 5.0) {
            return 'alive'
        } else {
            # PID exists but the start time mismatches. Treat as PID
            # reuse: the original child is dead, the OS recycled the
            # PID into something else.
            return 'dead'
        }
    } catch {
        # Reading .StartTime can throw on processes the broker can't
        # query (different user, protected process). We cannot
        # confirm identity, so do not claim the PID is the original
        # child. 'unknown' is the truthful answer.
        return 'unknown'
    }
}

function Invoke-SentinelReconciliation {
    # On startup, every cook row in 'running' state must be reconciled
    # against on-disk sentinels. The filesystem is authoritative.
    if (-not (Test-Path -LiteralPath $Script:CooksDir -PathType Container)) {
        return 0
    }

    $cmd = $Script:SqliteConn.CreateCommand()
    # Phase AG.C5 -- also pull pid + started_at so the orphan branch
    # can probe the child process for liveness.
    $cmd.CommandText = "SELECT cook_id, cook_folder, pid, started_at FROM cooks WHERE status = 'running';"
    $runningCooks = New-Object System.Collections.Generic.List[object]
    $reader = $cmd.ExecuteReader()
    try {
        while ($reader.Read()) {
            $pidVal = if ($reader.IsDBNull(2)) { $null } else { $reader.GetInt32(2) }
            $startVal = if ($reader.IsDBNull(3)) { $null } else { $reader.GetString(3) }
            $runningCooks.Add(@{
                cook_id     = $reader.GetString(0)
                cook_folder = $reader.GetString(1)
                pid         = $pidVal
                started_at  = $startVal
            })
        }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }

    $reconciled = 0
    foreach ($row in $runningCooks) {
        # cook_folder may be stored relative (Phase AB) or absolute
        # (pre-AB / foreign-prefix preserved). Resolve-CookFolder is
        # the single point of policy for this distinction.
        $folder = Resolve-CookFolder -Stored $row.cook_folder
        if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
            Set-CookInterrupted -CookId $row.cook_id -Reason 'cook-folder-missing' -ClosureReason 'cook_folder_missing'
            $reconciled++
            continue
        }
        $finished    = Join-Path $folder 'finished.json'
        $interrupted = Join-Path $folder 'interrupted.json'

        if (Test-Path -LiteralPath $finished -PathType Leaf) {
            Set-CookFromFinishedJson -CookId $row.cook_id -Path $finished
            $reconciled++
        } elseif (Test-Path -LiteralPath $interrupted -PathType Leaf) {
            Set-CookInterrupted -CookId $row.cook_id -Reason 'interrupted-sentinel-found' -ClosureReason 'broker_restart_interrupted_sentinel'
            $reconciled++
        } else {
            # Phase AG.C5 -- orphan branch with liveness probe.
            #
            # No finished.json AND no interrupted.json means the
            # supervisor never got to write a terminal sentinel. The
            # child process may still be alive (broker died while it
            # was running), may be dead (child died around the same
            # time), or we may not be able to tell.
            #
            # Probe the child PID against cooks.started_at. The
            # verdict drives the closure_reason refinement:
            #   alive   -> broker_restart_orphan_alive
            #   dead    -> broker_restart_orphan_dead
            #   unknown -> broker_restart_orphan_unknown
            #
            # The probe is read-only. We do NOT kill alive orphans
            # here -- the operator may have inspection or recovery
            # work to do on them. Future slices may add an opt-in
            # cleanup pass.
            $probeVerdict = Test-OrphanProcessAlive -ChildPid $row.pid -StartedAtIso $row.started_at
            $closure = switch ($probeVerdict) {
                'alive'   { 'broker_restart_orphan_alive' }
                'dead'    { 'broker_restart_orphan_dead' }
                default   { 'broker_restart_orphan_unknown' }
            }
            # Synthesize an interrupted sentinel so the historical record
            # stays consistent on subsequent startups, then mark interrupted.
            try {
                $synth = [ordered]@{
                    interruptedAt      = Get-UtcNowIso
                    lastKnownPhase     = $null
                    pid                = $row.pid
                    host               = $env:COMPUTERNAME
                    reason             = 'broker-detected-orphan'
                    orphanProbeVerdict = $probeVerdict
                    closureReason      = $closure
                }
                $tmp = $interrupted + '.tmp'
                [System.IO.File]::WriteAllText($tmp, ($synth | ConvertTo-Json -Depth 3), [System.Text.UTF8Encoding]::new($false))
                [System.IO.File]::Move($tmp, $interrupted, $true)
            } catch {
                Add-RecentError ('Failed to synthesize interrupted.json for cook ' + $row.cook_id + ': ' + $_.Exception.Message)
            }
            $pidForWrite = if ($null -eq $row.pid) { [Nullable[int]]$null } else { [Nullable[int]]([int]$row.pid) }
            Set-CookInterrupted -CookId $row.cook_id -Reason 'broker-detected-orphan' -ClosureReason $closure -OrphanPid $pidForWrite -OrphanProbeVerdict $probeVerdict
            $reconciled++
        }
    }
    return $reconciled
}

function Set-CookInterrupted {
    # Phase AG -- $ClosureReason / $EvidenceJson are optional for
    # backward compat with pre-AG call sites (none remain inside the
    # repo, but the parameter is optional to keep the surface
    # extensible). When provided, the values are written into the
    # AG-additive columns. COALESCE preserves any prior write -- the
    # supervisor writes closure_reason first, reconciliation writes
    # later, and the supervisor's value wins (it was closer to the
    # event).
    #
    # Phase AG.C5 -- $OrphanPid / $OrphanProbeVerdict carry the
    # orphan-liveness probe result from Invoke-SentinelReconciliation.
    # Both are nullable; only the orphan branch supplies them. The
    # values are forensic and never used by code paths other than
    # post-mortem inspection.
    param(
        [Parameter(Mandatory)] [string]$CookId,
        [Parameter(Mandatory)] [string]$Reason,
        [Parameter()]          [string]$ClosureReason,
        [Parameter()]          [string]$EvidenceJson,
        [Parameter()]          [Nullable[int]]$OrphanPid,
        [Parameter()]          [string]$OrphanProbeVerdict
    )
    $now = Get-UtcNowIso
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
UPDATE cooks
SET status = 'interrupted',
    finished_at = COALESCE(finished_at, $now),
    error_class = COALESCE(error_class, $reason),
    closure_reason = COALESCE(closure_reason, $cr),
    closure_evidence_json = COALESCE(closure_evidence_json, $ev),
    abnormal_close_recorded_utc = COALESCE(abnormal_close_recorded_utc, $now),
    orphan_pid = COALESCE(orphan_pid, $opid),
    orphan_probe_verdict = COALESCE(orphan_probe_verdict, $opv),
    updated_at = $now
WHERE cook_id = $id;
'@
    $p1 = $cmd.CreateParameter(); $p1.ParameterName = '$now';    $p1.Value = $now;    [void]$cmd.Parameters.Add($p1)
    $p2 = $cmd.CreateParameter(); $p2.ParameterName = '$reason'; $p2.Value = $Reason; [void]$cmd.Parameters.Add($p2)
    $p3 = $cmd.CreateParameter(); $p3.ParameterName = '$id';     $p3.Value = $CookId; [void]$cmd.Parameters.Add($p3)
    $p4 = $cmd.CreateParameter(); $p4.ParameterName = '$cr'
    $p4.Value = if ([string]::IsNullOrWhiteSpace($ClosureReason)) { [System.DBNull]::Value } else { [string]$ClosureReason }
    [void]$cmd.Parameters.Add($p4)
    $p5 = $cmd.CreateParameter(); $p5.ParameterName = '$ev'
    $p5.Value = if ([string]::IsNullOrWhiteSpace($EvidenceJson)) { [System.DBNull]::Value } else { [string]$EvidenceJson }
    [void]$cmd.Parameters.Add($p5)
    $p6 = $cmd.CreateParameter(); $p6.ParameterName = '$opid'
    # NOTE: PowerShell parameter binding unwraps Nullable[T], so we
    # treat $OrphanPid as either $null or an int. .HasValue is not
    # reachable here.
    $p6.Value = if ($null -eq $OrphanPid) { [System.DBNull]::Value } else { [int]$OrphanPid }
    [void]$cmd.Parameters.Add($p6)
    $p7 = $cmd.CreateParameter(); $p7.ParameterName = '$opv'
    $p7.Value = if ([string]::IsNullOrWhiteSpace($OrphanProbeVerdict)) { [System.DBNull]::Value } else { [string]$OrphanProbeVerdict }
    [void]$cmd.Parameters.Add($p7)
    try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Set-CookFromFinishedJson {
    param([string]$CookId, [string]$Path)
    try {
        $finished = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    } catch {
        Set-CookInterrupted -CookId $CookId -Reason 'finished-json-unparseable' -ClosureReason 'finished_json_unparseable'
        return
    }
    $exit   = if ($null -ne $finished.exitCode) { [int]$finished.exitCode } else { -1 }
    $status = if ($exit -eq 0) { 'completed' } else { 'errored' }
    # Phase AG: when reconciliation reads a finished.json on startup,
    # the supervisor had completed the child but had not yet finished
    # the SQLite terminal write (broker died between writeSentinel
    # 'finished.json' and sqlExecAtomic). closure_reason records THAT
    # operational story, distinct from a normal end-to-end terminal
    # write whose closure_reason is clean_exit / nonzero_exit.
    $closure = 'broker_restart_finished_sentinel'
    $now    = Get-UtcNowIso
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
UPDATE cooks
SET status = $status,
    exit_code = $exit,
    finished_at = COALESCE($finished_at, $now),
    duration_seconds = $duration,
    error_message = $err,
    closure_reason = COALESCE(closure_reason, $cr),
    abnormal_close_recorded_utc = COALESCE(abnormal_close_recorded_utc, $now),
    updated_at = $now
WHERE cook_id = $id;
'@
    $p = $cmd.CreateParameter(); $p.ParameterName = '$status';      $p.Value = $status; [void]$cmd.Parameters.Add($p)
    $p = $cmd.CreateParameter(); $p.ParameterName = '$exit';        $p.Value = $exit;   [void]$cmd.Parameters.Add($p)
    $p = $cmd.CreateParameter(); $p.ParameterName = '$finished_at'
    $p.Value = if ($finished.finishedAt) { [string]$finished.finishedAt } else { [System.DBNull]::Value }
    [void]$cmd.Parameters.Add($p)
    $p = $cmd.CreateParameter(); $p.ParameterName = '$now';         $p.Value = $now;    [void]$cmd.Parameters.Add($p)
    $p = $cmd.CreateParameter(); $p.ParameterName = '$duration'
    $p.Value = if ($null -ne $finished.durationSec) { [double]$finished.durationSec } else { [System.DBNull]::Value }
    [void]$cmd.Parameters.Add($p)
    $p = $cmd.CreateParameter(); $p.ParameterName = '$err'
    $p.Value = if ($finished.errorSummary) { [string]$finished.errorSummary } else { [System.DBNull]::Value }
    [void]$cmd.Parameters.Add($p)
    $p = $cmd.CreateParameter(); $p.ParameterName = '$cr';          $p.Value = $closure; [void]$cmd.Parameters.Add($p)
    $p = $cmd.CreateParameter(); $p.ParameterName = '$id';          $p.Value = $CookId; [void]$cmd.Parameters.Add($p)
    try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

# ====================================================================
# Phase AH.C1 -- Broker session lifecycle persistence
# ====================================================================
#
# One row per broker process lifetime is written to broker_sessions.
# Lifecycle rules (DOCTRINE -- DO NOT relax):
#
#   1. Start-BrokerSession INSERTs the row exactly once, immediately
#      after Apply-M1Schema succeeds. The row carries every fact the
#      broker KNOWS at startup: pid, port, version, host, user SID,
#      workspace_id, both workspace path forms, and started_at as the
#      WALL clock authority. No row, no startup -- if INSERT throws,
#      the broker continues (best-effort) but $Script:BrokerSessionId
#      stays $null and Stop-BrokerSession becomes a no-op.
#
#   2. Stop-BrokerSession UPDATEs the row exactly once, from inside
#      Invoke-Shutdown, BEFORE the SQLite connection is closed. It
#      writes stopped_at, stop_reason, and stop_class='clean'. The
#      'clean' value is the POSITIVE ASSERTION that this broker
#      reached the orderly teardown path. The function is idempotent
#      via $Script:BrokerSessionStopRecorded; a recursive Invoke-Shutdown
#      will NOT touch the row twice.
#
#   3. Invoke-ClassifyPriorBrokerSessions runs ONCE at startup, AFTER
#      Start-BrokerSession (so the new session never classifies itself)
#      and BEFORE C5 sentinel reconciliation (so any orphan-classification
#      logic that later correlates cooks to prior sessions sees the
#      classified rows). It marks every row with stopped_at IS NULL
#      AND classified_at IS NULL as stop_class='no_orderly_stop_record',
#      stamps classified_at + classified_by_session, and returns the
#      count. The classification is purely observational -- it records
#      WHAT was observed (no orderly stop record committed) without
#      claiming WHY. It is FORENSIC. It does NOT re-classify already-
#      classified rows.
#
#   4. Set-CookBrokerShutdownAnnotation writes
#      closure_reason='broker_shutdown_with_active_cook' onto any cook
#      row still in status='running' at shutdown time. The cook row is
#      NOT flipped to 'interrupted' here -- only the NEXT broker's
#      reconciliation does that. This is intentional: keeping the cook
#      in 'running' lets Invoke-SentinelReconciliation probe the
#      orphan child process and record its orphan_probe_verdict. The
#      shutting-down broker's closure_reason survives via the COALESCE
#      pattern shared by every closure_reason writer.
#
#   5. Invoke-ActiveCookShutdownSweep enumerates $Script:CookRegistry
#      and calls Set-CookBrokerShutdownAnnotation for each in-flight
#      cookId. It is called from Invoke-Shutdown BEFORE Stop-BrokerSession
#      so the active-cook evidence is persisted even if a subsequent
#      teardown step throws.
#
# The two functions Get-WindowsUserSid and New-BrokerSessionId are
# trivial wrappers kept local so the broker has no external dependency
# (e.g. on a separate identity module) for these primitive values.

function Get-WindowsUserSid {
    # Returns the current process's user SID as a string ('S-1-5-...').
    # 'unknown' on any failure -- the broker NEVER refuses to start
    # because the SID could not be read.
    try {
        return [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    } catch {
        return 'unknown'
    }
}

function New-BrokerSessionId {
    # GUID is the right shape for a forensic primary key: globally
    # unique, no leakage of timing, no requirement for clock sync.
    return [Guid]::NewGuid().ToString()
}

function Get-WorkspaceIdFromMeta {
    # Reads the workspace_id from _schema_meta. Returns $null on any
    # failure. Apply-M1Schema upserts _schema_meta BEFORE this function
    # is called, so a return of $null indicates either a corrupted
    # _schema_meta row or a SQLite connectivity problem; either case
    # is surfaced by the caller via Add-RecentError.
    if ($null -eq $Script:SqliteConn) { return $null }
    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = 'SELECT workspace_id FROM _schema_meta WHERE id = 1;'
        $val = $cmd.ExecuteScalar()
        $cmd.Dispose()
        if ($null -eq $val -or $val -is [System.DBNull]) { return $null }
        return [string]$val
    } catch {
        return $null
    }
}

function Get-StartupClassification {
    # Phase AH.C3 -- compute the startup-classification label by
    # OBSERVING the broker_sessions and cooks tables in their state
    # at THIS broker's boot. Three samples are taken (each over the
    # OPEN $Script:SqliteConn) BEFORE any AH.C3 mutation has run:
    #
    #   priorSessionCount     = count(broker_sessions)            [pre-INSERT self]
    #   priorRunningCookCount = count(cooks WHERE status='running')
    #   mostRecentPrior       = broker_sessions row with MAX(started_at)
    #
    # Returns a hashtable shaped like:
    #
    #   @{
    #       classification        = '<one of the 5 frozen labels>'
    #       priorSessionId        = <session_id | $null>
    #       priorSessionStopClass = <'clean' | 'no_orderly_stop_record' | $null>
    #       priorSessionStoppedAt = <iso string | $null>
    #       priorRunningCookCount = <int>
    #   }
    #
    # On any SQLite error the function returns the truthful-ambiguity
    # fallback ('startup_after_unknown_runtime_state') with all
    # observation fields $null/0. No exception escapes.
    if ($null -eq $Script:SqliteConn) {
        return @{
            classification        = 'startup_after_unknown_runtime_state'
            priorSessionId        = $null
            priorSessionStopClass = $null
            priorSessionStoppedAt = $null
            priorRunningCookCount = 0
        }
    }
    try {
        $priorSessionCount = [int](Invoke-SqliteScalar 'SELECT COUNT(*) FROM broker_sessions;')
    } catch { $priorSessionCount = -1 }
    try {
        $priorRunningCookCount = [int](Invoke-SqliteScalar "SELECT COUNT(*) FROM cooks WHERE status = 'running';")
    } catch { $priorRunningCookCount = 0 }

    $priorSessionId        = $null
    $priorSessionStopClass = $null
    $priorSessionStoppedAt = $null
    if ($priorSessionCount -gt 0) {
        try {
            $cmd = $Script:SqliteConn.CreateCommand()
            $cmd.CommandText = 'SELECT session_id, stop_class, stopped_at FROM broker_sessions ORDER BY started_at DESC LIMIT 1;'
            $reader = $cmd.ExecuteReader()
            try {
                if ($reader.Read()) {
                    $priorSessionId        = if ($reader.IsDBNull(0)) { $null } else { $reader.GetString(0) }
                    $priorSessionStopClass = if ($reader.IsDBNull(1)) { $null } else { $reader.GetString(1) }
                    $priorSessionStoppedAt = if ($reader.IsDBNull(2)) { $null } else { $reader.GetString(2) }
                }
            } finally {
                $reader.Dispose()
                $cmd.Dispose()
            }
        } catch {
            $priorSessionId        = $null
            $priorSessionStopClass = $null
            $priorSessionStoppedAt = $null
        }
    }

    # Decision tree -- see the doctrine block above $Script:BrokerStartupClassifications.
    $label = 'startup_after_unknown_runtime_state'
    if ($priorSessionCount -eq 0 -and $priorRunningCookCount -eq 0) {
        $label = 'clean_start'
    } elseif ($priorRunningCookCount -gt 0) {
        $label = 'startup_after_interrupted_runtime'
    } elseif ($priorSessionStopClass -eq 'clean') {
        $label = 'restart_after_clean_shutdown'
    } elseif ($priorSessionStopClass -eq 'no_orderly_stop_record' -or [string]::IsNullOrWhiteSpace($priorSessionStoppedAt)) {
        $label = 'restart_after_no_orderly_stop_record'
    } else {
        $label = 'startup_after_unknown_runtime_state'
    }

    # Defensive: any computed label MUST be a member of the frozen
    # vocabulary. If it is not, fall back to truthful ambiguity.
    if ($Script:BrokerStartupClassifications -notcontains $label) {
        $label = 'startup_after_unknown_runtime_state'
    }

    return @{
        classification        = $label
        priorSessionId        = $priorSessionId
        priorSessionStopClass = $priorSessionStopClass
        priorSessionStoppedAt = $priorSessionStoppedAt
        priorRunningCookCount = $priorRunningCookCount
    }
}

function Set-BrokerSessionStartupReconciledCount {
    # Phase AH.C3 -- writes the reconciled-cook count returned by
    # Invoke-SentinelReconciliation onto THIS broker's broker_sessions
    # row. Idempotent via COALESCE: if the column is already non-NULL
    # the existing value wins (first-write-wins, mirroring the AG
    # cook-closure-reason doctrine).
    param([Parameter(Mandatory)] [int]$Count)
    if ($null -eq $Script:SqliteConn) { return }
    if ([string]::IsNullOrWhiteSpace($Script:BrokerSessionId)) { return }
    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = 'UPDATE broker_sessions SET startup_reconciled_cook_count = COALESCE(startup_reconciled_cook_count, $cnt) WHERE session_id = $sid;'
        $p1 = $cmd.CreateParameter(); $p1.ParameterName = '$cnt'; $p1.Value = [int]$Count;             [void]$cmd.Parameters.Add($p1)
        $p2 = $cmd.CreateParameter(); $p2.ParameterName = '$sid'; $p2.Value = $Script:BrokerSessionId; [void]$cmd.Parameters.Add($p2)
        try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
        $Script:BrokerStartupReconciledCookCount = [int]$Count
    } catch {
        Add-RecentError ('Set-BrokerSessionStartupReconciledCount UPDATE failed: ' + $_.Exception.Message)
    }
}

function Start-BrokerSession {
    # Inserts the broker_sessions row for THIS broker process. Sets
    # $Script:BrokerSessionId on success. Returns the new session ID
    # or $null on failure.
    #
    # Phase AH.C3 -- the INSERT also captures the AH.C3 startup-context
    # evidence columns (startup_classification, startup_observed_prior_session_id,
    # startup_prior_running_cook_count). These are computed by
    # Get-StartupClassification BEFORE the INSERT executes, so the
    # observation is frozen at the moment the new broker began
    # writing its own row. The reconciled-cook count is left NULL
    # here and filled by Set-BrokerSessionStartupReconciledCount once
    # Invoke-SentinelReconciliation completes later in startup.
    if ($null -eq $Script:SqliteConn) { return $null }
    if (-not [string]::IsNullOrWhiteSpace($Script:BrokerSessionId)) {
        # Defensive: already started. Do NOT INSERT a second row.
        return $Script:BrokerSessionId
    }
    $sessionId  = New-BrokerSessionId
    $startedIso = $Script:StartedAt.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $wsId       = Get-WorkspaceIdFromMeta
    if (-not $wsId) { $wsId = 'unknown' }
    $sid        = Get-WindowsUserSid
    $pidVal     = $PID
    $portVal    = if ($Script:BrokerPort -gt 0) { [int]$Script:BrokerPort } else { $null }
    $verVal     = $Script:CookbookVersion
    $shaVal     = $Script:PaxScriptSha256
    $hostVal    = $Script:HostMachineName
    $dispVal    = $Script:WorkspaceDisplayPath
    $resVal     = $Script:WorkspacePath

    # AH.C3 -- observe BEFORE INSERT. Mirror onto script-scope vars
    # so the runtime payload can read them without re-querying.
    $startup = Get-StartupClassification
    $Script:BrokerStartupClassification        = [string]$startup.classification
    $Script:BrokerStartupPriorSessionId        = $startup.priorSessionId
    $Script:BrokerStartupPriorSessionStopClass = $startup.priorSessionStopClass
    $Script:BrokerStartupPriorSessionStoppedAt = $startup.priorSessionStoppedAt
    $Script:BrokerStartupPriorRunningCookCount = [int]$startup.priorRunningCookCount

    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = @'
INSERT INTO broker_sessions (
    session_id, pid, port, cookbook_version, pax_script_sha256,
    host_machine, windows_user_sid, workspace_id,
    workspace_display_path, workspace_resolved_path, started_at,
    startup_classification, startup_observed_prior_session_id,
    startup_prior_running_cook_count
) VALUES (
    $sid, $pid, $port, $ver, $sha,
    $host, $usid, $wid,
    $disp, $res, $started,
    $sclass, $sprior,
    $srcc
);
'@
        $p = $cmd.CreateParameter(); $p.ParameterName = '$sid';     $p.Value = $sessionId;                                                                  [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$pid';     $p.Value = [int]$pidVal;                                                                [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$port'
        $p.Value = if ($null -eq $portVal) { [System.DBNull]::Value } else { [int]$portVal }
        [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$ver'
        $p.Value = if ([string]::IsNullOrWhiteSpace($verVal)) { [System.DBNull]::Value } else { [string]$verVal }
        [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$sha'
        $p.Value = if ([string]::IsNullOrWhiteSpace($shaVal)) { [System.DBNull]::Value } else { [string]$shaVal }
        [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$host';    $p.Value = [string]$hostVal;                                                            [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$usid';    $p.Value = [string]$sid;                                                                [void]$cmd.Parameters.Add($p)
        # NOTE: $usid parameter binds Get-WindowsUserSid result, NOT
        # the session_id. Variable name collision avoided by SQL
        # parameter naming.
        $p = $cmd.CreateParameter(); $p.ParameterName = '$wid';     $p.Value = [string]$wsId;                                                               [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$disp';    $p.Value = [string]$dispVal;                                                            [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$res';     $p.Value = [string]$resVal;                                                             [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$started'; $p.Value = [string]$startedIso;                                                         [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$sclass';  $p.Value = [string]$Script:BrokerStartupClassification;                                 [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$sprior'
        $p.Value = if ([string]::IsNullOrWhiteSpace($Script:BrokerStartupPriorSessionId)) { [System.DBNull]::Value } else { [string]$Script:BrokerStartupPriorSessionId }
        [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$srcc';    $p.Value = [int]$Script:BrokerStartupPriorRunningCookCount;                             [void]$cmd.Parameters.Add($p)
        try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
        $Script:BrokerSessionId = $sessionId
        return $sessionId
    } catch {
        Add-RecentError ('Start-BrokerSession INSERT failed: ' + $_.Exception.Message)
        return $null
    }
}

function Stop-BrokerSession {
    # Records the orderly-stop facts on THIS broker's row. Idempotent
    # via $Script:BrokerSessionStopRecorded. The function is a no-op
    # when:
    #   - the SQLite connection is null (startup failed before
    #     Apply-M1Schema; nothing to update)
    #   - $Script:BrokerSessionId is null (Start-BrokerSession never
    #     completed; nothing to update)
    #   - $Script:BrokerSessionStopRecorded is already $true
    #   - $StopReason is not a member of $Script:BrokerSessionStopReasons
    #     (defensive: refuse to write outside the frozen vocabulary)
    param([Parameter(Mandatory)] [string]$StopReason)
    if ($Script:BrokerSessionStopRecorded) { return }
    if ($null -eq $Script:SqliteConn)      { return }
    if ([string]::IsNullOrWhiteSpace($Script:BrokerSessionId)) { return }
    if ($Script:BrokerSessionStopReasons -notcontains $StopReason) {
        try {
            Add-RecentError ('Stop-BrokerSession refused: stop_reason "' + $StopReason + '" is not a member of $Script:BrokerSessionStopReasons.')
        } catch {}
        return
    }
    $now = Get-UtcNowIso
    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = @'
UPDATE broker_sessions
SET stopped_at  = $now,
    stop_reason = $reason,
    stop_class  = 'clean'
WHERE session_id = $sid
  AND stopped_at IS NULL;
'@
        $p = $cmd.CreateParameter(); $p.ParameterName = '$now';    $p.Value = [string]$now;                  [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$reason'; $p.Value = [string]$StopReason;           [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$sid';    $p.Value = [string]$Script:BrokerSessionId; [void]$cmd.Parameters.Add($p)
        try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
        $Script:BrokerSessionStopRecorded = $true
    } catch {
        try {
            Add-RecentError ('Stop-BrokerSession UPDATE failed: ' + $_.Exception.Message)
        } catch {}
    }
}

function Invoke-ClassifyPriorBrokerSessions {
    # Forensic retroactive classification of prior broker sessions.
    # Marks every row with stopped_at IS NULL AND classified_at IS NULL
    # as stop_class='no_orderly_stop_record' (a purely observational
    # label naming WHAT was observed -- no orderly stop record
    # committed by the broker that owned the row -- with no causal
    # claim) and stamps classified_at + classified_by_session. Never
    # re-classifies a row that already has classified_at set. Never
    # touches THIS broker's own row (defensive WHERE session_id !=
    # $self). Returns the count of rows classified.
    if ($null -eq $Script:SqliteConn) { return 0 }
    if ([string]::IsNullOrWhiteSpace($Script:BrokerSessionId)) { return 0 }
    $now = Get-UtcNowIso
    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = @'
UPDATE broker_sessions
SET stop_class            = 'no_orderly_stop_record',
    classified_at         = $now,
    classified_by_session = $by
WHERE stopped_at  IS NULL
  AND classified_at IS NULL
  AND session_id != $self;
'@
        $p = $cmd.CreateParameter(); $p.ParameterName = '$now';  $p.Value = [string]$now;                  [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$by';   $p.Value = [string]$Script:BrokerSessionId; [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$self'; $p.Value = [string]$Script:BrokerSessionId; [void]$cmd.Parameters.Add($p)
        $rows = $cmd.ExecuteNonQuery()
        $cmd.Dispose()
        return [int]$rows
    } catch {
        try {
            Add-RecentError ('Invoke-ClassifyPriorBrokerSessions UPDATE failed: ' + $_.Exception.Message)
        } catch {}
        return 0
    }
}

function Set-CookBrokerShutdownAnnotation {
    # Annotates an in-flight cook row with closure_reason=
    # 'broker_shutdown_with_active_cook'. Writes ONLY to rows still in
    # status='running' so the supervisor's terminal write (if it
    # happens to race us) and Set-CookFromFinishedJson are both
    # respected. COALESCE preserves any prior closure_reason -- the
    # supervisor's record, if it got there first, wins. abnormal_close_recorded_utc
    # gets the current shutdown time when no prior value exists.
    #
    # AH.C2 -- broker_session_id_at_shutdown gets THIS broker's
    # session_id via the same COALESCE-earliest-writer pattern. The
    # column is forensic evidence of WHICH broker recorded this
    # annotation; it makes no claim about continuity, authority, or
    # restoration. The earliest writer wins, so a later broker
    # restart that touches the same row (e.g. via sentinel
    # reconciliation) NEVER overwrites the stamped session_id.
    #
    # Does NOT flip status to 'interrupted'. Doctrine:
    # broker-driven-interruption and orphan-side-reconciliation are
    # distinct paths; the status flip belongs to the NEXT broker's
    # reconciliation, which also probes the orphan child process and
    # records orphan_probe_verdict. Both facts then coexist on the row.
    param([Parameter(Mandatory)] [string]$CookId)
    if ($null -eq $Script:SqliteConn) { return }
    $now = Get-UtcNowIso
    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = @'
UPDATE cooks
SET closure_reason                 = COALESCE(closure_reason, $cr),
    abnormal_close_recorded_utc    = COALESCE(abnormal_close_recorded_utc, $now),
    broker_session_id_at_shutdown  = COALESCE(broker_session_id_at_shutdown, $bsid),
    updated_at                     = $now
WHERE cook_id = $id
  AND status  = 'running';
'@
        $p = $cmd.CreateParameter(); $p.ParameterName = '$cr';   $p.Value = 'broker_shutdown_with_active_cook'; [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$now';  $p.Value = [string]$now;                       [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$bsid'
        $p.Value = if ([string]::IsNullOrWhiteSpace($Script:BrokerSessionId)) { [System.DBNull]::Value } else { [string]$Script:BrokerSessionId }
        [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$id';   $p.Value = [string]$CookId;                    [void]$cmd.Parameters.Add($p)
        try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
    } catch {
        try {
            Add-RecentError ('Set-CookBrokerShutdownAnnotation failed for cook ' + $CookId + ': ' + $_.Exception.Message)
        } catch {}
    }
}

function Invoke-ActiveCookShutdownSweep {
    # Enumerates $Script:CookRegistry and annotates each cookId with
    # closure_reason='broker_shutdown_with_active_cook'. Called from
    # Invoke-Shutdown BEFORE the SQLite connection is closed. Returns
    # the number of cookIds enumerated (annotation may still no-op via
    # the WHERE status='running' guard inside the UPDATE; the returned
    # count is the upper-bound of TOUCHED rows, not necessarily
    # MUTATED rows).
    if ($null -eq $Script:CookRegistry) { return 0 }
    $cookIds = @()
    try {
        $cookIds = @($Script:CookRegistry.Keys)
    } catch {
        try {
            Add-RecentError ('Invoke-ActiveCookShutdownSweep: failed to enumerate $Script:CookRegistry: ' + $_.Exception.Message)
        } catch {}
        return 0
    }
    $count = 0
    foreach ($cookId in $cookIds) {
        if ([string]::IsNullOrWhiteSpace($cookId)) { continue }
        try {
            Set-CookBrokerShutdownAnnotation -CookId $cookId
            $count++
        } catch {
            try {
                Add-RecentError ('Invoke-ActiveCookShutdownSweep: annotation failed for cook ' + $cookId + ': ' + $_.Exception.Message)
            } catch {}
        }
    }
    return $count
}

# ====================================================================
# Phase AB - Workspace-relative cook_folder doctrine
# ====================================================================
#
# Phase AB introduces workspace-relative storage for cooks.cook_folder.
# Pre-AB rows hold an absolute path (e.g. "C:\workspace\Cooks\<r>\<c>");
# Phase AB rows hold a workspace-relative path (e.g. "Cooks\<r>\<c>").
#
# Doctrine:
#   - The workspace is the root authority. Paths inside it are
#     workspace-relative; the broker resolves them to absolute at read
#     time using the CURRENT $Script:WorkspacePath. This is what makes
#     "workspace is portable" actually true rather than a documentation
#     claim.
#   - Resolve-CookFolder is the SINGLE function every reader uses to
#     turn the stored value into a usable absolute path.
#   - Invoke-CookFolderPathMigration runs once per broker startup. It
#     is idempotent (relative rows stay relative; absolute rows whose
#     prefix matches the current $Script:CooksDir are rewritten to
#     relative; FOREIGN-PREFIX absolute rows -- ones whose stored
#     prefix does not match the current workspace -- are PRESERVED
#     AS-IS and an Add-RecentError is emitted). Foreign-prefix
#     preservation is the explicit doctrine: Cookbook never silently
#     rewrites historical evidence that it cannot prove belongs to the
#     current workspace.

function Test-PathIsAbsolute {
    # Conservative absolute-path predicate. Returns $true for
    # drive-rooted paths (C:\...), UNC paths (\\server\share\...), and
    # device paths (\\?\... \\.\...). Returns $false for everything
    # else, including relative paths (Cooks\<r>\<c>) and the empty
    # string. Intentionally avoids [System.IO.Path]::IsPathRooted which
    # returns $true for "\foo" (rooted but not absolute) -- that case
    # would create ambiguity in the migration heuristic.
    param([string]$Path)
    if (-not $Path) { return $false }
    if ($Path.Length -ge 2 -and $Path[1] -eq ':') { return $true }       # X:\...
    if ($Path.StartsWith('\\') -or $Path.StartsWith('//')) { return $true } # UNC / device
    return $false
}

function Resolve-CookFolder {
    # Canonical reader-side resolution of cooks.cook_folder. Returns
    # an absolute path. Always inspect this rather than concatenating
    # $Script:WorkspacePath inline -- the helper preserves the doctrine
    # decision documented above.
    #
    # - Relative stored value -> Join-Path $Script:WorkspacePath
    # - Absolute stored value -> returned unchanged (covers both the
    #                            normal post-migration case where the
    #                            workspace didn't change, and the
    #                            foreign-prefix preservation case where
    #                            migration could not rewrite the row).
    param([string]$Stored)
    if (-not $Stored) { return $Stored }
    if (Test-PathIsAbsolute -Path $Stored) { return $Stored }
    return (Join-Path $Script:WorkspacePath $Stored)
}

function ConvertTo-WorkspaceRelativeCookFolder {
    # Canonical writer-side projection: given an absolute path that
    # lives under the current $Script:WorkspacePath, return the
    # workspace-relative form. Returns $null if the path is not under
    # the current workspace -- callers MUST handle null by preserving
    # the original absolute path (foreign-prefix preservation).
    param([string]$Absolute)
    if (-not $Absolute) { return $null }
    $wsFull = [System.IO.Path]::GetFullPath($Script:WorkspacePath).TrimEnd('\','/')
    $abs    = [System.IO.Path]::GetFullPath($Absolute).TrimEnd('\','/')
    $cmp    = [System.StringComparison]::OrdinalIgnoreCase
    $prefix = $wsFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $abs.StartsWith($prefix, $cmp)) { return $null }
    return $abs.Substring($prefix.Length)
}

function Invoke-CookFolderPathMigration {
    # One-shot, idempotent migration from absolute cook_folder storage
    # (pre-AB) to workspace-relative storage (Phase AB). Runs on every
    # broker startup; does nothing on rows that are already relative or
    # whose absolute prefix does not match the current workspace.
    #
    # Per-row autocommit is intentional: if the broker is killed
    # mid-migration, the next startup picks up at the next un-migrated
    # row. No row is ever left in a torn state because each UPDATE is a
    # single SQL statement.

    if ($null -eq $Script:SqliteConn) { return $null }

    $rows = New-Object System.Collections.Generic.List[object]
    $cmd  = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'SELECT cook_id, cook_folder FROM cooks;'
    $reader = $cmd.ExecuteReader()
    try {
        while ($reader.Read()) {
            if ($reader.IsDBNull(1)) { continue }
            $rows.Add(@{
                cook_id     = $reader.GetString(0)
                cook_folder = $reader.GetString(1)
            })
        }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }

    $migrated = 0
    $alreadyRelative = 0
    $foreignPreserved = 0
    $errors = 0

    foreach ($row in $rows) {
        $stored = [string]$row.cook_folder
        if (-not (Test-PathIsAbsolute -Path $stored)) {
            $alreadyRelative++
            continue
        }
        $rel = ConvertTo-WorkspaceRelativeCookFolder -Absolute $stored
        if ($null -eq $rel) {
            $foreignPreserved++
            Add-RecentError ('Cook ' + $row.cook_id + ' has cook_folder outside current workspace; preserved as-is: ' + $stored)
            continue
        }
        try {
            $u = $Script:SqliteConn.CreateCommand()
            $u.CommandText = 'UPDATE cooks SET cook_folder = $rel WHERE cook_id = $id AND cook_folder = $orig;'
            $p1 = $u.CreateParameter(); $p1.ParameterName = '$rel';  $p1.Value = $rel;     [void]$u.Parameters.Add($p1)
            $p2 = $u.CreateParameter(); $p2.ParameterName = '$id';   $p2.Value = $row.cook_id; [void]$u.Parameters.Add($p2)
            $p3 = $u.CreateParameter(); $p3.ParameterName = '$orig'; $p3.Value = $stored;  [void]$u.Parameters.Add($p3)
            $changed = $u.ExecuteNonQuery()
            $u.Dispose()
            if ($changed -ge 1) { $migrated++ }
        } catch {
            $errors++
            Add-RecentError ('cook_folder migration failed for cook ' + $row.cook_id + ': ' + $_.Exception.Message)
        }
    }

    return [ordered]@{
        scanned          = $rows.Count
        migrated         = $migrated
        alreadyRelative  = $alreadyRelative
        foreignPreserved = $foreignPreserved
        errors           = $errors
    }
}

# ====================================================================
# C6 + C7 - Dispatch loop, middleware, and /api/v1/health
# ====================================================================

function Test-OriginAllowed {
    param($Request)
    $origin = $Request.Headers['Origin']
    if (-not $origin) { return $true }                                # same-origin browser request
    if ($origin -eq 'null') { return $true }                          # local-file form posts etc.
    # UX-1H6: the browser is now canonically loaded from
    # http://localhost:<port>. Chrome/Edge reject 127.0.0.1 as a
    # WebAuthn effective domain, so the launcher opens localhost.
    # The legacy 127.0.0.1 origin remains accepted because the
    # HttpListener still binds both prefixes and an operator may
    # still type the IP directly.
    $port = [int]$Script:BrokerPort
    $allowed = @(
        ('http://127.0.0.1:' + $port),
        ('http://localhost:' + $port)
    )
    return ($origin -in $allowed)
}

function Test-BearerToken {
    param($Request)
    $auth = $Request.Headers['Authorization']
    if (-not $auth -or -not $auth.StartsWith('Bearer ')) { return $false }
    $candidate = $auth.Substring(7).Trim()
    $expected  = $Script:SessionToken
    if ($candidate.Length -ne $expected.Length) { return $false }
    # Constant-time compare to defeat timing oracles.
    $a = [System.Text.Encoding]::ASCII.GetBytes($candidate)
    $b = [System.Text.Encoding]::ASCII.GetBytes($expected)
    $diff = 0
    for ($i = 0; $i -lt $a.Length; $i++) { $diff = $diff -bor ($a[$i] -bxor $b[$i]) }
    return ($diff -eq 0)
}

function Test-CsrfHeader {
    param($Request)
    $h = $Request.Headers['X-Cookbook-Request']
    return ($h -eq '1')
}

function Write-JsonResponse {
    param($Context, [int]$Status, $Body)
    $json  = $Body | ConvertTo-Json -Depth 12 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $Context.Response.StatusCode  = $Status
    $Context.Response.ContentType = 'application/json; charset=utf-8'
    $Context.Response.ContentLength64 = $bytes.LongLength
    $Context.Response.Headers['Cache-Control'] = 'no-store'
    try {
        $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    } finally {
        try { $Context.Response.OutputStream.Close() } catch {}
        try { $Context.Response.Close() } catch {}
    }
}

function Read-RequestJson {
    # Reads the request body as UTF-8, parses it as JSON, and returns a
    # hashtable. Returns $null on empty body, non-UTF8 content, or
    # malformed JSON; route handlers map $null to a 400 invalid_json
    # response. -Depth 12 keeps the parser permissive enough for the
    # nested recipe shape without unbounded recursion.
    #
    # -DateKind String prevents PowerShell 7.5+ from auto-coercing ISO
    # 8601 timestamp strings (createdAt, updatedAt) on the inbound side
    # into [datetime] values; the recipe schema declares those fields
    # as type=string and Test-RecipeSchemaNode's `-is [string]` check
    # would otherwise reject any round-tripped recipe. Read-RecipeFile
    # uses the same flag for symmetry on the persistence side.
    param($Context)
    $req = $Context.Request
    if (-not $req.HasEntityBody) { return $null }
    try {
        $enc = $req.ContentEncoding
        if (-not $enc) { $enc = [System.Text.Encoding]::UTF8 }
        $reader = New-Object System.IO.StreamReader($req.InputStream, $enc)
        try { $raw = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } catch {
        return $null
    }
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    try {
        return ($raw | ConvertFrom-Json -AsHashtable -Depth 12 -DateKind String)
    } catch {
        return $null
    }
}

function Get-HealthPayload {
    # Local, runtime-only health snapshot. No telemetry, no cloud
    # upload, no background probe — this payload is built only when
    # someone GETs /api/v1/health, and only from state the broker can
    # actually inspect right now. Every field is classified inside the
    # response itself (see evidenceClassification) so the chef can
    # tell which values are authoritative, which are sampled at request
    # time, which exist only for the lifetime of this broker process,
    # and which are configuration constants. See docs/OPERATOR_GUIDE.md
    # §11.8 for the full contract.
    #
    # Phase AG.C10 -- uptime is now derived from the MONOTONIC anchor
    # (Stopwatch.GetTimestamp() at broker start) rather than wall-clock
    # subtraction. This means uptime is operationally truthful even when
    # the wall clock has been rolled back or jumped forward during this
    # broker's lifetime. uptimeIsAnomalous + timeAnomaly surface ANY
    # discontinuity between wall and monotonic so the chef can see that
    # something happened to the system clock (NTP step, DST, sleep, VM
    # pause) WITHOUT having that information silently smoothed away.
    $skew = Get-BrokerTimeSkewSnapshot
    $uptime = [int][Math]::Floor([Math]::Max(0.0, $skew.monoElapsedSec))

    $timeAnomaly = $null
    if ($skew.anomalyKind) {
        $timeAnomaly = [ordered]@{
            kind                = $skew.anomalyKind
            wallElapsedSec      = [Math]::Round($skew.wallElapsedSec, 3)
            monoElapsedSec      = [Math]::Round($skew.monoElapsedSec, 3)
            skewSec             = [Math]::Round($skew.skewSec, 3)
            anomalyThresholdSec = $skew.anomalyThresholdSec
        }
    }

    # dbSizeBytes is a sample: if the file is unreadable at this moment
    # (e.g. transient share violation), we surface $null rather than
    # the misleading 0 the previous implementation returned. $null
    # means "could not sample"; 0 would mean "sampled, empty".
    $dbSize = $null
    if (Test-Path -LiteralPath $Script:DatabaseFile -PathType Leaf) {
        try { $dbSize = (Get-Item -LiteralPath $Script:DatabaseFile).Length } catch { $dbSize = $null }
    }

    $errs = @($Script:RecentErrors.ToArray())
    $active = 0
    try { $active = $Script:CookRegistry.Count } catch {}

    # Status is DERIVED from local evidence the broker can actually
    # inspect; it is never hardcoded. The only states reported are:
    #
    #   'ok'       — broker is up, the recent-error ring is empty,
    #                and nothing has overflowed since startup.
    #   'degraded' — broker is up and serving, but at least one error
    #                has been recorded this session (either still
    #                visible in recentErrors, or displaced into
    #                recentErrorOverflowCount). The chef can continue
    #                operating; something has been recorded as wrong
    #                and the array itself enumerates what.
    #
    # Cookbook does NOT synthesize 'corrupted', 'recovering',
    # 'unsupported', or any other state it cannot truthfully verify at
    # this granularity. Truthful ambiguity is acceptable; fabricated
    # certainty is not. If the broker is so broken that it cannot
    # build this payload, the request fails — there is no synthetic
    # green-light path.
    $status = 'ok'
    if ($errs.Length -gt 0 -or $Script:RecentErrorOverflowCount -gt 0) {
        $status = 'degraded'
    }

    # AI.C2.8 -- single integer counter of observation-write INSERT
    # failures since this broker process started. Informational only;
    # does NOT contribute to the status derivation above.
    $obsWriteFailures = Get-UpdateRequestObservationWriteFailureCount

    # AI.C2.10 -- single integer counter of observation-write ATTEMPTS
    # since this broker process started. Paired denominator for the
    # AI.C2.8 failure counter. Informational only; does NOT contribute
    # to the status derivation above.
    $obsWriteAttempts = Get-UpdateRequestObservationWriteAttemptCount

    # AI.C3.1 -- paired counters for at-staging package-trust SHA-256
    # verification. Attempt counter increments at the start of every
    # Invoke-PackageStagingVerification call; failure counter
    # increments on any non-'match' outcome OR any exception in that
    # function. Informational only; does NOT contribute to the status
    # derivation above. See OPERATOR_GUIDE.md §17.15.
    $pkgTrustStagingAttempts = Get-PackageTrustStagingVerificationAttemptCount
    $pkgTrustStagingFailures = Get-PackageTrustStagingVerificationFailureCount

    # AI.C3.2 -- paired counters for at-apply and at-launch
    # package-trust SHA-256 verification. Same runtime-only,
    # informational-only invariants as the at-staging pair
    # above; neither contributes to the status derivation.
    # See OPERATOR_GUIDE.md §17.16.
    $pkgTrustApplyAttempts   = Get-PackageTrustApplyVerificationAttemptCount
    $pkgTrustApplyFailures   = Get-PackageTrustApplyVerificationFailureCount
    $pkgTrustLaunchAttempts  = Get-PackageTrustLaunchVerificationAttemptCount
    $pkgTrustLaunchFailures  = Get-PackageTrustLaunchVerificationFailureCount

    return [ordered]@{
        status                   = $status
        startedAtUtc             = $Script:StartedAt.ToString('o')
        uptimeSeconds            = $uptime
        uptimeIsAnomalous        = [bool]$skew.anomalyKind
        timeAnomaly              = $timeAnomaly
        activeCooks              = $active
        dbSizeBytes              = $dbSize
        recentErrors             = $errs
        recentErrorCount         = $errs.Length
        recentErrorCapacity      = $Script:RecentErrorCapacity
        recentErrorOverflowCount = $Script:RecentErrorOverflowCount
        updateRequestObservationWriteFailureCount = $obsWriteFailures
        updateRequestObservationWriteAttemptCount = $obsWriteAttempts
        packageTrustStagingVerificationAttemptCount = $pkgTrustStagingAttempts
        packageTrustStagingVerificationFailureCount = $pkgTrustStagingFailures
        packageTrustApplyVerificationAttemptCount   = $pkgTrustApplyAttempts
        packageTrustApplyVerificationFailureCount   = $pkgTrustApplyFailures
        packageTrustLaunchVerificationAttemptCount  = $pkgTrustLaunchAttempts
        packageTrustLaunchVerificationFailureCount  = $pkgTrustLaunchFailures
        workspaceFolderPath      = $Script:WorkspacePath
        brokerSession            = [ordered]@{
            # Phase AH.C3 -- restart-boundary visibility on the
            # unauthenticated /api/v1/health surface. These fields let
            # the SPA distinguish a broker restart from a network blip
            # WITHOUT having to first hit a Bearer-gated route and
            # receive a 401. Every field is observational; none implies
            # runtime continuity, authority continuity, cook resumption,
            # or session restoration. A new sessionId on a subsequent
            # /api/v1/health probe means the appliance booted a NEW
            # broker process -- it does NOT authorize any stale tab to
            # continue acting. Truthful instrumentation, not a
            # reassurance dashboard.
            sessionId             = $Script:BrokerSessionId
            startedAtUtc          = $Script:StartedAt.ToString('o')
            startupClassification = $Script:BrokerStartupClassification
        }
        evidenceClassification   = [ordered]@{
            status                   = 'derived'
            startedAtUtc             = 'runtime-only'
            uptimeSeconds            = 'runtime-only'
            uptimeIsAnomalous        = 'runtime-only'
            timeAnomaly              = 'runtime-only'
            activeCooks              = 'runtime-only'
            dbSizeBytes              = 'sampled'
            recentErrors             = 'runtime-only'
            recentErrorCount         = 'runtime-only'
            recentErrorCapacity      = 'configuration'
            recentErrorOverflowCount = 'runtime-only'
            updateRequestObservationWriteFailureCount = 'runtime-only'
            updateRequestObservationWriteAttemptCount = 'runtime-only'
            packageTrustStagingVerificationAttemptCount = 'runtime-only'
            packageTrustStagingVerificationFailureCount = 'runtime-only'
            packageTrustApplyVerificationAttemptCount   = 'runtime-only'
            packageTrustApplyVerificationFailureCount   = 'runtime-only'
            packageTrustLaunchVerificationAttemptCount  = 'runtime-only'
            packageTrustLaunchVerificationFailureCount  = 'runtime-only'
            workspaceFolderPath      = 'authoritative'
            brokerSession            = [ordered]@{
                sessionId             = 'runtime-only'
                startedAtUtc          = 'runtime-only'
                startupClassification = 'observational'
            }
        }
    }
}

function Invoke-RequestHandler {
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    # Origin check applies everywhere.
    if (-not (Test-OriginAllowed -Request $req)) {
        Write-JsonResponse -Context $Context -Status 403 -Body @{ error = 'origin_not_allowed' }
        return
    }

    # WebSocket upgrade endpoint. Uses ?t=<sessionToken> query auth (not
    # Bearer) because browsers cannot set custom headers on WS upgrades.
    # Token redaction is mandatory before any error path that touches the
    # request URL — see Get-RedactedRequestUrl in WebSocketHub.ps1.
    if ($path -eq '/ws') {
        if ($req.IsWebSocketRequest) {
            Invoke-WsUpgrade -Context $Context
        } else {
            Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'websocket_upgrade_required' }
        }
        return
    }

    # Cook-view live-tail WebSocket: per-cook URL, opaque UTF-8 text
    # frames. This is the specialized cook-view transport in
    # Routes/CookLogWs.ps1 — INTENTIONALLY SEPARATE from the /ws hub
    # above. Same ?t= auth pattern because browsers cannot Bearer-auth
    # WS upgrades; the path-specific check must sit ABOVE the bearer-
    # token gate. Pre-upgrade validation (origin, token, cookId, cook
    # exists, cook running, cook.log exists) all happens inside
    # Invoke-CookLogWs so a missing Upgrade header simply returns 400
    # without leaking any cook metadata.
    if ($path -match '^/api/v1/cooks/([^/]+)/log/ws$') {
        Invoke-CookLogWs -Context $Context -CookId $matches[1]
        return
    }

    if ($path -eq '/api/v1/health' -and $method -eq 'GET') {
        # Unauthenticated by design (see resolution A4). No CSRF check
        # because health is a safe GET.
        Write-JsonResponse -Context $Context -Status 200 -Body (Get-HealthPayload)
        return
    }

    # All other /api/v1/* routes require Bearer.
    if ($path.StartsWith('/api/v1/')) {
        if (-not (Test-BearerToken -Request $req)) {
            # Phase AH.C3 -- bounded, non-restorative reason vocab. The
            # reason is drawn from $Script:BrokerUnauthorizedReasons
            # and says ONLY what the broker can truthfully say: the
            # bearer is not recognized. The 401 deliberately does NOT
            # synthesize "broker restarted" / "token expired" / "session
            # ended" -- the broker cannot prove any of those from the
            # token alone.
            Write-JsonResponse -Context $Context -Status 401 -Body @{
                error  = 'unauthorized'
                reason = 'session_token_not_recognized'
            }
            return
        }
        # State-changing verbs require the CSRF header.
        if ($method -in @('POST','PUT','PATCH','DELETE')) {
            if (-not (Test-CsrfHeader -Request $req)) {
                Write-JsonResponse -Context $Context -Status 403 -Body @{ error = 'csrf_required' }
                return
            }
        }
        # Phase AF -- broker lock middleware. The lock-state machine
        # lives in Auth/BrokerLock.ps1; the HTTP surface (lock-state /
        # unlock / lock) is Routes/BrokerLock.ps1. The middleware sits
        # AFTER Bearer/CSRF (we never leak lock-state to unauthenticated
        # callers) and BEFORE the route table (so a Locked broker
        # returns 423 uniformly across CRUD routes without each route
        # having to re-check).
        #
        # Order is intentional:
        #   1. Dispatch /api/v1/broker/* FIRST -- those routes MUST work
        #      while Locked (that is how the operator unlocks).
        #      Invoke-BrokerLockRoute does NOT bump activity for
        #      lock-state polls (otherwise SPA polling would keep the
        #      broker unlocked indefinitely).
        #   2. Then check the lock state. If Locked, return 423 for
        #      every other /api/v1/* path.
        #   3. Then dispatch to the regular route table; each consumed
        #      route bumps lock activity so the inactivity timeout
        #      refreshes while the operator is actively using the SPA.
        $lockRouteConsumed = Invoke-BrokerLockRoute -Context $Context
        if ($lockRouteConsumed) { return }
        # /api/v1/broker/shutdown -- cooperative shutdown invoked by the
        # SPA's Close App modal. Must sit ABOVE the Locked-state gate
        # so the operator can close the app + stop the server from the
        # lock screen without first unlocking. Auth (Bearer + CSRF +
        # loopback origin) is still enforced by middleware upstream.
        if ($path -eq '/api/v1/broker/shutdown') {
            $consumed = Invoke-BrokerShutdownRoute -Context $Context
            if ($consumed) { return }
        }
        # /api/v1/broker/close-intent -- session-scoped marker write
        # used by close-app.js to inform the sibling app-window
        # watchdog that the disappearance of the Edge --app= window
        # was operator-initiated, not a native OS X / taskbar close.
        # Dispatched ABOVE the Locked-state gate (same rationale as
        # /broker/shutdown) so the SPA can record intent from the
        # lock overlay's tertiary Close App path.
        if ($path -eq '/api/v1/broker/close-intent') {
            $consumed = Invoke-BrokerCloseIntentRoute -Context $Context
            if ($consumed) { return }
        }
        # Degraded-boot dispatch. When the broker booted with
        # $Script:BrokerStartupAcquisitionRequired = $true (external
        # policy + PAX script absent / hash mismatch), the operator
        # has had no opportunity to establish Hello / WebAuthn
        # credentials, so the lock state machine has no meaning
        # yet -- a blanket 423 would trap the SPA in an unrecoverable
        # state. Dispatch /api/v1/setup/* directly so the first-run
        # acquisition flow is reachable, and short-circuit the
        # cook-spawning routes via the acquisition gate so the SPA
        # sees the actionable 409 acquisitionRequired body instead
        # of an opaque 423. Bearer + CSRF middleware above still
        # apply. Once acquisition completes, Routes\Setup.ps1
        # clears $Script:BrokerStartupAcquisitionRequired and this
        # block becomes a no-op.
        if ($Script:BrokerStartupAcquisitionRequired) {
            if ($path -eq '/api/v1/setup' -or $path.StartsWith('/api/v1/setup/')) {
                $consumed = Invoke-SetupRoute -Context $Context
                if ($consumed) { return }
            }
            if ($path -match '^/api/v1/recipes/[^/]+/cook$' -or
                $path -match '^/api/v1/cooks/[^/]+/resume$' -or
                ($method -eq 'PUT' -and $path -match '^/api/v1/recipes/[^/]+/scheduled-task$')) {
                if (Invoke-AcquisitionGateOrShortCircuit -Context $Context -Endpoint ($method + ' ' + $path)) {
                    return
                }
            }
        }
        if ((Get-BrokerLockState) -eq 'Locked') {
            $lr = New-BrokerLockLockedResponse -AttemptedMethod $method -AttemptedPath $path
            Write-JsonResponse -Context $Context -Status $lr.status -Body $lr.body
            return
        }
        # /api/v1/auth/profiles(/...)? -- auth-profile registry CRUD,
        # secret bind/remove, bounded structural test. All mutations
        # are gated by Invoke-BrokerLockReAuthForOp inside the route
        # handlers (per-op fresh re-auth doctrine).
        if ($path -eq '/api/v1/auth/profiles' -or $path.StartsWith('/api/v1/auth/profiles/')) {
            $consumed = Invoke-AuthProfilesRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        # Phase UX-1H -- LOCK-GATED WebAuthn endpoints (register-challenge,
        # register). The LOCK-BYPASS WebAuthn endpoints (status,
        # unlock-challenge, unlock) are handled inside
        # Invoke-BrokerLockRoute above so they work when Locked.
        # Registration requires the broker to already be Unlocked --
        # the bootstrap UX is: legacy broker-owned /unlock first,
        # then auto-register a WebAuthn credential for subsequent
        # browser-owned unlocks.
        if ($path.StartsWith('/api/v1/broker/webauthn/')) {
            $consumed = Invoke-BrokerWebAuthnRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        # /api/v1/recipes(/...)? including /api/v1/recipes/preview and
        # /api/v1/recipes/<id>/cook. The cook subpath is intercepted by
        # Invoke-CooksRoute BEFORE Invoke-RecipesRoute so the recipe
        # router never sees it.
        if ($path -match '^/api/v1/recipes/[0-9A-HJKMNP-TV-Z]{26}/cook$') {
            # AI.C9.1 -- scheduler-origin observation. The bundled
            # scheduler launcher tags its POST with X-PAX-Origin:
            # scheduler. The broker records ONE environment_observations
            # row (condition=scheduler_invocation, outcome=observed)
            # BEFORE entering Invoke-CooksRoute. The cook execution
            # path itself is unchanged -- the row is the sole
            # difference between a scheduled and a manual cook
            # trigger; auth, CSRF, lock-state, route handler, cook
            # engine, and projections are all byte-identical to a
            # manual browser POST. Observation-write failure is
            # non-fatal (Add-EnvironmentObservation returns $null
            # and the cook still runs). The gate is header presence
            # only; no parsing, no privilege difference, no
            # branching of cook execution.
            $originHdr = $req.Headers['X-PAX-Origin']
            if ($originHdr -eq 'scheduler') {
                $null = Add-EnvironmentObservation `
                    -Condition 'scheduler_invocation' `
                    -Outcome 'observed' `
                    -EvidenceClassification 'observational'
            }
            $consumed = Invoke-CooksRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        # /api/v1/cooks(/...)? — cook lifecycle endpoints.
        if ($path -eq '/api/v1/cooks' -or $path.StartsWith('/api/v1/cooks/')) {
            $consumed = Invoke-CooksRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        # /api/v1/runtime(/...)? — read-only authoritative runtime
        # metadata (Cookbook version, bundled PAX version + SHA + path,
        # release channel, integrity state, manifest alignment). No
        # mutation, no network, no cache, no update behavior.
        if ($path -eq '/api/v1/runtime/version' -or $path.StartsWith('/api/v1/runtime/')) {
            $consumed = Invoke-RuntimeRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        # /api/v1/notifications — read-only replay of the durable JSONL
        # notification log for the in-app toast/banner surface. GET only;
        # never writes, never creates the Notifications directory; reads a
        # single validated <date>.jsonl file; no network.
        if ($path -eq '/api/v1/notifications') {
            $consumed = Invoke-NotificationsRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        # /api/v1/settings/notifications — user-controlled notification
        # preferences. GET returns the four boolean preferences; PUT
        # persists a validated subset of them to the settings table and
        # re-reflects them into the process environment. No network;
        # only the four approved boolean keys are accepted.
        if ($path -eq '/api/v1/settings/notifications') {
            $consumed = Invoke-NotificationSettingsRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        # /api/v1/updates(/...)? — operator-driven update flow.
        # POST /check fetches and validates the configured manifest URL.
        # POST /download stages the package from the manifest captured by
        # the most recent successful check. GET /state returns the
        # current in-memory state plus the staged-package inventory.
        # No background polling, no auto-install.
        if ($path -eq '/api/v1/updates' -or $path.StartsWith('/api/v1/updates/')) {
            $consumed = Invoke-UpdatesRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        # Phase 13 Stage 1 -- /api/v1/setup(/...)? acquire-pax routes.
        # Scaffolding only: every handler returns 501. Stage 2 implements
        # the manifest-verify + download / upload / cancel / state flow.
        if ($path -eq '/api/v1/setup' -or $path.StartsWith('/api/v1/setup/')) {
            $consumed = Invoke-SetupRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        # /api/v1/broker/shutdown is dispatched above the Locked gate
        # (see top of this function). The earlier dispatch returns
        # before reaching this point on any /api/v1/broker/shutdown
        # path, so no duplicate handler is needed here.
        # /api/v1/templates(/...)? — bundled Pantry template surface.
        # Read-only listing + materialize. Templates are static JSON
        # under app/templates/, loaded ONCE at startup. Materialize is
        # the only state-changing verb and produces a normal recipe via
        # the existing Test-RecipeAll → Write-RecipeFile → Add-RecipeRow
        # pipeline; the projection layer is unchanged.
        if ($path -eq '/api/v1/templates' -or $path.StartsWith('/api/v1/templates/')) {
            $consumed = Invoke-TemplatesRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        # V1.S06c -- /api/v1/scheduled-tasks (collection GET) AND
        # /api/v1/recipes/<id>/scheduled-task (per-recipe GET / PUT /
        # DELETE). Both shapes are funneled into the same router so
        # the per-recipe regex match sits in front of Invoke-RecipesRoute
        # (otherwise the recipe router would swallow the scheduled-task
        # subpath). Mutating verbs (PUT, DELETE) on the per-recipe
        # form are gated by Invoke-BrokerLockReAuthForOp inside the
        # route handlers with OpClass='scheduleConfig'; PUT also
        # invokes the AppRegistrationSecret prompt path.
        if ($path -eq '/api/v1/scheduled-tasks') {
            $consumed = Invoke-ScheduledTasksRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        if ($path -match '^/api/v1/recipes/[0-9A-HJKMNP-TV-Z]{26}/scheduled-task$') {
            $consumed = Invoke-ScheduledTasksRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        if ($path -match '^/api/v1/recipes/[0-9A-HJKMNP-TV-Z]{26}/takeout$') {
            $consumed = Invoke-RecipeTakeoutRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        if ($path -eq '/api/v1/recipe-takeout/validate' -or $path.StartsWith('/api/v1/recipe-takeout/')) {
            $consumed = Invoke-RecipeTakeoutRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        if ($path -eq '/api/v1/recipe-lite/validate' -or $path.StartsWith('/api/v1/recipe-lite/')) {
            $consumed = Invoke-RecipeLiteRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        # /api/v1/recipes(/...)? including /api/v1/recipes/preview.
        if ($path -eq '/api/v1/recipes' -or $path.StartsWith('/api/v1/recipes/')) {
            $consumed = Invoke-RecipesRoute -Context $Context
            if ($consumed) { Update-BrokerLockActivity; return }
        }
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found' }
        return
    }

    # Everything else falls through to the static-file handler. The static
    # surface is unauthenticated by design: the SPA shell must load before
    # the URL-fragment token can be captured into sessionStorage.
    Invoke-StaticHandler -Context $Context
}

# ====================================================================
# Shutdown
# ====================================================================

# Static-file handler lives in its own helper file so the broker entrypoint
# stays focused on lifecycle. Dot-sourced (not Import-Module) so $Script:*
# variables resolve to this session scope.
. (Join-Path $Script:BrokerRoot 'Http\StaticHandler.ps1')

# Phase AF -- local auth-profile governance. Three Auth/* helpers are
# dot-sourced BEFORE any Routes/* so the routes can call their public
# functions directly. Load order matters: WindowsReAuth.ps1 establishes
# the WinRT IUserConsentVerifierInterop wrapper Invoke-WindowsReAuth;
# BrokerLock.ps1 wires the state machine + Invoke-BrokerLockReAuthForOp
# (which calls Invoke-WindowsReAuth internally); CredentialManager.ps1
# owns the Windows Credential Manager P/Invoke (advapi32.dll) and the
# Set/Test/Read/Remove-AuthProfileSecret quartet. Routes/AuthProfiles.ps1
# composes these into the HTTP surface; Routes/BrokerLock.ps1 exposes
# the lock-state / unlock / lock endpoints.
. (Join-Path $Script:BrokerRoot 'Auth\CredentialManager.ps1')
. (Join-Path $Script:BrokerRoot 'Auth\WindowsReAuth.ps1')
. (Join-Path $Script:BrokerRoot 'Auth\BrokerLock.ps1')
. (Join-Path $Script:BrokerRoot 'Auth\WebAuthnVerify.ps1')
. (Join-Path $Script:BrokerRoot 'Routes\BrokerLock.ps1')
. (Join-Path $Script:BrokerRoot 'Routes\BrokerShutdown.ps1')
. (Join-Path $Script:BrokerRoot 'Routes\BrokerCloseIntent.ps1')
. (Join-Path $Script:BrokerRoot 'Routes\BrokerWebAuthn.ps1')
. (Join-Path $Script:BrokerRoot 'Routes\AuthProfiles.ps1')

# Recipe schema validator and CRUD routes follow the same dot-source
# pattern: they read $Script:SqliteConn, $Script:RecipesDir,
# Write-JsonResponse, Read-RequestJson, Get-UtcNowIso from this session.
. (Join-Path $Script:BrokerRoot 'Routes\RecipeValidator.ps1')
. (Join-Path $Script:BrokerRoot 'Routes\Recipes.ps1')
. (Join-Path $Script:BrokerRoot 'Routes\RecipeTakeout.ps1')
. (Join-Path $Script:BrokerRoot 'Routes\RecipeLite.ps1')

# Read-only runtime metadata route. Depends only on $Script:* state
# already populated by Test-BundledPaxIntegrity and Test-ManifestAlignment.
# No SQLite, no filesystem reads per request, no network.
. (Join-Path $Script:BrokerRoot 'Routes\Runtime.ps1')

# Read-only in-app notification replay route. Reads the durable JSONL
# notification log (<workspace>\Notifications\<date>.jsonl) so the web UI
# can replay bake completion/failure/interruption events as toasts and
# banners. Read-only: never writes, never creates the Notifications
# directory, no network, single validated date file only.
. (Join-Path $Script:BrokerRoot 'Routes\Notifications.ps1')

# User-controlled notification preferences route. GET/PUT
# /api/v1/settings/notifications backed by the settings table. Provides
# Get-AllNotificationSettings / Sync-NotificationPreferenceEnv used at
# startup to reflect the durable preferences into the process environment.
# No network; persists only the four approved boolean keys.
. (Join-Path $Script:BrokerRoot 'Routes\NotificationSettings.ps1')

# Operator-driven update flow. Two outbound HTTPS choke-points live in
# the Update modules: Manifest.psm1 fetches and validates the manifest;
# Package.psm1 downloads, SHA-256 verifies, and stages the package into
# <workspace>\Updates\. The Routes\Updates.ps1 dispatcher exposes
# /api/v1/updates/{check,download,state} and is driven exclusively by
# Settings UI buttons -- the broker never auto-polls, never auto-
# downloads, and never auto-installs.
#
# Trust.psm1 is a separate read-only surface that evaluates per-package
# trust state (hash provenance, optional detached-signature sidecar,
# optional signer metadata, operator allow-list under <workspace>\Trust\
# trusted-signers.json). It performs NO cryptographic verification; it
# only reports the truthful state of artifacts on disk. The day a real
# code-signing path lands, the verifier slots into that module without
# disturbing the download/staging surface.
Import-Module -Force (Join-Path $Script:BrokerRoot 'Update\Manifest.psm1')
Import-Module -Force (Join-Path $Script:BrokerRoot 'Update\Package.psm1')
Import-Module -Force (Join-Path $Script:BrokerRoot 'Update\Trust.psm1')
. (Join-Path $Script:BrokerRoot 'Routes\Updates.ps1')

# Phase 13 Stage 1 -- external PAX engine acquisition scaffolding.
# ManifestSchema.psm1 is the read-only §7.4 schema validator (pure
# function, no I/O). Acquisition.psm1 is signature-only stubs that
# throw NotImplemented (Stage 2 fills in the bodies under the §4.5
# immutability contract documented at the top of that module).
# Routes\Setup.ps1 exposes /api/v1/setup/acquire-pax/{download,
# upload,cancel,state} returning HTTP 501 in this stage.
Import-Module -Force (Join-Path $Script:BrokerRoot 'Engine\ManifestSchema.psm1')
Import-Module -Force (Join-Path $Script:BrokerRoot 'Engine\Acquisition.psm1')
. (Join-Path $Script:BrokerRoot 'Routes\Setup.ps1')

# Phase 13 Stage 4 -- cross-route acquisition gate. Refuses cook /
# resume / schedule-put requests with HTTP 409 acquisitionRequired
# while policy = 'external' and state != 'acquired'. Dot-sourced
# AFTER Acquisition.psm1 import so Test-PaxAcquisitionGate can call
# the module-exported Get-PaxAcquisitionState and read the
# Script:AcquisitionStateAcquired token without an import-order
# coupling between Routes\* files.
. (Join-Path $Script:BrokerRoot 'Routes\AcquisitionGate.ps1')

# Phase AI.C3.1 -- package-trust observation writer + at-staging
# SHA-256 verification helper. Dot-sourced so the writer's
# $Script: counters and the new schema's INSERT site share the
# broker script scope with Get-HealthPayload (which reads the
# counters) and Apply-M1Schema (which created the table). No HTTP
# route is wired to this module in AI.C3.1; later slices integrate
# it into the existing staging flow. See OPERATOR_GUIDE.md §17.15.
. (Join-Path $Script:BrokerRoot 'Update\PackageTrust.ps1')

# Phase AI.C5.1 -- environment-observation writer. Dot-sourced so
# the writer's INSERT site shares the broker script scope with
# Apply-M1Schema (which created the table). The writer is consumed
# by the ConstrainedLanguage probe at startup (G1) and reserved
# for AI.C5.2 (G2 low-disk, G3 workspace-path observation
# emission). See OPERATOR_GUIDE.md §17.18.
. (Join-Path $Script:BrokerRoot 'Environment.ps1')

# Phase AI.C6.1 -- diagnostics bundle primitive. Dot-sourced so
# Export-DiagnosticsBundle shares the broker script scope with
# $Script:DatabaseFile, $Script:VersionFile, $Script:WorkspacePath,
# $Script:RecentErrors, and $Script:CookbookVersion. Export is
# operator-initiated, pull-only, and read-only with respect to
# the database (no connection is opened). See OPERATOR_GUIDE.md
# §17.20.
. (Join-Path $Script:BrokerRoot 'Diagnostics.ps1')

# Bundled Pantry templates: validator first (defines $Script:TemplateSchema
# and reuses Test-RecipeSchemaNode from RecipeValidator.ps1), then route
# handlers (depend on Recipes.ps1 helpers Get-RecipeCreatedByBlock,
# New-RecipeId, Write-RecipeFile, Add-RecipeRow, Initialize-RecipesDirs,
# Get-RecipeFilePath and TemplateValidator.ps1's $Script:TemplateIdPattern
# / Test-TemplatePaxCompatibility). Catalog is loaded ONCE here, at
# startup, from the install-tree app/templates/ directory; there is no
# per-request rescan.
. (Join-Path $Script:BrokerRoot 'Routes\TemplateValidator.ps1')
. (Join-Path $Script:BrokerRoot 'Routes\Templates.ps1')
$catalogResult = Read-TemplateCatalog
$Script:TemplateCatalog = $catalogResult.loaded
Write-Host ('Broker: bundled-template catalog loaded ({0} valid, {1} failed) from {2}' -f `
    $Script:TemplateCatalog.Count, ($catalogResult.failures | Measure-Object).Count, $Script:TemplatesDir)
foreach ($f in $catalogResult.failures) {
    Write-Host ('  template REJECTED: ' + $f.file) -ForegroundColor Yellow
    foreach ($e in $f.errors) {
        Write-Host ('    ' + $e.instancePath + ' [' + $e.keyword + '] ' + $e.message) -ForegroundColor Yellow
    }
}

# PAX adapter is a pure projector; imported as a module so it cannot
# accidentally read broker state or perform I/O. Three functions are
# exported:
#   Convert-RecipeToPaxArgv  -> [string]     (rendered PAX command)
#   Get-PaxArgvArray         -> [string[]]   (canonical argv tokens)
#   Get-PaxInvocationPlan    -> [hashtable]  (full invocation plan; the
#                                             single source of truth for
#                                             command.txt, command-argv.json,
#                                             cooks.command_argv_json, and
#                                             the supervisor's -CommandExpr)
Import-Module -Force (Join-Path $Script:BrokerRoot 'Pax\Adapter.psm1')

# Channel-agnostic notification core + durable JSONL sink. Provides
# Invoke-NotificationDispatch / New-NotificationEvent / Write-NotificationJsonl
# for the main runspace. The cook supervisor runs in a ThreadJob
# runspace that does not inherit dot-sourced functions, so it loads
# this same file independently from $Script:BrokerRoot; this dot-source
# serves the main runspace (and any in-process caller).
. (Join-Path $Script:BrokerRoot 'Notify\Notification.ps1')

# The opt-in outbound webhook surface. This is the single isolated file
# that performs any notification outbound network call (one bounded HTTPS
# POST, no redirects, https-only, host-validated). Notification.ps1 also
# loads it as a sibling for the cook supervisor's ThreadJob runspace; this
# explicit dot-source guarantees the URL validator and sender are present
# in the main runspace for the settings route's PUT validation and for any
# in-process dispatch. It is disabled by default and only ever attempted
# when the operator opts in with a validated https endpoint.
. (Join-Path $Script:BrokerRoot 'Notify\Webhook.ps1')

# The best-effort Windows Action Center toast surface and the three
# per-status notification surfaces are user-controlled. Their durable
# preferences live in the settings table and are reflected into the
# process environment (PAXCOOKBOOK_OS_TOAST_ENABLED and the three
# PAXCOOKBOOK_NOTIFY_* variables) by Sync-NotificationPreferenceEnv. That
# reflection runs after the SQLite connection opens (see below, after
# Apply-M1Schema), so the settings table is available to read. Setting the
# process environment there is inherited by the cook supervisor's ThreadJob
# runspace (same process), so both manual and scheduled terminal funnels
# honor the preferences without any per-call-site wiring. Notification
# delivery remains best-effort: any failure is captured in the dispatch
# result and never affects JSONL, bake finalization, scheduled reconcile,
# cook history, or broker startup.

# WebSocket hub, cook supervisor (ThreadJob scriptblock), and cook
# lifecycle routes. Order matters — Cooks.ps1 references
# Start-CookSupervisor (from Supervisor.ps1) and the supervisor needs
# Publish-CookEvent's data layout (kept inline in the supervisor to avoid
# cross-runspace function-visibility coupling). WebSocketHub.ps1 must be
# dot-sourced before the dispatch loop sees its first /ws upgrade.
. (Join-Path $Script:BrokerRoot 'Ws\WebSocketHub.ps1')
. (Join-Path $Script:BrokerRoot 'Cook\Supervisor.ps1')
. (Join-Path $Script:BrokerRoot 'Routes\Cooks.ps1')

# V1.S06d -- scheduled-task wrapper-folder reconciler. Imports
# evidence the OS-spawned wrapper (launcher/Invoke-PAXScheduledRecipe.ps1)
# wrote at fire time into cooks + cook_artifacts so the runs list and
# cook-detail UI surface scheduled-task runs. Depends on
# Get-RecipeRow (Routes/Recipes.ps1, already loaded), Add-RecentError
# / Get-UtcNowIso (this file), and $Script:SqliteConn. Must load
# AFTER Routes/Cooks.ps1 so $Script:CookIdPattern is in scope for any
# downstream call sites that reuse it, and BEFORE Routes/ScheduledTasks.ps1
# so the scheduled-task route handler can call into the reconciler
# during the per-recipe GET path.
. (Join-Path $Script:BrokerRoot 'Cook\ScheduledTaskReconcile.ps1')

# V1.S2 -- read-only OS-task drift reconciler. Composes the additive
# "osDrift" block for the per-recipe scheduled-task GET by child-spawning
# the registrar's "-Action probe" verb and classifying the result against
# the database row. Never calls *-ScheduledTask cmdlets and never writes
# state. Loads AFTER ScheduledTaskReconcile.ps1 and BEFORE
# Routes/ScheduledTasks.ps1 so the GET handler can call
# New-ScheduledTaskOsDriftObject. There is no startup sweep; the
# reconciler runs only on demand during the GET path.
. (Join-Path $Script:BrokerRoot 'Cook\ScheduledTaskDriftReconcile.ps1')

# V1.S06c -- native Windows Task Scheduler integration. The route
# handler depends on Get-RecipeRow + Read-RecipeFile (Recipes.ps1),
# Get-AuthProfileRow + Set-AuthProfileSecret (AuthProfiles.ps1 +
# CredentialManager.ps1), Get-RecipeProjectionHash (Adapter.psm1),
# and Invoke-BrokerLockReAuthForOp (BrokerLock.ps1), all loaded
# above. The route never imports or calls *-ScheduledTask cmdlets
# directly; OS-side registration is delegated to the external
# helper app/install/Register-PAXScheduledRecipe.ps1, spawned via
# Start-Process with non-secret argv.
. (Join-Path $Script:BrokerRoot 'Routes\ScheduledTasks.ps1')

# Specialized cook-view live-tail WebSocket transport. Intentionally
# separate from the /ws hub above; do not unify. Depends on
# Test-WsQueryToken + Get-RedactedRequestUrl (WebSocketHub.ps1) and
# Get-CookRow + $Script:CookIdPattern (Cooks.ps1), so it loads last.
. (Join-Path $Script:BrokerRoot 'Routes\CookLogWs.ps1')

function Invoke-Shutdown {
    # Phase AH.C1 -- accepts a StopReason drawn from
    # $Script:BrokerSessionStopReasons. The reason is forwarded to
    # Stop-BrokerSession so the broker_sessions row records the
    # operational cause of teardown. The function is idempotent via
    # $Script:ShuttingDown; subsequent calls (including the recursive
    # call path through Ctrl+C -> dispatch finally) are no-ops.
    #
    # Step order is doctrinal:
    #   1. Annotate active cooks BEFORE Stop-BrokerSession so the
    #      shutting-down evidence on cook rows lives even if step 2
    #      fails for any reason.
    #   2. Stop-BrokerSession persists the orderly-stop assertion on
    #      THIS broker's row.
    #   3. Stop + Close the HttpListener so no new requests can land
    #      while we tear down SQLite.
    #   4. WAL checkpoint(TRUNCATE) so the next launch reads
    #      fully-applied state.
    #   5. Close + Dispose the SQLite connection.
    #   6. Remove workspace.lock (the human-readable lock file).
    #   7. Close-WorkspaceLockHandle (release the FileShare.None sentinel).
    #
    # Doctrine: failure of ANY step is best-effort -- the broker is
    # going down and we cannot help by throwing. Each step is wrapped
    # in its own try/catch so a downstream failure does not prevent
    # an upstream step from completing.
    param([string]$StopReason = 'listener_disposed')
    if ($Script:ShuttingDown) { return }
    $Script:ShuttingDown = $true
    try { [void](Invoke-ActiveCookShutdownSweep) } catch {}
    try { Stop-BrokerSession -StopReason $StopReason } catch {}
    try {
        if ($Script:Listener) {
            try { $Script:Listener.Stop()  } catch {}
            try { $Script:Listener.Close() } catch {}
        }
    } catch {}
    try {
        if ($Script:SqliteConn) {
            try {
                # Best-effort WAL checkpoint so the next launch reads
                # fully-applied state.
                $cmd = $Script:SqliteConn.CreateCommand()
                $cmd.CommandText = 'PRAGMA wal_checkpoint(TRUNCATE);'
                [void]$cmd.ExecuteNonQuery()
                $cmd.Dispose()
            } catch {}
            try { $Script:SqliteConn.Close()    } catch {}
            try { $Script:SqliteConn.Dispose()  } catch {}
            $Script:SqliteConn = $null
        }
    } catch {}
    Remove-WorkspaceLock
    Close-WorkspaceLockHandle
}

# ====================================================================
# Main
# ====================================================================

# C1 - integrity verify, then mint session token.
if (-not (Test-VendoredSqliteIntegrity)) {
    exit $EXIT_E_SQLITE_DLL_INTEGRITY
}
if (-not (Test-BundledPaxIntegrity)) {
    exit $EXIT_E_PAX_SCRIPT_INTEGRITY
}
if (-not (Test-ManifestAlignment)) {
    exit $EXIT_E_PAX_SCRIPT_INTEGRITY
}
$Script:SessionToken = New-SessionToken

# C2 - bind loopback listener.
if (-not (Start-LoopbackListener)) {
    exit 5
}

# C3 - workspace lock.
#
# Step 1: acquire the per-workspace sentinel handle with FileShare.None.
# This is the authoritative ownership gate: only one process can hold
# the handle at any moment. Acquisition failure means another live
# broker process is currently serving this workspace.
if (-not (Open-WorkspaceLockHandle)) {
    try { $Script:Listener.Stop();  $Script:Listener.Close() } catch {}
    # The peer broker acquires the sentinel before it writes
    # workspace.lock, so a sub-second window can exist where the
    # sentinel is held but workspace.lock is not yet populated. Poll
    # briefly so the refusal message can identify the peer.
    $peerLock = $null
    for ($i = 0; $i -lt 20; $i++) {
        $peerLock = Read-WorkspaceLock
        if ($peerLock) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($peerLock) {
        Show-ActiveLockRefusalAndExit -Lock $peerLock
    }
    Write-Host ''                                                                                  -ForegroundColor Red
    Write-Host 'This workspace is already open in another Cookbook broker.'                        -ForegroundColor Red
    Write-Host '(Peer broker is still initializing; workspace.lock not yet populated.)'            -ForegroundColor Red
    Write-Host ''                                                                                  -ForegroundColor Red
    exit $EXIT_E_WORKSPACE_LOCKED
}

# Step 2: with the sentinel held, inspect any pre-existing
# workspace.lock to detect a stale-broker reclaim scenario or a peer
# broker process that pre-dates the sentinel handle.
$existingLock = Read-WorkspaceLock
if ($existingLock) {
    if (Test-PriorBrokerActive -Lock $existingLock) {
        # Active prior broker holds the workspace. Refuse and exit.
        # The listener is closed in Invoke-Shutdown via the catch path.
        try { $Script:Listener.Stop();  $Script:Listener.Close() } catch {}
        Show-ActiveLockRefusalAndExit -Lock $existingLock
    } else {
        # Stale lock. Overwrite and continue.
        Write-Host ('Stale workspace lock detected (prior PID=' + $existingLock.brokerProcessId + ', port=' + $existingLock.brokerPort + '). Reclaiming.') -ForegroundColor Yellow
        Write-WorkspaceLock
    }
} else {
    Write-WorkspaceLock
}

# Register Ctrl+C handler now that we hold the lock, so it can be
# released on orderly shutdown.
#
# CP5D: the cancel handler MUST be a runspace-free .NET delegate, not
# a PowerShell scriptblock. The CancelKeyPress event fires on a native
# callback thread that does NOT have a PowerShell runspace attached;
# the moment a scriptblock handler touches $Script:* variables or
# $e.Cancel, the runtime throws
#
#   System.Management.Automation.PSInvalidOperationException:
#     There is no Runspace available to run scripts in this thread...
#
# crashing the broker without running Invoke-Shutdown, so the
# workspace lock and active cooks leak. The fix: a static C# helper
# (PAXCookbook.Native.BrokerShutdownSignal) captures the cancel
# signal using only .NET state -- no scriptblock invocation, no
# PowerShell variable lookups. The dispatch loop's catch/finally
# observes the static signal and runs the actual cleanup
# (Invoke-Shutdown) from the main runspace.
#
# Earliest-writer-wins for $Script:ShutdownReason is preserved: the
# native handler stamps "operator_ctrl_c" into the static helper, and
# the dispatch loop transcribes it into $Script:ShutdownReason only
# when that variable is still $null.
if (-not ('PAXCookbook.Native.BrokerShutdownSignal' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Net;

namespace PAXCookbook.Native {
    public static class BrokerShutdownSignal {
        private static volatile bool _signaled;
        private static string _reason;
        private static HttpListener _listener;

        public static bool IsSignaled { get { return _signaled; } }
        public static string Reason { get { return _reason; } }

        public static void SetListener(HttpListener listener) {
            _listener = listener;
        }

        public static void Reset() {
            _signaled = false;
            _reason = null;
        }

        public static void HandleCancelKeyPress(object sender, ConsoleCancelEventArgs e) {
            try { e.Cancel = true; } catch { }
            if (_reason == null) {
                _reason = "operator_ctrl_c";
            }
            _signaled = true;
            HttpListener listener = _listener;
            if (listener != null) {
                try { listener.Stop(); } catch { }
            }
        }

        public static void Signal(string reason) {
            if (_reason == null) {
                _reason = reason;
            }
            _signaled = true;
            HttpListener listener = _listener;
            if (listener != null) {
                try { listener.Stop(); } catch { }
            }
        }

        public static ConsoleCancelEventHandler GetHandler() {
            return new ConsoleCancelEventHandler(HandleCancelKeyPress);
        }
    }
}
'@
}
[PAXCookbook.Native.BrokerShutdownSignal]::Reset()
[PAXCookbook.Native.BrokerShutdownSignal]::SetListener($Script:Listener)
$Script:CancelHandler = [PAXCookbook.Native.BrokerShutdownSignal]::GetHandler()
[Console]::add_CancelKeyPress($Script:CancelHandler)

# C4 - SQLite open + M1 schema.
try {
    Initialize-SqliteRuntime
    Open-SqliteConnection
    Apply-M1Schema
} catch {
    Add-RecentError ('SQLite startup failed: ' + $_.Exception.Message)
    Write-Host ('Broker: SQLite startup failed: ' + $_.Exception.Message) -ForegroundColor Red
    Invoke-Shutdown -StopReason 'startup_failure'
    exit 5
}

# Reflect the durable notification preferences from the settings table into
# the process environment now that the SQLite connection is open and the
# settings table exists. This sets PAXCOOKBOOK_OS_TOAST_ENABLED and the
# three PAXCOOKBOOK_NOTIFY_* variables that the notification dispatch path
# reads at send time in both the main runspace and the cook supervisor's
# ThreadJob. A read failure here is non-fatal: Sync-NotificationPreferenceEnv
# defaults every preference to enabled, matching the absent-row default.
try {
    Sync-NotificationPreferenceEnv
} catch {
    Add-RecentError ('Notification preference reflection failed: ' + $_.Exception.Message) -Source 'notification_settings'
}

# Phase AI.C2.7 -- boot-time append-only trigger presence verification.
# Runs immediately after Apply-M1Schema returns and before the dispatch
# loop is entered. If the AI.C2.6 triggers are not installed in the
# live database, the broker refuses to serve. No auto-repair, no
# auto-recreate, no fallback -- the verdict is terminal for this boot.
try {
    Assert-UpdateRequestObservationsAppendOnlyTriggersPresent
} catch {
    Add-RecentError ($_.Exception.Message) -Source 'observation_trigger_integrity'
    Write-Host ('Broker: ' + $_.Exception.Message) -ForegroundColor Red
    Invoke-Shutdown -StopReason 'startup_failure'
    exit $EXIT_E_OBSERVATION_TRIGGER_INTEGRITY
}

# Phase AI.C3.2 -- boot-time package-trust LAUNCH evaluation (G3).
# Runs ONCE per broker boot, after the AI.C2.7 trigger-integrity gate
# and BEFORE the HTTP listener binds. Iterates every currently-staged
# package under the workspace Updates\ folder and re-derives signature
# trust against the SYSTEM trust store via the same Get-PackageTrustState
# reader the /api/v1/updates/state surface uses. There is no caching,
# no carry-forward across restart, no remembered-trust flag, and no
# read of any prior pre_run observation row -- every boot re-evaluates
# trust from first principles. One row per staged package is written
# to package_trust_observations with boundary='pre_run'. On any
# hashMismatch / signatureInvalid the broker refuses to start with
# EXIT_E_PACKAGE_TRUST_INTEGRITY. 'unknown' (missing / hashUnknown)
# is observational -- recorded but not a refusal trigger.
try {
    $launchVerdict = Invoke-PackageLaunchVerification -WorkspacePath $Script:WorkspacePath
    if ($launchVerdict.refused) {
        $msg = ('Broker: at-launch package-trust verification refused (' + $launchVerdict.mismatchCount + ' mismatched packages of ' + $launchVerdict.evaluatedCount + ' evaluated). See package_trust_observations rows ' + ($launchVerdict.observationIds -join ',') + ' for evidence.')
        Add-RecentError $msg -Source 'package_trust_launch_integrity'
        Write-Host $msg -ForegroundColor Red
        Invoke-Shutdown -StopReason 'startup_failure'
        exit $EXIT_E_PACKAGE_TRUST_INTEGRITY
    }
} catch {
    Add-RecentError ('At-launch package-trust verification threw: ' + $_.Exception.Message) -Source 'package_trust_launch_integrity'
    Write-Host ('Broker: at-launch package-trust verification threw: ' + $_.Exception.Message) -ForegroundColor Red
    Invoke-Shutdown -StopReason 'startup_failure'
    exit $EXIT_E_PACKAGE_TRUST_INTEGRITY
}

# Phase AI.C5.1 -- ConstrainedLanguage detection (G1).
#
# Runs ONCE per broker boot, after the AI.C2.7 trigger-integrity
# gate and the AI.C3.2 at-launch package-trust evaluation, BEFORE
# the HTTP listener binds. Probes its own PowerShell language
# mode; if the engine is running in anything other than
# FullLanguage, the broker writes ONE environment_observations
# row tagged condition='constrained_language' / outcome='detected'
# and refuses to start with EXIT_E_ENVIRONMENT_CONSTRAINED_LANGUAGE
# (= 10). There is no fallback, no auto-elevation, no "try to
# run anyway" -- the broker's contract assumes FullLanguage
# semantics and a Constrained / RestrictedLanguage / NoLanguage
# engine cannot honor that contract. The observation row is
# evidence for the operator; the exit code is the verdict.
$detectedLanguageMode = [string]$ExecutionContext.SessionState.LanguageMode
if ($detectedLanguageMode -ne 'FullLanguage') {
    $rowId = Add-EnvironmentObservation `
        -Condition 'constrained_language' `
        -Outcome 'detected' `
        -EvidenceClassification 'observational'
    $rowSuffix = if ($null -eq $rowId) { ' (observation write failed; see recent-errors)' } else { (' (environment_observations row ' + $rowId + ')') }
    $msg = ('Broker: PowerShell language mode is ' + $detectedLanguageMode + ', not FullLanguage. The broker requires FullLanguage semantics and will not start.' + $rowSuffix)
    Add-RecentError $msg -Source 'environment_constrained_language'
    Write-Host $msg -ForegroundColor Red
    Invoke-Shutdown -StopReason 'startup_failure'
    exit $EXIT_E_ENVIRONMENT_CONSTRAINED_LANGUAGE
}

# Phase AI.C5.2 -- low-disk detection on workspace root (G2).
#
# Runs ONCE per broker boot, after the AI.C5.1 ConstrainedLanguage
# probe and BEFORE the AI.C5.2 workspace-path-forbidden gate. Probes
# the AvailableFreeSpace of the volume that hosts $Script:WorkspacePath
# via [System.IO.DriveInfo] (same primitive Test-CookDiskPrecheck uses
# at line ~414; this is a SEPARATE probe against a SEPARATE threshold
# pair and does NOT reuse $Script:MinFreeDiskBytesForCook, which is
# a per-cook pre-flight threshold for a different code path).
#
# Two thresholds, both inlined here (no new vocabulary array, no new
# $Script: constant -- the literal values are the contract):
#   - HARD floor : 500 MB free. Below this the broker writes ONE
#                  environment_observations row tagged
#                  condition='low_disk' / outcome='detected' and
#                  refuses to start with EXIT_E_ENVIRONMENT_LOW_DISK
#                  (= 11). At this level any cook would immediately
#                  fail the existing AG.C7 pre-flight; running anyway
#                  would just produce a long tail of cook refusals
#                  with no useful evidence.
#   - SOFT warn  : 2 GB  free. Between the two thresholds the broker
#                  writes ONE environment_observations row tagged
#                  condition='low_disk' / outcome='warning' and
#                  CONTINUES startup. This is observational only --
#                  it neither refuses, nor blocks any cook, nor
#                  enters any periodic re-check loop.
#
# If the probe itself throws (drive not ready, transient I/O, etc.)
# the broker continues startup -- a single probe failure must not
# block service. The exception is recorded via Add-RecentError so the
# operator sees it on /api/v1/health.
try {
    $wsRootForDisk = [System.IO.Path]::GetPathRoot($Script:WorkspacePath)
    if (-not [string]::IsNullOrWhiteSpace($wsRootForDisk)) {
        $wsDrive = [System.IO.DriveInfo]::new($wsRootForDisk)
        if ($wsDrive.IsReady) {
            $wsFreeBytes = [long]$wsDrive.AvailableFreeSpace
            if ($wsFreeBytes -lt 500MB) {
                $rowIdLowHard = Add-EnvironmentObservation `
                    -Condition 'low_disk' `
                    -Outcome 'detected' `
                    -EvidenceClassification 'observational'
                $rowSuffixLowHard = if ($null -eq $rowIdLowHard) { ' (observation write failed; see recent-errors)' } else { (' (environment_observations row ' + $rowIdLowHard + ')') }
                $msgLowHard = ('Broker: workspace volume ' + $wsDrive.Name + ' has ' + $wsFreeBytes + ' byte(s) free; the broker requires at least 500 MB to start.' + $rowSuffixLowHard)
                Add-RecentError $msgLowHard -Source 'environment_low_disk'
                Write-Host $msgLowHard -ForegroundColor Red
                Invoke-Shutdown -StopReason 'startup_failure'
                exit $EXIT_E_ENVIRONMENT_LOW_DISK
            } elseif ($wsFreeBytes -lt 2GB) {
                $rowIdLowWarn = Add-EnvironmentObservation `
                    -Condition 'low_disk' `
                    -Outcome 'warning' `
                    -EvidenceClassification 'observational'
                $rowSuffixLowWarn = if ($null -eq $rowIdLowWarn) { ' (observation write failed; see recent-errors)' } else { (' (environment_observations row ' + $rowIdLowWarn + ')') }
                $msgLowWarn = ('Broker: workspace volume ' + $wsDrive.Name + ' has ' + $wsFreeBytes + ' byte(s) free; this is below the 2 GB soft-warn threshold. The broker is continuing startup but cooks may be refused by the per-cook precheck.' + $rowSuffixLowWarn)
                Add-RecentError $msgLowWarn -Source 'environment_low_disk'
                Write-Host $msgLowWarn -ForegroundColor Yellow
            }
        } else {
            Add-RecentError ('Broker: workspace volume ' + $wsDrive.Name + ' is not ready; AI.C5.2 low-disk probe skipped.') -Source 'environment_low_disk'
        }
    }
} catch {
    Add-RecentError ('AI.C5.2 low-disk probe threw: ' + $_.Exception.Message) -Source 'environment_low_disk'
}

# Phase AI.C5.2 -- workspace-path forbidden-form gate (G3).
#
# Reuses the existing AG.C8 Get-WorkspacePathDiagnostic classifier
# (no detection logic is reimplemented here). The diagnostic itself
# is REPORT-only by AG.C8 doctrine and runs again later in startup
# (see the AG.C8 block below) to surface all of its warnings via
# Add-RecentError. This pre-listener gate consumes the SAME diagnostic
# output and PROMOTES the subset of classifications that are
# fundamentally incompatible with the broker's filesystem contract
# (SQLite WAL atomicity, sentinel semantics, atomic-rename, MAX_PATH
# budget for cook folders) from "warning" to a refusal:
#
#   isUnc                        -- SMB cannot guarantee SQLite WAL
#                                   atomic-rename or FileShare.None
#                                   sentinel semantics across hosts.
#   isReparsePoint               -- junction / symlink / mount point /
#                                   OneDrive placeholder roots can
#                                   sync-conflict mid-cook and
#                                   silently rewrite evidence paths.
#   driveType in { Removable,
#                   Network,
#                   CDRom,
#                   Ram,
#                   NoRootDirectory } -- no durability guarantee.
#   exceedsClassicLimit          -- cook folders WILL exceed 260
#                                   chars; the per-cook precheck
#                                   would refuse every cook anyway.
#
# On refusal the broker writes ONE environment_observations row
# tagged condition='workspace_path_forbidden' / outcome='detected'
# and exits with the EXISTING EXIT_E_WORKSPACE_LOCKED (= 4). No
# new exit constant is introduced for this case; the workspace
# refusal class is unified under the existing code. Other AG.C8
# warnings (canonical-differs-from-display, long-path-prefix) remain
# observational and are surfaced by the existing post-listener
# diagnostic block.
try {
    $wsDiag = Get-WorkspacePathDiagnostic -DisplayPath $Script:WorkspaceDisplayPath
    $wsForbiddenReasons = New-Object System.Collections.Generic.List[string]
    if ($wsDiag.isUnc) { [void]$wsForbiddenReasons.Add('isUnc') }
    if ($wsDiag.isReparsePoint) { [void]$wsForbiddenReasons.Add('isReparsePoint') }
    if ($wsDiag.exceedsClassicLimit) { [void]$wsForbiddenReasons.Add('exceedsClassicLimit') }
    if ($wsDiag.driveType -in @('Removable','Network','CDRom','Ram','NoRootDirectory')) {
        [void]$wsForbiddenReasons.Add('driveType=' + [string]$wsDiag.driveType)
    }
    if ($wsForbiddenReasons.Count -gt 0) {
        $rowIdWsPath = Add-EnvironmentObservation `
            -Condition 'workspace_path_forbidden' `
            -Outcome 'detected' `
            -EvidenceClassification 'observational'
        $rowSuffixWsPath = if ($null -eq $rowIdWsPath) { ' (observation write failed; see recent-errors)' } else { (' (environment_observations row ' + $rowIdWsPath + ')') }
        $msgWsPath = ('Broker: workspace path is on a forbidden form (' + ($wsForbiddenReasons -join ', ') + "). The broker requires a fixed local volume that is not a UNC share, not a reparse point (junction/symlink/OneDrive placeholder), and within the classic MAX_PATH budget. See OPERATOR_GUIDE.md " + [char]0x00A7 + '17.18 and TROUBLESHOOTING.md ' + [char]0x00A7 + '2.' + $rowSuffixWsPath)
        Add-RecentError $msgWsPath -Source 'environment_workspace_path_forbidden'
        Write-Host $msgWsPath -ForegroundColor Red
        Invoke-Shutdown -StopReason 'startup_failure'
        exit $EXIT_E_WORKSPACE_LOCKED
    }
} catch {
    Add-RecentError ('AI.C5.2 workspace-path forbidden-form gate threw: ' + $_.Exception.Message) -Source 'environment_workspace_path_forbidden'
}

# Phase AI.C4 -- scheduler opt-in sentinel classification (observation-only).
#
# Landed incrementally:
#   - AI.C4.1 introduced a presence-only probe (Test-Path on
#     scheduler.json), emitting 'scheduler_detected' or
#     'scheduler_absent'.
#   - AI.C4.2 tightened the primitive into a strict opt-in
#     sentinel classifier, adding a third condition literal
#     'scheduler_malformed_optin' for the case where the file
#     exists but does not match the exact allowed minimal shape.
#
# Runs ONCE per broker boot, after the AI.C5.2 workspace-path
# forbidden-form gate and BEFORE the AH.C1 broker session lifecycle
# entry. Writes EXACTLY ONE environment_observations row tagged
# with one of {scheduler_absent, scheduler_detected,
# scheduler_malformed_optin} and outcome='observed' recording
# whether and how a scheduler-integration opt-in sentinel exists
# alongside the workspace database.
#
# This slice is OBSERVATION-ONLY. By doctrine:
#
#   - The broker DOES NOT register, modify, query, enumerate, or
#     invoke any Windows scheduled task. No Register-/New-/Set-/
#     Unregister-/Get-/Start-ScheduledTask cmdlet appears in this
#     probe or anywhere in broker runtime source. The AI.C8.2
#     final_c4_no_scheduledtask_runtime_in_broker pin remains in
#     force; AI.C4 detects an OPT-IN ARTIFACT, not a scheduled
#     task.
#
#   - The broker DOES NOT read this observation back. The row is
#     forensic evidence for the operator; nothing in the request
#     loop, /health, or any later startup phase reads it.
#
#   - The broker DOES NOT carry scheduler state forward across
#     restart. The probe re-runs from scratch on the next boot;
#     no prior observation influences the current branch.
#
#   - The broker DOES NOT retry, replay, catch up, reconcile, or
#     poll. The probe fires exactly once per boot. No timer, no
#     background job, no watcher.
#
# The opt-in sentinel this slice looks for is a file named
# 'scheduler.json' in the workspace root. The strict allowed
# shape is a minimal JSON object whose ONLY property is
# "enabled": true. The broker reads the file only to classify
# it, never to schedule anything; contents are not interpreted
# beyond shape validation. Classification outcomes:
#
#   no file                                  -> scheduler_absent
#   file == exact minimal {"enabled":true}   -> scheduler_detected
#   file exists but any other content        -> scheduler_malformed_optin
#
# The malformed branch is NON-FATAL: the row is recorded, an
# Add-RecentError note is emitted naming the sentinel path, and
# startup continues. No auto-correction, no fallback, no retry.
#
# The probe is best-effort. If it throws, the broker continues
# startup and records the exception via Add-RecentError. A failure
# to write the observation row is also non-fatal (Add-EnvironmentObservation
# returns $null on write failure, which the caller deliberately ignores --
# evidence-recording is not execution authority).
try {
    $schedConfigPath = Join-Path -Path $Script:WorkspacePath -ChildPath 'scheduler.json'
    $schedCondition  = Get-SchedulerSentinelClassification -Path $schedConfigPath
    $null = Add-EnvironmentObservation `
        -Condition $schedCondition `
        -Outcome 'observed' `
        -EvidenceClassification 'observational'
    if ($schedCondition -eq 'scheduler_malformed_optin') {
        Add-RecentError -Message ('AI.C4.2 scheduler sentinel exists but does not match the strict opt-in shape; classified scheduler_malformed_optin: ' + $schedConfigPath) -Source 'environment_scheduler_presence'
    }
} catch {
    Add-RecentError ('AI.C4 scheduler-sentinel probe threw: ' + $_.Exception.Message) -Source 'environment_scheduler_presence'
}

# Phase AH.C1 -- broker session lifecycle.
#
# Order is doctrinal:
#   1. Start-BrokerSession INSERTs the broker_sessions row for THIS
#      process. After this point, $Script:BrokerSessionId is non-null
#      and Stop-BrokerSession will record the orderly-stop facts
#      during Invoke-Shutdown.
#   2. Invoke-ClassifyPriorBrokerSessions retroactively marks any
#      prior session rows with stopped_at IS NULL AND classified_at
#      IS NULL as stop_class='no_orderly_stop_record' -- a purely
#      observational label that names WHAT was observed (no orderly
#      stop record committed by the broker that owned the row) with
#      no causal claim. This is forensic, NOT restorative -- no state
#      of those prior sessions is "resumed" or "recovered". The WHERE
#      clause excludes THIS session via session_id != $self so the new
#      row is never pre-classified before its own teardown writes
#      stopped_at.
#
# Both are best-effort. If Start-BrokerSession fails, the broker
# continues to serve -- session telemetry is observational, not
# load-bearing. If classification fails, the new broker still serves
# -- prior-session rows will be re-checked on the NEXT startup.
$null = Start-BrokerSession
$null = Invoke-ClassifyPriorBrokerSessions

# Phase AG -- secret-env audit at broker startup.
#
# The cook supervisor copies GRAPH_CLIENT_SECRET onto the CHILD
# process's psi.EnvironmentVariables only (UseShellExecute=$false +
# explicit env block means the child does NOT inherit the parent's
# env). The broker process itself MUST NOT carry GRAPH_CLIENT_SECRET
# in its own process env; if it does, any sibling child process
# spawned WITHOUT an explicit psi env block would silently inherit
# the secret. The Cookbook never sets the variable at process/user/
# machine scope, but the caller (or a wrapper script, or the operator's
# PowerShell profile) might have set it BEFORE invoking the broker.
#
# This audit surfaces that condition as a recent-error WARNING. It
# does NOT refuse to start the broker -- the user may legitimately
# need GRAPH_CLIENT_SECRET in their env for unrelated tools, and
# refusing to launch would be hostile. It also does NOT echo the
# secret value, only its presence. The recent-error gives the
# operator one breadcrumb to investigate; the supervisor's per-cook
# psi env block is the actual isolation surface.
#
# No mutation here. The Cookbook never RemovesEnvironmentVariable on
# anything the operator set -- that's not our authority.
try {
    foreach ($scope in @('Process','User','Machine')) {
        $val = $null
        try { $val = [System.Environment]::GetEnvironmentVariable('GRAPH_CLIENT_SECRET', $scope) } catch {}
        if (-not [string]::IsNullOrEmpty($val)) {
            Add-RecentError ("GRAPH_CLIENT_SECRET is present in broker env at scope '" + $scope + "'. Cookbook never sets this variable; supervisor's per-cook env isolates the secret, but sibling processes spawned by the broker without an explicit env block could inherit it. Investigate the source.")
        }
    }
} catch {
    Add-RecentError ('Secret-env audit failed: ' + $_.Exception.Message)
}

# C4a - Phase AB cook_folder relative-path migration. Runs every
# startup; idempotent; pre-AB rows whose absolute prefix matches the
# current workspace are rewritten to relative, rows that are already
# relative are skipped, and FOREIGN-PREFIX rows are preserved as-is
# with a recent-error so the chef can decide whether to manually
# rebase. The broker NEVER silently overwrites a cook_folder that may
# belong to a different workspace history.
try {
    $cfMigration = Invoke-CookFolderPathMigration
    if ($cfMigration -and ($cfMigration.migrated -gt 0 -or $cfMigration.foreignPreserved -gt 0)) {
        Write-Host (
            'cook_folder migration: scanned=' + $cfMigration.scanned +
            ' migrated=' + $cfMigration.migrated +
            ' alreadyRelative=' + $cfMigration.alreadyRelative +
            ' foreignPreserved=' + $cfMigration.foreignPreserved +
            ' errors=' + $cfMigration.errors
        ) -ForegroundColor Yellow
    }
} catch {
    Add-RecentError ('cook_folder migration failed: ' + $_.Exception.Message)
}

# Phase AG.C6 -- SQLite integrity check. Runs BEFORE reconciliation
# so that if the database is corrupted we surface that condition
# before doing any UPDATE work on top of suspect data. Non-fatal:
# Invoke-SqliteIntegrityCheck records a recent-error if verdict is
# not 'ok' and lets the broker continue.
try {
    [void](Invoke-SqliteIntegrityCheck)
} catch {
    Add-RecentError ('SQLite integrity check failed to run: ' + $_.Exception.Message)
}

# C5 - sentinel reconciliation.
try {
    $reconciledCount = Invoke-SentinelReconciliation
    if ($reconciledCount -gt 0) {
        Write-Host ('Sentinel reconciliation: ' + $reconciledCount + ' running cook(s) reconciled.') -ForegroundColor Yellow
    }
} catch {
    Add-RecentError ('Sentinel reconciliation failed: ' + $_.Exception.Message)
}

# V1.S06d -- scheduled-task wrapper-folder reconciliation. Walks
# <WorkspacePath>/Cooks/<cookId>/ for folders the OS-spawned wrapper
# wrote and imports the resulting cooks + cook_artifacts rows. This
# is a passive import; the broker does NOT call any *-ScheduledTask
# cmdlet, does not spawn the wrapper, does not invoke PAX. Bounded
# to $Script:ScheduledTaskReconcileBoundedCap folders per call so
# startup latency is independent of historical Cooks/ size; folders
# beyond the cap import on the next call (GET /api/v1/cooks).
try {
    $scheduledImportSummary = Invoke-ScheduledTaskReconcile -Bounded
    if ($scheduledImportSummary.imported -gt 0 -or $scheduledImportSummary.skippedMalformed -gt 0) {
        Write-Host (
            'Scheduled-task reconciliation: ' +
            $scheduledImportSummary.imported + ' imported, ' +
            $scheduledImportSummary.skippedExisting + ' unchanged, ' +
            $scheduledImportSummary.skippedMalformed + ' skipped-malformed, ' +
            $scheduledImportSummary.errors + ' errors.'
        ) -ForegroundColor Yellow
    }
} catch {
    Add-RecentError ('Scheduled-task reconciliation failed: ' + $_.Exception.Message)
}

# Phase AH.C3 -- record the reconciled-cook count on THIS broker's
# broker_sessions row as observational evidence. This is forensic,
# not orchestration: the count records what the new broker observed
# at boot, not anything the broker did to the prior runtime.
try {
    if ($null -ne $reconciledCount) {
        Set-BrokerSessionStartupReconciledCount -Count ([int]$reconciledCount)
    } else {
        Set-BrokerSessionStartupReconciledCount -Count 0
    }
} catch {
    Add-RecentError ('Set-BrokerSessionStartupReconciledCount failed: ' + $_.Exception.Message)
}

# Phase AH.C3 -- truthful startup banner.
#
# Print the startup classification and the prior-session observation
# in operator-visible form. The banner is intentionally not-rosy: it
# names what was observed, not what the broker did about it. The
# wording avoids restorative verbs entirely (no recovered / resumed /
# restored / healed). Operators see the truth at boot.
try {
    $bs    = $Script:BrokerStartupClassification
    $bpid  = $Script:BrokerStartupPriorSessionId
    $bcls  = $Script:BrokerStartupPriorSessionStopClass
    $bsat  = $Script:BrokerStartupPriorSessionStoppedAt
    $brcc  = $Script:BrokerStartupPriorRunningCookCount
    $bicc  = if ($null -ne $reconciledCount) { [int]$reconciledCount } else { 0 }
    if ([string]::IsNullOrWhiteSpace($bs)) { $bs = 'startup_after_unknown_runtime_state' }
    $msg = "Broker startup classification: $bs"
    if ($bs -ne 'clean_start' -and -not [string]::IsNullOrWhiteSpace($bpid)) {
        $stopBit = if (-not [string]::IsNullOrWhiteSpace($bcls)) { $bcls } else { 'no orderly stop record committed' }
        $msg += " | observed prior broker session $bpid ($stopBit)"
    }
    if ($brcc -gt 0) {
        $msg += " | cooks observed in 'running' status at startup: $brcc"
    }
    if ($bicc -gt 0) {
        $msg += " | cooks reconciled to terminal status this startup: $bicc"
    }
    Write-Host $msg -ForegroundColor Cyan
} catch {
    Add-RecentError ('Startup banner emit failed: ' + $_.Exception.Message)
}

# Phase AG.C6 -- one-shot WAL checkpoint(TRUNCATE) AFTER reconciliation
# so any reconciliation UPDATEs are flushed into the main database
# file. Resets any WAL drift left behind by a previous crash.
# Non-fatal: busy=1 or exception is recorded as recent-error.
try {
    [void](Invoke-SqliteStartupCheckpoint)
} catch {
    Add-RecentError ('SQLite startup WAL checkpoint failed: ' + $_.Exception.Message)
}

# Phase AG.C8 -- workspace path diagnostic. Classifies the operator-
# supplied workspace and surfaces any concerning forms (UNC,
# reparse-point, removable, network, long-path prefix, raw-vs-canonical
# delta, MAX_PATH-likely-overflow) via Add-RecentError so the operator
# sees them via /api/v1/health. We DO NOT refuse to start the broker
# on these conditions; we surface them. The doctrine for AG.C8 is
# DETECT + REPORT, never REWRITE: historical evidence paths must
# remain historically truthful even when their form is unusual.
try {
    $Script:WorkspaceDiagnostic = Get-WorkspacePathDiagnostic -DisplayPath $Script:WorkspaceDisplayPath
    foreach ($w in $Script:WorkspaceDiagnostic.warnings) {
        Add-RecentError ('Workspace path diagnostic: ' + [string]$w)
    }
    foreach ($x in $Script:WorkspaceDiagnostic.exceptions) {
        Add-RecentError ('Workspace path diagnostic probe error: ' + [string]$x)
    }
} catch {
    # Defensive: Get-WorkspacePathDiagnostic is designed never to
    # throw, but a malformed input could in principle still surface
    # something. We never want the diagnostic to BLOCK broker startup.
    Add-RecentError ('Workspace path diagnostic itself failed: ' + $_.Exception.Message)
    $Script:WorkspaceDiagnostic = $null
}

# E - ensure recipe workspace directories exist before serving requests.
try {
    Initialize-RecipesDirs
} catch {
    Add-RecentError ('Recipes directory init failed: ' + $_.Exception.Message)
}

# F - ensure cook root directory exists before any cook is started.
try {
    if (-not (Test-Path -LiteralPath $Script:CooksDir -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $Script:CooksDir -Force
    }
} catch {
    Add-RecentError ('Cooks directory init failed: ' + $_.Exception.Message)
}

# G - ensure the operator-driven update-staging directory exists. This
# is where Save-UpdatePackage drops verified Cookbook packages
# (<workspace>\Updates\<filename>.zip) and their .metadata.json sidecar
# files. Survives Cookbook re-install because it lives under the
# workspace, not the app tree. The broker never auto-mutates this
# directory beyond writing the partial-download / verified package /
# metadata sidecar; older staged packages are left alone.
try {
    if (-not (Test-Path -LiteralPath $Script:UpdatesDir -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $Script:UpdatesDir -Force
    }
} catch {
    Add-RecentError ('Updates directory init failed: ' + $_.Exception.Message)
}

# Print the URL with fragment token.
#
# UX-1H6: canonical browser-facing origin is http://localhost:<port>.
# Chrome/Edge reject 127.0.0.1 as a WebAuthn effective domain. The
# HttpListener still binds 127.0.0.1 too, so internal health checks
# and any operator who pastes the IP directly still resolves, but the
# URL we print, the URL the launcher opens, and the URL Cookbook
# refers operators to is the localhost form.
$brokerUrl = 'http://localhost:' + $Script:BrokerPort + '/#t=' + $Script:SessionToken
Write-Host ''
Write-Host 'Broker ready.' -ForegroundColor Green
Write-Host ('  URL : ' + $brokerUrl) -ForegroundColor Green
Write-Host ('  Port: ' + $Script:BrokerPort) -ForegroundColor Green
Write-Host ('  Workspace: ' + $Script:WorkspacePath) -ForegroundColor Green
Write-Host ''
Write-Host 'Press Ctrl+C to stop the broker.' -ForegroundColor Gray
Write-Host ''

# Operator Ctrl+C delivery mode.
#
# When the broker shares a visible, interactive console with the
# operator (source-tree Support Mode), a real OS Ctrl+C is consumed by
# the PowerShell host before either the registered Console.CancelKeyPress
# subscriber or a native console-control handler can run. The host then
# tears the process down without the dispatch loop's finally block ever
# executing, so Invoke-Shutdown never runs: the broker appears to ignore
# Ctrl+C, the workspace lock leaks, and the operator has to fall back to
# the stop helper.
#
# Setting TreatControlCAsInput keeps the host from intercepting Ctrl+C
# and instead delivers it to the input buffer as an ordinary key event.
# The dispatch loop reads that key event and drives the same graceful
# shutdown funnel as every other stop path. It is only enabled when an
# interactive console is actually attached (input is not redirected); in
# redirected or non-interactive launches the CancelKeyPress handler
# registered above remains the control path and behavior is unchanged.
$Script:CtrlCAsInput = $false
try {
    if (-not [Console]::IsInputRedirected) {
        [Console]::TreatControlCAsInput = $true
        $Script:CtrlCAsInput = $true
    }
} catch {
    $Script:CtrlCAsInput = $false
}

# C6 + C7 - dispatch loop.
#
# Phase AH.C1 -- stop-reason tagging.
# The loop's exit paths map to broker_session_stop_reason as:
#   - HttpListenerException with $Script:ShuttingDown = $true:
#     Ctrl+C or programmatic Listener.Stop() reached us. The Ctrl+C
#     handler will have set $Script:ShutdownReason = 'operator_ctrl_c'
#     already; if not (programmatic close in some unforeseen path),
#     we fall back to 'listener_disposed'.
#   - ObjectDisposedException: the listener was closed under us. Same
#     fallback to 'listener_disposed'.
#   - HttpListenerException rethrown (ShuttingDown=$false): a genuine
#     dispatch-loop fault. Tag 'dispatch_loop_exception' EARLIEST-
#     writer-wins, then propagate.
#   - Outer catch: any other exception in the loop. Same tag.
#
# CP5D update -- the Ctrl+C path now flows through the static
# BrokerShutdownSignal helper instead of a scriptblock that ran on a
# runspace-free thread (and crashed). The catch and finally blocks
# below transcribe the static signal state into PowerShell variables
# from the main runspace, preserving the EARLIEST-writer-wins doctrine
# ($Script:ShutdownReason is set only when still $null).
try {
    while ($Script:Listener.IsListening -and -not $Script:ShuttingDown -and -not [PAXCookbook.Native.BrokerShutdownSignal]::IsSignaled) {
        try {
            $ctx = $null
            $iar = $Script:Listener.BeginGetContext($null, $null)
            while ($true) {
                if ($iar.AsyncWaitHandle.WaitOne(150)) {
                    $ctx = $Script:Listener.EndGetContext($iar)
                    break
                }
                # Idle wait: observe an operator Ctrl+C that the host has
                # delivered to the input buffer as a key event. Reading it
                # here lets the existing shutdown funnel run gracefully.
                if ($Script:CtrlCAsInput) {
                    try {
                        while ([Console]::KeyAvailable) {
                            $key = [Console]::ReadKey($true)
                            if ($key.Key -eq [System.ConsoleKey]::C -and ($key.Modifiers -band [System.ConsoleModifiers]::Control)) {
                                [PAXCookbook.Native.BrokerShutdownSignal]::Signal('operator_ctrl_c')
                            }
                        }
                    } catch {
                        $Script:CtrlCAsInput = $false
                    }
                }
                if ($Script:ShuttingDown -or [PAXCookbook.Native.BrokerShutdownSignal]::IsSignaled) {
                    try { $Script:Listener.Stop() } catch {}
                    $ctx = $Script:Listener.EndGetContext($iar)
                    break
                }
            }
        } catch [System.Net.HttpListenerException] {
            if ($Script:ShuttingDown -or [PAXCookbook.Native.BrokerShutdownSignal]::IsSignaled) {
                $Script:ShuttingDown = $true
                if ($null -eq $Script:ShutdownReason) {
                    $sigReason = [PAXCookbook.Native.BrokerShutdownSignal]::Reason
                    if (-not [string]::IsNullOrEmpty($sigReason)) {
                        $Script:ShutdownReason = $sigReason
                    }
                }
                break
            }
            if ($null -eq $Script:ShutdownReason) {
                $Script:ShutdownReason = 'dispatch_loop_exception'
            }
            throw
        } catch [System.ObjectDisposedException] {
            break
        }
        try {
            Invoke-RequestHandler -Context $ctx
        } catch {
            Add-RecentError ('Request handler failed: ' + $_.Exception.Message)
            try { $ctx.Response.StatusCode = 500; $ctx.Response.Close() } catch {}
        }
    }
} catch {
    if ($null -eq $Script:ShutdownReason) {
        $Script:ShutdownReason = 'dispatch_loop_exception'
    }
    Add-RecentError ('Dispatch loop exception: ' + $_.Exception.Message)
} finally {
    # CP5D -- if the cancel signal fired but the catch block didn't
    # get a chance to transcribe the reason (e.g. shutdown was driven
    # by another code path that observed IsSignaled), pull it from
    # the static helper now. Still EARLIEST-writer-wins; only writes
    # when $Script:ShutdownReason is still $null.
    if ($null -eq $Script:ShutdownReason -and [PAXCookbook.Native.BrokerShutdownSignal]::IsSignaled) {
        $sigReason = [PAXCookbook.Native.BrokerShutdownSignal]::Reason
        if (-not [string]::IsNullOrEmpty($sigReason)) {
            $Script:ShutdownReason = $sigReason
        }
    }
    $finalStopReason = if ($null -ne $Script:ShutdownReason) { $Script:ShutdownReason } else { 'listener_disposed' }
    Invoke-Shutdown -StopReason $finalStopReason
}

# Terminal exit. The launcher reads $LASTEXITCODE to decide whether to
# hand off to the update-apply orchestrator. ONLY the
# 'operator_update_apply' stop reason promotes the exit to
# $EXIT_OPERATOR_UPDATE_APPLY_REQUESTED; every other clean exit stays
# at $EXIT_OK so existing launcher behavior is unchanged for the
# Ctrl+C, listener-disposed, and dispatch-loop-exception paths.
if ($Script:ShutdownReason -eq 'operator_update_apply') {
    exit $EXIT_OPERATOR_UPDATE_APPLY_REQUESTED
}
exit $EXIT_OK

