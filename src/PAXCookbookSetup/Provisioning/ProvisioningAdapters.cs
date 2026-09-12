#if MANAGED_INVENTORY_PROVISIONING
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provisioning;

// ---------------------------------------------------------------------------
// Cycle-8 gated provisioning executor abstractions (NON-LIVE this cycle).
//
// These narrow, internal, closed adapters are the ONLY seam through which the
// executor touches state. There is deliberately NO generic execute / shell /
// copy-anywhere / move-anywhere / delete-anywhere / set-arbitrary-ACL / registry
// / service / certificate surface. Every method names an EXACT symbolic owned or
// staged artifact; the adapter derives the real path internally (production) from
// Environment.GetFolderPath(CommonApplicationData) + fixed contract constants, or
// from an internal OS-temp test root (test seam). Paths never enter the executor.
// ---------------------------------------------------------------------------

// Bounded, content-free failure-injection points. Production adapters NEVER inject;
// only the in-memory / OS-temp TEST adapters honor a fail point so crash-recovery
// and partial-transaction behavior can be proven without a real crash.
internal enum ProvisioningFailPoint
{
    None,
    EnsureManagedDirectory,
    StageInventory,
    StagePreviousInventory,
    StageLedger,
    ApplyAcl,
    BeforeTransactionIntent,
    AfterTransactionIntent,
    BeforeReplacePreviousInventory,
    AfterReplacePreviousInventory,
    BeforeReplaceInventory,
    AfterReplaceInventory,
    BeforeReplaceLedger,
    AfterReplaceLedger,
    BeforeFinalVerify,
    BeforeTransactionClear,
    RemoveInventory,
    RemovePreviousInventory,
    RemoveLedger,
    RemoveTransactionArtifact,
    RemoveManagedDirectory,
}

// A bounded adapter fault. It carries ONLY the symbolic action kind that faulted —
// never a path, identifier, ACL/SID dump, inventory byte, or exception text — so a
// partial transaction can be modeled and recovered without leaking content.
internal sealed class ProvisioningAdapterFault : Exception
{
    public ProvisioningAdapterFault(ProvisioningActionKind step)
        : base(step.ToString())
        => Step = step;

    public ProvisioningActionKind Step { get; }
}

// Uppercase SHA-256 hex, byte-identical to the contract's private helper. Kept
// here so the executor never re-implements hashing inline.
internal static class ProvisioningHash
{
    public static string Sha256Hex(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var sb = new StringBuilder(64);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

// The narrow filesystem seam. Every operation targets an exact symbolic artifact.
// Owned artifacts are ONLY Inventory / PreviousInventory / Ledger; staged temps are
// ONLY TempInventory / TempPreviousInventory / TempLedger (per operationId). The
// managed directory + transaction marker are the sole additional owned entities.
internal interface IProvisioningFileSystem
{
    bool ManagedDirectoryExists();

    void EnsureManagedDirectory();

    // Whether an exact allow-listed owned artifact is present.
    bool OwnedExists(ProvisioningArtifact artifact);

    // Bounded UTF-8 read of an owned inventory / previous-inventory artifact.
    string ReadOwnedInventory(ProvisioningArtifact artifact);

    // SHA-256 hex of an owned inventory / previous-inventory artifact's bytes.
    string HashOwnedInventory(ProvisioningArtifact artifact);

    // Whether the managed directory holds a file that is not an allow-listed
    // owned artifact (an unknown sibling). Blocks directory removal + provisioning.
    bool HasUnknownSibling();

    // Stage bounded inventory content to the exact temp for this operation.
    void StageInventoryTemp(ProvisioningArtifact tempArtifact, string operationId, string content);

    string ReadInventoryTemp(ProvisioningArtifact tempArtifact, string operationId);

    string HashInventoryTemp(ProvisioningArtifact tempArtifact, string operationId);

    // Atomically replace an exact owned inventory / previous-inventory artifact
    // from its staged temp (same-volume replace/move).
    void ReplaceOwnedInventoryFromTemp(ProvisioningArtifact ownedArtifact, ProvisioningArtifact tempArtifact, string operationId);

    void RemoveOwned(ProvisioningArtifact artifact);

    void RemoveInventoryTemp(ProvisioningArtifact tempArtifact, string operationId);

    // Remove the managed directory ONLY when it is proven empty; returns false when
    // it is not empty (an unknown sibling or a residual artifact blocks removal).
    bool RemoveManagedDirectoryIfEmpty();

    // The parsed + validated owned ledger, or null when absent / unparseable /
    // invalid. The adapter never returns an unvalidated ledger.
    OwnershipLedger? ReadLedger();

    // Stage a fully-formed next-generation ledger to the ledger temp.
    void StageLedgerTemp(string operationId, OwnershipLedger ledger);

    // Atomically replace the owned ledger from its staged temp.
    void ReplaceLedgerFromTemp(string operationId);

    void RemoveLedgerTemp(string operationId);

    // Transaction marker: the crash-window record. Idle/Done are quiescent.
    ProvisioningTransactionState ReadTransactionState();

    void WriteTransactionState(ProvisioningTransactionState state, string operationId);

    void RemoveTransactionArtifact();
}

// The narrow ACL seam. It can ONLY inspect the DACL of an exact owned/staged
// artifact and apply the exact trusted descriptor. It cannot widen write, resolve
// a name, or touch a registry / service / certificate.
internal interface IProvisioningAcl
{
    AclDescriptor Inspect(ProvisioningArtifact artifact);

    void Apply(AclDescriptor descriptor, ProvisioningArtifact artifact);
}

// A bounded clock (RFC3339 UTC), injected so the executor is deterministic in tests.
internal interface IProvisioningClock
{
    string UtcNowIso();
}

internal sealed class SystemProvisioningClock : IProvisioningClock
{
    public string UtcNowIso()
        => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
#endif
