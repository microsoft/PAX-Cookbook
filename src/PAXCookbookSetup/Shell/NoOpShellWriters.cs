using System.Collections.Generic;
using System.IO;
using PAXCookbook.Shared.Hashing;

namespace PAXCookbookSetup.Shell;

// No-op shortcut + registry writers used ONLY when the
// PAXCOOKBOOK_TEST_NO_SHELL environment variable is set. This lets the
// installed-Setup self-handoff E2E exercise the real Setup binary
// without writing to the real HKCU or real Start Menu. Production
// runs do NOT set the env var, so production behavior is unchanged.
public static class TestShellGate
{
    public const string EnvVar = "PAXCOOKBOOK_TEST_NO_SHELL";

    public static bool IsActive() =>
        !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(EnvVar));
}

public sealed class NoOpShortcutWriter : IShortcutWriter
{
    public ShortcutWriteResult Write(string folderPath, ShortcutDefinition def)
    {
        // No .lnk is created. Report the write as SUPPRESSED (Created: false) and
        // carry no synthesized path/hash, so the caller records an accurate,
        // empty manifest instead of a shortcut that does not exist on disk.
        return new ShortcutWriteResult(
            LnkPath: string.Empty, Sha256: string.Empty,
            ExcludeAttempted: false, ExcludeSucceeded: false, Created: false);
    }

    public void Delete(string lnkPath) { /* no-op */ }
}

public sealed class NoOpRegistryWriter : IRegistryWriter
{
    public string? GetString(string subKey, string? valueName) => null;
    public void SetString(string subKey, string? valueName, string value) { }
    public void SetDword(string subKey, string valueName, int value) { }
    public bool DeleteValue(string subKey, string valueName) => false;
    public bool DeleteSubKeyTree(string subKey) => false;
    public bool SubKeyExists(string subKey) => false;
    public IEnumerable<string> EnumerateValueNames(string subKey) =>
        System.Array.Empty<string>();
}
