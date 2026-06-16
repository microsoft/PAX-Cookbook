using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- production spawner stub for the resume route.
// Returns a controlled 501 deferral envelope because the native
// broker does not yet own the cook execution path (Stage 3j wires
// CookExecutionService + PaxProcessRunner into NativeBrokerHost). The
// route family still ships in Stage 3i-C with full pre-spawn
// validation (re-auth, row lookup, closure_reason allowlist,
// checkpoint presence) so the SPA's Resume button can light up.
// At Stage 3j the production wiring swaps in a real spawner.
public sealed class DeferredCookResumeSpawner : ICookResumeSpawner
{
    public CookResumeSpawnResult Spawn(CookResumeSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CookResumeSpawnResult(
            Outcome:       "deferred",
            FailureCode:   "cook_resume_spawn_deferred_native_stage3i",
            FailureDetail: "Cook resume spawning is wired into NativeBrokerHost in Stage 3j; the native broker accepted the resume request and validated lineage but did not spawn a new pwsh cook.");
    }
}
