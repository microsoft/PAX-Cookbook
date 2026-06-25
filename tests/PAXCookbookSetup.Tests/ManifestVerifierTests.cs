using System;
using System.IO;
using PAXCookbookSetup.Gui;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Unit tests for ManifestVerifier — the versions.json parser, SHA-256 helper,
// and host allow-list for the manifest URL. Pure, offline; no network.
public class ManifestVerifierTests
{
    private const string ValidManifest = """
    {
      "schemaVersion": 1,
      "current": {
        "version": "1.0.0",
        "payload": {
          "filename": "PAX_Cookbook_Payload.zip",
          "sha256": "88FD78E55B351441F215D07418246B3052B9D9D14540A91AFAFD1601885A86CF",
          "size": 19996545
        },
        "engine": {
          "version": "1.11.9",
          "sha256": "007ad1a7f6d40b40e873c684d10b2a79b4d1dd03a1900ade19b6e482cc10c728"
        },
        "minimumSetupVersion": "1.0.0"
      }
    }
    """;

    [Fact]
    public void Parse_ValidManifest_ReturnsExpectationLowercased()
    {
        var e = ManifestVerifier.Parse(ValidManifest);
        Assert.NotNull(e);
        Assert.Equal("88fd78e55b351441f215d07418246b3052b9d9d14540a91afafd1601885a86cf", e!.Sha256);
        Assert.Equal(19996545L, e.Size);
        Assert.Equal("1.0.0", e.Version);
    }

    [Fact]
    public void Parse_MissingCurrent_ReturnsNull()
        => Assert.Null(ManifestVerifier.Parse("""{ "schemaVersion": 1 }"""));

    [Fact]
    public void Parse_MissingPayload_ReturnsNull()
        => Assert.Null(ManifestVerifier.Parse("""{ "current": { "version": "1.0.0" } }"""));

    [Fact]
    public void Parse_NoShaNoSize_ReturnsNull()
        => Assert.Null(ManifestVerifier.Parse("""{ "current": { "payload": { "filename": "x.zip" } } }"""));

    [Fact]
    public void Parse_ShaOnly_ReturnsExpectationWithNullSize()
    {
        var e = ManifestVerifier.Parse(
            """{ "current": { "payload": { "sha256": "ABC123" } } }""");
        Assert.NotNull(e);
        Assert.Equal("abc123", e!.Sha256);
        Assert.Null(e.Size);
    }

    [Fact]
    public void Parse_SizeOnly_ReturnsExpectationWithNullSha()
    {
        var e = ManifestVerifier.Parse(
            """{ "current": { "payload": { "size": 1234 } } }""");
        Assert.NotNull(e);
        Assert.Null(e!.Sha256);
        Assert.Equal(1234L, e.Size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ broken")]
    [InlineData("[]")]
    [InlineData("42")]
    public void Parse_Garbage_ReturnsNull(string json)
        => Assert.Null(ManifestVerifier.Parse(json));

    [Fact]
    public void ComputeSha256_MatchesKnownVector()
    {
        // SHA-256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
        var tmp = Path.Combine(Path.GetTempPath(), "pax_sha_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x61, 0x62, 0x63 }); // "abc"
            Assert.Equal(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                ManifestVerifier.ComputeSha256(tmp));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void ManifestUrl_IsAllowedHost()
        => Assert.True(PrereqDownloadHosts.IsAllowed(ManifestVerifier.ManifestUrl));
}
