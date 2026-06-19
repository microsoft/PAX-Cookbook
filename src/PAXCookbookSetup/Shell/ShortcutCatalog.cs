using System;
using System.Collections.Generic;
using System.IO;
using PAXCookbook.Shared;

namespace PAXCookbookSetup.Shell;

// Source-of-truth for the SINGLE shortcut Setup creates on the current user's
// system. Under corporate WDAC the unsigned apphost ("PAX Cookbook.exe") cannot
// be executed, so the shortcut launches the Microsoft-signed dotnet.exe with
// the app DLL as its argument; the apphost EXE is used ONLY as the icon source
// (reading an icon is allowed, executing is not).
//
// A SINGLE shortcut is created directly under Start Menu\Programs (NO product
// subfolder): the primary "PAX Cookbook" launcher. The former "PAX Cookbook -
// Repair" maintenance entry, the Support Mode / Uninstall shortcuts, and the
// "PAX Cookbook" group folder are no longer created — Repair and Uninstall are
// reached from Add/Remove Programs (Modify / Uninstall), not the Start Menu; the
// installer and uninstaller clean up any left by a prior version.
public static class ShortcutCatalog
{
    public const string PrimaryName       = "PAX Cookbook";
    public const string DesktopName       = "PAX Cookbook";

    // Legacy names — no longer created. Retained so install/uninstall can
    // recognize and remove shortcuts left by a prior version, and for
    // back-compatible references.
    //
    // RepairShortcutName: the old "PAX Cookbook - Repair" Start Menu entry.
    // Repair now lives ONLY in Add/Remove Programs (the Modify button), never as
    // a user-facing Start Menu shortcut; the installer deletes any stale copy.
    public const string RepairShortcutName = "PAX Cookbook - Repair";
    public const string SupportModeName   = "PAX Cookbook Support Mode";
    public const string RepairName        = "Repair PAX Cookbook";
    public const string UninstallName     = "Uninstall PAX Cookbook";

    // Legacy Start Menu group folder name. No longer created.
    public const string StartMenuGroup    = "PAX Cookbook";

    public static IReadOnlyList<ShortcutDefinition> StartMenuShortcuts(
        string installRoot, bool includeUninstall, bool includeSupport)
    {
        // The primary launcher ONLY. includeUninstall/includeSupport are
        // accepted for signature compatibility but the legacy Support Mode /
        // Uninstall / Repair shortcuts are not created (Repair and Uninstall
        // live in Add/Remove Programs).
        return new List<ShortcutDefinition>
        {
            PrimaryDefinition(installRoot),
        };
    }

    // The one canonical launch shortcut, shared by the Start Menu and Desktop.
    // WDAC-safe: target the Microsoft-signed dotnet.exe host directly with the
    // app DLL as its argument (no wscript / launch.vbs — strict corporate WDAC
    // blocks script hosts). The app hides its own console window at startup and
    // the .lnk is created minimized, so no blank terminal flashes. The icon
    // still comes from the apphost EXE (icon reads are allowed; executing it is
    // not). WorkingDirectory is the app bin folder.
    private static ShortcutDefinition PrimaryDefinition(string installRoot, string kind = "start-menu")
    {
        var appExe = AppExePath(installRoot);
        var appDir = Path.GetDirectoryName(appExe)!;
        var workspacePath = Path.Combine(installRoot, ProductConstants.WorkspaceFolderName);
        var appRootPath   = Path.Combine(installRoot, ProductConstants.AppRootFolderName);
        var argTail = $"--workspace \"{workspacePath}\" --approot \"{appRootPath}\"";
        return new ShortcutDefinition(
            Kind: kind, Name: kind == "desktop" ? DesktopName : PrimaryName,
            Target: DotNetLaunch.DotNetExePath(),
            Arguments: DotNetLaunch.AppDllArguments(installRoot, argTail),
            WorkingDirectory: appDir,
            Aumid: ProductConstants.Aumid, IconLocation: appExe + ",0",
            ExcludeFromRecommended: false, OrderHint: 0);
    }

    public static ShortcutDefinition DesktopShortcut(string installRoot)
        => PrimaryDefinition(installRoot, kind: "desktop");

    public static string AppExePath(string installRoot)
        => Path.Combine(installRoot, "App", "bin", "PAX Cookbook.exe");

    public static string SetupExePath(string installRoot)
        => Path.Combine(installRoot, "Setup", "PAXCookbookSetup.exe");
}
