using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3h -- production ICredentialSecretStore. Direct port of
// Auth\CredentialManager.ps1 Set-AuthProfileSecret /
// Test-AuthProfileSecretPresent / Remove-AuthProfileSecret via
// advapi32!CredWriteW / CredReadW / CredDeleteW / CredFree.
//
// Doctrine (matches PS impl verbatim):
//   * Target name: PAXCookbook.AuthProfile.<authProfileId>.ClientSecret
//   * Type        = CRED_TYPE_GENERIC (1)
//   * Persist     = CRED_PERSIST_LOCAL_MACHINE (2)
//   * Blob        = UTF-16 LE bytes of the secret string. NOT
//                   NUL-terminated. CredentialBlobSize is the byte
//                   length (string.Length * 2).
//   * Memory: secret bytes are zeroed in a finally block after
//             CredWrite returns, even on failure paths.
//   * authProfileId is validated against the UUID regex before
//             composing the target name. Invalid id -> ArgumentException.
//
// SECURITY:
//   * The secret string is held in a managed string for the duration
//     of the write. There is no fully-secure path in .NET to avoid a
//     managed copy short of using SecureString end-to-end, which the
//     PS broker also does not do (it marshals SecureString -> BSTR).
//     The zero-on-exit pattern matches the PS broker's
//     ZeroFreeBSTR + FreeHGlobal contract for the duration the
//     bytes are pinned.
public sealed class WindowsCredentialSecretStore : ICredentialSecretStore
{
    private const string TargetPrefix = "PAXCookbook.AuthProfile.";
    private const string TargetSuffix = ".ClientSecret";

    private const uint CRED_TYPE_GENERIC          = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    private const int ERROR_NOT_FOUND = 1168;

    private static readonly Regex UuidPattern = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    public string ComposeTarget(string authProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authProfileId);
        if (!UuidPattern.IsMatch(authProfileId))
        {
            throw new ArgumentException(
                "authProfileId is not a canonical UUID.",
                nameof(authProfileId));
        }
        return TargetPrefix + authProfileId + TargetSuffix;
    }

    public void Write(string authProfileId, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length == 0)
        {
            throw new ArgumentException(
                "Secret must not be empty.", nameof(secret));
        }
        var target = ComposeTarget(authProfileId);

        // UTF-16 LE encoding matches the PS broker's Marshal.SecureString
        // BSTR projection (UTF-16 LE on Windows). Byte length is the
        // string length * 2; NO NUL terminator (the API takes the
        // explicit byte count).
        var byteCount = checked(secret.Length * 2);
        var blob = Marshal.AllocHGlobal(byteCount);
        try
        {
            Marshal.Copy(StringToUtf16LeBytes(secret), 0, blob, byteCount);

            var cred = new CREDENTIAL
            {
                Flags              = 0,
                Type               = CRED_TYPE_GENERIC,
                TargetName         = target,
                Comment            = null,
                LastWritten        = default,
                CredentialBlobSize = (uint)byteCount,
                CredentialBlob     = blob,
                Persist            = CRED_PERSIST_LOCAL_MACHINE,
                AttributeCount     = 0,
                Attributes         = IntPtr.Zero,
                TargetAlias        = null,
                UserName           = string.Empty,
            };

            if (!CredWriteW(ref cred, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "CredWriteW failed for target " + target);
            }
        }
        finally
        {
            // Zero the secret bytes before releasing, parity with the
            // PS broker's ZeroFreeBSTR pattern.
            ZeroNativeBuffer(blob, byteCount);
            Marshal.FreeHGlobal(blob);
        }
    }

    public bool Exists(string authProfileId)
    {
        var target = ComposeTarget(authProfileId);
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return false;
            throw new Win32Exception(err,
                "CredReadW failed for target " + target);
        }
        CredFree(credPtr);
        return true;
    }

    public void Delete(string authProfileId)
    {
        var target = ComposeTarget(authProfileId);
        if (!CredDeleteW(target, CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
            {
                // Best-effort -- absent is success, parity with
                // Remove-AuthProfileSecret in the PS broker.
                return;
            }
            throw new Win32Exception(err,
                "CredDeleteW failed for target " + target);
        }
    }

    private static byte[] StringToUtf16LeBytes(string s)
    {
        var bytes = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            bytes[2 * i]     = (byte)(c & 0xFF);
            bytes[2 * i + 1] = (byte)((c >> 8) & 0xFF);
        }
        return bytes;
    }

    private static void ZeroNativeBuffer(IntPtr buffer, int byteCount)
    {
        if (buffer == IntPtr.Zero || byteCount <= 0) return;
        for (int i = 0; i < byteCount; i++)
        {
            Marshal.WriteByte(buffer, i, 0);
        }
    }

    // ============================================================
    //  advapi32 P/Invoke -- mirrors the PS broker exactly.
    // ============================================================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint    Flags;
        public uint    Type;
        public string  TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint    CredentialBlobSize;
        public IntPtr  CredentialBlob;
        public uint    Persist;
        public uint    AttributeCount;
        public IntPtr  Attributes;
        public string? TargetAlias;
        public string  UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL cred, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(
        string targetName, uint type, uint flags, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(
        string targetName, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
