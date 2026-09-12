using System;
using System.Collections.Generic;
using System.Security;
using Microsoft.Win32;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.App;

// Thin PRODUCTION reader for the authoritative machine-policy source.
//
// It reads ONLY HKLM\SOFTWARE\Policies\PAXCookbook, read-only (writable:false),
// least privilege. The sub-path is a hardcoded contract constant — it is NEVER
// configurable from user, environment, or command-line input, so nothing
// user-controlled can redirect or override the machine policy. This reader never
// touches HKCU, environment variables, command-line arguments, files, the
// network, a tenant, a token, WAM, Windows Hello, a certificate store, the
// Windows service, a Chef's Key, PAX, or a Bake. It performs NO registry write
// (read-only open; no value-set or subkey create/delete). It is defensive: any
// failure degrades to a bounded access outcome rather than throwing.
//
// It is trusted by construction: the HKLM\SOFTWARE\Policies subtree is
// administrator/SYSTEM-write-only, so this reader only ever returns Absent,
// Present, or AccessDenied. The `Untrusted` outcome is producible only through an
// injected IMachinePolicySource (used by tests to prove the pure parser fails
// closed), never by this production reader.
internal sealed class MachinePolicyRegistrySource : IMachinePolicySource
{
    public MachinePolicySnapshot Read()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                MachinePolicyContract.PolicySubKeyPath, writable: false);

            if (key is null)
            {
                return MachinePolicySnapshot.Absent();
            }

            var values = new Dictionary<string, MachinePolicyRawValue>(StringComparer.OrdinalIgnoreCase);
            foreach (string valueName in MachinePolicyContract.CanonicalValueNames)
            {
                MachinePolicyRawValue? raw = TryReadValue(key, valueName);
                if (raw is not null)
                {
                    values[valueName] = raw;
                }
            }

            return MachinePolicySnapshot.Present(values);
        }
        catch (SecurityException)
        {
            return MachinePolicySnapshot.AccessDenied();
        }
        catch (UnauthorizedAccessException)
        {
            return MachinePolicySnapshot.AccessDenied();
        }
        catch (Exception)
        {
            // Any other unexpected failure fails closed to invalid via the parser
            // (AccessDenied is the least-privilege-safe classification here).
            return MachinePolicySnapshot.AccessDenied();
        }
    }

    private static MachinePolicyRawValue? TryReadValue(RegistryKey key, string valueName)
    {
        try
        {
            RegistryValueKind kind;
            try
            {
                kind = key.GetValueKind(valueName);
            }
            catch (System.IO.IOException)
            {
                // Value does not exist.
                return null;
            }

            switch (kind)
            {
                case RegistryValueKind.DWord:
                {
                    object? boxed = key.GetValue(valueName);
                    if (boxed is int i)
                    {
                        return MachinePolicyRawValue.ForDword(i);
                    }
                    return MachinePolicyRawValue.ForOther(encodedLength: 0);
                }

                case RegistryValueKind.String:
                {
                    // No expansion; read the raw string without processing.
                    object? boxed = key.GetValue(valueName, defaultValue: null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (boxed is string s)
                    {
                        return MachinePolicyRawValue.ForString(s);
                    }
                    return MachinePolicyRawValue.ForOther(encodedLength: 0);
                }

                default:
                    return MachinePolicyRawValue.ForOther(encodedLength: 0);
            }
        }
        catch (SecurityException)
        {
            // Surface as an unreadable value; classification treats a partial read
            // conservatively (the caller's snapshot access remains Present, and a
            // missing required value fails closed to invalid/partial).
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
