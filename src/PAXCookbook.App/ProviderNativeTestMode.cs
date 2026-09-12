using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.App;

// Bounded one-shot provider native-test mode (invoked by the Setup installer's
// AppLaunchWorkAccountGate).
//
//   "PAX Cookbook.exe" --provider-native-test --config <path> --result <path>
//
// It performs EXACTLY ONE native-WAM interactive authentication against a
// supplied, schema-valid one-registration configuration and writes a bounded,
// token-free result file. It never starts the product broker, tray, cook, PAX,
// or the WebView shell, never mutates the installed product, never reads a
// token, and never calls Microsoft Graph. In a stable/default build it reports
// "unavailable" without loading MSAL (the MSAL-backed authenticator is compiled
// only under the EXPERIMENTAL_WAM gate).
internal static class ProviderNativeTestMode
{
    internal const string ModeFlag = "--provider-native-test";
    internal const string ConfigFlag = "--config";
    internal const string ResultFlag = "--result";

    // Distinct exit codes so the caller can classify the outcome without parsing
    // the result file.
    internal const int ExitSuccess = 0;
    internal const int ExitUnavailableInBuild = 10;
    internal const int ExitInvalidArguments = 11;
    internal const int ExitInvalidConfig = 12;
    internal const int ExitUserCancelled = 13;
    internal const int ExitProviderFailure = 14;
    internal const int ExitTenantMismatch = 15;
    internal const int ExitResultWriteFailure = 16;

    // Bounded outcome codes written to the result file. No identifier leaks.
    private const string OutcomeSuccess = "success";
    private const string OutcomeUnavailable = "unavailable_in_this_build";
    private const string OutcomeInvalidArguments = "invalid_arguments";
    private const string OutcomeInvalidConfig = "invalid_config";
    private const string OutcomeUserCancelled = "user_cancelled";
    private const string OutcomeProviderFailure = "provider_failure";
    private const string OutcomeTenantMismatch = "tenant_mismatch";

    internal static bool IsRequested(string[] args)
    {
        foreach (string a in args)
        {
            if (string.Equals(a, ModeFlag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // Runs the mode and returns the process exit code. Never throws.
    internal static int Run(string[] args)
    {
        if (!TryParseArgs(args, out string? configPath, out string? resultPath))
        {
            // Arguments (including the result path) are untrusted/malformed;
            // do not attempt to write a result file to an unvalidated path.
            return ExitInvalidArguments;
        }

        if (!TryLoadConfig(configPath!, out string tenantId, out string clientId))
        {
            TryWriteResult(resultPath!, success: false, OutcomeInvalidConfig, acquisitionCount: 0);
            return ExitInvalidConfig;
        }

#if EXPERIMENTAL_WAM
        return RunNativeTest(tenantId, clientId, resultPath!);
#else
        // Stable/default build: no MSAL, no window — report unavailable.
        _ = tenantId;
        _ = clientId;
        if (!TryWriteResult(resultPath!, success: false, OutcomeUnavailable, acquisitionCount: 0))
        {
            return ExitResultWriteFailure;
        }
        return ExitUnavailableInBuild;
#endif
    }

    // Strict argument parse: EXACTLY the mode flag plus one --config value and
    // one --result value. Rejects unknown flags, duplicates, missing values, and
    // any extra positional argument.
    private static bool TryParseArgs(string[] args, out string? configPath, out string? resultPath)
    {
        configPath = null;
        resultPath = null;
        bool sawMode = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, ModeFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (sawMode) { return false; } // duplicate mode flag
                sawMode = true;
            }
            else if (string.Equals(a, ConfigFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (configPath is not null || i + 1 >= args.Length) { return false; }
                configPath = args[++i];
            }
            else if (string.Equals(a, ResultFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (resultPath is not null || i + 1 >= args.Length) { return false; }
                resultPath = args[++i];
            }
            else
            {
                // Unknown flag or stray positional argument.
                return false;
            }
        }

        if (!sawMode || configPath is null || resultPath is null)
        {
            return false;
        }

        // The config path must be absolute and exist; the result path must be
        // absolute and sit under a directory the caller already created (we never
        // create arbitrary directories from an untrusted path).
        if (!IsAbsoluteExistingFile(configPath))
        {
            return false;
        }
        if (!IsAbsoluteWithExistingParent(resultPath))
        {
            return false;
        }

        return true;
    }

    private static bool IsAbsoluteExistingFile(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAbsoluteWithExistingParent(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return false;
            }
            string? dir = Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        }
        catch
        {
            return false;
        }
    }

    // Parses the non-secret one-registration config file (schema 2). No tenant
    // or client value ever appears on the command line; they come only from this
    // caller-supplied file.
    private static bool TryLoadConfig(string configPath, out string tenantId, out string clientId)
    {
        tenantId = string.Empty;
        clientId = string.Empty;

        string json;
        try
        {
            json = File.ReadAllText(configPath);
        }
        catch
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                schema.GetInt32() != ExperimentalWamOptions.SchemaVersion)
            {
                return false;
            }

            string? provider = root.TryGetProperty("providerId", out JsonElement p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : ExperimentalWamOptions.EntraWamProviderId;
            string? tenant = root.TryGetProperty("tenantId", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            string? client = root.TryGetProperty("clientId", out JsonElement c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            var input = new ExperimentalWamConfigInput
            {
                Enabled = true,
                ProviderId = provider,
                TenantId = tenant,
                ClientId = client,
            };
            ExperimentalWamOptions options = ExperimentalWamOptions.Create(input);
            if (!options.IsFullyConfigured)
            {
                return false;
            }

            tenantId = options.TenantId;
            clientId = options.ClientId;
            return true;
        }
        catch
        {
            return false;
        }
    }

#if EXPERIMENTAL_WAM
    private static int RunNativeTest(string tenantId, string clientId, string resultPath)
    {
        WamAcquireStatus status = WamAcquireStatus.ConfigurationFailure;
        WamValidation validation = WamValidation.NotSucceeded;
        var options = BuildOptions(tenantId, clientId);
        var request = new WamAuthRequest(WamAuthPurpose.SessionUnlock, tenantId, clientId, options.RequestScope);

        try
        {
            using var owner = new ProviderNativeTestWindow();
            IntPtr hwnd = owner.EnsureHandle();
            var authenticator = new MsalEntraWamAuthenticator();
            using var cts = new System.Threading.CancellationTokenSource();
            owner.Closed += (_, _) => { try { cts.Cancel(); } catch { } };

            WamInteractiveResult result = owner.RunAcquisition(() =>
                authenticator.AuthenticateAsync(request, hwnd, cts.Token));

            status = result.Status;
            if (status == WamAcquireStatus.Succeeded)
            {
                validation = WamScopeIdentityValidator.Validate(result, options);
            }
        }
        catch (OperationCanceledException)
        {
            status = WamAcquireStatus.UserCancelled;
        }
        catch
        {
            status = WamAcquireStatus.BrokerFailure;
        }

        (bool success, string outcome, int exit) = Classify(status, validation);
        if (!TryWriteResult(resultPath, success, outcome, acquisitionCount: 1))
        {
            return ExitResultWriteFailure;
        }
        return exit;
    }

    private static ExperimentalWamOptions BuildOptions(string tenantId, string clientId)
        => ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = ExperimentalWamOptions.EntraWamProviderId,
            TenantId = tenantId,
            ClientId = clientId,
        });

    private static (bool success, string outcome, int exit) Classify(WamAcquireStatus status, WamValidation validation)
    {
        if (status == WamAcquireStatus.Succeeded)
        {
            return validation switch
            {
                WamValidation.Valid => (true, OutcomeSuccess, ExitSuccess),
                WamValidation.TenantMismatch => (false, OutcomeTenantMismatch, ExitTenantMismatch),
                _ => (false, OutcomeProviderFailure, ExitProviderFailure),
            };
        }

        return status switch
        {
            WamAcquireStatus.UserCancelled => (false, OutcomeUserCancelled, ExitUserCancelled),
            WamAcquireStatus.Disabled => (false, OutcomeUnavailable, ExitUnavailableInBuild),
            _ => (false, OutcomeProviderFailure, ExitProviderFailure),
        };
    }
#endif

    // Writes the bounded, token-free result file atomically (temp + move within
    // the result directory). Returns false only on a write failure.
    private static bool TryWriteResult(string resultPath, bool success, string outcome, int acquisitionCount)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"schemaVersion\":1,");
            sb.Append("\"resultKind\":\"provider_native_test\",");
            sb.Append("\"success\":").Append(success ? "true" : "false").Append(',');
            sb.Append("\"outcome\":\"").Append(outcome).Append("\",");
            sb.Append("\"acquisitionCount\":").Append(acquisitionCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"requestedScope\":\"graph_user_read\",");
            sb.Append("\"verifiedUtc\":\"").Append(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('"');
            sb.Append('}');

            string dir = Path.GetDirectoryName(resultPath)!;
            string tmp = Path.Combine(dir, Path.GetFileName(resultPath) + ".tmp");
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(resultPath))
            {
                File.Replace(tmp, resultPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, resultPath);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
