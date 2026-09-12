using System;

namespace PAXCookbook.App;

// The bounded, neutral result delivered from the HWND-owning attached-window
// process to the daemon over the NATIVE-ONLY IPC channel (Track 1 / T1-S2A
// authorization-boundary repair).
//
// This is the ONLY WAM-derived shape that leaves the window process, and it now
// crosses a native named pipe (never an HTTP route the renderer controls). It
// carries NO token, NO raw tenant/object claim value, NO raw MSAL account
// identifier, and NO granted-scope array. It also carries NO challenge, purpose,
// recipe, provider, or lock generation: those are DAEMON-OWNED and keyed by the
// opaque requestId the daemon minted, so neither the renderer nor the window can
// choose them. Exact scope/identity validation happens INSIDE the window process
// against the raw values (WamInteractiveResult); this DTO reduces those to
// daemon-verifiable booleans.
//
// Containment taxonomy (what may exist where):
//   - token bytes:                never leave MSAL inside the window process.
//   - raw claim values:           window process only (WamInteractiveResult).
//   - opaque account handle:      window process only.
//   - neutral native result:      this type (requestId + booleans + category).
internal enum NeutralWamCategory
{
    Approved = 0,
    Denied = 1,
    Cancelled = 2,
    BrokerFailure = 3,
    ConfigurationFailure = 4,
    TransportFailure = 5,
    ScopeFailure = 6,
    IdentityFailure = 7,
    Disabled = 8,

    // cycle-02r5b bounded MSAL-failure categories. These cross the native pipe by
    // NAME (ExperimentalWamPipe serializes Category.ToString()), so appending them
    // does NOT change the wire shape of any existing value. Connectivity wording is
    // reserved for ConnectivityFailure ONLY.
    ConnectivityFailure = 9,
    AuthorityRegistrationMismatch = 10,
    ServiceRejected = 11,
    ConsentRequired = 12,
    UnknownFailure = 13,
}

internal sealed class NeutralWamResult
{
    internal NeutralWamResult(
        string requestId,
        NeutralWamCategory category,
        bool scopeValid,
        bool identityValid)
    {
        RequestId = requestId;
        Category = category;
        ScopeValid = scopeValid;
        IdentityValid = identityValid;
    }

    // The opaque, daemon-minted request identifier this result answers. The
    // daemon looks up the challenge and purpose by this id; neither the renderer
    // nor the window can influence them.
    internal string RequestId { get; }

    internal NeutralWamCategory Category { get; }

    // Daemon-verifiable booleans produced by exact validation in the window.
    internal bool ScopeValid { get; }

    internal bool IdentityValid { get; }

    internal bool IsApproved =>
        Category == NeutralWamCategory.Approved && ScopeValid && IdentityValid;

    internal static NeutralWamResult Approved(string requestId) =>
        new(requestId, NeutralWamCategory.Approved, scopeValid: true, identityValid: true);

    internal static NeutralWamResult Rejected(string requestId, NeutralWamCategory category,
        bool scopeValid = false, bool identityValid = false) =>
        new(requestId ?? string.Empty, category, scopeValid, identityValid);
}
