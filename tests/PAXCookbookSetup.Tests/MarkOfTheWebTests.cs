using System;
using System.IO;
using PAXCookbookSetup;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Exercises MarkOfTheWeb against real NTFS alternate data streams under %TEMP%.
// The Zone.Identifier stream is the OS "Mark of the Web" applied to internet
// downloads; stripping it is what unblocks the installed PAX Cookbook.exe.
public class MarkOfTheWebTests
{
    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "PAXMOTW_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // True when the file carries a Zone.Identifier alternate data stream.
    private static bool HasZone(string path)
    {
        try
        {
            using var _ = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read);
            return true;
        }
        catch { return false; }
    }

    private static void WriteZone(string path)
        => File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

    [Fact]
    public void StripFile_RemovesZoneIdentifier_KeepsContent()
    {
        var dir = NewTempDir();
        try
        {
            var f = Path.Combine(dir, "PAX Cookbook.exe");
            File.WriteAllText(f, "primary-content");
            WriteZone(f);
            Assert.True(HasZone(f));

            var removed = MarkOfTheWeb.StripFile(f);

            Assert.True(removed);
            Assert.False(HasZone(f));
            Assert.True(File.Exists(f));
            Assert.Equal("primary-content", File.ReadAllText(f));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StripFile_NoStream_ReturnsFalse_KeepsFile()
    {
        var dir = NewTempDir();
        try
        {
            var f = Path.Combine(dir, "clean.dll");
            File.WriteAllText(f, "x");

            Assert.False(MarkOfTheWeb.StripFile(f));
            Assert.True(File.Exists(f));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StripTree_RemovesZoneFromEveryFile()
    {
        var dir = NewTempDir();
        try
        {
            var bin = Path.Combine(dir, "App", "bin");
            Directory.CreateDirectory(bin);
            var exe = Path.Combine(bin, "PAX Cookbook.exe");
            var dll = Path.Combine(bin, "Some.dll");
            var setup = Path.Combine(dir, "Setup", "PAXCookbookSetup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
            foreach (var p in new[] { exe, dll, setup })
            {
                File.WriteAllText(p, "c");
                WriteZone(p);
                Assert.True(HasZone(p));
            }

            var visited = MarkOfTheWeb.StripTree(dir);

            Assert.Equal(3, visited);
            Assert.False(HasZone(exe));
            Assert.False(HasZone(dll));
            Assert.False(HasZone(setup));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Strip_NullEmptyOrMissing_DoesNotThrow()
    {
        Assert.False(MarkOfTheWeb.StripFile(null!));
        Assert.False(MarkOfTheWeb.StripFile(""));
        Assert.False(MarkOfTheWeb.StripFile(
            Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N") + ".exe")));
        Assert.Equal(0, MarkOfTheWeb.StripTree(
            Path.Combine(Path.GetTempPath(), "missingdir_" + Guid.NewGuid().ToString("N"))));
    }
}
