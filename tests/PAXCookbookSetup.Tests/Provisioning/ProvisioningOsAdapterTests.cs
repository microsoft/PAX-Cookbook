#if MANAGED_INVENTORY_PROVISIONING
using System;
using System.IO;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Provisioning;
using Xunit;

namespace PAXCookbookSetup.Tests.Provisioning;

// Cycle-8 OS-adapter proof. Unlike the in-memory matrix, these tests drive the
// REAL OsProvisioningFileSystem / OsProvisioningAcl against an isolated OS-TEMP
// managed directory (NEVER the real ProgramData path) to prove genuine atomic
// replace, byte-exact hashing, ledger JSON round-trip, transaction-marker
// persistence, and a real protected DACL. Everything is created under a unique
// OS-temp root and removed in a finally. NO process launch, NO UAC, NO real
// ProgramData mutation.
public sealed class ProvisioningOsAdapterTests
{
    private static string ValidInventory(string id)
        => "{\"schemaVersion\":1,\"entries\":[{\"entryVersion\":1,\"organizationKeyId\":\""
           + id
           + "\",\"displayName\":\"Key\",\"certificateReferenceType\":\"app_registration_certificate\","
           + "\"adminState\":\"enabled\",\"tenantReference\":\"t\",\"clientReference\":\"c\"}]}";

    private static string NewTempRoot()
        => Path.Combine(Path.GetTempPath(), "paxc-prov-os-" + Guid.NewGuid().ToString("N"));

    [Fact] // O01 — Real staging + atomic replace + hashing + ledger round-trip on OS-temp.
    public void O01_OsFileSystem_AtomicReplace_HashAndLedgerRoundTrip()
    {
        string root = NewTempRoot();
        OsProvisioningFileSystem fs = OsProvisioningFileSystem.CreateForTest(root);
        try
        {
            Assert.False(fs.ManagedDirectoryExists());
            // Create the managed directory WITHOUT the production lockdown DACL.
            // EnsureManagedDirectory() stamps a protected DACL granting write only
            // to SYSTEM/Administrators; in production the elevated helper (running
            // as SYSTEM) performs all subsequent IO. Under an UNELEVATED test
            // principal that lockdown would deny the round-trip writes below. The
            // protected-DACL stamp itself is proven separately by O02; here we
            // exercise the REAL byte-exact file IO / atomic replace / ledger JSON
            // round-trip on an isolated OS-temp directory the test can write.
            Directory.CreateDirectory(root);
            Assert.True(fs.ManagedDirectoryExists());

            string doc = ValidInventory("org-key-1");
            fs.StageInventoryTemp(ProvisioningArtifact.TempInventory, "op-1", doc);
            fs.ReplaceOwnedInventoryFromTemp(ProvisioningArtifact.Inventory, ProvisioningArtifact.TempInventory, "op-1");

            Assert.True(fs.OwnedExists(ProvisioningArtifact.Inventory));
            Assert.Equal(doc, fs.ReadOwnedInventory(ProvisioningArtifact.Inventory));
            Assert.Equal(ProvisioningHash.Sha256Hex(doc), fs.HashOwnedInventory(ProvisioningArtifact.Inventory));

            OwnershipLedger ledger = new(
                ProvisioningContract.LedgerSchemaVersion,
                ProvisioningContract.ProductOwnershipMarker,
                ProvisioningContract.ManagedFeatureId,
                1, ProvisioningHash.Sha256Hex(doc), null, null,
                ProvisioningTransactionState.Done,
                ProvisioningContract.OwnedFileNames,
                ProvisioningContract.AclPolicyVersion,
                "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", "op-1");
            fs.StageLedgerTemp("op-1", ledger);
            fs.ReplaceLedgerFromTemp("op-1");

            OwnershipLedger? readBack = fs.ReadLedger();
            Assert.NotNull(readBack);
            Assert.Equal(1, readBack!.CurrentGeneration);
            Assert.Equal(ProvisioningHash.Sha256Hex(doc), readBack.CurrentInventorySha256);
            Assert.True(OwnershipLedgerValidator.Validate(readBack).IsValid);

            fs.WriteTransactionState(ProvisioningTransactionState.Preparing, "op-1");
            Assert.Equal(ProvisioningTransactionState.Preparing, fs.ReadTransactionState());
            fs.RemoveTransactionArtifact();
            Assert.Equal(ProvisioningTransactionState.Idle, fs.ReadTransactionState());

            fs.RemoveOwned(ProvisioningArtifact.Inventory);
            fs.RemoveOwned(ProvisioningArtifact.Ledger);
            Assert.True(fs.RemoveManagedDirectoryIfEmpty());
            Assert.False(fs.ManagedDirectoryExists());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact] // O02 — Real ACL apply stamps a PROTECTED DACL that inspects back as protected.
    public void O02_OsAcl_Apply_ProducesProtectedDacl()
    {
        string root = NewTempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var acl = new OsProvisioningAcl(root);

            acl.Apply(ProvisioningAclBuilder.BuildManagedDirectoryDescriptor(), ProvisioningArtifact.ManagedDirectory);

            AclDescriptor inspected = acl.Inspect(ProvisioningArtifact.ManagedDirectory);
            Assert.True(inspected.ProtectedDacl);
            Assert.NotEmpty(inspected.Entries);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
#endif
