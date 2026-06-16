# ====================================================================
# Notify/Webhook.ps1 -- best-effort opt-in outbound webhook surface
# ====================================================================
#
# This file owns one concern: turning a finished-bake notification event
# into a single outbound HTTPS POST to a user-configured webhook endpoint.
# It is the ONLY file in the product that performs a notification webhook
# outbound call; no other notification file (Notification.ps1,
# WindowsToast.ps1, NotificationSettings.ps1) opens a socket or issues an
# HTTP request. Keeping the outbound surface isolated to this one adapter
# mirrors the broker's existing single-choke-point discipline for the
# Update module.
#
# Hard contract:
#   - DISABLED BY DEFAULT. The webhook surface is never attempted unless
#     the operator has explicitly opted in (a stored preference) AND
#     supplied a valid endpoint URL. A fresh install performs zero
#     outbound webhook traffic.
#   - BEST-EFFORT. Every validation, transport, timeout, redirect, and
#     HTTP-status failure is caught here and turned into a bounded,
#     sanitized reason string. This file never throws into its caller, so
#     a webhook can never block or fail bake finalization, scheduled
#     reconcile, cook history, broker startup, or the durable JSONL / in-
#     app / toast surfaces.
#   - HTTPS ONLY. The endpoint scheme must be https. http, file, and every
#     other scheme is rejected before any network call.
#   - NO REDIRECTS. The POST is issued with redirects disabled so the
#     configured endpoint cannot bounce the request (and its body) to a
#     different origin.
#   - BOUNDED TIMEOUT. A single request with a short, fixed timeout. There
#     is no background retry queue, no scheduled re-send, and no unbounded
#     wait.
#   - PRIVACY-SAFE BODY ONLY. The webhook body carries only the same
#     privacy-safe fields the durable JSONL line and the in-app feed
#     already expose: ts, eventId, cookId, recipeId, recipeName, source,
#     status, severity, exitCode, durationSec, rowCount, message (plus a
#     fixed app label and schema version). It NEVER carries file or output
#     paths, URLs (including the configured endpoint URL itself), tenant or
#     user identifiers, auth-profile names, tokens, secrets, raw error
#     text, stack traces, script arguments, the PAX command line, the
#     Windows username, or the machine name.
#
# SSRF posture: the endpoint URL is authored solely by the operator (no
# auto-discovery). Before any request the URL is validated to be an
# absolute https URI with a real host, no embedded credentials, and a host
# that is not a loopback, link-local, private, or cloud-metadata address.
# Validation performs NO DNS resolution; it inspects the literal URL only.
#
# Secret / HMAC signing is intentionally NOT implemented in this slice.
# The webhook is URL-only: no secret is requested, stored, or transmitted,
# so there is no new secret surface here. Signed delivery (an HMAC of the
# body using a secret held in Windows Credential Manager) is a separately
# scoped follow-up; until then this adapter neither reads nor writes any
# credential.
#
# Dependencies: the in-box .NET HTTP stack reached through Invoke-RestMethod
# only. No third-party module, no Install-Module, no PSGallery.
# ====================================================================

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A generous upper bound on the stored/validated endpoint length. This is
# a malformed-input guard (a multi-kilobyte "URL" is not a real endpoint),
# not a feature cap on the notification itself.
$Script:WebhookUrlMaxLength = 2048

# The single request timeout, in seconds. One short, bounded attempt.
$Script:WebhookTimeoutSec = 8

# The allowed payload shapes. 'generic' is a flat privacy-safe JSON object;
# 'teams' wraps the same privacy-safe values in a Microsoft Teams /
# Power Automate compatible MessageCard. No other format exists.
$Script:WebhookFormats = @('generic', 'teams')

# status -> fixed MessageCard theme color (Teams preset only). Derived from
# the event severity; no caller text reaches these values.
$Script:WebhookTeamsThemeColor = @{
    info    = '2EB67D'
    warning = 'ECB22E'
    error   = 'E01E5A'
}

function Test-WebhookHostBlocked {
    # Returns $true if the literal host is a loopback, link-local, private,
    # or cloud-metadata address that must never be a webhook target. No DNS
    # resolution is performed; only the literal host string is inspected.
    param([string]$HostName)

    if ([string]::IsNullOrWhiteSpace([string]$HostName)) { return $true }

    $h = ([string]$HostName).Trim().ToLowerInvariant()

    # Strip IPv6 brackets if the Uri handed them back bracketed.
    if ($h.StartsWith('[') -and $h.EndsWith(']') -and $h.Length -ge 2) {
        $h = $h.Substring(1, $h.Length - 2)
    }

    # Named loopback never resolves outward.
    if ($h -eq 'localhost') { return $true }
    if ($h.EndsWith('.localhost')) { return $true }

    $ip = $null
    if ([System.Net.IPAddress]::TryParse($h, [ref]$ip)) {
        if ($ip.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork) {
            $b = $ip.GetAddressBytes()
            if ($b[0] -eq 127) { return $true }                                   # 127.0.0.0/8 loopback
            if ($b[0] -eq 10)  { return $true }                                   # 10.0.0.0/8 private
            if ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) { return $true } # 172.16.0.0/12 private
            if ($b[0] -eq 192 -and $b[1] -eq 168) { return $true }                # 192.168.0.0/16 private
            if ($b[0] -eq 169 -and $b[1] -eq 254) { return $true }                # 169.254.0.0/16 link-local + metadata
            if ($b[0] -eq 0) { return $true }                                     # 0.0.0.0/8 "this host"
            return $false
        }
        if ($ip.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6) {
            if ($ip.Equals([System.Net.IPAddress]::IPv6Loopback)) { return $true } # ::1
            if ($ip.IsIPv6LinkLocal) { return $true }                             # fe80::/10
            if ($ip.IsIPv6SiteLocal) { return $true }
            if ($ip.IsIPv4MappedToIPv6) {
                $mb = $ip.MapToIPv4().GetAddressBytes()
                if ($mb[0] -eq 127) { return $true }
                if ($mb[0] -eq 10)  { return $true }
                if ($mb[0] -eq 172 -and $mb[1] -ge 16 -and $mb[1] -le 31) { return $true }
                if ($mb[0] -eq 192 -and $mb[1] -eq 168) { return $true }
                if ($mb[0] -eq 169 -and $mb[1] -eq 254) { return $true }
                if ($mb[0] -eq 0) { return $true }
                return $false
            }
            $vb = $ip.GetAddressBytes()
            if (($vb[0] -band 0xFE) -eq 0xFC) { return $true }                    # fc00::/7 unique-local
            return $false
        }
        # An address family that is neither IPv4 nor IPv6 is not a target.
        return $true
    }

    # A registered DNS name (not an IP literal). Allowed; no resolution here.
    return $false
}

function Test-WebhookUrl {
    # Validate a candidate webhook endpoint. Returns
    #   @{ ok = $true;  url = <normalized absolute uri> }   on success, or
    #   @{ ok = $false; error = 'webhook_invalid_url' }     on any rejection.
    # Pure string inspection; performs NO DNS resolution and NO network I/O.
    param([string]$Url)

    if ([string]::IsNullOrWhiteSpace([string]$Url)) {
        return @{ ok = $false; error = 'webhook_invalid_url' }
    }
    $candidate = ([string]$Url).Trim()
    if ($candidate.Length -gt $Script:WebhookUrlMaxLength) {
        return @{ ok = $false; error = 'webhook_invalid_url' }
    }

    $uri = $null
    if (-not [System.Uri]::TryCreate($candidate, [System.UriKind]::Absolute, [ref]$uri)) {
        return @{ ok = $false; error = 'webhook_invalid_url' }
    }
    if ($null -eq $uri) {
        return @{ ok = $false; error = 'webhook_invalid_url' }
    }

    # Scheme must be exactly https.
    if ($uri.Scheme -ne 'https') {
        return @{ ok = $false; error = 'webhook_invalid_url' }
    }

    # No embedded credentials (user:pass@host).
    if (-not [string]::IsNullOrEmpty($uri.UserInfo)) {
        return @{ ok = $false; error = 'webhook_invalid_url' }
    }

    # A real host is required.
    if ([string]::IsNullOrWhiteSpace($uri.Host)) {
        return @{ ok = $false; error = 'webhook_invalid_url' }
    }

    if (Test-WebhookHostBlocked -HostName $uri.Host) {
        return @{ ok = $false; error = 'webhook_invalid_url' }
    }

    return @{ ok = $true; url = $uri.AbsoluteUri }
}

function Get-WebhookPayload {
    # Build the privacy-safe webhook body for the given event and format.
    # The body carries ONLY the canonical event fields plus a fixed app
    # label and schema version. The configured endpoint URL, any secret,
    # paths, identifiers beyond the event's own ids, and raw error text are
    # never included. Returns an ordered dictionary ready for ConvertTo-Json.
    param(
        [System.Collections.IDictionary]$Event,
        [string]$Format
    )

    # Pull only the approved fields out of the event, defaulting any absent
    # key to $null so the wire shape is stable regardless of caller input.
    $ts          = if ($Event.Contains('ts'))          { $Event['ts'] }          else { $null }
    $eventId     = if ($Event.Contains('eventId'))     { $Event['eventId'] }     else { $null }
    $cookId      = if ($Event.Contains('cookId'))      { $Event['cookId'] }      else { $null }
    $recipeId    = if ($Event.Contains('recipeId'))    { $Event['recipeId'] }    else { $null }
    $recipeName  = if ($Event.Contains('recipeName'))  { $Event['recipeName'] }  else { $null }
    $source      = if ($Event.Contains('source'))      { $Event['source'] }      else { $null }
    $status      = if ($Event.Contains('status'))      { $Event['status'] }      else { $null }
    $severity    = if ($Event.Contains('severity'))    { $Event['severity'] }    else { $null }
    $exitCode    = if ($Event.Contains('exitCode'))    { $Event['exitCode'] }    else { $null }
    $durationSec = if ($Event.Contains('durationSec')) { $Event['durationSec'] } else { $null }
    $rowCount    = if ($Event.Contains('rowCount'))    { $Event['rowCount'] }    else { $null }
    $message     = if ($Event.Contains('message'))     { $Event['message'] }     else { $null }

    if ($Format -eq 'teams') {
        # Microsoft Teams / Power Automate compatible MessageCard. Every
        # value below is a privacy-safe event field or a fixed string. The
        # only free-text element is the operator-authored recipe name,
        # already shown in the Cookbook UI. No URL, path, secret, or
        # identifier outside the event's own ids appears.
        $safeName = [string]$recipeName
        if ([string]::IsNullOrWhiteSpace($safeName)) { $safeName = 'Bake' }

        $themeColor = '2EB67D'
        if ($null -ne $severity -and $Script:WebhookTeamsThemeColor.ContainsKey([string]$severity)) {
            $themeColor = $Script:WebhookTeamsThemeColor[[string]$severity]
        }

        $exitText = if ($null -eq $exitCode) { 'n/a' } else { [string]$exitCode }
        $durText  = if ($null -eq $durationSec) { 'n/a' } else { [string]$durationSec }
        $rowText  = if ($null -eq $rowCount) { 'n/a' } else { [string]$rowCount }

        $facts = @(
            [ordered]@{ name = 'Recipe';     value = $safeName }
            [ordered]@{ name = 'Status';     value = [string]$status }
            [ordered]@{ name = 'Severity';   value = [string]$severity }
            [ordered]@{ name = 'Source';     value = [string]$source }
            [ordered]@{ name = 'Exit code';  value = $exitText }
            [ordered]@{ name = 'Duration s'; value = $durText }
            [ordered]@{ name = 'Rows';       value = $rowText }
            [ordered]@{ name = 'Time (UTC)'; value = [string]$ts }
        )

        return [ordered]@{
            '@type'      = 'MessageCard'
            '@context'   = 'http://schema.org/extensions'
            'summary'    = 'PAX Cookbook notification'
            'themeColor' = $themeColor
            'title'      = 'PAX Cookbook'
            'sections'   = @(
                [ordered]@{
                    'activityTitle' = [string]$message
                    'facts'         = $facts
                    'markdown'      = $false
                }
            )
        }
    }

    # Generic flat JSON. The canonical privacy-safe event plus a fixed app
    # label and schema version.
    return [ordered]@{
        app           = 'PAX Cookbook'
        schemaVersion = 1
        ts            = $ts
        eventId       = $eventId
        cookId        = $cookId
        recipeId      = $recipeId
        recipeName    = $recipeName
        source        = $source
        status        = $status
        severity      = $severity
        exitCode      = $exitCode
        durationSec   = $durationSec
        rowCount      = $rowCount
        message       = $message
    }
}

function Send-WebhookNotification {
    # Best-effort single outbound webhook POST. Returns @{ ok = $true } on a
    # 2xx response, or @{ ok = $false; error = '<bounded_reason>' } on any
    # validation, transport, timeout, redirect, or HTTP failure. Never
    # throws.
    #
    # Bounded reasons (the only values ever returned in 'error'):
    #   webhook_invalid_url
    #   webhook_timeout
    #   webhook_http_failed
    #   webhook_send_failed
    #   webhook_unknown_failure
    #
    # This is the ONLY function in the product that issues a notification
    # webhook outbound call. The endpoint URL is validated here again
    # (defense in depth) even though the settings route validates before
    # storage, so a malformed stored value can never reach the network.
    param(
        [string]$Url,
        [System.Collections.IDictionary]$Event,
        [string]$Format = 'generic'
    )

    try {
        $check = Test-WebhookUrl -Url $Url
        if (-not $check.ok) {
            return @{ ok = $false; error = 'webhook_invalid_url' }
        }
        $validUrl = $check.url

        $fmt = if ($Format -eq 'teams') { 'teams' } else { 'generic' }

        $payload = Get-WebhookPayload -Event $Event -Format $fmt
        $bodyJson = $null
        try {
            $bodyJson = $payload | ConvertTo-Json -Depth 8 -Compress
        } catch {
            return @{ ok = $false; error = 'webhook_send_failed' }
        }

        try {
            # A single POST. Redirects disabled so the request body cannot
            # be bounced to another origin. Short bounded timeout. No retry.
            $null = Invoke-RestMethod -Uri $validUrl -Method Post `
                -ContentType 'application/json' -Body $bodyJson `
                -TimeoutSec $Script:WebhookTimeoutSec -MaximumRedirection 0 `
                -ErrorAction Stop
            return @{ ok = $true }
        } catch {
            $ex = $_.Exception
            $reason = 'webhook_send_failed'
            if ($null -ne $ex) {
                $typeName = $ex.GetType().FullName
                if ($ex -is [System.TimeoutException]) {
                    $reason = 'webhook_timeout'
                } elseif ($typeName -like '*HttpResponseException*') {
                    $reason = 'webhook_http_failed'
                } elseif ($typeName -like '*TaskCanceled*' -or $typeName -like '*OperationCanceled*') {
                    $reason = 'webhook_timeout'
                } elseif ($typeName -like '*HttpRequestException*') {
                    $reason = 'webhook_send_failed'
                }
            }
            return @{ ok = $false; error = $reason }
        }
    } catch {
        return @{ ok = $false; error = 'webhook_unknown_failure' }
    }
}
