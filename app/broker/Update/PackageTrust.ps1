#requires -Version 7.4

# PackageTrust.ps1
#
# Phase AI.C3.1 -- package-trust observation writer and at-staging
# SHA-256 verification helper. Closes the AI.C3 entry gate G1
# (at-staging integrity) and partial G7 (per-boundary fresh
# computation -- only the 'staging' boundary in this slice).
#
# This file is dot-sourced from Start-Broker.ps1, so the $Script:
# scope here is the broker script scope -- the same scope that
# defines Add-RecentError, Get-UtcNowIso, Get-HealthPayload, and
# the existing AI.C2 observation infrastructure. This matches the
# AI.C2 dot-source pattern for Routes\Updates.ps1.
#
# Doctrine (load-bearing; DO NOT paraphrase; mirrors §17.11 /
# §17.12 / §17.14 from OPERATOR_GUIDE.md, restated for the new
# table):
#
#   - A row in package_trust_observations says: "the broker
#     computed SHA-256 over THIS package path at THIS named
#     boundary at THIS UTC instant and the observed digest
#     equalled / differed from / could not be compared against
#     THIS expected digest." Nothing more.
#
#   - A row does NOT say: the package was applied, the package
#     is queued, the package is approved for any future step,
#     the package is trusted in any cryptographic sense, the
#     broker owns any future action against the package, restart
#     will resume anything, or any active state exists.
#
#   - On restart, the broker MUST NOT read this table. The
#     historical rows are forensic evidence for operators; they
#     are not runtime state. The AI.C3.1 smoke enforces this
#     statically (no SELECT / UPDATE / DELETE / ALTER / CREATE
#     INDEX site anywhere in app/broker/** against
#     package_trust_observations outside the schema-bootstrap
#     trigger DDL itself).
#
#   - AI.C3.1 implements ONLY the 'staging' boundary. The two
#     other boundary literals ('pre_apply', 'pre_run') are
#     accepted by the table's CHECK constraint but no AI.C3.1
#     code path writes them.
#
# See OPERATOR_GUIDE.md §17.15 and TROUBLESHOOTING.md §13w.

Set-StrictMode -Version Latest

# AI.C3.1 -- process-lifetime monotonic counter of at-staging
# package-trust verification ATTEMPTS (every call to
# Invoke-PackageStagingVerification, before the hash is
# computed). Paired denominator for the failure counter below.
# Internal to this broker process; resets to 0 on every broker
# restart; never persisted, never written to
# package_trust_observations, never sent off the appliance,
# never derived from a read of the table. Informational only --
# MUST NOT influence /api/v1/health status derivation. See
# OPERATOR_GUIDE.md §17.15.
$Script:PackageTrustStagingVerificationAttemptCount = 0

# AI.C3.1 -- process-lifetime monotonic counter of at-staging
# package-trust verification FAILURES (every mismatch outcome
# OR every exception during hash compute / observation write).
# Same runtime-only invariants as the attempt counter above.
$Script:PackageTrustStagingVerificationFailureCount = 0

function Get-PackageTrustStagingVerificationAttemptCount {
    # Pure read of the in-process counter. No DB touch, no clock
    # dependency, no aggregation. Returns an integer.
    return [int]$Script:PackageTrustStagingVerificationAttemptCount
}

function Get-PackageTrustStagingVerificationFailureCount {
    # Pure read of the in-process counter. No DB touch, no clock
    # dependency, no aggregation. Returns an integer.
    return [int]$Script:PackageTrustStagingVerificationFailureCount
}

# AI.C3.1 -- package_trust_observations writer.
#
# Append-only historical evidence writer. Inserts ONE row into
# package_trust_observations per observed at-boundary verification
# event. Function MUST NOT update / delete / read any other
# observation row, infer continuity from prior rows, or imply
# that the observed package is queued, pending, deferred,
# scheduled, retried, replayed, resumed, or owned by the broker
# for future execution.
#
# Returns the integer rowid the database assigned on success, or
# $null on failure (with the failure surfaced via Add-RecentError).
# The caller treats $null as "the broker observed the boundary
# event but could not record evidence" -- truthful messiness over
# fabricated cleanliness. The writer never refuses the caller's
# observation event on observation-write failure; that would
# conflate evidence-recording with execution authority. The broker
# has neither.
function Add-PackageTrustObservation {
    # ExpectedSha256 and ObservedSha256 carry the lower-hex digest
    # of the expected / observed package bytes when the relevant
    # value is known, and the empty string when it is genuinely
    # unknown -- e.g. a staged package whose metadata sidecar
    # is missing (the at-launch hashUnknown branch in
    # Invoke-PackageLaunchVerification), or a hash compute that
    # threw. The outcome column ('match' / 'mismatch' / 'unknown')
    # is the discriminator; empty hex is the established
    # vocabulary for "no value". [AllowEmptyString()] is required
    # because a [Parameter(Mandatory=$true)][string] would reject
    # the empty string at binding time and prevent the truthful
    # observation row from being written.
    param(
        [Parameter(Mandatory=$true)][string]$Boundary,
        [Parameter(Mandatory=$true)][string]$PackagePath,
        [Parameter(Mandatory=$true)][AllowEmptyString()][string]$ExpectedSha256,
        [Parameter(Mandatory=$true)][AllowEmptyString()][string]$ObservedSha256,
        [Parameter(Mandatory=$true)][string]$Outcome,
        [Parameter(Mandatory=$true)][string]$EvidenceClassification
    )

    if ($null -eq $Script:SqliteConn) {
        Add-RecentError -Message 'Add-PackageTrustObservation skipped: SQLite connection is null' -Source 'package_trust_observation'
        return $null
    }

    $observedAt = Get-UtcNowIso

    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = @'
INSERT INTO package_trust_observations (
    observed_at_utc, boundary, package_path,
    expected_sha256, observed_sha256, outcome, evidence_classification
) VALUES (
    $obs, $bd, $pp,
    $exp, $obsh, $oc, $ec
);
'@
        $p = $cmd.CreateParameter(); $p.ParameterName = '$obs';  $p.Value = [string]$observedAt;             [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$bd';   $p.Value = [string]$Boundary;               [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$pp';   $p.Value = [string]$PackagePath;            [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$exp';  $p.Value = [string]$ExpectedSha256;         [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$obsh'; $p.Value = [string]$ObservedSha256;         [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$oc';   $p.Value = [string]$Outcome;                [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$ec';   $p.Value = [string]$EvidenceClassification; [void]$cmd.Parameters.Add($p)
        try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
        # The Microsoft.Data.Sqlite SqliteConnection type does NOT
        # expose a CLR `LastInsertRowId` property; that surface
        # belongs to the third-party System.Data.SQLite wrapper.
        # Under Set-StrictMode -Version Latest, reading a missing
        # property throws PropertyNotFoundStrict, which the outer
        # catch would convert into a recentError -- making the
        # broker report `/health.status = "degraded"` despite the
        # row having been written successfully and silently
        # suppressing the observation_id the caller embeds in
        # response bodies and audit records. The connection-scoped
        # SQLite SQL function `last_insert_rowid()` returns the
        # rowid the engine assigned on the most recent INSERT on
        # THIS connection; querying it through Invoke-SqliteScalar
        # is the strict-mode-safe equivalent.
        $rowId = [int64](Invoke-SqliteScalar -Sql 'SELECT last_insert_rowid();')
        return $rowId
    } catch {
        Add-RecentError -Message ('Add-PackageTrustObservation INSERT failed: ' + $_.Exception.Message) -Source 'package_trust_observation'
        return $null
    }
}

# AI.C3.1 -- at-staging SHA-256 verification step.
#
# Takes the on-disk path of a freshly-staged package and the
# expected SHA-256 hex digest the caller is asserting against
# that path. Computes the actual SHA-256 over the file's bytes
# via Get-FileHash, compares, and writes ONE observation row
# describing the outcome. Returns a result object so the caller
# can react if it chooses; AI.C3.1 callers do NOT alter the
# outer code path based on the return -- the observation row
# is the evidence, not a gate.
#
# AI.C3.1 scope: this function is the building block. It is NOT
# wired to any HTTP route in this slice, and it is NOT wired to
# Save-UpdatePackage. AI.C3.2 (G2/G3/G4) will integrate it into
# the existing staging flow.
#
# Attempt counter increments BEFORE the hash compute, in its own
# try/catch, so it cannot be skipped by any later exception.
# Failure counter increments on any non-'match' outcome OR any
# exception in this function, in its own try/catch, so it cannot
# be skipped either.
function Invoke-PackageStagingVerification {
    param(
        [Parameter(Mandatory=$true)][string]$PackagePath,
        [Parameter(Mandatory=$true)][string]$ExpectedSha256
    )

    # FIRST executable statement -- attempt counter increment in
    # its own try/catch, before any file I/O, hash compute, null
    # check, or observation write. Mirrors AI.C2.10 writer pattern.
    try { $Script:PackageTrustStagingVerificationAttemptCount = [int]$Script:PackageTrustStagingVerificationAttemptCount + 1 } catch { }

    $boundary = 'staging'
    $ec       = 'observational'
    $observed = ''
    $outcome  = 'unknown'

    try {
        if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
            $outcome  = 'unknown'
            $observed = ''
        } else {
            $h = Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256
            $observed = [string]$h.Hash
            if ([string]::Equals($observed, [string]$ExpectedSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                $outcome = 'match'
            } else {
                $outcome = 'mismatch'
            }
        }
    } catch {
        $outcome  = 'unknown'
        $observed = ''
        Add-RecentError -Message ('Invoke-PackageStagingVerification hash compute failed: ' + $_.Exception.Message) -Source 'package_trust_observation'
    }

    if ($outcome -ne 'match') {
        try { $Script:PackageTrustStagingVerificationFailureCount = [int]$Script:PackageTrustStagingVerificationFailureCount + 1 } catch { }
    }

    $rowId = Add-PackageTrustObservation `
        -Boundary               $boundary `
        -PackagePath            $PackagePath `
        -ExpectedSha256         ([string]$ExpectedSha256).ToLowerInvariant() `
        -ObservedSha256         ([string]$observed).ToLowerInvariant() `
        -Outcome                $outcome `
        -EvidenceClassification $ec

    return [pscustomobject]@{
        boundary       = $boundary
        outcome        = $outcome
        expectedSha256 = ([string]$ExpectedSha256).ToLowerInvariant()
        observedSha256 = ([string]$observed).ToLowerInvariant()
        observationId  = $rowId
    }
}

# AI.C3.2 -- process-lifetime monotonic counter of at-apply
# package-trust verification ATTEMPTS (every call to
# Invoke-PackageApplyVerification, before the hash is
# computed). Paired denominator for the failure counter below.
# Same runtime-only invariants as the AI.C3.1 staging-attempt
# counter above. See OPERATOR_GUIDE.md §17.16.
$Script:PackageTrustApplyVerificationAttemptCount = 0

# AI.C3.2 -- process-lifetime monotonic counter of at-apply
# package-trust verification FAILURES (every mismatch outcome
# OR every exception during hash compute / observation write).
# Same runtime-only invariants as the AI.C3.1 staging-failure
# counter above.
$Script:PackageTrustApplyVerificationFailureCount = 0

# AI.C3.2 -- process-lifetime monotonic counter of at-launch
# package-trust verification ATTEMPTS. Increments by exactly 1
# per broker boot when Invoke-PackageLaunchVerification runs.
# Same runtime-only invariants as the other AI.C3 counters.
# Because the at-launch check runs once per process, the steady-
# state value is 1 (or 0 if the broker failed earlier in
# startup).
$Script:PackageTrustLaunchVerificationAttemptCount = 0

# AI.C3.2 -- process-lifetime monotonic counter of at-launch
# package-trust verification FAILURES. Increments by exactly 1
# if the at-launch evaluation found any staged package whose
# trust state demands refusal (hashMismatch / signatureInvalid).
# Because the broker exits immediately on failure, the steady-
# state value on a serving broker's /health is always 0. The
# counter exists so a post-mortem direct-SQLite or log inspection
# can confirm that the check fired before the broker exited.
$Script:PackageTrustLaunchVerificationFailureCount = 0

function Get-PackageTrustApplyVerificationAttemptCount {
    return [int]$Script:PackageTrustApplyVerificationAttemptCount
}

function Get-PackageTrustApplyVerificationFailureCount {
    return [int]$Script:PackageTrustApplyVerificationFailureCount
}

function Get-PackageTrustLaunchVerificationAttemptCount {
    return [int]$Script:PackageTrustLaunchVerificationAttemptCount
}

function Get-PackageTrustLaunchVerificationFailureCount {
    return [int]$Script:PackageTrustLaunchVerificationFailureCount
}

# AI.C3.2 -- at-apply SHA-256 re-verification step.
#
# Mirrors Invoke-PackageStagingVerification exactly except the
# boundary literal is 'pre_apply' instead of 'staging' and the
# attempt/failure counters target the at-apply pair. The expected
# hash is supplied by the caller (the apply route reads it from
# the staged-package sidecar metadata or the manifest snapshot).
# The on-disk bytes at $PackagePath are re-hashed from scratch
# every call -- the broker NEVER consults a prior staging-boundary
# observation row's observed_sha256 as the answer for the pre_apply
# row. Each boundary's evidence is independent. This is the
# closure of entry-gate G7 for the pre_apply boundary.
function Invoke-PackageApplyVerification {
    param(
        [Parameter(Mandatory=$true)][string]$PackagePath,
        [Parameter(Mandatory=$true)][string]$ExpectedSha256
    )

    try { $Script:PackageTrustApplyVerificationAttemptCount = [int]$Script:PackageTrustApplyVerificationAttemptCount + 1 } catch { }

    $boundary = 'pre_apply'
    $ec       = 'observational'
    $observed = ''
    $outcome  = 'unknown'

    try {
        if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
            $outcome  = 'unknown'
            $observed = ''
        } else {
            $h = Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256
            $observed = [string]$h.Hash
            if ([string]::Equals($observed, [string]$ExpectedSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                $outcome = 'match'
            } else {
                $outcome = 'mismatch'
            }
        }
    } catch {
        $outcome  = 'unknown'
        $observed = ''
        Add-RecentError -Message ('Invoke-PackageApplyVerification hash compute failed: ' + $_.Exception.Message) -Source 'package_trust_observation'
    }

    if ($outcome -ne 'match') {
        try { $Script:PackageTrustApplyVerificationFailureCount = [int]$Script:PackageTrustApplyVerificationFailureCount + 1 } catch { }
    }

    $rowId = Add-PackageTrustObservation `
        -Boundary               $boundary `
        -PackagePath            $PackagePath `
        -ExpectedSha256         ([string]$ExpectedSha256).ToLowerInvariant() `
        -ObservedSha256         ([string]$observed).ToLowerInvariant() `
        -Outcome                $outcome `
        -EvidenceClassification $ec

    return [pscustomobject]@{
        boundary       = $boundary
        outcome        = $outcome
        expectedSha256 = ([string]$ExpectedSha256).ToLowerInvariant()
        observedSha256 = ([string]$observed).ToLowerInvariant()
        observationId  = $rowId
    }
}

# AI.C3.2 -- at-launch package-trust evaluation.
#
# Runs ONCE per broker boot, after Apply-M1Schema and after the
# AI.C2.7/AI.C2.9 trigger-integrity gate, and BEFORE the HTTP
# listener binds. Iterates every currently-staged package under
# the workspace Updates\ folder (via Get-StagedPackageInventory).
# For each, the broker calls Get-PackageTrustState from
# Update\Trust.psm1 -- the SAME read-only trust evaluator the
# /api/v1/updates/state surface uses -- which re-derives trust
# from the SYSTEM trust store on every call. The broker MUST NOT
# consult any prior pre_run row's outcome, any $Script: variable
# carrying a remembered trust verdict, or any persisted "we
# trusted this once" flag. There is no carry-forward across
# restart; trust is re-evaluated from first principles every boot.
#
# One row per staged package is written to
# package_trust_observations with boundary='pre_run'. Outcome
# mapping from Get-PackageTrustState.overallStatus:
#
#   hashMismatch, signatureInvalid                   -> 'mismatch'
#   missing, hashUnknown                             -> 'unknown'
#   verified, unsigned, signaturePresentNotVerified,
#   signerUnknown                                    -> 'match'
#
# Refusal fires only on 'mismatch' (hash drift or cryptographic
# failure against the system trust store). 'unknown' is
# observational -- the broker records that it could not establish
# ground truth for that package but does NOT refuse to start; the
# AI.C2.7 model is the doctrine here: missing positive evidence
# is not a refusal trigger.
#
# Returns a structured verdict the caller (broker startup) uses
# to decide whether to exit with EXIT_E_PACKAGE_TRUST_INTEGRITY.
# Attempt counter increments as the FIRST statement, in its own
# try/catch, so it cannot be skipped by a downstream exception.
# Failure counter increments at most ONCE per call, in its own
# try/catch, if the verdict.refused is $true.
function Invoke-PackageLaunchVerification {
    param(
        [Parameter(Mandatory=$true)][string]$WorkspacePath
    )

    try { $Script:PackageTrustLaunchVerificationAttemptCount = [int]$Script:PackageTrustLaunchVerificationAttemptCount + 1 } catch { }

    $verdict = [pscustomobject]@{
        refused          = $false
        evaluatedCount   = 0
        mismatchCount    = 0
        unknownCount     = 0
        matchCount       = 0
        refusedPackages  = @()
        observationIds   = @()
    }

    $inventory = @()
    try {
        $invCmd = Get-Command -Name 'Get-StagedPackageInventory' -ErrorAction SilentlyContinue
        if ($null -ne $invCmd) {
            # Get-StagedPackageInventory emits its inventory array with
            # the `,$items` non-unrolling idiom, so the call yields a
            # single output unit whose value IS the inventory. Wrapping
            # the call in `@(...)` would double-wrap -- the outer @()
            # treats the single emitted Object[] as one item and
            # produces a 1-element array whose sole member IS the inner
            # inventory. The foreach below would then iterate once with
            # $pkg bound to that Object[], throwing PropertyNotFoundStrict
            # on `$pkg.path` under Set-StrictMode -Version Latest. Bare
            # assignment receives the inventory directly; in the
            # empty-staging case the assignment yields an empty Object[]
            # (the comma-wrap idiom preserves the empty array as a
            # single non-null emit), so the foreach is a true no-op.
            $rawInventory = & $invCmd -WorkspacePath $WorkspacePath
            if ($null -ne $rawInventory) {
                $inventory = $rawInventory
            }
        }
    } catch {
        Add-RecentError -Message ('Invoke-PackageLaunchVerification could not enumerate staged packages: ' + $_.Exception.Message) -Source 'package_trust_observation'
        $inventory = @()
    }

    $trustCmd = Get-Command -Name 'Get-PackageTrustState' -ErrorAction SilentlyContinue

    foreach ($pkg in $inventory) {
        # Defensive guard: skip any iteration element that does not
        # carry a `path` member. The producer contract guarantees a
        # PSCustomObject per item, but Set-StrictMode -Version Latest
        # would turn any future shape-drift here into a startup crash
        # rather than a recorded observation. Truthful messiness over
        # fabricated cleanliness.
        if ($null -eq $pkg) { continue }
        if (-not ($pkg -is [System.Management.Automation.PSObject])) { continue }
        if (-not $pkg.PSObject.Properties['path']) { continue }
        $pkgPath = [string]$pkg.path
        $expected = ''
        $observed = ''
        $outcome  = 'unknown'

        if ($null -ne $trustCmd) {
            try {
                $state = & $trustCmd -PackagePath $pkgPath -WorkspacePath $WorkspacePath
                if ($null -ne $state.expectedSha256) { $expected = [string]$state.expectedSha256 }
                if ($null -ne $state.actualSha256)   { $observed = [string]$state.actualSha256 }
                switch ([string]$state.overallStatus) {
                    'hashMismatch'                  { $outcome = 'mismatch' }
                    'signatureInvalid'              { $outcome = 'mismatch' }
                    'missing'                       { $outcome = 'unknown' }
                    'hashUnknown'                   { $outcome = 'unknown' }
                    'verified'                      { $outcome = 'match' }
                    'unsigned'                      { $outcome = 'match' }
                    'signaturePresentNotVerified'   { $outcome = 'match' }
                    'signerUnknown'                 { $outcome = 'match' }
                    default                         { $outcome = 'unknown' }
                }
            } catch {
                $outcome = 'unknown'
                Add-RecentError -Message ('Invoke-PackageLaunchVerification Get-PackageTrustState threw for ' + $pkgPath + ': ' + $_.Exception.Message) -Source 'package_trust_observation'
            }
        }

        $rowId = Add-PackageTrustObservation `
            -Boundary               'pre_run' `
            -PackagePath            $pkgPath `
            -ExpectedSha256         ([string]$expected).ToLowerInvariant() `
            -ObservedSha256         ([string]$observed).ToLowerInvariant() `
            -Outcome                $outcome `
            -EvidenceClassification 'observational'

        $verdict.evaluatedCount = [int]$verdict.evaluatedCount + 1
        $verdict.observationIds += $rowId
        switch ($outcome) {
            'match'    { $verdict.matchCount    = [int]$verdict.matchCount    + 1 }
            'unknown'  { $verdict.unknownCount  = [int]$verdict.unknownCount  + 1 }
            'mismatch' {
                $verdict.mismatchCount   = [int]$verdict.mismatchCount + 1
                $verdict.refusedPackages += $pkgPath
            }
        }
    }

    if ($verdict.mismatchCount -gt 0) {
        $verdict.refused = $true
        try { $Script:PackageTrustLaunchVerificationFailureCount = [int]$Script:PackageTrustLaunchVerificationFailureCount + 1 } catch { }
    }

    return $verdict
}
