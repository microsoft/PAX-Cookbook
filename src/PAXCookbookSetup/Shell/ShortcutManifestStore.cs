using System.IO;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Shell;

// Persists shortcut-manifest.json in the install root using the shared
// PAXCookbook.Shared.Contracts.ShortcutManifest types (defined to match
// shortcut-manifest.schema.json).
public interface IShortcutManifestStore
{
    ShortcutManifest? TryLoad(string installRoot);
    void Save(string installRoot, ShortcutManifest manifest);
    string PathFor(string installRoot);
}

public sealed class ShortcutManifestStore : IShortcutManifestStore
{
    public const string FileName = "shortcut-manifest.json";

    public string PathFor(string installRoot)
        => Path.Combine(installRoot, FileName);

    public ShortcutManifest? TryLoad(string installRoot)
    {
        var p = PathFor(installRoot);
        if (!File.Exists(p)) return null;
        try { return ShortcutManifestSerializer.Deserialize(File.ReadAllText(p)); }
        catch { return null; }
    }

    public void Save(string installRoot, ShortcutManifest manifest)
    {
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(PathFor(installRoot), ShortcutManifestSerializer.Serialize(manifest));
    }
}
