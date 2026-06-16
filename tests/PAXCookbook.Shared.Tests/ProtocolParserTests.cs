using PAXCookbook.Shared.Protocol;
using Xunit;

namespace PAXCookbook.Shared.Tests;

public class ProtocolParserTests
{
    [Theory]
    [InlineData("paxcookbook://open")]
    [InlineData("paxcookbook://open/")]
    public void Accepts_open(string uri)
    {
        var r = ProtocolParser.Parse(uri);
        Assert.True(r.Accepted, $"expected accepted, got reason={r.RejectReason}");
    }

    [Theory]
    [InlineData("",                            ProtocolRejectReason.NullOrEmpty)]
    [InlineData(null,                          ProtocolRejectReason.NullOrEmpty)]
    [InlineData("paxcookbook://stop",          ProtocolRejectReason.WrongVerbHost)]
    [InlineData("paxcookbook://support",       ProtocolRejectReason.WrongVerbHost)]
    [InlineData("paxcookbook://import",        ProtocolRejectReason.WrongVerbHost)]
    [InlineData("paxcookbook://export",        ProtocolRejectReason.WrongVerbHost)]
    [InlineData("paxcookbook://delete",        ProtocolRejectReason.WrongVerbHost)]
    [InlineData("paxcookbook://run",           ProtocolRejectReason.WrongVerbHost)]
    [InlineData("paxcookbook://open?x=1",      ProtocolRejectReason.HasQuery)]
    [InlineData("paxcookbook://open#frag",     ProtocolRejectReason.HasFragment)]
    [InlineData("paxcookbook://open/extra",    ProtocolRejectReason.DisallowedPath)]
    [InlineData("paxcookbook://open/extra/2",  ProtocolRejectReason.DisallowedPath)]
    [InlineData("https://open",                ProtocolRejectReason.WrongScheme)]
    [InlineData("paxcookbook://user@open",     ProtocolRejectReason.HasUserInfo)]
    [InlineData("paxcookbook://open%2f",       ProtocolRejectReason.PercentEncoded)]
    [InlineData("not a uri",                   ProtocolRejectReason.UnparseableUri)]
    public void Rejects_with_reason(string? uri, ProtocolRejectReason expected)
    {
        var r = ProtocolParser.Parse(uri);
        Assert.False(r.Accepted, $"expected rejected for '{uri}'");
        Assert.Equal(expected, r.RejectReason);
    }

    [Fact]
    public void Rejects_overlong_input()
    {
        var tooLong = "paxcookbook://open/" + new string('a', 100);
        var r = ProtocolParser.Parse(tooLong);
        Assert.False(r.Accepted);
        Assert.Equal(ProtocolRejectReason.TooLong, r.RejectReason);
    }

    [Fact]
    public void Rejects_control_character()
    {
        var r = ProtocolParser.Parse("paxcookbook://open\u0007");
        Assert.False(r.Accepted);
        Assert.Equal(ProtocolRejectReason.ControlCharacter, r.RejectReason);
    }
}
