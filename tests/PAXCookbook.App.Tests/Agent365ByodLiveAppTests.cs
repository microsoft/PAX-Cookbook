using System.Collections.Generic;
using System.Text.Json;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Live-app coverage for the Microsoft Agent 365 / BYOD (-UserInfoFile) and
// de-identify integration (engine v1.11.11). These exercise the SHIPPING broker
// stack (src/PAXCookbook.App): the closed save schema (RecipeValidationModel),
// the bake projection (PaxAdapter), and the resume command seam. The pre-existing
// 985-test suite covers the non-shipping native library, not this stack.
public sealed class Agent365ByodLiveAppTests
{
    // Mirrors the request pipeline's JSON -> object tree (objects become
    // Dictionary<string, object?>, arrays become List<object?>, integers become
    // long) so a recipe built here has the exact runtime shape ValidateAll and
    // GetArgvArray receive from a real save / bake.
    private static object? ToTree(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => BuildObject(el),
        JsonValueKind.Array => BuildArray(el),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? (object)l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static Dictionary<string, object?> BuildObject(JsonElement el)
    {
        var o = new Dictionary<string, object?>(System.StringComparer.Ordinal);
        foreach (JsonProperty p in el.EnumerateObject()) { o[p.Name] = ToTree(p.Value); }
        return o;
    }

    private static List<object?> BuildArray(JsonElement el)
    {
        var a = new List<object?>();
        foreach (JsonElement i in el.EnumerateArray()) { a.Add(ToTree(i)); }
        return a;
    }

    private static Dictionary<string, object?> Recipe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return BuildObject(doc.RootElement);
    }

    private const string ValidUlid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    private static string Fmt(List<object> errors) => JsonSerializer.Serialize(errors);

    // ---- Save schema: the new fields load without a 400 ---------------------

    [Fact]
    public void ValidateAll_accepts_agent365_only_recipe()
    {
        var recipe = Recipe($$"""
        {
          "recipeId": "{{ValidUlid}}",
          "recipeSchemaVersion": 1,
          "paxAdapterVersion": "1.11.11",
          "identity": { "name": "Agent 365 catalog" },
          "ingredients": {
            "m365Usage": { "includeM365Usage": false },
            "entraUserData": { "includeUserInfo": false },
            "agent365": { "onlyAgent365Info": true }
          },
          "query": { "mode": "agent365Only" },
          "processing": {},
          "destinations": { "agent365": { "mode": "outputPath", "path": "C:\\PAX\\catalog.csv" } },
          "auth": { "mode": "WebLogin" }
        }
        """);

        (bool ok, var errors) = RecipeValidationModel.ValidateAll(recipe);
        Assert.True(ok, "agent365-only recipe should validate: " + Fmt(errors));
    }

    [Fact]
    public void ValidateAll_accepts_include_agent365_alongside_audit()
    {
        var recipe = Recipe($$"""
        {
          "recipeId": "{{ValidUlid}}",
          "recipeSchemaVersion": 1,
          "paxAdapterVersion": "1.11.11",
          "identity": { "name": "Audit + catalog" },
          "ingredients": {
            "m365Usage": { "includeM365Usage": false },
            "entraUserData": { "includeUserInfo": false },
            "agent365": { "includeAgent365Info": true }
          },
          "query": { "mode": "audit", "dateMode": "previous-day" },
          "processing": {},
          "destinations": {
            "fact": { "mode": "outputPath", "path": "C:\\PAX\\audit.csv" },
            "agent365": { "mode": "outputPath", "path": "C:\\PAX\\catalog.csv" }
          },
          "auth": { "mode": "WebLogin" }
        }
        """);

        (bool ok, var errors) = RecipeValidationModel.ValidateAll(recipe);
        Assert.True(ok, "include-agent365 audit recipe should validate: " + Fmt(errors));
    }

    [Fact]
    public void ValidateAll_accepts_byod_user_info_file()
    {
        var recipe = Recipe($$"""
        {
          "recipeId": "{{ValidUlid}}",
          "recipeSchemaVersion": 1,
          "paxAdapterVersion": "1.11.11",
          "identity": { "name": "BYOD directory" },
          "ingredients": {
            "m365Usage": { "includeM365Usage": false },
            "entraUserData": { "includeUserInfo": true, "userInfoFile": "C:\\PAX\\directory.csv" }
          },
          "query": { "mode": "audit", "dateMode": "previous-day" },
          "processing": {},
          "destinations": { "fact": { "mode": "outputPath", "path": "C:\\PAX\\audit.csv" } },
          "auth": { "mode": "WebLogin" }
        }
        """);

        (bool ok, var errors) = RecipeValidationModel.ValidateAll(recipe);
        Assert.True(ok, "BYOD -UserInfoFile recipe should validate: " + Fmt(errors));
    }

    [Fact]
    public void ValidateAll_accepts_deidentify_and_filler_label()
    {
        // Regression for the closed-schema gap: the live save node was missing
        // processing.deidentify / fillerLabel / fillerLabelText, so a de-identify
        // recipe used to 400 even though the adapter already emitted them.
        var recipe = Recipe($$"""
        {
          "recipeId": "{{ValidUlid}}",
          "recipeSchemaVersion": 1,
          "paxAdapterVersion": "1.11.11",
          "identity": { "name": "De-identified rollup" },
          "ingredients": {
            "m365Usage": { "includeM365Usage": false },
            "entraUserData": { "includeUserInfo": false }
          },
          "query": { "mode": "audit", "dateMode": "previous-day" },
          "processing": { "rollup": "Rollup", "deidentify": true, "fillerLabel": "Fixed", "fillerLabelText": "Redacted" },
          "destinations": { "fact": { "mode": "outputPath", "path": "C:\\PAX\\audit.csv" } },
          "auth": { "mode": "WebLogin" }
        }
        """);

        (bool ok, var errors) = RecipeValidationModel.ValidateAll(recipe);
        Assert.True(ok, "de-identify / filler-label recipe should validate: " + Fmt(errors));
    }

    // ---- Bake projection: the new switches emit correctly -------------------

    [Fact]
    public void GetArgvArray_emits_agent365_include_and_byod_suppresses_include_user_info()
    {
        var recipe = Recipe($$"""
        {
          "recipeId": "{{ValidUlid}}",
          "recipeSchemaVersion": 1,
          "paxAdapterVersion": "1.11.11",
          "identity": { "name": "Audit + catalog + BYOD" },
          "ingredients": {
            "m365Usage": { "includeM365Usage": false },
            "entraUserData": { "includeUserInfo": true, "userInfoFile": "C:\\PAX\\directory.csv" },
            "agent365": { "includeAgent365Info": true }
          },
          "query": { "mode": "audit", "dateMode": "previous-day" },
          "processing": {},
          "destinations": {
            "fact": { "mode": "outputPath", "path": "C:\\PAX\\audit.csv" },
            "agent365": { "mode": "outputPath", "path": "C:\\PAX\\catalog.csv" }
          },
          "auth": { "mode": "WebLogin" }
        }
        """);

        List<string> argv = PaxAdapter.GetArgvArray(recipe, chefKey: null, executionMode: "local-manual");

        Assert.Contains("-IncludeAgent365Info", argv);
        Assert.Contains("-UserInfoFile", argv);
        Assert.Contains("C:\\PAX\\directory.csv", argv);
        Assert.Contains("-OutputPathAgent365Info", argv);
        Assert.Contains("C:\\PAX\\catalog.csv", argv);
        // BYOD supplies the directory, so -IncludeUserInfo must be suppressed.
        Assert.DoesNotContain("-IncludeUserInfo", argv);
    }

    [Fact]
    public void GetArgvArray_agent365_only_emits_only_agent365_and_skips_audit_shape()
    {
        var recipe = Recipe($$"""
        {
          "recipeId": "{{ValidUlid}}",
          "recipeSchemaVersion": 1,
          "paxAdapterVersion": "1.11.11",
          "identity": { "name": "Agent 365 catalog" },
          "ingredients": {
            "m365Usage": { "includeM365Usage": false },
            "entraUserData": { "includeUserInfo": false },
            "agent365": { "onlyAgent365Info": true }
          },
          "query": { "mode": "agent365Only" },
          "processing": {},
          "destinations": { "agent365": { "mode": "append", "appendFile": "C:\\PAX\\catalog.csv" } },
          "auth": { "mode": "WebLogin" }
        }
        """);

        List<string> argv = PaxAdapter.GetArgvArray(recipe, chefKey: null, executionMode: "local-manual");

        Assert.Contains("-OnlyAgent365Info", argv);
        Assert.Contains("-AppendAgent365Info", argv);
        Assert.Contains("C:\\PAX\\catalog.csv", argv);
        Assert.DoesNotContain("-OnlyUserInfo", argv);
        Assert.DoesNotContain("-IncludeUserInfo", argv);
        Assert.DoesNotContain("-StartDate", argv);
        Assert.DoesNotContain("-EndDate", argv);
    }

    // ---- Resume seam: output-shaping switches are checkpoint-restored --------

    [Fact]
    public void ResumeCommand_omits_output_shaping_switches()
    {
        // The engine saves and restores dashboard / de-identify / filler-label
        // from the checkpoint, so a resume must not re-pass them. The resume argv
        // carries only the resume target, -Force, and the auth tail.
        string command = RecipeReadModel.TestSeamBuildResumeCommand(
            "C:\\PAX\\cook\\checkpoint.json", force: true);

        Assert.Contains("-Resume", command);
        Assert.Contains("-Force", command);
        Assert.DoesNotContain("-Dashboard", command);
        Assert.DoesNotContain("-Deidentify", command);
        Assert.DoesNotContain("-FillerLabel", command);
    }
}
