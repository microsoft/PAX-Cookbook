namespace PAXCookbookSetup;

// Parsed CLI args. Phase 9 adds the two-flag uninstall opt-in pair for
// removing Workspace / per-user data (--remove-user-data must be paired
// with --confirm-remove-user-data; UninstallVerb enforces the pairing).
public sealed record ParsedArgs(
    string Verb,
    string? InstallRootOverride,
    string? PayloadRoot,
    bool Force,
    bool ReinstallSameVersion,
    bool AllowDowngrade,
    bool HandoffFromInstalled,
    string? HandoffFolder,
    bool DryRun,
    bool RemoveUserData,
    bool ConfirmRemoveUserData,
    List<string> Errors,
    bool Quiet = false,
    bool GuiUninstall = false)
{
    public bool IsSameVersionRepair => Force || ReinstallSameVersion;
}

public static class ArgParser
{
    public static readonly HashSet<string> KnownVerbs = new(StringComparer.OrdinalIgnoreCase)
    { "install", "update", "repair", "status", "uninstall", "version", "help" };

    public static ParsedArgs Parse(string[] argv)
    {
        var errors = new List<string>();
        string? verb = null;
        string? installRoot = null;
        string? payloadRoot = null;
        string? handoffFolder = null;
        bool force = false, reinstall = false, allowDown = false, handoffFromInstalled = false, dryRun = false;
        bool removeUserData = false, confirmRemoveUserData = false;
        bool quiet = false, guiUninstall = false;

        for (int i = 0; i < argv.Length; i++)
        {
            var a = argv[i];
            if (i == 0 && !a.StartsWith("--", StringComparison.Ordinal))
            {
                verb = a;
                continue;
            }
            switch (a)
            {
                case "--install-root":
                    installRoot = NextValue(argv, ref i, "--install-root", errors); break;
                case "--payload-root":
                    payloadRoot = NextValue(argv, ref i, "--payload-root", errors); break;
                case "--force":                       force = true; break;
                case "--reinstall-same-version":      reinstall = true; break;
                case "--allow-downgrade":             allowDown = true; break;
                case "--handoff-from-installed":      handoffFromInstalled = true; break;
                case "--handoff-folder":
                    handoffFolder = NextValue(argv, ref i, "--handoff-folder", errors); break;
                case "--dry-run":                     dryRun = true; break;
                case "--remove-user-data":            removeUserData = true; break;
                case "--confirm-remove-user-data":    confirmRemoveUserData = true; break;
                case "--quiet":                       quiet = true; break;
                case "--silent":                      quiet = true; break;
                case "--gui-uninstall":               guiUninstall = true; break;
                default:
                    errors.Add($"unknown argument: {a}"); break;
            }
        }

        if (verb is null) verb = "help";
        if (!KnownVerbs.Contains(verb)) errors.Add($"unknown verb: {verb}");
        if (handoffFromInstalled && string.IsNullOrEmpty(handoffFolder))
            errors.Add("--handoff-from-installed requires --handoff-folder");
        if (!handoffFromInstalled && !string.IsNullOrEmpty(handoffFolder))
            errors.Add("--handoff-folder requires --handoff-from-installed");

        return new ParsedArgs(verb, installRoot, payloadRoot, force, reinstall, allowDown,
            handoffFromInstalled, handoffFolder, dryRun,
            removeUserData, confirmRemoveUserData, errors, quiet, guiUninstall);
    }

    private static string? NextValue(string[] argv, ref int i, string name, List<string> errors)
    {
        if (i + 1 >= argv.Length) { errors.Add($"{name} requires a value"); return null; }
        return argv[++i];
    }
}
