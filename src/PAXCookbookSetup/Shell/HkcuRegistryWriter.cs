using System.Collections.Generic;
using Microsoft.Win32;

namespace PAXCookbookSetup.Shell;

// Real HKCU registry writer. Strictly per-user; never opens HKLM. Used
// in production wiring; tests use InMemoryRegistryWriter (test fake).
public sealed class HkcuRegistryWriter : IRegistryWriter
{
    private static RegistryKey OpenOrCreate(string subKey, bool writable)
    {
        var key = Registry.CurrentUser.CreateSubKey(subKey, writable);
        if (key is null) throw new System.IO.IOException($"unable to open HKCU\\{subKey}");
        return key;
    }

    public string? GetString(string subKey, string? valueName)
    {
        using var k = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return k?.GetValue(valueName ?? "") as string;
    }

    public void SetString(string subKey, string? valueName, string value)
    {
        using var k = OpenOrCreate(subKey, writable: true);
        k.SetValue(valueName ?? "", value, RegistryValueKind.String);
    }

    public void SetDword(string subKey, string valueName, int value)
    {
        using var k = OpenOrCreate(subKey, writable: true);
        k.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    public bool DeleteValue(string subKey, string valueName)
    {
        using var k = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        if (k is null) return false;
        try { k.DeleteValue(valueName, throwOnMissingValue: true); return true; }
        catch (System.ArgumentException) { return false; }
    }

    public bool DeleteSubKeyTree(string subKey)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: true);
            return true;
        }
        catch (System.ArgumentException) { return false; }
    }

    public bool SubKeyExists(string subKey)
    {
        using var k = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return k is not null;
    }

    public IEnumerable<string> EnumerateValueNames(string subKey)
    {
        using var k = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        if (k is null) return System.Array.Empty<string>();
        return k.GetValueNames();
    }
}
