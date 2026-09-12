using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// Cycle 43 - PROMOTION REFUSAL SEMANTICS.
//
// WHAT THIS FILE PROVES, AND WHY IT IS NAME-BASED. Cycle 42 approved exactly one
// provider-specific rights profile. The refusal the planner returns for
// AssessPromotion was still called RightsProfileNotApproved, which asserts something
// that is now FALSE: a profile IS approved. What is actually true is that promotion
// itself is not authorised - there is no promotion transition and no executor. This
// file pins the corrected name PromotionNotAuthorized at the SAME numeric value 9
// and proves the stale name is gone from the compiled enum.
//
// Every assertion below reads the refusal by NAME rather than by compile-time
// symbol. That is deliberate and load-bearing: this exact file must COMPILE both
// before and after the rename so a genuine RED phase can be recorded and the RED and
// GREEN runs can be proven to have executed BYTE-IDENTICAL test source. A file that
// referenced the new symbol directly could only ever fail to compile, which proves
// nothing about behaviour.
//
// SCOPE. Nothing here opens a certificate store, touches a private key, reads or
// writes an ACL, reads the registry, installs or starts a service, elevates, opens a
// socket, spawns a process, runs PAX or starts a Bake. No file is read or written.
public class ServiceOwnershipPromotionRefusalSemanticsTests
{
    private const string CorrectedName = "PromotionNotAuthorized";
    private const string StaleName = "RightsProfileNotApproved";
    private const int PromotionRefusalValue = 9;

    // ---- synthetic fixture values -------------------------------------------
    //
    // Synthetic throughout. No real tenant, account, certificate, key, machine, job
    // or directory is represented.

    private const string InstallId = "install-0001";
    private const string OwnerSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string SvcSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890";
    private const string Thumb = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string KeyId = "synthetic-key-identity_01.test";
    private const string ProviderUniqueName = "synthetic-unique-leaf_01.pvk";
    private const string Mask = "00000081";
    private const string EntryId = "entry-0001";
    private const string JobId = "job-0001";
    private const string Stamp = "2026-08-06T00:00:00Z";

    private static readonly string[] PopulatedTransactionStates =
    {
        "preparing", "credential-mutated", "ledger-committed", "restoring", "done",
    };

    private static readonly string[] MatrixLifecycleStates = { "intended", "restoring", "restored" };

    // ===========================================================================
    // THE ENUM ITSELF
    // ===========================================================================

    [Fact]
    public void The_refusal_at_value_nine_is_named_for_the_missing_authorization()
    {
        Type reason = typeof(ServiceOwnershipPlanRefusalReason);
        string[] names = Enum.GetNames(reason);

        Assert.Equal(CorrectedName, Enum.GetName(reason, PromotionRefusalValue));
        Assert.Contains(CorrectedName, names);
        Assert.Equal(
            PromotionRefusalValue,
            (int)(ServiceOwnershipPlanRefusalReason)Enum.Parse(reason, CorrectedName));

        // The stale name must be gone from the COMPILED enum, not merely unused.
        Assert.DoesNotContain(StaleName, names);

        // POSITIVE CONTROL for the absence assertion above: the same lookup finds a
        // member that genuinely is present, so DoesNotContain is not vacuous.
        Assert.Contains("LedgerRefused", names);

        // Nothing was renumbered or reordered while the member was renamed.
        Assert.Equal(18, names.Length);
        Assert.Equal(
            "0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17",
            string.Join(
                ",",
                Enum.GetValues(reason)
                    .Cast<ServiceOwnershipPlanRefusalReason>()
                    .Select(v => (int)v)
                    .OrderBy(v => v)));
    }

    [Fact]
    public void The_action_vocabulary_is_unchanged_by_the_rename()
    {
        Assert.Equal(
            "0,1,2,3,4,5,6,7,8,9",
            string.Join(
                ",",
                Enum.GetValues(typeof(ServiceOwnershipPlanAction))
                    .Cast<ServiceOwnershipPlanAction>()
                    .Select(v => (int)v)
                    .OrderBy(v => v)));
        Assert.Equal(10, Enum.GetNames(typeof(ServiceOwnershipPlanAction)).Length);

        // POSITIVE CONTROL: an action that would grant, apply or execute anything has
        // never existed and still does not.
        Assert.DoesNotContain("GrantRights", Enum.GetNames(typeof(ServiceOwnershipPlanAction)));
        Assert.Contains("PersistLedger", Enum.GetNames(typeof(ServiceOwnershipPlanAction)));
    }

    // ===========================================================================
    // PLANNER BEHAVIOUR UNDER THE CORRECTED NAME
    // ===========================================================================

    [Fact]
    public void Assess_promotion_returns_the_corrected_refusal_for_absent_empty_and_populated_ledgers()
    {
        var accepted = new List<(string Label, ServiceOwnershipLedgerValidationResult Result)>
        {
            ("absent", ServiceOwnershipLedgerValidator.ForAbsentLedger()),
            ("empty", Validated(Ledger("idle"))),
        };

        foreach (string tx in PopulatedTransactionStates)
        {
            foreach (string life in MatrixLifecycleStates)
            {
                accepted.Add((tx + "/" + life, Validated(Ledger(tx, Entry(lifecycle: life)))));
            }
        }

        Assert.Equal(17, accepted.Count);

        foreach ((string label, ServiceOwnershipLedgerValidationResult result) in accepted)
        {
            Assert.True(result.IsAccepted, label);

            ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(Assess(result));

            Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
            Assert.Equal(CorrectedName, NameOf(plan.RefusalReason));
            Assert.Empty(plan.Actions);
            Assert.NotNull(plan.ObservedLedgerOutcome);
            Assert.Equal(result.Outcome, plan.ObservedLedgerOutcome!.Value);
        }
    }

    [Fact]
    public void A_refused_ledger_still_wins_precedence_over_the_promotion_refusal()
    {
        var refused = new List<(string Label, ServiceOwnershipLedgerValidationResult Result)>
        {
            ("malformed", ServiceOwnershipLedgerValidator.Validate("{ this is not json")),
            ("inconsistent", ServiceOwnershipLedgerValidator.Validate(
                Ledger("preparing", Entry(lifecycle: "active")))),
        };

        foreach ((string label, ServiceOwnershipLedgerValidationResult result) in refused)
        {
            Assert.True(result.IsRefused, label);

            ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(Assess(result));

            Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
            Assert.Equal("LedgerRefused", NameOf(plan.RefusalReason));
            Assert.NotEqual(CorrectedName, NameOf(plan.RefusalReason));
            Assert.Empty(plan.Actions);
        }
    }

    [Fact]
    public void Every_assess_promotion_result_emits_zero_actions_and_records_no_intent()
    {
        int observed = 0;

        foreach (ServiceOwnershipLedgerValidationResult result in AllLedgerShapes())
        {
            ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(Assess(result));

            Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
            Assert.Empty(plan.Actions);

            // "No intent recorded" is structural: the only intent-bearing step in the
            // vocabulary is never present, and neither is any other step.
            Assert.DoesNotContain(ServiceOwnershipPlanAction.PersistLedger, plan.Actions);
            observed++;
        }

        // POSITIVE CONTROL for the emptiness assertion: a non-promotion operation on
        // the same fixture family DOES produce a non-empty action list, so
        // Assert.Empty above is discriminating rather than trivially satisfied.
        ServiceOwnershipLifecyclePlan restore = ServiceOwnershipLifecyclePlanner.Plan(
            Restore(
                Validated(Ledger("credential-mutated", Entry(lifecycle: "intended"))),
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant));
        Assert.NotEmpty(restore.Actions);

        Assert.Equal(20, observed);
    }

    // ===========================================================================
    // THE CYCLE-42 PROFILE IS APPROVED AND PROMOTION IS STILL UNAVAILABLE
    // ===========================================================================

    [Fact]
    public void The_approved_profile_remains_exact_while_promotion_stays_unavailable()
    {
        Assert.True(ServiceOwnershipLedgerContract.HasApprovedRightsProfile);
        Assert.Equal(0x00120009u, ServiceOwnershipLedgerContract.ApprovedRightsProfileMask);
        Assert.Equal(3, ServiceOwnershipLedgerContract.RightsPolicyVersion);
        Assert.Equal(3, ServiceOwnershipLedgerContract.LedgerSchemaVersion);
        Assert.Equal(
            ServiceOwnershipRightsProfileId
                .MicrosoftSoftwareKspBackingFileRsa2048Ps256AzureIdentity1_18_0Msal4_82_1GraphAuth2_39_0FileSystemRights,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId);

        // Slot 2 stays retired and no active-acceptance outcome reappeared.
        Assert.DoesNotContain("ValidActive", Enum.GetNames(typeof(ServiceOwnershipLedgerOutcome)));
        Assert.False(Enum.IsDefined(typeof(ServiceOwnershipLedgerOutcome), 2));

        // A profile being approved buys NOTHING for promotion.
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            Assess(Validated(Ledger("preparing", Entry()))));
        Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
        Assert.Equal(CorrectedName, NameOf(plan.RefusalReason));
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Neither_the_planner_nor_its_tests_still_claim_no_approved_profile_exists()
    {
        Type reason = typeof(ServiceOwnershipPlanRefusalReason);

        // The corrected vocabulary says what is true - promotion is not authorised -
        // and no member left in the enum asserts the absent-profile claim.
        Assert.Empty(Enum.GetNames(reason).Where(n => n.Contains("NotApproved", StringComparison.Ordinal)));

        // POSITIVE CONTROL: the same substring scan finds a substring that IS present.
        Assert.NotEmpty(Enum.GetNames(reason).Where(n => n.Contains("Promotion", StringComparison.Ordinal)));
    }

    // ===========================================================================
    // HELPERS
    // ===========================================================================

    private static string NameOf(ServiceOwnershipPlanRefusalReason reason)
        => Enum.GetName(typeof(ServiceOwnershipPlanRefusalReason), reason)!;

    private static IEnumerable<ServiceOwnershipLedgerValidationResult> AllLedgerShapes()
    {
        yield return ServiceOwnershipLedgerValidator.ForAbsentLedger();
        yield return Validated(Ledger("idle"));
        yield return ServiceOwnershipLedgerValidator.Validate("{ this is not json");
        yield return ServiceOwnershipLedgerValidator.Validate(Ledger("preparing", Entry(lifecycle: "active")));
        yield return Validated(Ledger("credential-mutated", Entry(lifecycle: "stale")));

        foreach (string tx in PopulatedTransactionStates)
        {
            foreach (string life in MatrixLifecycleStates)
            {
                yield return Validated(Ledger(tx, Entry(lifecycle: life)));
            }
        }
    }

    private static ServiceOwnershipLifecyclePlanRequest Assess(ServiceOwnershipLedgerValidationResult result)
    {
        ServiceOwnershipLifecyclePlanRequest? request =
            ServiceOwnershipLifecyclePlanRequest.ForAssessPromotion(result, InstallId, OwnerSid, SvcSid);
        Assert.NotNull(request);
        return request!;
    }

    private static ServiceOwnershipLifecyclePlanRequest Restore(
        ServiceOwnershipLedgerValidationResult result, ServiceOwnershipCredentialObservation observation)
    {
        ServiceOwnershipLifecyclePlanRequest? request =
            ServiceOwnershipLifecyclePlanRequest.ForRestoreCredential(
                result, InstallId, OwnerSid, SvcSid, EntryId, observation);
        Assert.NotNull(request);
        return request!;
    }

    // ---- ledger fixture builder ----------------------------------------------

    private static ServiceOwnershipLedgerValidationResult Validated(string json)
    {
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(json);
        Assert.True(result.IsAccepted, "fixture was expected to validate: " + result.Reason);
        return result;
    }

    private static string Ledger(string transactionState, params string[] entries)
    {
        var sb = new StringBuilder("{");
        sb.Append("\"schemaVersion\":3,");
        sb.Append("\"productOwnershipMarker\":\"")
          .Append(ServiceOwnershipLedgerContract.ProductOwnershipMarker).Append("\",");
        sb.Append("\"managedFeatureId\":\"")
          .Append(ServiceOwnershipLedgerContract.ManagedFeatureId).Append("\",");
        sb.Append("\"installationOwnershipId\":\"").Append(InstallId).Append("\",");
        sb.Append("\"generation\":1,");
        sb.Append("\"transactionState\":\"").Append(transactionState).Append("\",");
        sb.Append("\"entries\":[").Append(string.Join(",", entries)).Append("],");
        sb.Append("\"createdUtc\":\"").Append(Stamp).Append("\",");
        sb.Append("\"updatedUtc\":\"").Append(Stamp).Append("\",");
        sb.Append("\"lastOperationId\":\"op-0001\"}");
        return sb.ToString();
    }

    private static string Entry(string entryId = EntryId, string lifecycle = "intended")
    {
        byte[] priorBytes =
            { 0x01, 0x00, 0x04, 0x90, 0x14, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00, 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x15, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0xE9, 0x03, 0x00, 0x00, 0x02, 0x00, 0x34, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x03, 0x14, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x12, 0x00, 0x00, 0x00, 0x00, 0x03, 0x18, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00 };
        string priorB64 = Convert.ToBase64String(priorBytes);
        string priorSha = ToUpperHex(System.Security.Cryptography.SHA256.HashData(priorBytes));

        ServiceOwnershipLedgerContract.TryParseWireToken("present", out ServiceOwnershipPriorDaclState prior);

        string binding = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            1,
            OwnerSid,
            SvcSid,
            Thumb,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            KeyId,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            Mask,
            prior,
            priorSha,
            ProviderUniqueName,
            ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1);

        var sb = new StringBuilder("{");
        sb.Append("\"entryId\":\"").Append(entryId).Append("\",");
        sb.Append("\"owningUserSid\":\"").Append(OwnerSid).Append("\",");
        sb.Append("\"serviceSid\":\"").Append(SvcSid).Append("\",");
        sb.Append("\"credentialKind\":\"personal-app-registration-certificate\",");
        sb.Append("\"certificateThumbprintSha1\":\"").Append(Thumb).Append("\",");
        sb.Append("\"provenance\":\"referenced\",");
        sb.Append("\"privateKeyProviderKind\":\"microsoft-software-key-storage-provider\",");
        sb.Append("\"rightsProfileId\":\"")
          .Append(ServiceOwnershipLedgerContract.ToWireToken(
              ServiceOwnershipLedgerContract.ApprovedRightsProfileId))
          .Append("\",");
        sb.Append("\"keyIdentity\":\"").Append(KeyId).Append("\",");
        sb.Append("\"grantMechanism\":\"microsoft-software-ksp-backing-file-dacl\",");
        sb.Append("\"grantedRightsMask\":\"").Append(Mask).Append("\",");
        sb.Append("\"rightsPolicyVersion\":3,");
        sb.Append("\"priorDaclState\":\"present\",");
        sb.Append("\"priorDaclBytesBase64\":\"").Append(priorB64).Append("\",");
        sb.Append("\"priorDaclSha256\":\"").Append(priorSha).Append("\",");
        sb.Append("\"capturedStateBindingSha256\":\"").Append(binding).Append("\",");
        sb.Append("\"associatedPromotedJobIds\":[\"").Append(JobId).Append("\"],");
        sb.Append("\"lifecycleState\":\"").Append(lifecycle).Append("\",");
        sb.Append("\"createdUtc\":\"").Append(Stamp).Append("\",");
        sb.Append("\"updatedUtc\":\"").Append(Stamp).Append("\",");
        sb.Append("\"providerUniqueName\":\"").Append(ProviderUniqueName).Append("\",");
        sb.Append("\"keyStorageRoot\":\"microsoft-software-key-storage-provider-machine-keys\",");
        sb.Append("\"descriptorFormat\":\"microsoft-software-ksp-backing-file-self-relative-v1\"}");
        return sb.ToString();
    }

    private static string ToUpperHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
