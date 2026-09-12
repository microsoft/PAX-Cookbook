using System;

namespace PAXCookbook.App;

// Bounded, CUSTOMER-SAFE work-account rejection reason (cycle-02r5 Batch 1).
//
// This enum is the ONLY rejection detail that is ever allowed to reach the
// daemon terminal tombstone, the status route, HTTP, logs, evidence, or the
// renderer/JS. It is an ALLOW-LIST: every member is a generic outcome class and
// carries NO token, raw claim, tenant id, client id, object id, UPN, account
// handle, scope array, challenge, or generation value. Nothing identifier- or
// authority-bearing may be added to this enum.
//
// It is deliberately coarser than the internal NeutralWamCategory /
// DaemonWamReason taxonomies: the mapping in WamRejectionReasonMap is total and
// FAIL-CLOSED so that any value that cannot be mapped collapses to the most
// restrictive customer-safe outcome (Denied), never a permissive one.
internal enum WamRejectionReason
{
    // No rejection: the request was approved (or is still pending / has no
    // terminal rejection recorded). Emitted as the "none" status string.
    None = 0,

    // The user dismissed / cancelled the account picker or consent prompt.
    Cancelled = 1,

    // The signed-in identity did not satisfy the exact expected-identity check
    // (e.g. tenant/object mismatch). No identity value is carried — only the class.
    IdentityFailure = 2,

    // The granted scopes did not match the exact expected User.Read shape
    // (missing, unexpected, forbidden, malformed, or duplicated). No scope value
    // is carried — only the class.
    ScopeFailure = 3,

    // The work-account provider is not fully configured, or a required piece of
    // configuration (registration, window handle) was missing. Also the target
    // reason for a not-configured daemon outcome and for the Disabled category
    // (a disabled provider is a configuration-class outcome; see below).
    ConfigurationFailure = 4,

    // The underlying WAM broker failed or returned an unusable result.
    BrokerFailure = 5,

    // A transport/network failure reaching the identity service.
    TransportFailure = 6,

    // The pending request was abandoned and passed its TTL before a result
    // arrived (daemon lifecycle expiry). Distinct from an explicit denial.
    Expired = 7,

    // The provider is disabled by configuration/policy. This is itself a
    // configuration-class outcome; NeutralWamCategory.Disabled maps here 1:1 so
    // the disabled condition is surfaced distinctly rather than being folded into
    // the generic ConfigurationFailure. (Documents the "Disabled → configuration
    // class" decision: Disabled is the configuration-class reason we chose.)
    Disabled = 8,

    // Generic, fail-closed denial. This is the default for anything that cannot
    // be attributed to a more specific bounded class (unknown category, unknown
    // daemon reason, replay, request-not-found, challenge rejection, or any
    // unmapped value). NEVER a permissive value.
    Denied = 9,

    // cycle-02r5b bounded MSAL-failure reasons. Each is a generic outcome class
    // carrying NO identifier/authority value. Connectivity wording is reserved for
    // ConnectivityFailure ONLY.
    //
    // A genuine no-response transport condition (the sign-in service could not be
    // reached at all). This is the ONLY reason that carries connectivity copy.
    ConnectivityFailure = 10,

    // A reached service rejected sign-in because the registration/authority is not
    // compatible with the account picker, or the account's home directory is not
    // authorized for this sign-in (e.g. an out-of-tenant account via /common).
    AuthorityRegistrationMismatch = 11,

    // A reached service rejected sign-in for any other reason, or the response was
    // unparseable. Distinct from connectivity: the service WAS reached.
    ServiceRejected = 12,

    // A reached service requires administrator/user consent before sign-in.
    ConsentRequired = 13,

    // Fail-closed default for an unknown/unmapped failure shape. Never implies the
    // network is down.
    UnknownFailure = 14,
}

// Pure, total, fail-closed mapping from the internal WAM taxonomies to the
// bounded WamRejectionReason. Every function here is side-effect free and
// exhaustively defined for the whole input domain, with an explicit default that
// collapses any unknown/unmapped value to WamRejectionReason.Denied.
internal static class WamRejectionReasonMap
{
    // NeutralWamCategory (the native window→daemon result category) → bounded reason.
    //
    // Full mapping table (every NeutralWamCategory value; fail-closed default):
    //   Approved             -> None
    //   Cancelled            -> Cancelled
    //   IdentityFailure      -> IdentityFailure
    //   ScopeFailure         -> ScopeFailure
    //   ConfigurationFailure -> ConfigurationFailure
    //   BrokerFailure        -> BrokerFailure
    //   TransportFailure     -> TransportFailure
    //   Disabled             -> Disabled            (configuration-class; see enum)
    //   Denied               -> Denied
    //   ConnectivityFailure           -> ConnectivityFailure
    //   AuthorityRegistrationMismatch -> AuthorityRegistrationMismatch
    //   ServiceRejected               -> ServiceRejected
    //   ConsentRequired               -> ConsentRequired
    //   UnknownFailure                -> UnknownFailure
    //   (any unknown)        -> Denied              (FAIL-CLOSED default)
    internal static WamRejectionReason FromCategory(NeutralWamCategory category) => category switch
    {
        NeutralWamCategory.Approved => WamRejectionReason.None,
        NeutralWamCategory.Cancelled => WamRejectionReason.Cancelled,
        NeutralWamCategory.IdentityFailure => WamRejectionReason.IdentityFailure,
        NeutralWamCategory.ScopeFailure => WamRejectionReason.ScopeFailure,
        NeutralWamCategory.ConfigurationFailure => WamRejectionReason.ConfigurationFailure,
        NeutralWamCategory.BrokerFailure => WamRejectionReason.BrokerFailure,
        NeutralWamCategory.TransportFailure => WamRejectionReason.TransportFailure,
        NeutralWamCategory.Disabled => WamRejectionReason.Disabled,
        NeutralWamCategory.Denied => WamRejectionReason.Denied,
        NeutralWamCategory.ConnectivityFailure => WamRejectionReason.ConnectivityFailure,
        NeutralWamCategory.AuthorityRegistrationMismatch => WamRejectionReason.AuthorityRegistrationMismatch,
        NeutralWamCategory.ServiceRejected => WamRejectionReason.ServiceRejected,
        NeutralWamCategory.ConsentRequired => WamRejectionReason.ConsentRequired,
        NeutralWamCategory.UnknownFailure => WamRejectionReason.UnknownFailure,
        _ => WamRejectionReason.Denied,
    };

    // DaemonWamReason (the daemon-origin outcome) → bounded reason.
    //
    // Full mapping table (every DaemonWamReason value; fail-closed default):
    //   Approved              -> None
    //   NotConfigured         -> ConfigurationFailure
    //   RequestNotFound       -> Denied   (unknown / replay / late-arriving)
    //   ChallengeRejected     -> Denied   (replay / generation / malformed challenge)
    //   NeutralResultRejected -> Denied   (used only when the category-derived
    //                                       reason is unavailable; ApplyNativeResult
    //                                       always prefers FromCategory here)
    //   (any unknown)         -> Denied   (FAIL-CLOSED default)
    internal static WamRejectionReason FromDaemonReason(DaemonWamReason reason) => reason switch
    {
        DaemonWamReason.Approved => WamRejectionReason.None,
        DaemonWamReason.NotConfigured => WamRejectionReason.ConfigurationFailure,
        DaemonWamReason.RequestNotFound => WamRejectionReason.Denied,
        DaemonWamReason.ChallengeRejected => WamRejectionReason.Denied,
        DaemonWamReason.NeutralResultRejected => WamRejectionReason.Denied,
        _ => WamRejectionReason.Denied,
    };

    // Stable, lowercase snake_case wire string for the status route. Total and
    // fail-closed: any unmapped value renders as "denied".
    internal static string ToStatusString(WamRejectionReason reason) => reason switch
    {
        WamRejectionReason.None => "none",
        WamRejectionReason.Cancelled => "cancelled",
        WamRejectionReason.IdentityFailure => "identity_failure",
        WamRejectionReason.ScopeFailure => "scope_failure",
        WamRejectionReason.ConfigurationFailure => "configuration_failure",
        WamRejectionReason.BrokerFailure => "broker_failure",
        WamRejectionReason.TransportFailure => "transport_failure",
        WamRejectionReason.Expired => "expired",
        WamRejectionReason.Disabled => "disabled",
        WamRejectionReason.Denied => "denied",
        WamRejectionReason.ConnectivityFailure => "connectivity_failure",
        WamRejectionReason.AuthorityRegistrationMismatch => "authority_registration_mismatch",
        WamRejectionReason.ServiceRejected => "service_rejected",
        WamRejectionReason.ConsentRequired => "consent_required",
        WamRejectionReason.UnknownFailure => "unknown_failure",
        _ => "denied",
    };
}

// Bounded status projection returned by the daemon endpoint: the coarse request
// state plus the customer-safe rejection reason. Carries NO authority field.
internal readonly record struct ExperimentalWamStatusReport(
    ExperimentalWamRequestState State,
    WamRejectionReason Reason);
