using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Routes;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native;

// Stage 3i-A -- composition root for the broker-lifecycle / pax-
// script-export / update / cook-readiness routes. The host calls
// Register() once per StartAsync after the lock + WebAuthn surfaces
// are wired. Each route family is gated on the input it requires:
//
//   /api/v1/broker/close-intent  -> workspacePaths.RuntimeDir
//   /api/v1/broker/shutdown      -> always (uses IHostApplicationLifetime
//                                    or the override coordinator)
//   /api/v1/runtime/pax-script/download
//                                -> paxScriptPath + versionInfo.BundledPax
//   /api/v1/updates/state        -> versionInfo (read-only snapshot
//                                    of in-memory state)
//   /api/v1/updates/check        -> versionInfo + workspacePaths
//   /api/v1/updates/download     -> versionInfo + workspacePaths
//   /api/v1/updates/apply        -> always (controlled 501)
//   /api/v1/cooks/readiness      -> workspacePaths
//
// When a gate is unsatisfied the route is intentionally NOT
// registered -- the unmatched API path returns 404, matching the
// pre-Stage-3i-A behaviour. This keeps Stage 3a/3b fixtures
// (no AppRoot, no workspace) passing unchanged.
internal static class Stage3iAWiring
{
    public static void Register(
        WebApplication              app,
        WorkspacePaths?             workspacePaths,
        VersionInfo?                versionInfo,
        string?                     paxScriptPath,
        Stage3iAServiceBundle?      overrideBundle,
        IHostApplicationLifetime    appLifetime)
    {
        var clock = overrideBundle?.Clock ?? (() => DateTimeOffset.UtcNow);

        // ---- shutdown coordinator (always wired) ----
        var shutdown = overrideBundle?.ShutdownCoordinator
                    ?? new BrokerShutdownCoordinator(appLifetime);

        // ---- broker/close-intent : requires Runtime dir ----
        if (workspacePaths is not null
            && !string.IsNullOrWhiteSpace(workspacePaths.RuntimeDir))
        {
            var writer = new BrokerCloseIntentWriter(workspacePaths.RuntimeDir, clock);
            BrokerLifecycleRoutes.Register(app, writer, shutdown);
        }
        else
        {
            // shutdown can still be registered without a workspace.
            // Register a minimal lifecycle binding that only exposes
            // /broker/shutdown (close-intent stays unregistered ->
            // unmatched /broker/close-intent returns 404).
            BrokerLifecycleRoutes.RegisterShutdownOnly(app, shutdown);
        }

        // ---- runtime/pax-script/download : requires PaxScript + version ----
        if (!string.IsNullOrWhiteSpace(paxScriptPath)
            && versionInfo is not null
            && versionInfo.BundledPax is not null)
        {
            var reader = new PaxScriptExportReader(
                paxScriptPath!,
                versionInfo.BundledPax.Version);
            RuntimeDownloadRoutes.Register(app, reader);
        }

        // ---- updates/* : requires VERSION.json ----
        if (versionInfo is not null && workspacePaths is not null)
        {
            var store = new UpdateStateStore();
            var probe = new UpdateManifestProbe(
                versionInfo.CookbookVersion ?? string.Empty,
                handler: overrideBundle?.UpdateManifestHttpHandler);
            var parser = new UpdateManifestParser();
            var downloader = new UpdatePackageDownloader(
                workspacePath:        workspacePaths.WorkspaceFolderPath,
                cookbookVersionHint:  versionInfo.CookbookVersion ?? string.Empty,
                handler:              overrideBundle?.UpdatePackageHttpHandler,
                clock:                clock);

            UpdateConfigContext ContextFactory() => new UpdateConfigContext(
                ManifestUrl:            versionInfo.UpdateManifestUrl,
                CurrentCookbookVersion: versionInfo.CookbookVersion,
                CurrentReleaseChannel:  versionInfo.ReleaseChannel,
                BundledPaxVersion:      versionInfo.BundledPax?.Version,
                BundledPaxSha256:       versionInfo.BundledPax?.Sha256);

            UpdateRoutes.Register(
                app:             app,
                store:           store,
                probe:           probe,
                parser:          parser,
                downloader:      downloader,
                contextFactory:  ContextFactory);
        }
        else
        {
            // /updates/apply is the only route that must be reachable
            // even without VERSION.json -- it returns a controlled 501.
            UpdateRoutes.RegisterApplyOnly(app);
        }

        // ---- cooks/readiness : requires workspace + PaxScript+VersionInfo ----
        if (workspacePaths is not null)
        {
            var sqlite = new SqliteWorkspaceReader(workspacePaths);
            PaxScriptIntegrityVerifier? integrity = null;
            if (versionInfo is not null
                && !string.IsNullOrWhiteSpace(paxScriptPath)
                && File.Exists(paxScriptPath))
            {
                integrity = new PaxScriptIntegrityVerifier(versionInfo, paxScriptPath!);
            }
            var probe = new CookReadinessProbe(sqlite, integrity, clock);
            CookReadinessRoutes.Register(app, probe);
        }
    }
}
