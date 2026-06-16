namespace PAXCookbook.Broker.Native.Models;

// Stage 3c -- pure path arithmetic over the operator-supplied
// WorkspaceFolderPath. Mirrors the $Script:* path constants set up by
// Start-Broker.ps1 (lines 96-115). Computed once at broker startup;
// every field is a plain string. No filesystem access here -- the
// existence of each directory/file is probed lazily by the readers
// that actually touch them (SqliteWorkspaceReader, CookReadRoutes,
// etc.), so a missing directory surfaces as a controlled error at the
// route boundary instead of a startup crash.
public sealed record WorkspacePaths(
    string WorkspaceFolderPath,
    string DatabaseDir,
    string DatabaseFile,
    string RecipesDir,
    string CooksDir,
    string RuntimeDir);
