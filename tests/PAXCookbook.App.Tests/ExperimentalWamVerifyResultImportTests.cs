using System;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S3 Phase 2/4 — strict Verify-result validation bound to local config.
public sealed class ExperimentalWamVerifyResultImportTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static string Fingerprint() =>
        ExperimentalWamVerificationStore.ComputeFingerprint(Tenant, Client);

    private static string ValidVerify(
        bool structuralOk = true,
        bool grantOk = true,
        string? verifiedUtc = null,
        string? tenant = null,
        string? fingerprint = null,
        string resultKind = "verify")
    {
        string verified = verifiedUtc ?? Now.AddMinutes(-2).ToString("O");
        string fp = fingerprint ?? Fingerprint();
        string t = tenant ?? Tenant;
        return $$"""
        {
          "schemaVersion": 1,
          "kind": "pax-cookbook-wam-setup-result",
          "resultKind": "{{resultKind}}",
          "providerId": "entra-wam",
          "providerVersion": "1",
          "tenantId": "{{t}}",
          "clientAppId": "{{Client}}",
          "structuralVerification": { "success": {{(structuralOk ? "true" : "false")}} },
          "grantResult": { "allPrincipalsUserRead": {{(grantOk ? "true" : "false")}} },
          "configFingerprint": "{{fp}}",
          "verifiedUtc": "{{verified}}"
        }
        """;
    }

    private static ExperimentalWamVerifyResultImport.VerifyResult Validate(string json) =>
        ExperimentalWamVerifyResultImport.Validate(json, true, Tenant, Client, Fingerprint(), Now);

    [Fact]
    public void Valid_Succeeds()
    {
        Assert.True(Validate(ValidVerify()).Success);
    }

    [Fact]
    public void NotCompiled_Fails()
    {
        var r = ExperimentalWamVerifyResultImport.Validate(ValidVerify(), false, Tenant, Client, Fingerprint(), Now);
        Assert.Equal(ExperimentalWamVerifyResultImport.VerifyError.UnsupportedBuild, r.Error);
    }

    [Fact]
    public void WrongResultKind_Fails()
    {
        Assert.Equal(
            ExperimentalWamVerifyResultImport.VerifyError.UnsupportedResultKind,
            Validate(ValidVerify(resultKind: "provision")).Error);
    }

    [Fact]
    public void IdentifierMismatch_Fails()
    {
        Assert.Equal(
            ExperimentalWamVerifyResultImport.VerifyError.IdentifierMismatch,
            Validate(ValidVerify(tenant: "99999999-9999-9999-9999-999999999999")).Error);
    }

    [Fact]
    public void FingerprintMismatch_Fails()
    {
        Assert.Equal(
            ExperimentalWamVerifyResultImport.VerifyError.FingerprintMismatch,
            Validate(ValidVerify(fingerprint: "deadbeef")).Error);
    }

    [Fact]
    public void StructuralFalse_Fails()
    {
        Assert.Equal(
            ExperimentalWamVerifyResultImport.VerifyError.StructuralCheckFailed,
            Validate(ValidVerify(structuralOk: false)).Error);
    }

    [Fact]
    public void GrantFalse_MapsToConsentCheckFailed()
    {
        Assert.Equal(
            ExperimentalWamVerifyResultImport.VerifyError.ConsentCheckFailed,
            Validate(ValidVerify(grantOk: false)).Error);
    }

    [Fact]
    public void StaleTimestamp_Fails()
    {
        Assert.Equal(
            ExperimentalWamVerifyResultImport.VerifyError.StaleOrIncompleteVerification,
            Validate(ValidVerify(verifiedUtc: Now.AddHours(-2).ToString("O"))).Error);
    }

    [Fact]
    public void FutureTimestamp_Fails()
    {
        Assert.Equal(
            ExperimentalWamVerifyResultImport.VerifyError.StaleOrIncompleteVerification,
            Validate(ValidVerify(verifiedUtc: Now.AddHours(1).ToString("O"))).Error);
    }

    [Fact]
    public void UnknownField_Fails()
    {
        string json = ValidVerify().TrimEnd().TrimEnd('}') + ", \"accessToken\": \"x\" }";
        Assert.Equal(
            ExperimentalWamVerifyResultImport.VerifyError.UnknownField,
            Validate(json).Error);
    }

    [Fact]
    public void InvalidJson_Fails()
    {
        Assert.Equal(
            ExperimentalWamVerifyResultImport.VerifyError.InvalidJson,
            Validate("{ nope").Error);
    }

    [Fact]
    public void Fingerprint_MatchesPowerShellHelper_CrossLanguageParity()
    {
        // The product's ComputeFingerprint MUST be byte-identical to the helper's
        // Get-ConfigFingerprint so a Verify result binds to the exact local
        // configuration. This literal is produced by the PowerShell helper for
        // the same inputs; a divergence would silently break Verify.
        Assert.Equal(
            "d66bfc9dad28f84724047e1c38ba7b8b8fd2ba083af8e7189f6efc1fc1a00faf",
            ExperimentalWamVerificationStore.ComputeFingerprint(Tenant, Client));
    }
}
