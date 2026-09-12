namespace PAXCookbook.App;

// The kind of resume target a validated checkpoint path names.
//
// PAX accepts two resume shapes and they are NOT interchangeable: an explicit
// checkpoint FILE is passed as `-Resume "<file>"`, while a checkpoint FOLDER is
// passed as a bare auto-discover `-Resume -OutputPath "<folder>"`. Picking the
// wrong shape hands PAX a resume target it cannot bind.
internal enum ResumeCheckpointKind
{
    Directory,
    JsonFile,
}

// Why a checkpoint path was refused. Every value maps to the SAME existing
// 400 `invalid_checkpoint_path` contract; the reason is additive provenance
// for the operator and the logs, never a new error code.
internal enum ResumeCheckpointRejection
{
    None,
    Empty,
    NotFullyQualified,
    Malformed,
    NotFound,
    WrongKind,
}

// The bounded outcome of validating an operator-supplied checkpoint path.
// `CanonicalPath`, `Kind`, and `IdentityKey` are meaningful only when `Ok` is
// true; `Rejection` is meaningful only when `Ok` is false.
internal sealed record ResumeCheckpointPathResult(
    bool Ok,
    string CanonicalPath,
    ResumeCheckpointKind Kind,
    string IdentityKey,
    ResumeCheckpointRejection Rejection);

// The single server-side validation + normalization path for the operator's
// resume checkpoint location.
//
// Before this existed the resume route accepted any non-empty string and then
// decided the PAX resume shape purely from the ".json" suffix, so a relative
// path, a drive-relative path, a path that does not exist, and an existing
// DIRECTORY literally named "foo.json" all flowed through to argv projection.
// The last case is the sharp one: a folder named foo.json would have been
// rendered as `-Resume "<folder>"`, an explicit resume FILE, which is not what
// the operator asked for.
//
// This class deliberately imposes NO new restriction beyond correctness. There
// is no workspace containment rule, no length cap, no path-count cap, and no
// reparse-point ban: an operator may legitimately resume a run whose output
// lives anywhere on their own machine, including a mapped or UNC share. What it
// does enforce is that the path is unambiguous (fully qualified), canonical,
// real, and of a kind PAX can actually resume.
//
// It reads no file contents, opens no handle beyond an existence probe, spawns
// nothing, and returns no secret.
internal static class ResumeCheckpointPath
{
    private const string JsonSuffix = ".json";

    // Validate and normalize an operator-supplied checkpoint path.
    //
    // Order matters and each step is a distinct refusal:
    //   trim -> reject empty
    //        -> reject anything not fully qualified. Path.IsPathFullyQualified
    //           accepts drive-rooted ("C:\x") and UNC ("\\server\share\x") and
    //           rejects relative ("foo\bar"), rooted-but-not-qualified ("\foo"),
    //           and drive-relative ("C:foo") -- the last of which resolves
    //           against the process's per-drive current directory and is exactly
    //           the ambiguity a resume must never inherit.
    //        -> canonicalize with Path.GetFullPath
    //        -> classify by EXISTENCE, never by suffix: an existing directory is
    //           a checkpoint folder; an existing file must additionally end in
    //           .json; anything that does not exist is refused.
    internal static ResumeCheckpointPathResult Validate(string? candidate)
    {
        string trimmed = (candidate ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Reject(ResumeCheckpointRejection.Empty);
        }

        bool fullyQualified;
        try
        {
            fullyQualified = Path.IsPathFullyQualified(trimmed);
        }
        catch (ArgumentException)
        {
            return Reject(ResumeCheckpointRejection.Malformed);
        }

        if (!fullyQualified)
        {
            return Reject(ResumeCheckpointRejection.NotFullyQualified);
        }

        string canonical;
        try
        {
            canonical = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (
            ex is ArgumentException ||
            ex is NotSupportedException ||
            ex is PathTooLongException ||
            ex is IOException ||
            ex is System.Security.SecurityException)
        {
            // A malformed path (embedded null, invalid characters, an
            // unresolvable form) is refused rather than allowed to throw out of
            // the route.
            return Reject(ResumeCheckpointRejection.Malformed);
        }

        if (canonical.Length == 0)
        {
            return Reject(ResumeCheckpointRejection.Malformed);
        }

        if (Directory.Exists(canonical))
        {
            string normalized = TrimTrailingSeparatorsToRoot(canonical);
            return new ResumeCheckpointPathResult(
                Ok: true,
                CanonicalPath: normalized,
                Kind: ResumeCheckpointKind.Directory,
                IdentityKey: ToIdentityKey(normalized),
                Rejection: ResumeCheckpointRejection.None);
        }

        if (File.Exists(canonical))
        {
            if (!canonical.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return Reject(ResumeCheckpointRejection.WrongKind);
            }

            return new ResumeCheckpointPathResult(
                Ok: true,
                CanonicalPath: canonical,
                Kind: ResumeCheckpointKind.JsonFile,
                IdentityKey: ToIdentityKey(canonical),
                Rejection: ResumeCheckpointRejection.None);
        }

        return Reject(ResumeCheckpointRejection.NotFound);
    }

    // Root-safe trailing-separator normalization.
    //
    // "C:\PAX\out\" and "C:\PAX\out" name the same folder and must produce the
    // same identity, so trailing separators are trimmed -- but ONLY down to the
    // path root. Trimming "C:\" to "C:" would turn an absolute path into a
    // DRIVE-RELATIVE one (which resolves against the process's per-drive current
    // directory), and trimming past a UNC share would name a different location
    // entirely. Both are silently wrong, so the loop stops at the root boundary.
    //
    // Note the asymmetry in the framework, which this loop inherits rather than
    // fights: Path.GetPathRoot reports a drive root WITH its separator ("C:\")
    // and a UNC root WITHOUT one ("\\server\share"). So "C:\" is preserved
    // verbatim while "\\server\share\" normalizes to "\\server\share". Neither is
    // truncated, and both remain fully qualified.
    internal static string TrimTrailingSeparatorsToRoot(string canonical)
    {
        if (string.IsNullOrEmpty(canonical))
        {
            return canonical;
        }

        string root;
        try
        {
            root = Path.GetPathRoot(canonical) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            root = string.Empty;
        }

        int end = canonical.Length;
        while (end > root.Length && IsSeparator(canonical[end - 1]))
        {
            end--;
        }

        return end == canonical.Length ? canonical : canonical.Substring(0, end);
    }

    // Windows path identity is case-insensitive, so the concurrency key is the
    // normalized canonical path upper-cased invariantly. "c:\pax\out" and
    // "C:\PAX\OUT\" are the SAME resume target and must collide.
    internal static string ToIdentityKey(string normalizedCanonicalPath) =>
        normalizedCanonicalPath.ToUpperInvariant();

    private static bool IsSeparator(char c) =>
        c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    private static ResumeCheckpointPathResult Reject(ResumeCheckpointRejection rejection) =>
        new(
            Ok: false,
            CanonicalPath: string.Empty,
            Kind: ResumeCheckpointKind.Directory,
            IdentityKey: string.Empty,
            Rejection: rejection);
}
