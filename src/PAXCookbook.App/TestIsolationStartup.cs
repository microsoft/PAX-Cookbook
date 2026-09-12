using System;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.App;

// Single authoritative router for the per-run ROOTS that must move as a unit
// under test isolation. When the authoritative isolation context is active it is
// the ONE source of truth for the durable local-state base and the workspace
// root; every other seam argument (--engine-localappdata, --workspace) is
// ignored, so a bare --test-isolation launch can never fall through to a real
// installation root for engine acquisition, the WAM configuration, the selected
// session provider, coordination, the index database, or WebView2 user data.
//
// The production resolver is supplied as a LAZY delegate and is invoked ONLY
// when isolation is inactive. Under isolation the delegate is never called, so
// an isolated run provably does not even attempt to resolve a real per-user root
// (the unit tests assert this by passing a throwing delegate that stays
// unthrown). When isolation is inactive the delegate is invoked verbatim, so
// production / stable behavior is byte-for-byte unchanged.
//
// This helper contains no build-gated code and depends only on the always-
// compiled TestIsolationContext contract, so it is safe in stable builds where
// TestIsolationRuntime.Current is always null and every call is a passthrough.
internal static class TestIsolationStartup
{
    // The durable per-user local-state base that anchors engine acquisition
    // state, the WAM configuration, the selected session provider, and the
    // coordination broker.port. Under isolation this is the isolated
    // LocalStateRoot; otherwise the production resolver decides.
    internal static string ResolveLocalAppDataBase(
        TestIsolationContext? isolation,
        Func<string> productionResolver)
    {
        if (productionResolver is null) throw new ArgumentNullException(nameof(productionResolver));
        return isolation is not null ? isolation.LocalStateRoot : productionResolver();
    }

    // The workspace root that holds Runtime, the index database, Auth
    // credentials, and WebView2 user data. Under isolation this is the isolated
    // Workspace; otherwise the production resolver decides.
    internal static string ResolveWorkspaceRoot(
        TestIsolationContext? isolation,
        Func<string> productionResolver)
    {
        if (productionResolver is null) throw new ArgumentNullException(nameof(productionResolver));
        return isolation is not null ? isolation.Workspace : productionResolver();
    }
}
