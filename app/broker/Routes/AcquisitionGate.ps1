#requires -Version 7.4

# =====================================================================
# Routes\AcquisitionGate.ps1
#
# Cross-route gate that refuses cook-spawning and schedule-installing
# actions while the appliance is in an external-engine acquisition
# state that is not yet 'acquired'. The bundled-engine ("embedded")
# policy always passes the gate; the only blocking state is
# policy = 'external' AND state != 'acquired'.
#
# Late-bound by design. The Engine\Acquisition.psm1 module exports
# Get-PaxAcquisitionState, which already encapsulates VERSION.json
# policy resolution plus install-state.json reading. This file is
# dot-sourced from Start-Broker.ps1 after Acquisition.psm1 is
# imported, so the helper functions defined here can call into the
# module-exported synthesizer without an import-order coupling.
#
# Response contract on block (HTTP 409):
#   {
#     "code"           : "acquisitionRequired",
#     "endpoint"       : "<METHOD /api/v1/...>",
#     "state"          : "<state token from Get-PaxAcquisitionState>",
#     "isLegacyEmbedded": false,
#     "message"        : "PAX engine acquisition is required before this action can run.",
#     "details"        : { ... full ordered state snapshot ... }
#   }
#
# On any internal failure during gate evaluation the helper returns
# 500 internal_error with the diagnostic message. The route caller
# treats both as "request handled, do not proceed" by returning
# from the handler.
# =====================================================================

# Script-scope mirror of Engine\Acquisition.psm1's $Script:AcquisitionStateAcquired.
# AcquisitionGate.ps1 is dot-sourced into the broker's script scope, which cannot
# reach into a module's script scope; without this mirror, any reference to
# $Script:AcquisitionStateAcquired from the gate would raise a strict-mode
# "variable not set" error on the first cook / resume / schedule-put request.
$Script:AcquisitionStateAcquired = 'acquired'

function Test-PaxAcquisitionGate {
    <#
    .SYNOPSIS
        Pure read-only test of the acquisition gate. Returns:
            @{
                blocked  = $true | $false
                status   = 409 | 500          # populated when blocked
                body     = <hashtable>        # populated when blocked
                state    = <state token>      # populated regardless
                policy   = 'embedded'|'external'|$null
            }
    .DESCRIPTION
        Resolves the broker session variables $Script:VersionFile and
        $Script:PaxScriptPath that Test-BundledPaxIntegrity populates
        at startup. Any handler can call this; the response body is
        built by the caller via Invoke-AcquisitionGateOrShortCircuit
        when an HTTP short-circuit is desired.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Endpoint)

    $versionPath  = $Script:VersionFile
    $canonicalPax = $Script:PaxScriptPath

    if ([string]::IsNullOrWhiteSpace($versionPath)) {
        return @{
            blocked = $true
            status  = 500
            body    = @{
                error    = 'acquisition_gate_internal'
                endpoint = $Endpoint
                message  = 'Broker session variable $Script:VersionFile is not populated. Gate cannot evaluate acquisition state.'
            }
            state   = $null
            policy  = $null
        }
    }

    try {
        $state = Get-PaxAcquisitionState -VersionFilePath $versionPath -CanonicalScriptPath $canonicalPax
    } catch {
        return @{
            blocked = $true
            status  = 500
            body    = @{
                error    = 'acquisition_gate_internal'
                endpoint = $Endpoint
                message  = 'Gate failed to synthesize acquisition state: ' + $_.Exception.Message
            }
            state   = $null
            policy  = $null
        }
    }

    $policy     = [string]$state.policy
    $stateToken = [string]$state.state

    if ($policy -eq 'embedded' -or $stateToken -eq $Script:AcquisitionStateAcquired) {
        return @{
            blocked = $false
            state   = $stateToken
            policy  = $policy
        }
    }

    $details = [ordered]@{
        policy                 = $policy
        state                  = $stateToken
        isAcquired             = [bool]$state.isAcquired
        pending                = [bool]$state.pending
        source                 = $state.source
        version                = $state.version
        sha256                 = $state.sha256
        manifestId             = $state.manifestId
        manifestHash           = $state.manifestHash
        manifestVersion        = $state.manifestVersion
        validatedAtUtc         = $state.validatedAtUtc
        activatedAtUtc         = $state.activatedAtUtc
        lastAttemptError       = $state.lastAttemptError
        canonicalScriptPath    = $state.canonicalScriptPath
        canonicalScriptPresent = [bool]$state.canonicalScriptPresent
        installStatePath       = $state.installStatePath
    }

    return @{
        blocked = $true
        status  = 409
        body    = [ordered]@{
            code             = 'acquisitionRequired'
            endpoint         = $Endpoint
            state            = $stateToken
            isLegacyEmbedded = $false
            message          = 'PAX engine acquisition is required before this action can run.'
            details          = $details
        }
        state   = $stateToken
        policy  = $policy
    }
}

function Invoke-AcquisitionGateOrShortCircuit {
    <#
    .SYNOPSIS
        Tests the acquisition gate and, if blocked, writes the JSON
        response and returns $true so the caller knows to return
        without doing further work. Returns $false when the gate is
        open, in which case the caller proceeds normally.
    .NOTES
        Mirrors the call shape of the existing
        Invoke-AuthProfileReAuthOrShortCircuit pattern used by other
        gates so route handlers can adopt this helper without
        restructuring their early-return ladders.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Context,
        [Parameter(Mandatory)][string]$Endpoint
    )

    $verdict = Test-PaxAcquisitionGate -Endpoint $Endpoint
    if (-not $verdict.blocked) { return $false }

    Write-JsonResponse -Context $Context -Status ([int]$verdict.status) -Body $verdict.body
    return $true
}
