using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PAXCookbookSetup.Shell;

// Test-only fakes. NEVER touch the real Start Menu / Desktop / Registry.

public sealed class InMemoryShortcutWriter : IShortcutWriter
{
    public sealed record Record(string FolderPath, ShortcutDefinition Def, ShortcutWriteResult Result);

    public List<Record> Writes { get; } = new();
    public HashSet<string> Deleted { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Lets tests force ExcludeFromShowInNewInstall application to fail.
    public bool ForceExcludeFailure { get; set; }

    public ShortcutWriteResult Write(string folderPath, ShortcutDefinition def)
    {
        var lnk = Path.Combine(folderPath, def.Name + ".lnk");
        var bytes = Encoding.UTF8.GetBytes(
            $"{def.Target}|{def.Arguments}|{def.WorkingDirectory}|{def.Aumid}|{def.IconLocation}|{def.ExcludeFromRecommended}");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        bool excludeAttempted = def.ExcludeFromRecommended;
        bool excludeOk = def.ExcludeFromRecommended && !ForceExcludeFailure;
        var result = new ShortcutWriteResult(lnk, sha, excludeAttempted, excludeOk);
        Writes.Add(new Record(folderPath, def, result));
        return result;
    }

    public void Delete(string lnkPath) => Deleted.Add(lnkPath);
}

public sealed class InMemoryRegistryWriter : IRegistryWriter
{
    // Nested dictionary: subKey -> (valueName, value)
    public Dictionary<string, Dictionary<string, object>> Store { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, object> Bucket(string subKey)
    {
        if (!Store.TryGetValue(subKey, out var b))
        {
            b = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Store[subKey] = b;
        }
        return b;
    }

    public string? GetString(string subKey, string? valueName)
    {
        if (!Store.TryGetValue(subKey, out var b)) return null;
        return b.TryGetValue(valueName ?? "", out var v) ? v as string : null;
    }

    public void SetString(string subKey, string? valueName, string value)
        => Bucket(subKey)[valueName ?? ""] = value;

    public void SetDword(string subKey, string valueName, int value)
        => Bucket(subKey)[valueName] = value;

    public bool DeleteValue(string subKey, string valueName)
    {
        if (!Store.TryGetValue(subKey, out var b)) return false;
        return b.Remove(valueName);
    }

    public bool DeleteSubKeyTree(string subKey)
    {
        var keys = Store.Keys.Where(k =>
            k.Equals(subKey, StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
        if (keys.Count == 0) return false;
        foreach (var k in keys) Store.Remove(k);
        return true;
    }

    public bool SubKeyExists(string subKey) => Store.ContainsKey(subKey);

    public IEnumerable<string> EnumerateValueNames(string subKey)
        => Store.TryGetValue(subKey, out var b) ? b.Keys : Array.Empty<string>();
}
