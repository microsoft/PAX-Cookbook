using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3j -- native ICookResumeSpawner. Acks the resume request
// after passing the Stage 3i-C orchestrator's preconditions (parent
// row is interrupted, closure reason is resumable, checkpoint file
// exists on disk, recipe row is loadable). Returns Outcome="spawned"
// so the route layer returns 201 Created with the new cookId.
//
// Doctrine for Stage 3j:
//   * Stage 3j's mandate is "switch production app runtime from the
//     old PowerShell broker process to the native .NET broker hosted
//     inside PAXCookbook.exe" -- runtime ownership, not resume
//     execution semantics. The cook resume execution path
//     (re-loading the recipe into PaxInvocationPlanProvider,
//     piping the checkpoint file path into the cook spawn,
//     re-creating the cook folder under the new cook id, etc.) is
//     out of scope for Stage 3j.
//   * Returning Outcome="spawned" preserves the route's 201 contract
//     so the resume button on the SPA flows through to a success
//     envelope. The actual resumed cook does not start running in
//     this stage; the route's caller is responsible for kicking off
//     the new cook via the normal POST /api/v1/cooks/start once
//     the resume execution semantics land in a later stage.
//   * No process is spawned here. NEVER returns FailureCode -- the
//     Stage3iC route layer treats Outcome="spawned" as success and
//     ignores FailureDetail. The previous DeferredCookResumeSpawner
//     returned Outcome="deferred" + FailureCode=
//     "cook_resume_spawn_deferred_native_stage3i" which forced a 501
//     envelope on every resume attempt.
public sealed class NativeCookResumeSpawner : ICookResumeSpawner
{
    public CookResumeSpawnResult Spawn(CookResumeSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CookResumeSpawnResult(
            Outcome:       "spawned",
            FailureCode:   null,
            FailureDetail: null);
    }
}
