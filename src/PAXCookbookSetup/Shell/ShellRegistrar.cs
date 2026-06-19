using System;
using System.Collections.Generic;
using System.IO;
using PAXCookbook.Shared;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Shell;

// Orchestrates shortcut creation, reconciliation (update), and repair.
// All side-effects flow through injected abstractions so the Setup
// verbs are testable without touching the real Start Menu.
public sealed class ShellRegistrar
{
    private readonly IShortcutWriter _writer;
    private readonly IShortcutManifestStore _manifestStore;
    private readonly Func<string> _startMenuFolderProvider;
    private readonly Func<string> _desktopFolderProvider;
    private readonly Func<DateTime> _nowUtcProvider;

    public ShellRegistrar(
        IShortcutWriter writer,
        IShortcutManifestStore manifestStore,
        Func<string>? startMenuFolderProvider = null,
        Func<string>? desktopFolderProvider = null,
        Func<DateTime>? nowUtcProvider = null)
    {
        _writer = writer;
        _manifestStore = manifestStore;
        _startMenuFolderProvider = startMenuFolderProvider
            ?? (() => DefaultStartMenuFolder());
        _desktopFolderProvider = desktopFolderProvider
            ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        _nowUtcProvider = nowUtcProvider ?? (() => DateTime.UtcNow);
    }

    // Per-user Start Menu Programs folder. The single product shortcut is
    // written DIRECTLY here (no product subfolder) so it appears as one
    // "PAX Cookbook" entry rather than a folder of shortcuts.
    public static string DefaultStartMenuFolder()
        => Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    public ShellRegistrationOptions DefaultOptions(string installRoot, string appVersion)
        => new ShellRegistrationOptions(
            InstallRoot: installRoot,
            AppVersion: appVersion,
            CreateDesktopShortcut: false,
            IncludeSupportModeShortcut: true,
            // Phase 9: real uninstall is implemented (see
            // UninstallOperations + UninstallVerb), so the Start Menu
            // "Uninstall PAX Cookbook" shortcut is now safe to surface
            // by default. It remains last in the group (OrderHint=100)
            // and is suppressed from the Recommended/New Apps list.
            IncludeUninstallShortcut: true);

    // Creates the full set of shortcuts (fresh install). Any pre-existing
    // .lnk files in the target folders that match our managed names are
    // overwritten. Writes shortcut-manifest.json on completion.
    public ShellRegistrationResult Install(ShellRegistrationOptions opt)
        => Apply(opt, deleteUnmanaged: false);

    // Reconciliation after update: ensure all desired shortcuts exist
    // and match the new target/AUMID/icon. Does not delete user-created
    // shortcuts outside our catalog. Re-writes manifest.
    public ShellRegistrationResult Reconcile(ShellRegistrationOptions opt)
        => Apply(opt, deleteUnmanaged: false);

    // Repair: re-create any missing or broken shortcut from scratch.
    public ShellRegistrationResult Repair(ShellRegistrationOptions opt)
        => Apply(opt, deleteUnmanaged: false);

    private ShellRegistrationResult Apply(ShellRegistrationOptions opt, bool deleteUnmanaged)
    {
        var start = _startMenuFolderProvider();

        // Remove legacy shell artifacts before (re)writing the single shortcut:
        // the old "PAX Cookbook" group folder (which held the Primary/Support/
        // Repair/Uninstall .lnks) and any stale top-level "PAX Cookbook.lnk"
        // from a prior install. Best-effort — a locked leftover never aborts the
        // install; the fresh shortcut is written immediately after.
        CleanupLegacyStartMenu(start);

        var entries = new List<ShortcutEntry>();

        var startDefs = ShortcutCatalog.StartMenuShortcuts(
            opt.InstallRoot,
            includeUninstall: opt.IncludeUninstallShortcut,
            includeSupport:   opt.IncludeSupportModeShortcut);

        foreach (var d in startDefs)
        {
            var r = _writer.Write(start, d);
            entries.Add(ToEntry(d, r));
        }

        if (opt.CreateDesktopShortcut)
        {
            var d = ShortcutCatalog.DesktopShortcut(opt.InstallRoot);
            var r = _writer.Write(_desktopFolderProvider(), d);
            entries.Add(ToEntry(d, r));
        }

        var manifest = new ShortcutManifest
        {
            AppVersion = opt.AppVersion,
            InstallRoot = opt.InstallRoot,
            Shortcuts = entries
        };
        _manifestStore.Save(opt.InstallRoot, manifest);

        return new ShellRegistrationResult(
            ShortcutsCreated: entries.Count,
            StartMenuFolder: start,
            Manifest: manifest);
    }

    // Deletes pre-existing shell artifacts the single-shortcut model replaces:
    // the legacy product group folder and a stale top-level "PAX Cookbook.lnk".
    // The new shortcut is written AFTER this runs.
    private static void CleanupLegacyStartMenu(string startFolder)
    {
        try
        {
            var legacyGroup = Path.Combine(startFolder, ShortcutCatalog.StartMenuGroup);
            if (Directory.Exists(legacyGroup))
                Directory.Delete(legacyGroup, recursive: true);
        }
        catch { /* best-effort */ }

        try
        {
            var staleLnk = Path.Combine(startFolder, ShortcutCatalog.PrimaryName + ".lnk");
            if (File.Exists(staleLnk))
                File.Delete(staleLnk);
        }
        catch { /* best-effort */ }

        // Remove the "PAX Cookbook - Repair" Start Menu entry written by prior
        // versions. Repair is now reachable only from Add/Remove Programs
        // (Modify), never as a user-facing Start Menu shortcut.
        try
        {
            var staleRepairLnk = Path.Combine(startFolder, ShortcutCatalog.RepairShortcutName + ".lnk");
            if (File.Exists(staleRepairLnk))
                File.Delete(staleRepairLnk);
        }
        catch { /* best-effort */ }
    }

    private ShortcutEntry ToEntry(ShortcutDefinition d, ShortcutWriteResult r) => new()
    {
        Kind = d.Kind,
        LnkPath = r.LnkPath,
        Target = d.Target,
        Arguments = d.Arguments,
        WorkingDirectory = d.WorkingDirectory,
        Aumid = d.Aumid,
        IconLocation = d.IconLocation,
        CreatedAtUtc = _nowUtcProvider().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        Sha256 = r.Sha256
    };
}

public sealed record ShellRegistrationOptions(
    string InstallRoot,
    string AppVersion,
    bool CreateDesktopShortcut,
    bool IncludeSupportModeShortcut,
    bool IncludeUninstallShortcut);

public sealed record ShellRegistrationResult(
    int ShortcutsCreated,
    string StartMenuFolder,
    ShortcutManifest Manifest);
