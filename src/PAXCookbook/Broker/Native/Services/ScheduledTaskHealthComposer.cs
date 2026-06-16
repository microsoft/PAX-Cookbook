using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3g -- pure-function V1.S07 health priority composer.
// Faithful port of New-ScheduledTaskHealthObject from
// app\broker\Routes\ScheduledTasks.ps1. Performs NO SQL, NO IO, NO
// PAX invocation. The route handler gathers inputs and then calls
// Compose.
//
// Stage 3g honesty:
//   * The native broker cannot recompute the projection hash yet
//     (the projection-hash composer lands in Stage 3h). Routes call
//     Compose with hashRecomputed:false and currentHash:null --
//     which structurally makes the "stale" branch unreachable, so
//     the chain progresses to the cooks-table branches normally and,
//     in the all-clear case, terminates at "unknown" with the
//     verbatim PS broker message:
//        "Schedule registered, but staleness could not be determined.
//         Check the recipe and re-save the schedule if needed."
//     That is the documented Stage 3g behaviour. Routes additionally
//     surface a top-level staleReason='projection_hash_unavailable_
//     in_native_broker' field so consumers do not interpret the
//     "unknown" status as a registrar fault.
public static class ScheduledTaskHealthComposer
{
    public const string StatusNotRegistered    = "not_registered";
    public const string StatusStale            = "stale";
    public const string StatusLastRunRefused   = "last_run_refused";
    public const string StatusLastRunFailed    = "last_run_failed";
    public const string StatusLastRunInterrupted = "last_run_interrupted";
    public const string StatusLastRunRunning   = "last_run_running";
    public const string StatusCurrent          = "current";
    public const string StatusUnknown          = "unknown";

    public const string MessageNotRegistered =
        "Not registered with Windows Task Scheduler.";
    public const string MessageStale =
        "Schedule is stale: the saved recipe or PAX engine has changed since registration. Re-save the schedule to refresh the projection.";
    public const string MessageLastRunRefused =
        "Last scheduled run refused: recipe changed since registration. Update / re-register the scheduled task.";
    public const string MessageLastRunFailedPaxNonzeroExit =
        "Last scheduled run failed in PAX. Open the run and inspect the PAX log for the exit code and reason.";
    public const string MessageLastRunFailedWrapperSpawn =
        "Last scheduled run failed: the wrapper could not spawn PAX. Open the run and inspect the wrapper envelope.";
    public const string MessageLastRunFailedWrapperInternal =
        "Last scheduled run failed: wrapper internal error. Open the run and inspect the wrapper envelope.";
    public const string MessageLastRunFailedGeneric =
        "Last scheduled run failed. Open the run and inspect the PAX log.";
    public const string MessageLastRunInterruptedOrphan =
        "Last scheduled run was orphan-classified after the grace window. Inspect the wrapper folder and Task Scheduler history.";
    public const string MessageLastRunInterruptedGeneric =
        "Last scheduled run was interrupted. Inspect the wrapper folder and Task Scheduler history.";
    public const string MessageLastRunRunning =
        "A scheduled cook is currently running.";
    public const string MessageCurrentWithCompleted =
        "Schedule is current. Last scheduled run completed.";
    public const string MessageCurrentNoRuns =
        "Schedule is current. No scheduled runs have completed yet.";
    public const string MessageUnknown =
        "Schedule registered, but staleness could not be determined. Check the recipe and re-save the schedule if needed.";

    public static ScheduledTaskHealth Compose(
        ScheduledTaskRow? taskRow,
        string? currentHash,
        bool   hashRecomputed,
        string? staleCheckedAt,
        ScheduledTaskTerminalCook? lastTerminal,
        bool   hasRunning)
    {
        if (taskRow is null)
        {
            return new ScheduledTaskHealth(
                Status:                   StatusNotRegistered,
                Stale:                    false,
                ProjectionHashCurrent:    null,
                ProjectionHashRegistered: null,
                StaleProjectionCheckedAt: null,
                LastImportedCookId:       null,
                LastImportedAt:           null,
                LastTerminalCookId:       null,
                LastTerminalStatus:       null,
                LastTerminalErrorClass:   null,
                LastTerminalAt:           null,
                Message:                  MessageNotRegistered);
        }

        var registeredHash = taskRow.RecipeProjectionHash;
        var stale = hashRecomputed
            && !string.IsNullOrEmpty(currentHash)
            && !string.Equals(currentHash, registeredHash, StringComparison.Ordinal);

        string? lastTerminalCookId     = lastTerminal?.CookId;
        string? lastTerminalStatus     = lastTerminal?.Status;
        string? lastTerminalErrorClass = lastTerminal?.ErrorClass;
        string? lastTerminalAt         = null;
        if (lastTerminal is not null)
        {
            if (!string.IsNullOrEmpty(lastTerminal.FinishedAt))
                lastTerminalAt = lastTerminal.FinishedAt;
            else if (!string.IsNullOrEmpty(lastTerminal.StartedAt))
                lastTerminalAt = lastTerminal.StartedAt;
        }

        string status;
        string message;

        if (stale)
        {
            status  = StatusStale;
            message = MessageStale;
        }
        else if (lastTerminal is not null
                 && (string.Equals(lastTerminalStatus, "refused", StringComparison.Ordinal)
                     || string.Equals(lastTerminalErrorClass, "refused_stale_projection", StringComparison.Ordinal)))
        {
            status  = StatusLastRunRefused;
            message = MessageLastRunRefused;
        }
        else if (lastTerminal is not null
                 && string.Equals(lastTerminalStatus, "failed", StringComparison.Ordinal))
        {
            status = StatusLastRunFailed;
            message = lastTerminalErrorClass switch
            {
                "pax_nonzero_exit"       => MessageLastRunFailedPaxNonzeroExit,
                "wrapper_spawn_failed"   => MessageLastRunFailedWrapperSpawn,
                "wrapper_internal_error" => MessageLastRunFailedWrapperInternal,
                _                        => MessageLastRunFailedGeneric,
            };
        }
        else if (lastTerminal is not null
                 && string.Equals(lastTerminalStatus, "interrupted", StringComparison.Ordinal))
        {
            status  = StatusLastRunInterrupted;
            message = string.Equals(lastTerminalErrorClass, "wrapper_orphan_classified", StringComparison.Ordinal)
                ? MessageLastRunInterruptedOrphan
                : MessageLastRunInterruptedGeneric;
        }
        else if (hasRunning)
        {
            status  = StatusLastRunRunning;
            message = MessageLastRunRunning;
        }
        else if (hashRecomputed
                 && !string.IsNullOrEmpty(currentHash)
                 && string.Equals(currentHash, registeredHash, StringComparison.Ordinal))
        {
            status  = StatusCurrent;
            message = (lastTerminal is not null
                       && string.Equals(lastTerminalStatus, "completed", StringComparison.Ordinal))
                ? MessageCurrentWithCompleted
                : MessageCurrentNoRuns;
        }
        else
        {
            status  = StatusUnknown;
            message = MessageUnknown;
        }

        return new ScheduledTaskHealth(
            Status:                   status,
            Stale:                    stale,
            ProjectionHashCurrent:    currentHash,
            ProjectionHashRegistered: registeredHash,
            StaleProjectionCheckedAt: staleCheckedAt,
            LastImportedCookId:       taskRow.LastImportedCookId,
            LastImportedAt:           taskRow.LastImportedAt,
            LastTerminalCookId:       lastTerminalCookId,
            LastTerminalStatus:       lastTerminalStatus,
            LastTerminalErrorClass:   lastTerminalErrorClass,
            LastTerminalAt:           lastTerminalAt,
            Message:                  message);
    }
}
