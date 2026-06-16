using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PAXCookbook.App;

// Windows Credential Manager (WCM) access for Chef's Keys (CK-1).
//
// PAX Cookbook never stores credential material itself. Every Chef's Key lives
// in the Windows Credential Manager per-user vault and is reached only through
// this thin advapi32 layer. The storage convention is binding:
//
//   * Target name : PAXCookbook:ChefKey:<id>
//   * Type        : CRED_TYPE_GENERIC
//   * Persist     : CRED_PERSIST_LOCAL_MACHINE. Despite the name this is a
//                   per-USER vault entry that persists across the user's logon
//                   sessions on this machine. It is NOT an HKLM write, NOT a
//                   Windows service, NOT shared across users, and requires NO
//                   administrator rights. (CRED_PERSIST_ENTERPRISE would roam
//                   via the profile, which the local-first model forbids.)
//   * UserName    : JSON metadata { authType, displayName, tenantId?, clientId?,
//                                    certThumbprint?, upn? }
//   * Blob        : secret material (ClientSecret) for AppReg-Secret only;
//                   empty for the other three Chef's Key types.
//
// Constraint 14 posture: the read / enumerate surface returns metadata plus a
// HasSecret flag ONLY -- never the secret bytes. The single secret-bearing read
// (ReadSecretBytes) exists solely so an update can preserve an unchanged secret
// ("blank = keep"); its result is never serialized into a route response, DTO,
// log, or report. Native buffers holding secret bytes are zeroed before free.
internal static class WindowsCredentialStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    // Documented WCM field caps (wincred.h).
    internal const int MaxUserNameChars = 513;        // CRED_MAX_USERNAME_LENGTH
    internal const int MaxCredentialBlobBytes = 2560; // CRED_MAX_CREDENTIAL_BLOB_SIZE (5 * 512)

    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int reservedFlag);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string? filter, int flags, out int count, out IntPtr credentialsPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);

    // Read / enumerate projection: metadata + presence flag, NEVER the secret.
    internal sealed record CredentialRecord(string TargetName, string UserName, bool HasSecret);

    internal static bool Exists(string target) => Read(target) is not null;

    // Reads a single credential's metadata. Returns null when the target is
    // absent (ERROR_NOT_FOUND) or any read failure occurs. The secret blob is
    // inspected only for its length (HasSecret); its bytes are never copied out.
    internal static CredentialRecord? Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
        {
            return null;
        }

        try
        {
            CREDENTIAL cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            string userName = cred.UserName != IntPtr.Zero
                ? Marshal.PtrToStringUni(cred.UserName) ?? string.Empty
                : string.Empty;
            string targetName = cred.TargetName != IntPtr.Zero
                ? Marshal.PtrToStringUni(cred.TargetName) ?? target
                : target;
            return new CredentialRecord(targetName, userName, cred.CredentialBlobSize > 0);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    // Enumerates all credentials whose target matches the wildcard filter (for
    // Chef's Keys the filter is "PAXCookbook:ChefKey:*"). Returns metadata +
    // HasSecret for each; secret bytes are never copied out.
    internal static IReadOnlyList<CredentialRecord> Enumerate(string filter)
    {
        var list = new List<CredentialRecord>();
        if (!CredEnumerate(filter, 0, out int count, out IntPtr arrayPtr))
        {
            // ERROR_NOT_FOUND => no matching credentials => empty list.
            return list;
        }

        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr credPtr = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
                if (credPtr == IntPtr.Zero)
                {
                    continue;
                }

                CREDENTIAL cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                string userName = cred.UserName != IntPtr.Zero
                    ? Marshal.PtrToStringUni(cred.UserName) ?? string.Empty
                    : string.Empty;
                string targetName = cred.TargetName != IntPtr.Zero
                    ? Marshal.PtrToStringUni(cred.TargetName) ?? string.Empty
                    : string.Empty;
                list.Add(new CredentialRecord(targetName, userName, cred.CredentialBlobSize > 0));
            }
        }
        finally
        {
            CredFree(arrayPtr);
        }

        return list;
    }

    // Creates or replaces a credential. secret may be null/empty for the
    // no-secret types. The native secret copy is zeroed before it is freed; the
    // caller is responsible for clearing its own managed secret buffer.
    internal static void Write(string target, string userName, byte[]? secret)
    {
        IntPtr targetPtr = IntPtr.Zero;
        IntPtr userPtr = IntPtr.Zero;
        IntPtr blobPtr = IntPtr.Zero;
        int blobSize = 0;

        try
        {
            targetPtr = Marshal.StringToCoTaskMemUni(target);
            userPtr = Marshal.StringToCoTaskMemUni(userName);

            if (secret is { Length: > 0 })
            {
                blobSize = secret.Length;
                blobPtr = Marshal.AllocCoTaskMem(blobSize);
                Marshal.Copy(secret, 0, blobPtr, blobSize);
            }

            var cred = new CREDENTIAL
            {
                Flags = 0,
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                Comment = IntPtr.Zero,
                CredentialBlobSize = blobSize,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = IntPtr.Zero,
                UserName = userPtr,
            };

            if (!CredWrite(ref cred, 0))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"CredWrite failed for '{target}' (Win32 {err}).");
            }
        }
        finally
        {
            if (blobPtr != IntPtr.Zero)
            {
                for (int i = 0; i < blobSize; i++)
                {
                    Marshal.WriteByte(blobPtr, i, 0);
                }
                Marshal.FreeCoTaskMem(blobPtr);
            }
            if (targetPtr != IntPtr.Zero) { Marshal.FreeCoTaskMem(targetPtr); }
            if (userPtr != IntPtr.Zero) { Marshal.FreeCoTaskMem(userPtr); }
        }
    }

    // Deletes a credential. Returns false when the target did not exist
    // (ERROR_NOT_FOUND); throws on any other native failure.
    internal static bool Delete(string target)
    {
        if (CredDelete(target, CRED_TYPE_GENERIC, 0))
        {
            return true;
        }

        int err = Marshal.GetLastWin32Error();
        if (err == ERROR_NOT_FOUND)
        {
            return false;
        }
        throw new Win32Exception(err, $"CredDelete failed for '{target}' (Win32 {err}).");
    }

    // The ONLY secret-bearing read in CK-1. Used solely by the update
    // keep-existing branch to preserve an unchanged secret. Returns a managed
    // copy of the blob (or null when absent). The caller MUST zero the returned
    // bytes after use and MUST NEVER place them in a response, DTO, or log.
    internal static byte[]? ReadSecretBytes(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
        {
            return null;
        }

        try
        {
            CREDENTIAL cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize <= 0 || cred.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            byte[] buffer = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, buffer, 0, cred.CredentialBlobSize);
            return buffer;
        }
        finally
        {
            CredFree(credPtr);
        }
    }
}
