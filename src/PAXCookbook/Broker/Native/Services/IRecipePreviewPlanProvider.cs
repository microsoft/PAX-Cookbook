namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B2 -- injectable seam over the recipe -> PAX-invocation
// projection used by /api/v1/recipes/preview.
//
// Production wraps Stage 3e's PaxInvocationPlanProvider (the hidden
// one-shot pwsh sidecar that imports Adapter.psm1). Tests inject a
// stub returning a canned PaxInvocationPlanResult so the suite never
// spawns a real PowerShell child process.
//
// Stage 3i-B2 keeps the same Resolve(recipeJson, paxScriptPath)
// contract Stage 3i-A uses for cook execution. AuthProfile +
// ExecutionMode are NOT forwarded to the sidecar in this stage --
// preview's auth-profile binding gates (profileNotFound /
// profileModeMismatch) run BEFORE the projection so the early-error
// paths stay in PowerShell parity; the projected argv may omit
// -ClientId for AppRegistration recipes until a later stage extends
// the adapter sidecar surface.
public interface IRecipePreviewPlanProvider
{
    PaxInvocationPlanResult Resolve(string recipeJson, string paxScriptPath);
}

// Default production implementation: delegates to the existing
// PaxInvocationPlanProvider verbatim.
public sealed class DefaultRecipePreviewPlanProvider : IRecipePreviewPlanProvider
{
    private readonly PaxInvocationPlanProvider _inner;

    public DefaultRecipePreviewPlanProvider(PaxInvocationPlanProvider inner)
    {
        _inner = inner;
    }

    public PaxInvocationPlanResult Resolve(string recipeJson, string paxScriptPath) =>
        _inner.Resolve(recipeJson, paxScriptPath);
}
