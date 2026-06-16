using System.Text;
using System.Text.Json;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3e -- per-cook folder + the five initialization files written
// before the supervisor spawns PAX. Parity with the PS broker block
// inside Invoke-CookStart that constructs:
//
//   <workspace>/Cooks/<recipeId>/<cookId>/
//       recipe-snapshot.json
//       cook-context.json
//       command.txt
//       command-argv.json
//       cook.log              (empty UTF-8 no-BOM file)
//
// Doctrine (parity with the PS broker):
//   - All files are written UTF-8 no-BOM via the
//     "<file>.tmp" -> File.Move atomic pattern. The PS broker calls
//     Write-AtomicUtf8NoBom (Cooks.ps1); we replicate the same shape
//     so a crashed write never leaves a half-written file at the
//     final path.
//   - cook.log is created empty so the supervisor's append+share-read
//     open does not race a not-yet-existent file.
//   - The cook folder is computed under WorkspacePaths.CooksDir; the
//     workspace-relative form is returned so the cook row stores the
//     relative path (parity with ConvertTo-WorkspaceRelativeCookFolder).
//   - This service does NOT touch SQLite; it only manages the
//     filesystem layout.
public sealed class CookFolderService
{
    private readonly WorkspacePaths _paths;
    private static readonly JsonWriterOptions JsonWriterOpts = new()
    {
        Indented = false,
    };

    public CookFolderService(WorkspacePaths paths) => _paths = paths;

    public CookFolderLayout Create(string recipeId, string cookId, CookInitFiles files)
    {
        var absoluteCookFolder = Path.Combine(_paths.CooksDir, recipeId, cookId);
        try
        {
            Directory.CreateDirectory(absoluteCookFolder);
        }
        catch (Exception ex)
        {
            throw new CookFolderException("cook_folder_create_failed: " + ex.Message, ex);
        }

        try
        {
            WriteAtomicUtf8NoBom(Path.Combine(absoluteCookFolder, "recipe-snapshot.json"),
                files.RecipeSnapshotJson);
            WriteAtomicUtf8NoBom(Path.Combine(absoluteCookFolder, "cook-context.json"),
                files.CookContextJson);
            WriteAtomicUtf8NoBom(Path.Combine(absoluteCookFolder, "command.txt"),
                files.CommandText);
            WriteAtomicUtf8NoBom(Path.Combine(absoluteCookFolder, "command-argv.json"),
                files.CommandArgvJson);

            var logPath = Path.Combine(absoluteCookFolder, "cook.log");
            if (!File.Exists(logPath))
            {
                // Empty file -- the supervisor opens this same path in
                // Append mode immediately after we return.
                using var _ = File.Create(logPath);
            }
        }
        catch (CookFolderException) { throw; }
        catch (Exception ex)
        {
            throw new CookFolderException("cook_init_files_failed: " + ex.Message, ex);
        }

        var relativeCookFolder = ToWorkspaceRelative(absoluteCookFolder);
        return new CookFolderLayout(
            AbsolutePath:        absoluteCookFolder,
            WorkspaceRelative:   relativeCookFolder);
    }

    public string GetCookLogPath(string absoluteCookFolder) =>
        Path.Combine(absoluteCookFolder, "cook.log");

    // Parity with ConvertTo-WorkspaceRelativeCookFolder: returns a
    // path of the form "Cooks/<recipeId>/<cookId>" using FORWARD
    // slashes when the cook folder is inside the workspace; falls
    // back to the absolute path otherwise.
    private string ToWorkspaceRelative(string absoluteCookFolder)
    {
        var ws = _paths.WorkspaceFolderPath;
        try
        {
            var fullWs    = Path.GetFullPath(ws);
            var fullCook  = Path.GetFullPath(absoluteCookFolder);
            var separator = Path.DirectorySeparatorChar;
            var wsBoundary = fullWs.EndsWith(separator.ToString(), StringComparison.Ordinal)
                ? fullWs
                : fullWs + separator;
            if (fullCook.StartsWith(wsBoundary, StringComparison.OrdinalIgnoreCase))
            {
                return fullCook.Substring(wsBoundary.Length).Replace('\\', '/');
            }
        }
        catch { }
        return absoluteCookFolder;
    }

    private static void WriteAtomicUtf8NoBom(string finalPath, string contents)
    {
        var tmpPath = finalPath + ".tmp";
        // UTF8Encoding(false) = no BOM. AppendAllText/WriteAllText
        // with this encoding writes the bytes verbatim with no BOM
        // header. File.Move overwrites atomically on NTFS.
        File.WriteAllText(tmpPath, contents, new UTF8Encoding(false));
        File.Move(tmpPath, finalPath, overwrite: true);
    }

    // Helper for the orchestrator: build a compact JSON object whose
    // PropertyName ordering does not matter (matches PS ConvertTo-Json
    // -Compress). For Stage 3e the snapshot is the raw recipe JSON
    // (already canonical); the context block is a small fixed-shape
    // object the orchestrator constructs.
    public static string BuildCookContextJson(string cookId, string recipeId,
        string trigger, string createdAtUtc, string? cookbookVersion,
        string? bundledPaxVersion, string? releaseChannel,
        string? paxScriptSha256, string paxScriptPath)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonWriterOpts))
        {
            w.WriteStartObject();
            w.WriteString("cookId",            cookId);
            w.WriteString("recipeId",          recipeId);
            w.WriteString("trigger",           trigger);
            w.WriteString("createdAt",         createdAtUtc);
            w.WriteString("cookbookVersion",   cookbookVersion ?? string.Empty);
            w.WriteString("bundledPaxVersion", bundledPaxVersion ?? string.Empty);
            w.WriteString("releaseChannel",    releaseChannel ?? string.Empty);
            w.WriteString("paxScriptSha256",   paxScriptSha256 ?? string.Empty);
            w.WriteString("paxScriptPath",     paxScriptPath);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string BuildCommandArgvJson(PaxInvocationPlan plan)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonWriterOpts))
        {
            w.WriteStartObject();
            w.WriteStartArray("paxArgv");
            foreach (var a in plan.PaxArgv) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteString("extraArguments", plan.ExtraArguments ?? string.Empty);
            w.WriteStartArray("spawnArgv");
            foreach (var a in plan.SpawnArgv) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteString("paxScriptPath", plan.PaxScriptPath ?? string.Empty);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // Compact JSON array of plan.SpawnArgv for the cooks.command_argv_json
    // column. The PS broker stores this exact shape so a downstream
    // chef tool that opens the workspace can reconstruct the spawn.
    public static string BuildSpawnArgvJson(string[] spawnArgv)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonWriterOpts))
        {
            w.WriteStartArray();
            foreach (var a in spawnArgv) w.WriteStringValue(a);
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

public sealed record CookFolderLayout(
    string AbsolutePath,
    string WorkspaceRelative);

public sealed record CookInitFiles(
    string RecipeSnapshotJson,
    string CookContextJson,
    string CommandText,
    string CommandArgvJson);

public sealed class CookFolderException : Exception
{
    public CookFolderException(string message, Exception? inner = null)
        : base(message, inner) { }
}
