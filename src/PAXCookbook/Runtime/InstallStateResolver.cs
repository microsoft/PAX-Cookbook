namespace PAXCookbook.Runtime;

// Resolves the install root (where install-state.json lives) for the
// running App process.
//
// Resolution order:
//   1. Explicit override (CLI --install-root <path>, dev/test only).
//      Never accepted through the protocol activation path.
//   2. %LOCALAPPDATA%\PAXCookbook  (production location, AppPaths.InstallRoot).
public sealed class InstallStateResolver
{
    private readonly string _installRoot;

    public InstallStateResolver(string? overrideInstallRoot, string? localAppDataOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideInstallRoot))
        {
            _installRoot = Path.GetFullPath(overrideInstallRoot);
        }
        else
        {
            _installRoot = Shared.Paths.AppPaths.InstallRoot(localAppDataOverride);
        }
    }

    public string InstallRoot => _installRoot;
    public string InstallStateFile => Path.Combine(_installRoot, "install-state.json");
    public string LogsRoot => Path.Combine(_installRoot, "Logs");
    public string AppLogsRoot => Path.Combine(LogsRoot, "App");
    public bool InstallStatePresent => File.Exists(InstallStateFile);

    public Shared.Contracts.InstallState? TryReadInstallState()
    {
        if (!InstallStatePresent) return null;
        try
        {
            var json = File.ReadAllText(InstallStateFile);
            return Shared.Contracts.InstallStateSerializer.Deserialize(json);
        }
        catch
        {
            return null;
        }
    }
}
