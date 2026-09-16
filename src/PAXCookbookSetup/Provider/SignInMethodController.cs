using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provider;

// Drives the Setup wizard's mutually-exclusive sign-in-method step and its
// transactional final write. The WinForms panel is a thin shell over this
// controller so the selection/staging/gating/commit logic is testable without
// a UI.
//
// Work-account durable config and verification are staged under a Setup-owned
// temporary base first; the native-WAM test runs against the staged config; and
// on commit the staged files are copied into the final Config folder and the
// provider selection is written LAST. Any failure rolls back to the prior state
// with no partial selection and no partial local WAM configuration.
public sealed class SignInMethodController
{
    // App file names (parity with PAXCookbook.App stores — kept in sync).
    private const string WamConfigFileName = "experimental-wam.json";
    private const string WamVerificationFileName = "experimental-wam-verification.json";
    private const string EntraWamProviderId = "entra-wam";
    private const int WamConfigSchemaVersion = 2;
    private const int WamVerificationSchemaVersion = 1;

    private readonly bool _isExperimental;
    private readonly string _localAppDataBase;
    private readonly string _stagingBase;
    private readonly Func<string> _appExePath;
    private readonly IProcessLauncher _launcher;
    private readonly IHelloSupportProbe _helloProbe;
    private readonly object _nativeTestSync = new();
    private int _nativeTestGeneration;
    private AppLaunchWorkAccountGate? _nativeGate;
    private bool _nativeTestRunning;

    private string _tenantId = string.Empty;
    private string _clientId = string.Empty;

    public SignInMethodController(
        bool isExperimentalChannel,
        string localAppDataBase,
        string stagingBase,
        string appExePath,
        IProcessLauncher launcher,
        IHelloSupportProbe helloProbe)
        : this(isExperimentalChannel, localAppDataBase, stagingBase,
            () => appExePath, launcher, helloProbe)
    {
        ArgumentNullException.ThrowIfNull(appExePath);
    }

    public SignInMethodController(
        bool isExperimentalChannel,
        string localAppDataBase,
        string stagingBase,
        Func<string> appExePath,
        IProcessLauncher launcher,
        IHelloSupportProbe helloProbe)
    {
        _isExperimental = isExperimentalChannel;
        _localAppDataBase = localAppDataBase ?? throw new ArgumentNullException(nameof(localAppDataBase));
        _stagingBase = stagingBase ?? throw new ArgumentNullException(nameof(stagingBase));
        _appExePath = appExePath ?? throw new ArgumentNullException(nameof(appExePath));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _helloProbe = helloProbe ?? throw new ArgumentNullException(nameof(helloProbe));
    }

    public SignInProviderChoice Choice { get; private set; } = SignInProviderChoice.WindowsHello;

    public bool WorkConfigStaged { get; private set; }

    public bool WorkVerified { get; private set; }

    public bool NativeTestPassed { get; private set; }

    public bool WorkAccountOfferedInChannel => _isExperimental;

    public bool HelloAvailable => _helloProbe.IsWindowsHelloAvailable();

    public void ChooseWindowsHello() => Choice = SignInProviderChoice.WindowsHello;

    public void ChooseWorkAccount()
    {
        if (_isExperimental)
        {
            Choice = SignInProviderChoice.WorkAccount;
        }
    }

    private string StagedConfigDir => Path.Combine(_stagingBase, "PAXCookbook", "Config");
    public string StagedConfigPath => Path.Combine(StagedConfigDir, WamConfigFileName);
    private string StagedVerificationPath => Path.Combine(StagedConfigDir, WamVerificationFileName);

    // Imports configuration and optional portable IT verification. Native-test
    // success is always local and is never imported.
    public bool ImportSetupResult(string setupResultPath)
    {
        WorkConfigStaged = false;
        WorkVerified = false;
        NativeTestPassed = false;
        _tenantId = string.Empty;
        _clientId = string.Empty;
        try
        {
            Directory.CreateDirectory(StagedConfigDir);
            File.Delete(StagedVerificationPath);
            using FileStream input = File.OpenRead(setupResultPath);
            if (input.Length > 64 * 1024)
            {
                return false;
            }
            byte[] bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'),
                new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasUniqueProperties(root))
            {
                return false;
            }
            string? tenant = root.TryGetProperty("tenantId", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            string? client = root.TryGetProperty("clientAppId", out JsonElement c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            if (!Guid.TryParseExact(tenant, "D", out _) || !Guid.TryParseExact(client, "D", out _))
            {
                return false;
            }

            bool hasVerification = root.TryGetProperty("grantResult", out _)
                || root.TryGetProperty("configFingerprint", out _)
                || root.TryGetProperty("outcome", out _)
                || (root.TryGetProperty("resultKind", out JsonElement resultKind)
                    && resultKind.ValueKind == JsonValueKind.String && resultKind.GetString() == "verify");
            bool hasEnvelope = root.TryGetProperty("kind", out _)
                || root.TryGetProperty("resultKind", out _)
                || root.TryGetProperty("schemaVersion", out _)
                || root.TryGetProperty("providerId", out _)
                || root.TryGetProperty("providerVersion", out _)
                || root.TryGetProperty("authorizationModel", out _)
                || root.TryGetProperty("structuralVerification", out _)
                || root.TryGetProperty("verifiedUtc", out _)
                || root.TryGetProperty("helper", out _);
            if (hasVerification ? !IsVerifiedImport(root, tenant!, client!)
                : hasEnvelope && !IsProvisionImport(root))
            {
                return false;
            }

            File.WriteAllText(StagedConfigPath, BuildConfigJson(tenant!, client!),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (hasVerification)
            {
                File.WriteAllText(StagedVerificationPath,
                    BuildVerificationJson(tenant!, client!, root.GetProperty("verifiedUtc").GetString(),
                        root.GetProperty("configFingerprint").GetString()),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            _tenantId = tenant!;
            _clientId = client!;
            WorkConfigStaged = true;
            WorkVerified = hasVerification;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProvisionImport(JsonElement root)
    {
        if (root.GetProperty("kind").GetString() != "pax-cookbook-wam-setup-result"
            || root.GetProperty("resultKind").GetString() != "provision")
        {
            return false;
        }
        bool hasMetadata = root.TryGetProperty("schemaVersion", out _)
            || root.TryGetProperty("providerId", out _)
            || root.TryGetProperty("providerVersion", out _)
            || root.TryGetProperty("authorizationModel", out _)
            || root.TryGetProperty("structuralVerification", out _)
            || root.TryGetProperty("verifiedUtc", out _)
            || root.TryGetProperty("helper", out _);
        if (!hasMetadata) return true;
        if (!HasExactProperties(root, "schemaVersion", "kind", "resultKind", "providerId", "providerVersion",
            "authorizationModel", "tenantId", "clientAppId", "structuralVerification", "verifiedUtc", "helper")
            || !root.GetProperty("schemaVersion").TryGetInt32(out int schemaVersion) || schemaVersion != 1
            || root.GetProperty("providerId").GetString() != EntraWamProviderId
            || root.GetProperty("providerVersion").GetString() != "1"
            || root.GetProperty("authorizationModel").GetString() != "single-configured-tenant")
        {
            return false;
        }
        JsonElement helper = root.GetProperty("helper");
        JsonElement structural = root.GetProperty("structuralVerification");
        if (!HasExactProperties(helper, "ownershipTag", "name")
            || helper.GetProperty("ownershipTag").GetString() != "pax-cookbook-wam-provisioner"
            || helper.GetProperty("name").GetString() != "New-PaxCookbookEntraWamSetup"
            || !HasExactProperties(structural, "success", "checks")
            || structural.GetProperty("success").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        JsonElement checks = structural.GetProperty("checks");
        if (!HasExactProperties(checks, "applicationExists", "servicePrincipalExists", "pickerCompatibleAudience",
            "wamRedirectExact", "graphUserReadOnly", "noAppPermission", "noCustomScope", "noCredential",
            "noUnexpectedRedirect", "allPrincipalsUserReadGrant"))
        {
            return false;
        }
        foreach (JsonProperty check in checks.EnumerateObject())
        {
            if (check.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        }
        JsonElement timestamp = root.GetProperty("verifiedUtc");
        string? verifiedUtc = timestamp.ValueKind == JsonValueKind.String ? timestamp.GetString() : null;
        return verifiedUtc is not null
            && (verifiedUtc.EndsWith("Z", StringComparison.Ordinal) || verifiedUtc.EndsWith("+00:00", StringComparison.Ordinal))
            && timestamp.TryGetDateTimeOffset(out DateTimeOffset parsedUtc) && parsedUtc.Offset == TimeSpan.Zero;
    }

    private static bool IsVerifiedImport(JsonElement root, string tenantId, string clientId)
    {
        if (!HasExactProperties(root, "schemaVersion", "kind", "resultKind", "providerId", "providerVersion",
            "authorizationModel", "tenantId", "clientAppId", "structuralVerification", "grantResult",
            "configFingerprint", "verifiedUtc")
            || !root.GetProperty("schemaVersion").TryGetInt32(out int schemaVersion) || schemaVersion != 1
            || root.GetProperty("kind").GetString() != "pax-cookbook-wam-setup-result"
            || root.GetProperty("resultKind").GetString() != "verify"
            || root.GetProperty("providerId").GetString() != EntraWamProviderId
            || root.GetProperty("providerVersion").GetString() != "1"
            || root.GetProperty("authorizationModel").GetString() != "single-configured-tenant")
        {
            return false;
        }

        JsonElement structural = root.GetProperty("structuralVerification");
        JsonElement grant = root.GetProperty("grantResult");
        if (!HasExactProperties(structural, "success", "checks")
            || structural.GetProperty("success").ValueKind != JsonValueKind.True
            || !HasExactProperties(grant, "allPrincipalsUserRead")
            || grant.GetProperty("allPrincipalsUserRead").ValueKind != JsonValueKind.True)
        {
            return false;
        }
        JsonElement checks = structural.GetProperty("checks");
        if (!HasExactProperties(checks, "applicationExists", "servicePrincipalExists", "pickerCompatibleAudience",
            "wamRedirectExact", "graphUserReadOnly", "noAppPermission", "noCustomScope", "noCredential",
            "noUnexpectedRedirect", "allPrincipalsUserReadGrant"))
        {
            return false;
        }
        foreach (JsonProperty check in checks.EnumerateObject())
        {
            if (check.Value.ValueKind != JsonValueKind.True) return false;
        }

        JsonElement timestamp = root.GetProperty("verifiedUtc");
        string? verifiedUtc = timestamp.ValueKind == JsonValueKind.String ? timestamp.GetString() : null;
        return verifiedUtc is not null
            && (verifiedUtc.EndsWith("Z", StringComparison.Ordinal) || verifiedUtc.EndsWith("+00:00", StringComparison.Ordinal))
            && timestamp.TryGetDateTimeOffset(out DateTimeOffset parsedUtc) && parsedUtc.Offset == TimeSpan.Zero
            && string.Equals(root.GetProperty("configFingerprint").GetString(), ComputeFingerprint(tenantId, clientId),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExactProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!remaining.Remove(property.Name)) return false;
        }
        return remaining.Count == 0;
    }

    private static bool HasUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || !HasUniqueProperties(property.Value)) return false;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (!HasUniqueProperties(item)) return false;
            }
        }
        return true;
    }

    // Runs exactly one native-WAM test against the staged config. Requires the
    // config to be staged first.
    public bool RunNativeTest(string resultFilePath)
        => RunNativeTestAsync(resultFilePath, System.Threading.CancellationToken.None).GetAwaiter().GetResult();

    public async System.Threading.Tasks.Task<bool> RunNativeTestAsync(
        string resultFilePath, System.Threading.CancellationToken cancel)
    {
        InvalidateNativeTest();
        int generation;
        lock (_nativeTestSync)
        {
            if (_nativeTestRunning || _nativeGate is not null || !WorkConfigStaged || cancel.IsCancellationRequested)
                return false;
            generation = _nativeTestGeneration;
            _nativeTestRunning = true;
        }
        try
        {
            var gate = new AppLaunchWorkAccountGate(_stagingBase, _appExePath(), _launcher, resultFilePath);
            lock (_nativeTestSync) _nativeGate = gate;
            bool approved = await gate.RunNativeWamAuthTestAsync(cancel).ConfigureAwait(false);
            lock (_nativeTestSync)
            {
                NativeTestPassed = approved && !cancel.IsCancellationRequested && generation == _nativeTestGeneration;
                return NativeTestPassed;
            }
        }
        finally
        {
            lock (_nativeTestSync)
            {
                _nativeTestRunning = false;
                if (_nativeGate?.HasOutstandingNativeChild != true) _nativeGate = null;
            }
        }
    }

    public bool HasOutstandingNativeChild
    {
        get { lock (_nativeTestSync) return _nativeGate?.HasOutstandingNativeChild == true; }
    }

    public async System.Threading.Tasks.Task<bool> RetryNativeCleanupAsync()
    {
        InvalidateNativeTest();
        AppLaunchWorkAccountGate? gate;
        lock (_nativeTestSync)
        {
            if (_nativeTestRunning) return false;
            gate = _nativeGate;
        }
        if (gate is null) return true;
        if (!await gate.RetryNativeCleanupAsync().ConfigureAwait(false)) return false;
        lock (_nativeTestSync)
        {
            if (ReferenceEquals(_nativeGate, gate)) _nativeGate = null;
        }
        return true;
    }

    public void InvalidateNativeTest()
    {
        lock (_nativeTestSync)
        {
            _nativeTestGeneration++;
            NativeTestPassed = false;
        }
    }

    // Whether the wizard may proceed to installation with the current selection.
    public bool CanContinue()
        => Choice == SignInProviderChoice.WindowsHello
            ? HelloAvailable
            : _isExperimental && WorkConfigStaged && WorkVerified && NativeTestPassed;

    // Final transactional write, called AFTER installation files are in place.
    // Copies the staged work-account config + verification into the final Config
    // folder (work account only), then writes the provider selection LAST. On any
    // failure the prior selection is restored and no partial WAM config remains.
    public ProviderSetupResult Commit() => Commit(_localAppDataBase);

    public ProviderSetupResult Commit(string localAppDataBase)
    {
        var writer = new ProviderSelectionWriter(localAppDataBase);
        writer.Snapshot();

        try
        {
            if (Choice == SignInProviderChoice.WorkAccount)
            {
                if (!WorkConfigStaged || !WorkVerified || !NativeTestPassed)
                {
                    return ProviderSetupResult.Fail(ProviderSetupStatus.WorkAccountNotReady);
                }

                string finalConfigDir = Path.Combine(localAppDataBase, "PAXCookbook", "Config");
                Directory.CreateDirectory(finalConfigDir);
                File.Copy(StagedConfigPath, Path.Combine(finalConfigDir, WamConfigFileName), overwrite: true);
                File.Copy(StagedVerificationPath, Path.Combine(finalConfigDir, WamVerificationFileName), overwrite: true);
                writer.WriteSelectionLast(SelectedSessionProvider.WorkAccount, ProviderSelectionSource.Setup);
                return ProviderSetupResult.Ok(SelectedSessionProvider.WorkAccount);
            }

            if (!HelloAvailable)
            {
                return ProviderSetupResult.Fail(ProviderSetupStatus.HelloUnavailable);
            }
            writer.WriteSelectionLast(SelectedSessionProvider.WindowsHello, ProviderSelectionSource.Setup);
            return ProviderSetupResult.Ok(SelectedSessionProvider.WindowsHello);
        }
        catch
        {
            try { writer.Restore(); } catch { /* best-effort */ }
            return ProviderSetupResult.Fail(ProviderSetupStatus.Faulted);
        }
    }

    private static string BuildConfigJson(string tenantId, string clientId)
        => "{\n" +
           $"  \"schemaVersion\": {WamConfigSchemaVersion},\n" +
           "  \"enabled\": true,\n" +
           $"  \"providerId\": \"{EntraWamProviderId}\",\n" +
           $"  \"tenantId\": \"{tenantId}\",\n" +
           $"  \"clientId\": \"{clientId}\"\n" +
           "}\n";

    private static string BuildVerificationJson(string tenantId, string clientId,
        string? verifiedUtc = null, string? configFingerprint = null)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = WamVerificationSchemaVersion,
            outcome = "verified",
            verifiedUtc = verifiedUtc ?? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            configFingerprint = configFingerprint ?? ComputeFingerprint(tenantId, clientId)
        });

    // Byte-identical to PAXCookbook.App ExperimentalWamVerificationStore.ComputeFingerprint
    // and the helper Get-ConfigFingerprint: sha256 hex of
    // lower(tenant)|lower(client)|entra-wam|2.
    private static string ComputeFingerprint(string tenantId, string clientId)
    {
        string material = string.Join(
            '|',
            (tenantId ?? string.Empty).Trim().ToLowerInvariant(),
            (clientId ?? string.Empty).Trim().ToLowerInvariant(),
            EntraWamProviderId,
            WamConfigSchemaVersion.ToString(CultureInfo.InvariantCulture));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public enum SignInProviderChoice
{
    WindowsHello,
    WorkAccount,
}
