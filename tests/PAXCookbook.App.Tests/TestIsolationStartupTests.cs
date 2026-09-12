using System;
using System.IO;
using PAXCookbook.App;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Unit proof for the test-isolation activation repair (cycle-02r5h).
//
// Two root causes are pinned here:
//  H2 (the confirmed activation defect): the per-run local-state base and the
//      workspace root must be routed from the AUTHORITATIVE isolation context,
//      not from the independent --engine-localappdata / --workspace seams that
//      fall through to REAL profile roots on a bare --test-isolation launch.
//  H1 (a latent launcher footgun): a descriptor path containing spaces must be
//      passed as ONE argv element; a caller that lets the shell split it yields
//      only a truncated token.
//
// The routing seam and the argument reader always compile, so those proofs are
// unconditional. The activation gate itself is build-mode differentiated and is
// asserted under a single #if that behaves correctly in BOTH builds without
// mutating any process-static state.
public sealed class TestIsolationStartupTests
{
    // Builds a fully-populated context without touching the filesystem or the
    // parser's real-root/reparse validation — the routing seam only reads the
    // LocalStateRoot / Workspace properties.
    private static TestIsolationContext MakeContext(string localStateRoot, string workspace)
        => new TestIsolationContext(
            isolatedRoot: @"C:\pax_iso_ut",
            installRoot: @"C:\pax_iso_ut\install",
            localStateRoot: localStateRoot,
            workspace: workspace,
            webView2Data: @"C:\pax_iso_ut\ws\WebView2",
            engineState: @"C:\pax_iso_ut\lad\Engine",
            payloadCache: @"C:\pax_iso_ut\cache",
            logs: @"C:\pax_iso_ut\logs",
            coordinationState: @"C:\pax_iso_ut\lad",
            setupTempRoot: @"C:\pax_iso_ut\tmp",
            shellIntegrationEnabled: false,
            updateChecksEnabled: false,
            networkPayloadDownloadEnabled: false,
            updateApplyEnabled: false,
            preserveIsolationOnRelaunch: true,
            installedProductDiscoveryEnabled: false,
            testLocalPayloadPath: null,
            testLocalPayloadSha256: null);

    // A production resolver that MUST NOT run under isolation. If the seam ever
    // touches it while isolated, the test fails loudly instead of silently
    // resolving a real profile root.
    private static string ThrowingProductionResolver() =>
        throw new InvalidOperationException(
            "production root resolver invoked under active isolation (fall-through defect)");

    // ---- H2: local-state base routing --------------------------------------

    [Fact]
    public void ResolveLocalAppDataBase_UnderIsolation_ReturnsLocalStateRoot_AndNeverCallsProduction()
    {
        TestIsolationContext ctx = MakeContext(@"C:\pax_iso_ut\lad", @"C:\pax_iso_ut\ws");

        string result = TestIsolationStartup.ResolveLocalAppDataBase(ctx, ThrowingProductionResolver);

        Assert.Equal(@"C:\pax_iso_ut\lad", result);
    }

    [Fact]
    public void ResolveLocalAppDataBase_WithoutIsolation_UsesProductionResolver()
    {
        string result = TestIsolationStartup.ResolveLocalAppDataBase(
            isolation: null,
            productionResolver: () => @"C:\Users\real\AppData\Local");

        Assert.Equal(@"C:\Users\real\AppData\Local", result);
    }

    // ---- H2: workspace routing ---------------------------------------------

    [Fact]
    public void ResolveWorkspaceRoot_UnderIsolation_ReturnsWorkspace_AndNeverCallsProduction()
    {
        TestIsolationContext ctx = MakeContext(@"C:\pax_iso_ut\lad", @"C:\pax_iso_ut\ws");

        string result = TestIsolationStartup.ResolveWorkspaceRoot(ctx, ThrowingProductionResolver);

        Assert.Equal(@"C:\pax_iso_ut\ws", result);
    }

    [Fact]
    public void ResolveWorkspaceRoot_WithoutIsolation_UsesProductionResolver()
    {
        string result = TestIsolationStartup.ResolveWorkspaceRoot(
            isolation: null,
            productionResolver: () => @"C:\Users\real\AppData\Local\PAXCookbook\Workspace");

        Assert.Equal(@"C:\Users\real\AppData\Local\PAXCookbook\Workspace", result);
    }

    [Fact]
    public void ResolveStartupLogPath_UnderIsolation_UsesIsolatedLogs_AndNeverCallsProduction()
    {
        TestIsolationContext ctx = MakeContext(@"C:\pax_iso_ut\lad", @"C:\pax_iso_ut\ws");

        string result = StartupLog.ResolveLogPath(ctx, ThrowingProductionResolver);

        Assert.Equal(@"C:\pax_iso_ut\logs\startup.log", result);
    }

    [Fact]
    public void ResolveLocalAppDataBase_NullProductionResolver_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TestIsolationStartup.ResolveLocalAppDataBase(isolation: null, productionResolver: null!));
    }

    [Fact]
    public void ResolveWorkspaceRoot_NullProductionResolver_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TestIsolationStartup.ResolveWorkspaceRoot(isolation: null, productionResolver: null!));
    }

    // ---- H1: launcher argument-quoting regression --------------------------

    [Fact]
    public void ReadDescriptorArg_SingleElement_WithSpaces_ReturnsIntact()
    {
        string descriptor = @"C:\Users\me\OneDrive - Microsoft\PAX Cookbook\_temp\tests\pilot_iso\desc.json";
        string[] args = { "--test-isolation", descriptor, "--isolation-selfcheck" };

        Assert.Equal(descriptor, TestIsolationRuntime.ReadDescriptorArg(args));
    }

    [Fact]
    public void ReadDescriptorArg_ShellSplitPath_ReturnsTruncatedFirstToken()
    {
        // The footgun: a naive launcher lets the shell split the spaced path into
        // several argv elements. The reader then sees only the first fragment,
        // which is NOT a descriptor file — proving why the launcher must pass the
        // path as a single, correctly-quoted argument.
        string[] split = { "--test-isolation", @"C:\Users\me\OneDrive", "-", "Microsoft\\PAX", "Cookbook\\desc.json" };

        Assert.Equal(@"C:\Users\me\OneDrive", TestIsolationRuntime.ReadDescriptorArg(split));
    }

    [Fact]
    public void ReadDescriptorArg_Absent_ReturnsNull()
    {
        string[] args = { "--headless", "--no-window" };

        Assert.Null(TestIsolationRuntime.ReadDescriptorArg(args));
    }

    // ---- Descriptor validity (build-independent, no global mutation) -------

    [Fact]
    public void ValidDescriptor_ParsesAndRoutesEveryRootUnderIsolatedRoot()
    {
        string json = ValidDescriptorJson(@"C:\pax_iso_parse");
        TestIsolationParseResult result = TestIsolationParser.Parse(json);

        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.NotNull(result.Context);
        Assert.True(result.Context!.ContainsPath(result.Context.LocalStateRoot));
        Assert.True(result.Context.ContainsPath(result.Context.Workspace));
        Assert.True(result.Context.ContainsPath(result.Context.InstallRoot));
        Assert.True(result.Context.ContainsPath(result.Context.CoordinationState));
    }

    // ---- Activation gate: build-mode differentiated, no global mutation ----

    [Fact]
    public void TryActivate_InvalidDescriptor_IsBuildGated_AndNeverActivates()
    {
        string bad = Path.Combine(Path.GetTempPath(), "pax_iso_ut_bad_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(bad, "{ not valid json");
        try
        {
            int? exit = TestIsolationRuntime.TryActivate(new[] { "--test-isolation", bad });
#if PAXCOOKBOOK_TEST_ISOLATION
            // A test-isolation build FAILS CLOSED with the distinct nonzero exit.
            Assert.Equal(TestIsolationRuntime.IsolationActivationFailureExit, exit);
#else
            // A stable/customer build ignores the argument and never activates.
            Assert.Null(exit);
#endif
            // Either way, an invalid/ignored descriptor must NEVER enter isolation.
            Assert.False(TestIsolationRuntime.IsActive);
        }
        finally
        {
            try { File.Delete(bad); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TryActivate_ArgumentAbsent_ReturnsNull_AndDoesNotActivate()
    {
        int? exit = TestIsolationRuntime.TryActivate(new[] { "--headless" });

        Assert.Null(exit);
        Assert.False(TestIsolationRuntime.IsActive);
    }

    private static string ValidDescriptorJson(string isolatedRoot)
    {
        string J(string sub) => (isolatedRoot + sub).Replace(@"\", @"\\");
        string root = isolatedRoot.Replace(@"\", @"\\");
        return "{\n" +
            "  \"schemaVersion\": 1,\n" +
            "  \"isolatedRoot\": \"" + root + "\",\n" +
            "  \"installRoot\": \"" + J(@"\install") + "\",\n" +
            "  \"localStateRoot\": \"" + J(@"\lad") + "\",\n" +
            "  \"workspace\": \"" + J(@"\ws") + "\",\n" +
            "  \"webView2Data\": \"" + J(@"\ws\WebView2") + "\",\n" +
            "  \"engineState\": \"" + J(@"\lad\Engine") + "\",\n" +
            "  \"payloadCache\": \"" + J(@"\cache") + "\",\n" +
            "  \"logs\": \"" + J(@"\logs") + "\",\n" +
            "  \"coordinationState\": \"" + J(@"\lad") + "\",\n" +
            "  \"setupTempRoot\": \"" + J(@"\tmp") + "\",\n" +
            "  \"shellIntegrationEnabled\": false,\n" +
            "  \"updateChecksEnabled\": false,\n" +
            "  \"networkPayloadDownloadEnabled\": false,\n" +
            "  \"updateApplyEnabled\": false,\n" +
            "  \"preserveIsolationOnRelaunch\": true,\n" +
            "  \"installedProductDiscoveryEnabled\": false\n" +
            "}";
    }
}
