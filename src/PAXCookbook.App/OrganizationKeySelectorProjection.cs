using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.App;

// Cycle 14s — the BOUNDED, READ-ONLY ORGANIZATION-KEY SELECTOR PROJECTION.
//
// Cycle 14r proved the immutable PAX engine can select a certificate ONLY by
// SHA-1 thumbprint, so organization-bound Cook CANNOT work. This file builds the
// safe half and nothing else: a presentation-only list the Mini-Kitchen Chef's
// Key dropdown can render as a separate, read-only organization group.
//
// It performs ZERO I/O of its own: the inventory evaluation, the certificate
// catalog, and the clock are all INJECTED. Eligibility is decided by the EXISTING
// Cycle-14 `OrganizationKeyBindingEvaluator` -- there is no second readiness
// rule here. Nothing in this file derives a SHA-1 thumbprint, maps an
// organization key to a personal Windows Credential Manager target, builds argv,
// reaches `PaxAdapter`, constructs a process, mutates ProgramData, contacts a
// tenant/Graph/service, reads a private key or secret, runs PAX, or performs a
// Bake.
//
// `eligible` means LOCALLY READY BY POLICY / INVENTORY / RESOLUTION / USABILITY.
// It NEVER means runnable: an organization-bound Recipe is ALWAYS
// `organization_key_not_yet_runnable` (see OrganizationKeyRunnability).

// ONE bounded selector element. It carries EXACTLY three values and owns its own
// wire shape, so no caller can widen it. It has nowhere to put -- and never
// carries -- a tenant or client reference, a certificate SHA-256, a thumbprint, a
// subject, an issuer, a serial, a store or location, an admin state, a raw
// readiness reason, a private-key detail, a path, a secret, a token, or a claim.
internal sealed class OrganizationKeySelectorEntry
{
    internal OrganizationKeySelectorEntry(string organizationKeyId, string displayName, bool eligible)
    {
        OrganizationKeyId = organizationKeyId;
        DisplayName = displayName;
        Eligible = eligible;
    }

    // Opaque. It is projected to the selector array so the customer can pick a
    // key, and it is NEVER rendered as visible text by the UI.
    internal string OrganizationKeyId { get; }

    // The administrator-supplied name, and the ONLY visible text for this entry.
    internal string DisplayName { get; }

    // Bounded LOCAL readiness only.
    internal bool Eligible { get; }

    // The complete, closed wire shape: exactly three fields, built here so the
    // route projection cannot add a fourth.
    internal object ToWireObject() => new
    {
        organizationKeyId = OrganizationKeyId,
        displayName = DisplayName,
        eligible = Eligible,
    };

    // Content-free: never the identifier, the display name, or the flag.
    public override string ToString() => "OrganizationKeySelectorEntry[bounded]";
}

// The pure, deterministic builder.
//
// FAIL-CLOSED DOCTRINE: a missing evaluation, a non-provisioned inventory, an
// over-limit entry count, an out-of-bounds identifier or display name, and a
// duplicate identifier ALL collapse the WHOLE organization projection to empty.
// The personal Chef's Keys array is a separate, independent projection and is
// never affected by any of them.
internal static class OrganizationKeySelectorProjection
{
    private static readonly OrganizationKeySelectorEntry[] Empty = Array.Empty<OrganizationKeySelectorEntry>();

    internal static IReadOnlyList<OrganizationKeySelectorEntry> Build(
        OrganizationInventoryEvaluation? evaluation,
        ICertificateCatalog? catalog,
        ICertificateUsabilityClock? clock)
    {
        // Inventory gate first: nothing is selectable unless a trusted inventory
        // is actually provisioned.
        if (evaluation is null || !evaluation.Projection.InventoryLoaded)
        {
            return Empty;
        }

        IReadOnlyList<OrganizationKeyInventoryEntry> entries = evaluation.Entries;
        if (entries.Count > OrganizationKeyInventoryContract.MaxEntries)
        {
            return Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectable = new List<OrganizationKeySelectorEntry>();
        foreach (OrganizationKeyInventoryEntry entry in entries)
        {
            if (entry is null)
            {
                return Empty;
            }

            // A duplicate identifier is unrepresentable after parsing, and it
            // still fails the WHOLE organization projection closed rather than
            // letting an ambiguous id become selectable.
            if (!seen.Add(entry.OrganizationKeyId))
            {
                return Empty;
            }

            if (!WithinBounds(entry.OrganizationKeyId) || !WithinBounds(entry.DisplayName))
            {
                return Empty;
            }

            // Enabled entries ONLY. An administrator-disabled entry is omitted
            // entirely -- it is never offered, not even as a disabled option.
            if (string.Equals(
                    entry.AdminState,
                    OrganizationKeyInventoryContract.AdminStateDisabled,
                    StringComparison.Ordinal))
            {
                continue;
            }

            // Eligibility reuses the Cycle-14 evaluator verbatim, with a binding
            // shaped exactly like a Recipe binding to this entry.
            OrganizationKeyBindingReadiness readiness = OrganizationKeyBindingEvaluator.Evaluate(
                OrganizationKeyRecipeBinding.Create(
                    OrganizationKeyRecipeBinding.RequiredAuthMode, null, entry.OrganizationKeyId),
                evaluation,
                catalog,
                clock);

            selectable.Add(new OrganizationKeySelectorEntry(
                entry.OrganizationKeyId, entry.DisplayName, readiness.Ready));
        }

        // Stable, deterministic ORDINAL ordering: display name, then the opaque
        // identifier as the tie-break.
        selectable.Sort(static (a, b) =>
        {
            int byName = string.CompareOrdinal(a.DisplayName, b.DisplayName);
            return byName != 0 ? byName : string.CompareOrdinal(a.OrganizationKeyId, b.OrganizationKeyId);
        });

        return selectable;
    }

    private static bool WithinBounds(string? value)
        => value is not null
           && value.Length > 0
           && value.Length <= OrganizationKeyInventoryContract.MaxStringLength;
}

// A read-only decorator that queries an inner catalog AT MOST ONCE and replays
// the identical bounded result to every later caller. It exists so the aggregate
// coordinator and the per-entry selector eligibility share ONE certificate-store
// read instead of one per entry. It reads no certificate, holds no handle or key,
// mutates nothing, and cannot make a non-answering catalog answer.
internal sealed class OnceQueriedCertificateCatalog : ICertificateCatalog
{
    private readonly ICertificateCatalog _inner;
    private CertificateCatalogResult? _cached;

    internal OnceQueriedCertificateCatalog(ICertificateCatalog inner)
    {
        _inner = inner;
    }

    public CertificateCatalogResult Query()
        => _cached ??= _inner.Query() ?? CertificateCatalogResult.Unavailable();
}
