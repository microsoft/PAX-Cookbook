using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Unit tests for the pure releases-selector + host validator behind the
// experimental update-check broker route (ExperimentalUpdateProxy). The network
// fetch itself is not unit-tested (it is thin, deterministic I/O); these cover
// the selection logic and the GitHub-host gate that decide what the route does.
public class ExperimentalUpdateProxyTests
{
    private const string ExpUrl =
        "https://github.com/microsoft/PAX-Cookbook/releases/download/v2.0.0-exp.1/versions.json";

    [Fact]
    public void SelectNewestManifestUrl_PicksNewestPrereleaseByCreatedAt()
    {
        // Two pre-releases; exp.2 is newer by created_at even though it appears
        // second in the array. Selection must be by created_at, not order.
        string json = """
        [
          {
            "prerelease": true, "draft": false, "created_at": "2026-07-07T06:00:00Z",
            "assets": [ { "name": "versions.json", "browser_download_url": "https://github.com/microsoft/PAX-Cookbook/releases/download/v2.0.0-exp.1/versions.json" } ]
          },
          {
            "prerelease": true, "draft": false, "created_at": "2026-07-08T06:00:00Z",
            "assets": [ { "name": "versions.json", "browser_download_url": "https://github.com/microsoft/PAX-Cookbook/releases/download/v2.0.0-exp.2/versions.json" } ]
          }
        ]
        """;

        ExperimentalUpdateProxy.Selection sel = ExperimentalUpdateProxy.SelectNewestManifestUrl(json);

        Assert.Equal(ExperimentalUpdateProxy.SelectState.Ok, sel.State);
        Assert.Equal(
            "https://github.com/microsoft/PAX-Cookbook/releases/download/v2.0.0-exp.2/versions.json",
            sel.ManifestUrl);
    }

    [Fact]
    public void SelectNewestManifestUrl_SingleExp1_ReturnsItsManifest_AlreadyLatestCase()
    {
        // The real current state: exactly one pre-release (v2.0.0-exp.1), which
        // is what an experimental install is running. The route must resolve to
        // its versions.json so the SPA compares equal -> "on the latest
        // experimental build", NOT an error.
        string json = $$"""
        [
          {
            "tag_name": "v2.0.0-exp.1", "prerelease": true, "draft": false,
            "created_at": "2026-07-07T06:00:00Z",
            "assets": [
              { "name": "PAX_Cookbook_Setup.exe", "browser_download_url": "https://github.com/microsoft/PAX-Cookbook/releases/download/v2.0.0-exp.1/PAX_Cookbook_Setup.exe" },
              { "name": "PAX_Cookbook_Payload.zip", "browser_download_url": "https://github.com/microsoft/PAX-Cookbook/releases/download/v2.0.0-exp.1/PAX_Cookbook_Payload.zip" },
              { "name": "versions.json", "browser_download_url": "{{ExpUrl}}" }
            ]
          },
          {
            "tag_name": "v1.3.2", "prerelease": false, "draft": false,
            "created_at": "2026-07-06T18:17:21Z", "assets": []
          }
        ]
        """;

        ExperimentalUpdateProxy.Selection sel = ExperimentalUpdateProxy.SelectNewestManifestUrl(json);

        Assert.Equal(ExperimentalUpdateProxy.SelectState.Ok, sel.State);
        Assert.Equal(ExpUrl, sel.ManifestUrl);
    }

    [Fact]
    public void SelectNewestManifestUrl_IgnoresDraftsAndNonPrereleases()
    {
        // A draft pre-release (newest) and a stable release must both be ignored;
        // only the non-draft pre-release counts.
        string json = """
        [
          { "prerelease": true, "draft": true, "created_at": "2026-07-09T06:00:00Z",
            "assets": [ { "name": "versions.json", "browser_download_url": "https://github.com/x/y/releases/download/draft/versions.json" } ] },
          { "prerelease": false, "draft": false, "created_at": "2026-07-08T06:00:00Z",
            "assets": [ { "name": "versions.json", "browser_download_url": "https://github.com/x/y/releases/download/stable/versions.json" } ] },
          { "prerelease": true, "draft": false, "created_at": "2026-07-07T06:00:00Z",
            "assets": [ { "name": "versions.json", "browser_download_url": "https://github.com/microsoft/PAX-Cookbook/releases/download/v2.0.0-exp.1/versions.json" } ] }
        ]
        """;

        ExperimentalUpdateProxy.Selection sel = ExperimentalUpdateProxy.SelectNewestManifestUrl(json);

        Assert.Equal(ExperimentalUpdateProxy.SelectState.Ok, sel.State);
        Assert.Equal(ExpUrl, sel.ManifestUrl);
    }

    [Fact]
    public void SelectNewestManifestUrl_NoPrerelease_ReturnsNoPrerelease()
    {
        string json = """
        [ { "prerelease": false, "draft": false, "created_at": "2026-07-06T18:17:21Z", "assets": [] } ]
        """;

        ExperimentalUpdateProxy.Selection sel = ExperimentalUpdateProxy.SelectNewestManifestUrl(json);

        Assert.Equal(ExperimentalUpdateProxy.SelectState.NoPrerelease, sel.State);
        Assert.Null(sel.ManifestUrl);
    }

    [Fact]
    public void SelectNewestManifestUrl_EmptyArray_ReturnsNoPrerelease()
    {
        ExperimentalUpdateProxy.Selection sel = ExperimentalUpdateProxy.SelectNewestManifestUrl("[]");
        Assert.Equal(ExperimentalUpdateProxy.SelectState.NoPrerelease, sel.State);
    }

    [Fact]
    public void SelectNewestManifestUrl_PrereleaseMissingManifestAsset_ReturnsBadResponse()
    {
        string json = """
        [ { "prerelease": true, "draft": false, "created_at": "2026-07-07T06:00:00Z",
            "assets": [ { "name": "PAX_Cookbook_Payload.zip", "browser_download_url": "https://github.com/x/y/releases/download/v/PAX_Cookbook_Payload.zip" } ] } ]
        """;

        ExperimentalUpdateProxy.Selection sel = ExperimentalUpdateProxy.SelectNewestManifestUrl(json);

        Assert.Equal(ExperimentalUpdateProxy.SelectState.BadResponse, sel.State);
        Assert.Null(sel.ManifestUrl);
    }

    [Fact]
    public void SelectNewestManifestUrl_ManifestAssetOnDisallowedHost_ReturnsBadResponse()
    {
        // A spoofed asset URL pointing off GitHub must be rejected (treated as no
        // usable manifest), never returned as a fetch target.
        string json = """
        [ { "prerelease": true, "draft": false, "created_at": "2026-07-07T06:00:00Z",
            "assets": [ { "name": "versions.json", "browser_download_url": "https://evil.example.com/versions.json" } ] } ]
        """;

        ExperimentalUpdateProxy.Selection sel = ExperimentalUpdateProxy.SelectNewestManifestUrl(json);

        Assert.Equal(ExperimentalUpdateProxy.SelectState.BadResponse, sel.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"not\":\"an array\"}")]
    public void SelectNewestManifestUrl_MalformedOrNonArray_ReturnsBadResponse(string? json)
    {
        ExperimentalUpdateProxy.Selection sel = ExperimentalUpdateProxy.SelectNewestManifestUrl(json);
        Assert.Equal(ExperimentalUpdateProxy.SelectState.BadResponse, sel.State);
    }

    [Theory]
    [InlineData("https://github.com/microsoft/PAX-Cookbook/releases/download/v/versions.json", true)]
    [InlineData("https://api.github.com/repos/microsoft/PAX-Cookbook/releases", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/x", true)]
    [InlineData("https://raw.githubusercontent.com/microsoft/PAX-Cookbook/main/versions.json", true)]
    [InlineData("https://codeload.github.com/x/y/zip", true)]
    [InlineData("https://evil.example.com/versions.json", false)]
    [InlineData("https://github.com.evil.com/versions.json", false)]
    [InlineData("http://github.com/x/versions.json", false)]
    [InlineData("ftp://github.com/x", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsGitHubHost_AllowsOnlyHttpsGitHubHosts(string? url, bool expected)
    {
        Assert.Equal(expected, ExperimentalUpdateProxy.IsGitHubHost(url));
    }
}
