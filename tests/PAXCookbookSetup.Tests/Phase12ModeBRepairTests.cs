using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Payload;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Uninstall;
using PAXCookbookSetup.Verbs;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Phase 12 — Mode B failure repair regression tests.
//
// Three production bugs were discovered during Mode B real-Windows
// acceptance:
//
//   Bug 1 ("repair: NO PAYLOAD"). The installed
//     <installRoot>\Setup\PAXCookbookSetup.exe is the no-embedded-
//     payload variant. After install, repair/update from Settings or
//     the installed Setup exe failed with payload-unavailable because
//     there was no fallback source. Fix: install writes a verified
//     local payload cache at <installRoot>\PayloadCache, and the
//     resolution chain now tries --payload-root, embedded, then the
//     local cache. Standard uninstall removes the cache.
//
//   Bug 2 ("Settings cannot find PAXCookbookSetup.exe"). ARP
//     UninstallString could be written with a non-expanded or
//     relative path. Fix: UninstallRegistrar.Register passes
//     installRoot through Path.GetFullPath, derives appExe and
//     setupExe from the normalized root, and additionally writes a
//     QuietUninstallString value.
//
//   Bug 3 ("uninstall --dry-run mutates"). Setup uninstall --dry-run
//     forwarded through self-handoff and then ignored the flag on the
//     receiving side, performing a real uninstall (858 files removed,
//     ARP gone, protocol gone). Fix: UninstallVerb refuses to run
//     when --dry-run is requested and returns UsageError. A proper
//     preview implementation is deferred.
//
// These tests lock in those fixes so a future change can't silently
// regress them. All tests use temp folders and in-memory fakes — no
// real %LOCALAPPDATA%\PAXCookbook, no real HKCU.
public class Phase12ModeBRepairTests
{
    private const string AppVersion = "1.2.3";
    private const string FakeInstallRoot = @"C:\Users\Test\AppData\Local\PAXCookbook";

    // ============================================================
    // Bug 1 — Local payload cache resolver + install write-through
    // ============================================================

    // ---- 1. LocalCache resolver fails when install root is empty. ----
    [Fact]
    public void LocalCache_EmptyInstallRoot_Fails()
    {
        var r = new LocalCachePayloadSourceResolver("").Resolve();
        Assert.False(r.Success);
        Assert.Equal("local-cache", r.Origin);
        Assert.Contains("install-root", r.Error ?? "");
    }

    // ---- 2. LocalCache resolver fails when the cache directory is missing. ----
    [Fact]
    public void LocalCache_DirectoryMissing_Fails()
    {
        var installRoot = NewTempInstallRoot();
        try
        {
            var r = new LocalCachePayloadSourceResolver(installRoot).Resolve();
            Assert.False(r.Success);
            Assert.Equal("local-cache", r.Origin);
            Assert.Contains("does not exist", r.Error ?? "");
            Assert.False(LocalCachePayloadSourceResolver.HasCache(installRoot));
        }
        finally { TryRm(installRoot); }
    }

    // ---- 3. LocalCache resolver fails when cache exists without manifest. ----
    [Fact]
    public void LocalCache_DirectoryExists_NoManifest_Fails()
    {
        var installRoot = NewTempInstallRoot();
        var cache = LocalCachePayloadSourceResolver.CachePath(installRoot);
        Directory.CreateDirectory(cache);
        try
        {
            var r = new LocalCachePayloadSourceResolver(installRoot).Resolve();
            Assert.False(r.Success);
            Assert.Contains("manifest.json", r.Error ?? "");
            Assert.False(LocalCachePayloadSourceResolver.HasCache(installRoot));
        }
        finally { TryRm(installRoot); }
    }

    // ---- 4. LocalCache resolver succeeds with cache dir + manifest. ----
    [Fact]
    public void LocalCache_DirectoryExists_WithManifest_Resolves()
    {
        var installRoot = NewTempInstallRoot();
        var cache = LocalCachePayloadSourceResolver.CachePath(installRoot);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "manifest.json"), "{}");
        try
        {
            var r = new LocalCachePayloadSourceResolver(installRoot).Resolve();
            Assert.True(r.Success, r.Error);
            Assert.Equal("local-cache", r.Origin);
            Assert.Equal(Path.GetFullPath(cache), r.PayloadRoot);
            Assert.True(LocalCachePayloadSourceResolver.HasCache(installRoot));
        }
        finally { TryRm(installRoot); }
    }

    // ---- 5. LocalCache resolver returns a fully-qualified payload root. ----
    [Fact]
    public void LocalCache_ResolvedPayloadRoot_IsFullyQualified()
    {
        var installRoot = NewTempInstallRoot();
        var cache = LocalCachePayloadSourceResolver.CachePath(installRoot);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "manifest.json"), "{}");
        try
        {
            var r = new LocalCachePayloadSourceResolver(installRoot).Resolve();
            Assert.True(r.Success);
            Assert.NotNull(r.PayloadRoot);
            Assert.True(Path.IsPathFullyQualified(r.PayloadRoot!));
        }
        finally { TryRm(installRoot); }
    }

    // ---- 6. CachePath uses the PayloadCacheDirName constant. ----
    [Fact]
    public void LocalCache_CachePath_UsesPayloadCacheDirName()
    {
        Assert.Equal("PayloadCache", LocalCachePayloadSourceResolver.PayloadCacheDirName);
        var p = LocalCachePayloadSourceResolver.CachePath(FakeInstallRoot);
        Assert.Equal(Path.Combine(FakeInstallRoot, "PayloadCache"), p);
    }

    // ---- 7. DefaultPayloadOperations.WritePayloadCache mirrors the tree. ----
    [Fact]
    public void DefaultPayloadOps_WritePayloadCache_MirrorsTree()
    {
        var src = Path.Combine(Path.GetTempPath(),
            "p12mb-src-" + Guid.NewGuid().ToString("N"));
        var dst = Path.Combine(Path.GetTempPath(),
            "p12mb-dst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(src, "App", "bin"));
        Directory.CreateDirectory(Path.Combine(src, "Setup"));
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(src, "App", "bin", "PAXCookbook.exe"), "app");
        File.WriteAllText(Path.Combine(src, "Setup", "PAXCookbookSetup.exe"), "setup");
        try
        {
            DefaultPayloadOperations.Instance.WritePayloadCache(src, dst);
            Assert.True(File.Exists(Path.Combine(dst, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(dst, "App", "bin", "PAXCookbook.exe")));
            Assert.True(File.Exists(Path.Combine(dst, "Setup", "PAXCookbookSetup.exe")));
            Assert.Equal("app",
                File.ReadAllText(Path.Combine(dst, "App", "bin", "PAXCookbook.exe")));
        }
        finally { TryRm(src); TryRm(dst); }
    }

    // ---- 8. WritePayloadCache rejects self-overwrite (src == dst). ----
    [Fact]
    public void DefaultPayloadOps_WritePayloadCache_RejectsSelfOverwrite()
    {
        var d = Path.Combine(Path.GetTempPath(),
            "p12mb-self-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => DefaultPayloadOperations.Instance.WritePayloadCache(d, d));
        }
        finally { TryRm(d); }
    }

    // ---- 9. WritePayloadCache throws DirectoryNotFoundException for a missing source. ----
    [Fact]
    public void DefaultPayloadOps_WritePayloadCache_MissingSource_Throws()
    {
        var src = Path.Combine(Path.GetTempPath(),
            "p12mb-nope-" + Guid.NewGuid().ToString("N"));
        var dst = Path.Combine(Path.GetTempPath(),
            "p12mb-dst-"  + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dst);
        try
        {
            Assert.Throws<DirectoryNotFoundException>(
                () => DefaultPayloadOperations.Instance.WritePayloadCache(src, dst));
        }
        finally { TryRm(dst); }
    }

    // ============================================================
    // Bug 2 — Defensive ARP UninstallString / DisplayIcon normalization
    // ============================================================

    // ---- 10. UninstallString is wrapped, points at installed Setup, ends with verb. ----
    [Fact]
    public void Arp_UninstallString_ShapeAndQuoting()
    {
        var rg = new InMemoryRegistryWriter();
        var u = new UninstallRegistrar(rg);
        var r = u.Register(FakeInstallRoot, AppVersion);
        Assert.StartsWith("\"", r.UninstallString);
        Assert.EndsWith(@"Setup\PAXCookbookSetup.exe"" uninstall", r.UninstallString);
        Assert.Equal(r.UninstallString,
            rg.GetString(UninstallRegistrar.RootSubKey, "UninstallString"));
    }

    // ---- 11. UninstallString contains a fully-qualified path. ----
    [Fact]
    public void Arp_UninstallString_TargetIsFullyQualified()
    {
        var rg = new InMemoryRegistryWriter();
        var u = new UninstallRegistrar(rg);
        var r = u.Register(FakeInstallRoot, AppVersion);
        var inner = ExtractQuotedPath(r.UninstallString);
        Assert.True(Path.IsPathFullyQualified(inner),
            $"UninstallString target should be fully qualified, got: {inner}");
    }

    // ---- 12. QuietUninstallString is registered and ends with --force. ----
    [Fact]
    public void Arp_QuietUninstallString_IsRegistered_WithForce()
    {
        var rg = new InMemoryRegistryWriter();
        var u = new UninstallRegistrar(rg);
        var r = u.Register(FakeInstallRoot, AppVersion);
        Assert.False(string.IsNullOrEmpty(r.QuietUninstallString));
        Assert.EndsWith("uninstall --force", r.QuietUninstallString);
        Assert.Equal(r.QuietUninstallString,
            rg.GetString(UninstallRegistrar.RootSubKey, "QuietUninstallString"));
        // Same setup target as UninstallString (just an extra --force flag).
        Assert.Equal(ExtractQuotedPath(r.UninstallString),
                     ExtractQuotedPath(r.QuietUninstallString));
    }

    // ---- 13. DisplayIcon is registered and points to a fully-qualified app exe. ----
    [Fact]
    public void Arp_DisplayIcon_IsFullyQualified()
    {
        var rg = new InMemoryRegistryWriter();
        var u = new UninstallRegistrar(rg);
        var r = u.Register(FakeInstallRoot, AppVersion);
        Assert.False(string.IsNullOrEmpty(r.DisplayIcon));
        Assert.EndsWith(",0", r.DisplayIcon);
        var iconPath = r.DisplayIcon.Substring(0, r.DisplayIcon.Length - 2);
        Assert.True(Path.IsPathFullyQualified(iconPath),
            $"DisplayIcon target should be fully qualified, got: {iconPath}");
        Assert.EndsWith(@"App\bin\PAX Cookbook.exe", iconPath);
        Assert.Equal(r.DisplayIcon,
            rg.GetString(UninstallRegistrar.RootSubKey, "DisplayIcon"));
    }

    // ---- 14. InstallLocation is normalized to an absolute path. ----
    [Fact]
    public void Arp_InstallLocation_IsFullyQualified()
    {
        var rg = new InMemoryRegistryWriter();
        var u = new UninstallRegistrar(rg);
        var r = u.Register(FakeInstallRoot, AppVersion);
        Assert.Equal(Path.GetFullPath(FakeInstallRoot), r.InstallLocation);
        Assert.True(Path.IsPathFullyQualified(r.InstallLocation));
        Assert.Equal(r.InstallLocation,
            rg.GetString(UninstallRegistrar.RootSubKey, "InstallLocation"));
    }

    // ---- 15. A relative install root is expanded before being stored. ----
    [Fact]
    public void Arp_RelativeInstallRoot_IsExpandedBeforeWrite()
    {
        // Use a relative-looking path. Path.GetFullPath combines it with
        // the test process working directory; the resulting value must
        // be absolute and the produced UninstallString must not contain
        // any "\.\" segments.
        var rel = "PaxRel_" + Guid.NewGuid().ToString("N");
        var rg = new InMemoryRegistryWriter();
        var u = new UninstallRegistrar(rg);
        var r = u.Register(rel, AppVersion);
        Assert.True(Path.IsPathFullyQualified(r.InstallLocation));
        var inner = ExtractQuotedPath(r.UninstallString);
        Assert.True(Path.IsPathFullyQualified(inner));
        Assert.DoesNotContain(@"\.\", inner);
    }

    // ============================================================
    // Bug 3 — `uninstall --dry-run` is a no-op
    // ============================================================

    // ---- 16. --dry-run refuses to run and returns UsageError. ----
    [Fact]
    public void UninstallVerb_DryRun_RefusesAndReturnsUsageError()
    {
        var installRoot = NewTempInstallRoot();
        Directory.CreateDirectory(Path.Combine(installRoot, "Logs", "Setup"));
        try
        {
            using var log = new SetupLogger(Path.Combine(installRoot, "Logs", "Setup"));
            using var sw = new StringWriter();
            var parsed = ArgParser.Parse(new[]
            {
                "uninstall", "--install-root", installRoot, "--dry-run"
            });
            var rc = UninstallVerb.Run(installRoot, parsed, log, sw, BuildExplodingOperations());
            Assert.Equal(SetupExitCodes.UsageError, rc);
        }
        finally { TryRm(installRoot); }
    }

    // ---- 17. --dry-run prints the refusal message. ----
    [Fact]
    public void UninstallVerb_DryRun_PrintsRefusalMessage()
    {
        var installRoot = NewTempInstallRoot();
        Directory.CreateDirectory(Path.Combine(installRoot, "Logs", "Setup"));
        try
        {
            using var log = new SetupLogger(Path.Combine(installRoot, "Logs", "Setup"));
            using var sw = new StringWriter();
            var parsed = ArgParser.Parse(new[]
            {
                "uninstall", "--install-root", installRoot, "--dry-run"
            });
            var rc = UninstallVerb.Run(installRoot, parsed, log, sw, BuildExplodingOperations());
            Assert.Equal(SetupExitCodes.UsageError, rc);
            var output = sw.ToString();
            Assert.Contains("--dry-run is not yet implemented", output);
            Assert.Contains("Refusing to run", output);
        }
        finally { TryRm(installRoot); }
    }

    // ---- 18. --dry-run logs an uninstall-dryrun-refused event. ----
    [Fact]
    public void UninstallVerb_DryRun_LogsRefusalEvent()
    {
        var installRoot = NewTempInstallRoot();
        var logsDir = Path.Combine(installRoot, "Logs", "Setup");
        Directory.CreateDirectory(logsDir);
        try
        {
            using (var log = new SetupLogger(logsDir))
            using (var sw = new StringWriter())
            {
                var parsed = ArgParser.Parse(new[]
                {
                    "uninstall", "--install-root", installRoot, "--dry-run"
                });
                UninstallVerb.Run(installRoot, parsed, log, sw,
                    BuildExplodingOperations());
            }
            var combined = string.Concat(Directory.GetFiles(logsDir, "*.log")
                .Select(File.ReadAllText));
            Assert.Contains("uninstall-dryrun-refused", combined);
        }
        finally { TryRm(installRoot); }
    }

    // ---- 19. --dry-run never invokes uninstall operations. ----
    //
    // The injected ExplodingOperations throws on any access — so if the
    // dry-run gate ever regresses and the verb falls through to actual
    // work, this test will surface that as an InvalidOperationException
    // and fail loudly. The Asserts below also verify the install tree
    // is untouched (defense-in-depth).
    [Fact]
    public void UninstallVerb_DryRun_DoesNotMutateInstallTree()
    {
        var installRoot = NewTempInstallRoot();
        Directory.CreateDirectory(Path.Combine(installRoot, "App", "bin"));
        Directory.CreateDirectory(Path.Combine(installRoot, "Setup"));
        Directory.CreateDirectory(Path.Combine(installRoot, "Logs", "Setup"));
        File.WriteAllText(Path.Combine(installRoot, "App", "bin", "PAXCookbook.exe"), "app");
        File.WriteAllText(Path.Combine(installRoot, "Setup", "PAXCookbookSetup.exe"), "setup");
        try
        {
            using var log = new SetupLogger(Path.Combine(installRoot, "Logs", "Setup"));
            using var sw = new StringWriter();
            var parsed = ArgParser.Parse(new[]
            {
                "uninstall", "--install-root", installRoot, "--dry-run"
            });
            UninstallVerb.Run(installRoot, parsed, log, sw, BuildExplodingOperations());
            Assert.True(File.Exists(Path.Combine(installRoot, "App", "bin", "PAXCookbook.exe")));
            Assert.True(File.Exists(Path.Combine(installRoot, "Setup", "PAXCookbookSetup.exe")));
        }
        finally { TryRm(installRoot); }
    }

    // ============================================================
    // Cross-fix — PayloadCache cleanup contract
    // ============================================================

    // ---- 20. Standard uninstall lists PayloadCache as a removable subdir. ----
    [Fact]
    public void Uninstall_StandardRemovableSubdirs_IncludesPayloadCache()
    {
        var subs = UninstallOperations.StandardRemovableSubdirs().ToList();
        Assert.Contains("PayloadCache", subs);
        Assert.Contains("App", subs);
        Assert.Contains("Setup", subs);
        Assert.Contains("PreviousVersions", subs);
        Assert.Contains("WebView2Data", subs);
        Assert.Contains("Runtime", subs);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static string ExtractQuotedPath(string s)
    {
        // Expects: "<absolutePath>" <verbAndFlags...>
        var open = s.IndexOf('"');
        if (open < 0) return s;
        var close = s.IndexOf('"', open + 1);
        if (close < 0) return s;
        return s.Substring(open + 1, close - open - 1);
    }

    private static string NewTempInstallRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "p12mb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void TryRm(string d)
    {
        try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }

    // Builds a real UninstallOperations whose every dependency throws
    // on any method call. Used to prove the dry-run gate short-circuits
    // before any orchestration is attempted.
    private static UninstallOperations BuildExplodingOperations()
        => new UninstallOperations(
            new ExplodingAppStopper(),
            new ExplodingFileSystemRemover(),
            new ShellRemover(new InMemoryShortcutWriter(),
                             new InMemoryRegistryWriter(),
                             new ShortcutManifestStore()),
            new ExplodingTaskbarPinCleaner());

    private sealed class ExplodingAppStopper : IAppStopper
    {
        public AppStopResult TryStop(string installRoot, int timeoutMs)
            => throw new InvalidOperationException(
                "UninstallVerb invoked IAppStopper.TryStop under --dry-run");
    }

    private sealed class ExplodingFileSystemRemover : IFileSystemRemover
    {
        public RemoveResult RemoveFile(string path)
            => throw new InvalidOperationException(
                "UninstallVerb invoked IFileSystemRemover.RemoveFile under --dry-run");
        public RemoveResult RemoveDirectory(string path)
            => throw new InvalidOperationException(
                "UninstallVerb invoked IFileSystemRemover.RemoveDirectory under --dry-run");
    }

    private sealed class ExplodingTaskbarPinCleaner : ITaskbarPinCleaner
    {
        public TaskbarPinCleanupResult Cleanup(string installRoot)
            => throw new InvalidOperationException(
                "UninstallVerb invoked ITaskbarPinCleaner.Cleanup under --dry-run");
    }
}
