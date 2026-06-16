using System.Text.RegularExpressions;

namespace PAXCookbook.App;

// Read-only / non-mutating native port of the bundled PAX adapter projection
// (app\broker\Pax\Adapter.psm1). Computes the canonical PAX argv, the rendered
// PAX command string, and the outer pwsh spawn argv/command for a recipe. This
// is pure string projection: it never reads the PAX engine file, never spawns a
// process, and never touches the filesystem. The PaxScriptPath is used only as
// a literal token inside the rendered spawn expression.
internal static class PaxAdapter
{
    // Thrown by the projection-boundary guards. The preview route translates
    // this into an AJV-shaped validation error anchored on
    // /advanced/extraArguments (oracle parity).
    public sealed class ProjectionException : Exception
    {
        public ProjectionException(string message) : base(message) { }
    }

    // Resolved non-secret Chef's Key fields the projection consumes. The secret
    // itself lives only in Windows Credential Manager and is never read.
    public sealed record ChefKeyAuthRow(string Mode, string? ClientId, string? CertThumbprint);

    // Oracle: $Script:RemovedSwitches (Adapter.psm1).
    private static readonly string[] RemovedSwitches =
    {
        "ExportWorkbook", "ExplodeArrays", "ExplodeDeep", "RawInputCSV",
        "IncludeAgent365Info", "OnlyAgent365Info", "OutputPathAgent365Info", "AppendAgent365Info",
    };

    // Oracle: $Script:ForbiddenInExtraArguments (Adapter.psm1 secret-shape scan).
    private static readonly string[] ForbiddenInExtraArguments =
    {
        "Auth", "TenantId", "ClientId", "ClientSecret", "ClientCertificateThumbprint",
    };

    // Oracle: $Script:LocalAdapterAllowedExecutionModes.
    private static readonly string[] LocalAdapterAllowedExecutionModes =
    {
        "local-manual", "local-scheduled",
    };

    // Oracle: ConvertTo-PaxCommandString $alwaysQuoteValueSwitches.
    private static readonly string[] AlwaysQuoteValueSwitches =
    {
        "-OutputPath", "-AppendFile", "-Resume",
        "-OutputPathUserInfo", "-AppendUserInfo", "-ClientCertificatePath",
    };

    // Case-insensitive '(^|\s)-<name>($|\s|=)' token match.
    private static bool HasSwitch(string trailer, string name)
    {
        if (string.IsNullOrWhiteSpace(trailer))
        {
            return false;
        }
        string pattern = @"(^|\s)-" + Regex.Escape(name) + @"($|\s|=)";
        return Regex.IsMatch(trailer, pattern, RegexOptions.IgnoreCase);
    }

    // Oracle: ConvertTo-QuotedArg. Backtick first, then ", then $.
    public static string ConvertToQuotedArg(string? value)
    {
        if (value is null)
        {
            return "\"\"";
        }
        string escaped = value.Replace("`", "``");
        escaped = escaped.Replace("\"", "\"\"");
        escaped = escaped.Replace("$", "`$");
        return "\"" + escaped + "\"";
    }

    // PAX's -OutputPath is a DIRECTORY/container: PAX writes (and names) its own
    // CSV output files inside it. Recipes author destinations.fact.path as a file
    // (e.g. ...\fact.csv), so when the configured fact path is file-shaped (has a
    // filename with an extension) the PARENT directory is what is handed to
    // -OutputPath; an already-directory-shaped path is passed through unchanged.
    // This reshaping applies ONLY to the write-new (-OutputPath) fact mode.
    // -AppendFile is an explicit named file PAX appends to and is never reshaped,
    // and -OutputPathUserInfo follows PAX's own user-info rules (left unchanged).
    internal static string ResolveFactOutputDirectory(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return outputPath;
        }
        if (Path.HasExtension(outputPath))
        {
            string? parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                return parent;
            }
        }
        return outputPath;
    }

    // Oracle: Test-ExtraArgumentsForRemovedSwitches (throws).
    public static void ScanRemovedSwitches(string extraArguments)
    {
        if (string.IsNullOrWhiteSpace(extraArguments))
        {
            return;
        }
        foreach (string name in RemovedSwitches)
        {
            if (HasSwitch(extraArguments, name))
            {
                throw new ProjectionException(
                    $"advanced.extraArguments contains removed switch '-{name}'. " +
                    "This switch was removed in PAX v1.11.2 and is not reintroduced via the verbatim trailer. " +
                    "Edit the recipe to remove it; the projection layer does not rewrite recipes.");
            }
        }
    }

    // Oracle: Test-ExtraArgumentsForSecretShape (Adapter.psm1 — auth-token scan).
    public static void ScanSecretShape(string extraArguments)
    {
        if (string.IsNullOrWhiteSpace(extraArguments))
        {
            return;
        }
        foreach (string name in ForbiddenInExtraArguments)
        {
            if (HasSwitch(extraArguments, name))
            {
                string hint = name switch
                {
                    "ClientSecret" => "Client secrets are delivered to PAX via the GRAPH_CLIENT_SECRET environment variable, NEVER as a command-line argument. Store the secret in a Chef's Key and bind it to the recipe.",
                    "ClientCertificateThumbprint" => "Certificate thumbprints are emitted automatically from the bound Chef's Key's certThumbprint. Edit the Chef's Key instead of the recipe trailer.",
                    "ClientId" => "Client IDs are emitted automatically from the bound Chef's Key's clientId. Edit the Chef's Key instead of the recipe trailer.",
                    "TenantId" => "TenantId is emitted automatically from recipe.auth.tenantId. Edit the recipe's auth block instead of the trailer.",
                    "Auth" => "Auth mode is emitted automatically from recipe.auth.mode. Edit the recipe's auth block instead of the trailer.",
                    _ => string.Empty,
                };
                string msg = $"advanced.extraArguments contains forbidden auth-related switch '-{name}'.";
                if (!string.IsNullOrEmpty(hint))
                {
                    msg = msg + " " + hint;
                }
                throw new ProjectionException(msg);
            }
        }
    }

    // Oracle: Test-RecipeExecutionModeForLocalAdapter (throws).
    private static void CheckExecutionModeForLocalAdapter(string executionMode)
    {
        string mode = executionMode;
        if (string.IsNullOrWhiteSpace(mode))
        {
            mode = "local-manual";
        }
        if (!LocalAdapterAllowedExecutionModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            throw new ProjectionException(
                $"Recipe executionMode '{mode}' cannot be projected by the local PAX adapter. " +
                "This Cookbook instance runs local cooks only (local-manual / local-scheduled). " +
                "Edit the recipe's executionMode field or run this recipe on a Cookbook instance " +
                "deployed in the matching hosting environment.");
        }
    }

    private static bool CiEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Oracle: Get-PaxArgvArray. Returns the ordered, UNQUOTED logical tokens.
    public static List<string> GetArgvArray(Dictionary<string, object?> recipe, ChefKeyAuthRow? chefKey, string executionMode)
    {
        string execMode = executionMode;
        if (string.IsNullOrWhiteSpace(execMode) && recipe.ContainsKey("executionMode"))
        {
            execMode = JsonModel.Str(recipe["executionMode"]);
        }
        CheckExecutionModeForLocalAdapter(execMode);

        var tokens = new List<string>();

        // Run shape.
        string queryMode = string.Empty;
        Dictionary<string, object?>? query = GetChild(recipe, "query");
        if (query is not null && query.ContainsKey("mode"))
        {
            queryMode = JsonModel.Str(query["mode"]);
        }
        bool isUserInfoOnly = CiEq(queryMode, "userInfoOnly");

        // Pre-read destinations.userInfo.
        Dictionary<string, object?>? dest = GetChild(recipe, "destinations");
        Dictionary<string, object?>? uiHash = dest is null ? null : GetChild(dest, "userInfo");
        string uiMode = string.Empty, uiPath = string.Empty, uiAppendFile = string.Empty;
        if (uiHash is not null)
        {
            if (uiHash.ContainsKey("mode")) { uiMode = JsonModel.Str(uiHash["mode"]); }
            if (uiHash.ContainsKey("path")) { uiPath = JsonModel.Str(uiHash["path"]); }
            if (uiHash.ContainsKey("appendFile")) { uiAppendFile = JsonModel.Str(uiHash["appendFile"]); }
        }
        bool projectingUserInfoDest =
            (CiEq(uiMode, "outputPath") && uiPath.Length > 0) ||
            (CiEq(uiMode, "append") && uiAppendFile.Length > 0);

        // Boolean switches.
        Dictionary<string, object?>? ingredients = GetChild(recipe, "ingredients");
        Dictionary<string, object?>? m365Usage = ingredients is null ? null : GetChild(ingredients, "m365Usage");
        bool includeM365 = m365Usage is not null && m365Usage.ContainsKey("includeM365Usage") && JsonModel.Bool(m365Usage["includeM365Usage"]);

        Dictionary<string, object?>? processing = GetChild(recipe, "processing");
        string rollupTok = string.Empty;
        if (processing is not null && processing.ContainsKey("rollup"))
        {
            string rv = JsonModel.Str(processing["rollup"]);
            if (CiEq(rv, "Rollup")) { rollupTok = "-Rollup"; }
            else if (CiEq(rv, "RollupPlusRaw")) { rollupTok = "-RollupPlusRaw"; }
        }

        // The recipe's dashboard selector is read alongside rollup. Only the
        // AIBV value produces a switch; AIO is PAX's default (omitted) and M365
        // is implied by -IncludeM365Usage (omitted). A missing/unknown value is AIO.
        bool dashboardAibv = processing is not null && processing.ContainsKey("dashboard") && CiEq(JsonModel.Str(processing["dashboard"]), "aibv");

        Dictionary<string, object?>? entraUserData = ingredients is null ? null : GetChild(ingredients, "entraUserData");
        bool includeUserInfo = entraUserData is not null && entraUserData.ContainsKey("includeUserInfo") && JsonModel.Bool(entraUserData["includeUserInfo"]);

        if (isUserInfoOnly)
        {
            tokens.Add("-OnlyUserInfo");
            tokens.Add("-IncludeUserInfo");
        }
        else
        {
            if (includeM365) { tokens.Add("-IncludeM365Usage"); }
            if (rollupTok.Length > 0) { tokens.Add(rollupTok); }
            // -Dashboard AIBV is emitted only with a rollup, never alongside
            // -IncludeM365Usage (PAX rejects that pair), and only when the recipe
            // selected AIBV. The switch name and value are two separate tokens,
            // mirroring -StartDate / -EndDate below.
            if (dashboardAibv && !includeM365 && rollupTok.Length > 0)
            {
                tokens.Add("-Dashboard");
                tokens.Add("AIBV");
            }
            if (includeUserInfo || projectingUserInfoDest) { tokens.Add("-IncludeUserInfo"); }
        }

        // Dates (audit only).
        string startDate = string.Empty, endDate = string.Empty;
        if (query is not null)
        {
            if (query.ContainsKey("startDate")) { startDate = JsonModel.Str(query["startDate"]); }
            if (query.ContainsKey("endDate")) { endDate = JsonModel.Str(query["endDate"]); }
        }
        if (!isUserInfoOnly)
        {
            if (startDate.Length > 0) { tokens.Add("-StartDate"); tokens.Add(startDate); }
            if (endDate.Length > 0) { tokens.Add("-EndDate"); tokens.Add(endDate); }
        }

        // Tenant + auth.
        string tenantId = string.Empty, authMode = string.Empty, chefKeyId = string.Empty;
        Dictionary<string, object?>? auth = GetChild(recipe, "auth");
        if (auth is not null)
        {
            if (auth.ContainsKey("tenantId")) { tenantId = JsonModel.Str(auth["tenantId"]); }
            if (auth.ContainsKey("mode")) { authMode = JsonModel.Str(auth["mode"]); }
            if (auth.ContainsKey("chefKeyId")) { chefKeyId = JsonModel.Str(auth["chefKeyId"]); }
        }
        if (tenantId.Length > 0) { tokens.Add("-TenantId"); tokens.Add(tenantId); }

        string paxAuthValue;
        if (CiEq(authMode, "AppRegistrationSecret") || CiEq(authMode, "AppRegistrationCertificate"))
        {
            paxAuthValue = "AppRegistration";
        }
        else if (authMode.Length == 0)
        {
            paxAuthValue = string.Empty;
        }
        else
        {
            paxAuthValue = authMode;
        }
        if (paxAuthValue.Length > 0) { tokens.Add("-Auth"); tokens.Add(paxAuthValue); }

        if (CiEq(authMode, "AppRegistrationSecret") || CiEq(authMode, "AppRegistrationCertificate"))
        {
            if (chefKey is null)
            {
                throw new ProjectionException(
                    $"Get-PaxArgvArray: recipe.auth.mode is '{authMode}' but no Chef's Key was supplied. " +
                    "The caller (supervisor / preview route) must resolve the Chef's Key by recipe.auth.chefKeyId before projecting.");
            }
            string keyClientId = chefKey.ClientId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(keyClientId))
            {
                throw new ProjectionException(
                    $"Get-PaxArgvArray: Chef's Key '{chefKeyId}' has no clientId. The Chef's Key metadata is malformed.");
            }
            tokens.Add("-ClientId");
            tokens.Add(keyClientId);
            if (CiEq(authMode, "AppRegistrationCertificate"))
            {
                string keyThumb = chefKey.CertThumbprint ?? string.Empty;
                if (string.IsNullOrWhiteSpace(keyThumb))
                {
                    throw new ProjectionException(
                        $"Get-PaxArgvArray: Chef's Key '{chefKeyId}' is mode AppRegistrationCertificate but has no certThumbprint.");
                }
                tokens.Add("-ClientCertificateThumbprint");
                tokens.Add(keyThumb);
            }
        }

        Dictionary<string, object?>? factHash = dest is null ? null : GetChild(dest, "fact");
        string outputPath = string.Empty;
        if (factHash is not null && factHash.ContainsKey("path"))
        {
            outputPath = JsonModel.Str(factHash["path"]);
        }

        // Filter / agent / prompt switches (audit only).
        if (!isUserInfoOnly && query is not null)
        {
            AddArrayValues(tokens, query, "activityTypes", "-ActivityTypes");
            AddArrayValues(tokens, query, "userIds", "-UserIds");
            AddArrayValues(tokens, query, "groupNames", "-GroupNames");

            Dictionary<string, object?>? afHash = GetChild(query, "agentFilter");
            if (afHash is not null)
            {
                string afMode = afHash.ContainsKey("mode") ? JsonModel.Str(afHash["mode"]) : string.Empty;
                if (CiEq(afMode, "agentIds"))
                {
                    List<object?>? aidArr = afHash.ContainsKey("agentIds") ? JsonModel.AsList(afHash["agentIds"]) : null;
                    if (aidArr is not null && aidArr.Count > 0)
                    {
                        tokens.Add("-AgentId");
                        foreach (object? aid in aidArr) { tokens.Add(JsonModel.Str(aid)); }
                    }
                }
                else if (CiEq(afMode, "agentsOnly"))
                {
                    tokens.Add("-AgentsOnly");
                }
                else if (CiEq(afMode, "excludeAgents"))
                {
                    tokens.Add("-ExcludeAgents");
                }
            }

            if (query.ContainsKey("promptFilter"))
            {
                string pf = JsonModel.Str(query["promptFilter"]);
                if (pf.Length > 0) { tokens.Add("-PromptFilter"); tokens.Add(pf); }
            }
        }

        // Fact destination (audit only, unified mode).
        if (!isUserInfoOnly)
        {
            string factMode = string.Empty, factAppendBeh = string.Empty, factAppendFile = string.Empty;
            if (factHash is not null)
            {
                if (factHash.ContainsKey("mode")) { factMode = JsonModel.Str(factHash["mode"]); }
                if (factHash.ContainsKey("appendBehavior")) { factAppendBeh = JsonModel.Str(factHash["appendBehavior"]); }
                if (factHash.ContainsKey("appendFile")) { factAppendFile = JsonModel.Str(factHash["appendFile"]); }
            }
            string effectiveFactMode = string.Empty;
            if (factMode.Length > 0) { effectiveFactMode = factMode; }
            else if (CiEq(factAppendBeh, "append")) { effectiveFactMode = "append"; }
            else if (CiEq(factAppendBeh, "fresh")) { effectiveFactMode = "outputPath"; }
            else if (outputPath.Length > 0) { effectiveFactMode = "outputPath"; }

            if (CiEq(effectiveFactMode, "outputPath"))
            {
                // PAX -OutputPath is the output DIRECTORY (PAX names the file);
                // pass the directory derived from the recipe's file-shaped path.
                if (outputPath.Length > 0) { tokens.Add("-OutputPath"); tokens.Add(ResolveFactOutputDirectory(outputPath)); }
            }
            else if (CiEq(effectiveFactMode, "append"))
            {
                if (factAppendFile.Length > 0) { tokens.Add("-AppendFile"); tokens.Add(factAppendFile); }
            }
        }

        // userInfo destination (both shapes).
        if (CiEq(uiMode, "outputPath"))
        {
            if (uiPath.Length > 0) { tokens.Add("-OutputPathUserInfo"); tokens.Add(uiPath); }
        }
        else if (CiEq(uiMode, "append"))
        {
            if (uiAppendFile.Length > 0) { tokens.Add("-AppendUserInfo"); tokens.Add(uiAppendFile); }
        }

        // -ExcludeCopilotInteraction (audit + includeM365 only).
        if (!isUserInfoOnly && includeM365)
        {
            bool cpKeyExists = false;
            bool cpValue = true;
            if (m365Usage is not null && m365Usage.ContainsKey("includeCopilotInteraction"))
            {
                cpKeyExists = true;
                cpValue = JsonModel.Bool(m365Usage["includeCopilotInteraction"]);
            }
            if (cpKeyExists && !cpValue)
            {
                tokens.Add("-ExcludeCopilotInteraction");
            }
        }

        // Verbatim trailer.
        string extra = string.Empty;
        Dictionary<string, object?>? advanced = GetChild(recipe, "advanced");
        if (advanced is not null && advanced.ContainsKey("extraArguments"))
        {
            extra = JsonModel.Str(advanced["extraArguments"]);
        }
        extra = extra.Trim();
        if (extra.Length > 0)
        {
            ScanRemovedSwitches(extra);
            ScanSecretShape(extra);
            tokens.Add(extra);
        }

        return tokens;
    }

    private static void AddArrayValues(List<string> tokens, Dictionary<string, object?> query, string key, string switchToken)
    {
        if (!query.ContainsKey(key))
        {
            return;
        }
        List<object?>? arr = JsonModel.AsList(query[key]);
        if (arr is null || arr.Count == 0)
        {
            return;
        }
        tokens.Add(switchToken);
        foreach (object? v in arr)
        {
            tokens.Add(JsonModel.Str(v));
        }
    }

    private static Dictionary<string, object?>? GetChild(Dictionary<string, object?> node, string key) =>
        node.ContainsKey(key) ? JsonModel.AsDict(node[key]) : null;

    // Oracle: ConvertTo-PaxCommandString.
    public static string ConvertToCommandString(IReadOnlyList<string> argv)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < argv.Count)
        {
            string token = argv[i];
            if (AlwaysQuoteValueSwitches.Contains(token) && (i + 1) < argv.Count)
            {
                parts.Add(token);
                parts.Add(ConvertToQuotedArg(argv[i + 1]));
                i += 2;
            }
            else
            {
                parts.Add(token);
                i += 1;
            }
        }
        return string.Join(" ", parts);
    }

    public sealed record InvocationPlan(
        IReadOnlyList<string> PaxArgv,
        string ExtraArguments,
        string PaxCommand,
        IReadOnlyList<string> SpawnArgv,
        string SpawnCommand,
        string PaxScriptPath);

    // Oracle: Get-PaxInvocationPlan.
    public static InvocationPlan GetInvocationPlan(Dictionary<string, object?> recipe, string paxScriptPath, ChefKeyAuthRow? chefKey, string executionMode)
    {
        List<string> paxArgv = GetArgvArray(recipe, chefKey, executionMode);
        string paxCommand = ConvertToCommandString(paxArgv);

        string extra = string.Empty;
        Dictionary<string, object?>? advanced = GetChild(recipe, "advanced");
        if (advanced is not null && advanced.ContainsKey("extraArguments"))
        {
            extra = JsonModel.Str(advanced["extraArguments"]).Trim();
        }

        string escapedPath = paxScriptPath.Replace("'", "''");
        string commandExpr = $"& '{escapedPath}' {paxCommand}";
        commandExpr = commandExpr.TrimEnd();

        var spawnArgv = new List<string> { "-NoProfile", "-NoLogo", "-Command", commandExpr };

        string spawnCommand = "pwsh -NoProfile -NoLogo -Command \"" + commandExpr.Replace("\"", "\\\"") + "\"";

        return new InvocationPlan(paxArgv, extra, paxCommand, spawnArgv, spawnCommand, paxScriptPath);
    }
}
