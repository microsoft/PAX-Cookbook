using System;
using System.IO;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S3 Phase 2 — durable per-user experimental WAM configuration, durable-first
// resolution precedence, and the bounded 8-state classifier. Deterministic; no
// live WAM/tenant/MSAL dependency. Test identifiers are obviously-synthetic
// placeholders, never a real registration.
public sealed class ExperimentalWamDurableConfigAndStateTests
{
    private const string TestTenant = "11111111-1111-1111-1111-111111111111";
    private const string TestClient = "22222222-2222-2222-2222-222222222222";

    private static string NewTempBase()
    {
        string path = Path.Combine(Path.GetTempPath(), "paxwam_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ExperimentalWamConfigInput ValidInput() => new()
    {
        Enabled = true,
        ProviderId = "entra-wam",
        TenantId = TestTenant,
        ClientId = TestClient,
    };

    private static Func<string, string?> NoEnv() => _ => null;

    // ---- durable store: read/write/delete --------------------------------

    [Fact]
    public void Store_Load_NoFile_ReturnsNull()
    {
        string baseDir = NewTempBase();
        try
        {
            Assert.Null(ExperimentalWamConfigStore.Load(baseDir));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Store_SaveThenLoad_RoundTripsNonSecretValues()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamConfigStore.Save(baseDir, ValidInput());

            ExperimentalWamStoredConfig? loaded = ExperimentalWamConfigStore.Load(baseDir);
            Assert.NotNull(loaded);
            Assert.False(loaded!.IsMalformed);
            Assert.True(loaded.Enabled);
            Assert.Equal("entra-wam", loaded.ProviderId);
            Assert.Equal(TestTenant, loaded.TenantId);
            Assert.Equal(TestClient, loaded.ClientId);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Store_SavedFile_ContainsNoSecretFields()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamConfigStore.Save(baseDir, ValidInput());
            string json = File.ReadAllText(ExperimentalWamConfigStore.ResolveConfigPath(baseDir));

            // Only the non-secret identifiers are ever written.
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("certificate", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Store_Load_MalformedJson_ReturnsMalformed()
    {
        string baseDir = NewTempBase();
        try
        {
            string path = ExperimentalWamConfigStore.ResolveConfigPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not valid json ");

            ExperimentalWamStoredConfig? loaded = ExperimentalWamConfigStore.Load(baseDir);
            Assert.NotNull(loaded);
            Assert.True(loaded!.IsMalformed);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Store_Load_UnknownFutureSchemaVersion_ReturnsMalformed()
    {
        string baseDir = NewTempBase();
        try
        {
            string path = ExperimentalWamConfigStore.ResolveConfigPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"schemaVersion\":999,\"enabled\":true,\"providerId\":\"entra-wam\"}");

            ExperimentalWamStoredConfig? loaded = ExperimentalWamConfigStore.Load(baseDir);
            Assert.NotNull(loaded);
            Assert.True(loaded!.IsMalformed);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Store_Load_EmptyFile_ReturnsNull()
    {
        string baseDir = NewTempBase();
        try
        {
            string path = ExperimentalWamConfigStore.ResolveConfigPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "   ");

            Assert.Null(ExperimentalWamConfigStore.Load(baseDir));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Store_Delete_RemovesFile()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamConfigStore.Save(baseDir, ValidInput());
            Assert.NotNull(ExperimentalWamConfigStore.Load(baseDir));

            ExperimentalWamConfigStore.Delete(baseDir);
            Assert.Null(ExperimentalWamConfigStore.Load(baseDir));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    // ---- durable-first resolution precedence -----------------------------

    [Fact]
    public void Resolve_DurableFile_WinsOverEnvironment()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamConfigStore.Save(baseDir, ValidInput());

            // Environment carries a DIFFERENT (also-valid-shaped) tenant; the
            // durable file must win, so the resolved tenant is the durable one.
            Func<string, string?> env = name => name switch
            {
                "PAXCOOKBOOK_EXPERIMENTAL_WAM_ENABLED" => "1",
                "PAXCOOKBOOK_EXPERIMENTAL_WAM_PROVIDER" => "entra-wam",
                "PAXCOOKBOOK_EXPERIMENTAL_WAM_TENANT" => "99999999-9999-9999-9999-999999999999",
                "PAXCOOKBOOK_EXPERIMENTAL_WAM_CLIENT" => TestClient,
                _ => null,
            };

            ExperimentalWamResolution res = ExperimentalWamHost.ResolveFromSources(baseDir, env);
            Assert.True(res.ConfigSourcePresent);
            Assert.False(res.StoredMalformed);
            Assert.True(res.Options.IsFullyConfigured);
            Assert.Equal(TestTenant, res.Options.TenantId);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_NoDurableFile_UsesEnvironment()
    {
        string baseDir = NewTempBase();
        try
        {
            Func<string, string?> env = name => name switch
            {
                "PAXCOOKBOOK_EXPERIMENTAL_WAM_ENABLED" => "1",
                "PAXCOOKBOOK_EXPERIMENTAL_WAM_PROVIDER" => "entra-wam",
                "PAXCOOKBOOK_EXPERIMENTAL_WAM_TENANT" => TestTenant,
                "PAXCOOKBOOK_EXPERIMENTAL_WAM_CLIENT" => TestClient,
                _ => null,
            };

            ExperimentalWamResolution res = ExperimentalWamHost.ResolveFromSources(baseDir, env);
            Assert.True(res.ConfigSourcePresent);
            Assert.True(res.Options.IsFullyConfigured);
            Assert.Equal(TestTenant, res.Options.TenantId);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_NoDurableFile_NoEnvironment_IsNotPresentAndDisabled()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamResolution res = ExperimentalWamHost.ResolveFromSources(baseDir, NoEnv());
            Assert.False(res.ConfigSourcePresent);
            Assert.False(res.StoredMalformed);
            Assert.False(res.Options.IsFullyConfigured);
            Assert.Null(res.Attempted);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_MalformedDurableFile_IsPresentAndMalformed()
    {
        string baseDir = NewTempBase();
        try
        {
            string path = ExperimentalWamConfigStore.ResolveConfigPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ broken ");

            ExperimentalWamResolution res = ExperimentalWamHost.ResolveFromSources(baseDir, NoEnv());
            Assert.True(res.ConfigSourcePresent);
            Assert.True(res.StoredMalformed);
            Assert.False(res.Options.IsFullyConfigured);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_EnvironmentGatePresentButDisabling_IsPresent()
    {
        string baseDir = NewTempBase();
        try
        {
            // Only the enabled=0 var is set: a source is present but incomplete.
            Func<string, string?> env = name =>
                name == "PAXCOOKBOOK_EXPERIMENTAL_WAM_ENABLED" ? "0" : null;

            ExperimentalWamResolution res = ExperimentalWamHost.ResolveFromSources(baseDir, env);
            Assert.True(res.ConfigSourcePresent);
            Assert.False(res.Options.IsFullyConfigured);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    // ---- bounded 8-state classifier --------------------------------------

    private static ExperimentalWamStatusInput MakeInput(
        bool isCompiled = true,
        bool present = true,
        bool malformed = false,
        ExperimentalWamConfigInput? attempted = null,
        bool fullyConfigured = false,
        bool activated = false,
        ExperimentalWamVerification verification = ExperimentalWamVerification.None)
        => new(isCompiled, present, malformed, attempted, fullyConfigured, activated, verification);

    [Fact]
    public void State_NotCompiled_IsUnavailableInThisBuild()
    {
        ExperimentalWamConfigState state = ExperimentalWamStatus.Classify(
            MakeInput(isCompiled: false, present: true, fullyConfigured: true, activated: true));
        Assert.Equal(ExperimentalWamConfigState.UnavailableInThisBuild, state);
        Assert.Equal("unavailable_in_this_build", ExperimentalWamStatus.ToWireCode(state));
    }

    [Fact]
    public void State_NoSource_IsNotConfigured()
    {
        ExperimentalWamConfigState state = ExperimentalWamStatus.Classify(
            MakeInput(present: false));
        Assert.Equal(ExperimentalWamConfigState.NotConfigured, state);
        Assert.Equal("not_configured", ExperimentalWamStatus.ToWireCode(state));
    }

    [Fact]
    public void State_MalformedDurable_IsInvalidConfiguration()
    {
        ExperimentalWamConfigState state = ExperimentalWamStatus.Classify(
            MakeInput(present: true, malformed: true));
        Assert.Equal(ExperimentalWamConfigState.InvalidConfiguration, state);
        Assert.Equal("invalid_configuration", ExperimentalWamStatus.ToWireCode(state));
    }

    [Fact]
    public void State_PresentIncomplete_IsAdministratorSetupRequired()
    {
        // Enabled but the client id is blank: administrator has not finished.
        var attempted = new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = TestTenant,
            ClientId = null,
        };
        ExperimentalWamConfigState state = ExperimentalWamStatus.Classify(
            MakeInput(present: true, attempted: attempted, fullyConfigured: false));
        Assert.Equal(ExperimentalWamConfigState.AdministratorSetupRequired, state);
        Assert.Equal("administrator_setup_required", ExperimentalWamStatus.ToWireCode(state));
    }

    [Fact]
    public void State_PresentEnabledFalse_IsAdministratorSetupRequired()
    {
        var attempted = new ExperimentalWamConfigInput
        {
            Enabled = false,
            ProviderId = "entra-wam",
            TenantId = TestTenant,
            ClientId = TestClient,
        };
        ExperimentalWamConfigState state = ExperimentalWamStatus.Classify(
            MakeInput(present: true, attempted: attempted, fullyConfigured: false));
        Assert.Equal(ExperimentalWamConfigState.AdministratorSetupRequired, state);
    }

    [Fact]
    public void State_AttemptingButInvalid_IsInvalidConfiguration()
    {
        // Enabled, all fields supplied, but the values are malformed (bad GUIDs),
        // so validation fails: this is an invalid configuration, not incomplete.
        var attempted = new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = "not-a-guid",
            ClientId = "also-not-a-guid",
        };
        ExperimentalWamConfigState state = ExperimentalWamStatus.Classify(
            MakeInput(present: true, attempted: attempted, fullyConfigured: false));
        Assert.Equal(ExperimentalWamConfigState.InvalidConfiguration, state);
    }

    [Fact]
    public void State_FullyConfiguredVerifiedNotActivated_IsProviderUnavailable()
    {
        // Verified (so activation is attempted) but the native provider failed to
        // activate on this run: provider_unavailable, never ready.
        ExperimentalWamConfigState state = ExperimentalWamStatus.Classify(
            MakeInput(present: true, fullyConfigured: true, activated: false,
                verification: ExperimentalWamVerification.Verified));
        Assert.Equal(ExperimentalWamConfigState.ProviderUnavailable, state);
        Assert.Equal("provider_unavailable", ExperimentalWamStatus.ToWireCode(state));
    }

    [Fact]
    public void State_FullyConfiguredUnverified_IsConfiguredUnverified_RegardlessOfActivation()
    {
        // Structural validity alone is NOT usable: an unverified configuration is
        // configured_unverified even if a stray activation flag is set.
        Assert.Equal(
            ExperimentalWamConfigState.ConfiguredUnverified,
            ExperimentalWamStatus.Classify(MakeInput(present: true, fullyConfigured: true, activated: false,
                verification: ExperimentalWamVerification.None)));
        Assert.Equal(
            ExperimentalWamConfigState.ConfiguredUnverified,
            ExperimentalWamStatus.Classify(MakeInput(present: true, fullyConfigured: true, activated: true,
                verification: ExperimentalWamVerification.None)));
    }

    [Fact]
    public void State_FullyConfiguredActivatedUnverified_IsConfiguredUnverified()
    {
        ExperimentalWamConfigState state = ExperimentalWamStatus.Classify(
            MakeInput(present: true, fullyConfigured: true, activated: true,
                verification: ExperimentalWamVerification.None));
        Assert.Equal(ExperimentalWamConfigState.ConfiguredUnverified, state);
        Assert.Equal("configured_unverified", ExperimentalWamStatus.ToWireCode(state));
    }

    [Fact]
    public void State_FullyConfiguredActivatedVerified_IsReady()
    {
        ExperimentalWamConfigState state = ExperimentalWamStatus.Classify(
            MakeInput(present: true, fullyConfigured: true, activated: true,
                verification: ExperimentalWamVerification.Verified));
        Assert.Equal(ExperimentalWamConfigState.Ready, state);
        Assert.Equal("ready", ExperimentalWamStatus.ToWireCode(state));
    }

    [Fact]
    public void State_FullyConfiguredActivatedConsentFailed_IsConsentMissingOrRevoked()
    {
        ExperimentalWamConfigState state = ExperimentalWamStatus.Classify(
            MakeInput(present: true, fullyConfigured: true, activated: true,
                verification: ExperimentalWamVerification.ConsentFailed));
        Assert.Equal(ExperimentalWamConfigState.ConsentMissingOrRevoked, state);
        Assert.Equal("consent_missing_or_revoked", ExperimentalWamStatus.ToWireCode(state));
    }

    // ---- verification store: read-only cloud verification record ----------

    private static ExperimentalWamOptions ValidOptions() =>
        ExperimentalWamOptions.Create(ValidInput());

    private static ExperimentalWamOptions OtherOptions() =>
        ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = "33333333-3333-3333-3333-333333333333",
            ClientId = TestClient,
        });

    [Fact]
    public void Verification_RecordVerified_ThenResolve_IsVerified()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamVerificationStore.RecordVerified(baseDir, ValidOptions());
            Assert.Equal(
                ExperimentalWamVerification.Verified,
                ExperimentalWamVerificationStore.Resolve(baseDir, ValidOptions()));
            Assert.NotNull(ExperimentalWamVerificationStore.GetVerifiedTimestamp(baseDir, ValidOptions()));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Verification_FingerprintMismatch_ReturnsNone()
    {
        string baseDir = NewTempBase();
        try
        {
            // Verified for one identifier set; a DIFFERENT tenant must not inherit
            // the verification (stale-config protection).
            ExperimentalWamVerificationStore.RecordVerified(baseDir, ValidOptions());
            Assert.Equal(
                ExperimentalWamVerification.None,
                ExperimentalWamVerificationStore.Resolve(baseDir, OtherOptions()));
            Assert.Null(ExperimentalWamVerificationStore.GetVerifiedTimestamp(baseDir, OtherOptions()));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Verification_RecordConsentFailure_Resolves_ConsentFailed()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamVerificationStore.RecordConsentFailure(baseDir, ValidOptions());
            Assert.Equal(
                ExperimentalWamVerification.ConsentFailed,
                ExperimentalWamVerificationStore.Resolve(baseDir, ValidOptions()));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Verification_Clear_ReturnsNone()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamVerificationStore.RecordVerified(baseDir, ValidOptions());
            ExperimentalWamVerificationStore.Clear(baseDir);
            Assert.Equal(
                ExperimentalWamVerification.None,
                ExperimentalWamVerificationStore.Resolve(baseDir, ValidOptions()));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Verification_RecordFile_ContainsNoSecretsOrRawIdentifiers()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamVerificationStore.RecordVerified(baseDir, ValidOptions());
            string json = File.ReadAllText(ExperimentalWamVerificationStore.ResolveVerificationPath(baseDir));

            // Only a bounded outcome, timestamp, and non-reversible fingerprint.
            Assert.DoesNotContain(TestTenant, json);
            Assert.DoesNotContain(TestClient, json);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    // ---- effective verification: dev override is build-gated ---------------

    [Fact]
    public void EffectiveVerification_NotFullyConfigured_IsNone()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamOptions disabled = ExperimentalWamOptions.Disabled;
            Assert.Equal(
                ExperimentalWamVerification.None,
                ExperimentalWamHost.ResolveEffectiveVerification(baseDir, disabled, isCompiled: true,
                    readEnv: _ => "1"));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void EffectiveVerification_DevOverride_OnlyWorksInCompiledBuild()
    {
        string baseDir = NewTempBase();
        try
        {
            Func<string, string?> env = name =>
                name == "PAXCOOKBOOK_EXPERIMENTAL_WAM_ASSUME_VERIFIED_DEV_ONLY" ? "1" : null;

            // Compiled build: the explicit override grants Verified.
            Assert.Equal(
                ExperimentalWamVerification.Verified,
                ExperimentalWamHost.ResolveEffectiveVerification(baseDir, ValidOptions(), isCompiled: true, env));

            // Stable build: the same flag is inert; with no durable record the
            // effective verification is None.
            Assert.Equal(
                ExperimentalWamVerification.None,
                ExperimentalWamHost.ResolveEffectiveVerification(baseDir, ValidOptions(), isCompiled: false, env));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void EffectiveVerification_NoOverride_NoRecord_IsNone()
    {
        string baseDir = NewTempBase();
        try
        {
            Assert.Equal(
                ExperimentalWamVerification.None,
                ExperimentalWamHost.ResolveEffectiveVerification(baseDir, ValidOptions(), isCompiled: true, NoEnv()));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void EffectiveVerification_DurableRecord_MatchingFingerprint_IsVerified()
    {
        string baseDir = NewTempBase();
        try
        {
            ExperimentalWamVerificationStore.RecordVerified(baseDir, ValidOptions());
            Assert.Equal(
                ExperimentalWamVerification.Verified,
                ExperimentalWamHost.ResolveEffectiveVerification(baseDir, ValidOptions(), isCompiled: true, NoEnv()));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }
}
