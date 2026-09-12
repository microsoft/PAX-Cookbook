#if PAXCOOKBOOK_TEST_ISOLATION
using System;
using System.Linq;
using System.Text.Json;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.App;

// Build-gated, NON-INTERACTIVE isolation activation proof. This entire file is
// compiled ONLY into a /p:TestIsolation=true build and is absent from every
// stable/customer package, so it can never ship. When --isolation-selfcheck is
// supplied it REQUIRES that the authoritative isolation context already
// activated (TestIsolationRuntime.Current is non-null); otherwise it FAILS
// CLOSED with a distinct nonzero exit and starts nothing — it never probes a
// real per-user root.
//
// It performs only PURE, READ-ONLY resolution of the routed roots and the
// durable configuration: no Kestrel host, no WebView2 window, no broker port, no
// named pipe, no MSAL, no Windows Hello, no network, no engine acquisition, no
// Bake. It writes NOTHING to disk. It emits a single-line JSON object on stdout
// proving every routed root is at-or-under the isolated root, then returns 0.
internal static class IsolationSelfCheck
{
    internal const string Flag = "--isolation-selfcheck";

    // Distinct nonzero exit for "self-check requested but isolation not active".
    // Kept separate from TestIsolationRuntime.IsolationActivationFailureExit (78)
    // so a fall-through (a self-check that somehow ran against a real root) is
    // unambiguous in harness logs.
    internal const int NotIsolatedExit = 79;

    internal static bool IsRequested(string[] args) =>
        args is not null && args.Any(a =>
            string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

    internal static int Run()
    {
        TestIsolationContext? isolation = TestIsolationRuntime.Current;
        if (isolation is null)
        {
            // FAIL CLOSED: never resolve or probe a real installation root.
            try
            {
                Console.Error.WriteLine(
                    "FATAL: --isolation-selfcheck requires an active test-isolation context (fail closed).");
            }
            catch { /* console may be detached */ }
            return NotIsolatedExit;
        }

        // Pure read-only resolution of the routed roots straight from the
        // authoritative context — no seam arguments, no environment, no fallback.
        string localAppDataBase = isolation.LocalStateRoot;
        string workspaceRoot = isolation.Workspace;
        string installRoot = isolation.InstallRoot;
        string coordinationRoot = isolation.CoordinationState;

        // Durable, side-effect-free session-provider selection (file read only).
        SessionProviderSelection providerSel =
            new SessionProviderRuntime(localAppDataBase).GetSelection();
        string provider = providerSel.RecoveryRequired
            ? "recovery_required"
            : SessionProviderStore.ToWire(providerSel.Provider);

        // Durable, side-effect-free WAM configuration read (no endpoint / pipe /
        // MSAL / verification activation).
        ExperimentalWamResolution wam = ExperimentalWamHost.ResolveFromSources(localAppDataBase);
        string wamConfig = wam.Options.IsFullyConfigured ? "configured" : "not_configured";

        var report = new
        {
            isolationActive = true,
            descriptorPath = TestIsolationRuntime.DescriptorPath,
            isolatedRoot = isolation.IsolatedRoot,
            roots = new
            {
                localAppData = localAppDataBase,
                workspace = workspaceRoot,
                install = installRoot,
                coordination = coordinationRoot,
            },
            classifications = new
            {
                localAppData = isolation.ContainsPath(localAppDataBase),
                workspace = isolation.ContainsPath(workspaceRoot),
                install = isolation.ContainsPath(installRoot),
                coordination = isolation.ContainsPath(coordinationRoot),
            },
            provider,
            wamConfig,
            uiStarted = false,
            brokerStarted = false,
            wamStarted = false,
            helloStarted = false,
        };

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = false });
        Console.Out.WriteLine(json);
        return 0;
    }
}
#endif
