using System.IO;
using System.Linq;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup;
using PAXCookbookSetup.Shell;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Destructive-safety guard tests for the Setup-side test-isolation fix that do
// not require the PAXCOOKBOOK_TEST_ISOLATION activation symbol (the gated
// invariant behavior itself is exercised by the isolation build + smoke). These
// prove the always-compiled surfaces: argument capture, relaunch preserving the
// isolation descriptor, truthful shortcut manifests, and verb classification.
public sealed class TestIsolationGuardTests
{
    [Fact]
    public void ArgParser_Captures_TestIsolation_Descriptor()
    {
        var parsed = ArgParser.Parse(new[]
        {
            "apply-update",
            "--install-root", @"C:\iso\install",
            "--test-isolation", @"C:\iso\desc.json",
        });
        Assert.Empty(parsed.Errors);
        Assert.Equal(@"C:\iso\desc.json", parsed.TestIsolationDescriptor);
        Assert.Equal(@"C:\iso\install", parsed.InstallRootOverride);
    }

    [Fact]
    public void SelfHandoff_Relaunch_Preserves_IsolationDescriptor()
    {
        var parsed = ArgParser.Parse(new[]
        {
            "apply-update",
            "--install-root", @"C:\iso\install",
            "--test-isolation", @"C:\iso\desc.json",
        });

        var args = SelfHandoff.BuildHandoffArgs(parsed, @"C:\Temp\handoff", @"C:\iso\install");

        int idx = args.IndexOf("--test-isolation");
        Assert.True(idx >= 0, "handoff args must forward --test-isolation");
        Assert.Equal(@"C:\iso\desc.json", args[idx + 1]);
        // And the install root is still forwarded so the child targets the
        // isolated tree, never a rediscovered real default.
        int ridx = args.IndexOf("--install-root");
        Assert.True(ridx >= 0);
        Assert.Equal(@"C:\iso\install", args[ridx + 1]);
    }

    [Fact]
    public void SuppressedShortcuts_Produce_Empty_TruthfulManifest()
    {
        string installRoot = Path.Combine(Path.GetTempPath(),
            "paxiso-manifest-" + System.Guid.NewGuid().ToString("N"));
        string startFolder = Path.Combine(installRoot, "FakeStartMenu");
        string desktop = Path.Combine(installRoot, "FakeDesktop");
        Directory.CreateDirectory(installRoot);
        try
        {
            var registrar = new ShellRegistrar(
                new NoOpShortcutWriter(),
                new ShortcutManifestStore(),
                startMenuFolderProvider: () => startFolder,
                desktopFolderProvider: () => desktop);

            var opt = registrar.DefaultOptions(installRoot, "2.0.0")
                with { CreateDesktopShortcut = true };
            var result = registrar.Install(opt);

            // Suppressed writes must NOT be recorded as created shortcuts.
            Assert.Equal(0, result.ShortcutsCreated);
            Assert.Empty(result.Manifest.Shortcuts);

            // The persisted manifest on disk must also be truthful (no shortcuts).
            var loaded = new ShortcutManifestStore().TryLoad(installRoot);
            Assert.NotNull(loaded);
            Assert.Empty(loaded!.Shortcuts);
        }
        finally
        {
            try { Directory.Delete(installRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RealWrites_Are_Recorded_InManifest()
    {
        // Control: a writer that DOES create shortcuts records them, proving the
        // truthful-manifest change only suppresses no-op writes.
        string installRoot = Path.Combine(Path.GetTempPath(),
            "paxiso-manifest2-" + System.Guid.NewGuid().ToString("N"));
        string startFolder = Path.Combine(installRoot, "FakeStartMenu");
        Directory.CreateDirectory(installRoot);
        try
        {
            var registrar = new ShellRegistrar(
                new InMemoryShortcutWriter(),
                new ShortcutManifestStore(),
                startMenuFolderProvider: () => startFolder,
                desktopFolderProvider: () => Path.Combine(installRoot, "FakeDesktop"));

            var result = registrar.Install(registrar.DefaultOptions(installRoot, "2.0.0"));
            Assert.True(result.ShortcutsCreated > 0);
            Assert.NotEmpty(result.Manifest.Shortcuts);
        }
        finally
        {
            try { Directory.Delete(installRoot, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("install", true)]
    [InlineData("update", true)]
    [InlineData("repair", true)]
    [InlineData("apply-update", true)]
    [InlineData("uninstall", true)]
    [InlineData("status", false)]
    [InlineData("version", false)]
    public void IsMutatingVerb_Classifies_Correctly(string verb, bool expected)
    {
        Assert.Equal(expected, SetupTestIsolation.IsMutatingVerb(verb));
    }
}
