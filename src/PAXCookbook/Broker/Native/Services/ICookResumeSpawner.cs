using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- handoff seam between CookControlService's resume
// path and the actual pwsh-cook spawner. Stage 3i-C lands the route
// family and ALL pre-spawn validation (re-auth, cookId shape, row
// exists, closure_reason allowlist, checkpoint file exists, recipe
// loadable, no concurrent cook for the same recipe). The spawn
// itself is delegated to ICookResumeSpawner.
//
// Production: DeferredCookResumeSpawner returns Outcome="deferred"
// with FailureCode="cook_resume_spawn_deferred_native_stage3i".
// The route emits a controlled 501 envelope so SPA clients see a
// clear "Stage 3j wiring not yet present" signal rather than a
// pretend success. This matches the Stage 3i-A /updates/apply
// precedent.
//
// Tests: FakeCookResumeSpawner records the (parentCookId, newCookId,
// recipeId, cookFolder, checkpointPath) tuple and returns
// Outcome="spawned" so the route returns the success envelope and
// the test asserts the new cook row's lineage link.
public interface ICookResumeSpawner
{
    CookResumeSpawnResult Spawn(CookResumeSpawnRequest request);
}
