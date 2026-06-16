using System.Text.Json;
using PAXCookbook.Shared.Json;

namespace PAXCookbook.Shared.Contracts;

// Models matching shortcut-manifest.schema.json (Phase 1).
public sealed record ShortcutManifest
{
    public string Product { get; init; } = ProductConstants.ProductName.Replace(" ", "");
    public int ShortcutManifestSchemaVersion { get; init; } = 1;
    public string AppVersion { get; init; } = "0.0.0";
    public string Aumid { get; init; } = ProductConstants.Aumid;
    public string InstallRoot { get; init; } = "";
    public List<ShortcutEntry> Shortcuts { get; init; } = new();
}

public sealed record ShortcutEntry
{
    public string Kind { get; init; } = "start-menu"; // "start-menu" | "desktop" | "taskbar-pin"
    public string LnkPath { get; init; } = "";
    public string Target { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string Aumid { get; init; } = ProductConstants.Aumid;
    public string IconLocation { get; init; } = "";
    public string CreatedAtUtc { get; init; } = "1970-01-01T00:00:00Z";
    public string Sha256 { get; init; } = "";
}

public static class ShortcutManifestSerializer
{
    public static string Serialize(ShortcutManifest m)
        => JsonSerializer.Serialize(m, JsonOptionsFactory.Default);

    public static ShortcutManifest Deserialize(string json)
    {
        var r = JsonSerializer.Deserialize<ShortcutManifest>(json, JsonOptionsFactory.Default);
        if (r is null) throw new InvalidDataException("shortcut-manifest JSON deserialized to null");
        return r;
    }
}
