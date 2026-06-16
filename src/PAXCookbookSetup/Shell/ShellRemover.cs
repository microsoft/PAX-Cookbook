using System;
using System.Collections.Generic;
using System.IO;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Shell;

// Removes Phase 8 shell registrations (shortcuts + protocol + ARP).
// Positive-identification policy per uninstall-contract.md §3.1 + §6:
//
//   - A .lnk listed in the shortcut manifest is removed ONLY if its
//     recorded Target still resolves under <installRoot>. The user may
//     have repurposed the .lnk (Phase 8 manifest UN-3) — in that case
//     we leave it alone.
//   - HKCU\Software\Classes\paxcookbook is removed only if the
//     registered shell\open\command points at PAXCookbook.exe under
//     <installRoot>. Otherwise it belongs to a different installation
//     and is left alone.
//   - HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\PAXCookbook
//     is removed only if its UninstallString points at SetupExe under
//     <installRoot>. Otherwise it belongs to a different installation
//     and is left alone.
//
// Returns a detailed result so the caller can log per-category counts
// and report skipped (positive-ID-failed) entries.
public sealed class ShellRemover
{
    private readonly IShortcutWriter _writer;
    private readonly IRegistryWriter _registry;
    private readonly IShortcutManifestStore _manifestStore;

    public ShellRemover(IShortcutWriter writer, IRegistryWriter registry,
                        IShortcutManifestStore manifestStore)
    {
        _writer = writer;
        _registry = registry;
        _manifestStore = manifestStore;
    }

    public ShellRemovalResult Remove(string installRoot)
    {
        var shortcutsRemoved = new List<string>();
        var shortcutsSkipped = new List<string>();
        var registryRemoved = new List<string>();
        var registrySkipped = new List<string>();

        // ---- Shortcuts (manifest is source of truth) ----------------
        var manifest = _manifestStore.TryLoad(installRoot);
        if (manifest is not null)
        {
            var rootFull = Path.GetFullPath(installRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (var e in manifest.Shortcuts)
            {
                if (string.IsNullOrEmpty(e.LnkPath))
                {
                    shortcutsSkipped.Add(e.LnkPath ?? "<unknown>");
                    continue;
                }
                // Positive-ID: target must still resolve under installRoot.
                bool ownedTarget = !string.IsNullOrEmpty(e.Target) &&
                    Path.GetFullPath(e.Target).StartsWith(
                        rootFull, StringComparison.OrdinalIgnoreCase);
                if (!ownedTarget)
                {
                    shortcutsSkipped.Add(e.LnkPath);
                    continue;
                }
                try { _writer.Delete(e.LnkPath); shortcutsRemoved.Add(e.LnkPath); }
                catch { shortcutsSkipped.Add(e.LnkPath); }
            }

            // Delete the manifest itself.
            var manifestPath = _manifestStore.PathFor(installRoot);
            try { if (File.Exists(manifestPath)) File.Delete(manifestPath); }
            catch { /* logged by caller via result */ }
        }

        // ---- paxcookbook:// protocol --------------------------------
        bool protocolRemoved = false;
        bool protocolSkipped = false;
        if (_registry.SubKeyExists(ProtocolRegistrar.RootSubKey))
        {
            var cmd = _registry.GetString(
                ProtocolRegistrar.RootSubKey + @"\shell\open\command", null);
            if (CommandPointsUnderInstallRoot(cmd, installRoot))
            {
                protocolRemoved = _registry.DeleteSubKeyTree(ProtocolRegistrar.RootSubKey);
                if (protocolRemoved) registryRemoved.Add(ProtocolRegistrar.RootSubKey);
            }
            else
            {
                protocolSkipped = true;
                registrySkipped.Add(ProtocolRegistrar.RootSubKey);
            }
        }

        // ---- .paxlite / .pax file associations ----------------------
        // ProgID is removed only if its shell\open\command points under
        // installRoot. The extension key is removed only if it still points
        // at OUR ProgID (it may have been re-associated by the user/another
        // app, in which case we leave it alone).
        int fileAssociationsRemoved = 0;
        int fileAssociationsSkipped = 0;
        foreach (var a in FileAssociationRegistrar.Associations)
        {
            var progKey = FileAssociationRegistrar.ProgIdSubKey(a.ProgId);
            if (_registry.SubKeyExists(progKey))
            {
                var cmd = _registry.GetString(progKey + @"\shell\open\command", null);
                if (CommandPointsUnderInstallRoot(cmd, installRoot))
                {
                    if (_registry.DeleteSubKeyTree(progKey))
                    {
                        registryRemoved.Add(progKey);
                        fileAssociationsRemoved++;
                    }
                }
                else
                {
                    registrySkipped.Add(progKey);
                    fileAssociationsSkipped++;
                }
            }

            var extKey = FileAssociationRegistrar.ExtensionSubKey(a.Extension);
            if (_registry.SubKeyExists(extKey))
            {
                var owner = _registry.GetString(extKey, null);
                if (string.Equals(owner, a.ProgId, StringComparison.OrdinalIgnoreCase))
                {
                    if (_registry.DeleteSubKeyTree(extKey))
                        registryRemoved.Add(extKey);
                }
                else
                {
                    registrySkipped.Add(extKey);
                }
            }
        }

        // ---- HKCU Add/Remove Programs entry -------------------------
        bool arpRemoved = false;
        bool arpSkipped = false;
        if (_registry.SubKeyExists(UninstallRegistrar.RootSubKey))
        {
            var uninstallString = _registry.GetString(
                UninstallRegistrar.RootSubKey, "UninstallString");
            if (CommandPointsUnderInstallRoot(uninstallString, installRoot))
            {
                arpRemoved = _registry.DeleteSubKeyTree(UninstallRegistrar.RootSubKey);
                if (arpRemoved) registryRemoved.Add(UninstallRegistrar.RootSubKey);
            }
            else
            {
                arpSkipped = true;
                registrySkipped.Add(UninstallRegistrar.RootSubKey);
            }
        }

        // ---- HKCU auto-start (Run value) ----------------------------
        // Positive-ID: the Run value is removed only if it points at
        // PAX Cookbook.exe under installRoot. The Run KEY is SHARED with
        // other applications and is NEVER deleted — only OUR named value
        // is removed. A Run value pointing elsewhere belongs to a
        // different installation and is left alone.
        bool autoStartRemoved = false;
        bool autoStartSkipped = false;
        var autoStartPath = AutoStartRegistrar.RootSubKey + @"\" + AutoStartRegistrar.ValueName;
        var runCmd = _registry.GetString(AutoStartRegistrar.RootSubKey, AutoStartRegistrar.ValueName);
        if (!string.IsNullOrEmpty(runCmd))
        {
            if (CommandPointsUnderInstallRoot(runCmd, installRoot))
            {
                autoStartRemoved = _registry.DeleteValue(
                    AutoStartRegistrar.RootSubKey, AutoStartRegistrar.ValueName);
                if (autoStartRemoved) registryRemoved.Add(autoStartPath);
            }
            else
            {
                autoStartSkipped = true;
                registrySkipped.Add(autoStartPath);
            }
        }

        return new ShellRemovalResult(
            ShortcutsRemoved: shortcutsRemoved,
            ShortcutsSkipped: shortcutsSkipped,
            RegistryKeysRemoved: registryRemoved,
            RegistryKeysSkipped: registrySkipped,
            ProtocolRemoved: protocolRemoved,
            ProtocolSkipped: protocolSkipped,
            ArpRemoved: arpRemoved,
            ArpSkipped: arpSkipped,
            ManifestPresentAtStart: manifest is not null,
            FileAssociationsRemoved: fileAssociationsRemoved,
            FileAssociationsSkipped: fileAssociationsSkipped,
            AutoStartRemoved: autoStartRemoved,
            AutoStartSkipped: autoStartSkipped);
    }

    // Extracts the first quoted path from a command line and tests
    // whether it lies under installRoot. A protocol command of shape
    // `"<installRoot>\App\bin\PAX Cookbook.exe" protocol "%1"` and an
    // ARP UninstallString of shape `"<installRoot>\Setup\Setup.exe" uninstall`
    // both have the EXE in the first quoted segment.
    private static bool CommandPointsUnderInstallRoot(string? cmd, string installRoot)
    {
        if (string.IsNullOrEmpty(cmd)) return false;
        var path = cmd!;
        if (path.StartsWith("\""))
        {
            var end = path.IndexOf('"', 1);
            if (end > 1) path = path.Substring(1, end - 1);
        }
        else
        {
            var sp = path.IndexOf(' ');
            if (sp > 0) path = path.Substring(0, sp);
        }
        try
        {
            var pathFull = Path.GetFullPath(path);
            var rootFull = Path.GetFullPath(installRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

public sealed record ShellRemovalResult(
    IReadOnlyList<string> ShortcutsRemoved,
    IReadOnlyList<string> ShortcutsSkipped,
    IReadOnlyList<string> RegistryKeysRemoved,
    IReadOnlyList<string> RegistryKeysSkipped,
    bool ProtocolRemoved,
    bool ProtocolSkipped,
    bool ArpRemoved,
    bool ArpSkipped,
    bool ManifestPresentAtStart,
    int FileAssociationsRemoved = 0,
    int FileAssociationsSkipped = 0,
    bool AutoStartRemoved = false,
    bool AutoStartSkipped = false);
