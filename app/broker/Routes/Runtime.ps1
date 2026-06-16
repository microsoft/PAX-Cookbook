#requires -Version 7.4

# Runtime.ps1 — HTTP routes for read-only runtime metadata.
#
#   GET /api/v1/runtime/version              -> 200 (see below)
#
# /api/v1/runtime/version returns:
#       cookbookVersion, releaseChannel,
#       bundledPax:      { version, sha256, relativePath, integrity },
#       manifest:        { channel, aligned,
#                          latestCookbookVersion, packageUrlConfigured,
#                          relativePath },
#       host:            { machineName, psVersion, osPlatform, osVersion },
#       paths:           { appRoot, paxScript, versionFile, manifestFile,
#                          workspace, recipes, cooks, database, templates },
#       runtime:         { brokerProcessId, brokerPort, startedAtUtc,
#                          transport, bindAddress },
#       updateReadiness: { updaterAvailable, latestKnownCookbookVersion,
#                          upToDate, checkPerformedAt, lastCheckSource }
#
# These routes are read-only. They return authoritative startup-loaded state
# only. They do NOT:
#   - read VERSION.json or manifest.json per-request
#   - recompute SHA-256 per-request
#   - fetch any remote manifest
#   - trigger any update check
#   - mutate any broker state
#   - access SQLite or the filesystem
#
# All values are pulled from $Script:* variables populated at startup by
# Test-BundledPaxIntegrity and Test-ManifestAlignment in Start-Broker.ps1.
# If the broker is serving requests, $Script:PaxScriptSha256,
# $Script:PaxScriptVersion, $Script:CookbookVersion, and
# $Script:ReleaseChannel are all guaranteed populated (the broker exits
# with EXIT_E_PAX_SCRIPT_INTEGRITY otherwise). $Script:ManifestAligned
# and $Script:ManifestChannel are best-effort and reflect on-disk drift.
#
# Settings page (Phase L) is the primary consumer of the extended body.
# The page renders Runtime identity + Bundled PAX + Integrity + Paths +
# Runtime assumptions + Update readiness + Local diagnostics, all from
# this single endpoint. The page never writes anything back.
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - $Script:CookbookVersion    (string; VERSION.json.cookbook.version)
#   - $Script:ReleaseChannel     (string; VERSION.json.channel)
#   - $Script:PaxScriptVersion   (string; VERSION.json.paxScript.version)
#   - $Script:PaxScriptSha256    (string; VERSION.json.paxScript.sha256, upper-case)
#   - $Script:PaxScriptPath      (string; bundled PAX absolute path)
#   - $Script:AppRoot            (string; install-tree app/ root)
#   - $Script:VersionFile        (string; VERSION.json absolute path)
#   - $Script:ManifestFile       (string; manifest.json absolute path)
#   - $Script:WorkspacePath      (string; workspace root)
#   - $Script:RecipesDir         (string)
#   - $Script:CooksDir           (string)
#   - $Script:DatabaseFile       (string)
#   - $Script:TemplatesDir       (string)
#   - $Script:ManifestAligned    (bool;   manifest.json matches VERSION.json)
#   - $Script:ManifestChannel    (string; manifest.json.channel or $null)
#   - $Script:ManifestLatestCookbookVersion (string or $null)
#   - $Script:ManifestPackageUrlConfigured  (bool)
#   - $Script:UpdateManifestUrl  (string or $null; VERSION.json.updateManifestUrl)
#   - Test-UpdateManifestUrl     (Update\Manifest.psm1; syntax-only check)
#   - $Script:HostMachineName, $Script:HostPsVersion, $Script:HostOsPlatform, $Script:HostOsVersion
#   - $Script:BrokerPort         (int)
#   - $Script:StartedAt          (DateTime; UTC)
#   - Write-JsonResponse         (helper from broker)

function Get-PaxScriptRelativePath {
    # Compute the bundled-PAX path relative to $Script:AppRoot using
    # forward slashes for stable cross-document semantics (VERSION.json
    # uses forward slashes too). Pure path arithmetic; no filesystem
    # access.
    $appRootNormalized  = $Script:AppRoot.TrimEnd('\','/')
    $scriptNormalized   = $Script:PaxScriptPath
    if ($scriptNormalized.StartsWith($appRootNormalized, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rel = $scriptNormalized.Substring($appRootNormalized.Length).TrimStart('\','/')
        return $rel.Replace('\', '/')
    }
    return $scriptNormalized.Replace('\', '/')
}

function Get-ManifestRelativePath {
    # Same relative-to-AppRoot semantics for manifest.json. Pure path
    # arithmetic; no filesystem access.
    $appRootNormalized = $Script:AppRoot.TrimEnd('\','/')
    $manifestNorm      = $Script:ManifestFile
    if ($manifestNorm.StartsWith($appRootNormalized, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rel = $manifestNorm.Substring($appRootNormalized.Length).TrimStart('\','/')
        return $rel.Replace('\', '/')
    }
    return $manifestNorm.Replace('\', '/')
}

function Invoke-RuntimeVersionGet {
    param($Context)

    # Integrity state is reported as a string (not a boolean) so the
    # contract can grow to other allowed states (`mismatch`, `missing`)
    # in the future without breaking clients. At runtime the only
    # reachable state is `ok` because the broker exits on integrity
    # failure before this endpoint is bound.
    $integrity = 'ok'

    # Updater availability is driven exclusively by whether a syntactically
    # valid update-manifest URL is configured in VERSION.json. The Settings
    # UI uses this flag to decide whether the "Check for Updates" button is
    # enabled. URL validation here is a syntax check only -- no outbound
    # network. Actual fetching happens via POST /api/v1/updates/check.
    $updateManifestUrlConfigured = $false
    if (-not [string]::IsNullOrWhiteSpace($Script:UpdateManifestUrl)) {
        try {
            $urlCheck = Test-UpdateManifestUrl -Url $Script:UpdateManifestUrl
            if ($urlCheck.ok) { $updateManifestUrlConfigured = $true }
        } catch {
            $updateManifestUrlConfigured = $false
        }
    }

    # latestKnownCookbookVersion reflects the on-disk manifest.json
    # alignment surface only (a release-tagged hint). It is NOT the
    # result of an outbound update-manifest fetch -- that lives in
    # /api/v1/updates/state.lastCheck.
    $latestKnown = $Script:ManifestLatestCookbookVersion
    $upToDate    = $false
    if ($latestKnown -and $Script:CookbookVersion -and ($latestKnown -eq $Script:CookbookVersion)) {
        $upToDate = $true
    }

    $body = [ordered]@{
        cookbookVersion = $Script:CookbookVersion
        releaseChannel  = $Script:ReleaseChannel
        bundledPax      = [ordered]@{
            version      = $Script:PaxScriptVersion
            sha256       = $Script:PaxScriptSha256
            relativePath = (Get-PaxScriptRelativePath)
            integrity    = $integrity
        }
        manifest        = [ordered]@{
            channel               = $Script:ManifestChannel
            aligned               = [bool]$Script:ManifestAligned
            latestCookbookVersion = $Script:ManifestLatestCookbookVersion
            packageUrlConfigured  = [bool]$Script:ManifestPackageUrlConfigured
            relativePath          = (Get-ManifestRelativePath)
            updateManifestUrl     = $Script:UpdateManifestUrl
            updateManifestUrlConfigured = [bool]$updateManifestUrlConfigured
        }
        host            = [ordered]@{
            machineName = $Script:HostMachineName
            psVersion   = $Script:HostPsVersion
            osPlatform  = $Script:HostOsPlatform
            osVersion   = $Script:HostOsVersion
        }
        paths           = [ordered]@{
            appRoot      = $Script:AppRoot
            paxScript    = $Script:PaxScriptPath
            versionFile  = $Script:VersionFile
            manifestFile = $Script:ManifestFile
            workspace    = $Script:WorkspacePath
            recipes      = $Script:RecipesDir
            cooks        = $Script:CooksDir
            database     = $Script:DatabaseFile
            templates    = $Script:TemplatesDir
        }
        runtime         = [ordered]@{
            brokerProcessId = $PID
            brokerPort      = $Script:BrokerPort
            startedAtUtc    = $Script:StartedAt.ToString('o')
            transport       = 'loopback-http'
            bindAddress     = '127.0.0.1'
        }
        brokerSession   = [ordered]@{
            # Phase AH.C3 -- restart-truth surfacing on the runtime
            # payload. Every field below is observational. None of
            # them implies runtime continuity, authority continuity,
            # cook resumption, or session restoration. The new broker
            # minted a fresh session token, started Locked, and cleared
            # the WebSocket registry regardless of these values.
            #
            # Shape invariant: the brokerSession block on the (unauth)
            # /api/v1/health payload is a strict subset of the block
            # below, so a caller that read /health first and /runtime/
            # version later will never see contradictory shapes for
            # the same field. The Health block intentionally omits the
            # prior-session forensic fields (which would leak prior
            # broker identity to unauthenticated callers).
            sessionId                        = $Script:BrokerSessionId
            startedAtUtc                     = $Script:StartedAt.ToString('o')
            startupClassification            = $Script:BrokerStartupClassification
            observedPriorSessionId           = $Script:BrokerStartupPriorSessionId
            observedPriorSessionStopClass    = $Script:BrokerStartupPriorSessionStopClass
            observedPriorSessionStoppedAtUtc = $Script:BrokerStartupPriorSessionStoppedAt
            priorRunningCookCountAtStartup   = [int]$Script:BrokerStartupPriorRunningCookCount
            reconciledCookCountAtStartup     = $(
                if ($null -eq $Script:BrokerStartupReconciledCookCount) { $null }
                else { [int]$Script:BrokerStartupReconciledCookCount }
            )
            evidenceClassification           = [ordered]@{
                sessionId                        = 'runtime-only'
                startedAtUtc                     = 'runtime-only'
                startupClassification            = 'observational'
                observedPriorSessionId           = 'observational'
                observedPriorSessionStopClass    = 'observational'
                observedPriorSessionStoppedAtUtc = 'observational'
                priorRunningCookCountAtStartup   = 'observational'
                reconciledCookCountAtStartup     = 'observational'
            }
        }
        updateReadiness = [ordered]@{
            updaterAvailable           = [bool]$updateManifestUrlConfigured
            latestKnownCookbookVersion = $Script:ManifestLatestCookbookVersion
            upToDate                   = [bool]$upToDate
            checkPerformedAt           = $null
            lastCheckSource            = 'bundled-manifest'
        }
    }
    Write-JsonResponse -Context $Context -Status 200 -Body $body
}

function Invoke-RuntimeRoute {
    # Returns $true if the request was consumed by this handler.
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -eq '/api/v1/runtime/version') {
        if ($method -ne 'GET') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-RuntimeVersionGet -Context $Context
        return $true
    }

    return $false
}

