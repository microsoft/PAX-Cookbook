#if MANAGED_INVENTORY_PROVISIONING
using System;
using System.Collections.Generic;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Provisioning;

namespace PAXCookbookSetup.Tests.Provisioning;

// A deterministic in-memory reference implementation of the narrow provisioning
// adapters. It models the managed-directory state (inventory / previous-inventory
// content, the parsed ownership ledger, a separate transaction marker, and staged
// temps) with bounded single-shot fault injection, so the executor's transactional
// apply / reversible-swap rollback / read-only verify / exact remove / bounded
// crash recovery can be proven WITHOUT touching real ProgramData, an ACL, a
// process, or the network.
internal sealed class InMemoryProvisioningStore : IProvisioningFileSystem, IProvisioningAcl, IProvisioningClock
{
    private bool _dirExists;
    private string? _inventory;
    private string? _previous;
    private OwnershipLedger? _ledger;
    private ProvisioningTransactionState _marker = ProvisioningTransactionState.Idle;
    private bool _unknownSibling;
    private AclDescriptor _descriptor = ProvisioningAclBuilder.BuildManagedDirectoryDescriptor();

    private readonly Dictionary<string, string> _invTemps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnershipLedger> _ledgerTemps = new(StringComparer.Ordinal);

    private ProvisioningFailPoint _fail = ProvisioningFailPoint.None;
    private int _clockTick;

    public int MutationCount { get; private set; }

    // --- Test seeding -----------------------------------------------------

    public void SeedGeneration1(string inventoryContent)
    {
        _dirExists = true;
        _inventory = inventoryContent;
        _previous = null;
        _ledger = Ledger(1, Hash(inventoryContent), null, null);
        _marker = ProvisioningTransactionState.Idle;
    }

    public void SeedGeneration2(string currentContent, string previousContent)
    {
        _dirExists = true;
        _inventory = currentContent;
        _previous = previousContent;
        _ledger = Ledger(2, Hash(currentContent), 1, Hash(previousContent));
        _marker = ProvisioningTransactionState.Idle;
    }

    public void MakeUntrusted()
        => _descriptor = new AclDescriptor(
            AclDescriptor.SidEveryone,
            new List<AclEntry> { new(AclDescriptor.SidEveryone, AclAccess.Write) },
            protectedDacl: false);

    public void SetUnknownSibling(bool present) => _unknownSibling = present;

    public void ArmFault(ProvisioningFailPoint failPoint) => _fail = failPoint;

    public string? CurrentInventory => _inventory;

    public string? PreviousInventoryContent => _previous;

    public OwnershipLedger? CurrentLedger => _ledger;

    public ProvisioningTransactionState Marker => _marker;

    public bool DirectoryPresent => _dirExists;

    private static string Hash(string content) => ProvisioningHash.Sha256Hex(content);

    private static OwnershipLedger Ledger(int gen, string curHash, int? prevGen, string? prevHash)
        => new(
            ProvisioningContract.LedgerSchemaVersion,
            ProvisioningContract.ProductOwnershipMarker,
            ProvisioningContract.ManagedFeatureId,
            gen, curHash, prevGen, prevHash,
            ProvisioningTransactionState.Done,
            ProvisioningContract.OwnedFileNames,
            ProvisioningContract.AclPolicyVersion,
            "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", "seed-op");

    private void FireIf(ProvisioningFailPoint point)
    {
        if (_fail == point)
        {
            _fail = ProvisioningFailPoint.None; // single-shot.
            throw new ProvisioningAdapterFault(ProvisioningActionKind.ReplaceInventory);
        }
    }

    // --- IProvisioningFileSystem -----------------------------------------

    public bool ManagedDirectoryExists() => _dirExists;

    public void EnsureManagedDirectory()
    {
        MutationCount++;
        FireIf(ProvisioningFailPoint.EnsureManagedDirectory);
        _dirExists = true;
    }

    public bool OwnedExists(ProvisioningArtifact artifact) => artifact switch
    {
        ProvisioningArtifact.Inventory => _inventory is not null,
        ProvisioningArtifact.PreviousInventory => _previous is not null,
        ProvisioningArtifact.Ledger => _ledger is not null,
        _ => false,
    };

    public string ReadOwnedInventory(ProvisioningArtifact artifact) => artifact switch
    {
        ProvisioningArtifact.Inventory => _inventory!,
        ProvisioningArtifact.PreviousInventory => _previous!,
        _ => throw new InvalidOperationException(),
    };

    public string HashOwnedInventory(ProvisioningArtifact artifact) => Hash(ReadOwnedInventory(artifact));

    public bool HasUnknownSibling() => _unknownSibling;

    public void StageInventoryTemp(ProvisioningArtifact tempArtifact, string operationId, string content)
    {
        MutationCount++;
        if (tempArtifact == ProvisioningArtifact.TempInventory)
        {
            FireIf(ProvisioningFailPoint.StageInventory);
        }
        else if (tempArtifact == ProvisioningArtifact.TempPreviousInventory)
        {
            FireIf(ProvisioningFailPoint.StagePreviousInventory);
        }
        _invTemps[Key(tempArtifact, operationId)] = content;
    }

    public string ReadInventoryTemp(ProvisioningArtifact tempArtifact, string operationId) => _invTemps[Key(tempArtifact, operationId)];

    public string HashInventoryTemp(ProvisioningArtifact tempArtifact, string operationId) => Hash(_invTemps[Key(tempArtifact, operationId)]);

    public void ReplaceOwnedInventoryFromTemp(ProvisioningArtifact ownedArtifact, ProvisioningArtifact tempArtifact, string operationId)
    {
        MutationCount++;
        if (ownedArtifact == ProvisioningArtifact.PreviousInventory)
        {
            FireIf(ProvisioningFailPoint.BeforeReplacePreviousInventory);
            _previous = _invTemps[Key(tempArtifact, operationId)];
            FireIf(ProvisioningFailPoint.AfterReplacePreviousInventory);
        }
        else
        {
            FireIf(ProvisioningFailPoint.BeforeReplaceInventory);
            _inventory = _invTemps[Key(tempArtifact, operationId)];
            FireIf(ProvisioningFailPoint.AfterReplaceInventory);
        }
    }

    public void RemoveOwned(ProvisioningArtifact artifact)
    {
        MutationCount++;
        switch (artifact)
        {
            case ProvisioningArtifact.Inventory:
                FireIf(ProvisioningFailPoint.RemoveInventory);
                _inventory = null;
                break;
            case ProvisioningArtifact.PreviousInventory:
                FireIf(ProvisioningFailPoint.RemovePreviousInventory);
                _previous = null;
                break;
            case ProvisioningArtifact.Ledger:
                FireIf(ProvisioningFailPoint.RemoveLedger);
                _ledger = null;
                break;
        }
    }

    public void RemoveInventoryTemp(ProvisioningArtifact tempArtifact, string operationId)
    {
        MutationCount++;
        _invTemps.Remove(Key(tempArtifact, operationId));
    }

    public bool RemoveManagedDirectoryIfEmpty()
    {
        MutationCount++;
        FireIf(ProvisioningFailPoint.RemoveManagedDirectory);
        if (_inventory is null && _previous is null && _ledger is null && !_unknownSibling)
        {
            _dirExists = false;
            return true;
        }
        return false;
    }

    public OwnershipLedger? ReadLedger() => _ledger;

    public void StageLedgerTemp(string operationId, OwnershipLedger ledger)
    {
        MutationCount++;
        FireIf(ProvisioningFailPoint.StageLedger);
        _ledgerTemps[operationId] = ledger;
    }

    public void ReplaceLedgerFromTemp(string operationId)
    {
        MutationCount++;
        FireIf(ProvisioningFailPoint.BeforeReplaceLedger);
        _ledger = _ledgerTemps[operationId];
        FireIf(ProvisioningFailPoint.AfterReplaceLedger);
    }

    public void RemoveLedgerTemp(string operationId)
    {
        MutationCount++;
        _ledgerTemps.Remove(operationId);
    }

    public ProvisioningTransactionState ReadTransactionState() => _marker;

    public void WriteTransactionState(ProvisioningTransactionState state, string operationId)
    {
        MutationCount++;
        _marker = state;
    }

    public void RemoveTransactionArtifact()
    {
        MutationCount++;
        FireIf(ProvisioningFailPoint.BeforeTransactionClear);
        _marker = ProvisioningTransactionState.Idle;
    }

    // --- IProvisioningAcl -------------------------------------------------

    public AclDescriptor Inspect(ProvisioningArtifact artifact) => _descriptor;

    public void Apply(AclDescriptor descriptor, ProvisioningArtifact artifact)
    {
        MutationCount++;
        FireIf(ProvisioningFailPoint.ApplyAcl);
    }

    // --- IProvisioningClock ----------------------------------------------

    public string UtcNowIso() => "2026-07-22T00:00:" + (_clockTick++).ToString("D2") + "Z";

    private static string Key(ProvisioningArtifact artifact, string operationId) => artifact + "|" + operationId;
}
#endif
