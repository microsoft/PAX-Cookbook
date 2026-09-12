using System;
using System.IO;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Regression for the work-account lock-screen decline (CASE D): the window-side
// bridge options must be resolved DURABLE-FIRST from the same per-user base the
// daemon uses. A durable install writes no WAM environment variables, so an
// environment-only resolution yields an unconfigured, fail-closed bridge that
// rejects before invoking WAM even when the daemon endpoint is fully configured.
// Deterministic; no live WAM/tenant/MSAL dependency. Synthetic identifiers only.
public sealed class WamWindowDurableResolutionTests
{
    private const string TestTenant = "33333333-3333-3333-3333-333333333333";
    private const string TestClient = "44444444-4444-4444-4444-444444444444";

    private static string NewTempBase()
    {
        string path = Path.Combine(Path.GetTempPath(), "paxwamwin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ExperimentalWamConfigInput ValidInput() => new()
    {
        Enabled = true,
        ProviderId = "entra-wam",
        TenantId = TestTenant,
        ClientId = TestClient,
    };

    private static Func<string, string?> NoEnv() => _ => null;

    [Fact]
    public void DurableConfigPresent_ResolvesFullyConfigured_WithoutEnvironment()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamConfigStore.Save(baseDir, ValidInput());

            // Durable-first resolution (what the window factory now uses) must be
            // fully configured so AcquireAsync proceeds to the interactive
            // acquisition instead of failing closed.
            ExperimentalWamOptions options =
                ExperimentalWamHost.ResolveFromSources(baseDir, NoEnv()).Options;

            Assert.True(options.IsFullyConfigured);
            Assert.Equal(TestTenant, options.TenantId);
            Assert.Equal(TestClient, options.ClientId);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void EnvironmentOnlyResolution_WithNoEnvironment_IsNotConfigured()
    {
        // The environment-only resolver ignores the durable file; with no WAM
        // environment variables set (the production shape of a durable install)
        // it yields an unconfigured, fail-closed bridge. The window factory must
        // therefore never use it as its sole source.
        ExperimentalWamOptions envOnly = ExperimentalWamHost.Resolve(NoEnv());
        Assert.False(envOnly.IsFullyConfigured);
    }
}
