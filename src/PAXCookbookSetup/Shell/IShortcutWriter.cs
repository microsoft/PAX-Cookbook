namespace PAXCookbookSetup.Shell;

// Definition of one shortcut Setup may create. Pure data record so
// tests can assert per-shortcut shape without invoking COM.
public sealed record ShortcutDefinition(
    string Kind,                   // "start-menu" | "desktop"
    string Name,                   // visible display name (no extension)
    string Target,                 // absolute path
    string Arguments,              // verb + args
    string WorkingDirectory,
    string Aumid,
    string IconLocation,           // "path,index" form
    bool ExcludeFromRecommended,   // attempt System.AppUserModel.ExcludeFromShowInNewInstall
    int OrderHint                  // lower = earlier in folder
);

// Result returned by the shortcut writer.
public sealed record ShortcutWriteResult(
    string LnkPath,
    string Sha256,
    bool ExcludeAttempted,
    bool ExcludeSucceeded
);

// Read-only view of an existing .lnk, returned by Win32ShortcutWriter.ReadLink
// (IShellLinkW — no Windows Script Host).
public sealed record ShortcutReadResult(
    string Target,
    string Arguments,
    string WorkingDirectory,
    string IconLocation
);

// Abstraction so unit tests do not touch the real Start Menu / Desktop.
public interface IShortcutWriter
{
    // Creates (or overwrites) the .lnk file at the resolved path for
    // the given definition, applying AUMID and optional Recommended
    // suppression. Returns metadata for the manifest.
    ShortcutWriteResult Write(string folderPath, ShortcutDefinition def);

    // Deletes a .lnk file if present. Used by repair when reconciling.
    void Delete(string lnkPath);
}
