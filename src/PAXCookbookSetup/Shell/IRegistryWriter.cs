using System.Collections.Generic;

namespace PAXCookbookSetup.Shell;

// HKCU registry abstraction. Setup never touches HKLM. Tests use the
// in-memory fake so no real registry writes happen.
public interface IRegistryWriter
{
    // Reads a string value or null if absent.
    string? GetString(string subKey, string? valueName);

    // Sets a string value at HKCU\<subKey> with the given value name
    // ("" or null means default value). REG_SZ.
    void SetString(string subKey, string? valueName, string value);

    // Sets a DWORD value at HKCU\<subKey>.
    void SetDword(string subKey, string valueName, int value);

    // Deletes the value or returns false if absent.
    bool DeleteValue(string subKey, string valueName);

    // Deletes a subkey tree or returns false if absent.
    bool DeleteSubKeyTree(string subKey);

    // Returns true if HKCU\<subKey> exists.
    bool SubKeyExists(string subKey);

    // Enumerates value names under HKCU\<subKey>. Empty if subkey missing.
    IEnumerable<string> EnumerateValueNames(string subKey);
}
