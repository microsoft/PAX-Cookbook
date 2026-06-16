using System.Text.Json;

namespace PAXCookbook.Runtime;

// Reads %APPDATA%\PAXCookbook\cookbook.bootstrap.json to discover the
// workspace folder the user last picked. The bootstrap file is owned by
// launcher\Start-PAXCookbook.ps1; we read only the workspaceFolderPath
// field and never modify the file.
public sealed record BootstrapState(string? WorkspaceFolderPath, int? SelectedBrokerPort);

public sealed class BootstrapStateReader
{
    private readonly string _bootstrapFile;

    public BootstrapStateReader(string? appDataOverride = null)
    {
        var appData = appDataOverride
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _bootstrapFile = Path.Combine(appData, "PAXCookbook", "cookbook.bootstrap.json");
    }

    public string BootstrapFile => _bootstrapFile;
    public bool Present => File.Exists(_bootstrapFile);

    public BootstrapState? TryRead()
    {
        if (!Present) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_bootstrapFile));
            var root = doc.RootElement;
            string? wsPath = null;
            int? port = null;
            if (root.TryGetProperty("workspaceFolderPath", out var wp) &&
                wp.ValueKind == JsonValueKind.String)
            {
                wsPath = wp.GetString();
            }
            if (root.TryGetProperty("selectedBrokerPort", out var sp) &&
                sp.ValueKind == JsonValueKind.Number &&
                sp.TryGetInt32(out var p))
            {
                port = p;
            }
            return new BootstrapState(wsPath, port);
        }
        catch
        {
            return null;
        }
    }
}
