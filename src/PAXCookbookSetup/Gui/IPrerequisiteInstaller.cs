namespace PAXCookbookSetup.Gui;

// Common contract for an external-prerequisite installer (PowerShell 7,
// Python). Lets PrerequisiteCoordinator orchestrate them uniformly and be
// unit-tested with fakes. Both real installers already expose
// Install(tempDir, progress); this adds the Kind tag.
public interface IPrerequisiteInstaller
{
    PrerequisiteKind Kind { get; }

    // Download + install the prerequisite, reporting coarse progress. Never
    // throws (returns a Failed result instead). Downloads go under tempDir.
    PrerequisiteInstallResult Install(string tempDir, Action<string> progress);
}

// One prerequisite's name + the outcome of (attempting) its install — used to
// build the Complete-screen summary.
public sealed record NamedPrerequisiteResult(
    PrerequisiteKind Kind,
    string DisplayName,
    PrerequisiteInstallResult Result);

// The user's choice when a prerequisite install fails or is declined.
public enum RetrySkipDecision
{
    Retry,
    Skip
}
