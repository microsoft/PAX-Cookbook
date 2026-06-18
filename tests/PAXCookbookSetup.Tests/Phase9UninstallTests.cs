using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PAXCookbook.Shared;
using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Uninstall;
using PAXCookbookSetup.Verbs;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Phase 9 — real uninstall + shell removal + taskbar pin decision.
// All tests use temp install roots and in-memory shell/registry fakes;
// no real %LOCALAPPDATA%\PAXCookbook, no real HKCU, no real Start Menu.
public class Phase9UninstallTests
{
    private const string AppVersion = "1.2.3";

    private static string FreshInstallRoot()
    {
        var p = Path.Combine(Path.GetTempPath(),
            "paxcookbook-ph9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    // Builds a populated install root and an in-memory shell state so
    // uninstall has something to remove. Returns the harness for asserts.
    private sealed record Harness(
        string InstallRoot,
        UninstallOperations Ops,
        InMemoryShortcutWriter Shortcuts,
        InMemoryRegistryWriter Registry,
        ShortcutManifestStore ManifestStore,
        RecordingFileSystemRemover Files,
        RecordingAppStopper Stopper,
        ITaskbarPinCleaner Taskbar,
        string StartFolder,
        string DesktopFolder,
        string WorkspaceFolder);

    private static Harness BuildPopulated(
        bool createDesktop = false,
        string? workspacePath = null,
        ITaskbarPinCleaner? taskbar = null)
    {
        var installRoot = FreshInstallRoot();
        // Lay down the directories that standard uninstall removes.
        foreach (var sub in new[] { @"App\bin", "Setup", "PreviousVersions",
                                    "WebView2Data", "Runtime",
                                    @"Logs\Setup", @"Logs\App" })
            Directory.CreateDirectory(Path.Combine(installRoot, sub));
        // Drop sentinel files so the recorder/remover can observe them.
        File.WriteAllText(Path.Combine(installRoot, @"App\bin", "PAXCookbook.exe"), "fake");
        File.WriteAllText(Path.Combine(installRoot, "Setup", "PAXCookbookSetup.exe"), "fake");
        File.WriteAllText(Path.Combine(installRoot, "WebView2Data", "data.bin"), "fake");
        File.WriteAllText(Path.Combine(installRoot, "Runtime", "extra.bin"), "fake");
        File.WriteAllText(Path.Combine(installRoot, @"Logs\Setup", "setup.log"), "fake");

        // Workspace folder lives OUTSIDE the install root (typical).
        var ws = workspacePath ?? Path.Combine(Path.GetTempPath(),
            "paxcookbook-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, "recipe.txt"), "user recipe");

        // Phase 8 shell state.
        var sw = new InMemoryShortcutWriter();
        var rg = new InMemoryRegistryWriter();
        var ms = new ShortcutManifestStore();
        var startFolder = Path.Combine(installRoot, "StartMenu");
        var desktopFolder = Path.Combine(installRoot, "Desktop");
        var reg = new ShellRegistrar(sw, ms,
            startMenuFolderProvider: () => startFolder,
            desktopFolderProvider: () => desktopFolder);
        var prot = new ProtocolRegistrar(rg);
        var unin = new UninstallRegistrar(rg);
        var ops = new ShellOperations(reg, prot, unin, ms); // default = registerUninstall: true
        ops.Install(installRoot, AppVersion, createDesktop);

        // Persist an install-state so Full mode can locate Workspace.
        var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        InstallStateStore.Save(installRoot, new InstallState
        {
            AppVersion = AppVersion,
            SetupVersion = AppVersion,
            AppExeVersion = AppVersion,
            InstalledAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            InstallRoot = installRoot,
            AppRoot = Path.Combine(installRoot, "App"),
            BinRoot = Path.Combine(installRoot, "App", "bin"),
            AppExe = Path.Combine(installRoot, @"App\bin", "PAXCookbook.exe"),
            WorkspaceFolderPath = ws,
            WebView2RuntimeStatus = new WebView2RuntimeStatus { DetectedAtUtc = nowUtc },
            WebView2UserDataFolder = Path.Combine(installRoot, "WebView2Data"),
            LastOperation = new LastOperation { Kind = "install", Status = "ok",
                                                 At = nowUtc, ExitCode = 0 }
        });

        var shellRemover = new ShellRemover(sw, rg, ms);
        var files = new RecordingFileSystemRemover { PassThrough = true };
        var stopper = new RecordingAppStopper();
        var tb = taskbar ?? new NullTaskbarPinCleaner();
        var uops = new UninstallOperations(stopper, files, shellRemover, tb);

        return new Harness(installRoot, uops, sw, rg, ms, files, stopper, tb,
                            startFolder, desktopFolder, ws);
    }

    // ---- 1. Standard uninstall removes App files. ----
    [Fact]
    public void Standard_RemovesAppFiles()
    {
        var h = BuildPopulated();
        h.Ops.RunStandard(h.InstallRoot);
        Assert.Contains(h.Files.DirsRemoved,
            d => d.EndsWith(@"App", StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(Path.Combine(h.InstallRoot, "App")));
    }

    // ---- 2. Standard uninstall removes Setup files. ----
    [Fact]
    public void Standard_RemovesSetupFiles()
    {
        var h = BuildPopulated();
        h.Ops.RunStandard(h.InstallRoot);
        Assert.False(Directory.Exists(Path.Combine(h.InstallRoot, "Setup")));
    }

    // ---- 3. Standard uninstall removes WebView2Data. ----
    [Fact]
    public void Standard_RemovesWebView2Data()
    {
        var h = BuildPopulated();
        h.Ops.RunStandard(h.InstallRoot);
        Assert.False(Directory.Exists(Path.Combine(h.InstallRoot, "WebView2Data")));
    }

    // ---- 4. Standard uninstall removes Runtime sidecar. ----
    [Fact]
    public void Standard_RemovesRuntimeSidecar()
    {
        var h = BuildPopulated();
        h.Ops.RunStandard(h.InstallRoot);
        Assert.False(Directory.Exists(Path.Combine(h.InstallRoot, "Runtime")));
    }

    // ---- 5. Standard uninstall removes Start Menu shortcuts via writer. ----
    [Fact]
    public void Standard_RemovesStartMenuShortcuts()
    {
        var h = BuildPopulated();
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.True(r.ShortcutsRemoved >= 1); // single primary shortcut
        Assert.NotEmpty(h.Shortcuts.Deleted);
        Assert.All(h.Shortcuts.Deleted,
            p => Assert.StartsWith(h.StartFolder, p));
    }

    // ---- 6. Standard uninstall removes Desktop shortcut only when owned. ----
    [Fact]
    public void Standard_RemovesDesktopShortcut_WhenOwned()
    {
        var h = BuildPopulated(createDesktop: true);
        h.Ops.RunStandard(h.InstallRoot);
        Assert.Contains(h.Shortcuts.Deleted,
            p => p.StartsWith(h.DesktopFolder, StringComparison.OrdinalIgnoreCase));
    }

    // ---- 7. Standard uninstall removes protocol registration. ----
    [Fact]
    public void Standard_RemovesProtocolRegistration()
    {
        var h = BuildPopulated();
        Assert.True(h.Registry.SubKeyExists(ProtocolRegistrar.RootSubKey));
        h.Ops.RunStandard(h.InstallRoot);
        Assert.False(h.Registry.SubKeyExists(ProtocolRegistrar.RootSubKey));
    }

    // ---- 8. Standard uninstall removes ARP registration. ----
    [Fact]
    public void Standard_RemovesArpRegistration()
    {
        var h = BuildPopulated();
        Assert.True(h.Registry.SubKeyExists(UninstallRegistrar.RootSubKey));
        h.Ops.RunStandard(h.InstallRoot);
        Assert.False(h.Registry.SubKeyExists(UninstallRegistrar.RootSubKey));
    }

    // ---- 9. Standard uninstall preserves Workspace. ----
    [Fact]
    public void Standard_PreservesWorkspace()
    {
        var h = BuildPopulated();
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.True(r.WorkspacePreserved);
        Assert.False(r.WorkspaceRemoved);
        Assert.True(Directory.Exists(h.WorkspaceFolder));
        Assert.True(File.Exists(Path.Combine(h.WorkspaceFolder, "recipe.txt")));
    }

    // ---- 10. Standard uninstall preserves Logs. ----
    [Fact]
    public void Standard_PreservesLogs()
    {
        var h = BuildPopulated();
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.True(r.LogsPreserved);
        Assert.True(Directory.Exists(Path.Combine(h.InstallRoot, "Logs")));
    }

    // ---- 11. Standard uninstall preserves an external export folder. ----
    [Fact]
    public void Standard_PreservesExternalExportFolder()
    {
        var external = Path.Combine(Path.GetTempPath(),
            "paxcookbook-extexp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "purview.csv"), "data");
        try
        {
            var h = BuildPopulated();
            h.Ops.RunStandard(h.InstallRoot);
            // External path is not iterated by the orchestrator and must
            // be untouched.
            Assert.True(File.Exists(Path.Combine(external, "purview.csv")));
        }
        finally
        {
            try { Directory.Delete(external, true); } catch { }
        }
    }

    // ---- 12. Full uninstall requires both flags (--remove-user-data alone fails). ----
    [Fact]
    public void Full_RemoveUserDataWithoutConfirm_FailsWithUsageError()
    {
        var h = BuildPopulated();
        using var log = new SetupLogger(Path.Combine(h.InstallRoot, "Logs", "Setup"));
        using var sw = new StringWriter();
        var parsed = ArgParser.Parse(new[] { "uninstall", "--remove-user-data" });
        var rc = UninstallVerb.Run(h.InstallRoot, parsed, log, sw, h.Ops);
        Assert.Equal(SetupExitCodes.UsageError, rc);
        Assert.Contains("requires --confirm-remove-user-data", sw.ToString());
        // Workspace still present.
        Assert.True(Directory.Exists(h.WorkspaceFolder));
    }

    // ---- 13. --confirm-remove-user-data alone also fails (cannot be misused). ----
    [Fact]
    public void Full_ConfirmWithoutFlag_FailsWithUsageError()
    {
        var h = BuildPopulated();
        using var log = new SetupLogger(Path.Combine(h.InstallRoot, "Logs", "Setup"));
        using var sw = new StringWriter();
        var parsed = ArgParser.Parse(new[] { "uninstall", "--confirm-remove-user-data" });
        var rc = UninstallVerb.Run(h.InstallRoot, parsed, log, sw, h.Ops);
        Assert.Equal(SetupExitCodes.UsageError, rc);
    }

    // ---- 14. Full uninstall (both flags) removes Workspace. ----
    [Fact]
    public void Full_BothFlags_RemovesWorkspace()
    {
        var h = BuildPopulated();
        using var log = new SetupLogger(Path.Combine(h.InstallRoot, "Logs", "Setup"));
        using var sw = new StringWriter();
        var parsed = ArgParser.Parse(new[]
        {
            "uninstall", "--remove-user-data", "--confirm-remove-user-data"
        });
        var rc = UninstallVerb.Run(h.InstallRoot, parsed, log, sw, h.Ops);
        Assert.Equal(SetupExitCodes.Ok, rc);
        Assert.Contains("mode=full", sw.ToString());
        Assert.False(Directory.Exists(h.WorkspaceFolder));
    }

    // ---- 15. Full uninstall preserves external export folder. ----
    [Fact]
    public void Full_PreservesExternalExportFolder()
    {
        var external = Path.Combine(Path.GetTempPath(),
            "paxcookbook-extexp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "entra.json"), "data");
        try
        {
            var h = BuildPopulated();
            h.Ops.RunFull(h.InstallRoot);
            Assert.True(File.Exists(Path.Combine(external, "entra.json")));
        }
        finally
        {
            try { Directory.Delete(external, true); } catch { }
        }
    }

    // ---- 16. App stopper is invoked before deletion. ----
    [Fact]
    public void Standard_InvokesAppStopper_BeforeDelete()
    {
        var h = BuildPopulated();
        h.Ops.RunStandard(h.InstallRoot);
        Assert.Single(h.Stopper.Calls);
        Assert.Equal(h.InstallRoot, h.Stopper.Calls[0].InstallRoot);
    }

    // ---- 17. Default policy: stopper TIMEOUT aborts uninstall before deletion. ----
    [Fact]
    public void Standard_StopperTimeout_AbortsBeforeDeletion()
    {
        var h = BuildPopulated();
        h.Stopper.NextResult = new AppStopResult(true, true, false, null, "timeout");
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.True(r.Aborted);
        Assert.Equal("stop-timed-out", r.AbortReason);
        // Nothing deleted.
        Assert.True(Directory.Exists(Path.Combine(h.InstallRoot, "App")));
        Assert.True(Directory.Exists(Path.Combine(h.InstallRoot, "Setup")));
        Assert.True(File.Exists(InstallStateStore.PathFor(h.InstallRoot)));
        Assert.True(h.Registry.SubKeyExists(ProtocolRegistrar.RootSubKey));
        Assert.True(h.Registry.SubKeyExists(UninstallRegistrar.RootSubKey));
        Assert.True(Directory.Exists(h.WorkspaceFolder));
        Assert.Empty(h.Shortcuts.Deleted);
        Assert.Equal(0, r.FilesRemoved);
        Assert.Equal(0, r.ShortcutsRemoved);
    }

    // ---- 18. Broken install: no App EXE present still uninstalls. ----
    [Fact]
    public void Broken_NoAppExe_StillUninstalls()
    {
        var h = BuildPopulated();
        File.Delete(Path.Combine(h.InstallRoot, @"App\bin", "PAXCookbook.exe"));
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.Equal("standard", r.Mode);
    }

    // ---- 19. Missing shortcut manifest does not fail uninstall. ----
    [Fact]
    public void Broken_MissingManifest_DoesNotFail()
    {
        var h = BuildPopulated();
        File.Delete(h.ManifestStore.PathFor(h.InstallRoot));
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.Equal(0, r.ShortcutsRemoved);
    }

    // ---- 20. Missing protocol key does not fail uninstall. ----
    [Fact]
    public void Broken_MissingProtocol_DoesNotFail()
    {
        var h = BuildPopulated();
        h.Registry.DeleteSubKeyTree(ProtocolRegistrar.RootSubKey);
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.False(r.ProtocolRemoved);
    }

    // ---- 21. Missing ARP key does not fail uninstall. ----
    [Fact]
    public void Broken_MissingArp_DoesNotFail()
    {
        var h = BuildPopulated();
        h.Registry.DeleteSubKeyTree(UninstallRegistrar.RootSubKey);
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.False(r.ArpRemoved);
    }

    // ---- 22. UninstallVerb is no longer the placeholder (returns Ok
    //          and reports the new structured summary).
    [Fact]
    public void UninstallVerb_NotPlaceholder_PrintsSummary()
    {
        var h = BuildPopulated();
        using var log = new SetupLogger(Path.Combine(h.InstallRoot, "Logs", "Setup"));
        using var sw = new StringWriter();
        var parsed = ArgParser.Parse(new[] { "uninstall" });
        var rc = UninstallVerb.Run(h.InstallRoot, parsed, log, sw, h.Ops);
        Assert.Equal(SetupExitCodes.Ok, rc);
        Assert.Contains("uninstall: mode=standard", sw.ToString());
        Assert.DoesNotContain("not implemented yet", sw.ToString());
    }

    // ---- 23. ARP UninstallString runs the Setup DLL uninstall via dotnet.exe. ----
    [Fact]
    public void Arp_UninstallString_PointsAtInstalledSetupUninstall()
    {
        var h = BuildPopulated();
        var us = h.Registry.GetString(UninstallRegistrar.RootSubKey, "UninstallString")!;
        Assert.StartsWith("\"", us);
        Assert.Contains(h.InstallRoot, us);
        Assert.EndsWith("PAXCookbookSetup.dll\" uninstall", us);
    }

    // ---- 24. install-state.json is removed last in standard mode. ----
    [Fact]
    public void Standard_RemovesInstallStateJson()
    {
        var h = BuildPopulated();
        h.Ops.RunStandard(h.InstallRoot);
        Assert.False(File.Exists(InstallStateStore.PathFor(h.InstallRoot)));
    }

    // ---- 25. Shortcut whose target no longer points under installRoot
    //          is NOT removed (positive ID requirement).
    [Fact]
    public void Shell_DoesNotDelete_RepurposedShortcut()
    {
        var h = BuildPopulated();
        // Mutate the manifest so one entry points at notepad outside root.
        var manifest = h.ManifestStore.TryLoad(h.InstallRoot)!;
        var rewritten = new ShortcutManifest
        {
            AppVersion = manifest.AppVersion,
            InstallRoot = manifest.InstallRoot,
            Shortcuts = manifest.Shortcuts.Select(e =>
                e.Kind == "start-menu" && e.Arguments == "support"
                    ? e with { Target = @"C:\Windows\System32\notepad.exe" }
                    : e).ToList()
        };
        h.ManifestStore.Save(h.InstallRoot, rewritten);
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.Contains(r.Steps, s => s.StartsWith("shell:"));
        // The repurposed (now-notepad) shortcut must NOT appear in the
        // writer's Deleted set.
        Assert.DoesNotContain(h.Shortcuts.Deleted,
            p => p.EndsWith("PAX Cookbook Support Mode.lnk", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 26. Shell remover does NOT remove a protocol key whose
    //          command points at a different install root.
    [Fact]
    public void Shell_DoesNotDelete_ForeignProtocolKey()
    {
        var h = BuildPopulated();
        // Repoint the protocol command at an unrelated path.
        h.Registry.SetString(
            ProtocolRegistrar.RootSubKey + @"\shell\open\command", null,
            "\"C:\\Other\\App\\PAXCookbook.exe\" protocol \"%1\"");
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.False(r.ProtocolRemoved);
        Assert.True(h.Registry.SubKeyExists(ProtocolRegistrar.RootSubKey));
    }

    // ---- 27. Shell remover does NOT remove an ARP entry whose
    //          UninstallString points at a different install root.
    [Fact]
    public void Shell_DoesNotDelete_ForeignArpEntry()
    {
        var h = BuildPopulated();
        h.Registry.SetString(UninstallRegistrar.RootSubKey, "UninstallString",
            "\"C:\\Other\\Setup\\PAXCookbookSetup.exe\" uninstall");
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.False(r.ArpRemoved);
        Assert.True(h.Registry.SubKeyExists(UninstallRegistrar.RootSubKey));
    }

    // ============================================================
    // Taskbar pin cleanup: deferred by default; positive-ID logic
    // is exercised by unit tests so the decision rules are correct
    // when the contract's IPinnedList3 probe is run in a later phase.
    // ============================================================

    // ---- 28. Default taskbar cleaner is the deferred Null impl. ----
    [Fact]
    public void Taskbar_DefaultIsDeferred()
    {
        var h = BuildPopulated();
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.False(r.TaskbarPinResult.Performed);
        Assert.Equal("deferred", r.TaskbarPinResult.Reason);
        Assert.Empty(r.TaskbarPinResult.Removed);
    }

    // ---- 29. PositiveIdReason: target under installRoot matches. ----
    [Fact]
    public void Taskbar_PositiveId_TargetUnderInstallRoot()
    {
        var root = @"C:\Users\Test\AppData\Local\PAXCookbook";
        var info = new TaskbarLnkInfo(
            LnkPath: "x.lnk",
            Target: root + @"\App\bin\PAXCookbook.exe",
            WorkingDirectory: "",
            Aumid: "");
        Assert.Equal("target-under-installroot",
            LnkTaskbarPinCleaner.PositiveIdReason(root, info));
    }

    // ---- 30. PositiveIdReason: AUMID match. ----
    [Fact]
    public void Taskbar_PositiveId_AumidMatch()
    {
        var root = @"C:\Users\Test\AppData\Local\PAXCookbook";
        var info = new TaskbarLnkInfo(
            LnkPath: "x.lnk",
            Target: @"C:\Windows\System32\notepad.exe", // not under root
            WorkingDirectory: "",
            Aumid: ProductConstants.Aumid);
        Assert.Equal("aumid-match",
            LnkTaskbarPinCleaner.PositiveIdReason(root, info));
    }

    // ---- 31. PositiveIdReason: working directory under installRoot. ----
    [Fact]
    public void Taskbar_PositiveId_WorkingDirUnderInstallRoot()
    {
        var root = @"C:\Users\Test\AppData\Local\PAXCookbook";
        var info = new TaskbarLnkInfo(
            LnkPath: "x.lnk",
            Target: @"C:\Windows\System32\notepad.exe",
            WorkingDirectory: root + @"\App\bin",
            Aumid: "");
        Assert.Equal("workingdir-under-installroot",
            LnkTaskbarPinCleaner.PositiveIdReason(root, info));
    }

    // ---- 32. PositiveIdReason: unrelated Edge pin is NOT matched. ----
    [Fact]
    public void Taskbar_PositiveId_UnrelatedEdgePinNotMatched()
    {
        var root = @"C:\Users\Test\AppData\Local\PAXCookbook";
        var info = new TaskbarLnkInfo(
            LnkPath: "edge.lnk",
            Target: @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            WorkingDirectory: @"C:\Program Files (x86)\Microsoft\Edge\Application",
            Aumid: "MSEdge");
        Assert.Null(LnkTaskbarPinCleaner.PositiveIdReason(root, info));
    }

    // ---- 33. PositiveIdReason: unrelated Notepad pin is NOT matched. ----
    [Fact]
    public void Taskbar_PositiveId_UnrelatedNotepadNotMatched()
    {
        var root = @"C:\Users\Test\AppData\Local\PAXCookbook";
        var info = new TaskbarLnkInfo(
            LnkPath: "notepad.lnk",
            Target: @"C:\Windows\notepad.exe",
            WorkingDirectory: @"C:\Windows",
            Aumid: "");
        Assert.Null(LnkTaskbarPinCleaner.PositiveIdReason(root, info));
    }

    // ---- 34. LnkTaskbarPinCleaner removes only positive-ID matches
    //          and leaves unrelated .lnk files alone.
    [Fact]
    public void Taskbar_LnkCleaner_RemovesOnlyPositiveIdMatches()
    {
        var root = @"C:\Users\Test\AppData\Local\PAXCookbook";
        var rv = new InMemoryTaskbarLnkResolver();
        rv.Map["pax.lnk"]    = new TaskbarLnkInfo("pax.lnk",
            root + @"\App\bin\PAXCookbook.exe", "", "");
        rv.Map["edge.lnk"]   = new TaskbarLnkInfo("edge.lnk",
            @"C:\Program Files\Edge\msedge.exe", "", "MSEdge");
        rv.Map["chrome.lnk"] = new TaskbarLnkInfo("chrome.lnk",
            @"C:\Program Files\Google\chrome.exe", "", "Chrome");

        var cleaner = new LnkTaskbarPinCleaner(rv, folderOverride: "ignored");
        var r = cleaner.Cleanup(root);

        Assert.True(r.Performed);
        Assert.Equal("performed", r.Reason);
        Assert.Single(r.Candidates);
        Assert.Equal("pax.lnk", r.Candidates[0].LnkPath);
        Assert.Contains("pax.lnk", r.Removed);
        Assert.DoesNotContain("edge.lnk", r.Removed);
        Assert.DoesNotContain("chrome.lnk", r.Removed);
    }

    // ============================================================
    // CLI surface tests.
    // ============================================================

    // ---- 35. ArgParser recognizes --remove-user-data and --confirm-remove-user-data. ----
    [Fact]
    public void ArgParser_RecognizesFullUninstallFlags()
    {
        var p = ArgParser.Parse(new[]
        {
            "uninstall", "--remove-user-data", "--confirm-remove-user-data"
        });
        Assert.Equal("uninstall", p.Verb);
        Assert.True(p.RemoveUserData);
        Assert.True(p.ConfirmRemoveUserData);
        Assert.Empty(p.Errors);
    }

    // ---- 36. SelfHandoff forwards the full-uninstall flags to the temp copy. ----
    [Fact]
    public void SelfHandoff_ForwardsFullUninstallFlags()
    {
        var parsed = new ParsedArgs(
            Verb: "uninstall",
            InstallRootOverride: null,
            PayloadRoot: null,
            Force: false, ReinstallSameVersion: false, AllowDowngrade: false,
            HandoffFromInstalled: false, HandoffFolder: null,
            DryRun: false,
            RemoveUserData: true, ConfirmRemoveUserData: true,
            Errors: new());
        var args = SelfHandoff.BuildHandoffArgs(parsed, @"C:\Temp\x", @"C:\Install");
        Assert.Contains("--remove-user-data", args);
        Assert.Contains("--confirm-remove-user-data", args);
        Assert.Contains("--handoff-from-installed", args);
    }

    // ---- 37. Stopper NONZERO exit aborts default uninstall before deletion. ----
    [Fact]
    public void Standard_StopperNonzeroExit_AbortsBeforeDeletion()
    {
        var h = BuildPopulated();
        h.Stopper.NextResult = new AppStopResult(true, true, true, 1, "stop refused");
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.True(r.Aborted);
        Assert.Equal("stop-nonzero", r.AbortReason);
        Assert.True(Directory.Exists(Path.Combine(h.InstallRoot, "App")));
        Assert.True(File.Exists(InstallStateStore.PathFor(h.InstallRoot)));
        Assert.True(h.Registry.SubKeyExists(UninstallRegistrar.RootSubKey));
    }

    // ---- 38. Missing App EXE (ExeFound=false) continues uninstall (broken-install recovery). ----
    [Fact]
    public void Standard_MissingAppExe_ContinuesUninstall()
    {
        var h = BuildPopulated();
        h.Stopper.NextResult = new AppStopResult(
            Invoked: false, ExeFound: false, Exited: true, ExitCode: null, Detail: "no-exe");
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.False(r.Aborted);
        Assert.False(Directory.Exists(Path.Combine(h.InstallRoot, "App")));
    }

    // ---- 39. Stopper exits 0 (clean) continues uninstall. ----
    [Fact]
    public void Standard_StopperExitZero_ContinuesUninstall()
    {
        var h = BuildPopulated();
        h.Stopper.NextResult = new AppStopResult(true, true, true, 0, null);
        var r = h.Ops.RunStandard(h.InstallRoot);
        Assert.False(r.Aborted);
        Assert.False(Directory.Exists(Path.Combine(h.InstallRoot, "App")));
    }

    // ---- 40. --force overrides stop-failure abort. ----
    [Fact]
    public void Standard_Force_OverridesStopFailureAbort()
    {
        var h = BuildPopulated();
        h.Stopper.NextResult = new AppStopResult(true, true, true, 1, "stop refused");
        var r = h.Ops.RunStandard(h.InstallRoot,
            UninstallOptions.Defaults with { Force = true });
        Assert.False(r.Aborted);
        Assert.False(Directory.Exists(Path.Combine(h.InstallRoot, "App")));
        Assert.Contains(r.Steps, s => s.StartsWith("force:"));
    }

    // ---- 41. UninstallVerb returns UninstallFailed and prints abort message on stop failure. ----
    [Fact]
    public void UninstallVerb_StopFailure_PrintsAbortMessage_AndReturnsFailedExitCode()
    {
        var h = BuildPopulated();
        h.Stopper.NextResult = new AppStopResult(true, true, false, null, "timeout");
        using var log = new SetupLogger(Path.Combine(h.InstallRoot, "Logs", "Setup"));
        using var sw = new StringWriter();
        var parsed = ArgParser.Parse(new[] { "uninstall" });
        var rc = UninstallVerb.Run(h.InstallRoot, parsed, log, sw, h.Ops);
        Assert.Equal(SetupExitCodes.UninstallFailed, rc);
        var stdout = sw.ToString();
        Assert.Contains("PAX Cookbook is still running and could not be stopped", stdout);
        Assert.Contains("--force", stdout);
        // No deletion happened.
        Assert.True(Directory.Exists(Path.Combine(h.InstallRoot, "App")));
        Assert.True(File.Exists(InstallStateStore.PathFor(h.InstallRoot)));
    }

    // ---- 42. UninstallVerb with --force after stop failure proceeds and returns Ok. ----
    [Fact]
    public void UninstallVerb_ForceAfterStopFailure_Proceeds_AndReturnsOk()
    {
        var h = BuildPopulated();
        h.Stopper.NextResult = new AppStopResult(true, true, true, 1, "stop refused");
        using var log = new SetupLogger(Path.Combine(h.InstallRoot, "Logs", "Setup"));
        using var sw = new StringWriter();
        var parsed = ArgParser.Parse(new[] { "uninstall", "--force" });
        var rc = UninstallVerb.Run(h.InstallRoot, parsed, log, sw, h.Ops);
        Assert.Equal(SetupExitCodes.Ok, rc);
        Assert.False(Directory.Exists(Path.Combine(h.InstallRoot, "App")));
    }
}
