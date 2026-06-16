namespace PAXCookbook.Broker;

// Resolved on-disk paths the App needs to orchestrate the broker.
//
// AppRoot must contain a "broker\Start-Broker.ps1" script. Supports two
// layouts (same probe order as launcher\Start-PAXCookbook.ps1):
//
//   (a) source tree:    <repo>\app\broker\Start-Broker.ps1
//   (b) installed tree: <installRoot>\App\broker\Start-Broker.ps1
public sealed class BrokerPaths
{
    public string AppRoot { get; }
    public string BrokerStartScript { get; }

    private BrokerPaths(string appRoot, string brokerStartScript)
    {
        AppRoot = appRoot;
        BrokerStartScript = brokerStartScript;
    }

    public static BrokerPaths? TryResolve(string installRoot, string? repoRootHint = null)
    {
        // (b) installed: <installRoot>\App\broker\Start-Broker.ps1
        var installedAppRoot = Path.Combine(installRoot, "App");
        var installedScript = Path.Combine(installedAppRoot, "broker", "Start-Broker.ps1");
        if (File.Exists(installedScript))
            return new BrokerPaths(installedAppRoot, installedScript);

        // (a) source: <repoRoot>\app\broker\Start-Broker.ps1 — only useful in
        //     dev/test where installRoot is a temp folder. Search upward from
        //     the current process directory for a folder containing app\broker.
        var probeStart = repoRootHint ?? AppContext.BaseDirectory;
        var dir = new DirectoryInfo(probeStart);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "app", "broker", "Start-Broker.ps1");
            if (File.Exists(candidate))
                return new BrokerPaths(Path.Combine(dir.FullName, "app"), candidate);
            dir = dir.Parent;
        }
        return null;
    }
}
