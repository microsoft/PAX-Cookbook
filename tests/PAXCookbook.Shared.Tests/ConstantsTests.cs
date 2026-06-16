using PAXCookbook.Shared;
using Xunit;

namespace PAXCookbook.Shared.Tests;

public class ConstantsTests
{
    [Fact] public void PreferredPort_is_17654() => Assert.Equal(17654, ProductConstants.PreferredPort);
    [Fact] public void FallbackPortStart_is_17654() => Assert.Equal(17654, ProductConstants.FallbackPortStart);
    [Fact] public void FallbackPortEnd_is_17664() => Assert.Equal(17664, ProductConstants.FallbackPortEnd);
    [Fact] public void Aumid_is_pinned() => Assert.Equal("PAXCookbook.App.v1", ProductConstants.Aumid);
    [Fact] public void ProtocolScheme_is_paxcookbook() => Assert.Equal("paxcookbook", ProductConstants.ProtocolScheme);
    [Fact] public void AppExeName_is_PAX_Cookbook_exe() => Assert.Equal("PAX Cookbook.exe", ProductConstants.AppExeName);
    [Fact] public void SetupExeName_is_PAXCookbookSetup_exe() => Assert.Equal("PAXCookbookSetup.exe", ProductConstants.SetupExeName);
}
