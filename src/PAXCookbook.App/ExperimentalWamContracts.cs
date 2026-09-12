using System;
using System.Threading;
using System.Threading.Tasks;

namespace PAXCookbook.App;

// Experimental Entra WAM contracts (Track 1 / T1-S2A).
//
// These types define the in-process boundary between the MSAL/WAM acquisition
// (which owns the raw tokens and claims) and the provider-neutral seams (which
// see ONLY a bounded, token-free result). By construction, NO access, refresh,
// or ID token, and no raw claim string, appears on any type here: the
// authenticator sanitizes the MSAL result before returning it, retaining only
// the opaque WAM account handle, the granted app-scope strings, and the tenant
// and object claim VALUES needed for exact validation. None of these are token
// material and none are persisted, logged, returned over HTTP, sent to the
// daemon, or exposed to React.

// The concern a WAM authorization serves. Session unlock is the only concern
// (see SessionUnlock.cs).
internal enum WamAuthPurpose
{
    SessionUnlock = 0,
}

// The bounded native ACQUISITION MODE for a WAM authorization. This is a native/
// daemon-authored intent — NEVER a renderer-authored field. It is carried on the
// window-process WamAuthRequest (built only by native code — the window bridge
// and the provider native-test mode), never mapped from any React/shell message.
//
//   - UsePreferredAccount   (default): honor the protected preferred-account
//     preference as a picker hint when it resolves uniquely. The picker is still
//     required; selecting an existing account may complete through WAM SSO.
//   - ForceAccountSelection: clear/skip the preference and show the same account
//     picker without preselecting an account. It never forces credential entry.
//
// The mode changes nothing unless a preferred-account store seam is wired into
// the authenticator; with no store the acquisition is exactly the prior plain
// interactive flow regardless of mode.
internal enum WorkAccountAcquisitionMode
{
    UsePreferredAccount = 0,
    ForceAccountSelection = 1,
}

// The bounded outcome status of a WAM acquisition. Every non-success value is a
// hard, no-fallback failure category.
internal enum WamAcquireStatus
{
    // Interactive acquisition succeeded and returned a sanitized result. Scope
    // and identity still require validation before any approval.
    Succeeded = 0,

    // The provider was disabled or not fully configured — the default, and the
    // only status reachable in a stable/customer build.
    Disabled = 1,

    UserCancelled = 2,
    BrokerFailure = 3,
    ConfigurationFailure = 4,

    // LEGACY (cycle-02r5b): the former blanket "every MsalServiceException ->
    // TransportFailure" mapping produced this value for reached-service auth
    // rejections, mislabelling them as connectivity. No production code path emits
    // TransportFailure any longer (the bounded WamServiceFailureClassifier emits
    // the accurate categories below instead). The value is retained so the wire /
    // enum shape is unchanged and existing mappings stay total.
    TransportFailure = 5,
    ScopeFailure = 6,
    IdentityFailure = 7,

    // cycle-02r5b bounded MSAL-failure categories (see WamServiceFailureClassifier).
    // Connectivity wording is RESERVED for ConnectivityFailure ONLY.
    //
    // A genuine no-response transport condition (no HTTP response reached).
    ConnectivityFailure = 8,

    // A reached service rejected the sign-in because the registration/authority is
    // not compatible with the requested account picker, or the account's home
    // directory is not authorized for this sign-in
    // (e.g. AADSTS50194 / 90130 / 700016 / 50020).
    AuthorityRegistrationMismatch = 9,

    // A reached service rejected the sign-in for any other (unrecognized) reason,
    // or the structured error_codes were absent/malformed. Never connectivity.
    ServiceRejected = 10,

    // A reached service requires administrator/user consent before sign-in can
    // complete (e.g. AADSTS65001 / 90094 / 65004).
    ConsentRequired = 11,

    // Fail-closed default for an unknown/unmapped failure shape. Never connectivity.
    UnknownFailure = 12,
}

// A request to perform a WAM interactive authorization. Carries no secret and
// no token; the tenant/client values are the runtime-injected identifiers.
//
// It carries ONLY what the window needs to act on the daemon-authored native
// descriptor (T1-S2A native request-binding): the authoritative purpose. There
// is NO renderer-authored recipe, provider, or retained account handle. This
// request stays inside the HWND-owning window process.
internal sealed class WamAuthRequest
{
    internal WamAuthRequest(
        WamAuthPurpose purpose,
        string tenantId,
        string clientId,
        string requestScope,
        WorkAccountAcquisitionMode acquisitionMode = WorkAccountAcquisitionMode.UsePreferredAccount)
    {
        Purpose = purpose;
        TenantId = tenantId;
        ClientId = clientId;
        RequestScope = requestScope;
        AcquisitionMode = acquisitionMode;
    }

    internal WamAuthPurpose Purpose { get; }

    internal string TenantId { get; }

    internal string ClientId { get; }

    // The exact delegated sign-in permission requested from Microsoft Graph:
    // User.Read. Per the bounded token / photo doctrine, the resulting User.Read
    // access token is used ONLY inside the WAM-owning native process and ONLY for
    // the single exact GET /v1.0/me/photo/$value profile-photo request; it is
    // never used for audit, directory, managed-key, permission-profile, PAX/Bake,
    // or any other tenant-data query, and it never leaves that native process.
    internal string RequestScope { get; }

    // The bounded native acquisition mode (preferred-account picker hint vs no
    // hint). Native-authored only; every mode still displays the account picker.
    internal WorkAccountAcquisitionMode AcquisitionMode { get; }
}

// The sanitized result of a WAM acquisition, produced INSIDE the HWND-owning
// window process. It is token-free (no access/refresh/id token), but it DOES
// carry the raw tenant/object claim VALUES and the opaque account handle needed
// for exact in-process validation. Because it carries raw claim values, this
// type NEVER crosses the process boundary to the daemon — only the reduced,
// boolean NeutralWamResult does (see NeutralWamResult.cs). Keeping this type
// window-internal is what makes the "no raw claim crosses the boundary" property
// true.
internal sealed class WamInteractiveResult
{
    private WamInteractiveResult(
        WamAcquireStatus status,
        string accountHandle,
        string[] grantedScopes,
        string tenantClaim,
        string objectClaim)
    {
        Status = status;
        AccountHandle = accountHandle;
        GrantedScopes = grantedScopes;
        TenantClaim = tenantClaim;
        ObjectClaim = objectClaim;
    }

    internal WamAcquireStatus Status { get; }

    // Opaque MSAL account handle (an identifier, not a token).
    internal string AccountHandle { get; }

    // Granted app-scope strings (e.g. "User.Read"). Used
    // for exact-form validation. Never contains a token.
    internal string[] GrantedScopes { get; }

    // Tenant and object claim VALUES used for exact validation. Not tokens.
    internal string TenantClaim { get; }

    internal string ObjectClaim { get; }

    internal bool Succeeded => Status == WamAcquireStatus.Succeeded;

    internal static WamInteractiveResult Success(
        string accountHandle,
        string[] grantedScopes,
        string tenantClaim,
        string objectClaim) =>
        new(WamAcquireStatus.Succeeded, accountHandle, grantedScopes ?? Array.Empty<string>(),
            tenantClaim ?? string.Empty, objectClaim ?? string.Empty);

    internal static WamInteractiveResult Failure(WamAcquireStatus status) =>
        new(status, string.Empty, Array.Empty<string>(), string.Empty, string.Empty);
}

// The MSAL/WAM acquisition port. The real implementation (behind the
// EXPERIMENTAL_WAM compilation gate) runs the proven MSAL.NET broker pattern in
// the HWND-owning process and returns only the sanitized result above. The
// default implementation used by stable/customer builds is
// DisabledExperimentalWamAuthenticator, which always fails closed.
internal interface IExperimentalWamAuthenticator
{
    // Perform an interactive WAM authorization parented to the real attached-
    // window HWND. Returns a sanitized, token-free result. Never returns,
    // prints, serializes, or persists token bytes. Supports cancellation.
    Task<WamInteractiveResult> AuthenticateAsync(
        WamAuthRequest request,
        IntPtr parentWindow,
        CancellationToken cancellationToken);
}

// The default, always-disabled authenticator. Present in every build (including
// stable/customer) so the provider surface exists but can never authenticate:
// it returns Disabled without touching MSAL, a broker, or a window. The real
// MSAL-backed authenticator only exists under the EXPERIMENTAL_WAM gate.
internal sealed class DisabledExperimentalWamAuthenticator : IExperimentalWamAuthenticator
{
    internal static DisabledExperimentalWamAuthenticator Instance { get; } = new();

    public Task<WamInteractiveResult> AuthenticateAsync(
        WamAuthRequest request,
        IntPtr parentWindow,
        CancellationToken cancellationToken) =>
        Task.FromResult(WamInteractiveResult.Failure(WamAcquireStatus.Disabled));
}
