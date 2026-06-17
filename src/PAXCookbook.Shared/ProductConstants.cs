namespace PAXCookbook.Shared;

// Cross-component constants derived from the Phase 1 contracts.
// These values are the single source of truth for product identity,
// AUMID, protocol scheme, and broker port range.
public static class ProductConstants
{
    public const string ProductName = "PAX Cookbook";
    public const string AppExeName = "PAX Cookbook.exe";
    // Managed entry assembly. Under corporate WDAC the unsigned apphost
    // (AppExeName) cannot be executed, so every launch runs the Microsoft-
    // signed dotnet.exe host with this DLL as its argument. The EXE remains
    // only as the shortcut icon source (reading an icon is allowed).
    public const string AppDllName = "PAX Cookbook.dll";
    public const string SetupExeName = "PAXCookbookSetup.exe";

    // From webview2-host-contract.md + install-state.schema.json (const).
    public const string Aumid = "PAXCookbook.App.v1";

    // From paxcookbook-protocol-contract.md.
    public const string ProtocolScheme = "paxcookbook";
    public const string ProtocolOpenVerb = "open";

    // Recipe file associations (per-user, HKCU\Software\Classes).
    // Lite/Mini-Kitchen exports use *.json.paxlite; full Cookbook exports
    // use *.json.pax. Windows keys off the final extension only.
    public const string PaxLiteFileExtension = ".paxlite";
    public const string PaxFullFileExtension = ".pax";
    public const string PaxLiteProgId = "PAXCookbook.MiniRecipe.v1";
    public const string PaxFullProgId = "PAXCookbook.Recipe.v1";
    public const string PaxLiteDescription = "PAX Cookbook Mini-Kitchen Recipe";
    public const string PaxFullDescription = "PAX Cookbook Recipe";

    // From the architecture plan; broker localhost ports.
    public const int PreferredPort = 17654;
    public const int FallbackPortStart = 17654;
    public const int FallbackPortEnd = 17664;

    // From webview2-runtime-detection-contract.md.
    public const string WebView2RuntimeRegistrySubKey =
        @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    public const string WebView2RuntimeVersionValueName = "pv";

    // Per-session mutex and pipe identity (paxcookbook-ipc-contract.md).
    public const string PrimaryInstanceMutexName = @"Local\PAXCookbook.PrimaryInstance";
    public const string PipeNamePrefix = "PAXCookbook.";

    // Install layout (architecture plan).
    public const string InstallRootFolderName = "PAXCookbook";
    public const string AppRootFolderName = "App";
    public const string BinRootFolderName = "bin";
    public const string LogsRootFolderName = "Logs";
    public const string WebView2DataFolderName = "WebView2Data";
    // Canonical production per-user workspace folder (sibling of App/Logs).
    // The native host (PAXCookbook.App) mirrors this literal because it does
    // not reference Shared; keep the two in sync.
    public const string WorkspaceFolderName = "Workspace";
}
