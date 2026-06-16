#requires -Version 7.4

# CredentialManager.ps1
#
# Phase AF -- Windows Credential Manager integration for auth-profile
# client secrets. This is the SINGLE entry point Cookbook uses to write
# or read client-secret material. The doctrine is:
#
#   - Secrets live ONLY in Windows Credential Manager, under the running
#     Windows user's credential vault. Cookbook NEVER stores secret
#     material in SQLite, in recipe JSON, in cook snapshots, in log
#     lines, in argv, in command.txt, in command-argv.json, or in any
#     other on-disk surface that the appliance owns.
#
#   - Cookbook NEVER invents its own vault, NEVER encrypts secrets with
#     its own keys, NEVER derives keys from Windows passwords, NEVER
#     uses DPAPI directly (the OS-level CredMan API already wraps DPAPI
#     for us under CRED_PERSIST_LOCAL_MACHINE / _ENTERPRISE; choosing
#     CRED_PERSIST_LOCAL_MACHINE binds the credential to this machine
#     and this user account, which is the correct local-appliance
#     scope).
#
#   - The READ path (Read-AuthProfileSecret) is internal-only and is
#     called ONLY by Cook/Supervisor.ps1 at the moment of spawning the
#     PAX child process. The secret is then placed in the child's
#     environment block as GRAPH_CLIENT_SECRET -- never in the argv,
#     never echoed, never logged. There is NO HTTP endpoint that
#     returns secret material; that is the "after-saved, never
#     revealable again" doctrine made unavoidable by absence.
#
#   - The WRITE path accepts the secret ONCE at the moment the operator
#     binds it, then the broker has no further memory of the plaintext.
#     The plaintext is wiped from the PSObject as soon as the P/Invoke
#     call returns. We use SecureString as the in-process carrier and
#     marshal to a NUL-terminated unmanaged buffer only briefly during
#     CredWrite; the buffer is zeroed and freed on every path including
#     exceptions.
#
# The Win32 surface used:
#   advapi32!CredWriteW(CREDENTIALW*, DWORD)         -- store / overwrite
#   advapi32!CredReadW (LPCWSTR, DWORD, DWORD, PCREDENTIAL*) -- fetch
#   advapi32!CredDeleteW(LPCWSTR, DWORD, DWORD)      -- delete
#   advapi32!CredFree   (PVOID)                      -- free CredRead buffer
#
# Target naming convention:
#   PAXCookbook.AuthProfile.<authProfileId>.ClientSecret
#
# The prefix is constant and operator-visible in `cmdkey /list` -- the
# doctrine here is transparency: an operator inspecting Windows
# Credential Manager should see exactly which Cookbook auth profiles
# have stored secrets and which do not. There is no obfuscation.

if (-not ('PAXCookbook.Native.CredentialManagerNative' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace PAXCookbook.Native {

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL {
        public uint   Flags;
        public uint   Type;
        public IntPtr TargetName;       // LPWSTR
        public IntPtr Comment;          // LPWSTR (optional)
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint   CredentialBlobSize;
        public IntPtr CredentialBlob;   // raw bytes (NOT NUL-terminated by API; we choose to store NUL-terminated UTF-16 LE; size is set accordingly)
        public uint   Persist;
        public uint   AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    public static class CredentialManagerNative {

        public const uint CRED_TYPE_GENERIC          = 1;
        public const uint CRED_PERSIST_LOCAL_MACHINE = 2;
        // CRED_PRESERVE_CREDENTIAL_BLOB not used; we always overwrite.

        // Win32 ERROR_* constants we explicitly recognise.
        public const int ERROR_NOT_FOUND       = 1168;
        public const int ERROR_NO_SUCH_LOGON_SESSION = 1312;
        public const int ERROR_INVALID_PARAMETER = 87;

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", SetLastError = false)]
        public static extern void CredFree(IntPtr buffer);
    }
}
'@
}

# ---------------------------------------------------------------------
# Target-name composition
# ---------------------------------------------------------------------

$Script:CredManTargetPrefix = 'PAXCookbook.AuthProfile.'
$Script:CredManTargetSuffix = '.ClientSecret'

function Get-AuthProfileCredManTarget {
    # Build the Windows Credential Manager target string for the given
    # auth profile. The id is the UUID stored in auth_profiles.auth_profile_id.
    # We do NOT validate the id shape here -- callers (Routes/AuthProfiles.ps1)
    # validate the UUID before reaching this helper. The target string is
    # operator-visible by design (it shows up in `cmdkey /list`).
    param([Parameter(Mandatory)][string]$AuthProfileId)
    return $Script:CredManTargetPrefix + $AuthProfileId + $Script:CredManTargetSuffix
}

function Test-AuthProfileIdForCredMan {
    # UUID shape guard. Cookbook only ever asks CredMan about UUIDs it
    # generated itself; we reject anything else to keep the API surface
    # from drifting into a generic credential store.
    param([Parameter(Mandatory)][string]$AuthProfileId)
    return [regex]::IsMatch($AuthProfileId, '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')
}

# ---------------------------------------------------------------------
# Write
# ---------------------------------------------------------------------

function Set-AuthProfileSecret {
    # Store (or overwrite) the client secret for an auth profile. The
    # SecureString is marshalled to an unmanaged UTF-16 buffer only for
    # the duration of the CredWrite call, then zeroed and freed. The
    # plaintext never crosses back into managed memory after this call;
    # subsequent reads MUST go through Read-AuthProfileSecret (which is
    # itself supervisor-internal).
    #
    # Returns:
    #   $true  -- write succeeded
    #   throws -- on any Win32 failure (with the Win32 error code in the message)
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$AuthProfileId,
        [Parameter(Mandatory)][securestring]$Secret
    )

    if (-not (Test-AuthProfileIdForCredMan -AuthProfileId $AuthProfileId)) {
        throw "Set-AuthProfileSecret: AuthProfileId '$AuthProfileId' is not a valid UUID."
    }

    $target = Get-AuthProfileCredManTarget -AuthProfileId $AuthProfileId

    # Marshal SecureString -> BSTR (unmanaged UTF-16 LE, NUL-terminated).
    # We compute byte length from the SecureString's character count
    # rather than scanning for NUL because the secret itself could
    # contain a literal NUL byte (unlikely for client secrets, but the
    # contract is "treat as opaque bytes"). The byte count we hand to
    # CredentialBlobSize is the UTF-16 length in bytes, NOT including
    # the terminator -- CredMan stores the blob verbatim.
    $bstr = [IntPtr]::Zero
    $targetPtr = [IntPtr]::Zero
    try {
        $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secret)
        # The BSTR has a 4-byte length prefix at offset -4 (the byte
        # count of the string content, NOT including the NUL terminator).
        # That is exactly what CredentialBlobSize expects.
        $byteCount = [System.Runtime.InteropServices.Marshal]::ReadInt32($bstr, -4)

        # Allocate the target name as a separate unmanaged Unicode string
        # so the CREDENTIAL struct holds owned IntPtrs.
        $targetPtr = [System.Runtime.InteropServices.Marshal]::StringToHGlobalUni($target)

        $cred = New-Object PAXCookbook.Native.CREDENTIAL
        $cred.Flags              = 0
        $cred.Type               = [PAXCookbook.Native.CredentialManagerNative]::CRED_TYPE_GENERIC
        $cred.TargetName         = $targetPtr
        $cred.Comment            = [IntPtr]::Zero
        $cred.LastWritten        = New-Object System.Runtime.InteropServices.ComTypes.FILETIME
        $cred.CredentialBlobSize = [uint32]$byteCount
        $cred.CredentialBlob     = $bstr
        $cred.Persist            = [PAXCookbook.Native.CredentialManagerNative]::CRED_PERSIST_LOCAL_MACHINE
        $cred.AttributeCount     = 0
        $cred.Attributes         = [IntPtr]::Zero
        $cred.TargetAlias        = [IntPtr]::Zero
        $cred.UserName           = [IntPtr]::Zero

        $ok = [PAXCookbook.Native.CredentialManagerNative]::CredWrite([ref]$cred, [uint32]0)
        if (-not $ok) {
            $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "CredWrite failed for target '$target' with Win32 error $err."
        }
        return $true
    }
    finally {
        if ($bstr -ne [IntPtr]::Zero) {
            # Zero the unmanaged copy of the secret then free it.
            [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
        if ($targetPtr -ne [IntPtr]::Zero) {
            [System.Runtime.InteropServices.Marshal]::FreeHGlobal($targetPtr)
        }
    }
}

# ---------------------------------------------------------------------
# Existence test (no plaintext exposure)
# ---------------------------------------------------------------------

function Test-AuthProfileSecretPresent {
    # Return $true if a credential exists under the auth profile's
    # target, $false otherwise. The actual blob is read but immediately
    # freed; no caller ever sees the secret bytes through this path.
    # This is the routine the HTTP /test and lock-overlay surfaces use
    # to render "secret bound / secret missing" without ever decoding
    # the secret.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$AuthProfileId)
    if (-not (Test-AuthProfileIdForCredMan -AuthProfileId $AuthProfileId)) {
        return $false
    }
    $target = Get-AuthProfileCredManTarget -AuthProfileId $AuthProfileId
    $credPtr = [IntPtr]::Zero
    try {
        $ok = [PAXCookbook.Native.CredentialManagerNative]::CredRead($target, [PAXCookbook.Native.CredentialManagerNative]::CRED_TYPE_GENERIC, [uint32]0, [ref]$credPtr)
        if (-not $ok) {
            $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
            if ($err -eq [PAXCookbook.Native.CredentialManagerNative]::ERROR_NOT_FOUND) {
                return $false
            }
            # Any other failure surfaces as $false rather than a throw,
            # because this helper is on the rendering path for the
            # auth-profile list view; failures there should never break
            # the page.
            return $false
        }
        return $true
    }
    finally {
        if ($credPtr -ne [IntPtr]::Zero) {
            [PAXCookbook.Native.CredentialManagerNative]::CredFree($credPtr)
        }
    }
}

# ---------------------------------------------------------------------
# Read (supervisor-internal only)
# ---------------------------------------------------------------------

function Read-AuthProfileSecret {
    # Return the SecureString form of the client secret for the given
    # auth profile, or $null if no credential is stored.
    #
    # **CRITICAL** -- this function exists ONLY to be called by
    # Cook/Supervisor.ps1 immediately before spawning the PAX child
    # process. There is NO HTTP route that exposes this, and NO log
    # call site that records the returned value. The intended call
    # pattern is:
    #
    #   $secure = Read-AuthProfileSecret -AuthProfileId $id
    #   try {
    #       $plain = ConvertFrom-SecureString -SecureString $secure -AsPlainText
    #       $psi.EnvironmentVariables['GRAPH_CLIENT_SECRET'] = $plain
    #   } finally {
    #       # zero the local plain copy as soon as the child process
    #       # has the env block; rely on PowerShell GC for the
    #       # SecureString.
    #       if ($plain) { $plain = ('0' * $plain.Length); $plain = $null }
    #   }
    #
    # Any new call site MUST be audited; the harness contract scan
    # asserts this function is referenced from at most a small,
    # operator-known set of files.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$AuthProfileId)
    if (-not (Test-AuthProfileIdForCredMan -AuthProfileId $AuthProfileId)) {
        return $null
    }
    $target = Get-AuthProfileCredManTarget -AuthProfileId $AuthProfileId
    $credPtr = [IntPtr]::Zero
    try {
        $ok = [PAXCookbook.Native.CredentialManagerNative]::CredRead($target, [PAXCookbook.Native.CredentialManagerNative]::CRED_TYPE_GENERIC, [uint32]0, [ref]$credPtr)
        if (-not $ok) {
            return $null
        }
        # Marshal the CREDENTIAL struct from the native pointer.
        $cred = [System.Runtime.InteropServices.Marshal]::PtrToStructure($credPtr, [type][PAXCookbook.Native.CREDENTIAL])
        $byteCount = [int]$cred.CredentialBlobSize
        if ($byteCount -le 0) { return $null }

        # Copy the bytes into a managed buffer so we can build a
        # SecureString character-by-character. UTF-16 LE, two bytes per
        # char. We zero the managed buffer at the end.
        $bytes = New-Object byte[] $byteCount
        [System.Runtime.InteropServices.Marshal]::Copy($cred.CredentialBlob, $bytes, 0, $byteCount)
        $secure = New-Object securestring
        try {
            for ($i = 0; $i -lt $byteCount; $i += 2) {
                $c = [char](($bytes[$i+1] -shl 8) -bor $bytes[$i])
                $secure.AppendChar($c)
            }
            $secure.MakeReadOnly()
            return $secure
        }
        finally {
            for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = 0 }
        }
    }
    finally {
        if ($credPtr -ne [IntPtr]::Zero) {
            [PAXCookbook.Native.CredentialManagerNative]::CredFree($credPtr)
        }
    }
}

# ---------------------------------------------------------------------
# Delete
# ---------------------------------------------------------------------

function Remove-AuthProfileSecret {
    # Delete the credential. Idempotent: ERROR_NOT_FOUND is treated as
    # success (the post-condition "no secret bound to this profile" is
    # already met). Any other failure throws with the Win32 error code.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$AuthProfileId)
    if (-not (Test-AuthProfileIdForCredMan -AuthProfileId $AuthProfileId)) {
        throw "Remove-AuthProfileSecret: AuthProfileId '$AuthProfileId' is not a valid UUID."
    }
    $target = Get-AuthProfileCredManTarget -AuthProfileId $AuthProfileId
    $ok = [PAXCookbook.Native.CredentialManagerNative]::CredDelete($target, [PAXCookbook.Native.CredentialManagerNative]::CRED_TYPE_GENERIC, [uint32]0)
    if (-not $ok) {
        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        if ($err -eq [PAXCookbook.Native.CredentialManagerNative]::ERROR_NOT_FOUND) {
            return $true  # idempotent
        }
        throw "CredDelete failed for target '$target' with Win32 error $err."
    }
    return $true
}
