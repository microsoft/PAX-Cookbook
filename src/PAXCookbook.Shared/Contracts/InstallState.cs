using System.Text.Json;
using System.Text.Json.Serialization;
using PAXCookbook.Shared.Json;

namespace PAXCookbook.Shared.Contracts;

// Schema-strict mirror of install-state.schema.json. Owned by
// PAXCookbookSetup.exe; the App reads it only.
public sealed record InstallState
{
    public string Product { get; init; } = "PAXCookbook";
    public int InstallSchemaVersion { get; init; } = 1;
    public string AppVersion { get; init; } = "0.0.0";
    public string SetupVersion { get; init; } = "0.0.0";
    public string AppExeVersion { get; init; } = "0.0.0";
    public string InstalledAtUtc { get; init; } = "1970-01-01T00:00:00Z";
    public string UpdatedAtUtc { get; init; } = "1970-01-01T00:00:00Z";
    public string InstallRoot { get; init; } = "";
    public string AppRoot { get; init; } = "";
    public string BinRoot { get; init; } = "";
    public string AppExe { get; init; } = "";
    public string WorkspaceFolderPath { get; init; } = "";
    public string ActivationModel { get; init; } = "aumid";
    public string Aumid { get; init; } = ProductConstants.Aumid;
    public string UiSurface { get; init; } = "webview2-native-window";
    public WebView2RuntimeStatus WebView2RuntimeStatus { get; init; } = new();
    public string WebView2UserDataFolder { get; init; } = "";
    public ProtocolRegistered ProtocolRegistered { get; init; } = new();
    public ShortcutsCreated ShortcutsCreated { get; init; } = new();
    public UninstallRegistered UninstallRegistered { get; init; } = new();
    public List<PreviousVersion>? PreviousVersions { get; init; }
    public LastOperation LastOperation { get; init; } = new();
}

public sealed record WebView2RuntimeStatus
{
    // "unknown" until detection has actually run; then "present" or "missing".
    public string Status { get; init; } = "unknown";
    // Null when Status == "unknown". Bool when detection has run.
    public bool? Present { get; init; }
    public string? Pv { get; init; }
    public List<string> Sources { get; init; } = new();
    public string DetectedAtUtc { get; init; } = "1970-01-01T00:00:00Z";
}

public sealed record ProtocolRegistered
{
    public string Scheme { get; init; } = "paxcookbook";
    public string Scope { get; init; } = "HKCU";
    public bool Registered { get; init; }
}

public sealed record ShortcutsCreated
{
    public bool StartMenu { get; init; }
    public bool Desktop { get; init; }
    public bool AutoTaskbarPin { get; init; } // schema: const false
}

public sealed record UninstallRegistered
{
    public string Scope { get; init; } = "HKCU";
    public string KeyPath { get; init; } = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\PAXCookbook";
    public string DisplayName { get; init; } = "PAX Cookbook";
}

public sealed record PreviousVersion
{
    public string AppVersion { get; init; } = "0.0.0";
    public string SetupVersion { get; init; } = "0.0.0";
    public string At { get; init; } = "1970-01-01T00:00:00Z";
    public string Reason { get; init; } = "update"; // update | downgrade | repair
}

public sealed record LastOperation
{
    public string Kind { get; init; } = "install"; // install|update|downgrade|repair|uninstall|status
    public string Status { get; init; } = "ok";    // ok|failed|rolled-back|cancelled
    public string At { get; init; } = "1970-01-01T00:00:00Z";
    public int? ExitCode { get; init; }
    public string? Detail { get; init; }
}

public static class InstallStateSerializer
{
    public static string Serialize(InstallState state)
        => JsonSerializer.Serialize(state, JsonOptionsFactory.Default);

    public static InstallState Deserialize(string json)
    {
        var result = JsonSerializer.Deserialize<InstallState>(json, JsonOptionsFactory.Default);
        if (result is null) throw new InvalidDataException("install-state JSON deserialized to null");
        return result;
    }
}
