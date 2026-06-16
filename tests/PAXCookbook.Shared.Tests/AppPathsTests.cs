using PAXCookbook.Shared.Paths;
using Xunit;

namespace PAXCookbook.Shared.Tests;

public class AppPathsTests
{
    [Fact]
    public void InstallRoot_is_under_local_app_data()
    {
        var override_ = Path.Combine(Path.GetTempPath(), "pax-test-lad");
        var root = AppPaths.InstallRoot(override_);
        Assert.StartsWith(override_, root);
        Assert.EndsWith("PAXCookbook", root);
    }

    [Fact]
    public void BinRoot_is_under_app_root()
    {
        var override_ = Path.Combine(Path.GetTempPath(), "pax-test-lad");
        var bin = AppPaths.BinRoot(override_);
        var app = AppPaths.AppRoot(override_);
        Assert.StartsWith(app, bin);
        Assert.EndsWith("bin", bin);
    }

    [Fact]
    public void AppExePath_ends_with_app_exe_name()
    {
        var override_ = Path.Combine(Path.GetTempPath(), "pax-test-lad");
        Assert.EndsWith("PAX Cookbook.exe", AppPaths.AppExePath(override_));
    }

    [Fact]
    public void SetupExePath_ends_with_setup_exe_name()
    {
        var override_ = Path.Combine(Path.GetTempPath(), "pax-test-lad");
        Assert.EndsWith("PAXCookbookSetup.exe", AppPaths.SetupExePath(override_));
    }

    [Fact]
    public void WebView2DataRoot_is_under_install_root()
    {
        var override_ = Path.Combine(Path.GetTempPath(), "pax-test-lad");
        Assert.StartsWith(AppPaths.InstallRoot(override_), AppPaths.WebView2DataRoot(override_));
        Assert.EndsWith("WebView2Data", AppPaths.WebView2DataRoot(override_));
    }

    [Fact]
    public void LogsRoot_is_under_install_root()
    {
        var override_ = Path.Combine(Path.GetTempPath(), "pax-test-lad");
        Assert.StartsWith(AppPaths.InstallRoot(override_), AppPaths.LogsRoot(override_));
        Assert.EndsWith("Logs", AppPaths.LogsRoot(override_));
    }

    [Fact]
    public void Helpers_do_not_create_directories()
    {
        var override_ = Path.Combine(Path.GetTempPath(), "pax-test-lad-" + Guid.NewGuid().ToString("N"));
        _ = AppPaths.InstallRoot(override_);
        _ = AppPaths.AppRoot(override_);
        _ = AppPaths.BinRoot(override_);
        _ = AppPaths.LogsRoot(override_);
        _ = AppPaths.WebView2DataRoot(override_);
        Assert.False(Directory.Exists(override_), "path helpers must be pure; no filesystem mutation");
    }
}
