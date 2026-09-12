using System;
using System.Collections.Generic;

namespace PAXCookbook.Shared.Contracts;

// Canonical, cross-component contract for BOUNDED CERTIFICATE USABILITY
// VALIDATION (Cycle 12).
//
// Cycle 10 defined certificate REFERENCE RESOLUTION and Cycle 11 supplied the
// read-only machine catalog that answers it. This contract answers the strictly
// narrower follow-on question for an entry that already resolved to EXACTLY ONE
// certificate: could that certificate, LOCALLY and WITHOUT USING IT, plausibly
// serve as an app-registration certificate credential? It evaluates five bounded
// local facts only - validity window, TLS Client Authentication EKU, Digital
// Signature key usage, supported key algorithm, and private-key AVAILABILITY -
// and returns one bounded state.
//
// USABILITY IS NOT USE. Nothing here ever signs, verifies with a key, encrypts,
// decrypts, exports, caches, or persists a key; nothing builds a chain, checks
// revocation, or evaluates issuer trust; nothing contacts a tenant, Graph, a
// service, WAM/Hello, or the network; nothing binds a Recipe, adds a Cook step,
// runs PAX, or performs a Bake. `Usable` means ONLY that the bounded local
// checks passed.
//
// SINGLE SOURCE OF TRUTH. Compiled into PAXCookbook.Shared (used by Setup) AND
// linkable into the native host via the established <Compile Include=... Link=...>
// pattern. It depends only on System and System.Collections.Generic - NO Win32,
// NO cryptography API, NO certificate API, NO network, NO filesystem. FACTS IN,
// BOUNDED STATE OUT: no certificate, key, handle, byte, or identifier can even be
// expressed here.
//
// FAIL-CLOSED DOCTRINE (binding, and DELIBERATELY stricter than RFC 5280): an
// ABSENT Extended Key Usage extension and an ABSENT Key Usage extension are both
// REFUSALS, not permissions. RFC 5280 treats an absent EKU as "valid for all
// purposes"; this product does not, because a capability must never be granted by
// omission. A malformed or DUPLICATED extension, an unclassifiable algorithm, an
// inaccessible key, and any extraction failure are all refusals too. There is no
// permissive fallback and no state that grants a capability.

// Bounded tokens and limits for usability validation. The OID is recorded here
// once so no component re-spells it.
public static class OrganizationCertificateUsabilityContract
{
    // TLS Web Client Authentication (RFC 5280 id-kp-clientAuth). This names a
    // certificate PURPOSE POLICY. It is not, and never becomes, a client
    // identifier, a client reference, or an application id.
    public const string ClientAuthenticationPurposeOid = "1.3.6.1.5.5.7.3.2";

    // A certificate may declare at most this many bounded purpose OIDs; anything
    // beyond the bound is refused rather than truncated.
    public const int MaxPurposeOids = 64;
}

// How the certificate's PUBLIC key algorithm classifies. Classification happens
// BEFORE any private-key probe, so an unsupported algorithm is reported as such
// rather than mis-reported as a missing key. The supported set is EXACTLY RSA and
// ECDSA.
public enum CertificateKeyAlgorithmClass
{
    // Not determined; fail closed.
    Unknown,

    Rsa,

    Ecdsa,

    // Classified, but outside the supported set.
    Unsupported,
}

// Whether a private key for the certificate is AVAILABLE. This records
// availability ONLY - it is never a handle, a key, a container name, or a
// provider name, and it never implies the key was or may be used.
public enum CertificateKeyAvailability
{
    // Not determined; fail closed.
    Unknown,

    // A supported private key object was obtainable and was disposed immediately.
    Available,

    // Absent, of an unsupported type, or inaccessible.
    Unavailable,
}

// The bounded terminal state of a usability evaluation. The set is CLOSED at
// exactly eleven values: there is no permissive default and no state that grants
// a capability beyond the bounded local checks.
public enum CertificateUsabilityState
{
    // The reference never resolved to exactly one certificate, so usability was
    // not evaluated at all.
    NotResolved,

    // No catalog answered, so usability could not be evaluated.
    CatalogUnavailable,

    // The current instant precedes the validity window.
    NotYetValid,

    // The current instant is at or after the end of the validity window.
    Expired,

    // The TLS Client Authentication purpose is not permitted - including when the
    // extension is ABSENT entirely (fail closed).
    ClientAuthNotAllowed,

    // Digital Signature key usage is not permitted - including when the extension
    // is ABSENT entirely (fail closed).
    DigitalSignatureNotAllowed,

    // No supported private key is available: absent, of an unsupported type, or
    // inaccessible.
    PrivateKeyUnavailable,

    // The public key algorithm is outside the supported RSA/ECDSA set.
    UnsupportedKeyAlgorithm,

    // Every bounded LOCAL check passed. Nothing more. Never Recipe-bound, never
    // Bake-authorized, never service-ready.
    Usable,

    // Malformed, duplicated, or unparseable bounded input: fail closed.
    Invalid,

    // Missing facts, missing clock, or an unrepresentable state: fail closed.
    Unknown,
}

// The current instant, INJECTED. The evaluator never reads the system clock
// itself, so validity evaluation is deterministic and testable.
public interface ICertificateUsabilityClock
{
    DateTimeOffset UtcNow { get; }
}

// The production clock.
public sealed class SystemCertificateUsabilityClock : ICertificateUsabilityClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

// A deterministic, side-effect-free clock for tests and design exploration.
public sealed class FixedCertificateUsabilityClock : ICertificateUsabilityClock
{
    public FixedCertificateUsabilityClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow.ToUniversalTime();
    }

    public DateTimeOffset UtcNow { get; }
}

// The bounded FACTS extracted from ONE certificate. This is the ONLY thing that
// crosses into the evaluator. It carries a validity window, a normalized purpose
// OID list, bounded key-usage booleans, an algorithm classification, and an
// availability verdict - and DELIBERATELY carries no certificate, no key, no
// handle, no byte, no subject, no issuer, no serial, no thumbprint, no
// fingerprint, no store path, no key-container or provider name, and no
// tenant/client identifier.
public sealed class CertificateUsabilityFacts
{
    private static readonly IReadOnlyList<string> EmptyOids = Array.Empty<string>();

    private CertificateUsabilityFacts(
        bool extractionFailed,
        DateTimeOffset notValidBeforeUtc,
        DateTimeOffset notValidAfterUtc,
        bool purposeExtensionPresent,
        bool purposeExtensionMalformed,
        IReadOnlyList<string> purposeOids,
        bool keyUsageExtensionPresent,
        bool keyUsageExtensionMalformed,
        bool digitalSignatureAllowed,
        CertificateKeyAlgorithmClass keyAlgorithm,
        CertificateKeyAvailability keyAvailability)
    {
        ExtractionFailed = extractionFailed;
        NotValidBeforeUtc = notValidBeforeUtc;
        NotValidAfterUtc = notValidAfterUtc;
        PurposeExtensionPresent = purposeExtensionPresent;
        PurposeExtensionMalformed = purposeExtensionMalformed;
        PurposeOids = purposeOids;
        KeyUsageExtensionPresent = keyUsageExtensionPresent;
        KeyUsageExtensionMalformed = keyUsageExtensionMalformed;
        DigitalSignatureAllowed = digitalSignatureAllowed;
        KeyAlgorithm = keyAlgorithm;
        KeyAvailability = keyAvailability;
    }

    // The bounded facts could not be read at all; every downstream check fails
    // closed.
    public bool ExtractionFailed { get; }

    public DateTimeOffset NotValidBeforeUtc { get; }

    public DateTimeOffset NotValidAfterUtc { get; }

    public bool PurposeExtensionPresent { get; }

    // Malformed OR declared more than once. RFC 5280 permits each extension at
    // most once, so a duplicate is malformed.
    public bool PurposeExtensionMalformed { get; }

    // Normalized, bounded purpose OID strings ONLY.
    public IReadOnlyList<string> PurposeOids { get; }

    public bool KeyUsageExtensionPresent { get; }

    public bool KeyUsageExtensionMalformed { get; }

    public bool DigitalSignatureAllowed { get; }

    public CertificateKeyAlgorithmClass KeyAlgorithm { get; }

    public CertificateKeyAvailability KeyAvailability { get; }

    public static CertificateUsabilityFacts Create(
        DateTimeOffset notValidBeforeUtc,
        DateTimeOffset notValidAfterUtc,
        CertificateKeyAlgorithmClass keyAlgorithm,
        CertificateKeyAvailability keyAvailability,
        bool purposeExtensionPresent = false,
        IReadOnlyList<string>? purposeOids = null,
        bool purposeExtensionMalformed = false,
        bool keyUsageExtensionPresent = false,
        bool digitalSignatureAllowed = false,
        bool keyUsageExtensionMalformed = false)
    {
        bool malformedPurpose = purposeExtensionMalformed;
        IReadOnlyList<string> normalized = EmptyOids;
        if (purposeOids is not null && purposeOids.Count > 0)
        {
            // Above the bound the list is REFUSED as malformed rather than
            // truncated, because a truncated list could hide a refusal.
            if (purposeOids.Count > OrganizationCertificateUsabilityContract.MaxPurposeOids)
            {
                malformedPurpose = true;
            }
            else
            {
                var bounded = new string[purposeOids.Count];
                for (int i = 0; i < purposeOids.Count; i++)
                {
                    bounded[i] = purposeOids[i] ?? string.Empty;
                }
                normalized = bounded;
            }
        }

        return new CertificateUsabilityFacts(
            false,
            notValidBeforeUtc.ToUniversalTime(),
            notValidAfterUtc.ToUniversalTime(),
            purposeExtensionPresent,
            malformedPurpose,
            normalized,
            keyUsageExtensionPresent,
            keyUsageExtensionMalformed,
            digitalSignatureAllowed,
            keyAlgorithm,
            keyAvailability);
    }

    // The bounded facts could not be read; everything downstream fails closed.
    public static CertificateUsabilityFacts ExtractionUnavailable()
        => new(
            true,
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue,
            false,
            false,
            EmptyOids,
            false,
            false,
            false,
            CertificateKeyAlgorithmClass.Unknown,
            CertificateKeyAvailability.Unknown);

    // Content-free: never a window, an OID, or an availability verdict.
    public override string ToString() => "CertificateUsabilityFacts[bounded]";
}

// The immutable, bounded usability result that may cross the broker/API/UI
// boundary. It carries NO fingerprint, subject, issuer, serial, thumbprint,
// identifier, path, key name, provider name, secret, or raw certificate data, and
// ToString is content-free.
public sealed class CertificateUsability
{
    private CertificateUsability(CertificateUsabilityState state, string wireState)
    {
        State = state;
        WireState = wireState;
    }

    public CertificateUsabilityState State { get; }

    public string WireState { get; }

    // TRUE only when every bounded LOCAL check passed. It asserts nothing about
    // trust, chain, revocation, issuer, tenant acceptance, or entitlement.
    public bool Usable => State == CertificateUsabilityState.Usable;

    // Usability NEVER grants a downstream capability, in any state.
    public bool RecipeBound => false;

    public bool BakeAuthorized => false;

    public bool ServiceReady => false;

    // Bounded constraint markers, constant by construction.
    public bool ReadOnly => true;

    public bool KeyNeverUsed => true;

    internal static CertificateUsability From(CertificateUsabilityState state)
        => new(state, WireToken(state));

    private static string WireToken(CertificateUsabilityState state) => state switch
    {
        CertificateUsabilityState.NotResolved => "not_resolved",
        CertificateUsabilityState.CatalogUnavailable => "catalog_unavailable",
        CertificateUsabilityState.NotYetValid => "not_yet_valid",
        CertificateUsabilityState.Expired => "expired",
        CertificateUsabilityState.ClientAuthNotAllowed => "client_auth_not_allowed",
        CertificateUsabilityState.DigitalSignatureNotAllowed => "digital_signature_not_allowed",
        CertificateUsabilityState.PrivateKeyUnavailable => "private_key_unavailable",
        CertificateUsabilityState.UnsupportedKeyAlgorithm => "unsupported_key_algorithm",
        CertificateUsabilityState.Usable => "usable",
        CertificateUsabilityState.Invalid => "invalid",
        _ => "unknown",
    };

    // Content-free: a single bounded state token and nothing else.
    public override string ToString() => $"CertificateUsability[state={WireState}]";
}

// The PURE, deterministic, side-effect-free usability evaluator. It reads only
// the bounded facts and the injected clock it is given; it never opens a store,
// reads a certificate, probes a key, signs, decrypts, exports, builds a chain,
// checks revocation, touches the filesystem/registry/network, or throws through
// the boundary.
//
// Evaluation ORDER is fixed and FIRST MATCH WINS:
//   skip states -> validity -> purpose (EKU) -> key usage -> algorithm ->
//   private key -> Usable.
public static class CertificateUsabilityEvaluator
{
    public static CertificateUsability Evaluate(
        CertificateResolutionState resolutionState,
        CertificateUsabilityFacts? facts,
        ICertificateUsabilityClock? clock)
    {
        // 1. Usability is evaluated ONLY for a reference that resolved to EXACTLY
        //    one certificate. Every other resolution state - missing reference,
        //    not found, ambiguous, disabled entry, or any inventory-layer refusal
        //    - skips usability entirely.
        if (resolutionState == CertificateResolutionState.CatalogUnavailable)
        {
            return CertificateUsability.From(CertificateUsabilityState.CatalogUnavailable);
        }
        if (resolutionState != CertificateResolutionState.ResolvedMetadataOnly)
        {
            return CertificateUsability.From(CertificateUsabilityState.NotResolved);
        }

        // 2. Missing facts or a missing clock are refusals, never assumptions.
        if (facts is null || clock is null)
        {
            return CertificateUsability.From(CertificateUsabilityState.Unknown);
        }
        if (facts.ExtractionFailed)
        {
            return CertificateUsability.From(CertificateUsabilityState.Invalid);
        }

        // 3. Validity window, compared in UTC against the INJECTED instant. At
        //    NotBefore the certificate is valid; at NotAfter it is expired.
        DateTimeOffset now = clock.UtcNow.ToUniversalTime();
        if (facts.NotValidAfterUtc <= facts.NotValidBeforeUtc)
        {
            return CertificateUsability.From(CertificateUsabilityState.Invalid);
        }
        if (now < facts.NotValidBeforeUtc)
        {
            return CertificateUsability.From(CertificateUsabilityState.NotYetValid);
        }
        if (now >= facts.NotValidAfterUtc)
        {
            return CertificateUsability.From(CertificateUsabilityState.Expired);
        }

        // 4. Purpose. A malformed or duplicated extension is Invalid; an ABSENT
        //    extension is a REFUSAL, deliberately unlike RFC 5280's "absent means
        //    all purposes", because capability is never granted by omission.
        if (facts.PurposeExtensionMalformed)
        {
            return CertificateUsability.From(CertificateUsabilityState.Invalid);
        }
        if (!facts.PurposeExtensionPresent || !ContainsClientAuthentication(facts.PurposeOids))
        {
            return CertificateUsability.From(CertificateUsabilityState.ClientAuthNotAllowed);
        }

        // 5. Key usage. Same doctrine: malformed is Invalid, and an ABSENT
        //    extension is a REFUSAL rather than an unrestricted grant.
        if (facts.KeyUsageExtensionMalformed)
        {
            return CertificateUsability.From(CertificateUsabilityState.Invalid);
        }
        if (!facts.KeyUsageExtensionPresent || !facts.DigitalSignatureAllowed)
        {
            return CertificateUsability.From(CertificateUsabilityState.DigitalSignatureNotAllowed);
        }

        // 6. Algorithm BEFORE key availability, so an unsupported algorithm is
        //    reported as such instead of as a missing key.
        if (facts.KeyAlgorithm != CertificateKeyAlgorithmClass.Rsa
            && facts.KeyAlgorithm != CertificateKeyAlgorithmClass.Ecdsa)
        {
            return CertificateUsability.From(CertificateUsabilityState.UnsupportedKeyAlgorithm);
        }

        // 7. Private-key AVAILABILITY only. Absent, unsupported, inaccessible, or
        //    undetermined all refuse.
        if (facts.KeyAvailability != CertificateKeyAvailability.Available)
        {
            return CertificateUsability.From(CertificateUsabilityState.PrivateKeyUnavailable);
        }

        // 8. Every bounded LOCAL check passed. Nothing more is claimed.
        return CertificateUsability.From(CertificateUsabilityState.Usable);
    }

    private static bool ContainsClientAuthentication(IReadOnlyList<string> purposeOids)
    {
        for (int i = 0; i < purposeOids.Count; i++)
        {
            if (string.Equals(
                    purposeOids[i],
                    OrganizationCertificateUsabilityContract.ClientAuthenticationPurposeOid,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}

// A bounded, additive TALLY of usability outcomes. It exists so that usability
// state names live ONLY beside the usability evaluator: the Cycle-10 resolution
// contract stays literally free of every private-key token, exactly as its
// containment doctrine requires. It holds counts and one refusal flag - never a
// per-entry result, an identifier, a timestamp, a certificate, or a key.
public sealed class CertificateUsabilityTally
{
    private bool _unavailable;
    private int _usable;
    private int _notYetValid;
    private int _expired;
    private int _clientAuthNotAllowed;
    private int _digitalSignatureNotAllowed;
    private int _unsupportedKeyAlgorithm;
    private int _keyUnavailable;
    private int _invalid;

    // A null result, or any state that means "could not be evaluated", sets the
    // refusal flag instead of being counted as an outcome.
    public void Add(CertificateUsability? usability)
    {
        switch (usability?.State)
        {
            case CertificateUsabilityState.Usable:
                _usable++;
                break;

            case CertificateUsabilityState.NotYetValid:
                _notYetValid++;
                break;

            case CertificateUsabilityState.Expired:
                _expired++;
                break;

            case CertificateUsabilityState.ClientAuthNotAllowed:
                _clientAuthNotAllowed++;
                break;

            case CertificateUsabilityState.DigitalSignatureNotAllowed:
                _digitalSignatureNotAllowed++;
                break;

            case CertificateUsabilityState.UnsupportedKeyAlgorithm:
                _unsupportedKeyAlgorithm++;
                break;

            case CertificateUsabilityState.PrivateKeyUnavailable:
                _keyUnavailable++;
                break;

            case CertificateUsabilityState.Invalid:
                _invalid++;
                break;

            default:
                _unavailable = true;
                break;
        }
    }

    // Folds the bounded resolution counts and the bounded usability counts into
    // the single counts-only aggregate that may cross the boundary.
    internal OrganizationCertificateAggregate ToAggregate(
        bool catalogUnavailable,
        int resolvedMetadataCount,
        int notFoundCount,
        int ambiguousCount,
        int referenceMissingCount,
        int disabledCount)
        => OrganizationCertificateAggregate.Create(
            catalogUnavailable, resolvedMetadataCount, notFoundCount, ambiguousCount,
            referenceMissingCount, disabledCount, _unavailable, _usable, _notYetValid,
            _expired, _clientAuthNotAllowed, _digitalSignatureNotAllowed,
            _unsupportedKeyAlgorithm, _keyUnavailable, _invalid);
}
