namespace PAXCookbook.WebView2;

// Resolves the app-owned WebView2 user-data folder. Production uses
// %LOCALAPPDATA%\PAXCookbook\WebView2Data. Tests/dev can pass an
// override (typically a temp install root) so they do not pollute the
// real user-data folder or trigger Edge profile prompts.
//
// The host MUST NOT fall back to a default WebView2 user-data folder
// and MUST NOT share user-data with Microsoft Edge.
public sealed class WebView2DataPaths
{
    public string UserDataFolder { get; }
    public string Source { get; }

    private WebView2DataPaths(string folder, string source)
    {
        UserDataFolder = folder;
        Source = source;
    }

    public static WebView2DataPaths FromLocalAppData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(local, "PAXCookbook", "WebView2Data");
        return new WebView2DataPaths(folder, "LocalApplicationData");
    }

    // Test/dev override: place WebView2Data under the temp install root.
    // Used by xunit fixtures so each test gets an isolated folder.
    public static WebView2DataPaths FromInstallRoot(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new ArgumentException("installRoot required", nameof(installRoot));
        return new WebView2DataPaths(Path.Combine(installRoot, "WebView2Data"), "InstallRootOverride");
    }
}
