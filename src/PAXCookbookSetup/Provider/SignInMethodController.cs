using System;
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
    private readonly string _appExePath;
    private readonly IProcessLauncher _launcher;
    private readonly IHelloSupportProbe _helloProbe;

    private string _tenantId = string.Empty;
    private string _clientId = string.Empty;

    public SignInMethodController(
        bool isExperimentalChannel,
        string localAppDataBase,
        string stagingBase,
        string appExePath,
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

    // Imports a helper setup/provision result: reads the non-secret tenantId +
    // clientAppId and writes a staged one-registration config. Resets any prior
    // verification/native-test success (the config changed).
    public bool ImportSetupResult(string setupResultPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(setupResultPath));
            JsonElement root = doc.RootElement;
            string? tenant = root.TryGetProperty("tenantId", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            string? client = root.TryGetProperty("clientAppId", out JsonElement c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            if (!Guid.TryParseExact(tenant, "D", out _) || !Guid.TryParseExact(client, "D", out _))
            {
                return false;
            }

            _tenantId = tenant!;
            _clientId = client!;
            Directory.CreateDirectory(StagedConfigDir);
            File.WriteAllText(StagedConfigPath, BuildConfigJson(_tenantId, _clientId),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            WorkConfigStaged = true;
            WorkVerified = false;
            NativeTestPassed = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Records a successful live Verify (tenant-wide admin consent confirmed by
    // the helper) by staging a verification record whose fingerprint is
    // byte-identical to the app/helper Get-ConfigFingerprint.
    public bool MarkVerified(bool helperVerifySucceeded)
    {
        if (!WorkConfigStaged || !helperVerifySucceeded)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(StagedConfigDir);
            File.WriteAllText(StagedVerificationPath, BuildVerificationJson(_tenantId, _clientId),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            WorkVerified = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Runs exactly one native-WAM test against the staged config. Requires the
    // config to be staged first.
    public bool RunNativeTest(string resultFilePath)
    {
        if (!WorkConfigStaged)
        {
            return false;
        }
        var gate = new AppLaunchWorkAccountGate(_stagingBase, _appExePath, _launcher, resultFilePath);
        NativeTestPassed = gate.RunNativeWamAuthTest();
        return NativeTestPassed;
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
    public ProviderSetupResult Commit()
    {
        var writer = new ProviderSelectionWriter(_localAppDataBase);
        writer.Snapshot();

        try
        {
            if (Choice == SignInProviderChoice.WorkAccount)
            {
                if (!WorkConfigStaged || !WorkVerified || !NativeTestPassed)
                {
                    return ProviderSetupResult.Fail(ProviderSetupStatus.WorkAccountNotReady);
                }

                string finalConfigDir = Path.Combine(_localAppDataBase, "PAXCookbook", "Config");
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

    private static string BuildVerificationJson(string tenantId, string clientId)
        => "{\n" +
           $"  \"schemaVersion\": {WamVerificationSchemaVersion},\n" +
           "  \"outcome\": \"verified\",\n" +
           $"  \"verifiedUtc\": \"{DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}\",\n" +
           $"  \"configFingerprint\": \"{ComputeFingerprint(tenantId, clientId)}\"\n" +
           "}\n";

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
