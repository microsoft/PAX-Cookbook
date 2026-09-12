using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 33 - DESKTOP-SIDE coverage for routing the three Recipe cook entry points
// through the portable CookPreparationSequence. Cycle 34 adds the RESUME
// entry point and the discriminating per-trigger authorization matrix. Cycle 35
// retires the obsolete no-child preparation seam.
//
// SCOPE, stated plainly:
//   * The sequence owns the ORDER OF THREE PHASES. It does NOT own gates 1..18 and
//     it is NOT canonical: Resume now runs the same phase ORDER, but Resume has no
//     recipe, so its GATES are different gates and are not converged.
//   * Cycle 35 RETIRED the obsolete PrepareCookStart no-child seam, so every
//     SUPPORTED PRODUCTION cook execution entry point now traverses the sequence
//     and no unsequenced caller remains. Routing only: the sequence is still NOT
//     canonical, the per-trigger gates are still not converged, and the Windows
//     service is still NOT cook-ready.
//   * Pure phase-order behaviour is proven in PAXCookbook.Shared.Tests. This file
//     covers only the CookKind -> trigger mapping, the per-host authorization
//     routing, the narrow bounded seams, and the preserved production
//     statuses/bodies.
//   * Nothing here spawns a process, runs PAX, starts a Bake, reads engine bytes,
//     or reads a secret. The continuity tests perform a real PREPARATION (cook
//     row + folder + pre-spawn files) and stop there; the prepared state is never
//     handed to the spawn boundary.
public class CookPreparationSequenceRoutingTests : IDisposable
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
    private const string UnknownRecipeId = RecipeCookReservationTests.RecipeB;

    private string NewWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "paxcb-c33-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _workspaces.Add(root);
        WorkspaceDatabase.EnsureInitialized(root);
        return root;
    }

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

    // A plain data record. Constructing it reads no engine bytes, copies nothing,
    // and cannot start anything; it exists so gate 6 passes.
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

    private const string ScheduleBlock =
        """
        "schedule": { "enabled": true, "recurrence": { "kind": "daily", "hour": 3, "minute": 0 } },
        """;

    private static void SeedRecipeOnDisk(string workspace, bool withEnabledSchedule)
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
              "identity": { "name": "cycle 33 sequence routing" },
            """
            + (withEnabledSchedule ? "\n  " + ScheduleBlock : string.Empty)
            + """

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

    private static string Json(object body) => JsonSerializer.Serialize(body);

    // The gate-11 body reports the volume's CURRENT free bytes, which drifts
    // between two consecutive calls on a live machine. Long digit runs are masked
    // so the comparison is about the two wrappers, not about the disk. Nothing
    // else in the body is altered.
    private static string JsonStableDigits(object body) =>
        System.Text.RegularExpressions.Regex.Replace(Json(body), @"\d{5,}", "<n>");

    // ---- CookKind -> shared trigger mapping (bounded seam) -------------------

    [Fact]
    public void The_cook_kinds_are_exactly_manual_and_scheduled()
    {
        Assert.Equal(
            new[] { "Manual", "Scheduled" },
            RecipeReadModel.TestSeamCookKindNames());
    }

    [Fact]
    public void Every_declared_cook_kind_maps_to_its_matching_shared_trigger()
    {
        string[] names = RecipeReadModel.TestSeamCookKindNames();
        CookTriggerKind[] mapped = RecipeReadModel.TestSeamCookKindTriggerProjection();

        // EXHAUSTIVE: every declared kind is projected, not just the ones a test
        // happened to name.
        Assert.Equal(names.Length, mapped.Length);

        // EXACT: in declaration order, Manual -> Manual and Scheduled -> Scheduled.
        Assert.Equal(new[] { CookTriggerKind.Manual, CookTriggerKind.Scheduled }, mapped);
        Assert.All(mapped, t => Assert.True(Enum.IsDefined(t)));
    }

    [Fact]
    public void An_undeclared_cook_kind_maps_to_a_trigger_the_sequence_refuses()
    {
        CookTriggerKind failClosed = RecipeReadModel.TestSeamMapUndeclaredCookKind();

        Assert.False(Enum.IsDefined(failClosed));
        Assert.NotEqual(CookTriggerKind.Manual, failClosed);

        // POSITIVE CONTROL: Enum.IsDefined DOES return true for the real mappings,
        // so "not defined" above is a real finding rather than a check that can
        // only ever say false.
        Assert.All(
            RecipeReadModel.TestSeamCookKindTriggerProjection(),
            t => Assert.True(Enum.IsDefined(t)));
    }

    // ---- bounded disposition seam over the REAL desktop adapter ---------------

    private (CookPreparationDisposition Disposition, CookPreparationPhase Phase,
             bool HasRefusal, int Status, bool HasPrepared) RunDisposition(
        string workspace, string recipeId, bool scheduled, long minFreeDiskBytes) =>
        RecipeReadModel.TestSeamCookPreparationDisposition(
            workspace, Version(), AcquiredEngine(workspace), recipeId,
            method: scheduled ? "SCHEDULED" : "POST",
            requestPath: "/api/v1/recipes/" + recipeId + "/cook",
            minFreeDiskBytes, scheduled);

    [Fact]
    public void A_gate_refusal_reports_the_gates_phase_and_produces_no_prepared_state()
    {
        string workspace = NewWorkspace();

        var r = RunDisposition(workspace, UnknownRecipeId, scheduled: false, minFreeDiskBytes: 0);

        Assert.Equal(CookPreparationDisposition.RefusedAtGates, r.Disposition);
        Assert.Equal(CookPreparationPhase.Gates, r.Phase);
        Assert.True(r.HasRefusal);
        Assert.Equal(404, r.Status);
        Assert.False(r.HasPrepared);
    }

    [Fact]
    public void A_scheduled_authorization_refusal_reports_the_authorization_phase()
    {
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: false);

        var r = RunDisposition(workspace, RecipeId, scheduled: true, minFreeDiskBytes: 0);

        Assert.Equal(CookPreparationDisposition.RefusedAtAuthorization, r.Disposition);
        Assert.Equal(CookPreparationPhase.Authorization, r.Phase);
        Assert.Equal(409, r.Status);
        Assert.False(r.HasPrepared);

        // POSITIVE CONTROL: the SAME workspace and recipe reach a LATER phase once
        // the schedule is enabled, so "refused at authorization" is attributable to
        // the missing schedule rather than to something the seam always returns.
        SeedRecipeOnDisk(workspace, withEnabledSchedule: true);
        var later = RunDisposition(workspace, RecipeId, scheduled: true, minFreeDiskBytes: long.MaxValue);
        Assert.Equal(CookPreparationDisposition.RefusedAtPreparation, later.Disposition);
    }

    [Fact]
    public void A_preparation_refusal_reports_the_preparation_phase()
    {
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: false);

        var r = RunDisposition(workspace, RecipeId, scheduled: false, minFreeDiskBytes: long.MaxValue);

        Assert.Equal(CookPreparationDisposition.RefusedAtPreparation, r.Disposition);
        Assert.Equal(CookPreparationPhase.Preparation, r.Phase);
        Assert.Equal(507, r.Status);
        Assert.False(r.HasPrepared);
        Assert.Equal(0, CookFolderCount(workspace, RecipeId));
    }

    // ---- prepared-state continuity into the UNCHANGED spawn boundary ---------

    [Fact]
    public void Successful_preparation_hands_the_phase_three_output_to_the_spawn_boundary()
    {
        // Runs a REAL preparation and stops. Nothing is spawned: the prepared state
        // is never given to SpawnAndSupervise, no pwsh is resolved, and no engine
        // byte is read.
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: false);

        var c = RecipeReadModel.TestSeamPreparedStateContinuity(
            workspace, Version(), AcquiredEngine(workspace), RecipeId, minFreeDiskBytes: 0);

        Assert.True(c.Prepared);
        Assert.True(c.ContinuationMatchesPhaseThreeOutput);
        Assert.True(c.ContinuationCookRowIsRunning);
        Assert.True(c.ContinuationCookFolderExists);

        // The cycle-32 ordering is intact: exactly one running row and exactly one
        // cook folder for this recipe.
        using SqliteConnection conn = RecipeCookReservationTests.Open(workspace);
        Assert.Equal(1, RecipeCookReservationTests.CountRunning(conn, RecipeId));
        Assert.Equal(1, CookFolderCount(workspace, RecipeId));
    }

    [Fact]
    public void A_refused_preparation_carries_no_prepared_state_to_the_spawn_boundary()
    {
        // POSITIVE CONTROL for the four "true" assertions above: the same seam
        // reports all-false when preparation refuses, so those trues are earned.
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: false);

        var c = RecipeReadModel.TestSeamPreparedStateContinuity(
            workspace, Version(), AcquiredEngine(workspace), RecipeId, minFreeDiskBytes: long.MaxValue);

        Assert.False(c.Prepared);
        Assert.False(c.ContinuationMatchesPhaseThreeOutput);
        Assert.False(c.ContinuationCookRowIsRunning);
        Assert.False(c.ContinuationCookFolderExists);
    }

    [Fact]
    public void The_cycle_32_reservation_predicate_still_refuses_a_second_cook_of_the_same_recipe()
    {
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: false);

        var c = RecipeReadModel.TestSeamPreparedStateContinuity(
            workspace, Version(), AcquiredEngine(workspace), RecipeId, minFreeDiskBytes: 0);
        Assert.True(c.Prepared);

        // The reservation left a running row; the production entry point refuses.
        (int status, object body) = RecipeReadModel.StartManualCook(
            workspace, Version(), AcquiredEngine(workspace), RecipeId,
            method: "POST", requestPath: "/api/v1/recipes/" + RecipeId + "/cook",
            minFreeDiskBytes: 0, pwshPathOverride: null);

        Assert.Equal(409, status);
        Assert.Equal("recipe_busy", Prop(body, "error"));

        // Row-before-folder ordering intact: the loser added neither.
        using SqliteConnection conn = RecipeCookReservationTests.Open(workspace);
        Assert.Equal(1, RecipeCookReservationTests.CountRunning(conn, RecipeId));
        Assert.Equal(1, CookFolderCount(workspace, RecipeId));
    }

    // ---- preserved production statuses and bodies ----------------------------

    [Fact]
    public void Manual_cook_still_returns_400_invalid_recipe_id()
    {
        string workspace = NewWorkspace();

        (int status, object body) = RecipeReadModel.StartManualCook(
            workspace, Version(), AcquiredEngine(workspace), "not-a-ulid",
            method: "POST", requestPath: "/api/v1/recipes/not-a-ulid/cook",
            minFreeDiskBytes: 0, pwshPathOverride: null);

        Assert.Equal(400, status);
        Assert.Equal("invalid_recipe_id", Prop(body, "error"));
    }

    [Fact]
    public void Manual_cook_still_returns_404_for_an_unknown_recipe()
    {
        string workspace = NewWorkspace();

        (int status, object body) = RecipeReadModel.StartManualCook(
            workspace, Version(), AcquiredEngine(workspace), UnknownRecipeId,
            method: "POST", requestPath: "/api/v1/recipes/" + UnknownRecipeId + "/cook",
            minFreeDiskBytes: 0, pwshPathOverride: null);

        Assert.Equal(404, status);
        Assert.Equal("not_found", Prop(body, "error"));
    }

    [Fact]
    public void Scheduled_cook_without_an_enabled_schedule_still_returns_409_recipe_not_scheduled()
    {
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: false);

        (int status, object body) = RecipeReadModel.StartScheduledCook(
            workspace, Version(), AcquiredEngine(workspace), RecipeId,
            minFreeDiskBytes: 0, pwshPathOverride: null);

        Assert.Equal(409, status);
        Assert.Equal("recipe_not_scheduled", Prop(body, "error"));
        Assert.Equal(RecipeId, Prop(body, "recipeId"));
        Assert.Equal(
            "This recipe has no enabled schedule; a scheduled run is not authorized.",
            Prop(body, "message"));
        Assert.Equal(0, CookFolderCount(workspace, RecipeId));
    }

    [Fact]
    public void A_skipped_scheduled_run_still_returns_200_bake_skipped()
    {
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: true);
        ScheduleSkipMarker.Write(workspace, RecipeId, DateTimeOffset.Now);

        (int status, object body) = RecipeReadModel.StartScheduledCook(
            workspace, Version(), AcquiredEngine(workspace), RecipeId,
            minFreeDiskBytes: 0, pwshPathOverride: null);

        Assert.Equal(200, status);
        Assert.Equal("bake_skipped", Prop(body, "error"));
        Assert.Equal("skipped", Prop(body, "status"));
        Assert.Equal(
            "This scheduled run was skipped at the operator's request; the schedule continues.",
            Prop(body, "message"));
        Assert.Equal(0, CookFolderCount(workspace, RecipeId));
    }

    [Fact]
    public void The_scheduled_skip_marker_is_consumed_exactly_once()
    {
        // TWO-SIDED. The scheduled authorization phase WRITES: it consumes the
        // marker. If the sequence invoked it twice, the operator's single skipped
        // Bake would silently swallow two occurrences.
        //
        // An impossible disk floor is used for the second run so it refuses at gate
        // 11 - after authorization, before any reservation, folder, or child.
        // Nothing is spawned on either side.
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: true);
        ScheduleSkipMarker.Write(workspace, RecipeId, DateTimeOffset.Now);

        (int firstStatus, object firstBody) = RecipeReadModel.StartScheduledCookViaHttp(
            workspace, Version(), AcquiredEngine(workspace), RecipeId,
            method: "POST", requestPath: "/api/v1/recipes/" + RecipeId + "/cook/scheduled",
            minFreeDiskBytes: long.MaxValue, pwshPathOverride: null);

        // Side one: the marked occurrence IS skipped.
        Assert.Equal(200, firstStatus);
        Assert.Equal("bake_skipped", Prop(firstBody, "error"));

        (int secondStatus, object secondBody) = RecipeReadModel.StartScheduledCookViaHttp(
            workspace, Version(), AcquiredEngine(workspace), RecipeId,
            method: "POST", requestPath: "/api/v1/recipes/" + RecipeId + "/cook/scheduled",
            minFreeDiskBytes: long.MaxValue, pwshPathOverride: null);

        // Side two: the FOLLOWING occurrence is NOT skipped - it passes
        // authorization and is stopped later, by the disk floor.
        Assert.Equal(507, secondStatus);
        Assert.Equal("insufficient_disk_space", Prop(secondBody, "error"));
        Assert.Equal(0, CookFolderCount(workspace, RecipeId));
    }

    [Fact]
    public void Both_scheduled_wrappers_still_differ_only_by_join_supervisor_after_preparation()
    {
        // Both wrappers are driven to the SAME post-authorization refusal (the disk
        // floor, gate 11). Everything before the spawn boundary is identical, so the
        // status and the body must match byte for byte; the only remaining
        // difference between the two entry points is joinSupervisor, which is read
        // only AFTER SpawnAndSupervise returns.
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: true);

        (int oneShotStatus, object oneShotBody) = RecipeReadModel.StartScheduledCook(
            workspace, Version(), AcquiredEngine(workspace), RecipeId,
            minFreeDiskBytes: long.MaxValue, pwshPathOverride: null);

        (int httpStatus, object httpBody) = RecipeReadModel.StartScheduledCookViaHttp(
            workspace, Version(), AcquiredEngine(workspace), RecipeId,
            method: "POST", requestPath: "/api/v1/recipes/" + RecipeId + "/cook/scheduled",
            minFreeDiskBytes: long.MaxValue, pwshPathOverride: null);

        Assert.Equal(oneShotStatus, httpStatus);
        Assert.Equal(507, httpStatus);
        Assert.Equal(JsonStableDigits(oneShotBody), JsonStableDigits(httpBody));

        // POSITIVE CONTROL: the comparison CAN fail - a different refusal produces a
        // different body, so equality above is not a comparison of two constants.
        (_, object differentBody) = RecipeReadModel.StartScheduledCook(
            workspace, Version(), AcquiredEngine(workspace), UnknownRecipeId,
            minFreeDiskBytes: long.MaxValue, pwshPathOverride: null);
        Assert.NotEqual(JsonStableDigits(httpBody), JsonStableDigits(differentBody));
    }

    // ---- discriminating authorization-phase selection, per host adapter ------
    //
    // The sequence, not the adapter, decides WHICH authorization phase applies.
    // These tests drive each host adapter with each trigger - including triggers
    // that adapter can never serve - and assert the phase that was actually
    // invoked, the per-phase invocation counts, and the resulting disposition.

    private (string AuthorizationPhase, int GateCalls, int AuthorizationCalls, int PreparationCalls,
             CookPreparationDisposition Disposition, CookPreparationPhase FurthestPhase,
             bool HasRefusal, int Status, bool HasPrepared) RunDesktopRouting(
        string workspace, CookTriggerKind trigger, bool scheduled, long minFreeDiskBytes) =>
        RecipeReadModel.TestSeamDesktopAuthorizationRouting(
            workspace, Version(), AcquiredEngine(workspace), RecipeId,
            minFreeDiskBytes, scheduled, trigger);

    [Fact]
    public void A_manual_trigger_invokes_only_the_manual_authorization_phase()
    {
        // The seeded recipe has NO enabled schedule. If the scheduled authorization
        // phase had been invoked it would have refused 409 at AUTHORIZATION, so
        // reaching PREPARATION is positive proof that it was not.
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: false);

        var r = RunDesktopRouting(workspace, CookTriggerKind.Manual, scheduled: false, long.MaxValue);

        Assert.Equal("Manual", r.AuthorizationPhase);
        Assert.Equal(1, r.GateCalls);
        Assert.Equal(1, r.AuthorizationCalls);
        Assert.Equal(1, r.PreparationCalls);
        Assert.Equal(CookPreparationDisposition.RefusedAtPreparation, r.Disposition);
        Assert.Equal(507, r.Status);
        Assert.False(r.HasPrepared);
        Assert.Equal(0, CookFolderCount(workspace, RecipeId));
    }

    [Fact]
    public void A_scheduled_trigger_invokes_only_the_scheduled_authorization_phase()
    {
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: false);

        var r = RunDesktopRouting(workspace, CookTriggerKind.Scheduled, scheduled: true, long.MaxValue);

        Assert.Equal("Scheduled", r.AuthorizationPhase);
        Assert.Equal(1, r.AuthorizationCalls);
        Assert.Equal(CookPreparationDisposition.RefusedAtAuthorization, r.Disposition);
        Assert.Equal(409, r.Status);

        // The refusal SUPPRESSED preparation entirely.
        Assert.Equal(0, r.PreparationCalls);
        Assert.False(r.HasPrepared);
        Assert.Equal(0, CookFolderCount(workspace, RecipeId));
    }

    [Fact]
    public void A_resume_trigger_on_the_recipe_adapter_fails_closed_at_authorization()
    {
        // The desktop RECIPE adapter can never serve a Resume. Its resume
        // authorization phase must return FALSE and record a bounded refusal -
        // never true.
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: true);

        var r = RunDesktopRouting(workspace, CookTriggerKind.Resume, scheduled: false, minFreeDiskBytes: 0);

        Assert.Equal("Resume", r.AuthorizationPhase);
        Assert.Equal(1, r.GateCalls);
        Assert.Equal(1, r.AuthorizationCalls);
        Assert.Equal(CookPreparationDisposition.RefusedAtAuthorization, r.Disposition);
        Assert.Equal(CookPreparationPhase.Authorization, r.FurthestPhase);
        Assert.True(r.HasRefusal);
        Assert.Equal(500, r.Status);

        // Every later phase was suppressed and nothing was created.
        Assert.Equal(0, r.PreparationCalls);
        Assert.False(r.HasPrepared);
        Assert.Equal(0, CookFolderCount(workspace, RecipeId));

        // POSITIVE CONTROL: the SAME workspace, recipe, and disk floor DO reach
        // preparation under the manual trigger, so the refusal above is caused by
        // the trigger and not by the fixture.
        var control = RunDesktopRouting(workspace, CookTriggerKind.Manual, scheduled: false, long.MaxValue);
        Assert.Equal(CookPreparationDisposition.RefusedAtPreparation, control.Disposition);
        Assert.Equal(1, control.PreparationCalls);
    }

    [Fact]
    public void An_undeclared_trigger_invokes_no_authorization_phase_on_the_recipe_adapter()
    {
        string workspace = NewWorkspace();
        SeedRecipeOnDisk(workspace, withEnabledSchedule: true);

        var r = RunDesktopRouting(
            workspace, (CookTriggerKind)int.MaxValue, scheduled: false, minFreeDiskBytes: 0);

        Assert.Equal("None", r.AuthorizationPhase);
        Assert.Equal(0, r.GateCalls);
        Assert.Equal(0, r.AuthorizationCalls);
        Assert.Equal(0, r.PreparationCalls);
        Assert.Equal(CookPreparationDisposition.RefusedUnsupportedTrigger, r.Disposition);
        Assert.Equal(CookPreparationPhase.None, r.FurthestPhase);
        Assert.False(r.HasRefusal);
        Assert.False(r.HasPrepared);
    }

    // ---- the RESUME adapter ---------------------------------------------------

    private string NewCheckpointFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "paxcb-c34-ckpt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _workspaces.Add(dir);
        return dir;
    }

    // A workspace deliberately over the pre-spawn MAX_PATH budget, so a resume
    // refuses INSIDE phase 3 at the path gate - after authorization, before the
    // reservation. That yields phase-count evidence with zero side effects.
    private string NewOverBudgetWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "paxcb-c34-" + Guid.NewGuid().ToString("N"));
        while (root.Length <= 164)
        {
            root = Path.Combine(root, "seg" + Guid.NewGuid().ToString("N"));
        }
        Directory.CreateDirectory(root);
        _workspaces.Add(root);
        WorkspaceDatabase.EnsureInitialized(root);
        return root;
    }

    private (string AuthorizationPhase, int GateCalls, int AuthorizationCalls, int PreparationCalls,
             CookPreparationDisposition Disposition, CookPreparationPhase FurthestPhase,
             bool HasRefusal, int Status, bool HasPrepared) RunResumeRouting(
        string workspace, string? checkpoint, CookTriggerKind trigger) =>
        RecipeReadModel.TestSeamResumeAuthorizationRouting(
            workspace, Version(), AcquiredEngine(workspace), checkpoint,
            force: false, chefKeyId: null, trigger);

    [Fact]
    public void A_resume_runs_gates_then_resume_authorization_then_preparation()
    {
        string workspace = NewOverBudgetWorkspace();
        string checkpoint = NewCheckpointFolder();

        var r = RunResumeRouting(workspace, checkpoint, CookTriggerKind.Resume);

        // Phase order: gates -> RESUME authorization -> preparation, each exactly
        // once. The shared sequence is used ONCE, not once per phase.
        Assert.Equal("Resume", r.AuthorizationPhase);
        Assert.Equal(1, r.GateCalls);
        Assert.Equal(1, r.AuthorizationCalls);
        Assert.Equal(1, r.PreparationCalls);

        // It was stopped by the path budget INSIDE preparation, before the
        // reservation, so nothing was created.
        Assert.Equal(CookPreparationDisposition.RefusedAtPreparation, r.Disposition);
        Assert.Equal(CookPreparationPhase.Preparation, r.FurthestPhase);
        Assert.Equal(400, r.Status);
        Assert.False(r.HasPrepared);
        Assert.Equal(0, ResumeCookPreparationCharacterizationTests.ResumeCookFolderCount(workspace));
    }

    [Fact]
    public void A_resume_gate_refusal_suppresses_authorization_and_preparation()
    {
        string workspace = NewWorkspace();

        var r = RunResumeRouting(workspace, "not-a-full-path", CookTriggerKind.Resume);

        Assert.Equal(CookPreparationDisposition.RefusedAtGates, r.Disposition);
        Assert.Equal(1, r.GateCalls);
        Assert.Equal(0, r.AuthorizationCalls);
        Assert.Equal(0, r.PreparationCalls);
        Assert.Equal("None", r.AuthorizationPhase);
        Assert.Equal(400, r.Status);
    }

    [Theory]
    [InlineData(CookTriggerKind.Manual, "Manual")]
    [InlineData(CookTriggerKind.Scheduled, "Scheduled")]
    public void A_recipe_trigger_on_the_resume_adapter_fails_closed_at_authorization(
        CookTriggerKind trigger, string expectedPhase)
    {
        // The resume adapter can never serve a manual or scheduled recipe cook.
        // Both of those authorization phases must return FALSE and record a
        // bounded refusal - never true.
        string workspace = NewWorkspace();
        string checkpoint = NewCheckpointFolder();

        var r = RunResumeRouting(workspace, checkpoint, trigger);

        Assert.Equal(expectedPhase, r.AuthorizationPhase);
        Assert.Equal(1, r.GateCalls);
        Assert.Equal(1, r.AuthorizationCalls);
        Assert.Equal(CookPreparationDisposition.RefusedAtAuthorization, r.Disposition);
        Assert.Equal(CookPreparationPhase.Authorization, r.FurthestPhase);
        Assert.True(r.HasRefusal);
        Assert.Equal(500, r.Status);

        // Every later phase suppressed; no row, no folder, no file, no process.
        Assert.Equal(0, r.PreparationCalls);
        Assert.False(r.HasPrepared);
        Assert.Equal(0, ResumeCookPreparationCharacterizationTests.ResumeCookFolderCount(workspace));
        using SqliteConnection conn = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(0, ResumeCookPreparationCharacterizationTests.CountAllCookRows(conn));

        // POSITIVE CONTROL: the SAME workspace and checkpoint DO reach preparation
        // under the Resume trigger, so the refusal above is caused by the trigger.
        var control = RunResumeRouting(workspace, checkpoint, CookTriggerKind.Resume);
        Assert.Equal(1, control.PreparationCalls);
        Assert.True(control.HasPrepared);
    }

    [Fact]
    public void An_undeclared_trigger_invokes_no_authorization_phase_on_the_resume_adapter()
    {
        string workspace = NewWorkspace();
        string checkpoint = NewCheckpointFolder();

        var r = RunResumeRouting(workspace, checkpoint, (CookTriggerKind)int.MaxValue);

        Assert.Equal("None", r.AuthorizationPhase);
        Assert.Equal(0, r.GateCalls);
        Assert.Equal(0, r.AuthorizationCalls);
        Assert.Equal(0, r.PreparationCalls);
        Assert.Equal(CookPreparationDisposition.RefusedUnsupportedTrigger, r.Disposition);
        Assert.False(r.HasRefusal);
        Assert.False(r.HasPrepared);
    }

    [Fact]
    public void A_successful_resume_preparation_reaches_the_unchanged_spawn_boundary()
    {
        // Runs a REAL resume preparation and stops at the spawn boundary. Nothing
        // is spawned: the prepared state is never given to SpawnAndSupervise, no
        // pwsh is resolved, and no engine byte is read.
        string workspace = NewWorkspace();
        string checkpoint = NewCheckpointFolder();

        var r = RunResumeRouting(workspace, checkpoint, CookTriggerKind.Resume);

        Assert.Equal(CookPreparationDisposition.Prepared, r.Disposition);
        Assert.Equal(CookPreparationPhase.Preparation, r.FurthestPhase);
        Assert.True(r.HasPrepared);
        Assert.False(r.HasRefusal);
        Assert.Equal(1, r.GateCalls);
        Assert.Equal(1, r.AuthorizationCalls);
        Assert.Equal(1, r.PreparationCalls);

        // The cycle-31/32 ordering is intact: exactly ONE running row for the
        // checkpoint identity and exactly ONE resume cook folder, carrying the
        // pre-spawn files.
        using SqliteConnection conn = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(1, ResumeCookReservationTests.CountRunning(conn, checkpoint.ToUpperInvariant()));
        Assert.Equal(1, ResumeCookPreparationCharacterizationTests.CountAllCookRows(conn));
        Assert.Equal(1, ResumeCookPreparationCharacterizationTests.ResumeCookFolderCount(workspace));

        string bucket = Path.Combine(workspace, "Cooks", "__resume__");
        string folder = Directory.GetDirectories(bucket).Single();
        foreach (string name in new[]
                 { "recipe-snapshot.json", "cook-context.json", "command.txt", "command-argv.json" })
        {
            Assert.True(File.Exists(Path.Combine(folder, name)), name);
        }

        // No child was started: the row still has no pid and no started_at.
        Assert.Equal(0, ResumeCookPreparationCharacterizationTests.CountStartedCookRows(conn));
    }

    // ---- Resume now routes THROUGH the sequence -------------------------------

    [Fact]
    public void Resume_routes_through_the_preparation_sequence()
    {
        // Structural, comment-stripped, token-precise. Cycle 33 asserted the
        // opposite; cycle 34 brings Resume inside the shared phase order, so the
        // scan is inverted.
        string resume = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "PAXCookbook.App", "RecipeReadModel.ResumeCook.cs")));

        Assert.Contains("CookPreparationSequence.Execute(", resume, StringComparison.Ordinal);
        Assert.Contains("CookTriggerKind.Resume", resume, StringComparison.Ordinal);
        Assert.Contains("ResumeCookPreparationAdapter", resume, StringComparison.Ordinal);

        // Resume has its OWN adapter: it does not borrow the recipe adapter, whose
        // state is a recipe tree it does not have.
        Assert.DoesNotContain("DesktopCookPreparationAdapter", resume, StringComparison.Ordinal);

        // The spawn boundary is reached exactly ONCE from the resume path, and it
        // is the same shared helper - not a resume-specific spawn.
        int spawnCalls =
            System.Text.RegularExpressions.Regex.Matches(resume, @"\bSpawnAndSupervise\s*\(").Count;
        Assert.Equal(1, spawnCalls);

        // POSITIVE CONTROL: the same scans find DesktopCookPreparationAdapter in
        // the file that really does use it, so the DoesNotContain above is a real
        // finding.
        string supervisor = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "PAXCookbook.App", "RecipeReadModel.CookSupervisor.cs")));
        Assert.Contains("DesktopCookPreparationAdapter", supervisor, StringComparison.Ordinal);
        Assert.Contains("CookPreparationSequence", supervisor, StringComparison.Ordinal);
    }

    [Fact]
    public void The_structural_resume_scan_is_token_precise_and_comment_blind()
    {
        // POSITIVE CONTROLS for the scan above: the stripper really removes both
        // comment forms, and the SpawnAndSupervise token really is countable.
        const string sample = "a // SpawnAndSupervise(\nb /* SpawnAndSupervise( */ c\nSpawnAndSupervise(x);";
        string stripped = StripComments(sample);

        int strippedHits =
            System.Text.RegularExpressions.Regex.Matches(stripped, @"\bSpawnAndSupervise\s*\(").Count;
        int rawHits =
            System.Text.RegularExpressions.Regex.Matches(sample, @"\bSpawnAndSupervise\s*\(").Count;
        Assert.Equal(1, strippedHits);
        Assert.Equal(3, rawHits);
    }

    internal static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    internal static string StripComments(string source)
    {
        string withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
        return string.Join(
            "\n",
            withoutBlocks.Split('\n').Select(line =>
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx >= 0 ? line[..idx] : line;
            }));
    }
}
