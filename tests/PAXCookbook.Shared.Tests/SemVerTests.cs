using PAXCookbook.Shared.Versioning;
using Xunit;

namespace PAXCookbook.Shared.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0, 0)]
    [InlineData("1.2.3", 1, 2, 3, 0)]
    [InlineData("1.2.3.4", 1, 2, 3, 4)]
    [InlineData("v1.2.3", 1, 2, 3, 0)]
    [InlineData("1.2.3-beta", 1, 2, 3, 0)]
    [InlineData("1.2.3+build.42", 1, 2, 3, 0)]
    [InlineData("0.0", 0, 0, 0, 0)]
    public void Parses_valid(string s, int maj, int min, int pat, int rev)
    {
        Assert.True(SemVer.TryParse(s, out var v));
        Assert.Equal(maj, v.Major);
        Assert.Equal(min, v.Minor);
        Assert.Equal(pat, v.Patch);
        Assert.Equal(rev, v.Revision);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("-1.0.0")]
    public void Rejects_invalid(string? s)
    {
        Assert.False(SemVer.TryParse(s, out _));
    }

    [Fact]
    public void Compares_older_newer_equal()
    {
        var a = SemVer.Parse("1.2.3");
        var b = SemVer.Parse("1.2.4");
        var c = SemVer.Parse("1.2.3");

        Assert.Equal(VersionRelation.Newer, VersionCompare.Compare(a, b));
        Assert.Equal(VersionRelation.Older, VersionCompare.Compare(b, a));
        Assert.Equal(VersionRelation.Equal, VersionCompare.Compare(a, c));
    }

    [Fact]
    public void Downgrade_is_detectable_at_comparison_level()
    {
        // Per update-downgrade-rollback-contract.md: downgrade is detected
        // here; policy (prompt, --allow-downgrade) is enforced by Setup.
        var installed = SemVer.Parse("2.0.0");
        var candidate = SemVer.Parse("1.5.0");
        Assert.Equal(VersionRelation.Older, VersionCompare.Compare(installed, candidate));
    }

    [Fact]
    public void Same_version_is_recognized()
    {
        var v = SemVer.Parse("1.0.0");
        Assert.Equal(VersionRelation.Equal, VersionCompare.Compare(v, v));
    }

    [Fact]
    public void ToString_roundtrips_three_part()
    {
        Assert.Equal("1.2.3", SemVer.Parse("1.2.3").ToString());
    }

    [Fact]
    public void ToString_roundtrips_four_part()
    {
        Assert.Equal("1.2.3.4", SemVer.Parse("1.2.3.4").ToString());
    }
}
