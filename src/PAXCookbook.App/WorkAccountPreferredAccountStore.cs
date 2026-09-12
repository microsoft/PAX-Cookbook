// Protected preferred Work-account continuity store — EXPERIMENTAL, gated.
//
// This entire file compiles ONLY under the EXPERIMENTAL_WAM constant (set by the
// ExperimentalWam=true build property, which also adds the DPAPI ProtectedData
// package reference). Stable / customer builds define no such constant, reference
// no ProtectedData package, and contain none of this code, so the stable payload
// carries no preferred-account persistence at all.
//
// Containment doctrine (binding):
//   - The ONLY value ever persisted is the opaque MSAL account reference
//     (HomeAccountId.Identifier). It is written EXCLUSIVELY as a DPAPI
//     CurrentUser-protected reversible blob (ProtectedData.Protect with
//     DataProtectionScope.CurrentUser). The plaintext reference is NEVER written
//     to disk, NEVER logged, NEVER serialized, NEVER returned over any boundary,
//     and NEVER echoed. It is decrypted only transiently in-process for account
//     resolution.
//   - No token, id token, claim, UPN, tenant id, object id, display name, client
//     id, photo, or any other identity value is ever stored here or anywhere by
//     this store. The on-disk artifact is opaque ciphertext bytes only.
//   - The location is isolated-root-aware: it mirrors ExperimentalWamConfigStore's
//     path resolution under the same per-user localAppData base the app threads,
//     so an isolated test fixture (or the --engine-localappdata override) points
//     both stores at the same isolated profile and never at the real install.
//   - Writes are atomic (sibling temp file + move/replace) and are immediately
//     read back and unprotected to confirm the round trip before reporting success.
//   - Reads FAIL CLOSED to "no preference": a missing, empty, unreadable,
//     malformed, or undecryptable blob yields null (treated as no-preference by
//     the resolver), never a partial or guessed reference.

#if EXPERIMENTAL_WAM
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PAXCookbook.App;

// The bounded acknowledgement returned by the native different-account clear.
// It carries no identifier, no path, and no exception detail — only whether the
// protected preferred reference was actually removed.
internal enum WorkAccountPreferredClearAck
{
    Cleared = 0,
    Failed = 1,
}

// The window-process persistence port for the preferred-account reference. An
// implementation is injected into the authenticator ONLY in the HWND-owning
// window process; the default (stable) build never constructs it and the daemon
// never receives it. TryLoad returns the DECRYPTED reference transiently for
// in-process resolution only; callers must never log or persist that value.
internal interface IWorkAccountPreferredAccountStore
{
    // Persists the opaque account reference as a DPAPI CurrentUser-protected
    // blob (atomic write + read-back verify). Returns true only when the round
    // trip is confirmed. Never throws for an IO/crypto failure — returns false.
    bool TrySave(string accountReference);

    // Returns the decrypted opaque account reference, or null when there is no
    // usable preference (missing / empty / malformed / undecryptable). Never
    // throws; never logs the reference.
    string? TryLoad();

    // Removes the protected preferred reference (ownership-scoped to the current
    // user's per-user file). Returns true when the reference is absent after the
    // call (deleted or already absent); false only when removal failed.
    bool Clear();
}

// DPAPI CurrentUser-backed implementation. The blob is bound to the current
// Windows user by DataProtectionScope.CurrentUser plus a fixed, non-secret
// application entropy constant that scopes the ciphertext to this exact use.
internal sealed class WorkAccountPreferredAccountStore : IWorkAccountPreferredAccountStore
{
    private const string ProductFolder = "PAXCookbook";
    private const string ConfigFolder = "Config";
    private const string PreferredFileName = "work-account-preferred.bin";

    // Fixed, NON-SECRET application entropy. It is not a key and not a secret; it
    // only scopes the DPAPI blob to this exact preferred-account use so an
    // unrelated CurrentUser blob can never be unprotected here by accident.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("PAXCookbook.WorkAccount.PreferredAccount.v1");

    private readonly string _localAppDataBase;

    internal WorkAccountPreferredAccountStore(string localAppDataBase)
    {
        _localAppDataBase = localAppDataBase ?? throw new ArgumentNullException(nameof(localAppDataBase));
    }

    // Resolves the protected blob path under the per-user install anchor:
    // <localAppDataBase>\PAXCookbook\Config\work-account-preferred.bin — the same
    // anchor rule ExperimentalWamConfigStore uses, so an isolated fixture points
    // both at the same isolated profile.
    internal static string ResolvePreferredPath(string localAppDataBase)
        => Path.Combine(localAppDataBase, ProductFolder, ConfigFolder, PreferredFileName);

    public bool TrySave(string accountReference)
    {
        if (string.IsNullOrWhiteSpace(accountReference))
        {
            return false;
        }

        string path = ResolvePreferredPath(_localAppDataBase);
        string dir = Path.GetDirectoryName(path)!;
        string tempPath = path + ".tmp";

        byte[]? plaintext = null;
        byte[]? cipher = null;
        try
        {
            Directory.CreateDirectory(dir);

            plaintext = Encoding.UTF8.GetBytes(accountReference);
            cipher = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

            // Atomic publish: write the ciphertext to a sibling temp file, then
            // move/replace into place.
            File.WriteAllBytes(tempPath, cipher);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path);
            }

            // Immediate read-back + unprotect to confirm the round trip before
            // reporting success. The recovered plaintext is compared in-process
            // and then discarded; it is never logged.
            byte[] readBack = File.ReadAllBytes(path);
            byte[] recovered = ProtectedData.Unprotect(readBack, Entropy, DataProtectionScope.CurrentUser);
            bool ok = CryptographicOperations.FixedTimeEquals(recovered, plaintext);
            Array.Clear(recovered, 0, recovered.Length);
            Array.Clear(readBack, 0, readBack.Length);
            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            TryDeleteQuietly(tempPath);
            return false;
        }
        finally
        {
            if (plaintext is not null) { Array.Clear(plaintext, 0, plaintext.Length); }
            if (cipher is not null) { Array.Clear(cipher, 0, cipher.Length); }
        }
    }

    public string? TryLoad()
    {
        string path = ResolvePreferredPath(_localAppDataBase);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[]? cipher = null;
        byte[]? plaintext = null;
        try
        {
            cipher = File.ReadAllBytes(path);
            if (cipher.Length == 0)
            {
                return null;
            }

            plaintext = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            string reference = Encoding.UTF8.GetString(plaintext);
            return string.IsNullOrWhiteSpace(reference) ? null : reference;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Unreadable / malformed / undecryptable: fail closed to no-preference.
            return null;
        }
        finally
        {
            if (cipher is not null) { Array.Clear(cipher, 0, cipher.Length); }
            if (plaintext is not null) { Array.Clear(plaintext, 0, plaintext.Length); }
        }
    }

    public bool Clear()
    {
        string path = ResolvePreferredPath(_localAppDataBase);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            // Absent after the call (deleted or never present) is success.
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp file only.
        }
    }
}

// The bounded decision produced by preferred-account resolution: either bind to
// exactly one preselected account (still an interactive acquisition) or force an
// explicit account selection. It carries only the opaque bound identifier (when
// binding) — never a token, claim, or any other identity value.
internal sealed class WorkAccountAccountResolution
{
    private WorkAccountAccountResolution(bool bindToAccount, string? boundIdentifier)
    {
        BindToAccount = bindToAccount;
        BoundIdentifier = boundIdentifier;
    }

    // True when exactly one available account matched the stored preference.
    internal bool BindToAccount { get; }

    // The opaque HomeAccountId.Identifier to preselect — non-null ONLY when
    // BindToAccount is true.
    internal string? BoundIdentifier { get; }

    // Force an explicit account selection (Prompt.SelectAccount): no preference,
    // no match, an ambiguous match, or an unusable blob.
    internal static WorkAccountAccountResolution ForceSelect { get; } = new(bindToAccount: false, boundIdentifier: null);

    internal static WorkAccountAccountResolution Bind(string identifier) => new(bindToAccount: true, identifier);
}

// Pure, MSAL-free preferred-account resolution. Given the stored opaque reference
// (already decrypted transiently by the store) and the opaque identifiers of the
// currently available MSAL accounts, it decides whether to preselect exactly one
// account or force explicit selection. It NEVER performs a silent acquisition and
// NEVER selects a different account than the exact stored reference.
internal static class WorkAccountPreferredAccountResolver
{
    // EXACT unique match on HomeAccountId.Identifier:
    //   - no stored preference               -> force explicit selection
    //   - exactly one exact match            -> bind (preselect) that account
    //   - zero matches                       -> force explicit selection
    //   - more than one exact match          -> force explicit selection
    internal static WorkAccountAccountResolution Resolve(
        string? storedReference,
        IReadOnlyList<string> availableIdentifiers)
    {
        if (string.IsNullOrWhiteSpace(storedReference))
        {
            return WorkAccountAccountResolution.ForceSelect;
        }

        if (availableIdentifiers is null || availableIdentifiers.Count == 0)
        {
            return WorkAccountAccountResolution.ForceSelect;
        }

        int matches = 0;
        foreach (string id in availableIdentifiers)
        {
            if (!string.IsNullOrWhiteSpace(id) &&
                string.Equals(id, storedReference, StringComparison.Ordinal))
            {
                matches++;
                if (matches > 1)
                {
                    // Ambiguous — never silently choose one.
                    return WorkAccountAccountResolution.ForceSelect;
                }
            }
        }

        return matches == 1
            ? WorkAccountAccountResolution.Bind(storedReference)
            : WorkAccountAccountResolution.ForceSelect;
    }
}

// Pure acquisition policy for every Work-account session unlock. A uniquely
// resolved preference remains a picker hint via WithAccount; it never bypasses
// the WAM account picker. Every branch requires Prompt.SelectAccount, allowing
// an existing account to complete with WAM SSO after the user selects it without
// forcing credential re-entry.
internal sealed class WorkAccountInteractiveAcquisitionPlan
{
    private WorkAccountInteractiveAcquisitionPlan(string? boundIdentifier)
    {
        BoundIdentifier = boundIdentifier;
    }

    internal string? BoundIdentifier { get; }

    internal bool BindPreferredAccount => BoundIdentifier is { Length: > 0 };

    internal bool RequireAccountPicker => true;

    internal static WorkAccountInteractiveAcquisitionPlan Create(
        WorkAccountAcquisitionMode mode,
        string? storedReference,
        IReadOnlyList<string> availableIdentifiers)
    {
        if (mode == WorkAccountAcquisitionMode.ForceAccountSelection)
        {
            return new WorkAccountInteractiveAcquisitionPlan(boundIdentifier: null);
        }

        WorkAccountAccountResolution resolution =
            WorkAccountPreferredAccountResolver.Resolve(storedReference, availableIdentifiers);

        return resolution.BindToAccount
            ? new WorkAccountInteractiveAcquisitionPlan(resolution.BoundIdentifier)
            : new WorkAccountInteractiveAcquisitionPlan(boundIdentifier: null);
    }
}

// Pure policy gating the preferred-account REPLACEMENT. The stored reference is
// replaced ONLY after a fully successful scope + identity validated
// authorization — never on failure or cancel. This centralizes that decision so
// it is deterministic and unit-testable without MSAL, and is the exact same
// validation the window bridge applies before approval.
//
// Force-selection acquisitions never replace the stored reference: the mode
// exists to keep the preference from influencing a session unlock, so writing a
// fresh preference from that same unlock would reintroduce the binding it is
// meant to avoid and would mutate protected state during an attended run.
internal static class WorkAccountPreferredAccountReplacementPolicy
{
    internal static bool ShouldReplace(
        WamInteractiveResult result,
        ExperimentalWamOptions options,
        WorkAccountAcquisitionMode mode)
    {
        if (result is null || options is null || !result.Succeeded)
        {
            return false;
        }

        if (mode == WorkAccountAcquisitionMode.ForceAccountSelection)
        {
            return false;
        }

        return WamScopeIdentityValidator.Validate(result, options) == WamValidation.Valid;
    }
}
#endif
