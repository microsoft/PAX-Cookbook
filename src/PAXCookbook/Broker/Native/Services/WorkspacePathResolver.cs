using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3c -- pure path arithmetic over the workspace folder. Mirrors
// the $Script:WorkspacePath / $Script:DatabaseDir / $Script:DatabaseFile
// / $Script:RecipesDir / $Script:CooksDir / $Script:RuntimeDir
// resolution in Start-Broker.ps1 (lines 96-115).
//
// Doctrine:
//   - No filesystem access (no Test-Path, no Resolve-Path beyond the
//     standard Path.GetFullPath canonicalisation that the caller
//     already supplied as a canonical path).
//   - The workspace folder is assumed canonical/absolute by the
//     caller; this resolver does not re-canonicalise it.
//   - Returns null when the configured workspace path is empty or
//     whitespace. Routes that depend on workspace state must surface
//     that as a controlled error (typically 500 with a structured
//     payload).
public static class WorkspacePathResolver
{
    public static WorkspacePaths? Resolve(string? workspaceFolderPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceFolderPath)) return null;

        var ws         = workspaceFolderPath;
        var dbDir      = Path.Combine(ws, "Database");
        var dbFile     = Path.Combine(dbDir, "cookbook.sqlite");
        var recipesDir = Path.Combine(ws, "Recipes");
        var cooksDir   = Path.Combine(ws, "Cooks");
        var runtimeDir = Path.Combine(ws, "Runtime");

        return new WorkspacePaths(
            WorkspaceFolderPath: ws,
            DatabaseDir:         dbDir,
            DatabaseFile:        dbFile,
            RecipesDir:          recipesDir,
            CooksDir:            cooksDir,
            RuntimeDir:          runtimeDir);
    }
}
