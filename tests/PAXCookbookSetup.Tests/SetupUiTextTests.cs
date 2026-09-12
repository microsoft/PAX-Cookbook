using System;
using PAXCookbookSetup;
using PAXCookbookSetup.Gui;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Batch 4 — the customer-visible Setup copy carries NO "experimental" word. The
// internal SetupChannel value "experimental" is unchanged (asserted below);
// only the DISPLAYED text changed. The single authorized customer-visible
// "Experimental" notice lives in the running app shell, never in Setup.
public class SetupUiTextTests
{
    [Fact]
    public void SetupWindowTitle_IsBareProductTitle_NoMarker()
    {
        Assert.Equal("PAX Cookbook Setup", SetupUiText.SetupWindowTitle);
        Assert.DoesNotContain(
            "experimental",
            SetupUiText.SetupWindowTitle,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreReleaseSafetyStrip_UsesTestWording_NoExperimental()
    {
        Assert.Equal(
            "Test build \u2014 not for production",
            SetupUiText.PreReleaseSafetyStrip);
        Assert.DoesNotContain(
            "experimental",
            SetupUiText.PreReleaseSafetyStrip,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPreReleaseBuildFound_UsesPreReleaseWording_NoExperimental()
    {
        Assert.Equal("No pre-release build was found. ", SetupUiText.NoPreReleaseBuildFound);
        Assert.DoesNotContain(
            "experimental",
            SetupUiText.NoPreReleaseBuildFound,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InternalSetupChannelTokens_AreUnchanged()
    {
        // The copy sweep must NOT have renamed the internal channel data values.
        Assert.Equal("stable", SetupChannel.Stable);
        Assert.Equal("experimental", SetupChannel.Experimental);
        Assert.Equal("internal", SetupChannel.Internal);
    }

    [Theory]
    [InlineData("2.0.0+commit.abcdef0", "2.0.0")]
    [InlineData("2.0.0-exp.4+build.exp4", "2.0.0-exp.4")]
    [InlineData("2.0.0-internal.7+build.vm7", "2.0.0-internal.7")]
    public void VersionDisplay_StripsOnlyProvenance_AndRetainsPrerelease(
        string informationalVersion,
        string expected)
    {
        Assert.Equal(
            expected,
            SetupWizardForm.FormatDisplayVersion(informationalVersion, new Version(9, 9, 9, 9)));
    }

    [Fact]
    public void VersionDisplay_MalformedInformationalVersion_UsesBoundedAssemblyFallback()
    {
        Assert.Equal(
            "2.1.3",
            SetupWizardForm.FormatDisplayVersion("not-a-version", new Version(2, 1, 3, 0)));
    }
}
