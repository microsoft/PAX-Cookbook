// PAX Cookbook - CLOSED RUNTIME DOCUMENT VERIFIER (cycle 63R, helper only)
//
// WHAT THIS FILE IS. The ONE strict reader of the two documents the running
// service publishes into its runtime root: the status document and the heartbeat
// document. It is a VERIFIER, not a parser of convenience - every rule below is
// a refusal, and there is no lenient path.
//
// THE CLOSED RULES, applied in this order:
//   1. The file must be a regular file whose length is within a hard bound,
//      checked BEFORE a single byte is buffered.
//   2. The bytes must decode as STRICT UTF-8. An invalid sequence is a refusal,
//      never a replacement character.
//   3. The JSON must be a single object whose property set is EXACTLY the closed
//      set for that document - no unknown property, and no duplicate property.
//   4. schemaVersion and serviceVersion must equal the exact expected values.
//   5. timestampUtc must be an RFC 3339 timestamp with a ZERO UTC offset.
//   6. STRUCTURE: for the status document, state must be a JSON string. What the
//      string SAYS is not a structural question and is not decided here.
//   7. EXECUTION CONTEXT: sessionId must be exactly 0 and userInteractive must
//      be exactly false.
//   8. LIFECYCLE: only then is the status document's state string classified.
//
// CYCLE 75 - A LIFECYCLE STATE IS NOT AN EXECUTION-CONTEXT FAILURE. Before this
// cycle EVERY state that was not "running" - including the perfectly healthy
// "starting" the service writes before it finishes coming up - was reported as
// NotRunningInSessionZero, which reads as a SESSION/IDENTITY problem. A caller
// could not tell a service that had not finished starting from a service running
// in the wrong session. The three orderings above are now DISTINCT and their
// relative rank is fixed: structure outranks execution context, and execution
// context outranks lifecycle. A wrong session or an interactive host is STILL
// reported ahead of any lifecycle state, so nothing about that check weakened.
//
// LIVENESS IS NEVER INFERRED FROM A FILE TIMESTAMP. This file never reads
// LastWriteTime, CreationTime or any other filesystem metadata. A file's mtime
// says when something touched the file, not that the service is alive; only two
// SELF-REPORTED timestamps that genuinely ADVANCE prove a live host.
//
// THE WIRE NAMES ARE DECLARED HERE, AND THAT IS A DISCLOSED DUPLICATION. The
// service's own ServiceContract is internal to PAXCookbook.Service and the
// service's linked-contract set is pinned at exactly five files, so this
// verifier cannot derive the names from the producer. The focused helper tests
// pin these literals against the service source text so drift is caught.
//
// PRIVACY - FAIL CLOSED. Every answer is a bounded token. No path, no document
// content, no timestamp and no exception is ever returned.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// The fixed wire facts of the two runtime documents. Compile-time constants
/// only; nothing here is configurable or caller-supplied.
/// </summary>
internal static class ServiceRuntimeDocumentContract
{
    internal const string RuntimeFolderName = "Runtime";
    internal const string StatusFileName = "service-status.json";
    internal const string HeartbeatFileName = "service-heartbeat.json";
    internal const string ProbeRequestFileName = "startup-probe-request.json";
    internal const string ProbeResultFileName = "startup-probe-result.json";

    internal const int ExpectedSchemaVersion = 1;
    internal const string ExpectedServiceVersion = "1.0.0";

    /// <summary>The ONLY state value that is a readiness success.</summary>
    internal const string RunningStateValue = "running";

    /// <summary>
    /// CYCLE 75. The remaining CLOSED lifecycle wire values the producer can
    /// write. They are read here so each becomes its own bounded verdict; the
    /// producer and its schema are NOT changed by this cycle.
    /// </summary>
    internal const string StartingStateValue = "starting";

    internal const string StoppedStateValue = "stopped";

    internal const string FailedStateValue = "failed";

    internal const int RequiredSessionId = 0;

    /// <summary>A hard size bound applied BEFORE any byte is buffered.</summary>
    internal const long MaxDocumentBytes = 4096;

    internal static readonly string[] StatusProperties =
    {
        "schemaVersion", "state", "timestampUtc", "sessionId", "userInteractive", "serviceVersion",
    };

    internal static readonly string[] HeartbeatProperties =
    {
        "schemaVersion", "timestampUtc", "sessionId", "userInteractive", "serviceVersion",
    };
}

/// <summary>
/// The CLOSED set of document verdicts. Zero is the permanent, safe default so
/// an uninitialised value can never read as valid.
/// </summary>
internal enum ServiceRuntimeDocumentState
{
    Unspecified = 0,
    Valid = 1,

    /// <summary>The document is absent.</summary>
    Missing = 2,

    /// <summary>The document exceeded the hard size bound, or could not be read.</summary>
    Unreadable = 3,

    /// <summary>Strict UTF-8, JSON shape, property set, value or timestamp rules failed.</summary>
    Malformed = 4,

    /// <summary>
    /// Well-formed, but the document reports a session other than Session 0 or
    /// an interactive host. THIS IS A SECURITY/EXECUTION-CONTEXT VERDICT and it
    /// is never produced by a lifecycle state. It keeps ordinal 5, which the
    /// pre-cycle-75 member NotRunningInSessionZero occupied.
    /// </summary>
    WrongExecutionContext = 5,

    // ---- CYCLE 75: THE LIFECYCLE VERDICTS ---------------------------------
    //
    // APPENDED, never renumbered. Each is a well-formed document in the correct
    // execution context that simply does not report readiness yet, or never
    // will. Only Missing and Starting are TRANSIENT.

    /// <summary>The service reported "starting". Transient, and NOT a failure.</summary>
    Starting = 6,

    /// <summary>The service reported "stopped". Terminal.</summary>
    Stopped = 7,

    /// <summary>The service reported "failed". Terminal.</summary>
    Failed = 8,

    /// <summary>The state string is not a value this build recognises. Terminal, fail-closed.</summary>
    UnknownState = 9,
}

/// <summary>One verified document: its verdict plus, when valid, its self-reported timestamp.</summary>
internal readonly struct ServiceRuntimeDocumentResult
{
    private ServiceRuntimeDocumentResult(ServiceRuntimeDocumentState state, DateTimeOffset timestampUtc)
    {
        State = state;
        TimestampUtc = timestampUtc;
    }

    internal ServiceRuntimeDocumentState State { get; }

    /// <summary>
    /// The document's OWN reported timestamp. It is never a filesystem
    /// timestamp, and it is meaningful only when <see cref="State"/> is Valid.
    /// </summary>
    internal DateTimeOffset TimestampUtc { get; }

    internal bool IsValid => State == ServiceRuntimeDocumentState.Valid;

    internal static ServiceRuntimeDocumentResult Valid(DateTimeOffset timestampUtc) =>
        new(ServiceRuntimeDocumentState.Valid, timestampUtc);

    internal static ServiceRuntimeDocumentResult Refused(ServiceRuntimeDocumentState state) =>
        new(state, default);

    /// <summary>Carries the bounded state name only - never the timestamp.</summary>
    public override string ToString() => State.ToString();
}

/// <summary>
/// The verifier. An interface ONLY so the focused tests can drive the bounded
/// heartbeat proof without a live service. Production always uses
/// <see cref="StrictServiceRuntimeDocumentVerifier"/>.
/// </summary>
internal interface IServiceRuntimeDocumentVerifier
{
    ServiceRuntimeDocumentResult VerifyStatus(string statusPath);

    ServiceRuntimeDocumentResult VerifyHeartbeat(string heartbeatPath);
}

/// <summary>The real strict verifier. Reading and validating, and nothing else.</summary>
internal sealed class StrictServiceRuntimeDocumentVerifier : IServiceRuntimeDocumentVerifier
{
    public ServiceRuntimeDocumentResult VerifyStatus(string statusPath) =>
        Verify(statusPath, ServiceRuntimeDocumentContract.StatusProperties, requireRunningState: true);

    public ServiceRuntimeDocumentResult VerifyHeartbeat(string heartbeatPath) =>
        Verify(heartbeatPath, ServiceRuntimeDocumentContract.HeartbeatProperties, requireRunningState: false);

    private static ServiceRuntimeDocumentResult Verify(
        string path, string[] expectedProperties, bool requireRunningState)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Missing);
            }

            // BOUNDED BEFORE BUFFERED: the length is checked first, so an
            // oversized or hostile file is never read into memory.
            if (info.Length <= 0 || info.Length > ServiceRuntimeDocumentContract.MaxDocumentBytes)
            {
                return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Unreadable);
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Unreadable);
        }

        if (bytes.Length > ServiceRuntimeDocumentContract.MaxDocumentBytes)
        {
            return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Unreadable);
        }

        string text;
        try
        {
            // STRICT UTF-8: an invalid sequence throws instead of becoming U+FFFD.
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (Exception)
        {
            return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Malformed);
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(text);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Malformed);
            }

            var seen = new System.Collections.Generic.List<string>(expectedProperties.Length);
            foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
            {
                if (Array.IndexOf(expectedProperties, property.Name) < 0 || seen.Contains(property.Name))
                {
                    // Unknown property, or a duplicate of a known one.
                    return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Malformed);
                }

                seen.Add(property.Name);
            }

            if (seen.Count != expectedProperties.Length)
            {
                return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Malformed);
            }

            JsonElement root = parsed.RootElement;

            if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number
                || !root.GetProperty("schemaVersion").TryGetInt32(out int schemaVersion)
                || schemaVersion != ServiceRuntimeDocumentContract.ExpectedSchemaVersion)
            {
                return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Malformed);
            }

            if (root.GetProperty("serviceVersion").ValueKind != JsonValueKind.String
                || !string.Equals(
                    root.GetProperty("serviceVersion").GetString(),
                    ServiceRuntimeDocumentContract.ExpectedServiceVersion,
                    StringComparison.Ordinal))
            {
                return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Malformed);
            }

            if (root.GetProperty("timestampUtc").ValueKind != JsonValueKind.String
                || !TryParseRfc3339Utc(root.GetProperty("timestampUtc").GetString(), out DateTimeOffset stamp))
            {
                return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Malformed);
            }

            JsonElement sessionElement = root.GetProperty("sessionId");
            JsonElement interactiveElement = root.GetProperty("userInteractive");
            if (sessionElement.ValueKind != JsonValueKind.Number
                || !sessionElement.TryGetInt32(out int sessionId)
                || (interactiveElement.ValueKind != JsonValueKind.True
                    && interactiveElement.ValueKind != JsonValueKind.False))
            {
                return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Malformed);
            }

            // ---- STRUCTURE, BEFORE EXECUTION CONTEXT -----------------------
            //
            // Whether "state" is a JSON string is a SHAPE question and belongs
            // with the other schema rules. What the string SAYS is a lifecycle
            // question and is decided last, after the execution-context rules.
            string? stateText = null;
            if (requireRunningState)
            {
                JsonElement stateElement = root.GetProperty("state");
                if (stateElement.ValueKind != JsonValueKind.String)
                {
                    return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Malformed);
                }

                stateText = stateElement.GetString();
            }

            // ---- EXECUTION CONTEXT, WHICH OUTRANKS LIFECYCLE ---------------
            //
            // A wrong session or an interactive host is reported AHEAD of any
            // lifecycle state, exactly as it was before cycle 75.
            if (sessionId != ServiceRuntimeDocumentContract.RequiredSessionId
                || interactiveElement.GetBoolean())
            {
                return ServiceRuntimeDocumentResult.Refused(
                    ServiceRuntimeDocumentState.WrongExecutionContext);
            }

            // ---- LIFECYCLE, LAST ------------------------------------------
            return requireRunningState
                ? ClassifyLifecycle(stateText, stamp)
                : ServiceRuntimeDocumentResult.Valid(stamp);
        }
        catch (Exception)
        {
            return ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Malformed);
        }
    }

    /// <summary>
    /// CYCLE 75. The TOTAL lifecycle classification. Running is the ONE readiness
    /// success. Every other value is a bounded verdict of its own, and an
    /// unrecognised string is fail-closed rather than assumed benign. It never
    /// returns the raw string.
    /// </summary>
    private static ServiceRuntimeDocumentResult ClassifyLifecycle(string? state, DateTimeOffset stamp) =>
        state switch
        {
            ServiceRuntimeDocumentContract.RunningStateValue =>
                ServiceRuntimeDocumentResult.Valid(stamp),
            ServiceRuntimeDocumentContract.StartingStateValue =>
                ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Starting),
            ServiceRuntimeDocumentContract.StoppedStateValue =>
                ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Stopped),
            ServiceRuntimeDocumentContract.FailedStateValue =>
                ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.Failed),
            _ => ServiceRuntimeDocumentResult.Refused(ServiceRuntimeDocumentState.UnknownState),
        };

    /// <summary>
    /// RFC 3339 with a ZERO UTC offset. A local-time or offset-bearing timestamp
    /// is refused rather than silently converted.
    /// </summary>
    internal static bool TryParseRfc3339Utc(string? value, out DateTimeOffset parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset candidate))
        {
            return false;
        }

        // The text itself must carry a UTC designator; a bare local timestamp is
        // never accepted just because parsing assumed universal time.
        bool hasUtcDesignator =
            value.EndsWith("Z", StringComparison.Ordinal)
            || value.EndsWith("+00:00", StringComparison.Ordinal)
            || value.EndsWith("-00:00", StringComparison.Ordinal);

        if (!hasUtcDesignator || candidate.Offset != TimeSpan.Zero)
        {
            return false;
        }

        parsed = candidate;
        return true;
    }
}
