using System;
using System.Collections.Generic;
using System.IO;
using PAXCookbook.Shared;

namespace PAXCookbookSetup.Shell;

// Source-of-truth list of shortcut definitions Setup may create on the
// current user's system. The list is deliberately small and explicit
// per Phase 8 contract:
//
//   1. Primary       "PAX Cookbook"               -> PAX Cookbook.exe --workspace <ws> --approot <app>
//   2. Desktop       "PAX Cookbook"               -> PAX Cookbook.exe --workspace <ws> --approot <app>
//   3. Support Mode  "PAX Cookbook Support Mode"  -> PAX Cookbook.exe support
//   4. Repair        "Repair PAX Cookbook"        -> PAXCookbookSetup.exe repair
//   5. Uninstall     "Uninstall PAX Cookbook"     -> PAXCookbookSetup.exe uninstall
//
// Ordering:
//   - Primary first (OrderHint = 0).
//   - Uninstall last  (OrderHint = 100).
//   - Other maintenance shortcuts in between (10..40).
//
// Recommended suppression (System.AppUserModel.ExcludeFromShowInNewInstall):
//   - true for all maintenance shortcuts.
//   - false for the primary "PAX Cookbook" shortcut.
public static class ShortcutCatalog
{
    public const string PrimaryName       = "PAX Cookbook";
    public const string DesktopName       = "PAX Cookbook";
    public const string SupportModeName   = "PAX Cookbook Support Mode";
    public const string RepairName        = "Repair PAX Cookbook";
    public const string UninstallName     = "Uninstall PAX Cookbook";

    public const string StartMenuGroup    = "PAX Cookbook";

    public static IReadOnlyList<ShortcutDefinition> StartMenuShortcuts(string installRoot, bool includeUninstall, bool includeSupport)
    {
        var appExe = AppExePath(installRoot);
        var setupExe = SetupExePath(installRoot);
        var appDir = Path.GetDirectoryName(appExe)!;
        var setupDir = Path.GetDirectoryName(setupExe)!;
        var aumid = ProductConstants.Aumid;
        // The primary launch shortcut hands the native host the canonical
        // production workspace and app root explicitly — byte-identical to the
        // PowerShell installer's launch args — so the host never relies on its
        // own default and the window keys on the production workspace. The EXE
        // has no "open" verb dispatcher (it reads --workspace/--approot by name,
        // order-independent), so the previously-inert "open" verb is dropped.
        var workspacePath = Path.Combine(installRoot, ProductConstants.WorkspaceFolderName);
        var appRootPath   = Path.Combine(installRoot, ProductConstants.AppRootFolderName);
        var launchArgs    = $"--workspace \"{workspacePath}\" --approot \"{appRootPath}\"";
        // All shortcuts display the primary brand icon (PAX Cookbook), even
        // when the target is the Setup EXE for maintenance verbs.
        var primaryIcon = appExe + ",0";

        var list = new List<ShortcutDefinition>
        {
            // 1. Primary — NOT suppressed from Recommended.
            new(Kind: "start-menu", Name: PrimaryName, Target: appExe,
                Arguments: launchArgs, WorkingDirectory: appDir,
                Aumid: aumid, IconLocation: primaryIcon,
                ExcludeFromRecommended: false, OrderHint: 0),

            // 4. Repair — maintenance, suppressed.
            new(Kind: "start-menu", Name: RepairName, Target: setupExe,
                Arguments: "repair", WorkingDirectory: setupDir,
                Aumid: aumid, IconLocation: primaryIcon,
                ExcludeFromRecommended: true, OrderHint: 30),
        };

        if (includeSupport)
        {
            list.Insert(1, new ShortcutDefinition(
                Kind: "start-menu", Name: SupportModeName, Target: appExe,
                Arguments: "support", WorkingDirectory: appDir,
                Aumid: aumid, IconLocation: primaryIcon,
                ExcludeFromRecommended: true, OrderHint: 10));
        }

        if (includeUninstall)
        {
            // 5. Uninstall — maintenance, suppressed, LAST in folder.
            list.Add(new ShortcutDefinition(
                Kind: "start-menu", Name: UninstallName, Target: setupExe,
                Arguments: "uninstall", WorkingDirectory: setupDir,
                Aumid: aumid, IconLocation: primaryIcon,
                ExcludeFromRecommended: true, OrderHint: 100));
        }

        list.Sort((a, b) => a.OrderHint.CompareTo(b.OrderHint));
        return list;
    }

    public static ShortcutDefinition DesktopShortcut(string installRoot)
    {
        var appExe = AppExePath(installRoot);
        var appDir = Path.GetDirectoryName(appExe)!;
        // Same explicit production launch args as the Start-Menu primary.
        var workspacePath = Path.Combine(installRoot, ProductConstants.WorkspaceFolderName);
        var appRootPath   = Path.Combine(installRoot, ProductConstants.AppRootFolderName);
        var launchArgs    = $"--workspace \"{workspacePath}\" --approot \"{appRootPath}\"";
        return new ShortcutDefinition(
            Kind: "desktop", Name: DesktopName, Target: appExe,
            Arguments: launchArgs, WorkingDirectory: appDir,
            Aumid: ProductConstants.Aumid, IconLocation: appExe + ",0",
            ExcludeFromRecommended: false, OrderHint: 0);
    }

    public static string AppExePath(string installRoot)
        => Path.Combine(installRoot, "App", "bin", "PAX Cookbook.exe");

    public static string SetupExePath(string installRoot)
        => Path.Combine(installRoot, "Setup", "PAXCookbookSetup.exe");
}
