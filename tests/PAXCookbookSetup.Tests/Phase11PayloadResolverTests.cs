using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Payload;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Phase 11 — single-EXE setup payload bundle. Unit tests for the
// payload source resolver layer that lets PAXCookbookSetup.exe install
// without an external `--payload-root` directory.
public class Phase11PayloadResolverTests
{
    // ---------- DirectoryPayloadSourceResolver ----------

    [Fact]
    public void Directory_Empty_Fails()
    {
        var r = new DirectoryPayloadSourceResolver("").Resolve();
        Assert.False(r.Success);
        Assert.Equal("directory", r.Origin);
    }

    [Fact]
    public void Directory_Missing_Fails()
    {
        var r = new DirectoryPayloadSourceResolver(
            Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"))).Resolve();
        Assert.False(r.Success);
    }

    [Fact]
    public void Directory_NoManifest_Fails()
    {
        var d = Path.Combine(Path.GetTempPath(), "p11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        try
        {
            var r = new DirectoryPayloadSourceResolver(d).Resolve();
            Assert.False(r.Success);
            Assert.Contains("manifest.json", r.Error ?? "");
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Directory_WithManifest_Succeeds()
    {
        var d = Path.Combine(Path.GetTempPath(), "p11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "manifest.json"), "{}");
        try
        {
            var r = new DirectoryPayloadSourceResolver(d).Resolve();
            Assert.True(r.Success);
            Assert.Equal("directory", r.Origin);
            Assert.Equal(Path.GetFullPath(d), r.PayloadRoot);
        }
        finally { Directory.Delete(d, true); }
    }

    // ---------- EmbeddedPayloadSourceResolver ----------

    [Fact]
    public void Embedded_NullStream_Fails()
    {
        var r = new EmbeddedPayloadSourceResolver(() => null).Resolve();
        Assert.False(r.Success);
        Assert.Equal("embedded", r.Origin);
    }

    [Fact]
    public void Embedded_HappyPath_Extracts_ManifestPresent()
    {
        var zip = BuildZip(entries: new[]
        {
            ("manifest.json", "{\"product\":\"PAXCookbook\"}"),
            ("App/bin/PAXCookbook.exe", "fake-app-bytes"),
            ("PAXCookbookSetup.exe", "fake-setup-bytes"),
        });
        var tempBase = NewTempBase();
        try
        {
            var r = new EmbeddedPayloadSourceResolver(() => new MemoryStream(zip), tempBase).Resolve();
            Assert.True(r.Success, r.Error);
            Assert.Equal("embedded", r.Origin);
            Assert.True(Directory.Exists(r.PayloadRoot));
            Assert.True(File.Exists(Path.Combine(r.PayloadRoot!, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(r.PayloadRoot!, "App", "bin", "PAXCookbook.exe")));
            Assert.True(File.Exists(Path.Combine(r.PayloadRoot!, "PAXCookbookSetup.exe")));
            // Cleanup
            Assert.True(EmbeddedPayloadSourceResolver.TryCleanup(r.TempExtractionRoot));
            Assert.False(Directory.Exists(r.PayloadRoot));
        }
        finally { TryRm(tempBase); }
    }

    [Fact]
    public void Embedded_RejectsParentTraversal()
    {
        var zip = BuildZip(new[]
        {
            ("manifest.json", "{}"),
            ("../escape.txt",  "nope"),
        });
        var tempBase = NewTempBase();
        try
        {
            var r = new EmbeddedPayloadSourceResolver(() => new MemoryStream(zip), tempBase).Resolve();
            Assert.False(r.Success);
            Assert.Contains("..", r.Error ?? "");
            EmbeddedPayloadSourceResolver.TryCleanup(r.TempExtractionRoot);
        }
        finally { TryRm(tempBase); }
    }

    [Fact]
    public void Embedded_RejectsAbsolutePath()
    {
        // Cannot easily build a ZipArchive entry with a rooted name via
        // ZipArchive.CreateEntry (it accepts the name as-is). Use an
        // OS-style absolute path which Path.IsPathRooted will catch.
        var zip = BuildZip(new[]
        {
            ("manifest.json", "{}"),
            (@"C:\evil.txt",  "nope"),
        });
        var tempBase = NewTempBase();
        try
        {
            var r = new EmbeddedPayloadSourceResolver(() => new MemoryStream(zip), tempBase).Resolve();
            Assert.False(r.Success);
            Assert.True((r.Error ?? "").Contains("absolute") || (r.Error ?? "").Contains("drive"));
            EmbeddedPayloadSourceResolver.TryCleanup(r.TempExtractionRoot);
        }
        finally { TryRm(tempBase); }
    }

    [Fact]
    public void Embedded_MissingManifest_Fails()
    {
        var zip = BuildZip(new[] { ("just-a-file.txt", "hi") });
        var tempBase = NewTempBase();
        try
        {
            var r = new EmbeddedPayloadSourceResolver(() => new MemoryStream(zip), tempBase).Resolve();
            Assert.False(r.Success);
            Assert.Contains("manifest.json", r.Error ?? "");
            EmbeddedPayloadSourceResolver.TryCleanup(r.TempExtractionRoot);
        }
        finally { TryRm(tempBase); }
    }

    [Fact]
    public void TraversalCheck_Helpers()
    {
        Assert.NotNull(EmbeddedPayloadSourceResolver.TraversalCheck(@"..\evil.txt"));
        Assert.NotNull(EmbeddedPayloadSourceResolver.TraversalCheck("../evil.txt"));
        Assert.NotNull(EmbeddedPayloadSourceResolver.TraversalCheck("a/b/../c"));
        Assert.NotNull(EmbeddedPayloadSourceResolver.TraversalCheck(@"C:\foo.txt"));
        Assert.NotNull(EmbeddedPayloadSourceResolver.TraversalCheck(@"\\server\share"));
        Assert.Null(EmbeddedPayloadSourceResolver.TraversalCheck("manifest.json"));
        Assert.Null(EmbeddedPayloadSourceResolver.TraversalCheck("App/bin/PAXCookbook.exe"));
        Assert.Null(EmbeddedPayloadSourceResolver.TraversalCheck(@"Setup\dep.dll"));
    }

    // ---------- PayloadManifestVerifier ----------

    [Fact]
    public void Verifier_DetectsMissing_HashMismatch_SizeMismatch()
    {
        var d = Path.Combine(Path.GetTempPath(), "p11v-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(d, "App", "bin"));
        Directory.CreateDirectory(Path.Combine(d, "Setup"));
        try
        {
            var appExeBytes = Encoding.UTF8.GetBytes("hello-app");
            File.WriteAllBytes(Path.Combine(d, "App", "bin", "PAXCookbook.exe"), appExeBytes);
            var setupBytes = Encoding.UTF8.GetBytes("hello-setup");
            File.WriteAllBytes(Path.Combine(d, "PAXCookbookSetup.exe"), setupBytes);
            var ok = new Manifest
            {
                Payload = new ManifestPayload
                {
                    AppExe = new ManifestAppExe
                    {
                        RelativeInstallPath = "App\\bin\\PAXCookbook.exe",
                        Sha256 = Sha256Hex(appExeBytes),
                        SizeBytes = appExeBytes.Length
                    },
                    SetupExe = new ManifestSetupExe
                    {
                        Name = "PAXCookbookSetup.exe",
                        Sha256 = Sha256Hex(setupBytes),
                        SizeBytes = setupBytes.Length
                    }
                }
            };
            var v = PayloadManifestVerifier.Verify(d, ok);
            Assert.True(v.Ok, string.Join("; ", v.Errors));

            // Bad hash:
            var bad = ok with { Payload = ok.Payload with { AppExe = ok.Payload.AppExe with { Sha256 = new string('0',64) } } };
            var v2 = PayloadManifestVerifier.Verify(d, bad);
            Assert.False(v2.Ok);
            Assert.Contains(v2.Errors, e => e.Contains("sha256"));

            // Bad size:
            var bad2 = ok with { Payload = ok.Payload with { AppExe = ok.Payload.AppExe with { SizeBytes = 999999 } } };
            var v3 = PayloadManifestVerifier.Verify(d, bad2);
            Assert.False(v3.Ok);
            Assert.Contains(v3.Errors, e => e.Contains("size"));

            // Missing required file: the App EXE is always required. (The Setup
            // EXE is metadata only and is not shipped in the payload, so deleting
            // it would NOT be an error in the bootstrapper model.)
            File.Delete(Path.Combine(d, "App", "bin", "PAXCookbook.exe"));
            var v4 = PayloadManifestVerifier.Verify(d, ok);
            Assert.False(v4.Ok);
            Assert.Contains(v4.Errors, e => e.Contains("missing"));
        }
        finally { TryRm(d); }
    }

    // ---------- helpers ----------

    private static byte[] BuildZip((string Name, string Body)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (n, b) in entries)
            {
                var e = z.CreateEntry(n);
                using var es = e.Open();
                var bytes = Encoding.UTF8.GetBytes(b);
                es.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }

    private static string NewTempBase()
    {
        var d = Path.Combine(Path.GetTempPath(), "p11rb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void TryRm(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }

    private static string Sha256Hex(byte[] b)
    {
        using var s = System.Security.Cryptography.SHA256.Create();
        var h = s.ComputeHash(b);
        return Convert.ToHexString(h).ToLowerInvariant();
    }
}
