// Real MSAL.NET WAM authenticator — EXPERIMENTAL, gated.
//
// This entire file compiles ONLY when the EXPERIMENTAL_WAM constant is defined
// (set by the ExperimentalWam=true build property, which also adds the MSAL
// package references). Stable / customer builds define no such constant, pull
// in no MSAL package, and contain none of this code — so the stable payload has
// no OAuth broker and cannot perform live authentication. This is the explicit
// packaging boundary required by the experimental gate.
//
// The pattern mirrors the isolated audited WAM proof: PublicClientApplication +
// WithDefaultRedirectUri + WithBroker + WithParentActivityOrWindow(real HWND) +
// the Azure Public common account-picker authority, requesting exactly the delegated
// Microsoft Graph User.Read permission for ONE customer-owned, picker-compatible
// public-client registration (sign-in audience AzureADandPersonalMicrosoftAccount).
// WAM shows its native Windows account picker only for the common audience that
// admits both Entra ID and personal Microsoft accounts. The registration's broad
// audience is a picker-compatibility choice, NOT an authorization grant: PAX
// Cookbook constrains it to exactly the configured tenant by in-process
// validation, enforced before any profile or persistence side effect.
//
// Bounded token / photo doctrine (binding, supersedes the former "never calls
// Graph / tokens never read" language): after a successful acquisition the
// User.Read ACCESS token is read ONLY inside this native process and ONLY to
// perform the single exact request GET /v1.0/me/photo/$value (the signed-in
// user's own profile photo) via the injected bounded photo fetcher. The token is
// never assigned to a field, never serialized, never logged, and never passed
// anywhere else; it is never used for audit, directory, managed-key,
// permission-profile, PAX/Bake, or any other tenant-data query. The raw MSAL
// AuthenticationResult (and its token bytes) NEVER leaves this process: only a
// sanitized, token-free WamInteractiveResult crosses out of the authenticator,
// and the resulting profile presentation is delivered ONLY to the attached-
// window process through the injected sink — never to the daemon /
// NeutralWamResult. A photo failure of any bounded class never blocks
// authorization: the same successful sanitized WamInteractiveResult is returned
// and the presentation simply carries derived initials instead of a photo.

#if EXPERIMENTAL_WAM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace PAXCookbook.App;

internal sealed class MsalEntraWamAuthenticator : IExperimentalWamAuthenticator
{
    // A single PublicClientApplication is reused for the lifetime of this
    // authenticator (one per attached window). Tokens never leave this process;
    // the cache is in-memory only and never persisted to disk.
    private IPublicClientApplication? _app;
    private string? _appKey;
    private readonly object _appLock = new();

    // Window-process-only profile seam. Both are null in the default build path,
    // so no photo request is made and no presentation is produced (behavior is
    // exactly as before). They are injected ONLY in the HWND-owning window
    // process. Neither ever reaches the daemon or NeutralWamResult.
    private readonly IWorkAccountProfilePhotoFetcher? _photoFetcher;
    private readonly IWorkAccountProfilePresentationSink? _profileSink;

    // Window-process-only protected preferred-account seam. Null in the default
    // build path, so acquisition is EXACTLY the prior plain interactive flow (no
    // account preselection, no forced selection, no persistence). Injected ONLY in
    // the HWND-owning window process; never reaches the daemon or NeutralWamResult.
    // The store persists only the opaque DPAPI-protected account reference.
    private readonly IWorkAccountPreferredAccountStore? _preferredAccountStore;

    // Default constructor: no profile seam. Preserves the exact prior behavior
    // for call sites that do not wire the window presentation seam.
    internal MsalEntraWamAuthenticator()
    {
    }

    // Window-process constructor: inject the bounded photo fetcher and the
    // presentation sink. The sink is delivered to the attached-window process
    // only; it is never added to WamInteractiveResult and never crosses to the
    // daemon-bound NeutralWamResult.
    internal MsalEntraWamAuthenticator(
        IWorkAccountProfilePhotoFetcher photoFetcher,
        IWorkAccountProfilePresentationSink profileSink)
    {
        _photoFetcher = photoFetcher;
        _profileSink = profileSink;
    }

    // Window-process constructor: inject the protected preferred-account store
    // only (preferred-account continuity without the photo presentation seam).
    internal MsalEntraWamAuthenticator(IWorkAccountPreferredAccountStore preferredAccountStore)
    {
        _preferredAccountStore = preferredAccountStore;
    }

    // Window-process constructor: inject the full window seam — bounded photo
    // fetcher, presentation sink, and protected preferred-account store. Used by
    // the attached-window process; all three are window-only and never cross to
    // the daemon.
    internal MsalEntraWamAuthenticator(
        IWorkAccountProfilePhotoFetcher photoFetcher,
        IWorkAccountProfilePresentationSink profileSink,
        IWorkAccountPreferredAccountStore preferredAccountStore)
    {
        _photoFetcher = photoFetcher;
        _profileSink = profileSink;
        _preferredAccountStore = preferredAccountStore;
    }

    public async Task<WamInteractiveResult> AuthenticateAsync(
        WamAuthRequest request,
        IntPtr parentWindow,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return WamInteractiveResult.Failure(WamAcquireStatus.ConfigurationFailure);
        }

        if (!Guid.TryParseExact(request.TenantId, "D", out _) ||
            !Guid.TryParseExact(request.ClientId, "D", out _))
        {
            return WamInteractiveResult.Failure(WamAcquireStatus.ConfigurationFailure);
        }

        // WAM requires the attached native window's real HWND. A zero handle is
        // a hard configuration failure — never fabricate one.
        if (parentWindow == IntPtr.Zero)
        {
            return WamInteractiveResult.Failure(WamAcquireStatus.ConfigurationFailure);
        }

        IPublicClientApplication? app = GetOrBuildApp(request.ClientId, request.TenantId);
        if (app is null)
        {
            return WamInteractiveResult.Failure(WamAcquireStatus.ConfigurationFailure);
        }

        // Request exactly the single delegated Microsoft Graph User.Read
        // permission for the one customer-owned, picker-compatible public-client
        // registration. It never uses a refresh token (no explicit offline_access),
        // never requests ".default", and never requests a custom access_as_user
        // scope. The User.Read token is used for exactly one bounded call — the
        // profile-photo GET below — and no other Graph query. The scope is fixed; a
        // request carrying any other scope is a hard configuration failure.
        string requestScope = request.RequestScope ?? string.Empty;
        if (!string.Equals(requestScope, ExperimentalWamOptions.GraphUserReadScope, StringComparison.Ordinal))
        {
            return WamInteractiveResult.Failure(WamAcquireStatus.ConfigurationFailure);
        }

        string[] scopes = { ExperimentalWamOptions.GraphUserReadScope };

        // Preferred-account resolution (window process only). Production session
        // unlock runs in ForceAccountSelection, so no preference is loaded and no
        // account is bound: attended evidence showed that a bound account lets WAM
        // complete without rendering a picker even under Prompt.SelectAccount.
        // Prompt.SelectAccount is still applied on every path, and neither
        // Prompt.ForceLogin nor AcquireTokenSilent is ever used.
        IAccount? boundAccount = null;
        WorkAccountInteractiveAcquisitionPlan acquisitionPlan =
            WorkAccountInteractiveAcquisitionPlan.Create(
                request.AcquisitionMode, storedReference: null, Array.Empty<string>());
        if (_preferredAccountStore is not null)
        {
            string? preferred = request.AcquisitionMode == WorkAccountAcquisitionMode.ForceAccountSelection
                ? null
                : _preferredAccountStore.TryLoad();

            IAccount[] available;
            try
            {
                available = (await app.GetAccountsAsync().ConfigureAwait(false)).ToArray();
            }
            catch (MsalException)
            {
                available = Array.Empty<IAccount>();
            }

            var identifiers = new string[available.Length];
            for (int i = 0; i < available.Length; i++)
            {
                identifiers[i] = available[i].HomeAccountId?.Identifier ?? string.Empty;
            }

            acquisitionPlan = WorkAccountInteractiveAcquisitionPlan.Create(
                request.AcquisitionMode, preferred, identifiers);

            if (acquisitionPlan.BindPreferredAccount)
            {
                boundAccount = available.FirstOrDefault(a =>
                    string.Equals(
                        a.HomeAccountId?.Identifier,
                        acquisitionPlan.BoundIdentifier,
                        StringComparison.Ordinal));
            }
        }

        AuthenticationResult result;
        try
        {
            AcquireTokenInteractiveParameterBuilder builder = app
                .AcquireTokenInteractive(scopes)
                .WithParentActivityOrWindow(parentWindow)
                .WithPrompt(Prompt.SelectAccount);

            // The bound account is a picker hint, never a picker bypass.
            if (boundAccount is not null)
            {
                builder = builder.WithAccount(boundAccount);
            }

            result = await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WamInteractiveResult.Failure(WamAcquireStatus.UserCancelled);
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            return WamInteractiveResult.Failure(WamAcquireStatus.UserCancelled);
        }
        catch (MsalServiceException ex)
        {
            // THIN GLUE (cycle-02r5b): a MsalServiceException means the identity
            // service was REACHED (it returned an error response), so this is NEVER
            // classified as connectivity merely because auth failed. Extract ONLY
            // the non-sensitive shape: whether a response/status is present and the
            // structured error_codes (parsed in-memory via System.Text.Json). The
            // raw ResponseBody, Message, error_description, CorrelationId, Claims,
            // identifiers, and the numeric AADSTS code are NEVER retained, emitted,
            // logged, or passed on — only the bounded WamAcquireStatus leaves here.
            bool hasResponse = ex.StatusCode != 0 || !string.IsNullOrEmpty(ex.ResponseBody);
            var signal = new WamFailureSignal(
                WamFailureKind.Service,
                hasResponse,
                ex.StatusCode != 0 ? ex.StatusCode : (int?)null,
                WamServiceFailureClassifier.ParseErrorCodes(ex.ResponseBody),
                ex.ErrorCode,
                GenuineNoResponseTransport: false);
            return WamInteractiveResult.Failure(WamServiceFailureClassifier.Classify(signal));
        }
        catch (MsalClientException ex)
        {
            // THIN GLUE (cycle-02r5b): a client-side MSAL failure has no service
            // response. A genuine no-response network condition (an inner socket /
            // http / web exception) is the ONLY connectivity path; a documented
            // broker error is broker_failure; anything else is a client/config
            // failure. The error CODE string is inspected transiently in-memory and
            // never emitted — only the bounded category leaves here.
            var signal = new WamFailureSignal(
                IsBrokerErrorCode(ex.ErrorCode) ? WamFailureKind.Broker : WamFailureKind.Client,
                HasHttpResponse: false,
                StatusCode: null,
                ErrorCodes: Array.Empty<int>(),
                MsalErrorCode: ex.ErrorCode,
                GenuineNoResponseTransport: HasNoResponseTransport(ex));
            return WamInteractiveResult.Failure(WamServiceFailureClassifier.Classify(signal));
        }
        catch (MsalException ex)
        {
            // THIN GLUE (cycle-02r5b): an unexpected MSAL exception shape. Connectivity
            // only from a genuine no-response transport; otherwise fail closed to
            // unknown_failure (never connectivity).
            var signal = new WamFailureSignal(
                WamFailureKind.Unknown,
                HasHttpResponse: false,
                StatusCode: null,
                ErrorCodes: Array.Empty<int>(),
                MsalErrorCode: ex.ErrorCode,
                GenuineNoResponseTransport: HasNoResponseTransport(ex));
            return WamInteractiveResult.Failure(WamServiceFailureClassifier.Classify(signal));
        }

        if (result?.Account is null)
        {
            return WamInteractiveResult.Failure(WamAcquireStatus.IdentityFailure);
        }

        // TRIVIAL MSAL GLUE — field reads only. All identity DECISION logic lives
        // in the pure WorkAccountIssuedIdentity.Extract seam below, which is fully
        // unit-tested against synthetic MSAL shapes. This deliberately keeps the
        // only surface not covered by an isolated unit test down to plain field
        // reads (further proven by MsalEntraWamAuthenticator.ReadIssuedFields'
        // synthetic-AuthenticationResult test) so the field mapping cannot silently
        // regress the way it did before this repair.
        //
        // The identity is taken from the ISSUED (resource) tenant + the ISSUED oid
        // claim, NOT from the account's HOME identity:
        //   - issuedTenant = AuthenticationResult.TenantId. A resource-tenant guest's
        //     issued tenant IS the configured resource tenant, so the exact match
        //     still holds; a personal MSA's issued tenant differs and is correctly
        //     rejected. HomeAccountId.TenantId (the account's home tenant) must NOT
        //     drive the authorization decision.
        //   - oid = the ISSUED oid claim from result.ClaimsPrincipal — MSAL's parsed
        //     claim VALUES, not raw token bytes — read with the live-proof ordering:
        //     the short "oid" claim first, then the fully-qualified
        //     objectidentifier URI. The id/access tokens are never read here.
        //   - accountHandle = Account.HomeAccountId.Identifier — the opaque DPAPI
        //     preferred-account handle (an opaque MSAL account reference, unchanged).
        //   - grantedScopes = result.Scopes (unchanged).
        (string? issuedTenant, string? oid, string? accountHandle, IReadOnlyList<string> grantedScopes) =
            ReadIssuedFields(result);

        // Pure, total decision seam. Fails closed BEFORE any preferred-save /
        // photo-fetch / profile-publish / daemon-approval on a missing issued
        // tenant, missing oid, missing account handle, tenant mismatch, or invalid
        // scope — treating empty/whitespace as missing (never null-only coalescing).
        WamInteractiveResult sanitized = WorkAccountIssuedIdentity.Extract(
            issuedTenant, oid, accountHandle, grantedScopes, BuildValidationOptions(request));
        if (!sanitized.Succeeded)
        {
            return sanitized;
        }

        // The ACCESS token is passed to the bounded photo fetcher only AFTER this
        // validation succeeds (inside CompleteValidatedAcquisitionAsync).
        return await CompleteValidatedAcquisitionAsync(
            sanitized,
            request,
            result.AccessToken,
            result.Account.Username,
            cancellationToken).ConfigureAwait(false);
    }

    // TRIVIAL MSAL field-read glue, extracted as an internal static so a
    // synthetic-AuthenticationResult unit test can prove the exact field mapping
    // (ISSUED tenant + ISSUED oid, NOT the account's HOME tenant/object). Reads
    // the ISSUED oid with the live-proof claim ordering: the short "oid" claim
    // first, then the fully-qualified objectidentifier URI. ClaimsPrincipal is
    // MSAL's parsed claim VALUES, never raw token bytes; the id and access tokens
    // are never read, serialized, or logged here.
    internal static (string? IssuedTenant, string? Oid, string? AccountHandle, IReadOnlyList<string> Scopes)
        ReadIssuedFields(AuthenticationResult result)
    {
        string? issuedTenant = result.TenantId;
        string? oid =
            result.ClaimsPrincipal?.FindFirst("oid")?.Value
            ?? result.ClaimsPrincipal?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        string? accountHandle = result.Account?.HomeAccountId?.Identifier;
        IReadOnlyList<string> scopes = result.Scopes?.ToArray() ?? Array.Empty<string>();
        return (issuedTenant, oid, accountHandle, scopes);
    }

    // Builds the exact validation options from the runtime-injected request. Used
    // by both the issued-identity extraction seam and the post-sanitization
    // chokepoint so both validate against the identical configured tenant/client.
    private static ExperimentalWamOptions BuildValidationOptions(WamAuthRequest request) =>
        ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = ExperimentalWamOptions.EntraWamProviderId,
            TenantId = request.TenantId,
            ClientId = request.ClientId,
        });

    // Validates the configured tenant and exact scope immediately after
    // sanitization and before every preference/photo/presentation side effect.
    // Kept as one testable chokepoint so a guest/wrong-tenant account or bad
    // scope fails closed with zero profile/token side effects.
    internal async Task<WamInteractiveResult> CompleteValidatedAcquisitionAsync(
        WamInteractiveResult sanitized,
        WamAuthRequest request,
        string accessToken,
        string? username,
        CancellationToken cancellationToken)
    {
        ExperimentalWamOptions validationOptions = BuildValidationOptions(request);

        WamValidation validation = WamScopeIdentityValidator.Validate(sanitized, validationOptions);
        if (validation != WamValidation.Valid)
        {
            return WamInteractiveResult.Failure(
                WamScopeIdentityValidator.ToAcquireStatus(validation));
        }

        // Preferred-account REPLACEMENT (window process only). Runs ONLY when the
        // store seam is wired. The stored reference is replaced with this
        // session's opaque account handle ONLY after a fully successful scope +
        // identity validated authorization — the exact same validation the window
        // bridge applies before approval — and NEVER on failure or cancel. The
        // persisted value is the opaque HomeAccountId.Identifier, DPAPI-protected
        // by the store; the plaintext reference is never logged or echoed.
        if (_preferredAccountStore is not null)
        {
            if (WorkAccountPreferredAccountReplacementPolicy.ShouldReplace(
                    sanitized, validationOptions, request.AcquisitionMode))
            {
                try
                {
                    _preferredAccountStore.TrySave(sanitized.AccountHandle);
                }
                catch
                {
                    // A persistence failure never blocks authorization.
                }
            }
        }

        // Bounded profile presentation (window process only). Runs ONLY when the
        // window seam is wired. The access token is passed as an immediate
        // argument to the photo fetcher and nowhere else; the account's username
        // is used only to derive initials and is then discarded. A photo failure
        // of any bounded class NEVER changes the authorization outcome: the same
        // successful WamInteractiveResult is returned below and the presentation
        // simply carries initials instead of a photo. The presentation is
        // published ONLY to the attached-window process; it never reaches the
        // daemon or NeutralWamResult.
        if (_photoFetcher is not null && _profileSink is not null)
        {
            WorkAccountPhotoFetchResult? photo = null;
            try
            {
                photo = await _photoFetcher
                    .FetchAsync(accessToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                photo = null; // any failure degrades to initials below
            }

            WorkAccountProfilePresentation presentation =
                WorkAccountProfilePresentationFactory.Build(photo, username);

            try
            {
                _profileSink.Publish(presentation);
            }
            catch
            {
                // Presentation delivery never blocks authorization.
            }
        }

        return sanitized;
    }

    // Native different-account clear (window process only). Deletes the protected
    // preferred-account reference (ownership-scoped to the current user's per-user
    // file), clears any in-memory presentation via the sink, and returns a bounded
    // ack carrying no identifier and no exception detail. On a store-clear failure
    // it returns Failed and never falsely claims success. Requires the store seam;
    // with no store wired there is nothing to clear and it reports Failed.
    internal WorkAccountPreferredClearAck ClearPreferredAccount()
    {
        if (_preferredAccountStore is null)
        {
            return WorkAccountPreferredClearAck.Failed;
        }

        bool cleared;
        try
        {
            cleared = _preferredAccountStore.Clear();
        }
        catch
        {
            cleared = false;
        }

        // Best-effort in-memory presentation clear. It never changes the ack: the
        // authoritative signal is whether the protected reference was removed.
        try
        {
            _profileSink?.Clear();
        }
        catch
        {
            // Never blocks or alters the clear result.
        }

        return cleared ? WorkAccountPreferredClearAck.Cleared : WorkAccountPreferredClearAck.Failed;
    }

    // THIN GLUE (cycle-02r5b): detect a genuine NO-RESPONSE transport condition —
    // an inner socket / http / web exception with no HTTP response. Walks the inner
    // chain in-memory only; no message, host, or address is read, retained, or
    // emitted. Only a boolean leaves this method.
    private static bool HasNoResponseTransport(Exception? ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Http.HttpRequestException ||
                e is System.Net.Sockets.SocketException ||
                e is System.Net.WebException)
            {
                return true;
            }
        }

        return false;
    }

    // THIN GLUE (cycle-02r5b): documented WAM/broker failures surface as client
    // errors whose bounded MSAL error CODE names the broker. The code string is
    // inspected transiently in-memory and never emitted; only the resulting bounded
    // broker category leaves the classifier.
    private static bool IsBrokerErrorCode(string? errorCode)
    {
        if (string.IsNullOrEmpty(errorCode))
        {
            return false;
        }

        return errorCode.Contains("wam", StringComparison.OrdinalIgnoreCase) ||
               errorCode.Contains("broker", StringComparison.OrdinalIgnoreCase);
    }

    // Build once, reuse for this authenticator's lifetime (keyed by client+tenant
    // so a configuration change rebuilds). Reuse keeps the signed-in account in
    // the in-memory MSAL cache across a session so a rebuild (which would clear
    // the cache and force a re-consent) only happens on a real config change.
    private IPublicClientApplication? GetOrBuildApp(string clientId, string tenantId)
    {
        string key = clientId + "|" + tenantId;
        lock (_appLock)
        {
            if (_app is not null && string.Equals(_appKey, key, StringComparison.Ordinal))
            {
                return _app;
            }
            try
            {
                // WAM's native Windows account picker requires the Azure Public
                // common audience that admits Entra ID and personal Microsoft
                // accounts. The customer-owned registration is picker-compatible
                // (audience AzureADandPersonalMicrosoftAccount); its broad audience
                // is constrained to exactly the configured tenant by in-process
                // validation, enforced immediately after sanitization and before
                // every store, photo, or presentation side effect.
                _app = PublicClientApplicationBuilder
                    .Create(clientId)
                    .WithDefaultRedirectUri()
                    .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
                    .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "PAX Cookbook" })
                    .Build();
                _appKey = key;
                return _app;
            }
            catch (MsalException)
            {
                _app = null;
                _appKey = null;
                return null;
            }
        }
    }
}
#endif
