using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using PAXCookbook.Shared.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace PAXCookbook.Shared.Tests;

// Cycle 38b - SERVICE OWNERSHIP LIFECYCLE PLANNER.
//
// WHAT IS UNDER TEST. A pure, portable, deterministic function from
// (validated ledger result + expected identities + one bounded observation) to an
// ORDERED LIST OF SYMBOLIC ACTIONS. The planner performs no I/O of any kind: it
// opens no certificate store, touches no private key, reads or writes no ACL, reads
// no registry, reads no credential vault, starts no service or process, composes no
// path, opens no socket, and holds no delegate. It decides WHAT SHOULD HAPPEN. It
// never makes anything happen, and no executor exists in this cycle.
//
// WHY THE ACTIONS ARE SYMBOLIC. Every action name below describes a step over the
// LEDGER or over ALREADY-CAPTURED state. There is deliberately no action that grants
// access, applies a permission, imports a certificate, starts a service, runs a
// Cook, deletes a referenced certificate, or carries an arbitrary command. That
// absence is asserted, not merely intended.
//
// WHY THE MATRIX IS TWO DIMENSIONAL. The ledger's document-level transactionState
// and the target entry's lifecycleState are INDEPENDENT facts, and the schema's
// broad InProgress outcome collapses several genuinely different situations into one
// value. Branching on that broad outcome would silently merge them, so the planner
// branches on BOTH raw dimensions and never on InProgress itself. The proof of that
// is Ruling_A2_2 below: two documents that both validate to InProgress, differing
// ONLY in transactionState, must produce DIFFERENT plans.
public class ServiceOwnershipLifecyclePlannerTests
{
    private readonly ITestOutputHelper _output;

    public ServiceOwnershipLifecyclePlannerTests(ITestOutputHelper output) => _output = output;

    // ---- synthetic fixture values --------------------------------------------
    //
    // Everything below is synthetic. No real tenant, account, certificate, key,
    // machine, job or directory is represented. The values are deliberately
    // DISTINCTIVE so the no-leakage test can search for them literally.

    private const string InstallId = "install-0001";
    private const string OtherInstallId = "install-0002";
    private const string OwnerSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OtherOwnerSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string SvcSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890";
    private const string OtherSvcSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567891";
    private const string Thumb = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string KeyId = "synthetic-key-identity_01.test";
    private const string ProviderUniqueName = "synthetic-unique-leaf_01.pvk";
    private const string Mask = "00000081";
    private const string EntryId = "entry-0001";
    private const string OtherEntryId = "entry-0002";
    private const string JobId = "job-0001";
    private const string OtherJobId = "job-0002";
    private const string Stamp = "2026-08-06T00:00:00Z";

    private static readonly string[] IdentifierNeedles =
    {
        InstallId, OtherInstallId, OwnerSid, OtherOwnerSid, SvcSid, OtherSvcSid,
        Thumb, KeyId, EntryId, OtherEntryId, JobId, OtherJobId,
    };

    // The five COHERENT (transactionState, lifecycleState) pairs. Everything else
    // that the parser can produce is contradictory.
    private static readonly (string Tx, string Life)[] CoherentPairs =
    {
        ("preparing", "intended"),
        ("credential-mutated", "intended"),
        ("restoring", "restoring"),
        ("restoring", "restored"),
        ("done", "restored"),
    };

    // Every document transactionState a POPULATED ledger can carry. "idle" is
    // excluded because the parser refuses a populated idle ledger outright.
    private static readonly string[] PopulatedTransactionStates =
    {
        "preparing", "credential-mutated", "ledger-committed", "restoring", "done",
    };

    // Every lifecycleState a target entry can carry when the ledger is ACCEPTED and
    // NOT stale. "active" and "foreign" are refused by the parser; "stale" forces
    // the whole ledger to the Stale outcome, which never reaches the matrix.
    private static readonly string[] MatrixLifecycleStates = { "intended", "restoring", "restored" };

    private static readonly ServiceOwnershipCredentialObservation[] DefiniteObservations =
    {
        ServiceOwnershipCredentialObservation.Unavailable,
        ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
        ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
        ServiceOwnershipCredentialObservation.Diverged,
        ServiceOwnershipCredentialObservation.KeyIdentityMismatch,
    };

    private static readonly ServiceOwnershipPlannerOperation[] RecoveryOperations =
    {
        ServiceOwnershipPlannerOperation.RecoverInterrupted,
        ServiceOwnershipPlannerOperation.RestoreCredential,
        ServiceOwnershipPlannerOperation.DisableCleanup,
    };

    // The three ACTIONABLE job relations. Unspecified and NotApplicable are refused
    // by ForUnpublishJob before a request exists, so they are not matrix inputs.
    private static readonly ServiceOwnershipJobRelation[] ActionableJobRelations =
    {
        ServiceOwnershipJobRelation.JobNotAssociated,
        ServiceOwnershipJobRelation.OtherJobsRemain,
        ServiceOwnershipJobRelation.FinalAssociatedJob,
    };

    // ---- expected action sequences, spelled out here rather than imported -----

    private static readonly ServiceOwnershipPlanAction[] None = Array.Empty<ServiceOwnershipPlanAction>();

    private static readonly ServiceOwnershipPlanAction[] AbandonIntent =
    {
        ServiceOwnershipPlanAction.RemoveAbandonedIntentEntry,
        ServiceOwnershipPlanAction.PersistLedger,
    };

    private static readonly ServiceOwnershipPlanAction[] FullRollback =
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

    private static readonly ServiceOwnershipPlanAction[] FinalizeRestore =
    {
        ServiceOwnershipPlanAction.MarkRestored,
        ServiceOwnershipPlanAction.PersistLedger,
        ServiceOwnershipPlanAction.RemoveRestoredEntry,
        ServiceOwnershipPlanAction.PersistLedger,
    };

    private static readonly ServiceOwnershipPlanAction[] RemoveRestored =
    {
        ServiceOwnershipPlanAction.RemoveRestoredEntry,
        ServiceOwnershipPlanAction.PersistLedger,
    };

    private static readonly ServiceOwnershipPlanAction[] GoStale =
    {
        ServiceOwnershipPlanAction.MarkStale,
        ServiceOwnershipPlanAction.PersistLedger,
    };

    private static readonly ServiceOwnershipPlanAction[] DropJob =
    {
        ServiceOwnershipPlanAction.RemoveJobAssociation,
        ServiceOwnershipPlanAction.PersistLedger,
    };

    // Actions that must NEVER appear in an UnpublishJob plan formed against a
    // CONTRADICTORY pair: the association-removal that the pre-repair ordering
    // emitted, plus every restore-shaped and entry-removal step.
    private static readonly ServiceOwnershipPlanAction[] ForbiddenOnContradictoryUnpublish =
    {
        ServiceOwnershipPlanAction.RemoveJobAssociation,
        ServiceOwnershipPlanAction.BeginRestore,
        ServiceOwnershipPlanAction.RestoreCapturedPriorDacl,
        ServiceOwnershipPlanAction.VerifyCapturedPriorDacl,
        ServiceOwnershipPlanAction.MarkRestored,
        ServiceOwnershipPlanAction.RemoveRestoredEntry,
    };

    // ===========================================================================
    // VOCABULARY
    // ===========================================================================

    [Fact]
    public void Every_planner_enum_pins_its_numbering_and_its_invalid_zero()
    {
        Assert.Equal(0, (int)ServiceOwnershipPlannerOperation.Unspecified);
        Assert.Equal(1, (int)ServiceOwnershipPlannerOperation.AssessPromotion);
        Assert.Equal(2, (int)ServiceOwnershipPlannerOperation.Verify);
        Assert.Equal(3, (int)ServiceOwnershipPlannerOperation.RecoverInterrupted);
        Assert.Equal(4, (int)ServiceOwnershipPlannerOperation.UnpublishJob);
        Assert.Equal(5, (int)ServiceOwnershipPlannerOperation.RestoreCredential);
        Assert.Equal(6, (int)ServiceOwnershipPlannerOperation.DisableCleanup);
        Assert.Equal(7, Enum.GetValues(typeof(ServiceOwnershipPlannerOperation)).Length);

        Assert.Equal(0, (int)ServiceOwnershipCredentialObservation.Unspecified);
        Assert.Equal(1, (int)ServiceOwnershipCredentialObservation.NotApplicable);
        Assert.Equal(2, (int)ServiceOwnershipCredentialObservation.Unavailable);
        Assert.Equal(3, (int)ServiceOwnershipCredentialObservation.MatchesCapturedPriorState);
        Assert.Equal(4, (int)ServiceOwnershipCredentialObservation.MatchesRecordedGrant);
        Assert.Equal(5, (int)ServiceOwnershipCredentialObservation.Diverged);
        Assert.Equal(6, (int)ServiceOwnershipCredentialObservation.KeyIdentityMismatch);

        Assert.Equal(0, (int)ServiceOwnershipJobRelation.Unspecified);
        Assert.Equal(1, (int)ServiceOwnershipJobRelation.NotApplicable);
        Assert.Equal(2, (int)ServiceOwnershipJobRelation.JobNotAssociated);
        Assert.Equal(3, (int)ServiceOwnershipJobRelation.OtherJobsRemain);
        Assert.Equal(4, (int)ServiceOwnershipJobRelation.FinalAssociatedJob);

        Assert.Equal(0, (int)ServiceOwnershipPlanAction.Unspecified);
        Assert.Equal(0, (int)ServiceOwnershipPlanOutcome.NoAction);
        Assert.Equal(0, (int)ServiceOwnershipPlanRefusalReason.None);
    }

    [Fact]
    public void No_grant_apply_import_start_run_or_delete_action_exists_anywhere_in_the_assembly()
    {
        // 1. The exact forbidden spellings exist nowhere in the assembly, as a TYPE
        //    name or as ANY enum member name.
        string[] forbiddenExact =
        {
            "ApplyGrant", "GrantAccess", "ImportCertificate", "StartService",
            "RunCook", "DeleteReferencedCertificate",
        };

        Assembly assembly = typeof(ServiceOwnershipLifecyclePlanner).Assembly;
        var allEnumMemberNames = new List<string>();
        var allTypeNames = new List<string>();

        foreach (Type type in assembly.GetTypes())
        {
            allTypeNames.Add(type.Name);
            if (type.IsEnum)
            {
                allEnumMemberNames.AddRange(Enum.GetNames(type));
            }
        }

        Assert.NotEmpty(allTypeNames);
        Assert.NotEmpty(allEnumMemberNames);

        foreach (string forbidden in forbiddenExact)
        {
            Assert.DoesNotContain(forbidden, allEnumMemberNames);
            Assert.DoesNotContain(forbidden, allTypeNames);
        }

        // 2. The action vocabulary itself carries NO verb that could authorise a
        //    grant, an import, an execution, a deletion of a referenced credential,
        //    or an arbitrary command. This is the part that matters: a generic
        //    "run this" action would defeat the entire design.
        string[] forbiddenFragments =
        {
            "Grant", "Apply", "Import", "Start", "Run", "Execute", "Invoke",
            "Command", "Shell", "Process", "Install", "Elevate", "Register",
            "Delete", "Certificate", "PrivateKey", "Registry", "Network",
        };

        foreach (string name in Enum.GetNames(typeof(ServiceOwnershipPlanAction)))
        {
            foreach (string fragment in forbiddenFragments)
            {
                Assert.False(
                    name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    "action '" + name + "' contains forbidden verb fragment '" + fragment + "'");
            }
        }

        // 3. The vocabulary is EXACTLY the approved ten, in the approved order.
        Assert.Equal(
            new[]
            {
                "Unspecified", "RemoveAbandonedIntentEntry", "PersistLedger", "BeginRestore",
                "RestoreCapturedPriorDacl", "VerifyCapturedPriorDacl", "MarkRestored",
                "RemoveRestoredEntry", "RemoveJobAssociation", "MarkStale",
            },
            Enum.GetNames(typeof(ServiceOwnershipPlanAction)));
    }

    [Fact]
    public void The_planner_holds_no_delegate_and_no_strategy_input()
    {
        // A delegate-shaped member would reintroduce arbitrary behaviour through the
        // back door, which is exactly what the symbolic action vocabulary exists to
        // prevent.
        foreach (Type type in new[]
                 {
                     typeof(ServiceOwnershipLifecyclePlanner),
                     typeof(ServiceOwnershipLifecyclePlanRequest),
                     typeof(ServiceOwnershipLifecyclePlan),
                 })
        {
            foreach (MemberInfo member in type.GetMembers(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                Type? memberType = member switch
                {
                    PropertyInfo p => p.PropertyType,
                    FieldInfo f => f.FieldType,
                    _ => null,
                };
                if (memberType is not null)
                {
                    Assert.False(
                        typeof(Delegate).IsAssignableFrom(memberType),
                        type.Name + "." + member.Name + " is delegate-shaped");
                }
            }

            foreach (MethodInfo method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.False(
                        typeof(Delegate).IsAssignableFrom(parameter.ParameterType),
                        type.Name + "." + method.Name + " takes a delegate parameter");
                }
            }
        }
    }

    [Fact]
    public void The_validation_result_cannot_be_fabricated_by_a_caller()
    {
        // The planner consumes ONLY the schema's own result type, whose constructors
        // are inaccessible. A caller cannot hand the planner a hand-built "accepted"
        // verdict; it must come from the validator.
        ConstructorInfo[] ctors = typeof(ServiceOwnershipLedgerValidationResult)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(ctors);

        ConstructorInfo[] requestCtors = typeof(ServiceOwnershipLifecyclePlanRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(requestCtors);

        ConstructorInfo[] planCtors = typeof(ServiceOwnershipLifecyclePlan)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(planCtors);
    }

    // ===========================================================================
    // GLOBAL PRECEDENCE
    // ===========================================================================

    [Fact]
    public void A_null_request_is_refused_as_an_invalid_request_with_zero_actions()
    {
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(null);

        Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
        Assert.Equal(ServiceOwnershipPlanRefusalReason.InvalidRequest, plan.RefusalReason);
        Assert.Empty(plan.Actions);
        Assert.Equal(ServiceOwnershipPlannerOperation.Unspecified, plan.Operation);
        Assert.Null(plan.ObservedLedgerOutcome);
    }

    [Fact]
    public void Every_factory_rejects_a_null_or_contradictory_or_out_of_range_input()
    {
        ServiceOwnershipLedgerValidationResult ok = Validated(Ledger("preparing", Entry()));

        // null result
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(null, InstallId, OwnerSid, SvcSid, EntryId));
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForAssessPromotion(null, InstallId, OwnerSid, SvcSid));

        // null / malformed identity strings
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, null, OwnerSid, SvcSid, EntryId));
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, "", OwnerSid, SvcSid, EntryId));
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, "has space", OwnerSid, SvcSid, EntryId));
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, InstallId, null, SvcSid, EntryId));
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, InstallId, SvcSid, SvcSid, EntryId));
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, InstallId, OwnerSid, OwnerSid, EntryId));
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, InstallId, OwnerSid, SvcSid, null));
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, InstallId, OwnerSid, SvcSid, "not a token"));
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, InstallId, OwnerSid, SvcSid, new string('a', 200)));

        // an owner SID that is not a USER SID, and a service SID that is not a
        // per-service virtual account SID
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, InstallId, "S-1-5-18", SvcSid, EntryId));
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, InstallId, OwnerSid, "S-1-5-80-0", EntryId));

        // Unspecified and out-of-range observations
        foreach (ServiceOwnershipCredentialObservation bad in new[]
                 {
                     ServiceOwnershipCredentialObservation.Unspecified,
                     ServiceOwnershipCredentialObservation.NotApplicable,
                     (ServiceOwnershipCredentialObservation)int.MaxValue,
                     (ServiceOwnershipCredentialObservation)(-7),
                 })
        {
            Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(ok, InstallId, OwnerSid, SvcSid, EntryId, bad));
            Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForRestoreCredential(ok, InstallId, OwnerSid, SvcSid, EntryId, bad));
            Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForDisableCleanup(ok, InstallId, OwnerSid, SvcSid, EntryId, bad));
            Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob(
                ok, InstallId, OwnerSid, SvcSid, EntryId, JobId, bad, ServiceOwnershipJobRelation.OtherJobsRemain));
        }

        // Unspecified and out-of-range job relations
        foreach (ServiceOwnershipJobRelation bad in new[]
                 {
                     ServiceOwnershipJobRelation.Unspecified,
                     ServiceOwnershipJobRelation.NotApplicable,
                     (ServiceOwnershipJobRelation)int.MaxValue,
                     (ServiceOwnershipJobRelation)(-7),
                 })
        {
            Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob(
                ok, InstallId, OwnerSid, SvcSid, EntryId, JobId,
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant, bad));
        }

        // a missing or malformed job id
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob(
            ok, InstallId, OwnerSid, SvcSid, EntryId, null,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipJobRelation.OtherJobsRemain));
        Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob(
            ok, InstallId, OwnerSid, SvcSid, EntryId, "job id with spaces",
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipJobRelation.OtherJobsRemain));

        // the happy paths really do build, so the assertions above are not vacuous
        Assert.NotNull(ServiceOwnershipLifecyclePlanRequest.ForAssessPromotion(ok, InstallId, OwnerSid, SvcSid));
        Assert.NotNull(ServiceOwnershipLifecyclePlanRequest.ForVerify(ok, InstallId, OwnerSid, SvcSid, EntryId));
        Assert.NotNull(ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
            ok, InstallId, OwnerSid, SvcSid, EntryId, ServiceOwnershipCredentialObservation.MatchesRecordedGrant));
        Assert.NotNull(ServiceOwnershipLifecyclePlanRequest.ForRestoreCredential(
            ok, InstallId, OwnerSid, SvcSid, EntryId, ServiceOwnershipCredentialObservation.MatchesRecordedGrant));
        Assert.NotNull(ServiceOwnershipLifecyclePlanRequest.ForDisableCleanup(
            ok, InstallId, OwnerSid, SvcSid, EntryId, ServiceOwnershipCredentialObservation.MatchesRecordedGrant));
        Assert.NotNull(ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob(
            ok, InstallId, OwnerSid, SvcSid, EntryId, JobId,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipJobRelation.FinalAssociatedJob));
    }

    [Fact]
    public void A_refused_ledger_wins_over_every_operation_including_assess_promotion()
    {
        // All four refused OUTCOMES, each reached through a genuinely different
        // defect, and each driven through all six operations.
        var refusals = new (string Label, string Json, ServiceOwnershipLedgerOutcome Outcome)[]
        {
            ("malformed", "{ this is not json", ServiceOwnershipLedgerOutcome.Malformed),
            ("unsupported", LedgerCore("preparing", new[] { Entry() }, 1, null, null), ServiceOwnershipLedgerOutcome.Unsupported),
            ("foreign", LedgerCore("preparing", new[] { Entry() }, 3, "SomeOtherProduct.v1", null), ServiceOwnershipLedgerOutcome.Foreign),
            ("inconsistent", Ledger("preparing", Entry(lifecycle: "active")), ServiceOwnershipLedgerOutcome.Inconsistent),
        };

        foreach ((string label, string json, ServiceOwnershipLedgerOutcome expected) in refusals)
        {
            ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(json);
            Assert.True(result.IsRefused, label);
            Assert.Equal(expected, result.Outcome);

            foreach (ServiceOwnershipLifecyclePlanRequest request in AllOperations(result))
            {
                ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(request);

                Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
                Assert.Equal(ServiceOwnershipPlanRefusalReason.LedgerRefused, plan.RefusalReason);
                Assert.Empty(plan.Actions);
                Assert.Equal(expected, plan.ObservedLedgerOutcome);
            }
        }
    }

    // ===========================================================================
    // ABSENT AND EMPTY LEDGERS
    // ===========================================================================

    [Fact]
    public void An_absent_ledger_behaves_exactly_as_specified_per_operation()
    {
        ServiceOwnershipLedgerValidationResult absent = ServiceOwnershipLedgerValidator.ForAbsentLedger();
        Assert.Equal(ServiceOwnershipLedgerOutcome.Absent, absent.Outcome);

        AssertPlan(Verify(absent), ServiceOwnershipPlanOutcome.NoAction, ServiceOwnershipPlanRefusalReason.None, None);
        AssertPlan(Recover(absent, ServiceOwnershipCredentialObservation.MatchesRecordedGrant),
            ServiceOwnershipPlanOutcome.NoAction, ServiceOwnershipPlanRefusalReason.None, None);
        AssertPlan(Assess(absent), ServiceOwnershipPlanOutcome.Refused,
            ServiceOwnershipPlanRefusalReason.PromotionNotAuthorized, None);
        AssertPlan(Unpublish(absent, ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
                ServiceOwnershipJobRelation.FinalAssociatedJob),
            ServiceOwnershipPlanOutcome.Refused, ServiceOwnershipPlanRefusalReason.LedgerAbsent, None);
        AssertPlan(Restore(absent, ServiceOwnershipCredentialObservation.MatchesRecordedGrant),
            ServiceOwnershipPlanOutcome.Refused, ServiceOwnershipPlanRefusalReason.LedgerAbsent, None);
        AssertPlan(Disable(absent, ServiceOwnershipCredentialObservation.MatchesRecordedGrant),
            ServiceOwnershipPlanOutcome.NoAction, ServiceOwnershipPlanRefusalReason.None, None);
    }

    [Fact]
    public void A_valid_empty_ledger_behaves_exactly_as_specified_per_operation()
    {
        ServiceOwnershipLedgerValidationResult empty = Validated(Ledger("idle"));
        Assert.Equal(ServiceOwnershipLedgerOutcome.ValidEmpty, empty.Outcome);

        AssertPlan(Verify(empty), ServiceOwnershipPlanOutcome.NoAction, ServiceOwnershipPlanRefusalReason.None, None);
        AssertPlan(Recover(empty, ServiceOwnershipCredentialObservation.MatchesRecordedGrant),
            ServiceOwnershipPlanOutcome.NoAction, ServiceOwnershipPlanRefusalReason.None, None);
        AssertPlan(Assess(empty), ServiceOwnershipPlanOutcome.Refused,
            ServiceOwnershipPlanRefusalReason.PromotionNotAuthorized, None);
        AssertPlan(Unpublish(empty, ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
                ServiceOwnershipJobRelation.FinalAssociatedJob),
            ServiceOwnershipPlanOutcome.Refused, ServiceOwnershipPlanRefusalReason.EntryNotFound, None);
        AssertPlan(Restore(empty, ServiceOwnershipCredentialObservation.MatchesRecordedGrant),
            ServiceOwnershipPlanOutcome.Refused, ServiceOwnershipPlanRefusalReason.EntryNotFound, None);
        AssertPlan(Disable(empty, ServiceOwnershipCredentialObservation.MatchesRecordedGrant),
            ServiceOwnershipPlanOutcome.NoAction, ServiceOwnershipPlanRefusalReason.None, None);
    }

    // ===========================================================================
    // OWNERSHIP AND IDENTITY GATES
    // ===========================================================================

    [Fact]
    public void The_ownership_gates_refuse_before_the_matrix_and_emit_no_action()
    {
        ServiceOwnershipLedgerValidationResult result = Validated(Ledger("preparing", Entry()));
        Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, result.Outcome);

        var cases = new (string Label, ServiceOwnershipLifecyclePlanRequest? Request, ServiceOwnershipPlanRefusalReason Reason)[]
        {
            ("installation",
                ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
                    result, OtherInstallId, OwnerSid, SvcSid, EntryId,
                    ServiceOwnershipCredentialObservation.MatchesRecordedGrant),
                ServiceOwnershipPlanRefusalReason.InstallationOwnershipMismatch),
            ("owner",
                ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
                    result, InstallId, OtherOwnerSid, SvcSid, EntryId,
                    ServiceOwnershipCredentialObservation.MatchesRecordedGrant),
                ServiceOwnershipPlanRefusalReason.OwningPrincipalMismatch),
            ("service",
                ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
                    result, InstallId, OwnerSid, OtherSvcSid, EntryId,
                    ServiceOwnershipCredentialObservation.MatchesRecordedGrant),
                ServiceOwnershipPlanRefusalReason.ServicePrincipalMismatch),
            ("entry",
                ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
                    result, InstallId, OwnerSid, SvcSid, OtherEntryId,
                    ServiceOwnershipCredentialObservation.MatchesRecordedGrant),
                ServiceOwnershipPlanRefusalReason.EntryNotFound),
        };

        foreach ((string label, ServiceOwnershipLifecyclePlanRequest? request, ServiceOwnershipPlanRefusalReason reason) in cases)
        {
            Assert.NotNull(request);
            ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(request);
            Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
            Assert.Equal(reason, plan.RefusalReason);
            Assert.Empty(plan.Actions);
        }
    }

    [Fact]
    public void The_gates_are_ordered_installation_then_owner_then_service_then_entry()
    {
        // With EVERY gate simultaneously violated, the FIRST gate wins. Ordering is
        // observable behaviour, not an implementation detail: it decides which
        // refusal an operator is told to investigate.
        ServiceOwnershipLedgerValidationResult result = Validated(Ledger("preparing", Entry()));

        ServiceOwnershipLifecyclePlanRequest? all = ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
            result, OtherInstallId, OtherOwnerSid, OtherSvcSid, OtherEntryId,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant);
        Assert.NotNull(all);
        Assert.Equal(
            ServiceOwnershipPlanRefusalReason.InstallationOwnershipMismatch,
            ServiceOwnershipLifecyclePlanner.Plan(all).RefusalReason);

        ServiceOwnershipLifecyclePlanRequest? ownerOnward = ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
            result, InstallId, OtherOwnerSid, OtherSvcSid, OtherEntryId,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant);
        Assert.NotNull(ownerOnward);
        Assert.Equal(
            ServiceOwnershipPlanRefusalReason.OwningPrincipalMismatch,
            ServiceOwnershipLifecyclePlanner.Plan(ownerOnward).RefusalReason);

        ServiceOwnershipLifecyclePlanRequest? serviceOnward = ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
            result, InstallId, OwnerSid, OtherSvcSid, OtherEntryId,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant);
        Assert.NotNull(serviceOnward);
        Assert.Equal(
            ServiceOwnershipPlanRefusalReason.ServicePrincipalMismatch,
            ServiceOwnershipLifecyclePlanner.Plan(serviceOnward).RefusalReason);
    }

    [Fact]
    public void A_ledger_holding_a_second_owners_entry_is_refused_for_the_whole_document()
    {
        // Fail-closed: the gate is document-wide, not target-entry-wide. One foreign
        // principal anywhere in the ledger refuses the whole operation.
        string json = Ledger(
            "preparing",
            Entry(),
            Entry(entryId: OtherEntryId, ownerSid: OtherOwnerSid, thumb: "0123456789ABCDEF0123456789ABCDEF01234567", jobIds: new[] { OtherJobId }));

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(json);
        Assert.True(result.IsAccepted);
        Assert.Equal(2, result.Document!.Entries.Count);

        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            Recover(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant));

        Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
        Assert.Equal(ServiceOwnershipPlanRefusalReason.OwningPrincipalMismatch, plan.RefusalReason);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void A_duplicate_entry_id_is_refused_by_the_parser_so_ambiguity_never_reaches_the_planner()
    {
        // EntryAmbiguous is DEFENDED but PARSER-UNREACHABLE in this cycle: the schema
        // refuses duplicate entry ids before a planner ever sees the document. The
        // refusal is recorded here so the defence is not mistaken for dead weight.
        string json = Ledger("preparing", Entry(), Entry(thumb: "0123456789ABCDEF0123456789ABCDEF01234567", jobIds: new[] { OtherJobId }));

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(json);

        Assert.True(result.IsRefused);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.DuplicateEntryId, result.Reason);

        // And when it IS handed to the planner, precedence rule 2 refuses it.
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            Recover(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant));
        Assert.Equal(ServiceOwnershipPlanRefusalReason.LedgerRefused, plan.RefusalReason);
        Assert.Empty(plan.Actions);
    }

    // ===========================================================================
    // STALE
    // ===========================================================================

    [Fact]
    public void Any_stale_entry_poisons_the_whole_ledger_for_every_mutation_capable_operation()
    {
        // The target entry is PERFECTLY HEALTHY. A DIFFERENT entry is stale. That is
        // still a manual-action stop for anything that could mutate state.
        string json = Ledger(
            "credential-mutated",
            Entry(),
            Entry(entryId: OtherEntryId, lifecycle: "stale", thumb: "0123456789ABCDEF0123456789ABCDEF01234567", jobIds: new[] { OtherJobId }));

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(json);
        Assert.True(result.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Stale, result.Outcome);
        Assert.Equal(ServiceOwnershipLifecycleState.Intended, result.Document!.Entries[0].LifecycleState);

        foreach (ServiceOwnershipCredentialObservation observation in DefiniteObservations)
        {
            AssertPlan(Recover(result, observation), ServiceOwnershipPlanOutcome.Refused,
                ServiceOwnershipPlanRefusalReason.StaleManualActionRequired, None);
            AssertPlan(Restore(result, observation), ServiceOwnershipPlanOutcome.Refused,
                ServiceOwnershipPlanRefusalReason.StaleManualActionRequired, None);
            AssertPlan(Disable(result, observation), ServiceOwnershipPlanOutcome.Refused,
                ServiceOwnershipPlanRefusalReason.StaleManualActionRequired, None);

            foreach (ServiceOwnershipJobRelation relation in new[]
                     {
                         ServiceOwnershipJobRelation.JobNotAssociated,
                         ServiceOwnershipJobRelation.OtherJobsRemain,
                         ServiceOwnershipJobRelation.FinalAssociatedJob,
                     })
            {
                AssertPlan(Unpublish(result, observation, relation), ServiceOwnershipPlanOutcome.Refused,
                    ServiceOwnershipPlanRefusalReason.StaleManualActionRequired, None);
            }
        }

        // Verify still classifies, and promotion is still refused for its own reason.
        ServiceOwnershipLifecyclePlan verify = ServiceOwnershipLifecyclePlanner.Plan(Verify(result));
        Assert.Equal(ServiceOwnershipPlanOutcome.NoAction, verify.Outcome);
        Assert.Empty(verify.Actions);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Stale, verify.ObservedLedgerOutcome);

        AssertPlan(Assess(result), ServiceOwnershipPlanOutcome.Refused,
            ServiceOwnershipPlanRefusalReason.PromotionNotAuthorized, None);
    }

    // ===========================================================================
    // THE TWO-DIMENSIONAL MATRIX
    // ===========================================================================

    // EXACT COVERAGE CLAIM. This test iterates, and asserts on, exactly:
    //     15 parser-reachable (transactionState, lifecycleState) pairs
    //   x  5 definite credential observations
    //   x  6 operation/relation combinations
    //        = RecoverInterrupted, RestoreCredential, DisableCleanup,
    //          UnpublishJob x { JobNotAssociated, OtherJobsRemain, FinalAssociatedJob }
    //   = 450 asserted rows.
    //
    // It ALSO asserts Verify (75 checks) and AssessPromotion (75 checks) inside the
    // same loop. Those are counted and reported SEPARATELY and are deliberately NOT
    // folded into the 450, because Verify carries neither an observation nor a job
    // relation and AssessPromotion carries neither either - so neither of them is a
    // dimension of this matrix.
    //
    // WHAT THIS TEST DOES NOT COVER is printed at the end of the run rather than left
    // implied, so the name and the evidence cannot outrun the executable enumeration.
    [Fact]
    public void The_matrix_covers_15_pairs_x_5_observations_x_6_operation_relation_combinations()
    {
        int rows = 0;
        int recoveryRows = 0;
        int unpublishRows = 0;
        int verifyChecks = 0;
        int promotionChecks = 0;
        int coherentRows = 0;
        int contradictoryRows = 0;

        _output.WriteLine(
            "2-D MATRIX COVERAGE - 15 pairs x 5 observations x 6 operation/relation combinations = 450 rows");
        _output.WriteLine(
            "the 6 combinations are RecoverInterrupted, RestoreCredential, DisableCleanup, and");
        _output.WriteLine(
            "UnpublishJob under each of JobNotAssociated, OtherJobsRemain and FinalAssociatedJob");
        _output.WriteLine(
            "Verify and AssessPromotion are asserted in the same loop and counted SEPARATELY");
        _output.WriteLine("legend: actions are listed in EXACT emitted order; [] means zero actions");
        _output.WriteLine(string.Empty);

        foreach (string tx in PopulatedTransactionStates)
        {
            foreach (string life in MatrixLifecycleStates)
            {
                bool coherent = CoherentPairs.Contains((tx, life));
                ServiceOwnershipLedgerValidationResult result = Validated(Ledger(tx, Entry(lifecycle: life)));

                // Every populated non-stale accepted document reports the SAME broad
                // outcome. That is precisely why the planner must not branch on it.
                Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, result.Outcome);

                foreach (ServiceOwnershipCredentialObservation observation in DefiniteObservations)
                {
                    (ServiceOwnershipPlanOutcome outcome, ServiceOwnershipPlanRefusalReason reason,
                        ServiceOwnershipPlanAction[] actions) = ExpectedRecovery(tx, life, observation);

                    foreach (ServiceOwnershipPlannerOperation operation in RecoveryOperations)
                    {
                        ServiceOwnershipLifecyclePlanRequest? request = operation switch
                        {
                            ServiceOwnershipPlannerOperation.RecoverInterrupted => Recover(result, observation),
                            ServiceOwnershipPlannerOperation.RestoreCredential => Restore(result, observation),
                            _ => Disable(result, observation),
                        };

                        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(request);

                        string label = tx + " + " + life + " + " + observation + " + " + operation;
                        Assert.Equal(outcome, plan.Outcome);
                        Assert.Equal(reason, plan.RefusalReason);
                        Assert.True(
                            actions.SequenceEqual(plan.Actions),
                            label + " -> expected [" + string.Join(",", actions) + "] but got [" + string.Join(",", plan.Actions) + "]");

                        _output.WriteLine(
                            "  " + (coherent ? "COHERENT     " : "CONTRADICTORY") + "  " + label
                            + " => " + plan.Outcome + "/" + plan.RefusalReason
                            + " [" + string.Join(",", plan.Actions) + "]");
                        rows++;
                        recoveryRows++;
                    }

                    // UnpublishJob is driven through the SAME executable enumeration as
                    // the recovery operations, once per actionable relation. It is the
                    // ONLY operation carrying a caller-supplied relation, which is
                    // exactly why leaving it out of the matrix hid the ordering defect.
                    foreach (ServiceOwnershipJobRelation relation in ActionableJobRelations)
                    {
                        (ServiceOwnershipPlanOutcome jobOutcome, ServiceOwnershipPlanRefusalReason jobReason,
                            ServiceOwnershipPlanAction[] jobActions) =
                            ExpectedUnpublish(tx, life, observation, relation);

                        ServiceOwnershipLifecyclePlan plan =
                            ServiceOwnershipLifecyclePlanner.Plan(Unpublish(result, observation, relation));

                        string label = tx + " + " + life + " + " + observation
                            + " + UnpublishJob(" + relation + ")";
                        Assert.Equal(jobOutcome, plan.Outcome);
                        Assert.Equal(jobReason, plan.RefusalReason);
                        Assert.True(
                            jobActions.SequenceEqual(plan.Actions),
                            label + " -> expected [" + string.Join(",", jobActions) + "] but got [" + string.Join(",", plan.Actions) + "]");

                        _output.WriteLine(
                            "  " + (coherent ? "COHERENT     " : "CONTRADICTORY") + "  " + label
                            + " => " + plan.Outcome + "/" + plan.RefusalReason
                            + " [" + string.Join(",", plan.Actions) + "]");
                        rows++;
                        unpublishRows++;
                    }

                    // Verify never emits an action, whatever the pair.
                    ServiceOwnershipLifecyclePlan verify = ServiceOwnershipLifecyclePlanner.Plan(Verify(result));
                    Assert.Empty(verify.Actions);
                    if (coherent)
                    {
                        Assert.Equal(ServiceOwnershipPlanOutcome.NoAction, verify.Outcome);
                        Assert.Equal(ServiceOwnershipPlanRefusalReason.None, verify.RefusalReason);
                    }
                    else
                    {
                        Assert.Equal(ServiceOwnershipPlanOutcome.Refused, verify.Outcome);
                        Assert.Equal(ServiceOwnershipPlanRefusalReason.StateMismatch, verify.RefusalReason);
                    }

                    verifyChecks++;

                    // Promotion is refused for its own reason with zero actions, always.
                    AssertPlan(Assess(result), ServiceOwnershipPlanOutcome.Refused,
                        ServiceOwnershipPlanRefusalReason.PromotionNotAuthorized, None);
                    promotionChecks++;
                }

                if (coherent) { coherentRows++; } else { contradictoryRows++; }
            }
        }

        _output.WriteLine(string.Empty);
        _output.WriteLine("EXACT ROW COUNTS (numeric, and equal to what this test actually iterated)");
        _output.WriteLine("  pairs enumerated                        : " + (coherentRows + contradictoryRows)
            + " (coherent " + coherentRows + ", contradictory " + contradictoryRows + ")");
        _output.WriteLine("  definite observations per pair          : " + DefiniteObservations.Length);
        _output.WriteLine("  recovery operations per observation     : " + RecoveryOperations.Length);
        _output.WriteLine("  unpublish job relations per observation : " + ActionableJobRelations.Length);
        _output.WriteLine("  operation/relation combinations         : "
            + (RecoveryOperations.Length + ActionableJobRelations.Length));
        _output.WriteLine("  recovery rows asserted                  : " + recoveryRows);
        _output.WriteLine("  unpublish rows asserted                 : " + unpublishRows);
        _output.WriteLine("  TOTAL matrix rows asserted              : " + rows);
        _output.WriteLine("  Verify checks (counted separately)      : " + verifyChecks);
        _output.WriteLine("  AssessPromotion checks (separately)     : " + promotionChecks);
        _output.WriteLine(string.Empty);
        _output.WriteLine("NOT COVERED BY THIS TEST, stated so the name cannot outrun the enumeration:");
        _output.WriteLine("  * the Absent, ValidEmpty, Stale and refused ledger outcomes - each has its own test");
        _output.WriteLine("  * the 'idle' transactionState and the 'active'/'foreign'/'stale' lifecycleStates -");
        _output.WriteLine("    the validator refuses or reclassifies them before the planner is reached");
        _output.WriteLine("  * the non-actionable job relations Unspecified and NotApplicable -");
        _output.WriteLine("    ForUnpublishJob refuses them, so no request ever carries one");

        Assert.Equal(5, coherentRows);
        Assert.Equal(10, contradictoryRows);
        Assert.Equal(15, coherentRows + contradictoryRows);
        Assert.Equal(15 * 5 * 3, recoveryRows);
        Assert.Equal(15 * 5 * 3, unpublishRows);
        Assert.Equal(15 * 5 * 6, rows);
        Assert.Equal(450, rows);
        Assert.Equal(15 * 5, verifyChecks);
        Assert.Equal(15 * 5, promotionChecks);
    }

    [Fact]
    public void Unpublish_job_on_a_contradictory_pair_marks_stale_and_takes_no_other_action()
    {
        // Item 7. For EVERY contradictory pair, EVERY definite observation and EVERY
        // actionable job relation, the plan is EXACTLY MarkStale then PersistLedger -
        // identical to every recovery operation in the same position - and contains
        // NONE of the forbidden actions. No association is removed. No ACL step runs.
        int rows = 0;

        foreach (string tx in PopulatedTransactionStates)
        {
            foreach (string life in MatrixLifecycleStates)
            {
                if (CoherentPairs.Contains((tx, life)))
                {
                    continue;
                }

                ServiceOwnershipLedgerValidationResult result =
                    Validated(Ledger(tx, Entry(lifecycle: life, jobIds: new[] { JobId, OtherJobId })));

                foreach (ServiceOwnershipCredentialObservation observation in DefiniteObservations)
                {
                    foreach (ServiceOwnershipJobRelation relation in ActionableJobRelations)
                    {
                        ServiceOwnershipLifecyclePlan plan =
                            ServiceOwnershipLifecyclePlanner.Plan(Unpublish(result, observation, relation));

                        string label = tx + " + " + life + " + " + observation + " + " + relation;

                        Assert.Equal(ServiceOwnershipPlanOutcome.PlanAvailable, plan.Outcome);
                        Assert.Equal(ServiceOwnershipPlanRefusalReason.None, plan.RefusalReason);

                        // EXACTLY MarkStale, PersistLedger, in that order.
                        Assert.True(
                            new[]
                            {
                                ServiceOwnershipPlanAction.MarkStale,
                                ServiceOwnershipPlanAction.PersistLedger,
                            }.SequenceEqual(plan.Actions),
                            label + " -> expected [MarkStale,PersistLedger] but got ["
                            + string.Join(",", plan.Actions) + "]");

                        foreach (ServiceOwnershipPlanAction forbidden in ForbiddenOnContradictoryUnpublish)
                        {
                            Assert.False(
                                plan.Actions.Contains(forbidden),
                                label + " -> forbidden action " + forbidden + " appeared in ["
                                + string.Join(",", plan.Actions) + "]");
                        }

                        rows++;
                    }
                }
            }
        }

        // 10 contradictory pairs x 5 observations x 3 relations.
        Assert.Equal(10 * 5 * 3, rows);
        Assert.Equal(150, rows);
    }

    [Fact]
    public void The_pre_repair_ordering_would_have_removed_a_job_association_on_a_contradictory_pair()
    {
        // NEGATIVE CONTROL. Without this the new invariant is unfalsifiable: a test
        // that only asserts the CURRENT behaviour cannot show that the behaviour ever
        // differed. The local function below reproduces the PRE-REPAIR precedence, in
        // which the caller-supplied job relation was resolved BEFORE the
        // contradictory-pair coherence gate.
        const string Tx = "done";
        const string Life = "intended";
        Assert.False(CoherentPairs.Contains((Tx, Life)), "the fixture pair must be CONTRADICTORY");

        ServiceOwnershipLedgerValidationResult result =
            Validated(Ledger(Tx, Entry(lifecycle: Life, jobIds: new[] { JobId, OtherJobId })));

        // The old ordering: relation first, coherence second.
        static ServiceOwnershipPlanAction[] PreRepairOrdering(
            bool coherent, ServiceOwnershipJobRelation relation)
        {
            switch (relation)
            {
                case ServiceOwnershipJobRelation.JobNotAssociated:
                    return None;
                case ServiceOwnershipJobRelation.OtherJobsRemain:
                    return DropJob;
                case ServiceOwnershipJobRelation.FinalAssociatedJob:
                    break;
                default:
                    return None;
            }

            return coherent ? RemoveRestored : GoStale;
        }

        ServiceOwnershipPlanAction[] old =
            PreRepairOrdering(false, ServiceOwnershipJobRelation.OtherJobsRemain);

        // The old ordering DID emit a real ledger mutation and never marked the entry stale.
        Assert.True(DropJob.SequenceEqual(old));
        Assert.Contains(ServiceOwnershipPlanAction.RemoveJobAssociation, old);
        Assert.DoesNotContain(ServiceOwnershipPlanAction.MarkStale, old);

        // The REAL planner must not.
        ServiceOwnershipLifecyclePlan actual = ServiceOwnershipLifecyclePlanner.Plan(
            Unpublish(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
                ServiceOwnershipJobRelation.OtherJobsRemain));

        Assert.DoesNotContain(ServiceOwnershipPlanAction.RemoveJobAssociation, actual.Actions);
        Assert.True(GoStale.SequenceEqual(actual.Actions));
        Assert.False(old.SequenceEqual(actual.Actions));

        // And the divergence is specific to the CONTRADICTORY case: on a coherent pair
        // the two orderings still agree, which is what makes the control discriminating
        // rather than merely different.
        ServiceOwnershipLedgerValidationResult coherentResult =
            Validated(Ledger("done", Entry(lifecycle: "restored", jobIds: new[] { JobId, OtherJobId })));

        ServiceOwnershipLifecyclePlan coherentPlan = ServiceOwnershipLifecyclePlanner.Plan(
            Unpublish(coherentResult, ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
                ServiceOwnershipJobRelation.OtherJobsRemain));

        Assert.True(
            PreRepairOrdering(true, ServiceOwnershipJobRelation.OtherJobsRemain)
                .SequenceEqual(coherentPlan.Actions));
    }

    [Fact]
    public void The_five_coherent_pairs_are_exactly_the_five_specified()
    {
        var found = new List<string>();
        foreach (string tx in PopulatedTransactionStates)
        {
            foreach (string life in MatrixLifecycleStates)
            {
                ServiceOwnershipLedgerValidationResult result = Validated(Ledger(tx, Entry(lifecycle: life)));
                ServiceOwnershipLifecyclePlan verify = ServiceOwnershipLifecyclePlanner.Plan(Verify(result));
                if (verify.Outcome == ServiceOwnershipPlanOutcome.NoAction)
                {
                    found.Add(tx + "+" + life);
                }
            }
        }

        Assert.Equal(
            new[]
            {
                "preparing+intended",
                "credential-mutated+intended",
                "restoring+restoring",
                "restoring+restored",
                "done+restored",
            }.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            found.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Ruling_A2_2_two_in_progress_documents_differing_only_in_transaction_state_plan_differently()
    {
        // THE PROOF that the planner does not branch on the broad InProgress outcome.
        // Both documents validate to InProgress. They differ ONLY in transactionState.
        // The entry, the observation and the operation are identical.
        ServiceOwnershipLedgerValidationResult preparing =
            Validated(Ledger("preparing", Entry(lifecycle: "intended")));
        ServiceOwnershipLedgerValidationResult mutated =
            Validated(Ledger("credential-mutated", Entry(lifecycle: "intended")));

        Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, preparing.Outcome);
        Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, mutated.Outcome);
        Assert.Equal(preparing.Outcome, mutated.Outcome);
        Assert.Equal(
            ServiceOwnershipTransactionState.Preparing, preparing.Document!.TransactionState);
        Assert.Equal(
            ServiceOwnershipTransactionState.CredentialMutated, mutated.Document!.TransactionState);
        Assert.Equal(
            preparing.Document.Entries[0].LifecycleState,
            mutated.Document.Entries[0].LifecycleState);

        const ServiceOwnershipCredentialObservation observation =
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState;

        ServiceOwnershipLifecyclePlan a = ServiceOwnershipLifecyclePlanner.Plan(Recover(preparing, observation));
        ServiceOwnershipLifecyclePlan b = ServiceOwnershipLifecyclePlanner.Plan(Recover(mutated, observation));

        Assert.Equal(AbandonIntent, a.Actions);
        Assert.Equal(GoStale, b.Actions);
        Assert.False(a.Actions.SequenceEqual(b.Actions));

        // A second, independent witness pair: same broad outcome, different plans.
        ServiceOwnershipLedgerValidationResult restoringRestoring =
            Validated(Ledger("restoring", Entry(lifecycle: "restoring")));
        ServiceOwnershipLedgerValidationResult ledgerCommittedRestoring =
            Validated(Ledger("ledger-committed", Entry(lifecycle: "restoring")));

        Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, restoringRestoring.Outcome);
        Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, ledgerCommittedRestoring.Outcome);

        Assert.Equal(
            FinalizeRestore,
            ServiceOwnershipLifecyclePlanner.Plan(Recover(restoringRestoring, observation)).Actions);
        Assert.Equal(
            GoStale,
            ServiceOwnershipLifecyclePlanner.Plan(Recover(ledgerCommittedRestoring, observation)).Actions);
    }

    [Fact]
    public void A_contradictory_pair_never_restores_an_acl_and_never_finalises()
    {
        foreach (string tx in PopulatedTransactionStates)
        {
            foreach (string life in MatrixLifecycleStates)
            {
                if (CoherentPairs.Contains((tx, life)))
                {
                    continue;
                }

                ServiceOwnershipLedgerValidationResult result = Validated(Ledger(tx, Entry(lifecycle: life)));

                foreach (ServiceOwnershipCredentialObservation observation in DefiniteObservations)
                {
                    ServiceOwnershipLifecyclePlan plan =
                        ServiceOwnershipLifecyclePlanner.Plan(Recover(result, observation));

                    Assert.Equal(GoStale, plan.Actions);
                    Assert.DoesNotContain(ServiceOwnershipPlanAction.RestoreCapturedPriorDacl, plan.Actions);
                    Assert.DoesNotContain(ServiceOwnershipPlanAction.VerifyCapturedPriorDacl, plan.Actions);
                    Assert.DoesNotContain(ServiceOwnershipPlanAction.BeginRestore, plan.Actions);
                    Assert.DoesNotContain(ServiceOwnershipPlanAction.MarkRestored, plan.Actions);
                    Assert.DoesNotContain(ServiceOwnershipPlanAction.RemoveRestoredEntry, plan.Actions);
                    Assert.DoesNotContain(ServiceOwnershipPlanAction.RemoveAbandonedIntentEntry, plan.Actions);
                }
            }
        }
    }

    [Fact]
    public void An_unavailable_observation_never_yields_an_action_on_a_coherent_pair()
    {
        foreach ((string tx, string life) in CoherentPairs)
        {
            ServiceOwnershipLedgerValidationResult result = Validated(Ledger(tx, Entry(lifecycle: life)));

            foreach (ServiceOwnershipPlannerOperation operation in RecoveryOperations)
            {
                ServiceOwnershipLifecyclePlanRequest? request = operation switch
                {
                    ServiceOwnershipPlannerOperation.RecoverInterrupted =>
                        Recover(result, ServiceOwnershipCredentialObservation.Unavailable),
                    ServiceOwnershipPlannerOperation.RestoreCredential =>
                        Restore(result, ServiceOwnershipCredentialObservation.Unavailable),
                    _ => Disable(result, ServiceOwnershipCredentialObservation.Unavailable),
                };

                ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(request);

                Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
                Assert.Equal(ServiceOwnershipPlanRefusalReason.ObservationUnavailable, plan.RefusalReason);
                Assert.Empty(plan.Actions);
            }
        }
    }

    // ===========================================================================
    // RESTORE SEMANTICS (ruling E1)
    // ===========================================================================

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("present")]
    public void One_state_agnostic_restore_action_covers_all_three_prior_states(string priorState)
    {
        // ONE symbolic action. The planner never carries or interprets DACL bytes; a
        // FUTURE executor reads the meaning off the validated entry's priorDaclState.
        ServiceOwnershipLedgerValidationResult result =
            Validated(Ledger("credential-mutated", Entry(priorState: priorState)));

        Assert.Equal(
            priorState switch
            {
                "absent" => ServiceOwnershipPriorDaclState.Absent,
                "empty" => ServiceOwnershipPriorDaclState.Empty,
                _ => ServiceOwnershipPriorDaclState.Present,
            },
            result.Document!.Entries[0].PriorDaclState);

        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            Recover(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant));

        Assert.Equal(FullRollback, plan.Actions);
        Assert.Single(plan.Actions.Where(a => a == ServiceOwnershipPlanAction.RestoreCapturedPriorDacl));
        Assert.Single(plan.Actions.Where(a => a == ServiceOwnershipPlanAction.VerifyCapturedPriorDacl));

        // The restore is always VERIFIED immediately after it is performed.
        int restoreIndex = plan.Actions.ToList().IndexOf(ServiceOwnershipPlanAction.RestoreCapturedPriorDacl);
        int verifyIndex = plan.Actions.ToList().IndexOf(ServiceOwnershipPlanAction.VerifyCapturedPriorDacl);
        Assert.Equal(restoreIndex + 1, verifyIndex);
    }

    [Fact]
    public void A_one_byte_captured_binding_mismatch_is_refused_by_the_parser_before_the_planner_sees_it()
    {
        string good = Ledger("preparing", Entry());
        ServiceOwnershipLedgerValidationResult accepted = ServiceOwnershipLedgerValidator.Validate(good);
        Assert.True(accepted.IsAccepted);

        string binding = accepted.Document!.Entries[0].CapturedStateBindingSha256;
        char last = binding[^1];
        char flipped = last == '0' ? '1' : '0';
        string tampered = binding[..^1] + flipped;
        Assert.NotEqual(binding, tampered);
        Assert.Equal(binding.Length, tampered.Length);

        string bad = good.Replace("\"" + binding + "\"", "\"" + tampered + "\"", StringComparison.Ordinal);
        Assert.NotEqual(good, bad);

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(bad);
        Assert.True(result.IsRefused);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.CapturedStateBindingMismatch, result.Reason);
        Assert.Null(result.Document);

        // The planner therefore never reaches its own binding defence; the schema
        // already refused. CapturedStateBindingInvalid is DEFENDED but unreachable.
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            Recover(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant));
        Assert.Equal(ServiceOwnershipPlanRefusalReason.LedgerRefused, plan.RefusalReason);
        Assert.Empty(plan.Actions);
    }

    // ===========================================================================
    // OPERATION RULES
    // ===========================================================================

    [Fact]
    public void Assess_promotion_is_always_refused_with_zero_actions_everywhere()
    {
        var results = new List<ServiceOwnershipLedgerValidationResult>
        {
            ServiceOwnershipLedgerValidator.ForAbsentLedger(),
            Validated(Ledger("idle")),
            ServiceOwnershipLedgerValidator.Validate("{ not json"),
            ServiceOwnershipLedgerValidator.Validate(Ledger("preparing", Entry(lifecycle: "active"))),
        };

        foreach (string tx in PopulatedTransactionStates)
        {
            foreach (string life in MatrixLifecycleStates)
            {
                results.Add(Validated(Ledger(tx, Entry(lifecycle: life))));
            }
        }

        results.Add(Validated(Ledger("credential-mutated", Entry(lifecycle: "stale"))));

        foreach (ServiceOwnershipLedgerValidationResult result in results)
        {
            ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(Assess(result));

            Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
            Assert.Empty(plan.Actions);
            Assert.Contains(
                plan.RefusalReason,
                new[]
                {
                    ServiceOwnershipPlanRefusalReason.PromotionNotAuthorized,
                    ServiceOwnershipPlanRefusalReason.LedgerRefused,
                });

            // No promotion intent is EVER recorded, and the planner refusal does not
            // depend on whether a rights profile exists. Cycle 42 approved exactly one
            // profile and promotion still emits zero actions.
            Assert.True(ServiceOwnershipLedgerContract.HasApprovedRightsProfile);
        }
    }

    [Fact]
    public void Verify_is_always_read_only_with_zero_actions_everywhere()
    {
        var results = new List<ServiceOwnershipLedgerValidationResult>
        {
            ServiceOwnershipLedgerValidator.ForAbsentLedger(),
            Validated(Ledger("idle")),
            ServiceOwnershipLedgerValidator.Validate("{ not json"),
            Validated(Ledger("credential-mutated", Entry(lifecycle: "stale"))),
        };

        foreach (string tx in PopulatedTransactionStates)
        {
            foreach (string life in MatrixLifecycleStates)
            {
                results.Add(Validated(Ledger(tx, Entry(lifecycle: life))));
            }
        }

        foreach (ServiceOwnershipLedgerValidationResult result in results)
        {
            ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(Verify(result));
            Assert.Empty(plan.Actions);
            Assert.NotEqual(ServiceOwnershipPlanOutcome.PlanAvailable, plan.Outcome);
        }
    }

    [Fact]
    public void Unpublish_job_follows_the_job_relation_and_never_deletes_a_referenced_certificate()
    {
        ServiceOwnershipLedgerValidationResult result =
            Validated(Ledger("done", Entry(lifecycle: "restored", jobIds: new[] { JobId, OtherJobId })));

        // 1. The job is not associated at all: nothing to do, and it is a refusal
        //    rather than a silent success, because the caller believed otherwise.
        AssertPlan(
            Unpublish(result, ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
                ServiceOwnershipJobRelation.JobNotAssociated),
            ServiceOwnershipPlanOutcome.Refused, ServiceOwnershipPlanRefusalReason.JobNotAssociated, None);

        // 2. Other jobs still reference the credential: drop only the association.
        //    The observation is irrelevant here and must not change the plan.
        foreach (ServiceOwnershipCredentialObservation observation in DefiniteObservations)
        {
            AssertPlan(
                Unpublish(result, observation, ServiceOwnershipJobRelation.OtherJobsRemain),
                ServiceOwnershipPlanOutcome.PlanAvailable, ServiceOwnershipPlanRefusalReason.None, DropJob);
        }

        // 3. The final associated job: only a PROVEN state may unwind, and the unwind
        //    is a ledger/ACL rollback - never a certificate deletion.
        AssertPlan(
            Unpublish(result, ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
                ServiceOwnershipJobRelation.FinalAssociatedJob),
            ServiceOwnershipPlanOutcome.PlanAvailable, ServiceOwnershipPlanRefusalReason.None, RemoveRestored);

        AssertPlan(
            Unpublish(result, ServiceOwnershipCredentialObservation.Unavailable,
                ServiceOwnershipJobRelation.FinalAssociatedJob),
            ServiceOwnershipPlanOutcome.Refused, ServiceOwnershipPlanRefusalReason.ObservationUnavailable, None);

        foreach (ServiceOwnershipCredentialObservation contradictory in new[]
                 {
                     ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
                     ServiceOwnershipCredentialObservation.Diverged,
                     ServiceOwnershipCredentialObservation.KeyIdentityMismatch,
                 })
        {
            AssertPlan(
                Unpublish(result, contradictory, ServiceOwnershipJobRelation.FinalAssociatedJob),
                ServiceOwnershipPlanOutcome.PlanAvailable, ServiceOwnershipPlanRefusalReason.None, GoStale);
        }

        // 4. On a mid-rollback pair, the final job unwinds through the full rollback.
        ServiceOwnershipLedgerValidationResult mutated =
            Validated(Ledger("credential-mutated", Entry(jobIds: new[] { JobId })));

        AssertPlan(
            Unpublish(mutated, ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
                ServiceOwnershipJobRelation.FinalAssociatedJob),
            ServiceOwnershipPlanOutcome.PlanAvailable, ServiceOwnershipPlanRefusalReason.None, FullRollback);
    }

    [Fact]
    public void Ruling_C1_key_identity_mismatch_is_not_a_global_gate()
    {
        ServiceOwnershipLedgerValidationResult result = Validated(Ledger("preparing", Entry()));

        // Mutation-capable operations record the divergence and stop.
        foreach (ServiceOwnershipPlannerOperation operation in RecoveryOperations)
        {
            ServiceOwnershipLifecyclePlanRequest? request = operation switch
            {
                ServiceOwnershipPlannerOperation.RecoverInterrupted =>
                    Recover(result, ServiceOwnershipCredentialObservation.KeyIdentityMismatch),
                ServiceOwnershipPlannerOperation.RestoreCredential =>
                    Restore(result, ServiceOwnershipCredentialObservation.KeyIdentityMismatch),
                _ => Disable(result, ServiceOwnershipCredentialObservation.KeyIdentityMismatch),
            };

            AssertPlan(request, ServiceOwnershipPlanOutcome.PlanAvailable,
                ServiceOwnershipPlanRefusalReason.None, GoStale);
        }

        AssertPlan(
            Unpublish(result, ServiceOwnershipCredentialObservation.KeyIdentityMismatch,
                ServiceOwnershipJobRelation.FinalAssociatedJob),
            ServiceOwnershipPlanOutcome.PlanAvailable, ServiceOwnershipPlanRefusalReason.None, GoStale);

        // Verify and AssessPromotion keep zero actions: a key-identity mismatch is
        // NOT a global gate that rewrites what those two operations mean.
        Assert.Empty(ServiceOwnershipLifecyclePlanner.Plan(Verify(result)).Actions);
        Assert.Empty(ServiceOwnershipLifecyclePlanner.Plan(Assess(result)).Actions);
    }

    // ===========================================================================
    // RESULT SHAPE
    // ===========================================================================

    [Fact]
    public void The_action_list_is_genuinely_read_only()
    {
        ServiceOwnershipLedgerValidationResult result = Validated(Ledger("credential-mutated", Entry()));
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
            Recover(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant));

        Assert.Equal(8, plan.Actions.Count);
        Assert.IsNotType<ServiceOwnershipPlanAction[]>(plan.Actions);

        IList<ServiceOwnershipPlanAction> list = (IList<ServiceOwnershipPlanAction>)plan.Actions;
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(ServiceOwnershipPlanAction.MarkStale));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => { list[0] = ServiceOwnershipPlanAction.MarkStale; });

        // A second plan built from the same inputs is unaffected by any attempt above.
        ServiceOwnershipLifecyclePlan again = ServiceOwnershipLifecyclePlanner.Plan(
            Recover(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant));
        Assert.Equal(FullRollback, again.Actions);
    }

    [Fact]
    public void No_plan_member_and_no_to_string_can_leak_an_identifier()
    {
        // 1. STRUCTURAL: the result type declares no string-shaped public member at
        //    all, so there is nowhere for an identifier to live.
        foreach (PropertyInfo property in typeof(ServiceOwnershipLifecyclePlan)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.False(
                property.PropertyType == typeof(string)
                || typeof(IEnumerable<string>).IsAssignableFrom(property.PropertyType),
                "ServiceOwnershipLifecyclePlan." + property.Name + " is string-shaped");
        }

        // 2. BEHAVIOURAL: drive every operation over a document stuffed with
        //    distinctive synthetic identifiers and search every rendering.
        ServiceOwnershipLedgerValidationResult result =
            Validated(Ledger("credential-mutated", Entry(jobIds: new[] { JobId, OtherJobId })));

        var plans = new List<ServiceOwnershipLifecyclePlan>();
        foreach (ServiceOwnershipLifecyclePlanRequest request in AllOperations(result))
        {
            plans.Add(ServiceOwnershipLifecyclePlanner.Plan(request));
        }

        plans.Add(ServiceOwnershipLifecyclePlanner.Plan(null));
        Assert.Equal(7, plans.Count);

        foreach (ServiceOwnershipLifecyclePlan plan in plans)
        {
            var rendered = new StringBuilder();
            rendered.Append(plan.ToString());
            rendered.Append('|').Append(plan.Operation).Append('|').Append(plan.Outcome);
            rendered.Append('|').Append(plan.RefusalReason).Append('|').Append(plan.ObservedLedgerOutcome);
            foreach (ServiceOwnershipPlanAction action in plan.Actions)
            {
                rendered.Append('|').Append(action);
            }

            string text = rendered.ToString();
            Assert.False(string.IsNullOrWhiteSpace(plan.ToString()));

            foreach (string needle in IdentifierNeedles)
            {
                Assert.DoesNotContain(needle, text, StringComparison.OrdinalIgnoreCase);
            }

            // Nothing that even LOOKS like a SID or a 40-hex thumbprint.
            Assert.DoesNotContain("S-1-5", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ABCDEF", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ===========================================================================
    // NO-THROW FUZZ
    // ===========================================================================

    [Fact]
    public void No_input_can_make_the_planner_throw()
    {
        var results = new List<ServiceOwnershipLedgerValidationResult>
        {
            ServiceOwnershipLedgerValidator.ForAbsentLedger(),
            ServiceOwnershipLedgerValidator.Validate(null),
            ServiceOwnershipLedgerValidator.Validate(string.Empty),
            ServiceOwnershipLedgerValidator.Validate("   "),
            ServiceOwnershipLedgerValidator.Validate("[]"),
            ServiceOwnershipLedgerValidator.Validate("\0"),
            ServiceOwnershipLedgerValidator.Validate(new string('[', 512) + new string(']', 512)),
            Validated(Ledger("idle")),
            Validated(Ledger("preparing", Entry())),
            Validated(Ledger("credential-mutated", Entry(lifecycle: "stale"))),
        };

        int[] wild = { int.MinValue, -1, 0, 7, 99, int.MaxValue };
        int planned = 0;

        foreach (ServiceOwnershipLedgerValidationResult result in results)
        {
            foreach (int raw in wild)
            {
                // Out-of-range enum casts through EVERY factory, plus null strings.
                Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
                    result, null, null, null, null, (ServiceOwnershipCredentialObservation)raw));
                Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob(
                    result, null, null, null, null, null,
                    (ServiceOwnershipCredentialObservation)raw, (ServiceOwnershipJobRelation)raw));
                Assert.Null(ServiceOwnershipLifecyclePlanRequest.ForDisableCleanup(
                    result, InstallId, OwnerSid, SvcSid, EntryId, (ServiceOwnershipCredentialObservation)raw));
            }

            foreach (ServiceOwnershipLifecyclePlanRequest request in AllOperations(result))
            {
                ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(request);
                Assert.True(Enum.IsDefined(plan.Outcome));
                Assert.True(Enum.IsDefined(plan.RefusalReason));
                Assert.True(Enum.IsDefined(plan.Operation));
                foreach (ServiceOwnershipPlanAction action in plan.Actions)
                {
                    Assert.True(Enum.IsDefined(action));
                    Assert.NotEqual(ServiceOwnershipPlanAction.Unspecified, action);
                }

                // A refusal NEVER carries an action, and a NoAction never does either.
                if (plan.Outcome != ServiceOwnershipPlanOutcome.PlanAvailable)
                {
                    Assert.Empty(plan.Actions);
                }
                else
                {
                    Assert.NotEmpty(plan.Actions);
                    Assert.Equal(ServiceOwnershipPlanRefusalReason.None, plan.RefusalReason);
                }

                planned++;
            }
        }

        Assert.Equal(results.Count * 6, planned);
    }

    [Fact]
    public void The_planner_is_deterministic()
    {
        ServiceOwnershipLedgerValidationResult result = Validated(Ledger("restoring", Entry(lifecycle: "restoring")));

        for (int i = 0; i < 25; i++)
        {
            ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(
                Recover(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant));
            Assert.Equal(FullRollback, plan.Actions);
            Assert.Equal(ServiceOwnershipPlanOutcome.PlanAvailable, plan.Outcome);
        }
    }

    // ===========================================================================
    // EXPECTATION TABLE - written independently of the planner implementation
    // ===========================================================================

    private static (ServiceOwnershipPlanOutcome, ServiceOwnershipPlanRefusalReason, ServiceOwnershipPlanAction[])
        ExpectedRecovery(string tx, string life, ServiceOwnershipCredentialObservation observation)
    {
        bool coherent = CoherentPairs.Contains((tx, life));

        if (!coherent)
        {
            // Contradictory: record the divergence, restore nothing, finalise nothing.
            return (ServiceOwnershipPlanOutcome.PlanAvailable, ServiceOwnershipPlanRefusalReason.None, GoStale);
        }

        if (observation == ServiceOwnershipCredentialObservation.Unavailable)
        {
            return (ServiceOwnershipPlanOutcome.Refused,
                ServiceOwnershipPlanRefusalReason.ObservationUnavailable, None);
        }

        ServiceOwnershipPlanAction[] actions = (tx, life) switch
        {
            ("preparing", "intended") => observation switch
            {
                ServiceOwnershipCredentialObservation.MatchesCapturedPriorState => AbandonIntent,
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant => FullRollback,
                _ => GoStale,
            },
            ("credential-mutated", "intended") => observation switch
            {
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant => FullRollback,
                _ => GoStale,
            },
            ("restoring", "restoring") => observation switch
            {
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant => FullRollback,
                ServiceOwnershipCredentialObservation.MatchesCapturedPriorState => FinalizeRestore,
                _ => GoStale,
            },
            _ => observation switch
            {
                ServiceOwnershipCredentialObservation.MatchesCapturedPriorState => RemoveRestored,
                _ => GoStale,
            },
        };

        return (ServiceOwnershipPlanOutcome.PlanAvailable, ServiceOwnershipPlanRefusalReason.None, actions);
    }

    // UnpublishJob expectation, written independently of the implementation.
    //
    // Cycle 38c, option A: the contradictory-pair coherence gate runs BEFORE any
    // caller-supplied job relation is resolved. On a contradictory pair the plan is
    // therefore EXACTLY MarkStale + PersistLedger for every relation and every
    // observation - identical to every recovery operation in the same position.
    //
    // On a COHERENT pair the pre-existing behaviour is preserved unchanged:
    // JobNotAssociated is a bounded refusal with zero actions, OtherJobsRemain drops
    // only the association, and FinalAssociatedJob falls through to the same proven
    // restore decision the recovery operations use.
    private static (ServiceOwnershipPlanOutcome, ServiceOwnershipPlanRefusalReason, ServiceOwnershipPlanAction[])
        ExpectedUnpublish(
            string tx,
            string life,
            ServiceOwnershipCredentialObservation observation,
            ServiceOwnershipJobRelation relation)
    {
        if (!CoherentPairs.Contains((tx, life)))
        {
            return (ServiceOwnershipPlanOutcome.PlanAvailable, ServiceOwnershipPlanRefusalReason.None, GoStale);
        }

        return relation switch
        {
            ServiceOwnershipJobRelation.JobNotAssociated =>
                (ServiceOwnershipPlanOutcome.Refused, ServiceOwnershipPlanRefusalReason.JobNotAssociated, None),
            ServiceOwnershipJobRelation.OtherJobsRemain =>
                (ServiceOwnershipPlanOutcome.PlanAvailable, ServiceOwnershipPlanRefusalReason.None, DropJob),
            _ => ExpectedRecovery(tx, life, observation),
        };
    }

    // ===========================================================================
    // REQUEST HELPERS
    // ===========================================================================

    private static void AssertPlan(
        ServiceOwnershipLifecyclePlanRequest? request,
        ServiceOwnershipPlanOutcome outcome,
        ServiceOwnershipPlanRefusalReason reason,
        ServiceOwnershipPlanAction[] actions)
    {
        Assert.NotNull(request);
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(request);
        Assert.Equal(outcome, plan.Outcome);
        Assert.Equal(reason, plan.RefusalReason);
        Assert.Equal(actions, plan.Actions);
    }

    private static ServiceOwnershipLifecyclePlanRequest Assess(ServiceOwnershipLedgerValidationResult result)
    {
        ServiceOwnershipLifecyclePlanRequest? r =
            ServiceOwnershipLifecyclePlanRequest.ForAssessPromotion(result, InstallId, OwnerSid, SvcSid);
        Assert.NotNull(r);
        return r!;
    }

    private static ServiceOwnershipLifecyclePlanRequest Verify(ServiceOwnershipLedgerValidationResult result)
    {
        ServiceOwnershipLifecyclePlanRequest? r =
            ServiceOwnershipLifecyclePlanRequest.ForVerify(result, InstallId, OwnerSid, SvcSid, EntryId);
        Assert.NotNull(r);
        return r!;
    }

    private static ServiceOwnershipLifecyclePlanRequest Recover(
        ServiceOwnershipLedgerValidationResult result, ServiceOwnershipCredentialObservation observation)
    {
        ServiceOwnershipLifecyclePlanRequest? r = ServiceOwnershipLifecyclePlanRequest.ForRecoverInterrupted(
            result, InstallId, OwnerSid, SvcSid, EntryId, observation);
        Assert.NotNull(r);
        return r!;
    }

    private static ServiceOwnershipLifecyclePlanRequest Restore(
        ServiceOwnershipLedgerValidationResult result, ServiceOwnershipCredentialObservation observation)
    {
        ServiceOwnershipLifecyclePlanRequest? r = ServiceOwnershipLifecyclePlanRequest.ForRestoreCredential(
            result, InstallId, OwnerSid, SvcSid, EntryId, observation);
        Assert.NotNull(r);
        return r!;
    }

    private static ServiceOwnershipLifecyclePlanRequest Disable(
        ServiceOwnershipLedgerValidationResult result, ServiceOwnershipCredentialObservation observation)
    {
        ServiceOwnershipLifecyclePlanRequest? r = ServiceOwnershipLifecyclePlanRequest.ForDisableCleanup(
            result, InstallId, OwnerSid, SvcSid, EntryId, observation);
        Assert.NotNull(r);
        return r!;
    }

    private static ServiceOwnershipLifecyclePlanRequest Unpublish(
        ServiceOwnershipLedgerValidationResult result,
        ServiceOwnershipCredentialObservation observation,
        ServiceOwnershipJobRelation relation)
    {
        ServiceOwnershipLifecyclePlanRequest? r = ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob(
            result, InstallId, OwnerSid, SvcSid, EntryId, JobId, observation, relation);
        Assert.NotNull(r);
        return r!;
    }

    private static IEnumerable<ServiceOwnershipLifecyclePlanRequest> AllOperations(
        ServiceOwnershipLedgerValidationResult result)
    {
        yield return Assess(result);
        yield return Verify(result);
        yield return Recover(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant);
        yield return Unpublish(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipJobRelation.FinalAssociatedJob);
        yield return Restore(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant);
        yield return Disable(result, ServiceOwnershipCredentialObservation.MatchesRecordedGrant);
    }

    // ===========================================================================
    // LEDGER FIXTURE BUILDER
    // ===========================================================================

    private static ServiceOwnershipLedgerValidationResult Validated(string json)
    {
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(json);
        Assert.True(result.IsAccepted, "fixture was expected to validate: " + result.Reason);
        return result;
    }

    private static string Ledger(string transactionState, params string[] entries)
        => LedgerCore(transactionState, entries, 3, null, null);

    private static string LedgerCore(
        string transactionState,
        string[] entries,
        int schemaVersion,
        string? marker,
        string? installationOwnershipId)
    {
        marker ??= ServiceOwnershipLedgerContract.ProductOwnershipMarker;
        installationOwnershipId ??= InstallId;

        var sb = new StringBuilder("{");
        sb.Append("\"schemaVersion\":").Append(schemaVersion.ToString(CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"productOwnershipMarker\":\"").Append(marker).Append("\",");
        sb.Append("\"managedFeatureId\":\"").Append(ServiceOwnershipLedgerContract.ManagedFeatureId).Append("\",");
        sb.Append("\"installationOwnershipId\":\"").Append(installationOwnershipId).Append("\",");
        sb.Append("\"generation\":1,");
        sb.Append("\"transactionState\":\"").Append(transactionState).Append("\",");
        sb.Append("\"entries\":[").Append(string.Join(",", entries)).Append("],");
        sb.Append("\"createdUtc\":\"").Append(Stamp).Append("\",");
        sb.Append("\"updatedUtc\":\"").Append(Stamp).Append("\",");
        sb.Append("\"lastOperationId\":\"op-0001\"}");
        return sb.ToString();
    }

    private static string Entry(
        string entryId = EntryId,
        string lifecycle = "intended",
        string ownerSid = OwnerSid,
        string svcSid = SvcSid,
        string thumb = Thumb,
        string keyId = KeyId,
        string priorState = "present",
        string[]? jobIds = null)
    {
        jobIds ??= new[] { JobId };

        byte[] priorBytes =
            { 0x01, 0x00, 0x04, 0x90, 0x14, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00, 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x15, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0xE9, 0x03, 0x00, 0x00, 0x02, 0x00, 0x34, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x03, 0x14, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x12, 0x00, 0x00, 0x00, 0x00, 0x03, 0x18, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00 };
        string priorB64 = priorState == "present" ? Convert.ToBase64String(priorBytes) : string.Empty;
        string priorSha = priorState == "present"
            ? ToUpperHex(System.Security.Cryptography.SHA256.HashData(priorBytes))
            : string.Empty;

        ServiceOwnershipLedgerContract.TryParseWireToken(priorState, out ServiceOwnershipPriorDaclState prior);

        string binding = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            1,
            ownerSid,
            svcSid,
            thumb,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            keyId,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            Mask,
            prior,
            priorSha,
            ProviderUniqueName,
            ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1);

        var sb = new StringBuilder("{");
        sb.Append("\"entryId\":\"").Append(entryId).Append("\",");
        sb.Append("\"owningUserSid\":\"").Append(ownerSid).Append("\",");
        sb.Append("\"serviceSid\":\"").Append(svcSid).Append("\",");
        sb.Append("\"credentialKind\":\"personal-app-registration-certificate\",");
        sb.Append("\"certificateThumbprintSha1\":\"").Append(thumb).Append("\",");
        sb.Append("\"provenance\":\"referenced\",");
        sb.Append("\"privateKeyProviderKind\":\"microsoft-software-key-storage-provider\",");
        sb.Append("\"rightsProfileId\":\"")
          .Append(ServiceOwnershipLedgerContract.ToWireToken(
              ServiceOwnershipLedgerContract.ApprovedRightsProfileId))
          .Append("\",");
        sb.Append("\"keyIdentity\":\"").Append(keyId).Append("\",");
        sb.Append("\"grantMechanism\":\"microsoft-software-ksp-backing-file-dacl\",");
        sb.Append("\"grantedRightsMask\":\"").Append(Mask).Append("\",");
        sb.Append("\"rightsPolicyVersion\":3,");
        sb.Append("\"priorDaclState\":\"").Append(priorState).Append("\",");
        sb.Append("\"priorDaclBytesBase64\":\"").Append(priorB64).Append("\",");
        sb.Append("\"priorDaclSha256\":\"").Append(priorSha).Append("\",");
        sb.Append("\"capturedStateBindingSha256\":\"").Append(binding).Append("\",");
        sb.Append("\"associatedPromotedJobIds\":[")
          .Append(string.Join(",", jobIds.Select(j => "\"" + j + "\"")))
          .Append("],");
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
