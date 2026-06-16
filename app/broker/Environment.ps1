#requires -Version 7.4

# Environment.ps1
#
# Phase AI.C5.1 -- environment-observation writer.
#
# Closes the AI.C5 entry gate G1 (ConstrainedLanguage detection).
# Subsequent AI.C5 sub-slices reuse the same writer for the
# 'low_disk' (G2) and 'workspace_path_forbidden' (G3) conditions.
#
# This file is dot-sourced from Start-Broker.ps1, so the $Script:
# scope here is the broker script scope -- the same scope that
# defines Add-RecentError, Get-UtcNowIso, and the existing AI.C2 /
# AI.C3 observation infrastructure. This matches the AI.C3.1
# dot-source pattern for Update\PackageTrust.ps1.
#
# Doctrine (load-bearing; DO NOT paraphrase; mirrors §17.11 /
# §17.15 from OPERATOR_GUIDE.md, restated for the new table):
#
#   - A row in environment_observations says: "the broker
#     observed THIS named environmental condition with THIS
#     outcome at THIS UTC instant." Nothing more.
#
#   - A row does NOT say: the broker repaired the condition, the
#     broker degraded around the condition, the broker queued any
#     future action, the broker carries any state forward across
#     restart, or the broker owns any future remediation. The
#     broker observes; enterprise IT remediates.
#
#   - On restart, the broker MUST NOT read this table. The
#     historical rows are forensic evidence for operators; they
#     are not runtime state. The AI.C5.1 smoke enforces this
#     statically (no SELECT / UPDATE / DELETE / ALTER / CREATE
#     INDEX site anywhere in app/broker/** against
#     environment_observations outside the schema-bootstrap
#     trigger DDL itself).
#
#   - AI.C5.1 emits ONLY the 'constrained_language' condition with
#     the 'detected' outcome. The other condition literals
#     ('low_disk', 'workspace_path_forbidden',
#     'scheduler_detected', 'scheduler_absent',
#     'scheduler_malformed_optin', 'scheduler_invocation') and
#     the 'warning' / 'observed' outcomes are accepted by the
#     CHECK constraint but no AI.C5.1 code path writes them. The
#     'low_disk' and 'workspace_path_forbidden' literals are
#     written by AI.C5.2; the 'scheduler_detected' /
#     'scheduler_absent' / 'scheduler_malformed_optin' triple
#     (all with the 'observed' outcome) is written by AI.C4 (the
#     absent/detected pair landed in AI.C4.1; 'scheduler_malformed_optin'
#     landed in AI.C4.2 when the primitive was tightened from
#     presence detection to strict opt-in sentinel classification);
#     the 'scheduler_invocation' literal (also outcome='observed')
#     is written by AI.C9.1 in the cook-trigger HTTP handler when
#     the request carries header X-PAX-Origin: scheduler, tagging
#     the request as originating from the bundled scheduler
#     launcher without branching the cook execution path itself.
#
# See OPERATOR_GUIDE.md §17.18 and TROUBLESHOOTING.md §13z.

Set-StrictMode -Version Latest

# AI.C5.1 -- environment_observations writer.
#
# Append-only historical evidence writer. Inserts ONE row into
# environment_observations per observed environmental detection
# event at broker startup. Function MUST NOT update / delete /
# read any other observation row, infer continuity from prior
# rows, or imply that the broker carries any state forward.
#
# Returns the integer rowid the database assigned on success, or
# $null on failure (with the failure surfaced via Add-RecentError).
# The caller treats $null as "the broker observed the condition
# but could not record evidence" -- truthful messiness over
# fabricated cleanliness. The writer never refuses the caller's
# observation event on observation-write failure; that would
# conflate evidence-recording with execution authority. The
# broker has neither.
function Add-EnvironmentObservation {
    param(
        [Parameter(Mandatory=$true)][string]$Condition,
        [Parameter(Mandatory=$true)][string]$Outcome,
        [Parameter(Mandatory=$true)][string]$EvidenceClassification
    )

    if ($null -eq $Script:SqliteConn) {
        Add-RecentError -Message 'Add-EnvironmentObservation skipped: SQLite connection is null' -Source 'environment_observation'
        return $null
    }

    $observedAt = Get-UtcNowIso

    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = @'
INSERT INTO environment_observations (
    observed_at_utc, condition, outcome, evidence_classification
) VALUES (
    $obs, $cond, $oc, $ec
);
'@
        $p = $cmd.CreateParameter(); $p.ParameterName = '$obs';  $p.Value = [string]$observedAt;             [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$cond'; $p.Value = [string]$Condition;              [void]$cmd.Parameters.Add($p)
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
        # row having been written successfully. The connection-
        # scoped SQLite SQL function `last_insert_rowid()` returns
        # the rowid the engine assigned on the most recent INSERT
        # on THIS connection; querying it through Invoke-SqliteScalar
        # is the strict-mode-safe equivalent.
        $rowId = [int64](Invoke-SqliteScalar -Sql 'SELECT last_insert_rowid();')
        return $rowId
    } catch {
        Add-RecentError -Message ('Add-EnvironmentObservation INSERT failed: ' + $_.Exception.Message) -Source 'environment_observation'
        return $null
    }
}

# AI.C4.2 + AI.C9.1 -- scheduler opt-in sentinel classifier.
#
# Pure classification helper. Returns exactly one of the three
# canonical AI.C4 condition literals based on the contents of the
# file at $Path:
#
#   - 'scheduler_absent'           file does not exist
#   - 'scheduler_detected'         file matches the strict opt-in
#                                  shape: a JSON object with
#                                  EXACTLY three NoteProperties --
#                                  'enabled' (boolean true),
#                                  'recipe_id' (string, 26-char
#                                  Crockford ULID, alphabet
#                                  0-9 A-Z minus I,L,O,U), and
#                                  'daily_time' (string, 24-hour
#                                  'HH:mm' -- '00:00'..'23:59')
#   - 'scheduler_malformed_optin'  file exists but does not match
#                                  the strict allowed shape
#                                  (invalid JSON, empty/whitespace,
#                                  wrong shape, missing or extra
#                                  property, non-boolean enabled,
#                                  enabled=false, non-string or
#                                  malformed recipe_id, non-string
#                                  or malformed daily_time, etc.)
#
# AI.C4.2 introduced the strict-shape classifier with just the
# {"enabled": true} property. AI.C9.1 added 'recipe_id'. M3.1
# adds 'daily_time' so the operator-facing configuration flow
# (launcher/Set-ScheduledCook.ps1) can drive both the broker's
# acceptance decision AND the Windows scheduled task fire time
# from one sentinel file. The classifier still returns the same
# three literals; no fourth classification. Anything that is not
# exactly the 3-property shape with valid ULID and valid HH:mm
# falls into 'scheduler_malformed_optin'.
#
# This helper has NO side effects: no logging, no observation
# write, no scheduling, no task registration, no state mutation.
# It does not call any *-ScheduledTask cmdlet. It is the only
# function in broker runtime that reads scheduler.json, and it
# reads it only to classify it. recipe_id is validated for shape
# only; this helper does NOT verify the ULID resolves to an
# existing recipe row (that is a runtime concern of the cook
# trigger route, identical between manual and scheduled callers).
function Get-SchedulerSentinelClassification {
    param([Parameter(Mandatory=$true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return 'scheduler_absent'
    }

    try {
        $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        if ($null -eq $raw -or $raw.Trim().Length -eq 0) {
            return 'scheduler_malformed_optin'
        }
        $obj = $raw | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $obj) {
            return 'scheduler_malformed_optin'
        }
        $props = @($obj.PSObject.Properties | Where-Object { $_.MemberType -eq 'NoteProperty' })
        if ($props.Count -ne 3) {
            return 'scheduler_malformed_optin'
        }
        $names = @($props | ForEach-Object { $_.Name } | Sort-Object)
        if ($names[0] -ne 'daily_time' -or $names[1] -ne 'enabled' -or $names[2] -ne 'recipe_id') {
            return 'scheduler_malformed_optin'
        }
        $enabledVal = ($props | Where-Object { $_.Name -eq 'enabled' }).Value
        if ($enabledVal -isnot [bool]) {
            return 'scheduler_malformed_optin'
        }
        if ($enabledVal -ne $true) {
            return 'scheduler_malformed_optin'
        }
        $recipeVal = ($props | Where-Object { $_.Name -eq 'recipe_id' }).Value
        if ($recipeVal -isnot [string]) {
            return 'scheduler_malformed_optin'
        }
        # Crockford ULID: 26 chars, alphabet 0-9 A-Z minus I,L,O,U.
        # Matches the route regex used by the broker's cook trigger.
        if ($recipeVal -notmatch '^[0-9A-HJKMNP-TV-Z]{26}$') {
            return 'scheduler_malformed_optin'
        }
        $dailyVal = ($props | Where-Object { $_.Name -eq 'daily_time' }).Value
        if ($dailyVal -isnot [string]) {
            return 'scheduler_malformed_optin'
        }
        # 24-hour HH:mm. Hours 00-23, minutes 00-59. No seconds,
        # no AM/PM, no timezone -- the scheduled task runs in the
        # interactive user's local time on the registering machine.
        if ($dailyVal -notmatch '^([01][0-9]|2[0-3]):[0-5][0-9]$') {
            return 'scheduler_malformed_optin'
        }
        return 'scheduler_detected'
    } catch {
        return 'scheduler_malformed_optin'
    }
}
