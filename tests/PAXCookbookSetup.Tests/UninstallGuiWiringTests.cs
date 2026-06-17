using System;
using System.Collections.Generic;
using PAXCookbookSetup;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Uninstall;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Slice E: the GUI uninstall path. The GUI form itself is validated in the
// Slice F sandbox; these tests cover the non-GUI seams it relies on — the new
// CLI flags, the handoff-arg forwarding of --gui-uninstall, and the staged
// progress callback threaded through UninstallOperations.
public class UninstallGuiWiringTests
{
    // ---- CLI flags ----
    [Fact]
    public void ArgParser_ParsesQuietAndSilentAsQuiet()
    {
        Assert.True(ArgParser.Parse(new[] { "uninstall", "--quiet" }).Quiet);
        Assert.True(ArgParser.Parse(new[] { "uninstall", "--silent" }).Quiet);
        Assert.False(ArgParser.Parse(new[] { "uninstall" }).Quiet);
    }

    [Fact]
    public void ArgParser_ParsesGuiUninstall()
    {
        Assert.True(ArgParser.Parse(new[] { "uninstall", "--gui-uninstall" }).GuiUninstall);
        Assert.False(ArgParser.Parse(new[] { "uninstall" }).GuiUninstall);
    }

    [Fact]
    public void ArgParser_NoErrorsForNewFlags()
    {
        var p = ArgParser.Parse(new[] { "uninstall", "--quiet", "--gui-uninstall" });
        Assert.Empty(p.Errors);
    }

    // ---- Handoff forwarding ----
    [Fact]
    public void BuildHandoffArgs_ForwardsGuiUninstall_WhenSet()
    {
        var args = ArgParser.Parse(new[] { "uninstall" }) with { GuiUninstall = true };
        var handoff = SelfHandoff.BuildHandoffArgs(args, @"C:\Temp\h", @"C:\Install");
        Assert.Contains("--gui-uninstall", handoff);
    }

    [Fact]
    public void BuildHandoffArgs_OmitsGuiUninstall_WhenNotSet()
    {
        var args = ArgParser.Parse(new[] { "uninstall" });
        var handoff = SelfHandoff.BuildHandoffArgs(args, @"C:\Temp\h", @"C:\Install");
        Assert.DoesNotContain("--gui-uninstall", handoff);
    }

    // ---- Staged progress through UninstallOperations ----
    private static UninstallOperations BuildOps()
    {
        var stopper = new RecordingAppStopper();                 // Invoked=false -> no abort
        var files = new RecordingFileSystemRemover { PassThrough = true };
        var shellRemover = new ShellRemover(
            new InMemoryShortcutWriter(), new InMemoryRegistryWriter(), new ShortcutManifestStore());
        return new UninstallOperations(stopper, files, shellRemover, new NullTaskbarPinCleaner());
    }

    [Fact]
    public void RunStandard_EmitsStagedProgressInOrder()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "PAXUninstProg_" + Guid.NewGuid().ToString("N").Substring(0, 10));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var messages = new List<string>();
            BuildOps().RunStandard(root, null, msg => messages.Add(msg));

            // The three staged messages must appear, Stopping first and
            // Removing-application-files last.
            Assert.Contains(messages, m => m.StartsWith("Stopping PAX Cookbook"));
            Assert.Contains(messages, m => m.StartsWith("Removing shortcuts"));
            Assert.Contains(messages, m => m.StartsWith("Removing application files"));

            int stopIdx = messages.FindIndex(m => m.StartsWith("Stopping PAX Cookbook"));
            int filesIdx = messages.FindIndex(m => m.StartsWith("Removing application files"));
            Assert.True(stopIdx < filesIdx, "Stopping should be reported before file removal.");
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RunStandard_NullProgress_DoesNotThrow()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "PAXUninstNull_" + Guid.NewGuid().ToString("N").Substring(0, 10));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            // Back-compat: the default (null) progress callback path must work
            // exactly as before (every existing caller passes no progress).
            var r = BuildOps().RunStandard(root);
            Assert.False(r.Aborted);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
