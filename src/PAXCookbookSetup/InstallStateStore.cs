using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup;

// Atomic read/write of install-state.json. Writes to .tmp then File.Move.
public static class InstallStateStore
{
    public static string PathFor(string installRoot)
        => Path.Combine(installRoot, "install-state.json");

    public static bool Exists(string installRoot) => File.Exists(PathFor(installRoot));

    public static InstallState? TryLoad(string installRoot)
    {
        var p = PathFor(installRoot);
        if (!File.Exists(p)) return null;
        try
        {
            var json = File.ReadAllText(p);
            return InstallStateSerializer.Deserialize(json);
        }
        catch
        {
            return null;
        }
    }

    public static InstallState Load(string installRoot)
        => TryLoad(installRoot)
           ?? throw new InvalidDataException($"install-state missing or invalid at {PathFor(installRoot)}");

    public static void Save(string installRoot, InstallState state)
    {
        Directory.CreateDirectory(installRoot);
        var final = PathFor(installRoot);
        var tmp = final + ".tmp";
        var json = InstallStateSerializer.Serialize(state);
        File.WriteAllText(tmp, json);
        if (File.Exists(final)) File.Delete(final);
        File.Move(tmp, final);
    }
}
