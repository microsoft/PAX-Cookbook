using Microsoft.AspNetCore.Builder;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Routes;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native;

// Stage 3i-C -- composition root for the auth-profile mutation
// surface + auth-profile secret bind/remove + auth-profile
// structural-test + cook stop/kill/resume.
//
// Gating:
//   * workspacePaths + DatabaseFile required (mutation store opens a
//     writeable SQLite handle against the workspace cookbook.sqlite).
//   * sqlite reader required (CookControlService looks up cook +
//     recipe rows for the resume path).
//   * Stage3iCServiceBundle required (carries the Windows re-auth
//     verifier, the CredMan store, the certificate probe, the cook
//     process registry, and the cook resume spawner).
//
// When any gate is unsatisfied none of the route families are
// registered -- any inbound request falls through to the catch-all
// 404. Mirrors the Stage 3i-B3 pattern.
//
// Each route family activates INDEPENDENTLY of the others: e.g. the
// auth-profile family activates when the bundle is present even if
// the cook control family also activates; tests typically wire all
// four. The wiring keeps the four families separate so a future
// stage can hot-swap one without touching the others.
internal static class Stage3iCWiring
{
    public static void Register(
        WebApplication         app,
        WorkspacePaths?        workspacePaths,
        SqliteWorkspaceReader? sqlite,
        Stage3iCServiceBundle? bundle)
    {
        if (workspacePaths is null) return;
        if (string.IsNullOrWhiteSpace(workspacePaths.DatabaseFile)) return;
        if (sqlite is null) return;
        if (bundle is null) return;

        // Defensive: the bundle's required fields are non-null by C#
        // record contract, but a poorly-constructed override might
        // bypass that (e.g. via reflection). Treat the wiring as
        // "fail-closed when fields are absent" and skip silently --
        // matches Stage 3i-B3 semantic.
        if (bundle.ReAuth is null)        return;
        if (bundle.CredStore is null)     return;
        if (bundle.CertProbe is null)     return;
        if (bundle.CookRegistry is null)  return;
        if (bundle.ResumeSpawner is null) return;

        var clock           = bundle.Clock         ?? (() => DateTimeOffset.UtcNow);
        var newAuthId       = bundle.NewAuthProfileId
                              ?? (() => Guid.NewGuid().ToString("D").ToLowerInvariant());
        var newCookIdFactory = bundle.NewCookId
                              ?? (() => Guid.NewGuid().ToString("D").ToLowerInvariant());

        // ---- auth-profile mutation store + services ----
        var profileStore  = new AuthProfileMutationStore(workspacePaths.DatabaseFile);
        var profileMutate = new AuthProfileMutationService(
            store:     profileStore,
            credStore: bundle.CredStore,
            utcNow:    clock,
            newId:     newAuthId);
        var profileSecret = new AuthProfileSecretService(
            store:     profileStore,
            credStore: bundle.CredStore,
            utcNow:    clock);
        var profileTest = new AuthProfileTestService(
            store:     profileStore,
            credStore: bundle.CredStore,
            certProbe: bundle.CertProbe,
            utcNow:    clock);

        AuthProfileMutationRoutes.Register(app, bundle, profileMutate);
        AuthProfileSecretRoutes.Register  (app, bundle, profileSecret);
        AuthProfileTestRoutes.Register    (app, bundle, profileTest);

        // ---- cook control service ----
        var cookControl = new CookControlService(
            reader:        sqlite,
            registry:      bundle.CookRegistry,
            resumeSpawner: bundle.ResumeSpawner,
            newCookId:     newCookIdFactory);
        CookControlRoutes.Register(app, bundle, cookControl);
    }
}
