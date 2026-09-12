using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.App;

// The REAL, READ-ONLY production certificate catalog (Cycle 11, extended by
// Cycle 12 with BOUNDED USABILITY FACTS).
//
// It reports bounded OCCURRENCE METADATA and bounded USABILITY FACTS, and
// nothing else: for each certificate in the fixed machine catalog it publishes
// the uppercase hex SHA-256 over that certificate's DER bytes, an ordinal used
// only to distinguish duplicates, and a bounded fact record (validity window,
// normalized purpose OIDs, key-usage booleans, algorithm classification, and a
// private-key AVAILABILITY verdict). It never returns a certificate, a handle, a
// key, a key-container or provider name, or any other field, and it never
// mutates anything. Being listed here is an OBSERVATION ONLY: it never implies
// the certificate is trusted, chain-valid, non-revoked, permitted, Recipe-bound,
// or Bake-authorized. Every capability on the Cycle-10 resolution result stays
// constant-false.
//
// AVAILABILITY IS NOT USE. The private-key probe calls ONLY the availability
// APIs, disposes the returned key object IMMEDIATELY, and never returns, caches,
// persists, names, or projects it. It never signs, never verifies with a key,
// never enciphers, never deciphers, and never writes a key out.
//
// The source is fixed BY CONSTRUCTION to the machine catalog
// (StoreLocation.LocalMachine + StoreName.My) and opened with EXACTLY
// OpenFlags.OpenExistingOnly | OpenFlags.ReadOnly, so the catalog can never
// create a store and can never be pointed somewhere else: there is no
// configuration, environment variable, command-line switch, registry value, or
// user/HTTP/React input that selects the location. The only alternate paths are
// internal seams used by tests with synthetic bytes and a synthetic key probe;
// production never constructs them.
//
// It FAILS CLOSED for the WHOLE catalog - never partially. A missing or
// inaccessible catalog, an enumeration failure, a per-certificate encoding
// failure, or a count above the bounded maximum all yield Unavailable, because a
// truncated or partial list could turn a real occurrence into a false "not
// found". Per-certificate FACT extraction fails closed on its own, to a bounded
// unavailable fact record, so one unreadable certificate can never make another
// look usable. No exception text ever escapes.
//
// It does NOT: use a key pair; sign; decipher; open or name a key container;
// enumerate or match by any other field; build a chain; check revocation; judge
// issuer trust; write, delete, install, or remove anything; read ProgramData or
// the registry; contact a tenant, Graph, a service, WAM/Hello, or the network;
// bind a Recipe; add a Cook step; run PAX; or perform a Bake.
internal sealed class MachineCertificateCatalog : ICertificateCatalog
{
    // RFC 5280 extension identifiers, recorded as bounded tokens.
    private const string PurposeExtensionOid = "2.5.29.37";
    private const string KeyUsageExtensionOid = "2.5.29.15";

    // Public key algorithm identifiers. The supported set is EXACTLY these two.
    private const string RsaPublicKeyOid = "1.2.840.113549.1.1.1";
    private const string EllipticCurvePublicKeyOid = "1.2.840.10045.2.1";

    private readonly Func<IReadOnlyList<byte[]>?>? _encodedEnumerator;
    private readonly Func<X509Certificate2, CertificateKeyAvailability>? _keyProbe;

    // Production. The catalog source is fixed here and nowhere else, and the key
    // probe is the real availability-only probe.
    public MachineCertificateCatalog()
    {
        _encodedEnumerator = null;
        _keyProbe = null;
    }

    // Test-only seam, mirroring the ProgramData inventory source. It supplies
    // synthetic DER bytes so the bounded behavior can be proven deterministically
    // without ever touching the machine catalog. Production never calls it.
    internal MachineCertificateCatalog(Func<IReadOnlyList<byte[]>?> encodedEnumerator)
        : this(encodedEnumerator, null)
    {
    }

    // Test-only seam that ALSO substitutes the private-key availability probe, so
    // "declares a key but the supported accessor yields nothing" and "the accessor
    // throws" can be proven WITHOUT a real key ever existing. Production never
    // calls it.
    internal MachineCertificateCatalog(
        Func<IReadOnlyList<byte[]>?> encodedEnumerator,
        Func<X509Certificate2, CertificateKeyAvailability>? keyProbe)
    {
        _encodedEnumerator = encodedEnumerator;
        _keyProbe = keyProbe;
    }

    public CertificateCatalogResult Query()
    {
        IReadOnlyList<byte[]>? encoded;
        IReadOnlyList<CertificateUsabilityFacts>? facts = null;
        try
        {
            if (_encodedEnumerator is null)
            {
                (encoded, facts) = ReadMachineCatalog();
            }
            else
            {
                encoded = _encodedEnumerator();
            }
        }
        catch (Exception)
        {
            // Missing catalog, denied access, or enumeration failure: fail closed
            // for the whole catalog and surface no exception detail.
            return CertificateCatalogResult.Unavailable();
        }

        if (encoded is null)
        {
            return CertificateCatalogResult.Unavailable();
        }

        // Above the bounded maximum the catalog fails closed rather than
        // truncating, because a truncated list could report a false not-found.
        if (encoded.Count > OrganizationCertificateResolutionContract.MaxCatalogOccurrences)
        {
            ClearEncoded(encoded);
            return CertificateCatalogResult.Unavailable();
        }

        var occurrences = new CertificateCatalogOccurrence[encoded.Count];
        try
        {
            for (int i = 0; i < encoded.Count; i++)
            {
                byte[]? der = encoded[i];
                if (der is null || der.Length == 0)
                {
                    return CertificateCatalogResult.Unavailable();
                }

                // Production facts were captured from the live catalog entry (the
                // only place a private key can be observed at all). The synthetic
                // seam re-derives what it can from the supplied bytes alone.
                CertificateUsabilityFacts entryFacts = facts is not null && i < facts.Count
                    ? facts[i]
                    : ExtractFactsFromEncoded(der);

                // Duplicates are RETAINED (never de-duplicated) so the Cycle-10
                // resolver can report an ambiguous reference instead of a match.
                occurrences[i] = CertificateCatalogOccurrence.Create(
                    Convert.ToHexString(SHA256.HashData(der)), i, entryFacts);
            }
        }
        catch (Exception)
        {
            return CertificateCatalogResult.Unavailable();
        }
        finally
        {
            // The transient encoded bytes never outlive the hash.
            ClearEncoded(encoded);
        }

        return CertificateCatalogResult.Available(occurrences);
    }

    // The ONLY production read. It opens the fixed machine catalog read-only and
    // existing-only, extracts each certificate's bounded facts, copies its DER
    // bytes, and disposes every certificate and the catalog handle promptly. No
    // certificate object survives this method.
    private (IReadOnlyList<byte[]> Encoded, IReadOnlyList<CertificateUsabilityFacts> Facts) ReadMachineCatalog()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadOnly);

        X509Certificate2Collection certificates = store.Certificates;
        var encoded = new byte[certificates.Count][];
        var facts = new CertificateUsabilityFacts[certificates.Count];
        for (int i = 0; i < certificates.Count; i++)
        {
            using X509Certificate2 certificate = certificates[i];
            facts[i] = ExtractFacts(certificate, _keyProbe);
            encoded[i] = certificate.RawData;
        }
        return (encoded, facts);
    }

    // Synthetic-seam fact extraction. The bytes are a PUBLIC certificate encoding
    // only, so a private key can never be observed here; a probe seam supplies the
    // simulated availability verdict when a test needs one.
    private CertificateUsabilityFacts ExtractFactsFromEncoded(byte[] der)
    {
        try
        {
            using var certificate = new X509Certificate2(der);
            return ExtractFacts(certificate, _keyProbe);
        }
        catch (Exception)
        {
            // Unparseable bytes yield a bounded unavailable fact record; the whole
            // catalog stays answerable so an occurrence is never silently dropped.
            return CertificateUsabilityFacts.ExtractionUnavailable();
        }
    }

    // Bounded fact extraction. It reads ONLY the validity window, the two RFC 5280
    // extensions this product cares about, the public key algorithm, and private-
    // key AVAILABILITY. It reads no other field and retains no certificate. It is
    // internal and static so tests can prove the REAL availability APIs against an
    // in-memory certificate without ever opening the machine catalog.
    internal static CertificateUsabilityFacts ExtractFacts(
        X509Certificate2 certificate,
        Func<X509Certificate2, CertificateKeyAvailability>? keyProbe)
    {
        try
        {
            int purposeCount = 0;
            int keyUsageCount = 0;
            foreach (X509Extension extension in certificate.Extensions)
            {
                string? oid = extension?.Oid?.Value;
                if (string.Equals(oid, PurposeExtensionOid, StringComparison.Ordinal))
                {
                    purposeCount++;
                }
                else if (string.Equals(oid, KeyUsageExtensionOid, StringComparison.Ordinal))
                {
                    keyUsageCount++;
                }
            }

            // RFC 5280 permits each extension at most once, so a duplicate is
            // malformed and refuses rather than picking one.
            bool purposePresent = purposeCount > 0;
            bool purposeMalformed = purposeCount > 1;
            bool keyUsagePresent = keyUsageCount > 0;
            bool keyUsageMalformed = keyUsageCount > 1;

            string[] purposeOids = Array.Empty<string>();
            if (purposePresent && !purposeMalformed)
            {
                (purposeOids, purposeMalformed) = ReadPurposeOids(certificate);
            }

            bool digitalSignatureAllowed = false;
            if (keyUsagePresent && !keyUsageMalformed)
            {
                (digitalSignatureAllowed, keyUsageMalformed) = ReadDigitalSignatureUsage(certificate);
            }

            CertificateKeyAlgorithmClass algorithm = ClassifyAlgorithm(certificate);

            return CertificateUsabilityFacts.Create(
                certificate.NotBefore.ToUniversalTime(),
                certificate.NotAfter.ToUniversalTime(),
                algorithm,
                ProbeKeyAvailability(certificate, algorithm, keyProbe),
                purposePresent,
                purposeOids,
                purposeMalformed,
                keyUsagePresent,
                digitalSignatureAllowed,
                keyUsageMalformed);
        }
        catch (Exception)
        {
            return CertificateUsabilityFacts.ExtractionUnavailable();
        }
    }

    private static (string[] Oids, bool Malformed) ReadPurposeOids(X509Certificate2 certificate)
    {
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension is null
                || !string.Equals(extension.Oid?.Value, PurposeExtensionOid, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                X509EnhancedKeyUsageExtension typed = extension as X509EnhancedKeyUsageExtension
                    ?? new X509EnhancedKeyUsageExtension(extension, extension.Critical);
                OidCollection declared = typed.EnhancedKeyUsages;
                if (declared.Count > OrganizationCertificateUsabilityContract.MaxPurposeOids)
                {
                    return (Array.Empty<string>(), true);
                }

                var values = new string[declared.Count];
                for (int i = 0; i < declared.Count; i++)
                {
                    values[i] = declared[i].Value ?? string.Empty;
                }
                return (values, false);
            }
            catch (Exception)
            {
                // Undecodable extension content is malformed: refuse.
                return (Array.Empty<string>(), true);
            }
        }
        return (Array.Empty<string>(), true);
    }

    private static (bool DigitalSignatureAllowed, bool Malformed) ReadDigitalSignatureUsage(
        X509Certificate2 certificate)
    {
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension is null
                || !string.Equals(extension.Oid?.Value, KeyUsageExtensionOid, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                X509KeyUsageExtension typed = extension as X509KeyUsageExtension
                    ?? new X509KeyUsageExtension(extension, extension.Critical);
                bool allowed = (typed.KeyUsages & X509KeyUsageFlags.DigitalSignature)
                    == X509KeyUsageFlags.DigitalSignature;
                return (allowed, false);
            }
            catch (Exception)
            {
                return (false, true);
            }
        }
        return (false, true);
    }

    // Classified from the PUBLIC key algorithm identifier, BEFORE any private-key
    // probe, so an unsupported algorithm is reported as such rather than as a
    // missing key.
    internal static CertificateKeyAlgorithmClass ClassifyAlgorithm(X509Certificate2 certificate)
    {
        string? oid;
        try
        {
            oid = certificate.PublicKey?.Oid?.Value;
        }
        catch (Exception)
        {
            return CertificateKeyAlgorithmClass.Unknown;
        }

        if (string.Equals(oid, RsaPublicKeyOid, StringComparison.Ordinal))
        {
            return CertificateKeyAlgorithmClass.Rsa;
        }
        if (string.Equals(oid, EllipticCurvePublicKeyOid, StringComparison.Ordinal))
        {
            return CertificateKeyAlgorithmClass.Ecdsa;
        }
        return oid is null
            ? CertificateKeyAlgorithmClass.Unknown
            : CertificateKeyAlgorithmClass.Unsupported;
    }

    // AVAILABILITY ONLY. It asks whether the certificate declares a private key
    // and whether a SUPPORTED key object can be obtained, then DISPOSES that
    // object immediately. The object is never returned, cached, persisted, named,
    // or projected, and it is never used for any cryptographic operation. Any
    // failure - declared-but-absent, unsupported, denied, or throwing - fails
    // closed to Unavailable with no exception detail escaping.
    internal static CertificateKeyAvailability ProbeKeyAvailability(
        X509Certificate2 certificate,
        CertificateKeyAlgorithmClass algorithm,
        Func<X509Certificate2, CertificateKeyAvailability>? keyProbe)
    {
        try
        {
            if (keyProbe is not null)
            {
                return keyProbe(certificate);
            }

            if (!certificate.HasPrivateKey)
            {
                return CertificateKeyAvailability.Unavailable;
            }

            if (algorithm == CertificateKeyAlgorithmClass.Rsa)
            {
                using RSA? rsa = certificate.GetRSAPrivateKey();
                return rsa is null
                    ? CertificateKeyAvailability.Unavailable
                    : CertificateKeyAvailability.Available;
            }

            if (algorithm == CertificateKeyAlgorithmClass.Ecdsa)
            {
                using ECDsa? ecdsa = certificate.GetECDsaPrivateKey();
                return ecdsa is null
                    ? CertificateKeyAvailability.Unavailable
                    : CertificateKeyAvailability.Available;
            }

            return CertificateKeyAvailability.Unavailable;
        }
        catch (Exception)
        {
            return CertificateKeyAvailability.Unavailable;
        }
    }

    private static void ClearEncoded(IReadOnlyList<byte[]>? encoded)
    {
        if (encoded is null)
        {
            return;
        }
        for (int i = 0; i < encoded.Count; i++)
        {
            byte[]? der = encoded[i];
            if (der is not null)
            {
                Array.Clear(der, 0, der.Length);
            }
        }
    }
}
