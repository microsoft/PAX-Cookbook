namespace PAXCookbookSetup.Gui;

// Outcome of attempting to install one required prerequisite.
//
//   Installed      - we downloaded + installed it and re-detection confirms it.
//   AlreadyPresent - detection found a satisfying version; nothing to do.
//   UserDeclined   - an elevation (UAC) prompt was declined by the user.
//   Failed         - download or install failed.
//   Cancelled      - user chose to exit Setup rather than retry a failed install.
//
// All prerequisites are REQUIRED. Unsatisfied prerequisites abort Setup.
public enum PrerequisiteInstallOutcome
{
    Installed,
    AlreadyPresent,
    UserDeclined,
    Failed,
    Cancelled
}

public sealed record PrerequisiteInstallResult(PrerequisiteInstallOutcome Outcome, string Message)
{
    // True when the prerequisite ended up satisfied (already there or installed).
    public bool Satisfied => Outcome is PrerequisiteInstallOutcome.Installed
                                      or PrerequisiteInstallOutcome.AlreadyPresent;

    public static PrerequisiteInstallResult Installed(string m) => new(PrerequisiteInstallOutcome.Installed, m);
    public static PrerequisiteInstallResult AlreadyPresent(string m) => new(PrerequisiteInstallOutcome.AlreadyPresent, m);
    public static PrerequisiteInstallResult Declined(string m) => new(PrerequisiteInstallOutcome.UserDeclined, m);
    public static PrerequisiteInstallResult Failed(string m) => new(PrerequisiteInstallOutcome.Failed, m);
    public static PrerequisiteInstallResult Cancelled(string m) => new(PrerequisiteInstallOutcome.Cancelled, m);
}
