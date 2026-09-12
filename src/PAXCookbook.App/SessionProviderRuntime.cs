using System;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.App;

// Thread-safe live authority for the SELECTED session-authentication provider.
//
// Wraps the durable SessionProviderStore behind a process lock so the
// provider-status route, the Settings switch endpoints, and Setup all read and
// write one consistent selection. It owns ONLY the selection identity; the
// health of each provider (Work-account WAM state, Windows Hello enrolment) is
// composed by the caller, keeping selection and health strictly independent.
internal sealed class SessionProviderRuntime
{
    private readonly object _gate = new();
    private readonly string _localAppDataBase;

    internal SessionProviderRuntime(string localAppDataBase)
    {
        _localAppDataBase = localAppDataBase
            ?? throw new ArgumentNullException(nameof(localAppDataBase));
    }

    // Reads the currently selected provider. A missing selection migrates to
    // Windows Hello (historic default); a malformed selection reports
    // RecoveryRequired without guessing a provider.
    internal SessionProviderSelection GetSelection()
    {
        lock (_gate)
        {
            return SessionProviderStore.Load(_localAppDataBase);
        }
    }

    // Atomically persists an explicit, already-verified provider selection made
    // from a running, authenticated session (the Settings switch). Callers MUST
    // have verified the target provider is usable before calling this.
    internal void Select(SelectedSessionProvider provider)
    {
        lock (_gate)
        {
            SessionProviderStore.Save(_localAppDataBase, provider, ProviderSelectionSource.Settings);
        }
    }
}
