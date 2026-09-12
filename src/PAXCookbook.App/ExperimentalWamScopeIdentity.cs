using System;

namespace PAXCookbook.App;

// Exact scope + identity validation for the experimental Entra WAM result
// (Track 1 / T1-S3, one-registration model).
//
// This is a PURE function over a sanitized WamInteractiveResult and the
// runtime-injected options. It contains no MSAL dependency and no ambient
// state, so it is fully deterministic and unit-testable. It is the single place
// that decides whether a WAM result is acceptable; every provider consults it
// before any approval.
//
// Acceptance is deliberately narrow for the one-registration native WAM flow:
//   - Exactly ONE granted Microsoft Graph User.Read data scope, in either the
//     bare ("User.Read") or fully-qualified
//     ("https://graph.microsoft.com/User.Read") form.
//   - Implicitly-returned protocol/OIDC scopes (openid / profile / email /
//     offline_access) may additionally appear; they are companion scopes, not a
//     second requested data permission.
//   - Rejects a custom access_as_user scope, any resource ".default" scope, any
//     other Graph or non-Graph data permission, malformed scopes, missing
//     tenant/object claims, a tenant mismatch, and a missing account binding.
internal enum WamValidation
{
    Valid = 0,
    NotSucceeded = 1,
    MissingAccountBinding = 2,
    MissingTenantClaim = 3,
    TenantMismatch = 4,
    MissingObjectClaim = 5,
    ForbiddenScopePresent = 6,
    UnexpectedScope = 7,
    MalformedScope = 8,
    NoUserReadScope = 9,
    MultipleUserReadScopes = 10,
}

internal static class WamScopeIdentityValidator
{
    // The two accepted forms of the single Microsoft Graph User.Read data scope.
    private const string UserReadBare = "user.read";
    private const string UserReadGraph = "https://graph.microsoft.com/user.read";

    // Implicitly-returned protocol / OIDC scopes that may accompany User.Read.
    // These are NOT a second requested data permission — they are returned by
    // the identity platform as part of the sign-in protocol.
    private static readonly string[] CompanionProtocolScopes =
    {
        "openid",
        "profile",
        "email",
        "offline_access",
    };

    // Forbidden substrings (case-insensitive). The superseded custom scope and
    // any resource ".default" scope are never acceptable.
    private static readonly string[] ForbiddenFragments =
    {
        "access_as_user",
        ".default",
    };

    internal static WamValidation Validate(
        WamInteractiveResult result,
        ExperimentalWamOptions options)
    {
        if (result is null || !result.Succeeded)
        {
            return WamValidation.NotSucceeded;
        }

        if (string.IsNullOrWhiteSpace(result.AccountHandle))
        {
            return WamValidation.MissingAccountBinding;
        }

        if (string.IsNullOrWhiteSpace(result.TenantClaim))
        {
            return WamValidation.MissingTenantClaim;
        }

        if (!string.Equals(result.TenantClaim, options.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return WamValidation.TenantMismatch;
        }

        if (string.IsNullOrWhiteSpace(result.ObjectClaim))
        {
            return WamValidation.MissingObjectClaim;
        }

        return ValidateScopes(result.GrantedScopes);
    }

    // Maps a validation outcome to the bounded acquisition status category. Scope
    // problems map to ScopeFailure; every other non-Valid outcome maps to
    // IdentityFailure; Valid maps to Succeeded. Shared by the authenticator's
    // pure issued-identity extraction seam and its post-sanitization chokepoint
    // so the two mappings can never diverge.
    internal static WamAcquireStatus ToAcquireStatus(WamValidation validation) => validation switch
    {
        WamValidation.Valid => WamAcquireStatus.Succeeded,
        WamValidation.ForbiddenScopePresent
            or WamValidation.UnexpectedScope
            or WamValidation.MalformedScope
            or WamValidation.NoUserReadScope
            or WamValidation.MultipleUserReadScopes => WamAcquireStatus.ScopeFailure,
        _ => WamAcquireStatus.IdentityFailure,
    };

    // Validates the granted scopes contain exactly one Microsoft Graph User.Read
    // data scope and nothing beyond the permitted companion protocol scopes.
    private static WamValidation ValidateScopes(string[] grantedScopes)
    {
        if (grantedScopes is null || grantedScopes.Length == 0)
        {
            return WamValidation.NoUserReadScope;
        }

        int userReadCount = 0;

        foreach (string raw in grantedScopes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return WamValidation.MalformedScope;
            }

            string lower = raw.Trim().ToLowerInvariant();

            // Reject the superseded custom scope and any ".default" outright.
            foreach (string forbidden in ForbiddenFragments)
            {
                if (lower.Contains(forbidden))
                {
                    return WamValidation.ForbiddenScopePresent;
                }
            }

            // The single accepted data permission: Graph User.Read (bare or
            // fully-qualified).
            if (string.Equals(lower, UserReadBare, StringComparison.Ordinal) ||
                string.Equals(lower, UserReadGraph, StringComparison.Ordinal))
            {
                userReadCount++;
                continue;
            }

            // Companion protocol scopes may accompany User.Read.
            if (IsCompanionProtocolScope(lower))
            {
                continue;
            }

            // Anything else — another Graph permission, a resource scope, a
            // second data permission — is an unexpected scope.
            return WamValidation.UnexpectedScope;
        }

        if (userReadCount == 0)
        {
            return WamValidation.NoUserReadScope;
        }

        if (userReadCount > 1)
        {
            return WamValidation.MultipleUserReadScopes;
        }

        return WamValidation.Valid;
    }

    private static bool IsCompanionProtocolScope(string lowerScope)
    {
        foreach (string s in CompanionProtocolScopes)
        {
            if (string.Equals(lowerScope, s, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
