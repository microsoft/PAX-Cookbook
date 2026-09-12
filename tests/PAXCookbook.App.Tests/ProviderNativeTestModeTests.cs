using System;
using System.IO;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Deterministic tests for the bounded provider native-test mode. The
// interactive success path requires a live Windows account picker and is NOT
// exercised here; these cover argument strictness, config validation, the
// build-gated unavailable path, result schema, and containment.
public sealed class ProviderNativeTestModeTests
{
    private static string NewTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "paxpnt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteConfig(string dir, string json)
    {
        string p = Path.Combine(dir, "experimental-wam.json");
        File.WriteAllText(p, json);
        return p;
    }

    private const string ValidConfigJson =
        "{ \"schemaVersion\": 2, \"providerId\": \"entra-wam\", " +
        "\"tenantId\": \"11111111-1111-1111-1111-111111111111\", " +
        "\"clientId\": \"22222222-2222-2222-2222-222222222222\" }";

    [Fact]
    public void IsRequested_OnlyForModeFlag()
    {
        Assert.True(ProviderNativeTestMode.IsRequested(new[] { "--provider-native-test" }));
        Assert.False(ProviderNativeTestMode.IsRequested(new[] { "--headless" }));
    }

    [Theory]
    [InlineData(new object[] { new[] { "--provider-native-test", "--bogus" } })]                                   // unknown flag
    [InlineData(new object[] { new[] { "--provider-native-test", "--config" } })]                                   // missing value
    [InlineData(new object[] { new[] { "--provider-native-test", "extra" } })]                                      // stray positional
    public void Args_Malformed_ReturnsInvalidArguments(string[] args)
    {
        Assert.Equal(ProviderNativeTestMode.ExitInvalidArguments, ProviderNativeTestMode.Run(args));
    }

    [Fact]
    public void Args_DuplicateConfig_ReturnsInvalidArguments()
    {
        string dir = NewTempDir();
        try
        {
            string cfg = WriteConfig(dir, ValidConfigJson);
            string res = Path.Combine(dir, "r.json");
            int code = ProviderNativeTestMode.Run(new[]
            {
                "--provider-native-test", "--config", cfg, "--config", cfg, "--result", res,
            });
            Assert.Equal(ProviderNativeTestMode.ExitInvalidArguments, code);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Args_RelativeConfigPath_ReturnsInvalidArguments()
    {
        string dir = NewTempDir();
        try
        {
            string res = Path.Combine(dir, "r.json");
            int code = ProviderNativeTestMode.Run(new[]
            {
                "--provider-native-test", "--config", "relative\\config.json", "--result", res,
            });
            Assert.Equal(ProviderNativeTestMode.ExitInvalidArguments, code);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Args_ResultParentMissing_ReturnsInvalidArguments()
    {
        string dir = NewTempDir();
        try
        {
            string cfg = WriteConfig(dir, ValidConfigJson);
            string res = Path.Combine(dir, "no-such-subdir", "r.json");
            int code = ProviderNativeTestMode.Run(new[]
            {
                "--provider-native-test", "--config", cfg, "--result", res,
            });
            Assert.Equal(ProviderNativeTestMode.ExitInvalidArguments, code);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Config_BadSchema_ReturnsInvalidConfig_AndWritesBoundedResult()
    {
        string dir = NewTempDir();
        try
        {
            string cfg = WriteConfig(dir, "{ \"schemaVersion\": 1, \"tenantId\": \"x\", \"clientId\": \"y\" }");
            string res = Path.Combine(dir, "r.json");
            int code = ProviderNativeTestMode.Run(new[]
            {
                "--provider-native-test", "--config", cfg, "--result", res,
            });
            Assert.Equal(ProviderNativeTestMode.ExitInvalidConfig, code);
            Assert.True(File.Exists(res));
            string raw = File.ReadAllText(res);
            Assert.Contains("provider_native_test", raw);
            Assert.Contains("invalid_config", raw);
            AssertNoIdentifierLeak(raw);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Config_NonGuidIds_ReturnsInvalidConfig()
    {
        string dir = NewTempDir();
        try
        {
            string cfg = WriteConfig(dir, "{ \"schemaVersion\": 2, \"providerId\": \"entra-wam\", \"tenantId\": \"not-a-guid\", \"clientId\": \"nope\" }");
            string res = Path.Combine(dir, "r.json");
            int code = ProviderNativeTestMode.Run(new[]
            {
                "--provider-native-test", "--config", cfg, "--result", res,
            });
            Assert.Equal(ProviderNativeTestMode.ExitInvalidConfig, code);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ValidConfig_StableBuild_ReportsUnavailable_NoMsal()
    {
        // Only meaningful in a stable/default build. In an experimental build a
        // valid config would open the interactive WAM window (live), so skip.
        if (ExperimentalWamCapability.IsExperimentalAuthenticatorCompiled)
        {
            return;
        }

        string dir = NewTempDir();
        try
        {
            string cfg = WriteConfig(dir, ValidConfigJson);
            string res = Path.Combine(dir, "r.json");
            int code = ProviderNativeTestMode.Run(new[]
            {
                "--provider-native-test", "--config", cfg, "--result", res,
            });
            Assert.Equal(ProviderNativeTestMode.ExitUnavailableInBuild, code);
            string raw = File.ReadAllText(res);
            Assert.Contains("unavailable_in_this_build", raw);
            Assert.Contains("\"acquisitionCount\":0", raw);
            AssertNoIdentifierLeak(raw);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void AssertNoIdentifierLeak(string raw)
    {
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("upn", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("claim", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correlation", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account", raw, StringComparison.OrdinalIgnoreCase);
    }
}
