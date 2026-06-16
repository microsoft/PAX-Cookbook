# ====================================================================
# Notify/Notification.ps1 -- channel-agnostic notification foundation
# ====================================================================
#
# This file is the first slice of the v1 notification system. It owns
# three concerns and nothing else:
#
#   1. The canonical notification EVENT model. One object shape with a
#      fixed set of fields describes a finished bake regardless of how
#      it is later delivered. New-NotificationEvent validates and
#      normalizes caller input into that shape, derives a privacy-safe
#      message, and refuses malformed input with a structured failure
#      rather than an exception.
#
#   2. The DISPATCH helper and its result model. Invoke-NotificationDispatch
#      takes the raw fields of a finished bake, builds the event, and
#      drives the durable JSONL surface. It returns a channel-agnostic
#      result object (ok / event / surfacesAttempted / surfacesSucceeded
#      / surfaceErrors) whose shape is ready for additional surfaces --
#      in-app feed, Windows Action Center toast, generic opt-in webhook,
#      Teams preset -- to plug in later without redesign.
#
#   3. The JSONL adapter. Each finished bake appends exactly one line to
#      <Workspace>/Notifications/<YYYY-MM-DD>.jsonl (UTC date). JSONL is
#      the durable source of truth: it is written first in dispatch order
#      so the record survives even if every future best-effort channel
#      fails.
#
# What this file is NOT (by deliberate scope):
#   - It does NOT wire into bake execution. The supervisor terminal
#     funnel and the scheduled-task reconciler are untouched by this
#     slice; integration is a later slice.
#   - It performs ZERO outbound network. No webhook, no Teams, no email,
#     no SMTP, no Graph, no sockets, no HTTP. The result model reserves
#     room for an opt-in webhook surface, but no such surface exists or
#     is invoked here.
#   - It takes NO PSGallery / module dependency. No BurntToast, no
#     Install-Module, no Windows.UI.Notifications. Pure base library.
#
# Privacy doctrine: a notification carries only what an operator already
# sees in the Cookbook UI. The fixed message strings never embed file
# paths, output folders, URLs, tenant or user identifiers, auth-profile
# names, tokens, secrets, raw error text, script arguments, or the PAX
# command line. The operator-authored recipe name is the only free-text
# field, and it is already surfaced in the UI.
#
# Self-contained: this file resolves its own UTC timestamps and IDs so
# the notification core can be dot-sourced and exercised in isolation.
# It depends on no broker-process $Script:* state.
# ====================================================================

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Frozen vocabularies. Do not extend without a matching schema decision.
$Script:NotificationSourceSet   = @('manual', 'scheduled')
$Script:NotificationStatusSet   = @('completed', 'errored', 'interrupted')
$Script:NotificationSeveritySet = @('info', 'warning', 'error')

# status -> severity (derived, never trusted from the caller)
$Script:NotificationStatusSeverity = @{
    completed   = 'info'
    errored     = 'error'
    interrupted = 'warning'
}

# status -> fixed privacy-safe message. The caller cannot supply a
# message; it is always derived here so raw error text can never leak.
$Script:NotificationStatusMessage = @{
    completed   = 'Bake completed.'
    errored     = 'Bake failed. Open Cookbook for details.'
    interrupted = 'Bake stopped.'
}

# Best-effort load of the Windows Action Center toast surface (sibling
# file). It is an additive presentation surface: if it is absent or fails
# to load, dispatch still writes the durable JSONL record and simply
# reports the toast surface as unavailable. The WinRT projection lives
# entirely in that sibling file so this core stays dependency-free and
# self-contained.
$Script:WindowsToastModulePath = Join-Path $PSScriptRoot 'WindowsToast.ps1'
try {
    if (Test-Path -LiteralPath $Script:WindowsToastModulePath -PathType Leaf) {
        . $Script:WindowsToastModulePath
    }
} catch {
    # A toast-surface load failure must never break the notification core.
}

# Best-effort load of the opt-in outbound webhook surface (sibling file).
# It is the single isolated outbound choke-point for notifications: all
# webhook URL validation, privacy-safe body construction, and the one
# HTTPS POST live entirely in that file so this core performs no outbound
# itself. If it is absent or fails to load, dispatch still writes the
# durable JSONL record and the toast surface, and simply never attempts a
# webhook.
$Script:WebhookModulePath = Join-Path $PSScriptRoot 'Webhook.ps1'
try {
    if (Test-Path -LiteralPath $Script:WebhookModulePath -PathType Leaf) {
        . $Script:WebhookModulePath
    }
} catch {
    # A webhook-surface load failure must never break the notification core.
}

function Get-NotificationUtcTimestamp {
    [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-NotificationUtcDateStamp {
    [DateTime]::UtcNow.ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
}

function New-NotificationEvent {
    # Builds and validates the canonical notification event. On success
    # returns @{ ok = $true; event = <ordered dictionary> }. On invalid
    # input returns @{ ok = $false; error = 'invalid_notification_payload' }.
    # Never throws for ordinary bad input; the structured failure is the
    # contract.
    #
    # The returned event carries ONLY the twelve canonical fields, built
    # fresh into an ordered dictionary. Caller input is never echoed back
    # as a raw hashtable, so no unexpected key can ride along into the
    # durable record.
    param(
        [string]$CookId,
        [string]$RecipeId,
        [string]$RecipeName,
        [string]$Source,
        [string]$Status,
        [string]$EventId,
        $ExitCode,
        $DurationSec,
        $RowCount
    )

    # Required string fields must be present and non-blank.
    foreach ($pair in @(
            @{ name = 'CookId'; value = $CookId },
            @{ name = 'RecipeId'; value = $RecipeId },
            @{ name = 'RecipeName'; value = $RecipeName }
        )) {
        if ([string]::IsNullOrWhiteSpace([string]$pair.value)) {
            return @{ ok = $false; error = 'invalid_notification_payload' }
        }
    }

    if ($Script:NotificationSourceSet -notcontains $Source) {
        return @{ ok = $false; error = 'invalid_notification_payload' }
    }
    if ($Script:NotificationStatusSet -notcontains $Status) {
        return @{ ok = $false; error = 'invalid_notification_payload' }
    }

    $severity = $Script:NotificationStatusSeverity[$Status]
    $message = $Script:NotificationStatusMessage[$Status]

    # Optional numeric fields. Absent stays $null (JSON null). Present is
    # coerced to its target type; an uncoercible value is malformed input.
    $exitCodeValue = $null
    if ($null -ne $ExitCode -and -not ([string]::IsNullOrWhiteSpace([string]$ExitCode))) {
        try { $exitCodeValue = [int]$ExitCode } catch {
            return @{ ok = $false; error = 'invalid_notification_payload' }
        }
    }

    $durationValue = $null
    if ($null -ne $DurationSec -and -not ([string]::IsNullOrWhiteSpace([string]$DurationSec))) {
        try { $durationValue = [double]$DurationSec } catch {
            return @{ ok = $false; error = 'invalid_notification_payload' }
        }
    }

    $rowCountValue = $null
    if ($null -ne $RowCount -and -not ([string]::IsNullOrWhiteSpace([string]$RowCount))) {
        try { $rowCountValue = [int]$RowCount } catch {
            return @{ ok = $false; error = 'invalid_notification_payload' }
        }
    }

    $resolvedEventId = $EventId
    if ([string]::IsNullOrWhiteSpace([string]$resolvedEventId)) {
        $resolvedEventId = [Guid]::NewGuid().ToString()
    }

    $notifyEvent = [ordered]@{
        ts          = Get-NotificationUtcTimestamp
        eventId     = [string]$resolvedEventId
        cookId      = [string]$CookId
        recipeId    = [string]$RecipeId
        recipeName  = [string]$RecipeName
        source      = [string]$Source
        status      = [string]$Status
        severity    = [string]$severity
        exitCode    = $exitCodeValue
        durationSec = $durationValue
        rowCount    = $rowCountValue
        message     = [string]$message
    }

    return @{ ok = $true; event = $notifyEvent }
}

function Write-NotificationJsonl {
    # Durable JSONL adapter. Appends exactly one line carrying the event
    # plus dispatch fields to <Workspace>/Notifications/<UTC-date>.jsonl.
    #
    # Returns @{ ok = $true } on success, or @{ ok = $false; error = '<reason>' }
    # on any failure. Never throws for an expected filesystem condition;
    # the structured failure is the contract so a notification can never
    # fail bake finalization.
    #
    # Atomicity: the full line is serialized BEFORE the file is opened, so
    # a serialization fault cannot leave a partial line on disk. The line
    # is appended in a single write of UTF-8 (no BOM) bytes including its
    # trailing newline, under FileShare.ReadWrite so concurrent readers
    # are never locked out. A briefly held OS lock is retried a bounded
    # number of times; the loop never waits indefinitely.
    param(
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][System.Collections.IDictionary]$LineObject
    )

    $notifyDir = Join-Path $WorkspacePath 'Notifications'
    try {
        if (-not (Test-Path -LiteralPath $notifyDir -PathType Container)) {
            New-Item -ItemType Directory -Path $notifyDir -Force -ErrorAction Stop | Out-Null
        }
    } catch {
        return @{ ok = $false; error = 'notifications_directory_unavailable' }
    }

    $jsonLine = $null
    try {
        $jsonLine = $LineObject | ConvertTo-Json -Compress -Depth 6
    } catch {
        return @{ ok = $false; error = 'notification_json_serialization_failed' }
    }
    if ([string]::IsNullOrEmpty($jsonLine)) {
        return @{ ok = $false; error = 'notification_json_serialization_failed' }
    }

    $fileName = (Get-NotificationUtcDateStamp) + '.jsonl'
    $filePath = Join-Path $notifyDir $fileName

    $enc = [System.Text.UTF8Encoding]::new($false)
    $bytes = $enc.GetBytes($jsonLine + "`n")

    $maxAttempts = 5
    $attempt = 0
    while ($true) {
        $attempt++
        $fs = $null
        try {
            $fs = [System.IO.FileStream]::new(
                $filePath,
                [System.IO.FileMode]::Append,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::ReadWrite)
            $fs.Write($bytes, 0, $bytes.Length)
            $fs.Flush($true)
            return @{ ok = $true }
        } catch [System.IO.IOException] {
            if ($attempt -ge $maxAttempts) {
                return @{ ok = $false; error = 'notification_jsonl_locked' }
            }
            Start-Sleep -Milliseconds 25
        } catch {
            return @{ ok = $false; error = 'notification_jsonl_write_failed' }
        } finally {
            if ($null -ne $fs) { $fs.Dispose() }
        }
    }
}

function Invoke-NotificationDispatch {
    # Channel-agnostic dispatch entrypoint. Builds the canonical event
    # from raw finished-bake fields, then drives the durable JSONL surface
    # first. Returns the dispatch result model:
    #
    #     @{
    #         ok                = <bool>      # every attempted surface ok
    #         event             = <event|$null>
    #         surfacesAttempted = @(...)      # channel names tried
    #         surfacesSucceeded = @(...)      # channel names that wrote
    #         surfaceErrors     = @{ name = 'short_reason' }
    #     }
    #
    # Only the 'jsonl' surface exists in this slice. The shape is ready
    # for in-app, toast, webhook, and Teams surfaces to be added without
    # changing callers. A surface failure never throws into the caller by
    # default; it is reported in surfaceErrors and ok is set $false.
    param(
        [Parameter(Mandatory)][string]$WorkspacePath,
        [string]$CookId,
        [string]$RecipeId,
        [string]$RecipeName,
        [string]$Source,
        [string]$Status,
        [string]$EventId,
        $ExitCode,
        $DurationSec,
        $RowCount,
        [switch]$EnableOsToast
    )

    $built = New-NotificationEvent -CookId $CookId -RecipeId $RecipeId `
        -RecipeName $RecipeName -Source $Source -Status $Status `
        -EventId $EventId -ExitCode $ExitCode -DurationSec $DurationSec `
        -RowCount $RowCount

    if (-not $built.ok) {
        return @{
            ok                = $false
            event             = $null
            surfacesAttempted = @()
            surfacesSucceeded = @()
            surfaceErrors     = @{ event = $built.error }
        }
    }

    $notifyEvent = $built.event

    # JSONL is written first so the durable record exists before any
    # future best-effort channel is attempted. The line that lands on
    # disk records the event plus the surface outcome known at write
    # time: the jsonl surface itself. Future channels accumulate into
    # the returned result.
    $surfacesAttempted = @('jsonl')
    $surfacesSucceeded = @()
    $surfaceErrors = @{}

    $lineObject = [ordered]@{}
    foreach ($key in $notifyEvent.Keys) { $lineObject[$key] = $notifyEvent[$key] }
    $lineObject['surfacesAttempted'] = @('jsonl')
    $lineObject['surfacesSucceeded'] = @('jsonl')
    $lineObject['surfaceErrors'] = @{}

    $jsonlResult = Write-NotificationJsonl -WorkspacePath $WorkspacePath -LineObject $lineObject

    if ($jsonlResult.ok) {
        $surfacesSucceeded = @('jsonl')
    } else {
        $surfaceErrors['jsonl'] = $jsonlResult.error
    }

    # Windows Action Center toast -- best-effort second surface, attempted
    # AFTER the durable JSONL write. A JSONL failure does not prevent the
    # toast attempt (the event object was built); a toast failure does not
    # touch JSONL. The toast outcome accumulates into the returned model
    # only -- the on-disk line records the surface outcome known at write
    # time, which is the jsonl surface itself.
    #
    # Two user-controlled notification preferences gate this surface, each
    # reflected into the broker process environment from the durable
    # settings table so every runspace (the main runspace and the cook
    # supervisor's ThreadJob) reads the same live value:
    #   * The OS-toast master switch (PAXCOOKBOOK_OS_TOAST_ENABLED). The
    #     -EnableOsToast switch forces the master switch on for in-process
    #     callers regardless of the environment value.
    #   * The per-status switch for this bake's status
    #     (PAXCOOKBOOK_NOTIFY_COMPLETED / _ERRORED / _INTERRUPTED). An
    #     absent value, or any value other than '0', means enabled.
    # The toast is attempted only when both the master switch and this
    # status's switch are enabled. When either is disabled the toast is
    # not attempted and no os_toast surfaceErrors entry is recorded --
    # an intentionally disabled surface is not a delivery failure. The
    # durable JSONL record is written for every status regardless of these
    # switches, so in-app replay and cook history remain complete.
    $osToastEnabled = $EnableOsToast.IsPresent
    if (-not $osToastEnabled) {
        $osToastEnabled = ($env:PAXCOOKBOOK_OS_TOAST_ENABLED -eq '1')
    }

    $statusEnvName = $null
    switch ($Status) {
        'completed'   { $statusEnvName = 'PAXCOOKBOOK_NOTIFY_COMPLETED' }
        'errored'     { $statusEnvName = 'PAXCOOKBOOK_NOTIFY_ERRORED' }
        'interrupted' { $statusEnvName = 'PAXCOOKBOOK_NOTIFY_INTERRUPTED' }
    }
    $statusEnabled = $true
    if ($statusEnvName) {
        $statusPref = [System.Environment]::GetEnvironmentVariable($statusEnvName)
        $statusEnabled = ($statusPref -ne '0')
    }

    $attemptOsToast = ($osToastEnabled -and $statusEnabled)

    $osToastOk = $true
    if ($attemptOsToast) {
        $surfacesAttempted = @($surfacesAttempted + 'os_toast')
        $osToastOk = $false
        $toastResult = $null
        try {
            $toastCmd = Get-Command -Name Send-WindowsToastNotification -ErrorAction SilentlyContinue
            if ($null -eq $toastCmd) {
                $toastResult = @{ ok = $false; error = 'windows_toast_unavailable' }
            } else {
                $toastResult = Send-WindowsToastNotification -Status $Status -RecipeName $RecipeName
            }
        } catch {
            $toastResult = @{ ok = $false; error = 'windows_toast_unknown_failure' }
        }

        if ($toastResult -and $toastResult.ok) {
            $surfacesSucceeded = @($surfacesSucceeded + 'os_toast')
            $osToastOk = $true
        } else {
            $reason = 'windows_toast_unknown_failure'
            if ($toastResult -and $toastResult.error) { $reason = [string]$toastResult.error }
            $surfaceErrors['os_toast'] = $reason
        }
    }

    # Opt-in outbound webhook -- best-effort third surface, attempted AFTER
    # the durable JSONL write and the OS toast. JSONL and toast outcomes do
    # not gate it (the event object was built); a webhook failure does not
    # touch JSONL, toast, or bake finalization. The webhook outcome
    # accumulates into the returned model only -- the on-disk line records
    # the surface outcome known at write time, which is the jsonl surface
    # itself, and never the configured endpoint URL.
    #
    # This surface is DISABLED BY DEFAULT and gated by user-controlled
    # preferences reflected from the durable settings table into the broker
    # process environment, so every runspace (the main runspace and the
    # cook supervisor's ThreadJob) reads the same live value:
    #   * The webhook master switch (PAXCOOKBOOK_WEBHOOK_ENABLED), '1' only
    #     when the operator has explicitly opted in.
    #   * The configured endpoint (PAXCOOKBOOK_WEBHOOK_URL) and payload
    #     format (PAXCOOKBOOK_WEBHOOK_FORMAT; 'generic' or 'teams').
    #   * The same per-status switch the toast honors.
    # The webhook is attempted ONLY when the master switch is on, this
    # status's switch is enabled, an endpoint is configured, and that
    # endpoint passes URL validation (https-only, no loopback/private/
    # metadata host, no embedded credentials). When the webhook is disabled
    # or unconfigured it is NOT attempted, 'webhook' is NOT added to
    # surfacesAttempted, and no webhook surfaceErrors entry is recorded --
    # an intentionally-off or unconfigured surface is not a delivery
    # failure. Only a genuine attempt that fails records a bounded
    # surfaceErrors.webhook reason.
    $webhookEnabled = ($env:PAXCOOKBOOK_WEBHOOK_ENABLED -eq '1')
    $webhookUrl = [System.Environment]::GetEnvironmentVariable('PAXCOOKBOOK_WEBHOOK_URL')
    $webhookFormat = [System.Environment]::GetEnvironmentVariable('PAXCOOKBOOK_WEBHOOK_FORMAT')
    if ([string]::IsNullOrWhiteSpace([string]$webhookFormat)) { $webhookFormat = 'generic' }

    $attemptWebhook = $false
    $validatedWebhookUrl = $null
    if ($webhookEnabled -and $statusEnabled -and -not [string]::IsNullOrWhiteSpace([string]$webhookUrl)) {
        $urlCheckCmd = Get-Command -Name Test-WebhookUrl -ErrorAction SilentlyContinue
        $sendCmd = Get-Command -Name Send-WebhookNotification -ErrorAction SilentlyContinue
        if ($null -ne $urlCheckCmd -and $null -ne $sendCmd) {
            $urlCheck = $null
            try { $urlCheck = Test-WebhookUrl -Url $webhookUrl } catch { $urlCheck = $null }
            if ($urlCheck -and $urlCheck.ok) {
                $attemptWebhook = $true
                $validatedWebhookUrl = $urlCheck.url
            }
        }
    }

    $webhookOk = $true
    if ($attemptWebhook) {
        $surfacesAttempted = @($surfacesAttempted + 'webhook')
        $webhookOk = $false
        $webhookResult = $null
        try {
            $webhookResult = Send-WebhookNotification -Url $validatedWebhookUrl -Event $notifyEvent -Format $webhookFormat
        } catch {
            $webhookResult = @{ ok = $false; error = 'webhook_unknown_failure' }
        }

        if ($webhookResult -and $webhookResult.ok) {
            $surfacesSucceeded = @($surfacesSucceeded + 'webhook')
            $webhookOk = $true
        } else {
            $reason = 'webhook_unknown_failure'
            if ($webhookResult -and $webhookResult.error) { $reason = [string]$webhookResult.error }
            $surfaceErrors['webhook'] = $reason
        }
    }

    return @{
        ok                = ([bool]$jsonlResult.ok -and $osToastOk -and $webhookOk)
        event             = $notifyEvent
        surfacesAttempted = $surfacesAttempted
        surfacesSucceeded = $surfacesSucceeded
        surfaceErrors     = $surfaceErrors
    }
}
