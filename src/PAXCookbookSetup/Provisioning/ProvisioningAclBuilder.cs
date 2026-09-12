#if MANAGED_INVENTORY_PROVISIONING
using System.Collections.Generic;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provisioning;

// Builds the EXACT trusted ACL descriptor the future elevated helper would stamp
// on the managed directory and every owned/staged artifact. The owner + the only
// write-capable principals are LocalSystem (S-1-5-18) and BuiltinAdministrators
// (S-1-5-32-544) — identical to the Cycle-6 reader's closed-SID trust policy.
// Broad principals (Everyone/Authenticated Users/Users/Interactive) receive at
// most READ, never write, and the DACL is protected (non-inheriting) so no broad
// inherited write ACE can leak in. The descriptor is validated by the contract's
// AclDescriptorValidator before it is ever applied.
internal static class ProvisioningAclBuilder
{
    // The trusted owner used for every owned/staged artifact.
    public const string OwnerSid = AclDescriptor.SidLocalSystem;

    public static AclDescriptor BuildManagedDirectoryDescriptor()
    {
        // LocalSystem + Administrators: full (read+write). Users: read only. The
        // read grant to Users mirrors the desktop-read intent of the reader policy
        // (the app reads the inventory as a normal user) without granting write.
        var entries = new List<AclEntry>
        {
            new(AclDescriptor.SidLocalSystem, AclAccess.Write),
            new(AclDescriptor.SidLocalSystem, AclAccess.Read),
            new(AclDescriptor.SidBuiltinAdministrators, AclAccess.Write),
            new(AclDescriptor.SidBuiltinAdministrators, AclAccess.Read),
            new(AclDescriptor.SidUsers, AclAccess.Read),
        };
        return new AclDescriptor(OwnerSid, entries, protectedDacl: true);
    }

    // Owned/staged file artifacts carry the same trusted DACL as the directory.
    public static AclDescriptor BuildArtifactDescriptor()
        => BuildManagedDirectoryDescriptor();

    // Convenience guard: the descriptor this builder produces must always validate.
    public static bool ProducesValidDescriptor()
        => AclDescriptorValidator.Validate(BuildManagedDirectoryDescriptor()).IsValid;
}
#endif
