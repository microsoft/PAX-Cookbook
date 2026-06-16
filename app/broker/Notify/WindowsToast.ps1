# ====================================================================
# Notify/WindowsToast.ps1 -- best-effort Windows Action Center toast
# ====================================================================
#
# This file owns one concern: turning a finished-bake notification into
# a native Windows Action Center (toast) notification, delivered through
# the direct WinRT projection. It is a best-effort presentation surface
# layered on top of the channel-agnostic dispatch in Notification.ps1.
#
# Hard contract:
#   - Toast delivery is BEST-EFFORT. Every WinRT / type-load / AUMID /
#     session / elevation / OS-notification failure is caught here and
#     turned into a bounded, sanitized reason string. This file never
#     throws into its caller, so a toast can never block or fail bake
#     finalization, scheduled reconcile, cook history, broker startup,
#     app navigation, or the durable JSONL / in-app surfaces.
#   - Text toast ONLY. No action buttons, no deep links, no protocol
#     activation, no COM activator, no callback plumbing. A click simply
#     dismisses the toast (the default for a text toast).
#   - Privacy-safe text ONLY. The toast carries the fixed app title and
#     a short status line whose only free-text element is the
#     operator-authored recipe name (already shown in the Cookbook UI).
#     No file paths, output folders, URLs, tenant or user identifiers,
#     auth-profile names, tokens, secrets, raw error text, stack traces,
#     script arguments, the PAX command line, or raw JSON ever appear.
#
# Identity: the toast is shown under the existing app AppUserModelID
# 'PAXCookbook.Local.v1' -- the same identity the installer stamps on the
# Cookbook shortcuts and the launcher stamps on the Edge app-window
# process. No second AUMID is invented and no shortcut is restamped.
#
# Dependencies: the in-box WinRT projection only. No BurntToast, no
# Install-Module, no PSGallery, no third-party module. Pure base library
# plus the projected Windows.UI.Notifications / Windows.Data.Xml.Dom
# surfaces.
# ====================================================================

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The single app identity. Matches the installer's shortcut AUMID and the
# launcher's Edge app-window AUMID. A toast notifier created for this
# identity groups with the installed Cookbook app.
$Script:WindowsToastAumid = 'PAXCookbook.Local.v1'

# status -> fixed toast title/body. The body's only free-text element is
# the operator-authored recipe name; everything else is a fixed string
# derived from status. A missing/blank recipe name degrades to 'Bake'.
function Get-WindowsToastContent {
    param(
        [string]$Status,
        [string]$RecipeName
    )

    $safeName = [string]$RecipeName
    if ([string]::IsNullOrWhiteSpace($safeName)) { $safeName = 'Bake' }

    switch ($Status) {
        'completed' {
            return @{ title = 'PAX Cookbook'; body = ('Bake completed — ' + $safeName) }
        }
        'errored' {
            return @{ title = 'PAX Cookbook'; body = ('Bake failed — ' + $safeName + '. Open Cookbook for details.') }
        }
        'interrupted' {
            return @{ title = 'PAX Cookbook'; body = ('Bake stopped — ' + $safeName) }
        }
        default {
            return $null
        }
    }
}

function Send-WindowsToastNotification {
    # Best-effort native toast. Returns @{ ok = $true } on a successful
    # Show, or @{ ok = $false; error = '<bounded_reason>' } on any
    # failure or suppression. Never throws.
    #
    # Bounded reasons (the only values ever returned in 'error'):
    #   windows_toast_disabled_or_suppressed
    #   windows_toast_not_supported
    #   windows_toast_type_load_failed
    #   windows_toast_xml_failed
    #   windows_toast_notifier_failed
    #   windows_toast_show_failed
    #   windows_toast_unavailable
    #   windows_toast_unknown_failure
    param(
        [string]$Status,
        [string]$RecipeName,
        [string]$Aumid = $Script:WindowsToastAumid
    )

    try {
        # Suppression hook. Lets a host (tests today; the settings UI in a
        # later slice) turn the surface off without touching WinRT. When
        # set, no notification is shown and a bounded reason is returned.
        if ($env:PAXCOOKBOOK_OS_TOAST_SUPPRESS -eq '1') {
            return @{ ok = $false; error = 'windows_toast_disabled_or_suppressed' }
        }

        if ([string]::IsNullOrWhiteSpace([string]$Aumid)) {
            return @{ ok = $false; error = 'windows_toast_unavailable' }
        }

        $content = Get-WindowsToastContent -Status $Status -RecipeName $RecipeName
        if ($null -eq $content) {
            return @{ ok = $false; error = 'windows_toast_not_supported' }
        }

        # Project the in-box WinRT types. A platform that does not expose
        # them (non-Windows, trimmed runtime, missing OS feature) fails
        # here and degrades to a bounded reason.
        try {
            $null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime]
            $null = [Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime]
            $null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        } catch {
            return @{ ok = $false; error = 'windows_toast_type_load_failed' }
        }

        # Build the toast payload from a fixed, injection-free skeleton,
        # then set the two text nodes via InnerText so the recipe name is
        # XML-escaped automatically. The recipe name is never concatenated
        # into the markup string.
        $toastXml = $null
        try {
            $toastXml = [Windows.Data.Xml.Dom.XmlDocument]::new()
            $toastXml.LoadXml('<toast><visual><binding template="ToastGeneric"><text></text><text></text></binding></visual></toast>')
            $textNodes = $toastXml.GetElementsByTagName('text')
            $textNodes.Item(0).InnerText = [string]$content.title
            $textNodes.Item(1).InnerText = [string]$content.body
        } catch {
            return @{ ok = $false; error = 'windows_toast_xml_failed' }
        }

        $toast = $null
        try {
            $toast = [Windows.UI.Notifications.ToastNotification]::new($toastXml)
        } catch {
            return @{ ok = $false; error = 'windows_toast_xml_failed' }
        }

        $notifier = $null
        try {
            $notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($Aumid)
        } catch {
            return @{ ok = $false; error = 'windows_toast_notifier_failed' }
        }
        if ($null -eq $notifier) {
            return @{ ok = $false; error = 'windows_toast_notifier_failed' }
        }

        try {
            $notifier.Show($toast)
        } catch {
            return @{ ok = $false; error = 'windows_toast_show_failed' }
        }

        return @{ ok = $true }
    } catch {
        return @{ ok = $false; error = 'windows_toast_unknown_failure' }
    }
}
