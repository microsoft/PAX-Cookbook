using System;
using System.Threading;
using System.Threading.Tasks;

namespace PAXCookbook.App;

// Window-side experimental WAM bridge (Track 1 / T1-S2A native request-binding
// + restart-continuity repair).
//
// Runs in the HWND-owning attached-window process. It acts SOLELY on the
// daemon-authored native descriptor (never on any renderer-supplied field): it
// performs the interactive WAM acquisition against the window's REAL HWND,
// validates scope and identity IN THIS PROCESS against the raw claim values, and
// emits ONLY a bounded NeutralWamResult (booleans + category). Tokens and raw
// claim values never leave this process. The bridge holds NO window-scoped
// account state, so a window restart loses nothing: continuity is reconstructed
// from the daemon descriptor plus the MSAL account cache.
internal sealed class ExperimentalWamWindowBridge
{
    private readonly ExperimentalWamOptions _options;
    private readonly IExperimentalWamAuthenticator _authenticator;
    private readonly Func<IntPtr> _hwndProvider;

    internal ExperimentalWamWindowBridge(
        ExperimentalWamOptions options,
        IExperimentalWamAuthenticator authenticator,
        Func<IntPtr> hwndProvider)
    {
        _options = options;
        _authenticator = authenticator;
        _hwndProvider = hwndProvider;
    }

    internal async Task<NeutralWamResult> AcquireAsync(string requestId, WamNativeDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return NeutralWamResult.Rejected(requestId ?? string.Empty, NeutralWamCategory.ConfigurationFailure);
        }

        NeutralWamResult Reject(NeutralWamCategory cat) => NeutralWamResult.Rejected(requestId, cat);

        // The descriptor is the sole authority for the request. A NotFound
        // descriptor grants nothing.
        if (descriptor is null || !descriptor.Found)
        {
            return Reject(NeutralWamCategory.ConfigurationFailure);
        }

        if (_options is null || !_options.IsFullyConfigured)
        {
            return Reject(NeutralWamCategory.ConfigurationFailure);
        }

        // WAM requires the window's REAL HWND. A zero handle fails closed; never
        // fabricate one.
        IntPtr hwnd = _hwndProvider();
        if (hwnd == IntPtr.Zero)
        {
            return Reject(NeutralWamCategory.ConfigurationFailure);
        }

        // Every production session unlock forces account selection. An explicit
        // Lock ends the authenticated session, so the next unlock must render a
        // fresh native picker; a bound preferred account could satisfy WAM
        // without UI even under Prompt.SelectAccount. The mode is decided here
        // and is never accepted from the renderer, HTTP, configuration, or
        // environment.
        var authRequest = new WamAuthRequest(
            descriptor.Purpose, _options.TenantId, _options.ClientId, _options.RequestScope,
            WorkAccountAcquisitionMode.ForceAccountSelection);

        WamInteractiveResult result;
        try
        {
            result = await _authenticator.AuthenticateAsync(authRequest, hwnd, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Reject(NeutralWamCategory.Cancelled);
        }

        if (result is null || !result.Succeeded)
        {
            return Reject(MapStatus(result?.Status ?? WamAcquireStatus.BrokerFailure));
        }

        // Exact scope + identity validation in-process against the raw claim values.
        WamValidation validation = WamScopeIdentityValidator.Validate(result, _options);
        if (validation != WamValidation.Valid)
        {
            bool scopeIssue = validation is WamValidation.ForbiddenScopePresent or WamValidation.UnexpectedScope
                or WamValidation.MalformedScope or WamValidation.NoUserReadScope or WamValidation.MultipleUserReadScopes;
            return Reject(scopeIssue ? NeutralWamCategory.ScopeFailure : NeutralWamCategory.IdentityFailure);
        }

        // Only the request id crosses to the daemon over native IPC.
        return NeutralWamResult.Approved(requestId);
    }

    private static NeutralWamCategory MapStatus(WamAcquireStatus status) => status switch
    {
        WamAcquireStatus.UserCancelled => NeutralWamCategory.Cancelled,
        WamAcquireStatus.BrokerFailure => NeutralWamCategory.BrokerFailure,
        WamAcquireStatus.ConfigurationFailure => NeutralWamCategory.ConfigurationFailure,
        WamAcquireStatus.TransportFailure => NeutralWamCategory.TransportFailure,
        WamAcquireStatus.ScopeFailure => NeutralWamCategory.ScopeFailure,
        WamAcquireStatus.IdentityFailure => NeutralWamCategory.IdentityFailure,
        WamAcquireStatus.Disabled => NeutralWamCategory.Disabled,
        // cycle-02r5b bounded MSAL-failure categories (accurate diagnosis).
        WamAcquireStatus.ConnectivityFailure => NeutralWamCategory.ConnectivityFailure,
        WamAcquireStatus.AuthorityRegistrationMismatch => NeutralWamCategory.AuthorityRegistrationMismatch,
        WamAcquireStatus.ServiceRejected => NeutralWamCategory.ServiceRejected,
        WamAcquireStatus.ConsentRequired => NeutralWamCategory.ConsentRequired,
        WamAcquireStatus.UnknownFailure => NeutralWamCategory.UnknownFailure,
        _ => NeutralWamCategory.BrokerFailure,
    };
}
