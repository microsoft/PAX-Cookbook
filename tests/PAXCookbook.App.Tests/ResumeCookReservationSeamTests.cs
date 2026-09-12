using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 31 - the production reservation seam.
//
// ResumeCookReservationTests proves the gate exists in the SCHEMA. These tests
// prove the resume route's own reservation helper behaves correctly on top of
// it: exactly one winner under simultaneous contention, a typed conflict that
// names the real running cook, no debris left by a loser, and a compensation
// path that releases a reservation whose cook never started.
//
// Nothing here spawns a process, runs PAX, starts a Bake, creates a cook folder,
// resolves a Chef's Key, or touches the engine. The seam reaches the same
// ReserveResumeCookRowCore the route uses and stops there.
public class ResumeCookReservationSeamTests : IDisposable
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

    private string NewWorkspace() => ResumeCookReservationTests.TrackWorkspace(_workspaces);

    private static string CooksDir(string workspace) => Path.Combine(workspace, "Cooks");

    private static int ResumeCookFolderCount(string workspace)
    {
        string bucket = Path.Combine(CooksDir(workspace), "__resume__");
        return Directory.Exists(bucket) ? Directory.GetDirectories(bucket).Length : 0;
    }

    // ---- sequential contention ----------------------------------------------

    [Fact]
    public void A_second_reservation_for_the_same_identity_is_refused_and_names_the_running_cook()
    {
        string workspace = NewWorkspace();
        const string identity = "C:\\PAX\\OUT";

        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, "cook-first", identity));

        string? conflict = RecipeReadModel.TestSeamReserveResumeCookRow(
            workspace, "cook-second", identity);

        Assert.Equal("cook-first", conflict);

        using SqliteConnection conn = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(1, ResumeCookReservationTests.CountRunning(conn, identity));
        // The refused reservation wrote NO extra row of ANY status ...
        Assert.Equal(1, ResumeCookReservationTests.CountAny(conn, identity));
        // ... and created no cook folder, because the reservation is the first
        // side effect and the loser never gets past it.
        Assert.Equal(0, ResumeCookFolderCount(workspace));
    }

    [Fact]
    public void A_different_identity_reserves_successfully()
    {
        string workspace = NewWorkspace();

        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, "cook-a", "C:\\PAX\\OUT-A"));
        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, "cook-b", "C:\\PAX\\OUT-B"));

        using SqliteConnection conn = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(1, ResumeCookReservationTests.CountRunning(conn, "C:\\PAX\\OUT-A"));
        Assert.Equal(1, ResumeCookReservationTests.CountRunning(conn, "C:\\PAX\\OUT-B"));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("interrupted")]
    [InlineData("cancelled")]
    public void A_prior_terminal_resume_of_the_same_checkpoint_does_not_block(string terminalStatus)
    {
        string workspace = NewWorkspace();
        const string identity = "C:\\PAX\\OUT";

        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, "cook-old", identity));

        // Positive control: while the prior cook is still RUNNING the identity is
        // genuinely held, so the success after the terminal transition below is
        // not a vacuous pass.
        Assert.Equal("cook-old", RecipeReadModel.TestSeamReserveResumeCookRow(
            workspace, "cook-blocked", identity));

        using (SqliteConnection conn = ResumeCookReservationTests.Open(workspace))
        using (SqliteCommand upd = conn.CreateCommand())
        {
            upd.CommandText = "UPDATE cooks SET status = $status WHERE cook_id = 'cook-old';";
            upd.Parameters.AddWithValue("$status", terminalStatus);
            Assert.Equal(1, upd.ExecuteNonQuery());
        }

        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, "cook-new", identity));

        using SqliteConnection after = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(1, ResumeCookReservationTests.CountRunning(after, identity));
        Assert.Equal(2, ResumeCookReservationTests.CountAny(after, identity));
    }

    // ---- simultaneous contention --------------------------------------------

    [Fact]
    public void Simultaneous_reservations_for_the_same_identity_yield_exactly_one_winner()
    {
        string workspace = NewWorkspace();
        const string identity = "C:\\PAX\\OUT";
        const int racers = 8;

        (string?[] results, Exception?[] faults) = RaceReservations(
            workspace, racers, _ => identity);

        Assert.All(faults, fault => Assert.Null(fault));

        int winners = 0;
        string? winnerCookId = null;
        for (int i = 0; i < racers; i++)
        {
            if (results[i] is null)
            {
                winners++;
                winnerCookId = CookId(i);
            }
        }

        Assert.Equal(1, winners);
        Assert.NotNull(winnerCookId);

        // Every loser was told about the SAME winner, so the 409 body names the
        // real running cook rather than a guess.
        for (int i = 0; i < racers; i++)
        {
            if (results[i] is not null)
            {
                Assert.Equal(winnerCookId, results[i]);
            }
        }

        using SqliteConnection conn = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(1, ResumeCookReservationTests.CountRunning(conn, identity));
        Assert.Equal(1, ResumeCookReservationTests.CountAny(conn, identity));
        Assert.Equal(0, ResumeCookFolderCount(workspace));
    }

    [Fact]
    public void Simultaneous_reservations_for_different_identities_all_succeed()
    {
        // Positive control for the race test above: the same barrier, the same
        // thread count, and the same seam produce EIGHT winners when the
        // identities differ, so "exactly one winner" is a property of the identity
        // collision and not of the harness serializing everything.
        string workspace = NewWorkspace();
        const int racers = 8;

        (string?[] results, Exception?[] faults) = RaceReservations(
            workspace, racers, i => "C:\\PAX\\OUT-" + i);

        Assert.All(faults, fault => Assert.Null(fault));
        Assert.All(results, value => Assert.Null(value));

        using SqliteConnection conn = ResumeCookReservationTests.Open(workspace);
        for (int i = 0; i < racers; i++)
        {
            Assert.Equal(1, ResumeCookReservationTests.CountRunning(conn, "C:\\PAX\\OUT-" + i));
        }
    }

    // ---- compensation --------------------------------------------------------

    [Fact]
    public void Releasing_a_reservation_removes_the_row_and_frees_the_identity()
    {
        string workspace = NewWorkspace();
        const string identity = "C:\\PAX\\OUT";

        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, "cook-a", identity));

        // Positive control: the identity really IS held before the release.
        Assert.Equal("cook-a", RecipeReadModel.TestSeamReserveResumeCookRow(
            workspace, "cook-b", identity));

        RecipeReadModel.TestSeamReleaseResumeCookReservation(workspace, "cook-a");

        using (SqliteConnection conn = ResumeCookReservationTests.Open(workspace))
        {
            // No phantom row of ANY status survives a reservation that never started.
            Assert.Equal(0, ResumeCookReservationTests.CountAny(conn, identity));
        }

        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, "cook-b", identity));

        using SqliteConnection after = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(1, ResumeCookReservationTests.CountRunning(after, identity));
    }

    [Fact]
    public void Releasing_an_unknown_cook_id_is_a_harmless_no_op()
    {
        string workspace = NewWorkspace();
        const string identity = "C:\\PAX\\OUT";

        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, "cook-a", identity));

        RecipeReadModel.TestSeamReleaseResumeCookReservation(workspace, "cook-never-existed");

        using SqliteConnection conn = ResumeCookReservationTests.Open(workspace);
        Assert.Equal(1, ResumeCookReservationTests.CountRunning(conn, identity));
    }

    // ---- harness -------------------------------------------------------------

    private static string CookId(int index) => "cook-" + index.ToString("D2");

    private static (string?[] Results, Exception?[] Faults) RaceReservations(
        string workspace, int racers, Func<int, string> identityFor)
    {
        using var barrier = new Barrier(racers);
        var results = new string?[racers];
        var faults = new Exception?[racers];
        var tasks = new Task[racers];

        for (int i = 0; i < racers; i++)
        {
            int index = i;
            tasks[index] = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        results[index] = RecipeReadModel.TestSeamReserveResumeCookRow(
                            workspace, CookId(index), identityFor(index));
                    }
                    catch (Exception ex)
                    {
                        faults[index] = ex;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Task.WaitAll(tasks);
        return (results, faults);
    }
}
