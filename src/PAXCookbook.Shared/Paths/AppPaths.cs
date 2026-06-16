namespace PAXCookbook.Shared.Paths;

// Path helpers for the per-user install layout.
// All helpers are pure — they compute paths and never create, modify, or
// delete anything on disk.
public static class AppPaths
{
    // %LOCALAPPDATA%\PAXCookbook
    public static string InstallRoot(string? localAppDataOverride = null)
    {
        var lad = localAppDataOverride
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(lad, ProductConstants.InstallRootFolderName);
    }

    // %LOCALAPPDATA%\PAXCookbook\App
    public static string AppRoot(string? localAppDataOverride = null)
        => Path.Combine(InstallRoot(localAppDataOverride), ProductConstants.AppRootFolderName);

    // %LOCALAPPDATA%\PAXCookbook\App\bin
    public static string BinRoot(string? localAppDataOverride = null)
        => Path.Combine(AppRoot(localAppDataOverride), ProductConstants.BinRootFolderName);

    // %LOCALAPPDATA%\PAXCookbook\App\bin\PAX Cookbook.exe
    public static string AppExePath(string? localAppDataOverride = null)
        => Path.Combine(BinRoot(localAppDataOverride), ProductConstants.AppExeName);

    // %LOCALAPPDATA%\PAXCookbook\App\bin\PAXCookbookSetup.exe
    public static string SetupExePath(string? localAppDataOverride = null)
        => Path.Combine(BinRoot(localAppDataOverride), ProductConstants.SetupExeName);

    // %LOCALAPPDATA%\PAXCookbook\Logs
    public static string LogsRoot(string? localAppDataOverride = null)
        => Path.Combine(InstallRoot(localAppDataOverride), ProductConstants.LogsRootFolderName);

    // %LOCALAPPDATA%\PAXCookbook\WebView2Data
    public static string WebView2DataRoot(string? localAppDataOverride = null)
        => Path.Combine(InstallRoot(localAppDataOverride), ProductConstants.WebView2DataFolderName);

    // %LOCALAPPDATA%\PAXCookbook\Workspace
    public static string Workspace(string? localAppDataOverride = null)
        => Path.Combine(InstallRoot(localAppDataOverride), ProductConstants.WorkspaceFolderName);
}
