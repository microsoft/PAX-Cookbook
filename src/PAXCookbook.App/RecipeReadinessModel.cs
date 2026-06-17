namespace PAXCookbook.App;

// Read-only / non-persisting readiness projection for the Mini-Kitchen builder.
// Runs the exact same validate + projection pipeline as the recipe preview
// route (RecipePreviewModel.Project) and layers authoritative requirement state
// on top: PAX engine acquisition, sign-in / Chef's Key, and output destination.
// It answers the question "could this recipe run, and what is still missing?"
// WITHOUT running anything: it never invokes PAX, never spawns a process, never
// creates a cook/bake, never writes a recipe row or file, never reads or mutates
// the PAX bytes, never calls a tenant or Microsoft Graph, and never reads a
// secret. The command/argv it returns is the same authoritative projection the
// preview route renders, so the readiness card and the command preview can
// never describe different invocations.
internal static class RecipeReadinessModel
{
    public static (int Status, object Body) Handle(
        string workspacePath, string paxScriptPath, VersionInfo versionInfo,
        string engineLocalAppDataBase, object? body)
    {
        RecipePreviewModel.ProjectionResult proj =
            RecipePreviewModel.Project(workspacePath, paxScriptPath, versionInfo, body);

        // Engine acquisition is independent of recipe validity, so it is
        // resolved for both the valid and not-yet-valid branches.
        EngineAcquisitionResult engine = EngineAcquisition.Resolve(versionInfo, engineLocalAppDataBase);
        bool engineReady = engine.IsAcquired;
        string engineDetail = engineReady
            ? "The PAX script is installed."
            : "The PAX script has not been installed yet.";

        if (!proj.Ok)
        {
            // The draft is not yet complete or valid. Rather than surface the
            // raw validation HTTP error in the primary UI, translate it into a
            // friendly "needs setup" envelope and preserve the structured
            // validation detail under `details` for the support panel.
            var setupReqs = new List<Req>
            {
                new("recipe-complete", "Recipe details", false,
                    "Some required recipe details still need to be filled in."),
                new("pax-script", "PAX script installed", engineReady, engineDetail),
            };

            return (200, BuildBody(
                ok: false,
                status: "needs-setup",
                summary: "This recipe needs a little more setup before it can run.",
                canPreview: false,
                canRun: false,
                reqs: setupReqs,
                warnings: new List<string>(),
                errors: new List<string> { "Some required recipe details still need to be filled in." },
                engine: engine,
                auth: null,
                destination: null,
                recipeId: null,
                plan: null,
                details: proj.ErrorBody));
        }

        // Projection succeeded: the recipe is structurally valid and any
        // app-registration profile resolved. Derive the remaining run
        // requirements from authoritative state.
        PaxAdapter.InvocationPlan plan = proj.Plan!;
        Dictionary<string, object?> recipe = proj.Recipe!;

        // A bound Chef's Key must match the recipe's sign-in mode. App-registration
        // mismatches are already rejected by the projection above (so the !proj.Ok
        // branch handled them); this also covers the interactive modes (WebLogin /
        // DeviceCode), where a key may be bound for scheduling but the projection
        // does not resolve it. Metadata only -- the secret is never read here
        // (constraint 14).
        string boundChefKeyId = ExtractChefKeyId(recipe);
        ChefKeyModel.ChefKeyResolved? boundChefKey =
            string.IsNullOrWhiteSpace(boundChefKeyId) ? null : ChefKeyModel.ResolveForRecipe(boundChefKeyId);

        (bool authReady, string authDetail, bool needsChefsKey) =
            DeriveAuth(proj.AuthMode, proj.AppMode, proj.AuthRow, boundChefKey);
        (bool destReady, string destDetail, string destKind) = DeriveDestination(recipe);

        var reqs = new List<Req>
        {
            new("recipe-complete", "Recipe details", true, "All required recipe details are present."),
            new("pax-script", "PAX script installed", engineReady, engineDetail),
            new("auth", "Sign-in / Chef's Key", authReady, authDetail),
            new("destination", "Output destination", destReady, destDetail),
        };

        bool allReady = engineReady && authReady && destReady;
        string status;
        string summary;
        if (allReady)
        {
            status = "ready";
            summary = "This recipe is ready to run.";
        }
        else if (!engineReady)
        {
            status = "needs-pax-script";
            summary = "Install the PAX script to finish getting this recipe ready.";
        }
        else if (needsChefsKey)
        {
            status = "needs-chefs-key";
            summary = "This recipe needs a Chef's Key before it can run.";
        }
        else if (!destReady)
        {
            status = "needs-destination";
            summary = "Choose where the results should be written before this recipe can run.";
        }
        else
        {
            status = "needs-setup";
            summary = "This recipe needs a little more setup before it can run.";
        }

        var warnings = new List<string>();
        if (!engineReady) { warnings.Add(engineDetail); }
        if (!authReady) { warnings.Add(authDetail); }
        if (!destReady) { warnings.Add(destDetail); }

        return (200, BuildBody(
            ok: true,
            status: status,
            summary: summary,
            canPreview: true,
            canRun: allReady,
            reqs: reqs,
            warnings: warnings,
            errors: new List<string>(),
            engine: engine,
            auth: new { mode = proj.AuthMode, ready = authReady, detail = authDetail },
            destination: new { kind = destKind, ready = destReady, detail = destDetail },
            recipeId: JsonModel.Str(recipe["recipeId"]),
            plan: plan,
            details: null));
    }

    // Internal requirement tuple. Projected to camelCase anonymous objects for
    // the response because the runtime serializes property names verbatim.
    private sealed record Req(string Id, string Label, bool Met, string Detail);

    private static object BuildBody(
        bool ok, string status, string summary, bool canPreview, bool canRun,
        List<Req> reqs, List<string> warnings, List<string> errors,
        EngineAcquisitionResult engine, object? auth, object? destination,
        string? recipeId, PaxAdapter.InvocationPlan? plan, object? details)
    {
        return new
        {
            ok,
            status,
            summary,
            canPreview,
            // Whether the recipe is ready to run now: the approved engine is
            // acquired, sign-in / Chef's Key is satisfied, and the output
            // destination is set (the same allReady signal the summary uses).
            canRun,
            requirements = reqs.Select(r => new
            {
                id = r.Id,
                label = r.Label,
                met = r.Met,
                detail = r.Detail,
            }).ToList(),
            needsPrep = reqs.Where(r => !r.Met).Select(r => r.Label).ToList(),
            warnings,
            errors,
            engine = new { isAcquired = engine.IsAcquired, state = engine.State },
            auth,
            destination,
            recipeId,
            command = plan?.PaxCommand,
            argv = plan is null ? new List<string>() : plan.PaxArgv,
            extraArguments = plan?.ExtraArguments,
            details,
        };
    }

    // Sign-in / Chef's Key readiness. The secret/certificate material itself is
    // held only by Windows and verified when the recipe runs; this never reads
    // it and never contacts a tenant.
    private static (bool Ready, string Detail, bool NeedsChefsKey) DeriveAuth(
        string authMode, bool appMode, PaxAdapter.ChefKeyAuthRow? row,
        ChefKeyModel.ChefKeyResolved? boundChefKey)
    {
        // A bound Chef's Key whose type does not map to the recipe's sign-in
        // mode can never sign in -- fail readiness with a clear, secret-free
        // message. This applies to every mode (App-registration mismatches are
        // already rejected upstream; the interactive modes are caught here).
        if (boundChefKey is not null &&
            !string.Equals(boundChefKey.RecipeAuthMode, authMode, StringComparison.OrdinalIgnoreCase))
        {
            return (false,
                "The bound Chef's Key type doesn't match this recipe's sign-in mode.",
                false);
        }
        if (appMode)
        {
            if (row is null)
            {
                return (false,
                    "This recipe uses an app registration but no Chef's Key is configured yet.",
                    true);
            }
            return (true,
                "A Chef's Key is configured. Its secret is held by Windows and checked when the recipe runs.",
                false);
        }
        if (string.Equals(authMode, "WebLogin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(authMode, "DeviceCode", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "You'll be asked to sign in when the recipe runs.", false);
        }
        if (string.Equals(authMode, "ManagedIdentity", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "A managed identity is used by the host when the recipe runs.", false);
        }
        return (true, "Sign-in is handled when the recipe runs.", false);
    }

    private static (bool Ready, string Detail, string Kind) DeriveDestination(
        Dictionary<string, object?> recipe)
    {
        if (recipe.TryGetValue("destinations", out object? dRaw) &&
            dRaw is Dictionary<string, object?> dest &&
            dest.TryGetValue("fact", out object? fRaw) &&
            fRaw is Dictionary<string, object?> fact)
        {
            string path = fact.TryGetValue("path", out object? p) ? JsonModel.Str(p) : string.Empty;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return (true, "Results will be written to: " + path, "fact");
            }
        }
        return (false, "No output destination has been chosen yet.", "none");
    }

    // The recipe's bound Chef's Key id (auth.chefKeyId), or empty when none is
    // bound. Metadata lookup only -- never a secret read.
    private static string ExtractChefKeyId(Dictionary<string, object?> recipe)
    {
        if (recipe.TryGetValue("auth", out object? authObj) &&
            authObj is Dictionary<string, object?> auth &&
            auth.TryGetValue("chefKeyId", out object? ck))
        {
            return JsonModel.Str(ck);
        }
        return string.Empty;
    }
}
