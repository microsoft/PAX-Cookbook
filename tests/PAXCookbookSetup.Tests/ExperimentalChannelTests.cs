using PAXCookbookSetup;
using PAXCookbookSetup.Gui;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Unit tests for the experimental (test-build) distribution channel: the
// fail-safe channel normalizer, pure download-route decision, and the
// newest-pre-release asset locator. Pure, offline; no network.
public class ExperimentalChannelTests
{
    // ---------------- SetupChannel.Normalize / IsExperimental ----------------

    [Theory]
    [InlineData("experimental")]
    [InlineData("Experimental")]
    [InlineData("EXPERIMENTAL")]
    [InlineData("  experimental  ")]
    public void Normalize_Experimental_Variants_ResolveToExperimental(string raw)
    {
        Assert.Equal(SetupChannel.Experimental, SetupChannel.Normalize(raw));
        Assert.True(SetupChannel.IsExperimental(raw));
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("Internal")]
    [InlineData("  INTERNAL  ")]
    public void Normalize_Internal_Variants_ResolveToInternal(string raw)
    {
        Assert.Equal(SetupChannel.Internal, SetupChannel.Normalize(raw));
        Assert.True(SetupChannel.IsInternal(raw));
        Assert.False(SetupChannel.IsExperimental(raw));
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("Stable")]
    [InlineData("unknown")]
    [InlineData("exp")]
    [InlineData("experiment")]
    [InlineData("prerelease")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_NonExperimental_ResolvesToStable(string? raw)
    {
        Assert.Equal(SetupChannel.Stable, SetupChannel.Normalize(raw));
        Assert.False(SetupChannel.IsExperimental(raw));
    }

    [Fact]
    public void Resolve_DefaultBuild_IsStable()
    {
        // The test assembly is built with the default PaxChannel ('stable'),
        // so a plain (non-experimental) build must resolve to stable.
        Assert.Equal(SetupChannel.Stable, SetupChannel.Resolve());
    }

    [Theory]
    [InlineData("stable", PayloadDownloader.DownloadRoute.Stable)]
    [InlineData("experimental", PayloadDownloader.DownloadRoute.Experimental)]
    [InlineData("internal", PayloadDownloader.DownloadRoute.Unavailable)]
    [InlineData("unknown", PayloadDownloader.DownloadRoute.Stable)]
    [InlineData(null, PayloadDownloader.DownloadRoute.Stable)]
    public void ResolveDownloadRoute_IsClosedAndInternalIsUnavailable(
        string? channel,
        PayloadDownloader.DownloadRoute expected)
    {
        Assert.Equal(expected, PayloadDownloader.ResolveDownloadRoute(channel));
    }

    // ---------------- ExperimentalReleaseLocator.Parse ----------------

    private const string ExpAsset =
        "https://github.com/microsoft/PAX-Cookbook/releases/download";

    private static string Release(string tag, bool prerelease, bool draft, string createdAt,
                                  bool withPayload = true, bool withManifest = true,
                                  string? payloadHost = null)
    {
        string host = payloadHost ?? $"{ExpAsset}/{tag}";
        string payload = withPayload
            ? $$"""{ "name": "PAX_Cookbook_Payload.zip", "browser_download_url": "{{host}}/PAX_Cookbook_Payload.zip" }"""
            : $$"""{ "name": "other.zip", "browser_download_url": "{{host}}/other.zip" }""";
        string manifest = withManifest
            ? $$""", { "name": "versions.json", "browser_download_url": "{{host}}/versions.json" }"""
            : "";
        return $$"""
        {
          "tag_name": "{{tag}}",
          "prerelease": {{(prerelease ? "true" : "false")}},
          "draft": {{(draft ? "true" : "false")}},
          "created_at": "{{createdAt}}",
          "assets": [ {{payload}}{{manifest}} ]
        }
        """;
    }

    [Fact]
    public void Parse_PicksNewestPrereleaseByCreatedAt_RegardlessOfOrder()
    {
        // exp.1 listed first but older; exp.2 listed second but newer.
        string json = $$"""
        [
          {{Release("v2.0.0-exp.1", prerelease: true, draft: false, "2026-01-01T00:00:00Z")}},
          {{Release("v2.0.0-exp.2", prerelease: true, draft: false, "2026-02-01T00:00:00Z")}}
        ]
        """;

        var located = ExperimentalReleaseLocator.Parse(json);

        Assert.NotNull(located);
        Assert.Contains("v2.0.0-exp.2/PAX_Cookbook_Payload.zip", located!.PayloadUrl);
        Assert.Contains("v2.0.0-exp.2/versions.json", located.ManifestUrl);
    }

    [Fact]
    public void Parse_IgnoresStableReleases_EvenWhenNewer()
    {
        // A newer NON-pre-release must never be selected.
        string json = $$"""
        [
          {{Release("v2.0.0-exp.1", prerelease: true, draft: false, "2026-01-01T00:00:00Z")}},
          {{Release("v2.0.0", prerelease: false, draft: false, "2026-03-01T00:00:00Z")}}
        ]
        """;

        var located = ExperimentalReleaseLocator.Parse(json);

        Assert.NotNull(located);
        Assert.Contains("v2.0.0-exp.1/PAX_Cookbook_Payload.zip", located!.PayloadUrl);
    }

    [Fact]
    public void Parse_ExcludesDraftPrereleases()
    {
        string json = $$"""
        [ {{Release("v2.0.0-exp.9", prerelease: true, draft: true, "2026-09-01T00:00:00Z")}} ]
        """;
        Assert.Null(ExperimentalReleaseLocator.Parse(json));
    }

    [Fact]
    public void Parse_NoPrereleases_ReturnsNull()
    {
        string json = $$"""
        [ {{Release("v1.0.0", prerelease: false, draft: false, "2026-01-01T00:00:00Z")}} ]
        """;
        Assert.Null(ExperimentalReleaseLocator.Parse(json));
    }

    [Fact]
    public void Parse_MissingPayloadAsset_ReturnsNull()
    {
        string json = $$"""
        [ {{Release("v2.0.0-exp.1", prerelease: true, draft: false, "2026-01-01T00:00:00Z", withPayload: false)}} ]
        """;
        Assert.Null(ExperimentalReleaseLocator.Parse(json));
    }

    [Fact]
    public void Parse_MissingManifestAsset_ReturnsNull()
    {
        string json = $$"""
        [ {{Release("v2.0.0-exp.1", prerelease: true, draft: false, "2026-01-01T00:00:00Z", withManifest: false)}} ]
        """;
        Assert.Null(ExperimentalReleaseLocator.Parse(json));
    }

    [Fact]
    public void Parse_AssetOnDisallowedHost_ReturnsNull()
    {
        string json = $$"""
        [ {{Release("v2.0.0-exp.1", prerelease: true, draft: false, "2026-01-01T00:00:00Z", payloadHost: "https://evil.example.com/x")}} ]
        """;
        Assert.Null(ExperimentalReleaseLocator.Parse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{ \"not\": \"an array\" }")]
    public void Parse_MalformedOrEmpty_ReturnsNull(string? json)
        => Assert.Null(ExperimentalReleaseLocator.Parse(json));
}
