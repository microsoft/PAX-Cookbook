using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 34 Stage A - CHARACTERIZATION of the Resume entry point.
//
// These tests were authored and made green BEFORE the Resume path was routed
// through the shared preparation sequence. Their entire job is to PIN the exact
// existing observable behaviour - status codes, error codes, full response
// bodies, and the absence of side effects - so that any drift introduced by the
// routing change is caught rather than rationalised afterwards.
//
// Nothing here spawns a process, runs PAX, starts a Bake, reads engine bytes,
// registers a service, or reads a secret. Every scenario is deliberately steered
// to a refusal that occurs BEFORE the spawn boundary, so StartResumeCook returns
// without ever reaching SpawnAndSupervise.
public class ResumeCookPreparationCharacterizationTests : IDisposable
{
    private readonly List<string> _workspaces = new();
    private readonly List<string> _checkpoints = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string path in _workspaces)
        {
            TryDelete(path);
        }
        foreach (string path in _checkpoints)
        {
            TryDelete(path);
        }

        GC.SuppressFinalize(this);
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A leftover temp directory is harmless; never fail a test on cleanup.
        }
    }

    private string NewWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "paxcb-c34-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _workspaces.Add(root);
        WorkspaceDatabase.EnsureInitialized(root);
        return root;
    }

    // An existing, fully-qualified, canonical checkpoint FOLDER with no trailing
    // separator - so the canonical path the validator produces is byte-identical
    // to the string handed in, and the concurrency identity is simply its
    // upper-invariant form.
    private string NewCheckpointFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "paxcb-c34-ckpt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _checkpoints.Add(dir);
        return dir;
    }

    internal static VersionInfo Version() => new(
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

    // A plain data record. Constructing it reads no engine bytes, copies nothing,
    // and cannot start anything.
    internal static EngineAcquisitionResult AcquiredEngine(string workspace) => new()
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

    internal static EngineAcquisitionResult UnacquiredEngine(string workspace) => new()
    {
        Policy = "managed",
        State = "not_acquired",
        IsAcquired = false,
        AcquisitionRequired = true,
        ManagedEnginePathPresent = false,
        RecordedSha256 = null,
        Version = null,
        Source = "test",
        Message = "test",
        InstallStatePath = Path.Combine(workspace, "install-state.json"),
        ManagedEnginePath = Path.Combine(workspace, "engine", "PAX.ps1"),
    };

    internal static string Json(object body) => JsonSerializer.Serialize(body);

    internal static int ResumeCookFolderCount(string workspace)
    {
        string bucket = Path.Combine(workspace, "Cooks", "__resume__");
        return Directory.Exists(bucket) ? Directory.GetDirectories(bucket).Length : 0;
    }

    internal static long CountAllCookRows(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM cooks;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // No cook row anywhere carries a child process id or a start timestamp, which
    // is only possible if no supervised child was ever launched.
    internal static long CountStartedCookRows(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM cooks WHERE pid IS NOT NULL OR started_at IS NOT NULL;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private (int Status, object Body) Resume(
        string workspace,
        EngineAcquisitionResult engine,
        string? checkpointPath,
        bool force = false,
        string? chefKeyId = null) =>
        RecipeReadModel.StartResumeCook(
            workspace, Version(), engine, checkpointPath, force, chefKeyId, pwshPathOverride: null);

    // ---- 400 invalid_checkpoint_path -----------------------------------------

    [Theory]
    [InlineData(null, "checkpoint_path_empty",
        "A non-empty checkpointPath is required to resume a cook.")]
    [InlineData("", "checkpoint_path_empty",
        "A non-empty checkpointPath is required to resume a cook.")]
    [InlineData("   ", "checkpoint_path_empty",
        "A non-empty checkpointPath is required to resume a cook.")]
    [InlineData("relative\\out", "checkpoint_path_not_absolute",
        "checkpointPath must be a full path, such as C:\\... or \\\\server\\share\\...")]
    [InlineData("C:relative", "checkpoint_path_not_absolute",
        "checkpointPath must be a full path, such as C:\\... or \\\\server\\share\\...")]
    [InlineData("\\rooted-but-not-qualified", "checkpoint_path_not_absolute",
        "checkpointPath must be a full path, such as C:\\... or \\\\server\\share\\...")]
    [InlineData("C:\\paxcb-c34-does-not-exist\\nowhere", "checkpoint_path_not_found",
        "checkpointPath does not exist on this PC.")]
    public void An_invalid_checkpoint_path_still_returns_the_exact_400_body(
        string? candidate, string expectedReason, string expectedMessage)
    {
        string workspace = NewWorkspace();

        (int status, object body) = Resume(workspace, AcquiredEngine(workspace), candidate);

        Assert.Equal(400, status);
        Assert.Equal(
            Json(new
            {
                error = "invalid_checkpoint_path",
                reason = expectedReason,
                message = expectedMessage,
            }),
            Json(body));

        // No side effect of any kind: no row, no folder, no child.
        using SqliteConnection conn = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(0, CountAllCookRows(conn));
        Assert.Equal(0, ResumeCookFolderCount(workspace));
        Assert.Equal(0, CountStartedCookRows(conn));
    }

    [Fact]
    public void An_existing_non_json_file_still_returns_the_exact_wrong_kind_400_body()
    {
        string workspace = NewWorkspace();
        string dir = NewCheckpointFolder();
        string file = Path.Combine(dir, "checkpoint.txt");
        File.WriteAllText(file, "not a checkpoint");

        (int status, object body) = Resume(workspace, AcquiredEngine(workspace), file);

        Assert.Equal(400, status);
        Assert.Equal(
            Json(new
            {
                error = "invalid_checkpoint_path",
                reason = "checkpoint_path_wrong_kind",
                message = "checkpointPath names a file that is not a .json checkpoint. " +
                    "Point at the run's output folder or its checkpoint .json.",
            }),
            Json(body));

        // POSITIVE CONTROL: the SAME workspace and the SAME entry point produce a
        // DIFFERENT refusal for a valid checkpoint whose engine is unacquired, so
        // the body comparison above is not a comparison of two constants.
        (int otherStatus, object otherBody) = Resume(workspace, UnacquiredEngine(workspace), dir);
        Assert.NotEqual(400, otherStatus);
        Assert.NotEqual(Json(body), Json(otherBody));
    }

    // ---- 409 acquisitionRequired ---------------------------------------------

    [Fact]
    public void An_unacquired_engine_still_returns_the_exact_409_acquisition_gate_body()
    {
        string workspace = NewWorkspace();
        string checkpoint = NewCheckpointFolder();
        EngineAcquisitionResult engine = UnacquiredEngine(workspace);

        (int status, object body) = Resume(workspace, engine, checkpoint);

        Assert.Equal(409, status);

        // The resume route must emit the SAME gate-6 body the cook path emits, for
        // the resume method/path pair - that is what the SPA acquisition overlay
        // renders on.
        Assert.Equal(Json(engine.ToGate409Body("POST", "/api/v1/resume-cook")), Json(body));

        using SqliteConnection conn = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(0, CountAllCookRows(conn));
        Assert.Equal(0, ResumeCookFolderCount(workspace));
    }

    // ---- 400 workspace_path_too_long -----------------------------------------

    [Fact]
    public void A_workspace_over_the_path_budget_still_returns_the_exact_400_body()
    {
        // The budget refuses when workspaceLength + 96 > 260, i.e. anything over
        // 164 characters. A deliberately long (but still creatable) workspace root
        // drives that gate without touching the disk floor.
        string root = Path.Combine(Path.GetTempPath(), "paxcb-c34-" + Guid.NewGuid().ToString("N"));
        while (root.Length <= 164)
        {
            root = Path.Combine(root, "seg" + Guid.NewGuid().ToString("N"));
        }
        Directory.CreateDirectory(root);
        _workspaces.Add(root);
        WorkspaceDatabase.EnsureInitialized(root);

        string checkpoint = NewCheckpointFolder();
        int len = root.Length;

        (int status, object body) = Resume(root, AcquiredEngine(root), checkpoint);

        Assert.Equal(400, status);
        Assert.Equal(
            Json(new
            {
                error = "workspace_path_too_long",
                workspacePathLength = len,
                reservedChildBudget = 96,
                classicLimit = 260,
                reason = "pre_spawn_path_length_exceeds_max_path",
                detail = "Workspace path length (" + len +
                    ") plus the reserved per-cook child budget (96) exceeds the classic MAX_PATH limit (260).",
            }),
            Json(body));

        using SqliteConnection conn = ResumeCookReservationTests.Open(root);
        Assert.Equal(0, CountAllCookRows(conn));
        Assert.Equal(0, ResumeCookFolderCount(root));
    }

    // ---- 412 chefKeyNotFound --------------------------------------------------

    [Fact]
    public void An_unresolvable_chef_key_still_returns_the_exact_412_body()
    {
        // A malformed chefKeyId is rejected by the id validator itself, so this
        // scenario never reaches the Windows Credential Manager at all - no vault
        // read, no secret, no dependence on what keys this machine happens to hold.
        string workspace = NewWorkspace();
        string checkpoint = NewCheckpointFolder();
        const string badKeyId = "not a valid chef key id";

        (int status, object body) = Resume(
            workspace, AcquiredEngine(workspace), checkpoint, force: false, chefKeyId: badKeyId);

        Assert.Equal(412, status);
        Assert.Equal(
            Json(new
            {
                error = "chefKeyNotFound",
                chefKeyId = badKeyId,
                message = "Chef's Key '" + badKeyId + "' does not exist.",
            }),
            Json(body));

        using SqliteConnection conn = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(0, CountAllCookRows(conn));
        Assert.Equal(0, ResumeCookFolderCount(workspace));
        Assert.Equal(0, CountStartedCookRows(conn));
    }

    // ---- 409 resume_already_running (the highest-risk mapping) ----------------

    [Fact]
    public void A_duplicate_running_checkpoint_still_returns_the_exact_full_409_body()
    {
        string workspace = NewWorkspace();
        string checkpoint = NewCheckpointFolder();

        // The concurrency identity is the upper-invariant canonical path. It is
        // computed here INDEPENDENTLY of the production validator so the seeded
        // winner is not merely whatever production happens to produce.
        string identity = checkpoint.ToUpperInvariant();
        const string winner = "cook-winner-c34";
        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, winner, identity));

        (int status, object body) = Resume(workspace, AcquiredEngine(workspace), checkpoint);

        // FULL BODY. Every field, in order, with the exact literal message: the
        // error code, the winning Cook ID, and the NESTED checkpoint object whose
        // `path` carries the canonical checkpoint path.
        Assert.Equal(409, status);
        Assert.Equal(
            Json(new
            {
                error = "resume_already_running",
                cookId = winner,
                checkpoint = new { path = checkpoint },
                message = "That checkpoint is already being resumed by a run that is still going.",
            }),
            Json(body));

        // Field-level restatement so a failure names the offending field rather
        // than dumping two JSON blobs.
        using (JsonDocument doc = JsonDocument.Parse(Json(body)))
        {
            JsonElement r = doc.RootElement;
            Assert.Equal("resume_already_running", r.GetProperty("error").GetString());
            Assert.Equal(winner, r.GetProperty("cookId").GetString());
            Assert.Equal(JsonValueKind.Object, r.GetProperty("checkpoint").ValueKind);
            Assert.Equal(checkpoint, r.GetProperty("checkpoint").GetProperty("path").GetString());
            Assert.Equal(
                "That checkpoint is already being resumed by a run that is still going.",
                r.GetProperty("message").GetString());
            Assert.Equal(4, r.EnumerateObject().Count());
        }

        // NO additional side effect of ANY kind for the loser.
        using SqliteConnection conn = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(1, ResumeCookReservationTests.CountRunning(conn, identity));
        Assert.Equal(1, ResumeCookReservationTests.CountAny(conn, identity));
        Assert.Equal(1, CountAllCookRows(conn));          // no second row of any status
        Assert.Equal(0, ResumeCookFolderCount(workspace)); // no folder, so no files either
        Assert.Equal(0, CountStartedCookRows(conn));       // no pid, no started_at -> no process
    }

    [Fact]
    public void The_duplicate_conflict_side_effect_counters_are_all_live()
    {
        // POSITIVE CONTROLS for the four "no side effect" counters above. Each one
        // is shown moving off the value the conflict test asserts, so none of them
        // is a counter that can only ever read zero (or one).
        string workspace = NewWorkspace();
        string checkpoint = NewCheckpointFolder();
        string identity = checkpoint.ToUpperInvariant();

        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, "cook-a", identity));
        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(
            workspace, "cook-b", identity + "-OTHER"));

        using (SqliteConnection conn = ResumeCookReservationTests.Open(workspace))
        {
            // The row counters really do count past one.
            Assert.Equal(2, CountAllCookRows(conn));
            Assert.Equal(1, ResumeCookReservationTests.CountAny(conn, identity));

            // The started-row counter really does fire on a started row.
            using SqliteCommand upd = conn.CreateCommand();
            upd.CommandText =
                "UPDATE cooks SET pid = 4242, started_at = '2026-01-01T00:00:00.000Z' " +
                "WHERE cook_id = 'cook-a';";
            Assert.Equal(1, upd.ExecuteNonQuery());
            Assert.Equal(1, CountStartedCookRows(conn));
        }

        // The resume cook-folder counter really does count a folder. Created
        // directly here: producing one through the product would require a
        // successful preparation, which continues to the spawn boundary, and these
        // tests never spawn.
        Assert.Equal(0, ResumeCookFolderCount(workspace));
        Directory.CreateDirectory(Path.Combine(workspace, "Cooks", "__resume__", "synthetic"));
        Assert.Equal(1, ResumeCookFolderCount(workspace));
    }

    [Fact]
    public void A_prior_terminal_resume_of_the_same_checkpoint_is_not_refused_as_a_duplicate()
    {
        // Confirms the 409 above is attributable to a RUNNING holder, not merely to
        // the presence of a row carrying the same identity.
        string workspace = NewWorkspace();
        string checkpoint = NewCheckpointFolder();
        string identity = checkpoint.ToUpperInvariant();

        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, "cook-old", identity));
        using (SqliteConnection conn = ResumeCookReservationTests.Open(workspace))
        using (SqliteCommand upd = conn.CreateCommand())
        {
            upd.CommandText = "UPDATE cooks SET status = 'completed' WHERE cook_id = 'cook-old';";
            Assert.Equal(1, upd.ExecuteNonQuery());
        }

        // The identity is free, so the duplicate gate does not fire. The run is
        // stopped instead by a LATER, unrelated refusal, which is asserted rather
        // than allowed to continue to the spawn boundary.
        (int status, object body) = Resume(
            workspace, AcquiredEngine(workspace), checkpoint,
            force: false, chefKeyId: "not a valid chef key id");

        Assert.Equal(412, status);
        Assert.Equal("chefKeyNotFound", JsonDocument.Parse(Json(body)).RootElement
            .GetProperty("error").GetString());

        using SqliteConnection after = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(0, ResumeCookReservationTests.CountRunning(after, identity));
        Assert.Equal(1, CountAllCookRows(after));
    }
}
