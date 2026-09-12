using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace PAXCookbook.Service.Tests;

/// <summary>
/// Every test drives the worker at a unique directory built from
/// <see cref="Path.GetTempPath"/>. No test resolves or reaches the production
/// machine root; the production resolver is a private member of the composition
/// root and is never called here.
/// </summary>
public sealed class StartupProbeWorkerTests : IDisposable
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    // CYCLE 64. DEADLOCK GUARD, NOT A TIMEOUT AND NOT A TOLERANCE.
    //
    // The deterministic heartbeat tests below never wait for time to pass: every
    // handshake they await is signalled SYNCHRONOUSLY by the worker's own thread, so on
    // the passing path this guard is never approached. It exists only so that a genuine
    // regression - a worker that stops scheduling, or stops writing - fails the test
    // instead of hanging the run. Its expiry is a hard failure. It is never used to
    // retry, to re-poll, or to give a timing-sensitive outcome another chance, and
    // raising it could not turn a failing run into a passing one.
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(30);

    private readonly string _root;
    private readonly ServicePaths _paths;
    private readonly ITestOutputHelper _output;

    public StartupProbeWorkerTests(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));

        _root = Path.Combine(
            Path.GetTempPath(),
            "paxcookbook-service-tests",
            Guid.NewGuid().ToString("N"));

        _paths = ServicePaths.ForMetadataRoot(_root);

        // CYCLE 63R. The elevated helper creates and protects the RUNTIME root
        // before the service is ever started, and the service account cannot
        // create it. Every test therefore starts from that helper-created state,
        // exactly as production does. The two fail-closed tests below
        // deliberately undo it.
        Directory.CreateDirectory(_paths.Root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    [Fact]
    public void TheTestRootIsUnderTheTemporaryDirectoryAndNotTheProductionRoot()
    {
        Assert.StartsWith(
            Path.GetFullPath(Path.GetTempPath()),
            _paths.Root,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("ProgramData", _paths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProgramData", _paths.MetadataRoot, StringComparison.OrdinalIgnoreCase);
    }

    // CYCLE 63R (authorized repair). The service NEVER creates its runtime root -
    // creating it would mean writing the metadata parent it is deliberately not
    // permitted to write. It writes only INSIDE the helper-created runtime root.
    [Fact]
    public async Task StartCreatesTheRootAndWritesStatusAndHeartbeat()
    {
        Assert.True(Directory.Exists(_paths.Root));

        await RunAsync(async () =>
        {
            await WaitForStateAsync("running").ConfigureAwait(false);
            Assert.True(File.Exists(_paths.HeartbeatFile));
        });

        Assert.True(Directory.Exists(_paths.Root));
        Assert.Equal("stopped", ReadStatusState());

        // The service wrote NOTHING into the metadata parent: its only member is
        // the runtime directory the helper created.
        Assert.Equal(
            new[] { _paths.Root },
            Directory.GetFileSystemEntries(_paths.MetadataRoot));
    }

    // CYCLE 63R. Missing runtime root: fail closed. No recreation, no "running"
    // status, and a bounded host shutdown request instead of a crash loop.
    [Fact]
    public async Task AMissingRuntimeRootFailsClosedWithoutRecreatingItOrClaimingRunning()
    {
        Directory.Delete(_paths.Root, recursive: true);
        Assert.False(Directory.Exists(_paths.Root));

        var lifetime = new RecordingHostLifetime();
        var worker = new StartupProbeWorker(_paths, Heartbeat, lifetime);

        try
        {
            await worker.StartAsync(CancellationToken.None);
            await WaitForAsync(() => lifetime.StopRequested);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }

        Assert.Equal(ServiceRuntimeRootState.Missing, worker.LastRuntimeRootState);
        Assert.True(worker.RequestedHostShutdown);
        Assert.True(lifetime.StopRequested);

        // Nothing was recreated and no document was emitted anywhere.
        Assert.False(Directory.Exists(_paths.Root));
        Assert.False(File.Exists(_paths.StatusFile));
        Assert.False(File.Exists(_paths.HeartbeatFile));
        Assert.Empty(Directory.GetFileSystemEntries(_paths.MetadataRoot));
    }

    // CYCLE 63R. Unwritable runtime root: fail closed for the same reasons. The
    // runtime root exists but the status destination cannot be replaced.
    [Fact]
    public async Task AnUnwritableRuntimeRootFailsClosedAndNeverClaimsRunning()
    {
        // A DIRECTORY at the status destination makes the atomic replace fail
        // deterministically, with no ACL work and no lock timing.
        Directory.CreateDirectory(_paths.StatusFile);

        var lifetime = new RecordingHostLifetime();
        var worker = new StartupProbeWorker(_paths, Heartbeat, lifetime);

        try
        {
            await worker.StartAsync(CancellationToken.None);
            await WaitForAsync(() => lifetime.StopRequested);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }

        Assert.Equal(ServiceRuntimeRootState.Unwritable, worker.LastRuntimeRootState);
        Assert.True(worker.RequestedHostShutdown);
        Assert.True(lifetime.StopRequested);
        Assert.False(File.Exists(_paths.HeartbeatFile));

        // No status document exists at all, so no "running" claim can have been made.
        Assert.False(File.Exists(_paths.StatusFile));
    }

    [Fact]
    public async Task NoProbeRequestProducesNoProbeResult()
    {
        await RunAsync(async () =>
        {
            await WaitForStateAsync("running").ConfigureAwait(false);
        });

        Assert.False(File.Exists(_paths.ProbeResultFile));
    }

    [Fact]
    public async Task TheFixedProbeRequestIsConsumedExactlyOnce()
    {
        SeedProbeRequest();

        await RunAsync(async () =>
        {
            await WaitForFileAsync(_paths.ProbeResultFile).ConfigureAwait(false);
        });

        Assert.True(File.Exists(_paths.ProbeResultFile));
        Assert.False(File.Exists(_paths.ProbeRequestFile));

        using var parsed = JsonDocument.Parse(File.ReadAllText(_paths.ProbeResultFile));
        var root = parsed.RootElement;

        Assert.Equal(ServiceContract.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ServiceContract.StartupProbeId, root.GetProperty("probeId").GetString());
        Assert.Equal("completed", root.GetProperty("outcome").GetString());
        Assert.Equal(ServiceContract.ServiceVersion, root.GetProperty("serviceVersion").GetString());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("observedUtc").GetString(), out _));
        root.GetProperty("sessionId").GetInt32();
        root.GetProperty("userInteractive").GetBoolean();
    }

    [Fact]
    public async Task ReplayRefusalIsDurableAcrossHostInstances()
    {
        SeedProbeRequest();

        await RunAsync(async () =>
        {
            await WaitForFileAsync(_paths.ProbeResultFile).ConfigureAwait(false);
        });

        var firstResult = File.ReadAllBytes(_paths.ProbeResultFile);

        // A second, independent host instance sees the durable result and refuses.
        SeedProbeRequest();

        await RunAsync(async () =>
        {
            await WaitForStateAsync("running").ConfigureAwait(false);
        });

        Assert.Equal(firstResult, File.ReadAllBytes(_paths.ProbeResultFile));

        // Refusal consumes nothing: the replayed request is left untouched.
        Assert.True(File.Exists(_paths.ProbeRequestFile));
    }

    [Fact]
    public async Task APreExistingResultSuppressesTheProbeEntirely()
    {
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(_paths.ProbeResultFile, "{\"schemaVersion\":1}");
        var seeded = File.ReadAllBytes(_paths.ProbeResultFile);
        SeedProbeRequest();

        await RunAsync(async () =>
        {
            await WaitForStateAsync("running").ConfigureAwait(false);
        });

        Assert.Equal(seeded, File.ReadAllBytes(_paths.ProbeResultFile));
        Assert.True(File.Exists(_paths.ProbeRequestFile));
    }

    // =====================================================================================
    // CYCLE 64 - DETERMINISTIC HEARTBEAT TESTS.
    //
    // These replace the three tests that were flaky at 7-in-20 before this cycle. They use
    // NO real clock, NO real filesystem, NO FileSystemWatcher, NO file lock, NO sleep and
    // NO elapsed-time assertion. Time advances ONLY when a test explicitly releases exactly
    // one heartbeat, and every write outcome is INJECTED rather than provoked.
    //
    // PRODUCTION BEHAVIOUR IS UNCHANGED AND IS EXACTLY WHAT IS ASSERTED: one beat per
    // configured interval, ONE tolerated consecutive write failure, a success resets the
    // count, the SECOND consecutive failure is terminal, cancellation stops further writes.
    //
    // ATTEMPT NUMBERING USED THROUGHOUT: attempt 1 is the pre-loop beat the worker writes
    // before it claims "running". Attempt 2 is the first in-loop beat. The worker then parks
    // on the scheduler until a test releases it.
    // =====================================================================================

    [Fact]
    public async Task TheSteadyHeartbeatAdvancesExactlyOnceForEachExplicitlyReleasedTick()
    {
        await using var harness = new DeterministicWorkerHarness();
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the first scheduled heartbeat wait");
        Assert.Equal(2, harness.Writer.HeartbeatAttempts.Count);

        for (var released = 1; released <= 3; released++)
        {
            harness.Scheduler.ReleaseOneHeartbeat();
            await AwaitDeterministicAsync(
                harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "scheduled heartbeat wait " + (released + 1));

            // One released tick produces EXACTLY one further beat. Never zero, never two.
            Assert.Equal(2 + released, harness.Writer.HeartbeatAttempts.Count);
        }

        var attempts = harness.Writer.HeartbeatAttempts;
        Assert.Equal(5, attempts.Count);
        Assert.All(attempts, a => Assert.True(a.Succeeded));
        Assert.Equal(5, attempts.Select(a => a.AttemptId).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal("running", harness.Writer.LastStatusState);
        Assert.False(harness.Worker.ExecuteTask!.IsCompleted);
        Assert.Equal(4, harness.Scheduler.WaitsStarted);
        Assert.Equal(3, harness.Scheduler.WaitsCompleted);

        // The deterministic path touched no real filesystem at all.
        Assert.Empty(Directory.GetFileSystemEntries(_paths.Root));
    }

    [Fact]
    public async Task OneInjectedHeartbeatFailureIsToleratedAndTheNextReleasedAttemptRecovers()
    {
        // Attempt 2 - the first in-loop beat - is the injected failure.
        await using var harness = new DeterministicWorkerHarness(2);
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the tolerated failure");

        Assert.Equal(2, harness.Writer.HeartbeatAttempts.Count);
        Assert.True(harness.Writer.HeartbeatAttempts[0].Succeeded);
        Assert.False(harness.Writer.HeartbeatAttempts[1].Succeeded);

        // One failure is tolerated: not terminal, and the worker is still scheduling.
        Assert.NotEqual("failed", harness.Writer.LastStatusState);
        Assert.False(harness.Worker.ExecuteTask!.IsCompleted);

        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the recovering beat");

        Assert.Equal(3, harness.Writer.HeartbeatAttempts.Count);
        Assert.True(harness.Writer.HeartbeatAttempts[2].Succeeded);
        Assert.NotEqual("failed", harness.Writer.LastStatusState);
        Assert.False(harness.Worker.ExecuteTask.IsCompleted);
    }

    [Fact]
    public async Task AFailureThenASuccessThenAFailureStaysNonTerminalBecauseTheSuccessResetTheStrike()
    {
        // Attempts 2 and 4 fail; attempt 3 succeeds between them, so the two failures are
        // NOT consecutive and the second one must be tolerated exactly like the first.
        await using var harness = new DeterministicWorkerHarness(2, 4);
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the first tolerated failure");
        Assert.Equal(2, harness.Writer.HeartbeatAttempts.Count);

        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the resetting success");
        Assert.Equal(3, harness.Writer.HeartbeatAttempts.Count);

        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the second tolerated failure");

        var attempts = harness.Writer.HeartbeatAttempts;
        Assert.Equal(4, attempts.Count);
        Assert.False(attempts[1].Succeeded);
        Assert.True(attempts[2].Succeeded);
        Assert.False(attempts[3].Succeeded);

        // Still alive, still scheduling, still not terminal.
        Assert.NotEqual("failed", harness.Writer.LastStatusState);
        Assert.False(harness.Worker.ExecuteTask!.IsCompleted);
        Assert.Equal(3, harness.Scheduler.WaitsStarted);
    }

    [Fact]
    public async Task TwoConsecutiveFailuresUseTwoDistinctAttemptIdentitiesAndProduceTerminalFailed()
    {
        await using var harness = new DeterministicWorkerHarness(2, 3);
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the first failure");
        Assert.Equal(2, harness.Writer.HeartbeatAttempts.Count);
        Assert.NotEqual("failed", harness.Writer.LastStatusState);

        // The second CONSECUTIVE failure is terminal, so the worker exits instead of waiting.
        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(harness.Worker.ExecuteTask!, "the worker to reach its terminal state");

        var attempts = harness.Writer.HeartbeatAttempts;
        Assert.Equal(3, attempts.Count);
        Assert.False(attempts[1].Succeeded);
        Assert.False(attempts[2].Succeeded);

        // A genuine SECOND attempt, not one attempt observed twice: the identities differ.
        Assert.NotEqual(attempts[1].AttemptId, attempts[2].AttemptId);
        Assert.Equal(3, attempts.Select(a => a.AttemptId).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal("failed", harness.Writer.LastStatusState);
    }

    [Fact]
    public async Task NoThirdHeartbeatAttemptOccursAfterTheTerminalFailure()
    {
        await using var harness = new DeterministicWorkerHarness(2, 3);
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the first failure");
        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(harness.Worker.ExecuteTask!, "the worker to reach its terminal state");

        Assert.Equal(3, harness.Writer.HeartbeatAttempts.Count);

        // The worker never parked on the scheduler again, so there is nothing left to release.
        Assert.Equal(1, harness.Scheduler.WaitsStarted);
        Assert.Throws<InvalidOperationException>(() => harness.Scheduler.ReleaseOneHeartbeat());

        // And nothing arrived afterwards either.
        Assert.Equal(3, harness.Writer.HeartbeatAttempts.Count);
        Assert.Equal("failed", harness.Writer.LastStatusState);
    }

    [Fact]
    public async Task CancellationCausesNoLaterHeartbeatAttempt()
    {
        await using var harness = new DeterministicWorkerHarness();
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the scheduled heartbeat wait");
        Assert.Equal(2, harness.Writer.HeartbeatAttempts.Count);

        await AwaitDeterministicAsync(
            harness.Worker.StopAsync(CancellationToken.None), "cooperative shutdown");

        // Cancellation is observed by the pending scheduler wait; no further beat is written.
        Assert.Equal(2, harness.Writer.HeartbeatAttempts.Count);
        Assert.Equal(1, harness.Scheduler.WaitsCanceled);
        Assert.Equal(0, harness.Scheduler.WaitsCompleted);
        Assert.Equal("stopped", harness.Writer.LastStatusState);
    }

    // CYCLE 64 - CORRECTION B, LOCKED IN AS AN EXECUTABLE ASSERTION.
    //
    // Cycle 63RR observed `Expected: "stopped" Actual: "failed"` from a CANCELLATION test.
    // The cause was a real write failure inside the old harness, never an interaction
    // between cancellation and the two-strikes rule - the worker has exactly ONE
    // `terminalState = Failed` assignment and OperationCanceledException is caught one
    // clause earlier without assigning it. This test proves that directly: a tolerated
    // failure followed by cancellation must terminate as "stopped", never "failed".
    [Fact]
    public async Task CancellationAfterAToleratedFailureStillTerminatesAsStoppedAndNeverFailed()
    {
        await using var harness = new DeterministicWorkerHarness(2);
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the tolerated failure");
        Assert.False(harness.Writer.HeartbeatAttempts[1].Succeeded);

        await AwaitDeterministicAsync(
            harness.Worker.StopAsync(CancellationToken.None), "cooperative shutdown");

        Assert.Equal("stopped", harness.Writer.LastStatusState);
        Assert.NotEqual("failed", harness.Writer.LastStatusState);
        Assert.Equal(2, harness.Writer.HeartbeatAttempts.Count);
    }

    // =====================================================================================
    // CYCLE 77 - TERMINAL FAILURE MUST ALSO STOP THE HOST.
    //
    // THE FIELD DEFECT. ExecuteAsync caught the terminal exception, wrote "failed", and
    // RETURNED. Because the worker is a BackgroundService under UseWindowsService, the host
    // and the SCM both stayed Running while the worker was dead: SCM Running, process alive
    // in Session 0, status document "failed", heartbeat frozen at 02:12:39Z and failed at
    // 02:12:58Z. A dead worker inside a Running service is the worst possible shape - the
    // SCM reports health that no longer exists.
    //
    // EVERY SHUTDOWN ASSERTION BELOW CHECKS BOTH `RequestedHostShutdown` AND
    // `Lifetime.StopRequested`. The flag alone is vacuous: it would pass even if
    // StopApplication() were never called.
    // =====================================================================================

    [Fact]
    public async Task ATerminalHeartbeatFailureWritesFailedAndThenRequestsHostShutdown()
    {
        await using var harness = new DeterministicWorkerHarness(2, 3);
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the first failure");
        Assert.False(harness.Worker.RequestedHostShutdown);
        Assert.False(harness.Lifetime.StopRequested);

        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(harness.Worker.ExecuteTask!, "the worker to reach its terminal state");

        // The three facts the pre-repair worker already produced.
        Assert.Equal("failed", harness.Writer.LastStatusState);
        Assert.True(harness.Worker.ExecuteTask!.IsCompleted);
        Assert.Equal(ServiceRuntimeRootState.Ready, harness.Worker.LastRuntimeRootState);

        // The fact it did NOT produce, which left the host and the SCM Running.
        Assert.True(
            harness.Worker.RequestedHostShutdown,
            "A terminal worker failure must request a bounded host shutdown; without it the" +
            " BackgroundService completes while host.Run() and the SCM both remain Running.");
        Assert.True(
            harness.Lifetime.StopRequested,
            "StopApplication() must actually be called. Asserting only RequestedHostShutdown" +
            " would pass even if the host were never asked to stop.");
        Assert.Equal(1, harness.Lifetime.StopRequestCount);

        // ORDER, not just both facts: the terminal status was already durable when the
        // shutdown was requested.
        Assert.Equal("failed", harness.Lifetime.ObservedStatusStateAtStopRequest);
        Assert.Equal(
            new[] { "starting", "running", "failed" },
            harness.Writer.DurableStatusStates);
    }

    // A terminal status write that FAILS must still stop the host. TryWriteStatus already
    // swallows every exception, and the shutdown request sits AFTER it and OUTSIDE its try,
    // so this holds by construction rather than by a second handler.
    [Fact]
    public async Task AFailingTerminalStatusWriteStillRequestsHostShutdown()
    {
        await using var harness = new DeterministicWorkerHarness(2, 3);
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the first failure");

        // Flipped while the worker is parked, so exactly the terminal write throws.
        harness.Writer.FailSubsequentStatusWrites();

        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(harness.Worker.ExecuteTask!, "the worker to reach its terminal state");

        // The terminal write was attempted and failed, so no "failed" document is durable.
        Assert.Equal(1, harness.Writer.StatusWriteFailureCount);
        Assert.Equal("failed", harness.Writer.AttemptedStatusStates[^1]);
        Assert.DoesNotContain("failed", harness.Writer.DurableStatusStates);
        Assert.Equal("running", harness.Writer.LastStatusState);

        // The host is stopped anyway.
        Assert.True(harness.Worker.RequestedHostShutdown);
        Assert.True(harness.Lifetime.StopRequested);
        Assert.Equal(1, harness.Lifetime.StopRequestCount);
        Assert.True(harness.Worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task OneToleratedHeartbeatFailureRequestsNoHostShutdown()
    {
        await using var harness = new DeterministicWorkerHarness(2);
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the tolerated failure");

        Assert.False(harness.Writer.HeartbeatAttempts[1].Succeeded);
        Assert.False(harness.Worker.ExecuteTask!.IsCompleted);
        Assert.NotEqual("failed", harness.Writer.LastStatusState);

        Assert.False(harness.Worker.RequestedHostShutdown);
        Assert.False(harness.Lifetime.StopRequested);
        Assert.Equal(0, harness.Lifetime.StopRequestCount);
    }

    [Fact]
    public async Task ASuccessResetsTheStrikeSoTheNextFailureRequestsNoHostShutdown()
    {
        // Attempts 2 and 4 fail with a success at 3 between them: NOT consecutive.
        await using var harness = new DeterministicWorkerHarness(2, 4);
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the first tolerated failure");
        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the resetting success");
        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the second tolerated failure");

        var attempts = harness.Writer.HeartbeatAttempts;
        Assert.Equal(4, attempts.Count);
        Assert.False(attempts[1].Succeeded);
        Assert.True(attempts[2].Succeeded);
        Assert.False(attempts[3].Succeeded);

        Assert.NotEqual("failed", harness.Writer.LastStatusState);
        Assert.False(harness.Worker.ExecuteTask!.IsCompleted);
        Assert.False(harness.Worker.RequestedHostShutdown);
        Assert.False(harness.Lifetime.StopRequested);
        Assert.Equal(0, harness.Lifetime.StopRequestCount);
    }

    [Fact]
    public async Task CancellationWritesStoppedNeverFailedAndRequestsNoHostShutdown()
    {
        await using var harness = new DeterministicWorkerHarness();
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the scheduled heartbeat wait");

        await AwaitDeterministicAsync(
            harness.Worker.StopAsync(CancellationToken.None), "cooperative shutdown");

        Assert.Equal("stopped", harness.Writer.LastStatusState);
        Assert.DoesNotContain("failed", harness.Writer.DurableStatusStates);
        Assert.DoesNotContain("failed", harness.Writer.AttemptedStatusStates);

        // Cooperative cancellation is NOT a failure, so it must never ask the host to stop:
        // the host is already stopping, and a request here would be a second, unowned signal.
        Assert.False(harness.Worker.RequestedHostShutdown);
        Assert.False(harness.Lifetime.StopRequested);
        Assert.Equal(0, harness.Lifetime.StopRequestCount);
    }

    // A4. There is deliberately NO second property. The terminal-failure shutdown and the
    // startup runtime-root shutdown stay distinguishable through LastRuntimeRootState, and
    // this test asserts that pair directly so the two cases can never blur together.
    [Fact]
    public async Task TheTerminalFailureShutdownIsDistinguishableFromTheStartupRootShutdown()
    {
        await using var harness = new DeterministicWorkerHarness(2, 3);
        await harness.StartAsync();
        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the first failure");
        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(harness.Worker.ExecuteTask!, "the worker to reach its terminal state");

        Assert.True(harness.Worker.RequestedHostShutdown);
        Assert.True(harness.Lifetime.StopRequested);
        Assert.Equal(ServiceRuntimeRootState.Ready, harness.Worker.LastRuntimeRootState);

        // The startup path, driven against the REAL writer with the runtime root removed.
        Directory.Delete(_paths.Root, recursive: true);
        var startupLifetime = new RecordingHostLifetime();
        var startupWorker = new StartupProbeWorker(_paths, Heartbeat, startupLifetime);

        try
        {
            await startupWorker.StartAsync(CancellationToken.None);
            await WaitForAsync(() => startupLifetime.StopRequested);
        }
        finally
        {
            await startupWorker.StopAsync(CancellationToken.None);
            startupWorker.Dispose();
        }

        Assert.True(startupWorker.RequestedHostShutdown);
        Assert.True(startupLifetime.StopRequested);
        Assert.Equal(ServiceRuntimeRootState.Missing, startupWorker.LastRuntimeRootState);

        // Same flag, different verdict: the two shutdowns remain separable with no new property.
        Assert.NotEqual(harness.Worker.LastRuntimeRootState, startupWorker.LastRuntimeRootState);
    }

    [Fact]
    public async Task TheTerminalFailurePathEmitsNoExceptionTextPathIdentityOrNativeDetail()
    {
        await using var harness = new DeterministicWorkerHarness(2, 3);
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the wait after the first failure");
        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(harness.Worker.ExecuteTask!, "the worker to reach its terminal state");

        Assert.True(harness.Worker.RequestedHostShutdown);
        Assert.True(harness.Lifetime.StopRequested);

        var allowedStates = new[] { "starting", "running", "stopped", "failed" };
        var forbidden = new[]
        {
            "injected", "Exception", "IOException", "Unauthorized", "HRESULT", "0x",
            "Stack", "   at ", "PAXCookbook.Service.", ":\\", "//", "\\\\",
            Environment.MachineName, Environment.UserName,
        };

        var documents = harness.Writer.DurableStatusDocuments;
        Assert.NotEmpty(documents);

        foreach (var document in documents)
        {
            Assert.Contains(document.State, allowedStates);

            var json = JsonSerializer.Serialize(document, ServiceContract.JsonOptions);
            _output.WriteLine("STATUS_DOCUMENT|" + json);

            foreach (var needle in forbidden)
            {
                Assert.DoesNotContain(needle, json, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // CYCLE 64. The old harness inferred attempt identity from FileSystemWatcher Created and
    // Deleted deliveries, so a duplicate delivery could in principle be mistaken for a second
    // attempt. That whole failure mode is now structurally absent: identity is assigned INSIDE
    // the write call, once per invocation, from no notification channel at all.
    [Fact]
    public async Task DuplicateNotificationDeliveryCannotManufactureASecondAttemptBecauseNoneIsUsed()
    {
        await using var harness = new DeterministicWorkerHarness();
        await harness.StartAsync();

        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the first scheduled heartbeat wait");
        harness.Scheduler.ReleaseOneHeartbeat();
        await AwaitDeterministicAsync(
            harness.Scheduler.WaitUntilWorkerIsWaitingAsync(), "the second scheduled heartbeat wait");

        // Observing the record repeatedly cannot change it - there is no delivery to duplicate.
        var firstObservation = harness.Writer.HeartbeatAttempts.Select(a => a.AttemptId).ToArray();
        var secondObservation = harness.Writer.HeartbeatAttempts.Select(a => a.AttemptId).ToArray();
        Assert.Equal(firstObservation, secondObservation);

        // Attempt count is the INVOCATION count, counted independently of the record list.
        Assert.Equal(harness.Writer.HeartbeatInvocationCount, firstObservation.Length);
        Assert.Equal(3, firstObservation.Length);
        Assert.Equal(3, firstObservation.Distinct(StringComparer.Ordinal).Count());
    }

    // --- CYCLE 64: the two-strikes rule as a pure transition, with no clock and no IO. ---

    [Fact]
    public void TheToleratedConsecutiveFailureAllowanceIsExactlyOne()
    {
        Assert.Equal(1, HeartbeatStrikeRule.ToleratedConsecutiveFailures);
    }

    [Fact]
    public void TheStrikeStateStartsAtZeroAndIsNotTerminal()
    {
        Assert.Equal(0, HeartbeatStrikeState.None.ConsecutiveFailures);
        Assert.False(HeartbeatStrikeState.None.IsTerminal);
    }

    [Fact]
    public void ASuccessAlwaysResetsTheStrikeCountToZeroAndIsNeverTerminal()
    {
        var afterOneFailure = HeartbeatStrikeRule.AfterFailure(HeartbeatStrikeState.None);
        var afterSuccess = HeartbeatStrikeRule.AfterSuccess(afterOneFailure);

        Assert.Equal(0, afterSuccess.ConsecutiveFailures);
        Assert.False(afterSuccess.IsTerminal);
        Assert.Equal(HeartbeatStrikeState.None, afterSuccess);
    }

    [Fact]
    public void TheFirstFailureIsToleratedAndTheSecondConsecutiveFailureIsTerminal()
    {
        var first = HeartbeatStrikeRule.AfterFailure(HeartbeatStrikeState.None);
        Assert.Equal(1, first.ConsecutiveFailures);
        Assert.False(first.IsTerminal);

        var second = HeartbeatStrikeRule.AfterFailure(first);
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.True(second.IsTerminal);
    }

    [Fact]
    public void AFailureSuccessFailureSequenceIsNeverTerminalUnderThePureRule()
    {
        var state = HeartbeatStrikeState.None;
        state = HeartbeatStrikeRule.AfterFailure(state);
        Assert.False(state.IsTerminal);

        state = HeartbeatStrikeRule.AfterSuccess(state);
        state = HeartbeatStrikeRule.AfterFailure(state);

        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.False(state.IsTerminal);
    }

    // --- CYCLE 64: REAL-FILESYSTEM COVERAGE OF THE PRODUCTION WRITER. ------------------
    // These deliberately use the REAL AtomicRuntimeDocumentWriter against a REAL OS-temp
    // runtime root. Failure is induced deterministically by placing a DIRECTORY at the
    // destination - never by timing, never by a lock, never by a FileSystemWatcher.

    [Fact]
    public void TheProductionWriterPerformsARealAtomicWriteInStrictUtf8WithoutABom()
    {
        var writer = new AtomicRuntimeDocumentWriter(_paths);
        writer.WriteStatus(NewStatusDocument("running"));

        Assert.True(File.Exists(_paths.StatusFile));

        var bytes = File.ReadAllBytes(_paths.StatusFile);
        Assert.True(bytes.Length >= 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        // Round-trips as strict UTF-8 and carries the expected bounded state.
        var text = new UTF8Encoding(false, true).GetString(bytes);
        using var parsed = JsonDocument.Parse(text);
        Assert.Equal("running", parsed.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void TheProductionWriterComposesAUniqueSameDirectoryStagingNameForEveryAttempt()
    {
        const int Attempts = 200;

        var composed = Enumerable
            .Range(0, Attempts)
            .Select(_ => AtomicRuntimeDocumentWriter.ComposeStagingPath(_paths.HeartbeatFile))
            .ToArray();

        // Unique per attempt: no two attempts can ever collide on a staging file.
        Assert.Equal(Attempts, composed.Distinct(StringComparer.Ordinal).Count());

        foreach (var staging in composed)
        {
            // SAME DIRECTORY as the destination - a staging file never leaves the runtime root.
            Assert.Equal(_paths.Root, Path.GetDirectoryName(staging));
            Assert.StartsWith(
                ServiceContract.HeartbeatFileName + ".staging-",
                Path.GetFileName(staging),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AFailedProductionWriteRemovesItsStagingFileAndCorruptsNothingInTheNamespace()
    {
        var writer = new AtomicRuntimeDocumentWriter(_paths);

        // A durable, known-good neighbour document to prove the failure corrupts nothing.
        writer.WriteStatus(NewStatusDocument("running"));
        var statusBytesBefore = File.ReadAllBytes(_paths.StatusFile);

        // DETERMINISTIC FAILURE INDUCTION: a DIRECTORY at the heartbeat destination. No timing,
        // no lock, no notification - the atomic replace simply cannot succeed.
        Directory.CreateDirectory(_paths.HeartbeatFile);

        var thrown = Assert.ThrowsAny<Exception>(() => writer.WriteHeartbeat(NewHeartbeatDocument()));
        Assert.True(
            thrown is IOException or UnauthorizedAccessException,
            "the production writer must surface a failure the worker's filter recognises, got " +
            thrown.GetType().FullName);

        // The failed attempt cleaned up after itself: no staging residue anywhere.
        var residue = Directory
            .GetFiles(_paths.Root)
            .Select(Path.GetFileName)
            .Where(n => n is not null && n.Contains(".staging-", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(residue);

        // The neighbour document is byte-identical, and the failed destination is untouched.
        Assert.Equal(statusBytesBefore, File.ReadAllBytes(_paths.StatusFile));
        Assert.True(Directory.Exists(_paths.HeartbeatFile));
        Assert.Empty(Directory.GetFileSystemEntries(_paths.HeartbeatFile));

        // No exception text was persisted anywhere in the runtime namespace.
        foreach (var file in Directory.GetFiles(_paths.Root))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Exception", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheProductionWriterLeavesNoUnknownRuntimeArtifactAcrossRepeatedWrites()
    {
        var writer = new AtomicRuntimeDocumentWriter(_paths);

        for (var i = 0; i < 5; i++)
        {
            writer.WriteStatus(NewStatusDocument("running"));
            writer.WriteHeartbeat(NewHeartbeatDocument());
        }

        writer.WriteProbeResult(NewProbeResultDocument());

        var written = Directory.GetFiles(_paths.Root)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                ServiceContract.HeartbeatFileName,
                ServiceContract.ProbeResultFileName,
                ServiceContract.StatusFileName,
            }.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            written);

        Assert.Empty(Directory.GetDirectories(_paths.Root));
    }

    [Fact]
    public void TheProductionWriterPredicatesAnswerOnlyAboutTheirOwnFixedDocuments()
    {
        var writer = new AtomicRuntimeDocumentWriter(_paths);

        Assert.True(writer.RuntimeRootExists());
        Assert.False(writer.ProbeResultExists());
        Assert.False(writer.ProbeRequestExists());

        SeedProbeRequest();
        Assert.True(writer.ProbeRequestExists());
        Assert.False(writer.ProbeResultExists());

        writer.WriteProbeResult(NewProbeResultDocument());
        Assert.True(writer.ProbeResultExists());

        writer.RemoveProbeRequest();
        Assert.False(writer.ProbeRequestExists());
        Assert.True(writer.ProbeResultExists());
    }

    [Fact]
    public void TheProductionWriterRejectsANullPathBinding()
    {
        Assert.Throws<ArgumentNullException>(() => new AtomicRuntimeDocumentWriter(null!));
    }

    [Fact]
    public void TheProductionSchedulerRejectsANonPositiveInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FixedIntervalHeartbeatScheduler(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FixedIntervalHeartbeatScheduler(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void TheWorkerRejectsANullSeamBinding()
    {
        Assert.Throws<ArgumentNullException>(
            () => new StartupProbeWorker(null!, new ManualHeartbeatScheduler()));
        Assert.Throws<ArgumentNullException>(
            () => new StartupProbeWorker(new RecordingRuntimeDocumentWriter(), null!));
    }


    // --- Discriminating controls (test-owned; no production edit; no product process). ---
    // Each proves the synchronization instrument used above is capable of BOTH a pass and a
    // fail outcome for the corresponding capability, so none of these checks can only ever
    // succeed or only ever fail.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PollingInstrumentCorrectlyDetectsWhetherAValueAdvances(bool valueAdvances)
    {
        var start = DateTime.UtcNow;

        string? Selector()
        {
            if (!valueAdvances)
            {
                return null;
            }

            return (DateTime.UtcNow - start) > TimeSpan.FromMilliseconds(30) ? "advanced-value" : null;
        }

        if (valueAdvances)
        {
            var result = await WaitForValueAsync(Selector, TimeSpan.FromMilliseconds(500));
            Assert.Equal("advanced-value", result);
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(() => WaitForValueAsync(Selector, TimeSpan.FromMilliseconds(80)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollingInstrumentCorrectlyDetectsAByteChangeAfterASnapshot(bool bytesChangeDuringWindow)
    {
        var callCount = 0;

        byte[] Read()
        {
            callCount++;
            return bytesChangeDuringWindow && callCount > 2
                ? new byte[] { 9, 9, 9 }
                : new byte[] { 1, 2, 3 };
        }

        var snapshot = Read();

        if (!bytesChangeDuringWindow)
        {
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(20);
                Assert.Equal(snapshot, Read());
            }
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    await Task.Delay(20);
                    Assert.Equal(snapshot, Read());
                }
            });
        }
    }

    [Theory]
    [InlineData("stopped", true)]
    [InlineData("running", false)]
    public void DurableStatusEqualityCheckDistinguishesStoppedFromNonStopped(string observedState, bool expectedToPass)
    {
        if (expectedToPass)
        {
            Assert.Equal("stopped", observedState);
        }
        else
        {
            Assert.ThrowsAny<Exception>(() => Assert.Equal("stopped", observedState));
        }
    }

    // --- TASK 4: duplicate-delivery control (test-owned; no production edit; no product process).
    // First attempts a FIXED, PREDECLARED count of natural OS-temp operations and reports honestly
    // whether the platform delivered any duplicates. Regardless of that observation, a separate
    // DETERMINISTIC control feeds duplicate events directly into the correlation logic so
    // correctness never depends on the platform misbehaving on demand. ---

    [Fact]
    public async Task NaturalDuplicateDeliveryObservationUsesAFixedPredeclaredOperationCountAndReportsHonestly()
    {
        // FIXED, PREDECLARED count - never adjusted based on what is observed.
        const int PredeclaredOperationCount = 50;

        Directory.CreateDirectory(_root);
        var journal = new StagingEventJournal();

        using var watcher = new FileSystemWatcher(_root, "probe.staging-*")
        {
            NotifyFilter = NotifyFilters.FileName,
        };

        watcher.Created += (_, e) => journal.Record(StagingEventKind.Created, e.FullPath);
        watcher.Deleted += (_, e) => journal.Record(StagingEventKind.Deleted, e.FullPath);
        watcher.EnableRaisingEvents = true;

        for (var i = 0; i < PredeclaredOperationCount; i++)
        {
            var path = Path.Combine(_root, "probe.staging-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(path, "x");
            File.Delete(path);
        }

        // Bounded settle window for delivery of the final operations - not an assertion retry.
        await Task.Delay(500);

        var duplicateCreated = journal.DuplicateCreatedDeliveryCount;
        var duplicateDeleted = journal.DuplicateDeletedDeliveryCount;
        var duplicatesObserved = duplicateCreated > 0 || duplicateDeleted > 0;

        _output.WriteLine(
            "NATURAL DUPLICATE OBSERVATION (honest report, not a hypothesis test): " +
            "predeclaredOperationCount=" + PredeclaredOperationCount +
            " createdEvents=" + journal.CreatedCount +
            " deletedEvents=" + journal.DeletedCount +
            " uniqueCreatedPaths=" + journal.UniqueCreatedPaths.Count +
            " uniqueDeletedPaths=" + journal.UniqueDeletedPaths.Count +
            " duplicateCreatedDeliveries=" + duplicateCreated +
            " duplicateDeletedDeliveries=" + duplicateDeleted +
            " duplicatesObserved=" + duplicatesObserved +
            (duplicatesObserved
                ? " (RETAINED AS AN OBSERVED POSITIVE CONTROL)"
                : " (NONE OCCURRED ON THIS MACHINE THIS RUN - THIS DOES NOT PROVE DUPLICATES NEVER" +
                  " OCCUR, AND IT IS NOT INFERRED THAT DUPLICATES CAUSED CYCLE 53)"));

        // This test's purpose is honest observation, not a hypothesis about the platform: it must
        // pass regardless of whether duplicates occurred. The only invariant asserted is that the
        // journal never records more unique paths than operations actually performed.
        Assert.True(journal.UniqueCreatedPaths.Count <= PredeclaredOperationCount);
        Assert.True(journal.UniqueDeletedPaths.Count <= PredeclaredOperationCount);
    }

    [Fact]
    public void DeterministicDuplicateDeliveryControlStillYieldsExactlyOneFailedAttemptRegardlessOfPlatformBehaviour()
    {
        var journal = new StagingEventJournal();
        var path = Path.Combine(_root, "heartbeat.json.staging-" + Guid.NewGuid().ToString("N"));

        // Deliberately feeds DUPLICATE deliveries for the SAME path - simulating a real failed
        // attempt whose Created/Deleted notifications are each delivered twice by the platform.
        // Correctness must not depend on the platform ever doing this on demand.
        journal.Record(StagingEventKind.Created, path);
        journal.Record(StagingEventKind.Created, path);
        journal.Record(StagingEventKind.Deleted, path);
        journal.Record(StagingEventKind.Deleted, path);

        Assert.Equal(1, journal.DuplicateCreatedDeliveryCount);
        Assert.Equal(1, journal.DuplicateDeletedDeliveryCount);
        Assert.Single(journal.FailedAttemptPaths);
        Assert.Equal(path, journal.FailedAttemptPaths[0]);
    }

    // --- TASK 5: discriminating controls for the correlation logic (test-owned; two-sided; no
    // production edit; no product process). Each is capable of BOTH a pass and a fail outcome. ---

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorrelationLogicDetectsOneUniqueFailedAttempt(bool pathHasBothCreatedAndDeleted)
    {
        var journal = new StagingEventJournal();
        var path = Path.Combine(_root, "x.staging-" + Guid.NewGuid().ToString("N"));

        journal.Record(StagingEventKind.Created, path);

        if (pathHasBothCreatedAndDeleted)
        {
            journal.Record(StagingEventKind.Deleted, path);
        }

        Assert.Equal(pathHasBothCreatedAndDeleted ? 1 : 0, journal.FailedAttemptPaths.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorrelationLogicDistinguishesDuplicateDeliveryFromATrueSecondDistinctFailure(
        bool secondEventIsATrulyDistinctPath)
    {
        var journal = new StagingEventJournal();
        var pathA = Path.Combine(_root, "a.staging-" + Guid.NewGuid().ToString("N"));

        journal.Record(StagingEventKind.Created, pathA);
        journal.Record(StagingEventKind.Deleted, pathA);

        if (secondEventIsATrulyDistinctPath)
        {
            var pathB = Path.Combine(_root, "b.staging-" + Guid.NewGuid().ToString("N"));
            journal.Record(StagingEventKind.Created, pathB);
            journal.Record(StagingEventKind.Deleted, pathB);

            Assert.Equal(2, journal.FailedAttemptPaths.Count);
            Assert.NotEqual(journal.FailedAttemptPaths[0], journal.FailedAttemptPaths[1]);
        }
        else
        {
            // Duplicate delivery of the SAME already-failed path - must not count a second attempt.
            journal.Record(StagingEventKind.Created, pathA);
            journal.Record(StagingEventKind.Deleted, pathA);

            Assert.Single(journal.FailedAttemptPaths);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorrelationLogicNeverClassifiesASuccessfulStagingRenameAsFailed(bool renameSucceeds)
    {
        var journal = new StagingEventJournal();
        var path = Path.Combine(_root, "c.staging-" + Guid.NewGuid().ToString("N"));

        // A successful staging rename produces a Created event for the staging path and NO
        // corresponding Deleted event for that path (WriteAtomicJson's File.Move takes it away
        // under a different, non-staging-pattern name). Only a failed attempt's own catch deletes
        // the staging path itself.
        journal.Record(StagingEventKind.Created, path);

        if (!renameSucceeds)
        {
            journal.Record(StagingEventKind.Deleted, path);
        }

        Assert.Equal(renameSucceeds ? 0 : 1, journal.FailedAttemptPaths.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WatcherDisposalProducesNoLateCallbackAffectingTheNextTest(bool disposeBeforeFileOperation)
    {
        Directory.CreateDirectory(_root);
        var watcher = new StagingActivityWatcher(_root, "disposal-probe.json");

        try
        {
            if (disposeBeforeFileOperation)
            {
                watcher.Dispose();
            }

            var path = Path.Combine(_root, "disposal-probe.json.staging-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(path, "x");
            File.Delete(path);

            // Bounded settle window - not an assertion retry.
            await Task.Delay(300);

            if (disposeBeforeFileOperation)
            {
                Assert.Equal(0, watcher.Journal.CreatedCount);
                Assert.Equal(0, watcher.Journal.DeletedCount);
            }
            else
            {
                Assert.True(watcher.Journal.CreatedCount >= 1);
            }
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task CancellationShutsDownCleanlyAndDoesNotThrow()
    {
        var worker = new StartupProbeWorker(_paths, Heartbeat);

        await worker.StartAsync(CancellationToken.None);
        await WaitForStateAsync("running");

        var stop = worker.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stop, Task.Delay(WaitLimit));

        Assert.Same(stop, completed);
        await stop;

        worker.Dispose();

        Assert.Equal("stopped", ReadStatusState());
    }

    [Fact]
    public async Task EmittedDocumentsCarryNoIdentifyingOrPathContent()
    {
        SeedProbeRequest();

        await RunAsync(async () =>
        {
            await WaitForFileAsync(_paths.ProbeResultFile).ConfigureAwait(false);
        });

        var forbidden = new[]
        {
            Environment.MachineName,
            Environment.UserName,
            Environment.UserDomainName,
            _paths.Root,
            _paths.MetadataRoot,
            "ProgramData",
            "C:\\",
            "Exception",
            "   at ",
        };

        foreach (var file in Directory.GetFiles(_paths.Root))
        {
            var text = File.ReadAllText(file);

            foreach (var value in forbidden)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TheWorkerRejectsANullPathBindingOrNonPositiveInterval()
    {
        Assert.Throws<ArgumentNullException>(() => new StartupProbeWorker(null!, Heartbeat));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StartupProbeWorker(_paths, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StartupProbeWorker(_paths, TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public async Task OnlyTheFixedContractFilesAreEverWritten()
    {
        SeedProbeRequest();

        await RunAsync(async () =>
        {
            await WaitForFileAsync(_paths.ProbeResultFile).ConfigureAwait(false);
        });

        var written = Directory.GetFiles(_paths.Root)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                ServiceContract.HeartbeatFileName,
                ServiceContract.ProbeResultFileName,
                ServiceContract.StatusFileName,
            }.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            written);

        Assert.False(File.Exists(_paths.OwnershipLedgerFile));
        Assert.Empty(Directory.GetDirectories(_paths.Root));

        // CYCLE 63R. The metadata parent gained nothing: no ownership record was
        // created, and the runtime directory remains its only member.
        Assert.Equal(
            new[] { _paths.Root },
            Directory.GetFileSystemEntries(_paths.MetadataRoot));
    }

    private async Task RunAsync(Func<Task> whileRunning)
    {
        var worker = new StartupProbeWorker(_paths, Heartbeat);

        try
        {
            await worker.StartAsync(CancellationToken.None);
            await whileRunning();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    /// <summary>
    /// One discrete staging Created/Deleted delivery: kind, FULL staging path, UTC timestamp and a
    /// monotonic sequence number assigned at record time.
    /// </summary>
    private enum StagingEventKind
    {
        Created,
        Deleted,
    }

    private readonly record struct StagingEvent(StagingEventKind Kind, string Path, DateTime UtcTimestamp, long Sequence);

    /// <summary>
    /// Test-owned correlation logic, deliberately independent of any real
    /// <see cref="FileSystemWatcher"/> so its correctness can be proven with deterministic,
    /// directly-fed events (Task 4/5 controls) as well as with real watcher deliveries
    /// (<see cref="StagingActivityWatcher"/>). Replaces the prior discard-the-event-args counters:
    /// a FileSystemWatcher is a NOTIFICATION MECHANISM, NOT AN EXACTLY-ONCE JOURNAL, so one real
    /// failed attempt delivered twice must never be mistaken for two distinct attempts.
    /// A FAILED ATTEMPT counts ONLY when ONE unique full staging path has BOTH a Created and a
    /// Deleted event recorded. Deduplication is ORDINAL by full path. A path with only a Created
    /// event (a successful rename away) is NEVER classified as failed.
    /// </summary>
    private sealed class StagingEventJournal
    {
        private readonly object _gate = new();
        private readonly List<StagingEvent> _events = new();
        private long _sequence;

        internal void Record(StagingEventKind kind, string path)
        {
            lock (_gate)
            {
                _sequence++;
                _events.Add(new StagingEvent(kind, path, DateTime.UtcNow, _sequence));
            }
        }

        internal IReadOnlyList<StagingEvent> Snapshot()
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }

        internal int CreatedCount
        {
            get { lock (_gate) { return _events.Count(e => e.Kind == StagingEventKind.Created); } }
        }

        internal int DeletedCount
        {
            get { lock (_gate) { return _events.Count(e => e.Kind == StagingEventKind.Deleted); } }
        }

        internal IReadOnlyCollection<string> UniqueCreatedPaths
        {
            get { lock (_gate) { return UniquePaths(StagingEventKind.Created); } }
        }

        internal IReadOnlyCollection<string> UniqueDeletedPaths
        {
            get { lock (_gate) { return UniquePaths(StagingEventKind.Deleted); } }
        }

        internal int DuplicateCreatedDeliveryCount
        {
            get { lock (_gate) { return DuplicateCount(StagingEventKind.Created); } }
        }

        internal int DuplicateDeletedDeliveryCount
        {
            get { lock (_gate) { return DuplicateCount(StagingEventKind.Deleted); } }
        }

        /// <summary>
        /// Distinct full staging paths that have BOTH a Created and a Deleted event, deduplicated
        /// ordinally by path and returned in first-observed order. Never derived from raw counts.
        /// </summary>
        internal IReadOnlyList<string> FailedAttemptPaths
        {
            get
            {
                lock (_gate)
                {
                    var created = new HashSet<string>(StringComparer.Ordinal);
                    var deleted = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var e in _events)
                    {
                        if (e.Kind == StagingEventKind.Created)
                        {
                            created.Add(e.Path);
                        }
                        else
                        {
                            deleted.Add(e.Path);
                        }
                    }

                    var failed = new List<string>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var e in _events)
                    {
                        if (created.Contains(e.Path) && deleted.Contains(e.Path) && seen.Add(e.Path))
                        {
                            failed.Add(e.Path);
                        }
                    }

                    return failed;
                }
            }
        }

        private HashSet<string> UniquePaths(StagingEventKind kind) =>
            new(_events.Where(e => e.Kind == kind).Select(e => e.Path), StringComparer.Ordinal);

        private int DuplicateCount(StagingEventKind kind)
        {
            var byPath = _events.Where(e => e.Kind == kind).GroupBy(e => e.Path, StringComparer.Ordinal);
            return byPath.Sum(g => Math.Max(0, g.Count() - 1));
        }
    }

    /// <summary>
    /// Real <see cref="FileSystemWatcher"/> adapter for heartbeat-destination staging
    /// <c>*.staging-&lt;guid&gt;</c> activity. Forwards every Created/Deleted delivery's FULL path
    /// into a <see cref="StagingEventJournal"/> instead of discarding the event args - the defect
    /// this cycle exists to correct. Never polls staging-file existence directly - that window is a
    /// race because <c>WriteAtomicJson</c> deletes the staging file in its own catch the instant a
    /// replace fails.
    /// </summary>
    private sealed class StagingActivityWatcher : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly object _gate = new();
        private readonly Action? _onFirstDeleted;
        private readonly StagingEventJournal _journal = new();
        private bool _firstDeletedFired;

        /// <summary>
        /// <paramref name="onFirstDeleted"/>, when supplied, runs SYNCHRONOUSLY on the watcher's
        /// own callback thread the instant the FIRST failed-attempt cleanup is observed - used
        /// to release a test's hold with no test-side polling round-trip in between, so a second
        /// scheduled attempt can never slip in ahead of release.
        /// </summary>
        internal StagingActivityWatcher(string root, string destinationFileName, Action? onFirstDeleted = null)
        {
            _onFirstDeleted = onFirstDeleted;

            _watcher = new FileSystemWatcher(root, destinationFileName + ".staging-*")
            {
                NotifyFilter = NotifyFilters.FileName,
            };

            _watcher.Created += (_, e) => Record(StagingEventKind.Created, e.FullPath);
            _watcher.Deleted += (_, e) => Record(StagingEventKind.Deleted, e.FullPath);
            _watcher.EnableRaisingEvents = true;
        }

        internal StagingEventJournal Journal => _journal;

        internal int CreatedCount => _journal.CreatedCount;

        internal int DeletedCount => _journal.DeletedCount;

        public void Dispose() => _watcher.Dispose();

        private void Record(StagingEventKind kind, string path)
        {
            _journal.Record(kind, path);

            if (kind != StagingEventKind.Deleted)
            {
                return;
            }

            bool isFirst;

            lock (_gate)
            {
                isFirst = !_firstDeletedFired;
                _firstDeletedFired = true;
            }

            if (isFirst)
            {
                _onFirstDeleted?.Invoke();
            }
        }
    }

    private void SeedProbeRequest()
    {
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(
            _paths.ProbeRequestFile,
            "{\"schemaVersion\":1,\"probeId\":\"" + ServiceContract.StartupProbeId + "\"}");
    }

    private string ReadStatusState()
    {
        using var parsed = JsonDocument.Parse(ReadAllTextResilient(_paths.StatusFile));
        return parsed.RootElement.GetProperty("state").GetString() ?? string.Empty;
    }

    private static string ReadAllTextResilient(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private Task WaitForFileAsync(string path) => WaitForAsync(() => File.Exists(path));

    private Task WaitForStateAsync(string expected, TimeSpan? timeout = null) =>
        WaitForAsync(
            () =>
            {
                if (!File.Exists(_paths.StatusFile))
                {
                    return false;
                }

                try
                {
                    return string.Equals(ReadStatusState(), expected, StringComparison.Ordinal);
                }
                catch (Exception)
                {
                    return false;
                }
            },
            timeout);

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? WaitLimit);

        while (DateTime.UtcNow < deadline)
        {
            bool satisfied;

            try
            {
                satisfied = predicate();
            }
            catch (IOException)
            {
                satisfied = false;
            }
            catch (JsonException)
            {
                satisfied = false;
            }

            if (satisfied)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for the expected worker state.");
    }

    /// <summary>
    /// Like <see cref="WaitForAsync(Func{bool})"/> but returns the VALUE that satisfied the
    /// predicate, so callers never need an unrelated extra read after the wait succeeds.
    /// Tolerates only transient file-IO / JSON-read conditions while polling.
    /// </summary>
    private static async Task<T> WaitForValueAsync<T>(Func<T?> selector, TimeSpan? timeout = null)
        where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? WaitLimit);

        while (DateTime.UtcNow < deadline)
        {
            T? candidate;

            try
            {
                candidate = selector();
            }
            catch (IOException)
            {
                candidate = null;
            }
            catch (JsonException)
            {
                candidate = null;
            }

            if (candidate != null)
            {
                return candidate;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for the expected worker state.");
        throw new InvalidOperationException("Unreachable: Assert.Fail always throws.");
    }

    /// <summary>
    /// CYCLE 64. Bounded deadlock guard. On the passing path the awaited task is already
    /// signalled by the worker's own thread, so no time elapses here. Expiry is a HARD
    /// FAILURE, never a retry and never a tolerance.
    /// </summary>
    private static async Task AwaitDeterministicAsync(Task task, string what)
    {
        var completed = await Task.WhenAny(task, Task.Delay(DeadlockGuard));

        Assert.True(
            ReferenceEquals(completed, task),
            "DEADLOCK GUARD EXPIRED waiting for " + what +
            ". The worker did not reach the expected deterministic handshake. This is a" +
            " regression, not a timing tolerance: the guard is never approached on a passing run.");

        await task;
    }

    private static ServiceStatusDocument NewStatusDocument(string state) => new()
    {
        SchemaVersion = ServiceContract.SchemaVersion,
        State = state,
        TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
        SessionId = 0,
        UserInteractive = false,
        ServiceVersion = ServiceContract.ServiceVersion,
    };

    private static ServiceHeartbeatDocument NewHeartbeatDocument() => new()
    {
        SchemaVersion = ServiceContract.SchemaVersion,
        TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
        SessionId = 0,
        UserInteractive = false,
        ServiceVersion = ServiceContract.ServiceVersion,
    };

    private static StartupProbeResultDocument NewProbeResultDocument() => new()
    {
        SchemaVersion = ServiceContract.SchemaVersion,
        ProbeId = ServiceContract.StartupProbeId,
        Outcome = "completed",
        ObservedUtc = DateTimeOffset.UtcNow.ToString("O"),
        SessionId = 0,
        UserInteractive = false,
        ServiceVersion = ServiceContract.ServiceVersion,
    };

    /// <summary>
    /// CYCLE 64. Binds the worker to the manual scheduler and the recording writer so a test
    /// drives it with no real clock and no real filesystem.
    /// </summary>
    private sealed class DeterministicWorkerHarness : IAsyncDisposable
    {
        // CYCLE 77. The harness now ALWAYS binds a recording lifetime, so a shutdown
        // assertion can never be vacuous: `RequestedHostShutdown` alone would still be
        // true if `StopApplication()` were never called, and every shutdown test here
        // therefore asserts the RECORDED CALL as well as the flag.
        internal DeterministicWorkerHarness(params int[] failingHeartbeatAttemptOrdinals)
        {
            Writer = new RecordingRuntimeDocumentWriter(failingHeartbeatAttemptOrdinals);
            Scheduler = new ManualHeartbeatScheduler();
            Lifetime = new RecordingHostLifetime
            {
                // ORDER PROOF, not two independent facts: the lifetime records the last
                // DURABLE status state at the instant shutdown is requested.
                ObserveStatusOnStop = () => Writer.LastStatusState,
            };
            Worker = new StartupProbeWorker(Writer, Scheduler, Lifetime);
        }

        internal RecordingRuntimeDocumentWriter Writer { get; }

        internal ManualHeartbeatScheduler Scheduler { get; }

        internal RecordingHostLifetime Lifetime { get; }

        internal StartupProbeWorker Worker { get; }

        internal Task StartAsync() => Worker.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
        }
    }

    /// <summary>
    /// CYCLE 64. The manual scheduler. It NEVER sleeps and NEVER reads a clock. A wait
    /// completes only when the test releases exactly one heartbeat, or when the worker's
    /// token is cancelled - both of which are synchronous, observable events.
    /// </summary>
    private sealed class ManualHeartbeatScheduler : IHeartbeatScheduler
    {
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly object _gate = new();
        private TaskCompletionSource<bool>? _pending;
        private int _waitsStarted;
        private int _waitsCompleted;
        private int _waitsCanceled;

        internal int WaitsStarted { get { lock (_gate) { return _waitsStarted; } } }

        internal int WaitsCompleted { get { lock (_gate) { return _waitsCompleted; } } }

        internal int WaitsCanceled { get { lock (_gate) { return _waitsCanceled; } } }

        public async Task WaitForNextHeartbeatAsync(CancellationToken cancellationToken)
        {
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate)
            {
                _waitsStarted++;
                _pending = pending;
            }

            _arrived.Release();

            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), pending);

            try
            {
                await pending.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _waitsCanceled++;
                    _pending = null;
                }

                throw;
            }

            lock (_gate)
            {
                _waitsCompleted++;
                _pending = null;
            }
        }

        /// <summary>Completes the instant the worker parks on its next heartbeat wait.</summary>
        internal Task WaitUntilWorkerIsWaitingAsync() => _arrived.WaitAsync();

        /// <summary>Releases EXACTLY ONE heartbeat. Refuses when nothing is pending.</summary>
        internal void ReleaseOneHeartbeat()
        {
            TaskCompletionSource<bool>? pending;

            lock (_gate)
            {
                pending = _pending;
            }

            if (pending is null)
            {
                throw new InvalidOperationException(
                    "No heartbeat wait is pending, so no heartbeat can be released.");
            }

            pending.TrySetResult(true);
        }
    }

    /// <summary>One heartbeat write attempt: its order, its unique identity, and its outcome.</summary>
    private readonly record struct HeartbeatAttempt(int Ordinal, string AttemptId, bool Succeeded);

    /// <summary>
    /// CYCLE 64. The recording writer. It touches NO filesystem at all, assigns a unique
    /// attempt identity INSIDE each write call - never from a notification channel - and
    /// injects success or a recognised IOException per attempt ordinal.
    ///
    /// The attempt record is the bounded test-side stand-in for staging identity. The
    /// production writer never exposes one.
    /// </summary>
    private sealed class RecordingRuntimeDocumentWriter : IRuntimeDocumentWriter
    {
        private readonly HashSet<int> _failingHeartbeatOrdinals;
        private readonly List<HeartbeatAttempt> _heartbeatAttempts = new();
        private readonly List<string> _statusStates = new();
        private readonly List<string> _attemptedStatusStates = new();
        private readonly List<ServiceStatusDocument> _statusDocuments = new();
        private readonly object _gate = new();
        private int _heartbeatInvocationCount;
        private int _statusWriteFailureCount;
        private bool _failStatusWrites;
        private bool _probeResultExists;
        private bool _probeRequestExists;

        internal RecordingRuntimeDocumentWriter(params int[] failingHeartbeatAttemptOrdinals)
        {
            _failingHeartbeatOrdinals = new HashSet<int>(failingHeartbeatAttemptOrdinals);
        }

        internal IReadOnlyList<HeartbeatAttempt> HeartbeatAttempts
        {
            get { lock (_gate) { return _heartbeatAttempts.ToArray(); } }
        }

        internal int HeartbeatInvocationCount
        {
            get { lock (_gate) { return _heartbeatInvocationCount; } }
        }

        internal string LastStatusState
        {
            get { lock (_gate) { return _statusStates.Count == 0 ? string.Empty : _statusStates[^1]; } }
        }

        /// <summary>Every state whose write SUCCEEDED, in order.</summary>
        internal IReadOnlyList<string> DurableStatusStates
        {
            get { lock (_gate) { return _statusStates.ToArray(); } }
        }

        /// <summary>Every state the worker ATTEMPTED to write, including failed attempts.</summary>
        internal IReadOnlyList<string> AttemptedStatusStates
        {
            get { lock (_gate) { return _attemptedStatusStates.ToArray(); } }
        }

        /// <summary>Every status document whose write succeeded, for emission-surface assertions.</summary>
        internal IReadOnlyList<ServiceStatusDocument> DurableStatusDocuments
        {
            get { lock (_gate) { return _statusDocuments.ToArray(); } }
        }

        internal int StatusWriteFailureCount
        {
            get { lock (_gate) { return _statusWriteFailureCount; } }
        }

        /// <summary>
        /// CYCLE 77. Makes every LATER status write fail deterministically. A test flips
        /// this while the worker is parked on the scheduler, so the terminal write - and
        /// only the terminal write - throws.
        /// </summary>
        internal void FailSubsequentStatusWrites()
        {
            lock (_gate) { _failStatusWrites = true; }
        }

        internal void SeedProbeRequest()
        {
            lock (_gate) { _probeRequestExists = true; }
        }

        public bool RuntimeRootExists() => true;

        public bool ProbeResultExists()
        {
            lock (_gate) { return _probeResultExists; }
        }

        public bool ProbeRequestExists()
        {
            lock (_gate) { return _probeRequestExists; }
        }

        public void WriteStatus(ServiceStatusDocument document)
        {
            lock (_gate)
            {
                _attemptedStatusStates.Add(document.State);

                if (_failStatusWrites)
                {
                    _statusWriteFailureCount++;
                }
                else
                {
                    _statusStates.Add(document.State);
                    _statusDocuments.Add(document);
                }
            }

            bool shouldThrow;
            lock (_gate) { shouldThrow = _failStatusWrites; }

            if (shouldThrow)
            {
                throw new IOException(
                    "CYCLE 77 injected status write failure for state " + document.State + ".");
            }
        }

        public void WriteHeartbeat(ServiceHeartbeatDocument document)
        {
            int ordinal;

            lock (_gate)
            {
                _heartbeatInvocationCount++;
                ordinal = _heartbeatInvocationCount;
            }

            // Identity is assigned HERE, once per invocation. There is no external delivery
            // that could repeat it and no way to observe one attempt twice.
            var attemptId = Guid.NewGuid().ToString("N");
            var succeeded = !_failingHeartbeatOrdinals.Contains(ordinal);

            lock (_gate)
            {
                _heartbeatAttempts.Add(new HeartbeatAttempt(ordinal, attemptId, succeeded));
            }

            if (!succeeded)
            {
                throw new IOException("CYCLE 64 injected heartbeat write failure for attempt " + ordinal + ".");
            }
        }

        public void WriteProbeResult(StartupProbeResultDocument document)
        {
            lock (_gate) { _probeResultExists = true; }
        }

        public void RemoveProbeRequest()
        {
            lock (_gate) { _probeRequestExists = false; }
        }
    }

    /// <summary>
    /// CYCLE 63R. Records a bounded host-shutdown request. It grants no
    /// capability: the only thing the worker may do with it is ask the host to
    /// stop, and this records that it asked.
    /// </summary>
    private sealed class RecordingHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        internal bool StopRequested { get; private set; }

        /// <summary>How many times the worker actually called <see cref="StopApplication"/>.</summary>
        internal int StopRequestCount { get; private set; }

        /// <summary>
        /// CYCLE 77. Optional observer sampled AT THE INSTANT shutdown is requested. It is
        /// how the tests prove ORDER rather than merely proving two facts both happened.
        /// Null for the runtime-root tests, which bind the real production writer.
        /// </summary>
        internal Func<string>? ObserveStatusOnStop { get; init; }

        /// <summary>The durable status state observed when shutdown was requested.</summary>
        internal string ObservedStatusStateAtStopRequest { get; private set; } = string.Empty;

        public void StopApplication()
        {
            StopRequested = true;
            StopRequestCount++;
            ObservedStatusStateAtStopRequest = ObserveStatusOnStop?.Invoke() ?? string.Empty;
        }
    }
}
