using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// ===========================================================================
// CYCLE 82 - NARROW ACTIVE LEDGER ACCEPTANCE (D1), Done+Active COHERENCE (D2),
// and the SCHEMA-V3 SERIALIZER SURFACE (D3).
// ===========================================================================
//
// SCOPE, stated plainly. Everything here runs against PURE, PORTABLE contract
// code. Nothing in this file opens a certificate store, touches a private key,
// reads or writes an ACL, reads the registry, installs/starts/stops a service,
// elevates, reads or writes ProgramData, opens a socket, or starts a process.
// Every SID, thumbprint, key identity and descriptor below is SYNTHETIC.
//
// WHY SOME ASSERTIONS ARE REFLECTION-BASED. These tests were authored RED, before
// the production change existed. A compile-time reference to an enum member or a
// type that does not exist yet does not fail - it stops the whole test assembly
// from compiling, which would destroy the RED evidence for every OTHER case in
// this file. Reflection over the LIVE vocabulary compiles both before and after
// and fails loudly and specifically in between.
public class ServiceOwnershipActiveLedgerAcceptanceTests
{
    // ---- synthetic fixture values --------------------------------------------

    private const string OwnerSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string SvcSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890";
    private const string Thumb = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string Thumb2 = "0123456789ABCDEF0123456789ABCDEF01234567";
    private const string KeyId = "synthetic-key-identity_01.test";
    private const string ProviderToken = "microsoft-software-key-storage-provider";
    private const string RetiredProfileToken =
        "microsoft-software-ksp-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0";
    private const string ProfileToken =
        "microsoft-software-ksp-backing-file-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0-filesystemrights";
    private const string MechanismToken = "microsoft-software-ksp-backing-file-dacl";
    private const string RetiredMechanismToken = "cng-security-descriptor";
    private const string DescriptorFormatToken = "microsoft-software-ksp-backing-file-self-relative-v1";
    private const string KeyStorageRootToken = "microsoft-software-key-storage-provider-machine-keys";
    private const string ProviderUniqueNameValue = "synthetic-unique-leaf_01.pvk";
    private const string Stamp = "2026-08-06T00:00:00Z";
    private const string InstallId = "install-0001";
    private const string ApprovedMaskText = "00120009";

    // The ONE numeric slot cycle 82 is authorized to add. Slot 2 held ValidActive
    // and is PERMANENTLY RETIRED; it must never be reused.
    private const int ExpectedActiveOutcomeValue = 9;
    private const int RetiredValidActiveSlot = 2;

    // A STRUCTURALLY VALID prior descriptor: owner BUILTIN\Administrators, a
    // captured group SID, DACL present+protected, no SACL, exactly the two measured
    // ACEs (SYSTEM then BUILTIN\Administrators). No service ACE is present, which
    // the parser requires for a promotable captured prior state.
    private static readonly byte[] PriorBytes =
        { 0x01, 0x00, 0x04, 0x90, 0x14, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00, 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x15, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0xE9, 0x03, 0x00, 0x00, 0x02, 0x00, 0x34, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x03, 0x14, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x12, 0x00, 0x00, 0x00, 0x00, 0x03, 0x18, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00 };

    // =======================================================================
    // RED 1 - THE EXACT APPROVED ACTIVE ENTRY IS ACCEPTED
    // =======================================================================

    [Fact]
    public void The_exact_approved_active_entry_is_accepted()
    {
        ServiceOwnershipLedgerValidationResult result = Validate(HealthyActiveDoc());

        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, result.Reason);
        Assert.True(result.IsAccepted, "the exact approved active entry must be accepted");
        Assert.NotNull(result.Document);
        Assert.Single(result.Document!.Entries);
        Assert.Equal(
            ServiceOwnershipLifecycleState.Active,
            result.Document.Entries[0].LifecycleState);

        // The bytes needed to unwind the grant survive acceptance. This is the whole
        // point of D1: an accepted active ledger keeps the restore payload reachable.
        Assert.Equal(Convert.ToBase64String(PriorBytes), result.Document.Entries[0].PriorDaclBytesBase64);
        Assert.Equal(Hex(SHA256.HashData(PriorBytes)), result.Document.Entries[0].PriorDaclSha256);
    }

    // =======================================================================
    // RED 2 - A HEALTHY Done+Active LEDGER REPORTS THE NEW Active OUTCOME
    // =======================================================================

    [Fact]
    public void The_outcome_vocabulary_gains_active_at_slot_nine_and_never_reuses_slot_two()
    {
        string[] names = Enum.GetNames(typeof(ServiceOwnershipLedgerOutcome));

        Assert.Contains("Active", names);
        Assert.DoesNotContain("ValidActive", names);

        object active = Enum.Parse(typeof(ServiceOwnershipLedgerOutcome), "Active");
        Assert.Equal(ExpectedActiveOutcomeValue, (int)active);

        // Slot 2 stays empty forever.
        int[] values = Enum.GetValues(typeof(ServiceOwnershipLedgerOutcome))
            .Cast<object>()
            .Select(v => (int)v)
            .ToArray();
        Assert.DoesNotContain(RetiredValidActiveSlot, values);

        // Nothing else moved.
        Assert.Equal(
            new[] { 0, 1, 3, 4, 5, 6, 7, 8, ExpectedActiveOutcomeValue },
            values.OrderBy(v => v).ToArray());
    }

    [Fact]
    public void A_healthy_done_plus_active_ledger_reports_the_active_outcome()
    {
        ServiceOwnershipLedgerValidationResult result = Validate(HealthyActiveDoc());

        Assert.True(result.IsAccepted);
        Assert.Equal(ExpectedActiveOutcomeValue, (int)result.Outcome);

        // A healthy active ledger must NEVER be reported as an in-flight transaction.
        Assert.NotEqual(ServiceOwnershipLedgerOutcome.InProgress, result.Outcome);
    }

    [Fact]
    public void A_non_done_active_ledger_is_never_reported_as_active()
    {
        foreach (string transaction in new[] { "preparing", "credential-mutated", "ledger-committed", "restoring" })
        {
            DocModel doc = HealthyActiveDoc();
            doc.TransactionState = transaction;

            ServiceOwnershipLedgerValidationResult result = Validate(doc);

            Assert.NotEqual(ExpectedActiveOutcomeValue, (int)result.Outcome);
        }
    }

    [Fact]
    public void A_mixed_or_stale_ledger_is_never_reported_as_active()
    {
        // Done + [active, restored]: incomplete, so not the Active outcome.
        DocModel mixed = HealthyActiveDoc();
        mixed.Entries.Add(ActiveEntry());
        mixed.Entries[1].EntryId = "entry-0002";
        mixed.Entries[1].CertificateThumbprintSha1 = Thumb2;
        mixed.Entries[1].KeyIdentity = "synthetic-key-identity_02.test";
        mixed.Entries[1].ProviderUniqueName = "synthetic-unique-leaf_02.pvk";
        mixed.Entries[1].JobIds = new[] { "job-0002" };
        mixed.Entries[1].LifecycleState = "restored";

        ServiceOwnershipLedgerValidationResult mixedResult = Validate(mixed);
        Assert.True(mixedResult.IsAccepted);
        Assert.NotEqual(ExpectedActiveOutcomeValue, (int)mixedResult.Outcome);

        // Done + [active, stale]: stale poisons the whole ledger, and stale wins.
        DocModel stale = HealthyActiveDoc();
        stale.Entries.Add(ActiveEntry());
        stale.Entries[1].EntryId = "entry-0003";
        stale.Entries[1].CertificateThumbprintSha1 = Thumb2;
        stale.Entries[1].KeyIdentity = "synthetic-key-identity_03.test";
        stale.Entries[1].ProviderUniqueName = "synthetic-unique-leaf_03.pvk";
        stale.Entries[1].JobIds = new[] { "job-0003" };
        stale.Entries[1].LifecycleState = "stale";

        ServiceOwnershipLedgerValidationResult staleResult = Validate(stale);
        Assert.True(staleResult.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Stale, staleResult.Outcome);
    }

    // =======================================================================
    // THE NEAR-MISS REFUSAL MATRIX
    // =======================================================================
    //
    // Each row changes EXACTLY ONE field of the otherwise-exact approved active
    // entry. Every row must remain refused, and the document must be null so no
    // partial state escapes.

    public static IEnumerable<object[]> NearMissRows()
    {
        yield return new object[] { "provider-kind-broad-cng", "privateKeyProviderKind", "cng" };
        yield return new object[] { "provider-kind-legacy-csp", "privateKeyProviderKind", "legacy-csp" };
        yield return new object[] { "rights-profile-retired", "rightsProfileId", RetiredProfileToken };
        yield return new object[] { "grant-mechanism-retired-cng", "grantMechanism", RetiredMechanismToken };
        yield return new object[] { "grant-mechanism-csp-key-file", "grantMechanism", "csp-key-file-dacl" };
        yield return new object[] { "rights-policy-version-2", "rightsPolicyVersion", "2" };
        yield return new object[] { "rights-policy-version-4", "rightsPolicyVersion", "4" };
        yield return new object[] { "mask-missing-one-bit", "grantedRightsMask", "00120008" };
        yield return new object[] { "mask-one-extra-bit", "grantedRightsMask", "00120089" };
        yield return new object[] { "mask-generic-read", "grantedRightsMask", "80120009" };
        yield return new object[] { "mask-zero", "grantedRightsMask", "00000000" };
        yield return new object[] { "prior-dacl-state-absent", "priorDaclState", "absent" };
        yield return new object[] { "prior-dacl-digest-wrong", "priorDaclSha256", "0000000000000000000000000000000000000000000000000000000000000000" };
        yield return new object[] { "prior-dacl-bytes-truncated", "priorDaclBytesBase64", "AQAEkA==" };
        yield return new object[] { "captured-binding-wrong", "capturedStateBindingSha256", "1111111111111111111111111111111111111111111111111111111111111111" };
        yield return new object[] { "owner-sid-service-shaped", "owningUserSid", SvcSid };
        yield return new object[] { "service-sid-user-shaped", "serviceSid", OwnerSid };
        yield return new object[] { "descriptor-format-unspecified", "descriptorFormat", "not-a-descriptor-format" };
        yield return new object[] { "key-storage-root-unknown", "keyStorageRoot", "not-a-key-storage-root" };
        yield return new object[] { "credential-kind-unknown", "credentialKind", "some-other-credential" };
        yield return new object[] { "provenance-unknown", "provenance", "discovered" };
    }

    [Theory]
    [MemberData(nameof(NearMissRows))]
    public void Every_one_field_near_miss_active_entry_stays_refused(string label, string field, string value)
    {
        DocModel doc = HealthyActiveDoc();
        Mutate(doc.Entries[0], field, value);

        ServiceOwnershipLedgerValidationResult result = Validate(doc);

        Assert.True(result.IsRefused, label + " must remain refused");
        Assert.NotEqual(ServiceOwnershipLedgerInvalidReason.None, result.Reason);
        Assert.Null(result.Document);
        Assert.NotEqual(ExpectedActiveOutcomeValue, (int)result.Outcome);
    }

    [Fact]
    public void The_near_miss_matrix_is_not_vacuous()
    {
        // POSITIVE CONTROL. The unmutated fixture the matrix mutates must itself be
        // ACCEPTED, otherwise every row above would pass for the wrong reason.
        Assert.True(Validate(HealthyActiveDoc()).IsAccepted);
        Assert.Equal(21, NearMissRows().Count());
    }

    [Fact]
    public void A_prior_descriptor_that_already_carries_the_service_ace_is_refused_for_an_active_entry()
    {
        byte[] withServiceAce = PriorDescriptorWithServiceAce();

        DocModel doc = HealthyActiveDoc();
        doc.Entries[0].PriorDaclBytesBase64 = Convert.ToBase64String(withServiceAce);
        doc.Entries[0].PriorDaclSha256 = Hex(SHA256.HashData(withServiceAce));

        ServiceOwnershipLedgerValidationResult result = Validate(doc);

        Assert.True(result.IsRefused);
        Assert.Null(result.Document);
    }

    // =======================================================================
    // RED 3 - Done+Active LIFECYCLE COHERENCE
    // =======================================================================

    [Fact]
    public void Verify_on_a_healthy_done_plus_active_entry_plans_nothing()
    {
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            ServiceOwnershipLifecyclePlanRequest.ForVerify(
                Validate(HealthyActiveDoc()), InstallId, OwnerSid, SvcSid, "entry-0001"));

        Assert.Equal(ServiceOwnershipPlanOutcome.NoAction, plan.Outcome);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Recover_interrupted_never_tears_down_a_healthy_active_grant()
    {
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
                Validate(HealthyActiveDoc()), InstallId, OwnerSid, SvcSid, "entry-0001",
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant));

        Assert.Equal(ServiceOwnershipPlanOutcome.NoAction, plan.Outcome);
        Assert.Empty(plan.Actions);
        Assert.DoesNotContain(ServiceOwnershipPlanAction.RestoreCapturedPriorDacl, plan.Actions);
        Assert.DoesNotContain(ServiceOwnershipPlanAction.BeginRestore, plan.Actions);
    }

    [Theory]
    [InlineData(ServiceOwnershipCredentialObservation.MatchesCapturedPriorState)]
    [InlineData(ServiceOwnershipCredentialObservation.Diverged)]
    [InlineData(ServiceOwnershipCredentialObservation.KeyIdentityMismatch)]
    public void Recover_interrupted_on_an_unexplained_active_state_marks_stale(
        ServiceOwnershipCredentialObservation observation)
    {
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
                Validate(HealthyActiveDoc()), InstallId, OwnerSid, SvcSid, "entry-0001", observation));

        Assert.Equal(ServiceOwnershipPlanOutcome.PlanAvailable, plan.Outcome);
        Assert.Equal(
            new[] { ServiceOwnershipPlanAction.MarkStale, ServiceOwnershipPlanAction.PersistLedger },
            plan.Actions.ToArray());
    }

    [Fact]
    public void An_unavailable_observation_on_an_active_entry_is_a_bounded_refusal()
    {
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
                Validate(HealthyActiveDoc()), InstallId, OwnerSid, SvcSid, "entry-0001",
                ServiceOwnershipCredentialObservation.Unavailable));

        Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
        Assert.Equal(ServiceOwnershipPlanRefusalReason.ObservationUnavailable, plan.RefusalReason);
        Assert.Empty(plan.Actions);
    }

    [Theory]
    [InlineData(ServiceOwnershipPlannerOperation.RestoreCredential)]
    [InlineData(ServiceOwnershipPlannerOperation.DisableCleanup)]
    public void Restore_and_disable_cleanup_fully_roll_back_a_proven_active_grant(
        ServiceOwnershipPlannerOperation operation)
    {
        ServiceOwnershipLifecyclePlanRequest? request =
            operation == ServiceOwnershipPlannerOperation.RestoreCredential
                ? ServiceOwnershipLifecyclePlanRequest.ForRestoreCredential(
                    Validate(HealthyActiveDoc()), InstallId, OwnerSid, SvcSid, "entry-0001",
                    ServiceOwnershipCredentialObservation.MatchesRecordedGrant)
                : ServiceOwnershipLifecyclePlanRequest.ForDisableCleanup(
                    Validate(HealthyActiveDoc()), InstallId, OwnerSid, SvcSid, "entry-0001",
                    ServiceOwnershipCredentialObservation.MatchesRecordedGrant);

        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(request);

        Assert.Equal(ServiceOwnershipPlanOutcome.PlanAvailable, plan.Outcome);
        Assert.Equal(ExpectedFullRollback, plan.Actions.ToArray());
    }

    [Fact]
    public void Unpublish_of_a_non_associated_job_on_an_active_entry_is_refused()
    {
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob(
                Validate(HealthyActiveDoc()), InstallId, OwnerSid, SvcSid, "entry-0001", "job-0001",
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
                ServiceOwnershipJobRelation.JobNotAssociated));

        Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
        Assert.Equal(ServiceOwnershipPlanRefusalReason.JobNotAssociated, plan.RefusalReason);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Unpublish_with_other_jobs_remaining_removes_only_the_association()
    {
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob(
                Validate(HealthyActiveDoc()), InstallId, OwnerSid, SvcSid, "entry-0001", "job-0001",
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
                ServiceOwnershipJobRelation.OtherJobsRemain));

        Assert.Equal(ServiceOwnershipPlanOutcome.PlanAvailable, plan.Outcome);
        Assert.Equal(
            new[]
            {
                ServiceOwnershipPlanAction.RemoveJobAssociation,
                ServiceOwnershipPlanAction.PersistLedger,
            },
            plan.Actions.ToArray());
        Assert.DoesNotContain(ServiceOwnershipPlanAction.RestoreCapturedPriorDacl, plan.Actions);
    }

    [Fact]
    public void Unpublish_of_the_final_associated_job_runs_the_existing_full_restoration_sequence()
    {
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob(
                Validate(HealthyActiveDoc()), InstallId, OwnerSid, SvcSid, "entry-0001", "job-0001",
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
                ServiceOwnershipJobRelation.FinalAssociatedJob));

        Assert.Equal(ServiceOwnershipPlanOutcome.PlanAvailable, plan.Outcome);
        Assert.Equal(ExpectedFullRollback, plan.Actions.ToArray());
    }

    // =======================================================================
    // A4 - THE INTERRUPTED-PROMOTION PAIR IS UNTOUCHED
    // =======================================================================
    //
    // This is the regression Cycle 81 said would be catastrophic if Done+Active
    // were faked by terminating at CredentialMutated+Intended. Adding Done+Active
    // must leave the interrupted pair EXACTLY as it was.

    [Fact]
    public void Credential_mutated_plus_intended_still_plans_a_full_rollback()
    {
        DocModel doc = HealthyActiveDoc();
        doc.TransactionState = "credential-mutated";
        doc.Entries[0].LifecycleState = "intended";

        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
                Validate(doc), InstallId, OwnerSid, SvcSid, "entry-0001",
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant));

        Assert.Equal(ServiceOwnershipPlanOutcome.PlanAvailable, plan.Outcome);
        Assert.Equal(ExpectedFullRollback, plan.Actions.ToArray());
    }

    [Fact]
    public void The_five_pre_existing_coherent_pairs_still_behave_exactly_as_before()
    {
        // preparing+intended / untouched -> abandon the intent
        AssertPlan("preparing", "intended", ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipPlanAction.RemoveAbandonedIntentEntry, ServiceOwnershipPlanAction.PersistLedger);

        // preparing+intended / applied -> full rollback
        AssertPlan("preparing", "intended", ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ExpectedFullRollback);

        // credential-mutated+intended / contradictory -> stale
        AssertPlan("credential-mutated", "intended", ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipPlanAction.MarkStale, ServiceOwnershipPlanAction.PersistLedger);

        // restoring+restoring / not yet applied -> full rollback
        AssertPlan("restoring", "restoring", ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ExpectedFullRollback);

        // restoring+restoring / already applied -> finalize bookkeeping
        AssertPlan("restoring", "restoring", ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipPlanAction.MarkRestored, ServiceOwnershipPlanAction.PersistLedger,
            ServiceOwnershipPlanAction.RemoveRestoredEntry, ServiceOwnershipPlanAction.PersistLedger);

        // done+restored / already back -> retire the entry
        AssertPlan("done", "restored", ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipPlanAction.RemoveRestoredEntry, ServiceOwnershipPlanAction.PersistLedger);
    }

    [Fact]
    public void An_incoherent_active_pair_still_only_marks_stale()
    {
        foreach (string transaction in new[] { "preparing", "credential-mutated", "ledger-committed", "restoring" })
        {
            DocModel doc = HealthyActiveDoc();
            doc.TransactionState = transaction;

            ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
                ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
                    Validate(doc), InstallId, OwnerSid, SvcSid, "entry-0001",
                    ServiceOwnershipCredentialObservation.MatchesRecordedGrant));

            Assert.Equal(ServiceOwnershipPlanOutcome.PlanAvailable, plan.Outcome);
            Assert.Equal(
                new[] { ServiceOwnershipPlanAction.MarkStale, ServiceOwnershipPlanAction.PersistLedger },
                plan.Actions.ToArray());
        }
    }

    // =======================================================================
    // RED 4 - THE SCHEMA-V3 SERIALIZER EXISTS AND ROUND-TRIPS
    // =======================================================================
    //
    // Reflection-based on purpose: authored RED, before the type existed.

    [Fact]
    public void A_schema_v3_ledger_serializer_exists_on_the_contract_assembly()
    {
        Type? serializer = typeof(ServiceOwnershipLedgerContract).Assembly
            .GetType("PAXCookbook.Shared.Contracts.ServiceOwnershipLedgerSerializer", throwOnError: false);

        Assert.NotNull(serializer);
        Assert.True(serializer!.IsAbstract && serializer.IsSealed, "the serializer must be a static class");

        MethodInfo? serialize = serializer.GetMethod(
            "Serialize", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(serialize);

        // It must consume the VALIDATOR'S OWN result type, so a caller cannot hand it
        // a fabricated document.
        ParameterInfo[] parameters = serialize!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(ServiceOwnershipLedgerValidationResult), parameters[0].ParameterType);
    }

    [Fact]
    public void The_serializer_round_trips_an_active_document_back_to_the_active_outcome()
    {
        string json = SerializeAccepted(Validate(HealthyActiveDoc()));

        ServiceOwnershipLedgerValidationResult reparsed = ServiceOwnershipLedgerValidator.Validate(json);

        Assert.True(reparsed.IsAccepted);
        Assert.Equal(ExpectedActiveOutcomeValue, (int)reparsed.Outcome);
        Assert.NotNull(reparsed.Document);
        Assert.Equal(
            ServiceOwnershipLifecycleState.Active,
            reparsed.Document!.Entries[0].LifecycleState);
    }

    [Fact]
    public void Serialize_validate_serialize_is_byte_identical()
    {
        string first = SerializeAccepted(Validate(HealthyActiveDoc()));
        string second = SerializeAccepted(ServiceOwnershipLedgerValidator.Validate(first));

        Assert.Equal(first, second, StringComparer.Ordinal);
        Assert.Equal(
            Encoding.UTF8.GetBytes(first),
            Encoding.UTF8.GetBytes(second));
    }

    // =======================================================================
    // HELPERS
    // =======================================================================

    private static readonly ServiceOwnershipPlanAction[] ExpectedFullRollback =
    {
        ServiceOwnershipPlanAction.BeginRestore,
        ServiceOwnershipPlanAction.PersistLedger,
        ServiceOwnershipPlanAction.RestoreCapturedPriorDacl,
        ServiceOwnershipPlanAction.VerifyCapturedPriorDacl,
        ServiceOwnershipPlanAction.MarkRestored,
        ServiceOwnershipPlanAction.PersistLedger,
        ServiceOwnershipPlanAction.RemoveRestoredEntry,
        ServiceOwnershipPlanAction.PersistLedger,
    };

    private static void AssertPlan(
        string transaction,
        string lifecycle,
        ServiceOwnershipCredentialObservation observation,
        params ServiceOwnershipPlanAction[] expected)
    {
        DocModel doc = HealthyActiveDoc();
        doc.TransactionState = transaction;
        doc.Entries[0].LifecycleState = lifecycle;
        if (lifecycle != "active")
        {
            doc.Entries[0].GrantedRightsMask = "00000081";
        }

        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
                Validate(doc), InstallId, OwnerSid, SvcSid, "entry-0001", observation));

        Assert.Equal(ServiceOwnershipPlanOutcome.PlanAvailable, plan.Outcome);
        Assert.Equal(expected, plan.Actions.ToArray());
    }

    /// <summary>
    /// Calls the serializer through reflection and asserts it reported success.
    /// Returns the emitted JSON.
    /// </summary>
    private static string SerializeAccepted(ServiceOwnershipLedgerValidationResult source)
    {
        Type? serializer = typeof(ServiceOwnershipLedgerContract).Assembly
            .GetType("PAXCookbook.Shared.Contracts.ServiceOwnershipLedgerSerializer", throwOnError: false);
        Assert.NotNull(serializer);

        MethodInfo? serialize = serializer!.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(serialize);

        object? result = serialize!.Invoke(null, new object?[] { source });
        Assert.NotNull(result);

        object? isSerialized = result!.GetType().GetProperty("IsSerialized")?.GetValue(result);
        Assert.True(isSerialized is bool ok && ok, "the serializer refused an accepted document");

        object? json = result.GetType().GetProperty("Json")?.GetValue(result);
        Assert.IsType<string>(json);
        return (string)json!;
    }

    private static ServiceOwnershipLedgerValidationResult Validate(DocModel doc) =>
        ServiceOwnershipLedgerValidator.Validate(Build(doc));

    private static DocModel HealthyActiveDoc()
    {
        DocModel doc = new() { TransactionState = "done" };
        doc.Entries.Add(ActiveEntry());
        return doc;
    }

    private static EntryModel ActiveEntry() => new()
    {
        GrantedRightsMask = ApprovedMaskText,
        LifecycleState = "active",
    };

    private static void Mutate(EntryModel entry, string field, string value)
    {
        switch (field)
        {
            case "privateKeyProviderKind": entry.PrivateKeyProviderKind = value; break;
            case "rightsProfileId": entry.RightsProfileId = value; break;
            case "grantMechanism": entry.GrantMechanism = value; break;
            case "rightsPolicyVersion": entry.RightsPolicyVersionRaw = value; break;
            case "grantedRightsMask": entry.GrantedRightsMask = value; break;
            case "priorDaclState": entry.PriorDaclState = value; break;
            case "priorDaclSha256": entry.PriorDaclSha256 = value; break;
            case "priorDaclBytesBase64": entry.PriorDaclBytesBase64 = value; break;
            case "capturedStateBindingSha256": entry.CapturedStateBindingOverride = value; break;
            case "owningUserSid": entry.OwningUserSid = value; break;
            case "serviceSid": entry.ServiceSid = value; break;
            case "descriptorFormat": entry.DescriptorFormat = value; break;
            case "keyStorageRoot": entry.KeyStorageRoot = value; break;
            case "credentialKind": entry.CredentialKind = value; break;
            case "provenance": entry.Provenance = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, "unmapped near-miss field");
        }
    }

    /// <summary>
    /// The measured prior descriptor with an EXTRA AccessAllowed ACE for the service
    /// SID appended. The parser must refuse it: a captured "prior" state that already
    /// grants the service is not a prior state at all.
    /// </summary>
    private static byte[] PriorDescriptorWithServiceAce()
    {
        // 15 sub-authorities are not needed: the fixture service SID has 6, so the
        // binary SID is 8 + 6*4 = 32 bytes and the ACE is 8 + 32 = 40 bytes.
        byte[] serviceSidBytes = EncodeSid(SvcSid);
        var ace = new List<byte> { 0x00, 0x03 };
        ushort aceSize = (ushort)(8 + serviceSidBytes.Length);
        ace.Add((byte)(aceSize & 0xFF));
        ace.Add((byte)(aceSize >> 8));
        ace.AddRange(new byte[] { 0x09, 0x00, 0x12, 0x00 });
        ace.AddRange(serviceSidBytes);

        var bytes = new List<byte>(PriorBytes);

        // The DACL offset is at bytes 16..19 of the self-relative header.
        int daclOffset = BitConverter.ToInt32(PriorBytes, 16);
        ushort aclSize = BitConverter.ToUInt16(PriorBytes, daclOffset + 2);
        ushort aceCount = BitConverter.ToUInt16(PriorBytes, daclOffset + 4);

        bytes.AddRange(ace);

        ushort newAclSize = (ushort)(aclSize + ace.Count);
        bytes[daclOffset + 2] = (byte)(newAclSize & 0xFF);
        bytes[daclOffset + 3] = (byte)(newAclSize >> 8);
        ushort newCount = (ushort)(aceCount + 1);
        bytes[daclOffset + 4] = (byte)(newCount & 0xFF);
        bytes[daclOffset + 5] = (byte)(newCount >> 8);

        return bytes.ToArray();
    }

    private static byte[] EncodeSid(string sid)
    {
        string[] parts = sid.Split('-');
        byte revision = byte.Parse(parts[1], CultureInfo.InvariantCulture);
        ulong authority = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
        uint[] subAuthorities = parts.Skip(3)
            .Select(p => uint.Parse(p, CultureInfo.InvariantCulture))
            .ToArray();

        var bytes = new List<byte> { revision, (byte)subAuthorities.Length };
        for (int i = 5; i >= 0; i--)
        {
            bytes.Add((byte)((authority >> (8 * i)) & 0xFF));
        }
        foreach (uint sub in subAuthorities)
        {
            bytes.AddRange(BitConverter.GetBytes(sub));
        }
        return bytes.ToArray();
    }

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // ---- fixture builders ----------------------------------------------------

    private sealed class DocModel
    {
        public string SchemaVersionRaw = "3";
        public string ProductOwnershipMarker = ServiceOwnershipLedgerContract.ProductOwnershipMarker;
        public string ManagedFeatureId = ServiceOwnershipLedgerContract.ManagedFeatureId;
        public string InstallationOwnershipId = InstallId;
        public string GenerationRaw = "1";
        public string TransactionState = "done";
        public string CreatedUtc = Stamp;
        public string UpdatedUtc = Stamp;
        public string LastOperationId = "op-0001";
        public List<EntryModel> Entries = new();
    }

    private sealed class EntryModel
    {
        public string EntryId = "entry-0001";
        public string OwningUserSid = OwnerSid;
        public string ServiceSid = SvcSid;
        public string CredentialKind = "personal-app-registration-certificate";
        public string CertificateThumbprintSha1 = Thumb;
        public string Provenance = "referenced";
        public string PrivateKeyProviderKind = ProviderToken;
        public string RightsProfileId = ProfileToken;
        public string KeyIdentity = KeyId;
        public string GrantMechanism = MechanismToken;
        public string GrantedRightsMask = ApprovedMaskText;
        public string RightsPolicyVersionRaw = "3";
        public string PriorDaclState = "present";
        public string PriorDaclBytesBase64 = Convert.ToBase64String(PriorBytes);
        public string PriorDaclSha256 = Hex(SHA256.HashData(PriorBytes));
        public string? CapturedStateBindingOverride;
        public string[] JobIds = { "job-0001" };
        public string LifecycleState = "active";
        public string CreatedUtc = Stamp;
        public string UpdatedUtc = Stamp;
        public string ProviderUniqueName = ProviderUniqueNameValue;
        public string KeyStorageRoot = KeyStorageRootToken;
        public string DescriptorFormat = DescriptorFormatToken;
    }

    private static string Q(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Build(DocModel doc)
    {
        var sb = new StringBuilder("{");
        Append(sb, "schemaVersion", doc.SchemaVersionRaw, first: true);
        Append(sb, "productOwnershipMarker", Q(doc.ProductOwnershipMarker));
        Append(sb, "managedFeatureId", Q(doc.ManagedFeatureId));
        Append(sb, "installationOwnershipId", Q(doc.InstallationOwnershipId));
        Append(sb, "generation", doc.GenerationRaw);
        Append(sb, "transactionState", Q(doc.TransactionState));
        Append(sb, "entries", BuildEntries(doc));
        Append(sb, "createdUtc", Q(doc.CreatedUtc));
        Append(sb, "updatedUtc", Q(doc.UpdatedUtc));
        Append(sb, "lastOperationId", Q(doc.LastOperationId));
        return sb.Append('}').ToString();
    }

    private static void Append(StringBuilder sb, string name, string raw, bool first = false)
    {
        if (!first)
        {
            sb.Append(',');
        }
        sb.Append(Q(name)).Append(':').Append(raw);
    }

    private static string BuildEntries(DocModel doc)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < doc.Entries.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(BuildEntry(doc.Entries[i], doc.GenerationRaw));
        }
        return sb.Append(']').ToString();
    }

    private static string BuildEntry(EntryModel e, string generationRaw)
    {
        string binding = e.CapturedStateBindingOverride ?? ComputeBinding(e, generationRaw);

        var sb = new StringBuilder("{");
        Append(sb, "entryId", Q(e.EntryId), first: true);
        Append(sb, "owningUserSid", Q(e.OwningUserSid));
        Append(sb, "serviceSid", Q(e.ServiceSid));
        Append(sb, "credentialKind", Q(e.CredentialKind));
        Append(sb, "certificateThumbprintSha1", Q(e.CertificateThumbprintSha1));
        Append(sb, "provenance", Q(e.Provenance));
        Append(sb, "privateKeyProviderKind", Q(e.PrivateKeyProviderKind));
        Append(sb, "rightsProfileId", Q(e.RightsProfileId));
        Append(sb, "keyIdentity", Q(e.KeyIdentity));
        Append(sb, "grantMechanism", Q(e.GrantMechanism));
        Append(sb, "grantedRightsMask", Q(e.GrantedRightsMask));
        Append(sb, "rightsPolicyVersion", e.RightsPolicyVersionRaw);
        Append(sb, "priorDaclState", Q(e.PriorDaclState));
        Append(sb, "priorDaclBytesBase64", Q(e.PriorDaclBytesBase64));
        Append(sb, "priorDaclSha256", Q(e.PriorDaclSha256));
        Append(sb, "capturedStateBindingSha256", Q(binding));
        Append(sb, "associatedPromotedJobIds", "[" + string.Join(",", e.JobIds.Select(Q)) + "]");
        Append(sb, "lifecycleState", Q(e.LifecycleState));
        Append(sb, "createdUtc", Q(e.CreatedUtc));
        Append(sb, "updatedUtc", Q(e.UpdatedUtc));
        Append(sb, "providerUniqueName", Q(e.ProviderUniqueName));
        Append(sb, "keyStorageRoot", Q(e.KeyStorageRoot));
        Append(sb, "descriptorFormat", Q(e.DescriptorFormat));
        return sb.Append('}').ToString();
    }

    private static string ComputeBinding(EntryModel e, string generationRaw)
    {
        int generation = int.TryParse(
            generationRaw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int g) ? g : 0;

        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.PrivateKeyProviderKind, out ServiceOwnershipPrivateKeyProviderKind provider);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.RightsProfileId, out ServiceOwnershipRightsProfileId profile);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.GrantMechanism, out ServiceOwnershipGrantMechanism mechanism);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.PriorDaclState, out ServiceOwnershipPriorDaclState priorState);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.KeyStorageRoot, out ServiceOwnershipKeyStorageRoot keyStorageRoot);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.DescriptorFormat, out ServiceOwnershipDescriptorFormat descriptorFormat);

        return ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            generation, e.OwningUserSid, e.ServiceSid, e.CertificateThumbprintSha1,
            provider, profile, e.KeyIdentity, mechanism, e.GrantedRightsMask,
            priorState, e.PriorDaclSha256, e.ProviderUniqueName, keyStorageRoot, descriptorFormat);
    }
}
