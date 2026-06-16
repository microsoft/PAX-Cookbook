using System.Net.Http;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-A -- service bundle wired into NativeBrokerHost to
// override the production defaults for the broker-lifecycle / pax-
// script-export / update / cook-readiness routes. Null in production;
// tests pass a bundle with fakes for the HTTP message handler (so
// /api/v1/updates/* never touches the network), a deterministic
// clock, and a stubbed broker shutdown coordinator (so the test host
// is not actually torn down on POST /api/v1/broker/shutdown).
//
// Doctrine:
//   * Every field is optional. The host uses the corresponding
//     production default when the field is null. This keeps Stage 3a
//     through Stage 3h fixtures passing unchanged.
//   * The bundle does NOT carry any per-test state beyond the
//     deterministic substitutes. State that belongs to the host
//     (UpdateStateStore, etc.) is still owned by NativeBrokerHost.
//   * Tests inject the handler before StartAsync via
//     NativeBrokerHost.WithStage3iAServiceOverride.
public sealed class Stage3iAServiceBundle
{
    // Injectable HttpMessageHandler for the manifest probe. Used by
    // /api/v1/updates/check.
    public HttpMessageHandler? UpdateManifestHttpHandler { get; init; }

    // Injectable HttpMessageHandler for the package downloader.
    // Used by /api/v1/updates/download.
    public HttpMessageHandler? UpdatePackageHttpHandler { get; init; }

    // Deterministic clock for marker file timestamps + UpdateDownloadResult.
    public Func<DateTimeOffset>? Clock { get; init; }

    // Optional shutdown coordinator override. When null the host
    // uses BrokerShutdownCoordinator(IHostApplicationLifetime) which
    // calls StopApplication() on POST /api/v1/broker/shutdown.
    public IBrokerShutdownCoordinator? ShutdownCoordinator { get; init; }
}
