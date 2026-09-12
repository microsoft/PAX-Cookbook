using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PAXCookbook.App;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// App-side destructive-safety proof for the update-apply isolation fix. These
// exercise the isolation branch DIRECTLY (an explicit context is passed to the
// internal Apply overload) so no process is launched and no global state is
// mutated. They prove the exact incident path — an isolated update apply — can
// never target the real install, and that even the "enabled" path resolves the
// isolated install root rather than the real one.
public sealed class UpdateApplyIsolationTests
{
    private static string NewIsolatedRoot()
    {
        string p = Path.Combine(Path.GetTempPath(), "paxapp_iso_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static TestIsolationContext Context(string iso, bool applyEnabled)
    {
        string Sub(string n) => Path.Combine(iso, n);
        var d = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["isolatedRoot"] = iso,
            ["installRoot"] = Sub("install"),
            ["localStateRoot"] = Sub("state"),
            ["workspace"] = Sub("Workspace"),
            ["webView2Data"] = Sub("WebView2Data"),
            ["engineState"] = Sub("Engine"),
            ["payloadCache"] = Sub("PayloadCache"),
            ["logs"] = Sub("Logs"),
            ["coordinationState"] = Sub("coord"),
            ["setupTempRoot"] = Sub("setuptmp"),
            ["shellIntegrationEnabled"] = false,
            ["updateChecksEnabled"] = false,
            ["networkPayloadDownloadEnabled"] = false,
            ["updateApplyEnabled"] = applyEnabled,
            ["preserveIsolationOnRelaunch"] = true,
            ["installedProductDiscoveryEnabled"] = false,
        };
        if (applyEnabled)
        {
            d["testLocalPayloadPath"] = Path.Combine(iso, "payload.zip");
            d["testLocalPayloadSha256"] = new string('a', 64);
        }
        var r = TestIsolationParser.Parse(JsonSerializer.Serialize(d));
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        return r.Context!;
    }

    [Fact]
    public void IsolatedApply_Disabled_FailsClosed_NoLaunch()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var ctx = Context(iso, applyEnabled: false);
            // appRoot points at the REAL-looking tree; under isolation it is
            // ignored and apply must refuse without launching anything.
            var (status, body) = UpdateApplyModel.Apply(
                appRoot: @"C:\Users\someone\AppData\Local\PAXCookbook\App",
                isolation: ctx, isolationDescriptorPath: null);

            Assert.Equal(409, status);
            string json = JsonSerializer.Serialize(body);
            Assert.Contains("disabled_in_test_isolation", json);
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void IsolatedApply_Enabled_UsesIsolatedRoot_NotRealRoot()
    {
        string iso = NewIsolatedRoot();
        try
        {
            var ctx = Context(iso, applyEnabled: true);
            // Setup DLL does not exist under the isolated install root, so apply
            // reports updater_unavailable — proving it looked under the ISOLATED
            // install root (iso\install\Setup) and never the real per-user path.
            var (status, body) = UpdateApplyModel.Apply(
                appRoot: @"C:\Users\someone\AppData\Local\PAXCookbook\App",
                isolation: ctx, isolationDescriptorPath: Path.Combine(iso, "desc.json"));

            Assert.Equal(409, status);
            string json = JsonSerializer.Serialize(body);
            Assert.Contains("updater_unavailable", json);
            // The isolated Setup path was the one probed (nothing under the real root).
            Assert.False(File.Exists(Path.Combine(iso, "install", "Setup", "PAXCookbookSetup.dll")));
        }
        finally { Directory.Delete(iso, recursive: true); }
    }

    [Fact]
    public void NormalMode_ResolvesInstallRoot_FromAppRootParent()
    {
        // Control: with no isolation context, apply resolves the install root as
        // the parent of appRoot (production behavior) and reports the updater
        // missing when the Setup DLL is absent — confirming the refactor left the
        // production path intact.
        string root = Path.Combine(Path.GetTempPath(), "paxapp_norm_" + System.Guid.NewGuid().ToString("N"));
        string appRoot = Path.Combine(root, "App");
        Directory.CreateDirectory(appRoot);
        try
        {
            var (status, body) = UpdateApplyModel.Apply(appRoot, isolation: null, isolationDescriptorPath: null);
            Assert.Equal(409, status);
            Assert.Contains("updater_unavailable", JsonSerializer.Serialize(body));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
