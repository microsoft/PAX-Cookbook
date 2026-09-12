// PAX Cookbook - SERVICE-ENABLE ACCESS-CONTROL PROFILE (cycle 62, helper only)
//
// WHAT THIS FILE IS. The ONE description of the access control the installed
// service payload must carry, plus the narrow adapter that applies and verifies
// it. Every identity here is a FIXED well-known SID or the service's own SID;
// no account name, no caller-supplied principal and no group name is ever
// accepted.
//
// THE TWO PROFILES.
//
//   STAGING (before the service exists, so no service SID exists yet):
//     owner            Builtin Administrators (S-1-5-32-544)
//     protected DACL, no inherited ACE
//     LocalSystem            (S-1-5-18)   FullControl
//     Builtin Administrators (S-1-5-32-544) FullControl
//     and NO other allow ACE.
//
//   FINAL (after the service is created and its SID is resolvable):
//     owner            Builtin Administrators (S-1-5-32-544)
//     protected DACL, no inherited ACE
//     LocalSystem            FullControl
//     Builtin Administrators FullControl
//     the fixed service SID  ReadAndExecute + Synchronize
//     and NO other allow ACE.
//
// The service is given READ AND EXECUTE ONLY. It must be able to load and run
// what it was installed with; it must NOT be able to rewrite its own binaries,
// because that would let a compromised service make its compromise permanent.
//
// WHAT THIS FILE CANNOT DO, by construction. It starts no process, elevates
// nothing, opens no certificate store, key or credential vault, reads or writes
// no registry key, performs no service control, opens no socket, touches no PAX
// and starts no Bake. It reads and writes access control on paths it is given
// by the fixed-path resolver, and nothing else.
//
// PRIVACY - FAIL CLOSED. Nothing here returns a path, an account name, a
// security descriptor or an exception. Every answer is a bool or a bounded
// token.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// The fixed well-known identities and rights of the two profiles. Compile-time
/// constants and pure builders only.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceEnableAclProfile
{
    /// <summary>NT AUTHORITY\SYSTEM.</summary>
    internal const string LocalSystemSid = "S-1-5-18";

    /// <summary>BUILTIN\Administrators - the required owner of every installed object.</summary>
    internal const string BuiltinAdministratorsSid = "S-1-5-32-544";

    /// <summary>The rights the service itself receives: run it, never rewrite it.</summary>
    internal const FileSystemRights ServiceRights =
        FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    /// <summary>The rights the two administrative identities receive.</summary>
    internal const FileSystemRights AdministrativeRights = FileSystemRights.FullControl;

    internal static SecurityIdentifier LocalSystem => new(LocalSystemSid);

    internal static SecurityIdentifier BuiltinAdministrators => new(BuiltinAdministratorsSid);

    /// <summary>
    /// The STAGING profile: administrative identities only. It is applied BEFORE
    /// any member is written, so no payload byte ever exists at a weaker
    /// protection level than the one it will finally carry.
    /// </summary>
    internal static DirectorySecurity BuildStagingDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(BuiltinAdministrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddDirectoryRule(security, LocalSystem, AdministrativeRights);
        AddDirectoryRule(security, BuiltinAdministrators, AdministrativeRights);
        return security;
    }

    /// <summary>The FINAL directory profile. Requires the now-existing service SID.</summary>
    internal static DirectorySecurity BuildFinalDirectorySecurity(SecurityIdentifier serviceSid)
    {
        var security = new DirectorySecurity();
        security.SetOwner(BuiltinAdministrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddDirectoryRule(security, LocalSystem, AdministrativeRights);
        AddDirectoryRule(security, BuiltinAdministrators, AdministrativeRights);
        AddDirectoryRule(security, serviceSid, ServiceRights);
        return security;
    }

    /// <summary>The FINAL file profile. Files carry no inheritance flags.</summary>
    internal static FileSecurity BuildFinalFileSecurity(SecurityIdentifier serviceSid)
    {
        var security = new FileSecurity();
        security.SetOwner(BuiltinAdministrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFileRule(security, LocalSystem, AdministrativeRights);
        AddFileRule(security, BuiltinAdministrators, AdministrativeRights);
        AddFileRule(security, serviceSid, ServiceRights);
        return security;
    }

    private static void AddDirectoryRule(
        DirectorySecurity security, SecurityIdentifier identity, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static void AddFileRule(
        FileSecurity security, SecurityIdentifier identity, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

    /// <summary>
    /// The EXACT verification predicate, shared by the staging and final
    /// profiles. It requires the exact owner, a protected DACL with no inherited
    /// ACE, exactly the expected allow ACEs, and NO other ACE of any kind - so a
    /// single extra grant, a deny ACE or an inherited ACE fails it.
    /// </summary>
    internal static bool MatchesProfile(
        CommonObjectSecurity? security, IReadOnlyList<KeyValuePair<string, FileSystemRights>> expected)
    {
        if (security is null || expected is null || expected.Count == 0)
        {
            return false;
        }

        try
        {
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
                || !string.Equals(owner.Value, BuiltinAdministratorsSid, StringComparison.Ordinal))
            {
                return false;
            }

            AuthorizationRuleCollection rules =
                security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

            var remaining = new List<KeyValuePair<string, FileSystemRights>>(expected);
            int seen = 0;
            foreach (AuthorizationRule rule in rules)
            {
                if (rule is not FileSystemAccessRule access)
                {
                    return false;
                }

                // An inherited ACE means the DACL is not protected, whatever the
                // protection flag claims.
                if (access.IsInherited || access.AccessControlType != AccessControlType.Allow)
                {
                    return false;
                }

                if (access.IdentityReference is not SecurityIdentifier identity)
                {
                    return false;
                }

                seen++;
                int index = -1;
                for (int i = 0; i < remaining.Count; i++)
                {
                    if (string.Equals(remaining[i].Key, identity.Value, StringComparison.Ordinal)
                        && remaining[i].Value == access.FileSystemRights)
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

    /// <summary>The expected staging ACE set: administrative identities only.</summary>
    internal static IReadOnlyList<KeyValuePair<string, FileSystemRights>> ExpectedStagingAces() => new[]
    {
        new KeyValuePair<string, FileSystemRights>(LocalSystemSid, AdministrativeRights),
        new KeyValuePair<string, FileSystemRights>(BuiltinAdministratorsSid, AdministrativeRights),
    };

    /// <summary>The expected final ACE set, including the now-existing service SID.</summary>
    internal static IReadOnlyList<KeyValuePair<string, FileSystemRights>> ExpectedFinalAces(string serviceSid) => new[]
    {
        new KeyValuePair<string, FileSystemRights>(LocalSystemSid, AdministrativeRights),
        new KeyValuePair<string, FileSystemRights>(BuiltinAdministratorsSid, AdministrativeRights),
        new KeyValuePair<string, FileSystemRights>(serviceSid, ServiceRights),
    };

    /// <summary>
    /// The runtime-host safety question, asked of an ALREADY-READ rule set: does
    /// any identity that is NOT one of the three trusted machine identities hold
    /// a write, append, delete, ownership or permission-change grant? A "yes" is
    /// a refusal, because a rewritable dotnet.exe means the service's own host
    /// can be replaced by a non-administrator.
    /// </summary>
    internal static bool HasUntrustedWriteGrant(AuthorizationRuleCollection? rules)
    {
        if (rules is null)
        {
            return true;
        }

        const FileSystemRights DangerousRights =
            FileSystemRights.WriteData
            | FileSystemRights.AppendData
            | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.TakeOwnership
            | FileSystemRights.ChangePermissions
            | FileSystemRights.WriteAttributes
            | FileSystemRights.WriteExtendedAttributes;

        // TrustedInstaller owns the machine-wide runtime on a normal Windows
        // install; it is a system identity, not an untrusted principal.
        const string TrustedInstallerSid =
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

        try
        {
            foreach (AuthorizationRule rule in rules)
            {
                if (rule is not FileSystemAccessRule access
                    || access.AccessControlType != AccessControlType.Allow
                    || access.IdentityReference is not SecurityIdentifier identity)
                {
                    continue;
                }

                if ((access.FileSystemRights & DangerousRights) == 0)
                {
                    continue;
                }

                if (string.Equals(identity.Value, LocalSystemSid, StringComparison.Ordinal)
                    || string.Equals(identity.Value, BuiltinAdministratorsSid, StringComparison.Ordinal)
                    || string.Equals(identity.Value, TrustedInstallerSid, StringComparison.Ordinal))
                {
                    continue;
                }

                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

/// <summary>
/// The narrow access-control adapter. An interface ONLY so the focused tests can
/// prove the ordering, the exact expected profiles and every compensation path
/// without requiring SeRestorePrivilege or writing the real machine. Production
/// always uses <see cref="WindowsServiceEnableSecurityAdapter"/>.
/// </summary>
internal interface IServiceEnableSecurityAdapter
{
    /// <summary>Applies the staging profile to a directory. Called BEFORE any member is written.</summary>
    bool TryApplyStagingProtection(string directoryPath);

    /// <summary>Applies the final profile to one directory or file.</summary>
    bool TryApplyFinalProtection(string path, bool isDirectory, string serviceSid);

    /// <summary>Verifies the exact final owner and DACL of one directory or file.</summary>
    bool VerifyFinalProtection(string path, bool isDirectory, string serviceSid);

    /// <summary>Verifies the exact staging owner and DACL of a directory.</summary>
    bool VerifyStagingProtection(string directoryPath);

    /// <summary>
    /// True when an identity outside the trusted machine set can write, append,
    /// delete, take ownership of or re-permission the fixed runtime host.
    /// </summary>
    bool RuntimeHostHasUntrustedWriteGrant(string filePath);
}

/// <summary>The real Windows adapter. Access-control work and nothing else.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceEnableSecurityAdapter : IServiceEnableSecurityAdapter
{
    public bool TryApplyStagingProtection(string directoryPath)
    {
        try
        {
            var info = new DirectoryInfo(directoryPath);
            info.SetAccessControl(ServiceEnableAclProfile.BuildStagingDirectorySecurity());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryApplyFinalProtection(string path, bool isDirectory, string serviceSid)
    {
        try
        {
            var identity = new SecurityIdentifier(serviceSid);
            if (isDirectory)
            {
                new DirectoryInfo(path).SetAccessControl(
                    ServiceEnableAclProfile.BuildFinalDirectorySecurity(identity));
            }
            else
            {
                new FileInfo(path).SetAccessControl(
                    ServiceEnableAclProfile.BuildFinalFileSecurity(identity));
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool VerifyFinalProtection(string path, bool isDirectory, string serviceSid)
    {
        try
        {
            CommonObjectSecurity security = isDirectory
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);

            return ServiceEnableAclProfile.MatchesProfile(
                security, ServiceEnableAclProfile.ExpectedFinalAces(serviceSid));
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool VerifyStagingProtection(string directoryPath)
    {
        try
        {
            DirectorySecurity security = new DirectoryInfo(directoryPath)
                .GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return ServiceEnableAclProfile.MatchesProfile(
                security, ServiceEnableAclProfile.ExpectedStagingAces());
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool RuntimeHostHasUntrustedWriteGrant(string filePath)
    {
        try
        {
            FileSecurity security = new FileInfo(filePath).GetAccessControl(AccessControlSections.Access);
            return ServiceEnableAclProfile.HasUntrustedWriteGrant(
                security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)));
        }
        catch (Exception)
        {
            // An unreadable descriptor is treated as untrusted. Fail closed.
            return true;
        }
    }
}
