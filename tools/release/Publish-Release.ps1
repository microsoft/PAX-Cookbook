#requires -Version 7.4

# =====================================================================
# Publish-Release.ps1   Local fail-closed publish gate for PAX Cookbook
# release artifacts.
#
# WHAT THIS SCRIPT DOES
#
#   Reads a <pkg>.release.json sidecar produced by Build-Release.ps1
#   and verifies that the artifact satisfies every PLAN 6.1
#   publish-gate invariant:
#
#     - top-level publishable == true
#     - signing.state         == 'signed'
#     - signing.profile       == 'production'
#     - no placeholder strings remain in paxScript fields
#     - closed-schema top-level allow-list
#
#   When ALL invariants pass the script exits 0 and prints a single
#   APPROVED line. When ANY invariant fails the script exits 1 and
#   prints a single ERROR line naming the failing invariant code.
#
# WHAT THIS SCRIPT DELIBERATELY DOES NOT DO
#
#   - Does not perform any network call. There is no upload, push,
#     or HTTP at all. Distribution is a downstream distributor step.
#   - Does not modify the .release.json file. Read-only.
#   - Does not verify the ZIP's SHA-256 against the sidecar (that is
#     a separate Verify-Release.ps1 step).
#   - Does not sign anything. Signing is performed by Sign-Release.ps1
#     after build, before publish.
#
# PARAMETERS
#
#   -ReleaseJsonPath : absolute path to <pkg>.release.json (required).
#   -Quiet           : suppress the per-check trace; only emit the
#                      final APPROVED or ERROR line.
# =====================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseJsonPath,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'Release.psm1'
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
    Write-Host ('[publish] ERROR module_missing: Release.psm1 not found alongside Publish-Release.ps1: ' + $modulePath) -ForegroundColor Red
    exit 1
}
Import-Module $modulePath -Force -Global -ErrorAction Stop

if (-not $Quiet) {
    Write-Host ('[publish] release.json   : ' + $ReleaseJsonPath)
}

$result = Test-ReleasePublishable -ReleaseJsonPath $ReleaseJsonPath

if (-not $result.ok) {
    Write-Host ('[publish] ERROR ' + $result.reason + ': ' + $result.message) -ForegroundColor Red
    exit 1
}

if (-not $Quiet) {
    Write-Host '[publish] APPROVED' -ForegroundColor Green
} else {
    Write-Host 'APPROVED'
}
exit 0
