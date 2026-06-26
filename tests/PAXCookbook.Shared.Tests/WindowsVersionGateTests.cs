using PAXCookbook.Shared.Platform;
using Xunit;

namespace PAXCookbook.Shared.Tests;

public class WindowsVersionGateTests
{
    [Theory]
    [InlineData(5, false)]    // Windows XP / Server 2003 era -> unsupported
    [InlineData(6, false)]    // Windows 7 / 8 / 8.1 (all major 6) -> unsupported
    [InlineData(10, true)]    // Windows 10 AND Windows 11 (both major 10)
    [InlineData(11, true)]    // defensive: any future major >= 10 -> supported
    public void IsSupportedMajorVersion_decides_by_floor(int major, bool expected)
        => Assert.Equal(expected, WindowsVersionGate.IsSupportedMajorVersion(major));

    [Fact]
    public void IsSupportedMajorVersion_fails_open_when_version_unknown()
        => Assert.True(WindowsVersionGate.IsSupportedMajorVersion(null));

    [Fact]
    public void TrueMajorVersion_on_this_supported_host_is_at_least_10_and_not_blocked()
    {
        // The dev/CI host is Windows 10/11. Detection must report >= 10 and the
        // gate must NOT false-positive (block) a supported OS.
        int? major = WindowsVersionGate.TryGetTrueMajorVersion();
        Assert.NotNull(major);
        Assert.True(major >= 10, $"expected true major >= 10 but got {major}");
        Assert.True(WindowsVersionGate.IsSupported());
    }

    [Fact]
    public void UnsupportedMessage_is_friendly_and_explanation_free()
    {
        var msg = WindowsVersionGate.UnsupportedMessage;
        Assert.Contains("Windows 10 or later", msg);
        // The message must NOT leak the underlying technical reason.
        Assert.DoesNotContain("Hello", msg);
        Assert.DoesNotContain("WebAuthn", msg);
        Assert.DoesNotContain("API", msg);
    }
}
