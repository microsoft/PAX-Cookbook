using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PAXCookbookSetup.Gui;

internal static class SetupProviderHelper
{
    private const string ResourceName = "PAXCookbookSetup.Entra.New-PaxCookbookEntraWamSetup.ps1";
    private const string HelperName = "New-PaxCookbookEntraWamSetup.ps1";
    private const FileSystemRights WriteRights = FileSystemRights.WriteData
        | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership | (FileSystemRights)0x50000000;

    internal static string ResolveHelperPath()
    {
        try
        {
            byte[] resource = ReadResource();
            SecurityIdentifier user = CurrentUser();
            string path = PreparePath(resource, user, create: true);
            if (!File.Exists(path))
            {
                string staging = Path.Combine(Path.GetDirectoryName(path)!, Path.GetRandomFileName());
                bool created = false;
                try
                {
                    using (FileStream output = new FileInfo(staging).Create(
                        FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write,
                        FileShare.None, 4096, FileOptions.WriteThrough, CreateFileSecurity(user)))
                    {
                        created = true;
                        output.Write(resource);
                        output.Flush(flushToDisk: true);
                        VerifyFile(output, resource, user);
                    }
                    File.Move(staging, path, overwrite: false);
                }
                finally
                {
                    if (created && File.Exists(staging)) { File.Delete(staging); }
                }
            }
            using FileStream lease = OpenVerified(path, resource, user);
            return path;
        }
        catch
        {
            throw new IOException("Setup helper is unavailable.");
        }
    }

    internal static FileStream AcquireLease(string path)
    {
        try
        {
            byte[] resource = ReadResource();
            SecurityIdentifier user = CurrentUser();
            string expected = PreparePath(resource, user, create: false);
            if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Setup helper path is invalid.");
            }
            return OpenVerified(path, resource, user);
        }
        catch
        {
            throw new IOException("Setup helper is unavailable.");
        }
    }

    private static byte[] ReadResource()
    {
        using Stream resource = typeof(SetupProviderHelper).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new IOException("Setup helper resource is missing.");
        if (resource.Length <= 0 || resource.Length > 1024 * 1024)
        {
            throw new IOException("Setup helper resource size is invalid.");
        }
        byte[] bytes = new byte[checked((int)resource.Length)];
        resource.ReadExactly(bytes);
        if (resource.ReadByte() != -1) { throw new IOException("Setup helper resource changed."); }
        return bytes;
    }

    private static SecurityIdentifier CurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new IOException("Setup helper user is unavailable.");
    }

    private static string PreparePath(byte[] resource, SecurityIdentifier user, bool create)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(local) || Path.GetPathRoot(local)?.Length != 3)
        {
            throw new IOException("Setup helper root is invalid.");
        }
        for (DirectoryInfo? ancestor = new(local); ancestor is not null; ancestor = ancestor.Parent)
        {
            RequireOrdinary(ancestor, directory: true);
        }
        string directory = local;
        foreach (string component in new[] { "PAXCookbookSetup", "EntraHelper", Convert.ToHexString(SHA256.HashData(resource)) })
        {
            directory = Path.Combine(directory, component);
            var info = new DirectoryInfo(directory);
            if (create && !info.Exists) { info.Create(CreateDirectorySecurity(user)); }
            RequireOrdinary(info, directory: true);
            VerifySecurity(info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), user);
        }
        return Path.Combine(directory, HelperName);
    }

    private static void RequireOrdinary(FileSystemInfo info, bool directory)
    {
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null
            || (info.Attributes & FileAttributes.ReparsePoint) != 0
            || ((info.Attributes & FileAttributes.Directory) != 0) != directory)
        {
            throw new IOException("Setup helper filesystem entry is invalid.");
        }
    }

    private static FileStream OpenVerified(string path, byte[] resource, SecurityIdentifier user)
    {
        RequireOrdinary(new FileInfo(path), directory: false);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            VerifyFile(stream, resource, user);
            PreparePath(resource, user, create: false);
            RequireOrdinary(new FileInfo(path), directory: false);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void VerifyFile(FileStream stream, byte[] resource, SecurityIdentifier user)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out FileInformation information)
            || information.NumberOfLinks != 1
            || (information.FileAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new IOException("Setup helper file identity is invalid.");
        }
        VerifySecurity(stream.GetAccessControl(), user);
        if (stream.Length != resource.LongLength) { throw new IOException("Setup helper length is invalid."); }
        stream.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), SHA256.HashData(resource)))
        {
            throw new IOException("Setup helper content is invalid.");
        }
        stream.Position = 0;
    }

    private static SecurityIdentifier[] TrustedUsers(SecurityIdentifier user) => new[]
    {
        user,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
    };

    private static DirectorySecurity CreateDirectorySecurity(SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier identity in TrustedUsers(user))
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    private static FileSecurity CreateFileSecurity(SecurityIdentifier user)
    {
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier identity in TrustedUsers(user))
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    private static void VerifySecurity(FileSystemSecurity security, SecurityIdentifier user)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier)))
            || !security.AreAccessRulesProtected || descriptor.DiscretionaryAcl is null
            || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0)
        {
            throw new IOException("Setup helper access policy is invalid.");
        }
        SecurityIdentifier[] trusted = TrustedUsers(user);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & WriteRights) != 0
                && !Array.Exists(trusted, identity => identity.Equals(rule.IdentityReference)))
            {
                throw new IOException("Setup helper grants untrusted write access.");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}