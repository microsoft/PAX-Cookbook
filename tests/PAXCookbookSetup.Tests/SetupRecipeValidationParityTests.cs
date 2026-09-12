using System;
using System.Collections.Generic;
using System.Linq;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Cycle 83B-R - CANONICAL RECIPE VALIDATION IN SETUP.
//
// PAXCookbookSetup compiles the SAME parser (JsonModel) and the SAME semantic
// gates (RecipeValidationModel) the App compiles, linked from
// PAXCookbook.Shared\Contracts\AppSharedSource. These tests assert that Setup
// accepts and refuses Recipes identically - same acceptance, same refusal, same
// error order, same message shape, same exception text.
//
// Every input is a SYNTHETIC in-memory JSON string. Nothing here opens a
// certificate store, touches a private key, reads a secret, writes a file
// outside the test process, starts a process or a service, contacts a tenant,
// runs PAX, or performs a Bake.
public sealed class SetupRecipeValidationParityTests
{
    private const string ValidUlid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string OrgKeyId = "contoso.managed_key-1";

    private static string RecipeJson(string authJson, string tail = "") => $$"""
    {
      "recipeId": "{{ValidUlid}}",
      "recipeSchemaVersion": 1,
      "paxAdapterVersion": "1.11.11",
      "identity": { "name": "Organization binding" },
      "ingredients": {
        "m365Usage": { "includeM365Usage": false },
        "entraUserData": { "includeUserInfo": false }
      },
      "query": { "mode": "audit", "dateMode": "previous-day" },
      "processing": {},
      "destinations": { "fact": { "mode": "outputPath", "path": "C:\\PAX\\audit.csv" } },
      "auth": {{authJson}}{{tail}}
    }
    """;

    private static string CertificateAuth(string organizationKeyId = OrgKeyId) => $$"""
    {
      "mode": "AppRegistrationCertificate",
      "tenantId": "{{TenantId}}",
      "organizationKeyId": "{{organizationKeyId}}"
    }
    """;

    // Setup parses through the canonical JsonModel, exactly as the App does.
    private static (bool Ok, List<object> Errors) Validate(string json)
        => RecipeValidationModel.ValidateAll(JsonModel.Parse(json));

    private static IReadOnlyList<string> Keywords(List<object> errors) =>
        errors.Select(e => ((Dictionary<string, object?>)e)["keyword"] as string ?? "").ToList();

    private static IReadOnlyList<string> Paths(List<object> errors) =>
        errors.Select(e => ((Dictionary<string, object?>)e)["instancePath"] as string ?? "").ToList();

    private static string MessageFor(List<object> errors, string keyword) =>
        errors.Select(e => (Dictionary<string, object?>)e)
              .Where(d => (d["keyword"] as string) == keyword)
              .Select(d => d["message"] as string ?? "")
              .FirstOrDefault() ?? "";

    // ---- parsing -------------------------------------------------------------

    [Fact]
    public void Setup_parses_valid_recipe_json_through_the_canonical_JsonModel()
    {
        object? tree = JsonModel.Parse(RecipeJson(CertificateAuth()));

        var root = Assert.IsType<Dictionary<string, object?>>(tree);
        Assert.Equal(1L, root["recipeSchemaVersion"]);            // integral stays long
        Assert.Equal(ValidUlid, root["recipeId"]);
        // PowerShell-hashtable parity: keys are case-insensitive.
        Assert.True(root.ContainsKey("RECIPEID"));
    }

    [Fact]
    public void Setup_rejects_malformed_json_with_a_null_tree()
    {
        Assert.Null(JsonModel.Parse("{ this is not json "));
        Assert.Null(JsonModel.Parse(""));
        Assert.Null(JsonModel.Parse("   "));
        Assert.Null(JsonModel.Parse(null));
    }

    // ---- acceptance ----------------------------------------------------------

    [Fact]
    public void Setup_accepts_a_representative_valid_certificate_recipe()
    {
        (bool ok, List<object> errors) = Validate(RecipeJson(CertificateAuth()));

        Assert.True(ok, string.Join(" | ", Keywords(errors)));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("contoso.managed_key-1")]
    [InlineData("a")]
    [InlineData("A.B_C-9")]
    public void Setup_accepts_every_organization_key_id_the_pattern_allows(string id)
    {
        (bool ok, List<object> errors) = Validate(RecipeJson(CertificateAuth(id)));

        Assert.True(ok, string.Join(" | ", Keywords(errors)));
    }

    // ---- refusal -------------------------------------------------------------

    [Fact]
    public void Setup_rejects_a_schema_invalid_recipe()
    {
        // recipeSchemaVersion must be the integer constant, not a string.
        string json = RecipeJson(CertificateAuth()).Replace("\"recipeSchemaVersion\": 1", "\"recipeSchemaVersion\": \"1\"");

        (bool ok, List<object> errors) = Validate(json);

        Assert.False(ok);
        Assert.Contains("/recipeSchemaVersion", Paths(errors));
    }

    [Fact]
    public void Setup_rejects_an_unknown_field_because_the_schema_is_closed()
    {
        string json = RecipeJson(CertificateAuth(), ",\n      \"notARecipeField\": true");

        (bool ok, List<object> errors) = Validate(json);

        Assert.False(ok);
        Assert.Contains("additionalProperties", Keywords(errors));
        Assert.Equal("must NOT have additional property 'notARecipeField'", MessageFor(errors, "additionalProperties"));
    }

    [Fact]
    public void Setup_rejects_a_cross_field_invalid_recipe()
    {
        // A certificate Recipe may bind an organization key OR a personal Chef's
        // Key - never both. Nothing in the closed schema forbids the pair; only
        // the cross-field gate does.
        string auth = $$"""
        {
          "mode": "AppRegistrationCertificate",
          "tenantId": "{{TenantId}}",
          "organizationKeyId": "{{OrgKeyId}}",
          "chefKeyId": "personal-key-1"
        }
        """;

        (bool ok, List<object> errors) = Validate(RecipeJson(auth));

        Assert.False(ok);
        Assert.Contains("organizationKeyBindingConflict", Keywords(errors));
    }

    [Theory]
    [InlineData("contoso:managed")]          // ':' can never masquerade as a credential target
    [InlineData("has space")]
    [InlineData("")]
    public void Setup_enforces_the_organization_key_identifier_pattern(string id)
    {
        (bool ok, List<object> errors) = Validate(RecipeJson(CertificateAuth(id)));

        Assert.False(ok);
        Assert.Contains("pattern", Keywords(errors));
        Assert.Contains("/auth/organizationKeyId", Paths(errors));
        Assert.Equal("must match pattern \"^[A-Za-z0-9._-]{1,128}$\"", MessageFor(errors, "pattern"));
    }

    [Fact]
    public void Setup_enforces_the_organization_key_length_bound()
    {
        (bool ok, _) = Validate(RecipeJson(CertificateAuth(new string('a', 128))));
        Assert.True(ok);

        (bool tooLong, List<object> errors) = Validate(RecipeJson(CertificateAuth(new string('a', 129))));
        Assert.False(tooLong);
        Assert.Contains("pattern", Keywords(errors));
    }

    // ---- removed switches ----------------------------------------------------

    [Theory]
    [InlineData("ExportWorkbook")]
    [InlineData("ExplodeArrays")]
    [InlineData("ExplodeDeep")]
    [InlineData("RawInputCSV")]
    [InlineData("IncludeAgent365Info")]
    [InlineData("OnlyAgent365Info")]
    [InlineData("OutputPathAgent365Info")]
    [InlineData("AppendAgent365Info")]
    public void Setup_refuses_every_removed_switch_with_the_exact_engine_message(string name)
    {
        string json = RecipeJson(CertificateAuth(), $",\n      \"advanced\": {{ \"extraArguments\": \"-{name}\" }}");

        (bool ok, List<object> errors) = Validate(json);

        Assert.False(ok);
        Assert.Contains("removedSwitch", Keywords(errors));
        Assert.Contains("/advanced/extraArguments", Paths(errors));
        Assert.Equal(
            $"advanced.extraArguments contains removed switch '-{name}'. " +
            "This switch was removed in PAX v1.11.2 and is not reintroduced via the verbatim trailer. " +
            "Edit the recipe to remove it; the projection layer does not rewrite recipes.",
            MessageFor(errors, "removedSwitch"));
    }

    [Fact]
    public void Setup_matches_removed_switches_case_insensitively()
    {
        string json = RecipeJson(CertificateAuth(), ",\n      \"advanced\": { \"extraArguments\": \"-exportworkbook\" }");

        (bool ok, List<object> errors) = Validate(json);

        Assert.False(ok);
        Assert.Contains("removedSwitch", Keywords(errors));
    }

    [Fact]
    public void Setup_does_not_treat_an_unrelated_trailer_token_as_a_removed_switch()
    {
        string json = RecipeJson(CertificateAuth(), ",\n      \"advanced\": { \"extraArguments\": \"-ExportWorkbookish\" }");

        (bool _, List<object> errors) = Validate(json);

        Assert.DoesNotContain("removedSwitch", Keywords(errors));
    }

    [Fact]
    public void Setup_raises_the_projection_exception_with_the_exact_text()
    {
        var ex = Assert.Throws<PaxAdapter.ProjectionException>(
            () => PaxAdapter.ScanRemovedSwitches("-ExplodeDeep"));

        Assert.Equal(
            "advanced.extraArguments contains removed switch '-ExplodeDeep'. " +
            "This switch was removed in PAX v1.11.2 and is not reintroduced via the verbatim trailer. " +
            "Edit the recipe to remove it; the projection layer does not rewrite recipes.",
            ex.Message);
    }

    // ---- error order and shape ----------------------------------------------

    [Fact]
    public void Setup_emits_schema_errors_before_gate_errors()
    {
        // One closed-schema violation (unknown field) plus one gate violation
        // (removed switch). ValidateAll runs SchemaErrors first, then the gates.
        string json = RecipeJson(CertificateAuth(),
            ",\n      \"advanced\": { \"extraArguments\": \"-ExportWorkbook\" },\n      \"notARecipeField\": true");

        (bool ok, List<object> errors) = Validate(json);

        Assert.False(ok);
        IReadOnlyList<string> keywords = Keywords(errors);
        int schemaIx = keywords.ToList().IndexOf("additionalProperties");
        int gateIx = keywords.ToList().IndexOf("removedSwitch");
        Assert.True(schemaIx >= 0 && gateIx >= 0, string.Join(" | ", keywords));
        Assert.True(schemaIx < gateIx, $"schema={schemaIx} gate={gateIx} :: {string.Join(" | ", keywords)}");
    }

    [Fact]
    public void Setup_error_records_carry_the_exact_ajv_shape()
    {
        (bool _, List<object> errors) = Validate(RecipeJson(CertificateAuth("bad:id")));

        var first = Assert.IsType<Dictionary<string, object?>>(errors[0]);
        Assert.True(first.ContainsKey("instancePath"));
        Assert.True(first.ContainsKey("keyword"));
        Assert.True(first.ContainsKey("message"));
        Assert.IsType<string>(first["instancePath"]);
        Assert.IsType<string>(first["keyword"]);
        Assert.IsType<string>(first["message"]);
    }

    // ---- App parity on shared synthetic fixtures ------------------------------
    //
    // These are the SAME fixtures the App-side compiled-surface audit replays
    // through "PAX Cookbook.dll". Pinning the expected outcome here means a
    // divergence between the two compilations breaks a test rather than passing
    // silently.
    public static TheoryData<string, bool, string> SharedFixtures() => new()
    {
        { "valid-certificate",       true,  "" },
        { "bad-organization-key",    false, "pattern" },
        { "removed-switch",          false, "removedSwitch" },
        { "unknown-field",           false, "additionalProperties" },
        { "binding-conflict",        false, "organizationKeyBindingConflict" },
    };

    internal static string FixtureJson(string name) => name switch
    {
        "valid-certificate" => RecipeJson(CertificateAuth()),
        "bad-organization-key" => RecipeJson(CertificateAuth("contoso:managed")),
        "removed-switch" => RecipeJson(CertificateAuth(), ",\n      \"advanced\": { \"extraArguments\": \"-ExportWorkbook\" }"),
        "unknown-field" => RecipeJson(CertificateAuth(), ",\n      \"notARecipeField\": true"),
        "binding-conflict" => RecipeJson($$"""
        {
          "mode": "AppRegistrationCertificate",
          "tenantId": "{{TenantId}}",
          "organizationKeyId": "{{OrgKeyId}}",
          "chefKeyId": "personal-key-1"
        }
        """),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown fixture"),
    };

    [Theory]
    [MemberData(nameof(SharedFixtures))]
    public void Setup_produces_the_pinned_result_for_every_shared_fixture(string name, bool expectedOk, string expectedKeyword)
    {
        (bool ok, List<object> errors) = Validate(FixtureJson(name));

        Assert.Equal(expectedOk, ok);
        if (expectedKeyword.Length == 0) { Assert.Empty(errors); }
        else { Assert.Contains(expectedKeyword, Keywords(errors)); }
    }
}
