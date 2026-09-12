using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace PAXCookbook.Service;

/// <summary>
/// Minimal UI-free background worker. It writes bounded status and liveness
/// documents beneath the RUNTIME root, runs one fixed synthetic startup probe at
/// most once per runtime root, and shuts down cleanly. It executes no external
/// code, opens no network or certificate handle, and reads no user-scoped state.
///
/// CYCLE 63R - THE RUNTIME ROOT IS FAIL-CLOSED. The elevated helper creates and
/// protects the runtime directory before the service is ever started. The
/// service account holds Traverse only on the metadata parent, so it CANNOT
/// recreate the runtime directory - and this worker deliberately never tries.
/// A missing or unwritable runtime root requests a bounded host shutdown and
/// emits no "running" status, rather than broadening an ACL, recreating a
/// directory it does not own, or crash-looping.
///
/// CYCLE 64 - TWO CLOSED SEAMS, NO BEHAVIOUR CHANGE. Waiting and document writing
/// now go through <see cref="IHeartbeatScheduler"/> and
/// <see cref="IRuntimeDocumentWriter"/>. In production those bind the real clock
/// and the real atomic filesystem writer, so the shipped behaviour is identical:
/// one beat per configured interval, ONE tolerated consecutive write failure, a
/// success resets the count, the SECOND consecutive failure is terminal, and
/// cancellation stops further writes. Neither seam accepts a path, an interval, a
/// callback or a configuration value from a caller.
/// </summary>
internal sealed class StartupProbeWorker : BackgroundService
{
    private readonly IRuntimeDocumentWriter _writer;
    private readonly IHeartbeatScheduler _scheduler;
    private readonly IHostApplicationLifetime? _lifetime;

    /// <summary>
    /// The production entry point. The roots are already resolved by the composition
    /// root; the worker never resolves one itself. The lifetime is optional ONLY so
    /// focused tests can drive the worker directly; production always supplies it.
    /// This overload binds the REAL clock and the REAL atomic filesystem writer.
    /// </summary>
    internal StartupProbeWorker(
        ServicePaths paths, TimeSpan heartbeatInterval, IHostApplicationLifetime? lifetime = null)
        : this(BindProductionWriter(paths), BindProductionScheduler(heartbeatInterval), lifetime)
    {
    }

    /// <summary>
    /// The seam-bound constructor, so focused tests can drive the worker with a
    /// manual scheduler and a recording writer - no real clock, no real filesystem.
    /// It grants no new capability: both parameters are closed internal interfaces
    /// whose operations are fixed and purpose-specific.
    /// </summary>
    internal StartupProbeWorker(
        IRuntimeDocumentWriter writer,
        IHeartbeatScheduler scheduler,
        IHostApplicationLifetime? lifetime = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _lifetime = lifetime;
    }

    /// <summary>
    /// The bounded runtime-root verdict of the most recent start. It carries no
    /// path, no identity and no native status.
    /// </summary>
    internal ServiceRuntimeRootState LastRuntimeRootState { get; private set; } =
        ServiceRuntimeRootState.Unspecified;

    /// <summary>
    /// True when this start requested a bounded host shutdown - either because the runtime
    /// root was not usable so the worker never ran, or because a run that had started ended
    /// in a terminal failure. <see cref="LastRuntimeRootState"/> separates the two without a
    /// second property: <c>Ready</c> is the terminal-failure case, and <c>Missing</c> or
    /// <c>Unwritable</c> is the startup case.
    /// </summary>
    internal bool RequestedHostShutdown { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var terminalState = ServiceStateCode.Stopped;

        try
        {
            // ---- FAIL-CLOSED RUNTIME ROOT, BEFORE ANY "running" CLAIM -------
            //
            // The first status write IS the writability probe, so no extra
            // artifact is ever placed in the closed runtime namespace.
            LastRuntimeRootState = StartInsideExistingRuntimeRoot();
            if (LastRuntimeRootState != ServiceRuntimeRootState.Ready)
            {
                RequestedHostShutdown = true;
                _lifetime?.StopApplication();
                return;
            }

            WriteHeartbeat();
            RunStartupProbeAtMostOnce();
            WriteStatus(ServiceStateCode.Running);

            var strikes = HeartbeatStrikeState.None;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    WriteHeartbeat();
                    strikes = HeartbeatStrikeRule.AfterSuccess(strikes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // First consecutive failure: tolerate one missed beat. A second consecutive
                    // failure rethrows to the existing outer handler -> terminal "failed".
                    strikes = HeartbeatStrikeRule.AfterFailure(strikes);

                    if (strikes.IsTerminal)
                    {
                        throw;
                    }
                }

                try
                {
                    await _scheduler.WaitForNextHeartbeatAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cooperative shutdown. Never propagates out of the worker.
        }
        catch (Exception)
        {
            // Bounded failure state only. Exception text is never persisted.
            terminalState = ServiceStateCode.Failed;
        }

        TryWriteStatus(terminalState);

        // CYCLE 77 - A TERMINAL FAILURE MUST ALSO STOP THE HOST. Returning here left the
        // BackgroundService completed while host.Run() and the SCM both stayed Running, so
        // the SCM reported health that no longer existed. This request sits AFTER and
        // OUTSIDE TryWriteStatus, which already swallows every exception, so a FAILED
        // terminal status write still shuts the host down. Cancellation leaves terminalState
        // Stopped and therefore requests nothing.
        if (terminalState == ServiceStateCode.Failed)
        {
            RequestedHostShutdown = true;
            _lifetime?.StopApplication();
        }
    }

    private static IRuntimeDocumentWriter BindProductionWriter(ServicePaths paths) =>
        new AtomicRuntimeDocumentWriter(paths ?? throw new ArgumentNullException(nameof(paths)));

    private static IHeartbeatScheduler BindProductionScheduler(TimeSpan heartbeatInterval)
    {
        if (heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        }

        return new FixedIntervalHeartbeatScheduler(heartbeatInterval);
    }

    /// <summary>
    /// The whole fail-closed startup precondition. The runtime root must already
    /// exist - this worker never creates it, because creating it would mean
    /// writing the metadata parent it is deliberately not permitted to write.
    /// </summary>
    private ServiceRuntimeRootState StartInsideExistingRuntimeRoot()
    {
        if (!_writer.RuntimeRootExists())
        {
            return ServiceRuntimeRootState.Missing;
        }

        try
        {
            WriteStatus(ServiceStateCode.Starting);
            return ServiceRuntimeRootState.Ready;
        }
        catch (Exception)
        {
            return ServiceRuntimeRootState.Unwritable;
        }
    }

    /// <summary>
    /// Consumes the fixed probe request exactly once. Replay refusal is durable:
    /// it is decided from the on-disk result document, so a later host instance
    /// refuses too.
    /// </summary>
    private void RunStartupProbeAtMostOnce()
    {
        if (_writer.ProbeResultExists())
        {
            return;
        }

        if (!_writer.ProbeRequestExists())
        {
            return;
        }

        var outcome = ProbeOutcomeCode.Completed;
        var observedUtc = DateTimeOffset.UtcNow;
        var sessionId = CurrentSessionId();
        var userInteractive = Environment.UserInteractive;

        var document = new StartupProbeResultDocument
        {
            SchemaVersion = ServiceContract.SchemaVersion,
            ProbeId = ServiceContract.StartupProbeId,
            Outcome = ServiceContract.ToWireValue(outcome),
            ObservedUtc = observedUtc.ToString("O"),
            SessionId = sessionId,
            UserInteractive = userInteractive,
            ServiceVersion = ServiceContract.ServiceVersion,
        };

        // Result first, then request removal: a crash between the two leaves a
        // durable result, and the next start refuses the replay.
        _writer.WriteProbeResult(document);
        _writer.RemoveProbeRequest();
    }

    private void WriteStatus(ServiceStateCode state)
    {
        var document = new ServiceStatusDocument
        {
            SchemaVersion = ServiceContract.SchemaVersion,
            State = ServiceContract.ToWireValue(state),
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            SessionId = CurrentSessionId(),
            UserInteractive = Environment.UserInteractive,
            ServiceVersion = ServiceContract.ServiceVersion,
        };

        _writer.WriteStatus(document);
    }

    private void TryWriteStatus(ServiceStateCode state)
    {
        try
        {
            // No directory is created here either: a runtime root that vanished
            // mid-run is a fail-closed condition, not something to recreate.
            if (_writer.RuntimeRootExists())
            {
                WriteStatus(state);
            }
        }
        catch (Exception)
        {
            // A terminal write failure must not escape shutdown.
        }
    }

    private void WriteHeartbeat()
    {
        var document = new ServiceHeartbeatDocument
        {
            SchemaVersion = ServiceContract.SchemaVersion,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            SessionId = CurrentSessionId(),
            UserInteractive = Environment.UserInteractive,
            ServiceVersion = ServiceContract.ServiceVersion,
        };

        _writer.WriteHeartbeat(document);
    }

    private static int CurrentSessionId()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }
}
