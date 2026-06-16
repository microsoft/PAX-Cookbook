using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-A -- native cook readiness probe.
//
// Parity scope (mirrors Test-CookReadiness in
// app/broker/Routes/Cooks.ps1 ~line 2547):
//   * Wire shape preserved verbatim: {recipeId, resumeCookId,
//     generatedAtUtc, status, summary{blocked,warning,ok,notChecked},
//     checks[{id,label,scope,severity,status,detail,evidence,
//     remediation}]}.
//   * status precedence: blocked > warning > ok. Any not_checked
//     check is counted in summary.notChecked but does NOT alter
//     status (parity with the PS broker, which also treats
//     network.reachability as not_checked without lowering an
//     otherwise-ok overall verdict).
//   * checks emitted by the native port (full coverage):
//       recipe.recipe_id_format        -- Crockford ULID format.
//       recipe.recipe_present          -- DB row exists, not deleted.
//       recipe.snapshot_loadable       -- file readable JSON.
//       pax.script_present             -- bundled file exists.
//       pax.script_integrity           -- SHA-256 vs VERSION.json.
//       workspace.directory_present    -- workspace folder exists.
//       workspace.database_present     -- cookbook.sqlite exists.
//       resume.cook_id_format          -- ULID when cookId supplied.
//       resume.cook_present            -- cook row exists.
//       resume.recipe_id_match         -- cook's recipe_id matches.
//       resume.checkpoint_present      -- cook_folder + checkpoint
//                                         file (presence-only).
//   * checks the PS broker emits that the native port does NOT yet
//     cover (auth profile detail / disk space / param matrix /
//     network reachability / M365 licensing) are surfaced verbatim
//     as status=not_checked, detail="<scope> readiness deferred to a
//     later native stage; PowerShell broker covers this check."
//     This is honest and preserves the readiness contract: the SPA
//     sees a non-zero notChecked count and renders the same
//     diagnostic banner it does for network reachability.
public sealed class CookReadinessProbe
{
    private static readonly Regex UlidRegex = new(
        "^[0-9A-HJKMNP-TV-Z]{26}$", RegexOptions.Compiled);

    private readonly SqliteWorkspaceReader        _sqlite;
    private readonly PaxScriptIntegrityVerifier?  _pax;
    private readonly Func<DateTimeOffset>         _clock;

    public CookReadinessProbe(
        SqliteWorkspaceReader        sqlite,
        PaxScriptIntegrityVerifier?  pax,
        Func<DateTimeOffset>?        clock = null)
    {
        _sqlite = sqlite;
        _pax    = pax;
        _clock  = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public CookReadinessResult Probe(string? recipeId, string? cookId)
    {
        var generatedAtUtc = _clock().ToUniversalTime().ToString("o");
        var checks = new List<CookReadinessCheck>();

        // -------- recipe.recipe_id_format --------
        var recipeIdTrim = recipeId?.Trim() ?? string.Empty;
        var recipeFormatOk =
            !string.IsNullOrEmpty(recipeIdTrim) && UlidRegex.IsMatch(recipeIdTrim);
        checks.Add(new CookReadinessCheck(
            Id:          "recipe.recipe_id_format",
            Label:       "Recipe identifier format",
            Scope:       "recipe",
            Severity:    "blocker",
            Status:      recipeFormatOk ? "ok" : "blocked",
            Detail:      recipeFormatOk
                ? "Recipe identifier is a valid 26-character Crockford-base32 ULID."
                : "Recipe identifier is missing or not a 26-character Crockford-base32 ULID.",
            Evidence:    new Dictionary<string, object?>
            {
                ["recipeId"]    = recipeId,
                ["pattern"]     = "^[0-9A-HJKMNP-TV-Z]{26}$",
            },
            Remediation: "Provide a 26-character Crockford-base32 recipe ULID."));

        // -------- workspace.directory_present --------
        var workspaceDir = _sqlite.Paths.WorkspaceFolderPath;
        var workspaceExists = !string.IsNullOrEmpty(workspaceDir) && Directory.Exists(workspaceDir);
        checks.Add(new CookReadinessCheck(
            Id:          "workspace.directory_present",
            Label:       "Workspace folder present",
            Scope:       "local",
            Severity:    "blocker",
            Status:      workspaceExists ? "ok" : "blocked",
            Detail:      workspaceExists
                ? "Workspace folder exists at \"" + workspaceDir + "\"."
                : "Workspace folder does not exist at \"" + workspaceDir + "\".",
            Evidence:    new Dictionary<string, object?>
            {
                ["workspaceFolderPath"] = workspaceDir,
            },
            Remediation: "Reopen or re-create the configured workspace folder."));

        // -------- workspace.database_present --------
        var dbExists = _sqlite.DatabaseFileExists();
        checks.Add(new CookReadinessCheck(
            Id:          "workspace.database_present",
            Label:       "Workspace SQLite database present",
            Scope:       "local",
            Severity:    "blocker",
            Status:      dbExists ? "ok" : "blocked",
            Detail:      dbExists
                ? "cookbook.sqlite is present in the workspace database directory."
                : "cookbook.sqlite is missing from the workspace database directory.",
            Evidence:    new Dictionary<string, object?>
            {
                ["databaseFile"] = _sqlite.Paths.DatabaseFile,
            },
            Remediation: "Open or repair the workspace so cookbook.sqlite is created."));

        // -------- recipe.recipe_present --------
        RecipeMetaRow? recipeRow = null;
        if (recipeFormatOk && dbExists)
        {
            recipeRow = _sqlite.GetRecipeById(recipeIdTrim);
        }
        var recipeRowOk = recipeRow is not null && string.IsNullOrEmpty(recipeRow.DeletedAt);
        checks.Add(new CookReadinessCheck(
            Id:          "recipe.recipe_present",
            Label:       "Recipe row present",
            Scope:       "recipe",
            Severity:    "blocker",
            Status:      recipeFormatOk && dbExists
                            ? (recipeRowOk ? "ok" : "blocked")
                            : "not_checked",
            Detail:      recipeRowOk
                ? "Recipe row exists and is not soft-deleted."
                : (recipeFormatOk && dbExists
                    ? "Recipe row is missing or has been soft-deleted."
                    : "Recipe row lookup skipped because earlier blockers fired."),
            Evidence:    new Dictionary<string, object?>
            {
                ["recipeId"] = recipeIdTrim,
            },
            Remediation: "Import or restore the recipe before launching the cook."));

        // -------- recipe.snapshot_loadable --------
        string? recipeFilePath = null;
        if (recipeRow is not null)
        {
            recipeFilePath = Path.IsPathRooted(recipeRow.FilePath)
                ? recipeRow.FilePath
                : Path.Combine(_sqlite.Paths.WorkspaceFolderPath, recipeRow.FilePath);
        }
        bool? snapshotLoadable = null;
        string? snapshotDetail = null;
        if (recipeRow is not null && recipeFilePath is not null)
        {
            try
            {
                if (!File.Exists(recipeFilePath))
                {
                    snapshotLoadable = false;
                    snapshotDetail   = "Recipe file does not exist on disk.";
                }
                else
                {
                    using var s = File.OpenRead(recipeFilePath);
                    using var doc = JsonDocument.Parse(s);
                    snapshotLoadable = true;
                    snapshotDetail   = "Recipe JSON is loadable.";
                }
            }
            catch (Exception ex)
            {
                snapshotLoadable = false;
                snapshotDetail   = "Recipe JSON load failed: " + ex.Message;
            }
        }
        checks.Add(new CookReadinessCheck(
            Id:          "recipe.snapshot_loadable",
            Label:       "Recipe snapshot loadable",
            Scope:       "recipe",
            Severity:    "blocker",
            Status:      snapshotLoadable is null
                            ? "not_checked"
                            : (snapshotLoadable.Value ? "ok" : "blocked"),
            Detail:      snapshotDetail
                            ?? "Recipe snapshot load skipped because earlier blockers fired.",
            Evidence:    new Dictionary<string, object?>
            {
                ["recipeFilePath"] = recipeFilePath,
            },
            Remediation: "Repair or re-export the recipe JSON file."));

        // -------- pax.script_present --------
        var paxScriptPath = _pax?.PaxScriptPath;
        var paxScriptExists =
            !string.IsNullOrEmpty(paxScriptPath) && File.Exists(paxScriptPath);
        checks.Add(new CookReadinessCheck(
            Id:          "pax.script_present",
            Label:       "Bundled PAX script present",
            Scope:       "pax",
            Severity:    "blocker",
            Status:      paxScriptExists ? "ok" : "blocked",
            Detail:      paxScriptExists
                ? "Bundled PAX script exists at \"" + paxScriptPath + "\"."
                : "Bundled PAX script is missing or unconfigured.",
            Evidence:    new Dictionary<string, object?>
            {
                ["paxScriptPath"] = paxScriptPath,
            },
            Remediation: "Reinstall PAX Cookbook to restore the bundled PAX script."));

        // -------- pax.script_integrity --------
        string paxIntegrityStatus  = "not_checked";
        string paxIntegrityDetail  = "PAX integrity check skipped (no integrity verifier wired).";
        string? paxExpected        = null;
        string? paxActual          = null;
        if (_pax is not null)
        {
            var verdict = _pax.Verify();
            switch (verdict.Status)
            {
                case PaxIntegrityStatus.Match:
                    paxIntegrityStatus = "ok";
                    paxIntegrityDetail = "Bundled PAX SHA-256 matches the VERSION.json baseline.";
                    paxExpected        = verdict.Expected;
                    paxActual          = verdict.Actual;
                    break;
                case PaxIntegrityStatus.Mismatch:
                    paxIntegrityStatus = "blocked";
                    paxIntegrityDetail = "Bundled PAX SHA-256 does NOT match the VERSION.json baseline.";
                    paxExpected        = verdict.Expected;
                    paxActual          = verdict.Actual;
                    break;
                case PaxIntegrityStatus.MissingScript:
                    paxIntegrityStatus = "blocked";
                    paxIntegrityDetail = "Bundled PAX script is missing; integrity cannot be verified.";
                    break;
                case PaxIntegrityStatus.NoBaseline:
                    paxIntegrityStatus = "blocked";
                    paxIntegrityDetail = "VERSION.json baseline is unavailable: " + (verdict.Detail ?? "");
                    break;
                case PaxIntegrityStatus.HashFailed:
                    paxIntegrityStatus = "blocked";
                    paxIntegrityDetail = "Bundled PAX hash failed: " + (verdict.Detail ?? "");
                    break;
            }
        }
        checks.Add(new CookReadinessCheck(
            Id:          "pax.script_integrity",
            Label:       "Bundled PAX script SHA-256 integrity",
            Scope:       "pax",
            Severity:    "blocker",
            Status:      paxIntegrityStatus,
            Detail:      paxIntegrityDetail,
            Evidence:    new Dictionary<string, object?>
            {
                ["expected"] = paxExpected,
                ["actual"]   = paxActual,
            },
            Remediation: "Reinstall PAX Cookbook to restore the bundled PAX script."));

        // -------- resume.* (only when cookId supplied) --------
        var cookIdTrim = cookId?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(cookIdTrim))
        {
            var resumeFormatOk = UlidRegex.IsMatch(cookIdTrim);
            checks.Add(new CookReadinessCheck(
                Id:          "resume.cook_id_format",
                Label:       "Resume cook identifier format",
                Scope:       "resume",
                Severity:    "blocker",
                Status:      resumeFormatOk ? "ok" : "blocked",
                Detail:      resumeFormatOk
                    ? "Resume cook identifier is a valid Crockford-base32 ULID."
                    : "Resume cook identifier is not a valid Crockford-base32 ULID.",
                Evidence:    new Dictionary<string, object?>
                {
                    ["cookId"]  = cookIdTrim,
                    ["pattern"] = "^[0-9A-HJKMNP-TV-Z]{26}$",
                },
                Remediation: "Supply a 26-character Crockford-base32 cook ULID, or omit cookId to start a new cook."));

            CookRow? cookRow = null;
            if (resumeFormatOk && dbExists)
            {
                cookRow = _sqlite.GetCookById(cookIdTrim);
            }
            checks.Add(new CookReadinessCheck(
                Id:          "resume.cook_present",
                Label:       "Resume cook row present",
                Scope:       "resume",
                Severity:    "blocker",
                Status:      resumeFormatOk && dbExists
                                ? (cookRow is not null ? "ok" : "blocked")
                                : "not_checked",
                Detail:      cookRow is not null
                    ? "Cook row exists; status=\"" + cookRow.Status + "\"."
                    : (resumeFormatOk && dbExists
                        ? "Cook row not found for the supplied resume cookId."
                        : "Cook row lookup skipped because earlier blockers fired."),
                Evidence:    new Dictionary<string, object?>
                {
                    ["cookId"] = cookIdTrim,
                },
                Remediation: "Verify the cookId or start a new cook without resume."));

            // resume.recipe_id_match -- WARNING severity (parity with PS).
            string matchStatus = "not_checked";
            string matchDetail = "Recipe-cook ID match skipped.";
            if (cookRow is not null)
            {
                if (string.Equals(cookRow.RecipeId, recipeIdTrim, StringComparison.Ordinal))
                {
                    matchStatus = "ok";
                    matchDetail = "Resume cook is associated with the same recipe.";
                }
                else
                {
                    matchStatus = "warning";
                    matchDetail = "Resume cook was originally produced by recipe \""
                                + (cookRow.RecipeId ?? "<null>")
                                + "\", not \"" + recipeIdTrim + "\".";
                }
            }
            checks.Add(new CookReadinessCheck(
                Id:          "resume.recipe_id_match",
                Label:       "Resume cook recipe match",
                Scope:       "resume",
                Severity:    "warning",
                Status:      matchStatus,
                Detail:      matchDetail,
                Evidence:    new Dictionary<string, object?>
                {
                    ["recipeId"]       = recipeIdTrim,
                    ["cookRecipeId"]   = cookRow?.RecipeId,
                },
                Remediation: "Confirm that you intend to resume a cook produced by a different recipe."));

            // resume.checkpoint_present -- presence only.
            string ckptStatus  = "not_checked";
            string ckptDetail  = "Checkpoint presence skipped.";
            string? checkpointPath = null;
            if (cookRow is not null && !string.IsNullOrEmpty(cookRow.CookFolder))
            {
                var folderPath = Path.IsPathRooted(cookRow.CookFolder)
                    ? cookRow.CookFolder
                    : Path.Combine(_sqlite.Paths.WorkspaceFolderPath, cookRow.CookFolder);
                checkpointPath = Path.Combine(folderPath, "checkpoint.json");
                if (File.Exists(checkpointPath))
                {
                    ckptStatus = "ok";
                    ckptDetail = "checkpoint.json is present in the cook folder.";
                }
                else
                {
                    ckptStatus = "warning";
                    ckptDetail = "checkpoint.json is missing; resume may begin from the start.";
                }
            }
            checks.Add(new CookReadinessCheck(
                Id:          "resume.checkpoint_present",
                Label:       "Resume cook checkpoint present",
                Scope:       "resume",
                Severity:    "warning",
                Status:      ckptStatus,
                Detail:      ckptDetail,
                Evidence:    new Dictionary<string, object?>
                {
                    ["checkpointPath"] = checkpointPath,
                },
                Remediation: "Confirm that the cook folder still contains a checkpoint, or start the cook fresh."));
        }

        // -------- Honest "not yet ported" checks --------
        // Parity with PS Test-CookReadiness coverage that this stage
        // does NOT yet implement; the wire shape stays consistent so
        // the SPA renders the same readiness scaffolding.
        AddDeferred(checks, "auth.profile_present", "Auth profile bound to recipe",
            "auth",        "blocker",
            "Auth profile readiness check is deferred to a later native broker stage; the PowerShell broker covers this check.");
        AddDeferred(checks, "local.disk_space", "Workspace disk space sufficient",
            "local",       "warning",
            "Disk-space readiness check is deferred to a later native broker stage; the PowerShell broker covers this check.");
        AddDeferred(checks, "recipe.parameter_matrix", "Recipe parameters consistent with PAX",
            "recipe",      "warning",
            "Recipe parameter-matrix readiness check is deferred to a later native broker stage; the PowerShell broker covers this check.");
        AddDeferred(checks, "network.reachability", "M365 reachability",
            "network",     "info",
            "Network reachability is owned by PAX at cook-time; readiness probe does not perform external calls.");
        AddDeferred(checks, "destination.license_check", "Destination tenant license",
            "destination", "info",
            "Destination tenant license check is deferred to a later native broker stage; the PowerShell broker covers this check.");

        // -------- Aggregate --------
        int blocked    = 0;
        int warning    = 0;
        int ok         = 0;
        int notChecked = 0;
        foreach (var c in checks)
        {
            switch (c.Status)
            {
                case "blocked":     blocked++;    break;
                case "warning":     warning++;    break;
                case "ok":          ok++;         break;
                case "not_checked": notChecked++; break;
            }
        }
        string overall;
        if (blocked > 0)      overall = "blocked";
        else if (warning > 0) overall = "warning";
        else                  overall = "ok";

        return new CookReadinessResult(
            RecipeId:       recipeId ?? string.Empty,
            ResumeCookId:   cookId   ?? string.Empty,
            GeneratedAtUtc: generatedAtUtc,
            Status:         overall,
            Summary:        new CookReadinessSummary(blocked, warning, ok, notChecked),
            Checks:         checks);
    }

    private static void AddDeferred(
        List<CookReadinessCheck> sink,
        string id, string label, string scope, string severity, string detail)
    {
        sink.Add(new CookReadinessCheck(
            Id:          id,
            Label:       label,
            Scope:       scope,
            Severity:    severity,
            Status:      "not_checked",
            Detail:      detail,
            Evidence:    new Dictionary<string, object?>(),
            Remediation: "No remediation required; this check is informational on the native broker."));
    }
}
