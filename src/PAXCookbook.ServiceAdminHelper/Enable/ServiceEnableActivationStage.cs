// PAX Cookbook - SERVICE-ENABLE ACTIVATION STAGE (cycle 63RR, helper only)
//
// WHAT THIS FILE IS. The ONE place that turns a REGISTERED service into a
// RUNNING, PROVEN service. It owns the three machine-data access-control
// profiles, the creation of the runtime directory, the single authorized start,
// and the end-to-end verification that follows it. It is the last step of the
// enable transaction and it never runs before the anchor is durable.
//
// THE ORDER, and it is not negotiable:
//   1. METADATA directory profile  - applied (when authorized), then VERIFIED.
//   2. INSTALLATION ANCHOR profile - applied ONLY when THIS attempt created the
//      anchor, then VERIFIED. An anchor that already existed is VERIFIED ONLY.
//      A pre-existing anchor whose owner or DACL is wrong is REFUSED, never
//      silently repaired: rewriting a security descriptor this product did not
//      place would destroy the evidence that the state is not ours.
//   3. RUNTIME directory profile   - the directory is created when absent and
//      authorized, then the profile is applied and VERIFIED.
//   4. StartServiceW, with NO argument vector, and ONLY when this attempt is
//      authorized to start. An already-running service is never restarted.
//   5. The full startability proof: Running, process token SID, Session 0, the
//      closed status document, an ADVANCING heartbeat, and no forbidden child.
//
// WHY THE PROFILES ARE VERIFIED EVEN WHEN THEY WERE JUST APPLIED. "Applied"
// means a call returned true. "Verified" means the owner and the complete DACL
// were read back and matched exactly. Only the second one is evidence.
//
// THE R4 VERIFY-ONLY SHAPE. When the service is ALREADY RUNNING against an
// exactly matching installation, nothing is applied, nothing is created and
// nothing is started - every profile is VERIFIED and the run state is PROVEN.
// A single failed check is RecoveryRequired, never an adoption and never a
// repair.
//
// OWNERSHIP IS TRACKED, NEVER INFERRED. This stage reports back exactly two
// ordering facts - whether IT created the runtime directory and whether IT
// started the service - so compensation can undo only what this attempt did.
// Matching bytes never imply ownership.
//
// WHAT THIS FILE CANNOT DO, by construction. It starts no process other than
// through the fixed SCM adapter's StartServiceW, opens no certificate store,
// key or credential vault, reads or writes no registry key, opens no socket,
// runs no shell, touches no PAX and starts no Bake. It terminates, signals and
// suspends nothing.
//
// PRIVACY - FAIL CLOSED. Every answer is a bounded token. Nothing here returns a
// path, a SID, a timestamp, a process id, a document body or an exception.
using System;
using System.IO;
using System.Runtime.Versioning;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// The CLOSED set of activation verdicts. Zero is the permanent, safe default so
/// an uninitialised value can never read as verified.
/// </summary>
internal enum ServiceEnableActivationState
{
    Unspecified = 0,

    /// <summary>Every profile verified exactly, and the service is provably running and alive.</summary>
    Verified = 1,

    /// <summary>The metadata directory profile could not be applied, or did not verify exactly.</summary>
    MetadataProtectionRefused = 2,

    /// <summary>The anchor profile could not be applied, or did not verify exactly. Never repaired.</summary>
    AnchorProtectionRefused = 3,

    /// <summary>The runtime directory could not be created or protected, or did not verify exactly.</summary>
    RuntimeProtectionRefused = 4,

    /// <summary>StartServiceW refused.</summary>
    StartRefused = 5,

    /// <summary>The SCM never reported Running inside the bounded wait.</summary>
    NeverReachedRunning = 6,

    /// <summary>The live process is not the fixed service identity, or is not in Session 0.</summary>
    ProcessIdentityRefused = 7,

    /// <summary>
    /// The status document failed the closed schema, state, session or
    /// interactivity rules. RETAINED so an already-emitted code still decodes;
    /// NO live branch produces it after cycle 75.
    /// </summary>
    StatusDocumentRefused = 8,

    /// <summary>Two valid, strictly advancing heartbeat observations were not seen in the window.</summary>
    HeartbeatDidNotAdvance = 9,

    /// <summary>A PAX or Bake child process was observed beneath the service process.</summary>
    ForbiddenChildProcess = 10,

    /// <summary>A bounded access or stability failure. NEVER treated as verified.</summary>
    Unavailable = 11,

    // ---- CYCLE 74: THE DOCUMENTED StartServiceW SUB-CLASSIFICATIONS -------
    //
    // APPENDED, never renumbered. StartRefused (ordinal 5) REMAINS the closed
    // fallback for a start refusal with no finer documented category.

    /// <summary>ERROR_SERVICE_LOGON_FAILED.</summary>
    StartLogonRefused = 12,

    /// <summary>ERROR_ACCESS_DENIED.</summary>
    StartAccessRefused = 13,

    /// <summary>ERROR_PATH_NOT_FOUND.</summary>
    StartBinaryUnavailable = 14,

    /// <summary>ERROR_SERVICE_DEPENDENCY_FAIL or ERROR_SERVICE_DEPENDENCY_DELETED.</summary>
    StartDependencyRefused = 15,

    /// <summary>
    /// ERROR_SERVICE_REQUEST_TIMEOUT - a SYNCHRONOUS refusal of the start call.
    /// It is NEVER folded into <see cref="NeverReachedRunning"/>.
    /// </summary>
    StartRequestTimeout = 16,

    // ---- CYCLE 75: THE BOUNDED STATUS-READINESS SUB-CLASSIFICATIONS -------
    //
    // APPENDED, never renumbered. StatusDocumentRefused (ordinal 8) is RETAINED
    // for backward decoding only; no live branch reaches it after this cycle.

    /// <summary>The readiness window expired having observed ONLY an absent status document.</summary>
    StatusMissingTimeout = 17,

    /// <summary>The readiness window expired after at least one valid "starting" document.</summary>
    StatusStartingTimeout = 18,

    /// <summary>The status document exceeded the hard size bound or could not be read.</summary>
    StatusUnreadable = 19,

    /// <summary>The status document failed the strict UTF-8, JSON, property-set or value rules.</summary>
    StatusMalformed = 20,

    /// <summary>The status document reported a session other than 0, or an interactive host.</summary>
    StatusWrongContext = 21,

    /// <summary>The status document reported "stopped".</summary>
    StatusStopped = 22,

    /// <summary>The status document reported "failed".</summary>
    StatusFailed = 23,

    /// <summary>The status document reported a state string this build does not recognise.</summary>
    StatusUnknownState = 24,
}

/// <summary>
/// Everything one activation is allowed to know. Every path is COMPOSED by the
/// transaction from the fixed metadata root; none is caller-supplied, and there
/// is no field through which a service name, account, command or override could
/// travel.
/// </summary>
internal readonly struct ServiceEnableActivationRequest
{
    internal ServiceEnableActivationRequest(
        string serviceSid,
        string metadataDirectory,
        string anchorPath,
        string runtimeDirectory,
        string statusPath,
        string heartbeatPath,
        bool applyProfiles,
        bool startAuthorized,
        bool anchorCreatedThisAttempt)
    {
        ServiceSid = serviceSid;
        MetadataDirectory = metadataDirectory;
        AnchorPath = anchorPath;
        RuntimeDirectory = runtimeDirectory;
        StatusPath = statusPath;
        HeartbeatPath = heartbeatPath;
        ApplyProfiles = applyProfiles;
        StartAuthorized = startAuthorized;
        AnchorCreatedThisAttempt = anchorCreatedThisAttempt;
    }

    internal string ServiceSid { get; }

    internal string MetadataDirectory { get; }

    internal string AnchorPath { get; }

    internal string RuntimeDirectory { get; }

    internal string StatusPath { get; }

    internal string HeartbeatPath { get; }

    /// <summary>
    /// False on the R4 already-running path: nothing is applied, nothing is
    /// created, and every profile is VERIFIED as it already stands.
    /// </summary>
    internal bool ApplyProfiles { get; }

    /// <summary>False when the service is already running. An already-running service is never restarted.</summary>
    internal bool StartAuthorized { get; }

    /// <summary>
    /// True only when THIS attempt created the anchor. It is the sole
    /// authorization for writing the anchor's security descriptor.
    /// </summary>
    internal bool AnchorCreatedThisAttempt { get; }

    /// <summary>Carries the bounded type name only - never a path or SID.</summary>
    public override string ToString() => nameof(ServiceEnableActivationRequest);
}

/// <summary>
/// The bounded activation result plus the two ORDERING facts compensation
/// depends on. Both facts are recorded from what this attempt actually did.
/// </summary>
internal readonly struct ServiceEnableActivationResult
{
    internal ServiceEnableActivationResult(
        ServiceEnableActivationState state, bool runtimeDirectoryCreated, bool serviceStarted)
    {
        State = state;
        RuntimeDirectoryCreatedThisAttempt = runtimeDirectoryCreated;
        ServiceStartedThisAttempt = serviceStarted;
    }

    internal ServiceEnableActivationState State { get; }

    /// <summary>True ONLY when this attempt created the runtime directory.</summary>
    internal bool RuntimeDirectoryCreatedThisAttempt { get; }

    /// <summary>True ONLY when this attempt's StartServiceW call succeeded.</summary>
    internal bool ServiceStartedThisAttempt { get; }

    internal bool IsVerified => State == ServiceEnableActivationState.Verified;

    /// <summary>Carries the bounded state name only.</summary>
    public override string ToString() => State.ToString();
}

/// <summary>
/// The activation stage. An interface ONLY so the focused tests can prove every
/// ordering and every refusal without writing a real machine access-control
/// database, creating %ProgramData% or starting a real service. Production
/// always uses <see cref="ServiceEnableActivationStage"/>.
/// </summary>
internal interface IServiceEnableActivationStage
{
    /// <summary>Applies (when authorized) and VERIFIES the three profiles, then starts and proves.</summary>
    ServiceEnableActivationResult ActivateAndVerify(ServiceEnableActivationRequest request);

    /// <summary>
    /// COMPENSATION ONLY, and only for a service THIS attempt started. It sends
    /// the single permitted control code and waits for a bounded Stopped.
    /// </summary>
    bool TryStopServiceStartedThisAttempt();

    /// <summary>
    /// COMPENSATION ONLY, and only for a runtime directory THIS attempt created.
    /// It removes the CLOSED runtime leaves and then the directory, and only
    /// when the directory is empty. An unknown sibling blocks removal.
    /// </summary>
    bool TryRemoveRuntimeDirectoryCreatedThisAttempt(string runtimeDirectory);
}

/// <summary>
/// THE ONE PRODUCTION ACTIVATION STAGE. Both collaborators are injected, so no
/// test reaches the real machine access-control database or the real Service
/// Control Manager.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServiceEnableActivationStage : IServiceEnableActivationStage
{
    private readonly IServiceMachineDataSecurityAdapter _security;
    private readonly ServiceStartabilityCoordinator _startability;
    private readonly IServiceControlManagerAdapter _serviceControl;

    internal ServiceEnableActivationStage(
        IServiceMachineDataSecurityAdapter security,
        ServiceStartabilityCoordinator startability,
        IServiceControlManagerAdapter serviceControl)
    {
        _security = security;
        _startability = startability;
        _serviceControl = serviceControl;
    }

    public ServiceEnableActivationResult ActivateAndVerify(ServiceEnableActivationRequest request)
    {
        bool runtimeCreated = false;
        bool started = false;

        if (_security is null || _startability is null || _serviceControl is null
            || string.IsNullOrWhiteSpace(request.ServiceSid)
            || string.IsNullOrEmpty(request.MetadataDirectory)
            || string.IsNullOrEmpty(request.AnchorPath)
            || string.IsNullOrEmpty(request.RuntimeDirectory)
            || string.IsNullOrEmpty(request.StatusPath)
            || string.IsNullOrEmpty(request.HeartbeatPath))
        {
            return Refuse(ServiceEnableActivationState.Unavailable, runtimeCreated, started);
        }

        try
        {
            // ---- 1. THE METADATA DIRECTORY ---------------------------------
            if (!Directory.Exists(request.MetadataDirectory))
            {
                return Refuse(ServiceEnableActivationState.Unavailable, runtimeCreated, started);
            }

            if (request.ApplyProfiles
                && !_security.TryApplyMetadataProtection(request.MetadataDirectory, request.ServiceSid))
            {
                return Refuse(ServiceEnableActivationState.MetadataProtectionRefused, runtimeCreated, started);
            }

            if (!_security.VerifyMetadataProtection(request.MetadataDirectory, request.ServiceSid))
            {
                return Refuse(ServiceEnableActivationState.MetadataProtectionRefused, runtimeCreated, started);
            }

            // ---- 2. THE INSTALLATION ANCHOR --------------------------------
            //
            // The anchor is written ONLY when this very attempt created it. An
            // anchor that already existed is verified as it stands and REFUSED
            // when wrong - it is never repaired, and its bytes and descriptor
            // are left exactly as found.
            if (!File.Exists(request.AnchorPath))
            {
                return Refuse(ServiceEnableActivationState.AnchorProtectionRefused, runtimeCreated, started);
            }

            if (request.ApplyProfiles
                && request.AnchorCreatedThisAttempt
                && !_security.TryApplyAnchorProtection(request.AnchorPath))
            {
                return Refuse(ServiceEnableActivationState.AnchorProtectionRefused, runtimeCreated, started);
            }

            if (!_security.VerifyAnchorProtection(request.AnchorPath))
            {
                return Refuse(ServiceEnableActivationState.AnchorProtectionRefused, runtimeCreated, started);
            }

            // ---- 3. THE RUNTIME DIRECTORY ----------------------------------
            if (!Directory.Exists(request.RuntimeDirectory))
            {
                if (!request.ApplyProfiles || File.Exists(request.RuntimeDirectory))
                {
                    // On the verify-only path an absent runtime directory is not
                    // the exact expected state, and a FILE where the directory
                    // belongs is never replaced.
                    return Refuse(ServiceEnableActivationState.RuntimeProtectionRefused, runtimeCreated, started);
                }

                Directory.CreateDirectory(request.RuntimeDirectory);
                runtimeCreated = Directory.Exists(request.RuntimeDirectory);
                if (!runtimeCreated)
                {
                    return Refuse(ServiceEnableActivationState.RuntimeProtectionRefused, runtimeCreated, started);
                }
            }

            if (request.ApplyProfiles
                && !_security.TryApplyRuntimeProtection(request.RuntimeDirectory, request.ServiceSid))
            {
                return Refuse(ServiceEnableActivationState.RuntimeProtectionRefused, runtimeCreated, started);
            }

            if (!_security.VerifyRuntimeProtection(request.RuntimeDirectory, request.ServiceSid))
            {
                return Refuse(ServiceEnableActivationState.RuntimeProtectionRefused, runtimeCreated, started);
            }

            // ---- 4. THE SINGLE AUTHORIZED START ----------------------------
            if (request.StartAuthorized)
            {
                ServiceStartAttemptResult attempt =
                    _serviceControl.TryStartService(ServiceIdentityContract.ServiceName);

                if (!ServiceStartAttemptResultMap.IsStarted(attempt))
                {
                    return Refuse(MapStartAttempt(attempt), runtimeCreated, started);
                }

                // ALREADY-RUNNING counts as started for OWNERSHIP too, exactly as
                // it did before this cycle: the transaction only authorizes a
                // start when it believes the service is stopped, so an
                // already-running answer here is a race this attempt must remain
                // responsible for compensating.
                started = true;
            }

            // ---- 5. THE FULL STARTABILITY PROOF ----------------------------
            ServiceStartabilityState proof =
                _startability.Verify(request.ServiceSid, request.StatusPath, request.HeartbeatPath);

            return new ServiceEnableActivationResult(MapProof(proof), runtimeCreated, started);
        }
        catch (Exception)
        {
            return Refuse(ServiceEnableActivationState.Unavailable, runtimeCreated, started);
        }
    }

    public bool TryStopServiceStartedThisAttempt() =>
        _serviceControl is not null
        && _startability is not null
        && _serviceControl.TryStopService(ServiceIdentityContract.ServiceName)
        && _startability.WaitForStopped();

    public bool TryRemoveRuntimeDirectoryCreatedThisAttempt(string runtimeDirectory)
    {
        try
        {
            if (string.IsNullOrEmpty(runtimeDirectory) || !Directory.Exists(runtimeDirectory))
            {
                return !File.Exists(runtimeDirectory);
            }

            if (Directory.GetDirectories(runtimeDirectory).Length != 0)
            {
                return false;
            }

            foreach (string file in Directory.GetFiles(runtimeDirectory))
            {
                // The CLOSED leaf rule is the disable path's, reused rather than
                // restated, so removal can never be based on a second idea of
                // what "our" runtime files are.
                if (!ServiceDisableTransaction.IsClosedRuntimeLeaf(Path.GetFileName(file)))
                {
                    return false;
                }
            }

            foreach (string file in Directory.GetFiles(runtimeDirectory))
            {
                File.Delete(file);
            }

            return ServiceDisableTransaction.RemoveDirectoryWhenEmpty(runtimeDirectory);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The TOTAL startability-to-activation map. Only Verified is a success.
    ///
    /// CYCLE 75. The eight bounded status-readiness verdicts each keep their own
    /// identity here; nothing maps to
    /// <see cref="ServiceEnableActivationState.StatusDocumentRefused"/>, which is
    /// retained only so an already-emitted code still decodes.
    /// </summary>
    internal static ServiceEnableActivationState MapProof(ServiceStartabilityState proof) => proof switch
    {
        ServiceStartabilityState.Verified => ServiceEnableActivationState.Verified,
        ServiceStartabilityState.StartRefused => ServiceEnableActivationState.StartRefused,
        ServiceStartabilityState.NeverReachedRunning => ServiceEnableActivationState.NeverReachedRunning,
        ServiceStartabilityState.ProcessIdentityRefused => ServiceEnableActivationState.ProcessIdentityRefused,
        ServiceStartabilityState.StatusDocumentRefused => ServiceEnableActivationState.StatusDocumentRefused,
        ServiceStartabilityState.StatusMissingTimeout => ServiceEnableActivationState.StatusMissingTimeout,
        ServiceStartabilityState.StatusStartingTimeout => ServiceEnableActivationState.StatusStartingTimeout,
        ServiceStartabilityState.StatusUnreadable => ServiceEnableActivationState.StatusUnreadable,
        ServiceStartabilityState.StatusMalformed => ServiceEnableActivationState.StatusMalformed,
        ServiceStartabilityState.StatusWrongContext => ServiceEnableActivationState.StatusWrongContext,
        ServiceStartabilityState.StatusStopped => ServiceEnableActivationState.StatusStopped,
        ServiceStartabilityState.StatusFailed => ServiceEnableActivationState.StatusFailed,
        ServiceStartabilityState.StatusUnknownState => ServiceEnableActivationState.StatusUnknownState,
        ServiceStartabilityState.HeartbeatDidNotAdvance => ServiceEnableActivationState.HeartbeatDidNotAdvance,
        ServiceStartabilityState.ForbiddenChildProcess => ServiceEnableActivationState.ForbiddenChildProcess,
        _ => ServiceEnableActivationState.Unavailable,
    };

    /// <summary>
    /// CYCLE 74. The TOTAL bounded-start-attempt-to-activation map. Every
    /// non-started result is a refusal, and the two results that mean "the
    /// service is started" never reach it. An unrecognised value falls to the
    /// CLOSED FALLBACK <see cref="ServiceEnableActivationState.StartRefused"/>,
    /// which is a refusal, so there is no default that could read as success.
    /// </summary>
    internal static ServiceEnableActivationState MapStartAttempt(ServiceStartAttemptResult attempt) =>
        attempt switch
        {
            ServiceStartAttemptResult.LogonRefused => ServiceEnableActivationState.StartLogonRefused,
            ServiceStartAttemptResult.AccessRefused => ServiceEnableActivationState.StartAccessRefused,
            ServiceStartAttemptResult.BinaryUnavailable => ServiceEnableActivationState.StartBinaryUnavailable,
            ServiceStartAttemptResult.DependencyRefused => ServiceEnableActivationState.StartDependencyRefused,
            ServiceStartAttemptResult.RequestTimeout => ServiceEnableActivationState.StartRequestTimeout,
            _ => ServiceEnableActivationState.StartRefused,
        };

    private static ServiceEnableActivationResult Refuse(
        ServiceEnableActivationState state, bool runtimeCreated, bool started) =>
        new(state, runtimeCreated, started);
}
