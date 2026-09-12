using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.App;

// Canonical contract for the ORGANIZATION-KEY RECIPE BINDING (Cycle 14).
//
// A Recipe may reference ONE organization-provided key through an OPAQUE
// IDENTIFIER AND NOTHING ELSE. This file defines that reference, its bounded
// readiness state model, and the pure evaluator that decides between them. It
// does NOT: open a certificate store, read or resolve a certificate, touch a
// private key, sign, decrypt, export, import, delete, read a secret, reach a
// Windows Credential Manager target, query a tenant, query Microsoft Graph,
// acquire a token, use WAM/Windows Hello, contact a service, mutate ProgramData,
// inject a Cook credential, run PAX, or perform a Bake. It performs ZERO I/O:
// every input is injected and every output is a bounded token.
//
// LAYERED SEPARATION (being at layer N never implies layer N+1):
//   1 policy authorization        — Cycle 4 gate.
//   2 inventory shape/source      — Cycle 5 parser + source boundary.
//   3 inventory availability      — a PROVISIONED document.
//   4 certificate resolution      — Cycle 10/11 resolver + coordinator.
//   5 certificate usability       — Cycle 12 bounded local checks.
//   6 RECIPE BINDING READINESS    — THIS cycle, and it stops here.
//   7 Bake authorization / service readiness / authentication — NEVER true.
// `organization_key_ready` means ONLY that the Recipe binding is LOCALLY READY
// for a FUTURE Cook integration. It never means Bake-authorized, service-ready,
// authentication-verified, or execution-tested; those markers are constant-false
// by construction and no caller can set them.
//
// FAIL-CLOSED DOCTRINE (binding): a malformed identifier, two competing
// bindings, an unsupported auth mode, a missing evaluation, an unauthorized or
// non-provisioned inventory, an unknown id, a duplicate id, a disabled entry, a
// missing certificate reference, an unavailable catalog, zero matches, more than
// one match, and any non-usable certificate ALL resolve to a bounded non-ready
// state. There is no permissive fallback and no state that grants a capability.
//
// NON-LEAKAGE (binding): the identifier is carried IN-PROCESS only. It is never
// projected onto the wire, never rendered in visible copy, never written to the
// command preview or PAX argv, never logged, and never included in ToString.

// The immutable, bounded description of a Recipe's key binding. It is produced
// only from already-validated recipe content and carries the opaque identifier
// as an INTERNAL value so it cannot cross a serialization boundary.
internal sealed class OrganizationKeyRecipeBinding
{
    // Mirrors the inventory contract's identifier charset (letters, digits, '.',
    // '_', '-') and its 128-character bound. It deliberately EXCLUDES ':' so an
    // organization id can never masquerade as a personal Windows-Credential-
    // Manager target, and it is deliberately NOT the personal chefKeyId pattern.
    internal const string IdPattern = OrganizationKeyIdentifierPattern.Value;

    // Organization origin is certificate-only: every other sign-in mode refuses.
    internal const string RequiredAuthMode = "AppRegistrationCertificate";

    private OrganizationKeyRecipeBinding(
        bool hasPersonalBinding, bool hasOrganizationBinding, string organizationKeyId, string authMode)
    {
        HasPersonalBinding = hasPersonalBinding;
        HasOrganizationBinding = hasOrganizationBinding;
        OrganizationKeyId = organizationKeyId;
        AuthMode = authMode;
    }

    internal bool HasPersonalBinding { get; }

    // True when the recipe named an organization key at all, VALID OR NOT.
    internal bool HasOrganizationBinding { get; }

    // Opaque, in-process only. Internal so it cannot leak onto the wire.
    internal string OrganizationKeyId { get; }

    internal string AuthMode { get; }

    internal static OrganizationKeyRecipeBinding Create(
        string? authMode, string? chefKeyId, string? organizationKeyId)
    {
        string orgId = (organizationKeyId ?? string.Empty).Trim();
        return new OrganizationKeyRecipeBinding(
            !string.IsNullOrWhiteSpace(chefKeyId),
            (organizationKeyId ?? string.Empty).Length > 0,
            orgId,
            (authMode ?? string.Empty).Trim());
    }

    // Charset/length only. It asserts nothing about existence or authorization.
    internal bool IdWellFormed()
    {
        if (OrganizationKeyId.Length == 0
            || OrganizationKeyId.Length > OrganizationKeyInventoryContract.MaxStringLength)
        {
            return false;
        }
        return OrganizationKeyInventoryContract.IsValidIdCharset(OrganizationKeyId);
    }

    // Content-free: never the identifier, the auth mode, or any reference.
    public override string ToString() => "OrganizationKeyRecipeBinding[bounded]";
}

// Cycle 14s — the RUNNABILITY contract for an organization-bound Recipe.
//
// The immutable PAX engine selects a certificate ONLY by SHA-1 thumbprint, so an
// organization binding CANNOT be executed. This layer therefore adds a THIRTEENTH
// bounded fact that sits ABOVE binding readiness and is CONSTANT: an
// organization-bound Recipe is ALWAYS `organization_key_not_yet_runnable`. Local
// binding readiness (`organization_key_ready`) says the binding is set up on this
// PC; it NEVER means runnable, and no state, policy, setting, request, or
// inventory content can make it runnable.
//
// The copy below is the ONLY customer-visible wording for this state. It never
// names SHA-1, SHA-256, a thumbprint, a certificate store, a fallback, or any
// other engine internal; it never says misconfigured; and it never directs the
// customer to repair or reselect a certificate. Nothing here derives a selector,
// builds argv, or reaches PaxAdapter.
internal static class OrganizationKeyRunnability
{
    // The bounded execution status token. Closed set of one.
    internal const string ExecutionStatus = "organization_key_not_yet_runnable";

    // The AJV-shaped anchor and keyword for the bounded refusal body.
    internal const string InstancePath = "/auth/organizationKeyId";

    internal const string Keyword = "organizationKeyNotYetRunnable";

    // Verbatim customer copy.
    internal const string Message = "This organization-provided certificate is not yet available for Bakes.";

    // Verbatim supporting copy.
    internal const string Detail = "PAX Cookbook is waiting for a fail-closed engine certificate selector.";
}

// The bounded terminal state of a binding readiness evaluation. The set is
// CLOSED at exactly twelve values: there is no permissive default and no state
// that grants a capability beyond LOCAL binding readiness.
internal enum OrganizationKeyBindingState
{
    // Malformed identifier, both binding types present, or an unsupported mode.
    InvalidBinding,

    // Machine policy does not authorize organization keys.
    OrganizationKeyNotAuthorized,

    // Authorized, but no usable inventory is provisioned (including an
    // unavailable, untrusted, or invalid one): fail closed.
    OrganizationInventoryNotProvisioned,

    // The bound identifier is not present in the trusted inventory.
    OrganizationKeyNotFound,

    // The administrator disabled the bound entry.
    OrganizationKeyDisabled,

    // The bound entry is schema-v1 and carries no certificate reference.
    CertificateReferenceMissing,

    // No catalog answered: null, unavailable, or not connected (production).
    CatalogUnavailable,

    // The catalog reported no occurrence of the entry's reference.
    CertificateNotFound,

    // The catalog reported more than one occurrence: fail closed.
    CertificateAmbiguous,

    // Exactly one certificate resolved, but a bounded LOCAL usability check
    // refused it. The specific usability category is deliberately NOT restated
    // here; the bounded usability contract remains its only vocabulary.
    CertificateUnusable,

    // Every bounded LOCAL check passed. The Recipe binding is locally ready for
    // a FUTURE Cook integration. NOTHING MORE.
    OrganizationKeyReady,

    // A missing evaluation, a duplicate identifier, or any unrepresentable
    // state: fail closed.
    Unknown,
}

// The immutable, bounded readiness result. EVERY later-stage capability is
// constant-false by construction and cannot be set by any caller. It carries NO
// identifier, fingerprint, thumbprint, subject, issuer, serial, display name,
// tenant/client reference, path, store selector, or secret, and ToString is
// content-free.
internal sealed class OrganizationKeyBindingReadiness
{
    private OrganizationKeyBindingReadiness(OrganizationKeyBindingState state, string wireState)
    {
        State = state;
        WireState = wireState;
    }

    internal OrganizationKeyBindingState State { get; }

    internal string WireState { get; }

    // TRUE only when every bounded LOCAL check passed. It asserts nothing about
    // trust, chain, revocation, tenant acceptance, entitlement, or execution.
    internal bool Ready => State == OrganizationKeyBindingState.OrganizationKeyReady;

    // Binding readiness NEVER grants a downstream capability, in any state.
    internal bool BakeAuthorized => false;

    internal bool ServiceReady => false;

    internal bool AuthenticationVerified => false;

    internal bool ExecutionTested => false;

    // Bounded constraint markers, constant by construction.
    internal bool ReadOnly => true;

    internal bool ReferenceOnly => true;

    internal bool KeyNeverUsed => true;

    internal static OrganizationKeyBindingReadiness From(OrganizationKeyBindingState state)
        => new(state, WireToken(state));

    private static string WireToken(OrganizationKeyBindingState state) => state switch
    {
        OrganizationKeyBindingState.InvalidBinding => "invalid_binding",
        OrganizationKeyBindingState.OrganizationKeyNotAuthorized => "organization_key_not_authorized",
        OrganizationKeyBindingState.OrganizationInventoryNotProvisioned => "organization_inventory_not_provisioned",
        OrganizationKeyBindingState.OrganizationKeyNotFound => "organization_key_not_found",
        OrganizationKeyBindingState.OrganizationKeyDisabled => "organization_key_disabled",
        OrganizationKeyBindingState.CertificateReferenceMissing => "certificate_reference_missing",
        OrganizationKeyBindingState.CatalogUnavailable => "catalog_unavailable",
        OrganizationKeyBindingState.CertificateNotFound => "certificate_not_found",
        OrganizationKeyBindingState.CertificateAmbiguous => "certificate_ambiguous",
        OrganizationKeyBindingState.CertificateUnusable => "certificate_unusable",
        OrganizationKeyBindingState.OrganizationKeyReady => "organization_key_ready",
        _ => "unknown",
    };

    // Customer-safe, identifier-free copy. It never renders a raw enum token.
    internal string Detail => State switch
    {
        OrganizationKeyBindingState.InvalidBinding =>
            "This recipe's organization key reference isn't valid for its sign-in mode.",
        OrganizationKeyBindingState.OrganizationKeyNotAuthorized =>
            "Your organization's policy doesn't allow organization-provided keys on this device.",
        OrganizationKeyBindingState.OrganizationInventoryNotProvisioned =>
            "No organization-provided keys are set up on this device yet.",
        OrganizationKeyBindingState.OrganizationKeyNotFound =>
            "The organization key this recipe refers to is no longer listed.",
        OrganizationKeyBindingState.OrganizationKeyDisabled =>
            "Your administrator has turned off the organization key this recipe refers to.",
        OrganizationKeyBindingState.CertificateReferenceMissing =>
            "The organization key this recipe refers to isn't finished being set up.",
        OrganizationKeyBindingState.CatalogUnavailable =>
            "Cookbook couldn't check the organization key on this device.",
        OrganizationKeyBindingState.CertificateNotFound =>
            "The certificate for this organization key isn't installed on this device.",
        OrganizationKeyBindingState.CertificateAmbiguous =>
            "This device has more than one certificate for this organization key, so Cookbook can't tell them apart.",
        OrganizationKeyBindingState.CertificateUnusable =>
            "The certificate for this organization key can't be used on this device.",
        OrganizationKeyBindingState.OrganizationKeyReady =>
            "This recipe's organization key is set up on this device.",
        _ => "Cookbook couldn't determine the state of this recipe's organization key.",
    };

    // Content-free: a single bounded state token and nothing else.
    public override string ToString() => $"OrganizationKeyBindingReadiness[state={WireState}]";
}

// The PURE, deterministic, side-effect-free binding readiness evaluator. It
// reads only the bounded values it is given, never opens a store, never reads a
// certificate or key, never touches the filesystem/registry/network/credential
// vault, and never throws through the boundary.
//
// Evaluation ORDER is fixed and FIRST MATCH WINS, and the catalog is consulted
// at most ONCE and ONLY at the last step - so an invalid binding, an
// unauthorized policy, a non-provisioned inventory, an unknown or disabled
// entry, and a missing reference can never cause a certificate store to open.
// Resolution remains the Cycle-10 resolver's decision alone, and usability
// remains the Cycle-12 evaluator's decision alone.
internal static class OrganizationKeyBindingEvaluator
{
    internal static OrganizationKeyBindingReadiness Evaluate(
        OrganizationKeyRecipeBinding? binding,
        OrganizationInventoryEvaluation? evaluation,
        ICertificateCatalog? catalog,
        ICertificateUsabilityClock? clock)
    {
        // 1. Binding shape. A missing binding, a malformed identifier, two
        //    competing bindings, and an unsupported sign-in mode all refuse
        //    BEFORE any inventory or catalog is consulted.
        if (binding is null
            || !binding.HasOrganizationBinding
            || binding.HasPersonalBinding
            || !binding.IdWellFormed()
            || !string.Equals(binding.AuthMode, OrganizationKeyRecipeBinding.RequiredAuthMode, StringComparison.OrdinalIgnoreCase))
        {
            return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.InvalidBinding);
        }

        // 2-3. Inventory-layer gates. A missing evaluation is unrepresentable; a
        //      denied gate is a policy refusal; and every remaining
        //      non-provisioned bounded state (not provisioned, unavailable,
        //      untrusted, invalid) fails closed together.
        if (evaluation is null)
        {
            return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.Unknown);
        }

        switch (evaluation.Projection.State)
        {
            case OrganizationInventoryState.NotAuthorized:
                return OrganizationKeyBindingReadiness.From(
                    OrganizationKeyBindingState.OrganizationKeyNotAuthorized);

            case OrganizationInventoryState.AuthorizedNotProvisioned:
            case OrganizationInventoryState.Unavailable:
            case OrganizationInventoryState.Untrusted:
            case OrganizationInventoryState.Invalid:
                return OrganizationKeyBindingReadiness.From(
                    OrganizationKeyBindingState.OrganizationInventoryNotProvisioned);

            case OrganizationInventoryState.AuthorizedProvisioned:
                break;

            default:
                return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.Unknown);
        }

        // 4. Locate the bound entry. Matching is ordinal-ignore-case, exactly like
        //    the Cycle-5 parser's duplicate detection, so a case variant can never
        //    be treated as a distinct key. The parser already rejects duplicate
        //    ids, so more than one match is UNREPRESENTABLE - and it still fails
        //    closed rather than silently picking one.
        OrganizationKeyInventoryEntry? entry = null;
        foreach (OrganizationKeyInventoryEntry candidate in evaluation.Entries)
        {
            if (candidate is null
                || !string.Equals(candidate.OrganizationKeyId, binding.OrganizationKeyId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (entry is not null)
            {
                return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.Unknown);
            }
            entry = candidate;
        }

        if (entry is null)
        {
            return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.OrganizationKeyNotFound);
        }

        // 5. Administrator disablement outranks everything about the certificate.
        if (string.Equals(entry.AdminState, OrganizationKeyInventoryContract.AdminStateDisabled, StringComparison.Ordinal))
        {
            return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.OrganizationKeyDisabled);
        }

        // 6. A schema-v1 entry carries no reference; there is nothing to look up,
        //    so the catalog is NEVER consulted.
        if (entry.CertificateSha256.Length == 0)
        {
            return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.CertificateReferenceMissing);
        }

        // 7. Exactly ONE catalog query, and only now.
        if (catalog is null)
        {
            return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.CatalogUnavailable);
        }

        CertificateCatalogResult? catalogResult = catalog.Query();
        if (catalogResult is null || catalogResult.Status != CertificateCatalogStatus.Available)
        {
            return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.CatalogUnavailable);
        }

        // 8-10. The Cycle-10 resolver decides resolution; this evaluator only maps
        //       its bounded state onto a bounded binding state.
        CertificateResolution resolution = OrganizationCertificateResolver.Resolve(
            evaluation.Projection, entry, catalogResult);

        switch (resolution.State)
        {
            case CertificateResolutionState.NotFound:
                return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.CertificateNotFound);

            case CertificateResolutionState.Ambiguous:
                return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.CertificateAmbiguous);

            case CertificateResolutionState.CatalogUnavailable:
                return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.CatalogUnavailable);

            case CertificateResolutionState.EntryDisabled:
                return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.OrganizationKeyDisabled);

            case CertificateResolutionState.ReferenceMissing:
                return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.CertificateReferenceMissing);

            case CertificateResolutionState.ResolvedMetadataOnly:
                break;

            default:
                return OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.Unknown);
        }

        // 11. The Cycle-12 evaluator decides bounded LOCAL usability from the one
        //     matching occurrence's facts alone. Any non-usable outcome refuses
        //     without restating the usability category.
        CertificateUsability usability = CertificateUsabilityEvaluator.Evaluate(
            resolution.State,
            FindUniqueOccurrence(catalogResult, entry.CertificateSha256)?.Usability,
            clock);

        return usability.Usable
            ? OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.OrganizationKeyReady)
            : OrganizationKeyBindingReadiness.From(OrganizationKeyBindingState.CertificateUnusable);
    }

    // Returns the single occurrence matching a reference, or null when there is
    // not EXACTLY one, so an ambiguous reference can never surface one arbitrary
    // certificate.
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
