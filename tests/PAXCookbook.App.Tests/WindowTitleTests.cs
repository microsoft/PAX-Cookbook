using System;
using Xunit;

namespace PAXCookbook.App.Tests;

// Batch 4 — the native window title is the BARE product name on every channel;
// the pre-release marker was removed from the window chrome (the single
// authorized customer-visible pre-release notice is the app's top-of-app
// banner). These tests also positively assert that the internal identifiers a
// naive customer-copy rename could have broken remain UNCHANGED.
public class WindowTitleTests
{
    private static VersionInfo Version(string channel) => new(
        CookbookVersion: "2.0.0",
        ReleaseChannel: channel,
        PaxVersion: "1.11.14",
        PaxSha256: "0000000000000000000000000000000000000000000000000000000000000000",
        PaxRelativePath: "Engine/pax.ps1",
        PaxAcquisitionPolicy: "managed",
        EngineManifestUrl: null,
        EngineManifestTrustAnchorThumbprint: null,
        ManifestSignaturePolicy: "none",
        BuildTimestamp: null);

    [Theory]
    [InlineData("experimental")]
    [InlineData("stable")]
    [InlineData("unknown")]
    public void WindowTitle_IsAlwaysBareProductTitle_NoMarker(string channel)
    {
        string title = Program.WindowTitle(Version(channel));

        Assert.Equal("PAX Cookbook", title);
        Assert.DoesNotContain(
            "experimental", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InternalReleaseChannelToken_IsPreservedOnVersionInfo()
    {
        // The copy sweep changed only DISPLAY strings — the raw data token the
        // runtime reports is unchanged.
        Assert.Equal("experimental", Version("experimental").ReleaseChannel);
    }

    [Fact]
    public void InternalWamIdentifiers_AreUnchanged()
    {
        // A naive rename must NOT have touched these internal identifiers.
        Assert.Equal(
            "cookbook:experimental-wam-request",
            ExperimentalWamWindowMessage.RequestType);
        Assert.Equal(
            "PAXCOOKBOOK_EXPERIMENTAL_WAM_ENABLED",
            ExperimentalWamHost.EnabledEnvVar);
    }
}
