using System;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S3 Phase 4 — strict setup-result import. Positive and adversarial coverage.
// Deterministic; synthetic identifiers only.
public sealed class ExperimentalWamSetupResultImportTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static string ValidJson(
        string? verifiedUtc = null,
        bool structuralSuccess = true,
        string? clientOverride = null)
    {
        string verified = verifiedUtc ?? Now.AddDays(-1).ToString("O");
        string client = clientOverride ?? Client;
        string success = structuralSuccess ? "true" : "false";
        return $$"""
        {
          "schemaVersion": 1,
          "kind": "pax-cookbook-wam-setup-result",
          "resultKind": "provision",
          "providerId": "entra-wam",
          "providerVersion": "1",
          "authorizationModel": "single-configured-tenant",
          "tenantId": "{{Tenant}}",
          "clientAppId": "{{client}}",
          "structuralVerification": { "success": {{success}} },
          "verifiedUtc": "{{verified}}",
          "helper": { "ownershipTag": "pax-cookbook-wam-provisioner", "name": "New-PaxCookbookEntraWamSetup" }
        }
        """;
    }

    private static ExperimentalWamSetupResultImport.ImportResult ParseValid(string json)
        => ExperimentalWamSetupResultImport.Parse(json, isCompiled: true, Now);

    // ---- positive --------------------------------------------------------

    [Fact]
    public void Valid_Succeeds_AndProducesConfigInput()
    {
        var result = ParseValid(ValidJson());
        Assert.True(result.Success);
        Assert.NotNull(result.Input);
        Assert.True(result.Input!.Enabled);
        Assert.Equal("entra-wam", result.Input.ProviderId);
        Assert.Equal(Tenant, result.Input.TenantId);
        Assert.Equal(Client, result.Input.ClientId);

        // The produced input validates as fully configured downstream.
        Assert.True(ExperimentalWamOptions.Create(result.Input).IsFullyConfigured);
    }

    // ---- adversarial -----------------------------------------------------

    [Fact]
    public void InvalidJson_Fails()
    {
        var result = ExperimentalWamSetupResultImport.Parse("{ not json", isCompiled: true, Now);
        Assert.False(result.Success);
        Assert.Equal(ExperimentalWamSetupResultImport.ImportError.InvalidJson, result.Error);
    }

    [Fact]
    public void EmptyOrNull_Fails()
    {
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.InvalidJson,
            ExperimentalWamSetupResultImport.Parse("", isCompiled: true, Now).Error);
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.InvalidJson,
            ExperimentalWamSetupResultImport.Parse(null, isCompiled: true, Now).Error);
    }

    [Fact]
    public void UnknownTopLevelField_Fails()
    {
        string json = ValidJson().TrimEnd().TrimEnd('}') + ", \"accessToken\": \"leaked\" }";
        var result = ParseValid(json);
        Assert.False(result.Success);
        Assert.Equal(ExperimentalWamSetupResultImport.ImportError.UnknownField, result.Error);
    }

    [Fact]
    public void WrongSchemaVersion_Fails()
    {
        string json = ValidJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.UnsupportedSchemaVersion,
            ParseValid(json).Error);
    }

    [Fact]
    public void WrongKind_Fails()
    {
        string json = ValidJson().Replace("pax-cookbook-wam-setup-result", "something-else");
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.UnsupportedKind,
            ParseValid(json).Error);
    }

    [Fact]
    public void WrongProvider_Fails()
    {
        string json = ValidJson().Replace("\"providerId\": \"entra-wam\"", "\"providerId\": \"other\"");
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.UnsupportedProvider,
            ParseValid(json).Error);
    }

    [Fact]
    public void NotCompiledBuild_Fails()
    {
        var result = ExperimentalWamSetupResultImport.Parse(ValidJson(), isCompiled: false, Now);
        Assert.Equal(ExperimentalWamSetupResultImport.ImportError.UnsupportedBuild, result.Error);
    }

    [Fact]
    public void InvalidTenant_Fails()
    {
        string json = ValidJson().Replace(Tenant, "not-a-guid");
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.InvalidTenantId,
            ParseValid(json).Error);
    }

    [Fact]
    public void InvalidClient_Fails()
    {
        string json = ValidJson(clientOverride: "not-a-guid");
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.InvalidClientId,
            ParseValid(json).Error);
    }

    [Fact]
    public void WrongResultKind_Fails()
    {
        string json = ValidJson().Replace("\"resultKind\": \"provision\"", "\"resultKind\": \"verify\"");
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.UnsupportedResultKind,
            ParseValid(json).Error);
    }

    [Fact]
    public void MissingAuthorizationModel_Fails()
    {
        // An older single-tenant-model result that lacks the marker is rejected.
        string json = ValidJson().Replace("\"authorizationModel\": \"single-configured-tenant\",", "");
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.UnsupportedAuthorizationModel,
            ParseValid(json).Error);
    }

    [Fact]
    public void WrongAuthorizationModel_Fails()
    {
        string json = ValidJson().Replace("single-configured-tenant", "multi-tenant");
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.UnsupportedAuthorizationModel,
            ParseValid(json).Error);
    }

    [Fact]
    public void ResourceAppIdUriField_IsRejectedAsUnknown()
    {
        // The superseded resource field must be rejected by the strict allow-list.
        string json = ValidJson().TrimEnd().TrimEnd('}') + ", \"resourceAppIdUri\": \"api://x\" }";
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.UnknownField,
            ParseValid(json).Error);
    }

    [Fact]
    public void StructuralVerificationFalse_Fails()
    {
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.StructuralVerificationFailed,
            ParseValid(ValidJson(structuralSuccess: false)).Error);
    }

    [Fact]
    public void MissingVerifiedTimestamp_Fails()
    {
        string json = ValidJson().Replace("\"verifiedUtc\": \"" + Now.AddDays(-1).ToString("O") + "\"", "\"verifiedUtc\": \"\"");
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.StaleOrIncompleteVerification,
            ParseValid(json).Error);
    }

    [Fact]
    public void FutureVerifiedTimestamp_Fails()
    {
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.StaleOrIncompleteVerification,
            ParseValid(ValidJson(verifiedUtc: Now.AddDays(2).ToString("O"))).Error);
    }

    [Fact]
    public void StaleVerifiedTimestamp_Fails()
    {
        Assert.Equal(
            ExperimentalWamSetupResultImport.ImportError.StaleOrIncompleteVerification,
            ParseValid(ValidJson(verifiedUtc: Now.AddDays(-200).ToString("O"))).Error);
    }
}
