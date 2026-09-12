using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.Service;

/// <summary>
/// Explicitly DISABLED service-side cook preparation adapter.
///
/// The service links the portable <see cref="CookPreparationSequence"/> so
/// there is ONE source of the cook preparation phase order across both hosts.
/// Linking that order grants the service NO capability, and this type is the
/// proof: it refuses on the FIRST phase the sequence requests, with a bounded
/// non-secret disposition, and never reaches authorization or preparation.
///
/// It performs NO filesystem, SQLite, process, network, registry, certificate,
/// credential, WAM, Windows Hello, WebView2, or WinForms operation. It reads no
/// configuration and honours no override: nothing a caller supplies can turn it
/// on.
///
/// It has NO production caller. <c>Program</c> and <c>StartupProbeWorker</c> do
/// not reference it, and the service test project asserts that. Compile-linking
/// the phase order does NOT make service cook execution ready; the service host
/// remains unimplemented and this type exists so that fact is enforced in code
/// rather than asserted in a comment.
/// </summary>
internal sealed class DisabledServiceCookPreparationAdapter : ICookPreparationAdapter
{
    /// <summary>The bounded reason recorded by the first phase the sequence requested.</summary>
    internal ServiceCookPreparationRefusal Refusal { get; private set; } =
        ServiceCookPreparationRefusal.NotRequested;

    /// <summary>How many phases the sequence was able to request. A fail-closed run stops at 1.</summary>
    internal int PhasesRequested { get; private set; }

    public bool EvaluateGatesPhase() => Refuse(ServiceCookPreparationRefusal.GatesNotEnabledInService);

    // The service serves NO trigger, so every authorization phase is a
    // cross-trigger dead end and every one of them refuses. None may ever return
    // true: a true here would authorize a mis-routed trigger inside a host that
    // has no cook implementation at all.
    public bool AuthorizeManualPhase() => Refuse(ServiceCookPreparationRefusal.AuthorizationNotEnabledInService);

    public bool AuthorizeScheduledPhase() => Refuse(ServiceCookPreparationRefusal.AuthorizationNotEnabledInService);

    public bool AuthorizeResumePhase() => Refuse(ServiceCookPreparationRefusal.AuthorizationNotEnabledInService);

    public bool PreparePhase() => Refuse(ServiceCookPreparationRefusal.PreparationNotEnabledInService);

    private bool Refuse(ServiceCookPreparationRefusal reason)
    {
        PhasesRequested++;
        if (Refusal == ServiceCookPreparationRefusal.NotRequested)
        {
            Refusal = reason;
        }
        return false;
    }
}

/// <summary>
/// Closed set of service-side preparation refusals. No free-form text, no
/// exception message, no path, and no identifier is ever emitted. The zero value
/// means the sequence never asked, so a default-initialised value can never read
/// as an authorized phase.
/// </summary>
internal enum ServiceCookPreparationRefusal
{
    NotRequested = 0,
    GatesNotEnabledInService = 1,
    AuthorizationNotEnabledInService = 2,
    PreparationNotEnabledInService = 3,
}
