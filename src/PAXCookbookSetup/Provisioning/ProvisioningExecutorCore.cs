#if MANAGED_INVENTORY_PROVISIONING
using System;
using System.Linq;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provisioning;

// ---------------------------------------------------------------------------
// Cycle-8 gated provisioning executor (NON-LIVE this cycle).
//
// The executor OBSERVES disk through the narrow closed adapters, builds the three
// synthetic states, and defers ALL policy to the pure, portable
// ProvisioningPlanner.Plan (the single source of ownership proof + ordered action
// list). It then FAITHFULLY performs exactly the planned actions through a closed
// switch (default => fail-closed adapter fault) with a persisted transaction
// marker so a crash mid-transaction is recoverable or fails closed.
//
// Backups (previous-generation bytes) are ALWAYS taken from the ownership-proven
// CURRENT owned inventory bytes on the machine — never from the caller's request.
// Rollback is a reversible swap through a NEW monotonic generation. Nothing here
// runs a process, elevates, or writes outside the adapter's exact owned/staged
// artifacts. This cycle the dispatch never constructs a live executor.
// ---------------------------------------------------------------------------
internal sealed class ProvisioningExecutor
{
    private readonly IProvisioningFileSystem _fs;
    private readonly IProvisioningAcl _acl;
    private readonly IProvisioningClock _clock;

    internal ProvisioningExecutor(IProvisioningFileSystem fs, IProvisioningAcl acl, IProvisioningClock clock)
    {
        _fs = fs;
        _acl = acl;
        _clock = clock;
    }

    // Production wiring. NOT invoked this cycle (dispatch is non-live); present so
    // the real adapter graph is proven to compile and is ready for a future cycle.
    internal static ProvisioningExecutor CreateProduction()
    {
        OsProvisioningFileSystem fs = OsProvisioningFileSystem.CreateProduction();
        var acl = new OsProvisioningAcl(fs.ManagedDirectory);
        return new ProvisioningExecutor(fs, acl, new SystemProvisioningClock());
    }

    public ProvisioningResult Execute(ProvisioningRequest? request)
    {
        ProvisioningPlan plan = BuildPlan(request);
        if (!plan.IsPlanned)
        {
            return ProvisioningResult.FromPlan(plan);
        }

        // Bounded crash recovery always executes: it mutates to restore a quiescent
        // state. Its returned code is RecoveryRequired (non-success) even on success;
        // a follow-up verify confirms the clean terminal state.
        if (plan.ResultState == ProvisioningPlanState.RecoveryPlanned)
        {
            return ExecuteActions(plan, request!);
        }

        // Read-only / advisory paths never mutate.
        if (!plan.WouldMutate
            || plan.Operation == ProvisioningOperation.Verify
            || plan.ResultState == ProvisioningPlanState.NoOpUpToDate
            || request!.PlanOnly)
        {
            return ProvisioningResult.FromPlan(plan);
        }

        return ExecuteActions(plan, request!);
    }

    // --- Observation -> pure planner -------------------------------------

    private ProvisioningPlan BuildPlan(ProvisioningRequest? request)
    {
        SyntheticTrustState trust = ObserveTrust();
        SyntheticSourceState source = ObserveSource();
        SyntheticLedgerState ledger = ObserveLedger();
        return ProvisioningPlanner.Plan(request, source, ledger, trust);
    }

    private SyntheticTrustState ObserveTrust()
    {
        if (!_fs.ManagedDirectoryExists())
        {
            return SyntheticTrustState.AbsentDirectory();
        }

        AclDescriptor descriptor = _acl.Inspect(ProvisioningArtifact.ManagedDirectory);
        bool ownerTrusted = AclDescriptor.RequiredOwnerSids.Contains(descriptor.OwnerSid);
        bool aclTrusted = AclDescriptorValidator.Validate(descriptor).IsValid;
        return new SyntheticTrustState(
            managedDirectoryPresent: true,
            ownerTrusted: ownerTrusted,
            aclTrusted: aclTrusted,
            noReparsePoint: true,
            pathContained: true);
    }

    private SyntheticSourceState ObserveSource()
    {
        if (!_fs.OwnedExists(ProvisioningArtifact.Inventory))
        {
            return SyntheticSourceState.Absent();
        }

        string inventoryHash = _fs.HashOwnedInventory(ProvisioningArtifact.Inventory);
        bool unknownSibling = _fs.HasUnknownSibling();

        if (_fs.OwnedExists(ProvisioningArtifact.PreviousInventory))
        {
            string previousHash = _fs.HashOwnedInventory(ProvisioningArtifact.PreviousInventory);
            bool previousParses = OrganizationKeyInventoryParser
                .Parse(_fs.ReadOwnedInventory(ProvisioningArtifact.PreviousInventory)).IsValid;
            return SyntheticSourceState.PresentWithPrevious(inventoryHash, previousHash, unknownSibling, previousParses);
        }

        return SyntheticSourceState.Present(inventoryHash, unknownSibling);
    }

    private SyntheticLedgerState ObserveLedger()
    {
        OwnershipLedger? ledger = _fs.ReadLedger();
        if (ledger is null)
        {
            return SyntheticLedgerState.Absent();
        }

        // Present the LIVE transaction marker to the planner so an in-progress crash
        // window (marker != quiescent) is visible and triggers bounded recovery.
        ProvisioningTransactionState marker = _fs.ReadTransactionState();
        OwnershipLedger observed = WithTransactionState(ledger, marker);
        bool valid = OwnershipLedgerValidator.Validate(observed).IsValid;
        return SyntheticLedgerState.Present(observed, valid);
    }

    private static OwnershipLedger WithTransactionState(OwnershipLedger ledger, ProvisioningTransactionState state)
        => new(
            ledger.LedgerSchemaVersion,
            ledger.ProductOwnershipMarker,
            ledger.ManagedFeatureId,
            ledger.CurrentGeneration,
            ledger.CurrentInventorySha256,
            ledger.PreviousGeneration,
            ledger.PreviousInventorySha256,
            state,
            ledger.OwnedFileNames,
            ledger.AclPolicyVersion,
            ledger.CreatedUtc,
            ledger.UpdatedUtc,
            ledger.LastOperationId);

    // --- Faithful action performance -------------------------------------

    private ProvisioningResult ExecuteActions(ProvisioningPlan plan, ProvisioningRequest request)
    {
        try
        {
            MutationContext ctx = ResolveContext(plan, request);
            foreach (ProvisioningPlanAction action in plan.Actions)
            {
                PerformAction(action, ctx);
            }

            return ProvisioningResult.FromPlan(plan);
        }
        catch (ProvisioningAdapterFault)
        {
            // A fault mid-transaction leaves the marker in-progress. Re-observe and
            // re-plan: a recoverable window yields RecoveryRequired, an unrecoverable
            // one a fail-closed rejection. Either result is NON-success.
            ProvisioningPlan replanned = BuildPlan(request);
            return ProvisioningResult.FromPlan(replanned);
        }
    }

    private void PerformAction(ProvisioningPlanAction action, MutationContext ctx)
    {
        switch (action.Kind)
        {
            case ProvisioningActionKind.EnsureManagedDirectory:
                _fs.EnsureManagedDirectory();
                break;

            case ProvisioningActionKind.StampAcl:
                _acl.Apply(ProvisioningAclBuilder.BuildManagedDirectoryDescriptor(), ProvisioningArtifact.ManagedDirectory);
                break;

            case ProvisioningActionKind.WriteTempInventory:
                _fs.StageInventoryTemp(ProvisioningArtifact.TempInventory, ctx.OperationId, ctx.NewInventoryContent!);
                break;

            case ProvisioningActionKind.ValidateTempInventory:
                RequireValidInventoryTemp(ProvisioningArtifact.TempInventory, ctx.OperationId, ctx.NewInventoryHash);
                break;

            case ProvisioningActionKind.ReplaceInventory:
                MarkPreparing(ctx);
                _fs.ReplaceOwnedInventoryFromTemp(ProvisioningArtifact.Inventory, ProvisioningArtifact.TempInventory, ctx.OperationId);
                _fs.WriteTransactionState(ProvisioningTransactionState.InventoryCommitted, ctx.OperationId);
                break;

            case ProvisioningActionKind.WriteTempPreviousInventory:
                _fs.StageInventoryTemp(ProvisioningArtifact.TempPreviousInventory, ctx.OperationId, ctx.PreviousInventoryContent!);
                break;

            case ProvisioningActionKind.ValidateTempPreviousInventory:
                RequireValidInventoryTemp(ProvisioningArtifact.TempPreviousInventory, ctx.OperationId, ctx.PreviousInventoryHash);
                break;

            case ProvisioningActionKind.ReplacePreviousInventory:
                MarkPreparing(ctx);
                _fs.ReplaceOwnedInventoryFromTemp(ProvisioningArtifact.PreviousInventory, ProvisioningArtifact.TempPreviousInventory, ctx.OperationId);
                break;

            case ProvisioningActionKind.RestorePreviousGeneration:
                // Used by rollback (temp already staged with the swap bytes) AND by
                // recovery (whose action list has no preceding WriteTempInventory).
                // Stage the resolved restore bytes idempotently, then atomically
                // replace the current inventory.
                MarkPreparing(ctx);
                _fs.StageInventoryTemp(ProvisioningArtifact.TempInventory, ctx.OperationId, ctx.NewInventoryContent!);
                _fs.ReplaceOwnedInventoryFromTemp(ProvisioningArtifact.Inventory, ProvisioningArtifact.TempInventory, ctx.OperationId);
                _fs.WriteTransactionState(ProvisioningTransactionState.InventoryCommitted, ctx.OperationId);
                break;

            case ProvisioningActionKind.WriteTempLedger:
                _fs.StageLedgerTemp(ctx.OperationId, ctx.NextLedger);
                break;

            case ProvisioningActionKind.ValidateTempLedger:
                if (!OwnershipLedgerValidator.Validate(ctx.NextLedger).IsValid)
                {
                    throw new ProvisioningAdapterFault(ProvisioningActionKind.ValidateTempLedger);
                }
                break;

            case ProvisioningActionKind.ReplaceLedger:
                MarkPreparing(ctx);
                _fs.ReplaceLedgerFromTemp(ctx.OperationId);
                _fs.WriteTransactionState(ProvisioningTransactionState.LedgerCommitted, ctx.OperationId);
                break;

            case ProvisioningActionKind.VerifyInventoryLedgerPair:
                RequireConsistentPair();
                break;

            case ProvisioningActionKind.RemoveInventory:
                _fs.RemoveOwned(ProvisioningArtifact.Inventory);
                break;

            case ProvisioningActionKind.RemovePreviousInventory:
                _fs.RemoveOwned(ProvisioningArtifact.PreviousInventory);
                break;

            case ProvisioningActionKind.RemoveLedger:
                _fs.RemoveOwned(ProvisioningArtifact.Ledger);
                break;

            case ProvisioningActionKind.RemoveTransactionArtifact:
                CompleteTransaction(ctx);
                break;

            case ProvisioningActionKind.RemoveManagedDirectoryIfEmpty:
                _fs.RemoveManagedDirectoryIfEmpty();
                break;

            default:
                // Fail closed: an unmapped action is a defect, never a silent skip.
                throw new ProvisioningAdapterFault(action.Kind);
        }
    }

    private void MarkPreparing(MutationContext ctx)
    {
        if (!ctx.PreparingMarked)
        {
            _fs.WriteTransactionState(ProvisioningTransactionState.Preparing, ctx.OperationId);
            ctx.PreparingMarked = true;
        }
    }

    private void CompleteTransaction(MutationContext ctx)
    {
        _fs.RemoveInventoryTemp(ProvisioningArtifact.TempInventory, ctx.OperationId);
        _fs.RemoveInventoryTemp(ProvisioningArtifact.TempPreviousInventory, ctx.OperationId);
        _fs.RemoveLedgerTemp(ctx.OperationId);
        if (ctx.RecoveryDropsPreviousBackup && _fs.OwnedExists(ProvisioningArtifact.PreviousInventory))
        {
            _fs.RemoveOwned(ProvisioningArtifact.PreviousInventory);
        }
        _fs.RemoveTransactionArtifact();
    }

    private void RequireValidInventoryTemp(ProvisioningArtifact tempArtifact, string operationId, string? expectedHash)
    {
        string staged = _fs.ReadInventoryTemp(tempArtifact, operationId);
        if (!OrganizationKeyInventoryParser.Parse(staged).IsValid)
        {
            throw new ProvisioningAdapterFault(ProvisioningActionKind.ValidateTempInventory);
        }

        string actualHash = _fs.HashInventoryTemp(tempArtifact, operationId);
        if (expectedHash is not null && !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProvisioningAdapterFault(ProvisioningActionKind.ValidateTempInventory);
        }
    }

    private void RequireConsistentPair()
    {
        OwnershipLedger? ledger = _fs.ReadLedger();
        if (ledger is null || !_fs.OwnedExists(ProvisioningArtifact.Inventory))
        {
            throw new ProvisioningAdapterFault(ProvisioningActionKind.VerifyInventoryLedgerPair);
        }

        string inventoryHash = _fs.HashOwnedInventory(ProvisioningArtifact.Inventory);
        if (!string.Equals(inventoryHash, ledger.CurrentInventorySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProvisioningAdapterFault(ProvisioningActionKind.VerifyInventoryLedgerPair);
        }
    }

    // --- Context resolution ----------------------------------------------

    private MutationContext ResolveContext(ProvisioningPlan plan, ProvisioningRequest request)
    {
        var ctx = new MutationContext { OperationId = request.OperationId };
        switch (plan.ResultState)
        {
            case ProvisioningPlanState.CreatePlanned:
            {
                string content = request.InventoryDocument;
                ctx.NewInventoryContent = content;
                ctx.NewInventoryHash = ProvisioningHash.Sha256Hex(content);
                ctx.NextLedger = BuildLedger(1, ctx.NewInventoryHash, null, null, ctx.OperationId);
                break;
            }

            case ProvisioningPlanState.ReplacePlanned:
            {
                OwnershipLedger current = RequireOwnedLedger();
                string currentContent = _fs.ReadOwnedInventory(ProvisioningArtifact.Inventory);
                string currentHash = ProvisioningHash.Sha256Hex(currentContent);
                RequireBackupMatchesLedger(currentHash, current.CurrentInventorySha256);

                string newContent = request.InventoryDocument;
                ctx.NewInventoryContent = newContent;
                ctx.NewInventoryHash = ProvisioningHash.Sha256Hex(newContent);
                // Backup is the ownership-proven CURRENT owned bytes, never the caller's.
                ctx.PreviousInventoryContent = currentContent;
                ctx.PreviousInventoryHash = currentHash;
                ctx.NextLedger = BuildLedger(
                    current.CurrentGeneration + 1, ctx.NewInventoryHash,
                    current.CurrentGeneration, currentHash, ctx.OperationId);
                break;
            }

            case ProvisioningPlanState.RollbackPlanned:
            {
                OwnershipLedger current = RequireOwnedLedger();
                string currentContent = _fs.ReadOwnedInventory(ProvisioningArtifact.Inventory);
                string currentHash = ProvisioningHash.Sha256Hex(currentContent);
                RequireBackupMatchesLedger(currentHash, current.CurrentInventorySha256);

                string previousContent = _fs.ReadOwnedInventory(ProvisioningArtifact.PreviousInventory);
                string previousHash = ProvisioningHash.Sha256Hex(previousContent);

                // Reversible swap through a NEW monotonic generation:
                //   new current  = old PREVIOUS bytes
                //   new previous = old CURRENT bytes
                ctx.NewInventoryContent = previousContent;
                ctx.NewInventoryHash = previousHash;
                ctx.PreviousInventoryContent = currentContent;
                ctx.PreviousInventoryHash = currentHash;
                ctx.NextLedger = BuildLedger(
                    current.CurrentGeneration + 1, previousHash,
                    current.CurrentGeneration, currentHash, ctx.OperationId);
                break;
            }

            case ProvisioningPlanState.RemovePlanned:
                // Remove actions target owned artifacts directly; no content needed.
                ctx.NextLedger = RequireOwnedLedger();
                break;

            case ProvisioningPlanState.RecoveryPlanned:
            {
                OwnershipLedger current = RequireOwnedLedger();
                if (current.PreviousGeneration is int previousGeneration)
                {
                    // Revert to the last committed previous generation.
                    string previousContent = _fs.ReadOwnedInventory(ProvisioningArtifact.PreviousInventory);
                    string previousHash = ProvisioningHash.Sha256Hex(previousContent);
                    ctx.NewInventoryContent = previousContent;
                    ctx.NewInventoryHash = previousHash;
                    ctx.NextLedger = BuildLedger(previousGeneration, previousHash, null, null, ctx.OperationId);
                    ctx.RecoveryDropsPreviousBackup = true;
                }
                else
                {
                    // A generation-1 create crash: re-stamp the current generation forward.
                    string currentContent = _fs.ReadOwnedInventory(ProvisioningArtifact.Inventory);
                    string currentHash = ProvisioningHash.Sha256Hex(currentContent);
                    ctx.NewInventoryContent = currentContent;
                    ctx.NewInventoryHash = currentHash;
                    ctx.NextLedger = BuildLedger(current.CurrentGeneration, currentHash, null, null, ctx.OperationId);
                }
                break;
            }

            default:
                throw new ProvisioningAdapterFault(ProvisioningActionKind.StampAcl);
        }

        return ctx;
    }

    private OwnershipLedger RequireOwnedLedger()
    {
        OwnershipLedger? ledger = _fs.ReadLedger();
        if (ledger is null)
        {
            throw new ProvisioningAdapterFault(ProvisioningActionKind.VerifyInventoryLedgerPair);
        }

        return ledger;
    }

    private static void RequireBackupMatchesLedger(string actualHash, string ledgerHash)
    {
        // Defensive: the backup MUST be the ownership-proven current bytes. The
        // planner already proved this; a mismatch here means disk changed under us.
        if (!string.Equals(actualHash, ledgerHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProvisioningAdapterFault(ProvisioningActionKind.ValidateTempPreviousInventory);
        }
    }

    private OwnershipLedger BuildLedger(int generation, string currentHash, int? previousGeneration, string? previousHash, string operationId)
        => new(
            ProvisioningContract.LedgerSchemaVersion,
            ProvisioningContract.ProductOwnershipMarker,
            ProvisioningContract.ManagedFeatureId,
            generation,
            currentHash,
            previousGeneration,
            previousHash,
            ProvisioningTransactionState.Done,
            ProvisioningContract.OwnedFileNames,
            ProvisioningContract.AclPolicyVersion,
            _clock.UtcNowIso(),
            _clock.UtcNowIso(),
            operationId);

    private sealed class MutationContext
    {
        public string OperationId { get; set; } = string.Empty;

        public string? NewInventoryContent { get; set; }

        public string? NewInventoryHash { get; set; }

        public string? PreviousInventoryContent { get; set; }

        public string? PreviousInventoryHash { get; set; }

        public OwnershipLedger NextLedger { get; set; } = null!;

        public bool PreparingMarked { get; set; }

        public bool RecoveryDropsPreviousBackup { get; set; }
    }
}
#endif
