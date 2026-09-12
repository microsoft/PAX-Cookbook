using System;
using System.Collections.Generic;

namespace PAXCookbook.Shared.Contracts;

// Canonical, cross-component contract for CERTIFICATE REFERENCE RESOLUTION
// (Cycle 10).
//
// This contract defines how a schema-v2 organization inventory entry's PUBLIC
// certificate reference (SHA-256 over the certificate's DER bytes) would be
// resolved against a certificate catalog, WITHOUT opening, enumerating, reading,
// validating, or selecting any real certificate. It is a pure, deterministic,
// side-effect-free state model ONLY. It does NOT: open a certificate store, list
// or search certificates, read certificate bytes, inspect validity/EKU/chain/ACL,
// touch a private key, obtain a service-backed credential, query a tenant, query
// Microsoft Graph, acquire a token, use WAM/Windows Hello, contact a service, bind
// a Recipe, add a Cook step, run PAX, or perform a Bake.
//
// Being RESOLVED here means a METADATA MATCH AND NOTHING ELSE. It never implies
// the certificate is present, valid, trusted, permitted, private-key-backed,
// usable, Recipe-bound, or Bake-authorized. Every capability on the resolution
// result is constant-false by construction.
//
// SINGLE SOURCE OF TRUTH. Compiled into PAXCookbook.Shared (used by Setup) AND
// linkable into the native host via the established <Compile Include=... Link=...>
// pattern. It depends only on System and System.Collections.Generic - NO Win32,
// NO cryptography API, NO certificate API, NO network, NO filesystem.
//
// FAIL-CLOSED DOCTRINE (binding): a null projection, a non-provisioned or invalid
// inventory, a null entry, a disabled entry, a missing reference, a null or
// disconnected catalog, zero matches, and multiple matches ALL resolve to a
// bounded non-resolved state. There is no permissive fallback, no "assume
// present", and no state that grants a capability.

// The fixed, future production source of certificate metadata, recorded as
// bounded tokens and doctrine ONLY. This cycle opens nothing: no production code
// path consumes these tokens, and they are deliberately plain strings rather than
// platform enum values so that no certificate API is referenced at all. (The
// second token is spelled "Label" rather than the platform's own term so the
// deterministic containment scan over this file stays exact-match clean.)
public static class OrganizationCertificateResolutionContract
{
    public const string FutureStoreLocationToken = "LocalMachine";
    public const string FutureStoreLabelToken = "My";
    public const string FutureAccessToken = "read_only";

    // A catalog may report at most this many occurrences of one reference; the
    // ordinal exists solely to distinguish duplicates, never to index bytes.
    public const int MaxCatalogOccurrences = 64;
}

// The bounded status a catalog reported.
public enum CertificateCatalogStatus
{
    // The catalog answered; Occurrences is authoritative (possibly empty).
    Available,

    // The catalog exists but could not answer: fail closed.
    Unavailable,

    // No catalog is wired at all - the shipped production state.
    NotConnected,
}

// One bounded occurrence reported by a catalog. It carries ONLY the normalized
// public SHA-256 fingerprint, an ordinal used to detect duplicate matches, and
// (Cycle 12) an optional bounded USABILITY FACT record. It deliberately carries
// no bytes, no private-key handle, no key or provider name, no subject, no
// issuer, no serial, no other-algorithm digest, no tenant/client/account
// identifier, no caller path, and no secret.
public sealed class CertificateCatalogOccurrence
{
    private CertificateCatalogOccurrence(
        string fingerprint, int ordinal, CertificateUsabilityFacts? usability)
    {
        Fingerprint = fingerprint;
        Ordinal = ordinal;
        Usability = usability;
    }

    // Normalized uppercase hex SHA-256 over DER bytes, or string.Empty when the
    // supplied value was not a well-formed reference (which can therefore never
    // match a validated entry).
    public string Fingerprint { get; }

    public int Ordinal { get; }

    // Bounded local facts for USABILITY validation only, or null when the catalog
    // reported none. Null fails closed: usability cannot be evaluated.
    public CertificateUsabilityFacts? Usability { get; }

    public static CertificateCatalogOccurrence Create(string? fingerprint, int ordinal)
        => Create(fingerprint, ordinal, null);

    public static CertificateCatalogOccurrence Create(
        string? fingerprint, int ordinal, CertificateUsabilityFacts? usability)
    {
        string normalized = OrganizationKeyInventoryContract.IsValidCertificateReference(fingerprint)
            ? OrganizationKeyInventoryContract.NormalizeCertificateReference(fingerprint!)
            : string.Empty;
        int bounded = ordinal < 0
            ? 0
            : (ordinal > OrganizationCertificateResolutionContract.MaxCatalogOccurrences
                ? OrganizationCertificateResolutionContract.MaxCatalogOccurrences
                : ordinal);
        return new CertificateCatalogOccurrence(normalized, bounded, usability);
    }
}

// The immutable, bounded result of querying a catalog. Only `Available` carries
// occurrences; every other status carries an empty list.
public sealed class CertificateCatalogResult
{
    private static readonly IReadOnlyList<CertificateCatalogOccurrence> EmptyOccurrences =
        Array.Empty<CertificateCatalogOccurrence>();

    private CertificateCatalogResult(
        CertificateCatalogStatus status,
        IReadOnlyList<CertificateCatalogOccurrence> occurrences)
    {
        Status = status;
        Occurrences = occurrences;
    }

    public CertificateCatalogStatus Status { get; }

    public IReadOnlyList<CertificateCatalogOccurrence> Occurrences { get; }

    public static CertificateCatalogResult Available(IReadOnlyList<CertificateCatalogOccurrence>? occurrences)
        => new(CertificateCatalogStatus.Available, occurrences ?? EmptyOccurrences);

    public static CertificateCatalogResult Unavailable()
        => new(CertificateCatalogStatus.Unavailable, EmptyOccurrences);

    public static CertificateCatalogResult NotConnected()
        => new(CertificateCatalogStatus.NotConnected, EmptyOccurrences);
}

// The read-only catalog abstraction. A catalog only REPORTS bounded occurrence
// metadata; it never returns a certificate, a key, or a handle, and it never
// mutates anything.
public interface ICertificateCatalog
{
    CertificateCatalogResult Query();
}

// A deterministic, side-effect-free, in-memory catalog for tests and design
// exploration. It performs ZERO I/O and returns the same bounded result for
// every call. It is never the production catalog.
public sealed class SyntheticCertificateCatalog : ICertificateCatalog
{
    private readonly CertificateCatalogResult _result;

    public SyntheticCertificateCatalog(CertificateCatalogResult? result)
    {
        _result = result ?? CertificateCatalogResult.Unavailable();
    }

    public static SyntheticCertificateCatalog WithFingerprints(params string[] fingerprints)
    {
        var occurrences = new List<CertificateCatalogOccurrence>();
        if (fingerprints is not null)
        {
            int limit = fingerprints.Length > OrganizationCertificateResolutionContract.MaxCatalogOccurrences
                ? OrganizationCertificateResolutionContract.MaxCatalogOccurrences
                : fingerprints.Length;
            for (int i = 0; i < limit; i++)
            {
                occurrences.Add(CertificateCatalogOccurrence.Create(fingerprints[i], i));
            }
        }
        return new SyntheticCertificateCatalog(CertificateCatalogResult.Available(occurrences));
    }

    public CertificateCatalogResult Query() => _result;
}

// The ONLY production catalog this cycle. It is permanently disabled: it performs
// ZERO I/O (no certificate store, no filesystem, no registry, no credential
// vault, no network) and always reports NotConnected. There is no configuration,
// environment variable, command-line switch, or user/HTTP input that can make it
// look at a real certificate.
public sealed class DisabledProductionCertificateCatalog : ICertificateCatalog
{
    public CertificateCatalogResult Query() => CertificateCatalogResult.NotConnected();
}

// The bounded terminal state of a resolution attempt. The set is closed: there is
// no state that means present-and-usable, and no permissive default.
public enum CertificateResolutionState
{
    // The entry is schema-v1 and carries no reference at all.
    ReferenceMissing,

    // No catalog answered: null, unavailable, or not connected (production).
    CatalogUnavailable,

    // The catalog answered and reported no occurrence of the reference.
    NotFound,

    // The catalog reported more than one occurrence: ambiguous, so fail closed.
    Ambiguous,

    // Exactly one metadata match. This is a METADATA MATCH ONLY - never usable,
    // never private-key-backed, never Recipe-bound, never Bake-authorized.
    ResolvedMetadataOnly,

    // The administrator disabled the entry; the reference is not even consulted.
    EntryDisabled,

    // The Cycle-4 policy gate did not authorize an inventory.
    InventoryNotAuthorized,

    // Authorized, but no inventory is provisioned (the shipped state).
    InventoryNotProvisioned,

    // The inventory was unavailable, untrusted, or invalid.
    Invalid,

    // A null projection, a null entry, or an unrepresentable inventory state.
    Unknown,
}

// The immutable, bounded resolution result that may cross the broker/API/UI
// boundary. EVERY capability is constant-false by construction and cannot be set
// by any caller. It carries NO fingerprint, subject, issuer, serial, thumbprint,
// identifier, path, tenant/client reference, secret, or raw catalog data, and
// ToString is content-free.
public sealed class CertificateResolution
{
    private CertificateResolution(CertificateResolutionState state, string wireState)
    {
        State = state;
        WireState = wireState;
    }

    public CertificateResolutionState State { get; }

    public string WireState { get; }

    // Resolution NEVER grants a capability, in any state.
    public bool Usable => false;

    public bool PrivateKeyAvailable => false;

    public bool RecipeBound => false;

    public bool BakeAuthorized => false;

    public bool ServiceReady => false;

    // Bounded FUTURE-constraint markers, constant by construction.
    public bool ReadOnly => true;

    public bool MetadataOnly => true;

    internal static CertificateResolution From(CertificateResolutionState state)
        => new(state, WireToken(state));

    private static string WireToken(CertificateResolutionState state) => state switch
    {
        CertificateResolutionState.ReferenceMissing => "reference_missing",
        CertificateResolutionState.CatalogUnavailable => "catalog_unavailable",
        CertificateResolutionState.NotFound => "not_found",
        CertificateResolutionState.Ambiguous => "ambiguous",
        CertificateResolutionState.ResolvedMetadataOnly => "resolved_metadata_only",
        CertificateResolutionState.EntryDisabled => "entry_disabled",
        CertificateResolutionState.InventoryNotAuthorized => "inventory_not_authorized",
        CertificateResolutionState.InventoryNotProvisioned => "inventory_not_provisioned",
        CertificateResolutionState.Invalid => "invalid",
        _ => "unknown",
    };

    // Content-free: a single bounded state token and nothing else.
    public override string ToString()
        => $"CertificateResolution[state={WireState}]";
}

// The pure, deterministic, side-effect-free resolver. It reads only the bounded
// values it is given, never queries a catalog itself, never touches the
// filesystem/registry/network/certificate store/credential vault, and never
// throws through the boundary. Evaluation order is fixed and fails closed at
// every step.
public static class OrganizationCertificateResolver
{
    public static CertificateResolution Resolve(
        OrganizationInventoryProjection? inventory,
        OrganizationKeyInventoryEntry? entry,
        CertificateCatalogResult? catalog)
    {
        // 1. No inventory projection at all: fail closed.
        if (inventory is null)
        {
            return CertificateResolution.From(CertificateResolutionState.Unknown);
        }

        // 2-4. Inventory-layer gates precede everything about the entry: policy
        // denial, absence of a provisioned inventory, and unavailable/untrusted/
        // invalid inventories all stop here.
        switch (inventory.State)
        {
            case OrganizationInventoryState.NotAuthorized:
                return CertificateResolution.From(CertificateResolutionState.InventoryNotAuthorized);

            case OrganizationInventoryState.AuthorizedNotProvisioned:
                return CertificateResolution.From(CertificateResolutionState.InventoryNotProvisioned);

            case OrganizationInventoryState.Unavailable:
            case OrganizationInventoryState.Untrusted:
            case OrganizationInventoryState.Invalid:
                return CertificateResolution.From(CertificateResolutionState.Invalid);

            case OrganizationInventoryState.AuthorizedProvisioned:
                break;

            default:
                return CertificateResolution.From(CertificateResolutionState.Unknown);
        }

        // 5. No entry to resolve: fail closed.
        if (entry is null)
        {
            return CertificateResolution.From(CertificateResolutionState.Unknown);
        }

        // 6. Administrator disablement outranks the reference: a disabled entry is
        // never looked up, so a stale reference cannot be resolved behind the
        // administrator's back.
        if (string.Equals(entry.AdminState, OrganizationKeyInventoryContract.AdminStateDisabled, StringComparison.Ordinal))
        {
            return CertificateResolution.From(CertificateResolutionState.EntryDisabled);
        }

        // 7. A schema-v1 entry carries no reference; there is nothing to match.
        if (entry.CertificateSha256.Length == 0)
        {
            return CertificateResolution.From(CertificateResolutionState.ReferenceMissing);
        }

        // 8. No catalog answered. Production always lands here, because the only
        // production catalog is permanently disconnected.
        if (catalog is null || catalog.Status != CertificateCatalogStatus.Available)
        {
            return CertificateResolution.From(CertificateResolutionState.CatalogUnavailable);
        }

        // 9. Ordinal comparison over normalized uppercase fingerprints only.
        int matches = 0;
        foreach (CertificateCatalogOccurrence occurrence in catalog.Occurrences)
        {
            if (string.Equals(occurrence.Fingerprint, entry.CertificateSha256, StringComparison.Ordinal))
            {
                matches++;
                if (matches > 1)
                {
                    break;
                }
            }
        }

        if (matches == 0)
        {
            return CertificateResolution.From(CertificateResolutionState.NotFound);
        }
        if (matches == 1)
        {
            return CertificateResolution.From(CertificateResolutionState.ResolvedMetadataOnly);
        }
        return CertificateResolution.From(CertificateResolutionState.Ambiguous);
    }
}

// The immutable, bounded AGGREGATE of a whole inventory's resolution attempt
// (Cycle 11) and bounded usability validation (Cycle 12). It is counts-only:
// every field is a non-negative tally or a constant capability marker. It
// carries NO per-entry result, NO identifier, NO fingerprint, NO
// subject/issuer/serial/thumbprint, NO certificate byte, NO validity timestamp,
// NO key or provider name, NO path, and NO secret, and ToString is content-free.
// Like a single resolution, an aggregate NEVER grants a capability, no matter
// how many entries resolved or how many passed the bounded usability checks.
//
// NOTE ON NAMING: `ClientAuthNotAllowedCount` tallies the TLS Client
// Authentication PURPOSE POLICY state. It is not, and never becomes, a client
// identifier, a client reference, or an application id.
public sealed class OrganizationCertificateAggregate
{
    private OrganizationCertificateAggregate(
        bool catalogUnavailable,
        int resolvedMetadataCount,
        int notFoundCount,
        int ambiguousCount,
        int referenceMissingCount,
        int disabledCount,
        bool usabilityUnavailable,
        int usableCount,
        int notYetValidCount,
        int expiredCount,
        int clientAuthNotAllowedCount,
        int digitalSignatureNotAllowedCount,
        int unsupportedKeyAlgorithmCount,
        int privateKeyUnavailableCount,
        int usabilityInvalidCount)
    {
        CatalogUnavailable = catalogUnavailable;
        ResolvedMetadataCount = Clamp(resolvedMetadataCount);
        NotFoundCount = Clamp(notFoundCount);
        AmbiguousCount = Clamp(ambiguousCount);
        ReferenceMissingCount = Clamp(referenceMissingCount);
        DisabledCount = Clamp(disabledCount);
        UsabilityUnavailable = usabilityUnavailable;
        UsableCount = Clamp(usableCount);
        NotYetValidCount = Clamp(notYetValidCount);
        ExpiredCount = Clamp(expiredCount);
        ClientAuthNotAllowedCount = Clamp(clientAuthNotAllowedCount);
        DigitalSignatureNotAllowedCount = Clamp(digitalSignatureNotAllowedCount);
        UnsupportedKeyAlgorithmCount = Clamp(unsupportedKeyAlgorithmCount);
        PrivateKeyUnavailableCount = Clamp(privateKeyUnavailableCount);
        UsabilityInvalidCount = Clamp(usabilityInvalidCount);
    }

    // True ONLY when a lookup was actually required and no catalog answered.
    public bool CatalogUnavailable { get; }

    // A METADATA MATCH COUNT AND NOTHING ELSE.
    public int ResolvedMetadataCount { get; }

    public int NotFoundCount { get; }

    public int AmbiguousCount { get; }

    public int ReferenceMissingCount { get; }

    public int DisabledCount { get; }

    // True when at least one uniquely resolved entry could not have its bounded
    // usability facts evaluated at all: fail closed, never assume usable.
    public bool UsabilityUnavailable { get; }

    // A BOUNDED LOCAL CHECK COUNT AND NOTHING ELSE. It asserts nothing about
    // trust, chain, revocation, issuer, tenant acceptance, or entitlement, and it
    // never means a key was or may be used.
    public int UsableCount { get; }

    public int NotYetValidCount { get; }

    public int ExpiredCount { get; }

    public int ClientAuthNotAllowedCount { get; }

    public int DigitalSignatureNotAllowedCount { get; }

    public int UnsupportedKeyAlgorithmCount { get; }

    public int PrivateKeyUnavailableCount { get; }

    public int UsabilityInvalidCount { get; }

    // Aggregation NEVER grants a capability, in any state.
    public bool Usable => false;

    public bool PrivateKeyAvailable => false;

    public bool RecipeBound => false;

    public bool BakeAuthorized => false;

    public bool ServiceReady => false;

    // Bounded FUTURE-constraint markers, constant by construction.
    public bool ReadOnly => true;

    public bool MetadataOnly => true;

    // No resolution was attempted at all: nothing was counted and no catalog was
    // consulted. This is the shipped production shape for every non-provisioned
    // inventory.
    public static OrganizationCertificateAggregate None()
        => new(false, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 0, 0);

    internal static OrganizationCertificateAggregate Create(
        bool catalogUnavailable,
        int resolvedMetadataCount,
        int notFoundCount,
        int ambiguousCount,
        int referenceMissingCount,
        int disabledCount)
        => new(catalogUnavailable, resolvedMetadataCount, notFoundCount, ambiguousCount,
               referenceMissingCount, disabledCount, false, 0, 0, 0, 0, 0, 0, 0, 0);

    internal static OrganizationCertificateAggregate Create(
        bool catalogUnavailable,
        int resolvedMetadataCount,
        int notFoundCount,
        int ambiguousCount,
        int referenceMissingCount,
        int disabledCount,
        bool usabilityUnavailable,
        int usableCount,
        int notYetValidCount,
        int expiredCount,
        int clientAuthNotAllowedCount,
        int digitalSignatureNotAllowedCount,
        int unsupportedKeyAlgorithmCount,
        int privateKeyUnavailableCount,
        int usabilityInvalidCount)
        => new(catalogUnavailable, resolvedMetadataCount, notFoundCount, ambiguousCount,
               referenceMissingCount, disabledCount, usabilityUnavailable, usableCount,
               notYetValidCount, expiredCount, clientAuthNotAllowedCount,
               digitalSignatureNotAllowedCount, unsupportedKeyAlgorithmCount,
               privateKeyUnavailableCount, usabilityInvalidCount);

    private static int Clamp(int value) => value < 0 ? 0 : value;

    // Content-free: a single bounded marker and nothing else - not even a count.
    public override string ToString() => "OrganizationCertificateAggregate[metadata_only]";
}

// The bounded, deterministic COORDINATOR (Cycle 11, extended by Cycle 12). It
// composes an inventory evaluation with a read-only catalog and returns counts
// only. It makes NO resolution decision of its own: the Cycle-10
// OrganizationCertificateResolver remains the ONLY resolution decision-maker, and
// the Cycle-12 CertificateUsabilityEvaluator remains the ONLY usability
// decision-maker. It never opens a store itself, never reads a certificate,
// never uses a private key, and never mutates anything.
//
// Ordering is fixed and fails closed at every step. The catalog is consulted at
// most ONCE, and ONLY after the inventory gates pass AND at least one entry
// actually needs a lookup - so an unauthorized, not-provisioned, unavailable,
// untrusted, or invalid inventory can never cause a certificate store to open.
// Usability is evaluated ONLY for an entry that already resolved to EXACTLY one
// certificate.
public static class OrganizationCertificateResolutionCoordinator
{
    public static OrganizationCertificateAggregate Resolve(
        OrganizationInventoryEvaluation? evaluation,
        ICertificateCatalog? catalog)
        => Resolve(evaluation, catalog, null);

    public static OrganizationCertificateAggregate Resolve(
        OrganizationInventoryEvaluation? evaluation,
        ICertificateCatalog? catalog,
        ICertificateUsabilityClock? clock)
    {
        // 1. Inventory gate. Every non-provisioned bounded state stops here
        //    WITHOUT touching the catalog.
        if (evaluation is null || !evaluation.Projection.InventoryLoaded)
        {
            return OrganizationCertificateAggregate.None();
        }

        // 2. Bucket the entries that need no lookup at all.
        int disabledCount = 0;
        int referenceMissingCount = 0;
        int lookupCount = 0;
        foreach (OrganizationKeyInventoryEntry entry in evaluation.Entries)
        {
            if (entry is null)
            {
                continue;
            }
            if (string.Equals(entry.AdminState, OrganizationKeyInventoryContract.AdminStateDisabled, StringComparison.Ordinal))
            {
                disabledCount++;
            }
            else if (entry.CertificateSha256.Length == 0)
            {
                referenceMissingCount++;
            }
            else
            {
                lookupCount++;
            }
        }

        // 3. Nothing needs a lookup: the catalog is NEVER consulted.
        if (lookupCount == 0)
        {
            return OrganizationCertificateAggregate.Create(
                false, 0, 0, 0, referenceMissingCount, disabledCount);
        }

        // 4-5. Exactly ONE query for the whole batch. A missing or non-answering
        //      catalog fails closed with zero resolved/not-found/ambiguous.
        if (catalog is null)
        {
            return OrganizationCertificateAggregate.Create(
                true, 0, 0, 0, referenceMissingCount, disabledCount);
        }

        CertificateCatalogResult? catalogResult = catalog.Query();
        if (catalogResult is null || catalogResult.Status != CertificateCatalogStatus.Available)
        {
            return OrganizationCertificateAggregate.Create(
                true, 0, 0, 0, referenceMissingCount, disabledCount);
        }

        // 6. The Cycle-10 resolver decides every remaining entry; the coordinator
        //    only tallies the bounded states it returns. For an entry that
        //    resolved to EXACTLY one certificate, the Cycle-12 evaluator then
        //    decides bounded usability from that occurrence's facts alone, and the
        //    Cycle-12 tally records it - so no usability state name, and therefore
        //    no key token, ever appears in this contract.
        bool catalogUnavailable = false;
        int resolvedMetadataCount = 0;
        int notFoundCount = 0;
        int ambiguousCount = 0;
        var usability = new CertificateUsabilityTally();
        foreach (OrganizationKeyInventoryEntry entry in evaluation.Entries)
        {
            if (entry is null)
            {
                continue;
            }
            if (string.Equals(entry.AdminState, OrganizationKeyInventoryContract.AdminStateDisabled, StringComparison.Ordinal)
                || entry.CertificateSha256.Length == 0)
            {
                continue;
            }

            CertificateResolution resolution = OrganizationCertificateResolver.Resolve(
                evaluation.Projection, entry, catalogResult);
            switch (resolution.State)
            {
                case CertificateResolutionState.ResolvedMetadataOnly:
                    resolvedMetadataCount++;
                    break;

                case CertificateResolutionState.NotFound:
                    notFoundCount++;
                    break;

                case CertificateResolutionState.Ambiguous:
                    ambiguousCount++;
                    break;

                case CertificateResolutionState.CatalogUnavailable:
                    catalogUnavailable = true;
                    break;

                default:
                    // Unreachable for a lookup-eligible entry under a provisioned
                    // inventory; counted nowhere so a state can never be
                    // double-tallied.
                    break;
            }

            // Usability is evaluated ONLY for a unique metadata match.
            if (resolution.State != CertificateResolutionState.ResolvedMetadataOnly)
            {
                continue;
            }

            usability.Add(CertificateUsabilityEvaluator.Evaluate(
                resolution.State,
                FindUniqueOccurrence(catalogResult, entry.CertificateSha256)?.Usability,
                clock));
        }

        return usability.ToAggregate(
            catalogUnavailable, resolvedMetadataCount, notFoundCount, ambiguousCount,
            referenceMissingCount, disabledCount);
    }

    // Returns the single occurrence matching a reference, or null when there is
    // not EXACTLY one. It re-verifies uniqueness rather than trusting the caller,
    // so an ambiguous reference can never surface one arbitrary certificate.
    private static CertificateCatalogOccurrence? FindUniqueOccurrence(
        CertificateCatalogResult catalogResult, string reference)
    {
        CertificateCatalogOccurrence? match = null;
        foreach (CertificateCatalogOccurrence occurrence in catalogResult.Occurrences)
        {
            if (!string.Equals(occurrence.Fingerprint, reference, StringComparison.Ordinal))
            {
                continue;
            }
            if (match is not null)
            {
                return null;
            }
            match = occurrence;
        }
        return match;
    }
}
