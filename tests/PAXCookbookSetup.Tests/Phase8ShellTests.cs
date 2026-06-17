using System.IO;
using System.Linq;
using PAXCookbook.Shared;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Verbs;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Phase 8 — Windows shell identity (shortcuts + protocol + ARP + icon).
// All tests use InMemoryShortcutWriter + InMemoryRegistryWriter so no
// real Start Menu or HKCU writes happen.
public class Phase8ShellTests
{
    private const string AppVersion = "1.2.3";
    // Catalog/registry-only tests don't write to disk so a fake install
    // root that never gets created is fine.
    private const string FakeInstallRoot = @"C:\Users\Test\AppData\Local\PAXCookbook";

    private static string FreshInstallRoot()
    {
        var p = Path.Combine(Path.GetTempPath(),
            "paxcookbook-ph8-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private sealed record Harness(
        ShellOperations Ops,
        InMemoryShortcutWriter Shortcuts,
        InMemoryRegistryWriter Registry,
        ShortcutManifestStore ManifestStore,
        string StartFolder,
        string InstallRoot);

    private static Harness Build()
    {
        var sc = new InMemoryShortcutWriter();
        var rg = new InMemoryRegistryWriter();
        var ms = new ShortcutManifestStore();
        var installRoot = FreshInstallRoot();
        var startFolder = Path.Combine(installRoot, "StartMenu");
        var desktopFolder = Path.Combine(installRoot, "Desktop");
        var reg = new ShellRegistrar(sc, ms,
            startMenuFolderProvider: () => startFolder,
            desktopFolderProvider: () => desktopFolder);
        var prot = new ProtocolRegistrar(rg);
        var unin = new UninstallRegistrar(rg);
        var ops = new ShellOperations(reg, prot, unin, ms);
        return new Harness(ops, sc, rg, ms, startFolder, installRoot);
    }

    // ---- 1. ShortcutCatalog emits exactly the single primary shortcut. ----
    [Fact]
    public void Catalog_EmitsSinglePrimaryShortcut()
    {
        var defs = ShortcutCatalog.StartMenuShortcuts(FakeInstallRoot,
            includeUninstall: true, includeSupport: true);
        Assert.Single(defs);
        Assert.Equal(ShortcutCatalog.PrimaryName, defs.First().Name);
    }

    // ---- 2. Recommended-list suppression: primary FALSE, maintenance TRUE. ----
    [Fact]
    public void Catalog_PrimaryNotExcluded_MaintenanceExcluded()
    {
        var defs = ShortcutCatalog.StartMenuShortcuts(FakeInstallRoot, true, true);
        var primary = defs.First(d => d.Name == ShortcutCatalog.PrimaryName);
        Assert.False(primary.ExcludeFromRecommended);
        foreach (var d in defs.Where(d => d.Name != ShortcutCatalog.PrimaryName))
            Assert.True(d.ExcludeFromRecommended, $"{d.Name} should be excluded");
    }

    // ---- 3. AUMID PAXCookbook.App.v1 on every shortcut. ----
    [Fact]
    public void Catalog_AumidIsProductConstant_OnAllShortcuts()
    {
        var defs = ShortcutCatalog.StartMenuShortcuts(FakeInstallRoot, true, true);
        Assert.All(defs, d => Assert.Equal("PAXCookbook.App.v1", d.Aumid));
        Assert.Equal("PAXCookbook.App.v1", ProductConstants.Aumid);
    }

    // ---- 4. Target is the signed dotnet host; args carry the DLL + paths. ----
    [Fact]
    public void Catalog_TargetIsWScript_ArgsCarryVbs()
    {
        var defs = ShortcutCatalog.StartMenuShortcuts(FakeInstallRoot, true, true);
        var primary = defs.First(d => d.Name == ShortcutCatalog.PrimaryName);
        // No console window: the shortcut runs wscript.exe on the hidden
        // launch.vbs (which starts the signed dotnet host hidden), never dotnet
        // directly and never the unsigned apphost.
        Assert.EndsWith("wscript.exe", primary.Target, System.StringComparison.OrdinalIgnoreCase);
        var expectedVbs = Path.Combine(FakeInstallRoot, "App", "bin", "launch.vbs");
        var expectedWs  = Path.Combine(FakeInstallRoot, "Workspace");
        var expectedApp = Path.Combine(FakeInstallRoot, "App");
        Assert.Equal($"\"{expectedVbs}\" --workspace \"{expectedWs}\" --approot \"{expectedApp}\"", primary.Arguments);
        // Icon still comes from the apphost EXE (icon reads are allowed).
        Assert.EndsWith(@"App\bin\PAX Cookbook.exe,0", primary.IconLocation);
        // The desktop shortcut shares the same hidden launch.
        var desktop = ShortcutCatalog.DesktopShortcut(FakeInstallRoot);
        Assert.EndsWith("wscript.exe", desktop.Target, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"\"{expectedVbs}\" --workspace \"{expectedWs}\" --approot \"{expectedApp}\"", desktop.Arguments);
    }

    // ---- 5. ShellOperations.Install creates expected shortcut count. ----
    // Phase 9 update: real uninstall exists, so the default flow now
    // surfaces the uninstall shortcut as well.
    [Fact]
    public void Install_CreatesSingleShortcut_WithoutDesktop()
    {
        var h = Build();
        var r = h.Ops.Install(h.InstallRoot, AppVersion, createDesktopShortcut: false);
        // Single primary shortcut (no folder, no maintenance shortcuts, no desktop).
        Assert.Equal(1, r.ShortcutsCreated);
        Assert.Single(h.Shortcuts.Writes);
        Assert.Equal(ShortcutCatalog.PrimaryName, h.Shortcuts.Writes.First().Def.Name);
    }

    // ---- 6. Desktop shortcut included when requested. ----
    [Fact]
    public void Install_WithDesktop_AddsDesktopShortcut()
    {
        var h = Build();
        var r = h.Ops.Install(h.InstallRoot, AppVersion, createDesktopShortcut: true);
        // Primary (start-menu) + desktop = 2.
        Assert.Equal(2, r.ShortcutsCreated);
        Assert.Contains(h.Shortcuts.Writes, w => w.Def.Kind == "desktop");
    }

    // ---- 7. Manifest persisted with required schema fields. ----
    [Fact]
    public void Install_WritesShortcutManifest_WithSchemaFields()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, false);
        var m = h.ManifestStore.TryLoad(h.InstallRoot);
        Assert.NotNull(m);
        Assert.Equal("PAXCookbook", m!.Product);
        Assert.Equal(1, m.ShortcutManifestSchemaVersion);
        Assert.Equal(AppVersion, m.AppVersion);
        Assert.Equal("PAXCookbook.App.v1", m.Aumid);
        Assert.Equal(h.InstallRoot, m.InstallRoot);
        Assert.Single(m.Shortcuts);
    }

    // ---- 8. Every manifest entry carries sha256 + aumid. ----
    [Fact]
    public void Install_ManifestEntries_CarrySha256AndAumid()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, true);
        var m = h.ManifestStore.TryLoad(h.InstallRoot)!;
        Assert.All(m.Shortcuts, e =>
        {
            Assert.False(string.IsNullOrEmpty(e.Sha256));
            Assert.Equal(64, e.Sha256.Length);
            Assert.Equal("PAXCookbook.App.v1", e.Aumid);
            Assert.False(string.IsNullOrEmpty(e.LnkPath));
            Assert.False(string.IsNullOrEmpty(e.CreatedAtUtc));
        });
    }

    // ---- 9. Reconcile preserves desktop choice from existing manifest. ----
    [Fact]
    public void Reconcile_PreservesDesktopChoice_FromExistingManifest()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, createDesktopShortcut: true);
        h.Shortcuts.Writes.Clear();
        h.Ops.Reconcile(h.InstallRoot, "1.2.4");
        Assert.Contains(h.Shortcuts.Writes, w => w.Def.Kind == "desktop");
    }

    // ---- 10. Reconcile WITHOUT prior desktop does NOT add desktop. ----
    [Fact]
    public void Reconcile_NoPriorDesktop_DoesNotAddDesktop()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, createDesktopShortcut: false);
        h.Shortcuts.Writes.Clear();
        h.Ops.Reconcile(h.InstallRoot, "1.2.4");
        Assert.DoesNotContain(h.Shortcuts.Writes, w => w.Def.Kind == "desktop");
    }

    // ---- 11. Repair re-creates all shortcuts. ----
    [Fact]
    public void Repair_ReCreatesAllShortcuts()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, false);
        h.Shortcuts.Writes.Clear();
        var r = h.Ops.Repair(h.InstallRoot, AppVersion);
        Assert.Single(h.Shortcuts.Writes);
        Assert.Equal(1, r.ShortcutsCreated);
    }

    // ---- 12. ProtocolRegistrar.Register sets all 4 expected values. ----
    [Fact]
    public void Protocol_Register_WritesAllValues()
    {
        var rg = new InMemoryRegistryWriter();
        var p = new ProtocolRegistrar(rg);
        var r = p.Register(FakeInstallRoot);
        Assert.Equal("URL:PAX Cookbook Protocol",
            rg.GetString(@"Software\Classes\paxcookbook", null));
        Assert.Equal("",
            rg.GetString(@"Software\Classes\paxcookbook", "URL Protocol"));
        Assert.EndsWith(@"App\bin\PAX Cookbook.exe,0",
            rg.GetString(@"Software\Classes\paxcookbook\DefaultIcon", null));
        var cmd = rg.GetString(@"Software\Classes\paxcookbook\shell\open\command", null);
        Assert.NotNull(cmd);
        Assert.EndsWith("\" protocol \"%1\"", cmd);
        Assert.Equal("paxcookbook", r.Scheme);
    }

    // ---- 13. ProtocolRegistrar.IsRegistered toggles after Register. ----
    [Fact]
    public void Protocol_IsRegistered_TogglesAfterRegister()
    {
        var rg = new InMemoryRegistryWriter();
        var p = new ProtocolRegistrar(rg);
        Assert.False(p.IsRegistered());
        p.Register(FakeInstallRoot);
        Assert.True(p.IsRegistered());
    }

    // ---- 14. UninstallRegistrar.Register writes DisplayName=PAX Cookbook. ----
    [Fact]
    public void Arp_Register_DisplayNameAndPublisher()
    {
        var rg = new InMemoryRegistryWriter();
        var u = new UninstallRegistrar(rg);
        u.Register(FakeInstallRoot, AppVersion);
        Assert.Equal("PAX Cookbook", rg.GetString(UninstallRegistrar.RootSubKey, "DisplayName"));
        Assert.Equal("Microsoft",    rg.GetString(UninstallRegistrar.RootSubKey, "Publisher"));
        Assert.Equal(AppVersion,     rg.GetString(UninstallRegistrar.RootSubKey, "DisplayVersion"));
        Assert.Equal(FakeInstallRoot, rg.GetString(UninstallRegistrar.RootSubKey, "InstallLocation"));
    }

    // ---- 15. UninstallRegistrar.Register UninstallString shape. ----
    [Fact]
    public void Arp_Register_UninstallStringShape()
    {
        var rg = new InMemoryRegistryWriter();
        var u = new UninstallRegistrar(rg);
        var r = u.Register(FakeInstallRoot, AppVersion);
        // WDAC-safe: dotnet.exe runs the framework-dependent Setup DLL.
        Assert.EndsWith(@"Setup\PAXCookbookSetup.dll"" uninstall", r.UninstallString);
        Assert.StartsWith("\"", r.UninstallString);
    }

    // ---- 16. UninstallRegistrar.Register NoModify=1, NoRepair=0. ----
    [Fact]
    public void Arp_Register_NoModifyAndNoRepair()
    {
        var rg = new InMemoryRegistryWriter();
        var u = new UninstallRegistrar(rg);
        u.Register(FakeInstallRoot, AppVersion);
        Assert.Equal(1, (int)rg.Store[UninstallRegistrar.RootSubKey]["NoModify"]);
        Assert.Equal(0, (int)rg.Store[UninstallRegistrar.RootSubKey]["NoRepair"]);
    }

    // ---- 17. UninstallRegistrar.IsRegistered toggles. ----
    [Fact]
    public void Arp_IsRegistered_TogglesAfterRegister()
    {
        var rg = new InMemoryRegistryWriter();
        var u = new UninstallRegistrar(rg);
        Assert.False(u.IsRegistered());
        u.Register(FakeInstallRoot, AppVersion);
        Assert.True(u.IsRegistered());
    }

    // ---- 18. ShellOperations.Install: protocol AND ARP register by
    //          default (Phase 9: real uninstall is implemented).
    [Fact]
    public void Operations_Install_RegistersProtocol_AndArp()
    {
        var h = Build();
        var r = h.Ops.Install(h.InstallRoot, AppVersion, false);
        Assert.True(r.ProtocolRegistered);
        Assert.True(r.UninstallRegistered);
        Assert.False(string.IsNullOrEmpty(r.UninstallString));
        Assert.True(r.ShortcutsCreated >= 1);
    }

    // ---- 19. ShellOperations.Inspect reflects post-install state. ----
    [Fact]
    public void Operations_Inspect_AfterInstall_ReflectsState()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, false);
        var s = h.Ops.Inspect(h.InstallRoot);
        Assert.True(s.ProtocolRegistered);
        Assert.True(s.UninstallRegistered);
        Assert.Equal(1, s.ShortcutsCount);
    }

    // ---- 20. The single primary shortcut is NOT excluded from Recommended. ----
    [Fact]
    public void Writer_PrimaryNotExcludedFromRecommended()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, false);
        var primary = h.Shortcuts.Writes.First(w => w.Def.Name == ShortcutCatalog.PrimaryName);
        Assert.False(primary.Result.ExcludeAttempted);
    }

    // ---- 21. Force exclude failure does not crash install. ----
    [Fact]
    public void Writer_ExcludeFailure_DoesNotCrash()
    {
        var h = Build();
        h.Shortcuts.ForceExcludeFailure = true;
        var r = h.Ops.Install(h.InstallRoot, AppVersion, false);
        // The single primary shortcut is not excluded, so a forced exclude
        // failure is a no-op; the install still succeeds with one shortcut.
        Assert.Equal(1, r.ShortcutsCreated);
    }

    // ---- 22. .lnk paths land under provided Start Menu folder. ----
    [Fact]
    public void Install_LnkPaths_AreUnderStartMenuFolder()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, false);
        foreach (var w in h.Shortcuts.Writes.Where(w => w.Def.Kind == "start-menu"))
            Assert.StartsWith(h.StartFolder, w.Result.LnkPath);
    }

    // ---- 23. Icon location uses "path,N" form. ----
    [Fact]
    public void Catalog_IconLocation_IsPathCommaIndex()
    {
        var defs = ShortcutCatalog.StartMenuShortcuts(FakeInstallRoot, true, true);
        foreach (var d in defs)
            Assert.Matches(@"\.exe,\d+$", d.IconLocation);
    }

    // ---- 24. UninstallVerb is no longer the placeholder. With no
    //          install present in the temp install root, the standard
    //          uninstall path still returns Ok (nothing to remove is
    //          not a failure per uninstall-contract §6 broken-install).
    [Fact]
    public void UninstallVerb_NoLongerReturnsPlaceholderError()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "paxcookbook-unin-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var logsDir = Path.Combine(dir, "Logs");
        Directory.CreateDirectory(logsDir);
        using var log = new SetupLogger(logsDir);
        using var sw = new StringWriter();
        var parsed = ArgParser.Parse(new[] { "uninstall" });
        var rc = UninstallVerb.Run(dir, parsed, log, sw,
            operations: BuildInMemoryUninstallOperations());
        Assert.Equal(PAXCookbook.Shared.ExitCodes.SetupExitCodes.Ok, rc);
        // Output now reports the structured uninstall summary, not the
        // "not implemented yet" placeholder message.
        Assert.DoesNotContain("not implemented yet", sw.ToString());
        Assert.Contains("uninstall: mode=standard", sw.ToString());
    }

    // ---- 25. ArgParser knows the uninstall verb. ----
    [Fact]
    public void ArgParser_RecognizesUninstallVerb()
    {
        var p = ArgParser.Parse(new[] { "uninstall" });
        Assert.Equal("uninstall", p.Verb);
        Assert.Empty(p.Errors);
    }

    // ---- 26. Manifest entries kind is start-menu or desktop only. ----
    [Fact]
    public void Manifest_ShortcutKinds_AreStartMenuOrDesktop()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, true);
        var m = h.ManifestStore.TryLoad(h.InstallRoot)!;
        Assert.All(m.Shortcuts, e =>
            Assert.True(e.Kind == "start-menu" || e.Kind == "desktop", $"unexpected kind {e.Kind}"));
    }

    // ============================================================
    // Phase 9 update: real uninstall exists, so the default install/
    // update/repair flow MAY now surface a working uninstall shortcut
    // + ARP entry. The opt-OUT path (registerUninstall: false) is
    // preserved so we can prove the no-surface code still works.
    // ============================================================

    // ---- 27. Catalog defaults from ShellRegistrar include uninstall. ----
    [Fact]
    public void Registrar_DefaultOptions_IncludesUninstallShortcut()
    {
        var sc = new InMemoryShortcutWriter();
        var ms = new ShortcutManifestStore();
        var reg = new ShellRegistrar(sc, ms,
            startMenuFolderProvider: () => Path.Combine(FakeInstallRoot, "StartMenu"));
        var opt = reg.DefaultOptions(FakeInstallRoot, AppVersion);
        Assert.True(opt.IncludeUninstallShortcut);
    }

    // ---- 28. Default flow surfaces exactly the single primary shortcut. ----
    [Fact]
    public void DefaultShellFlow_SurfacesSinglePrimaryShortcut()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, false);
        var startMenu = h.Shortcuts.Writes.Where(w => w.Def.Kind == "start-menu").ToList();
        Assert.Single(startMenu);
        Assert.Equal(ShortcutCatalog.PrimaryName, startMenu[0].Def.Name);
    }

    // ---- 29. Default flow registers ARP and points UninstallString at
    //          the installed Setup uninstall verb.
    [Fact]
    public void DefaultShellFlow_RegistersArp_WithUninstallStringPointingAtInstalledSetup()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, false);
        Assert.True(h.Registry.SubKeyExists(UninstallRegistrar.RootSubKey));
        var us = h.Registry.GetString(UninstallRegistrar.RootSubKey, "UninstallString")!;
        Assert.EndsWith("PAXCookbookSetup.dll\" uninstall", us);
        Assert.Contains(h.InstallRoot, us);
    }

    // ---- 30. Manifest produced by default flow contains the launcher entry. ----
    [Fact]
    public void Manifest_DefaultFlow_ContainsPrimaryLauncherEntry()
    {
        var h = Build();
        h.Ops.Install(h.InstallRoot, AppVersion, false);
        var m = h.ManifestStore.TryLoad(h.InstallRoot)!;
        Assert.Contains(m.Shortcuts,
            e => e.Target.EndsWith("wscript.exe", System.StringComparison.OrdinalIgnoreCase)
                 && e.Arguments.Contains("launch.vbs")
                 && e.Arguments.Contains("--workspace"));
    }

    // ---- 31. Catalog ignores include flags and always emits one shortcut. ----
    [Fact]
    public void Catalog_IgnoresIncludeFlags_AlwaysSinglePrimary()
    {
        var defs = ShortcutCatalog.StartMenuShortcuts(FakeInstallRoot,
            includeUninstall: true, includeSupport: true);
        Assert.Single(defs);
        Assert.Equal(ShortcutCatalog.PrimaryName, defs.First().Name);
        Assert.DoesNotContain(defs, d => d.Name == ShortcutCatalog.UninstallName);
    }

    // ---- 32. ShellOperations can opt OUT of ARP registration
    //          (preserves the no-surface code path for callers that
    //          want to install without ARP).
    [Fact]
    public void Operations_OptOut_SkipsArpRegistration()
    {
        var sc = new InMemoryShortcutWriter();
        var rg = new InMemoryRegistryWriter();
        var ms = new ShortcutManifestStore();
        var installRoot = FreshInstallRoot();
        var startFolder = Path.Combine(installRoot, "StartMenu");
        var reg = new ShellRegistrar(sc, ms,
            startMenuFolderProvider: () => startFolder);
        var prot = new ProtocolRegistrar(rg);
        var unin = new UninstallRegistrar(rg);
        var ops = new ShellOperations(reg, prot, unin, ms, registerUninstall: false);

        var r = ops.Install(installRoot, AppVersion, false);
        Assert.False(r.UninstallRegistered);
        Assert.Equal(string.Empty, r.UninstallString);
        Assert.False(rg.SubKeyExists(UninstallRegistrar.RootSubKey));
    }

    // ---- 33. Install removes a legacy "PAX Cookbook" group folder + stale
    //          top-level lnk before writing the single shortcut (FIX 3). ----
    [Fact]
    public void Install_RemovesLegacyStartMenuGroupFolderAndStaleLnk()
    {
        var h = Build();
        // Simulate a prior version's 4-shortcut group folder and a stale
        // top-level "PAX Cookbook.lnk".
        var legacyFolder = Path.Combine(h.StartFolder, ShortcutCatalog.StartMenuGroup);
        Directory.CreateDirectory(legacyFolder);
        File.WriteAllText(Path.Combine(legacyFolder, "PAX Cookbook.lnk"), "old");
        File.WriteAllText(Path.Combine(legacyFolder, "Uninstall PAX Cookbook.lnk"), "old");
        var staleLnk = Path.Combine(h.StartFolder, ShortcutCatalog.PrimaryName + ".lnk");
        File.WriteAllText(staleLnk, "stale");

        h.Ops.Install(h.InstallRoot, AppVersion, createDesktopShortcut: false);

        // Legacy folder gone; stale top-level lnk deleted (the in-memory writer
        // does not re-create a real file); exactly one shortcut written.
        Assert.False(Directory.Exists(legacyFolder));
        Assert.False(File.Exists(staleLnk));
        Assert.Single(h.Shortcuts.Writes);
    }

    // Helper for test 24: builds an UninstallOperations that uses
    // in-memory shortcut/registry fakes and a recording filesystem
    // remover so the test never touches real disk/HKCU.
    private static PAXCookbookSetup.Uninstall.UninstallOperations BuildInMemoryUninstallOperations()
    {
        var sw = new InMemoryShortcutWriter();
        var rg = new InMemoryRegistryWriter();
        var ms = new ShortcutManifestStore();
        var shellRemover = new ShellRemover(sw, rg, ms);
        return new PAXCookbookSetup.Uninstall.UninstallOperations(
            new PAXCookbookSetup.Uninstall.RecordingAppStopper(),
            new PAXCookbookSetup.Uninstall.RecordingFileSystemRemover(),
            shellRemover,
            new PAXCookbookSetup.Uninstall.NullTaskbarPinCleaner());
    }
}
