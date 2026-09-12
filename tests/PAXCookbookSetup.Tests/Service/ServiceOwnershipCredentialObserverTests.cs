using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 49 - FIXED READ-ONLY SERVICE CREDENTIAL OBSERVER (Setup only), PHASE 1
// ===========================================================================
//
// PHASE 1 STATUS. ServiceOwnershipCredentialObserver.cs is a COMPILING STUB
// (cycle 49): every type/member below has its FINAL, FROZEN signature, but
// every production method returns the bounded FAILURE value
// (ServiceOwnershipCredentialObserverState.Unspecified /
// ServiceOwnershipCredentialObservation.Unspecified) WITHOUT THROWING. Every
// assertion below states the FINAL expected behavior for phase 2, so the
// interpreter tests are EXPECTED TO FAIL RED right now (genuine assertion
// failures against the stub's permanent Unspecified return, never a compiler
// error). The structural and native-marshalling tests describe invariants
// that must hold both now and after phase 2; a few of them (native-sequence
// presence, handle cleanup) also fail red now because that code does not
// exist yet, and must turn green once phase 2 adds it without editing this
// file. This whole file is BYTE-FROZEN from the end of phase 1 onward.
//
// A DOCUMENTED, HONEST LIMITATION. The certified ServiceOwnershipLedgerContract
// parser (see the contract's own ParseEntry path) ALREADY enforces - for ANY
// accepted document, in ANY lifecycle state, not only "active" - that an
// entry's provider kind, rights-profile id, grant mechanism, descriptor
// format, rights-policy version, key identity, provider unique name, service
// SID, and prior-descriptor decode/hash/shape are all individually valid and
// (for provider/profile/mechanism/format/version) exactly the one approved
// combination. That means an entry obtained through this file's ONLY
// construction path (ServiceOwnershipLedgerValidator.Validate) can NEVER
// carry a defect on any of those dimensions: the interpreter's own redundant
// checks for them are real, honest defense-in-depth, but are STRUCTURALLY
// UNREACHABLE to exercise as failures via a full accepted entry. Rather than
// silently omitting mandate coverage for "wrong provider/profile/mechanism/
// policy version", this file PROVES the claim at the layer where it is
// actually enforced (see the "...refused_before_it_ever_reaches_the_observer"
// facts below). The ONE entry-level dimension that genuinely reaches the
// observer with room to be wrong is grantedRightsMask, because the parser
// only requires exact-mask-equality for an "active" lifecycle entry, and the
// entries this file can construct are deliberately "intended" (the only
// combination, alongside transactionState "preparing", that both validates
// and carries entries into an ACCEPTED document under the current schema).
public sealed class ServiceOwnershipCredentialObserverInterpreterTests
{
    // ---- synthetic fixture values ---------------------------------------
    //
    // Every value below is synthetic. No real tenant, account, certificate,
    // key, machine, or directory is represented.

    private const string OwningUserSidValue = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string ValidServiceSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890";
    private const string ForeignServiceSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567899";
    private const string ThumbprintValue = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string KeyIdentityValue = "synthetic-key-identity_01.test";
    private const string ProviderUniqueNameValue = "synthetic-unique-leaf_01.pvk";
    private const string ApprovedProviderToken = "microsoft-software-key-storage-provider";
    private const string ApprovedProfileToken =
        "microsoft-software-ksp-backing-file-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0-filesystemrights";
    private const string ApprovedMechanismToken = "microsoft-software-ksp-backing-file-dacl";
    private const string ApprovedDescriptorFormatToken = "microsoft-software-ksp-backing-file-self-relative-v1";
    private const string ApprovedKeyStorageRootToken = "microsoft-software-key-storage-provider-machine-keys";
    private const string ApprovedMaskText = "00120009";
    private const string ValidButNotApprovedMaskText = "00000081"; // valid vocabulary, not the approved mask
    private const string Stamp = "2026-08-06T00:00:00Z";

    // A structurally valid, SUPPORTED prior descriptor (schema v3): owner
    // BUILTIN\Administrators, a captured group SID, DaclPresent+DaclProtected,
    // no SACL, and EXACTLY the two measured ACEs (SYSTEM then
    // BUILTIN\Administrators, AccessAllowed, ObjectInherit|ContainerInherit,
    // FileFullControlRights). Byte-identical to the certified contract's own
    // cycle-47 measured fixture (tests/PAXCookbook.Shared.Tests/
    // ServiceOwnershipLedgerContractTests.cs), reused here as a synthetic
    // constant - nothing here is read from any machine.
    private static readonly byte[] PriorBytes =
    {
        0x01, 0x00, 0x04, 0x90, 0x14, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x40, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00,
        0x20, 0x02, 0x00, 0x00, 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x15, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0xE9, 0x03, 0x00, 0x00,
        0x02, 0x00, 0x34, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x03, 0x14, 0x00, 0xFF, 0x01, 0x1F, 0x00,
        0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x12, 0x00, 0x00, 0x00, 0x00, 0x03, 0x18, 0x00,
        0xFF, 0x01, 0x1F, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00,
        0x20, 0x02, 0x00, 0x00,
    };

    private static string PriorB64 => Convert.ToBase64String(PriorBytes);

    private static string PriorSha => Hex(SHA256.HashData(PriorBytes));

    private static string Hex(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // ---- entry fixture builder -------------------------------------------
    //
    // The ONLY way to obtain a ServiceOwnershipLedgerEntry is through the
    // certified ServiceOwnershipLedgerValidator, because its constructor is
    // internal to a DIFFERENT assembly with no InternalsVisibleTo to this
    // project. Every fixture below therefore round-trips through real JSON
    // schema-v3 validation, so every entry this file can construct is, by
    // construction, one the certified parser has already accepted.

    private sealed class EntryFixture
    {
        public int SchemaVersion = 3;
        public int Generation = 1;
        public string EntryId = "entry-c49-0001";
        public string OwningUserSid = OwningUserSidValue;
        public string ServiceSid = ValidServiceSid;
        public string CredentialKind = "personal-app-registration-certificate";
        public string CertificateThumbprintSha1 = ThumbprintValue;
        public string Provenance = "referenced";
        public string PrivateKeyProviderKind = ApprovedProviderToken;
        public string RightsProfileId = ApprovedProfileToken;
        public string KeyIdentity = KeyIdentityValue;
        public string GrantMechanism = ApprovedMechanismToken;
        public string GrantedRightsMask = ApprovedMaskText;
        public int RightsPolicyVersion = 3;
        public string PriorDaclState = "present";
        public string PriorDaclBytesBase64 = PriorB64;
        public string PriorDaclSha256 = PriorSha;
        public string[] JobIds = { "job-c49-0001" };
        public string LifecycleState = "intended";
        public string ProviderUniqueName = ProviderUniqueNameValue;
        public string KeyStorageRoot = ApprovedKeyStorageRootToken;
        public string DescriptorFormat = ApprovedDescriptorFormatToken;
        public string Stamp = ServiceOwnershipCredentialObserverInterpreterTests.Stamp;
    }

    private static string Q(string value) => "\"" + value + "\"";

    private static string BuildEntryJson(EntryFixture f)
    {
        ServiceOwnershipLedgerContract.TryParseWireToken(
            f.PrivateKeyProviderKind, out ServiceOwnershipPrivateKeyProviderKind provider);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            f.RightsProfileId, out ServiceOwnershipRightsProfileId profile);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            f.GrantMechanism, out ServiceOwnershipGrantMechanism mechanism);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            f.PriorDaclState, out ServiceOwnershipPriorDaclState priorState);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            f.KeyStorageRoot, out ServiceOwnershipKeyStorageRoot keyStorageRoot);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            f.DescriptorFormat, out ServiceOwnershipDescriptorFormat descriptorFormat);

        string binding = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            f.Generation, f.OwningUserSid, f.ServiceSid, f.CertificateThumbprintSha1,
            provider, profile, f.KeyIdentity, mechanism, f.GrantedRightsMask, priorState,
            f.PriorDaclSha256, f.ProviderUniqueName, keyStorageRoot, descriptorFormat);

        string jobIds = "[" + string.Join(",", f.JobIds.Select(Q)) + "]";

        return "{"
            + Q("entryId") + ":" + Q(f.EntryId) + ","
            + Q("owningUserSid") + ":" + Q(f.OwningUserSid) + ","
            + Q("serviceSid") + ":" + Q(f.ServiceSid) + ","
            + Q("credentialKind") + ":" + Q(f.CredentialKind) + ","
            + Q("certificateThumbprintSha1") + ":" + Q(f.CertificateThumbprintSha1) + ","
            + Q("provenance") + ":" + Q(f.Provenance) + ","
            + Q("privateKeyProviderKind") + ":" + Q(f.PrivateKeyProviderKind) + ","
            + Q("rightsProfileId") + ":" + Q(f.RightsProfileId) + ","
            + Q("keyIdentity") + ":" + Q(f.KeyIdentity) + ","
            + Q("grantMechanism") + ":" + Q(f.GrantMechanism) + ","
            + Q("grantedRightsMask") + ":" + Q(f.GrantedRightsMask) + ","
            + Q("rightsPolicyVersion") + ":" + f.RightsPolicyVersion.ToString(CultureInfo.InvariantCulture) + ","
            + Q("priorDaclState") + ":" + Q(f.PriorDaclState) + ","
            + Q("priorDaclBytesBase64") + ":" + Q(f.PriorDaclBytesBase64) + ","
            + Q("priorDaclSha256") + ":" + Q(f.PriorDaclSha256) + ","
            + Q("capturedStateBindingSha256") + ":" + Q(binding) + ","
            + Q("associatedPromotedJobIds") + ":" + jobIds + ","
            + Q("lifecycleState") + ":" + Q(f.LifecycleState) + ","
            + Q("createdUtc") + ":" + Q(f.Stamp) + ","
            + Q("updatedUtc") + ":" + Q(f.Stamp) + ","
            + Q("providerUniqueName") + ":" + Q(f.ProviderUniqueName) + ","
            + Q("keyStorageRoot") + ":" + Q(f.KeyStorageRoot) + ","
            + Q("descriptorFormat") + ":" + Q(f.DescriptorFormat)
            + "}";
    }

    private static string BuildDocumentJson(EntryFixture f)
    {
        return "{"
            + Q("schemaVersion") + ":" + f.SchemaVersion.ToString(CultureInfo.InvariantCulture) + ","
            + Q("productOwnershipMarker") + ":" + Q(ServiceOwnershipLedgerContract.ProductOwnershipMarker) + ","
            + Q("managedFeatureId") + ":" + Q(ServiceOwnershipLedgerContract.ManagedFeatureId) + ","
            + Q("installationOwnershipId") + ":" + Q("install-c49-0001") + ","
            + Q("generation") + ":" + f.Generation.ToString(CultureInfo.InvariantCulture) + ","
            + Q("transactionState") + ":" + Q("preparing") + ","
            + Q("entries") + ":[" + BuildEntryJson(f) + "],"
            + Q("createdUtc") + ":" + Q(f.Stamp) + ","
            + Q("updatedUtc") + ":" + Q(f.Stamp) + ","
            + Q("lastOperationId") + ":" + Q("op-c49-0001")
            + "}";
    }

    private static ServiceOwnershipLedgerEntry BuildEntry(Action<EntryFixture>? customize = null)
    {
        var f = new EntryFixture();
        customize?.Invoke(f);
        string json = BuildDocumentJson(f);

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(json);
        Assert.True(result.IsAccepted, "fixture entry failed to validate: " + result.Reason);
        Assert.NotNull(result.Document);
        Assert.Single(result.Document!.Entries);
        return result.Document.Entries[0];
    }

    private static ServiceOwnershipObservedNativeFacts GoodFacts(byte[]? observedBytes) =>
        new(true, true, true, observedBytes);

    // ---- descriptor-variant byte helpers -----------------------------------
    //
    // Every variant below is built EITHER from the certified contract's own
    // TryBuildPostGrantDescriptor output, or by simple arithmetic on the
    // documented offsets that same contract already relies on (see its own
    // "the DACL is the LAST structure in the buffer" comment). No independent
    // SID/ACE encoder is hand-rolled here.

    private static byte[] CanonicalGrant(string serviceSid)
    {
        bool built = ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            PriorBytes, serviceSid, out byte[] grant, out _, out _);
        Assert.True(built, "the canonical post-grant descriptor was expected to build for: " + serviceSid);
        return grant;
    }

    private static uint ReadU32(byte[] b, int offset) =>
        (uint)b[offset] | ((uint)b[offset + 1] << 8) | ((uint)b[offset + 2] << 16) | ((uint)b[offset + 3] << 24);

    private static ushort ReadU16(byte[] b, int offset) => (ushort)(b[offset] | (b[offset + 1] << 8));

    private static void WriteU16(byte[] b, int offset, ushort value)
    {
        b[offset] = (byte)(value & 0xFF);
        b[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteU32(byte[] b, int offset, uint value)
    {
        b[offset] = (byte)(value & 0xFF);
        b[offset + 1] = (byte)((value >> 8) & 0xFF);
        b[offset + 2] = (byte)((value >> 16) & 0xFF);
        b[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    /// <summary>The appended service ACE's mask lives at PriorBytes.Length + 4 (type,flags,size,then mask).</summary>
    private static byte[] WithGrantMask(uint mask)
    {
        byte[] grant = CanonicalGrant(ValidServiceSid);
        WriteU32(grant, PriorBytes.Length + 4, mask);
        return grant;
    }

    private static byte[] DuplicatedServiceAce()
    {
        byte[] grant = CanonicalGrant(ValidServiceSid);
        byte[] serviceAce = grant[PriorBytes.Length..];
        uint offsetDacl = ReadU32(grant, 16);
        ushort oldAclSize = ReadU16(grant, (int)offsetDacl + 2);
        ushort oldAceCount = ReadU16(grant, (int)offsetDacl + 4);

        var result = new byte[grant.Length + serviceAce.Length];
        Array.Copy(grant, result, grant.Length);
        Array.Copy(serviceAce, 0, result, grant.Length, serviceAce.Length);
        WriteU16(result, (int)offsetDacl + 2, (ushort)(oldAclSize + serviceAce.Length));
        WriteU16(result, (int)offsetDacl + 4, (ushort)(oldAceCount + 1));
        return result;
    }

    private static byte[] ReorderedGrant()
    {
        uint offsetDacl = ReadU32(PriorBytes, 16);
        int aclHeaderEnd = (int)offsetDacl + 8;
        byte[] head = PriorBytes[..(int)offsetDacl];
        byte[] aclHeader = (byte[])PriorBytes[(int)offsetDacl..aclHeaderEnd].Clone();
        byte[] existingAces = PriorBytes[aclHeaderEnd..];
        byte[] serviceAce = CanonicalGrant(ValidServiceSid)[PriorBytes.Length..];

        WriteU16(aclHeader, 2, (ushort)(8 + serviceAce.Length + existingAces.Length));
        WriteU16(aclHeader, 4, 3);

        var result = new byte[head.Length + aclHeader.Length + serviceAce.Length + existingAces.Length];
        int pos = 0;
        Array.Copy(head, 0, result, pos, head.Length);
        pos += head.Length;
        Array.Copy(aclHeader, 0, result, pos, aclHeader.Length);
        pos += aclHeader.Length;
        Array.Copy(serviceAce, 0, result, pos, serviceAce.Length);
        pos += serviceAce.Length;
        Array.Copy(existingAces, 0, result, pos, existingAces.Length);
        return result;
    }

    public static TheoryData<string, byte[]> DivergedVariants()
    {
        var data = new TheoryData<string, byte[]>
        {
            { "generic-read-mask", WithGrantMask(0x80000000u) }, // FileGenericReadRight
            { "superset-mask", WithGrantMask(0x0012000Bu) }, // approved mask plus FileWriteDataRight
            { "duplicate-service-ace", DuplicatedServiceAce() },
            { "foreign-service-ace", CanonicalGrant(ForeignServiceSid) },
            { "reordered-aces", ReorderedGrant() },
        };
        return data;
    }

    // =======================================================================
    // DEFAULT / UNSPECIFIED INPUT - NEVER SUCCESS
    // =======================================================================

    [Fact]
    public void A_default_uninitialised_result_is_unspecified_and_never_a_success()
    {
        var result = default(ServiceOwnershipCredentialObservationResult);

        Assert.Equal(ServiceOwnershipCredentialObserverState.Unspecified, result.State);
        Assert.Equal(ServiceOwnershipCredentialObservation.Unspecified, result.Observation);
        Assert.Equal("Unspecified", result.ToString());
    }

    [Fact]
    public void A_null_entry_is_refused_as_unavailable_and_never_success()
    {
        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(null, GoodFacts(PriorBytes));

        Assert.Equal(ServiceOwnershipCredentialObserverState.Observed, result.State);
        Assert.Equal(ServiceOwnershipCredentialObservation.Unavailable, result.Observation);
    }

    // =======================================================================
    // THE ONE ENTRY-LEVEL DEFECT REACHABLE VIA A FULLY VALIDATED ENTRY
    // =======================================================================

    [Fact]
    public void A_valid_but_not_approved_granted_rights_mask_is_refused_as_unavailable()
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry(f => f.GrantedRightsMask = ValidButNotApprovedMaskText);

        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, GoodFacts(PriorBytes));

        Assert.Equal(ServiceOwnershipCredentialObservation.Unavailable, result.Observation);
    }

    [Fact]
    public void A_wrong_mask_is_refused_even_when_the_observed_descriptor_would_otherwise_exactly_match_the_prior()
    {
        // Mask precondition must be checked BEFORE descriptor classification, so an
        // otherwise-exact prior match still refuses.
        ServiceOwnershipLedgerEntry entry = BuildEntry(f => f.GrantedRightsMask = ValidButNotApprovedMaskText);

        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, GoodFacts((byte[])PriorBytes.Clone()));

        Assert.Equal(ServiceOwnershipCredentialObservation.Unavailable, result.Observation);
    }

    // =======================================================================
    // OBSERVED PROVIDER UNIQUE NAME - KEY IDENTITY MISMATCH
    // =======================================================================

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void An_unsafe_or_mismatched_observed_unique_name_maps_to_key_identity_mismatch(
        bool observedValidGrammar, bool observedMatchesEntry)
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry();
        var facts = new ServiceOwnershipObservedNativeFacts(
            observedValidGrammar, observedMatchesEntry, true, (byte[])PriorBytes.Clone());

        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, facts);

        Assert.Equal(ServiceOwnershipCredentialObservation.KeyIdentityMismatch, result.Observation);
    }

    [Fact]
    public void A_key_identity_mismatch_is_reported_even_when_the_descriptor_would_otherwise_match_the_recorded_grant()
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry();
        var facts = new ServiceOwnershipObservedNativeFacts(true, false, true, CanonicalGrant(ValidServiceSid));

        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, facts);

        Assert.Equal(ServiceOwnershipCredentialObservation.KeyIdentityMismatch, result.Observation);
    }

    // =======================================================================
    // DESCRIPTOR AVAILABILITY
    // =======================================================================

    [Fact]
    public void An_incomplete_descriptor_read_is_unavailable_regardless_of_whatever_bytes_were_partially_captured()
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry();
        var facts = new ServiceOwnershipObservedNativeFacts(true, true, false, (byte[])PriorBytes.Clone());

        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, facts);

        Assert.Equal(ServiceOwnershipCredentialObservation.Unavailable, result.Observation);
    }

    [Fact]
    public void A_missing_observed_descriptor_despite_a_claimed_complete_read_is_unavailable()
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry();
        var facts = new ServiceOwnershipObservedNativeFacts(true, true, true, null);

        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, facts);

        Assert.Equal(ServiceOwnershipCredentialObservation.Unavailable, result.Observation);
    }

    // =======================================================================
    // MATCHES CAPTURED PRIOR STATE / MATCHES RECORDED GRANT
    // =======================================================================

    [Fact]
    public void The_exact_captured_prior_descriptor_matches_captured_prior_state()
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry();
        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, GoodFacts((byte[])PriorBytes.Clone()));

        Assert.Equal(ServiceOwnershipCredentialObservation.MatchesCapturedPriorState, result.Observation);
    }

    [Fact]
    public void The_exact_generated_grant_matches_recorded_grant()
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry();
        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, GoodFacts(CanonicalGrant(ValidServiceSid)));

        Assert.Equal(ServiceOwnershipCredentialObservation.MatchesRecordedGrant, result.Observation);
    }

    // =======================================================================
    // DIVERGED - GenericRead, superset, duplicate, foreign, reordered
    // =======================================================================

    [Theory]
    [MemberData(nameof(DivergedVariants))]
    public void Every_structural_divergence_from_prior_or_grant_maps_to_diverged(string variantName, byte[] observedBytes)
    {
        Assert.False(string.IsNullOrEmpty(variantName)); // positive control: theory data is real
        ServiceOwnershipLedgerEntry entry = BuildEntry();

        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, GoodFacts(observedBytes));

        Assert.Equal(ServiceOwnershipCredentialObservation.Diverged, result.Observation);
    }

    [Fact]
    public void A_malformed_descriptor_that_was_read_completely_is_diverged_not_unavailable()
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry();
        byte[] truncated = CanonicalGrant(ValidServiceSid)[..10]; // too short to parse structurally
        var facts = new ServiceOwnershipObservedNativeFacts(true, true, true, truncated);

        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, facts);

        Assert.Equal(ServiceOwnershipCredentialObservation.Diverged, result.Observation);
    }

    [Fact]
    public void The_same_malformed_bytes_are_unavailable_instead_when_the_read_was_incomplete()
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry();
        byte[] truncated = CanonicalGrant(ValidServiceSid)[..10];
        var facts = new ServiceOwnershipObservedNativeFacts(true, true, false, truncated);

        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, facts);

        Assert.Equal(ServiceOwnershipCredentialObservation.Unavailable, result.Observation);
    }

    // =======================================================================
    // EVERY BOUNDED OBSERVATION IS REACHABLE, AND EXCEPTION CONTAINMENT
    // =======================================================================

    [Fact]
    public void Every_bounded_observation_the_observer_can_return_is_individually_reachable()
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry();

        var reached = new HashSet<ServiceOwnershipCredentialObservation>
        {
            ServiceOwnershipCredentialObserverInterpreter.Interpret(null, GoodFacts(PriorBytes)).Observation,
            ServiceOwnershipCredentialObserverInterpreter
                .Interpret(entry, new ServiceOwnershipObservedNativeFacts(false, true, true, PriorBytes))
                .Observation,
            ServiceOwnershipCredentialObserverInterpreter
                .Interpret(entry, GoodFacts((byte[])PriorBytes.Clone()))
                .Observation,
            ServiceOwnershipCredentialObserverInterpreter
                .Interpret(entry, GoodFacts(CanonicalGrant(ValidServiceSid)))
                .Observation,
            ServiceOwnershipCredentialObserverInterpreter
                .Interpret(entry, GoodFacts(CanonicalGrant(ForeignServiceSid)))
                .Observation,
        };

        Assert.Equal(
            new[]
            {
                ServiceOwnershipCredentialObservation.Unavailable,
                ServiceOwnershipCredentialObservation.KeyIdentityMismatch,
                ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
                ServiceOwnershipCredentialObservation.Diverged,
            }.OrderBy(v => v.ToString(), StringComparer.Ordinal),
            reached.OrderBy(v => v.ToString(), StringComparer.Ordinal));
    }

    [Fact]
    public void The_interpreter_never_throws_for_any_input_including_hostile_or_truncated_bytes()
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry();
        byte[][] hostileInputs =
        {
            Array.Empty<byte>(),
            new byte[] { 0xFF },
            new byte[2048],
        };

        foreach (byte[] hostile in hostileInputs)
        {
            var facts = new ServiceOwnershipObservedNativeFacts(true, true, true, hostile);
            Exception? exception = Record.Exception(
                () => ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, facts));
            Assert.Null(exception);
        }

        var allDefaultFacts = new ServiceOwnershipObservedNativeFacts(false, false, false, null);
        Assert.Null(Record.Exception(
            () => ServiceOwnershipCredentialObserverInterpreter.Interpret(null, allDefaultFacts)));
    }

    // =======================================================================
    // NO SENSITIVE VALUE ANYWHERE IN THE RESULT, INCLUDING TOSTRING()
    // =======================================================================

    [Fact]
    public void No_sensitive_value_is_representable_in_the_result_or_its_tostring()
    {
        var unspecified = default(ServiceOwnershipCredentialObservationResult);
        Assert.Equal("Unspecified", unspecified.ToString());

        ServiceOwnershipLedgerEntry entry = BuildEntry();
        ServiceOwnershipCredentialObservationResult observed =
            ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, GoodFacts((byte[])PriorBytes.Clone()));
        Assert.Equal("Observed", observed.ToString());

        foreach (string token in new[]
                 {
                     "S-1-", "\\", KeyIdentityValue, ProviderUniqueNameValue, ThumbprintValue,
                     "MatchesCapturedPriorState", "MatchesRecordedGrant", "Diverged", "KeyIdentityMismatch",
                     "Unavailable",
                 })
        {
            Assert.DoesNotContain(token, unspecified.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(token, observed.ToString(), StringComparison.Ordinal);
        }

        // The whole public shape is exactly two members plus ToString.
        string[] members = typeof(ServiceOwnershipCredentialObservationResult)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Observation", "State" }, members);
    }

    // =======================================================================
    // THE TRANSITION ALWAYS BUILDS FOR A VALIDATED ENTRY (proves the
    // interpreter's reliance on TryBuildPostGrantDescriptor is never
    // vacuously safe by accident)
    // =======================================================================

    [Fact]
    public void The_transition_the_interpreter_relies_on_always_builds_for_any_validated_entry()
    {
        ServiceOwnershipLedgerEntry entry = BuildEntry();

        bool built = ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            PriorBytes, entry.ServiceSid, out byte[] grant, out string sha, out _);

        Assert.True(built);
        Assert.NotEmpty(grant);
        Assert.Equal(64, sha.Length);
    }

    // =======================================================================
    // DOCUMENTED LIMITATION - "wrong provider/profile/mechanism/policy
    // version" is refused ONE LAYER UP, before an entry ever exists to hand
    // to this observer. Proven here rather than merely asserted in prose.
    // =======================================================================

    [Fact]
    public void Wrong_provider_kind_is_refused_by_the_certified_parser_before_an_entry_can_ever_be_built()
    {
        // "cng" does not pair with the fixture's approved backing-file-DACL
        // mechanism (IsMechanismPairedWith), so the mechanism-pairing gate
        // refuses it before the profile-approval gate is ever reached.
        var f = new EntryFixture { PrivateKeyProviderKind = "cng" };
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(BuildDocumentJson(f));

        Assert.True(result.IsRefused);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.ProviderMechanismMismatch, result.Reason);
    }

    [Fact]
    public void Wrong_mechanism_is_refused_by_the_certified_parser_before_an_entry_can_ever_be_built()
    {
        var f = new EntryFixture { GrantMechanism = "cng-security-descriptor" };
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(BuildDocumentJson(f));

        Assert.True(result.IsRefused);
        Assert.Equal(
            ServiceOwnershipLedgerInvalidReason.RetiredCngSecurityDescriptorMechanismUnsupported, result.Reason);
    }

    [Fact]
    public void Wrong_rights_policy_version_is_refused_by_the_certified_parser_before_an_entry_can_ever_be_built()
    {
        var f = new EntryFixture { RightsPolicyVersion = 2 };
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(BuildDocumentJson(f));

        Assert.True(result.IsRefused);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.InvalidRightsPolicyVersion, result.Reason);
    }

    [Fact]
    public void An_invalid_key_identity_is_refused_by_the_certified_parser_before_an_entry_can_ever_be_built()
    {
        var f = new EntryFixture { KeyIdentity = "../escape" };
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(BuildDocumentJson(f));

        Assert.True(result.IsRefused);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.InvalidKeyIdentity, result.Reason);
    }

    [Fact]
    public void A_non_canonical_service_sid_is_refused_by_the_certified_parser_before_an_entry_can_ever_be_built()
    {
        var f = new EntryFixture { ServiceSid = "not-a-sid" };
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(BuildDocumentJson(f));

        Assert.True(result.IsRefused);
    }
}

// ===========================================================================
// STRUCTURAL CONTAINMENT - the fixed shim and pure interpreter, from source
// ===========================================================================
public sealed class ServiceOwnershipCredentialObserverStructuralTests
{
    private static string RepoRoot() => ServiceSidResolverStructuralTests.RepoRoot();

    private static string ObserverPath() =>
        Path.Combine(RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipCredentialObserver.cs");

    private static string ObserverRaw() => File.ReadAllText(ObserverPath());

    private static string ObserverCode() => SetupCSharpLexicalScanner.ExtractCode(ObserverRaw());

    private static string MethodBody(string code, string signatureFragment) =>
        ServiceSidResolverStructuralTests.MethodBody(code, signatureFragment);

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ---- positive control ----------------------------------------------------

    [Fact]
    public void The_scanner_really_read_the_observer_file()
    {
        string code = ObserverCode();
        Assert.Contains("class ServiceOwnershipCredentialObserver", code, StringComparison.Ordinal);
        Assert.Contains("class ServiceOwnershipCredentialObserverInterpreter", code, StringComparison.Ordinal);
        Assert.True(code.Length > 500, "the extracted observer code looks too small to be real: " + code.Length);
    }

    // ---- ONE fixed provider string ---------------------------------------

    [Fact]
    public void Exactly_one_fixed_provider_string_appears_and_no_other_provider_name()
    {
        // The lexical scanner deliberately BLANKS string-literal CONTENT (so the
        // capability scan cannot false-positive on a token that only appears
        // inside a string or a comment). Checking for the literal provider
        // string value itself therefore requires the RAW source, not the
        // scanned code.
        string raw = ObserverRaw();

        Assert.Equal(1, Occurrences(raw, "\"Microsoft Software Key Storage Provider\""));

        string[] otherProviderNames =
        {
            "Microsoft Enhanced RSA and AES Cryptographic Provider",
            "Microsoft Base Cryptographic Provider",
            "Microsoft Enhanced Cryptographic Provider",
        };
        foreach (string other in otherProviderNames)
        {
            Assert.DoesNotContain(other, raw, StringComparison.Ordinal);
        }
    }

    // ---- no injectable seam ----------------------------------------------

    [Fact]
    public void No_type_in_the_observer_declares_a_delegate_field_virtual_member_or_settable_static_field()
    {
        foreach (Type t in new[]
                 {
                     typeof(ServiceOwnershipCredentialObserverState),
                     typeof(ServiceOwnershipCredentialObservationResult),
                     typeof(ServiceOwnershipObservedNativeFacts),
                     typeof(ServiceOwnershipCredentialObserverInterpreter),
                     typeof(ServiceOwnershipCredentialObserver),
                 })
        {
            // Enums always implement IComparable/IFormattable/IConvertible/
            // ISpanFormattable via System.Enum, and always carry a mutable
            // instance field named "value__" for their underlying integral
            // storage; both are BCL guarantees, not an injectable seam, so
            // enums are excluded from the field/interface checks below.
            if (t.IsEnum)
            {
                continue;
            }

            Assert.Empty(t.GetInterfaces());

            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                Assert.True(f.IsLiteral || f.IsInitOnly, t.Name + "." + f.Name + " is mutable");
                Assert.False(typeof(Delegate).IsAssignableFrom(f.FieldType), t.Name + "." + f.Name + " is a delegate");
            }

            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.Name is "ToString" or "Equals" or "GetHashCode")
                {
                    // Standard object overrides, not an injectable seam.
                    continue;
                }

                Assert.False(m.IsVirtual && !m.IsFinal, t.Name + "." + m.Name + " is virtual/overridable");
                Assert.False(m.IsAbstract, t.Name + "." + m.Name + " is abstract");

                foreach (ParameterInfo p in m.GetParameters())
                {
                    Assert.False(typeof(Delegate).IsAssignableFrom(p.ParameterType), t.Name + "." + m.Name + " accepts a delegate");
                }
            }
        }
    }

    // ---- no caller-supplied path/provider/descriptor-bytes/delegate on any
    //      public or internal method (private native declarations, added in
    //      phase 2, are exempt: they are unreachable by any caller) ---------

    [Fact]
    public void No_public_or_internal_method_accepts_a_bare_string_byte_array_or_delegate_parameter()
    {
        foreach (Type t in new[]
                 {
                     typeof(ServiceOwnershipCredentialObserverInterpreter),
                     typeof(ServiceOwnershipCredentialObserver),
                 })
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.IsPrivate)
                {
                    continue; // native P/Invoke declarations are private and exempt
                }

                foreach (ParameterInfo p in m.GetParameters())
                {
                    Assert.NotEqual(typeof(string), p.ParameterType);
                    Assert.NotEqual(typeof(byte[]), p.ParameterType);
                    Assert.False(typeof(Delegate).IsAssignableFrom(p.ParameterType));
                }
            }
        }
    }

    [Fact]
    public void The_shims_one_entry_point_takes_only_a_validated_ledger_entry()
    {
        Type t = typeof(ServiceOwnershipCredentialObserver);
        Assert.True(t.IsAbstract && t.IsSealed, "the shim must be a static class");

        MethodInfo[] nonPrivate = t
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsPrivate)
            .ToArray();

        MethodInfo observe = Assert.Single(nonPrivate);
        Assert.Equal("Observe", observe.Name);

        ParameterInfo[] parameters = observe.GetParameters();
        ParameterInfo entryParam = Assert.Single(parameters);
        Assert.Equal(typeof(ServiceOwnershipLedgerEntry), entryParam.ParameterType);
        Assert.Equal(typeof(ServiceOwnershipCredentialObservationResult), observe.ReturnType);

        // No instance surface and no retained state of any kind.
        Assert.Empty(t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Empty(t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void The_pure_interpreters_one_entry_point_takes_only_the_entry_and_bounded_facts()
    {
        Type t = typeof(ServiceOwnershipCredentialObserverInterpreter);
        Assert.True(t.IsAbstract && t.IsSealed, "the interpreter must be a static class");

        MethodInfo[] nonPrivate = t
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsPrivate)
            .ToArray();

        MethodInfo interpret = Assert.Single(nonPrivate);
        Assert.Equal("Interpret", interpret.Name);

        ParameterInfo[] parameters = interpret.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(ServiceOwnershipLedgerEntry), parameters[0].ParameterType);
        Assert.Equal(typeof(ServiceOwnershipObservedNativeFacts), parameters[1].ParameterType);
        Assert.Equal(typeof(ServiceOwnershipCredentialObservationResult), interpret.ReturnType);
    }

    // ---- no forbidden capability -------------------------------------------

    public static TheoryData<string> ForbiddenCapabilityTokens() => new()
    {
        // ACL write
        "SetKernelObjectSecurity", "SetFileSecurity", "SetNamedSecurityInfo", "SetSecurityInfo",
        "NCryptSetProperty", "SetAccessControl", "AddAccessRule", "SetOwner",
        // ledger and lifecycle-planner
        "File.ReadAll", "File.WriteAll", "Directory.", "ownership-ledger",
        "ServiceOwnershipLifecyclePlanner.Plan",
        // service control manager
        "OpenSCManager", "CreateService", "ChangeServiceConfig", "DeleteService",
        "StartService", "ControlService", "ServiceController", "ServiceInstaller",
        // certificate store and registry
        "X509Store", "CngKey", "CngProvider", "RegistryKey", "Microsoft.Win32.Registry",
        "CredRead", "CredWrite", "PasswordVault",
        // process / elevation / network
        "Process.Start", "ProcessStartInfo", "runas", "HttpClient", "WebClient", "Socket",
        // desktop host, React, PAX, Bake
        "PAXCookbook.App", "web-react", "WebView2", "CoreWebView2",
        "PAX_Purview", "PaxEngine", "StartBake", "Start-Bake", "startCook",
    };

    [Theory]
    [MemberData(nameof(ForbiddenCapabilityTokens))]
    public void The_observer_contains_no_forbidden_capability(string token)
    {
        Assert.DoesNotContain(token, ObserverCode(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ForbiddenCapabilityTokens))]
    public void The_forbidden_capability_scan_can_actually_fire(string token)
    {
        Assert.False(string.IsNullOrWhiteSpace(token));
        string synthetic = "class X { void M() { var y = " + token + " ; } }";
        Assert.Contains(token, SetupCSharpLexicalScanner.ExtractCode(synthetic), StringComparison.Ordinal);
        Assert.DoesNotContain(
            token,
            SetupCSharpLexicalScanner.ExtractCode("// " + token + "\nclass Z { }"),
            StringComparison.Ordinal);
    }

    // ---- no exception text can escape -------------------------------------

    [Fact]
    public void No_exception_text_can_escape_the_observer()
    {
        string stripped = ObserverCode();

        Assert.Equal(0, Regex.Matches(stripped, @"catch\s*\(\s*[\w\.]+\s+\w").Count);
        Assert.Equal(1, Regex.Matches("try { } catch (ArgumentException ex) { }", @"catch\s*\(\s*[\w\.]+\s+\w").Count);

        foreach (string token in new[] { ".Message", "StackTrace", "FormatMessage", "GetLastPInvokeErrorMessage" })
        {
            Assert.DoesNotContain(token, stripped, StringComparison.Ordinal);
        }
    }

    // ---- no production call site -------------------------------------------

    private static readonly string[] ObserverTokens =
    {
        "ServiceOwnershipCredentialObserver",
        "ServiceOwnershipCredentialObserverInterpreter",
        "ServiceOwnershipCredentialObservationResult",
        "ServiceOwnershipCredentialObserverState",
        "ServiceOwnershipObservedNativeFacts",
    };

    private static string[] SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void No_production_source_file_outside_the_observer_calls_the_observer()
    {
        string[] files = SourceFiles(Path.Combine(RepoRoot(), "src"));
        Assert.True(files.Length >= 20, "expected the authored product sources, found " + files.Length);

        var offenders = new List<string>();
        foreach (string f in files)
        {
            if (string.Equals(f, ObserverPath(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(f));
            foreach (string token in ObserverTokens)
            {
                if (code.Contains(token, StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetFileName(f) + ":" + token);
                }
            }
        }

        Assert.Empty(offenders);

        // POSITIVE CONTROL: the observer file itself DOES contain every token.
        string ownCode = ObserverCode();
        foreach (string token in ObserverTokens)
        {
            Assert.Contains(token, ownCode, StringComparison.Ordinal);
        }
    }

    // ---- deferred-to-phase-2 completeness checks ---------------------------
    //
    // These are EXPECTED TO FAIL RED right now: the stub has no native call at
    // all. They describe what phase 2 must add, and must turn green then,
    // without this file being edited.

    // ---- CYCLE 50 REPAIR ----------------------------------------------------
    //
    // The cycle-49 guard counted the literal spelling `api + "("`, which only
    // matches a NO-SPACE call. Five of the six required declarations are
    // written WITH a space before the parenthesis, so the guard read 1 where
    // the true whitespace-tolerant total was 2 - it passed FOR A FORMATTING
    // REASON (see impl_evidence\evidence_p1_01_old_guard_evasion_red.log for
    // the demonstrated evasion). GetFinalPathNameByHandleW was additionally
    // ABSENT from the old guard list entirely, so its call count was never
    // constrained at all. The repaired guard below is whitespace-independent
    // and counts DECLARATIONS and INVOCATIONS SEPARATELY (see NativeCallGuard),
    // which is what lets it constrain GetFinalPathNameByHandleW correctly even
    // though its declaration (unlike the other five) has no space.
    [Fact]
    public void Phase_2_the_shim_reads_through_exactly_one_ordered_native_call_sequence()
    {
        string code = ObserverCode();
        foreach (string api in new[]
                 {
                     "NCryptOpenStorageProvider", "NCryptOpenKey", "NCryptGetProperty",
                     "CreateFileW", "GetFinalPathNameByHandleW", "GetKernelObjectSecurity",
                 })
        {
            NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(code, api);
            int independentTotal = NativeCallGuard.CountMatchesIndependently(code, api);

            Assert.Equal(1, counts.Declarations);
            Assert.Equal(1, counts.Invocations);
            Assert.Equal(independentTotal, counts.Total);
        }
    }

    // NCryptFreeObject is NOT one of the six required APIs above. Its TWO
    // invocations are CORRECT BY DESIGN - it frees the key handle and the
    // provider handle, both in the finally block - so it must never be given
    // a one-invocation assertion. Reconciled explicitly here so a future
    // reader does not "fix" the two calls down to one.
    [Fact]
    public void NCryptFreeObject_is_declared_once_and_correctly_invoked_twice_for_key_and_provider_handles()
    {
        string code = ObserverCode();
        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(code, "NCryptFreeObject");
        int independentTotal = NativeCallGuard.CountMatchesIndependently(code, "NCryptFreeObject");

        Assert.Equal(1, counts.Declarations);
        Assert.Equal(2, counts.Invocations); // correct: key handle + provider handle, both freed
        Assert.Equal(independentTotal, counts.Total);
    }

    // ---- REFLECTION - DECLARATIONS ONLY (never proves call-site count) -----
    //
    // Reflection can prove exactly one method of each name is declared, that
    // it is private/static/PinvokeImpl, and its DllImport/parameter/return
    // shape. It CANNOT count call sites - a call site is not metadata - so
    // invocation counting above remains source-scanning only.

    private static readonly Type ObserverReflectedType = typeof(ServiceOwnershipCredentialObserver);

    private static MethodInfo RequireExactlyOneDeclaredMethod(string name)
    {
        MethodInfo[] matches = ObserverReflectedType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name)
            .ToArray();
        return Assert.Single(matches);
    }

    private static void AssertPrivateStaticPInvoke(
        string apiName,
        string expectedDll,
        string? expectedEntryPoint,
        bool expectedSetLastError,
        Type expectedReturnType,
        Type[] expectedParameterTypes)
    {
        MethodInfo method = RequireExactlyOneDeclaredMethod(apiName);
        Assert.True(method.IsPrivate, apiName + " must be private");
        Assert.True(method.IsStatic, apiName + " must be static");
        Assert.True(
            (method.Attributes & MethodAttributes.PinvokeImpl) == MethodAttributes.PinvokeImpl,
            apiName + " must carry MethodAttributes.PinvokeImpl");

        DllImportAttribute? import = method.GetCustomAttribute<DllImportAttribute>();
        if (import is not null)
        {
            Assert.Equal(expectedDll, import.Value);
            Assert.Equal(expectedEntryPoint, import.EntryPoint);
            Assert.Equal(expectedSetLastError, import.SetLastError);
        }
        // else: DllImportAttribute reflection returned null on this runtime. The
        // PinvokeImpl assertion above still proves this is a genuine P/Invoke
        // declaration; dll/entry-point identity in that case is proven only by
        // the source-derived planner inventory, not by reflection, and that gap
        // is disclosed in the implementer report rather than silently ignored.

        Assert.Equal(expectedReturnType, method.ReturnType);
        ParameterInfo[] parameters = method.GetParameters();
        Assert.Equal(expectedParameterTypes.Length, parameters.Length);
        for (int i = 0; i < parameters.Length; i++)
        {
            Assert.Equal(expectedParameterTypes[i], parameters[i].ParameterType);
        }
    }

    // POSITIVE CONTROL, required before relying on DllImportAttribute reflection
    // anywhere below: DllImportAttribute is a PSEUDO-custom attribute stored as
    // metadata, and GetCustomAttribute<DllImportAttribute>() is not guaranteed
    // to reconstruct it on every runtime.
    [Fact]
    public void DllImportAttribute_reflection_positive_control_confirms_non_null_on_this_runtime()
    {
        MethodInfo method = RequireExactlyOneDeclaredMethod("NCryptFreeObject");
        DllImportAttribute? import = method.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(import);
        Assert.Equal("ncrypt.dll", import!.Value);
    }

    // Named to avoid the exact ordinal substrings "NCryptOpenStorageProvider" and
    // "NCryptOpenKey" appearing as C# IDENTIFIERS in this test file's own source -
    // The_marshalling_proof_never_touches_a_real_key_service_or_certificate_store
    // (below) scans this file's own scanned code for those exact tokens as a
    // boundary check, and identifiers (unlike string-literal arguments) are not
    // blanked by the lexical scanner.
    [Fact]
    public void The_ncrypt_open_storage_provider_pinvoke_declaration_matches_exactly_via_reflection()
    {
        AssertPrivateStaticPInvoke(
            "NCryptOpenStorageProvider", "ncrypt.dll", expectedEntryPoint: "NCryptOpenStorageProvider",
            expectedSetLastError: false,
            expectedReturnType: typeof(int),
            expectedParameterTypes: new[] { typeof(IntPtr).MakeByRefType(), typeof(string), typeof(int) });
    }

    [Fact]
    public void The_ncrypt_open_key_pinvoke_declaration_matches_exactly_via_reflection()
    {
        AssertPrivateStaticPInvoke(
            "NCryptOpenKey", "ncrypt.dll", expectedEntryPoint: "NCryptOpenKey", expectedSetLastError: false,
            expectedReturnType: typeof(int),
            expectedParameterTypes: new[]
            {
                typeof(IntPtr), typeof(IntPtr).MakeByRefType(), typeof(string), typeof(int), typeof(int),
            });
    }

    [Fact]
    public void NCryptGetProperty_declaration_matches_exactly_via_reflection()
    {
        AssertPrivateStaticPInvoke(
            "NCryptGetProperty", "ncrypt.dll", expectedEntryPoint: "NCryptGetProperty", expectedSetLastError: false,
            expectedReturnType: typeof(int),
            expectedParameterTypes: new[]
            {
                typeof(IntPtr), typeof(string), typeof(byte[]), typeof(int), typeof(int).MakeByRefType(), typeof(int),
            });
    }

    [Fact]
    public void CreateFileW_declaration_matches_exactly_via_reflection()
    {
        AssertPrivateStaticPInvoke(
            "CreateFileW", "kernel32.dll", expectedEntryPoint: "CreateFileW", expectedSetLastError: true,
            expectedReturnType: typeof(SafeFileHandle),
            expectedParameterTypes: new[]
            {
                typeof(string), typeof(uint), typeof(uint), typeof(IntPtr), typeof(uint), typeof(uint), typeof(IntPtr),
            });
    }

    [Fact]
    public void GetFinalPathNameByHandleW_declaration_matches_exactly_via_reflection()
    {
        AssertPrivateStaticPInvoke(
            "GetFinalPathNameByHandleW", "kernel32.dll", expectedEntryPoint: "GetFinalPathNameByHandleW",
            expectedSetLastError: true,
            expectedReturnType: typeof(int),
            expectedParameterTypes: new[] { typeof(SafeFileHandle), typeof(char[]), typeof(int), typeof(int) });
    }

    [Fact]
    public void GetKernelObjectSecurity_declaration_matches_exactly_via_reflection()
    {
        AssertPrivateStaticPInvoke(
            "GetKernelObjectSecurity", "advapi32.dll", expectedEntryPoint: "GetKernelObjectSecurity",
            expectedSetLastError: true,
            expectedReturnType: typeof(bool),
            expectedParameterTypes: new[]
            {
                typeof(SafeFileHandle), typeof(int), typeof(byte[]), typeof(int), typeof(int).MakeByRefType(),
            });
    }

    [Fact]
    public void Phase_2_every_native_handle_is_closed_and_every_temporary_buffer_is_cleared()
    {
        string body = MethodBody(
            ObserverCode(), "ServiceOwnershipCredentialObservationResult Observe(ServiceOwnershipLedgerEntry entry)");

        Assert.Contains("finally", body, StringComparison.Ordinal);
        Assert.Contains("Array.Clear(", body, StringComparison.Ordinal);
    }
}

// ===========================================================================
// TEST-OWNED NATIVE MARSHALLING PROOF - self-contained, no product dependency
// ===========================================================================
//
// WHAT THIS PROVES. That a handle-based GetKernelObjectSecurity read - the
// same shape of call phase 2's shim will use - marshals correctly on this
// host and that the returned bytes structurally parse via the certified
// contract's own parser.
//
// WHAT THIS DOES NOT DO. It never touches the machine-key root, never opens
// or creates a CNG key, never opens a certificate store, never registers or
// starts a service, and never calls the product observer. The ONE file it
// creates is a disposable, uniquely-named temporary file, deleted in a
// finally block, and its residue is asserted gone.
public sealed class ServiceOwnershipCredentialObserverNativeMarshallingTests
{
    private const int OwnerSecurityInformation = 0x1;
    private const int GroupSecurityInformation = 0x2;
    private const int DaclSecurityInformation = 0x4;

    [DllImport("advapi32.dll", EntryPoint = "GetKernelObjectSecurity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        SafeFileHandle handle,
        int securityInformation,
        byte[]? securityDescriptor,
        int length,
        out int lengthNeeded);

    [Fact]
    public void The_handle_based_descriptor_read_mirror_round_trips_on_a_test_owned_temporary_file()
    {
        const int securityInformation = OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation;
        string path = Path.Combine(Path.GetTempPath(), "pax-c49-observer-" + Guid.NewGuid().ToString("N") + ".tmp");
        Assert.False(File.Exists(path));

        try
        {
            using FileStream fs = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

            bool sized = GetKernelObjectSecurity(fs.SafeFileHandle, securityInformation, null, 0, out int needed);
            Assert.False(sized);
            Assert.True(needed > 0, "the sizing call was expected to report a positive required length");

            var buffer = new byte[needed];
            bool read = GetKernelObjectSecurity(fs.SafeFileHandle, securityInformation, buffer, buffer.Length, out int actualNeeded);
            Assert.True(read);
            Assert.True(actualNeeded > 0 && actualNeeded <= buffer.Length);

            bool parsed = ServiceOwnershipLedgerContract.TryParseFileSecurityDescriptor(
                buffer, out ServiceOwnershipParsedFileSecurityDescriptor? descriptor);
            Assert.True(parsed, "the default descriptor for a freshly created temp file was expected to parse structurally");
            Assert.NotNull(descriptor);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public void The_marshalling_proof_never_touches_a_real_key_service_or_certificate_store()
    {
        string path = Path.Combine(
            ServiceSidResolverStructuralTests.RepoRoot(),
            "tests", "PAXCookbookSetup.Tests", "Service", "ServiceOwnershipCredentialObserverTests.cs");
        Assert.True(File.Exists(path), "this test file was expected to exist at its authored path");

        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(path));
        foreach (string token in new[]
                 {
                     "NCryptOpenStorageProvider", "NCryptOpenKey", "X509Store", "ServiceController",
                     "OpenSCManager", "ServiceOwnershipCredentialObserver.Observe(",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }
    }
}

// ===========================================================================
// NATIVE CALL GUARD (cycle 50) - whitespace-independent, declaration-aware
// ===========================================================================
//
// Repairs the cycle-49 defect: a guard that counted the literal spelling
// `api + "("` was evaded by any P/Invoke declaration or call site written
// with whitespace (space, tab, or newline) before the opening parenthesis,
// because the production declarations in ServiceOwnershipCredentialObserver.cs
// are written WITH a space while the counted spelling required NONE.
//
// This guard runs on code ALREADY put through
// SetupCSharpLexicalScanner.ExtractCode (declared in ServiceSidResolverTests.cs
// in this same test project), which already removes line/block comments and
// blanks the content of char literals, ordinary/verbatim/interpolated/raw
// strings - including a DllImport EntryPoint argument string, which is exactly
// why that token never counts as an invocation. No second comment/string
// stripper is added here.
//
// For a given identifier, over scanned code, this guard:
//   - matches an EXACT identifier token (the character immediately before and
//     immediately after must not be a C# identifier character), so
//     `ApiExtra(` and `XxxApi(` never match `Api`;
//   - allows ZERO OR MORE whitespace of any kind between the identifier and
//     the opening parenthesis;
//   - classifies each match as a DECLARATION or an INVOCATION by walking
//     BACKWARDS to the nearest ';', '{', '}' (or start of file) and checking
//     whether that prefix contains the STANDALONE token "extern".
// This is formatting-independent because it depends on token structure, not
// on line breaks or spacing.
internal static class NativeCallGuard
{
    internal readonly struct CallSiteCounts
    {
        internal CallSiteCounts(int declarations, int invocations)
        {
            Declarations = declarations;
            Invocations = invocations;
        }

        internal int Declarations { get; }

        internal int Invocations { get; }

        internal int Total => Declarations + Invocations;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Finds the next exact-token occurrence of `identifier` at or after `from`
    // that is followed by optional whitespace then '('. Returns the index of
    // the first character of the identifier match, or -1 if none remain.
    private static int FindNextCallSite(string code, string identifier, int from)
    {
        int index = from;
        while (true)
        {
            int found = code.IndexOf(identifier, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return -1;
            }

            index = found + 1; // always make forward progress even on a rejected candidate

            bool boundaryBefore = found == 0 || !IsIdentifierChar(code[found - 1]);
            int afterIdentifier = found + identifier.Length;
            bool boundaryAfter = afterIdentifier >= code.Length || !IsIdentifierChar(code[afterIdentifier]);
            if (!boundaryBefore || !boundaryAfter)
            {
                continue;
            }

            int cursor = afterIdentifier;
            while (cursor < code.Length && char.IsWhiteSpace(code[cursor]))
            {
                cursor++;
            }
            if (cursor < code.Length && code[cursor] == '(')
            {
                return found;
            }
        }
    }

    // CYCLE 50 REPAIR (defect 1). Counts total exact-token
    // identifier-then-optional-whitespace-then-'(' matches by walking the SAME
    // FindNextCallSite enumerator that CountCallSites below also walks. This is
    // NOT an independent cross-check: CountCallSites classifies every match this
    // method visits as exactly one of declaration or invocation, so
    // CountCallSites(...).Total always equals this method's result by
    // definition, for any input - comparing them proves nothing. This method is
    // used directly by the boundary/whitespace/reject controls below, where a
    // single count from the guard's own logic is exactly what is under test.
    // For a reconciliation that is a genuine cross-check against a SEPARATE
    // implementation, see CountMatchesIndependently.
    internal static int CountMatches(string code, string identifier)
    {
        int count = 0;
        int index = 0;
        int found;
        while ((found = FindNextCallSite(code, identifier, index)) >= 0)
        {
            count++;
            index = found + identifier.Length;
        }
        return count;
    }

    // CYCLE 50 REPAIR (defect 1). Genuinely independent reconciliation counter:
    // implemented with .NET Regex lookaround boundary assertions instead of
    // FindNextCallSite, so it shares NO code path with CountCallSites/
    // CountMatches above. Comparing this method's result against
    // CountCallSites(...).Total is a real cross-check between two independently
    // written implementations of "count exact-token identifier then optional
    // whitespace then '('" - not a definitional identity. See
    // NativeCallGuardTests.The_reconciliation_against_a_deliberately_wrong_independent_counter_detects_the_mismatch
    // for proof that this comparison can actually fail.
    internal static int CountMatchesIndependently(string code, string identifier)
    {
        string pattern = @"(?<![A-Za-z0-9_])" + Regex.Escape(identifier) + @"(?![A-Za-z0-9_])\s*\(";
        return Regex.Matches(code, pattern).Count;
    }

    private static bool ContainsStandaloneToken(string text, string token)
    {
        int index = 0;
        while (true)
        {
            int found = text.IndexOf(token, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            bool before = found == 0 || !IsIdentifierChar(text[found - 1]);
            int after = found + token.Length;
            bool afterOk = after >= text.Length || !IsIdentifierChar(text[after]);
            if (before && afterOk)
            {
                return true;
            }

            index = found + 1;
        }
    }

    private static bool IsDeclaration(string code, int identifierStart)
    {
        int i = identifierStart - 1;
        while (i >= 0 && code[i] != ';' && code[i] != '{' && code[i] != '}')
        {
            i--;
        }

        string prefix = code.Substring(i + 1, identifierStart - (i + 1));
        return ContainsStandaloneToken(prefix, "extern");
    }

    internal static CallSiteCounts CountCallSites(string code, string identifier)
    {
        int declarations = 0;
        int invocations = 0;
        int index = 0;
        int found;
        while ((found = FindNextCallSite(code, identifier, index)) >= 0)
        {
            if (IsDeclaration(code, found))
            {
                declarations++;
            }
            else
            {
                invocations++;
            }

            index = found + identifier.Length;
        }

        return new CallSiteCounts(declarations, invocations);
    }
}

// ===========================================================================
// NATIVE CALL GUARD - DISCRIMINATING CONTROLS AND REGRESSION WITNESS (cycle 50)
// ===========================================================================
//
// Proves the guard both DETECTS what it must detect and REJECTS what it must
// reject, over synthetic sources that never touch the real observer. Also
// carries a PERMANENT regression witness proving the OLD cycle-49 tight
// counter is fooled by whitespace on a SYNTHETIC source (so the witness never
// depends on the real observer's current formatting) while the repaired
// guard is not.
public sealed class NativeCallGuardTests
{
    private static string Scan(string source) => SetupCSharpLexicalScanner.ExtractCode(source);

    // Mirrors the OLD cycle-49 counter EXACTLY, so the regression witness below
    // exercises the real historical defect and not a strawman.
    private static int OldTightOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    // ---- DETECT: whitespace tolerance on invocations -----------------------

    [Theory]
    [InlineData("class X { void M() { Api(); } }")] // Api()
    [InlineData("class X { void M() { Api (); } }")] // Api ()
    [InlineData("class X { void M() { Api\t(); } }")] // TAB before paren
    [InlineData("class X { void M() { Api\n(); } }")] // NEWLINE before paren
    public void The_guard_detects_a_single_invocation_regardless_of_whitespace_before_the_parenthesis(string source)
    {
        string code = Scan(source);
        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(code, "Api");
        Assert.Equal(0, counts.Declarations);
        Assert.Equal(1, counts.Invocations);
        Assert.Equal(NativeCallGuard.CountMatches(code, "Api"), counts.Total);
    }

    // ---- DETECT: declarations, with and without whitespace ------------------

    [Theory]
    [InlineData("private static extern int Api(int x);")] // no whitespace
    [InlineData("private static extern int Api (int x);")] // with whitespace
    public void The_guard_detects_a_single_declaration_regardless_of_whitespace_before_the_parenthesis(string source)
    {
        string code = Scan(source);
        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(code, "Api");
        Assert.Equal(1, counts.Declarations);
        Assert.Equal(0, counts.Invocations);
        Assert.Equal(NativeCallGuard.CountMatches(code, "Api"), counts.Total);
    }

    // ---- DETECT: a second call using alternate whitespace is still counted -

    [Fact]
    public void The_guard_counts_two_invocations_when_the_second_call_uses_different_whitespace()
    {
        string code = Scan("private static extern int Api(int x); class X { void M() { Api(1); Api (2); } }");
        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(code, "Api");
        Assert.Equal(1, counts.Declarations);
        Assert.Equal(2, counts.Invocations);
        Assert.Equal(NativeCallGuard.CountMatches(code, "Api"), counts.Total);
    }

    // ---- REJECT: exact identifier-token boundaries ---------------------------

    [Fact]
    public void ApiExtra_must_not_match_Api()
    {
        string code = Scan("class X { void M() { ApiExtra(); } }");
        Assert.Equal(0, NativeCallGuard.CountMatches(code, "Api"));
    }

    [Fact]
    public void XxxApi_must_not_match_Api()
    {
        string code = Scan("class X { void M() { XxxApi(); } }");
        Assert.Equal(0, NativeCallGuard.CountMatches(code, "Api"));
    }

    // ---- REJECT: comment, string, char-literal content never counts ---------

    [Fact]
    public void The_api_text_inside_a_line_comment_must_not_count()
    {
        string code = Scan("// Api()\nclass X { }");
        Assert.Equal(0, NativeCallGuard.CountMatches(code, "Api"));
    }

    [Fact]
    public void The_api_text_inside_a_string_literal_must_not_count()
    {
        string code = Scan("class X { void M() { var s = \"Api()\"; } }");
        Assert.Equal(0, NativeCallGuard.CountMatches(code, "Api"));
    }

    [Fact]
    public void The_api_text_inside_a_char_literal_must_not_count()
    {
        // Not valid C# (a char literal holds one character), but the lexical
        // scanner is a text-level tokenizer, not a compiler: it blanks
        // everything between an opening and matching closing single quote,
        // which is exactly the boundary this control must prove is honored.
        string code = Scan("class X { void M() { char c = 'Api('; } }");
        Assert.Equal(0, NativeCallGuard.CountMatches(code, "Api"));
    }

    // ---- DEMONSTRATE: both polarities over a correct shape -------------------

    private const string CorrectShape =
        "private static extern int Api(int x); class X { void M() { Api(1); } }";

    [Fact]
    public void Correct_shape_passes_the_exact_one_declaration_and_exact_one_invocation_assertion()
    {
        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(Scan(CorrectShape), "Api");
        Assert.Equal(1, counts.Declarations);
        Assert.Equal(1, counts.Invocations);
    }

    [Fact]
    public void One_added_invocation_fails_the_exact_one_invocation_assertion()
    {
        string mutated = CorrectShape.Replace("Api(1); }", "Api(1); Api(2); }");
        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(Scan(mutated), "Api");
        Assert.Equal(1, counts.Declarations);
        Assert.Equal(2, counts.Invocations);
        Assert.NotEqual(1, counts.Invocations); // this is what would fail a real guard assertion
    }

    [Fact]
    public void One_removed_invocation_fails_the_exact_one_invocation_assertion()
    {
        string mutated = CorrectShape.Replace("void M() { Api(1); }", "void M() { }");
        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(Scan(mutated), "Api");
        Assert.Equal(1, counts.Declarations);
        Assert.Equal(0, counts.Invocations);
        Assert.NotEqual(1, counts.Invocations);
    }

    [Fact]
    public void A_duplicate_declaration_fails_the_exact_one_declaration_assertion()
    {
        string mutated = "private static extern int Api(int x); private static extern int Api(int y); "
            + "class X { void M() { Api(1); } }";
        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(Scan(mutated), "Api");
        Assert.Equal(2, counts.Declarations);
        Assert.Equal(1, counts.Invocations);
        Assert.NotEqual(1, counts.Declarations);
    }

    [Fact]
    public void Reformatting_alone_changes_nothing()
    {
        string reformatted = "private static extern int Api (int x);\r\nclass X { void M() { Api\t(1); } }";
        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(Scan(reformatted), "Api");
        Assert.Equal(1, counts.Declarations);
        Assert.Equal(1, counts.Invocations);
    }

    // ---- PERMANENT REGRESSION WITNESS ---------------------------------------
    //
    // On a SYNTHETIC source (never the real observer, so this witness never
    // depends on the observer's current formatting): mirrors the REAL cycle-49
    // defect shape exactly - a declaration written WITH a space (evades the old
    // tight "Api(" spelling), an original invocation written WITHOUT a space
    // (the only thing the old counter ever saw), and a SECOND invocation - the
    // one that was silently added - ALSO written WITH a space, so the old
    // counter cannot see it either.
    [Fact]
    public void The_old_tight_counter_is_fooled_by_whitespace_while_the_repaired_guard_is_not()
    {
        string source = "private static extern int Api (int x); "
            + "class X { void M() { Api(1); Api (2); } }";
        string code = Scan(source);

        int oldCount = OldTightOccurrences(code, "Api" + "(");
        Assert.Equal(1, oldCount); // OLD counter: reads 1, unaware a second call exists - FOOLED

        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(code, "Api");
        Assert.Equal(1, counts.Declarations);
        Assert.Equal(2, counts.Invocations); // REPAIRED guard: sees both calls - NOT FOOLED
        Assert.NotEqual(1, counts.Invocations);
    }

    // ---- CYCLE 50 REPAIR (defect 1) - CountMatchesIndependently itself -------
    //
    // CountMatchesIndependently must be trustworthy on its own before it can
    // serve as a reconciliation partner: prove it agrees with CountMatches on
    // the exact whitespace-evasion shape that fooled the retired cycle-49
    // counter, and that it honors the same exact-token identifier boundary.

    [Fact]
    public void CountMatchesIndependently_counts_whitespace_separated_calls_that_the_old_counter_missed()
    {
        string source = "private static extern int Api (int x); "
            + "class X { void M() { Api(1); Api (2); } }";
        string code = Scan(source);

        Assert.Equal(3, NativeCallGuard.CountMatchesIndependently(code, "Api"));
        Assert.Equal(3, NativeCallGuard.CountMatches(code, "Api"));
    }

    [Fact]
    public void CountMatchesIndependently_respects_exact_identifier_boundaries()
    {
        string code = Scan("class X { void M() { ApiExtra(); XxxApi(); Api(); } }");
        Assert.Equal(1, NativeCallGuard.CountMatchesIndependently(code, "Api"));
    }

    // ---- CYCLE 50 REPAIR (defect 1) - PROVE THE RECONCILIATION CAN FAIL ------
    //
    // The reconciliation assertion used in the observer's reads-through-exactly-
    // one-call-sequence test (Assert.Equal(independentTotal, counts.Total))
    // is only a real cross-check if it is CAPABLE of failing. This feeds that
    // exact comparison a DELIBERATELY WRONG independent counter - the retired
    // cycle-49 tight counter, which silently misses whitespace-separated calls
    // - and proves the mismatch IS detected. Without this test, swapping
    // CountMatches for CountMatchesIndependently would be an unproven claim of
    // independence rather than a demonstrated one.
    [Fact]
    public void The_reconciliation_against_a_deliberately_wrong_independent_counter_detects_the_mismatch()
    {
        string source = "private static extern int Api (int x); "
            + "class X { void M() { Api(1); Api (2); } }";
        string code = Scan(source);

        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(code, "Api");
        int wrongIndependentTotal = OldTightOccurrences(code, "Api" + "("); // deliberately WRONG: misses whitespace-separated calls

        // The genuinely independent counter agrees with counts.Total...
        Assert.Equal(NativeCallGuard.CountMatchesIndependently(code, "Api"), counts.Total);
        // ...but a wrong counter does not: the reconciliation assertion pattern
        // is falsifiable, not a tautology.
        Assert.NotEqual(wrongIndependentTotal, counts.Total);
    }
}
