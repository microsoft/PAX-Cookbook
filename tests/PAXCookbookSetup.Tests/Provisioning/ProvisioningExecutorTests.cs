#if MANAGED_INVENTORY_PROVISIONING
using System.Text;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Provisioning;
using Xunit;

namespace PAXCookbookSetup.Tests.Provisioning;

// Cycle-8 executor matrix (GATED, NON-LIVE). Every test drives the real gated
// ProvisioningExecutor against the deterministic in-memory adapter: NO process,
// NO real ProgramData, NO ACL, NO UAC, NO network. It proves the transactional
// apply, reversible-swap rollback, read-only verify, exact remove, and bounded
// crash recovery — including that backups come from the CURRENT owned bytes.
public sealed class ProvisioningExecutorTests
{
    private static string ValidInventory(string id = "org-key-1")
        => "{\"schemaVersion\":1,\"entries\":[{\"entryVersion\":1,\"organizationKeyId\":\""
           + id
           + "\",\"displayName\":\"Key\",\"certificateReferenceType\":\"app_registration_certificate\","
           + "\"adminState\":\"enabled\",\"tenantReference\":\"t\",\"clientReference\":\"c\"}]}";

    private static ProvisioningRequest Request(
        string operation, string? inventory = null, int? expectedGeneration = null, bool planOnly = false)
    {
        var sb = new StringBuilder();
        sb.Append("{\"requestSchemaVersion\":1,\"operation\":\"").Append(operation).Append("\",");
        sb.Append("\"operationId\":\"op-1\",");
        sb.Append("\"inventoryDocument\":").Append(JsonString(inventory ?? string.Empty)).Append(',');
        if (expectedGeneration is int g)
        {
            sb.Append("\"expectedLedgerGeneration\":").Append(g).Append(',');
        }
        sb.Append("\"planOnly\":").Append(planOnly ? "true" : "false").Append('}');
        ProvisioningRequestValidationResult v = ProvisioningRequestValidator.Validate(sb.ToString());
        Assert.True(v.IsValid);
        return v.Request!;
    }

    private static string JsonString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static ProvisioningExecutor Executor(InMemoryProvisioningStore store)
        => new(store, store, store);

    [Fact] // E01 — Safely-absent apply creates generation 1 with NO previous backup.
    public void E01_Apply_FromAbsent_CreatesGeneration1_NoPrevious()
    {
        var store = new InMemoryProvisioningStore();
        string doc = ValidInventory();

        ProvisioningResult r = Executor(store).Execute(Request("apply", doc));

        Assert.Equal(ProvisioningResultCodes.Ok, r.Code);
        Assert.Equal(1, store.CurrentLedger!.CurrentGeneration);
        Assert.Null(store.CurrentLedger.PreviousGeneration);
        Assert.Null(store.PreviousInventoryContent);
        Assert.Equal(doc, store.CurrentInventory);
        Assert.Equal(ProvisioningTransactionState.Idle, store.Marker);
    }

    [Fact] // E02 — Replace advances gen 1->2 and stages the backup from the CURRENT owned bytes (never caller bytes).
    public void E02_Apply_Replace_StagesBackupFromCurrentOwnedBytes_MonotonicGeneration()
    {
        var store = new InMemoryProvisioningStore();
        string oldDoc = ValidInventory("org-key-1");
        string newDoc = ValidInventory("org-key-2");
        store.SeedGeneration1(oldDoc);

        ProvisioningResult r = Executor(store).Execute(Request("apply", newDoc));

        Assert.Equal(ProvisioningResultCodes.Ok, r.Code);
        Assert.Equal(2, store.CurrentLedger!.CurrentGeneration);
        Assert.Equal(1, store.CurrentLedger.PreviousGeneration);
        Assert.Equal(newDoc, store.CurrentInventory);
        // The backup is the PRIOR owned inventory bytes, taken from the machine.
        Assert.Equal(oldDoc, store.PreviousInventoryContent);
    }

    [Fact] // E03 — Rollback reversible-swaps through a new monotonic generation using ONLY on-disk bytes.
    public void E03_Rollback_ReversibleSwap_RestoresExactPreviousBytes_MonotonicGeneration()
    {
        var store = new InMemoryProvisioningStore();
        string gen1 = ValidInventory("org-key-1");
        string gen2 = ValidInventory("org-key-2");
        store.SeedGeneration2(gen2, gen1);

        ProvisioningResult r = Executor(store).Execute(Request("rollback"));

        Assert.Equal(ProvisioningResultCodes.Ok, r.Code);
        Assert.Equal(3, store.CurrentLedger!.CurrentGeneration);
        Assert.Equal(2, store.CurrentLedger.PreviousGeneration);
        // Current is the exact prior-generation bytes; the swapped-out bytes are the new backup.
        Assert.Equal(gen1, store.CurrentInventory);
        Assert.Equal(gen2, store.PreviousInventoryContent);
    }

    [Fact] // E04 — Remove deletes exactly the owned artifacts and the emptied directory.
    public void E04_Remove_DeletesOwnedArtifactsAndDirectory()
    {
        var store = new InMemoryProvisioningStore();
        store.SeedGeneration2(ValidInventory("b"), ValidInventory("a"));

        ProvisioningResult r = Executor(store).Execute(Request("remove"));

        Assert.Equal(ProvisioningResultCodes.Ok, r.Code);
        Assert.Null(store.CurrentInventory);
        Assert.Null(store.PreviousInventoryContent);
        Assert.Null(store.CurrentLedger);
        Assert.False(store.DirectoryPresent);
    }

    [Fact] // E05 — Verify on an owned, consistent pair is Ok and mutates NOTHING.
    public void E05_Verify_Owned_IsOk_ZeroMutation()
    {
        var store = new InMemoryProvisioningStore();
        store.SeedGeneration1(ValidInventory());
        int before = store.MutationCount;

        ProvisioningResult r = Executor(store).Execute(Request("verify"));

        Assert.Equal(ProvisioningResultCodes.Ok, r.Code);
        Assert.Equal(before, store.MutationCount);
    }

    [Fact] // E06 — An untrusted managed directory blocks any apply.
    public void E06_Apply_UntrustedEnvironment_Rejected_ZeroMutation()
    {
        var store = new InMemoryProvisioningStore();
        store.SeedGeneration1(ValidInventory("org-key-1"));
        store.MakeUntrusted();
        int before = store.MutationCount;

        ProvisioningResult r = Executor(store).Execute(Request("apply", ValidInventory("org-key-2")));

        Assert.Equal(ProvisioningResultCodes.UntrustedEnvironment, r.Code);
        Assert.Equal(before, store.MutationCount);
    }

    [Fact] // E07 — An unknown sibling artifact blocks apply (fail closed).
    public void E07_Apply_UnknownSibling_Rejected()
    {
        var store = new InMemoryProvisioningStore();
        store.SeedGeneration1(ValidInventory("org-key-1"));
        store.SetUnknownSibling(true);

        ProvisioningResult r = Executor(store).Execute(Request("apply", ValidInventory("org-key-2")));

        Assert.Equal(ProvisioningResultCodes.UnknownSibling, r.Code);
    }

    [Fact] // E08 — planOnly apply is advisory: planned but ZERO mutation.
    public void E08_Apply_PlanOnly_ZeroMutation()
    {
        var store = new InMemoryProvisioningStore();
        store.SeedGeneration1(ValidInventory("org-key-1"));
        int before = store.MutationCount;

        ProvisioningResult r = Executor(store).Execute(Request("apply", ValidInventory("org-key-2"), planOnly: true));

        Assert.Equal(ProvisioningResultCodes.Ok, r.Code);
        Assert.Equal(before, store.MutationCount);
        Assert.Equal(1, store.CurrentLedger!.CurrentGeneration);
    }

    [Fact] // E09 — Identical proposed content is a bounded no-op (no generation bump, no mutation).
    public void E09_Apply_IdenticalContent_NoOp()
    {
        var store = new InMemoryProvisioningStore();
        string doc = ValidInventory("org-key-1");
        store.SeedGeneration1(doc);
        int before = store.MutationCount;

        ProvisioningResult r = Executor(store).Execute(Request("apply", doc));

        Assert.Equal(ProvisioningResultCodes.Ok, r.Code);
        Assert.Equal(ProvisioningPlanState.NoOpUpToDate, r.State);
        Assert.Equal(before, store.MutationCount);
        Assert.Equal(1, store.CurrentLedger!.CurrentGeneration);
    }

    [Fact] // E10 — A crash after full commit but before clearing the marker recovers to a valid quiescent state.
    public void E10_Apply_CrashBeforeClear_RecoversToValidQuiescentState()
    {
        var store = new InMemoryProvisioningStore();
        string oldDoc = ValidInventory("org-key-1");
        string newDoc = ValidInventory("org-key-2");
        store.SeedGeneration1(oldDoc);
        store.ArmFault(ProvisioningFailPoint.BeforeTransactionClear);

        // First run faults after everything is committed; the marker stays in-progress.
        ProvisioningResult crashed = Executor(store).Execute(Request("apply", newDoc));
        Assert.Equal(ProvisioningResultCodes.RecoveryRequired, crashed.Code);
        Assert.NotEqual(ProvisioningTransactionState.Idle, store.Marker);

        // Second run detects the crash window and runs bounded recovery to a
        // fully-valid, quiescent terminal state (reverted to the previous generation).
        ProvisioningResult recovered = Executor(store).Execute(Request("apply", newDoc));
        Assert.Equal(ProvisioningTransactionState.Idle, store.Marker);
        Assert.Equal(oldDoc, store.CurrentInventory);
        Assert.Null(store.PreviousInventoryContent);

        // The recovered state now verifies clean.
        ProvisioningResult verify = Executor(store).Execute(Request("verify"));
        Assert.Equal(ProvisioningResultCodes.Ok, verify.Code);
    }

    [Fact] // E11 — Rollback with no previous generation is rejected (nothing to restore).
    public void E11_Rollback_NoPreviousGeneration_Rejected()
    {
        var store = new InMemoryProvisioningStore();
        store.SeedGeneration1(ValidInventory());

        ProvisioningResult r = Executor(store).Execute(Request("rollback"));

        Assert.Equal(ProvisioningResultCodes.NoPreviousGeneration, r.Code);
    }

    // ---------------------------------------------------------------------
    // Universal fault / crash-recovery invariants.
    //
    // For EVERY single-shot fault injected at EVERY wired transaction point, and
    // across the create / replace / rollback operations, the executor must uphold
    // three security-critical invariants no matter where the crash lands:
    //   (1) NO FALSE SUCCESS — an Ok result implies an internally consistent
    //       (inventory hash == ledger hash, or both absent) DURABLE machine. After
    //       a mid-transaction fault the executor legitimately re-plans and returns
    //       the re-planned code; that code may be Ok over a not-yet-applied but
    //       clean/consistent machine (the transient transaction marker is a benign
    //       stale value with no committed artifacts and is NOT a durable-state fact).
    //   (2) NO CORRUPTION — any present inventory / previous-backup is always ONE
    //       of the known COMPLETE documents (never a torn / partial / foreign one).
    //   (3) TERMINAL / SAFETY — retrying the SAME operation reaches a terminal:
    //       either a clean Ok (operation completed or a consistent re-plannable
    //       machine) OR a STABLE fail-closed non-success that does not change on
    //       retry. It never reports Ok over an inconsistent durable machine.
    // NOTE: rollback is a reversible TOGGLE — repeating it keeps performing valid
    // swaps and advancing the monotonic generation forever, so a snapshot
    // fixed-point is intentionally NOT expected; the terminal is the first clean Ok.
    // ---------------------------------------------------------------------

    private static void AssertConsistent(InMemoryProvisioningStore store)
    {
        string? inv = store.CurrentInventory;
        OwnershipLedger? led = store.CurrentLedger;
        if (inv is null && led is null)
        {
            return;
        }

        Assert.NotNull(inv);
        Assert.NotNull(led);
        Assert.Equal(led!.CurrentInventorySha256, ProvisioningHash.Sha256Hex(inv!));
    }

    private static void AssertNoFalseSuccess(InMemoryProvisioningStore store, ProvisioningResult r)
    {
        if (r.Code != ProvisioningResultCodes.Ok)
        {
            return;
        }

        // An Ok result may NEVER be reported over an inconsistent DURABLE machine.
        AssertConsistent(store);
    }

    private static void AssertNoCorruption(InMemoryProvisioningStore store, string[] knownDocs)
    {
        if (store.CurrentInventory is string inv)
        {
            Assert.Contains(inv, knownDocs);
        }
        if (store.PreviousInventoryContent is string prev)
        {
            Assert.Contains(prev, knownDocs);
        }
    }

    private static string Snapshot(InMemoryProvisioningStore store)
        => store.Marker + "|" + (store.CurrentInventory ?? "<none>") + "|"
           + (store.PreviousInventoryContent ?? "<none>") + "|"
           + (store.CurrentLedger?.CurrentGeneration.ToString() ?? "<none>") + "|"
           + store.DirectoryPresent;

    private static void DriveFaultAndAssertInvariants(
        InMemoryProvisioningStore store,
        string failPointName,
        System.Func<ProvisioningRequest> makeRequest,
        string[] knownDocs)
    {
        var failPoint = (ProvisioningFailPoint)System.Enum.Parse(typeof(ProvisioningFailPoint), failPointName);
        store.ArmFault(failPoint);

        string previous = "<init>";
        bool reachedTerminal = false;
        for (int i = 0; i < 8; i++)
        {
            ProvisioningResult r = Executor(store).Execute(makeRequest());
            AssertNoFalseSuccess(store, r);
            AssertNoCorruption(store, knownDocs);

            // A clean Ok is a terminal: either the operation completed or the
            // machine is in a consistent, re-plannable state.
            if (r.Code == ProvisioningResultCodes.Ok)
            {
                reachedTerminal = true;
                break;
            }

            // Otherwise, a fail-closed non-success that repeats unchanged is a
            // stable terminal (a wedged, safe, no-forward-progress stop).
            string now = Snapshot(store);
            if (i > 0 && now == previous)
            {
                reachedTerminal = true;
                break;
            }
            previous = now;
        }

        Assert.True(reachedTerminal, "operation did not reach a safe terminal after fault " + failPointName);
    }

    [Theory] // E12 — Apply/replace (gen1->gen2) upholds the fault invariants at every wired point.
    [InlineData("None")]
    [InlineData("StageInventory")]
    [InlineData("StagePreviousInventory")]
    [InlineData("ApplyAcl")]
    [InlineData("BeforeReplacePreviousInventory")]
    [InlineData("AfterReplacePreviousInventory")]
    [InlineData("BeforeReplaceInventory")]
    [InlineData("AfterReplaceInventory")]
    [InlineData("StageLedger")]
    [InlineData("BeforeReplaceLedger")]
    [InlineData("AfterReplaceLedger")]
    [InlineData("BeforeTransactionClear")]
    public void E12_Apply_Replace_FaultInvariants_HoldAtEveryPoint(string failPointName)
    {
        var store = new InMemoryProvisioningStore();
        string oldDoc = ValidInventory("org-key-1");
        string newDoc = ValidInventory("org-key-2");
        store.SeedGeneration1(oldDoc);

        DriveFaultAndAssertInvariants(store, failPointName, () => Request("apply", newDoc), new[] { oldDoc, newDoc });
    }

    [Theory] // E13 — Fresh create (absent->gen1) upholds the fault invariants at every wired point.
    [InlineData("None")]
    [InlineData("EnsureManagedDirectory")]
    [InlineData("ApplyAcl")]
    [InlineData("StageInventory")]
    [InlineData("BeforeReplaceInventory")]
    [InlineData("AfterReplaceInventory")]
    [InlineData("StageLedger")]
    [InlineData("BeforeReplaceLedger")]
    [InlineData("AfterReplaceLedger")]
    [InlineData("BeforeTransactionClear")]
    public void E13_Apply_Create_FaultInvariants_HoldAtEveryPoint(string failPointName)
    {
        var store = new InMemoryProvisioningStore();
        string doc = ValidInventory("org-key-1");

        DriveFaultAndAssertInvariants(store, failPointName, () => Request("apply", doc), new[] { doc });
    }

    [Theory] // E14 — Reversible-swap rollback (gen2->gen3) upholds the fault invariants at every wired point.
    [InlineData("None")]
    [InlineData("StageInventory")]
    [InlineData("StagePreviousInventory")]
    [InlineData("ApplyAcl")]
    [InlineData("BeforeReplaceInventory")]
    [InlineData("AfterReplaceInventory")]
    [InlineData("BeforeReplacePreviousInventory")]
    [InlineData("AfterReplacePreviousInventory")]
    [InlineData("StageLedger")]
    [InlineData("BeforeReplaceLedger")]
    [InlineData("AfterReplaceLedger")]
    [InlineData("BeforeTransactionClear")]
    public void E14_Rollback_Swap_FaultInvariants_HoldAtEveryPoint(string failPointName)
    {
        var store = new InMemoryProvisioningStore();
        string gen1 = ValidInventory("org-key-1");
        string gen2 = ValidInventory("org-key-2");
        store.SeedGeneration2(gen2, gen1);

        DriveFaultAndAssertInvariants(store, failPointName, () => Request("rollback"), new[] { gen1, gen2 });
    }

    [Fact] // E15 — A second replace advances gen2->gen3 with the backup taken from the gen2 owned bytes.
    public void E15_Apply_SecondReplace_AdvancesGeneration_BackupFromCurrentBytes()
    {
        var store = new InMemoryProvisioningStore();
        string gen1 = ValidInventory("org-key-1");
        string gen2 = ValidInventory("org-key-2");
        string gen3 = ValidInventory("org-key-3");
        store.SeedGeneration2(gen2, gen1);

        ProvisioningResult r = Executor(store).Execute(Request("apply", gen3));

        Assert.Equal(ProvisioningResultCodes.Ok, r.Code);
        Assert.Equal(3, store.CurrentLedger!.CurrentGeneration);
        Assert.Equal(2, store.CurrentLedger.PreviousGeneration);
        Assert.Equal(gen3, store.CurrentInventory);
        // Backup is the prior CURRENT (gen2) owned bytes, not the caller's or gen1.
        Assert.Equal(gen2, store.PreviousInventoryContent);
    }

    [Fact] // E16 — Rollback of a rollback swaps back to the original current bytes via monotonic generations.
    public void E16_Rollback_OfRollback_SwapsBackToOriginal_MonotonicGeneration()
    {
        var store = new InMemoryProvisioningStore();
        string gen1 = ValidInventory("org-key-1");
        string gen2 = ValidInventory("org-key-2");
        store.SeedGeneration2(gen2, gen1);

        ProvisioningResult first = Executor(store).Execute(Request("rollback"));
        Assert.Equal(ProvisioningResultCodes.Ok, first.Code);
        Assert.Equal(gen1, store.CurrentInventory);
        Assert.Equal(3, store.CurrentLedger!.CurrentGeneration);

        ProvisioningResult second = Executor(store).Execute(Request("rollback"));
        Assert.Equal(ProvisioningResultCodes.Ok, second.Code);
        // Swapped back to the original current bytes; generation advanced monotonically.
        Assert.Equal(gen2, store.CurrentInventory);
        Assert.Equal(gen1, store.PreviousInventoryContent);
        Assert.Equal(4, store.CurrentLedger!.CurrentGeneration);
    }

    [Fact] // E17 — Verify on a gen2 owned pair is Ok and mutates NOTHING.
    public void E17_Verify_Gen2_IsOk_ZeroMutation()
    {
        var store = new InMemoryProvisioningStore();
        store.SeedGeneration2(ValidInventory("org-key-2"), ValidInventory("org-key-1"));
        int before = store.MutationCount;

        ProvisioningResult r = Executor(store).Execute(Request("verify"));

        Assert.Equal(ProvisioningResultCodes.Ok, r.Code);
        Assert.Equal(before, store.MutationCount);
    }

    [Fact] // E18 — planOnly rollback is advisory: planned but ZERO mutation.
    public void E18_Rollback_PlanOnly_ZeroMutation()
    {
        var store = new InMemoryProvisioningStore();
        string gen1 = ValidInventory("org-key-1");
        string gen2 = ValidInventory("org-key-2");
        store.SeedGeneration2(gen2, gen1);
        int before = store.MutationCount;

        ProvisioningResult r = Executor(store).Execute(Request("rollback", planOnly: true));

        Assert.Equal(ProvisioningResultCodes.Ok, r.Code);
        Assert.Equal(before, store.MutationCount);
        Assert.Equal(2, store.CurrentLedger!.CurrentGeneration);
        Assert.Equal(gen2, store.CurrentInventory);
    }
}
#endif
