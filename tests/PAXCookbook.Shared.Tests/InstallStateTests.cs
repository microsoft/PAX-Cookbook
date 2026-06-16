using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

public class InstallStateTests
{
    [Fact]
    public void RoundTrip_PreservesCoreFields()
    {
        var s = new InstallState
        {
            AppVersion = "1.2.3",
            SetupVersion = "1.2.3",
            AppExeVersion = "1.2.3",
            InstallRoot = @"C:\Foo\PAXCookbook",
            AppRoot = @"C:\Foo\PAXCookbook\App",
            BinRoot = @"C:\Foo\PAXCookbook\App\bin",
            AppExe = @"C:\Foo\PAXCookbook\App\bin\PAXCookbook.exe",
            WorkspaceFolderPath = @"C:\Users\u\Workspace",
            WebView2UserDataFolder = @"C:\Foo\PAXCookbook\WebView2Data"
        };
        var json = InstallStateSerializer.Serialize(s);
        var back = InstallStateSerializer.Deserialize(json);
        Assert.Equal("1.2.3", back.AppVersion);
        Assert.Equal("PAXCookbook", back.Product);
        Assert.Equal(1, back.InstallSchemaVersion);
        Assert.Equal("aumid", back.ActivationModel);
        Assert.Equal("HKCU", back.UninstallRegistered.Scope);
        Assert.Equal("paxcookbook", back.ProtocolRegistered.Scheme);
    }

    [Fact]
    public void Deserialize_RejectsBadJson()
    {
        Assert.ThrowsAny<Exception>(() => InstallStateSerializer.Deserialize("not json"));
    }
}
