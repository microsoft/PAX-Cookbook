using System.IO;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup;
using PAXCookbookSetup.Uninstall;
using PAXCookbookSetup.Verbs;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Upgrade / reinstall over an existing installation must succeed without the
// old "exit code 50" failure: the running app is stopped first, existing files
// are overwritten, stale binaries are pruned, and user-facing state (workspace
// path, original install time) is preserved.
public class UpgradeReinstallTests
{
    private static string AppExePath(string installRoot)
        => Path.Combine(installRoot, "App", "bin", "PAXCookbook.exe");

    [Fact]
    public void FreshInstall_Succeeds()
    {
        var (installRoot, payloadRoot, m, scope) = TestFixture.BuildPayload();
        using (scope)
        {
            var log = TestFixture.NewLogger(installRoot);
            // A stop on a fresh install finds nothing running and is a safe no-op.
            var stopper = new RecordingAppStopper();
            var rc = InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                                     m, payloadRoot, installRoot, log, appStopper: stopper);
            Assert.Equal(SetupExitCodes.Ok, rc);
            Assert.True(File.Exists(AppExePath(installRoot)));
        }
    }

    [Fact]
    public void Reinstall_StopsRunningApp_BeforeOverwriting()
    {
        var (installRoot, payloadRoot, m, scope) = TestFixture.BuildPayload();
        using (scope)
        {
            var log = TestFixture.NewLogger(installRoot);
            // First install creates the install root.
            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);

            // Second install (reinstall) must stop the existing install first.
            var stopper = new RecordingAppStopper();
            var rc = InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                                     m, payloadRoot, installRoot, log, appStopper: stopper);
            Assert.Equal(SetupExitCodes.Ok, rc);
            Assert.Single(stopper.Calls);
            Assert.Equal(installRoot, stopper.Calls[0].InstallRoot);
        }
    }

    [Fact]
    public void Reinstall_DifferentVersion_Overwrites_AndSucceeds()
    {
        var (installRoot, p1, m1, scope1) = TestFixture.BuildPayload("1.0.0");
        using (scope1)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, p1), m1, p1, installRoot, log);
            Assert.Equal("fake-app-1.0.0", File.ReadAllText(AppExePath(installRoot)));

            var (_, p2, m2, scope2) = TestFixture.BuildPayload("1.1.0");
            using (scope2)
            {
                var rc = InstallVerb.Run(TestFixture.Args("install", installRoot, p2),
                                         m2, p2, installRoot, log);
                Assert.Equal(SetupExitCodes.Ok, rc);
                Assert.Equal("fake-app-1.1.0", File.ReadAllText(AppExePath(installRoot)));
                var s = InstallStateStore.Load(installRoot);
                Assert.Equal("1.1.0", s.AppVersion);
            }
        }
    }

    [Fact]
    public void Reinstall_PreservesWorkspacePath()
    {
        var (installRoot, payloadRoot, m, scope) = TestFixture.BuildPayload();
        using (scope)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);

            // Simulate the app recording a custom workspace location after install.
            const string custom = @"D:\My PAX Data\Workspace";
            var s0 = InstallStateStore.Load(installRoot);
            InstallStateStore.Save(installRoot, s0 with { WorkspaceFolderPath = custom });

            // Reinstall must not reset the recorded workspace.
            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);
            var s1 = InstallStateStore.Load(installRoot);
            Assert.Equal(custom, s1.WorkspaceFolderPath);
        }
    }

    [Fact]
    public void Reinstall_PreservesOriginalInstallTime()
    {
        var (installRoot, payloadRoot, m, scope) = TestFixture.BuildPayload();
        using (scope)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);
            var first = InstallStateStore.Load(installRoot).InstalledAtUtc;

            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);
            var second = InstallStateStore.Load(installRoot);
            Assert.Equal(first, second.InstalledAtUtc);
        }
    }

    [Fact]
    public void Reinstall_RemovesStaleBinFile_ButKeepsCurrent()
    {
        var (installRoot, payloadRoot, m, scope) = TestFixture.BuildPayload();
        using (scope)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);

            // A leftover DLL from a previous version that is not in the new payload.
            var stale = Path.Combine(installRoot, "App", "bin", "OldDependency.dll");
            File.WriteAllText(stale, "stale");
            Assert.True(File.Exists(stale));

            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);

            Assert.False(File.Exists(stale));               // stale binary pruned
            Assert.True(File.Exists(AppExePath(installRoot))); // current binary kept
        }
    }

    [Fact]
    public void Reinstall_PreservesUserDataDirectories()
    {
        var (installRoot, payloadRoot, m, scope) = TestFixture.BuildPayload();
        using (scope)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);

            // Stand in for the user's database / recipes under the workspace.
            var dbDir = Path.Combine(installRoot, "Workspace", "Database");
            Directory.CreateDirectory(dbDir);
            var db = Path.Combine(dbDir, "workspace.db");
            File.WriteAllText(db, "user-recipes-and-history");

            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);

            Assert.True(File.Exists(db));
            Assert.Equal("user-recipes-and-history", File.ReadAllText(db));
        }
    }
}
