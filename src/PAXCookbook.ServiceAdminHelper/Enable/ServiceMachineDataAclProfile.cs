// PAX Cookbook - MACHINE DATA ACCESS-CONTROL PROFILES (cycle 63R, helper only)
//
// WHAT THIS FILE IS. The ONE description of the three access-control profiles
// that separate the service's writable RUNTIME namespace from the METADATA
// namespace holding ownership records, plus the narrow adapter that applies and
// verifies them. Every identity is a FIXED well-known SID or the service's own
// resolved SID; no account name, no group name and no caller-supplied principal
// is ever accepted.
//
// THE THREE PROFILES, and why each is shaped the way it is.
//
//   METADATA DIRECTORY  (%ProgramData%\PAXCookbook\Service)
//     owner            Builtin Administrators (S-1-5-32-544)
//     protected DACL, no inherited ACE
//     LocalSystem            FullControl   (ContainerInherit|ObjectInherit)
//     Builtin Administrators FullControl   (ContainerInherit|ObjectInherit)
//     the fixed service SID  Traverse ONLY, NON-INHERITING (plus the SYNCHRONIZE
//                            bit Windows adds to every non-full allow ACE)
//     and NO other allow ACE.
//   The service must be able to PASS THROUGH this directory to reach its own
//   runtime child, and nothing more. Traverse grants no read of the directory
//   listing, no create, no write and no delete. The ACE is deliberately
//   non-inheriting so it can never reach the anchor or the ownership ledger.
//
//   INSTALLATION ANCHOR FILE (installation-anchor.json)
//     owner            Builtin Administrators
//     protected DACL, no inherited ACE
//     LocalSystem            FullControl
//     Builtin Administrators FullControl
//     and NO service ACE and NO ordinary-user ACE of any kind.
//   The anchor records WHO owns the installation. A service that could read or
//   rewrite it could rewrite the proof of its own ownership.
//
//   RUNTIME DIRECTORY   (<metadata>\Runtime)
//     owner            Builtin Administrators
//     protected DACL, no inherited ACE
//     LocalSystem            FullControl   (ContainerInherit|ObjectInherit)
//     Builtin Administrators FullControl   (ContainerInherit|ObjectInherit)
//     the fixed service SID  Modify + Synchronize (ContainerInherit|ObjectInherit)
//     and NO other allow ACE.
//   This is the ONLY machine location the service account may write.
//
// AN EXISTING OBJECT WITH THE WRONG OWNER OR DACL IS REFUSED, NEVER SILENTLY
// REPAIRED. Rewriting a security descriptor this product did not place would
// destroy the very evidence that says the state is not ours.
//
// WHAT THIS FILE CANNOT DO, by construction. It starts no process, elevates
// nothing, opens no certificate store, key or credential vault, reads or writes
// no registry key, performs no service control, opens no socket, touches no PAX
// and starts no Bake.
//
// PRIVACY - FAIL CLOSED. Every answer is a bool or a bounded token. Nothing here
// returns a path, an account name, a security descriptor or an exception.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// One expected allow ACE: identity, exact rights, exact inheritance. Inheritance
/// is part of the expectation because a Traverse grant that INHERITED into the
/// metadata directory's children would silently reach the ownership records.
/// </summary>
internal readonly struct ServiceMachineDataAce
{
    internal ServiceMachineDataAce(string sid, FileSystemRights rights, InheritanceFlags inheritance)
    {
        Sid = sid;
        Rights = rights;
        Inheritance = inheritance;
    }

    internal string Sid { get; }

    internal FileSystemRights Rights { get; }

    internal InheritanceFlags Inheritance { get; }

    /// <summary>Carries the bounded type name only - never a SID.</summary>
    public override string ToString() => nameof(ServiceMachineDataAce);
}

/// <summary>
/// The three fixed profiles. Compile-time constants and pure builders only.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceMachineDataAclProfile
{
    /// <summary>
    /// The rights the service holds on the METADATA directory: pass through, and
    /// nothing else.
    ///
    /// SYNCHRONIZE IS PART OF THE EXPECTATION BECAUSE WINDOWS PUTS IT THERE.
    /// <see cref="FileSystemAccessRule"/> adds SYNCHRONIZE to every non-full
    /// allow ACE it constructs, so an expectation of bare Traverse could never
    /// match a descriptor this file's own builder produced - the verification
    /// would fail on every real machine. SYNCHRONIZE grants the right to wait on
    /// a handle; it grants no read of the directory listing, no create, no write
    /// and no delete. The same convention already governs
    /// <see cref="ServiceEnableAclProfile.ServiceRights"/> and
    /// <see cref="RuntimeServiceRights"/>.
    /// </summary>
    internal const FileSystemRights MetadataTraverseRights =
        FileSystemRights.Traverse | FileSystemRights.Synchronize;

    /// <summary>The rights the service holds on the RUNTIME directory: write its own state.</summary>
    internal const FileSystemRights RuntimeServiceRights =
        FileSystemRights.Modify | FileSystemRights.Synchronize;

    private const InheritanceFlags FullInheritance =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    private static SecurityIdentifier LocalSystem => ServiceEnableAclProfile.LocalSystem;

    private static SecurityIdentifier BuiltinAdministrators => ServiceEnableAclProfile.BuiltinAdministrators;

    /// <summary>The expected METADATA directory ACE set.</summary>
    internal static IReadOnlyList<ServiceMachineDataAce> ExpectedMetadataAces(string serviceSid) => new[]
    {
        new ServiceMachineDataAce(
            ServiceEnableAclProfile.LocalSystemSid,
            ServiceEnableAclProfile.AdministrativeRights,
            FullInheritance),
        new ServiceMachineDataAce(
            ServiceEnableAclProfile.BuiltinAdministratorsSid,
            ServiceEnableAclProfile.AdministrativeRights,
            FullInheritance),

        // NON-INHERITING on purpose: pass through this directory only.
        new ServiceMachineDataAce(serviceSid, MetadataTraverseRights, InheritanceFlags.None),
    };

    /// <summary>The expected ANCHOR file ACE set. There is no service ACE at all.</summary>
    internal static IReadOnlyList<ServiceMachineDataAce> ExpectedAnchorAces() => new[]
    {
        new ServiceMachineDataAce(
            ServiceEnableAclProfile.LocalSystemSid,
            ServiceEnableAclProfile.AdministrativeRights,
            InheritanceFlags.None),
        new ServiceMachineDataAce(
            ServiceEnableAclProfile.BuiltinAdministratorsSid,
            ServiceEnableAclProfile.AdministrativeRights,
            InheritanceFlags.None),
    };

    /// <summary>The expected RUNTIME directory ACE set.</summary>
    internal static IReadOnlyList<ServiceMachineDataAce> ExpectedRuntimeAces(string serviceSid) => new[]
    {
        new ServiceMachineDataAce(
            ServiceEnableAclProfile.LocalSystemSid,
            ServiceEnableAclProfile.AdministrativeRights,
            FullInheritance),
        new ServiceMachineDataAce(
            ServiceEnableAclProfile.BuiltinAdministratorsSid,
            ServiceEnableAclProfile.AdministrativeRights,
            FullInheritance),
        new ServiceMachineDataAce(serviceSid, RuntimeServiceRights, FullInheritance),
    };

    internal static DirectorySecurity BuildMetadataDirectorySecurity(SecurityIdentifier serviceSid) =>
        BuildDirectory(ExpectedMetadataAces(serviceSid.Value), serviceSid);

    internal static DirectorySecurity BuildRuntimeDirectorySecurity(SecurityIdentifier serviceSid) =>
        BuildDirectory(ExpectedRuntimeAces(serviceSid.Value), serviceSid);

    internal static FileSecurity BuildAnchorFileSecurity()
    {
        var security = new FileSecurity();
        security.SetOwner(BuiltinAdministrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            LocalSystem,
            ServiceEnableAclProfile.AdministrativeRights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            BuiltinAdministrators,
            ServiceEnableAclProfile.AdministrativeRights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    private static DirectorySecurity BuildDirectory(
        IReadOnlyList<ServiceMachineDataAce> expected, SecurityIdentifier serviceSid)
    {
        var security = new DirectorySecurity();
        security.SetOwner(BuiltinAdministrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (ServiceMachineDataAce ace in expected)
        {
            SecurityIdentifier identity =
                string.Equals(ace.Sid, serviceSid.Value, StringComparison.Ordinal)
                    ? serviceSid
                    : new SecurityIdentifier(ace.Sid);

            security.AddAccessRule(new FileSystemAccessRule(
                identity, ace.Rights, ace.Inheritance, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// THE EXACT verification predicate for all three profiles. It requires the
    /// exact owner, a protected DACL with no inherited ACE, exactly the expected
    /// allow ACEs INCLUDING their inheritance flags, and NO other ACE of any
    /// kind - so one extra grant, one deny ACE, one inherited ACE or one widened
    /// inheritance flag fails it.
    /// </summary>
    internal static bool MatchesProfile(
        CommonObjectSecurity? security, IReadOnlyList<ServiceMachineDataAce> expected)
    {
        if (security is null || expected is null || expected.Count == 0)
        {
            return false;
        }

        try
        {
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
                || !string.Equals(
                    owner.Value, ServiceEnableAclProfile.BuiltinAdministratorsSid, StringComparison.Ordinal))
            {
                return false;
            }

            AuthorizationRuleCollection rules =
                security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

            var remaining = new List<ServiceMachineDataAce>(expected);
            int seen = 0;

            foreach (AuthorizationRule rule in rules)
            {
                if (rule is not FileSystemAccessRule access
                    || access.IsInherited
                    || access.AccessControlType != AccessControlType.Allow
                    || access.IdentityReference is not SecurityIdentifier identity)
                {
                    return false;
                }

                seen++;
                int index = -1;
                for (int i = 0; i < remaining.Count; i++)
                {
                    if (string.Equals(remaining[i].Sid, identity.Value, StringComparison.Ordinal)
                        && remaining[i].Rights == access.FileSystemRights
                        && remaining[i].Inheritance == access.InheritanceFlags)
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    return false;
                }

                remaining.RemoveAt(index);
            }

            return remaining.Count == 0 && seen == expected.Count;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// The narrow machine-data access-control adapter. An interface ONLY so the
/// focused tests can prove the ordering and the exact expected profiles without
/// writing the real machine. Production always uses
/// <see cref="WindowsServiceMachineDataSecurityAdapter"/>.
/// </summary>
internal interface IServiceMachineDataSecurityAdapter
{
    /// <summary>Applies the METADATA directory profile.</summary>
    bool TryApplyMetadataProtection(string directoryPath, string serviceSid);

    /// <summary>Applies the ANCHOR file profile.</summary>
    bool TryApplyAnchorProtection(string filePath);

    /// <summary>Applies the RUNTIME directory profile.</summary>
    bool TryApplyRuntimeProtection(string directoryPath, string serviceSid);

    bool VerifyMetadataProtection(string directoryPath, string serviceSid);

    bool VerifyAnchorProtection(string filePath);

    bool VerifyRuntimeProtection(string directoryPath, string serviceSid);
}

/// <summary>The real Windows adapter. Access-control work and nothing else.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceMachineDataSecurityAdapter : IServiceMachineDataSecurityAdapter
{
    public bool TryApplyMetadataProtection(string directoryPath, string serviceSid)
    {
        try
        {
            new DirectoryInfo(directoryPath).SetAccessControl(
                ServiceMachineDataAclProfile.BuildMetadataDirectorySecurity(new SecurityIdentifier(serviceSid)));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryApplyAnchorProtection(string filePath)
    {
        try
        {
            new FileInfo(filePath).SetAccessControl(ServiceMachineDataAclProfile.BuildAnchorFileSecurity());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryApplyRuntimeProtection(string directoryPath, string serviceSid)
    {
        try
        {
            new DirectoryInfo(directoryPath).SetAccessControl(
                ServiceMachineDataAclProfile.BuildRuntimeDirectorySecurity(new SecurityIdentifier(serviceSid)));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool VerifyMetadataProtection(string directoryPath, string serviceSid) =>
        VerifyDirectory(directoryPath, ServiceMachineDataAclProfile.ExpectedMetadataAces(serviceSid));

    public bool VerifyRuntimeProtection(string directoryPath, string serviceSid) =>
        VerifyDirectory(directoryPath, ServiceMachineDataAclProfile.ExpectedRuntimeAces(serviceSid));

    public bool VerifyAnchorProtection(string filePath)
    {
        try
        {
            FileSecurity security = new FileInfo(filePath)
                .GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return ServiceMachineDataAclProfile.MatchesProfile(
                security, ServiceMachineDataAclProfile.ExpectedAnchorAces());
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool VerifyDirectory(string directoryPath, IReadOnlyList<ServiceMachineDataAce> expected)
    {
        try
        {
            DirectorySecurity security = new DirectoryInfo(directoryPath)
                .GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return ServiceMachineDataAclProfile.MatchesProfile(security, expected);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
