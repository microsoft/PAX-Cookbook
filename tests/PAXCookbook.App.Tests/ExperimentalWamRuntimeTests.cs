using System;
using System.IO;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S3 Phase 3 backend — live runtime state machine + unlock provenance.
// Deterministic; synthetic identifiers only. The runtime is forced compiled=true
// so the store/state logic runs in both build configs; native activation still
// depends on the real compile flag, so the verified state is asserted as
// ready-or-provider_unavailable (both mean "verification recorded").
public sealed class ExperimentalWamRuntimeTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";

    private static string NewBase()
    {
        string p = Path.Combine(Path.GetTempPath(), "paxwamrt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static ExperimentalWamRuntime NewRuntime(string baseDir) =>
        new(baseDir, ExperimentalWamPipe.PipeName(baseDir), compiled: true);

    private static string ValidSetupResult()
    {
        string verified = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        return $$"""
        {
          "schemaVersion": 1,
          "kind": "pax-cookbook-wam-setup-result",
          "resultKind": "provision",
          "providerId": "entra-wam",
          "providerVersion": "1",
          "authorizationModel": "single-configured-tenant",
          "tenantId": "{{Tenant}}",
          "clientAppId": "{{Client}}",
          "structuralVerification": { "success": true },
          "verifiedUtc": "{{verified}}",
          "helper": { "ownershipTag": "pax-cookbook-wam-provisioner" }
        }
        """;
    }

    [Fact]
    public void FreshRuntime_NoConfig_IsNotConfigured()
    {
        string b = NewBase();
        try
        {
            ExperimentalWamStateInfo s = NewRuntime(b).GetState();
            Assert.Equal("not_configured", s.StateCode);
            Assert.False(s.CapabilityAvailable);
            Assert.False(s.Configured);
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void Import_ValidResult_TransitionsToConfiguredUnverified_NotReady()
    {
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            ExperimentalWamActionResult r = rt.ImportSetupResult(ValidSetupResult());
            Assert.True(r.Success);
            // A helper "verified" claim is NEVER treated as current ready proof.
            Assert.Equal("configured_unverified", r.State.StateCode);
            Assert.False(r.State.CapabilityAvailable);
            Assert.True(r.State.Configured);
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void Import_Malformed_Fails_AndDoesNotConfigure()
    {
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            ExperimentalWamActionResult r = rt.ImportSetupResult("{ not valid");
            Assert.False(r.Success);
            Assert.Equal("invalid_json", r.Reason);
            Assert.Equal("not_configured", r.State.StateCode);
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void RecordVerified_AfterImport_RecordsVerification()
    {
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            rt.ImportSetupResult(ValidSetupResult());
            ExperimentalWamActionResult r = rt.RecordVerified();
            Assert.True(r.Success);
            // ready when the native pipe activates (experimental build), else
            // provider_unavailable — both mean verification was recorded.
            Assert.Contains(r.State.StateCode, new[] { "ready", "provider_unavailable" });
            Assert.Equal(r.State.StateCode == "ready", r.State.CapabilityAvailable);
            Assert.NotNull(r.State.VerifiedUtc);
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void Disable_AfterImport_ReportsDisabled_AndNoCapability()
    {
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            rt.ImportSetupResult(ValidSetupResult());
            rt.RecordVerified();
            ExperimentalWamActionResult r = rt.SetLocallyEnabled(false);
            Assert.True(r.Success);
            Assert.Equal("disabled", r.State.StateCode);
            Assert.True(r.State.Disabled);
            Assert.False(r.State.CapabilityAvailable);
            // Identifiers retained through disable.
            Assert.True(r.State.Configured);
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void EnableAfterDisable_RestoresPriorState()
    {
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            rt.ImportSetupResult(ValidSetupResult());
            rt.SetLocallyEnabled(false);
            ExperimentalWamActionResult r = rt.SetLocallyEnabled(true);
            Assert.True(r.Success);
            // Re-enabled but not verified in this path -> configured_unverified.
            Assert.Equal("configured_unverified", r.State.StateCode);
            Assert.False(r.State.Disabled);
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void Remove_DeletesLocalConfig_ReturnsNotConfigured()
    {
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            rt.ImportSetupResult(ValidSetupResult());
            rt.RecordVerified();
            ExperimentalWamActionResult r = rt.RemoveLocalConfiguration();
            Assert.True(r.Success);
            Assert.Equal("not_configured", r.State.StateCode);
            Assert.False(r.State.Configured);
            Assert.Null(rt.GetAdminDetails());
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void AdminDetails_AfterImport_ReturnsIdentifiers()
    {
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            rt.ImportSetupResult(ValidSetupResult());
            ExperimentalWamAdminDetails? d = rt.GetAdminDetails();
            Assert.NotNull(d);
            Assert.Equal(Tenant, d!.TenantId);
            Assert.Equal(Client, d.ClientId);
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void VerificationDrift_AfterImportingDifferentConfig_RemovesReady()
    {
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            rt.ImportSetupResult(ValidSetupResult());
            rt.RecordVerified();

            // Import a DIFFERENT tenant: the fingerprint-bound verification no
            // longer matches, so the state drops back to configured_unverified.
            string other = ValidSetupResult().Replace(Tenant, "33333333-3333-3333-3333-333333333333");
            ExperimentalWamActionResult r = rt.ImportSetupResult(other);
            Assert.True(r.Success);
            Assert.Equal("configured_unverified", r.State.StateCode);
            Assert.False(r.State.CapabilityAvailable);
        }
        finally { Directory.Delete(b, true); }
    }

    // ---- unlock provenance (global lock state; self-contained) -----------

    [Fact]
    public void Provenance_WindowsHello_SetAndClearedOnLock()
    {
        try
        {
            BrokerLock.SetUnlocked("windows_hello");
            Assert.Equal("windows_hello", BrokerLock.GetUnlockProvenance());
            BrokerLock.SetLocked();
            Assert.Null(BrokerLock.GetUnlockProvenance());
        }
        finally { BrokerLock.SetLocked(); }
    }

    [Fact]
    public void Provenance_WorkAccount_Recorded()
    {
        try
        {
            BrokerLock.SetUnlocked("work_account");
            Assert.Equal("work_account", BrokerLock.GetUnlockProvenance());
        }
        finally { BrokerLock.SetLocked(); }
    }

    [Fact]
    public void Provenance_Locked_IsNull()
    {
        BrokerLock.SetLocked();
        Assert.Null(BrokerLock.GetUnlockProvenance());
    }

    [Fact]
    public void Provenance_UnspecifiedUnlock_HasNoProvenance()
    {
        try
        {
            BrokerLock.SetUnlocked();
            Assert.Null(BrokerLock.GetUnlockProvenance());
        }
        finally { BrokerLock.SetLocked(); }
    }
}
