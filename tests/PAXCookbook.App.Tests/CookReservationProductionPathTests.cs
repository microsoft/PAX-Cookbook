using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 32 - PRODUCTION-ENTRY coverage for the two cook reservation paths.
//
// Read the scope of each test carefully; they prove different things.
//
//   * The RECIPE test is FAST-PATH SIDE-EFFECT COVERAGE ONLY. StartManualCook
//     refuses at gate 9 (the read-only busy fast path) long before the atomic
//     reservation is reached, so this test proves that a recipe_busy refusal
//     creates NO cook row and NO cook folder. It does NOT prove the reservation:
//     it would stay green even if the WHERE NOT EXISTS predicate were deleted.
//     The reservation is proven at the persistence level in
//     RecipeCookReservationTests.
//
//   * The RESUME test IS production-path reservation-order coverage.
//     StartResumeCook has NO earlier busy gate, so a pre-seeded winner drives the
//     real caller all the way into the reservation. It fails if cook-folder
//     creation is ever moved back in front of the reservation.
//
// Nothing here spawns a process, runs PAX, starts a Bake, reads engine bytes, or
// resolves a Chef's Key. Every refusal happens before any child could exist.
public class CookReservationProductionPathTests : IDisposable
{
    private readonly List<string> _workspaces = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string workspace in _workspaces)
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch
            {
                // A leftover temp workspace is harmless; never fail a test on cleanup.
            }
        }

        GC.SuppressFinalize(this);
    }

    private const string RecipeId = RecipeCookReservationTests.RecipeA;

    private string NewWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "paxcb-c32p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _workspaces.Add(root);
        WorkspaceDatabase.EnsureInitialized(root);
        return root;
    }

    // A VersionInfo carrying no secret and no real acquisition state; the paths
    // under test never read a version value before refusing.
    private static VersionInfo Version() => new(
        CookbookVersion: "0.0.0-test",
        ReleaseChannel: "test",
        PaxVersion: "1.11.11",
        PaxSha256: new string('0', 64),
        PaxRelativePath: "resources/pax/PAX.ps1",
        PaxAcquisitionPolicy: "managed",
        EngineManifestUrl: null,
        EngineManifestTrustAnchorThumbprint: null,
        ManifestSignaturePolicy: "required",
        BuildTimestamp: null);

    // An ACQUIRED engine RECORD. It is a plain data object: constructing it reads
    // no engine bytes, copies nothing, and cannot start anything. It exists only
    // so gate 6 passes and the busy / reservation gates below can be reached.
    private static EngineAcquisitionResult AcquiredEngine(string workspace) => new()
    {
        Policy = "managed",
        State = "acquired",
        IsAcquired = true,
        AcquisitionRequired = false,
        ManagedEnginePathPresent = true,
        RecordedSha256 = new string('A', 64),
        Version = "1.11.11",
        Source = "test",
        Message = "test",
        InstallStatePath = Path.Combine(workspace, "install-state.json"),
        ManagedEnginePath = Path.Combine(workspace, "engine", "PAX.ps1"),
    };

    private static string? Prop(object body, string name) =>
        body.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(body) as string;

    private static void SeedRecipeOnDisk(string workspace)
    {
        using (SqliteConnection conn = RecipeCookReservationTests.Open(workspace))
        {
            RecipeCookReservationTests.SeedRecipeRow(conn, RecipeId);
        }

        string recipesDir = Path.Combine(workspace, "Recipes");
        Directory.CreateDirectory(recipesDir);
        File.WriteAllText(
            Path.Combine(recipesDir, RecipeId + ".recipe.json"),
            """
            {
              "recipeId": "01ARZ3NDEKTSV4RRFFQ69G5FAV",
              "recipeSchemaVersion": 1,
              "paxAdapterVersion": "1.11.11",
              "executionMode": "local-manual",
              "identity": { "name": "cycle 32 fast path" },
              "ingredients": {
                "m365Usage": { "includeM365Usage": false },
                "entraUserData": { "includeUserInfo": false },
                "agent365": { "onlyAgent365Info": true }
              },
              "query": { "mode": "agent365Only" },
              "processing": {},
              "destinations": { "agent365": { "mode": "outputPath", "path": "C:\\PAX\\catalog.csv" } },
              "auth": { "mode": "WebLogin" }
            }
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static int CookFolderCount(string workspace, string bucket)
    {
        string dir = Path.Combine(workspace, "Cooks", bucket);
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Length : 0;
    }

    // Proves the folder counter above is LIVE. Cycle 31 shipped a zero-folder
    // assertion that nobody had shown could ever be non-zero; this makes the
    // negative meaningful.
    private static void AssertFolderCounterCanSeeAFolder(string workspace, string bucket)
    {
        string probe = Path.Combine(workspace, "Cooks", bucket, "counter-probe");
        Directory.CreateDirectory(probe);
        Assert.Equal(1, CookFolderCount(workspace, bucket));
        Directory.Delete(probe);
        Assert.Equal(0, CookFolderCount(workspace, bucket));
    }

    // ---- RECIPE: gate-9 fast path, side effects only -------------------------

    [Fact]
    public void Manual_cook_refused_by_the_busy_fast_path_creates_no_row_and_no_folder()
    {
        // SCOPE: fast-path side effects ONLY. Gate 9 refuses before the atomic
        // reservation is ever reached, so this does not exercise the reservation.
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace);

        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(
            workspace, "cook-winner", RecipeId).Reserved);

        (int status, object body) = RecipeReadModel.StartManualCook(
            workspace, Version(), AcquiredEngine(workspace), RecipeId,
            method: "POST", requestPath: "/api/v1/recipes/" + RecipeId + "/cook",
            minFreeDiskBytes: 0, pwshPathOverride: null);

        Assert.Equal(409, status);
        Assert.Equal("recipe_busy", Prop(body, "error"));
        Assert.Equal("cook-winner", Prop(body, "cookId"));

        using (SqliteConnection conn = RecipeCookReservationTests.Open(workspace))
        {
            Assert.Equal(1, RecipeCookReservationTests.CountAny(conn, RecipeId));
            Assert.Equal(1, RecipeCookReservationTests.CountRunning(conn, RecipeId));
        }

        Assert.Equal(0, CookFolderCount(workspace, RecipeId));
        AssertFolderCounterCanSeeAFolder(workspace, RecipeId);
    }

    [Fact]
    public void The_busy_refusal_is_caused_by_the_running_row_and_not_by_the_harness()
    {
        // POSITIVE CONTROL for the fast-path test. With the SAME workspace, the
        // SAME recipe, and the SAME call, a TERMINAL prior cook produces a
        // different outcome: the request passes gate 9 and is then stopped by the
        // gate-11 disk floor. So "409 recipe_busy" above is attributable to the
        // running row rather than to something the harness always returns.
        //
        // An impossible disk floor is used deliberately: gate 11 is the first
        // gate after the busy check, so the control still refuses BEFORE any
        // reservation, cook folder, or child could exist. Nothing is spawned.
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace);

        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(
            workspace, "cook-old", RecipeId).Reserved);
        using (SqliteConnection conn = RecipeCookReservationTests.Open(workspace))
        using (SqliteCommand upd = conn.CreateCommand())
        {
            upd.CommandText = "UPDATE cooks SET status = 'completed' WHERE cook_id = 'cook-old';";
            Assert.Equal(1, upd.ExecuteNonQuery());
        }

        (int status, object body) = RecipeReadModel.StartManualCook(
            workspace, Version(), AcquiredEngine(workspace), RecipeId,
            method: "POST", requestPath: "/api/v1/recipes/" + RecipeId + "/cook",
            minFreeDiskBytes: long.MaxValue, pwshPathOverride: null);

        Assert.Equal(507, status);
        Assert.Equal("insufficient_disk_space", Prop(body, "error"));

        using (SqliteConnection conn = RecipeCookReservationTests.Open(workspace))
        {
            Assert.Equal(1, RecipeCookReservationTests.CountAny(conn, RecipeId));
            Assert.Equal(0, RecipeCookReservationTests.CountRunning(conn, RecipeId));
        }
        Assert.Equal(0, CookFolderCount(workspace, RecipeId));
    }

    // ---- RESUME: production-path reservation-order coverage ------------------

    [Fact]
    public void Resume_loser_reaches_the_real_reservation_and_creates_no_folder()
    {
        // SCOPE: production-path reservation ORDER. StartResumeCook has no early
        // busy gate, so this drives the real route into the atomic reservation.
        // If cook-folder creation were moved back in front of the reservation the
        // folder assertion below would fail.
        string workspace = NewWorkspace();
        string checkpoint = Path.Combine(workspace, "checkpoint");
        Directory.CreateDirectory(checkpoint);

        ResumeCheckpointPathResult validated = ResumeCheckpointPath.Validate(checkpoint);
        Assert.True(validated.Ok);

        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(
            workspace, "cook-winner", validated.IdentityKey));

        (int status, object body) = RecipeReadModel.StartResumeCook(
            workspace, Version(), AcquiredEngine(workspace), checkpoint,
            force: false, chefKeyId: null, pwshPathOverride: null);

        Assert.Equal(409, status);
        Assert.Equal("resume_already_running", Prop(body, "error"));
        Assert.Equal("cook-winner", Prop(body, "cookId"));

        Assert.Equal(0, CookFolderCount(workspace, "__resume__"));
        AssertFolderCounterCanSeeAFolder(workspace, "__resume__");
    }

    // ---- the new intermediate state: a running row with no folder yet --------

    [Fact]
    public void A_running_row_whose_cook_folder_does_not_exist_yet_is_tolerated_everywhere()
    {
        // Moving the insert in front of folder creation changes the winner's
        // side-effect order from folder -> files -> row to row -> folder -> files,
        // so a running row now briefly exists with no cook folder on disk. This
        // asserts every consumer of that row tolerates it.
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace);
        string cookId = Guid.NewGuid().ToString().ToLowerInvariant();

        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, cookId, RecipeId).Reserved);
        string cookFolderAbs = Path.Combine(workspace, "Cooks", RecipeId, cookId);
        Assert.False(Directory.Exists(cookFolderAbs));

        // Index projection: the cook is listed, not skipped and not an error.
        object list = RecipeReadModel.ListCooks(workspace);
        var cooks = list.GetType().GetProperty("cooks")!.GetValue(list) as System.Collections.IEnumerable;
        Assert.NotNull(cooks);
        int listed = 0;
        foreach (object? item in cooks!)
        {
            listed++;
            Assert.Equal(cookId, Prop(item!, "cookId"));
        }
        Assert.Equal(1, listed);

        // Detail projection: 200, with every folder-derived field a clean absence.
        (int detailStatus, object detail) = RecipeReadModel.GetCookDetail(workspace, cookId);
        Assert.Equal(200, detailStatus);
        Assert.Equal("running", Prop(detail, "status"));
        Assert.Null(detail.GetType().GetProperty("logPath")!.GetValue(detail));
        Assert.Null(detail.GetType().GetProperty("readiness")!.GetValue(detail));
        Assert.Null(detail.GetType().GetProperty("outputs")!.GetValue(detail));

        // Log route: a bounded 404, never a filesystem exception.
        RecipeReadModel.CookLogResult log = RecipeReadModel.GetCookLog(workspace, cookId);
        Assert.Equal(404, log.Status);

        // Startup reconciliation: heals the row rather than throwing. It
        // best-effort CREATES the missing folder and writes interrupted.json, so
        // an orphaned reservation converges to interrupted / broker_exited.
        Assert.Equal(1, RecipeReadModel.ReconcileCooksAtStartup(workspace));

        using (SqliteConnection conn = RecipeCookReservationTests.Open(workspace))
        {
            Assert.Equal(0, RecipeCookReservationTests.CountRunning(conn, RecipeId));
            Assert.Equal(1, RecipeCookReservationTests.CountAny(conn, RecipeId));

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status, error_class FROM cooks WHERE cook_id = $id;";
            cmd.Parameters.AddWithValue("$id", cookId);
            using SqliteDataReader r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("interrupted", r.GetString(0));
            Assert.Equal("broker_exited", r.GetString(1));
        }

        Assert.True(Directory.Exists(cookFolderAbs));
        Assert.True(File.Exists(Path.Combine(cookFolderAbs, "interrupted.json")));
    }
}
