using System;

namespace PAXCookbook.App;

// Explicit auth-provider selection (Track 1 / T1-S2A).
//
// Provider selection is EXPLICIT and CLOSED. There are exactly two provider
// identifiers, and there is NO automatic fallback between them: if a caller
// explicitly asks for the experimental provider and it is not enabled/fully
// configured (or the requested id is unknown/blank), selection FAILS CLOSED —
// it does not silently substitute Windows Hello.
//
// Windows Hello remains the default only in the sense that when NO explicit
// experimental selection is made, the caller uses the existing Hello path
// unchanged. This selector never invents a provider and never leaks provider-
// specific types.
internal static class AuthProviderIds
{
    internal const string WindowsHello = "windows-hello";
    internal const string EntraWam = ExperimentalWamOptions.EntraWamProviderId; // "entra-wam"
}

internal enum AuthProviderSelectionStatus
{
    // Use the default Windows Hello provider (no explicit experimental request).
    WindowsHello = 0,

    // Use the experimental Entra WAM provider (explicitly requested, enabled,
    // and fully configured).
    EntraWam = 1,

    // Fail closed: the requested provider cannot be honored and there is NO
    // fallback. The reason is a bounded, redacted category for diagnostics.
    FailClosed = 2,
}

// Bounded, redacted reason categories. Only these values are ever surfaced in
// diagnostics — never tenant/client/object identifiers, tokens, or claims.
internal enum AuthProviderSelectionReason
{
    None = 0,
    DefaultHello = 1,
    ExperimentalSelected = 2,
    ExperimentalNotEnabled = 3,
    ExperimentalNotConfigured = 4,
    UnknownProvider = 5,
}

internal readonly record struct AuthProviderSelection(
    AuthProviderSelectionStatus Status,
    AuthProviderSelectionReason Reason)
{
    internal bool IsEntraWam => Status == AuthProviderSelectionStatus.EntraWam;
    internal bool IsWindowsHello => Status == AuthProviderSelectionStatus.WindowsHello;
    internal bool IsFailClosed => Status == AuthProviderSelectionStatus.FailClosed;
}

internal static class AuthProviderSelector
{
    // Resolve which provider to use.
    //
    //   requestedProviderId:
    //     - null / blank  => no explicit request => default Windows Hello.
    //     - "windows-hello" => explicit Windows Hello.
    //     - "entra-wam"   => experimental; honored ONLY if options.IsFullyConfigured,
    //                        else FailClosed (NOT Hello fallback).
    //     - anything else => unknown => FailClosed.
    //
    // There is no path where a denied/misconfigured/unknown experimental
    // request silently degrades to Windows Hello.
    internal static AuthProviderSelection Select(string? requestedProviderId, ExperimentalWamOptions options)
    {
        if (string.IsNullOrWhiteSpace(requestedProviderId) ||
            string.Equals(requestedProviderId, AuthProviderIds.WindowsHello, StringComparison.Ordinal))
        {
            return new AuthProviderSelection(
                AuthProviderSelectionStatus.WindowsHello,
                AuthProviderSelectionReason.DefaultHello);
        }

        if (string.Equals(requestedProviderId, AuthProviderIds.EntraWam, StringComparison.Ordinal))
        {
            if (options is null || !options.Enabled)
            {
                return new AuthProviderSelection(
                    AuthProviderSelectionStatus.FailClosed,
                    AuthProviderSelectionReason.ExperimentalNotEnabled);
            }

            if (!options.IsFullyConfigured)
            {
                return new AuthProviderSelection(
                    AuthProviderSelectionStatus.FailClosed,
                    AuthProviderSelectionReason.ExperimentalNotConfigured);
            }

            return new AuthProviderSelection(
                AuthProviderSelectionStatus.EntraWam,
                AuthProviderSelectionReason.ExperimentalSelected);
        }

        // Unknown / malformed provider id => fail closed, no fallback.
        return new AuthProviderSelection(
            AuthProviderSelectionStatus.FailClosed,
            AuthProviderSelectionReason.UnknownProvider);
    }
}
