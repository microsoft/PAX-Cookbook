using System.Security.Cryptography;

namespace PAXCookbook.App;

// Read-only / non-persisting native port of the recipe preview route
// (Invoke-RecipePreview in app\broker\Routes\Recipes.ps1). Validates a recipe
// (stored lookup OR in-memory draft) and projects the authoritative PAX
// invocation plan WITHOUT inserting a recipe row, writing a recipe file,
// creating a cook, invoking PAX, or mutating any user state. The only fills it
// performs are the server-managed draft fields the oracle fills for validation,
// and those live only in the request's in-memory value tree.
internal static class RecipePreviewModel
{
    // Oracle: Invoke-RecipePreview. Returns (httpStatus, body). The body is
    // emitted by the caller via Results.Json; error bodies that carry AJV
    // errors use Dictionary<string,object?> so their keys serialize verbatim.
    public static (int Status, object Body) Handle(
        string workspacePath, string paxScriptPath, VersionInfo versionInfo, object? body,
        Func<RecipeReadModel.OrganizationCookPreparationContext>? organizationAuthority)
    {
        ProjectionResult r = Project(workspacePath, paxScriptPath, versionInfo, body, organizationAuthority);
        if (!r.Ok)
        {
            return (r.Status, r.ErrorBody!);
        }

        PaxAdapter.InvocationPlan plan = r.Plan!;
        return (200, new
        {
            recipeId = JsonModel.Str(r.Recipe!["recipeId"]),
            command = plan.PaxCommand,
            argv = plan.PaxArgv,
            extraArguments = plan.ExtraArguments,
            spawn = new
            {
                command = plan.SpawnCommand,
                argv = plan.SpawnArgv,
            },
        });
    }

    // Outcome of the shared validate + projection pipeline. The preview route
    // (which renders only the authoritative invocation plan) and the readiness
    // route (which layers requirement state on top) both run this exact
    // pipeline, so the command they describe can never diverge.
    internal sealed class ProjectionResult
    {
        public int Status;
        public object? ErrorBody;
        public Dictionary<string, object?>? Recipe;
        public PaxAdapter.InvocationPlan? Plan;
        public PaxAdapter.ChefKeyAuthRow? AuthRow;
        public string AuthMode = string.Empty;
        public string ExecutionMode = string.Empty;
        public bool AppMode;
        public bool Ok => Status == 200;
    }

    // Validates a recipe (stored lookup OR in-memory draft) and projects the
    // authoritative PAX invocation plan. Never inserts a recipe row, writes a
    // recipe file, creates a cook, invokes PAX, or reads the PAX bytes. The
    // only fills it performs are the server-managed draft fields, and those
    // live only in the request's in-memory value tree.
    //
    // Cycle 16 — `organizationAuthority` is the REAL production authority
    // factory. It is required (never defaulted) so no production caller can
    // silently project an organization-bound Recipe against nothing, it is
    // invoked ONLY for an organization-bound Recipe so the personal path does no
    // extra work, and a null factory fails closed.
    internal static ProjectionResult Project(
        string workspacePath, string paxScriptPath, VersionInfo versionInfo, object? body,
        Func<RecipeReadModel.OrganizationCookPreparationContext>? organizationAuthority)
    {
        var result = new ProjectionResult();

        // Oracle: $body = Read-RequestJson; $null -> 400 invalid_json.
        if (body is not Dictionary<string, object?> recipe)
        {
            result.Status = 400;
            result.ErrorBody = new { error = "invalid_json" };
            return result;
        }

        // Discriminator: a bare { recipeId } (no identity) is a stored-recipe
        // lookup; anything that looks like a full recipe is a draft.
        bool isLookup = recipe.ContainsKey("recipeId") && !recipe.ContainsKey("identity");

        if (isLookup)
        {
            string rid = JsonModel.Str(recipe["recipeId"]);
            RecipeReadModel.PreviewLoadResult loaded = RecipeReadModel.LoadForPreview(workspacePath, rid);
            if (loaded.Status != 200)
            {
                result.Status = loaded.Status;
                result.ErrorBody = loaded.ErrorBody!;
                return result;
            }
            recipe = loaded.Recipe!;
        }
        else
        {
            // Draft preview. Fill the server-managed fields the editor does not
            // manage so validation succeeds for a UI body that carries only the
            // human-managed leaves. None of these fills persist.
            if (!recipe.ContainsKey("recipeId")) { recipe["recipeId"] = NewRecipeId(); }
            if (!recipe.ContainsKey("recipeSchemaVersion")) { recipe["recipeSchemaVersion"] = 1L; }
            if (!recipe.ContainsKey("paxAdapterVersion")) { recipe["paxAdapterVersion"] = versionInfo.PaxVersion; }
            if (!recipe.ContainsKey("createdBy")) { recipe["createdBy"] = CreatedByBlock(versionInfo); }

            (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);
            if (!ok)
            {
                result.Status = 400;
                result.ErrorBody = ValidationFailed(errors);
                return result;
            }
        }

        // Resolve auth + executionMode for the projection. App-registration
        // modes require a resolved Chef's Key so the adapter can emit -ClientId
        // (and -ClientCertificateThumbprint for cert mode). The Chef's Key is
        // read from the per-user Windows Credential Manager vault (CK-1):
        // metadata only -- the secret is never read here (constraint 14).
        string authMode = string.Empty;
        string chefKeyId = string.Empty;
        string organizationKeyId = string.Empty;
        if (recipe.ContainsKey("auth") && recipe["auth"] is Dictionary<string, object?> auth)
        {
            if (auth.ContainsKey("mode")) { authMode = JsonModel.Str(auth["mode"]); }
            if (auth.ContainsKey("chefKeyId")) { chefKeyId = JsonModel.Str(auth["chefKeyId"]); }
            if (auth.ContainsKey("organizationKeyId")) { organizationKeyId = JsonModel.Str(auth["organizationKeyId"]); }
        }

        string executionMode = recipe.ContainsKey("executionMode") ? JsonModel.Str(recipe["executionMode"]) : string.Empty;

        PaxAdapter.ChefKeyAuthRow? chefKeyRow = null;
        bool appMode =
            string.Equals(authMode, "AppRegistrationSecret", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(authMode, "AppRegistrationCertificate", StringComparison.OrdinalIgnoreCase);

        // Cycle 14s / Cycle 16 — ORGANIZATION-KEY PROJECTION.
        //
        // An organization-bound Recipe is refused here, before any personal
        // Chef's Key resolution and before PaxAdapter.GetInvocationPlan, unless
        // the REAL production authority (machine policy -> trusted inventory ->
        // certificate catalog -> clock) reports the binding locally ready AND the
        // ACQUIRED engine attests the sanctioned SHA-256 selector capability.
        // The current production engine attests nothing, so this always refuses
        // and NO command, argv, or spawn projection is produced. A null authority
        // fails closed the same way. It never falls back to the personal key
        // path, never derives a thumbprint, and never puts a certificate
        // reference in the body.
        //
        // THIS RESULT GRANTS NO EXECUTION AUTHORITY: it is not stored or cached,
        // and Cook re-evaluates its own fresh snapshot at the pre-Cook gate.
        if (!string.IsNullOrWhiteSpace(organizationKeyId))
        {
            chefKeyRow = RecipeReadModel.TryPrepareOrganizationAuthRow(
                recipe, organizationKeyId, organizationAuthority?.Invoke());
            if (chefKeyRow is null)
            {
                result.Status = 400;
                result.ErrorBody = OrganizationKeyNotYetRunnable();
                return result;
            }
        }
        else if (appMode)
        {
            if (string.IsNullOrWhiteSpace(chefKeyId))
            {
                result.Status = 400;
                result.ErrorBody = ValidationFailed(new List<object>
                {
                    AjvError("/auth/chefKeyId", "required",
                        $"must have required property 'chefKeyId' when auth.mode is '{authMode}'",
                        new Dictionary<string, object?> { ["missingProperty"] = "chefKeyId" }),
                });
                return result;
            }
            ChefKeyModel.ChefKeyResolved? resolved = ChefKeyModel.ResolveForRecipe(chefKeyId);
            if (resolved is null)
            {
                result.Status = 400;
                result.ErrorBody = ValidationFailed(new List<object>
                {
                    AjvError("/auth/chefKeyId", "chefKeyNotFound",
                        $"Chef's Key '{chefKeyId}' does not exist",
                        new Dictionary<string, object?> { ["chefKeyId"] = chefKeyId }),
                });
                return result;
            }
            // The bound Chef's Key's type must map to the recipe's auth.mode.
            if (!string.Equals(resolved.RecipeAuthMode, authMode, StringComparison.OrdinalIgnoreCase))
            {
                result.Status = 400;
                result.ErrorBody = ValidationFailed(new List<object>
                {
                    AjvError("/auth/chefKeyId", "chefKeyModeMismatch",
                        $"Chef's Key '{chefKeyId}' is type '{resolved.AuthType}' but recipe.auth.mode is '{authMode}'",
                        new Dictionary<string, object?> { ["recipeMode"] = authMode, ["chefKeyType"] = resolved.AuthType }),
                });
                return result;
            }
            chefKeyRow = new PaxAdapter.ChefKeyAuthRow(authMode, resolved.ClientId, resolved.CertThumbprint);
        }

        PaxAdapter.InvocationPlan plan;
        try
        {
            plan = PaxAdapter.GetInvocationPlan(recipe, paxScriptPath, chefKeyRow, executionMode);
        }
        catch (Exception ex)
        {
            // Defensive fallback mirroring the oracle's catch: surface any
            // projection-time throw as an AJV-shape error anchored on the only
            // operator-editable input that can influence projection.
            result.Status = 400;
            result.ErrorBody = ValidationFailed(new List<object>
            {
                AjvError("/advanced/extraArguments", "projection", ex.Message, new Dictionary<string, object?>()),
            });
            return result;
        }

        result.Status = 200;
        result.Recipe = recipe;
        result.Plan = plan;
        result.AuthRow = chefKeyRow;
        result.AuthMode = authMode;
        result.ExecutionMode = executionMode;
        result.AppMode = appMode;
        return result;
    }

    private static object ValidationFailed(List<object> errors) =>
        new { error = "validation_failed", errors };

    // Cycle 14s. The single bounded body for an organization-bound Recipe: an
    // explicit, truthful, identifier-free refusal carrying the canonical
    // `organization_key_not_yet_runnable` execution status. It never names the
    // opaque identifier, a thumbprint, a certificate, a store, or any engine
    // internal, and it never advises the customer to repair or reselect.
    private static object OrganizationKeyNotYetRunnable() => new
    {
        error = "validation_failed",
        executionStatus = OrganizationKeyRunnability.ExecutionStatus,
        errors = new List<object>
        {
            AjvError(
                OrganizationKeyRunnability.InstancePath,
                OrganizationKeyRunnability.Keyword,
                OrganizationKeyRunnability.Message,
                new Dictionary<string, object?>
                {
                    ["executionStatus"] = OrganizationKeyRunnability.ExecutionStatus,
                    ["detail"] = OrganizationKeyRunnability.Detail,
                }),
        },
    };

    // AJV-shaped error dict (oracle: New-ValidationError). Built as a
    // Dictionary<string,object?> so the keys serialize verbatim.
    private static Dictionary<string, object?> AjvError(
        string instancePath, string keyword, string message, Dictionary<string, object?> prms) =>
        new()
        {
            ["instancePath"] = instancePath,
            ["keyword"] = keyword,
            ["message"] = message,
            ["params"] = prms,
        };

    // Oracle: Get-RecipeCreatedByBlock. Provenance sourced from authoritative
    // version metadata (VERSION.json). bundledPaxVersion mirrors the oracle's
    // $Script:PaxScriptVersion, which is the bundled PAX version.
    private static Dictionary<string, object?> CreatedByBlock(VersionInfo v) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cookbookVersion"] = v.CookbookVersion,
            ["bundledPaxVersion"] = v.PaxVersion,
            ["releaseChannel"] = v.ReleaseChannel,
        };

    // Oracle: New-RecipeId. 128-bit ULID (Crockford base32, 26 chars): 48-bit
    // ms-since-epoch timestamp (10 chars) + 80-bit randomness (16 chars).
    private static readonly char[] UlidAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

    private static string NewRecipeId()
    {
        long msSinceEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var tsChars = new char[10];
        long v = msSinceEpoch;
        for (int i = 9; i >= 0; i--)
        {
            tsChars[i] = UlidAlphabet[(int)(v & 0x1F)];
            v >>= 5;
        }

        byte[] randBytes = new byte[10];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randBytes);
        }

        var rndChars = new char[16];
        long bitBuf = 0;
        int bitCount = 0;
        int outIdx = 0;
        foreach (byte b in randBytes)
        {
            bitBuf = (bitBuf << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                int idx = (int)((bitBuf >> bitCount) & 0x1F);
                rndChars[outIdx++] = UlidAlphabet[idx];
            }
        }

        return new string(tsChars) + new string(rndChars);
    }
}
