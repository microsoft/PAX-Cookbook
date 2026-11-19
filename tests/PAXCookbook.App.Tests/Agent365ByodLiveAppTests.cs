using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using PAXCookbook.App;
using Xunit;
using Xunit.Abstractions;

namespace PAXCookbook.App.Tests;

// Live-app coverage for the Microsoft Agent 365 / BYOD (-UserInfoFile) and
// de-identify integration (engine v1.11.11). These exercise the SHIPPING broker
// stack (src/PAXCookbook.App): the closed save schema (RecipeValidationModel),
// the bake projection (PaxAdapter), and the resume command seam. The pre-existing
// 985-test suite covers the non-shipping native library, not this stack.
public sealed class Agent365ByodLiveAppTests
{
  private readonly ITestOutputHelper _output;

  public Agent365ByodLiveAppTests(ITestOutputHelper output) => _output = output;

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

      [Theory]
      [InlineData("plain-user")]
      [InlineData("literal,$($global:LiteralTransportSentinel='changed';'value')")]
      public async Task InvocationPlan_preserves_literal_parameter_values(string userId)
      {
        string testRoot = Path.Combine(Path.GetTempPath(), "literal-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
          string receiverPath = Path.Combine(testRoot, "receiver.ps1");
          await File.WriteAllTextAsync(receiverPath, """
            param([string[]]$UserIds, [string]$Auth, [string]$OutputPath)
            $sentinel = Get-Variable -Name LiteralTransportSentinel -Scope Global -ErrorAction SilentlyContinue
            [ordered]@{
              ParameterNames = @($PSBoundParameters.Keys | Sort-Object)
              UserIdsType = $UserIds.GetType().FullName
              UserIdsCount = $UserIds.Count
              UserIds = @($UserIds)
              AuthType = $Auth.GetType().FullName
              Auth = $Auth
              OutputPathType = $OutputPath.GetType().FullName
              OutputPath = $OutputPath
              Sentinel = if ($null -eq $sentinel) { 'unchanged' } else { $sentinel.Value }
              UnboundArguments = @($args | ForEach-Object { [string]$_ })
              PowerShellVersion = $PSVersionTable.PSVersion.ToString()
            } | ConvertTo-Json -Depth 5 -Compress
            """);
          var recipe = Recipe($$"""
            {
              "recipeId": "{{ValidUlid}}",
              "recipeSchemaVersion": 1,
              "paxAdapterVersion": "1.11.11",
              "identity": { "name": "Literal transport" },
              "query": { "mode": "audit", "dateMode": "previous-day", "userIds": [{{JsonSerializer.Serialize(userId)}}] },
              "processing": {},
              "ingredients": {
              "m365Usage": { "includeM365Usage": false },
              "entraUserData": { "includeUserInfo": false }
              },
              "destinations": { "fact": { "mode": "outputPath", "path": {{JsonSerializer.Serialize(Path.Combine(testRoot, "fact.csv"))}} } },
              "auth": { "mode": "WebLogin" }
            }
            """);
          var plan = PaxAdapter.GetInvocationPlan(recipe, receiverPath, null, "local-manual", RecipeParameterTransportTests.FindAppRoot());
          string pwshPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "pwsh.exe"))
            .First(File.Exists);
          var startInfo = new ProcessStartInfo(Path.GetFullPath(pwshPath))
          {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = testRoot,
          };
          foreach (string argument in plan.SpawnArgv) { startInfo.ArgumentList.Add(argument); }
          foreach (string key in startInfo.Environment.Keys.ToArray())
          {
            if (key.StartsWith("GRAPH_", StringComparison.OrdinalIgnoreCase))
            {
              startInfo.Environment.Remove(key);
            }
          }

          using var child = new Process { StartInfo = startInfo };
          Assert.True(child.Start());
          await child.StandardInput.WriteLineAsync(Assert.IsType<string>(plan.ParameterJson));
          child.StandardInput.Close();
          Task<string> stdoutTask = child.StandardOutput.ReadToEndAsync();
          Task<string> stderrTask = child.StandardError.ReadToEndAsync();
          using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
          try
          {
            await child.WaitForExitAsync(timeout.Token);
          }
          catch (OperationCanceledException)
          {
            if (!child.HasExited) { child.Kill(); }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            throw new TimeoutException("The owned literal transport receiver exceeded its time limit.");
          }
          string stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5));
          string stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
          _output.WriteLine(JsonSerializer.Serialize(new
          {
            Executable = startInfo.FileName,
            Arguments = plan.SpawnArgv,
            ProcessId = child.Id,
            child.ExitCode,
            Stdout = stdout,
            Stderr = stderr,
            AssemblyIdentity = typeof(PaxAdapter).Assembly.FullName,
            AssemblyPath = typeof(PaxAdapter).Assembly.Location,
            AssemblyModuleId = typeof(PaxAdapter).Module.ModuleVersionId,
          }));
          Assert.Equal(0, child.ExitCode);
          Assert.Equal(string.Empty, stderr);
          var receipt = JsonSerializer.Deserialize<LiteralTransportReceipt>(stdout);
          Assert.NotNull(receipt);
          var positive = new LiteralTransportReceipt(
            new[] { "Auth", "OutputPath", "UserIds" }, "System.String[]", 1,
            new[] { userId }, "System.String", "WebLogin", "System.String", testRoot,
            "unchanged", Array.Empty<string>(), receipt.PowerShellVersion);
          var negative = positive with { Sentinel = "changed" };
          bool positiveAccepted = MatchesLiteralReceipt(positive, userId, testRoot);
          bool negativeRejected = !MatchesLiteralReceipt(negative, userId, testRoot);
          bool actualAccepted = MatchesLiteralReceipt(receipt, userId, testRoot);
          _output.WriteLine(JsonSerializer.Serialize(new
          {
            Universe = "Receiver PSBoundParameters names, CLR types, UserIds count and exact values, Auth, OutputPath, unbound arguments, and child-local sentinel",
            Expected = positive,
            Actual = receipt,
            PositiveControl = positive,
            NegativeControl = negative,
            PositiveAccepted = positiveAccepted,
            NegativeRejected = negativeRejected,
            ActualAccepted = actualAccepted,
          }));
          Assert.True(positiveAccepted, "The literal receipt predicate refused its positive control.");
          Assert.True(negativeRejected, "The literal receipt predicate accepted its negative control.");
          Assert.True(actualAccepted, "The invocation changed a literal parameter or the child-local sentinel; see the structured receiver receipt.");
        }
        finally
        {
          Directory.Delete(testRoot, recursive: true);
        }
      }

      [Theory]
      [InlineData("plain", 0)]
      [InlineData("O'Brien", 1)]
      [InlineData("literal $5", 3)]
      [InlineData("tick`label", 1)]
      [InlineData("first\nsecond", 3)]
      [InlineData("caf\u00e9 \u2018quoted\u2019 \u201cvalue\u201d", 1)]
      [InlineData("-Force", 3)]
      public async Task DisplayedCommand_preserves_literal_values_and_types(string value, int count)
      {
        string[] values = new[] { value, "", "-Auth" }.Take(count).ToArray();
        var recipe = TypedRecipe();
        ((Dictionary<string, object?>)recipe["query"]!)["userIds"] = values.Cast<object?>().ToList();
        recipe["processing"] = new Dictionary<string, object?>
        {
          ["rollup"] = "Rollup", ["fillerLabel"] = "Fixed", ["fillerLabelText"] = value,
        };
        recipe["advanced"] = new Dictionary<string, object?> { ["extraArguments"] = "-SkipVersionCheck:$false" };
        var expected = new Dictionary<string, object?>
        {
          ["Auth"] = "WebLogin", ["UserIds"] = values, ["Rollup"] = true,
          ["FillerLabel"] = "Fixed", ["FillerLabelText"] = value, ["SkipVersionCheck"] = false,
        };
        var types = new Dictionary<string, string>
        {
          ["Auth"] = "System.String", ["UserIds"] = "System.String[]",
          ["Rollup"] = "System.Management.Automation.SwitchParameter", ["FillerLabel"] = "System.String",
          ["FillerLabelText"] = "System.String", ["SkipVersionCheck"] = "System.Management.Automation.SwitchParameter",
        };
        await AssertBoundInvocation(path => PaxAdapter.GetInvocationPlan(recipe, path, null, "local-manual", RecipeParameterTransportTests.FindAppRoot()), expected, types, displayedCommand: true);
      }

      [Theory]
      [InlineData(0)]
      [InlineData(1)]
      [InlineData(4)]
      public async Task TypedInvocation_preserves_array_cardinality(int count)
      {
        string[] values = new[] { "-Auth", "", "O'Brien; $5, [label]", "caf\u00e9 space" }.Take(count).ToArray();
        var recipe = TypedRecipe();
        ((Dictionary<string, object?>)recipe["query"]!)["userIds"] = values.Cast<object?>().ToList();
        await AssertBoundInvocation(path => PaxAdapter.GetInvocationPlan(recipe, path, null, "local-manual", RecipeParameterTransportTests.FindAppRoot()),
          new() { ["Auth"] = "WebLogin", ["UserIds"] = values },
          new() { ["Auth"] = "System.String", ["UserIds"] = "System.String[]" });
      }

      [Theory]
      [InlineData("O'Brien; $5, [label] \"quoted\" `literal")]
      [InlineData("caf\u00e9 \u2018quoted\u2019 label")]
      [InlineData("\u201aquoted\u201b label")]
      [InlineData("-Auth")]
      public async Task TypedInvocation_preserves_scalar_literals(string value)
      {
        var recipe = TypedRecipe();
        recipe["processing"] = new Dictionary<string, object?>
        {
          ["rollup"] = "Rollup", ["fillerLabel"] = "Fixed", ["fillerLabelText"] = value,
        };
        await AssertBoundInvocation(path => PaxAdapter.GetInvocationPlan(recipe, path, null, "local-manual", RecipeParameterTransportTests.FindAppRoot()),
          new() { ["Auth"] = "WebLogin", ["Rollup"] = true, ["FillerLabel"] = "Fixed", ["FillerLabelText"] = value },
          new() { ["Auth"] = "System.String", ["Rollup"] = "System.Management.Automation.SwitchParameter", ["FillerLabel"] = "System.String", ["FillerLabelText"] = "System.String" });
      }

      [Theory]
      [InlineData(false)]
      [InlineData(true)]
      public async Task TypedInvocation_preserves_advanced_types_in_noninvariant_culture(bool flag)
      {
        var recipe = TypedRecipe();
        recipe["advanced"] = new Dictionary<string, object?>
        {
          ["extraArguments"] = "-BlockHours '+0.25' -MaxMemoryMB -1 -ParallelMode \"Auto\" -SkipVersionCheck:$" + flag.ToString().ToLowerInvariant(),
        };
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
          System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
          await AssertBoundInvocation(path => PaxAdapter.GetInvocationPlan(recipe, path, null, "local-manual", RecipeParameterTransportTests.FindAppRoot()),
            new() { ["Auth"] = "WebLogin", ["BlockHours"] = 0.25, ["MaxMemoryMB"] = -1, ["ParallelMode"] = "Auto", ["SkipVersionCheck"] = flag },
            new() { ["Auth"] = "System.String", ["BlockHours"] = "System.Double", ["MaxMemoryMB"] = "System.Int32", ["ParallelMode"] = "System.String", ["SkipVersionCheck"] = "System.Management.Automation.SwitchParameter" });
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
      }

      [Theory]
      [InlineData("-SkipVersionCheck -SkipVersionCheck")]
      [InlineData("-SkipVersion")]
      [InlineData("-UnknownOption value")]
      [InlineData("-Auth WebLogin")]
      [InlineData("-Resume 'checkpoint.json'")]
      [InlineData("-OutputPath 'C:\\output'")]
      [InlineData("-Dashboard AIBV")]
      [InlineData("-UserInfoFile 'directory.csv'")]
      [InlineData("-BlockHours (0.25)")]
      [InlineData("-BlockHours 0,25")]
      [InlineData("-BlockHours 0.25; -Force")]
      [InlineData("-SkipVersionCheck | Out-Null")]
      [InlineData("-SkipVersionCheck > output.txt")]
      [InlineData("-SkipVersionCheck # note")]
      [InlineData("@options")]
      [InlineData("-ParallelMode @('Auto')")]
      [InlineData("-ParallelMode 'Auto")]
      public void AdvancedGrammar_rejects_invalid_input_before_projection(string extra)
      {
        Assert.True(RecipeValidationModel.TryParseAdvancedArguments("-SkipVersionCheck:$false -BlockHours 0.25", out _));
        Assert.False(RecipeValidationModel.TryParseAdvancedArguments(extra, out var parameters));
        Assert.Empty(parameters);
        var recipe = TypedRecipe();
        recipe["advanced"] = new Dictionary<string, object?> { ["extraArguments"] = extra };
        var validation = RecipeValidationModel.ValidateAll(recipe);
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors.Cast<Dictionary<string, object?>>(), error =>
          Equals(error["instancePath"], "/advanced/extraArguments") && Equals(error["keyword"], "literalArguments"));
        Assert.Throws<PaxAdapter.ProjectionException>(() => PaxAdapter.GetInvocationPlan(recipe, "unused.ps1", null, "local-manual"));
      }

      [Theory]
      [InlineData(true, false, false)]
      [InlineData(true, true, false)]
      [InlineData(false, false, false)]
      [InlineData(false, true, false)]
      [InlineData(true, false, true)]
      [InlineData(true, true, true)]
      [InlineData(false, false, true)]
      [InlineData(false, true, true)]
      public async Task ResumeInvocation_preserves_remaining_arguments(bool explicitFile, bool force, bool displayedCommand)
      {
        string target = explicitFile ? "C:\\literal folder\\O'Brien\u2019s checkpoint.json" : "C:\\literal folder\\O'Brien\u2019s output";
        var method = typeof(RecipeReadModel).GetMethod("BuildResumeInvocationPlan",
          System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        object kind = Enum.Parse(method.GetParameters()[1].ParameterType, explicitFile ? "JsonFile" : "Directory");
        var expected = new Dictionary<string, object?> { ["RemainingArgs"] = explicitFile ? new[] { "-Resume", target } : new[] { "-Resume" } };
        var types = new Dictionary<string, string> { ["RemainingArgs"] = "System.String[]" };
        if (!explicitFile) { expected["OutputPath"] = target; types["OutputPath"] = "System.String"; }
        if (force) { expected["Force"] = true; types["Force"] = "System.Management.Automation.SwitchParameter"; }
        await AssertBoundInvocation(path => (PaxAdapter.InvocationPlan)method.Invoke(null,
          new object?[] { target, kind, force, null, path, RecipeParameterTransportTests.FindAppRoot() })!, expected, types, displayedCommand);
      }

      [Theory]
      [InlineData(false)]
      [InlineData(true)]
      public void Preview_validates_stored_and_draft_content_before_authority(bool stored)
      {
        WithStoredRecipe((workspace, recipe, file) =>
        {
          object body = stored ? new Dictionary<string, object?> { ["recipeId"] = ValidUlid } : recipe;
          var accepted = RecipePreviewModel.Project(workspace, "unused.ps1", ResumeCookPreparationCharacterizationTests.Version(), body,
            () => throw new InvalidOperationException("Authority must not be accessed."));
          Assert.True(accepted.Ok);
          Assert.NotNull(accepted.Plan);
          recipe["advanced"] = new Dictionary<string, object?> { ["extraArguments"] = "-SkipVersion" };
          recipe["auth"] = new Dictionary<string, object?>
          {
            ["mode"] = "AppRegistrationCertificate", ["tenantId"] = "11111111-2222-3333-4444-555555555555", ["organizationKeyId"] = "synthetic.key",
          };
          File.WriteAllText(file, JsonSerializer.Serialize(recipe));
          var rejected = RecipePreviewModel.Project(workspace, "unused.ps1", ResumeCookPreparationCharacterizationTests.Version(), body,
            () => throw new InvalidOperationException("Invalid content reached authority resolution."));
          Assert.Equal(400, rejected.Status);
          Assert.Null(rejected.Plan);
          AssertLiteralFieldError(rejected.ErrorBody!);
        });
      }

      [Fact]
      public void Schedule_rejects_invalid_content_before_key_resolution()
      {
        WithStoredRecipe((workspace, recipe, file) =>
        {
          var recurrence = new Dictionary<string, object?> { ["kind"] = "daily", ["hour"] = 2L, ["minute"] = 0L };
          var valid = RecipeScheduleModel.PutScheduledTask(workspace, workspace, ValidUlid, recurrence, null, null, null);
          Assert.Equal(409, valid.Status);
          recipe["advanced"] = new Dictionary<string, object?> { ["extraArguments"] = "-SkipVersion" };
          File.WriteAllText(file, JsonSerializer.Serialize(recipe));
          var invalid = RecipeScheduleModel.PutScheduledTask(workspace, workspace, ValidUlid, recurrence, null, null, null);
          Assert.Equal(412, invalid.Status);
          AssertLiteralFieldError(invalid.Body);
        });
      }

      private static void AssertLiteralFieldError(object body)
      {
        var document = JsonSerializer.SerializeToElement(body);
        Assert.Contains(document.GetProperty("errors").EnumerateArray(), error =>
          error.GetProperty("instancePath").GetString() == "/advanced/extraArguments" && error.GetProperty("keyword").GetString() == "literalArguments");
      }

      private static void WithStoredRecipe(Action<string, Dictionary<string, object?>, string> action)
      {
        string workspace = Path.Combine(Path.GetTempPath(), "semantic-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
          WorkspaceDatabase.EnsureInitialized(workspace);
          using (var connection = RecipeCookReservationTests.Open(workspace)) { RecipeCookReservationTests.SeedRecipeRow(connection, ValidUlid); }
          var recipe = TypedRecipe();
          recipe["destinations"] = new Dictionary<string, object?>
          {
            ["fact"] = new Dictionary<string, object?> { ["mode"] = "outputPath", ["path"] = Path.Combine(workspace, "fact.csv") },
          };
          string file = Path.Combine(workspace, "Recipes", ValidUlid + ".recipe.json");
          Directory.CreateDirectory(Path.GetDirectoryName(file)!);
          File.WriteAllText(file, JsonSerializer.Serialize(recipe));
          action(workspace, recipe, file);
        }
        finally
        {
          Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
          Directory.Delete(workspace, recursive: true);
        }
      }

      private static Dictionary<string, object?> TypedRecipe() => Recipe($$$"""
        {
            "recipeId":"{{{ValidUlid}}}", "recipeSchemaVersion":1, "paxAdapterVersion":"1.11.11",
          "identity":{"name":"Literal binding"}, "ingredients":{"m365Usage":{"includeM365Usage":false},"entraUserData":{"includeUserInfo":false}},
          "query":{"mode":"audit","dateMode":"previous-day"}, "processing":{}, "destinations":{}, "auth":{"mode":"WebLogin"}
        }
        """);

      private async Task AssertBoundInvocation(Func<string, PaxAdapter.InvocationPlan> project,
        Dictionary<string, object?> expected, Dictionary<string, string> expectedTypes, bool displayedCommand = false,
        [System.Runtime.CompilerServices.CallerFilePath] string testSourcePath = "")
      {
        string root = Path.Combine(Path.GetTempPath(), "typed-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
          string receiver = Path.Combine(root, "receiver.ps1");
          await File.WriteAllTextAsync(receiver, """
            param(
              [AllowEmptyCollection()][AllowEmptyString()][string[]]$UserIds,
              [string]$Auth, [switch]$Rollup, [string]$FillerLabel, [string]$FillerLabelText,
              [double]$BlockHours, [int]$MaxMemoryMB, [string]$ParallelMode, [switch]$SkipVersionCheck,
              [string]$OutputPath, [switch]$Force,
              [Parameter(ValueFromRemainingArguments=$true)][string[]]$RemainingArgs
            )
            $values = @{}
            $types = @{}
            foreach ($entry in $PSBoundParameters.GetEnumerator()) {
              $types[$entry.Key] = $entry.Value.GetType().FullName
                  if ($entry.Value -is [switch]) { $values[$entry.Key] = [bool]$entry.Value }
                  else { $values[$entry.Key] = $entry.Value }
            }
                $resume = $null
                $resumeSpecified = $false
                for ($index = 0; $index -lt $RemainingArgs.Count; $index++) {
                  if ($RemainingArgs[$index] -eq '-Resume') {
                    $resumeSpecified = $true
                    if (($index + 1) -lt $RemainingArgs.Count -and $RemainingArgs[$index + 1] -notmatch '^-') {
                      $resume = $RemainingArgs[$index + 1]
                      $index++
                    } else { $resume = '' }
                  }
                }
                $unbound = if ($null -eq $args) { ,([object[]]::new(0)) } else { ,@($args) }
                @{ Values=$values; Types=$types; Unbound=$unbound; ResumeSpecified=$resumeSpecified; ResumeTarget=$resume; PowerShellVersion=$PSVersionTable.PSVersion.ToString() } | ConvertTo-Json -Depth 8 -Compress -EscapeHandling EscapeNonAscii
            """);
          var plan = project(receiver);
          string executable = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "pwsh.exe")).First(File.Exists);
          var info = new ProcessStartInfo(executable)
          {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = root,
          };
          if (displayedCommand)
          {
            Assert.StartsWith("pwsh -NoProfile -NoLogo -File ", plan.SpawnCommand, StringComparison.Ordinal);
            Assert.Contains(receiver, plan.SpawnCommand, StringComparison.Ordinal);
          }
          foreach (string argument in plan.SpawnArgv) { info.ArgumentList.Add(argument); }
          foreach (string key in info.Environment.Keys.ToArray())
          {
            if (key.StartsWith("GRAPH_", StringComparison.OrdinalIgnoreCase)) { info.Environment.Remove(key); }
          }
          using var child = new Process { StartInfo = info };
          Assert.True(child.Start());
          await child.StandardInput.WriteLineAsync(Assert.IsType<string>(plan.ParameterJson));
          child.StandardInput.Close();
          Task<string> stdout = child.StandardOutput.ReadToEndAsync();
          Task<string> stderr = child.StandardError.ReadToEndAsync();
          using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
          try { await child.WaitForExitAsync(timeout.Token); }
          catch (OperationCanceledException)
          {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            throw;
          }
          string output = await stdout.WaitAsync(TimeSpan.FromSeconds(5));
          string error = await stderr.WaitAsync(TimeSpan.FromSeconds(5));
          _output.WriteLine(JsonSerializer.Serialize(new { Executable = executable, displayedCommand, plan.SpawnArgv, plan.SpawnCommand, child.ExitCode, Stdout = output, Stderr = error,
            AssemblyPath = typeof(PaxAdapter).Assembly.Location, AssemblyIdentity = typeof(PaxAdapter).Assembly.FullName,
            AssemblyModuleId = typeof(PaxAdapter).Module.ModuleVersionId,
            AssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(PaxAdapter).Assembly.Location))),
            TestAssemblyPath = GetType().Assembly.Location, TestModuleId = GetType().Module.ModuleVersionId,
            TestAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(GetType().Assembly.Location))),
            TestSourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(testSourcePath))) }));
          BoundReceipt? actual = null;
          try { actual = JsonSerializer.Deserialize<BoundReceipt>(output); }
          catch (JsonException) { }
          var remaining = expected.TryGetValue("RemainingArgs", out object? remainingValue) ? (string[])remainingValue! : Array.Empty<string>();
          var positive = new BoundReceipt(JsonSerializer.SerializeToElement(expected), expectedTypes, Array.Empty<string>(), actual?.PowerShellVersion ?? string.Empty,
            remaining.Length > 0, remaining.Length > 1 ? remaining[1] : remaining.Length == 1 ? string.Empty : null);
          var negative = positive with { Unbound = new[] { "unexpected" } };
          bool Matches(BoundReceipt? receipt) => receipt is not null && receipt.Unbound.Length == 0 &&
            receipt.ResumeSpecified == positive.ResumeSpecified && receipt.ResumeTarget == positive.ResumeTarget &&
            System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(receipt.Values.GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(positive.Values.GetRawText())) &&
            receipt.Types.Count == expectedTypes.Count && expectedTypes.All(pair => receipt.Types.TryGetValue(pair.Key, out string? type) && type == pair.Value);
          bool positiveAccepted = Matches(positive), negativeRejected = !Matches(negative), actualAccepted = Matches(actual);
            _output.WriteLine(JsonSerializer.Serialize(new { Universe = "Exact bound parameter names, values, CLR types, unbound arguments, and manual Resume interpretation of the owned inert receiver", Positive = positive, Negative = negative, Actual = actual, positiveAccepted, negativeRejected, actualAccepted }));
          Assert.True(positiveAccepted);
          Assert.True(negativeRejected);
          Assert.Equal(0, child.ExitCode);
          Assert.Equal(string.Empty, error);
          Assert.True(actualAccepted);
        }
        finally { Directory.Delete(root, recursive: true); }
      }

      private sealed record BoundReceipt(JsonElement Values, Dictionary<string, string> Types, string[] Unbound, string PowerShellVersion, bool ResumeSpecified, string? ResumeTarget);

        private sealed record LiteralTransportReceipt(
        string[] ParameterNames, string UserIdsType, int UserIdsCount, string[] UserIds,
        string AuthType, string Auth, string OutputPathType, string OutputPath,
        string Sentinel, string[] UnboundArguments, string PowerShellVersion);

      private static bool MatchesLiteralReceipt(LiteralTransportReceipt receipt, string userId, string outputPath) =>
        receipt.ParameterNames.SequenceEqual(new[] { "Auth", "OutputPath", "UserIds" }, StringComparer.Ordinal) &&
        receipt.UserIdsType == "System.String[]" && receipt.UserIdsCount == 1 &&
        receipt.UserIds.SequenceEqual(new[] { userId }, StringComparer.Ordinal) &&
        receipt.AuthType == "System.String" && receipt.Auth == "WebLogin" &&
        receipt.OutputPathType == "System.String" && receipt.OutputPath == outputPath &&
        receipt.Sentinel == "unchanged" && receipt.UnboundArguments.Length == 0;
}
