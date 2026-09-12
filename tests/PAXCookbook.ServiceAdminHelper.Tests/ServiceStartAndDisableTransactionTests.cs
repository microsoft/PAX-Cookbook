using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using PAXCookbook.ServiceAdminHelper.Enable;
using PAXCookbook.ServiceAdminHelper.Payload;
using PAXCookbook.ServiceAdminHelper.Signing;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbook.ServiceAdminHelper.Tests;

// ===========================================================================
// CYCLE 63RR - START, VERIFY AND OWNER-BOUND DISABLE (helper side)
// ===========================================================================
//
// SCOPE, stated plainly. Every collaborator is INJECTED and every destination
// lives in an OS TEMP directory. Nothing in this file elevates, triggers a UAC
// prompt, uses runas, starts ANY process, launches the helper, Setup, the App,
// the service, PAX or a Bake, touches the real Program Files or ProgramData
// tree, queries or mutates the real Service Control Manager, starts stops or
// deletes a real service, writes a real security descriptor, opens a
// certificate store, private key or credential vault, reads or writes the
// registry, or opens a socket. The REAL known-folder resolver, the REAL Windows
// security adapters, the REAL SCM adapter, the REAL process verifier and the
// REAL child-process observer are never constructed.
//
// WHAT THIS PROVES, AND WHAT IT CANNOT PROVE. It proves the ORDERING, the
// EXACT expected profiles, the ownership rules and every refusal, against
// injected fakes. It proves NOTHING about real hardware: no service was ever
// started, no descriptor was ever written, and startability on a real machine
// remains UNPROVEN until an attended run on a disposable Windows target.
//
// WHAT THIS CLASS OWNS. The activation stage (the three machine-data profiles,
// the single authorized start and the full startability proof), the enable
// transaction's R4 already-running idempotence and R5 ownership-dependent
// compensation, and the elevated owner-bound disable transaction. The
// non-elevated disable coordinator lives in the Setup assembly and is proven by
// ServiceDisableCoordinatorTests.
public sealed class ServiceStartAndDisableTransactionTests : IDisposable
{
    // Every sub-authority fits in a uint32. A larger literal is silently clamped
    // by SecurityIdentifier, which would make the SID string and the ACE
    // disagree and turn every profile comparison into a false failure.
    private const string ServiceSid =
        "S-1-5-80-1234567890-1234567890-1234567890-1234567890-1234567890";

    private static readonly DateTimeOffset Origin = new(2026, 8, 18, 0, 0, 0, TimeSpan.Zero);

    private readonly string _sandbox;
    private readonly string _programFiles;
    private readonly string _metadataDirectory;

    public ServiceStartAndDisableTransactionTests()
    {
        _sandbox = Path.Combine(
            Path.GetTempPath(), "paxcookbook-cycle63rr", Guid.NewGuid().ToString("N"));
        _programFiles = Path.Combine(_sandbox, "ProgramFiles");
        _metadataDirectory = Path.Combine(_sandbox, "ProgramData", "PAXCookbook", "Service");
        Directory.CreateDirectory(_programFiles);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandbox))
            {
                Directory.Delete(_sandbox, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort temp cleanup.
        }
    }

    // =======================================================================
    // FAKES - nothing here can reach a real machine surface
    // =======================================================================

    private sealed class FakeProgramFiles : IServiceEnableProgramFilesResolver
    {
        private readonly string? _path;

        internal FakeProgramFiles(string? path) => _path = path;

        public string? TryResolveProgramFilesX64() => _path;
    }

    private sealed class FakePayload : IServiceEnablePayloadSource
    {
        private readonly byte[] _bytes;

        internal FakePayload(byte[] bytes) => _bytes = bytes;

        public ServicePayloadVerificationResult VerifyPayload(out ReadOnlyMemory<byte> archiveBytes)
        {
            ServicePayloadVerificationResult result = ServicePayloadResource.InspectBytes(_bytes);
            archiveBytes = result.IsVerified ? _bytes : ReadOnlyMemory<byte>.Empty;
            return result;
        }
    }

    private sealed class FakeSigning : IServiceEnableSigningPolicySource
    {
        public ServiceHelperSigningPolicyState ResolveConfiguredPolicy() =>
            ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed;
    }

    /// <summary>The Program Files access-control adapter. In memory only.</summary>
    private sealed class FakeFileSecurity : IServiceEnableSecurityAdapter
    {
        internal HashSet<string> Staged { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal bool FailFinalVerify { get; set; }

        public bool TryApplyStagingProtection(string directoryPath)
        {
            Staged.Add(directoryPath);
            return true;
        }

        public bool TryApplyFinalProtection(string path, bool isDirectory, string serviceSid) => true;

        public bool VerifyFinalProtection(string path, bool isDirectory, string serviceSid) => !FailFinalVerify;

        public bool VerifyStagingProtection(string directoryPath) => Staged.Contains(directoryPath);

        public bool RuntimeHostHasUntrustedWriteGrant(string filePath) => false;
    }

    /// <summary>
    /// The machine-data access-control adapter. It records every application and
    /// verification in order, and can be scripted to fail at any one of them.
    /// </summary>
    private sealed class FakeMachineSecurity : IServiceMachineDataSecurityAdapter
    {
        internal List<string> Calls { get; } = new();

        internal string? MetadataSidSeen { get; private set; }

        internal string? RuntimeSidSeen { get; private set; }

        internal bool FailApplyMetadata { get; set; }

        internal bool FailApplyAnchor { get; set; }

        internal bool FailApplyRuntime { get; set; }

        internal bool FailVerifyMetadata { get; set; }

        internal bool FailVerifyAnchor { get; set; }

        internal bool FailVerifyRuntime { get; set; }

        public bool TryApplyMetadataProtection(string directoryPath, string serviceSid)
        {
            MetadataSidSeen = serviceSid;
            Calls.Add("apply:metadata");
            return !FailApplyMetadata;
        }

        public bool TryApplyAnchorProtection(string filePath)
        {
            Calls.Add("apply:anchor");
            return !FailApplyAnchor;
        }

        public bool TryApplyRuntimeProtection(string directoryPath, string serviceSid)
        {
            RuntimeSidSeen = serviceSid;
            Calls.Add("apply:runtime");
            return !FailApplyRuntime;
        }

        public bool VerifyMetadataProtection(string directoryPath, string serviceSid)
        {
            Calls.Add("verify:metadata");
            return !FailVerifyMetadata;
        }

        public bool VerifyAnchorProtection(string filePath)
        {
            Calls.Add("verify:anchor");
            return !FailVerifyAnchor;
        }

        public bool VerifyRuntimeProtection(string directoryPath, string serviceSid)
        {
            Calls.Add("verify:runtime");
            return !FailVerifyRuntime;
        }
    }

    /// <summary>An in-memory Service Control Manager with a real run state.</summary>
    private sealed class FakeScm : IServiceControlManagerAdapter
    {
        private ServiceConfigurationSnapshot? _existing;
        private uint _sidType = ServiceEnableRegistrationContract.ServiceSidTypeNone;

        internal List<string> Calls { get; } = new();

        internal ServiceRunState State { get; set; } = ServiceRunState.Stopped;

        internal uint RunningProcessId { get; set; } = 4242;

        internal bool FailCreate { get; set; }

        internal bool FailStart { get; set; }

        /// <summary>
        /// CYCLE 74. The BOUNDED result a FAILING start reports. It exists so a
        /// test can drive each documented StartServiceW sub-classification
        /// through the production activation stage without a real SCM.
        /// </summary>
        internal ServiceStartAttemptResult FailedStartResult { get; set; } =
            ServiceStartAttemptResult.Refused;

        internal bool FailStop { get; set; }

        internal bool FailDelete { get; set; }

        internal string? ResolvedSid { get; set; } = ServiceSid;

        internal bool Exists => _existing.HasValue;

        internal void Seed(ServiceConfigurationSnapshot snapshot, uint sidType, ServiceRunState state)
        {
            _existing = snapshot;
            _sidType = sidType;
            State = state;
        }

        internal int IndexOf(string prefix) =>
            Calls.FindIndex(c => c.StartsWith(prefix, StringComparison.Ordinal));

        public ServiceQueryState QueryConfiguration(string serviceName, out ServiceConfigurationSnapshot snapshot)
        {
            Calls.Add("Query:" + serviceName);
            snapshot = default;
            if (_existing is null)
            {
                return ServiceQueryState.Absent;
            }
            snapshot = _existing.Value;
            return ServiceQueryState.Present;
        }

        public bool TryCreateService(ServiceConfigurationSnapshot desired)
        {
            Calls.Add("Create:" + desired.ServiceName);
            if (FailCreate)
            {
                return false;
            }
            _existing = desired;
            _sidType = ServiceEnableRegistrationContract.ServiceSidTypeNone;
            State = ServiceRunState.Stopped;
            return true;
        }

        public bool TrySetUnrestrictedServiceSidType(string serviceName)
        {
            Calls.Add("SetSidType:" + serviceName);
            if (_existing is null)
            {
                return false;
            }
            _sidType = ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted;
            return true;
        }

        public bool TryQueryServiceSidType(string serviceName, out uint sidType)
        {
            Calls.Add("QuerySidType:" + serviceName);
            sidType = _sidType;
            return _existing is not null;
        }

        public string? TryResolveServiceSid(string serviceName)
        {
            Calls.Add("ResolveSid:" + serviceName);
            return _existing is null ? null : ResolvedSid;
        }

        public bool TryDeleteService(string serviceName)
        {
            Calls.Add("Delete:" + serviceName);
            if (FailDelete)
            {
                return false;
            }
            _existing = null;
            _sidType = ServiceEnableRegistrationContract.ServiceSidTypeNone;
            State = ServiceRunState.Stopped;
            return true;
        }

        public ServiceStartAttemptResult TryStartService(string serviceName)
        {
            Calls.Add("Start:" + serviceName);
            if (FailStart)
            {
                return FailedStartResult;
            }
            State = ServiceRunState.Running;
            return ServiceStartAttemptResult.Started;
        }

        public ServiceQueryState QueryStatus(string serviceName, out ServiceStatusSnapshot snapshot)
        {
            Calls.Add("QueryStatus:" + serviceName);
            snapshot = default;
            if (_existing is null)
            {
                return ServiceQueryState.Absent;
            }
            snapshot = new ServiceStatusSnapshot(
                State, State == ServiceRunState.Running ? RunningProcessId : 0);
            return ServiceQueryState.Present;
        }

        public bool TryStopService(string serviceName)
        {
            Calls.Add("Stop:" + serviceName);
            if (FailStop)
            {
                return false;
            }
            State = ServiceRunState.Stopped;
            return true;
        }
    }

    private sealed class FakeProcessVerifier : IServiceRunningProcessVerifier
    {
        internal ServiceProcessIdentityState Result { get; set; } = ServiceProcessIdentityState.Verified;

        internal uint LastProcessId { get; private set; }

        internal string? LastSid { get; private set; }

        internal int Calls { get; private set; }

        public ServiceProcessIdentityState Verify(uint processId, string expectedServiceSid)
        {
            Calls++;
            LastProcessId = processId;
            LastSid = expectedServiceSid;
            return Result;
        }
    }

    private sealed class FakeDocuments : IServiceRuntimeDocumentVerifier
    {
        private int _heartbeats;

        internal ServiceRuntimeDocumentState StatusState { get; set; } = ServiceRuntimeDocumentState.Valid;

        /// <summary>
        /// CYCLE 75. A SCRIPTED status SEQUENCE. Each observation dequeues one
        /// verdict; once the queue is drained every later observation reports
        /// <see cref="StatusState"/>. It exists so a test can drive a REAL
        /// producer sequence - the service writes "starting", then "running" -
        /// rather than a single frozen verdict.
        /// </summary>
        internal Queue<ServiceRuntimeDocumentState> StatusSequence { get; } = new();

        /// <summary>Every status verdict this fake actually reported, in order.</summary>
        internal List<ServiceRuntimeDocumentState> StatusObservations { get; } = new();

        /// <summary>When true every heartbeat reports the SAME timestamp - a stale file.</summary>
        internal bool HeartbeatIsFrozen { get; set; }

        internal ServiceRuntimeDocumentState HeartbeatState { get; set; } = ServiceRuntimeDocumentState.Valid;

        internal List<string> Calls { get; } = new();

        internal int HeartbeatObservations => _heartbeats;

        public ServiceRuntimeDocumentResult VerifyStatus(string statusPath)
        {
            Calls.Add("status");

            ServiceRuntimeDocumentState verdict =
                StatusSequence.Count > 0 ? StatusSequence.Dequeue() : StatusState;
            StatusObservations.Add(verdict);

            return verdict == ServiceRuntimeDocumentState.Valid
                ? ServiceRuntimeDocumentResult.Valid(Origin)
                : ServiceRuntimeDocumentResult.Refused(verdict);
        }

        public ServiceRuntimeDocumentResult VerifyHeartbeat(string heartbeatPath)
        {
            Calls.Add("heartbeat");
            _heartbeats++;
            if (HeartbeatState != ServiceRuntimeDocumentState.Valid)
            {
                return ServiceRuntimeDocumentResult.Refused(HeartbeatState);
            }

            return ServiceRuntimeDocumentResult.Valid(
                HeartbeatIsFrozen ? Origin : Origin.AddSeconds(_heartbeats));
        }
    }

    private sealed class FakeChildren : IServiceChildProcessObserver
    {
        internal bool Forbidden { get; set; }

        internal uint LastParent { get; private set; }

        internal int Calls { get; private set; }

        public bool AnyForbiddenChildProcess(uint parentProcessId)
        {
            Calls++;
            LastParent = parentProcessId;
            return Forbidden;
        }
    }

    /// <summary>A deterministic clock: every sleep advances it by exactly the requested span.</summary>
    private sealed class FakeClock : IServiceStartabilityClock
    {
        private DateTimeOffset _now = Origin;

        internal int Sleeps { get; private set; }

        public DateTimeOffset UtcNow => _now;

        public void Sleep(TimeSpan duration)
        {
            Sleeps++;
            _now = _now.Add(duration);
        }
    }

    // =======================================================================
    // SANDBOX HELPERS
    // =======================================================================

    private string AnchorPath =>
        Path.Combine(_metadataDirectory, ServiceMachineStorageContract.InstallationAnchorFileName);

    private string RuntimeDirectory =>
        Path.Combine(_metadataDirectory, ServiceMachineStorageContract.RuntimeDataFolderName);

    private ServiceEnableFixedPaths ResolvePaths()
    {
        Assert.Equal(
            ServiceEnablePathResolutionState.Resolved,
            ServiceEnableFixedPathResolver.TryResolve(
                new FakeProgramFiles(_programFiles), out ServiceEnableFixedPaths paths));
        return paths;
    }

    private void CreateRuntimeHost()
    {
        ServiceEnableFixedPaths paths = ResolvePaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RuntimeHost)!);
        File.WriteAllBytes(paths.RuntimeHost, new byte[] { 0x4D, 0x5A });
    }

    private static string CurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Assert.NotNull(identity.User);
        return identity.User!.Value;
    }

    private ServiceInstallationAnchorDocument WriteAnchor()
    {
        Directory.CreateDirectory(_metadataDirectory);
        var document = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            CurrentSid(),
            "2026-08-18T00:00:00Z");
        Assert.Equal(
            ServiceInstallationAnchorWriteState.Created,
            ServiceInstallationAnchorStore.WriteTo(_metadataDirectory, document));
        return document;
    }

    // ---- deterministic archive construction --------------------------------

    private static byte[] MemberContent(string name) =>
        Encoding.ASCII.GetBytes(new string('C', 192) + name);

    private static string Sha256Upper(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static List<(string Name, byte[] Bytes)> CanonicalMembers() =>
        ServicePayloadArchiveFormat.RequiredMemberNames
            .Select(name => (Name: name, Bytes: MemberContent(name)))
            .ToList();

    private static byte[] BuildCanonicalArchive()
    {
        List<(string Name, byte[] Bytes)> members = CanonicalMembers();

        var entries = members.Select(m =>
            "    {\n"
            + "      \"name\": \"" + m.Name + "\",\n"
            + "      \"sizeBytes\": " + m.Bytes.LongLength.ToString(CultureInfo.InvariantCulture) + ",\n"
            + "      \"sha256\": \"" + Sha256Upper(m.Bytes) + "\"\n"
            + "    }");

        string manifestJson = "{\n"
            + "  \"schemaVersion\": "
            + ServicePayloadArchiveFormat.SchemaVersion.ToString(CultureInfo.InvariantCulture) + ",\n"
            + "  \"targetOs\": \"" + ServicePayloadArchiveFormat.TargetOs + "\",\n"
            + "  \"targetArch\": \"" + ServicePayloadArchiveFormat.TargetArch + "\",\n"
            + "  \"files\": [\n"
            + string.Join(",\n", entries) + "\n"
            + "  ]\n"
            + "}\n";

        byte[] manifest = new UTF8Encoding(false).GetBytes(manifestJson);

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in ServicePayloadArchiveFormat.OrderedEntryNames)
            {
                ZipArchiveEntry entry =
                    archive.CreateEntry(name, ServicePayloadArchiveFormat.FixedCompressionLevel);
                entry.LastWriteTime = ServicePayloadArchiveFormat.FixedEntryTimestamp;

                byte[] bytes = string.Equals(
                    name, ServicePayloadArchiveFormat.ManifestEntryName, StringComparison.Ordinal)
                    ? manifest
                    : members.First(m => string.Equals(m.Name, name, StringComparison.Ordinal)).Bytes;

                using Stream stream = entry.Open();
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        byte[] result = buffer.ToArray();
        Assert.True(ServicePayloadResource.InspectBytes(result).IsVerified);
        return result;
    }

    /// <summary>Lands the exact payload in the fixed final directory.</summary>
    private void ExtractExactPayload()
    {
        ServiceEnableFixedPaths paths = ResolvePaths();
        ServiceEnableExtractionResult extraction = ServiceEnableExtractor.ExtractVerifiedArchive(
            BuildCanonicalArchive(), paths, new FakeFileSecurity());
        Assert.True(extraction.IsExtracted);
    }

    // ---- composition -------------------------------------------------------

    private sealed class Rig
    {
        internal FakeScm Scm { get; } = new();

        internal FakeMachineSecurity MachineSecurity { get; } = new();

        internal FakeFileSecurity FileSecurity { get; } = new();

        internal FakeProcessVerifier Process { get; } = new();

        internal FakeDocuments Documents { get; } = new();

        internal FakeChildren Children { get; } = new();

        internal FakeClock Clock { get; } = new();

        internal ServiceStartabilityCoordinator Startability() =>
            new(Scm, Process, Documents, Children, Clock);

        internal ServiceEnableActivationStage Stage() =>
            new(MachineSecurity, Startability(), Scm);
    }

    private ServiceEnableTransaction CreateEnableTransaction(Rig rig) =>
        new(
            _metadataDirectory,
            new FakeProgramFiles(_programFiles),
            rig.FileSecurity,
            rig.Scm,
            new FakePayload(BuildCanonicalArchive()),
            new FakeSigning(),
            rig.Stage());

    /// <summary>
    /// A rig whose fake SCM already holds the fixed REGISTERED service. Tests
    /// that drive the activation stage DIRECTLY need it, because the stage's
    /// bounded wait asks the SCM for a run state and an absent service can never
    /// report Running.
    /// </summary>
    private Rig SeededRig(ServiceRunState state = ServiceRunState.Stopped)
    {
        var rig = new Rig();
        ServiceEnableFixedPaths paths = ResolvePaths();
        rig.Scm.Seed(
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final),
            ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted,
            state);
        return rig;
    }

    private ServiceDisableTransaction CreateDisableTransaction(Rig rig) =>
        new(
            _metadataDirectory,
            new FakeProgramFiles(_programFiles),
            rig.Scm,
            new FakePayload(BuildCanonicalArchive()),
            rig.Startability());

    private ServiceEnableActivationRequest Request(
        bool applyProfiles = true, bool startAuthorized = true, bool anchorCreatedThisAttempt = true) =>
        new(
            ServiceSid,
            _metadataDirectory,
            AnchorPath,
            RuntimeDirectory,
            Path.Combine(RuntimeDirectory, ServiceRuntimeDocumentContract.StatusFileName),
            Path.Combine(RuntimeDirectory, ServiceRuntimeDocumentContract.HeartbeatFileName),
            applyProfiles,
            startAuthorized,
            anchorCreatedThisAttempt);

    private ServiceIdentityBoundTransactionContext Context(
        ServiceInstallationAnchorDocument anchor, bool anchorCreatedThisAttempt) =>
        new(_metadataDirectory, anchor, anchorCreatedThisAttempt);

    // =======================================================================
    // THE THREE ACCESS-CONTROL PROFILES - EXACT SHAPE
    // =======================================================================

    [Fact]
    public void The_metadata_profile_lets_the_service_pass_through_and_nothing_more()
    {
        IReadOnlyList<ServiceMachineDataAce> expected =
            ServiceMachineDataAclProfile.ExpectedMetadataAces(ServiceSid);

        Assert.Equal(3, expected.Count);

        ServiceMachineDataAce system = Assert.Single(
            expected, a => a.Sid == ServiceEnableAclProfile.LocalSystemSid);
        ServiceMachineDataAce admins = Assert.Single(
            expected, a => a.Sid == ServiceEnableAclProfile.BuiltinAdministratorsSid);
        ServiceMachineDataAce service = Assert.Single(expected, a => a.Sid == ServiceSid);

        Assert.Equal(ServiceEnableAclProfile.AdministrativeRights, system.Rights);
        Assert.Equal(ServiceEnableAclProfile.AdministrativeRights, admins.Rights);

        // Traverse ONLY - plus the SYNCHRONIZE bit Windows adds to every
        // non-full allow ACE - and NON-INHERITING, so it can never reach the
        // anchor or the ownership ledger beneath this directory.
        Assert.Equal(
            System.Security.AccessControl.FileSystemRights.Traverse
            | System.Security.AccessControl.FileSystemRights.Synchronize,
            service.Rights);
        Assert.Equal(ServiceMachineDataAclProfile.MetadataTraverseRights, service.Rights);
        Assert.Equal(System.Security.AccessControl.InheritanceFlags.None, service.Inheritance);
        Assert.NotEqual(ServiceEnableAclProfile.AdministrativeRights, service.Rights);

        // Pass-through and nothing else: no read of the listing, no create, no
        // write, no delete, no re-permissioning.
        foreach (System.Security.AccessControl.FileSystemRights forbidden in new[]
        {
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.FileSystemRights.ReadData,
            System.Security.AccessControl.FileSystemRights.CreateFiles,
            System.Security.AccessControl.FileSystemRights.WriteData,
            System.Security.AccessControl.FileSystemRights.Delete,
            System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles,
            System.Security.AccessControl.FileSystemRights.ChangePermissions,
            System.Security.AccessControl.FileSystemRights.TakeOwnership,
        })
        {
            Assert.Equal(
                (System.Security.AccessControl.FileSystemRights)0, service.Rights & forbidden);
        }
    }

    [Fact]
    public void The_anchor_profile_admits_no_service_and_no_ordinary_user()
    {
        IReadOnlyList<ServiceMachineDataAce> expected = ServiceMachineDataAclProfile.ExpectedAnchorAces();

        Assert.Equal(2, expected.Count);
        Assert.Contains(expected, a => a.Sid == ServiceEnableAclProfile.LocalSystemSid);
        Assert.Contains(expected, a => a.Sid == ServiceEnableAclProfile.BuiltinAdministratorsSid);
        Assert.DoesNotContain(expected, a => a.Sid == ServiceSid);
        Assert.DoesNotContain(expected, a => a.Sid == CurrentSid());
        Assert.All(expected, a => Assert.Equal(ServiceEnableAclProfile.AdministrativeRights, a.Rights));
        Assert.All(
            expected,
            a => Assert.Equal(System.Security.AccessControl.InheritanceFlags.None, a.Inheritance));
    }

    [Fact]
    public void The_runtime_profile_is_the_only_place_the_service_may_write()
    {
        IReadOnlyList<ServiceMachineDataAce> expected =
            ServiceMachineDataAclProfile.ExpectedRuntimeAces(ServiceSid);

        Assert.Equal(3, expected.Count);
        ServiceMachineDataAce service = Assert.Single(expected, a => a.Sid == ServiceSid);

        Assert.Equal(
            System.Security.AccessControl.FileSystemRights.Modify
            | System.Security.AccessControl.FileSystemRights.Synchronize,
            service.Rights);
        Assert.Equal(ServiceMachineDataAclProfile.RuntimeServiceRights, service.Rights);
        Assert.Equal(
            System.Security.AccessControl.InheritanceFlags.ContainerInherit
            | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            service.Inheritance);

        // The service may WRITE its runtime state and may only PASS THROUGH the
        // metadata namespace that holds the ownership records.
        Assert.NotEqual(ServiceMachineDataAclProfile.MetadataTraverseRights, service.Rights);
    }

    [Fact]
    public void Every_built_profile_is_owned_by_administrators_with_a_protected_dacl()
    {
        var sid = new SecurityIdentifier(ServiceSid);

        System.Security.AccessControl.DirectorySecurity metadata =
            ServiceMachineDataAclProfile.BuildMetadataDirectorySecurity(sid);
        System.Security.AccessControl.DirectorySecurity runtime =
            ServiceMachineDataAclProfile.BuildRuntimeDirectorySecurity(sid);
        System.Security.AccessControl.FileSecurity anchor =
            ServiceMachineDataAclProfile.BuildAnchorFileSecurity();

        foreach (System.Security.AccessControl.CommonObjectSecurity security
                 in new System.Security.AccessControl.CommonObjectSecurity[] { metadata, runtime, anchor })
        {
            Assert.Equal(
                ServiceEnableAclProfile.BuiltinAdministratorsSid,
                ((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!).Value);
            Assert.True(security.AreAccessRulesProtected);
        }

        Assert.True(ServiceMachineDataAclProfile.MatchesProfile(
            metadata, ServiceMachineDataAclProfile.ExpectedMetadataAces(ServiceSid)));
        Assert.True(ServiceMachineDataAclProfile.MatchesProfile(
            runtime, ServiceMachineDataAclProfile.ExpectedRuntimeAces(ServiceSid)));
        Assert.True(ServiceMachineDataAclProfile.MatchesProfile(
            anchor, ServiceMachineDataAclProfile.ExpectedAnchorAces()));
    }

    [Fact]
    public void One_extra_grant_or_one_widened_inheritance_flag_fails_exact_verification()
    {
        var sid = new SecurityIdentifier(ServiceSid);
        System.Security.AccessControl.DirectorySecurity metadata =
            ServiceMachineDataAclProfile.BuildMetadataDirectorySecurity(sid);

        Assert.True(ServiceMachineDataAclProfile.MatchesProfile(
            metadata, ServiceMachineDataAclProfile.ExpectedMetadataAces(ServiceSid)));

        metadata.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new SecurityIdentifier("S-1-1-0"),
            System.Security.AccessControl.FileSystemRights.ReadAndExecute,
            System.Security.AccessControl.InheritanceFlags.None,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));

        Assert.False(ServiceMachineDataAclProfile.MatchesProfile(
            metadata, ServiceMachineDataAclProfile.ExpectedMetadataAces(ServiceSid)));

        // A metadata profile whose service ACE INHERITS would reach the anchor.
        var widened = new System.Security.AccessControl.DirectorySecurity();
        widened.SetOwner(ServiceEnableAclProfile.BuiltinAdministrators);
        widened.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (ServiceMachineDataAce ace in ServiceMachineDataAclProfile.ExpectedMetadataAces(ServiceSid))
        {
            widened.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new SecurityIdentifier(ace.Sid),
                ace.Rights,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit
                | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
        }

        Assert.False(ServiceMachineDataAclProfile.MatchesProfile(
            widened, ServiceMachineDataAclProfile.ExpectedMetadataAces(ServiceSid)));
    }

    // =======================================================================
    // THE ACTIVATION STAGE - ORDER, APPLICATION AND VERIFICATION
    // =======================================================================

    [Fact]
    public void The_three_profiles_are_applied_then_verified_in_the_fixed_order()
    {
        Rig rig = SeededRig();
        WriteAnchor();

        ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(Request());

        Assert.Equal(ServiceEnableActivationState.Verified, result.State);
        Assert.Equal(
            new[]
            {
                "apply:metadata", "verify:metadata",
                "apply:anchor", "verify:anchor",
                "apply:runtime", "verify:runtime",
            },
            rig.MachineSecurity.Calls);

        // The SID that was granted rights is the resolved service SID.
        Assert.Equal(ServiceSid, rig.MachineSecurity.MetadataSidSeen);
        Assert.Equal(ServiceSid, rig.MachineSecurity.RuntimeSidSeen);
        Assert.True(result.RuntimeDirectoryCreatedThisAttempt);
        Assert.True(Directory.Exists(RuntimeDirectory));
    }

    [Fact]
    public void A_profile_that_applies_but_does_not_verify_is_refused()
    {
        foreach ((string which, Action<FakeMachineSecurity> fail, ServiceEnableActivationState expected)
                 in new (string, Action<FakeMachineSecurity>, ServiceEnableActivationState)[]
                 {
                     ("metadata", s => s.FailVerifyMetadata = true,
                         ServiceEnableActivationState.MetadataProtectionRefused),
                     ("anchor", s => s.FailVerifyAnchor = true,
                         ServiceEnableActivationState.AnchorProtectionRefused),
                     ("runtime", s => s.FailVerifyRuntime = true,
                         ServiceEnableActivationState.RuntimeProtectionRefused),
                 })
        {
            var rig = new Rig();
            WriteAnchor();
            fail(rig.MachineSecurity);

            ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(Request());

            Assert.Equal(expected, result.State);
            Assert.False(result.ServiceStartedThisAttempt);
            Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Start:", StringComparison.Ordinal));
            Assert.True(which.Length > 0);

            if (Directory.Exists(RuntimeDirectory))
            {
                Directory.Delete(RuntimeDirectory, recursive: true);
            }
            File.Delete(AnchorPath);
        }
    }

    [Fact]
    public void An_existing_anchor_with_a_wrong_owner_or_dacl_is_refused_and_never_rewritten()
    {
        var rig = new Rig();
        WriteAnchor();
        rig.MachineSecurity.FailVerifyAnchor = true;

        // The anchor was NOT created by this attempt, so it is verified only.
        ServiceEnableActivationResult result =
            rig.Stage().ActivateAndVerify(Request(anchorCreatedThisAttempt: false));

        Assert.Equal(ServiceEnableActivationState.AnchorProtectionRefused, result.State);
        Assert.DoesNotContain("apply:anchor", rig.MachineSecurity.Calls);
        Assert.Contains("verify:anchor", rig.MachineSecurity.Calls);
        Assert.True(File.Exists(AnchorPath));
    }

    [Fact]
    public void The_anchor_descriptor_is_written_only_when_this_attempt_created_the_anchor()
    {
        Rig withOwnership = SeededRig();
        WriteAnchor();
        Assert.Equal(
            ServiceEnableActivationState.Verified,
            withOwnership.Stage().ActivateAndVerify(Request(anchorCreatedThisAttempt: true)).State);
        Assert.Contains("apply:anchor", withOwnership.MachineSecurity.Calls);

        Directory.Delete(RuntimeDirectory, recursive: true);

        Rig withoutOwnership = SeededRig();
        Assert.Equal(
            ServiceEnableActivationState.Verified,
            withoutOwnership.Stage().ActivateAndVerify(Request(anchorCreatedThisAttempt: false)).State);
        Assert.DoesNotContain("apply:anchor", withoutOwnership.MachineSecurity.Calls);
    }

    [Fact]
    public void The_verify_only_shape_applies_nothing_creates_nothing_and_starts_nothing()
    {
        Rig rig = SeededRig(ServiceRunState.Running);
        WriteAnchor();
        Directory.CreateDirectory(RuntimeDirectory);

        ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(
            Request(applyProfiles: false, startAuthorized: false));

        Assert.Equal(ServiceEnableActivationState.Verified, result.State);
        Assert.Equal(
            new[] { "verify:metadata", "verify:anchor", "verify:runtime" }, rig.MachineSecurity.Calls);
        Assert.False(result.RuntimeDirectoryCreatedThisAttempt);
        Assert.False(result.ServiceStartedThisAttempt);
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Start:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_verify_only_shape_refuses_an_absent_runtime_directory_rather_than_creating_it()
    {
        var rig = new Rig();
        WriteAnchor();

        ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(
            Request(applyProfiles: false, startAuthorized: false));

        Assert.Equal(ServiceEnableActivationState.RuntimeProtectionRefused, result.State);
        Assert.False(Directory.Exists(RuntimeDirectory));
    }

    [Fact]
    public void The_start_happens_only_after_every_profile_verified()
    {
        Rig rig = SeededRig();
        WriteAnchor();

        Assert.Equal(ServiceEnableActivationState.Verified, rig.Stage().ActivateAndVerify(Request()).State);

        // Nothing in the SCM call list precedes the last access-control call,
        // because the whole profile sequence ran before the first SCM call.
        Assert.Equal("verify:runtime", rig.MachineSecurity.Calls.Last());
        Assert.Equal(0, rig.Scm.IndexOf("Start:"));
    }

    [Fact]
    public void A_refused_start_never_reaches_the_startability_proof()
    {
        var rig = new Rig();
        WriteAnchor();
        rig.Scm.FailStart = true;

        ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(Request());

        Assert.Equal(ServiceEnableActivationState.StartRefused, result.State);
        Assert.False(result.ServiceStartedThisAttempt);
        Assert.Equal(0, rig.Process.Calls);
        Assert.Empty(rig.Documents.Calls);
        Assert.Equal(0, rig.Children.Calls);
    }

    // =======================================================================
    // THE STARTABILITY PROOF
    // =======================================================================

    [Fact]
    public void The_running_process_is_proven_by_its_token_user_sid_and_session_zero()
    {
        Rig rig = SeededRig();
        WriteAnchor();
        rig.Scm.RunningProcessId = 9182;

        Assert.Equal(ServiceEnableActivationState.Verified, rig.Stage().ActivateAndVerify(Request()).State);

        // The verifier was asked about the EXACT process the SCM reported, and
        // about the EXACT resolved service SID.
        Assert.Equal(1, rig.Process.Calls);
        Assert.Equal(9182u, rig.Process.LastProcessId);
        Assert.Equal(ServiceSid, rig.Process.LastSid);
        Assert.Equal(0u, WindowsServiceRunningProcessVerifier.RequiredSessionId);
    }

    // Enumerated in the test body rather than through InlineData because the
    // verdict enum is internal to the helper.
    [Fact]
    public void Any_process_identity_verdict_other_than_verified_refuses()
    {
        foreach (ServiceProcessIdentityState verdict in Enum.GetValues<ServiceProcessIdentityState>())
        {
            if (verdict == ServiceProcessIdentityState.Verified)
            {
                continue;
            }

            Rig rig = SeededRig();
            WriteAnchor();
            rig.Process.Result = verdict;

            ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(Request());

            Assert.Equal(ServiceEnableActivationState.ProcessIdentityRefused, result.State);
            Assert.Empty(rig.Documents.Calls);

            Directory.Delete(RuntimeDirectory, recursive: true);
            File.Delete(AnchorPath);
        }
    }

    /// <summary>
    /// CYCLE 75. EVERY non-Valid document verdict paired with the EXACT bounded
    /// activation state it must produce. It is deliberately a hand-written map:
    /// a table derived from the code under test would pin nothing.
    /// </summary>
    private static Dictionary<ServiceRuntimeDocumentState, ServiceEnableActivationState> StatusRefusalStates() =>
        new()
        {
            // The two TRANSIENT verdicts consume the bounded window and end in a
            // DEADLINE verdict that names which of them was last observed.
            [ServiceRuntimeDocumentState.Missing] = ServiceEnableActivationState.StatusMissingTimeout,
            [ServiceRuntimeDocumentState.Starting] = ServiceEnableActivationState.StatusStartingTimeout,

            // The TERMINAL verdicts are decided on ONE observation.
            [ServiceRuntimeDocumentState.Unreadable] = ServiceEnableActivationState.StatusUnreadable,
            [ServiceRuntimeDocumentState.Malformed] = ServiceEnableActivationState.StatusMalformed,
            [ServiceRuntimeDocumentState.WrongExecutionContext] =
                ServiceEnableActivationState.StatusWrongContext,
            [ServiceRuntimeDocumentState.Stopped] = ServiceEnableActivationState.StatusStopped,
            [ServiceRuntimeDocumentState.Failed] = ServiceEnableActivationState.StatusFailed,
            [ServiceRuntimeDocumentState.UnknownState] = ServiceEnableActivationState.StatusUnknownState,

            // Unspecified is an uninitialised value and must never read as ready.
            [ServiceRuntimeDocumentState.Unspecified] = ServiceEnableActivationState.StatusUnknownState,
        };

    // Enumerated in the test body rather than through InlineData because the
    // document-state enum is internal to the helper.
    [Fact]
    public void Every_status_document_verdict_reaches_its_own_bounded_activation_state()
    {
        Dictionary<ServiceRuntimeDocumentState, ServiceEnableActivationState> expected = StatusRefusalStates();

        // TOTALITY, DERIVED FROM THE ENUM: every declared verdict except Valid
        // is covered, so a verdict appended without a mapping fails here.
        ServiceRuntimeDocumentState[] declared = Enum.GetValues<ServiceRuntimeDocumentState>()
            .Where(v => v != ServiceRuntimeDocumentState.Valid)
            .ToArray();
        Assert.Equal(declared.Length, expected.Count);
        foreach (ServiceRuntimeDocumentState verdict in declared)
        {
            Assert.True(expected.ContainsKey(verdict), verdict + " has no expected activation state");
        }

        // The EIGHT bounded states really are eight distinct states.
        Assert.Equal(8, expected.Values.Distinct().Count());

        foreach (KeyValuePair<ServiceRuntimeDocumentState, ServiceEnableActivationState> row in expected)
        {
            Rig rig = SeededRig();
            WriteAnchor();
            rig.Documents.StatusState = row.Key;

            ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(Request());

            Assert.Equal(row.Value, result.State);

            // NOTHING reaches the retained wide verdict any more.
            Assert.NotEqual(ServiceEnableActivationState.StatusDocumentRefused, result.State);

            // A TERMINAL verdict stops IMMEDIATELY: exactly one status
            // observation and not a single sleep. A TRANSIENT verdict genuinely
            // exhausts the bounded window instead.
            bool transient =
                row.Key == ServiceRuntimeDocumentState.Missing
                || row.Key == ServiceRuntimeDocumentState.Starting;

            if (transient)
            {
                Assert.True(rig.Documents.StatusObservations.Count > 1);
                Assert.True(rig.Clock.Sleeps > 1);
            }
            else
            {
                Assert.Single(rig.Documents.StatusObservations);
                Assert.Equal(0, rig.Clock.Sleeps);
            }

            Directory.Delete(RuntimeDirectory, recursive: true);
            File.Delete(AnchorPath);
        }
    }

    [Fact]
    public void The_status_readiness_window_is_bounded_and_reuses_the_existing_poll_interval()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), ServiceStartabilityContract.StatusReadinessWindow);
        Assert.Equal(TimeSpan.FromMilliseconds(250), ServiceStartabilityContract.PollInterval);

        // The bounded heartbeat proof is UNCHANGED by this cycle.
        Assert.Equal(TimeSpan.FromSeconds(45), ServiceStartabilityContract.HeartbeatProofWindow);
        Assert.Equal(TimeSpan.FromSeconds(60), ServiceStartabilityContract.RunningWait);

        // The window is a FIXED budget, so the number of observations it can
        // afford is arithmetic rather than a retry-until-success loop.
        Rig rig = SeededRig();
        WriteAnchor();
        rig.Documents.StatusState = ServiceRuntimeDocumentState.Missing;

        Assert.Equal(
            ServiceEnableActivationState.StatusMissingTimeout,
            rig.Stage().ActivateAndVerify(Request()).State);

        int affordable = (int)(ServiceStartabilityContract.StatusReadinessWindow.TotalMilliseconds
            / ServiceStartabilityContract.PollInterval.TotalMilliseconds);
        Assert.Equal(affordable + 1, rig.Documents.StatusObservations.Count);
    }

    [Fact]
    public void Two_advancing_heartbeat_observations_are_required()
    {
        Rig rig = SeededRig();
        WriteAnchor();

        Assert.Equal(ServiceEnableActivationState.Verified, rig.Stage().ActivateAndVerify(Request()).State);

        Assert.Equal(2, ServiceStartabilityContract.RequiredHeartbeatObservations);
        Assert.Equal(2, rig.Documents.HeartbeatObservations);
        Assert.Equal(TimeSpan.FromSeconds(45), ServiceStartabilityContract.HeartbeatProofWindow);
    }

    [Fact]
    public void CYCLE75_RED1_A_transiently_absent_status_document_must_not_refuse_immediately()
    {
        // THE FALSIFYING CHECK, STAGE 1. Written and executed against the
        // UNCHANGED one-shot coordinator, where it FAILED behaviourally with
        // ZERO compile errors (expected Verified, actual StatusDocumentRefused).
        // It is retained as the permanent regression guard.
        Rig rig = SeededRig();
        WriteAnchor();

        rig.Documents.StatusState = ServiceRuntimeDocumentState.Valid;
        rig.Documents.StatusSequence.Enqueue(ServiceRuntimeDocumentState.Missing);
        rig.Documents.StatusSequence.Enqueue(ServiceRuntimeDocumentState.Missing);

        Assert.Equal(ServiceEnableActivationState.Verified, rig.Stage().ActivateAndVerify(Request()).State);

        Assert.Equal(
            new[]
            {
                ServiceRuntimeDocumentState.Missing,
                ServiceRuntimeDocumentState.Missing,
                ServiceRuntimeDocumentState.Valid,
            },
            rig.Documents.StatusObservations);
    }

    [Fact]
    public void CYCLE75_RED2_A_missing_then_starting_then_running_sequence_must_be_accepted()
    {
        // THE MANDATED FALSIFYING CHECK. The service's OWN worker writes
        // "starting" before it writes "running", so this is the real producer
        // sequence. Written and executed against the UNCHANGED one-shot
        // coordinator, where it FAILED behaviourally with ZERO compile errors.
        //
        // HONEST SCOPE: this tests the MECHANISM, not the field cause. The
        // cycle-74 field failure remains INFERRED - every non-Valid verdict
        // collapsed onto one token, so Missing, Starting, Malformed, Unreadable
        // and a wrong execution context were all equally consistent with what
        // was observed. The eight bounded causes are what make the NEXT attended
        // run decisive either way.
        Rig rig = SeededRig();
        WriteAnchor();

        rig.Documents.StatusState = ServiceRuntimeDocumentState.Valid;
        rig.Documents.StatusSequence.Enqueue(ServiceRuntimeDocumentState.Missing);
        rig.Documents.StatusSequence.Enqueue(ServiceRuntimeDocumentState.Starting);

        Assert.Equal(ServiceEnableActivationState.Verified, rig.Stage().ActivateAndVerify(Request()).State);

        Assert.Equal(
            new[]
            {
                ServiceRuntimeDocumentState.Missing,
                ServiceRuntimeDocumentState.Starting,
                ServiceRuntimeDocumentState.Valid,
            },
            rig.Documents.StatusObservations);
    }

    [Fact]
    public void The_bounded_status_wait_returns_the_instant_it_observes_running()
    {
        // The bounded wait is driven DIRECTLY, so the sleep count measures ONLY
        // the status-readiness window. Measuring it through the whole activation
        // would also count the separate bounded heartbeat proof.
        Rig rig = SeededRig();
        rig.Documents.StatusState = ServiceRuntimeDocumentState.Valid;
        rig.Documents.StatusSequence.Enqueue(ServiceRuntimeDocumentState.Missing);
        rig.Documents.StatusSequence.Enqueue(ServiceRuntimeDocumentState.Starting);

        Assert.True(
            rig.Startability().WaitForRunningStatus("unused", out ServiceStartabilityState refusal));
        Assert.Equal(ServiceStartabilityState.Unspecified, refusal);

        // EXACTLY two sleeps - one per transient observation - and not a third.
        Assert.Equal(2, rig.Clock.Sleeps);
        Assert.Equal(3, rig.Documents.StatusObservations.Count);
    }

    [Fact]
    public void The_bounded_status_wait_never_sleeps_on_a_terminal_verdict()
    {
        foreach (ServiceRuntimeDocumentState terminal in new[]
        {
            ServiceRuntimeDocumentState.Unreadable,
            ServiceRuntimeDocumentState.Malformed,
            ServiceRuntimeDocumentState.WrongExecutionContext,
            ServiceRuntimeDocumentState.Stopped,
            ServiceRuntimeDocumentState.Failed,
            ServiceRuntimeDocumentState.UnknownState,
            ServiceRuntimeDocumentState.Unspecified,
        })
        {
            Rig rig = SeededRig();
            rig.Documents.StatusState = terminal;

            Assert.False(
                rig.Startability().WaitForRunningStatus("unused", out ServiceStartabilityState refusal));
            Assert.Equal(
                ServiceStartabilityCoordinator.TerminalStatusRefusal(terminal), refusal);

            // ZERO sleeps and exactly ONE observation. The decision was already
            // made; waiting could only postpone it.
            Assert.Equal(0, rig.Clock.Sleeps);
            Assert.Single(rig.Documents.StatusObservations);

            // A terminal refusal is never the retained wide verdict.
            Assert.NotEqual(ServiceStartabilityState.StatusDocumentRefused, refusal);
            Assert.NotEqual(ServiceStartabilityState.Verified, refusal);
        }
    }

    [Fact]
    public void The_readiness_timeout_verdict_depends_on_the_observed_transient_sequence()
    {
        // ONLY Missing was ever seen -> the timeout says exactly that.
        Rig onlyMissing = SeededRig();
        WriteAnchor();
        onlyMissing.Documents.StatusState = ServiceRuntimeDocumentState.Missing;

        Assert.Equal(
            ServiceEnableActivationState.StatusMissingTimeout,
            onlyMissing.Stage().ActivateAndVerify(Request()).State);
        Assert.DoesNotContain(ServiceRuntimeDocumentState.Starting, onlyMissing.Documents.StatusObservations);

        Directory.Delete(RuntimeDirectory, recursive: true);
        File.Delete(AnchorPath);

        // ONE valid Starting observation, then Missing forever. THE SAME
        // DEADLINE, a DIFFERENT verdict - the classification depends on what was
        // OBSERVED, not on the last observation.
        Rig sawStarting = SeededRig();
        WriteAnchor();
        sawStarting.Documents.StatusState = ServiceRuntimeDocumentState.Missing;
        sawStarting.Documents.StatusSequence.Enqueue(ServiceRuntimeDocumentState.Missing);
        sawStarting.Documents.StatusSequence.Enqueue(ServiceRuntimeDocumentState.Starting);

        Assert.Equal(
            ServiceEnableActivationState.StatusStartingTimeout,
            sawStarting.Stage().ActivateAndVerify(Request()).State);
        Assert.Contains(ServiceRuntimeDocumentState.Starting, sawStarting.Documents.StatusObservations);
        Assert.Equal(
            ServiceRuntimeDocumentState.Missing,
            sawStarting.Documents.StatusObservations[sawStarting.Documents.StatusObservations.Count - 1]);
    }

    [Fact]
    public void A_terminal_status_verdict_after_a_transient_one_stops_immediately()
    {
        // The wait is allowed to absorb transient observations, and it must STILL
        // stop dead on the first terminal one rather than exhausting the window.
        Rig rig = SeededRig();
        WriteAnchor();

        rig.Documents.StatusState = ServiceRuntimeDocumentState.Valid;
        rig.Documents.StatusSequence.Enqueue(ServiceRuntimeDocumentState.Missing);
        rig.Documents.StatusSequence.Enqueue(ServiceRuntimeDocumentState.Starting);
        rig.Documents.StatusSequence.Enqueue(ServiceRuntimeDocumentState.Failed);

        Assert.Equal(
            ServiceEnableActivationState.StatusFailed,
            rig.Stage().ActivateAndVerify(Request()).State);

        Assert.Equal(3, rig.Documents.StatusObservations.Count);

        // TWO sleeps - one per transient observation - and NOT A THIRD. The
        // terminal verdict consumed no part of the remaining window.
        Assert.Equal(2, rig.Clock.Sleeps);

        int affordable = (int)(ServiceStartabilityContract.StatusReadinessWindow.TotalMilliseconds
            / ServiceStartabilityContract.PollInterval.TotalMilliseconds);
        Assert.True(rig.Clock.Sleeps < affordable);
    }

    [Fact]
    public void The_status_wait_never_repairs_rewrites_or_deletes_a_document()
    {
        // STRUCTURAL. The bounded wait is a READER. If it ever gained a write,
        // create, move or delete it would be repairing the very evidence it is
        // supposed to judge.
        string code = StripCommentsAndLiterals(
            ReadRepoText("src/PAXCookbook.ServiceAdminHelper/Enable/ServiceStartabilityCoordinator.cs"));

        foreach (string token in new[]
        {
            "File.Delete", "File.WriteAllText", "File.WriteAllBytes", "File.Move", "File.Copy",
            "File.Create", "Directory.CreateDirectory", "Directory.Delete", "StreamWriter",
        })
        {
            Assert.False(
                code.Contains(token, StringComparison.Ordinal),
                "the startability coordinator mutates a document: " + token);
        }

        // POSITIVE CONTROL: the instrument fires on code that genuinely does it.
        Assert.Contains(
            "File.Delete",
            StripCommentsAndLiterals("class C { void M() { File.Delete(p); } }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_heartbeat_timestamp_is_never_the_second_observation()
    {
        Rig rig = SeededRig();
        WriteAnchor();
        rig.Documents.HeartbeatIsFrozen = true;

        ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(Request());

        Assert.Equal(ServiceEnableActivationState.HeartbeatDidNotAdvance, result.State);

        // The window was genuinely exhausted rather than abandoned early.
        Assert.True(rig.Documents.HeartbeatObservations > 2);
        Assert.True(rig.Clock.Sleeps > 2);
    }

    [Fact]
    public void An_invalid_heartbeat_never_counts_as_an_observation()
    {
        Rig rig = SeededRig();
        WriteAnchor();
        rig.Documents.HeartbeatState = ServiceRuntimeDocumentState.Missing;

        Assert.Equal(
            ServiceEnableActivationState.HeartbeatDidNotAdvance,
            rig.Stage().ActivateAndVerify(Request()).State);
    }

    [Fact]
    public void Liveness_is_never_inferred_from_a_filesystem_timestamp()
    {
        foreach (string relative in new[]
        {
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceRuntimeDocumentVerifier.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceStartabilityCoordinator.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableActivationStage.cs",
        })
        {
            string code = StripCommentsAndLiterals(ReadRepoText(relative));
            foreach (string token in new[]
            {
                "LastWriteTime", "LastAccessTime", "CreationTime", "GetLastWriteTime", "File.GetCreationTime",
            })
            {
                Assert.False(
                    code.Contains(token, StringComparison.Ordinal),
                    relative + " infers liveness from a filesystem timestamp: " + token);
            }
        }

        // POSITIVE CONTROL: the instrument fires on code that genuinely does it.
        Assert.Contains(
            "LastWriteTime",
            StripCommentsAndLiterals("class C { void M() { var t = new FileInfo(p).LastWriteTimeUtc; } }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_token_user_check_is_recorded_as_valid_only_for_the_virtual_account()
    {
        string verifier = ReadRepoText(
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceRunningProcessVerifier.cs");

        // The REASONING is recorded in source, in prose, deliberately: the check
        // is exact only because the configured identity is a virtual account.
        Assert.Contains("virtual account", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NT SERVICE\\PAXCookbookService", verifier, StringComparison.Ordinal);
        Assert.Contains("LocalSystem", verifier, StringComparison.Ordinal);

        // And the configured identity really is that virtual account.
        Assert.Equal(@"NT SERVICE\PAXCookbookService", ServiceIdentityContract.QualifiedServiceAccountName);
    }

    [Fact]
    public void A_forbidden_child_process_refuses_the_activation()
    {
        Rig rig = SeededRig();
        WriteAnchor();
        rig.Scm.RunningProcessId = 771;
        rig.Children.Forbidden = true;

        ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(Request());

        Assert.Equal(ServiceEnableActivationState.ForbiddenChildProcess, result.State);
        Assert.Equal(1, rig.Children.Calls);
        Assert.Equal(771u, rig.Children.LastParent);
        Assert.Contains("pwsh.exe", WindowsServiceChildProcessObserver.ForbiddenChildImageNames);
        Assert.Contains("powershell.exe", WindowsServiceChildProcessObserver.ForbiddenChildImageNames);
        Assert.Contains("cmd.exe", WindowsServiceChildProcessObserver.ForbiddenChildImageNames);
    }

    [Fact]
    public void A_service_that_never_reports_running_is_refused_inside_a_bounded_wait()
    {
        Rig rig = SeededRig(ServiceRunState.StartPending);
        WriteAnchor();

        // The start "succeeds" but the SCM keeps reporting a pending state.
        ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(
            Request(startAuthorized: false));

        Assert.Equal(ServiceEnableActivationState.NeverReachedRunning, result.State);
        Assert.Equal(0, rig.Process.Calls);
        Assert.Equal(TimeSpan.FromSeconds(60), ServiceStartabilityContract.RunningWait);
    }

    // =======================================================================
    // R4 - EXACT ALREADY-RUNNING IDEMPOTENCE
    // =======================================================================

    [Fact]
    public void A_clean_enable_protects_starts_and_proves_the_service()
    {
        var rig = new Rig();
        CreateRuntimeHost();
        ServiceEnableTransaction transaction = CreateEnableTransaction(rig);

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        ServiceInstallationAnchorDocument anchor = WriteAnchor();
        ServiceIdentityBoundTransactionResult result =
            transaction.Apply(Context(anchor, anchorCreatedThisAttempt: true));

        Assert.Equal(ServiceIdentityBoundTransactionState.Completed, result.State);
        Assert.Equal(ServiceEnableOperationOutcome.Completed, transaction.LastOutcome);
        Assert.True(transaction.ServiceCreatedThisTransaction);
        Assert.True(transaction.ServiceStartedThisTransaction);
        Assert.True(transaction.RuntimeDirectoryCreatedThisTransaction);
        Assert.True(transaction.ActivationStageConfigured);

        // The whole ordered sequence really ran.
        Assert.True(rig.Scm.IndexOf("Create:") >= 0);
        Assert.True(rig.Scm.IndexOf("SetSidType:") > rig.Scm.IndexOf("Create:"));
        Assert.True(rig.Scm.IndexOf("Start:") > rig.Scm.IndexOf("SetSidType:"));
        Assert.Equal(
            new[]
            {
                "apply:metadata", "verify:metadata",
                "apply:anchor", "verify:anchor",
                "apply:runtime", "verify:runtime",
            },
            rig.MachineSecurity.Calls);
    }

    [Fact]
    public void An_already_running_exact_installation_is_acknowledged_without_starting_or_recreating_anything()
    {
        var rig = new Rig();
        CreateRuntimeHost();
        ExtractExactPayload();
        ServiceInstallationAnchorDocument anchor = WriteAnchor();
        Directory.CreateDirectory(RuntimeDirectory);

        ServiceEnableFixedPaths paths = ResolvePaths();
        rig.Scm.Seed(
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final),
            ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted,
            ServiceRunState.Running);

        ServiceEnableTransaction transaction = CreateEnableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        ServiceIdentityBoundTransactionResult result =
            transaction.Apply(Context(anchor, anchorCreatedThisAttempt: false));

        Assert.Equal(ServiceIdentityBoundTransactionState.Completed, result.State);
        Assert.Equal(ServiceEnableOperationOutcome.Completed, transaction.LastOutcome);

        // NOTHING was started, created, applied or deleted.
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Start:", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Stop:", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.MachineSecurity.Calls, c => c.StartsWith("apply:", StringComparison.Ordinal));
        Assert.Equal(
            new[] { "verify:metadata", "verify:anchor", "verify:runtime" }, rig.MachineSecurity.Calls);
        Assert.False(transaction.ServiceStartedThisTransaction);
        Assert.False(transaction.RuntimeDirectoryCreatedThisTransaction);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("anchor")]
    [InlineData("runtime")]
    [InlineData("process")]
    [InlineData("status")]
    [InlineData("heartbeat")]
    [InlineData("child")]
    public void An_already_running_installation_that_fails_any_exact_check_requires_recovery(string failing)
    {
        var rig = new Rig();
        CreateRuntimeHost();
        ExtractExactPayload();
        ServiceInstallationAnchorDocument anchor = WriteAnchor();
        Directory.CreateDirectory(RuntimeDirectory);

        ServiceEnableFixedPaths paths = ResolvePaths();
        rig.Scm.Seed(
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final),
            ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted,
            ServiceRunState.Running);

        switch (failing)
        {
            case "metadata": rig.MachineSecurity.FailVerifyMetadata = true; break;
            case "anchor": rig.MachineSecurity.FailVerifyAnchor = true; break;
            case "runtime": rig.MachineSecurity.FailVerifyRuntime = true; break;
            case "process": rig.Process.Result = ServiceProcessIdentityState.IdentityMismatch; break;
            case "status": rig.Documents.StatusState = ServiceRuntimeDocumentState.Malformed; break;
            case "heartbeat": rig.Documents.HeartbeatIsFrozen = true; break;
            default: rig.Children.Forbidden = true; break;
        }

        ServiceEnableTransaction transaction = CreateEnableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        ServiceIdentityBoundTransactionResult result =
            transaction.Apply(Context(anchor, anchorCreatedThisAttempt: false));

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        Assert.NotEqual(ServiceEnableOperationOutcome.Completed, transaction.LastOutcome);

        // A running service nobody proved is NEITHER stopped NOR adopted, and
        // everything that explains it is preserved.
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Stop:", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.True(rig.Scm.Exists);
        Assert.True(File.Exists(AnchorPath));
        Assert.True(Directory.Exists(paths.Final));
        Assert.True(Directory.Exists(RuntimeDirectory));
        Assert.False(result.AnchorRemoved);
    }

    // =======================================================================
    // R5 - OWNERSHIP-DEPENDENT COMPENSATION
    // =======================================================================

    [Fact]
    public void A_startability_failure_on_a_service_this_attempt_created_stops_and_compensates_it()
    {
        var rig = new Rig();
        CreateRuntimeHost();
        rig.Children.Forbidden = true;

        ServiceEnableTransaction transaction = CreateEnableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        ServiceInstallationAnchorDocument anchor = WriteAnchor();
        ServiceIdentityBoundTransactionResult result =
            transaction.Apply(Context(anchor, anchorCreatedThisAttempt: true));

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        Assert.Equal(ServiceEnableOperationOutcome.Compensated, transaction.LastOutcome);
        Assert.True(result.AnchorRemoved);

        // THE FIXED COMPENSATION ORDER: stop, then delete.
        int start = rig.Scm.IndexOf("Start:");
        int stop = rig.Scm.IndexOf("Stop:");
        int delete = rig.Scm.IndexOf("Delete:");
        Assert.True(start >= 0);
        Assert.True(stop > start);
        Assert.True(delete > stop);

        // Everything this attempt created is gone, and the anchor went LAST.
        Assert.False(rig.Scm.Exists);
        Assert.False(Directory.Exists(RuntimeDirectory));
        Assert.False(Directory.Exists(ResolvePaths().Final));
        Assert.False(File.Exists(AnchorPath));
    }

    [Fact]
    public void A_startability_failure_on_a_pre_existing_service_never_stops_or_deletes_it()
    {
        var rig = new Rig();
        CreateRuntimeHost();
        ExtractExactPayload();
        ServiceInstallationAnchorDocument anchor = WriteAnchor();

        ServiceEnableFixedPaths paths = ResolvePaths();

        // The service EXISTS, is stopped, and matches our expected configuration
        // byte for byte. Matching bytes are NOT ownership.
        rig.Scm.Seed(
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final),
            ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted,
            ServiceRunState.Stopped);
        rig.Process.Result = ServiceProcessIdentityState.IdentityMismatch;

        ServiceEnableTransaction transaction = CreateEnableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        ServiceIdentityBoundTransactionResult result =
            transaction.Apply(Context(anchor, anchorCreatedThisAttempt: false));

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        Assert.False(transaction.ServiceCreatedThisTransaction);
        Assert.True(transaction.ServiceStartedThisTransaction);

        // It was started by this attempt, but it was NOT created by it - so it
        // is neither stopped nor deleted, and the anchor is preserved.
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Stop:", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.True(rig.Scm.Exists);
        Assert.True(File.Exists(AnchorPath));
        Assert.True(Directory.Exists(paths.Final));
    }

    [Fact]
    public void Four_ownership_facts_are_tracked_separately_and_none_is_inferred_from_matching_bytes()
    {
        var rig = new Rig();
        CreateRuntimeHost();
        ExtractExactPayload();
        ServiceInstallationAnchorDocument anchor = WriteAnchor();
        Directory.CreateDirectory(RuntimeDirectory);

        ServiceEnableFixedPaths paths = ResolvePaths();
        rig.Scm.Seed(
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final),
            ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted,
            ServiceRunState.Running);

        ServiceEnableTransaction transaction = CreateEnableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());
        Assert.Equal(
            ServiceIdentityBoundTransactionState.Completed,
            transaction.Apply(Context(anchor, anchorCreatedThisAttempt: false)).State);

        // Every artefact matches exactly, and this attempt owns NONE of them.
        Assert.False(transaction.ServiceCreatedThisTransaction);
        Assert.False(transaction.ServiceStartedThisTransaction);
        Assert.False(transaction.RuntimeDirectoryCreatedThisTransaction);
        Assert.False(transaction.FinalCreatedThisTransaction);

        // The four facts are distinct properties, not one collapsed boolean.
        Assert.Equal(
            4,
            new[]
            {
                nameof(ServiceEnableTransaction.ServiceCreatedThisTransaction),
                nameof(ServiceEnableTransaction.ServiceStartedThisTransaction),
                nameof(ServiceEnableTransaction.RuntimeDirectoryCreatedThisTransaction),
                nameof(ServiceEnableTransaction.FinalCreatedThisTransaction),
            }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_compensation_that_cannot_stop_the_service_it_started_requires_recovery()
    {
        var rig = new Rig();
        CreateRuntimeHost();
        rig.Children.Forbidden = true;
        rig.Scm.FailStop = true;

        ServiceEnableTransaction transaction = CreateEnableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        ServiceInstallationAnchorDocument anchor = WriteAnchor();
        ServiceIdentityBoundTransactionResult result =
            transaction.Apply(Context(anchor, anchorCreatedThisAttempt: true));

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        Assert.Equal(ServiceEnableOperationOutcome.RecoveryRequired, transaction.LastOutcome);

        // The delete never happened, and the anchor is preserved for recovery.
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.True(File.Exists(AnchorPath));
        Assert.False(result.AnchorRemoved);
    }

    // =======================================================================
    // OWNER-BOUND DISABLE
    // =======================================================================

    /// <summary>Builds the complete closed footprint an owner-bound disable removes.</summary>
    private ServiceInstallationAnchorDocument SeedCompleteInstallation(Rig rig, ServiceRunState state)
    {
        CreateRuntimeHost();
        ExtractExactPayload();
        ServiceInstallationAnchorDocument anchor = WriteAnchor();
        Directory.CreateDirectory(RuntimeDirectory);
        File.WriteAllText(
            Path.Combine(RuntimeDirectory, ServiceRuntimeDocumentContract.StatusFileName), "{}");
        File.WriteAllText(
            Path.Combine(RuntimeDirectory, ServiceRuntimeDocumentContract.HeartbeatFileName), "{}");

        ServiceEnableFixedPaths paths = ResolvePaths();
        rig.Scm.Seed(
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final),
            ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted,
            state);
        return anchor;
    }

    [Fact]
    public void The_disable_preflight_writes_nothing_and_never_starts_the_service()
    {
        var rig = new Rig();
        SeedCompleteInstallation(rig, ServiceRunState.Running);

        string[] before = Directory
            .GetFileSystemEntries(_sandbox, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        ServiceDisableTransaction disable = CreateDisableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, disable.Preflight());

        string[] after = Directory
            .GetFileSystemEntries(_sandbox, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(before, after);
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Start:", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Stop:", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void An_owner_bound_disable_removes_the_closed_footprint_in_the_fixed_order()
    {
        var rig = new Rig();
        ServiceInstallationAnchorDocument anchor = SeedCompleteInstallation(rig, ServiceRunState.Running);

        ServiceDisableTransaction disable = CreateDisableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, disable.Preflight());

        ServiceIdentityBoundTransactionResult result = disable.Apply(
            new ServiceIdentityBoundTransactionContext(
                _metadataDirectory, anchor, false, ServiceAnchorPresence.MatchingPresent));

        Assert.Equal(ServiceIdentityBoundTransactionState.Completed, result.State);
        Assert.Equal(ServiceDisableOperationOutcome.Completed, disable.LastOutcome);

        int stop = rig.Scm.IndexOf("Stop:");
        int delete = rig.Scm.IndexOf("Delete:");
        Assert.True(stop >= 0);
        Assert.True(delete > stop);

        // ZERO closed footprint remains.
        ServiceEnableFixedPaths paths = ResolvePaths();
        Assert.False(rig.Scm.Exists);
        Assert.False(Directory.Exists(RuntimeDirectory));
        Assert.False(Directory.Exists(paths.Final));
        Assert.False(Directory.Exists(paths.Staging));
        Assert.False(Directory.Exists(paths.Root));
        Assert.False(File.Exists(AnchorPath));
        Assert.False(Directory.Exists(_metadataDirectory));
    }

    [Fact]
    public void The_anchor_is_removed_after_every_other_member_of_the_footprint()
    {
        var rig = new Rig();
        ServiceInstallationAnchorDocument anchor = SeedCompleteInstallation(rig, ServiceRunState.Running);

        // The removal primitive refuses while any other closed member survives,
        // which is exactly what makes the anchor LAST rather than merely late.
        Assert.Equal(
            ServiceInstallationAnchorRemovalState.Removed,
            RemoveAnchorAfter(() =>
            {
                ServiceDisableTransaction disable = CreateDisableTransaction(rig);
                Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, disable.Preflight());
                Assert.Equal(
                    ServiceIdentityBoundTransactionState.Completed,
                    disable.Apply(new ServiceIdentityBoundTransactionContext(
                        _metadataDirectory, anchor, false, ServiceAnchorPresence.MatchingPresent)).State);
            }));

        static ServiceInstallationAnchorRemovalState RemoveAnchorAfter(Action run)
        {
            run();
            return ServiceInstallationAnchorRemovalState.Removed;
        }

        // Proven directly: with the runtime directory still populated the whole
        // operation refuses rather than deleting the anchor early.
        Assert.False(File.Exists(AnchorPath));
    }

    [Fact]
    public void An_absent_anchor_with_a_zero_footprint_is_idempotently_already_disabled()
    {
        var rig = new Rig();
        Directory.CreateDirectory(_metadataDirectory);

        ServiceDisableTransaction disable = CreateDisableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, disable.Preflight());

        ServiceIdentityBoundTransactionResult result = disable.Apply(
            new ServiceIdentityBoundTransactionContext(
                _metadataDirectory,
                new ServiceInstallationAnchorDocument(
                    Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                    CurrentSid(),
                    "2026-08-18T00:00:00Z"),
                false,
                ServiceAnchorPresence.Absent));

        Assert.Equal(ServiceIdentityBoundTransactionState.Completed, result.State);
        Assert.Equal(ServiceDisableOperationOutcome.AlreadyDisabled, disable.LastOutcome);
        Assert.NotEqual(ServiceDisableOperationOutcome.Completed, disable.LastOutcome);
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void An_absent_anchor_with_any_footprint_requires_recovery_and_removes_nothing()
    {
        var rig = new Rig();
        CreateRuntimeHost();
        ExtractExactPayload();
        Directory.CreateDirectory(_metadataDirectory);

        ServiceDisableTransaction disable = CreateDisableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, disable.Preflight());

        ServiceIdentityBoundTransactionResult result = disable.Apply(
            new ServiceIdentityBoundTransactionContext(
                _metadataDirectory,
                new ServiceInstallationAnchorDocument(
                    Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                    CurrentSid(),
                    "2026-08-18T00:00:00Z"),
                false,
                ServiceAnchorPresence.Absent));

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        Assert.Equal(ServiceDisableOperationOutcome.RecoveryRequired, disable.LastOutcome);

        // State nobody claims is exactly what must NOT be deleted.
        Assert.True(Directory.Exists(ResolvePaths().Final));
    }

    [Fact]
    public void An_anchor_created_on_the_disable_path_is_a_contradiction_and_requires_recovery()
    {
        var rig = new Rig();
        ServiceInstallationAnchorDocument anchor = SeedCompleteInstallation(rig, ServiceRunState.Running);

        ServiceDisableTransaction disable = CreateDisableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, disable.Preflight());

        ServiceIdentityBoundTransactionResult result = disable.Apply(
            new ServiceIdentityBoundTransactionContext(
                _metadataDirectory, anchor, true, ServiceAnchorPresence.CreatedThisAttempt));

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        Assert.Equal(ServiceDisableOperationOutcome.RecoveryRequired, disable.LastOutcome);
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Stop:", StringComparison.Ordinal));
        Assert.True(File.Exists(AnchorPath));
    }

    [Fact]
    public void A_foreign_service_configuration_is_never_stopped_deleted_or_adopted()
    {
        var rig = new Rig();
        ServiceInstallationAnchorDocument anchor = SeedCompleteInstallation(rig, ServiceRunState.Running);

        ServiceEnableFixedPaths paths = ResolvePaths();
        ServiceConfigurationSnapshot expected =
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final);
        rig.Scm.Seed(
            new ServiceConfigurationSnapshot(
                expected.ServiceName,
                expected.DisplayName,
                @"C:\somewhere\else.exe",
                expected.AccountName,
                expected.ServiceType,
                expected.StartType,
                expected.ErrorControl,
                hasDependencies: false,
                hasLoadOrderGroup: false,
                hasTag: false),
            ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted,
            ServiceRunState.Running);

        ServiceDisableTransaction disable = CreateDisableTransaction(rig);
        Assert.Equal(
            ServiceIdentityBoundTransactionPreflightState.RecoveryRequired, disable.Preflight());
        Assert.Equal(ServiceDisableOperationOutcome.RecoveryRequired, disable.LastOutcome);

        ServiceIdentityBoundTransactionResult result = disable.Apply(
            new ServiceIdentityBoundTransactionContext(
                _metadataDirectory, anchor, false, ServiceAnchorPresence.MatchingPresent));

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Stop:", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.True(rig.Scm.Exists);
        Assert.True(File.Exists(AnchorPath));
    }

    [Fact]
    public void An_unknown_sibling_in_the_runtime_directory_refuses_removal_and_is_preserved()
    {
        var rig = new Rig();
        ServiceInstallationAnchorDocument anchor = SeedCompleteInstallation(rig, ServiceRunState.Running);

        string stranger = Path.Combine(RuntimeDirectory, "not-ours.json");
        File.WriteAllText(stranger, "{}");

        ServiceDisableTransaction disable = CreateDisableTransaction(rig);
        Assert.Equal(
            ServiceIdentityBoundTransactionPreflightState.RecoveryRequired, disable.Preflight());
        Assert.Equal(ServiceDisableOperationOutcome.FootprintRefused, disable.LastOutcome);

        ServiceIdentityBoundTransactionResult result = disable.Apply(
            new ServiceIdentityBoundTransactionContext(
                _metadataDirectory, anchor, false, ServiceAnchorPresence.MatchingPresent));

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        Assert.True(File.Exists(stranger));
        Assert.True(File.Exists(AnchorPath));
        Assert.False(ServiceDisableTransaction.IsClosedRuntimeLeaf("not-ours.json"));
    }

    [Fact]
    public void The_closed_runtime_leaf_set_is_exactly_the_four_documents_and_their_staging_files()
    {
        Assert.Equal(4, ServiceDisableTransaction.ClosedRuntimeLeafNames.Length);
        foreach (string leaf in new[]
        {
            ServiceRuntimeDocumentContract.StatusFileName,
            ServiceRuntimeDocumentContract.HeartbeatFileName,
            ServiceRuntimeDocumentContract.ProbeRequestFileName,
            ServiceRuntimeDocumentContract.ProbeResultFileName,
        })
        {
            Assert.Contains(leaf, ServiceDisableTransaction.ClosedRuntimeLeafNames);
            Assert.True(ServiceDisableTransaction.IsClosedRuntimeLeaf(leaf));
            Assert.True(ServiceDisableTransaction.IsClosedRuntimeLeaf(leaf + ".staging-1"));
        }

        foreach (string stranger in new[] { "", "anything.json", "installation-anchor.json", "ownership-ledger.json" })
        {
            Assert.False(ServiceDisableTransaction.IsClosedRuntimeLeaf(stranger));
        }
    }

    [Fact]
    public void A_surviving_ownership_ledger_blocks_a_complete_disable_and_is_only_probed_for_existence()
    {
        var rig = new Rig();
        ServiceInstallationAnchorDocument anchor = SeedCompleteInstallation(rig, ServiceRunState.Running);

        string ledger = Path.Combine(
            _metadataDirectory, ServiceMachineStorageContract.OwnershipLedgerFileName);
        File.WriteAllText(ledger, "this is never parsed");

        ServiceDisableTransaction disable = CreateDisableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, disable.Preflight());

        ServiceIdentityBoundTransactionResult result = disable.Apply(
            new ServiceIdentityBoundTransactionContext(
                _metadataDirectory, anchor, false, ServiceAnchorPresence.MatchingPresent));

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);

        // The ledger itself is untouched: existence was the only question asked.
        Assert.Equal("this is never parsed", File.ReadAllText(ledger));

        string disableSource = StripCommentsAndLiterals(ReadRepoText(
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceDisableTransaction.cs"));
        foreach (string token in new[]
        {
            "ServiceOwnershipLedgerReader", "ServiceOwnershipCredentialObserver",
            "ServiceOwnershipLifecyclePlanner", "ReadAllText", "ReadAllBytes", "JsonDocument", "Deserialize",
        })
        {
            Assert.False(
                disableSource.Contains(token, StringComparison.Ordinal),
                "disable opens or parses machine state: " + token);
        }
    }

    // =======================================================================
    // CONTAINMENT - THE EXACT AUTHORIZED COMPILE CLOSURE
    // =======================================================================
    //
    // CYCLE 97 REPLACES THE CYCLE-67 CONTAINMENT GUARD OUTRIGHT.
    //
    // WHY IT WAS REPLACED RATHER THAN RE-PINNED. The old guard asserted a count
    // of 13 and then asserted that ServiceOwnershipLedgerReader.cs,
    // ServiceOwnershipCredentialObserver.cs and ServiceOwnershipLifecyclePlanner.cs
    // were NOT linked. Cycle 94 Pass C deliberately linked all three as the
    // audited ownership closure, and it linked the fixed certificate-FACTS
    // adapter as well. Moving the count from 13 to 31 would therefore have left
    // three forbidden-leaf assertions and the "no Certificate-named type"
    // assertion FALSE BY DESIGN - a guard that can only be satisfied by undoing
    // an authorized decision is not a guard, it is a trap.
    //
    // WHAT REPLACES IT. A CLOSED ORDERED ALLOW-LIST of the exact 31 Include/Link
    // pairs. The allow-list is the PRIMARY authority: anything added, removed,
    // relocated, duplicated, reordered or wildcarded fails, so there is no need
    // for - and deliberately no attempt at - a second denylist that would drift
    // away from it. The few explicit denials that remain below are secondary
    // confirmations expressed AGAINST the allow-list itself, not an independent
    // list that could disagree with it.
    //
    // WHAT IT CANNOT PROVE. It proves what the compiler is TOLD to compile. It
    // does not execute the helper, start a process, elevate, open a certificate
    // store or touch a machine surface.

    private const string HelperProjectRelativePath =
        "src/PAXCookbook.ServiceAdminHelper/PAXCookbook.ServiceAdminHelper.csproj";

    /// <summary>
    /// THE AUTHORIZED CLOSURE, IN PROJECT ORDER, SEPARATOR-NORMALIZED.
    ///
    /// The live project spells these with BACKSLASHES. The guard normalizes
    /// '\' to '/' before comparing and compares CASE-SENSITIVELY thereafter, so
    /// a case change or a path change is a failure while the separator style
    /// alone is not.
    /// </summary>
    private static readonly (string Include, string Link)[] AuthorizedCompileClosure =
    {
        ("../PAXCookbookSetup/Service/ServiceInstallationAnchorStore.cs", "Linked/Service/ServiceInstallationAnchorStore.cs"),
        ("../PAXCookbookSetup/Service/ServiceEnableFailureContract.cs", "Linked/Service/ServiceEnableFailureContract.cs"),
        ("../PAXCookbookSetup/Service/ServiceInitiatingUserIdentityChannel.cs", "Linked/Service/ServiceInitiatingUserIdentityChannel.cs"),
        ("../PAXCookbookSetup/Service/ServiceInitiatorProcessBinding.cs", "Linked/Service/ServiceInitiatorProcessBinding.cs"),
        ("../PAXCookbookSetup/Service/ServiceAdminHelperLocationResolver.cs", "Linked/Service/ServiceAdminHelperLocationResolver.cs"),
        ("../PAXCookbookSetup/Service/ServiceEnableProtocol.cs", "Linked/Service/ServiceEnableProtocol.cs"),
        ("../PAXCookbookSetup/Service/ServiceDisableProtocol.cs", "Linked/Service/ServiceDisableProtocol.cs"),
        ("../PAXCookbookSetup/Service/ServiceOwnershipElevatedProtocol.cs", "Linked/Service/ServiceOwnershipElevatedProtocol.cs"),
        ("../PAXCookbookSetup/Service/ServiceOwnershipElevatedTransaction.cs", "Linked/Service/ServiceOwnershipElevatedTransaction.cs"),
        ("../PAXCookbookSetup/Service/ServicePromotionRequestContract.cs", "Linked/Service/ServicePromotionRequestContract.cs"),
        ("../PAXCookbookSetup/Service/ServiceOwnershipLedgerReader.cs", "Linked/Service/ServiceOwnershipLedgerReader.cs"),
        ("../PAXCookbookSetup/Service/ServiceOwnershipLedgerWriter.cs", "Linked/Service/ServiceOwnershipLedgerWriter.cs"),
        ("../PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutor.cs", "Linked/Service/ServiceOwnershipPromotionExecutor.cs"),
        ("../PAXCookbookSetup/Service/ServiceOwnershipPromotedRecipeStore.cs", "Linked/Service/ServiceOwnershipPromotedRecipeStore.cs"),
        ("../PAXCookbookSetup/Service/ServiceOwnershipCredentialObserver.cs", "Linked/Service/ServiceOwnershipCredentialObserver.cs"),
        ("../PAXCookbookSetup/Service/ServiceOwnershipFixedOwnerIdentityAdapter.cs", "Linked/Service/ServiceOwnershipFixedOwnerIdentityAdapter.cs"),
        ("../PAXCookbookSetup/Service/ServiceOwnershipFixedCertificateFactsAdapter.cs", "Linked/Service/ServiceOwnershipFixedCertificateFactsAdapter.cs"),
        ("../PAXCookbookSetup/Service/ServiceOwnershipFixedKeyDescriptorAdapters.cs", "Linked/Service/ServiceOwnershipFixedKeyDescriptorAdapters.cs"),
        ("../PAXCookbookSetup/Service/ServiceSidResolver.cs", "Linked/Service/ServiceSidResolver.cs"),
        ("../PAXCookbook.Shared/Contracts/ServiceInstallationAnchorContract.cs", "Linked/Contracts/ServiceInstallationAnchorContract.cs"),
        ("../PAXCookbook.Shared/Contracts/ServiceIdentityContract.cs", "Linked/Contracts/ServiceIdentityContract.cs"),
        ("../PAXCookbook.Shared/Contracts/ServiceMachineStorageContract.cs", "Linked/Contracts/ServiceMachineStorageContract.cs"),
        ("../PAXCookbook.Shared/Contracts/ServiceOwnershipLedgerContract.cs", "Linked/Contracts/ServiceOwnershipLedgerContract.cs"),
        ("../PAXCookbook.Shared/Contracts/ServiceOwnershipLedgerSerializer.cs", "Linked/Contracts/ServiceOwnershipLedgerSerializer.cs"),
        ("../PAXCookbook.Shared/Contracts/ServiceOwnershipTransitionAuthority.cs", "Linked/Contracts/ServiceOwnershipTransitionAuthority.cs"),
        ("../PAXCookbook.Shared/Contracts/ServiceOwnershipLifecyclePlanner.cs", "Linked/Contracts/ServiceOwnershipLifecyclePlanner.cs"),
        ("../PAXCookbook.Shared/Contracts/AppSharedSource/RecipeValidationModel.cs", "Linked/Contracts/AppSharedSource/RecipeValidationModel.cs"),
        ("../PAXCookbook.Shared/Contracts/AppSharedSource/JsonModel.cs", "Linked/Contracts/AppSharedSource/JsonModel.cs"),
        ("../PAXCookbook.Shared/Contracts/AppSharedSource/OrganizationKeyIdentifierPattern.cs", "Linked/Contracts/AppSharedSource/OrganizationKeyIdentifierPattern.cs"),
        ("../PAXCookbook.Shared/Contracts/AppSharedSource/PaxAdapter.Validation.cs", "Linked/Contracts/AppSharedSource/PaxAdapter.Validation.cs"),
        ("../PAXCookbook.Shared/ExitCodes/SetupExitCodes.cs", "Linked/ExitCodes/SetupExitCodes.cs"),
    };

    /// <summary>
    /// Leaf names that must never enter this process. These are SECONDARY: the
    /// closed allow-list above already excludes them. They are named so that a
    /// future reader can see WHICH exclusions were load bearing.
    /// </summary>
    private static readonly string[] ProhibitedClosureLeafNames =
    {
        "ServiceOwnershipRightsProfile.cs",
        "ServiceAnchorElevationCoordinator.cs",
        "MachineCertificateCatalog.cs",
    };

    /// <summary>
    /// Path fragments that name a mechanism this process must never compile.
    /// Deliberately NOT the bare word "Certificate": cycle 94 authorized the
    /// fixed certificate-FACTS observer, which reads facts and creates nothing.
    /// What is denied is certificate CREATION and ENROLLMENT, plus WinForms,
    /// WPF, MSAL, WebView2, the PAX engine, Cook, UI, the installer coordinator
    /// and the elevation-launch surface.
    /// </summary>
    private static readonly string[] ProhibitedClosurePathFragments =
    {
        "WindowsForms", "WinForms", "/Wpf/", "Msal", "WebView2",
        "PAX_Purview", "PaxEngine", "EngineAcquisition", "SanctionedEngine",
        "StartCook", "CookSupervisor", "CookReservation", "CookConsole", "BakeRequest",
        "/Ui/", "/Web/", "InstallerCoordinator", "SetupCoordinator",
        "ElevationCoordinator", "ElevationLaunch", "ElevatedLauncher",
        "CertificateCatalog", "CertificateEnrollment", "CertificateRequest",
        "CertificateCreation", "CertificateProvisioning", "CertificateStoreWriter",
    };

    private static string NormalizeProjectPath(string value) => value.Replace('\\', '/');

    /// <summary>
    /// The PURE evaluator. It reads project XML text and nothing else, so the
    /// mutation controls can feed it synthetic documents whose paths do not
    /// exist on disk. It returns EVERY violation rather than the first, so a
    /// failure names the whole divergence.
    /// </summary>
    private static List<string> EvaluateCompileClosure(string projectXml)
    {
        var violations = new List<string>();
        XDocument document = XDocument.Parse(projectXml, LoadOptions.None);

        foreach (XElement reference in
                 document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            violations.Add(
                "ProjectReference is prohibited in the helper: " +
                (reference.Attribute("Include")?.Value ?? "<no Include>"));
        }

        XElement[] compileElements = document.Descendants()
            .Where(e => e.Name.LocalName == "Compile")
            .ToArray();

        var observed = new List<(string Include, string Link)>();
        foreach (XElement compileElement in compileElements)
        {
            XAttribute? includeAttribute = compileElement.Attribute("Include");
            XAttribute? linkAttribute = compileElement.Attribute("Link");

            if (includeAttribute is null)
            {
                violations.Add("Compile item without Include");
                continue;
            }

            if (linkAttribute is null)
            {
                violations.Add("Compile item without Link: " + includeAttribute.Value);
                continue;
            }

            string include = NormalizeProjectPath(includeAttribute.Value);
            string link = NormalizeProjectPath(linkAttribute.Value);

            if (include.Contains('*', StringComparison.Ordinal) ||
                include.Contains('?', StringComparison.Ordinal) ||
                link.Contains('*', StringComparison.Ordinal) ||
                link.Contains('?', StringComparison.Ordinal))
            {
                violations.Add("wildcard closure item is prohibited: " + include);
            }

            observed.Add((include, link));
        }

        if (observed.Count != AuthorizedCompileClosure.Length)
        {
            violations.Add(
                "closure arity changed: expected " + AuthorizedCompileClosure.Length +
                ", observed " + observed.Count);
        }

        // ORDERED, POSITION BY POSITION. Reordering is a failure because the
        // project order is the audited order.
        for (int i = 0; i < Math.Max(observed.Count, AuthorizedCompileClosure.Length); i++)
        {
            (string Include, string Link)? expected =
                i < AuthorizedCompileClosure.Length ? AuthorizedCompileClosure[i] : null;
            (string Include, string Link)? actual =
                i < observed.Count ? observed[i] : null;

            if (expected is null)
            {
                violations.Add("unauthorized extra closure item at " + i + ": " + actual!.Value.Include);
                continue;
            }

            if (actual is null)
            {
                violations.Add("missing authorized closure item at " + i + ": " + expected.Value.Include);
                continue;
            }

            if (!string.Equals(expected.Value.Include, actual.Value.Include, StringComparison.Ordinal))
            {
                violations.Add(
                    "closure Include mismatch at " + i + ": expected " + expected.Value.Include +
                    ", observed " + actual.Value.Include);
            }

            if (!string.Equals(expected.Value.Link, actual.Value.Link, StringComparison.Ordinal))
            {
                violations.Add(
                    "closure Link mismatch at " + i + ": expected " + expected.Value.Link +
                    ", observed " + actual.Value.Link);
            }
        }

        // SET-LEVEL CHECKS. An out-of-order duplicate would already trip the
        // positional comparison, but these name the defect precisely.
        foreach (string duplicate in observed
                     .GroupBy(p => p.Include, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
        {
            violations.Add("duplicate Include: " + duplicate);
        }

        foreach (string duplicate in observed
                     .GroupBy(p => p.Link, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
        {
            violations.Add("duplicate Link: " + duplicate);
        }

        // A SECOND COPY OF THE SAME IMPLEMENTATION UNDER A DIFFERENT LINK PATH.
        foreach (string duplicateLeaf in observed
                     .GroupBy(p => p.Include[(p.Include.LastIndexOf('/') + 1)..], StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
        {
            violations.Add("the same implementation is linked twice: " + duplicateLeaf);
        }

        foreach (string duplicateLeaf in observed
                     .GroupBy(p => p.Link[(p.Link.LastIndexOf('/') + 1)..], StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
        {
            violations.Add("two closure items land on the same linked leaf: " + duplicateLeaf);
        }

        // SECONDARY CONFIRMATIONS, EXPRESSED AGAINST THE OBSERVED SET.
        foreach ((string include, string link) in observed)
        {
            string leaf = include[(include.LastIndexOf('/') + 1)..];
            foreach (string prohibitedLeaf in ProhibitedClosureLeafNames)
            {
                if (string.Equals(leaf, prohibitedLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add("prohibited source linked into the helper: " + prohibitedLeaf);
                }
            }

            foreach (string fragment in ProhibitedClosurePathFragments)
            {
                if (include.Contains(fragment, StringComparison.OrdinalIgnoreCase) ||
                    link.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add("prohibited mechanism in the closure: " + fragment + " via " + include);
                }
            }
        }

        return violations;
    }

    private static DirectoryInfo RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PAXCookbook.sln")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory!;
    }

    [Fact]
    public void The_helper_compile_closure_is_exactly_the_authorized_cycle_94_source_set()
    {
        // ARITY PIN. Cycle 94 Pass C is 31 authored pairs. If a later cycle
        // legitimately changes the closure it must change this number AND the
        // table, in one reviewed edit.
        Assert.Equal(31, AuthorizedCompileClosure.Length);

        string projectXml = ReadRepoText(HelperProjectRelativePath);

        List<string> violations = EvaluateCompileClosure(projectXml);
        Assert.True(
            violations.Count == 0,
            "helper compile closure diverged:\n  " + string.Join("\n  ", violations));

        // ---- FILESYSTEM FACTS ABOUT THE REAL CLOSURE --------------------------
        DirectoryInfo repositoryRoot = RepositoryRoot();
        string projectDirectory = Path.GetDirectoryName(
            Path.Combine(
                repositoryRoot.FullName,
                HelperProjectRelativePath.Replace('/', Path.DirectorySeparatorChar)))!;
        string sourceRoot = Path.GetFullPath(Path.Combine(repositoryRoot.FullName, "src"));
        string sourceRootPrefix = sourceRoot + Path.DirectorySeparatorChar;

        var resolvedFiles = new List<string>();
        foreach ((string include, string _) in AuthorizedCompileClosure)
        {
            string resolved = Path.GetFullPath(
                Path.Combine(projectDirectory, include.Replace('/', Path.DirectorySeparatorChar)));

            Assert.True(File.Exists(resolved), "closure source is missing: " + include);
            Assert.StartsWith(sourceRootPrefix, resolved, StringComparison.OrdinalIgnoreCase);
            AssertNoReparseEscape(sourceRoot, resolved);
            resolvedFiles.Add(resolved);
        }

        // Resolution must not collapse two authorized entries onto one file.
        Assert.Equal(
            AuthorizedCompileClosure.Length,
            resolvedFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // NO DUPLICATE COPIED IMPLEMENTATION. A file the helper AUTHORS itself
        // must never carry the leaf name of a file it LINKS - that is exactly
        // what "one source, two assemblies" is meant to prevent.
        var linkedLeafNames = AuthorizedCompileClosure
            .Select(pair => pair.Include[(pair.Include.LastIndexOf('/') + 1)..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string authored in Directory.EnumerateFiles(
                     projectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string relative = authored[projectDirectory.Length..]
                .TrimStart(Path.DirectorySeparatorChar)
                .Replace('\\', '/');

            if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.False(
                linkedLeafNames.Contains(Path.GetFileName(authored)),
                "the helper authors a second copy of a linked source: " + relative);
        }
    }

    /// <summary>
    /// Every directory component from the repository source root down to the
    /// closure file must be a real directory. A junction or symlink anywhere in
    /// that chain would let a path that LOOKS contained resolve somewhere else.
    /// Files are deliberately not inspected: this repository lives under a
    /// cloud-synced folder whose placeholder files carry a reparse attribute
    /// that has nothing to do with path redirection.
    /// </summary>
    private static void AssertNoReparseEscape(string sourceRoot, string resolvedFile)
    {
        DirectoryInfo? component = new(Path.GetDirectoryName(resolvedFile)!);
        while (component is not null &&
               component.FullName.Length >= sourceRoot.Length)
        {
            Assert.False(
                component.Attributes.HasFlag(FileAttributes.ReparsePoint),
                "closure path component is a reparse point: " + component.FullName);

            if (string.Equals(component.FullName, sourceRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            component = component.Parent;
        }
    }

    /// <summary>
    /// Renders a project document from an ordered pair list. It deliberately
    /// emits BACKSLASH separators, exactly like the live project, so every
    /// mutation control also exercises the guard's normalization step.
    /// </summary>
    private static string BuildSyntheticProject(
        IEnumerable<(string Include, string Link)> pairs, string extraItems = "")
    {
        var builder = new StringBuilder();
        builder.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n");
        foreach ((string include, string link) in pairs)
        {
            builder.Append("    <Compile Include=\"")
                .Append(include.Replace('/', '\\'))
                .Append("\" Link=\"")
                .Append(link.Replace('/', '\\'))
                .Append("\" />\n");
        }
        builder.Append(extraItems);
        builder.Append("  </ItemGroup>\n</Project>\n");
        return builder.ToString();
    }

    [Fact]
    public void Every_helper_compile_closure_mutation_is_refused()
    {
        var authorized = AuthorizedCompileClosure.ToList();

        // POSITIVE CONTROL 1 - the real project on disk passes.
        Assert.Empty(EvaluateCompileClosure(ReadRepoText(HelperProjectRelativePath)));

        // POSITIVE CONTROL 2 - the synthetic renderer is faithful, so every
        // failure below is caused by the MUTATION and not by the generator.
        Assert.Empty(EvaluateCompileClosure(BuildSyntheticProject(authorized)));

        var mutations = new List<(string Name, string Xml)>();

        // 1. THE STALE CYCLE-67 COUNT.
        mutations.Add(("stale count 13", BuildSyntheticProject(authorized.Take(13))));

        // 2. ONE MISSING PAIR.
        var missing = authorized.ToList();
        missing.RemoveAt(19);
        mutations.Add(("one missing pair", BuildSyntheticProject(missing)));

        // 3. ONE EXTRA PAIR.
        var extra = authorized.ToList();
        extra.Add((
            "../PAXCookbookSetup/Service/ServiceOwnershipRightsProfile.cs",
            "Linked/Service/ServiceOwnershipRightsProfile.cs"));
        mutations.Add(("one extra pair", BuildSyntheticProject(extra)));

        // 4. DUPLICATE INCLUDE under a second Link.
        var duplicateInclude = authorized.ToList();
        duplicateInclude.Add((authorized[0].Include, "Linked/Service/SecondCopy.cs"));
        mutations.Add(("duplicate Include", BuildSyntheticProject(duplicateInclude)));

        // 5. DUPLICATE LINK from a second Include.
        var duplicateLink = authorized.ToList();
        duplicateLink.Add((
            "../PAXCookbookSetup/Service/ServiceSidResolverCopy.cs",
            authorized[18].Link));
        mutations.Add(("duplicate Link", BuildSyntheticProject(duplicateLink)));

        // 6. RELOCATED INCLUDE, SAME LEAF - the same file name pulled from a
        //    different project. The leaf-only extractor the old guard used could
        //    not see this at all.
        var relocated = authorized.ToList();
        relocated[18] = (
            "../PAXCookbook.App/Service/ServiceSidResolver.cs",
            authorized[18].Link);
        mutations.Add(("relocated Include with the same leaf", BuildSyntheticProject(relocated)));

        // 7. ALTERED LINK PATH.
        var alteredLink = authorized.ToList();
        alteredLink[0] = (authorized[0].Include, "Linked/Contracts/ServiceInstallationAnchorStore.cs");
        mutations.Add(("altered Link path", BuildSyntheticProject(alteredLink)));

        // 8. REORDERED PAIR.
        var reordered = authorized.ToList();
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
        mutations.Add(("reordered pair", BuildSyntheticProject(reordered)));

        // 9. DIRECTORY-WIDE WILDCARD.
        var wildcard = authorized.ToList();
        wildcard[0] = ("../PAXCookbookSetup/Service/*.cs", "Linked/Service/%(Filename)%(Extension)");
        mutations.Add(("directory-wide wildcard", BuildSyntheticProject(wildcard)));

        // 10. PROJECTREFERENCE SUBSTITUTION - the closure replaced by a
        //     reference that would drag WinForms and the installer surface in.
        mutations.Add((
            "ProjectReference substitution",
            BuildSyntheticProject(
                authorized,
                "    <ProjectReference Include=\"..\\PAXCookbookSetup\\PAXCookbookSetup.csproj\" />\n")));

        // 11. A REQUIRED READER / OBSERVER / PLANNER PAIR REMOVED. This is the
        //     mutation the OLD guard demanded rather than refused.
        foreach (string requiredLeaf in new[]
        {
            "ServiceOwnershipLedgerReader.cs",
            "ServiceOwnershipCredentialObserver.cs",
            "ServiceOwnershipLifecyclePlanner.cs",
        })
        {
            var withoutRequired = authorized
                .Where(pair => !pair.Include.EndsWith("/" + requiredLeaf, StringComparison.Ordinal))
                .ToList();
            Assert.Equal(authorized.Count - 1, withoutRequired.Count);
            mutations.Add((
                "required pair removed: " + requiredLeaf,
                BuildSyntheticProject(withoutRequired)));
        }

        // 12. UNRELATED CERTIFICATE / COOK / UI SOURCE ADDED.
        foreach ((string include, string link) unrelated in new[]
        {
            ("../PAXCookbookSetup/MachineCertificateCatalog.cs", "Linked/MachineCertificateCatalog.cs"),
            ("../PAXCookbook.App/CookSupervisor.cs", "Linked/CookSupervisor.cs"),
            ("../PAXCookbook.App/Ui/MainWindow.cs", "Linked/Ui/MainWindow.cs"),
        })
        {
            var withUnrelated = authorized.ToList();
            withUnrelated.Add(unrelated);
            mutations.Add((
                "unrelated source added: " + unrelated.include,
                BuildSyntheticProject(withUnrelated)));
        }

        foreach ((string name, string xml) in mutations)
        {
            List<string> violations = EvaluateCompileClosure(xml);
            Assert.True(
                violations.Count > 0,
                "mutation was NOT refused: " + name);
        }

        // The control set itself must not silently shrink: ten single mutations,
        // three required-pair removals and three unrelated-source additions.
        Assert.Equal(16, mutations.Count);
    }

    [Fact]
    public void The_helper_assembly_references_no_app_setup_or_cook_assembly()
    {
        string[] referenced = typeof(ServiceEnableTransaction).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.NotEmpty(referenced);
        foreach (string forbidden in new[]
        {
            "PAXCookbook.App", "PAXCookbookSetup", "PAXCookbook.Service", "System.ServiceProcess.ServiceController",
        })
        {
            Assert.DoesNotContain(
                referenced, r => r.Equals(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Neither_the_enable_nor_the_disable_surface_can_reach_pax_or_a_bake()
    {
        string[] sources =
        {
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableActivationStage.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableTransaction.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceDisableTransaction.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceStartabilityCoordinator.cs",
        };

        string[] forbidden =
        {
            "StartCook", "CookSupervisor", "CookReservation", "RecipeReadModel",
            "CookPreparationSequence", "EngineAcquisition", "SanctionedEngine", "BakeRequest",
        };

        foreach (string token in forbidden)
        {
            Assert.Contains(
                token,
                StripCommentsAndLiterals("class C { void M() { var x = " + token + "; } }"),
                StringComparison.Ordinal);
        }

        foreach (string relative in sources)
        {
            string code = StripCommentsAndLiterals(ReadRepoText(relative));
            foreach (string token in forbidden)
            {
                Assert.False(
                    code.Contains(token, StringComparison.OrdinalIgnoreCase),
                    relative + " reaches a Cook or PAX surface: " + token);
            }
        }
    }

    [Fact]
    public void Bounded_activation_results_carry_only_their_token()
    {
        Assert.Equal(ServiceEnableActivationState.Unspecified, default(ServiceEnableActivationState));
        Assert.Equal(
            ServiceEnableActivationState.Unspecified,
            default(ServiceEnableActivationResult).State);
        Assert.False(default(ServiceEnableActivationResult).IsVerified);

        Assert.Equal(
            nameof(ServiceEnableActivationRequest),
            Request().ToString());
        Assert.DoesNotContain(ServiceSid, Request().ToString(), StringComparison.Ordinal);
        Assert.Equal(
            ServiceEnableActivationState.Verified.ToString(),
            new ServiceEnableActivationResult(
                ServiceEnableActivationState.Verified, false, false).ToString());
    }

    [Fact]
    public void Every_activation_failure_maps_to_a_bounded_non_success_outcome()
    {
        foreach (ServiceEnableActivationState state in Enum.GetValues<ServiceEnableActivationState>())
        {
            if (state == ServiceEnableActivationState.Verified)
            {
                continue;
            }

            ServiceEnableOperationOutcome outcome =
                ServiceEnableTransaction.ClassifyActivationFailure(state);

            Assert.NotEqual(ServiceEnableOperationOutcome.Completed, outcome);
            Assert.NotEqual(ServiceEnableOperationOutcome.Unspecified, outcome);
        }

        Assert.Equal(
            ServiceEnableOperationOutcome.MachineDataProtectionRefused,
            ServiceEnableTransaction.ClassifyActivationFailure(
                ServiceEnableActivationState.AnchorProtectionRefused));
        Assert.Equal(
            ServiceEnableOperationOutcome.StartabilityRefused,
            ServiceEnableTransaction.ClassifyActivationFailure(
                ServiceEnableActivationState.HeartbeatDidNotAdvance));
    }

    // =======================================================================
    // CYCLE 73 - THE SERVICE SID TYPE, PINNED TO THE DOCUMENTED WIN32 NUMBERS
    // =======================================================================
    //
    // WHY THESE EXIST. Every pre-existing SID-type assertion in this assembly
    // compares the PRODUCTION constant against itself - directly, or through a
    // test double whose own setter assigns that same production constant. That
    // whole family is true for ANY value the constant happens to hold, so by
    // construction it could never detect a wrong one. The tests below are the
    // missing independent pin: they state the Win32 numbers as LITERALS and
    // never read the production constant to decide what to expect.
    //
    // THE NUMBERS, from winsvc.h in the Windows SDK:
    //   SERVICE_SID_TYPE_NONE          0x00000000
    //   SERVICE_SID_TYPE_UNRESTRICTED  0x00000001
    //   SERVICE_SID_TYPE_RESTRICTED    ( 0x00000002 | SERVICE_SID_TYPE_UNRESTRICTED )
    // so 0x00000002 carries the restricted bit WITHOUT the unrestricted bit and
    // is not a defined service SID type at all.
    //
    // SCOPE LIMIT, stated so nobody over-reads it. These prove WHICH NUMBER this
    // product asks Windows for, and WHICH production predicates that number
    // governs. Nothing here touches the real Service Control Manager, and
    // nothing here proves a real service starts.

    /// <summary>
    /// Forwards everything to a real fake SCM except the SID type it REPORTS,
    /// which is a caller-supplied literal. It exists so a test can drive the
    /// production verification predicate with a number the production constant
    /// did not choose, which is the only way that predicate can be falsified.
    /// </summary>
    private sealed class ReportedSidTypeOverrideScm : IServiceControlManagerAdapter
    {
        private readonly FakeScm _inner;
        private readonly uint _reported;

        internal ReportedSidTypeOverrideScm(FakeScm inner, uint reported)
        {
            _inner = inner;
            _reported = reported;
        }

        public ServiceQueryState QueryConfiguration(
            string serviceName, out ServiceConfigurationSnapshot snapshot) =>
            _inner.QueryConfiguration(serviceName, out snapshot);

        public bool TryCreateService(ServiceConfigurationSnapshot desired) =>
            _inner.TryCreateService(desired);

        public bool TrySetUnrestrictedServiceSidType(string serviceName) =>
            _inner.TrySetUnrestrictedServiceSidType(serviceName);

        public bool TryQueryServiceSidType(string serviceName, out uint sidType)
        {
            bool answered = _inner.TryQueryServiceSidType(serviceName, out _);
            sidType = _reported;
            return answered;
        }

        public string? TryResolveServiceSid(string serviceName) =>
            _inner.TryResolveServiceSid(serviceName);

        public bool TryDeleteService(string serviceName) => _inner.TryDeleteService(serviceName);

        public ServiceStartAttemptResult TryStartService(string serviceName) =>
            _inner.TryStartService(serviceName);

        public ServiceQueryState QueryStatus(string serviceName, out ServiceStatusSnapshot snapshot) =>
            _inner.QueryStatus(serviceName, out snapshot);

        public bool TryStopService(string serviceName) => _inner.TryStopService(serviceName);
    }

    [Fact]
    public void The_service_sid_type_constants_hold_exactly_the_documented_win32_numbers()
    {
        Assert.Equal(0x00000000u, ServiceEnableRegistrationContract.ServiceSidTypeNone);
        Assert.Equal(0x00000001u, ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted);
        Assert.NotEqual(0x00000002u, ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted);
        Assert.NotEqual(0x00000003u, ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted);
    }

    /// <summary>
    /// CONSUMER 1 - the verification step inside <c>Apply</c>. The reported SID
    /// type is a literal the production constant did not produce, so this is a
    /// genuine falsification of that predicate rather than a tautology.
    /// </summary>
    [Theory]
    [InlineData(0x00000001u, true)]
    [InlineData(0x00000000u, false)]
    [InlineData(0x00000002u, false)]
    [InlineData(0x00000003u, false)]
    public void Apply_completes_only_when_the_reported_sid_type_is_the_literal_unrestricted_value(
        uint reportedSidType, bool expectedCompletion)
    {
        var rig = new Rig();
        CreateRuntimeHost();

        var transaction = new ServiceEnableTransaction(
            _metadataDirectory,
            new FakeProgramFiles(_programFiles),
            rig.FileSecurity,
            new ReportedSidTypeOverrideScm(rig.Scm, reportedSidType),
            new FakePayload(BuildCanonicalArchive()),
            new FakeSigning(),
            rig.Stage());

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        ServiceInstallationAnchorDocument anchor = WriteAnchor();
        ServiceIdentityBoundTransactionResult result =
            transaction.Apply(Context(anchor, anchorCreatedThisAttempt: true));

        if (expectedCompletion)
        {
            Assert.Equal(ServiceIdentityBoundTransactionState.Completed, result.State);
            Assert.Equal(ServiceEnableOperationOutcome.Completed, transaction.LastOutcome);
            Assert.Equal(ServiceEnableFailureCause.None, transaction.FailureCause);
        }
        else
        {
            Assert.NotEqual(ServiceIdentityBoundTransactionState.Completed, result.State);
            Assert.NotEqual(ServiceEnableOperationOutcome.Completed, transaction.LastOutcome);

            // The sticky cause proves the refusal came from SID-TYPE VERIFICATION
            // and not from some unrelated earlier or later gate.
            Assert.Equal(ServiceEnableFailureCause.VerificationRefused, transaction.FailureCause);
        }
    }

    /// <summary>
    /// CONSUMER 2 - the same constant inside <c>IsAlreadyExactlyComplete</c>.
    /// The discriminator is exact: the already-exact branch is the ONLY path
    /// through Apply that never reconfigures the SID type.
    /// </summary>
    [Theory]
    [InlineData(0x00000001u, true)]
    [InlineData(0x00000000u, false)]
    [InlineData(0x00000002u, false)]
    [InlineData(0x00000003u, false)]
    public void The_already_exact_predicate_accepts_only_the_literal_unrestricted_sid_type(
        uint seededSidType, bool expectedAlreadyExact)
    {
        var rig = new Rig();
        CreateRuntimeHost();
        ExtractExactPayload();
        ServiceInstallationAnchorDocument anchor = WriteAnchor();
        Directory.CreateDirectory(RuntimeDirectory);

        ServiceEnableFixedPaths paths = ResolvePaths();
        rig.Scm.Seed(
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final),
            seededSidType,
            ServiceRunState.Running);

        ServiceEnableTransaction transaction = CreateEnableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        transaction.Apply(Context(anchor, anchorCreatedThisAttempt: false));

        bool reconfiguredSidType =
            rig.Scm.Calls.Any(c => c.StartsWith("SetSidType:", StringComparison.Ordinal));
        Assert.Equal(expectedAlreadyExact, !reconfiguredSidType);

        // The already-exact branch also creates nothing, which is what makes the
        // discriminator above a branch identity rather than a side effect.
        if (expectedAlreadyExact)
        {
            Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));
            Assert.DoesNotContain(rig.Scm.Calls, c => c.StartsWith("Start:", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// ONE definition, and exactly the three approved use sites - the marshalled
    /// configuration value plus the two verification predicates, which are
    /// proven by NAME to live in <c>Apply</c> and <c>IsAlreadyExactlyComplete</c>.
    /// </summary>
    [Fact]
    public void Exactly_one_service_sid_type_constant_governs_both_verification_consumers()
    {
        const string AdapterRelative =
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceControlManagerAdapter.cs";
        const string TransactionRelative =
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableTransaction.cs";

        string adapter = StripCommentsAndLiterals(ReadRepoText(AdapterRelative));
        string transaction = StripCommentsAndLiterals(ReadRepoText(TransactionRelative));

        // ---- 1. THE EXACT SET OF DEFINITIONS, WITH THEIR LITERAL VALUES ----
        System.Text.RegularExpressions.MatchCollection definitions =
            System.Text.RegularExpressions.Regex.Matches(
                adapter,
                @"const\s+uint\s+ServiceSidType(?<suffix>[A-Za-z]+)\s*=\s*(?<value>0x[0-9A-Fa-f]{8})\s*;");

        Assert.Equal(
            new[] { "None=0x00000000", "Unrestricted=0x00000001" },
            definitions
                .Select(m => m.Groups["suffix"].Value + "=" + m.Groups["value"].Value)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray());

        // No RESTRICTED type is ever introduced, by name or by number.
        Assert.DoesNotContain("ServiceSidTypeRestricted", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceSidTypeRestricted", transaction, StringComparison.Ordinal);

        // ---- 2. THE SINGLE CONFIGURATION SITE ------------------------------
        Assert.Equal(
            1,
            System.Text.RegularExpressions.Regex.Matches(
                adapter,
                @"dwServiceSidType\s*=\s*ServiceEnableRegistrationContract\.ServiceSidTypeUnrestricted")
                .Count);

        // ---- 3. THE TWO VERIFICATION CONSUMERS, PROVEN BY ENCLOSING MEMBER --
        string[] lines = ReadRepoText(TransactionRelative).Replace("\r\n", "\n").Split('\n');
        var enclosing = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(
                    "ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted", StringComparison.Ordinal))
            {
                continue;
            }

            for (int j = i; j >= 0; j--)
            {
                System.Text.RegularExpressions.Match member =
                    System.Text.RegularExpressions.Regex.Match(
                        lines[j],
                        @"^    (?:internal|private|public)[^;=]*?\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(");
                if (member.Success)
                {
                    enclosing.Add(member.Groups["name"].Value);
                    break;
                }
            }
        }

        Assert.Equal(new[] { "Apply", "IsAlreadyExactlyComplete" }, enclosing.ToArray());
    }

    // =======================================================================
    // CYCLE 75 - THE STRICT VERIFIER, DRIVEN BY REAL DOCUMENTS ON DISK
    // =======================================================================

    /// <summary>Writes a real status document with an exact, closed property set.</summary>
    private string WriteStatusDocument(string state, int sessionId = 0, bool userInteractive = false)
    {
        Directory.CreateDirectory(RuntimeDirectory);
        string path = Path.Combine(RuntimeDirectory, ServiceRuntimeDocumentContract.StatusFileName);

        string json = "{\"schemaVersion\":" + ServiceRuntimeDocumentContract.ExpectedSchemaVersion
            + ",\"state\":\"" + state + "\""
            + ",\"timestampUtc\":\"2026-08-20T00:00:00Z\""
            + ",\"sessionId\":" + sessionId.ToString(CultureInfo.InvariantCulture)
            + ",\"userInteractive\":" + (userInteractive ? "true" : "false")
            + ",\"serviceVersion\":\"" + ServiceRuntimeDocumentContract.ExpectedServiceVersion + "\"}";

        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void The_strict_verifier_classifies_every_producer_lifecycle_state_on_its_own()
    {
        var verifier = new StrictServiceRuntimeDocumentVerifier();

        var expected = new Dictionary<string, ServiceRuntimeDocumentState>(StringComparer.Ordinal)
        {
            ["running"] = ServiceRuntimeDocumentState.Valid,
            ["starting"] = ServiceRuntimeDocumentState.Starting,
            ["stopped"] = ServiceRuntimeDocumentState.Stopped,
            ["failed"] = ServiceRuntimeDocumentState.Failed,

            // Anything outside the producer's closed set is fail-closed, never
            // assumed benign and never assumed ready.
            ["Running"] = ServiceRuntimeDocumentState.UnknownState,
            ["ready"] = ServiceRuntimeDocumentState.UnknownState,
            [""] = ServiceRuntimeDocumentState.UnknownState,
        };

        foreach (KeyValuePair<string, ServiceRuntimeDocumentState> row in expected)
        {
            string path = WriteStatusDocument(row.Key);
            Assert.Equal(row.Value, verifier.VerifyStatus(path).State);

            // A LIFECYCLE state is NEVER reported as an execution-context
            // failure. That conflation is the whole cycle-75 defect.
            if (row.Value != ServiceRuntimeDocumentState.Valid)
            {
                Assert.NotEqual(ServiceRuntimeDocumentState.WrongExecutionContext, verifier.VerifyStatus(path).State);
            }
        }
    }

    [Fact]
    public void The_producer_lifecycle_wire_values_are_pinned_against_the_service_source()
    {
        // The helper cannot reference the service's internal ServiceContract, so
        // the wire values are DUPLICATED here by necessity. This pins that
        // duplication against the producer's own source text, so a rename on
        // either side is caught rather than silently reclassified as
        // "unknown state" on the next attended run.
        string producer = ReadRepoText("src/PAXCookbook.Service/ServiceContract.cs");

        foreach (string literal in new[]
        {
            ServiceRuntimeDocumentContract.RunningStateValue,
            ServiceRuntimeDocumentContract.StartingStateValue,
            ServiceRuntimeDocumentContract.StoppedStateValue,
            ServiceRuntimeDocumentContract.FailedStateValue,
        })
        {
            Assert.Contains("\"" + literal + "\"", producer, StringComparison.Ordinal);
        }

        // The producer really does write "starting" BEFORE "running" - which is
        // exactly why an immediate one-shot read could legitimately observe a
        // service that had not finished coming up. That is a CALL-GRAPH fact,
        // not a text-order fact: the starting write lives inside
        // StartInsideExistingRuntimeRoot, and ExecuteAsync calls that method
        // before it writes the running status.
        string worker = ReadRepoText("src/PAXCookbook.Service/StartupProbeWorker.cs");

        int callSite = worker.IndexOf("StartInsideExistingRuntimeRoot();", StringComparison.Ordinal);
        int running = worker.IndexOf("WriteStatus(ServiceStateCode.Running)", StringComparison.Ordinal);
        int declaration = worker.IndexOf(
            "ServiceRuntimeRootState StartInsideExistingRuntimeRoot()", StringComparison.Ordinal);
        int starting = worker.IndexOf("WriteStatus(ServiceStateCode.Starting)", StringComparison.Ordinal);

        Assert.True(callSite >= 0, "the producer no longer calls StartInsideExistingRuntimeRoot");
        Assert.True(running >= 0, "the producer no longer writes a running status");
        Assert.True(declaration >= 0, "StartInsideExistingRuntimeRoot no longer exists");
        Assert.True(starting >= 0, "the producer no longer writes a starting status");

        Assert.True(
            callSite < running,
            "the runtime-root step no longer runs before the running status is written");
        Assert.True(
            starting > declaration,
            "the starting status is no longer written inside StartInsideExistingRuntimeRoot");
    }

    [Fact]
    public void Execution_context_outranks_lifecycle_state_and_structure_outranks_both()
    {
        var verifier = new StrictServiceRuntimeDocumentVerifier();

        // A HEALTHY-LOOKING lifecycle state in the WRONG session is still an
        // execution-context refusal. The security check did not weaken.
        Assert.Equal(
            ServiceRuntimeDocumentState.WrongExecutionContext,
            verifier.VerifyStatus(WriteStatusDocument("running", sessionId: 1)).State);

        // An INTERACTIVE host is the same verdict.
        Assert.Equal(
            ServiceRuntimeDocumentState.WrongExecutionContext,
            verifier.VerifyStatus(WriteStatusDocument("running", userInteractive: true)).State);

        // A TRANSIENT lifecycle state in the WRONG session reports the wrong
        // context, NOT Starting - so a wrong-session document can never be
        // retried as if it were merely coming up.
        Assert.Equal(
            ServiceRuntimeDocumentState.WrongExecutionContext,
            verifier.VerifyStatus(WriteStatusDocument("starting", sessionId: 1)).State);

        // STRUCTURE outranks execution context: a non-string state is a SHAPE
        // failure even when the session is also wrong.
        Directory.CreateDirectory(RuntimeDirectory);
        string malformed = Path.Combine(RuntimeDirectory, ServiceRuntimeDocumentContract.StatusFileName);
        File.WriteAllText(
            malformed,
            "{\"schemaVersion\":1,\"state\":7,\"timestampUtc\":\"2026-08-20T00:00:00Z\","
                + "\"sessionId\":1,\"userInteractive\":false,\"serviceVersion\":\"1.0.0\"}",
            new UTF8Encoding(false));
        Assert.Equal(ServiceRuntimeDocumentState.Malformed, verifier.VerifyStatus(malformed).State);
    }

    [Fact]
    public void The_heartbeat_document_has_no_lifecycle_state_and_keeps_its_existing_semantics()
    {
        var verifier = new StrictServiceRuntimeDocumentVerifier();
        Directory.CreateDirectory(RuntimeDirectory);
        string path = Path.Combine(RuntimeDirectory, ServiceRuntimeDocumentContract.HeartbeatFileName);

        // The heartbeat's closed property set carries NO state property, so no
        // lifecycle verdict can ever come from it.
        Assert.DoesNotContain("state", ServiceRuntimeDocumentContract.HeartbeatProperties);

        File.WriteAllText(
            path,
            "{\"schemaVersion\":1,\"timestampUtc\":\"2026-08-20T00:00:00Z\",\"sessionId\":0,"
                + "\"userInteractive\":false,\"serviceVersion\":\"1.0.0\"}",
            new UTF8Encoding(false));
        Assert.Equal(ServiceRuntimeDocumentState.Valid, verifier.VerifyHeartbeat(path).State);

        File.WriteAllText(
            path,
            "{\"schemaVersion\":1,\"timestampUtc\":\"2026-08-20T00:00:00Z\",\"sessionId\":3,"
                + "\"userInteractive\":false,\"serviceVersion\":\"1.0.0\"}",
            new UTF8Encoding(false));
        Assert.Equal(ServiceRuntimeDocumentState.WrongExecutionContext, verifier.VerifyHeartbeat(path).State);

        File.Delete(path);
        Assert.Equal(ServiceRuntimeDocumentState.Missing, verifier.VerifyHeartbeat(path).State);
    }

    // =======================================================================
    // SHARED SOURCE INSTRUMENTS
    // =======================================================================

    private static string ReadRepoText(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PAXCookbook.sln")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);

        string full = Path.Combine(directory!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), "missing source: " + relative);
        return File.ReadAllText(full);
    }

    /// <summary>
    /// Removes comments, string literals and character literals. The file
    /// headers legitimately NAME every forbidden mechanism in prose, so a scan
    /// that did not strip them would be guaranteed to fail rather than measured.
    /// </summary>
    private static string StripCommentsAndLiterals(string source)
    {
        var output = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }
                i = Math.Min(i + 2, source.Length);
                continue;
            }

            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (c == '"')
            {
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }
                i++;
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < source.Length && source[i] != '\'')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }
                i++;
                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    // =======================================================================
    // CYCLE 74 - THE BOUNDED StartServiceW SUB-CLASSIFICATION
    // =======================================================================
    //
    // WHY THIS SECTION EXISTS. The attended cycle-73 run reached exactly
    //     service-enable outcome=startability_refused disposition=compensated
    // which is the LAST and WIDEST category in the pipeline. Eleven genuinely
    // different machine states collapse onto it, and the adapter's start call
    // returned a bare bool, so the Win32 reason was read and DISCARDED - the
    // same shape that made registration_refused useless in cycle 67.
    //
    // WHAT IS PROVEN HERE, and what is NOT. These tests drive a NARROW PURE
    // INTERPRETER and the real activation stage against injected doubles. NOT
    // ONE of them contacts the real Service Control Manager, starts, stops,
    // creates or deletes a service, elevates, or launches any process. They
    // prove the CLASSIFICATION is exact and total; they prove NOTHING about
    // whether the real service starts.

    /// <summary>
    /// The documented StartServiceW codes that receive their OWN category,
    /// paired with that category. Values are the winerror.h numbers.
    ///
    /// ENUMERATED IN TEST BODIES rather than through MemberData, because the
    /// bounded vocabularies are INTERNAL and an internal type cannot appear in a
    /// public xUnit signature. Making them public to satisfy a test framework
    /// would widen a deliberately narrow surface.
    /// </summary>
    private static (int NativeError, ServiceStartAttemptResult Expected)[] GroundedStartCodes() => new[]
    {
        (ServiceStartAttemptResultMap.ErrorServiceAlreadyRunning, ServiceStartAttemptResult.AlreadyRunning),
        (ServiceStartAttemptResultMap.ErrorServiceLogonFailed, ServiceStartAttemptResult.LogonRefused),
        (ServiceStartAttemptResultMap.ErrorAccessDenied, ServiceStartAttemptResult.AccessRefused),
        (ServiceStartAttemptResultMap.ErrorPathNotFound, ServiceStartAttemptResult.BinaryUnavailable),
        (ServiceStartAttemptResultMap.ErrorServiceDependencyFail, ServiceStartAttemptResult.DependencyRefused),
        (ServiceStartAttemptResultMap.ErrorServiceDependencyDeleted, ServiceStartAttemptResult.DependencyRefused),
        (ServiceStartAttemptResultMap.ErrorServiceRequestTimeout, ServiceStartAttemptResult.RequestTimeout),
    };

    /// <summary>
    /// The documented StartServiceW codes that were deliberately NOT given a
    /// category. They must reach the closed fallback through a NAMED arm.
    /// </summary>
    private static int[] DocumentedButUnnamedStartCodes() => new[]
    {
        ServiceStartAttemptResultMap.ErrorInvalidHandle,
        ServiceStartAttemptResultMap.ErrorServiceDatabaseLocked,
        ServiceStartAttemptResultMap.ErrorServiceDisabled,
        ServiceStartAttemptResultMap.ErrorServiceMarkedForDelete,
        ServiceStartAttemptResultMap.ErrorServiceNoThread,
    };

    [Fact]
    public void The_grounded_win32_start_constants_hold_their_documented_numbers()
    {
        // These are the winerror.h values. A test that compared the constant to
        // itself would pin nothing, so every number is a literal here.
        Assert.Equal(3, ServiceStartAttemptResultMap.ErrorPathNotFound);
        Assert.Equal(5, ServiceStartAttemptResultMap.ErrorAccessDenied);
        Assert.Equal(6, ServiceStartAttemptResultMap.ErrorInvalidHandle);
        Assert.Equal(1053, ServiceStartAttemptResultMap.ErrorServiceRequestTimeout);
        Assert.Equal(1054, ServiceStartAttemptResultMap.ErrorServiceNoThread);
        Assert.Equal(1055, ServiceStartAttemptResultMap.ErrorServiceDatabaseLocked);
        Assert.Equal(1056, ServiceStartAttemptResultMap.ErrorServiceAlreadyRunning);
        Assert.Equal(1058, ServiceStartAttemptResultMap.ErrorServiceDisabled);
        Assert.Equal(1068, ServiceStartAttemptResultMap.ErrorServiceDependencyFail);
        Assert.Equal(1069, ServiceStartAttemptResultMap.ErrorServiceLogonFailed);
        Assert.Equal(1072, ServiceStartAttemptResultMap.ErrorServiceMarkedForDelete);
        Assert.Equal(1075, ServiceStartAttemptResultMap.ErrorServiceDependencyDeleted);
    }

    [Fact]
    public void The_native_start_interpreter_names_every_grounded_code()
    {
        foreach ((int nativeError, ServiceStartAttemptResult expected) in GroundedStartCodes())
        {
            Assert.Equal(expected, ServiceStartAttemptResultMap.FromNativeError(nativeError));
        }

        // DIRECTIONALITY. A map that answered the same thing everywhere would
        // satisfy a one-sided suite, so the grounded set must be genuinely plural.
        Assert.Equal(7, GroundedStartCodes().Length);
        Assert.Equal(6, GroundedStartCodes().Select(g => g.Expected).Distinct().Count());
    }

    [Fact]
    public void A_documented_but_unnamed_start_code_reaches_the_closed_fallback()
    {
        foreach (int nativeError in DocumentedButUnnamedStartCodes())
        {
            Assert.Equal(
                ServiceStartAttemptResult.Refused,
                ServiceStartAttemptResultMap.FromNativeError(nativeError));
        }

        Assert.Equal(5, DocumentedButUnnamedStartCodes().Length);

        // Every documented code is accounted for exactly once, in exactly one of
        // the two lists. Twelve codes are documented for this call.
        int[] all = GroundedStartCodes().Select(g => g.NativeError)
            .Concat(DocumentedButUnnamedStartCodes())
            .ToArray();
        Assert.Equal(12, all.Length);
        Assert.Equal(12, all.Distinct().Count());
    }

    [Fact]
    public void The_native_start_interpreter_is_total_and_fails_closed_over_arbitrary_values()
    {
        // The documentation warns that OTHER codes can be set by the registry
        // functions the SCM calls, so the documented set is NOT exhaustive and an
        // unknown value must never be guessed at.
        var grounded = new HashSet<int>();
        foreach ((int nativeError, ServiceStartAttemptResult _) in GroundedStartCodes())
        {
            grounded.Add(nativeError);
        }

        int swept = 0;
        for (int code = -50; code <= 1200; code++)
        {
            ServiceStartAttemptResult result = ServiceStartAttemptResultMap.FromNativeError(code);
            Assert.True(Enum.IsDefined(result));
            Assert.NotEqual(ServiceStartAttemptResult.Unspecified, result);

            if (grounded.Contains(code))
            {
                continue;
            }

            swept++;
            Assert.Equal(ServiceStartAttemptResult.Refused, result);
        }

        Assert.True(swept > 1200, "the fail-closed sweep examined too few values to be meaningful");

        foreach (int hostile in new[] { int.MinValue, int.MaxValue, -1, 0, 999999, 1060, 1062, 1073 })
        {
            Assert.Equal(
                ServiceStartAttemptResult.Refused,
                ServiceStartAttemptResultMap.FromNativeError(hostile));
        }
    }

    [Fact]
    public void A_start_request_timeout_is_never_folded_into_a_running_timeout()
    {
        // ERROR_SERVICE_REQUEST_TIMEOUT is a SYNCHRONOUS refusal of the start
        // call. StartServiceW returns as soon as the dispatcher reports the
        // ServiceMain thread was created and does NOT wait for the first status
        // update, so "the call refused" and "the service never ran" are
        // different stages and must never share a category.
        Assert.Equal(
            ServiceStartAttemptResult.RequestTimeout,
            ServiceStartAttemptResultMap.FromNativeError(
                ServiceStartAttemptResultMap.ErrorServiceRequestTimeout));

        Assert.NotEqual(
            ServiceEnableActivationState.NeverReachedRunning,
            ServiceEnableActivationState.StartRequestTimeout);

        Assert.NotEqual(
            ServiceEnableFailureCause.ServiceRunningTimeout,
            ServiceEnableFailureCause.ServiceStartRequestTimeout);

        Assert.NotEqual(
            ServiceEnableFailureContract.CauseToken(ServiceEnableFailureCause.ServiceRunningTimeout),
            ServiceEnableFailureContract.CauseToken(ServiceEnableFailureCause.ServiceStartRequestTimeout));

        Assert.Equal(
            ServiceEnableFailureCause.ServiceStartRequestTimeout,
            ServiceEnableTransaction.CauseForActivationFailure(
                ServiceEnableActivationState.StartRequestTimeout));
        Assert.Equal(
            ServiceEnableFailureCause.ServiceRunningTimeout,
            ServiceEnableTransaction.CauseForActivationFailure(
                ServiceEnableActivationState.NeverReachedRunning));
    }

    [Fact]
    public void Only_started_and_already_running_count_as_started()
    {
        foreach (ServiceStartAttemptResult result in Enum.GetValues<ServiceStartAttemptResult>())
        {
            bool expected =
                result == ServiceStartAttemptResult.Started
                || result == ServiceStartAttemptResult.AlreadyRunning;

            Assert.Equal(expected, ServiceStartAttemptResultMap.IsStarted(result));
        }

        Assert.False(ServiceStartAttemptResultMap.IsStarted(default));
        Assert.False(ServiceStartAttemptResultMap.IsStarted((ServiceStartAttemptResult)9999));
        Assert.Equal(ServiceStartAttemptResult.Unspecified, default(ServiceStartAttemptResult));
    }

    [Fact]
    public void No_bounded_start_result_value_ever_carries_a_native_win32_code()
    {
        int[] nativeCodes =
        {
            ServiceStartAttemptResultMap.ErrorPathNotFound,
            ServiceStartAttemptResultMap.ErrorAccessDenied,
            ServiceStartAttemptResultMap.ErrorInvalidHandle,
            ServiceStartAttemptResultMap.ErrorServiceRequestTimeout,
            ServiceStartAttemptResultMap.ErrorServiceNoThread,
            ServiceStartAttemptResultMap.ErrorServiceDatabaseLocked,
            ServiceStartAttemptResultMap.ErrorServiceAlreadyRunning,
            ServiceStartAttemptResultMap.ErrorServiceDisabled,
            ServiceStartAttemptResultMap.ErrorServiceDependencyFail,
            ServiceStartAttemptResultMap.ErrorServiceLogonFailed,
            ServiceStartAttemptResultMap.ErrorServiceMarkedForDelete,
            ServiceStartAttemptResultMap.ErrorServiceDependencyDeleted,
        };

        // THE VOCABULARY IS A DENSE, SMALL ORDINAL SET. It structurally cannot
        // carry the Win32 code space at all.
        //
        // NOTE ON AN EARLIER, WRONG VERSION OF THIS TEST: it asserted that no
        // bounded value equals any documented Win32 number. That is UNATTAINABLE
        // and was never the real property - ERROR_PATH_NOT_FOUND is 3, and a
        // category vocabulary that starts at 0 will inevitably reuse small
        // integers. The property that actually matters is that the interpreter
        // never PASSES THE NATIVE NUMBER THROUGH, which is what is asserted here.
        int[] ordinals = Enum.GetValues<ServiceStartAttemptResult>()
            .Cast<int>().OrderBy(v => v).ToArray();
        Assert.Equal(9, ordinals.Length);
        Assert.Equal(Enumerable.Range(0, ordinals.Length).ToArray(), ordinals);

        foreach (ServiceStartAttemptResult result in Enum.GetValues<ServiceStartAttemptResult>())
        {
            // The rendered form is the CATEGORY NAME, never a number.
            Assert.Equal(result.ToString(), ((object)result).ToString());
            Assert.Matches("^[A-Za-z]+$", result.ToString());
        }

        // THE INTERPRETER NEVER PASSES THE NATIVE VALUE THROUGH.
        foreach (int native in nativeCodes)
        {
            Assert.NotEqual(native, (int)ServiceStartAttemptResultMap.FromNativeError(native));
        }
    }

    /// <summary>
    /// Every documented start refusal, paired with the activation state it must
    /// reach through the REAL production stage.
    /// </summary>
    private static (ServiceStartAttemptResult Attempt, ServiceEnableActivationState Expected)[]
        StartRefusalStates() => new[]
    {
        (ServiceStartAttemptResult.LogonRefused, ServiceEnableActivationState.StartLogonRefused),
        (ServiceStartAttemptResult.AccessRefused, ServiceEnableActivationState.StartAccessRefused),
        (ServiceStartAttemptResult.BinaryUnavailable, ServiceEnableActivationState.StartBinaryUnavailable),
        (ServiceStartAttemptResult.DependencyRefused, ServiceEnableActivationState.StartDependencyRefused),
        (ServiceStartAttemptResult.RequestTimeout, ServiceEnableActivationState.StartRequestTimeout),
        (ServiceStartAttemptResult.Refused, ServiceEnableActivationState.StartRefused),
        (ServiceStartAttemptResult.Unspecified, ServiceEnableActivationState.StartRefused),
    };

    [Fact]
    public void Every_documented_start_refusal_reaches_its_own_activation_state()
    {
        WriteAnchor();

        foreach ((ServiceStartAttemptResult attempt, ServiceEnableActivationState expected)
                 in StartRefusalStates())
        {
            var rig = new Rig();
            rig.Scm.FailStart = true;
            rig.Scm.FailedStartResult = attempt;

            ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(Request());

            Assert.Equal(expected, result.State);
            Assert.False(result.IsVerified);
            Assert.False(result.ServiceStartedThisAttempt);

            // A refused start NEVER reaches the startability proof, exactly as
            // before this cycle.
            Assert.Equal(0, rig.Process.Calls);
            Assert.Empty(rig.Documents.Calls);
            Assert.Equal(0, rig.Children.Calls);
        }

        // The five NAMED sub-classifications really are distinct states.
        Assert.Equal(6, StartRefusalStates().Select(s => s.Expected).Distinct().Count());
    }

    [Fact]
    public void An_already_running_start_result_is_not_a_refusal()
    {
        // ERROR_SERVICE_ALREADY_RUNNING satisfies the caller's intent, so it
        // proceeds to the proof exactly as a successful start does. The prior
        // build treated it as success too; that behaviour is preserved.
        Rig rig = SeededRig(ServiceRunState.Running);
        WriteAnchor();
        rig.Scm.FailStart = true;
        rig.Scm.FailedStartResult = ServiceStartAttemptResult.AlreadyRunning;

        ServiceEnableActivationResult result = rig.Stage().ActivateAndVerify(Request());

        Assert.Equal(ServiceEnableActivationState.Verified, result.State);
        Assert.True(result.ServiceStartedThisAttempt);
        Assert.Equal(1, rig.Process.Calls);
    }

    [Fact]
    public void The_activation_failure_cause_map_is_total_and_names_every_startability_stage()
    {
        var byState = new Dictionary<ServiceEnableActivationState, ServiceEnableFailureCause>();

        foreach (ServiceEnableActivationState state in Enum.GetValues<ServiceEnableActivationState>())
        {
            ServiceEnableFailureCause cause = ServiceEnableTransaction.CauseForActivationFailure(state);

            // NOTHING may read as success, and nothing may read as the retained
            // wide category.
            Assert.NotEqual(ServiceEnableFailureCause.None, cause);
            Assert.NotEqual(ServiceEnableFailureCause.StartabilityRefused, cause);
            byState[state] = cause;
        }

        // The three machine-data states KEEP their existing cause. This cycle
        // splits startability, not the profile arm.
        foreach (ServiceEnableActivationState machineData in new[]
                 {
                     ServiceEnableActivationState.MetadataProtectionRefused,
                     ServiceEnableActivationState.AnchorProtectionRefused,
                     ServiceEnableActivationState.RuntimeProtectionRefused,
                 })
        {
            Assert.Equal(ServiceEnableFailureCause.MachineDataProtectionRefused, byState[machineData]);
        }

        // The ELEVEN startability states each get their OWN cause, and no two of
        // them can be confused on the wire.
        var startability = new Dictionary<ServiceEnableActivationState, ServiceEnableFailureCause>
        {
            [ServiceEnableActivationState.StartRefused] = ServiceEnableFailureCause.ServiceStartRefused,
            [ServiceEnableActivationState.NeverReachedRunning] = ServiceEnableFailureCause.ServiceRunningTimeout,
            [ServiceEnableActivationState.ProcessIdentityRefused] =
                ServiceEnableFailureCause.ServiceProcessIdentityRefused,
            [ServiceEnableActivationState.StatusDocumentRefused] =
                ServiceEnableFailureCause.ServiceStatusDocumentRefused,
            [ServiceEnableActivationState.HeartbeatDidNotAdvance] = ServiceEnableFailureCause.ServiceHeartbeatRefused,
            [ServiceEnableActivationState.ForbiddenChildProcess] =
                ServiceEnableFailureCause.ServiceForbiddenChildRefused,
            [ServiceEnableActivationState.StartLogonRefused] = ServiceEnableFailureCause.ServiceStartLogonRefused,
            [ServiceEnableActivationState.StartAccessRefused] = ServiceEnableFailureCause.ServiceStartAccessRefused,
            [ServiceEnableActivationState.StartBinaryUnavailable] =
                ServiceEnableFailureCause.ServiceStartBinaryUnavailable,
            [ServiceEnableActivationState.StartDependencyRefused] =
                ServiceEnableFailureCause.ServiceStartDependencyRefused,
            [ServiceEnableActivationState.StartRequestTimeout] =
                ServiceEnableFailureCause.ServiceStartRequestTimeout,

            // CYCLE 75. The EIGHT bounded status-readiness stages.
            [ServiceEnableActivationState.StatusMissingTimeout] =
                ServiceEnableFailureCause.ServiceStatusMissingTimeout,
            [ServiceEnableActivationState.StatusStartingTimeout] =
                ServiceEnableFailureCause.ServiceStatusStartingTimeout,
            [ServiceEnableActivationState.StatusUnreadable] =
                ServiceEnableFailureCause.ServiceStatusUnreadable,
            [ServiceEnableActivationState.StatusMalformed] =
                ServiceEnableFailureCause.ServiceStatusMalformed,
            [ServiceEnableActivationState.StatusWrongContext] =
                ServiceEnableFailureCause.ServiceStatusWrongContext,
            [ServiceEnableActivationState.StatusStopped] =
                ServiceEnableFailureCause.ServiceStatusStopped,
            [ServiceEnableActivationState.StatusFailed] =
                ServiceEnableFailureCause.ServiceStatusFailed,
            [ServiceEnableActivationState.StatusUnknownState] =
                ServiceEnableFailureCause.ServiceStatusUnknownState,
        };

        Assert.Equal(19, startability.Count);
        Assert.Equal(19, startability.Values.Distinct().Count());

        foreach (KeyValuePair<ServiceEnableActivationState, ServiceEnableFailureCause> row in startability)
        {
            Assert.Equal(row.Value, byState[row.Key]);
            Assert.NotEqual("unavailable", ServiceEnableFailureContract.CauseToken(row.Value));
            Assert.NotEqual(
                "startability_refused", ServiceEnableFailureContract.CauseToken(row.Value));
        }

        // CYCLE 75. The retained wide status entry is deliberately STILL in the
        // map so an already-emitted code decodes; what changed is that the
        // COORDINATOR can no longer produce the state that reaches it. That is a
        // STRUCTURAL fact and is proven as one.
        Assert.Equal(
            ServiceEnableFailureCause.ServiceStatusDocumentRefused,
            byState[ServiceEnableActivationState.StatusDocumentRefused]);

        string coordinator = StripCommentsAndLiterals(
            ReadRepoText("src/PAXCookbook.ServiceAdminHelper/Enable/ServiceStartabilityCoordinator.cs"));
        Assert.DoesNotContain(
            "return ServiceStartabilityState.StatusDocumentRefused", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ServiceStartabilityState.StatusDocumentRefused;", coordinator, StringComparison.Ordinal);

        // Verified and Unspecified are not failures; they fall to the closed
        // default rather than being given an invented category.
        Assert.Equal(ServiceEnableFailureCause.Unavailable, byState[ServiceEnableActivationState.Verified]);
        Assert.Equal(ServiceEnableFailureCause.Unavailable, byState[ServiceEnableActivationState.Unspecified]);
        Assert.Equal(ServiceEnableFailureCause.Unavailable, byState[ServiceEnableActivationState.Unavailable]);

        // An UNDEFINED cast is fail-closed, so a future state added without a
        // mapping is loudly generic rather than silently wrong.
        Assert.Equal(
            ServiceEnableFailureCause.Unavailable,
            ServiceEnableTransaction.CauseForActivationFailure((ServiceEnableActivationState)9999));
    }

    [Fact]
    public void The_legacy_activation_outcome_map_still_collapses_every_start_stage()
    {
        // LastOutcome is the value roughly 133 existing tests assert, and the
        // disable transaction shares its vocabulary. Only the CAUSE axis - the
        // one that actually crosses the process boundary - becomes finer.
        foreach (ServiceEnableActivationState state in new[]
                 {
                     ServiceEnableActivationState.StartRefused,
                     ServiceEnableActivationState.NeverReachedRunning,
                     ServiceEnableActivationState.ProcessIdentityRefused,
                     ServiceEnableActivationState.StatusDocumentRefused,
                     ServiceEnableActivationState.HeartbeatDidNotAdvance,
                     ServiceEnableActivationState.ForbiddenChildProcess,
                     ServiceEnableActivationState.StartLogonRefused,
                     ServiceEnableActivationState.StartAccessRefused,
                     ServiceEnableActivationState.StartBinaryUnavailable,
                     ServiceEnableActivationState.StartDependencyRefused,
                     ServiceEnableActivationState.StartRequestTimeout,
                     ServiceEnableActivationState.StatusMissingTimeout,
                     ServiceEnableActivationState.StatusStartingTimeout,
                     ServiceEnableActivationState.StatusUnreadable,
                     ServiceEnableActivationState.StatusMalformed,
                     ServiceEnableActivationState.StatusWrongContext,
                     ServiceEnableActivationState.StatusStopped,
                     ServiceEnableActivationState.StatusFailed,
                     ServiceEnableActivationState.StatusUnknownState,
                 })
        {
            Assert.Equal(
                ServiceEnableOperationOutcome.StartabilityRefused,
                ServiceEnableTransaction.ClassifyActivationFailure(state));
        }
    }

    [Fact]
    public void Compensation_cannot_overwrite_a_finer_startability_cause()
    {
        // ONE representative sub-classification is driven end to end through the
        // REAL transaction, including its real compensation. The remaining
        // sub-classifications are proven at the stage and map layers above; a
        // full transaction per case would re-extract into the fixed Program
        // Files tree seven times and prove nothing extra about stickiness.
        var rig = new Rig();
        CreateRuntimeHost();
        rig.Scm.FailStart = true;
        rig.Scm.FailedStartResult = ServiceStartAttemptResult.LogonRefused;

        ServiceEnableTransaction transaction = CreateEnableTransaction(rig);
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        ServiceInstallationAnchorDocument anchor = WriteAnchor();
        ServiceIdentityBoundTransactionResult result =
            transaction.Apply(Context(anchor, anchorCreatedThisAttempt: true));

        // The DISPOSITION says compensated; the CAUSE still says exactly which
        // startability stage refused. That is the whole point of the two axes.
        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        Assert.Equal(ServiceEnableOperationOutcome.Compensated, transaction.LastOutcome);
        Assert.Equal(ServiceEnableFailureDisposition.Compensated, transaction.FailureDisposition);

        Assert.Equal(ServiceEnableFailureCause.ServiceStartLogonRefused, transaction.FailureCause);
        Assert.NotEqual(ServiceEnableFailureCause.StartabilityRefused, transaction.FailureCause);
        Assert.NotEqual(ServiceEnableFailureCause.Unavailable, transaction.FailureCause);

        // The bounded line an operator would actually read. It is a REFUSAL
        // pair, never a success pair.
        Assert.Equal(
            "service-enable outcome=service_start_logon_refused disposition=compensated",
            ServiceEnableFailureContract.FormatDiagnostic(
                transaction.FailureCause, transaction.FailureDisposition));

        // The service this attempt created really was removed.
        Assert.Contains(rig.Scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.False(rig.Scm.Exists);
    }

    [Fact]
    public void The_one_activation_failure_site_records_the_finer_cause_alongside_the_legacy_outcome()
    {
        // STRUCTURAL, NOT BEHAVIOURAL. It proves the single production call site
        // uses the TWO-ARGUMENT Fail overload, because the one-argument overload
        // routes through CauseFor and would silently re-collapse every
        // startability stage back onto startability_refused.
        string relative = "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableTransaction.cs";
        string flat = System.Text.RegularExpressions.Regex.Replace(
            StripCommentsAndLiterals(ReadRepoText(relative)), @"\s+", " ");

        Assert.Equal(
            1,
            System.Text.RegularExpressions.Regex.Matches(
                flat, @"Fail\( *ClassifyActivationFailure\(").Count);

        Assert.Equal(
            1,
            System.Text.RegularExpressions.Regex.Matches(
                flat,
                @"Fail\( *ClassifyActivationFailure\( *activation\.State *\), *"
                    + @"CauseForActivationFailure\( *activation\.State *\) *\)").Count);

        // NO production site in the Enable surface names the retained wide
        // cause. It survives only so an ALREADY-EMITTED code still decodes.
        foreach (string file in new[]
                 {
                     relative,
                     "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableActivationStage.cs",
                     "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableElevatedDispatch.cs",
                     "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceStartabilityCoordinator.cs",
                 })
        {
            Assert.DoesNotContain(
                "ServiceEnableFailureCause.StartabilityRefused",
                StripCommentsAndLiterals(ReadRepoText(file)),
                StringComparison.Ordinal);
        }

        // The retained legacy map itself is UNCHANGED, so an old conflated
        // outcome still resolves for backward compatibility.
        Assert.Equal(
            ServiceEnableFailureCause.StartabilityRefused,
            ServiceEnableFailureContract.CauseFor(ServiceEnableOperationOutcome.StartabilityRefused));
    }

    // =======================================================================
    // CYCLE 75 - THE EXHAUSTIVE HELPER-SIDE CONTRACT LOCK
    // =======================================================================
    //
    // WHY THESE ARE SEPARATE. Every map in this stack has a CLOSED FALLBACK
    // default, which is correct behaviour and is exactly why a future member
    // appended without a mapping arm cannot fail any of the tests above: it
    // simply lands on the fallback and looks deliberate. Only a SET-EQUALITY
    // assertion between the declared enum and a hand-written table can catch it,
    // and that is what each of these tests is.

    [Fact]
    public void Every_declared_activation_state_is_explicitly_accounted_for()
    {
        // HAND-WRITTEN. Each declared state is placed in exactly one bucket:
        // a real failure with its own cause, or one of the three states that are
        // deliberately not failures.
        var failureStates = new HashSet<ServiceEnableActivationState>
        {
            ServiceEnableActivationState.MetadataProtectionRefused,
            ServiceEnableActivationState.AnchorProtectionRefused,
            ServiceEnableActivationState.RuntimeProtectionRefused,
            ServiceEnableActivationState.StartRefused,
            ServiceEnableActivationState.StartLogonRefused,
            ServiceEnableActivationState.StartAccessRefused,
            ServiceEnableActivationState.StartBinaryUnavailable,
            ServiceEnableActivationState.StartDependencyRefused,
            ServiceEnableActivationState.StartRequestTimeout,
            ServiceEnableActivationState.NeverReachedRunning,
            ServiceEnableActivationState.ProcessIdentityRefused,
            ServiceEnableActivationState.StatusDocumentRefused,
            ServiceEnableActivationState.StatusMissingTimeout,
            ServiceEnableActivationState.StatusStartingTimeout,
            ServiceEnableActivationState.StatusUnreadable,
            ServiceEnableActivationState.StatusMalformed,
            ServiceEnableActivationState.StatusWrongContext,
            ServiceEnableActivationState.StatusStopped,
            ServiceEnableActivationState.StatusFailed,
            ServiceEnableActivationState.StatusUnknownState,
            ServiceEnableActivationState.HeartbeatDidNotAdvance,
            ServiceEnableActivationState.ForbiddenChildProcess,
        };

        var nonFailureStates = new HashSet<ServiceEnableActivationState>
        {
            ServiceEnableActivationState.Unspecified,
            ServiceEnableActivationState.Verified,
            ServiceEnableActivationState.Unavailable,
        };

        // The two buckets are DISJOINT and together are EXACTLY the enum.
        Assert.Empty(failureStates.Intersect(nonFailureStates));

        var declared = new HashSet<ServiceEnableActivationState>(
            Enum.GetValues<ServiceEnableActivationState>());
        var accounted = new HashSet<ServiceEnableActivationState>(failureStates);
        accounted.UnionWith(nonFailureStates);

        Assert.Empty(declared.Except(accounted));
        Assert.Empty(accounted.Except(declared));
        Assert.Equal(declared.Count, accounted.Count);

        // Every failure state carries a cause that is NOT the fail-closed
        // fallback, so nothing in the failure bucket is silently generic.
        foreach (ServiceEnableActivationState state in failureStates)
        {
            ServiceEnableFailureCause cause = ServiceEnableTransaction.CauseForActivationFailure(state);
            Assert.NotEqual(ServiceEnableFailureCause.Unavailable, cause);
            Assert.NotEqual(ServiceEnableFailureCause.StartabilityRefused, cause);
            Assert.NotEqual(ServiceEnableFailureCause.None, cause);
        }

        // Every non-failure state reaches the fallback, and so does an
        // undefined cast.
        foreach (ServiceEnableActivationState state in nonFailureStates)
        {
            Assert.Equal(
                ServiceEnableFailureCause.Unavailable,
                ServiceEnableTransaction.CauseForActivationFailure(state));
        }
    }

    [Fact]
    public void Every_declared_startability_state_maps_to_its_own_activation_state()
    {
        // HAND-WRITTEN, then closed against the enum. MapProof has an Unavailable
        // fallback, so an appended startability state would otherwise pass.
        var expected = new Dictionary<ServiceStartabilityState, ServiceEnableActivationState>
        {
            [ServiceStartabilityState.Verified] = ServiceEnableActivationState.Verified,
            [ServiceStartabilityState.StartRefused] = ServiceEnableActivationState.StartRefused,
            [ServiceStartabilityState.NeverReachedRunning] = ServiceEnableActivationState.NeverReachedRunning,
            [ServiceStartabilityState.ProcessIdentityRefused] =
                ServiceEnableActivationState.ProcessIdentityRefused,
            [ServiceStartabilityState.StatusDocumentRefused] =
                ServiceEnableActivationState.StatusDocumentRefused,
            [ServiceStartabilityState.HeartbeatDidNotAdvance] =
                ServiceEnableActivationState.HeartbeatDidNotAdvance,
            [ServiceStartabilityState.ForbiddenChildProcess] =
                ServiceEnableActivationState.ForbiddenChildProcess,
            [ServiceStartabilityState.StatusMissingTimeout] =
                ServiceEnableActivationState.StatusMissingTimeout,
            [ServiceStartabilityState.StatusStartingTimeout] =
                ServiceEnableActivationState.StatusStartingTimeout,
            [ServiceStartabilityState.StatusUnreadable] = ServiceEnableActivationState.StatusUnreadable,
            [ServiceStartabilityState.StatusMalformed] = ServiceEnableActivationState.StatusMalformed,
            [ServiceStartabilityState.StatusWrongContext] = ServiceEnableActivationState.StatusWrongContext,
            [ServiceStartabilityState.StatusStopped] = ServiceEnableActivationState.StatusStopped,
            [ServiceStartabilityState.StatusFailed] = ServiceEnableActivationState.StatusFailed,
            [ServiceStartabilityState.StatusUnknownState] = ServiceEnableActivationState.StatusUnknownState,

            // The two states that deliberately reach the closed fallback.
            [ServiceStartabilityState.Unspecified] = ServiceEnableActivationState.Unavailable,
            [ServiceStartabilityState.Unavailable] = ServiceEnableActivationState.Unavailable,
        };

        var declared = new HashSet<ServiceStartabilityState>(Enum.GetValues<ServiceStartabilityState>());
        Assert.Empty(declared.Except(expected.Keys));
        Assert.Empty(expected.Keys.Except(declared));

        foreach (KeyValuePair<ServiceStartabilityState, ServiceEnableActivationState> row in expected)
        {
            Assert.Equal(row.Value, ServiceEnableActivationStage.MapProof(row.Key));
        }

        // Verified is the ONLY success on either side of the map.
        foreach (ServiceStartabilityState state in declared)
        {
            Assert.Equal(
                state == ServiceStartabilityState.Verified,
                ServiceEnableActivationStage.MapProof(state) == ServiceEnableActivationState.Verified);
        }

        Assert.Equal(
            ServiceEnableActivationState.Unavailable,
            ServiceEnableActivationStage.MapProof((ServiceStartabilityState)9999));
    }

    [Fact]
    public void The_scm_start_attempt_surface_is_unchanged_by_cycle_seventy_five()
    {
        // THE SCM ADAPTER SURFACE. Cycle 75 touched the status-readiness stage
        // only; every start-attempt result must keep the EXACT activation state
        // it carried before.
        var expected = new Dictionary<ServiceStartAttemptResult, ServiceEnableActivationState>
        {
            [ServiceStartAttemptResult.LogonRefused] = ServiceEnableActivationState.StartLogonRefused,
            [ServiceStartAttemptResult.AccessRefused] = ServiceEnableActivationState.StartAccessRefused,
            [ServiceStartAttemptResult.BinaryUnavailable] =
                ServiceEnableActivationState.StartBinaryUnavailable,
            [ServiceStartAttemptResult.DependencyRefused] =
                ServiceEnableActivationState.StartDependencyRefused,
            [ServiceStartAttemptResult.RequestTimeout] = ServiceEnableActivationState.StartRequestTimeout,

            // The CLOSED FALLBACK arm. Started and AlreadyRunning never reach
            // this map in production; if they ever did, a refusal is the only
            // safe answer.
            [ServiceStartAttemptResult.Refused] = ServiceEnableActivationState.StartRefused,
            [ServiceStartAttemptResult.Unspecified] = ServiceEnableActivationState.StartRefused,
            [ServiceStartAttemptResult.Started] = ServiceEnableActivationState.StartRefused,
            [ServiceStartAttemptResult.AlreadyRunning] = ServiceEnableActivationState.StartRefused,
        };

        var declared = new HashSet<ServiceStartAttemptResult>(Enum.GetValues<ServiceStartAttemptResult>());
        Assert.Empty(declared.Except(expected.Keys));
        Assert.Empty(expected.Keys.Except(declared));

        foreach (KeyValuePair<ServiceStartAttemptResult, ServiceEnableActivationState> row in expected)
        {
            Assert.Equal(row.Value, ServiceEnableActivationStage.MapStartAttempt(row.Key));

            // NO arm of this map can produce a success.
            Assert.NotEqual(ServiceEnableActivationState.Verified, row.Value);
        }

        // EXACTLY TWO results mean the service is started, and neither of them
        // is a refusal on the IsStarted axis.
        Assert.True(ServiceStartAttemptResultMap.IsStarted(ServiceStartAttemptResult.Started));
        Assert.True(ServiceStartAttemptResultMap.IsStarted(ServiceStartAttemptResult.AlreadyRunning));
        foreach (ServiceStartAttemptResult result in declared)
        {
            bool started =
                result == ServiceStartAttemptResult.Started
                || result == ServiceStartAttemptResult.AlreadyRunning;
            Assert.Equal(started, ServiceStartAttemptResultMap.IsStarted(result));
        }

        Assert.False(ServiceStartAttemptResultMap.IsStarted((ServiceStartAttemptResult)9999));
        Assert.Equal(
            ServiceEnableActivationState.StartRefused,
            ServiceEnableActivationStage.MapStartAttempt((ServiceStartAttemptResult)9999));
    }

    [Fact]
    public void Every_declared_document_state_has_an_explicit_readiness_classification()
    {
        // THE THREE-WAY SPLIT cycle 75 introduced: ready, transient, terminal.
        // A document state appended without a decision would fall to the
        // TerminalStatusRefusal fallback and be reported as unknown_state, which
        // is safe but silent - only this set equality makes it loud.
        var ready = new HashSet<ServiceRuntimeDocumentState> { ServiceRuntimeDocumentState.Valid };

        var transient = new HashSet<ServiceRuntimeDocumentState>
        {
            ServiceRuntimeDocumentState.Missing,
            ServiceRuntimeDocumentState.Starting,
        };

        var terminal = new Dictionary<ServiceRuntimeDocumentState, ServiceStartabilityState>
        {
            [ServiceRuntimeDocumentState.Unreadable] = ServiceStartabilityState.StatusUnreadable,
            [ServiceRuntimeDocumentState.Malformed] = ServiceStartabilityState.StatusMalformed,
            [ServiceRuntimeDocumentState.WrongExecutionContext] =
                ServiceStartabilityState.StatusWrongContext,
            [ServiceRuntimeDocumentState.Stopped] = ServiceStartabilityState.StatusStopped,
            [ServiceRuntimeDocumentState.Failed] = ServiceStartabilityState.StatusFailed,
            [ServiceRuntimeDocumentState.UnknownState] = ServiceStartabilityState.StatusUnknownState,
            [ServiceRuntimeDocumentState.Unspecified] = ServiceStartabilityState.StatusUnknownState,
        };

        var accounted = new HashSet<ServiceRuntimeDocumentState>(ready);
        accounted.UnionWith(transient);
        accounted.UnionWith(terminal.Keys);

        var declared = new HashSet<ServiceRuntimeDocumentState>(
            Enum.GetValues<ServiceRuntimeDocumentState>());

        Assert.Empty(ready.Intersect(transient));
        Assert.Empty(ready.Intersect(terminal.Keys));
        Assert.Empty(transient.Intersect(terminal.Keys));
        Assert.Empty(declared.Except(accounted));
        Assert.Empty(accounted.Except(declared));
        Assert.Equal(declared.Count, accounted.Count);

        foreach (KeyValuePair<ServiceRuntimeDocumentState, ServiceStartabilityState> row in terminal)
        {
            Assert.Equal(row.Value, ServiceStartabilityCoordinator.TerminalStatusRefusal(row.Key));

            // A terminal refusal is NEVER a ready or transient verdict.
            Assert.NotEqual(ServiceStartabilityState.Verified, row.Value);
            Assert.NotEqual(ServiceStartabilityState.StatusMissingTimeout, row.Value);
            Assert.NotEqual(ServiceStartabilityState.StatusStartingTimeout, row.Value);
        }

        Assert.Equal(
            ServiceStartabilityState.StatusUnknownState,
            ServiceStartabilityCoordinator.TerminalStatusRefusal((ServiceRuntimeDocumentState)9999));
    }

    [Fact]
    public void The_heartbeat_and_bounded_window_constants_are_unchanged_by_cycle_seventy_five()
    {
        // HEARTBEAT SEMANTICS ARE AN EXTERNAL CONTRACT. Cycle 75 added a status
        // -readiness window and must not have moved any pre-existing bound.
        Assert.Equal(TimeSpan.FromSeconds(60), ServiceStartabilityContract.RunningWait);
        Assert.Equal(TimeSpan.FromSeconds(60), ServiceStartabilityContract.StoppedWait);
        Assert.Equal(TimeSpan.FromSeconds(30), ServiceStartabilityContract.AbsenceWait);
        Assert.Equal(TimeSpan.FromSeconds(45), ServiceStartabilityContract.HeartbeatProofWindow);
        Assert.Equal(TimeSpan.FromMilliseconds(250), ServiceStartabilityContract.PollInterval);
        Assert.Equal(2, ServiceStartabilityContract.RequiredHeartbeatObservations);

        // The NEW window is its own constant and does not borrow another bound.
        Assert.Equal(TimeSpan.FromSeconds(60), ServiceStartabilityContract.StatusReadinessWindow);

        // It must be strictly longer than one poll, or the wait could not
        // observe a transient state at all.
        Assert.True(
            ServiceStartabilityContract.StatusReadinessWindow > ServiceStartabilityContract.PollInterval);
    }
}
