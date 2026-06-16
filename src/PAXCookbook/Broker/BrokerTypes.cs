namespace PAXCookbook.Broker;

public sealed record BrokerStatus(
    bool Running,
    int? Pid,
    int? Port,
    string? Url,
    string? LockFile,
    string Source); // "install-state" | "workspace-lock" | "process-probe" | "none"

public sealed record BrokerStartOptions(
    string WorkspaceFolderPath,
    string BrokerStartScript,
    TimeSpan ReadyTimeout);

public sealed record BrokerStopOptions(
    int BrokerPid,
    TimeSpan ExitTimeout);

public enum BrokerStartOutcome
{
    Started,
    AlreadyRunning,
    Failed
}

public sealed record BrokerStartResult(BrokerStartOutcome Outcome, BrokerStatus Status, string? FailureDetail);

public enum BrokerStopOutcome
{
    Stopped,
    AlreadyStopped,
    Failed
}

public sealed record BrokerStopResult(BrokerStopOutcome Outcome, string? FailureDetail);
