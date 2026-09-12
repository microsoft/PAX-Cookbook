using System.Text.Json;
using System.Text.Json.Serialization;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.Service;

/// <summary>
/// Closed, fixed service contract. Every value here is a compile-time constant.
/// Nothing on this type may be supplied, overridden, or redirected by a caller,
/// an environment variable, a configuration file, a command-line argument, or a
/// machine configuration key.
/// </summary>
internal static class ServiceContract
{
    internal const int SchemaVersion = 1;

    // DERIVED, not retyped. The linked ServiceIdentityContract is the single
    // source of truth for both fixed names, so Setup and the service can never
    // drift into two spellings. A const may reference another const, so these
    // stay compile-time constants and the shipped values and response bytes are
    // byte-for-byte unchanged.
    internal const string ServiceName = ServiceIdentityContract.ServiceName;
    internal const string ServiceDisplayName = ServiceIdentityContract.ServiceDisplayName;
    internal const string ServiceVersion = "1.0.0";

    // DERIVED, not retyped (cycle 51), from the fifth compile-linked Shared
    // contract. Same reasoning as ServiceName/ServiceDisplayName above: a const
    // may reference another const, so these stay compile-time constants and the
    // shipped values are byte-for-byte unchanged.
    internal const string MachineRootFolderName = ServiceMachineStorageContract.MachineRootFolderName;
    internal const string MachineDataFolderName = ServiceMachineStorageContract.ServiceDataFolderName;

    /// <summary>
    /// CYCLE 63R. The fixed writable RUNTIME child of the metadata folder.
    /// DERIVED, not retyped, for the same reason as the two names above.
    /// </summary>
    internal const string RuntimeDataFolderName = ServiceMachineStorageContract.RuntimeDataFolderName;

    internal const string StatusFileName = "service-status.json";
    internal const string HeartbeatFileName = "service-heartbeat.json";
    internal const string ProbeRequestFileName = "startup-probe-request.json";
    internal const string ProbeResultFileName = "startup-probe-result.json";
    internal const string OwnershipLedgerFileName = ServiceMachineStorageContract.OwnershipLedgerFileName;

    internal const string StartupProbeId = "startup-session0-probe";

    internal static readonly IReadOnlyList<string> FixedLeafFileNames = new[]
    {
        StatusFileName,
        HeartbeatFileName,
        ProbeRequestFileName,
        ProbeResultFileName,
        OwnershipLedgerFileName,
    };

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static string ToWireValue(ServiceStateCode state) => state switch
    {
        ServiceStateCode.Starting => "starting",
        ServiceStateCode.Running => "running",
        ServiceStateCode.Stopped => "stopped",
        ServiceStateCode.Failed => "failed",
        _ => "failed",
    };

    internal static string ToWireValue(ProbeOutcomeCode outcome) => outcome switch
    {
        ProbeOutcomeCode.Completed => "completed",
        ProbeOutcomeCode.Failed => "failed",
        _ => "failed",
    };
}

/// <summary>Closed set of service lifecycle states. No free-form text is ever emitted.</summary>
internal enum ServiceStateCode
{
    Starting = 0,
    Running = 1,
    Stopped = 2,
    Failed = 3,
}

/// <summary>Closed set of startup-probe outcomes. Failures map here, never to exception text.</summary>
internal enum ProbeOutcomeCode
{
    Completed = 0,
    Failed = 1,
}

/// <summary>
/// CYCLE 63R. Closed set of runtime-root startup verdicts. Zero is the
/// permanent, safe default so an uninitialised value can never read as ready.
/// </summary>
internal enum ServiceRuntimeRootState
{
    Unspecified = 0,

    /// <summary>The runtime root exists and this host proved it writable.</summary>
    Ready = 1,

    /// <summary>The runtime root is absent. It is NEVER created by this service.</summary>
    Missing = 2,

    /// <summary>The runtime root exists but this host could not write it.</summary>
    Unwritable = 3,
}

/// <summary>
/// Status document. The property set is the whole permitted emission surface:
/// schema version, bounded state, UTC timestamp, session id, interactivity flag,
/// service version. No machine name, user name, SID, directory, or message.
/// </summary>
internal sealed class ServiceStatusDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("timestampUtc")]
    public string TimestampUtc { get; init; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public int SessionId { get; init; }

    [JsonPropertyName("userInteractive")]
    public bool UserInteractive { get; init; }

    [JsonPropertyName("serviceVersion")]
    public string ServiceVersion { get; init; } = string.Empty;
}

/// <summary>Liveness document. Distinct from status: proves the host is still running.</summary>
internal sealed class ServiceHeartbeatDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("timestampUtc")]
    public string TimestampUtc { get; init; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public int SessionId { get; init; }

    [JsonPropertyName("userInteractive")]
    public bool UserInteractive { get; init; }

    [JsonPropertyName("serviceVersion")]
    public string ServiceVersion { get; init; } = string.Empty;
}

/// <summary>Result of the single fixed synthetic startup probe.</summary>
internal sealed class StartupProbeResultDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("probeId")]
    public string ProbeId { get; init; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    [JsonPropertyName("observedUtc")]
    public string ObservedUtc { get; init; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public int SessionId { get; init; }

    [JsonPropertyName("userInteractive")]
    public bool UserInteractive { get; init; }

    [JsonPropertyName("serviceVersion")]
    public string ServiceVersion { get; init; } = string.Empty;
}
