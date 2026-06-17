namespace PAXCookbookSetup.Gui;

// System-interaction seam for prerequisite detection. The production
// implementation (SystemPrerequisiteProbe) talks to the real OS — PATH,
// the filesystem, child processes, and the read-only registry. Tests inject
// a fake to exercise the (pure) ordering + version-gate logic in
// PrerequisiteDetector without touching the machine.
//
// Registry access is READ-ONLY HKLM (constraint 18 governs HKLM *writes*;
// reading the PowerShellCore install record is allowed and never mutates).
public interface IPrerequisiteProbe
{
    // Full path of an executable resolved on the PATH, or null if not found.
    string? ResolveOnPath(string exeName);

    // True if the file exists on disk.
    bool FileExists(string path);

    // Directory names matching a wildcard pattern directly under a parent
    // directory (e.g. "Python3*"). Empty when the parent does not exist.
    IEnumerable<string> EnumerateDirectories(string parent, string pattern);

    // Value of an environment variable (e.g. "ProgramFiles", "LOCALAPPDATA"),
    // or null when unset.
    string? GetEnvPath(string envVarName);

    // Run an executable with arguments and return its combined stdout+stderr,
    // or null on any failure / timeout. Used only for "--version" probes.
    string? RunVersion(string exePath, string arguments);

    // Read a string value under HKLM\<subKey> (read-only), or null if absent.
    string? ReadHklmString(string subKey, string? valueName);

    // Enumerate the immediate subkey names under HKLM\<subKey>. Empty if absent.
    IEnumerable<string> EnumerateHklmSubKeyNames(string subKey);
}
