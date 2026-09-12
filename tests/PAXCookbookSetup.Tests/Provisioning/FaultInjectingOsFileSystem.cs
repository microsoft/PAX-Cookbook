#if MANAGED_INVENTORY_PROVISIONING
using System;
using System.IO;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Provisioning;

namespace PAXCookbookSetup.Tests.Provisioning;

// ---------------------------------------------------------------------------
// Cycle-9 TEST-HARNESS-ONLY fault seam (GATED, NON-LIVE).
//
// The Cycle-9 sandbox dry-run drives the REAL Cycle-8 provisioning executor over
// the REAL OsProvisioningFileSystem rooted at an absolute OS-temp sandbox. To
// prove the crash-recovery matrix over the REAL on-disk state WITHOUT a real
// crash, a decorator wraps the real OS filesystem, delegates EVERY genuine IO
// operation (staging, atomic replace, byte-exact hashing, ledger JSON round-trip,
// transaction-marker persistence, remove, unknown-sibling detection) to it, and
// throws a bounded single-shot ProvisioningAdapterFault at the armed transaction
// point. A FRESH executor over the SAME sandbox then runs bounded recovery over
// the REAL residual on-disk state.
//
// The single deliberate divergence from production is EnsureManagedDirectory: the
// production adapter stamps a protected lockdown DACL (write only to SYSTEM /
// BuiltinAdministrators) that would DENY the UNELEVATED test principal's genuine
// writes (exactly why the Cycle-8 O01 adapter test bypasses it). This decorator
// therefore creates the managed directory test-safe (inherited OS-temp DACL, so
// the test can write) and NEVER applies the production lockdown to the directory
// the executor writes. The EXACT production ACL descriptor is proven STRUCTURALLY
// (AclDescriptorValidator + ProvisioningAclBuilder.BuildManagedDirectoryDescriptor)
// and a REAL protected-DACL apply/inspect is proven on a SEPARATE throwaway probe
// directory — both DISTINCT from this integration run.
//
// This file is compiled ONLY under MANAGED_INVENTORY_PROVISIONING and is reachable
// ONLY from the Setup test project via InternalsVisibleTo. It has no production
// call site.
// ---------------------------------------------------------------------------

// A bounded single-shot fault arming shared by the filesystem decorator and the
// sandbox ACL, so a fault injected at ONE transaction point fires exactly once and
// then disarms (mirrors the in-memory reference store's single-shot behavior).
internal sealed class SandboxFaultArmer
{
    private ProvisioningFailPoint _armed = ProvisioningFailPoint.None;

    public void Arm(ProvisioningFailPoint point) => _armed = point;

    public bool IsArmed => _armed != ProvisioningFailPoint.None;

    // Fire (throw a bounded, content-free adapter fault) exactly when the armed
    // point is reached, then disarm. The action kind carried is deliberately a
    // fixed bounded token (identical to the in-memory reference store) — it leaks
    // no path, identifier, byte, ACL, or SID.
    public void FireIf(ProvisioningFailPoint point)
    {
        if (_armed == point)
        {
            _armed = ProvisioningFailPoint.None;
            throw new ProvisioningAdapterFault(ProvisioningActionKind.ReplaceInventory);
        }
    }
}

// The real OS filesystem decorator. Delegates all genuine IO to the wrapped real
// OsProvisioningFileSystem; neutralizes ONLY the lockdown in EnsureManagedDirectory
// (test-safe create) so the unelevated test retains write; injects the armed
// single-shot fault at the mirrored transaction points.
internal sealed class FaultInjectingOsFileSystem : IProvisioningFileSystem
{
    private readonly OsProvisioningFileSystem _inner;
    private readonly SandboxFaultArmer _armer;

    public FaultInjectingOsFileSystem(OsProvisioningFileSystem inner, SandboxFaultArmer armer)
    {
        _inner = inner;
        _armer = armer;
    }

    public bool ManagedDirectoryExists() => _inner.ManagedDirectoryExists();

    // Test-safe create: NO production lockdown DACL (see file header). Fault fires
    // BEFORE the directory is created, mirroring the in-memory reference store.
    public void EnsureManagedDirectory()
    {
        _armer.FireIf(ProvisioningFailPoint.EnsureManagedDirectory);
        Directory.CreateDirectory(_inner.ManagedDirectory);
    }

    public bool OwnedExists(ProvisioningArtifact artifact) => _inner.OwnedExists(artifact);

    public string ReadOwnedInventory(ProvisioningArtifact artifact) => _inner.ReadOwnedInventory(artifact);

    public string HashOwnedInventory(ProvisioningArtifact artifact) => _inner.HashOwnedInventory(artifact);

    public bool HasUnknownSibling() => _inner.HasUnknownSibling();

    public void StageInventoryTemp(ProvisioningArtifact tempArtifact, string operationId, string content)
    {
        if (tempArtifact == ProvisioningArtifact.TempInventory)
        {
            _armer.FireIf(ProvisioningFailPoint.StageInventory);
        }
        else if (tempArtifact == ProvisioningArtifact.TempPreviousInventory)
        {
            _armer.FireIf(ProvisioningFailPoint.StagePreviousInventory);
        }
        _inner.StageInventoryTemp(tempArtifact, operationId, content);
    }

    public string ReadInventoryTemp(ProvisioningArtifact tempArtifact, string operationId)
        => _inner.ReadInventoryTemp(tempArtifact, operationId);

    public string HashInventoryTemp(ProvisioningArtifact tempArtifact, string operationId)
        => _inner.HashInventoryTemp(tempArtifact, operationId);

    public void ReplaceOwnedInventoryFromTemp(ProvisioningArtifact ownedArtifact, ProvisioningArtifact tempArtifact, string operationId)
    {
        if (ownedArtifact == ProvisioningArtifact.PreviousInventory)
        {
            _armer.FireIf(ProvisioningFailPoint.BeforeReplacePreviousInventory);
            _inner.ReplaceOwnedInventoryFromTemp(ownedArtifact, tempArtifact, operationId);
            _armer.FireIf(ProvisioningFailPoint.AfterReplacePreviousInventory);
        }
        else
        {
            _armer.FireIf(ProvisioningFailPoint.BeforeReplaceInventory);
            _inner.ReplaceOwnedInventoryFromTemp(ownedArtifact, tempArtifact, operationId);
            _armer.FireIf(ProvisioningFailPoint.AfterReplaceInventory);
        }
    }

    public void RemoveOwned(ProvisioningArtifact artifact)
    {
        switch (artifact)
        {
            case ProvisioningArtifact.Inventory:
                _armer.FireIf(ProvisioningFailPoint.RemoveInventory);
                break;
            case ProvisioningArtifact.PreviousInventory:
                _armer.FireIf(ProvisioningFailPoint.RemovePreviousInventory);
                break;
            case ProvisioningArtifact.Ledger:
                _armer.FireIf(ProvisioningFailPoint.RemoveLedger);
                break;
        }
        _inner.RemoveOwned(artifact);
    }

    public void RemoveInventoryTemp(ProvisioningArtifact tempArtifact, string operationId)
        => _inner.RemoveInventoryTemp(tempArtifact, operationId);

    public bool RemoveManagedDirectoryIfEmpty()
    {
        _armer.FireIf(ProvisioningFailPoint.RemoveManagedDirectory);
        return _inner.RemoveManagedDirectoryIfEmpty();
    }

    public OwnershipLedger? ReadLedger() => _inner.ReadLedger();

    public void StageLedgerTemp(string operationId, OwnershipLedger ledger)
    {
        _armer.FireIf(ProvisioningFailPoint.StageLedger);
        _inner.StageLedgerTemp(operationId, ledger);
    }

    public void ReplaceLedgerFromTemp(string operationId)
    {
        _armer.FireIf(ProvisioningFailPoint.BeforeReplaceLedger);
        _inner.ReplaceLedgerFromTemp(operationId);
        _armer.FireIf(ProvisioningFailPoint.AfterReplaceLedger);
    }

    public void RemoveLedgerTemp(string operationId) => _inner.RemoveLedgerTemp(operationId);

    public ProvisioningTransactionState ReadTransactionState() => _inner.ReadTransactionState();

    public void WriteTransactionState(ProvisioningTransactionState state, string operationId)
        => _inner.WriteTransactionState(state, operationId);

    public void RemoveTransactionArtifact()
    {
        _armer.FireIf(ProvisioningFailPoint.BeforeTransactionClear);
        _inner.RemoveTransactionArtifact();
    }
}

// The sandbox ACL the executor observes. Its Inspect returns a configurable
// descriptor (default = the EXACT trusted production descriptor) so the executor's
// trust gate is satisfied deterministically under NON-elevation (the real
// owner-set to SYSTEM cannot succeed without elevation, which is forbidden). Its
// Apply is a no-op over the test-safe managed directory (so the test is never
// locked out) and honors the ApplyAcl fault point. The REAL protected-DACL apply
// is proven separately by the sandbox test on a throwaway probe directory.
internal sealed class SandboxProvisioningAcl : IProvisioningAcl
{
    private readonly SandboxFaultArmer _armer;
    private AclDescriptor _descriptor = ProvisioningAclBuilder.BuildManagedDirectoryDescriptor();

    public SandboxProvisioningAcl(SandboxFaultArmer armer) => _armer = armer;

    // Force the observed trust state to UNTRUSTED (broad write, unprotected) so the
    // S6 fail-closed matrix can prove an untrusted environment blocks mutation.
    public void MakeUntrusted()
        => _descriptor = new AclDescriptor(
            AclDescriptor.SidEveryone,
            new[] { new AclEntry(AclDescriptor.SidEveryone, AclAccess.Write) },
            protectedDacl: false);

    public AclDescriptor Inspect(ProvisioningArtifact artifact) => _descriptor;

    public void Apply(AclDescriptor descriptor, ProvisioningArtifact artifact)
        => _armer.FireIf(ProvisioningFailPoint.ApplyAcl);
}
#endif
