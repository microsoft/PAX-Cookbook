// PAX Cookbook - SERVICE-ENABLE TRANSACTION (cycle 62, helper only)
//
// WHAT THIS FILE IS. The ONE production implementation of
// IServiceIdentityBoundTransaction. It runs inside the elevated helper, inside
// the identity-bound window the cycle-58/59 channel opens, and it is the only
// place where Program Files extraction and Service Control Manager registration
// are sequenced together.
//
// THE ORDER THE CHANNEL ENFORCES AND THIS FILE OBEYS. The channel proves live
// kernel identity, reads and classifies the existing anchor, and refuses a
// conflicting or malformed one. Only then does it call Preflight, which writes
// NOTHING. Only after a Proceed does it persist the anchor. Only after the
// anchor is DURABLE does it call Apply. The single acknowledgement is sent only
// after Apply's own exact final verification.
//
// WHY THE ANCHOR COMES FIRST. If Program Files or the SCM were mutated before
// the anchor were durable, a crash would leave machine state that no anchor
// explains - and an unexplained footprint is, by this cycle's own preflight
// rule, unrecoverable without an attended operation. Persisting the anchor
// first is what makes every later failure compensable.
//
// SERVICE SID RESOLUTION IS A POLICY CHOICE, NOT A NECESSITY. The per-service
// SID is deterministic and could be computed before the service exists. This
// transaction deliberately resolves it ONLY after successful creation and
// successful unrestricted-SID configuration, so the SID it grants rights to is
// the SID of a service that demonstrably exists and is demonstrably configured.
// It never invokes `sc showsid` and never reimplements the derivation.
//
// WHAT REGISTRATION DOES NOT PROVE - the Cycle 62 obligation, discharged in
// Cycle 63RR. Creating and configuring the service proves NOTHING about whether
// it starts under NT SERVICE\PAXCookbookService, runs with
// SERVICE_SID_TYPE_UNRESTRICTED, loads the framework-dependent assembly through
// the machine-wide dotnet.exe, or reaches a bounded running or heartbeat state.
// That proof now lives in the ACTIVATION STAGE, which applies the three machine
// data profiles, performs the single authorized start, and verifies the run
// state, the process identity, Session 0, the status document and an ADVANCING
// heartbeat before this transaction may report Completed.
//
// OWNERSHIP IS TRACKED, NEVER INFERRED FROM BYTES. Four facts are recorded
// SEPARATELY - service created this attempt, service started this attempt,
// runtime directory created this attempt, anchor created this attempt - because
// compensation may undo only what this attempt actually did. A service, runtime
// directory or anchor that merely LOOKS like ours is never adopted, never
// stopped and never deleted.
//
// THE LEDGER CHECK IS EXISTENCE ONLY. Compensation asks whether an object
// exists at the fixed ownership-ledger path from ServiceMachineStorageContract.
// It does not parse, open, validate, enumerate or link the ledger reader.
//
// PRIVACY - FAIL CLOSED. Every result is a bounded token. Nothing here returns
// a path, a SID, a member name, a hash, a native status or an exception.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using PAXCookbook.ServiceAdminHelper.Payload;
using PAXCookbook.ServiceAdminHelper.Signing;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// The embedded payload, behind an interface ONLY so the focused tests can drive
/// every extraction and verification path with constructed archives. Production
/// always uses <see cref="EmbeddedServiceEnablePayloadSource"/>, which takes no
/// path, no assembly and no resource name.
/// </summary>
internal interface IServiceEnablePayloadSource
{
    /// <summary>
    /// Reads the fixed embedded archive into memory and verifies it. The bytes
    /// are non-empty ONLY when the result is Verified.
    /// </summary>
    ServicePayloadVerificationResult VerifyPayload(out ReadOnlyMemory<byte> archiveBytes);
}

/// <summary>The real source: the fixed embedded resource of THIS assembly.</summary>
internal sealed class EmbeddedServiceEnablePayloadSource : IServiceEnablePayloadSource
{
    public ServicePayloadVerificationResult VerifyPayload(out ReadOnlyMemory<byte> archiveBytes)
    {
        archiveBytes = ReadOnlyMemory<byte>.Empty;
        byte[]? bytes = ServicePayloadResource.TryReadEmbeddedBytes(
            out ServicePayloadVerificationOutcome failure);
        if (bytes is null)
        {
            return ServicePayloadVerificationResult.Refused(failure);
        }

        ServicePayloadVerificationResult result = ServicePayloadResource.InspectBytes(bytes);
        if (result.IsVerified)
        {
            archiveBytes = bytes;
        }
        return result;
    }
}

/// <summary>
/// The configured signing policy, behind an interface ONLY so the focused tests
/// can prove BOTH the default refusal and the prerelease allowance from a single
/// build. Production reads compile-time facts alone.
/// </summary>
internal interface IServiceEnableSigningPolicySource
{
    ServiceHelperSigningPolicyState ResolveConfiguredPolicy();
}

internal sealed class RealServiceEnableSigningPolicySource : IServiceEnableSigningPolicySource
{
    public ServiceHelperSigningPolicyState ResolveConfiguredPolicy() =>
        ServiceHelperSigningPolicy.ResolveConfiguredPolicy();
}

/// <summary>
/// THE ONE FIXED SERVICE-ENABLE TRANSACTION. Every collaborator is injected so
/// no test can reach the real Program Files tree, the real Service Control
/// Manager or the real machine access-control database; production supplies the
/// real adapters and no caller-supplied path enters through any of them.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServiceEnableTransaction
    : IServiceIdentityBoundTransaction,
      IServiceEnableFailureClassified,
      IServiceAnchorPersistenceFailureRecoverable
{
    private readonly string _serviceDirectory;
    private readonly IServiceEnableProgramFilesResolver _programFiles;
    private readonly IServiceEnableSecurityAdapter _security;
    private readonly IServiceControlManagerAdapter _serviceControl;
    private readonly IServiceEnablePayloadSource _payload;
    private readonly IServiceEnableSigningPolicySource _signing;
    private readonly IServiceEnableActivationStage? _activation;

    // Gathered by Preflight and reused by Apply, so the bytes Apply extracts are
    // the exact bytes Preflight verified.
    private ReadOnlyMemory<byte> _verifiedArchive;
    private IReadOnlyList<ServicePayloadManifestFileEntryFacts>? _manifestFacts;
    private ServiceEnableFixedPaths _paths;

    /// <summary>
    /// THE REGISTRATION-ONLY SHAPE. It is a DISCLOSED TEST SEAM for the cycle-62
    /// focused class, which proves extraction, registration, access control and
    /// compensation in isolation. Production NEVER uses it: the elevated
    /// dispatch always supplies an activation stage, and a transaction without
    /// one can never start or prove a service.
    /// </summary>
    internal ServiceEnableTransaction(
        string serviceDirectory,
        IServiceEnableProgramFilesResolver programFiles,
        IServiceEnableSecurityAdapter security,
        IServiceControlManagerAdapter serviceControl,
        IServiceEnablePayloadSource payload,
        IServiceEnableSigningPolicySource signing)
        : this(serviceDirectory, programFiles, security, serviceControl, payload, signing, activation: null)
    {
    }

    /// <summary>THE PRODUCTION SHAPE. Registration AND activation, in that order.</summary>
    internal ServiceEnableTransaction(
        string serviceDirectory,
        IServiceEnableProgramFilesResolver programFiles,
        IServiceEnableSecurityAdapter security,
        IServiceControlManagerAdapter serviceControl,
        IServiceEnablePayloadSource payload,
        IServiceEnableSigningPolicySource signing,
        IServiceEnableActivationStage? activation)
    {
        _serviceDirectory = serviceDirectory;
        _programFiles = programFiles;
        _security = security;
        _serviceControl = serviceControl;
        _payload = payload;
        _signing = signing;
        _activation = activation;
    }

    /// <summary>True when this transaction can start and prove the service.</summary>
    internal bool ActivationStageConfigured => _activation is not null;

    /// <summary>The runtime root beneath the metadata directory. Composed, never supplied.</summary>
    internal string RuntimeDirectory =>
        Path.Combine(_serviceDirectory, ServiceMachineStorageContract.RuntimeDataFolderName);

    /// <summary>The fixed installation anchor path. Composed, never supplied.</summary>
    internal string AnchorPath =>
        Path.Combine(_serviceDirectory, ServiceMachineStorageContract.InstallationAnchorFileName);

    /// <summary>
    /// The bounded outcome of the most recent phase. Exposed so the focused
    /// tests and the dispatch can observe WHY without any refusal carrying a
    /// path, identity or native status.
    /// </summary>
    internal ServiceEnableOperationOutcome LastOutcome { get; private set; } =
        ServiceEnableOperationOutcome.Unspecified;

    /// <summary>The bounded preflight result of the most recent Preflight call.</summary>
    internal ServiceEnablePreflightResult LastPreflight { get; private set; }

    // ---- CYCLE 67: THE CAUSE SURVIVES COMPENSATION --------------------------
    //
    // LastOutcome CONFLATES cause and disposition, and Compensate must keep
    // overwriting it with Compensated because roughly 133 existing tests assert
    // exactly that. These TWO fields are the separate, non-conflated record:
    // FailureCause is STICKY - the FIRST cause wins and nothing may overwrite
    // it - and FailureDisposition is the LAST word on what was left behind.

    /// <summary>
    /// The FIRST failure cause this transaction observed. Once set it is NEVER
    /// overwritten, so compensation can change what was left behind without
    /// destroying why the attempt failed.
    /// </summary>
    public ServiceEnableFailureCause FailureCause { get; private set; } = ServiceEnableFailureCause.None;

    /// <summary>
    /// The FINAL disposition. Compensation sets Compensated or RecoveryRequired
    /// here and never touches <see cref="FailureCause"/>.
    /// </summary>
    public ServiceEnableFailureDisposition FailureDisposition { get; private set; } =
        ServiceEnableFailureDisposition.None;

    /// <summary>
    /// STICKY. The first non-None cause wins; every later call is ignored. This
    /// is the whole of the cycle-67 fix for the defect where compensation
    /// overwrote the only record of why an attempt failed.
    /// </summary>
    private void RecordCause(ServiceEnableFailureCause cause)
    {
        if (cause == ServiceEnableFailureCause.None
            || FailureCause != ServiceEnableFailureCause.None)
        {
            return;
        }

        FailureCause = cause;
    }

    /// <summary>
    /// Records a failing legacy outcome AND its sticky cause together, so no
    /// failure site can set one without the other.
    /// </summary>
    private void Fail(ServiceEnableOperationOutcome outcome)
    {
        LastOutcome = outcome;
        RecordCause(ServiceEnableFailureContract.CauseFor(outcome));
    }

    /// <summary>
    /// CYCLE 71. Records a failing legacy outcome together with a cause that is
    /// FINER than the conflated outcome can express.
    ///
    /// WHY THE LEGACY OUTCOME IS STILL PASSED. LastOutcome is the value roughly
    /// 133 existing tests assert, and the disable transaction shares its
    /// vocabulary, so it is deliberately NOT re-pointed. Only the CAUSE axis -
    /// the one that actually crosses the process boundary - becomes finer.
    ///
    /// The cause remains STICKY: this overload routes through the same
    /// <see cref="RecordCause"/> gate, so a first cause can never be overwritten
    /// by a later site or by compensation.
    /// </summary>
    private void Fail(ServiceEnableOperationOutcome outcome, ServiceEnableFailureCause cause)
    {
        LastOutcome = outcome;
        RecordCause(cause);
    }

    /// <summary>
    /// The TOTAL map from a bounded preflight reason to a bounded cause. It is
    /// the finer classification the conflated outcome cannot express: every
    /// preflight refusal reaches this map BEFORE the legacy PreflightRefused
    /// value could ever be consulted for a cause.
    /// </summary>
    internal static ServiceEnableFailureCause CauseForPreflightReason(
        ServiceEnablePreflightReason reason) => reason switch
    {
        ServiceEnablePreflightReason.SigningPolicyRefused => ServiceEnableFailureCause.SigningPolicyRefused,

        ServiceEnablePreflightReason.PayloadUnverified => ServiceEnableFailureCause.PayloadRefused,

        ServiceEnablePreflightReason.ProgramFilesUnavailable
            or ServiceEnablePreflightReason.RuntimeHostUnusable
            or ServiceEnablePreflightReason.ReparsePointRefused
            or ServiceEnablePreflightReason.RuntimeHostWritableByUntrustedPrincipal =>
            ServiceEnableFailureCause.ProgramFilesPreflightRefused,

        ServiceEnablePreflightReason.StagingStateRefused
            or ServiceEnablePreflightReason.FinalStateRefused
            or ServiceEnablePreflightReason.ServiceStateRefused
            or ServiceEnablePreflightReason.UnexplainedExistingFootprint
            or ServiceEnablePreflightReason.AnchorStateRefused =>
            ServiceEnableFailureCause.ExistingStateRefused,

        _ => ServiceEnableFailureCause.Unavailable,
    };

    /// <summary>True once THIS transaction created the fixed service. Compensation depends on it.</summary>
    internal bool ServiceCreatedThisTransaction { get; private set; }

    /// <summary>
    /// True once THIS transaction's own StartServiceW call succeeded. It is the
    /// sole authorization for stopping the service during compensation; a
    /// service that was already running is never stopped.
    /// </summary>
    internal bool ServiceStartedThisTransaction { get; private set; }

    /// <summary>True once THIS transaction created the fixed runtime directory.</summary>
    internal bool RuntimeDirectoryCreatedThisTransaction { get; private set; }

    /// <summary>True once THIS transaction created the fixed product root directory.</summary>
    internal bool RootCreatedThisTransaction { get; private set; }

    /// <summary>True once THIS transaction created the fixed final directory.</summary>
    internal bool FinalCreatedThisTransaction { get; private set; }

    /// <summary>True once THIS transaction created the fixed staging directory.</summary>
    internal bool StagingCreatedThisTransaction { get; private set; }

    // ---- CYCLE 67: MACHINE DATA DIRECTORY OWNERSHIP -------------------------
    //
    // OWNERSHIP IS PROBED, NEVER INFERRED. The anchor store creates
    // %ProgramData%\PAXCookbook and %ProgramData%\PAXCookbook\Service as a side
    // effect of Directory.CreateDirectory and its write state does not report
    // which of them it created. The MUTATION-FREE preflight therefore records
    // whether each was ABSENT before anything could create it. Emptiness and a
    // matching name are NEVER treated as proof of ownership.

    private bool _machineDataProbeCompleted;
    private bool _machineRootAbsentAtPreflight;
    private bool _metadataDirectoryAbsentAtPreflight;

    /// <summary>True only when the mutation-free preflight actually completed its probe.</summary>
    internal bool MachineDataOwnershipProbed => _machineDataProbeCompleted;

    /// <summary>The fixed ProgramData machine root was ABSENT when preflight ran.</summary>
    internal bool MachineRootAbsentAtPreflight => _machineRootAbsentAtPreflight;

    /// <summary>The fixed metadata Service directory was ABSENT when preflight ran.</summary>
    internal bool MetadataDirectoryAbsentAtPreflight => _metadataDirectoryAbsentAtPreflight;

    /// <summary>
    /// True once THIS attempt is proven to have created the fixed metadata
    /// Service directory: it was absent at preflight AND this attempt is the one
    /// that created the anchor whose persistence created it.
    /// </summary>
    internal bool MetadataDirectoryCreatedThisTransaction { get; private set; }

    /// <summary>
    /// True once THIS attempt is proven to have created the fixed ProgramData
    /// machine root. It can never be true unless the metadata directory beneath
    /// it was also created by this attempt.
    /// </summary>
    internal bool MachineRootCreatedThisTransaction { get; private set; }

    /// <summary>The fixed ProgramData machine root. Composed, never supplied.</summary>
    internal string? MachineRootDirectory
    {
        get
        {
            try
            {
                string? parent = Path.GetDirectoryName(_serviceDirectory);
                return string.IsNullOrEmpty(parent) ? null : parent;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// MUTATION-FREE. Directory.Exists and File.Exists only: it creates nothing,
    /// opens nothing and changes no access control.
    /// </summary>
    private void ProbeMachineDataDirectories()
    {
        _machineDataProbeCompleted = false;
        _machineRootAbsentAtPreflight = false;
        _metadataDirectoryAbsentAtPreflight = false;

        try
        {
            string? root = MachineRootDirectory;
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            _metadataDirectoryAbsentAtPreflight =
                !Directory.Exists(_serviceDirectory) && !File.Exists(_serviceDirectory);
            _machineRootAbsentAtPreflight = !Directory.Exists(root) && !File.Exists(root);
            _machineDataProbeCompleted = true;
        }
        catch (Exception)
        {
            _machineDataProbeCompleted = false;
        }
    }

    /// <summary>
    /// Derives the two ownership facts once the anchor situation is known. A
    /// failed or absent probe yields FALSE for both, so an unprobed transaction
    /// can never authorize a directory removal.
    /// </summary>
    private void RecordMachineDataOwnership(ServiceIdentityBoundTransactionContext context)
    {
        MetadataDirectoryCreatedThisTransaction =
            _machineDataProbeCompleted
            && _metadataDirectoryAbsentAtPreflight
            && context.AnchorCreatedThisAttempt;

        MachineRootCreatedThisTransaction =
            MetadataDirectoryCreatedThisTransaction && _machineRootAbsentAtPreflight;
    }

    /// <summary>
    /// MUTATION-FREE. It gathers observations and evaluates the pure preflight.
    /// It creates no directory, writes no file, changes no access control and
    /// performs no service control.
    /// </summary>
    public ServiceIdentityBoundTransactionPreflightState Preflight()
    {
        ProbeMachineDataDirectories();

        ServiceEnablePreflightResult result = GatherAndEvaluate();
        LastPreflight = result;

        switch (result.Verdict)
        {
            case ServiceEnablePreflightVerdict.Proceed:
                LastOutcome = ServiceEnableOperationOutcome.Unspecified;
                return ServiceIdentityBoundTransactionPreflightState.Proceed;

            case ServiceEnablePreflightVerdict.RecoveryRequired:
                LastOutcome = ServiceEnableOperationOutcome.RecoveryRequired;
                RecordCause(CauseForPreflightReason(result.Reason));
                FailureDisposition = ServiceEnableFailureDisposition.RecoveryRequired;
                return ServiceIdentityBoundTransactionPreflightState.RecoveryRequired;

            default:
                LastOutcome = ServiceEnableOperationOutcome.PreflightRefused;
                RecordCause(CauseForPreflightReason(result.Reason));
                FailureDisposition = ServiceEnableFailureDisposition.RefusedBeforeMutation;
                return ServiceIdentityBoundTransactionPreflightState.Refused;
        }
    }

    /// <summary>
    /// Every observation is read-only. The one write-capable collaborator - the
    /// security adapter - is asked only its READ question here.
    /// </summary>
    private ServiceEnablePreflightResult GatherAndEvaluate()
    {
        if (!OperatingSystem.IsWindows()
            || _programFiles is null || _security is null || _serviceControl is null
            || _payload is null || _signing is null)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.Unavailable);
        }

        bool signingPermits =
            _signing.ResolveConfiguredPolicy() == ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed;

        ServicePayloadVerificationResult verification =
            _payload.VerifyPayload(out ReadOnlyMemory<byte> archiveBytes);
        bool payloadVerified = verification.IsVerified;
        if (payloadVerified)
        {
            _verifiedArchive = archiveBytes;
            _manifestFacts = TryReadManifestFacts(archiveBytes);
            payloadVerified = _manifestFacts is not null;
        }

        ServiceEnablePathResolutionState pathState =
            ServiceEnableFixedPathResolver.TryResolve(_programFiles, out ServiceEnableFixedPaths paths);
        bool pathsResolved = pathState == ServiceEnablePathResolutionState.Resolved;
        _paths = paths;

        bool runtimeHostIsRegularFile = false;
        bool reparse = false;
        bool untrustedWrite = false;
        ServiceEnableDirectoryState staging = ServiceEnableDirectoryState.Absent;
        ServiceEnableDirectoryState final = ServiceEnableDirectoryState.Absent;

        if (pathsResolved)
        {
            try
            {
                runtimeHostIsRegularFile = File.Exists(paths.RuntimeHost) && !Directory.Exists(paths.RuntimeHost);
                string? programFilesBase = Path.GetDirectoryName(paths.Root);
                reparse = string.IsNullOrEmpty(programFilesBase)
                    || ServiceEnableExtractor.AnyComponentIsReparsePoint(paths.Root, programFilesBase)
                    || (runtimeHostIsRegularFile
                        && ServiceEnableExtractor.AnyComponentIsReparsePoint(paths.RuntimeHost, programFilesBase));

                untrustedWrite = runtimeHostIsRegularFile
                    && _security.RuntimeHostHasUntrustedWriteGrant(paths.RuntimeHost);

                staging = ClassifyStaging(paths);
                final = ClassifyFinal(paths);
            }
            catch (Exception)
            {
                return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.Unavailable);
            }
        }

        ServiceQueryState servicePresence = _serviceControl.QueryConfiguration(
            ServiceIdentityContract.ServiceName, out ServiceConfigurationSnapshot existing);

        bool serviceMatches = pathsResolved
            && servicePresence == ServiceQueryState.Present
            && ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final).MatchesExactly(existing);

        ServiceInstallationAnchorReadResult anchor =
            ServiceInstallationAnchorStore.ReadFrom(_serviceDirectory);

        return ServiceEnablePreflight.Evaluate(new ServiceEnablePreflightObservations(
            signingPermits,
            payloadVerified,
            pathsResolved,
            runtimeHostIsRegularFile,
            reparse,
            untrustedWrite,
            staging,
            final,
            servicePresence,
            serviceMatches,
            anchor.State == ServiceInstallationAnchorReadState.Absent,
            anchor.State == ServiceInstallationAnchorReadState.Validated));
    }

    private ServiceEnableDirectoryState ClassifyStaging(ServiceEnableFixedPaths paths)
    {
        if (!Directory.Exists(paths.Staging))
        {
            return File.Exists(paths.Staging)
                ? ServiceEnableDirectoryState.Foreign
                : ServiceEnableDirectoryState.Absent;
        }

        // A staging directory is by definition an incomplete write of OUR
        // payload: it only ever exists between creation and promotion. It is a
        // recognised remnant, never an installation to adopt.
        return ServiceEnableDirectoryState.RecoverableRemnant;
    }

    private ServiceEnableDirectoryState ClassifyFinal(ServiceEnableFixedPaths paths)
    {
        if (!Directory.Exists(paths.Final))
        {
            return File.Exists(paths.Final)
                ? ServiceEnableDirectoryState.Foreign
                : ServiceEnableDirectoryState.Absent;
        }

        if (_manifestFacts is null
            || !ServiceEnableExtractor.VerifyExactMembership(paths.Final, _manifestFacts))
        {
            return ServiceEnableDirectoryState.Foreign;
        }

        // The membership and bytes are exact. The ACL can only be judged once a
        // service SID exists, so an existing final directory with no service is
        // still not an adoptable installation.
        string? serviceSid = _serviceControl.TryResolveServiceSid(ServiceIdentityContract.ServiceName);
        if (string.IsNullOrEmpty(serviceSid))
        {
            return ServiceEnableDirectoryState.Foreign;
        }

        return VerifyFinalProtectionEverywhere(paths.Final, serviceSid!)
            ? ServiceEnableDirectoryState.ExactMatch
            : ServiceEnableDirectoryState.Foreign;
    }

    private bool VerifyFinalProtectionEverywhere(string finalDirectory, string serviceSid)
    {
        try
        {
            if (!_security.VerifyFinalProtection(finalDirectory, isDirectory: true, serviceSid))
            {
                return false;
            }

            foreach (string directory in Directory.GetDirectories(
                         finalDirectory, "*", SearchOption.AllDirectories))
            {
                if (!_security.VerifyFinalProtection(directory, isDirectory: true, serviceSid))
                {
                    return false;
                }
            }

            foreach (string file in Directory.GetFiles(finalDirectory, "*", SearchOption.AllDirectories))
            {
                if (!_security.VerifyFinalProtection(file, isDirectory: false, serviceSid))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool ApplyFinalProtectionEverywhere(string finalDirectory, string serviceSid)
    {
        try
        {
            if (!_security.TryApplyFinalProtection(finalDirectory, isDirectory: true, serviceSid))
            {
                return false;
            }

            foreach (string directory in Directory.GetDirectories(
                         finalDirectory, "*", SearchOption.AllDirectories))
            {
                if (!_security.TryApplyFinalProtection(directory, isDirectory: true, serviceSid))
                {
                    return false;
                }
            }

            foreach (string file in Directory.GetFiles(finalDirectory, "*", SearchOption.AllDirectories))
            {
                if (!_security.TryApplyFinalProtection(file, isDirectory: false, serviceSid))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// CYCLE 63R. The exact expected member list, read from the SAME verified
    /// embedded payload the enable path uses. The disable transaction shares it
    /// so removal can never be based on a second, independently derived idea of
    /// what "our" members are.
    /// </summary>
    internal static IReadOnlyList<ServicePayloadManifestFileEntryFacts>? TryReadManifestFactsFrom(
        IServiceEnablePayloadSource payload)
    {
        if (payload is null)
        {
            return null;
        }

        ServicePayloadVerificationResult verification =
            payload.VerifyPayload(out ReadOnlyMemory<byte> archiveBytes);

        return verification.IsVerified ? TryReadManifestFacts(archiveBytes) : null;
    }

    private static IReadOnlyList<ServicePayloadManifestFileEntryFacts>? TryReadManifestFacts(
        ReadOnlyMemory<byte> archiveBytes)
    {        try
        {
            using var buffer = new MemoryStream(archiveBytes.ToArray(), writable: false);
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

            ZipArchiveEntry? manifestEntry = archive.GetEntry(ServicePayloadArchiveFormat.ManifestEntryName);
            if (manifestEntry is null)
            {
                return null;
            }

            byte[] manifestBytes;
            using (Stream source = manifestEntry.Open())
            using (var target = new MemoryStream())
            {
                source.CopyTo(target);
                manifestBytes = target.ToArray();
            }

            ServicePayloadManifestResult manifest = ServicePayloadManifestContract.Parse(manifestBytes);
            if (!manifest.IsValid)
            {
                return null;
            }

            var facts = new List<ServicePayloadManifestFileEntryFacts>(manifest.Files.Count + 1)
            {
                new(
                    ServicePayloadArchiveFormat.ManifestEntryName,
                    manifestBytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(manifestBytes))),
            };

            foreach (ServicePayloadManifestFileEntry entry in manifest.Files)
            {
                facts.Add(new ServicePayloadManifestFileEntryFacts(entry.Name, entry.SizeBytes, entry.Sha256));
            }

            return facts;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs only after the anchor is DURABLE. It extracts, registers, verifies,
    /// and on any failure compensates every piece of state it created.
    /// </summary>
    public ServiceIdentityBoundTransactionResult Apply(ServiceIdentityBoundTransactionContext context)
    {
        // CYCLE 67. The two ownership facts are derived HERE, from the
        // mutation-free probe plus the channel's anchor-creation fact, and never
        // from emptiness or a matching directory name.
        RecordMachineDataOwnership(context);

        if (!OperatingSystem.IsWindows()
            || _manifestFacts is null
            || _verifiedArchive.IsEmpty
            || string.IsNullOrEmpty(_paths.Final))
        {
            Fail(ServiceEnableOperationOutcome.Unavailable);
            FailureDisposition = ServiceEnableFailureDisposition.RecoveryRequired;
            return ServiceIdentityBoundTransactionResult.RecoveryRequired();
        }

        ServiceEnableFixedPaths paths = _paths;

        try
        {
            // ---- THE EXACT ALREADY-INSTALLED STATE (R4, first half) ---------
            //
            // A matching anchor plus the exact payload, the exact access control
            // and the exact service configuration means nothing is created a
            // second time. Whether that is also a COMPLETED state depends on the
            // activation proof below; registration alone never answers it.
            bool registrationAlreadyExact = IsAlreadyExactlyComplete(paths);
            string? serviceSid;

            if (registrationAlreadyExact)
            {
                serviceSid = _serviceControl.TryResolveServiceSid(ServiceIdentityContract.ServiceName);
                if (string.IsNullOrEmpty(serviceSid))
                {
                    Fail(ServiceEnableOperationOutcome.RecoveryRequired);
                    FailureDisposition = ServiceEnableFailureDisposition.RecoveryRequired;
                    return ServiceIdentityBoundTransactionResult.RecoveryRequired();
                }
            }
            else
            {
                // ---- 1. EXTRACT, WITH THE PROTECTED STAGING PROFILE FIRST ---
                if (!Directory.Exists(paths.Final))
                {
                    ServiceEnableExtractionResult extraction =
                        ServiceEnableExtractor.ExtractVerifiedArchive(_verifiedArchive, paths, _security);

                    RootCreatedThisTransaction |= extraction.RootCreated;
                    StagingCreatedThisTransaction |= extraction.StagingCreated;
                    FinalCreatedThisTransaction |= extraction.FinalCreated;

                    if (!extraction.IsExtracted)
                    {
                        Fail(ServiceEnableOperationOutcome.ExtractionRefused);
                        return Compensate(context);
                    }
                }

                // ---- 2. CREATE THE SERVICE IF ABSENT -----------------------
                ServiceQueryState presence = _serviceControl.QueryConfiguration(
                    ServiceIdentityContract.ServiceName, out ServiceConfigurationSnapshot existing);
                ServiceConfigurationSnapshot expected =
                    ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final);

                if (presence == ServiceQueryState.Absent)
                {
                    if (!_serviceControl.TryCreateService(expected))
                    {
                        // CYCLE 71. CreateServiceW itself refused.
                        Fail(
                            ServiceEnableOperationOutcome.RegistrationRefused,
                            ServiceEnableFailureCause.ServiceCreationRefused);
                        return Compensate(context);
                    }
                    ServiceCreatedThisTransaction = true;
                }
                else if (presence != ServiceQueryState.Present || !expected.MatchesExactly(existing))
                {
                    // A foreign or mismatched service is NEVER modified or deleted.
                    //
                    // CYCLE 71, RULING 2 - A BOUNDED CLASSIFICATION CORRECTION,
                    // NOT PURELY ADDITIVE OBSERVABILITY. This branch's reported
                    // cause and therefore its exit code CHANGE: it is EXISTING
                    // MACHINE STATE that was refused, which is exactly what
                    // ExistingStateRefused already meant, and it was only ever
                    // called "registration" because of where it sits in the
                    // sequence. The BEHAVIOUR is unchanged - nothing here
                    // modifies, adopts or deletes the mismatched service.
                    Fail(
                        ServiceEnableOperationOutcome.RegistrationRefused,
                        ServiceEnableFailureCause.ExistingStateRefused);
                    return Compensate(context);
                }

                // ---- 3. UNRESTRICTED SERVICE SID TYPE ----------------------
                if (!_serviceControl.TrySetUnrestrictedServiceSidType(ServiceIdentityContract.ServiceName))
                {
                    // CYCLE 71. CONFIGURING the SID type refused. Step 4 below
                    // VERIFIES it and has always reported VerificationFailed, so
                    // the two are now distinct on the wire as well as in code.
                    Fail(
                        ServiceEnableOperationOutcome.RegistrationRefused,
                        ServiceEnableFailureCause.ServiceSidConfigurationRefused);
                    return Compensate(context);
                }

                // ---- 4. VERIFY THE FIXED CONFIGURATION AND THE SID TYPE ----
                if (_serviceControl.QueryConfiguration(
                        ServiceIdentityContract.ServiceName, out ServiceConfigurationSnapshot registered)
                        != ServiceQueryState.Present
                    || !expected.MatchesExactly(registered)
                    || !_serviceControl.TryQueryServiceSidType(
                        ServiceIdentityContract.ServiceName, out uint sidType)
                    || sidType != ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted)
                {
                    Fail(ServiceEnableOperationOutcome.VerificationFailed);
                    return Compensate(context);
                }

                // ---- 5. RESOLVE THE NOW-EXISTING SERVICE SID ---------------
                //
                // A deliberate POLICY choice: the SID is deterministic and could
                // have been computed earlier, but this transaction grants rights
                // only to the SID of a service it has already proven exists and
                // is already configured unrestricted.
                serviceSid = _serviceControl.TryResolveServiceSid(ServiceIdentityContract.ServiceName);
                if (string.IsNullOrEmpty(serviceSid))
                {
                    // CYCLE 71. The service is already proven PRESENT and already
                    // proven configured unrestricted, so this is specifically a
                    // SID RESOLUTION refusal and nothing earlier.
                    Fail(
                        ServiceEnableOperationOutcome.RegistrationRefused,
                        ServiceEnableFailureCause.ServiceSidResolutionRefused);
                    return Compensate(context);
                }

                // ---- 6. APPLY THE FINAL PROFILE TO EVERY DIRECTORY AND FILE -
                if (!ApplyFinalProtectionEverywhere(paths.Final, serviceSid!))
                {
                    // CYCLE 71. APPLYING the final Program Files profile refused.
                    // Step 7 below VERIFIES it and reports VerificationFailed.
                    Fail(
                        ServiceEnableOperationOutcome.RegistrationRefused,
                        ServiceEnableFailureCause.ProgramFilesProtectionRefused);
                    return Compensate(context);
                }

                // ---- 7. VERIFY OWNER AND DACL EXACTLY ----------------------
                if (!VerifyFinalProtectionEverywhere(paths.Final, serviceSid!))
                {
                    Fail(ServiceEnableOperationOutcome.VerificationFailed);
                    return Compensate(context);
                }

                // ---- 8. VERIFY ALL FINAL PAYLOAD BYTES AGAIN ---------------
                if (!ServiceEnableExtractor.VerifyExactMembership(paths.Final, _manifestFacts))
                {
                    Fail(ServiceEnableOperationOutcome.VerificationFailed);
                    return Compensate(context);
                }
            }

            // ---- 9. THE REGISTRATION-ONLY TEST SEAM ------------------------
            //
            // A transaction constructed WITHOUT an activation stage stops here,
            // exactly as the cycle-62 shape did. Production always supplies one,
            // so a real enable can never end at registration.
            if (_activation is null)
            {
                return ReportCompleted();
            }

            // ---- 10. ACCESS CONTROL, THE SINGLE START, AND THE PROOF -------
            //
            // R4. An exactly matching installation that is ALREADY RUNNING is
            // never restarted and nothing is recreated: every profile and the
            // whole run state are VERIFIED as they stand. Anything less than a
            // complete verification is RecoveryRequired - the running service is
            // neither stopped nor adopted, and the anchor is preserved.
            bool alreadyRunning =
                _serviceControl.QueryStatus(
                    ServiceIdentityContract.ServiceName, out ServiceStatusSnapshot current)
                    == ServiceQueryState.Present
                && current.State == ServiceRunState.Running;

            bool verifyOnly = registrationAlreadyExact && alreadyRunning;

            ServiceEnableActivationResult activation = _activation.ActivateAndVerify(
                new ServiceEnableActivationRequest(
                    serviceSid!,
                    _serviceDirectory,
                    AnchorPath,
                    RuntimeDirectory,
                    Path.Combine(RuntimeDirectory, ServiceRuntimeDocumentContract.StatusFileName),
                    Path.Combine(RuntimeDirectory, ServiceRuntimeDocumentContract.HeartbeatFileName),
                    applyProfiles: !verifyOnly,
                    startAuthorized: !alreadyRunning,
                    anchorCreatedThisAttempt: context.AnchorCreatedThisAttempt));

            RuntimeDirectoryCreatedThisTransaction |= activation.RuntimeDirectoryCreatedThisAttempt;
            ServiceStartedThisTransaction |= activation.ServiceStartedThisAttempt;

            if (!activation.IsVerified)
            {
                Fail(ClassifyActivationFailure(activation.State), CauseForActivationFailure(activation.State));

                // R5. Only a service THIS attempt created may be stopped and
                // compensated. A service that PRE-EXISTED is preserved exactly
                // as found, along with the anchor that explains it.
                return ServiceCreatedThisTransaction
                    ? Compensate(context)
                    : PreserveAndRequireRecovery();
            }

            // ---- 11. COMPLETED, AND PROVEN RUNNING -------------------------
            return ReportCompleted();
        }
        catch (Exception)
        {
            Fail(ServiceEnableOperationOutcome.Unavailable);
            return Compensate(context);
        }
    }

    /// <summary>
    /// CYCLE 67. The ONE success site. Success requires BOTH a cause of None and
    /// a Completed disposition, so a transaction that recorded a cause earlier
    /// can never report success even if a later step happens to succeed.
    /// </summary>
    private ServiceIdentityBoundTransactionResult ReportCompleted()
    {
        if (FailureCause != ServiceEnableFailureCause.None)
        {
            LastOutcome = ServiceEnableOperationOutcome.RecoveryRequired;
            FailureDisposition = ServiceEnableFailureDisposition.RecoveryRequired;
            return ServiceIdentityBoundTransactionResult.RecoveryRequired();
        }

        LastOutcome = ServiceEnableOperationOutcome.Completed;
        FailureDisposition = ServiceEnableFailureDisposition.Completed;
        return ServiceIdentityBoundTransactionResult.Completed();
    }

    /// <summary>
    /// The TOTAL activation-failure map. Every value is a refusal; there is no
    /// default that could read as success.
    /// </summary>
    internal static ServiceEnableOperationOutcome ClassifyActivationFailure(
        ServiceEnableActivationState state) => state switch
    {
        ServiceEnableActivationState.MetadataProtectionRefused
            or ServiceEnableActivationState.AnchorProtectionRefused
            or ServiceEnableActivationState.RuntimeProtectionRefused =>
            ServiceEnableOperationOutcome.MachineDataProtectionRefused,

        ServiceEnableActivationState.StartRefused
            or ServiceEnableActivationState.NeverReachedRunning
            or ServiceEnableActivationState.ProcessIdentityRefused
            or ServiceEnableActivationState.StatusDocumentRefused
            or ServiceEnableActivationState.StatusMissingTimeout
            or ServiceEnableActivationState.StatusStartingTimeout
            or ServiceEnableActivationState.StatusUnreadable
            or ServiceEnableActivationState.StatusMalformed
            or ServiceEnableActivationState.StatusWrongContext
            or ServiceEnableActivationState.StatusStopped
            or ServiceEnableActivationState.StatusFailed
            or ServiceEnableActivationState.StatusUnknownState
            or ServiceEnableActivationState.HeartbeatDidNotAdvance
            or ServiceEnableActivationState.ForbiddenChildProcess
            or ServiceEnableActivationState.StartLogonRefused
            or ServiceEnableActivationState.StartAccessRefused
            or ServiceEnableActivationState.StartBinaryUnavailable
            or ServiceEnableActivationState.StartDependencyRefused
            or ServiceEnableActivationState.StartRequestTimeout =>
            ServiceEnableOperationOutcome.StartabilityRefused,

        _ => ServiceEnableOperationOutcome.Unavailable,
    };

    /// <summary>
    /// CYCLE 74. The TOTAL activation-failure to CAUSE map - the axis that
    /// actually crosses the process boundary.
    ///
    /// WHY IT IS SEPARATE FROM <see cref="ClassifyActivationFailure"/>. That map
    /// answers a different question and must keep answering it unchanged:
    /// LastOutcome is the value roughly 133 existing tests assert and the disable
    /// transaction shares its vocabulary, so re-pointing it would be a large,
    /// unrelated behaviour change. Only the CAUSE becomes finer.
    ///
    /// THE MACHINE-DATA ARM IS UNTOUCHED. This cycle splits STARTABILITY; the
    /// three access-control profiles keep the cause they already had.
    ///
    /// NOTHING maps to <see cref="ServiceEnableFailureCause.StartabilityRefused"/>.
    /// That member is retained only so an ALREADY-EMITTED exit code still
    /// decodes; no live branch produces it after this cycle.
    ///
    /// Verified and Unspecified are not failures. They reach the closed default
    /// rather than being given an invented category, and so does any future
    /// state added without a mapping - loudly generic instead of silently wrong.
    /// </summary>
    internal static ServiceEnableFailureCause CauseForActivationFailure(
        ServiceEnableActivationState state) => state switch
    {
        ServiceEnableActivationState.MetadataProtectionRefused
            or ServiceEnableActivationState.AnchorProtectionRefused
            or ServiceEnableActivationState.RuntimeProtectionRefused =>
            ServiceEnableFailureCause.MachineDataProtectionRefused,

        ServiceEnableActivationState.StartRefused => ServiceEnableFailureCause.ServiceStartRefused,
        ServiceEnableActivationState.StartLogonRefused => ServiceEnableFailureCause.ServiceStartLogonRefused,
        ServiceEnableActivationState.StartAccessRefused => ServiceEnableFailureCause.ServiceStartAccessRefused,
        ServiceEnableActivationState.StartBinaryUnavailable =>
            ServiceEnableFailureCause.ServiceStartBinaryUnavailable,
        ServiceEnableActivationState.StartDependencyRefused =>
            ServiceEnableFailureCause.ServiceStartDependencyRefused,
        ServiceEnableActivationState.StartRequestTimeout =>
            ServiceEnableFailureCause.ServiceStartRequestTimeout,

        ServiceEnableActivationState.NeverReachedRunning => ServiceEnableFailureCause.ServiceRunningTimeout,
        ServiceEnableActivationState.ProcessIdentityRefused =>
            ServiceEnableFailureCause.ServiceProcessIdentityRefused,
        ServiceEnableActivationState.StatusDocumentRefused =>
            ServiceEnableFailureCause.ServiceStatusDocumentRefused,

        // CYCLE 75. The EIGHT bounded status-readiness verdicts. Each is its own
        // cause on the wire, so the next attended run names WHICH way the status
        // document refused instead of collapsing every possibility onto one
        // token. ServiceStatusDocumentRefused above is retained for backward
        // decoding; the production path can no longer reach it, because the
        // coordinator no longer produces StatusDocumentRefused.
        ServiceEnableActivationState.StatusMissingTimeout =>
            ServiceEnableFailureCause.ServiceStatusMissingTimeout,
        ServiceEnableActivationState.StatusStartingTimeout =>
            ServiceEnableFailureCause.ServiceStatusStartingTimeout,
        ServiceEnableActivationState.StatusUnreadable =>
            ServiceEnableFailureCause.ServiceStatusUnreadable,
        ServiceEnableActivationState.StatusMalformed =>
            ServiceEnableFailureCause.ServiceStatusMalformed,
        ServiceEnableActivationState.StatusWrongContext =>
            ServiceEnableFailureCause.ServiceStatusWrongContext,
        ServiceEnableActivationState.StatusStopped =>
            ServiceEnableFailureCause.ServiceStatusStopped,
        ServiceEnableActivationState.StatusFailed =>
            ServiceEnableFailureCause.ServiceStatusFailed,
        ServiceEnableActivationState.StatusUnknownState =>
            ServiceEnableFailureCause.ServiceStatusUnknownState,
        ServiceEnableActivationState.HeartbeatDidNotAdvance =>
            ServiceEnableFailureCause.ServiceHeartbeatRefused,
        ServiceEnableActivationState.ForbiddenChildProcess =>
            ServiceEnableFailureCause.ServiceForbiddenChildRefused,

        _ => ServiceEnableFailureCause.Unavailable,
    };

    /// <summary>
    /// R5. State this attempt did not create is PRESERVED exactly as found. It
    /// is not stopped, not deleted, not adopted and not repaired, and the anchor
    /// that explains it is left in place for an attended recovery.
    /// </summary>
    private ServiceIdentityBoundTransactionResult PreserveAndRequireRecovery()
    {
        FailureDisposition = ServiceEnableFailureDisposition.RecoveryRequired;
        return ServiceIdentityBoundTransactionResult.RecoveryRequired();
    }

    /// <summary>
    /// The exact idempotent-completion predicate. Every clause must hold; a
    /// single difference means this is not a completed installation.
    /// </summary>
    private bool IsAlreadyExactlyComplete(ServiceEnableFixedPaths paths)
    {
        if (!Directory.Exists(paths.Final)
            || _manifestFacts is null
            || !ServiceEnableExtractor.VerifyExactMembership(paths.Final, _manifestFacts))
        {
            return false;
        }

        if (_serviceControl.QueryConfiguration(
                ServiceIdentityContract.ServiceName, out ServiceConfigurationSnapshot existing)
                != ServiceQueryState.Present
            || !ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final).MatchesExactly(existing))
        {
            return false;
        }

        if (!_serviceControl.TryQueryServiceSidType(ServiceIdentityContract.ServiceName, out uint sidType)
            || sidType != ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted)
        {
            return false;
        }

        string? serviceSid = _serviceControl.TryResolveServiceSid(ServiceIdentityContract.ServiceName);
        return !string.IsNullOrEmpty(serviceSid)
            && VerifyFinalProtectionEverywhere(paths.Final, serviceSid!);
    }

    private ServiceIdentityBoundTransactionResult Compensate(ServiceIdentityBoundTransactionContext context)
    {
        if (!TryCompensate(context, out bool anchorRemoved))
        {
            // CYCLE 67. LastOutcome is still OVERWRITTEN here, because roughly
            // 133 existing tests assert exactly that; the sticky FailureCause is
            // what preserves WHY, and only the DISPOSITION changes.
            LastOutcome = ServiceEnableOperationOutcome.RecoveryRequired;
            FailureDisposition = ServiceEnableFailureDisposition.RecoveryRequired;
            return ServiceIdentityBoundTransactionResult.RecoveryRequired();
        }

        LastOutcome = ServiceEnableOperationOutcome.Compensated;
        FailureDisposition = ServiceEnableFailureDisposition.Compensated;
        return ServiceIdentityBoundTransactionResult.Compensated(anchorRemoved);
    }

    /// <summary>
    /// THE COMPENSATION SEQUENCE, exposed so the focused tests can drive every
    /// failure point directly. It undoes ONLY what this transaction created, in
    /// reverse order. If any step cannot be proven, it returns false and the
    /// caller must report RecoveryRequired with the anchor preserved.
    ///
    /// THE FIXED ORDER: stop a service THIS attempt started, delete a service
    /// THIS attempt created, remove a runtime directory THIS attempt created,
    /// remove Program Files state THIS attempt created, remove an anchor THIS
    /// attempt created, then - CYCLE 67 - remove the fixed metadata Service
    /// directory and then the fixed ProgramData machine root, but ONLY when THIS
    /// attempt created them, and finally prove both absent.
    /// </summary>
    internal bool TryCompensate(ServiceIdentityBoundTransactionContext context, out bool anchorRemoved)
    {
        anchorRemoved = false;
        ServiceEnableFixedPaths paths = _paths;

        try
        {
            // 0. A service THIS transaction STARTED is stopped first, so the
            //    delete that follows cannot race a live process. A service that
            //    was already running when this attempt began is never stopped.
            if (ServiceStartedThisTransaction)
            {
                if (_activation is null || !_activation.TryStopServiceStartedThisAttempt())
                {
                    return false;
                }

                ServiceStartedThisTransaction = false;
            }

            // 1. A service THIS transaction created is deleted; one it did not
            //    create is never touched.
            if (ServiceCreatedThisTransaction)
            {
                if (!_serviceControl.TryDeleteService(ServiceIdentityContract.ServiceName))
                {
                    return false;
                }

                // Bounded eventual absence, verified rather than assumed.
                if (_serviceControl.QueryConfiguration(
                        ServiceIdentityContract.ServiceName, out _) != ServiceQueryState.Absent)
                {
                    return false;
                }

                ServiceCreatedThisTransaction = false;
            }

            // 2. A runtime directory THIS transaction created, and only when it
            //    holds nothing but the CLOSED runtime leaves.
            if (RuntimeDirectoryCreatedThisTransaction)
            {
                if (_activation is null
                    || !_activation.TryRemoveRuntimeDirectoryCreatedThisAttempt(RuntimeDirectory))
                {
                    return false;
                }

                RuntimeDirectoryCreatedThisTransaction = false;
            }

            // 3. Transaction-created payload directories, whose membership is
            //    still closed. A pre-existing final directory is never removed.
            if (FinalCreatedThisTransaction && Directory.Exists(paths.Final))
            {
                if (_manifestFacts is null
                    || !ServiceEnableExtractor.VerifyExactMembership(paths.Final, _manifestFacts))
                {
                    return false;
                }
                Directory.Delete(paths.Final, recursive: true);
                FinalCreatedThisTransaction = false;
            }

            if (StagingCreatedThisTransaction && Directory.Exists(paths.Staging))
            {
                Directory.Delete(paths.Staging, recursive: true);
                StagingCreatedThisTransaction = false;
            }

            // 4. Transaction-created directories only when EMPTY.
            if (RootCreatedThisTransaction && Directory.Exists(paths.Root))
            {
                if (Directory.GetFileSystemEntries(paths.Root).Length != 0)
                {
                    return false;
                }
                Directory.Delete(paths.Root, recursive: false);
                RootCreatedThisTransaction = false;
            }

            // 5. THE ANCHOR, and only when every condition holds.
            if (!context.AnchorCreatedThisAttempt)
            {
                // A REUSED anchor means the fixed machine data directories were
                // not created by this attempt either, so nothing beneath
                // %ProgramData% is removed.
                return true;
            }

            ServiceEnableAnchorRemovalFacts facts = GatherAnchorRemovalFacts(context, paths);
            if (!AnchorRemovalAuthorized(facts))
            {
                // A newly created anchor that cannot be proven safe to remove is
                // PRESERVED, and that is a recovery-required outcome rather than
                // a clean compensation.
                return false;
            }

            ServiceInstallationAnchorRemovalState removal =
                ServiceInstallationAnchorStore.RemoveAnchorCreatedThisTransactionFrom(
                    context.ServiceDirectory, context.Anchor);

            if (removal != ServiceInstallationAnchorRemovalState.Removed)
            {
                return false;
            }

            anchorRemoved = true;

            // 6/7/8. THE FIXED MACHINE DATA DIRECTORIES, and only ones THIS
            //        attempt created. A FAILED anchor removal can never reach
            //        here, so a surviving anchor always blocks parent removal.
            return TryRemoveMachineDataDirectoriesCreatedThisTransaction();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// CYCLE 67, COMPENSATION STEPS 7, 8 AND 9. Before this cycle, compensation
    /// stopped at the anchor and left %ProgramData%\PAXCookbook\Service and
    /// %ProgramData%\PAXCookbook behind - which is exactly the residue the first
    /// attended enable failure left on the pilot VM.
    ///
    /// EVERY CONDITION MUST HOLD, for each directory independently: this attempt
    /// created it, the anchor removal that precedes it already succeeded, it is
    /// EMPTY, it is NOT a reparse point, and its path is the exact expected
    /// fixed shape. A pre-existing or reused directory is NEVER removed, and
    /// emptiness alone is never treated as proof of ownership.
    /// </summary>
    private bool TryRemoveMachineDataDirectoriesCreatedThisTransaction()
    {
        // 7. The metadata Service directory. A directory this attempt did not
        //    create is left exactly as found, and that is a clean compensation.
        if (!MetadataDirectoryCreatedThisTransaction)
        {
            return true;
        }

        string? root = MachineRootDirectory;
        if (string.IsNullOrEmpty(root) || !IsExpectedMetadataDirectory(_serviceDirectory, root!))
        {
            return false;
        }

        if (!TryRemoveEmptyDirectoryCreatedThisTransaction(_serviceDirectory))
        {
            return false;
        }

        MetadataDirectoryCreatedThisTransaction = false;

        // 8. The ProgramData machine root, and ONLY when the metadata child
        //    beneath it was removed by the step above.
        if (MachineRootCreatedThisTransaction)
        {
            if (!IsExpectedMachineRoot(root!)
                || !TryRemoveEmptyDirectoryCreatedThisTransaction(root!))
            {
                return false;
            }

            MachineRootCreatedThisTransaction = false;

            // 9. Proven absent, not assumed absent.
            if (Directory.Exists(root!) || File.Exists(root!))
            {
                return false;
            }
        }

        // 9. Proven absent, not assumed absent.
        return !Directory.Exists(_serviceDirectory) && !File.Exists(_serviceDirectory);
    }

    /// <summary>
    /// THE reparse-point guard, as a PURE predicate so it can be proven without
    /// creating a junction - which would require a privilege no focused test may
    /// assume. A reparse point is never removed, because deleting one deletes a
    /// link into state this transaction never created.
    /// </summary>
    internal static bool DirectoryAttributesPermitRemoval(FileAttributes attributes) =>
        !attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    /// Removes ONE directory only when it is empty and is not a reparse point.
    /// An unknown sibling, a file of the same name, a junction or any access
    /// failure is a refusal, never a forced delete.
    /// </summary>
    private static bool TryRemoveEmptyDirectoryCreatedThisTransaction(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                // Nothing to remove is only acceptable when nothing is there at all.
                return !File.Exists(path);
            }

            var info = new DirectoryInfo(path);
            if (!DirectoryAttributesPermitRemoval(info.Attributes))
            {
                return false;
            }

            if (Directory.GetFileSystemEntries(path).Length != 0)
            {
                return false;
            }

            Directory.Delete(path, recursive: false);
            return !Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The metadata directory must be EXACTLY the fixed Service child of its own
    /// parent, composed from the shared storage contract's names alone. It is
    /// never a caller-shaped path and never a name match by coincidence.
    /// </summary>
    private static bool IsExpectedMetadataDirectory(string metadataDirectory, string root)
    {
        try
        {
            string expected = Path.GetFullPath(
                Path.Combine(root, ServiceMachineStorageContract.ServiceDataFolderName));

            return string.Equals(
                Path.GetFullPath(metadataDirectory), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The machine root must carry the fixed product folder name and must have a
    /// real, DIFFERENT, EXISTING parent - so a volume root or a path whose
    /// parent has vanished can never be deleted.
    /// </summary>
    private static bool IsExpectedMachineRoot(string root)
    {
        try
        {
            string full = Path.GetFullPath(root);
            string? parent = Path.GetDirectoryName(full);

            return !string.IsNullOrEmpty(parent)
                && !string.Equals(full, parent, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetFileName(full),
                    ServiceMachineStorageContract.MachineRootFolderName,
                    StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(parent);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private ServiceEnableAnchorRemovalFacts GatherAnchorRemovalFacts(
        ServiceIdentityBoundTransactionContext context, ServiceEnableFixedPaths paths)
    {
        ServiceInstallationAnchorReadResult current =
            ServiceInstallationAnchorStore.ReadFrom(context.ServiceDirectory);

        bool identityMatches =
            current.State == ServiceInstallationAnchorReadState.Validated
            && ServiceInstallationAnchorContract.HasIdenticalImmutableIdentity(
                current.Validation?.Document, context.Anchor);

        bool serviceAbsent = _serviceControl.QueryConfiguration(
            ServiceIdentityContract.ServiceName, out _) == ServiceQueryState.Absent;

        string ledgerPath = Path.Combine(
            context.ServiceDirectory, ServiceMachineStorageContract.OwnershipLedgerFileName);
        string temporaryPath = Path.Combine(
            context.ServiceDirectory, ServiceInstallationAnchorStore.TemporaryLeafFileName);

        // EXISTENCE ONLY. The ledger is never opened, parsed, validated or
        // enumerated, and no ledger reader is linked into this assembly.
        bool ledgerAbsent = !File.Exists(ledgerPath) && !Directory.Exists(ledgerPath);
        bool temporaryAbsent = !File.Exists(temporaryPath) && !Directory.Exists(temporaryPath);

        bool onlyAnchor = false;
        try
        {
            string[] entries = Directory.Exists(context.ServiceDirectory)
                ? Directory.GetFileSystemEntries(context.ServiceDirectory)
                : Array.Empty<string>();

            onlyAnchor = entries.All(entry =>
                string.Equals(
                    Path.GetFileName(entry),
                    ServiceMachineStorageContract.InstallationAnchorFileName,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetFileName(entry),
                    ServiceInstallationAnchorStore.TemporaryLeafFileName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            onlyAnchor = false;
        }

        return new ServiceEnableAnchorRemovalFacts(
            context.AnchorCreatedThisAttempt,
            identityMatches,
            serviceAbsent,
            !Directory.Exists(paths.Final) && !File.Exists(paths.Final),
            !Directory.Exists(paths.Staging) && !File.Exists(paths.Staging),
            ledgerAbsent,
            onlyAnchor,
            temporaryAbsent);
    }

    /// <summary>
    /// The R3 closed-state predicate: may a newly created anchor be removed?
    /// EVERY condition must hold. It is deliberately a pure function of already
    /// gathered facts so the whole rule is visible in one place and testable.
    /// </summary>
    internal static bool AnchorRemovalAuthorized(ServiceEnableAnchorRemovalFacts facts) =>
        facts.TransactionCreatedThisAnchor
        && facts.AnchorIdentityStillMatches
        && facts.FixedServiceAbsent
        && facts.FinalPathAbsent
        && facts.StagingPathAbsent
        && facts.OwnershipLedgerPathAbsent
        && facts.ServiceDirectoryHoldsOnlyAnchor
        && facts.TemporaryLeafAbsent;

    // ---- CYCLE 68: THE POST-ANCHOR-WRITE FAILURE RECOVERY --------------------
    //
    // WHY IT EXISTS. WriteTo creates the fixed machine root and metadata
    // directories as the FIRST durable statement of its write. When persistence
    // then fails, the channel returns on the wire BEFORE Apply - so Compensate,
    // and therefore every compensation step above, is UNREACHABLE for that path.
    // Cycle 67 disclosed that as knownRisk B2 and deferred it. This is its
    // narrow, fail-closed close.
    //
    // WHAT IT IS NOT. It is not a recovery, reset or purge verb. It takes no
    // path and no deletion target - only the bounded write state the store
    // itself reported. It deletes NOTHING recursively: the only deletion it can
    // reach is the existing empty-directory primitive above. And NEITHER of its
    // two result values ever means enablement succeeded.

    /// <summary>
    /// CYCLE 68. THE ONE ENTRY POINT for undoing a post-anchor-write residue.
    ///
    /// It acts ONLY for a DEFINED write state that the store itself classifies
    /// as possibly having created the fixed machine directories, and which is
    /// not one of the two success states. Everything else - a pre-create
    /// refusal, a success, or a value this build cannot name - is refused
    /// outright with nothing removed.
    /// </summary>
    public ServiceAnchorPersistenceRecoveryState RecoverAfterAnchorPersistenceFailure(
        ServiceInstallationAnchorWriteState state)
    {
        // The original failure cause is NOT known on this path and is NOT
        // guessed. RecordCause is STICKY, so a truthful earlier cause always
        // wins over this deliberately honest Unavailable.
        RecordCause(ServiceEnableFailureCause.Unavailable);

        bool compensated = IsPostCreateAnchorWriteFailure(state)
            && TryRemoveAnchorPersistenceResidue();

        LastOutcome = compensated
            ? ServiceEnableOperationOutcome.Compensated
            : ServiceEnableOperationOutcome.RecoveryRequired;
        FailureDisposition = compensated
            ? ServiceEnableFailureDisposition.Compensated
            : ServiceEnableFailureDisposition.RecoveryRequired;

        return compensated
            ? ServiceAnchorPersistenceRecoveryState.Compensated
            : ServiceAnchorPersistenceRecoveryState.RecoveryRequired;
    }

    /// <summary>
    /// TRUE only for a write state that is DEFINED by this build, is not one of
    /// the two success states, and which the STORE - the one owner of the
    /// mutation boundary - says may have created the fixed directories.
    ///
    /// An UNDEFINED cast is refused here even though
    /// <c>MayHaveCreatedDirectories</c> reports true for it. The two answers are
    /// not in conflict: the store is asked "could this have mutated?" and
    /// answers yes for an unknown value, while this predicate is asked "may I
    /// DELETE for this value?" and both questions fail closed.
    /// </summary>
    private static bool IsPostCreateAnchorWriteFailure(ServiceInstallationAnchorWriteState state) =>
        Enum.IsDefined(state)
        && state is not ServiceInstallationAnchorWriteState.Created
        && state is not ServiceInstallationAnchorWriteState.AlreadyMatching
        && ServiceInstallationAnchorStore.MayHaveCreatedDirectories(state);

    /// <summary>
    /// CYCLE 68 SCAFFOLD REPLACED. Removes the fixed machine-data residue a
    /// FAILED anchor write may have created, in ONE fixed order, non-recursively,
    /// and only when the closed predicates authorize it.
    ///
    /// A PRE-EXISTING DIRECTORY IS ALWAYS PRESERVED. Preserving a machine root
    /// this attempt did not create is still a CLEAN compensation - it is the
    /// truthful outcome, not a partial one - but preserving anything for a
    /// reason the predicate could not verify is NOT, and returns false.
    /// </summary>
    private bool TryRemoveAnchorPersistenceResidue()
    {
        try
        {
            string? root = MachineRootDirectory;
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            ServiceEnableAnchorResidueFacts facts = GatherAnchorResidueFacts(root!);
            if (!AnchorResidueMetadataRemovalAuthorized(facts))
            {
                return false;
            }

            // 1. THE METADATA DIRECTORY, non-recursively, then PROVEN absent.
            if (!TryRemoveEmptyDirectoryCreatedThisTransaction(_serviceDirectory)
                || Directory.Exists(_serviceDirectory)
                || File.Exists(_serviceDirectory))
            {
                return false;
            }

            // 2. A machine root that already existed when the mutation-free
            //    probe ran was NOT created by this attempt, so it is preserved
            //    and nothing further is owed.
            if (!facts.MachineRootAbsentAtPreflight)
            {
                return true;
            }

            // 3. THE MACHINE ROOT, only when every root condition also holds.
            if (!AnchorResidueMachineRootRemovalAuthorized(facts)
                || !TryRemoveEmptyDirectoryCreatedThisTransaction(root!))
            {
                return false;
            }

            // 4. PROVEN absent, not assumed absent, for BOTH paths.
            return !Directory.Exists(root!)
                && !File.Exists(root!)
                && !Directory.Exists(_serviceDirectory)
                && !File.Exists(_serviceDirectory);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Gathers the complete residue snapshot. EVERY observation fails to FALSE:
    /// an access failure, a vanished path or an unreadable attribute is never
    /// read as permission to delete.
    /// </summary>
    private ServiceEnableAnchorResidueFacts GatherAnchorResidueFacts(string root)
    {
        bool metadataIsDirectory = false;
        bool metadataNotReparse = false;
        bool metadataEmpty = false;
        try
        {
            if (Directory.Exists(_serviceDirectory))
            {
                metadataIsDirectory = true;
                metadataNotReparse = DirectoryAttributesPermitRemoval(
                    new DirectoryInfo(_serviceDirectory).Attributes);
                metadataEmpty = Directory.GetFileSystemEntries(_serviceDirectory).Length == 0;
            }
        }
        catch (Exception)
        {
            metadataIsDirectory = false;
            metadataNotReparse = false;
            metadataEmpty = false;
        }

        bool rootIsDirectory = false;
        bool rootNotReparse = false;
        bool rootHoldsOnlyMetadata = false;
        try
        {
            if (Directory.Exists(root))
            {
                rootIsDirectory = true;
                rootNotReparse = DirectoryAttributesPermitRemoval(new DirectoryInfo(root).Attributes);

                // ANY unknown sibling is a refusal: it is state beside the
                // residue that this attempt cannot explain.
                rootHoldsOnlyMetadata = true;
                foreach (string entry in Directory.GetFileSystemEntries(root))
                {
                    if (!string.Equals(
                            Path.GetFileName(entry),
                            ServiceMachineStorageContract.ServiceDataFolderName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        rootHoldsOnlyMetadata = false;
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            rootIsDirectory = false;
            rootNotReparse = false;
            rootHoldsOnlyMetadata = false;
        }

        bool serviceAbsent;
        try
        {
            serviceAbsent = _serviceControl.QueryConfiguration(
                ServiceIdentityContract.ServiceName, out _) == ServiceQueryState.Absent;
        }
        catch (Exception)
        {
            serviceAbsent = false;
        }

        // An UNRESOLVED fixed path is never read as an absent footprint.
        ServiceEnableFixedPaths paths = _paths;
        bool finalAbsent = !string.IsNullOrEmpty(paths.Final)
            && !Directory.Exists(paths.Final) && !File.Exists(paths.Final);
        bool stagingAbsent = !string.IsNullOrEmpty(paths.Staging)
            && !Directory.Exists(paths.Staging) && !File.Exists(paths.Staging);

        string anchorPath = AnchorPath;
        string temporaryPath = Path.Combine(
            _serviceDirectory, ServiceInstallationAnchorStore.TemporaryLeafFileName);
        string ledgerPath = Path.Combine(
            _serviceDirectory, ServiceMachineStorageContract.OwnershipLedgerFileName);
        string runtimePath = RuntimeDirectory;

        // THE EXACT FIXED SHAPE, composed from the shared contract's names only.
        bool exactParent = IsExpectedMetadataDirectory(_serviceDirectory, root);

        return new ServiceEnableAnchorResidueFacts(
            machineDataProbeCompleted: _machineDataProbeCompleted,
            metadataDirectoryAbsentAtPreflight: _metadataDirectoryAbsentAtPreflight,
            fixedServiceAbsent: serviceAbsent,
            finalPathAbsent: finalAbsent,
            stagingPathAbsent: stagingAbsent,
            anchorPathAbsent: !File.Exists(anchorPath) && !Directory.Exists(anchorPath),
            anchorTemporaryPathAbsent: !File.Exists(temporaryPath) && !Directory.Exists(temporaryPath),
            ownershipLedgerPathAbsent: !File.Exists(ledgerPath) && !Directory.Exists(ledgerPath),
            runtimePathAbsent: !Directory.Exists(runtimePath) && !File.Exists(runtimePath),
            metadataDirectoryIsRealDirectory: metadataIsDirectory,
            metadataDirectoryIsNotReparsePoint: metadataNotReparse,
            metadataDirectoryIsEmpty: metadataEmpty,
            metadataDirectoryHasExactFixedParent: exactParent,
            machineRootHoldsOnlyMetadataDirectory: rootHoldsOnlyMetadata,
            machineRootAbsentAtPreflight: _machineRootAbsentAtPreflight,
            machineRootIsRealDirectory: rootIsDirectory,
            machineRootIsNotReparsePoint: rootNotReparse,
            machineRootIsExpectedShape: IsExpectedMachineRoot(root) && exactParent);
    }

    /// <summary>
    /// THE CLOSED PREDICATE for removing the fixed metadata Service directory
    /// after a failed anchor write. EVERY clause must hold. It is deliberately a
    /// pure function of already gathered facts so the whole rule is visible in
    /// one place and every clause can be shown to be load bearing.
    ///
    /// EMPTINESS IS NEVER OWNERSHIP. The load-bearing authority is
    /// <see cref="ServiceEnableAnchorResidueFacts.MetadataDirectoryAbsentAtPreflight"/>,
    /// captured by the MUTATION-FREE probe before anything could create it;
    /// every other clause narrows what may be removed, and none of them widens
    /// it.
    /// </summary>
    internal static bool AnchorResidueMetadataRemovalAuthorized(ServiceEnableAnchorResidueFacts facts) =>
        facts.MachineDataProbeCompleted
        && facts.MetadataDirectoryAbsentAtPreflight
        && facts.FixedServiceAbsent
        && facts.FinalPathAbsent
        && facts.StagingPathAbsent
        && facts.AnchorPathAbsent
        && facts.AnchorTemporaryPathAbsent
        && facts.OwnershipLedgerPathAbsent
        && facts.RuntimePathAbsent
        && facts.MetadataDirectoryIsRealDirectory
        && facts.MetadataDirectoryIsNotReparsePoint
        && facts.MetadataDirectoryIsEmpty
        && facts.MetadataDirectoryHasExactFixedParent
        && facts.MachineRootHoldsOnlyMetadataDirectory;

    /// <summary>
    /// THE CLOSED PREDICATE for removing the fixed ProgramData machine root. It
    /// is strictly narrower than the metadata predicate: the root can never be
    /// removed unless the child beneath it was authorized and removed first.
    /// </summary>
    internal static bool AnchorResidueMachineRootRemovalAuthorized(ServiceEnableAnchorResidueFacts facts) =>
        AnchorResidueMetadataRemovalAuthorized(facts)
        && facts.MachineRootAbsentAtPreflight
        && facts.MachineRootIsRealDirectory
        && facts.MachineRootIsNotReparsePoint
        && facts.MachineRootIsExpectedShape;
}

/// <summary>
/// CYCLE 68. The complete, closed set of facts that may authorize removing the
/// fixed machine-data directories a FAILED anchor write may have created. It is
/// a plain snapshot: it holds no handle, performs no I/O and grants no
/// capability. Every member defaults to false, so a default snapshot authorizes
/// nothing at all.
/// </summary>
internal readonly struct ServiceEnableAnchorResidueFacts
{
    internal ServiceEnableAnchorResidueFacts(
        bool machineDataProbeCompleted,
        bool metadataDirectoryAbsentAtPreflight,
        bool fixedServiceAbsent,
        bool finalPathAbsent,
        bool stagingPathAbsent,
        bool anchorPathAbsent,
        bool anchorTemporaryPathAbsent,
        bool ownershipLedgerPathAbsent,
        bool runtimePathAbsent,
        bool metadataDirectoryIsRealDirectory,
        bool metadataDirectoryIsNotReparsePoint,
        bool metadataDirectoryIsEmpty,
        bool metadataDirectoryHasExactFixedParent,
        bool machineRootHoldsOnlyMetadataDirectory,
        bool machineRootAbsentAtPreflight,
        bool machineRootIsRealDirectory,
        bool machineRootIsNotReparsePoint,
        bool machineRootIsExpectedShape)
    {
        MachineDataProbeCompleted = machineDataProbeCompleted;
        MetadataDirectoryAbsentAtPreflight = metadataDirectoryAbsentAtPreflight;
        FixedServiceAbsent = fixedServiceAbsent;
        FinalPathAbsent = finalPathAbsent;
        StagingPathAbsent = stagingPathAbsent;
        AnchorPathAbsent = anchorPathAbsent;
        AnchorTemporaryPathAbsent = anchorTemporaryPathAbsent;
        OwnershipLedgerPathAbsent = ownershipLedgerPathAbsent;
        RuntimePathAbsent = runtimePathAbsent;
        MetadataDirectoryIsRealDirectory = metadataDirectoryIsRealDirectory;
        MetadataDirectoryIsNotReparsePoint = metadataDirectoryIsNotReparsePoint;
        MetadataDirectoryIsEmpty = metadataDirectoryIsEmpty;
        MetadataDirectoryHasExactFixedParent = metadataDirectoryHasExactFixedParent;
        MachineRootHoldsOnlyMetadataDirectory = machineRootHoldsOnlyMetadataDirectory;
        MachineRootAbsentAtPreflight = machineRootAbsentAtPreflight;
        MachineRootIsRealDirectory = machineRootIsRealDirectory;
        MachineRootIsNotReparsePoint = machineRootIsNotReparsePoint;
        MachineRootIsExpectedShape = machineRootIsExpectedShape;
    }

    /// <summary>The MUTATION-FREE preflight probe actually ran to completion.</summary>
    internal bool MachineDataProbeCompleted { get; }

    /// <summary>The fixed metadata Service directory was ABSENT when the probe ran.</summary>
    internal bool MetadataDirectoryAbsentAtPreflight { get; }

    /// <summary>The fixed SCM service is absent.</summary>
    internal bool FixedServiceAbsent { get; }

    /// <summary>The fixed Program Files FINAL path is absent.</summary>
    internal bool FinalPathAbsent { get; }

    /// <summary>The fixed Program Files STAGING path is absent.</summary>
    internal bool StagingPathAbsent { get; }

    /// <summary>Nothing exists at the fixed installation-anchor path.</summary>
    internal bool AnchorPathAbsent { get; }

    /// <summary>Nothing exists at the fixed same-directory temporary leaf path.</summary>
    internal bool AnchorTemporaryPathAbsent { get; }

    /// <summary>
    /// Nothing exists at the fixed ownership-ledger path. EXISTENCE ONLY: the
    /// ledger is never parsed, opened, validated or enumerated.
    /// </summary>
    internal bool OwnershipLedgerPathAbsent { get; }

    /// <summary>Nothing exists at the fixed Runtime path.</summary>
    internal bool RuntimePathAbsent { get; }

    /// <summary>The metadata path is a REAL directory, not a file and not absent.</summary>
    internal bool MetadataDirectoryIsRealDirectory { get; }

    /// <summary>The metadata directory is not a reparse point.</summary>
    internal bool MetadataDirectoryIsNotReparsePoint { get; }

    /// <summary>The metadata directory holds no member of any kind.</summary>
    internal bool MetadataDirectoryIsEmpty { get; }

    /// <summary>
    /// The metadata directory is EXACTLY the fixed Service child of the fixed
    /// machine root, composed from the shared storage contract's names alone.
    /// </summary>
    internal bool MetadataDirectoryHasExactFixedParent { get; }

    /// <summary>
    /// The fixed machine root contains NO member except the fixed metadata
    /// directory. Any unknown sibling is a refusal - it means state this attempt
    /// cannot explain lives beside the residue.
    /// </summary>
    internal bool MachineRootHoldsOnlyMetadataDirectory { get; }

    /// <summary>The fixed machine root was ABSENT when the probe ran.</summary>
    internal bool MachineRootAbsentAtPreflight { get; }

    /// <summary>The machine root path is a REAL directory.</summary>
    internal bool MachineRootIsRealDirectory { get; }

    /// <summary>The machine root is not a reparse point.</summary>
    internal bool MachineRootIsNotReparsePoint { get; }

    /// <summary>
    /// The machine root carries the exact fixed product folder name, has a real,
    /// DIFFERENT, EXISTING parent, and is exactly the parent of this
    /// transaction's fixed metadata directory.
    /// </summary>
    internal bool MachineRootIsExpectedShape { get; }

    /// <summary>Carries the bounded type name only.</summary>
    public override string ToString() => nameof(ServiceEnableAnchorResidueFacts);
}

/// <summary>
/// The complete, closed set of facts that authorize removing an anchor this
/// transaction created. It is a plain snapshot and grants no capability.
/// </summary>
internal readonly struct ServiceEnableAnchorRemovalFacts
{
    internal ServiceEnableAnchorRemovalFacts(
        bool transactionCreatedThisAnchor,
        bool anchorIdentityStillMatches,
        bool fixedServiceAbsent,
        bool finalPathAbsent,
        bool stagingPathAbsent,
        bool ownershipLedgerPathAbsent,
        bool serviceDirectoryHoldsOnlyAnchor,
        bool temporaryLeafAbsent)
    {
        TransactionCreatedThisAnchor = transactionCreatedThisAnchor;
        AnchorIdentityStillMatches = anchorIdentityStillMatches;
        FixedServiceAbsent = fixedServiceAbsent;
        FinalPathAbsent = finalPathAbsent;
        StagingPathAbsent = stagingPathAbsent;
        OwnershipLedgerPathAbsent = ownershipLedgerPathAbsent;
        ServiceDirectoryHoldsOnlyAnchor = serviceDirectoryHoldsOnlyAnchor;
        TemporaryLeafAbsent = temporaryLeafAbsent;
    }

    /// <summary>This very attempt created the anchor. A reused anchor is NEVER removed.</summary>
    internal bool TransactionCreatedThisAnchor { get; }

    /// <summary>The immutable anchor identity on disk still matches what was written.</summary>
    internal bool AnchorIdentityStillMatches { get; }

    /// <summary>The fixed SCM service is absent.</summary>
    internal bool FixedServiceAbsent { get; }

    /// <summary>The fixed final path is absent.</summary>
    internal bool FinalPathAbsent { get; }

    /// <summary>The fixed staging path is absent.</summary>
    internal bool StagingPathAbsent { get; }

    /// <summary>
    /// Nothing exists at the fixed ownership-ledger path. EXISTENCE ONLY: the
    /// ledger is never parsed, opened, validated or enumerated.
    /// </summary>
    internal bool OwnershipLedgerPathAbsent { get; }

    /// <summary>
    /// The fixed ProgramData Service directory contains no member except the
    /// matching anchor and its fixed temporary leaf. Any unknown sibling is a
    /// refusal.
    /// </summary>
    internal bool ServiceDirectoryHoldsOnlyAnchor { get; }

    /// <summary>The fixed temporary leaf is itself absent.</summary>
    internal bool TemporaryLeafAbsent { get; }

    /// <summary>Carries the bounded type name only.</summary>
    public override string ToString() => nameof(ServiceEnableAnchorRemovalFacts);
}
