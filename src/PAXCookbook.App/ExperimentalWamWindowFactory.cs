using System;

namespace PAXCookbook.App;

// Window-side experimental WAM bridge factory (Track 1 / T1-S2A repair).
//
// Builds the window bridge with the real attached-window HWND provider and the
// correct authenticator for the build: the real MSAL-backed authenticator ONLY
// under the EXPERIMENTAL_WAM gate, and the always-fail-closed disabled
// authenticator otherwise. The options are resolved from explicit experimental
// runtime configuration, so a stable/default build yields a disabled, inert
// bridge that can never authenticate.
internal static class ExperimentalWamWindowFactory
{
    // The durable per-user local-app-data base the window bridge resolves its
    // options from — the same base the in-process daemon uses (the platform
    // LocalApplicationData folder in production, or the isolated base under the
    // test-only --engine-localappdata override). Set once, early, by Program.Run
    // before any window is created.
    private static string? _localAppDataBase;

    internal static void SetLocalAppDataBase(string? localAppDataBase)
        => _localAppDataBase = localAppDataBase;

    internal static ExperimentalWamWindowBridge Create(Func<IntPtr> hwndProvider)
    {
        // Durable-first resolution, matching the daemon: the window bridge reads
        // the per-user experimental configuration written by Setup/Settings and
        // falls back to the environment gate only when no durable file is
        // present, so a durable install configures the bridge exactly as it
        // configures the daemon endpoint.
        ExperimentalWamOptions options = ExperimentalWamHost.ResolveFromSources(_localAppDataBase).Options;

        IExperimentalWamAuthenticator authenticator =
#if EXPERIMENTAL_WAM
            new MsalEntraWamAuthenticator();
#else
            DisabledExperimentalWamAuthenticator.Instance;
#endif

        return new ExperimentalWamWindowBridge(options, authenticator, hwndProvider);
    }

#if EXPERIMENTAL_WAM
    // Window-side bridge PLUS the bounded Work-account profile presentation
    // channel and the native different-account clear seam. Compiles ONLY under the
    // experimental gate; the stable/default build never has a profile channel and
    // its window uses the ungated, presentation-free Create above (so a stable
    // window never posts a presentation and never handles a different-account
    // intent).
    //
    // Wires the FULL window seam into the real authenticator: the bounded photo
    // fetcher, the presentation channel (delivered ONLY to this window's top-level
    // document), and the DPAPI-protected preferred-account store. The channel's
    // top-level poster is wired later, once the CoreWebView2 is ready.
    internal static ExperimentalWamWindowComponents CreateWithProfile(Func<IntPtr> hwndProvider)
    {
        ExperimentalWamOptions options = ExperimentalWamHost.ResolveFromSources(_localAppDataBase).Options;

        string baseDir = string.IsNullOrWhiteSpace(_localAppDataBase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : _localAppDataBase!;

        var store = new WorkAccountPreferredAccountStore(baseDir);
        var fetcher = new WorkAccountProfilePhotoFetcher();
        var channel = new WorkAccountProfileWindowChannel();
        var authenticator = new MsalEntraWamAuthenticator(fetcher, channel, store);
        var bridge = new ExperimentalWamWindowBridge(options, authenticator, hwndProvider);

        return new ExperimentalWamWindowComponents(bridge, channel, authenticator);
    }
#endif
}

#if EXPERIMENTAL_WAM
// Window-process component set: the bridge, the bounded profile presentation
// channel, and the native different-account clear seam. All three are window-only
// and never cross to the daemon. Compiles ONLY under the experimental gate.
internal sealed class ExperimentalWamWindowComponents
{
    private readonly MsalEntraWamAuthenticator _authenticator;

    internal ExperimentalWamWindowComponents(
        ExperimentalWamWindowBridge bridge,
        WorkAccountProfileWindowChannel channel,
        MsalEntraWamAuthenticator authenticator)
    {
        Bridge = bridge;
        Channel = channel;
        _authenticator = authenticator;
    }

    internal ExperimentalWamWindowBridge Bridge { get; }

    internal WorkAccountProfileWindowChannel Channel { get; }

    // Native different-account clear: deletes the protected preferred-account
    // reference and clears any in-memory presentation via the channel, returning a
    // bounded ack carrying no identifier and no exception detail.
    internal WorkAccountPreferredClearAck ClearPreferredAccount()
        => _authenticator.ClearPreferredAccount();
}
#endif
