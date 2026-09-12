using System;
using System.Collections.Generic;
using System.Linq;

namespace PAXCookbook.App;

// Pure, total issued-identity extraction seam for the native Work-account WAM
// flow (cycle-02r5 field-mapping repair).
//
// WHY THIS EXISTS — the prior defect lived in the MSAL glue where the sanitized
// identity was read from the account's HOME identity (HomeAccountId.TenantId /
// HomeAccountId.ObjectId) with NULL-ONLY ("??") coalescing:
//   * a resource-tenant GUEST whose home tenant differs from the issued tenant
//     was wrongly rejected as a tenant mismatch, and
//   * an EMPTY-STRING home component never fell back (null-only), so the presence
//     check produced a spurious IdentityFailure.
// The extraction changed with no MSAL-shape test to catch it. All identity
// DECISION logic now lives here, behind a fully unit-testable pure function, so
// the mapping cannot silently regress again.
//
// This is a PURE function over plain strings + the runtime-injected options: no
// MSAL dependency, no ambient state, fully deterministic. It treats empty /
// whitespace as MISSING everywhere (never null-only coalescing) and delegates the
// exact configured-tenant + exact-scope decision to the authoritative
// WamScopeIdentityValidator — it does NOT fork that logic. It returns EITHER a
// sanitized token-free WamInteractiveResult (Succeeded) OR a bounded failure
// (IdentityFailure vs ScopeFailure), reusing the existing WamAcquireStatus /
// WamValidation categories.
internal static class WorkAccountIssuedIdentity
{
    // Inputs (all plain values — NEVER token bytes):
    //   issuedTenantId        = AuthenticationResult.TenantId — the ISSUED / resource
    //                           tenant. A guest's issued tenant IS the resource
    //                           tenant, so the exact configured-tenant match still
    //                           holds; a personal MSA's issued tenant differs and is
    //                           correctly rejected. NEVER HomeAccountId.TenantId.
    //   oidClaim              = the ISSUED oid claim read from
    //                           AuthenticationResult.ClaimsPrincipal. NEVER
    //                           HomeAccountId.ObjectId.
    //   homeAccountIdentifier = Account.HomeAccountId.Identifier — the opaque DPAPI
    //                           preferred-account handle. This one is INTENTIONALLY
    //                           the home handle: it is only an opaque MSAL account
    //                           reference used for preferred-account continuity, not
    //                           an authorization claim.
    //   grantedScopes         = AuthenticationResult.Scopes.
    internal static WamInteractiveResult Extract(
        string? issuedTenantId,
        string? oidClaim,
        string? homeAccountIdentifier,
        IReadOnlyList<string>? grantedScopes,
        ExperimentalWamOptions options)
    {
        // Empty / whitespace is MISSING — the exact fail-closed the prior
        // null-only coalescing skipped. Fails closed BEFORE any preferred-save /
        // photo-fetch / profile-publish / daemon-approval side effect.
        if (string.IsNullOrWhiteSpace(homeAccountIdentifier) ||
            string.IsNullOrWhiteSpace(issuedTenantId) ||
            string.IsNullOrWhiteSpace(oidClaim))
        {
            return WamInteractiveResult.Failure(WamAcquireStatus.IdentityFailure);
        }

        string[] scopes = grantedScopes is null
            ? Array.Empty<string>()
            : grantedScopes.ToArray();

        // Build the sanitized result from the ISSUED identity (the account handle
        // is preserved verbatim so later Ordinal matching against the MSAL account
        // cache still works), then let the authoritative validator make the exact
        // configured-tenant + exact-scope decision. No tenant/scope logic is
        // duplicated here.
        WamInteractiveResult sanitized = WamInteractiveResult.Success(
            homeAccountIdentifier!,
            scopes,
            issuedTenantId!,
            oidClaim!);

        WamValidation validation = WamScopeIdentityValidator.Validate(sanitized, options);
        if (validation != WamValidation.Valid)
        {
            return WamInteractiveResult.Failure(
                WamScopeIdentityValidator.ToAcquireStatus(validation));
        }

        return sanitized;
    }
}
