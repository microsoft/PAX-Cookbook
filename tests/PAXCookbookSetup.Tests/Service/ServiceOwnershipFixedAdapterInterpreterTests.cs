using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 92 PASS B - EXHAUSTIVE BEHAVIOURAL TESTS FOR THE PURE INTERPRETERS
// ===========================================================================
//
// WHAT THIS FILE COVERS. Every decision the pass-B adapters can take WITHOUT
// live native state. Each interpreter is a pure function, so each of its
// branches is driven directly by synthetic bounded facts.
//
// WHAT THIS FILE DELIBERATELY DOES NOT DO. It NEVER invokes a fixed shim. No
// test here opens a certificate store, opens or reopens a machine private key,
// reads a real security descriptor or mutates a real ACL. The shims are covered
// STRUCTURALLY in ServiceOwnershipFixedAdapterStructuralTests, and that file
// proves the absence of shim invocation across this whole test project rather
// than leaving it to a promise.
//
// The one filesystem this file touches is an OS-temp sandbox for the
// promoted-Recipe adapter, exactly as the certified store suite already does.
// That is not machine state: no ProgramData, no certificate store, no ACL.
public sealed class ServiceOwnershipFixedAdapterInterpreterTests
{
    // =======================================================================
    // OWNER IDENTITY - derived ONLY from the validated installation anchor
    // =======================================================================

    /// <summary>
    /// CYCLE 96. The observed pair and the constructor-fixed pair default to the
    /// SAME values, so these long-standing cases keep testing exactly what they
    /// always tested - the anchor's presence and the SID's shape - while the new
    /// substitution cases live in ServiceOwnershipOwnerContinuityTests.
    /// </summary>
    private static ServiceOwnershipObservedAnchorFacts AnchorFacts(
        bool validated = true,
        bool accepted = true,
        bool documentPresent = true,
        string? sid = ServiceOwnershipPromotionFixtures.OwnerSid) =>
        new(validated, accepted, documentPresent,
            ServiceOwnershipPromotionFixtures.InstallId, sid,
            ServiceOwnershipPromotionFixtures.InstallId, sid);

    [Fact]
    public void The_owner_is_the_sid_the_validated_anchor_itself_recorded()
    {
        ServiceOwnershipOwnerIdentityObservation observed =
            ServiceOwnershipOwnerIdentityInterpreter.Interpret(AnchorFacts());

        Assert.Equal(ServiceOwnershipOwnerIdentityState.Observed, observed.State);
        Assert.True(observed.IsObserved);
        Assert.Equal(ServiceOwnershipPromotionFixtures.OwnerSid, observed.OwningUserSid);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void An_anchor_that_is_not_validated_accepted_and_present_yields_no_owner(
        bool validated, bool accepted, bool documentPresent)
    {
        ServiceOwnershipOwnerIdentityObservation observed =
            ServiceOwnershipOwnerIdentityInterpreter.Interpret(
                AnchorFacts(validated, accepted, documentPresent));

        Assert.Equal(ServiceOwnershipOwnerIdentityState.Unavailable, observed.State);
        Assert.False(observed.IsObserved);
        Assert.Equal(string.Empty, observed.OwningUserSid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_anchor_carrying_no_sid_is_unavailable_rather_than_misshapen(string? sid)
    {
        Assert.Equal(
            ServiceOwnershipOwnerIdentityState.Unavailable,
            ServiceOwnershipOwnerIdentityInterpreter.Interpret(AnchorFacts(sid: sid)).State);
    }

    [Theory]
    [InlineData("S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890")] // a service SID
    [InlineData("S-1-5-32-544")] // BUILTIN\Administrators
    [InlineData("S-1-5-18")] // LOCAL SYSTEM
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-1-0")]
    [InlineData("NT AUTHORITY\\SYSTEM")]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-21")]
    public void A_sid_that_is_not_a_user_owner_is_refused_as_not_user_shaped(string sid)
    {
        ServiceOwnershipOwnerIdentityObservation observed =
            ServiceOwnershipOwnerIdentityInterpreter.Interpret(AnchorFacts(sid: sid));

        Assert.Equal(ServiceOwnershipOwnerIdentityState.NotUserShaped, observed.State);
        Assert.Equal(string.Empty, observed.OwningUserSid);
    }

    [Fact]
    public void A_default_anchor_observation_never_reads_as_an_owner()
    {
        ServiceOwnershipOwnerIdentityObservation observed =
            ServiceOwnershipOwnerIdentityInterpreter.Interpret(default);

        Assert.False(observed.IsObserved);
        Assert.NotEqual(ServiceOwnershipOwnerIdentityState.Observed, observed.State);
    }

    // =======================================================================
    // CERTIFICATE FACTS - one store, one match, one provider, one key size
    // =======================================================================

    private static ServiceOwnershipObservedCertificateFacts CertFacts(
        int matchCount = 1,
        string? observedThumbprint = ServiceOwnershipPromotionFixtures.Thumb,
        bool privateKeyAccessible = true,
        bool privateKeyIsRsaCng = true,
        bool machineScopedKey = true,
        int keySizeBits = 2048,
        string? providerName = "Microsoft Software Key Storage Provider",
        string? keyIdentity = ServiceOwnershipPromotionFixtures.KeyId,
        string? providerUniqueName = ServiceOwnershipPromotionFixtures.ProviderUniqueNameValue) =>
        new(matchCount, observedThumbprint, privateKeyAccessible, privateKeyIsRsaCng,
            machineScopedKey, keySizeBits, providerName, keyIdentity, providerUniqueName);

    private static ServiceOwnershipCertificateFacts InterpretCert(
        ServiceOwnershipObservedCertificateFacts facts) =>
        ServiceOwnershipCertificateFactsInterpreter.Interpret(
            ServiceOwnershipPromotionFixtures.Thumb, facts);

    [Fact]
    public void The_one_eligible_certificate_produces_exactly_the_certified_bounded_facts()
    {
        ServiceOwnershipCertificateFacts facts = InterpretCert(CertFacts());

        Assert.Equal(ServiceOwnershipCertificateFactsState.Observed, facts.State);
        Assert.True(facts.IsObserved);
        Assert.Equal(ServiceOwnershipPromotionFixtures.Thumb, facts.ThumbprintSha1);
        Assert.Equal(ServiceOwnershipCredentialKind.PersonalAppRegistrationCertificate, facts.CredentialKind);
        Assert.Equal(ServiceOwnershipProvenance.Referenced, facts.Provenance);
        Assert.Equal(ServiceOwnershipLedgerContract.ApprovedRightsProfileId, facts.RightsProfileId);
        Assert.Equal(ServiceOwnershipLedgerContract.ApprovedRightsProfileGrantMechanism, facts.GrantMechanism);

        Assert.True(facts.Key.IsUsable);
        Assert.Equal(ServiceOwnershipPromotionFixtures.KeyId, facts.Key.KeyIdentity);
        Assert.Equal(ServiceOwnershipPromotionFixtures.ProviderUniqueNameValue, facts.Key.ProviderUniqueName);
        Assert.Equal(
            ServiceOwnershipLedgerContract.ApprovedRightsProfileProviderKind, facts.Key.ProviderKind);
        Assert.Equal(
            ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            facts.Key.KeyStorageRoot);
        Assert.Equal(
            ServiceOwnershipLedgerContract.ApprovedDescriptorFormat, facts.Key.DescriptorFormat);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef01")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF012")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0Z")]
    public void A_requested_thumbprint_that_is_not_a_normalized_sha1_never_reaches_a_match(string? requested)
    {
        ServiceOwnershipCertificateFacts facts =
            ServiceOwnershipCertificateFactsInterpreter.Interpret(requested, CertFacts());

        Assert.Equal(ServiceOwnershipCertificateFactsState.NotFound, facts.State);
        Assert.False(facts.IsObserved);
    }

    [Fact]
    public void Zero_matches_is_not_found()
    {
        Assert.Equal(
            ServiceOwnershipCertificateFactsState.NotFound,
            InterpretCert(CertFacts(matchCount: 0)).State);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(64)]
    public void More_than_one_match_is_ambiguous_and_is_never_resolved_by_picking_one(int matches)
    {
        Assert.Equal(
            ServiceOwnershipCertificateFactsState.Ambiguous,
            InterpretCert(CertFacts(matchCount: matches)).State);
    }

    [Fact]
    public void Ambiguity_outranks_every_later_gate()
    {
        // Two matches AND no usable key: the duplicate must still be the reported
        // reason, because there is no single certificate to have judged.
        Assert.Equal(
            ServiceOwnershipCertificateFactsState.Ambiguous,
            InterpretCert(CertFacts(
                matchCount: 2, privateKeyAccessible: false, keySizeBits: 4096,
                providerName: "somewhere else")).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000000000000000000000000000000000000000")]
    public void A_match_whose_observed_thumbprint_is_not_the_requested_one_is_not_found(string? observed)
    {
        Assert.Equal(
            ServiceOwnershipCertificateFactsState.NotFound,
            InterpretCert(CertFacts(observedThumbprint: observed)).State);
    }

    [Fact]
    public void An_inaccessible_private_key_is_reported_as_a_private_key_failure()
    {
        Assert.Equal(
            ServiceOwnershipCertificateFactsState.PrivateKeyUnavailable,
            InterpretCert(CertFacts(privateKeyAccessible: false)).State);
    }

    [Fact]
    public void A_key_that_is_not_cng_rsa_is_an_unsupported_provider()
    {
        Assert.Equal(
            ServiceOwnershipCertificateFactsState.UnsupportedProvider,
            InterpretCert(CertFacts(privateKeyIsRsaCng: false)).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Microsoft Enhanced RSA and AES Cryptographic Provider")]
    [InlineData("Microsoft Platform Crypto Provider")]
    [InlineData("Microsoft Smart Card Key Storage Provider")]
    [InlineData("microsoft software key storage provider")]
    [InlineData("Microsoft Software Key Storage Provider ")]
    public void Any_provider_but_the_one_approved_provider_is_refused(string? providerName)
    {
        Assert.Equal(
            ServiceOwnershipCertificateFactsState.UnsupportedProvider,
            InterpretCert(CertFacts(providerName: providerName)).State);
    }

    [Fact]
    public void A_key_that_is_not_machine_scoped_is_refused_and_never_falls_back_to_the_user()
    {
        Assert.Equal(
            ServiceOwnershipCertificateFactsState.UnsupportedProvider,
            InterpretCert(CertFacts(machineScopedKey: false)).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    [InlineData(2047)]
    [InlineData(2049)]
    [InlineData(3072)]
    [InlineData(4096)]
    public void Any_key_size_but_exactly_two_thousand_forty_eight_bits_is_refused(int bits)
    {
        Assert.Equal(
            ServiceOwnershipCertificateFactsState.UnsupportedProvider,
            InterpretCert(CertFacts(keySizeBits: bits)).State);

        // The bound is EXACT, not a minimum.
        Assert.Equal(2048, ServiceOwnershipCertificateFactsInterpreter.ApprovedKeySizeBits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".leading-dot")]
    [InlineData("has..traversal")]
    [InlineData("has/separator")]
    [InlineData("has\\separator")]
    [InlineData("has space")]
    public void An_unsafe_key_identity_is_refused(string? keyIdentity)
    {
        Assert.Equal(
            ServiceOwnershipCertificateFactsState.UnsupportedProvider,
            InterpretCert(CertFacts(keyIdentity: keyIdentity)).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".leading-dot")]
    [InlineData("trailing-dot.")]
    [InlineData("has..traversal")]
    [InlineData("c:stream")]
    [InlineData("CON")]
    [InlineData("NUL.pvk")]
    public void An_unsafe_provider_unique_name_is_refused(string? uniqueName)
    {
        Assert.Equal(
            ServiceOwnershipCertificateFactsState.UnsupportedProvider,
            InterpretCert(CertFacts(providerUniqueName: uniqueName)).State);
    }

    [Fact]
    public void A_default_certificate_observation_never_reads_as_observed()
    {
        ServiceOwnershipCertificateFacts facts =
            ServiceOwnershipCertificateFactsInterpreter.Interpret(
                ServiceOwnershipPromotionFixtures.Thumb, default);

        Assert.False(facts.IsObserved);
        Assert.Equal(ServiceOwnershipCertificateFactsState.NotFound, facts.State);
    }

    // =======================================================================
    // PRIOR DESCRIPTOR CAPTURE - read only, owner/group/DACL only
    // =======================================================================

    private static ServiceOwnershipObservedKeyAccessFacts AccessFacts(
        bool keyOpened = true,
        bool containmentProven = true,
        bool reparsePointPresent = false,
        bool handleIdentityProven = true,
        bool saclRequested = false,
        bool descriptorReadComplete = true,
        byte[]? bytes = null) =>
        new(keyOpened, containmentProven, reparsePointPresent, handleIdentityProven,
            saclRequested, descriptorReadComplete,
            bytes ?? ServiceOwnershipPromotionFixtures.PriorBytes);

    private static byte[] PostGrantBytes()
    {
        Assert.True(ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            ServiceOwnershipPromotionFixtures.PriorBytes,
            ServiceOwnershipPromotionFixtures.SvcSid,
            out byte[] postGrant, out _, out _));
        return postGrant;
    }

    /// <summary>
    /// CYCLE 96. The captured snapshot for an arbitrary byte sequence, so each
    /// approved-descriptor case below can keep testing the exact thing it always
    /// tested rather than tripping the new snapshot-equality gate first.
    /// </summary>
    private static ServiceOwnershipCapturedPriorDescriptor CapturedOf(byte[] bytes)
    {
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            "present", out ServiceOwnershipPriorDaclState present));

        return ServiceOwnershipCapturedPriorDescriptor.Captured(
            present,
            Convert.ToBase64String(bytes),
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    [Fact]
    public void A_supported_prior_descriptor_is_captured_exactly_as_the_ledger_records_it()
    {
        ServiceOwnershipCapturedPriorDescriptor captured =
            ServiceOwnershipPriorDescriptorInterpreter.Interpret(
                ServiceOwnershipPromotionFixtures.KeyHandle(), AccessFacts());

        Assert.Equal(ServiceOwnershipPriorDescriptorState.Captured, captured.State);
        Assert.True(captured.IsCaptured);
        Assert.Equal(ServiceOwnershipPriorDaclState.Present, captured.DaclState);
        Assert.Equal(ServiceOwnershipPromotionFixtures.PriorB64, captured.BytesBase64);
        Assert.Equal(ServiceOwnershipPromotionFixtures.PriorSha256, captured.Sha256);
    }

    [Fact]
    public void An_unusable_key_handle_captures_nothing()
    {
        Assert.Equal(
            ServiceOwnershipPriorDescriptorState.Unavailable,
            ServiceOwnershipPriorDescriptorInterpreter.Interpret(default, AccessFacts()).State);
    }

    [Fact]
    public void Requesting_sacl_access_is_always_a_refusal()
    {
        Assert.Equal(
            ServiceOwnershipPriorDescriptorState.Unavailable,
            ServiceOwnershipPriorDescriptorInterpreter.Interpret(
                ServiceOwnershipPromotionFixtures.KeyHandle(),
                AccessFacts(saclRequested: true)).State);
    }

    [Theory]
    [InlineData(false, true, false, true, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, false, true, false)]
    public void Any_unproven_native_precondition_captures_nothing(
        bool keyOpened, bool containment, bool reparse, bool identity, bool readComplete)
    {
        Assert.Equal(
            ServiceOwnershipPriorDescriptorState.Unavailable,
            ServiceOwnershipPriorDescriptorInterpreter.Interpret(
                ServiceOwnershipPromotionFixtures.KeyHandle(),
                AccessFacts(keyOpened, containment, reparse, identity, false, readComplete)).State);
    }

    [Fact]
    public void A_missing_or_empty_descriptor_read_captures_nothing()
    {
        Assert.Equal(
            ServiceOwnershipPriorDescriptorState.Unavailable,
            ServiceOwnershipPriorDescriptorInterpreter.Interpret(
                ServiceOwnershipPromotionFixtures.KeyHandle(),
                AccessFacts(bytes: Array.Empty<byte>())).State);

        var nullFacts = new ServiceOwnershipObservedKeyAccessFacts(
            true, true, false, true, false, true, null);
        Assert.Equal(
            ServiceOwnershipPriorDescriptorState.Unavailable,
            ServiceOwnershipPriorDescriptorInterpreter.Interpret(
                ServiceOwnershipPromotionFixtures.KeyHandle(), nullFacts).State);
    }

    [Fact]
    public void A_descriptor_larger_than_the_certified_bound_is_an_unsupported_shape()
    {
        var oversized = new byte[ServiceOwnershipLedgerContract.MaxPriorDaclDecodedBytes + 1];
        Buffer.BlockCopy(
            ServiceOwnershipPromotionFixtures.PriorBytes, 0, oversized, 0,
            ServiceOwnershipPromotionFixtures.PriorBytes.Length);

        Assert.Equal(
            ServiceOwnershipPriorDescriptorState.UnsupportedShape,
            ServiceOwnershipPriorDescriptorInterpreter.Interpret(
                ServiceOwnershipPromotionFixtures.KeyHandle(),
                AccessFacts(bytes: oversized)).State);
    }

    [Fact]
    public void A_malformed_or_unsupported_descriptor_is_an_unsupported_shape()
    {
        Assert.Equal(
            ServiceOwnershipPriorDescriptorState.UnsupportedShape,
            ServiceOwnershipPriorDescriptorInterpreter.Interpret(
                ServiceOwnershipPromotionFixtures.KeyHandle(),
                AccessFacts(bytes: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })).State);
    }

    [Fact]
    public void A_descriptor_that_already_carries_a_service_ace_is_refused_by_its_own_reason()
    {
        Assert.Equal(
            ServiceOwnershipPriorDescriptorState.ServiceAceAlreadyPresent,
            ServiceOwnershipPriorDescriptorInterpreter.Interpret(
                ServiceOwnershipPromotionFixtures.KeyHandle(),
                AccessFacts(bytes: PostGrantBytes())).State);
    }

    // =======================================================================
    // APPROVED DESCRIPTOR - built ONLY by the certified contract
    // =======================================================================

    [Fact]
    public void The_approved_descriptor_is_exactly_what_the_certified_contract_builds()
    {
        Assert.True(ServiceOwnershipApprovedDescriptorInterpreter.TryPlanApprovedDescriptor(
            ServiceOwnershipPromotionFixtures.GrantPlan(),
            ServiceOwnershipPromotionFixtures.Captured(), AccessFacts(),
            out byte[] approved, out ServiceOwnershipDescriptorApplyState refusal));

        Assert.Equal(PostGrantBytes(), approved);
        Assert.Equal(ServiceOwnershipDescriptorApplyState.Applied, refusal);

        // EXACTLY ONE new ACE, for EXACTLY the approved service SID, with EXACTLY
        // the certified mask - and every prior fact preserved.
        Assert.True(ServiceOwnershipLedgerContract.TryParseFileSecurityDescriptor(
            ServiceOwnershipPromotionFixtures.PriorBytes,
            out ServiceOwnershipParsedFileSecurityDescriptor? prior));
        Assert.True(ServiceOwnershipLedgerContract.TryParseFileSecurityDescriptor(
            approved, out ServiceOwnershipParsedFileSecurityDescriptor? post));

        Assert.Equal(prior!.Aces.Count + 1, post!.Aces.Count);
        Assert.Equal(prior.OwnerSid, post.OwnerSid);
        Assert.Equal(prior.GroupSid, post.GroupSid);
        Assert.Equal(prior.ControlFlags, post.ControlFlags);
        Assert.False(post.SaclPresent);

        ServiceOwnershipParsedAce added = Assert.Single(
            post.Aces.Where(a => a.Sid == ServiceOwnershipPromotionFixtures.SvcSid));
        Assert.Equal(ServiceOwnershipLedgerContract.ApprovedRightsProfileMask, added.Mask);
        Assert.Equal(0, added.AceFlags);
    }

    [Fact]
    public void An_unusable_grant_plan_builds_nothing()
    {
        Assert.False(ServiceOwnershipApprovedDescriptorInterpreter.TryPlanApprovedDescriptor(
            default, ServiceOwnershipPromotionFixtures.Captured(), AccessFacts(),
            out byte[] approved, out ServiceOwnershipDescriptorApplyState refusal));

        Assert.Empty(approved);
        Assert.Equal(ServiceOwnershipDescriptorApplyState.Refused, refusal);
    }

    [Theory]
    [InlineData(false, true, false, true, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, false, true, false)]
    public void An_unproven_native_precondition_never_produces_an_approved_descriptor(
        bool keyOpened, bool containment, bool reparse, bool identity, bool readComplete)
    {
        Assert.False(ServiceOwnershipApprovedDescriptorInterpreter.TryPlanApprovedDescriptor(
            ServiceOwnershipPromotionFixtures.GrantPlan(),
            ServiceOwnershipPromotionFixtures.Captured(),
            AccessFacts(keyOpened, containment, reparse, identity, false, readComplete),
            out byte[] approved, out ServiceOwnershipDescriptorApplyState refusal));

        Assert.Empty(approved);
        Assert.Equal(ServiceOwnershipDescriptorApplyState.Failed, refusal);
    }

    [Fact]
    public void An_apply_that_requested_sacl_access_is_refused_outright()
    {
        Assert.False(ServiceOwnershipApprovedDescriptorInterpreter.TryPlanApprovedDescriptor(
            ServiceOwnershipPromotionFixtures.GrantPlan(),
            ServiceOwnershipPromotionFixtures.Captured(),
            AccessFacts(saclRequested: true),
            out _, out ServiceOwnershipDescriptorApplyState refusal));

        Assert.Equal(ServiceOwnershipDescriptorApplyState.Refused, refusal);
    }

    [Fact]
    public void A_prior_descriptor_that_already_carries_the_service_ace_is_never_re_granted()
    {
        // The capture and the observation AGREE here, so the ONLY thing that can
        // refuse is the certified builder's own already-granted rule.
        Assert.False(ServiceOwnershipApprovedDescriptorInterpreter.TryPlanApprovedDescriptor(
            ServiceOwnershipPromotionFixtures.GrantPlan(),
            CapturedOf(PostGrantBytes()),
            AccessFacts(bytes: PostGrantBytes()),
            out byte[] approved, out ServiceOwnershipDescriptorApplyState refusal));

        Assert.Empty(approved);
        Assert.Equal(ServiceOwnershipDescriptorApplyState.Refused, refusal);
    }

    [Fact]
    public void A_malformed_prior_descriptor_is_never_repaired_into_an_approved_one()
    {
        byte[] malformed = new byte[] { 9, 9, 9, 9 };

        Assert.False(ServiceOwnershipApprovedDescriptorInterpreter.TryPlanApprovedDescriptor(
            ServiceOwnershipPromotionFixtures.GrantPlan(),
            CapturedOf(malformed),
            AccessFacts(bytes: malformed),
            out byte[] approved, out ServiceOwnershipDescriptorApplyState refusal));

        Assert.Empty(approved);
        Assert.Equal(ServiceOwnershipDescriptorApplyState.Refused, refusal);
    }

    // ---- CYCLE 96: the snapshot the plan is bound to --------------------------

    [Fact]
    public void An_observed_descriptor_that_is_not_the_captured_one_produces_nothing()
    {
        // The observation is a PERFECTLY VALID supported descriptor. It is refused
        // solely because it is not the snapshot step (f) captured.
        byte[] changed = (byte[])ServiceOwnershipPromotionFixtures.PriorBytes.Clone();
        changed[60] = 0xEA;

        Assert.False(ServiceOwnershipApprovedDescriptorInterpreter.TryPlanApprovedDescriptor(
            ServiceOwnershipPromotionFixtures.GrantPlan(),
            ServiceOwnershipPromotionFixtures.Captured(),
            AccessFacts(bytes: changed),
            out byte[] approved, out ServiceOwnershipDescriptorApplyState refusal));

        Assert.Empty(approved);
        Assert.Equal(ServiceOwnershipDescriptorApplyState.Refused, refusal);

        // CALIBRATION: that same descriptor IS supported and IS grantable, so the
        // refusal above really is about the snapshot and nothing else.
        Assert.True(ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            changed, ServiceOwnershipPromotionFixtures.SvcSid, out _, out _, out _));
    }

    [Fact]
    public void An_uncaptured_or_digest_broken_snapshot_produces_nothing()
    {
        Assert.False(ServiceOwnershipApprovedDescriptorInterpreter.TryPlanApprovedDescriptor(
            ServiceOwnershipPromotionFixtures.GrantPlan(),
            default,
            AccessFacts(),
            out _, out ServiceOwnershipDescriptorApplyState uncaptured));

        Assert.Equal(ServiceOwnershipDescriptorApplyState.Refused, uncaptured);

        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            "present", out ServiceOwnershipPriorDaclState present));

        ServiceOwnershipCapturedPriorDescriptor tampered =
            ServiceOwnershipCapturedPriorDescriptor.Captured(
                present,
                Convert.ToBase64String(ServiceOwnershipPromotionFixtures.PriorBytes),
                Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })));

        Assert.False(ServiceOwnershipApprovedDescriptorInterpreter.TryPlanApprovedDescriptor(
            ServiceOwnershipPromotionFixtures.GrantPlan(), tampered, AccessFacts(),
            out _, out ServiceOwnershipDescriptorApplyState broken));

        Assert.Equal(ServiceOwnershipDescriptorApplyState.Refused, broken);
    }

    private static ServiceOwnershipObservedDescriptorWriteFacts WriteFacts(
        bool keyOpened = true,
        bool containmentProven = true,
        bool handleIdentityProven = true,
        bool writeAttempted = true,
        bool writeSucceeded = true,
        bool verificationReadComplete = true,
        byte[]? verified = null) =>
        new(keyOpened, containmentProven, handleIdentityProven, writeAttempted,
            writeSucceeded, verificationReadComplete, verified ?? PostGrantBytes());

    [Fact]
    public void A_write_that_was_reread_and_matched_is_the_only_applied_outcome()
    {
        Assert.Equal(
            ServiceOwnershipDescriptorApplyState.Applied,
            ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                PostGrantBytes(), WriteFacts()));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void An_apply_whose_target_was_never_proven_reports_failure(
        bool keyOpened, bool containment, bool identity)
    {
        Assert.Equal(
            ServiceOwnershipDescriptorApplyState.Failed,
            ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                PostGrantBytes(), WriteFacts(keyOpened, containment, identity)));
    }

    [Fact]
    public void An_apply_that_never_attempted_a_write_is_a_refusal_not_a_failure()
    {
        Assert.Equal(
            ServiceOwnershipDescriptorApplyState.Refused,
            ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                PostGrantBytes(), WriteFacts(writeAttempted: false)));
    }

    [Fact]
    public void An_apply_with_nothing_to_write_is_a_refusal()
    {
        Assert.Equal(
            ServiceOwnershipDescriptorApplyState.Refused,
            ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                Array.Empty<byte>(), WriteFacts()));
    }

    [Fact]
    public void An_attempted_write_that_did_not_succeed_is_a_failure()
    {
        Assert.Equal(
            ServiceOwnershipDescriptorApplyState.Failed,
            ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                PostGrantBytes(), WriteFacts(writeSucceeded: false)));
    }

    [Fact]
    public void An_apply_that_cannot_be_reread_or_does_not_match_is_never_reported_as_applied()
    {
        Assert.Equal(
            ServiceOwnershipDescriptorApplyState.Failed,
            ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                PostGrantBytes(), WriteFacts(verificationReadComplete: false)));

        Assert.Equal(
            ServiceOwnershipDescriptorApplyState.Failed,
            ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                PostGrantBytes(),
                WriteFacts(verified: ServiceOwnershipPromotionFixtures.PriorBytes)));

        Assert.Equal(
            ServiceOwnershipDescriptorApplyState.Failed,
            ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                PostGrantBytes(), WriteFacts(verified: Array.Empty<byte>())));
    }

    // =======================================================================
    // RESTORE - exactly the captured bytes, never a rebuild
    // =======================================================================

    [Fact]
    public void A_restoration_carries_exactly_the_captured_bytes_and_nothing_derived()
    {
        Assert.True(ServiceOwnershipDescriptorRestoreInterpreter.TryPlanRestoration(
            ServiceOwnershipPromotionFixtures.KeyHandle(),
            ServiceOwnershipPromotionFixtures.Captured(),
            out byte[] restoration, out ServiceOwnershipDescriptorRestoreState refusal));

        Assert.Equal(ServiceOwnershipPromotionFixtures.PriorBytes, restoration);
        Assert.Equal(
            ServiceOwnershipPromotionFixtures.PriorSha256,
            Convert.ToHexString(SHA256.HashData(restoration)));
        Assert.Equal(ServiceOwnershipDescriptorRestoreState.RestoredAndVerified, refusal);
    }

    [Fact]
    public void An_unusable_key_handle_restores_nothing()
    {
        Assert.False(ServiceOwnershipDescriptorRestoreInterpreter.TryPlanRestoration(
            default, ServiceOwnershipPromotionFixtures.Captured(),
            out byte[] restoration, out ServiceOwnershipDescriptorRestoreState refusal));

        Assert.Empty(restoration);
        Assert.Equal(ServiceOwnershipDescriptorRestoreState.Failed, refusal);
    }

    [Fact]
    public void A_descriptor_that_was_never_captured_restores_nothing()
    {
        Assert.False(ServiceOwnershipDescriptorRestoreInterpreter.TryPlanRestoration(
            ServiceOwnershipPromotionFixtures.KeyHandle(), default,
            out byte[] restoration, out ServiceOwnershipDescriptorRestoreState refusal));

        Assert.Empty(restoration);
        Assert.Equal(ServiceOwnershipDescriptorRestoreState.Failed, refusal);
    }

    [Fact]
    public void A_capture_whose_digest_does_not_bind_its_own_bytes_restores_nothing()
    {
        ServiceOwnershipCapturedPriorDescriptor tampered =
            ServiceOwnershipCapturedPriorDescriptor.Captured(
                ServiceOwnershipPriorDaclState.Present,
                ServiceOwnershipPromotionFixtures.PriorB64,
                new string('A', 64));

        Assert.False(ServiceOwnershipDescriptorRestoreInterpreter.TryPlanRestoration(
            ServiceOwnershipPromotionFixtures.KeyHandle(), tampered,
            out byte[] restoration, out ServiceOwnershipDescriptorRestoreState refusal));

        Assert.Empty(restoration);
        Assert.Equal(ServiceOwnershipDescriptorRestoreState.Failed, refusal);
    }

    [Theory]
    [InlineData("not base64 at all!!")]
    [InlineData("")]
    public void A_capture_whose_bytes_cannot_be_decoded_restores_nothing(string base64)
    {
        ServiceOwnershipCapturedPriorDescriptor broken =
            ServiceOwnershipCapturedPriorDescriptor.Captured(
                ServiceOwnershipPriorDaclState.Present, base64,
                ServiceOwnershipPromotionFixtures.PriorSha256);

        Assert.False(ServiceOwnershipDescriptorRestoreInterpreter.TryPlanRestoration(
            ServiceOwnershipPromotionFixtures.KeyHandle(), broken,
            out byte[] restoration, out ServiceOwnershipDescriptorRestoreState refusal));

        Assert.Empty(restoration);
        Assert.Equal(ServiceOwnershipDescriptorRestoreState.Failed, refusal);
    }

    [Fact]
    public void Only_a_reread_that_proves_byte_identity_reports_restored_and_verified()
    {
        Assert.Equal(
            ServiceOwnershipDescriptorRestoreState.RestoredAndVerified,
            ServiceOwnershipDescriptorRestoreInterpreter.InterpretRestoreOutcome(
                ServiceOwnershipPromotionFixtures.PriorBytes,
                WriteFacts(verified: ServiceOwnershipPromotionFixtures.PriorBytes)));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void A_restore_whose_target_was_never_proven_reports_failure(
        bool keyOpened, bool containment, bool identity)
    {
        Assert.Equal(
            ServiceOwnershipDescriptorRestoreState.Failed,
            ServiceOwnershipDescriptorRestoreInterpreter.InterpretRestoreOutcome(
                ServiceOwnershipPromotionFixtures.PriorBytes,
                WriteFacts(keyOpened, containment, identity,
                    verified: ServiceOwnershipPromotionFixtures.PriorBytes)));
    }

    [Fact]
    public void A_restore_that_was_never_attempted_is_a_failure_not_an_unproven_restore()
    {
        Assert.Equal(
            ServiceOwnershipDescriptorRestoreState.Failed,
            ServiceOwnershipDescriptorRestoreInterpreter.InterpretRestoreOutcome(
                ServiceOwnershipPromotionFixtures.PriorBytes,
                WriteFacts(writeAttempted: false,
                    verified: ServiceOwnershipPromotionFixtures.PriorBytes)));

        Assert.Equal(
            ServiceOwnershipDescriptorRestoreState.Failed,
            ServiceOwnershipDescriptorRestoreInterpreter.InterpretRestoreOutcome(
                Array.Empty<byte>(), WriteFacts()));
    }

    [Fact]
    public void A_restore_that_cannot_be_proven_byte_identical_is_never_reported_as_verified()
    {
        // Attempted but failed, unreadable, and mismatched all land on NotProven,
        // which is the RecoveryRequired-compatible outcome the transaction expects.
        Assert.Equal(
            ServiceOwnershipDescriptorRestoreState.NotProven,
            ServiceOwnershipDescriptorRestoreInterpreter.InterpretRestoreOutcome(
                ServiceOwnershipPromotionFixtures.PriorBytes,
                WriteFacts(writeSucceeded: false,
                    verified: ServiceOwnershipPromotionFixtures.PriorBytes)));

        Assert.Equal(
            ServiceOwnershipDescriptorRestoreState.NotProven,
            ServiceOwnershipDescriptorRestoreInterpreter.InterpretRestoreOutcome(
                ServiceOwnershipPromotionFixtures.PriorBytes,
                WriteFacts(verificationReadComplete: false,
                    verified: ServiceOwnershipPromotionFixtures.PriorBytes)));

        Assert.Equal(
            ServiceOwnershipDescriptorRestoreState.NotProven,
            ServiceOwnershipDescriptorRestoreInterpreter.InterpretRestoreOutcome(
                ServiceOwnershipPromotionFixtures.PriorBytes,
                WriteFacts(verified: PostGrantBytes())));
    }

    // =======================================================================
    // LEDGER PERSISTENCE - a pure mapping over EVERY bounded write outcome
    // =======================================================================

    [Fact]
    public void A_verified_durable_write_is_the_only_persisted_state()
    {
        Assert.Equal(
            ServiceOwnershipLedgerPersistState.Persisted,
            ServiceOwnershipFixedLedgerPersistencePort.MapWriteResult(
                ServiceOwnershipLedgerWriteResult.Written()));
    }

    [Fact]
    public void A_missing_write_result_is_a_refusal_and_never_a_success()
    {
        Assert.Equal(
            ServiceOwnershipLedgerPersistState.Refused,
            ServiceOwnershipFixedLedgerPersistencePort.MapWriteResult(null));
    }

    [Fact]
    public void An_unprovable_rollback_is_the_one_outcome_that_demands_a_human()
    {
        Assert.Equal(
            ServiceOwnershipLedgerPersistState.RecoveryRequired,
            ServiceOwnershipFixedLedgerPersistencePort.MapWriteResult(
                ServiceOwnershipLedgerWriteResult.Refused(
                    ServiceOwnershipLedgerWriteOutcome.RecoveryRequired)));
    }

    [Fact]
    public void Every_bounded_write_outcome_maps_to_a_bounded_persistence_state()
    {
        foreach (ServiceOwnershipLedgerWriteOutcome outcome in
                 Enum.GetValues<ServiceOwnershipLedgerWriteOutcome>())
        {
            // STATED HONESTLY: a refusal CARRYING the Written outcome is not a
            // distinguishable value - the certified result type builds Written()
            // and Refused(Written) with identical fields - so this loop covers
            // every outcome that a refusal can actually represent, and the
            // verified-write case is asserted on its own above.
            if (outcome == ServiceOwnershipLedgerWriteOutcome.Written)
            {
                continue;
            }

            ServiceOwnershipLedgerPersistState refused =
                ServiceOwnershipFixedLedgerPersistencePort.MapWriteResult(
                    ServiceOwnershipLedgerWriteResult.Refused(outcome));
            ServiceOwnershipLedgerPersistState rolledBack =
                ServiceOwnershipFixedLedgerPersistencePort.MapWriteResult(
                    ServiceOwnershipLedgerWriteResult.RolledBack(outcome));

            Assert.NotEqual(ServiceOwnershipLedgerPersistState.Unspecified, refused);
            Assert.NotEqual(ServiceOwnershipLedgerPersistState.Unspecified, rolledBack);

            // Only a write PROVEN on disk is Persisted, and a refusal is never one.
            Assert.NotEqual(ServiceOwnershipLedgerPersistState.Persisted, refused);
            Assert.NotEqual(ServiceOwnershipLedgerPersistState.Persisted, rolledBack);

            Assert.Equal(
                outcome == ServiceOwnershipLedgerWriteOutcome.RecoveryRequired
                    ? ServiceOwnershipLedgerPersistState.RecoveryRequired
                    : ServiceOwnershipLedgerPersistState.Refused,
                refused);
            Assert.Equal(
                outcome == ServiceOwnershipLedgerWriteOutcome.RecoveryRequired
                    ? ServiceOwnershipLedgerPersistState.RecoveryRequired
                    : ServiceOwnershipLedgerPersistState.RolledBack,
                rolledBack);
        }

        // The loop really did cover every member but the one excluded above.
        Assert.Equal(
            17, Enum.GetValues<ServiceOwnershipLedgerWriteOutcome>().Length);
    }

    // =======================================================================
    // PROMOTED RECIPE - persistence, receipt-bound compensation, disposal
    // =======================================================================

    private sealed class RecipeSandbox : IDisposable
    {
        internal RecipeSandbox()
        {
            Root = Path.Combine(
                Path.GetTempPath(), "paxc92-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new ServiceOwnershipPromotedRecipeStore(Root);
            Port = new ServiceOwnershipFixedPromotedRecipePort(Store);
        }

        internal string Root { get; }

        internal ServiceOwnershipPromotedRecipeStore Store { get; }

        internal ServiceOwnershipFixedPromotedRecipePort Port { get; }

        internal string[] Files() =>
            Directory.GetFiles(Root, "*", SearchOption.AllDirectories);

        public void Dispose()
        {
            Port.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A sandbox that outlives one test run is harmless.
            }
        }
    }

    [Fact]
    public void The_accepted_requests_own_recipe_is_persisted_and_a_receipt_is_retained()
    {
        using var sandbox = new RecipeSandbox();

        Assert.False(sandbox.Port.HasReceipt);
        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Completed,
            sandbox.Port.PersistPromotedRecipe(ServiceOwnershipPromotionFixtures.PromotionRequest()));
        Assert.True(sandbox.Port.HasReceipt);

        string persisted = Assert.Single(sandbox.Files());
        Assert.Equal(
            ServiceOwnershipPromotionFixtures.JobId + ".json", Path.GetFileName(persisted));
        Assert.Equal(
            ServiceOwnershipPromotionFixtures.RecipeBytes, File.ReadAllBytes(persisted));
    }

    [Fact]
    public void A_null_request_persists_nothing()
    {
        using var sandbox = new RecipeSandbox();

        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Refused,
            sandbox.Port.PersistPromotedRecipe(null!));
        Assert.False(sandbox.Port.HasReceipt);
        Assert.Empty(sandbox.Files());
    }

    [Fact]
    public void Compensation_removes_only_the_placement_this_adapter_made()
    {
        using var sandbox = new RecipeSandbox();
        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Completed,
            sandbox.Port.PersistPromotedRecipe(ServiceOwnershipPromotionFixtures.PromotionRequest()));

        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Completed,
            sandbox.Port.CompensatePromotedRecipe(ServiceOwnershipPromotionFixtures.JobId));

        Assert.Empty(sandbox.Files());
        Assert.False(sandbox.Port.HasReceipt);
    }

    [Theory]
    [InlineData("job-0002")]
    [InlineData("JOB-0001")]
    [InlineData("")]
    [InlineData(null)]
    public void A_job_id_that_is_not_the_receipts_own_compensates_nothing(string? jobId)
    {
        using var sandbox = new RecipeSandbox();
        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Completed,
            sandbox.Port.PersistPromotedRecipe(ServiceOwnershipPromotionFixtures.PromotionRequest()));

        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Refused,
            sandbox.Port.CompensatePromotedRecipe(jobId!));

        Assert.Single(sandbox.Files());
        Assert.True(sandbox.Port.HasReceipt);
    }

    [Fact]
    public void A_compensation_without_a_receipt_removes_nothing()
    {
        using var sandbox = new RecipeSandbox();

        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Refused,
            sandbox.Port.CompensatePromotedRecipe(ServiceOwnershipPromotionFixtures.JobId));
    }

    [Fact]
    public void The_receipt_is_cleared_by_a_completed_compensation_and_never_reused()
    {
        using var sandbox = new RecipeSandbox();
        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Completed,
            sandbox.Port.PersistPromotedRecipe(ServiceOwnershipPromotionFixtures.PromotionRequest()));
        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Completed,
            sandbox.Port.CompensatePromotedRecipe(ServiceOwnershipPromotionFixtures.JobId));

        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Refused,
            sandbox.Port.CompensatePromotedRecipe(ServiceOwnershipPromotionFixtures.JobId));
    }

    [Fact]
    public void Terminal_disposal_clears_the_receipt()
    {
        using var sandbox = new RecipeSandbox();
        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Completed,
            sandbox.Port.PersistPromotedRecipe(ServiceOwnershipPromotionFixtures.PromotionRequest()));
        Assert.True(sandbox.Port.HasReceipt);

        sandbox.Port.Dispose();

        Assert.False(sandbox.Port.HasReceipt);
        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Refused,
            sandbox.Port.CompensatePromotedRecipe(ServiceOwnershipPromotionFixtures.JobId));

        // Disposal compensates NOTHING on its own: the file is still there.
        Assert.Single(sandbox.Files());
    }

    [Fact]
    public void A_second_persist_is_refused_and_never_replaces_the_first_receipt()
    {
        using var sandbox = new RecipeSandbox();
        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Completed,
            sandbox.Port.PersistPromotedRecipe(ServiceOwnershipPromotionFixtures.PromotionRequest()));

        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Refused,
            sandbox.Port.PersistPromotedRecipe(ServiceOwnershipPromotionFixtures.PromotionRequest()));

        Assert.Single(sandbox.Files());
        Assert.Equal(
            ServiceOwnershipPromotedRecipePortOutcome.Completed,
            sandbox.Port.CompensatePromotedRecipe(ServiceOwnershipPromotionFixtures.JobId));
        Assert.Empty(sandbox.Files());
    }

    [Fact]
    public void The_job_id_gate_is_the_certified_key_identity_grammar()
    {
        // The gate this adapter applies is NARROWER than the store's own bounded
        // token rule, and the accepted request's id passes it.
        Assert.True(ServiceOwnershipLedgerContract.IsValidKeyIdentity(
            ServiceOwnershipPromotionFixtures.JobId));

        // CALIBRATION: a spelling the looser store rule accepts but the certified
        // key-identity grammar refuses really does exist.
        Assert.True(ServiceOwnershipLedgerContract.IsValidBoundedToken(
            ".hidden", ServiceOwnershipLedgerContract.MaxStringLength));
        Assert.False(ServiceOwnershipLedgerContract.IsValidKeyIdentity(".hidden"));
    }

    [Fact]
    public void Every_bounded_compensation_outcome_maps_to_a_bounded_port_outcome()
    {
        foreach (ServiceOwnershipPromotedRecipeCompensationOutcome outcome in
                 Enum.GetValues<ServiceOwnershipPromotedRecipeCompensationOutcome>())
        {
            ServiceOwnershipPromotedRecipePortOutcome mapped =
                ServiceOwnershipFixedPromotedRecipePort.MapCompensation(outcome);

            Assert.NotEqual(ServiceOwnershipPromotedRecipePortOutcome.Unspecified, mapped);

            bool clean =
                outcome == ServiceOwnershipPromotedRecipeCompensationOutcome.Removed
                || outcome == ServiceOwnershipPromotedRecipeCompensationOutcome.NothingToRemove;

            Assert.Equal(
                clean
                    ? ServiceOwnershipPromotedRecipePortOutcome.Completed
                    : ServiceOwnershipPromotedRecipePortOutcome.Refused,
                mapped);
        }
    }
}
