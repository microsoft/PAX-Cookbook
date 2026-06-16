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

    // Per-user Start Menu folder for the product group.
    public static string DefaultStartMenuFolder()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        return Path.Combine(programs, ShortcutCatalog.StartMenuGroup);
    }

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
