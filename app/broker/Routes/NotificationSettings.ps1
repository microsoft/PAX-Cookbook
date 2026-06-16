#requires -Version 7.4

# NotificationSettings.ps1 — HTTP route for user-controlled notification
# preferences.
#
#   GET /api/v1/settings/notifications -> 200 { <preference keys> }
#   PUT /api/v1/settings/notifications -> 200 { <preference keys> }
#
# The preferences control which finished-bake notification surfaces a user
# wants. JSONL is always written and is the durable source of truth for
# in-app replay and cook history, so it is intentionally NOT
# user-disableable. These preferences gate only the user-facing push
# surfaces:
#
#   notify.completed.enabled     -- notify on a bake that completed
#   notify.errored.enabled       -- notify on a bake that errored/failed
#   notify.interrupted.enabled   -- notify on a bake that was stopped
#   notify.os_toast.enabled      -- deliver a Windows Action Center toast
#
# Plus an opt-in outbound webhook surface (disabled by default):
#
#   notify.webhook.enabled       -- deliver an outbound HTTPS webhook POST
#   notify.webhook.url           -- the user-authored https endpoint
#   notify.webhook.format        -- 'generic' or 'teams' payload shape
#
# Durable storage is the existing `settings` table (key/value/scope).
# Each boolean preference is stored under scope 'global' as the literal
# text 'true' or 'false'; the webhook url and format are stored as their
# literal string values. An absent boolean row means the default, which is
# enabled (true) for the four status/toast preferences and disabled (false)
# for the webhook. An absent url is the empty string; an absent format is
# 'generic'. No secret is accepted or stored by this route; the webhook is
# URL-only.
#
# Process reflection: the notification dispatch path runs in two runspaces
# (the broker main runspace and the cook supervisor's ThreadJob). Both read
# the live process environment, so each durable preference is reflected into
# a process environment variable that dispatch consults at send time:
#
#   notify.completed.enabled     -> PAXCOOKBOOK_NOTIFY_COMPLETED
#   notify.errored.enabled       -> PAXCOOKBOOK_NOTIFY_ERRORED
#   notify.interrupted.enabled   -> PAXCOOKBOOK_NOTIFY_INTERRUPTED
#   notify.os_toast.enabled      -> PAXCOOKBOOK_OS_TOAST_ENABLED
#   notify.webhook.enabled       -> PAXCOOKBOOK_WEBHOOK_ENABLED
#   notify.webhook.url           -> PAXCOOKBOOK_WEBHOOK_URL
#   notify.webhook.format        -> PAXCOOKBOOK_WEBHOOK_FORMAT
#
# Start-Broker reflects the durable values into the environment once at
# startup; a successful PUT re-reflects them immediately so a preference
# change takes effect for the next bake without a restart.
#
# Contract:
#   - GET returns every key. The four status/toast keys and the webhook
#     enable flag are booleans; the webhook url and format are strings. It
#     NEVER writes defaults; a missing boolean row reads as its default and
#     a missing url/format reads as '' / 'generic'.
#   - PUT accepts a JSON object whose keys are a non-empty subset of the
#     approved keys. Each status/toast/webhook-enable key requires a real
#     JSON boolean (non-boolean -> 400 invalid_value). notify.webhook.url
#     requires a string; a non-empty value must pass webhook URL validation
#     (https-only, no loopback/private/metadata host, no embedded
#     credentials) or it is rejected 400 invalid_webhook_url; an empty
#     string clears the configured endpoint. notify.webhook.format requires
#     the string 'generic' or 'teams' (else 400 invalid_webhook_format).
#     Any unknown key -> 400 unknown_setting. An empty or non-object body
#     -> 400. Validation runs over the entire body before any write, so an
#     invalid body is never partially applied.
#   - On success PUT persists the provided keys, re-reflects all
#     preferences into the environment, and returns the full normalized
#     settings object.
#
# This route does NOT:
#   - accept or store any secret, HMAC key, token, password, Teams/Slack
#     target, email address, tenant identifier, or any key outside the
#     approved set; the webhook is URL-only
#   - perform any outbound network call itself (the opt-in webhook POST
#     lives entirely in Notify\Webhook.ps1)
#   - expose filesystem paths or raw database paths
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - $Script:SqliteConn   (open Microsoft.Data.Sqlite connection)
#   - Write-JsonResponse / Read-RequestJson   (broker helpers)
#   - Add-RecentError      (broker helper)

# The ONLY boolean keys this route reads or writes. Order is the response
# order so the serialized object is stable and self-documenting. This list
# is the single allow-list used by both storage and the PUT validator. The
# first four default to enabled (true) when their row is absent.
$Script:NotificationSettingKeys = @(
    'notify.completed.enabled'
    'notify.errored.enabled'
    'notify.interrupted.enabled'
    'notify.os_toast.enabled'
)

# Opt-in outbound webhook keys. These are stored and validated separately
# from the four status/toast booleans because the webhook is disabled by
# default (its enable flag defaults to false, not true) and the url/format
# values are strings rather than booleans.
$Script:NotificationWebhookEnabledKey = 'notify.webhook.enabled'
$Script:NotificationWebhookUrlKey     = 'notify.webhook.url'
$Script:NotificationWebhookFormatKey  = 'notify.webhook.format'
$Script:NotificationWebhookFormats    = @('generic', 'teams')

# Maps each durable preference key to the process environment variable that
# the notification dispatch path reads at send time.
$Script:NotificationSettingEnvMap = @{
    'notify.completed.enabled'   = 'PAXCOOKBOOK_NOTIFY_COMPLETED'
    'notify.errored.enabled'     = 'PAXCOOKBOOK_NOTIFY_ERRORED'
    'notify.interrupted.enabled' = 'PAXCOOKBOOK_NOTIFY_INTERRUPTED'
    'notify.os_toast.enabled'    = 'PAXCOOKBOOK_OS_TOAST_ENABLED'
}

function Get-NotificationSettingBool {
    # Read one preference from the settings table. Returns $true when the
    # row is absent or unparseable (enabled is the default) and $false only
    # when the stored text is exactly 'false'. Pure read; never writes.
    param([string]$Key)

    if ($null -eq $Script:SqliteConn) { return $true }

    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = 'SELECT value FROM settings WHERE key = $k AND scope = ''global'' LIMIT 1;'
        $p = $cmd.CreateParameter(); $p.ParameterName = '$k'; $p.Value = [string]$Key; [void]$cmd.Parameters.Add($p)
        $raw = $null
        try { $raw = $cmd.ExecuteScalar() } finally { $cmd.Dispose() }

        if ($null -eq $raw -or $raw -is [System.DBNull]) { return $true }
        $text = ([string]$raw).Trim().ToLowerInvariant()
        if ($text -eq 'false') { return $false }
        return $true
    } catch {
        return $true
    }
}

function Set-NotificationSettingBool {
    # UPSERT one preference into the settings table as the literal text
    # 'true' or 'false'. Returns $true on success.
    param([string]$Key, [bool]$Value)

    if ($null -eq $Script:SqliteConn) { return $false }

    $textValue = if ($Value) { 'true' } else { 'false' }

    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = @'
INSERT INTO settings (key, value, scope) VALUES ($k, $v, 'global')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;
'@
        $p = $cmd.CreateParameter(); $p.ParameterName = '$k'; $p.Value = [string]$Key;       [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$v'; $p.Value = [string]$textValue; [void]$cmd.Parameters.Add($p)
        try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
        return $true
    } catch {
        Add-RecentError -Message ('Set-NotificationSettingBool failed: ' + $_.Exception.Message) -Source 'notification_settings'
        return $false
    }
}

function Get-NotificationWebhookEnabled {
    # Read the webhook master switch. Unlike the four status/toast
    # preferences, the webhook is DISABLED BY DEFAULT: an absent or
    # unparseable row reads as $false, and only the literal text 'true'
    # enables it. Pure read; never writes.
    if ($null -eq $Script:SqliteConn) { return $false }

    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = 'SELECT value FROM settings WHERE key = $k AND scope = ''global'' LIMIT 1;'
        $p = $cmd.CreateParameter(); $p.ParameterName = '$k'; $p.Value = [string]$Script:NotificationWebhookEnabledKey; [void]$cmd.Parameters.Add($p)
        $raw = $null
        try { $raw = $cmd.ExecuteScalar() } finally { $cmd.Dispose() }

        if ($null -eq $raw -or $raw -is [System.DBNull]) { return $false }
        $text = ([string]$raw).Trim().ToLowerInvariant()
        return ($text -eq 'true')
    } catch {
        return $false
    }
}

function Get-NotificationSettingString {
    # Read one string-valued preference from the settings table. Returns the
    # default when the row is absent or null. Pure read; never writes.
    param([string]$Key, [string]$Default = '')

    if ($null -eq $Script:SqliteConn) { return $Default }

    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = 'SELECT value FROM settings WHERE key = $k AND scope = ''global'' LIMIT 1;'
        $p = $cmd.CreateParameter(); $p.ParameterName = '$k'; $p.Value = [string]$Key; [void]$cmd.Parameters.Add($p)
        $raw = $null
        try { $raw = $cmd.ExecuteScalar() } finally { $cmd.Dispose() }

        if ($null -eq $raw -or $raw -is [System.DBNull]) { return $Default }
        return [string]$raw
    } catch {
        return $Default
    }
}

function Set-NotificationSettingString {
    # UPSERT one string-valued preference into the settings table. Returns
    # $true on success.
    param([string]$Key, [string]$Value)

    if ($null -eq $Script:SqliteConn) { return $false }

    try {
        $cmd = $Script:SqliteConn.CreateCommand()
        $cmd.CommandText = @'
INSERT INTO settings (key, value, scope) VALUES ($k, $v, 'global')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;
'@
        $p = $cmd.CreateParameter(); $p.ParameterName = '$k'; $p.Value = [string]$Key;   [void]$cmd.Parameters.Add($p)
        $p = $cmd.CreateParameter(); $p.ParameterName = '$v'; $p.Value = [string]$Value; [void]$cmd.Parameters.Add($p)
        try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
        return $true
    } catch {
        Add-RecentError -Message ('Set-NotificationSettingString failed: ' + $_.Exception.Message) -Source 'notification_settings'
        return $false
    }
}

function Get-NotificationWebhookFormat {
    # Read the webhook payload format, normalized to a known value. An
    # absent/unknown value reads as the default 'generic'.
    $fmt = Get-NotificationSettingString -Key $Script:NotificationWebhookFormatKey -Default 'generic'
    $fmt = ([string]$fmt).Trim().ToLowerInvariant()
    if ($Script:NotificationWebhookFormats -notcontains $fmt) { return 'generic' }
    return $fmt
}

function Get-AllNotificationSettings {
    # Read every preference as a stable, ordered map. The four status/toast
    # keys and the webhook enable flag are booleans; the webhook url and
    # format are strings. The url is exposed to the UI because it is not a
    # secret -- no secret is ever stored or returned by this route.
    $result = [ordered]@{}
    foreach ($key in $Script:NotificationSettingKeys) {
        $result[$key] = Get-NotificationSettingBool -Key $key
    }
    $result[$Script:NotificationWebhookEnabledKey] = Get-NotificationWebhookEnabled
    $result[$Script:NotificationWebhookUrlKey]     = Get-NotificationSettingString -Key $Script:NotificationWebhookUrlKey -Default ''
    $result[$Script:NotificationWebhookFormatKey]  = Get-NotificationWebhookFormat
    return $result
}

function Sync-NotificationPreferenceEnv {
    # Reflect every durable preference into its process environment variable
    # so the notification dispatch path in any runspace reads the current
    # values. The four status/toast booleans and the webhook enable flag
    # reflect as '1' (enabled) or '0' (disabled); the webhook url and format
    # reflect as their literal strings. Called once at broker startup and
    # again after every successful settings change.
    foreach ($key in $Script:NotificationSettingKeys) {
        $envName = $Script:NotificationSettingEnvMap[$key]
        $enabled = [bool](Get-NotificationSettingBool -Key $key)
        $envValue = if ($enabled) { '1' } else { '0' }
        Set-Item -Path ('Env:' + $envName) -Value $envValue
    }

    $webhookEnabled = [bool](Get-NotificationWebhookEnabled)
    $webhookEnabledValue = if ($webhookEnabled) { '1' } else { '0' }
    Set-Item -Path 'Env:PAXCOOKBOOK_WEBHOOK_ENABLED' -Value $webhookEnabledValue

    $webhookUrl = Get-NotificationSettingString -Key $Script:NotificationWebhookUrlKey -Default ''
    Set-Item -Path 'Env:PAXCOOKBOOK_WEBHOOK_URL' -Value ([string]$webhookUrl)

    $webhookFormat = Get-NotificationWebhookFormat
    Set-Item -Path 'Env:PAXCOOKBOOK_WEBHOOK_FORMAT' -Value ([string]$webhookFormat)
}

function Test-NotificationSettingsBody {
    # Validate a PUT body against the approved allow-list. Returns a
    # hashtable: @{ ok = $true; values = @{ key -> value } } on success, or
    # @{ ok = $false; error = <reason>; field = <key> } on failure. The
    # whole body is validated before any value is accepted, so an invalid
    # body never partially applies.
    #
    # Per-key type rules:
    #   * the four status/toast keys and notify.webhook.enabled require a
    #     real JSON boolean
    #   * notify.webhook.url requires a string; a non-empty value must pass
    #     webhook URL validation (https-only, no loopback/private/metadata
    #     host, no embedded credentials). An empty string clears the
    #     endpoint. If the validator helper is unavailable, a non-empty url
    #     is rejected (fail closed)
    #   * notify.webhook.format requires the string 'generic' or 'teams'
    param($Body)

    if ($null -eq $Body) {
        return @{ ok = $false; error = 'invalid_body' }
    }
    if ($Body -isnot [System.Collections.IDictionary]) {
        return @{ ok = $false; error = 'invalid_body' }
    }

    $keys = @($Body.Keys)
    if ($keys.Count -eq 0) {
        return @{ ok = $false; error = 'no_recognized_settings' }
    }

    $values = @{}
    foreach ($k in $keys) {
        $ks = [string]$k
        $v = $Body[$k]

        if ($Script:NotificationSettingKeys -contains $ks -or $ks -eq $Script:NotificationWebhookEnabledKey) {
            if ($v -isnot [bool]) {
                return @{ ok = $false; error = 'invalid_value'; field = $ks }
            }
            $values[$ks] = [bool]$v
            continue
        }

        if ($ks -eq $Script:NotificationWebhookUrlKey) {
            if ($v -isnot [string]) {
                return @{ ok = $false; error = 'invalid_value'; field = $ks }
            }
            $urlText = [string]$v
            if (-not [string]::IsNullOrWhiteSpace($urlText)) {
                $urlCheckCmd = Get-Command -Name Test-WebhookUrl -ErrorAction SilentlyContinue
                if ($null -eq $urlCheckCmd) {
                    return @{ ok = $false; error = 'invalid_webhook_url'; field = $ks }
                }
                $urlCheck = $null
                try { $urlCheck = Test-WebhookUrl -Url $urlText } catch { $urlCheck = $null }
                if (-not ($urlCheck -and $urlCheck.ok)) {
                    return @{ ok = $false; error = 'invalid_webhook_url'; field = $ks }
                }
                # Store the normalized absolute URI the validator returned.
                $values[$ks] = [string]$urlCheck.url
            } else {
                $values[$ks] = ''
            }
            continue
        }

        if ($ks -eq $Script:NotificationWebhookFormatKey) {
            if ($v -isnot [string]) {
                return @{ ok = $false; error = 'invalid_value'; field = $ks }
            }
            $fmt = ([string]$v).Trim().ToLowerInvariant()
            if ($Script:NotificationWebhookFormats -notcontains $fmt) {
                return @{ ok = $false; error = 'invalid_webhook_format'; field = $ks }
            }
            $values[$ks] = $fmt
            continue
        }

        return @{ ok = $false; error = 'unknown_setting'; field = $ks }
    }

    return @{ ok = $true; values = $values }
}

function Invoke-NotificationSettingsGet {
    param($Context)
    $all = Get-AllNotificationSettings
    Write-JsonResponse -Context $Context -Status 200 -Body $all
}

function Invoke-NotificationSettingsPut {
    param($Context)

    $body  = Read-RequestJson -Context $Context
    $check = Test-NotificationSettingsBody -Body $body
    if (-not $check.ok) {
        $errBody = @{ error = $check.error }
        if ($check.ContainsKey('field')) { $errBody['field'] = $check.field }
        Write-JsonResponse -Context $Context -Status 400 -Body $errBody
        return
    }

    $persistOk = $true
    foreach ($k in $check.values.Keys) {
        $val = $check.values[$k]
        if ($k -eq $Script:NotificationWebhookUrlKey -or $k -eq $Script:NotificationWebhookFormatKey) {
            if (-not (Set-NotificationSettingString -Key $k -Value ([string]$val))) {
                $persistOk = $false
            }
        } else {
            if (-not (Set-NotificationSettingBool -Key $k -Value ([bool]$val))) {
                $persistOk = $false
            }
        }
    }
    if (-not $persistOk) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'settings_persist_failed' }
        return
    }

    # Re-reflect the durable preferences into the process environment so the
    # next bake honors the change without a broker restart.
    Sync-NotificationPreferenceEnv

    $all = Get-AllNotificationSettings
    Write-JsonResponse -Context $Context -Status 200 -Body $all
}

function Invoke-NotificationSettingsRoute {
    # Returns $true if the request was consumed by this handler.
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -eq '/api/v1/settings/notifications') {
        if ($method -eq 'GET') {
            Invoke-NotificationSettingsGet -Context $Context
            return $true
        }
        if ($method -eq 'PUT') {
            Invoke-NotificationSettingsPut -Context $Context
            return $true
        }
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return $true
    }

    return $false
}
