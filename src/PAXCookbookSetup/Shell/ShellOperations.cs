using System;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Shell;

// Bundle of Phase 8 shell-side side effects invoked by Install/Update/
// Repair verbs. Wrapped in a single interface so the verbs depend on
// one abstraction (and tests can inject a no-op variant if needed).
//
// Phase 9 update: real uninstall is implemented (UninstallOperations +
// UninstallVerb), so the production default activates the HKCU
// Add/Remove Programs entry alongside the Phase 8 Start Menu uninstall
// shortcut. The opt-OUT path (`registerUninstall: false`) is preserved
// for tests that exercise the no-ARP-surface code path.
public interface IShellOperations
{
    ShellApplyResult Install(string installRoot, string appVersion, bool createDesktopShortcut);
    ShellApplyResult Reconcile(string installRoot, string appVersion);
    ShellApplyResult Repair(string installRoot, string appVersion);
    ShellStatus Inspect(string installRoot);
}

public sealed record ShellApplyResult(
    int ShortcutsCreated,
    bool ProtocolRegistered,
    bool UninstallRegistered,
    string ProtocolCommand,
    string UninstallString,
    bool FileAssociationsRegistered = false,
    bool AutoStartRegistered = false);

public sealed record ShellStatus(
    int ShortcutsCount,
    bool ProtocolRegistered,
    bool UninstallRegistered,
    bool AppIconPresent,
    bool FileAssociationsRegistered = false,
    bool AutoStartRegistered = false);

public sealed class ShellOperations : IShellOperations
{
    private readonly ShellRegistrar _shortcuts;
    private readonly ProtocolRegistrar _protocol;
    private readonly UninstallRegistrar _uninstall;
    private readonly IShortcutManifestStore _manifestStore;
    private readonly FileAssociationRegistrar? _fileAssoc;
    private readonly AutoStartRegistrar? _autoStart;
    private readonly bool _registerUninstall;

    public ShellOperations(
        ShellRegistrar shortcuts,
        ProtocolRegistrar protocol,
        UninstallRegistrar uninstall,
        IShortcutManifestStore manifestStore,
        FileAssociationRegistrar? fileAssoc = null,
        AutoStartRegistrar? autoStart = null,
        bool registerUninstall = true)
    {
        _shortcuts = shortcuts;
        _protocol = protocol;
        _uninstall = uninstall;
        _manifestStore = manifestStore;
        _fileAssoc = fileAssoc;
        _autoStart = autoStart;
        _registerUninstall = registerUninstall;
    }

    public ShellApplyResult Install(string installRoot, string appVersion, bool createDesktopShortcut)
    {
        var opt = _shortcuts.DefaultOptions(installRoot, appVersion)
                  with { CreateDesktopShortcut = createDesktopShortcut };
        var s = _shortcuts.Install(opt);
        var p = _protocol.Register(installRoot);
        bool fa = RegisterFileAssociations(installRoot);
        bool au = RegisterAutoStart(installRoot);
        return ApplyUninstallAndBuild(installRoot, appVersion, s.ShortcutsCreated, p.CommandLine, fa, au);
    }

    public ShellApplyResult Reconcile(string installRoot, string appVersion)
    {
        // Update preserves whatever desktop choice the user originally had.
        var existing = _manifestStore.TryLoad(installRoot);
        bool desktop = false;
        if (existing is not null)
            foreach (var e in existing.Shortcuts)
                if (string.Equals(e.Kind, "desktop", StringComparison.OrdinalIgnoreCase))
                    desktop = true;

        var opt = _shortcuts.DefaultOptions(installRoot, appVersion)
                  with { CreateDesktopShortcut = desktop };
        var s = _shortcuts.Reconcile(opt);
        var p = _protocol.Register(installRoot);
        bool fa = RegisterFileAssociations(installRoot);
        bool au = RegisterAutoStart(installRoot);
        return ApplyUninstallAndBuild(installRoot, appVersion, s.ShortcutsCreated, p.CommandLine, fa, au);
    }

    public ShellApplyResult Repair(string installRoot, string appVersion)
    {
        var existing = _manifestStore.TryLoad(installRoot);
        bool desktop = false;
        if (existing is not null)
            foreach (var e in existing.Shortcuts)
                if (string.Equals(e.Kind, "desktop", StringComparison.OrdinalIgnoreCase))
                    desktop = true;

        var opt = _shortcuts.DefaultOptions(installRoot, appVersion)
                  with { CreateDesktopShortcut = desktop };
        var s = _shortcuts.Repair(opt);
        var p = _protocol.Register(installRoot);
        bool fa = RegisterFileAssociations(installRoot);
        bool au = RegisterAutoStart(installRoot);
        return ApplyUninstallAndBuild(installRoot, appVersion, s.ShortcutsCreated, p.CommandLine, fa, au);
    }

    private bool RegisterFileAssociations(string installRoot)
    {
        if (_fileAssoc is null) return false;
        _fileAssoc.Register(installRoot);
        return true;
    }

    private bool RegisterAutoStart(string installRoot)
    {
        if (_autoStart is null) return false;
        _autoStart.Register(installRoot);
        return true;
    }

    private ShellApplyResult ApplyUninstallAndBuild(
        string installRoot, string appVersion, int shortcutsCreated, string protocolCommand,
        bool fileAssociationsRegistered, bool autoStartRegistered)
    {
        if (_registerUninstall)
        {
            var u = _uninstall.Register(installRoot, appVersion);
            return new ShellApplyResult(shortcutsCreated, true, true, protocolCommand, u.UninstallString, fileAssociationsRegistered, autoStartRegistered);
        }
        return new ShellApplyResult(shortcutsCreated, true, false, protocolCommand, string.Empty, fileAssociationsRegistered, autoStartRegistered);
    }

    public ShellStatus Inspect(string installRoot)
    {
        var manifest = _manifestStore.TryLoad(installRoot);
        var count = manifest?.Shortcuts?.Count ?? 0;
        bool icon = System.IO.File.Exists(ShortcutCatalog.AppExePath(installRoot));
        return new ShellStatus(
            ShortcutsCount: count,
            ProtocolRegistered: _protocol.IsRegistered(),
            UninstallRegistered: _uninstall.IsRegistered(),
            AppIconPresent: icon,
            FileAssociationsRegistered: _fileAssoc?.IsRegistered() ?? false,
            AutoStartRegistered: _autoStart?.IsRegistered() ?? false);
    }
}
