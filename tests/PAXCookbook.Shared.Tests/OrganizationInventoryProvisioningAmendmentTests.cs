// Cycle-08 amendment RED test.
// This test references the previous-generation ("prev") owned artifact that
// the Cycle-7 contract does NOT yet model. It must FAIL (compile error, then
// assertion) until the amendment adds EXACTLY ONE owned previous-generation
// artifact so rollback bytes are recoverable.
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

public sealed class OrganizationInventoryProvisioningAmendmentTests
{
    [Fact]
    public void A01_PreviousGenerationArtifact_IsOwned_SoRollbackBytesAreRecoverable()
    {
        // (a) the prev-generation file name must be a first-class contract constant
        Assert.Equal("organization-key-inventory.prev.json",
            ProvisioningContract.PreviousInventoryFileName);

        // (b) EXACTLY three owned files: inventory, ledger, prev-inventory
        Assert.Equal(3, ProvisioningContract.OwnedFileNames.Count);
        Assert.Contains(ProvisioningContract.PreviousInventoryFileName,
            ProvisioningContract.OwnedFileNames);

        // (c) the planner must have a symbolic artifact for the previous generation
        Assert.True(System.Enum.IsDefined(typeof(ProvisioningArtifact),
            ProvisioningArtifact.PreviousInventory));
    }
}
