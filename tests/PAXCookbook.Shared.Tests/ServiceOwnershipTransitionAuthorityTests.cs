using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// ===========================================================================
// CYCLE 88 - THE PURE LEDGER TRANSITION AUTHORITY
// ===========================================================================
//
// SCOPE, STATED PLAINLY. Everything here runs against PURE, PORTABLE contract
// code. Nothing in this file opens a file, composes a path, reads an environment
// variable or a clock, opens a certificate store, touches a private key, reads
// or writes an ACL, reads the registry, installs or starts a service, elevates,
// reads or writes ProgramData, opens a socket, or starts a process. Every SID,
// thumbprint, key identity and descriptor below is SYNTHETIC.
public class ServiceOwnershipTransitionAuthorityTests
{
    // ---- synthetic fixture values --------------------------------------------

    private const string OwnerSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string SvcSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890";
    private const string Thumb = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string Thumb2 = "0123456789ABCDEF0123456789ABCDEF01234567";
    private const string KeyId = "synthetic-key-identity_01.test";
    private const string KeyId2 = "synthetic-key-identity_02.test";
    private const string ProviderUniqueNameValue = "synthetic-unique-leaf_01.pvk";
    private const string ProviderUniqueNameValue2 = "synthetic-unique-leaf_02.pvk";
    private const string KeyStorageRootToken = "microsoft-software-key-storage-provider-machine-keys";
    private const string CredentialKindToken = "personal-app-registration-certificate";
    private const string ProvenanceToken = "referenced";
    private const string Stamp = "2026-08-06T00:00:00Z";
    private const string Stamp2 = "2026-08-06T00:00:01Z";
    private const string InstallId = "install-0001";
    private const string EntryId = "entry-0001";
    private const string EntryId2 = "entry-0002";
    private const string JobId = "job-0001";
    private const string JobId2 = "job-0002";
    private const string OperationId = "op-0001";

    /// <summary>
    /// The SAME structurally valid prior descriptor the acceptance suite uses:
    /// owner BUILTIN\Administrators, DACL present and protected, no SACL, exactly
    /// two ACEs, and NO service ACE - which the parser requires for a promotable
    /// captured prior state.
    /// </summary>
    private static readonly byte[] PriorBytes =
        { 0x01, 0x00, 0x04, 0x90, 0x14, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00, 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x15, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0xE9, 0x03, 0x00, 0x00, 0x02, 0x00, 0x34, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x03, 0x14, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x12, 0x00, 0x00, 0x00, 0x00, 0x03, 0x18, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00 };

    private static string PriorB64 => Convert.ToBase64String(PriorBytes);

    private static string PriorSha256 =>
        ToUpperHex(System.Security.Cryptography.SHA256.HashData(PriorBytes));

    private static string ToUpperHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // ---- fixture builders ----------------------------------------------------

    private static ServiceOwnershipTransitionFacts Facts(
        string entryId = EntryId,
        string keyIdentity = KeyId,
        string providerUniqueName = ProviderUniqueNameValue,
        string thumbprint = Thumb,
        string jobId = JobId,
        string owningUserSid = OwnerSid,
        string serviceSid = SvcSid,
        ServiceOwnershipPriorDaclState? priorState = null,
        string? priorBase64 = null,
        string? priorSha256 = null)
    {
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            CredentialKindToken, out ServiceOwnershipCredentialKind credentialKind));
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            ProvenanceToken, out ServiceOwnershipProvenance provenance));
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            KeyStorageRootToken, out ServiceOwnershipKeyStorageRoot keyStorageRoot));
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            "present", out ServiceOwnershipPriorDaclState presentState));

        ServiceOwnershipPriorDaclState state = priorState ?? presentState;

        Assert.True(
            ServiceOwnershipTransitionFacts.TryCreate(
                entryId,
                InstallId,
                owningUserSid,
                serviceSid,
                credentialKind,
                thumbprint,
                provenance,
                ServiceOwnershipLedgerContract.ApprovedRightsProfileProviderKind,
                ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
                keyIdentity,
                ServiceOwnershipLedgerContract.ApprovedRightsProfileGrantMechanism,
                new ServiceOwnershipRightsMask(ServiceOwnershipLedgerContract.ApprovedRightsProfileMask),
                ServiceOwnershipLedgerContract.RightsPolicyVersion,
                state,
                priorBase64 ?? PriorB64,
                priorSha256 ?? PriorSha256,
                jobId,
                providerUniqueName,
                keyStorageRoot,
                ServiceOwnershipLedgerContract.ApprovedDescriptorFormat,
                out ServiceOwnershipTransitionFacts? facts),
            "the synthetic facts fixture must be accepted");

        return facts!;
    }

    private static ServiceOwnershipTransitionResult Apply(
        ServiceOwnershipTransitionOperation operation,
        ServiceOwnershipLedgerValidationResult source,
        ServiceOwnershipTransitionFacts? facts = null,
        string entryId = EntryId,
        string promotedJobId = "",
        string stamp = Stamp)
    {
        Assert.True(
            ServiceOwnershipTransitionRequest.TryCreate(
                operation, source, facts, entryId, promotedJobId, OperationId, stamp,
                out ServiceOwnershipTransitionRequest? request),
            "the synthetic request fixture must be accepted");

        return ServiceOwnershipTransitionAuthority.Apply(request);
    }

    private static ServiceOwnershipLedgerValidationResult Absent() =>
        ServiceOwnershipLedgerValidator.ForAbsentLedger();

    private static ServiceOwnershipLedgerValidationResult Intent(
        ServiceOwnershipTransitionFacts? facts = null)
    {
        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.BeginPromotionIntent, Absent(), facts ?? Facts());
        Assert.True(result.IsTransitioned, "BeginPromotionIntent: " + result.Outcome);
        return result.Accepted!;
    }

    private static ServiceOwnershipLedgerValidationResult ActiveLedger()
    {
        ServiceOwnershipTransitionResult mutated = Apply(
            ServiceOwnershipTransitionOperation.RecordCredentialMutated, Intent());
        Assert.True(mutated.IsTransitioned, "RecordCredentialMutated: " + mutated.Outcome);

        ServiceOwnershipTransitionResult complete = Apply(
            ServiceOwnershipTransitionOperation.CompletePromotion, mutated.Accepted!);
        Assert.True(complete.IsTransitioned, "CompletePromotion: " + complete.Outcome);
        return complete.Accepted!;
    }

    // =======================================================================
    // THE FORWARD CHAIN
    // =======================================================================

    [Fact]
    public void An_absent_ledger_becomes_preparing_plus_intended_at_generation_one()
    {
        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.BeginPromotionIntent, Absent(), Facts());

        Assert.Equal(ServiceOwnershipTransitionOutcome.Transitioned, result.Outcome);
        Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, result.LedgerOutcome);
        Assert.Equal(1, result.Generation);

        ServiceOwnershipLedgerDocument document = result.Accepted!.Document!;
        Assert.Equal(ServiceOwnershipTransactionState.Preparing, document.TransactionState);
        Assert.Equal(InstallId, document.InstallationOwnershipId);
        Assert.Equal(OperationId, document.LastOperationId);

        ServiceOwnershipLedgerEntry entry = Assert.Single(document.Entries);
        Assert.Equal(ServiceOwnershipLifecycleState.Intended, entry.LifecycleState);
        Assert.Equal(EntryId, entry.EntryId);
        Assert.Equal(JobId, Assert.Single(entry.AssociatedPromotedJobIds));

        // The restore payload is present from the very first state.
        Assert.Equal(PriorB64, entry.PriorDaclBytesBase64);
        Assert.Equal(PriorSha256, entry.PriorDaclSha256);
    }

    [Fact]
    public void Preparing_becomes_credential_mutated_and_the_generation_advances()
    {
        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.RecordCredentialMutated, Intent());

        Assert.True(result.IsTransitioned);
        Assert.Equal(2, result.Generation);
        Assert.Equal(
            ServiceOwnershipTransactionState.CredentialMutated,
            result.Accepted!.Document!.TransactionState);
        Assert.Equal(
            ServiceOwnershipLifecycleState.Intended,
            result.Accepted.Document.Entries[0].LifecycleState);
    }

    [Fact]
    public void Credential_mutated_becomes_done_plus_active_and_reports_the_active_outcome()
    {
        ServiceOwnershipTransitionResult mutated = Apply(
            ServiceOwnershipTransitionOperation.RecordCredentialMutated, Intent());

        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.CompletePromotion, mutated.Accepted!);

        Assert.True(result.IsTransitioned);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Active, result.LedgerOutcome);
        Assert.Equal(3, result.Generation);
        Assert.Equal(
            ServiceOwnershipTransactionState.Done,
            result.Accepted!.Document!.TransactionState);
        Assert.Equal(
            ServiceOwnershipLifecycleState.Active,
            result.Accepted.Document.Entries[0].LifecycleState);
    }

    [Fact]
    public void Every_successful_transition_carries_bytes_that_revalidate_to_the_same_state()
    {
        foreach (ServiceOwnershipTransitionResult result in ForwardChain())
        {
            Assert.True(result.Serialized!.IsSerialized);
            Assert.NotEmpty(result.Serialized.Utf8Bytes);

            // No byte-order mark ever, because the product's read path refuses one.
            byte[] bytes = result.Serialized.Utf8Bytes;
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

            ServiceOwnershipLedgerValidationResult round =
                ServiceOwnershipLedgerValidator.Validate(result.Serialized.Json);

            Assert.True(round.IsAccepted);
            Assert.Equal(result.LedgerOutcome, round.Outcome);
            Assert.Equal(result.Generation, round.Document!.Generation);
        }
    }

    private static List<ServiceOwnershipTransitionResult> ForwardChain()
    {
        var chain = new List<ServiceOwnershipTransitionResult>();

        ServiceOwnershipTransitionResult intent = Apply(
            ServiceOwnershipTransitionOperation.BeginPromotionIntent, Absent(), Facts());
        chain.Add(intent);

        ServiceOwnershipTransitionResult mutated = Apply(
            ServiceOwnershipTransitionOperation.RecordCredentialMutated, intent.Accepted!);
        chain.Add(mutated);

        ServiceOwnershipTransitionResult complete = Apply(
            ServiceOwnershipTransitionOperation.CompletePromotion, mutated.Accepted!);
        chain.Add(complete);

        ServiceOwnershipTransitionResult restoring = Apply(
            ServiceOwnershipTransitionOperation.BeginRestore, complete.Accepted!);
        chain.Add(restoring);

        ServiceOwnershipTransitionResult restored = Apply(
            ServiceOwnershipTransitionOperation.MarkRestored, restoring.Accepted!);
        chain.Add(restored);

        ServiceOwnershipTransitionResult removed = Apply(
            ServiceOwnershipTransitionOperation.RemoveRestoredEntry, restored.Accepted!);
        chain.Add(removed);

        return chain;
    }

    // =======================================================================
    // THE RESTORE CHAIN
    // =======================================================================

    [Fact]
    public void Active_begins_restore_and_the_document_enters_the_restoring_transaction()
    {
        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.BeginRestore, ActiveLedger());

        Assert.True(result.IsTransitioned);
        Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, result.LedgerOutcome);
        Assert.Equal(
            ServiceOwnershipTransactionState.Restoring,
            result.Accepted!.Document!.TransactionState);
        Assert.Equal(
            ServiceOwnershipLifecycleState.Restoring,
            result.Accepted.Document.Entries[0].LifecycleState);
    }

    [Fact]
    public void Restoring_becomes_restored_and_then_the_entry_can_be_reaped_to_a_valid_empty_ledger()
    {
        ServiceOwnershipTransitionResult restoring = Apply(
            ServiceOwnershipTransitionOperation.BeginRestore, ActiveLedger());
        ServiceOwnershipTransitionResult restored = Apply(
            ServiceOwnershipTransitionOperation.MarkRestored, restoring.Accepted!);

        Assert.True(restored.IsTransitioned);
        Assert.Equal(
            ServiceOwnershipLifecycleState.Restored,
            restored.Accepted!.Document!.Entries[0].LifecycleState);

        ServiceOwnershipTransitionResult reaped = Apply(
            ServiceOwnershipTransitionOperation.RemoveRestoredEntry, restored.Accepted);

        Assert.True(reaped.IsTransitioned);
        Assert.Equal(ServiceOwnershipLedgerOutcome.ValidEmpty, reaped.LedgerOutcome);
        Assert.Empty(reaped.Accepted!.Document!.Entries);
        Assert.Equal(ServiceOwnershipTransactionState.Idle, reaped.Accepted.Document.TransactionState);
    }

    [Fact]
    public void An_abandoned_intent_entry_can_be_dropped_without_ever_becoming_active()
    {
        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.RemoveAbandonedIntentEntry, Intent());

        Assert.True(result.IsTransitioned);
        Assert.Equal(ServiceOwnershipLedgerOutcome.ValidEmpty, result.LedgerOutcome);
        Assert.Empty(result.Accepted!.Document!.Entries);
    }

    [Fact]
    public void A_job_association_can_be_removed_and_nothing_else_moves()
    {
        ServiceOwnershipLedgerValidationResult active = ActiveLedger();
        ServiceOwnershipLedgerEntry before = active.Document!.Entries[0];

        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.RemoveJobAssociation, active, promotedJobId: JobId);

        Assert.True(result.IsTransitioned);
        ServiceOwnershipLedgerEntry after = result.Accepted!.Document!.Entries[0];

        Assert.Empty(after.AssociatedPromotedJobIds);
        Assert.Equal(before.LifecycleState, after.LifecycleState);
        Assert.Equal(before.PriorDaclBytesBase64, after.PriorDaclBytesBase64);
        Assert.Equal(before.CertificateThumbprintSha1, after.CertificateThumbprintSha1);
    }

    [Fact]
    public void Removing_a_job_that_is_not_associated_is_refused()
    {
        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.RemoveJobAssociation, ActiveLedger(),
            promotedJobId: JobId2);

        Assert.Equal(ServiceOwnershipTransitionOutcome.TargetJobUnusable, result.Outcome);
        Assert.Null(result.Accepted);
        Assert.Null(result.Serialized);
    }

    // =======================================================================
    // STRUCTURAL STALE PRESERVATION
    // =======================================================================

    /// <summary>
    /// The ONLY properties MarkStale is permitted to move. Everything else on the
    /// entry must survive byte for byte, because those are the values a human needs
    /// to finish an interrupted job by hand.
    /// </summary>
    private static readonly string[] StaleMayChange =
    {
        nameof(ServiceOwnershipLedgerEntry.LifecycleState),
        nameof(ServiceOwnershipLedgerEntry.UpdatedUtc),
        nameof(ServiceOwnershipLedgerEntry.CapturedStateBindingSha256),
    };

    private static List<string> DifferingProperties(
        ServiceOwnershipLedgerEntry left, ServiceOwnershipLedgerEntry right)
    {
        var differing = new List<string>();

        foreach (PropertyInfo property in EntryProperties())
        {
            object? a = property.GetValue(left);
            object? b = property.GetValue(right);

            bool equal = a is IReadOnlyList<string> listA && b is IReadOnlyList<string> listB
                ? listA.SequenceEqual(listB, StringComparer.Ordinal)
                : Equals(a, b);

            if (!equal)
            {
                differing.Add(property.Name);
            }
        }

        return differing;
    }

    private static PropertyInfo[] EntryProperties() =>
        typeof(ServiceOwnershipLedgerEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void Mark_stale_preserves_every_captured_fact_and_moves_only_the_three_declared_properties()
    {
        ServiceOwnershipLedgerValidationResult active = ActiveLedger();
        ServiceOwnershipLedgerEntry before = active.Document!.Entries[0];

        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.MarkStale, active, stamp: Stamp2);

        Assert.True(result.IsTransitioned);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Stale, result.LedgerOutcome);

        ServiceOwnershipLedgerEntry after = Assert.Single(result.Accepted!.Document!.Entries);
        Assert.Equal(ServiceOwnershipLifecycleState.Stale, after.LifecycleState);

        // NAMED PRESERVATION, stated explicitly so a reader can audit the list.
        Assert.Equal(before.PriorDaclBytesBase64, after.PriorDaclBytesBase64);
        Assert.Equal(before.PriorDaclSha256, after.PriorDaclSha256);
        Assert.Equal(before.PriorDaclState, after.PriorDaclState);
        Assert.Equal(before.CertificateThumbprintSha1, after.CertificateThumbprintSha1);
        Assert.Equal(before.PrivateKeyProviderKind, after.PrivateKeyProviderKind);
        Assert.Equal(before.RightsProfileId, after.RightsProfileId);
        Assert.Equal(before.GrantMechanism, after.GrantMechanism);
        Assert.Equal(before.RightsPolicyVersion, after.RightsPolicyVersion);
        Assert.Equal(before.GrantedRightsMask, after.GrantedRightsMask);
        Assert.Equal(before.KeyIdentity, after.KeyIdentity);
        Assert.Equal(before.ProviderUniqueName, after.ProviderUniqueName);
        Assert.Equal(before.KeyStorageRoot, after.KeyStorageRoot);
        Assert.Equal(before.DescriptorFormat, after.DescriptorFormat);
        Assert.Equal(before.OwningUserSid, after.OwningUserSid);
        Assert.Equal(before.ServiceSid, after.ServiceSid);
        Assert.Equal(before.EntryId, after.EntryId);
        Assert.Equal(before.CreatedUtc, after.CreatedUtc);
        Assert.Equal(before.CredentialKind, after.CredentialKind);
        Assert.Equal(before.Provenance, after.Provenance);
        Assert.Equal(
            before.AssociatedPromotedJobIds,
            after.AssociatedPromotedJobIds,
            StringComparer.Ordinal);

        // ...and the SAME conclusion reached structurally, over every property the
        // type actually has, so the named list above cannot silently fall behind.
        Assert.Equal(
            StaleMayChange.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            DifferingProperties(before, after).ToArray());
    }

    /// <summary>
    /// REFLECTION CONTROL. If a property is added to the ledger entry later, this
    /// fails immediately rather than leaving the new field silently unprotected.
    /// </summary>
    [Fact]
    public void Every_entry_property_is_either_declared_changeable_or_covered_by_the_preservation_check()
    {
        string[] all = EntryProperties().Select(p => p.Name).ToArray();

        Assert.Equal(23, all.Length);

        foreach (string mayChange in StaleMayChange)
        {
            Assert.Contains(mayChange, all, StringComparer.Ordinal);
        }

        // The preservation set is the complement, and it is non-trivial.
        string[] preserved = all.Except(StaleMayChange, StringComparer.Ordinal).ToArray();
        Assert.Equal(20, preserved.Length);
    }

    /// <summary>
    /// MUTATION CONTROL. The structural comparator must FAIL when a captured field
    /// diverges. Two chains that differ in seven bounded facts are compared, and
    /// the comparator must name exactly those seven plus the derived binding.
    /// </summary>
    [Fact]
    public void MUTATION_the_preservation_comparator_detects_every_field_it_is_varied_on()
    {
        ServiceOwnershipTransitionResult a = Apply(
            ServiceOwnershipTransitionOperation.MarkStale, Intent(Facts()), stamp: Stamp2);

        ServiceOwnershipTransitionResult b = Apply(
            ServiceOwnershipTransitionOperation.MarkStale,
            Intent(Facts(
                entryId: EntryId2,
                keyIdentity: KeyId2,
                providerUniqueName: ProviderUniqueNameValue2,
                thumbprint: Thumb2,
                jobId: JobId2)),
            entryId: EntryId2,
            stamp: Stamp2);

        Assert.True(a.IsTransitioned);
        Assert.True(b.IsTransitioned);

        List<string> differing = DifferingProperties(
            a.Accepted!.Document!.Entries[0], b.Accepted!.Document!.Entries[0]);

        foreach (string expected in new[]
                 {
                     nameof(ServiceOwnershipLedgerEntry.EntryId),
                     nameof(ServiceOwnershipLedgerEntry.KeyIdentity),
                     nameof(ServiceOwnershipLedgerEntry.ProviderUniqueName),
                     nameof(ServiceOwnershipLedgerEntry.CertificateThumbprintSha1),
                     nameof(ServiceOwnershipLedgerEntry.AssociatedPromotedJobIds),
                     nameof(ServiceOwnershipLedgerEntry.CapturedStateBindingSha256),
                 })
        {
            Assert.Contains(expected, differing, StringComparer.Ordinal);
        }

        // ...and the comparator does NOT cry wolf on values that really are equal.
        Assert.DoesNotContain(
            nameof(ServiceOwnershipLedgerEntry.PriorDaclBytesBase64), differing, StringComparer.Ordinal);
        Assert.DoesNotContain(
            nameof(ServiceOwnershipLedgerEntry.OwningUserSid), differing, StringComparer.Ordinal);
    }

    /// <summary>
    /// MUTATION CONTROL. A DIFFERENT captured prior descriptor really is detected,
    /// which is the field whose silent loss would be an unrecoverable ACL.
    /// </summary>
    [Fact]
    public void MUTATION_a_dropped_prior_descriptor_payload_is_detected()
    {
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            "empty", out ServiceOwnershipPriorDaclState emptyState));

        ServiceOwnershipTransitionResult withPayload = Apply(
            ServiceOwnershipTransitionOperation.MarkStale, Intent(Facts()), stamp: Stamp2);

        ServiceOwnershipTransitionResult withoutPayload = Apply(
            ServiceOwnershipTransitionOperation.MarkStale,
            Intent(Facts(priorState: emptyState, priorBase64: "", priorSha256: "")),
            stamp: Stamp2);

        Assert.True(withPayload.IsTransitioned);
        Assert.True(withoutPayload.IsTransitioned);

        List<string> differing = DifferingProperties(
            withPayload.Accepted!.Document!.Entries[0],
            withoutPayload.Accepted!.Document!.Entries[0]);

        Assert.Contains(
            nameof(ServiceOwnershipLedgerEntry.PriorDaclBytesBase64), differing, StringComparer.Ordinal);
        Assert.Contains(
            nameof(ServiceOwnershipLedgerEntry.PriorDaclSha256), differing, StringComparer.Ordinal);
        Assert.Contains(
            nameof(ServiceOwnershipLedgerEntry.PriorDaclState), differing, StringComparer.Ordinal);
    }

    [Fact]
    public void Mark_stale_never_removes_an_entry_an_association_or_reports_completion()
    {
        ServiceOwnershipLedgerValidationResult active = ActiveLedger();

        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.MarkStale, active, stamp: Stamp2);

        Assert.Single(result.Accepted!.Document!.Entries);
        Assert.Single(result.Accepted.Document.Entries[0].AssociatedPromotedJobIds);

        // A stale ledger is NEVER the Active outcome, so nothing downstream can read
        // it as a finished, healthy promotion.
        Assert.Equal(ServiceOwnershipLedgerOutcome.Stale, result.LedgerOutcome);
        Assert.NotEqual(ServiceOwnershipLedgerOutcome.Active, result.LedgerOutcome);
    }

    [Fact]
    public void Marking_an_already_stale_entry_again_is_refused()
    {
        ServiceOwnershipTransitionResult first = Apply(
            ServiceOwnershipTransitionOperation.MarkStale, ActiveLedger(), stamp: Stamp2);

        ServiceOwnershipTransitionResult second = Apply(
            ServiceOwnershipTransitionOperation.MarkStale, first.Accepted!, stamp: Stamp2);

        Assert.Equal(ServiceOwnershipTransitionOutcome.TargetEntryUnusable, second.Outcome);
    }

    // =======================================================================
    // REFUSALS
    // =======================================================================

    [Fact]
    public void A_null_request_is_refused_without_throwing()
    {
        ServiceOwnershipTransitionResult result = ServiceOwnershipTransitionAuthority.Apply(null);

        Assert.Equal(ServiceOwnershipTransitionOutcome.InvalidRequest, result.Outcome);
        Assert.Null(result.Accepted);
        Assert.Equal(0, result.Generation);
    }

    [Fact]
    public void A_refused_source_can_never_reach_the_authority()
    {
        ServiceOwnershipLedgerValidationResult refused =
            ServiceOwnershipLedgerValidator.Validate("{ not json");
        Assert.True(refused.IsRefused);

        Assert.False(ServiceOwnershipTransitionRequest.TryCreate(
            ServiceOwnershipTransitionOperation.BeginPromotionIntent, refused, Facts(),
            EntryId, string.Empty, OperationId, Stamp, out _));
    }

    [Theory]
    [InlineData(ServiceOwnershipTransitionOperation.RecordCredentialMutated)]
    [InlineData(ServiceOwnershipTransitionOperation.CompletePromotion)]
    [InlineData(ServiceOwnershipTransitionOperation.MarkRestored)]
    public void A_transition_from_the_wrong_transaction_state_is_refused(
        ServiceOwnershipTransitionOperation operation)
    {
        ServiceOwnershipTransitionResult result = Apply(operation, ActiveLedger());

        Assert.NotEqual(ServiceOwnershipTransitionOutcome.Transitioned, result.Outcome);
        Assert.Null(result.Accepted);
    }

    [Fact]
    public void An_unknown_entry_id_is_refused()
    {
        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.BeginRestore, ActiveLedger(), entryId: "entry-9999");

        Assert.Equal(ServiceOwnershipTransitionOutcome.TargetEntryUnusable, result.Outcome);
    }

    [Fact]
    public void Beginning_an_intent_without_facts_is_refused()
    {
        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.BeginPromotionIntent, Absent());

        Assert.Equal(ServiceOwnershipTransitionOutcome.FactsUnusable, result.Outcome);
    }

    [Fact]
    public void Beginning_an_intent_on_a_ledger_that_already_holds_entries_is_refused()
    {
        ServiceOwnershipTransitionResult result = Apply(
            ServiceOwnershipTransitionOperation.BeginPromotionIntent, ActiveLedger(), Facts());

        Assert.Equal(ServiceOwnershipTransitionOutcome.SourceStateMismatch, result.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-08-06T00:00:00")]
    [InlineData("2026-08-06 00:00:00Z")]
    [InlineData("2026-08-06T00:00:00.000Z")]
    [InlineData("2026-08-06T00:00:00+00:00")]
    [InlineData("not-a-timestamp-at-all")]
    public void A_non_canonical_timestamp_can_never_form_a_request(string stamp)
    {
        Assert.False(ServiceOwnershipTransitionRequest.TryCreate(
            ServiceOwnershipTransitionOperation.BeginPromotionIntent, Absent(), Facts(),
            EntryId, string.Empty, OperationId, stamp, out _));
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("bad/id")]
    [InlineData("")]
    [InlineData(null)]
    public void A_non_bounded_operation_id_can_never_form_a_request(string? operationId)
    {
        Assert.False(ServiceOwnershipTransitionRequest.TryCreate(
            ServiceOwnershipTransitionOperation.BeginPromotionIntent, Absent(), Facts(),
            EntryId, string.Empty, operationId, Stamp, out _));
    }

    [Theory]
    [InlineData("bad entry id")]
    [InlineData("entry\"quote")]
    [InlineData("entry\\backslash")]
    public void Facts_reject_an_unbounded_entry_id(string entryId)
    {
        Assert.False(ServiceOwnershipTransitionFacts.TryCreate(
            entryId, InstallId, OwnerSid, SvcSid,
            ServiceOwnershipCredentialKind.Unspecified, Thumb,
            ServiceOwnershipProvenance.Unspecified,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileProviderKind,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, KeyId,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileGrantMechanism,
            new ServiceOwnershipRightsMask(ServiceOwnershipLedgerContract.ApprovedRightsProfileMask),
            ServiceOwnershipLedgerContract.RightsPolicyVersion,
            ServiceOwnershipPriorDaclState.Unspecified, PriorB64, PriorSha256, JobId,
            ProviderUniqueNameValue, ServiceOwnershipKeyStorageRoot.Unspecified,
            ServiceOwnershipLedgerContract.ApprovedDescriptorFormat, out _));
    }

    [Fact]
    public void An_unspecified_operation_can_never_form_a_request()
    {
        Assert.False(ServiceOwnershipTransitionRequest.TryCreate(
            ServiceOwnershipTransitionOperation.Unspecified, Absent(), Facts(),
            EntryId, string.Empty, OperationId, Stamp, out _));

        Assert.False(ServiceOwnershipTransitionRequest.TryCreate(
            (ServiceOwnershipTransitionOperation)9999, Absent(), Facts(),
            EntryId, string.Empty, OperationId, Stamp, out _));
    }

    // =======================================================================
    // PURITY AND CONTAINMENT, PROVEN FROM SOURCE AND BY REFLECTION
    // =======================================================================

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string AuthorityPath() =>
        Path.Combine(
            RepoRoot(), "src", "PAXCookbook.Shared", "Contracts",
            "ServiceOwnershipTransitionAuthority.cs");

    [Fact]
    public void The_transition_authority_performs_no_io_and_reads_no_environment_or_clock()
    {
        string raw = File.ReadAllText(AuthorityPath());
        string source = StripComments(raw);

        // POSITIVE CONTROL: the scan really read this file's CODE.
        Assert.Contains("class ServiceOwnershipTransitionAuthority", source, StringComparison.Ordinal);

        // ...and the stripper really removed something, so a comment-only mention
        // cannot be mistaken for the absence of a capability.
        Assert.True(source.Length < raw.Length, "the comment stripper removed nothing");

        foreach (string token in new[]
                 {
                     "System.IO", "File.", "Directory.", "FileStream", "Path.",
                     "Environment.", "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.UtcNow",
                     "Registry", "Process", "HttpClient", "Socket",
                     "X509", "CngKey", "AccessControl", "SecurityIdentifier", "NTAccount",
                     "DllImport", "LibraryImport", "Marshal", "IntPtr", "SafeHandle",
                     "Func<", "Action<", "delegate", "Random", "Guid.NewGuid",
                 })
        {
            Assert.DoesNotContain(token, source, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL for the token scan itself.
        Assert.Contains("File.", StripComments("class X { void M() { File.ReadAllText(\"a\"); } }"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Line and block comment removal. String-literal-unaware, exactly like the
    /// project's existing guard stripper, which is why the token list above avoids
    /// values that appear inside string literals.
    /// </summary>
    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
                sb.Append('\n');
                continue;
            }

            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }
                i++;
                continue;
            }

            sb.Append(source[i]);
        }
        return sb.ToString();
    }

    [Fact]
    public void The_transition_authority_is_static_stateless_and_takes_no_delegate()
    {
        Type type = typeof(ServiceOwnershipTransitionAuthority);

        Assert.True(type.IsAbstract && type.IsSealed, "the authority must be a static class");
        Assert.Empty(type.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            | BindingFlags.Instance | BindingFlags.DeclaredOnly));

        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.False(
                    typeof(Delegate).IsAssignableFrom(parameter.ParameterType),
                    type.Name + "." + method.Name + " accepts a delegate");
            }
        }

        // ONE public verb, and it is the only way in.
        MethodInfo apply = Assert.Single(type.GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Equal("Apply", apply.Name);
    }

    [Fact]
    public void The_authority_is_deterministic_for_the_same_inputs()
    {
        ServiceOwnershipTransitionResult first = Apply(
            ServiceOwnershipTransitionOperation.BeginPromotionIntent, Absent(), Facts());
        ServiceOwnershipTransitionResult second = Apply(
            ServiceOwnershipTransitionOperation.BeginPromotionIntent, Absent(), Facts());

        Assert.Equal(first.Serialized!.Json, second.Serialized!.Json, StringComparer.Ordinal);
        Assert.Equal(first.Serialized.Utf8Bytes, second.Serialized.Utf8Bytes);
    }

    /// <summary>
    /// The transition authority lives under src/, so its own type names are subject
    /// to the certified ledger-token guard. This checks them here too, at the point
    /// of authorship, so the name choice is deliberate rather than lucky.
    /// </summary>
    [Fact]
    public void No_type_this_cycle_added_to_shared_carries_any_guarded_ledger_token()
    {
        string[] guardedTokens =
        {
            "OwnershipLedgerReader", "OwnershipLedgerWriter", "OwnershipLedgerExecutor",
            "OwnershipLedgerObserver", "ServiceOwnershipLedgerStore", "ServiceOwnershipLedgerRepository",
        };

        string[] addedTypeNames =
        {
            nameof(ServiceOwnershipTransitionAuthority),
            nameof(ServiceOwnershipTransitionRequest),
            nameof(ServiceOwnershipTransitionResult),
            nameof(ServiceOwnershipTransitionFacts),
            nameof(ServiceOwnershipTransitionOperation),
            nameof(ServiceOwnershipTransitionOutcome),
        };

        foreach (string typeName in addedTypeNames)
        {
            foreach (string token in guardedTokens)
            {
                Assert.DoesNotContain(token, typeName, StringComparison.Ordinal);
            }
        }

        // POSITIVE CONTROL: the same containment check DOES fire on a name that
        // would trip the guard, so the zeros above are not vacuous.
        Assert.Contains("OwnershipLedgerWriter", "ServiceOwnershipLedgerWriter", StringComparison.Ordinal);
    }
}
