#requires -Version 7.4

# Notifications.ps1 — HTTP route for read-only in-app notification replay.
#
#   GET /api/v1/notifications?date=YYYY-MM-DD   -> 200 { date, notifications: [...] }
#
# Surfaces the durable JSONL notification log produced by the notification
# core (Notify/Notification.ps1) so the web UI can replay bake completion,
# failure, and interruption events as in-app toasts/banners. The JSONL file
# is the single source of truth; this route only READS it.
#
# Contract:
#   - The query parameter `date` is optional. When absent, today's UTC date
#     (yyyy-MM-dd) is used. When present it MUST match ^\d{4}-\d{2}-\d{2}$
#     AND parse as a real calendar date; anything else -> 400. This is the
#     only file-selection input and it is never used to build an arbitrary
#     path: the validated date is the bare file name, joined to the fixed
#     <Workspace>\Notifications directory. No traversal, no caller-supplied
#     path segment, no extension control.
#   - A missing file (no notifications written for that date yet) returns an
#     empty list with 200, never 404 and never an error.
#   - Each returned object carries ONLY the approved notification fields
#     (see $approvedFields). Any extra key present on a JSONL line is
#     stripped; any approved key missing from a line surfaces as $null.
#   - Malformed (non-JSON) lines are skipped. Valid lines on either side of
#     a malformed line are still returned.
#   - The result is bounded to the most recent $maxNotifications entries
#     (JSONL is appended chronologically, so the tail is the newest).
#
# This route is read-only. It does NOT:
#   - write, append to, or truncate any file
#   - create the Notifications directory
#   - read any file other than the single validated date file
#   - perform any network call
#   - expose filesystem paths, URLs, tenant/user/auth/token/secret material,
#     stack traces, command lines, or any field outside the approved set
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - $Script:WorkspacePath   (string; workspace root)
#   - Write-JsonResponse      (helper from broker)
#
# It deliberately does NOT depend on the notification write path
# (Write-NotificationJsonl / Invoke-NotificationDispatch). Reading is fully
# decoupled from writing so a UI fetch can never trigger a write.

# The exact 15-key notification schema. Order is the on-disk schema order so
# the response shape is stable and self-documenting. Any other key on a line
# is dropped; any of these missing from a line is emitted as $null.
$Script:NotificationApprovedFields = @(
    'ts'
    'eventId'
    'cookId'
    'recipeId'
    'recipeName'
    'source'
    'status'
    'severity'
    'exitCode'
    'durationSec'
    'rowCount'
    'message'
    'surfacesAttempted'
    'surfacesSucceeded'
    'surfaceErrors'
)

# Upper bound on returned entries. The newest entries are the tail of the
# chronologically-appended JSONL, so we keep the last N.
$Script:NotificationMaxReturned = 50

function Test-NotificationDateParam {
    # Returns $true only when $Value is a syntactically well-formed AND
    # real calendar date in yyyy-MM-dd form. Used to gate the single
    # file-selection input. Pure validation; no filesystem access.
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return $false }
    if ($Value -notmatch '^\d{4}-\d{2}-\d{2}$') { return $false }
    $parsed = [datetime]::new(0)
    $ok = [datetime]::TryParseExact(
        $Value,
        'yyyy-MM-dd',
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::None,
        [ref]$parsed
    )
    return $ok
}

function Get-NotificationDateFile {
    # Resolve the absolute path of the JSONL file for a VALIDATED date.
    # The caller MUST have passed $Date through Test-NotificationDateParam
    # first. The date is used only as the bare file name; the directory is
    # the fixed <Workspace>\Notifications. Pure path arithmetic; no read.
    param([string]$Date)
    $dir = Join-Path -Path $Script:WorkspacePath -ChildPath 'Notifications'
    return (Join-Path -Path $dir -ChildPath ($Date + '.jsonl'))
}

function ConvertTo-NotificationView {
    # Project a parsed JSONL object down to EXACTLY the approved fields.
    # Extra keys are dropped; missing approved keys become $null. The
    # output is an ordered dictionary so JSON serialization is stable.
    param($Parsed)
    $view = [ordered]@{}
    foreach ($field in $Script:NotificationApprovedFields) {
        $value = $null
        if ($null -ne $Parsed -and ($Parsed.PSObject.Properties.Name -contains $field)) {
            $value = $Parsed.$field
        }
        $view[$field] = $value
    }
    return $view
}

function Get-NotificationsForDate {
    # Read the validated date file and return the bounded, field-stripped
    # list of notification views. Missing file -> empty list. Malformed
    # lines are skipped. Never writes. Never creates the directory.
    param([string]$Date)

    $file = Get-NotificationDateFile -Date $Date
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        return @()
    }

    $lines = @()
    try {
        $lines = @(Get-Content -LiteralPath $file -Encoding utf8 -ErrorAction Stop)
    } catch {
        # Unreadable file (transient lock / IO) is treated like an empty
        # day for the UI rather than a 500 — the JSONL is best-effort.
        return @()
    }

    $views = New-Object System.Collections.Generic.List[object]
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parsed = $null
        try { $parsed = $line | ConvertFrom-Json -ErrorAction Stop }
        catch { continue }   # malformed line — skip, keep going
        if ($null -eq $parsed) { continue }
        $views.Add((ConvertTo-NotificationView -Parsed $parsed))
    }

    # Bound to the most recent N entries (tail of the chronological log).
    $count = $views.Count
    if ($count -gt $Script:NotificationMaxReturned) {
        $start = $count - $Script:NotificationMaxReturned
        return $views.GetRange($start, $Script:NotificationMaxReturned).ToArray()
    }
    return $views.ToArray()
}

function Invoke-NotificationsGet {
    param($Context)

    $qs   = $Context.Request.QueryString
    $date = $qs['date']

    if ([string]::IsNullOrEmpty($date)) {
        # Default to today's UTC date in the same stamp format the writer
        # uses for the file name.
        $date = [datetime]::UtcNow.ToString('yyyy-MM-dd')
    } elseif (-not (Test-NotificationDateParam -Value $date)) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_date' }
        return
    }

    $items = @(Get-NotificationsForDate -Date $date)
    Write-JsonResponse -Context $Context -Status 200 -Body ([ordered]@{
        date          = $date
        count         = $items.Count
        notifications = $items
    })
}

function Invoke-NotificationsRoute {
    # Returns $true if the request was consumed by this handler.
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -eq '/api/v1/notifications') {
        if ($method -ne 'GET') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-NotificationsGet -Context $Context
        return $true
    }

    return $false
}
