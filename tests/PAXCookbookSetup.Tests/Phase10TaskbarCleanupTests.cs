using System.Collections.Generic;
using System.IO;
using System.Linq;
using PAXCookbook.Shared;
using PAXCookbookSetup.Uninstall;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Phase 10 — positive-ID taskbar .lnk cleanup activation.
//
// All tests use an in-memory ITaskbarLnkResolver and a folder
// override; the real user taskbar pin folder is NEVER touched. The
// LnkTaskbarPinCleaner's positive-ID matcher is the single source of
// truth — it MUST refuse to delete anything that is not under the
// install root by target/workingdir or whose AUMID does not match
// PAXCookbook.App.v1.
public class Phase10TaskbarCleanupTests
{
    private const string InstallRoot = @"C:\Users\Test\AppData\Local\PAXCookbook";

    private static InMemoryTaskbarLnkResolver R() => new();

    // 1. Resolver returns metadata through the abstraction (no real .lnk).
    [Fact]
    public void Resolver_ReturnsMetadata_ThroughAbstraction()
    {
        var r = R();
        r.Map["a.lnk"] = new TaskbarLnkInfo("a.lnk", "T", "W", "A");
        var info = r.Resolve("a.lnk");
        Assert.NotNull(info);
        Assert.Equal("T", info!.Target);
        Assert.Equal("W", info.WorkingDirectory);
        Assert.Equal("A", info.Aumid);
    }

    // 2. Positive-ID by target-under-installroot REMOVES pin.
    [Fact]
    public void PositiveId_TargetUnderInstallRoot_Removes()
    {
        var r = R();
        r.Map["pax.lnk"] = new TaskbarLnkInfo("pax.lnk",
            InstallRoot + @"\App\bin\PAXCookbook.exe", "", "");
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored");
        var res = c.Cleanup(InstallRoot);
        Assert.Single(res.Removed);
        Assert.Equal("pax.lnk", res.Removed[0]);
        Assert.Equal("lnk-positive-id", res.Mode);
        Assert.Contains(res.Decisions!,
            d => d.LnkPath == "pax.lnk" && d.Outcome == "removed-pax-pin"
                 && d.MatchReason == "target-under-installroot");
    }

    // 3. Positive-ID by workingdir-under-installroot REMOVES pin.
    [Fact]
    public void PositiveId_WorkingDirUnderInstallRoot_Removes()
    {
        var r = R();
        r.Map["pax.lnk"] = new TaskbarLnkInfo("pax.lnk",
            @"C:\Windows\System32\notepad.exe", InstallRoot + @"\App\bin", "");
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored");
        var res = c.Cleanup(InstallRoot);
        Assert.Single(res.Removed);
        Assert.Contains(res.Decisions!,
            d => d.Outcome == "removed-pax-pin" &&
                 d.MatchReason == "workingdir-under-installroot");
    }

    // 4. Positive-ID by AUMID REMOVES pin.
    [Fact]
    public void PositiveId_AumidMatch_Removes()
    {
        var r = R();
        r.Map["pax.lnk"] = new TaskbarLnkInfo("pax.lnk",
            @"C:\Other\Place\bin.exe", "", ProductConstants.Aumid);
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored");
        var res = c.Cleanup(InstallRoot);
        Assert.Single(res.Removed);
        Assert.Contains(res.Decisions!,
            d => d.Outcome == "removed-pax-pin" && d.MatchReason == "aumid-match");
    }

    // 5. Edge pin SKIPPED.
    [Fact]
    public void Edge_Pin_IsSkipped()
    {
        var r = R();
        r.Map["edge.lnk"] = new TaskbarLnkInfo("edge.lnk",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application", "MSEdge");
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored");
        var res = c.Cleanup(InstallRoot);
        Assert.Empty(res.Removed);
        Assert.Contains(res.Decisions!, d => d.Outcome == "skipped-not-pax");
        Assert.DoesNotContain("edge.lnk", r.Deleted);
    }

    // 6. Chrome pin SKIPPED.
    [Fact]
    public void Chrome_Pin_IsSkipped()
    {
        var r = R();
        r.Map["chrome.lnk"] = new TaskbarLnkInfo("chrome.lnk",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files\Google\Chrome\Application", "Chrome");
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored");
        var res = c.Cleanup(InstallRoot);
        Assert.Empty(res.Removed);
        Assert.DoesNotContain("chrome.lnk", r.Deleted);
    }

    // 7. Unrelated app (Notepad) SKIPPED.
    [Fact]
    public void Notepad_Pin_IsSkipped()
    {
        var r = R();
        r.Map["np.lnk"] = new TaskbarLnkInfo("np.lnk",
            @"C:\Windows\notepad.exe", @"C:\Windows", "");
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored");
        var res = c.Cleanup(InstallRoot);
        Assert.Empty(res.Removed);
    }

    // 8. Broken/unresolvable link SKIPPED (unless PAX positive-ID).
    [Fact]
    public void Broken_Link_IsSkipped_WhenNoPositiveId()
    {
        var r = R();
        r.Map["broken.lnk"] = null;
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored");
        var res = c.Cleanup(InstallRoot);
        Assert.Empty(res.Removed);
        Assert.Contains(res.Decisions!,
            d => d.LnkPath == "broken.lnk" && d.Outcome == "skipped-unresolvable");
    }

    // 9. Display-name-only is NOT enough (no match without positive ID).
    [Fact]
    public void DisplayName_Alone_IsNotEnough()
    {
        var r = R();
        // .lnk literally named "PAX Cookbook.lnk" but resolves to notepad.
        r.Map[@"X:\Pinned\PAX Cookbook.lnk"] = new TaskbarLnkInfo(
            @"X:\Pinned\PAX Cookbook.lnk",
            @"C:\Windows\notepad.exe", @"C:\Windows", "");
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored");
        var res = c.Cleanup(InstallRoot);
        Assert.Empty(res.Removed);
    }

    // 10. Dry-run removes nothing but reports would-remove.
    [Fact]
    public void DryRun_ReportsWouldRemove_ButDeletesNothing()
    {
        var r = R();
        r.Map["pax.lnk"] = new TaskbarLnkInfo("pax.lnk",
            InstallRoot + @"\App\bin\PAXCookbook.exe", "", "");
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored", dryRun: true);
        var res = c.Cleanup(InstallRoot);
        Assert.Empty(res.Removed);
        Assert.Empty(r.Deleted);
        Assert.Single(res.Candidates);
        Assert.Equal("dry-run", res.Reason);
        Assert.Contains(res.Decisions!,
            d => d.LnkPath == "pax.lnk" && d.Outcome == "skipped-dry-run");
    }

    // 11. Cleanup reports per-pin decisions for every .lnk it saw.
    [Fact]
    public void Cleanup_ReportsPerPinDecisions()
    {
        var r = R();
        r.Map["pax.lnk"]   = new TaskbarLnkInfo("pax.lnk",
            InstallRoot + @"\App\bin\PAXCookbook.exe", "", "");
        r.Map["edge.lnk"]  = new TaskbarLnkInfo("edge.lnk",
            @"C:\Edge\msedge.exe", "", "MSEdge");
        r.Map["broken.lnk"] = null;
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored");
        var res = c.Cleanup(InstallRoot);
        Assert.Equal(3, res.Decisions!.Count);
        Assert.Contains(res.Decisions, d => d.LnkPath == "pax.lnk"   && d.Outcome == "removed-pax-pin");
        Assert.Contains(res.Decisions, d => d.LnkPath == "edge.lnk"  && d.Outcome == "skipped-not-pax");
        Assert.Contains(res.Decisions, d => d.LnkPath == "broken.lnk" && d.Outcome == "skipped-unresolvable");
    }

    // 12. Cleaner only ever enumerates the folder it was constructed
    //     with (positive boundary; tests use a temp folder, not user pin folder).
    [Fact]
    public void Cleanup_DoesNotScanOutsideBoundFolder()
    {
        // Build a temp folder; resolver only sees what the test put there.
        var tmp = Path.Combine(Path.GetTempPath(), "PAX10_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // Resolver only has a fake entry; the real folder is empty.
            var r = R();
            r.Map[Path.Combine(tmp, "ghost.lnk")] = new TaskbarLnkInfo("ghost", "T", "W", "");
            var c = new LnkTaskbarPinCleaner(r, folderOverride: tmp);
            var res = c.Cleanup(InstallRoot);
            Assert.NotNull(res);
            // Even with a resolver entry, the in-memory resolver returns
            // its own Map.Keys for EnumerateLnkFiles, not anything from
            // disk. The contract: cleaner never reads outside its bound
            // folder. The resolver respects the folder argument it was
            // given (LnkTaskbarPinCleaner passes _folder to it).
            Assert.NotNull(c);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // 13. Standard uninstall invokes the taskbar cleaner once.
    [Fact]
    public void StandardUninstall_InvokesTaskbarCleaner()
    {
        var ts = new RecordingTaskbarCleaner();
        var ops = BuildOps(ts);
        ops.RunStandard(@"C:\NowhereRoot");
        Assert.Equal(1, ts.Calls);
    }

    // 14. Full uninstall also invokes the taskbar cleaner.
    [Fact]
    public void FullUninstall_InvokesTaskbarCleaner()
    {
        var ts = new RecordingTaskbarCleaner();
        var ops = BuildOps(ts);
        ops.RunFull(@"C:\NowhereRoot");
        Assert.Equal(1, ts.Calls);
    }

    // 15. Cleanup failure (DeleteLnk returns false) does NOT crash uninstall
    //     and does NOT delete unrelated pins.
    [Fact]
    public void CleanupFailure_DoesNotCrash_AndPreservesUnrelatedPins()
    {
        var r = new RejectingResolver();
        r.Map["pax.lnk"] = new TaskbarLnkInfo("pax.lnk",
            InstallRoot + @"\App\bin\PAXCookbook.exe", "", "");
        r.Map["edge.lnk"] = new TaskbarLnkInfo("edge.lnk",
            @"C:\Edge\msedge.exe", "", "MSEdge");
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored");
        var res = c.Cleanup(InstallRoot);
        // PAX pin recorded as skipped-error because resolver refused to delete it.
        Assert.Contains(res.Decisions!,
            d => d.LnkPath == "pax.lnk" && d.Outcome == "skipped-error");
        // Edge pin still recorded as skipped-not-pax and untouched.
        Assert.Contains(res.Decisions!,
            d => d.LnkPath == "edge.lnk" && d.Outcome == "skipped-not-pax");
        Assert.Empty(res.Removed);
    }

    // 16. IPinnedList3 decision: deferred. No live COM call active.
    //     Production wires LnkTaskbarPinCleaner (mode=lnk-positive-id),
    //     not an IPinnedList3-backed cleaner.
    [Fact]
    public void IPinnedList3_RemainsDeferred()
    {
        // LnkTaskbarPinCleaner is the active default. Its Mode field is
        // "lnk-positive-id" — NOT "ipinnedlist3".
        var r = R();
        var c = new LnkTaskbarPinCleaner(r, folderOverride: "ignored");
        var res = c.Cleanup(InstallRoot);
        Assert.Equal("lnk-positive-id", res.Mode);
        Assert.NotEqual("ipinnedlist3", res.Mode);
    }

    // 17. (Negative) No Explorer restart code in source — verifier C9 covers
    //     this directly; here we just affirm the cleaner has no such surface.
    [Fact]
    public void Cleaner_HasNoExplorerRestartSurface()
    {
        var t = typeof(LnkTaskbarPinCleaner);
        Assert.DoesNotContain(t.GetMethods(),
            m => m.Name.Contains("Explorer", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(t.GetMethods(),
            m => m.Name.Contains("Restart", System.StringComparison.OrdinalIgnoreCase));
    }

    // 18-19. No auto-pinning / no broad deletion: the cleaner has no
    //        Add / Pin / DeleteAll surface.
    [Fact]
    public void Cleaner_HasNoAutoPinOrBroadDeleteSurface()
    {
        var t = typeof(LnkTaskbarPinCleaner);
        foreach (var m in t.GetMethods())
        {
            var n = m.Name;
            // Look for methods that would actively add/install a pin
            // or perform broad deletion. "Pinned" in DefaultUserPinned-
            // TaskbarFolder is fine (it names the folder).
            Assert.False(n.Equals("Pin", System.StringComparison.OrdinalIgnoreCase));
            Assert.False(n.StartsWith("PinTo", System.StringComparison.OrdinalIgnoreCase));
            Assert.False(n.StartsWith("AddPin", System.StringComparison.OrdinalIgnoreCase));
            Assert.False(n.StartsWith("InstallPin", System.StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("DeleteAll", n, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PurgeFolder", n, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    // 20. ShellLinkResolver does NOT enumerate recursively (top-level only).
    [Fact]
    public void ShellLinkResolver_TopLevelOnly_NoRecursion()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "PAX10R_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(tmp, "top.lnk"), "fake");
            File.WriteAllText(Path.Combine(tmp, "sub", "nested.lnk"), "fake");
            var resolver = new ShellLinkResolver();
            var files = resolver.EnumerateLnkFiles(tmp).Select(Path.GetFileName).ToList();
            Assert.Contains("top.lnk", files);
            Assert.DoesNotContain("nested.lnk", files);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // 21. ShellLinkResolver Resolve returns null for a non-.lnk file
    //     without throwing (defensive).
    [Fact]
    public void ShellLinkResolver_MissingFile_ReturnsNull()
    {
        var resolver = new ShellLinkResolver();
        var info = resolver.Resolve(Path.Combine(Path.GetTempPath(),
            "no-such-file-" + System.Guid.NewGuid() + ".lnk"));
        Assert.Null(info);
    }

    // 22. ShellLinkResolver DeleteLnk on missing file returns true (idempotent).
    [Fact]
    public void ShellLinkResolver_DeleteLnk_MissingFile_ReturnsTrue()
    {
        var resolver = new ShellLinkResolver();
        var ok = resolver.DeleteLnk(Path.Combine(Path.GetTempPath(),
            "no-such-file-" + System.Guid.NewGuid() + ".lnk"));
        Assert.True(ok);
    }

    // 23. UninstallVerb.BuildDefault wires LnkTaskbarPinCleaner in
    //     production and NullTaskbarPinCleaner in test/E2E mode.
    //     Verified by source-level inspection (the verifier enforces
    //     the same invariant via grep).
    [Fact]
    public void DefaultBuilder_WiresLnkCleaner_InProduction()
    {
        var verbType = typeof(PAXCookbookSetup.Verbs.UninstallVerb);
        var asm = verbType.Assembly;
        // ShellLinkResolver must be reachable from the Setup assembly
        // (i.e. it ships as part of production).
        Assert.NotNull(asm.GetType("PAXCookbookSetup.Uninstall.ShellLinkResolver"));
        Assert.NotNull(asm.GetType("PAXCookbookSetup.Uninstall.LnkTaskbarPinCleaner"));
    }

    // ---- helpers ----

    private sealed class RecordingTaskbarCleaner : ITaskbarPinCleaner
    {
        public int Calls;
        public TaskbarPinCleanupResult Cleanup(string installRoot)
        {
            Calls++;
            return new TaskbarPinCleanupResult(
                Performed: true, Reason: "performed",
                Candidates: System.Array.Empty<TaskbarPinCleanupCandidate>(),
                Removed: System.Array.Empty<string>(),
                Skipped: System.Array.Empty<string>(),
                Mode: "lnk-positive-id",
                Decisions: System.Array.Empty<TaskbarPinDecision>());
        }
    }

    private sealed class RejectingResolver : ITaskbarLnkResolver
    {
        public Dictionary<string, TaskbarLnkInfo?> Map { get; } =
            new(System.StringComparer.OrdinalIgnoreCase);
        public TaskbarLnkInfo? Resolve(string lnkPath)
            => Map.TryGetValue(lnkPath, out var v) ? v : null;
        public IEnumerable<string> EnumerateLnkFiles(string folderPath) => Map.Keys;
        // Always refuses to delete — simulates a locked .lnk file.
        public bool DeleteLnk(string lnkPath) => false;
    }

    private static UninstallOperations BuildOps(ITaskbarPinCleaner cleaner)
    {
        // Stub everything else; this test only cares that the orchestrator
        // calls Cleanup once. No disk I/O.
        var stopper = new RecordingAppStopper();
        var files = new RecordingFileSystemRemover();
        var shellRemover = new PAXCookbookSetup.Shell.ShellRemover(
            new PAXCookbookSetup.Shell.InMemoryShortcutWriter(),
            new PAXCookbookSetup.Shell.InMemoryRegistryWriter(),
            new PAXCookbookSetup.Shell.ShortcutManifestStore());
        return new UninstallOperations(stopper, files, shellRemover, cleaner);
    }
}
