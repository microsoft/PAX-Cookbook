using System;
using System.IO;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S3 — deprovision plan/execute coordinator. Deterministic: fake pwsh helper.
// Exercises the plan-token guards (unknown/replay), the configured/not-configured
// gates, and a verified-cleanup execute that removes local configuration.
[Collection("WamHelperRunnerSerial")]
public sealed class ExperimentalWamDeprovisionCoordinatorTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";

    private static bool PwshAvailable() => !string.IsNullOrEmpty(PwshLocator.Resolve());

    private static string NewBase()
    {
        string p = Path.Combine(Path.GetTempPath(), "paxwamdp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static ExperimentalWamRuntime NewRuntime(string baseDir) =>
        new(baseDir, ExperimentalWamPipe.PipeName(baseDir), compiled: true);

    private static string ValidProvisionResult()
    {
        string verified = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        return $$"""
        { "schemaVersion": 1, "kind": "pax-cookbook-wam-setup-result", "resultKind": "provision",
          "providerId": "entra-wam", "providerVersion": "1", "authorizationModel": "single-configured-tenant", "tenantId": "{{Tenant}}",
          "clientAppId": "{{Client}}", "structuralVerification": { "success": true },
          "verifiedUtc": "{{verified}}", "helper": { "ownershipTag": "pax-cookbook-wam-provisioner" } }
        """;
    }

    // Fake helper that emits a deprovision result with verified absence.
    private static string WriteDeprovisionHelper(string dir)
    {
        string path = Path.Combine(dir, "New-PaxCookbookEntraWamSetup.ps1");
        string script = @"
param([string]$Action,[string]$SetupResultPath,[switch]$PlanOnly,[Parameter(ValueFromRemainingArguments=$true)]$Rest)
'{""resultKind"":""deprovision"",""objectCategories"":{""registration"":true,""servicePrincipals"":1,""tenantWideGrant"":true},""absenceVerified"":true,""partialFailure"":[]}' | Set-Content -LiteralPath $SetupResultPath
exit 0
";
        File.WriteAllText(path, script);
        return path;
    }

    private static ExperimentalWamHelperRunner Runner(string helperPath) =>
        new(helperPath, timeout: TimeSpan.FromSeconds(30));

    [Fact]
    public void Prepare_NotConfigured_Fails()
    {
        if (!PwshAvailable()) return;
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            var runner = Runner(WriteDeprovisionHelper(b));
            var r = ExperimentalWamDeprovisionCoordinator.Prepare(rt, runner, true, DateTimeOffset.UtcNow);
            Assert.False(r.Ok);
            Assert.Equal("not_configured", r.Reason);
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void Execute_UnknownPlanId_IsRejected()
    {
        if (!PwshAvailable()) return;
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            rt.ImportSetupResult(ValidProvisionResult());
            var runner = Runner(WriteDeprovisionHelper(b));
            var r = ExperimentalWamDeprovisionCoordinator.Execute(rt, runner, true, "bogus-plan", DateTimeOffset.UtcNow);
            Assert.False(r.Success);
            Assert.Equal("invalid_or_replayed_plan", r.Reason);
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void Prepare_MintsPlanId()
    {
        if (!PwshAvailable()) return;
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            rt.ImportSetupResult(ValidProvisionResult());
            var runner = Runner(WriteDeprovisionHelper(b));
            var r = ExperimentalWamDeprovisionCoordinator.Prepare(rt, runner, true, DateTimeOffset.UtcNow);
            Assert.True(r.Ok);
            Assert.False(string.IsNullOrEmpty(r.PlanId));
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void Execute_ValidPlan_RemovesLocalConfig_ThenReplayRefused()
    {
        if (!PwshAvailable()) return;
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            rt.ImportSetupResult(ValidProvisionResult());
            var runner = Runner(WriteDeprovisionHelper(b));
            var now = DateTimeOffset.UtcNow;

            var plan = ExperimentalWamDeprovisionCoordinator.Prepare(rt, runner, true, now);
            Assert.True(plan.Ok);

            var exec = ExperimentalWamDeprovisionCoordinator.Execute(rt, runner, true, plan.PlanId, now);
            Assert.True(exec.Success);
            Assert.Equal("not_configured", exec.State.StateCode);
            Assert.Null(rt.GetAdminDetails());

            // Replay: the plan was consumed AND the config is gone.
            var replay = ExperimentalWamDeprovisionCoordinator.Execute(rt, runner, true, plan.PlanId, now);
            Assert.False(replay.Success);
        }
        finally { Directory.Delete(b, true); }
    }

    [Fact]
    public void Prepare_Expired_ThenExecute_Rejected()
    {
        if (!PwshAvailable()) return;
        string b = NewBase();
        try
        {
            var rt = NewRuntime(b);
            rt.ImportSetupResult(ValidProvisionResult());
            var runner = Runner(WriteDeprovisionHelper(b));
            var t0 = DateTimeOffset.UtcNow;
            var plan = ExperimentalWamDeprovisionCoordinator.Prepare(rt, runner, true, t0);
            Assert.True(plan.Ok);
            // Execute far in the future: the plan has expired.
            var exec = ExperimentalWamDeprovisionCoordinator.Execute(rt, runner, true, plan.PlanId, t0.AddHours(1));
            Assert.False(exec.Success);
            Assert.Equal("plan_expired", exec.Reason);
        }
        finally { Directory.Delete(b, true); }
    }
}
