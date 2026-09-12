using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

// ---------------------------------------------------------------------------
// FIXED CERTIFICATE-FACTS ADAPTER - cycle 92 pass B. NON-LIVE.
//
// WHAT THIS IS. The production implementation of the promotion transaction's
// certificate-facts port: it turns the ONE SHA-1 thumbprint the ACCEPTED request
// names into the bounded facts the ledger records, and refuses everything else.
//
// WHAT IT CAN NEVER DO. It never creates, imports, exports, copies, replaces,
// deletes, enrolls or renews a certificate or a private key. It opens exactly
// one store - LocalMachine\My - read-only and open-existing, and there is no
// CurrentUser fallback of any kind.
//
// HOW IT IS SPLIT. A PURE interpreter owns every decision; a FIXED shim performs
// the one read sequence and hands the interpreter bounded facts only.
// ---------------------------------------------------------------------------

/// <summary>
/// BOUNDED OBSERVED CERTIFICATE FACTS. Never a certificate, never a key handle,
/// never a path, never a delegate - only the small closed set of observations
/// the interpreter needs.
/// </summary>
internal readonly struct ServiceOwnershipObservedCertificateFacts
{
    private readonly string? observedThumbprintSha1;
    private readonly string? providerName;
    private readonly string? keyIdentity;
    private readonly string? providerUniqueName;

    internal ServiceOwnershipObservedCertificateFacts(
        int matchCount,
        string? observedThumbprintSha1,
        bool privateKeyAccessible,
        bool privateKeyIsRsaCng,
        bool machineScopedKey,
        int keySizeBits,
        string? providerName,
        string? keyIdentity,
        string? providerUniqueName)
    {
        MatchCount = matchCount;
        this.observedThumbprintSha1 = observedThumbprintSha1;
        PrivateKeyAccessible = privateKeyAccessible;
        PrivateKeyIsRsaCng = privateKeyIsRsaCng;
        MachineScopedKey = machineScopedKey;
        KeySizeBits = keySizeBits;
        this.providerName = providerName;
        this.keyIdentity = keyIdentity;
        this.providerUniqueName = providerUniqueName;
    }

    /// <summary>How many certificates in the ONE approved store carried the requested thumbprint.</summary>
    internal int MatchCount { get; }

    internal string ObservedThumbprintSha1 => observedThumbprintSha1 ?? string.Empty;

    internal bool PrivateKeyAccessible { get; }

    internal bool PrivateKeyIsRsaCng { get; }

    internal bool MachineScopedKey { get; }

    internal int KeySizeBits { get; }

    internal string ProviderName => providerName ?? string.Empty;

    internal string KeyIdentity => keyIdentity ?? string.Empty;

    internal string ProviderUniqueName => providerUniqueName ?? string.Empty;
}

/// <summary>
/// PURE interpreter. Zero I/O, no state, no delegate: every decision is
/// reproducible from its arguments alone.
///
/// THE ORDER IS DELIBERATE. A duplicate outranks every later gate, because with
/// two matches there is no single certificate whose key could have been judged;
/// reporting "private key unavailable" for an ambiguous pair would name the wrong
/// problem. Everything after the single-match gate describes THAT certificate.
/// </summary>
internal static class ServiceOwnershipCertificateFactsInterpreter
{
    /// <summary>The ONE approved provider. Never caller-supplied.</summary>
    internal const string ApprovedProviderName = "Microsoft Software Key Storage Provider";

    /// <summary>The ONE approved key size. Never a range, never a minimum.</summary>
    internal const int ApprovedKeySizeBits = 2048;

    internal static ServiceOwnershipCertificateFacts Interpret(
        string? requestedThumbprintSha1,
        ServiceOwnershipObservedCertificateFacts facts)
    {
        // A request whose thumbprint is not already normalized can never name a
        // certificate: there is no normalization step here to rescue it.
        if (!ServiceOwnershipLedgerContract.IsUppercaseSha1Thumbprint(requestedThumbprintSha1))
        {
            return ServiceOwnershipCertificateFacts.Failure(
                ServiceOwnershipCertificateFactsState.NotFound);
        }

        if (facts.MatchCount > 1)
        {
            return ServiceOwnershipCertificateFacts.Failure(
                ServiceOwnershipCertificateFactsState.Ambiguous);
        }

        if (facts.MatchCount != 1
            || !string.Equals(facts.ObservedThumbprintSha1, requestedThumbprintSha1, StringComparison.Ordinal))
        {
            return ServiceOwnershipCertificateFacts.Failure(
                ServiceOwnershipCertificateFactsState.NotFound);
        }

        if (!facts.PrivateKeyAccessible)
        {
            return ServiceOwnershipCertificateFacts.Failure(
                ServiceOwnershipCertificateFactsState.PrivateKeyUnavailable);
        }

        if (!facts.PrivateKeyIsRsaCng
            || !string.Equals(facts.ProviderName, ApprovedProviderName, StringComparison.Ordinal)
            || !facts.MachineScopedKey
            || facts.KeySizeBits != ApprovedKeySizeBits
            || !ServiceOwnershipLedgerContract.IsValidKeyIdentity(facts.KeyIdentity)
            || !ServiceOwnershipLedgerContract.IsValidProviderUniqueName(facts.ProviderUniqueName))
        {
            return ServiceOwnershipCertificateFacts.Failure(
                ServiceOwnershipCertificateFactsState.UnsupportedProvider);
        }

        // Re-derive the whole approved combination from the certified gate rather
        // than asserting it, so a future contract change cannot leave this adapter
        // silently emitting a profile the ledger no longer authorizes.
        if (!ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
                new ServiceOwnershipRightsMask(ServiceOwnershipLedgerContract.ApprovedRightsProfileMask),
                ServiceOwnershipLedgerContract.ApprovedRightsProfileProviderKind,
                ServiceOwnershipLedgerContract.ApprovedRightsProfileGrantMechanism,
                ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
                ServiceOwnershipLedgerContract.RightsPolicyVersion,
                ServiceOwnershipLedgerContract.ApprovedDescriptorFormat,
                out _))
        {
            return ServiceOwnershipCertificateFacts.Failure(
                ServiceOwnershipCertificateFactsState.UnsupportedProvider);
        }

        ServiceOwnershipPromotionKeyHandle key = ServiceOwnershipPromotionKeyHandle.Create(
            facts.KeyIdentity,
            facts.ProviderUniqueName,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileProviderKind,
            ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipLedgerContract.ApprovedDescriptorFormat);

        if (!key.IsUsable)
        {
            return ServiceOwnershipCertificateFacts.Failure(
                ServiceOwnershipCertificateFactsState.UnsupportedProvider);
        }

        return ServiceOwnershipCertificateFacts.Observed(
            requestedThumbprintSha1!,
            ServiceOwnershipCredentialKind.PersonalAppRegistrationCertificate,
            ServiceOwnershipProvenance.Referenced,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileGrantMechanism,
            key);
    }
}

/// <summary>
/// The FIXED SHIM. ONE entry point taking ONLY the normalized thumbprint the
/// accepted request already carried - no store name, no location, no provider
/// name, no path and no delegate.
///
/// THE FIXED SEQUENCE: reject a non-Windows platform; reject a thumbprint that is
/// not already normalized; open ONLY the machine personal store, read-only and
/// open-existing; resolve ONLY that thumbprint; require EXACTLY ONE match; read
/// the bounded key facts from that one certificate; hand them to the pure
/// interpreter; dispose every certificate, collection, key and store in a finally
/// block. There is no branch that creates, imports, exports, copies, replaces,
/// deletes, enrolls or renews anything.
/// </summary>
internal sealed class ServiceOwnershipFixedCertificateFactsPort : IServiceOwnershipCertificateFactsPort
{
    public ServiceOwnershipCertificateFacts ObserveCertificateFacts(string normalizedThumbprintSha1)
    {
        if (!OperatingSystem.IsWindows()
            || !ServiceOwnershipLedgerContract.IsUppercaseSha1Thumbprint(normalizedThumbprintSha1))
        {
            return ServiceOwnershipCertificateFacts.Failure(
                ServiceOwnershipCertificateFactsState.NotFound);
        }

        X509Store? store = null;
        X509Certificate2Collection? matches = null;
        RSA? privateKey = null;

        try
        {
            store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

            matches = store.Certificates.Find(
                X509FindType.FindByThumbprint, normalizedThumbprintSha1, validOnly: false);

            if (matches.Count != 1)
            {
                return ServiceOwnershipCertificateFactsInterpreter.Interpret(
                    normalizedThumbprintSha1,
                    new ServiceOwnershipObservedCertificateFacts(
                        matches.Count, null, false, false, false, 0, null, null, null));
            }

            X509Certificate2 certificate = matches[0];
            string observedThumbprint = certificate.Thumbprint ?? string.Empty;

            bool accessible = false;
            bool rsaCng = false;
            bool machineScoped = false;
            int keySizeBits = 0;
            string? providerName = null;
            string? keyIdentity = null;
            string? providerUniqueName = null;

            if (certificate.HasPrivateKey)
            {
                try
                {
                    privateKey = certificate.GetRSAPrivateKey();
                }
                catch (CryptographicException)
                {
                    privateKey = null;
                }

                accessible = privateKey is not null;

                if (privateKey is RSACng cngBacked)
                {
                    rsaCng = true;
                    keySizeBits = cngBacked.KeySize;
                    try
                    {
                        CngKey cngKey = cngBacked.Key;
                        providerName = cngKey.Provider?.Provider;
                        machineScoped = cngKey.IsMachineKey;
                        keyIdentity = cngKey.KeyName;
                        providerUniqueName = cngKey.UniqueName;
                    }
                    catch (CryptographicException)
                    {
                        providerName = null;
                        keyIdentity = null;
                        providerUniqueName = null;
                    }
                }
            }

            return ServiceOwnershipCertificateFactsInterpreter.Interpret(
                normalizedThumbprintSha1,
                new ServiceOwnershipObservedCertificateFacts(
                    matches.Count, observedThumbprint, accessible, rsaCng, machineScoped,
                    keySizeBits, providerName, keyIdentity, providerUniqueName));
        }
        catch (CryptographicException)
        {
            return ServiceOwnershipCertificateFacts.Failure(
                ServiceOwnershipCertificateFactsState.NotFound);
        }
        finally
        {
            privateKey?.Dispose();

            if (matches is not null)
            {
                foreach (X509Certificate2 held in matches)
                {
                    held.Dispose();
                }
            }

            store?.Dispose();
        }
    }
}
