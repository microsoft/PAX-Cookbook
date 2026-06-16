namespace PAXCookbook.Ipc;

// Wire types and contract constants for the per-user named pipe IPC,
// per paxcookbook-ipc-contract.md §6-§8.

public sealed record IpcRequest(string Verb, string Id, string Ts);

public sealed record IpcResponse(string Id, bool Ok, string Code, string? Detail);

public static class IpcResponseCodes
{
    public const string Ok = "ok";
    public const string UnknownVerb = "unknown-verb";
    public const string BadFrame = "bad-frame";
    public const string BadJson = "bad-json";
    public const string LengthExceeded = "length-exceeded";
    public const string VerbFailed = "verb-failed";
    public const string Busy = "busy";
}

public static class IpcAllowlist
{
    // Verbs the primary will service. Anything else => unknown-verb.
    // Phase 7 ships open/reopen/stop/status-query; support is deferred
    // (no primary-side handler yet).
    public const string Open = "open";
    public const string Reopen = "reopen";
    public const string Stop = "stop";
    public const string StatusQuery = "status-query";

    public static readonly IReadOnlySet<string> Verbs =
        new HashSet<string>(StringComparer.Ordinal) { Open, Reopen, Stop, StatusQuery };
}

// Server-side handler. Returns the response code/detail for the verb.
public interface IIpcVerbHandler
{
    IpcResponse Handle(IpcRequest request);
}

// One-shot duplex IPC server (start/stop owned by the primary process).
public interface IIpcServer : IDisposable
{
    string EndpointName { get; }
    void Start(IIpcVerbHandler handler);
    void Stop();
}

// One-shot client used by a secondary invocation to forward a single verb.
public interface IIpcClient
{
    IpcClientForwardResult Forward(string endpointName, string verb, TimeSpan timeout);
}

public enum IpcClientOutcome
{
    Accepted,
    NoPrimary,
    BadResponse,
    Timeout,
    VerbFailed
}

public sealed record IpcClientForwardResult(IpcClientOutcome Outcome, IpcResponse? Response, string? FailureDetail);
