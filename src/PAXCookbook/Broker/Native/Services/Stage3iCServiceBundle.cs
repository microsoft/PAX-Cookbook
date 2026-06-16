namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- composition bundle handed to Stage3iCWiring at
// MapApiRoutes time. Mirrors Stage 3i-A / 3i-B1 / 3i-B2 / 3i-B3
// shape: a record with required production seams (ReAuth verifier,
// CredMan store, certificate probe, clock, cook process registry,
// cook resume spawner) and optional overrides for tests.
//
// Doctrine:
//   * The bundle is null in production for now; the wiring layer
//     composes a "live" bundle from the host options + the singleton
//     Windows services (WindowsReAuthVerifier, WindowsCredentialSecret
//     Store, WindowsCertificateProbe, InMemoryCookProcessRegistry,
//     DeferredCookResumeSpawner).
//   * Tests inject a bundle with Fake* implementations of every
//     seam. The host accepts the override via WithStage3iCService
//     Override at construction time, and the wiring layer treats the
//     override as the authoritative bundle (no fallback merge --
//     tests must provide every required field).
//   * BrokerLockService is intentionally NOT in this bundle. The
//     lock-activity bump after a successful re-auth is performed
//     inside the route layer via the (optional) BrokerLockService
//     handed to Stage3iCWiring.Register, not via a separate seam in
//     the bundle. This matches the shape of Stage3hServiceBundle.
public sealed record Stage3iCServiceBundle
{
    public required IWindowsReAuthVerifier ReAuth        { get; init; }
    public required ICredentialSecretStore CredStore     { get; init; }
    public required ICertificateProbe      CertProbe     { get; init; }
    public required ICookProcessRegistry   CookRegistry  { get; init; }
    public required ICookResumeSpawner     ResumeSpawner { get; init; }
    public required Func<DateTimeOffset>   Clock         { get; init; }

    // Optional. When null the wiring layer falls back to
    // Guid.NewGuid().ToString("D").ToLowerInvariant() for both
    // auth-profile ids and new cook ids. Tests inject deterministic
    // factories so envelope ids are byte-stable across runs.
    public Func<string>? NewAuthProfileId { get; init; }
    public Func<string>? NewCookId        { get; init; }

    // Optional. When supplied, the route layer calls TouchActivity()
    // after every successful re-auth on the four gated routes. Null
    // in tests that don't exercise the lock pathway.
    public BrokerLockService? LockService { get; init; }
}
