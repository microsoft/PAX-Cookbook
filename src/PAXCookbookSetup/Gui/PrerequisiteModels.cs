namespace PAXCookbookSetup.Gui;

// Which external prerequisite a status describes.
public enum PrerequisiteKind
{
    PowerShell7,
    Python
}

// Result of probing the local machine for one prerequisite.
//
//   Satisfied        - the requirement is met (a usable version is present).
//   DetectedVersion  - any version we found, even one too old to satisfy the
//                      minimum (null when nothing was found, or when an
//                      executable was located but its version could not be read).
//   Path             - the resolved executable path, when known.
//   DetectionSource  - where we found it ("PATH", "ProgramFiles", "Registry",
//                      "py-launcher", "too-old", "not-found").
public sealed record PrerequisiteStatus(
    PrerequisiteKind Kind,
    string DisplayName,
    bool Satisfied,
    string? DetectedVersion,
    string? Path,
    string DetectionSource)
{
    public static PrerequisiteStatus Missing(PrerequisiteKind kind, string displayName)
        => new(kind, displayName, Satisfied: false, DetectedVersion: null,
               Path: null, DetectionSource: "not-found");

    // A short human-readable line for the Prerequisites screen.
    public string ToDisplayLine()
    {
        if (Satisfied)
            return DetectedVersion is { Length: > 0 }
                ? $"{DisplayName} found (v{DetectedVersion})"
                : $"{DisplayName} found";

        return DetectedVersion is { Length: > 0 }
            ? $"{DisplayName} v{DetectedVersion} found, but a newer version is required — will be installed"
            : $"{DisplayName} not found — will be installed";
    }
}
