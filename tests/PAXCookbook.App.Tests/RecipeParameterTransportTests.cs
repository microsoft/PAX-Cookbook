using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PAXCookbook.App;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

public sealed class RecipeParameterTransportTests
{
    private const string RecipeId = RecipeCookReservationTests.RecipeA;
    private const string SyntheticSecret = "inert-fixture-credential-6E6C7D";

    [Theory]
    [InlineData(CookTriggerKind.Manual, 17)]
    [InlineData(CookTriggerKind.Scheduled, 37)]
    [InlineData(CookTriggerKind.Resume, 0)]
    public async Task ProductionCallersDeliverTypedDataAndRecordTheTargetExit(CookTriggerKind trigger, int targetExit)
    {
        string workspace = Directory.CreateTempSubdirectory("pax-transport-").FullName;
        try
        {
            var (version, engine, checkpoint) = CreateCookFixture(workspace, trigger, targetExit, invalid: false);
            JsonElement? preview = null;
            if (trigger != CookTriggerKind.Resume)
            {
                var response = RecipePreviewModel.Handle(workspace, engine.ManagedEnginePath, version,
                    new Dictionary<string, object?> { ["recipeId"] = RecipeId }, organizationAuthority: null);
                Assert.Equal(200, response.Status);
                preview = JsonSerializer.SerializeToElement(response.Body);
                Assert.DoesNotContain(SyntheticSecret, preview.Value.GetRawText(), StringComparison.Ordinal);
                string?[] displayed = preview.Value.GetProperty("argv").EnumerateArray().Select(value => value.GetString()).ToArray();
                int resultSizeIndex = Array.IndexOf(displayed, "-ResultSize");
                Assert.InRange(resultSizeIndex, 0, displayed.Length - 2);
                Assert.Equal("[int]50", displayed[resultSizeIndex + 1]);
            }
            var terminal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            int launches = 0;
            var dependencies = InertDependencies(terminal, info =>
            {
                Assert.Equal(new[] { "-NoProfile", "-NoLogo", "-File" }, info.ArgumentList.Take(3));
                Assert.Equal(engine.ManagedEnginePath, info.ArgumentList[^1]);
                Assert.DoesNotContain(SyntheticSecret, string.Join(" ", info.ArgumentList), StringComparison.Ordinal);
                if (preview is { } view)
                {
                    Assert.Equal(view.GetProperty("spawn").GetProperty("argv").EnumerateArray().Select(value => value.GetString()),
                        info.ArgumentList);
                }
                Interlocked.Increment(ref launches);
                return new Process { StartInfo = info };
            });

            var result = RecipeReadModel.TestSeamRunInertCook(trigger, workspace, version, engine,
                RecipeId, checkpoint, InertPowerShell(), dependencies);
            Assert.Equal(201, result.Status);
            Assert.Equal(targetExit, await terminal.Task.WaitAsync(TimeSpan.FromSeconds(75)));
            Assert.Equal(1, launches);
            JsonElement responseBody = JsonSerializer.SerializeToElement(result.Body);
            string cookFolder = Path.Combine(workspace, responseBody.GetProperty("cookFolder").GetString()!);
            using var finished = JsonDocument.Parse(File.ReadAllText(Path.Combine(cookFolder, "finished.json")));
            Assert.Equal(targetExit, finished.RootElement.GetProperty("exitCode").GetInt32());

            using var connection = RecipeCookReservationTests.Open(workspace);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT trigger, status, exit_code, command_argv_json FROM cooks WHERE cook_id = $id;";
            command.Parameters.AddWithValue("$id", responseBody.GetProperty("cookId").GetString()!);
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal(trigger.ToString().ToLowerInvariant(), row.GetString(0));
            Assert.Equal(targetExit == 0 ? "completed" : "errored", row.GetString(1));
            Assert.Equal(targetExit, row.GetInt32(2));
            Assert.DoesNotContain(SyntheticSecret, row.GetString(3), StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticSecret, responseBody.GetRawText(), StringComparison.Ordinal);

            string logPath = Path.Combine(cookFolder, "cook.log");
            string receipt = Assert.Single(File.ReadLines(logPath).Where(line => line.StartsWith("fixture=", StringComparison.Ordinal)));
            using var capture = JsonDocument.Parse(receipt["fixture=".Length..]);
            JsonElement values = capture.RootElement;
            Assert.True(values.GetProperty("SawSyntheticCredential").GetBoolean());
            if (trigger == CookTriggerKind.Resume)
            {
                Assert.Equal(new[] { "-Resume", checkpoint },
                    values.GetProperty("Remaining").EnumerateArray().Select(value => value.GetString()));
            }
            else
            {
                Assert.True(values.GetProperty("Catalog").GetBoolean());
                Assert.Equal(50, values.GetProperty("Count").GetInt32());
                Assert.Equal("System.Int32", values.GetProperty("CountType").GetString());
                Assert.False(values.GetProperty("Parallel").GetBoolean());
            }
            foreach (string file in Directory.EnumerateFiles(cookFolder, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(SyntheticSecret, File.ReadAllText(file), StringComparison.Ordinal);
            }
            Assert.Contains("[REDACTED]", File.ReadAllText(logPath), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData(CookTriggerKind.Manual, 412)]
    [InlineData(CookTriggerKind.Scheduled, 412)]
    [InlineData(CookTriggerKind.Resume, 400)]
    public void ProductionCallersRefuseInvalidInputBeforeCreatingAChild(CookTriggerKind trigger, int expectedStatus)
    {
        string workspace = Directory.CreateTempSubdirectory("pax-transport-").FullName;
        try
        {
            var (version, engine, checkpoint) = CreateCookFixture(workspace, trigger, 0, invalid: true);
            int launches = 0;
            var terminal = new TaskCompletionSource<int>();
            var dependencies = InertDependencies(terminal, _ =>
            {
                Interlocked.Increment(ref launches);
                throw new InvalidOperationException("Invalid input must never reach process creation.");
            });
            var result = RecipeReadModel.TestSeamRunInertCook(trigger, workspace, version, engine,
                RecipeId, checkpoint, InertPowerShell(), dependencies);
            Assert.Equal(expectedStatus, result.Status);
            Assert.Equal(0, launches);
            Assert.False(terminal.Task.IsCompleted);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, 201)]
    [InlineData(true, 400)]
    public void HandoffTextPassesThroughTheSameContractBeforeSave(bool invalid, int expectedStatus)
    {
        string workspace = Directory.CreateTempSubdirectory("pax-transport-").FullName;
        try
        {
            var (version, engine, _) = CreateCookFixture(workspace, CookTriggerKind.Manual, 0, invalid);
            string recipePath = Path.Combine(workspace, "Recipes", RecipeId + ".recipe.json");
            string importedFile = Path.Combine(workspace, "inert-import.pax");
            File.Copy(recipePath, importedFile);
            string handoff = ImportHandoffQueue.ResolveDir(workspace);
            string ticket = Assert.IsType<string>(ImportHandoffQueue.Enqueue(handoff, new FileOpenRequest(importedFile, "pax", true)));
            var consumed = ImportHandoffQueue.HandleConsume(handoff, new Dictionary<string, object?> { ["id"] = ticket });
            Assert.Equal(200, consumed.status);
            string text = JsonSerializer.SerializeToElement(consumed.body).GetProperty("text").GetString()!;
            var saved = RecipeCreateModel.Handle(workspace, version, JsonModel.Parse(text, rejectAmbiguousProperties: true));
            Assert.Equal(expectedStatus, saved.Status);
            Assert.Equal(invalid ? 1 : 2, Directory.GetFiles(Path.Combine(workspace, "Recipes"), "*.recipe.json").Length);
            if (!invalid)
            {
                string savedId = JsonSerializer.SerializeToElement(saved.Body).GetProperty("recipeId").GetString()!;
                var preview = RecipePreviewModel.Project(workspace, engine.ManagedEnginePath, version,
                    new Dictionary<string, object?> { ["recipeId"] = savedId }, organizationAuthority: null);
                Assert.True(preview.Ok);
                Assert.Contains(preview.Plan!.Parameters, parameter => parameter.Name == "ResultSize" && parameter.Value is 50);
            }
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task PlanBindsTypedValuesWithoutPuttingRecipeValuesOnTheProcessArguments()
    {
        string fixtureDirectory = Directory.CreateTempSubdirectory("pax-parameter-test-").FullName;
        try
        {
            string stub = Path.Combine(fixtureDirectory, "inert-recorder.ps1");
            await File.WriteAllTextAsync(stub, """
                param([string]$PromptFilter, [string[]]$UserIds, [int]$ResultSize, [switch]$Force)
                ConvertTo-Json -Compress -InputObject @{
                    Label = $PromptFilter; Names = $UserIds; Count = $ResultSize; Flag = [bool]$Force
                }
                """);
            var parameters = new PaxParameter[]
            {
                new("PromptFilter", "2026-01-02T03:04:05Z"),
                new("UserIds", new[] { "O'Brien", "two words" }),
                new("ResultSize", 50),
                new("Force", false),
            };
            var plan = PaxAdapter.CreateInvocationPlan(parameters, stub, appRoot: FindAppRoot());

            Assert.Equal(new[] { "-NoProfile", "-NoLogo", "-File" }, plan.SpawnArgv.Take(3));
            Assert.Equal(new[] { "-EnginePath", stub }, plan.SpawnArgv.Skip(4));
            Assert.DoesNotContain("2026-01-02T03:04:05Z", plan.SpawnArgv);
            Assert.Contains("2026-01-02T03:04:05Z", plan.PaxArgv);

            var result = await LaunchInertFixtureAsync(plan.SpawnArgv, Assert.IsType<string>(plan.ParameterJson));
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StandardError);
            using var capture = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("2026-01-02T03:04:05Z", capture.RootElement.GetProperty("Label").GetString());
            Assert.Equal(new[] { "O'Brien", "two words" },
                capture.RootElement.GetProperty("Names").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(50, capture.RootElement.GetProperty("Count").GetInt32());
            Assert.False(capture.RootElement.GetProperty("Flag").GetBoolean());
        }
        finally
        {
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"PromptFilter\":null}")]
    [InlineData("{\"PromptFilter\":\"one\",\"promptfilter\":\"two\"}")]
    [InlineData("{\"UserIds\":[\"one\",2]}")]
    public async Task InvalidParameterDataDoesNotInvokeTheInertTarget(string invalidData)
    {
        string fixtureDirectory = Directory.CreateTempSubdirectory("pax-parameter-test-").FullName;
        try
        {
            string stub = Path.Combine(fixtureDirectory, "inert-recorder.ps1");
            await File.WriteAllTextAsync(stub, "[Console]::WriteLine('fixture was invoked')");
            var plan = PaxAdapter.CreateInvocationPlan(Array.Empty<PaxParameter>(), stub, appRoot: FindAppRoot());

            var result = await LaunchInertFixtureAsync(plan.SpawnArgv, invalidData);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Equal("PAX Cookbook refused invalid invocation data.", result.StandardError.Trim());
        }
        finally
        {
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    [Fact]
    public void UnsupportedDuplicateOrIncorrectlyTypedParametersCannotProduceALaunchPlan()
    {
        Assert.Throws<PaxAdapter.ProjectionException>(() => PaxAdapter.CreateInvocationPlan(
            new PaxParameter[] { new("UnrecognizedOption", "fixture") }, "unused.ps1"));
        Assert.Throws<PaxAdapter.ProjectionException>(() => PaxAdapter.CreateInvocationPlan(
            new PaxParameter[] { new("ResultSize", 50), new("ResultSize", 100) }, "unused.ps1"));
        Assert.Throws<PaxAdapter.ProjectionException>(() => PaxAdapter.CreateInvocationPlan(
            new PaxParameter[] { new("ResultSize", "50") }, "unused.ps1"));
        Assert.Throws<PaxAdapter.ProjectionException>(() => PaxAdapter.CreateInvocationPlan(
            new PaxParameter[] { new("OutputPath", true) }, "unused.ps1"));
    }

    [Theory]
    [InlineData("{\"recipeSchemaVersion\":1,\"recipeSchemaVersion\":1}")]
    [InlineData("{\"recipeSchemaVersion\":1,\"query\":{\"promptFilter\":\"one\",\"PromptFilter\":\"two\"}}")]
    [InlineData("{\"recipeSchemaVersion\":1,\"fixture\":[{\"name\":\"one\",\"Name\":\"two\"}]}")]
    public void AmbiguousRequestsAndSavedRecipesAreRejected(string json)
    {
        Assert.NotNull(JsonModel.Parse(json));
        Assert.Null(JsonModel.Parse(json, rejectAmbiguousProperties: true));
        Assert.NotNull(JsonModel.Parse("{\"recipeSchemaVersion\":1,\"identity\":{\"name\":\"Inert\"}}",
            rejectAmbiguousProperties: true));

        string fixtureDirectory = Directory.CreateTempSubdirectory("pax-parameter-test-").FullName;
        try
        {
            const string recipeId = "pax-inert-fixture";
            Directory.CreateDirectory(Path.Combine(fixtureDirectory, "Recipes"));
            File.WriteAllText(Path.Combine(fixtureDirectory, "Recipes", recipeId + ".recipe.json"), json);
            var loaded = RecipeReadModel.LoadRecipeTree(fixtureDirectory, recipeId);
            Assert.Equal("malformed", loaded.Status);
            Assert.Equal("duplicate_json_property", loaded.Detail);
            Assert.Null(loaded.Recipe);
        }
        finally
        {
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    internal static string FindAppRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string appRoot = Path.Combine(directory.FullName, "app");
            if (File.Exists(Path.Combine(appRoot, "broker", "Pax", "Invoke-PaxParameters.ps1"))) { return appRoot; }
        }
        throw new DirectoryNotFoundException("Cannot locate the repository's fixed launcher.");
    }

    private static string InertPowerShell() => Environment.GetEnvironmentVariable("PAX_TRANSPORT_TEST_PWSH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");

    private static RecipeReadModel.CookProcessDependencies InertDependencies(TaskCompletionSource<int> terminal,
        Func<ProcessStartInfo, Process> createProcess) => new(
        (environment, _) =>
        {
            CookCredentialInjection.ScrubSecretEnv(environment);
            environment[CookCredentialInjection.GraphClientSecret] = SyntheticSecret;
            return CookCredentialInjection.CredentialInjectionOutcome.Injected;
        },
        (_, _) => throw new InvalidOperationException("The inert fixture never relays a device code."),
        () => throw new InvalidOperationException("The inert fixture never relays an expiry."),
        (_, _, _, _, exit, _, _, _) => terminal.TrySetResult(exit),
        createProcess,
        process =>
        {
            if (process.WaitForExit(60_000)) { return; }
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("The inert target did not receive its parameter data.");
        });

    private static (VersionInfo Version, EngineAcquisitionResult Engine, string Checkpoint) CreateCookFixture(
        string workspace, CookTriggerKind trigger, int targetExit, bool invalid)
    {
        string target = Path.Combine(workspace, "inert-target.ps1");
        File.WriteAllText(target, $$"""
            param([int]$ResultSize, [switch]$EnableParallel, [switch]$OnlyAgent365Info, [string]$Auth,
                  [string]$OutputPathAgent365Info, [string[]]$RemainingArgs)
            $receipt = @{
                Count = $ResultSize; CountType = $ResultSize.GetType().FullName; Parallel = [bool]$EnableParallel
                Catalog = [bool]$OnlyAgent365Info; Remaining = $RemainingArgs
                SawSyntheticCredential = $env:GRAPH_CLIENT_SECRET -ceq '{{SyntheticSecret}}'
            }
            [Console]::WriteLine('fixture=' + (ConvertTo-Json -InputObject $receipt -Compress))
            [Console]::WriteLine($env:GRAPH_CLIENT_SECRET)
            [Console]::Error.WriteLine($env:GRAPH_CLIENT_SECRET)
            exit {{targetExit}}
            """);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target)));
        string checkpoint = Path.Combine(workspace, "inert checkpoint.json");
        if (!invalid) { File.WriteAllText(checkpoint, "{}"); }
        WorkspaceDatabase.EnsureInitialized(workspace);
        if (trigger != CookTriggerKind.Resume)
        {
            using var connection = RecipeCookReservationTests.Open(workspace);
            RecipeCookReservationTests.SeedRecipeRow(connection, RecipeId);
            string recipes = Path.Combine(workspace, "Recipes");
            Directory.CreateDirectory(recipes);
            File.WriteAllText(Path.Combine(recipes, RecipeId + ".recipe.json"), JsonSerializer.Serialize(new
            {
                recipeId = RecipeId,
                recipeSchemaVersion = 1,
                paxAdapterVersion = "1.11.14",
                executionMode = "local-manual",
                identity = new { name = "inert fixture" },
                schedule = new { enabled = trigger == CookTriggerKind.Scheduled, recurrence = new { kind = "daily", hour = 3, minute = 0 } },
                ingredients = new { m365Usage = new { includeM365Usage = false }, entraUserData = new { includeUserInfo = false }, agent365 = new { onlyAgent365Info = true } },
                query = new { mode = "agent365Only" },
                processing = new { },
                destinations = new { agent365 = new { mode = "outputPath", path = Path.Combine(workspace, "catalog.csv") } },
                auth = new { mode = "WebLogin" },
                advanced = new { extraArguments = invalid ? "-FixtureOption 50" : "-ResultSize 50 -EnableParallel:$false" },
            }));
        }
        var version = new VersionInfo("0.0.0-test", "experimental", "1.11.14", hash, "inert-target.ps1",
            "managed", null, null, "required", null) { AppRoot = FindAppRoot() };
        return (version, new EngineAcquisitionResult
        {
            Policy = "managed", State = "acquired", IsAcquired = true, AcquisitionRequired = false,
            ManagedEnginePathPresent = true, RecordedSha256 = hash, Version = "1.11.14",
            Source = "inert test fixture", Message = "inert test fixture",
            InstallStatePath = Path.Combine(workspace, "install-state.json"), ManagedEnginePath = target,
        }, checkpoint);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> LaunchInertFixtureAsync(
        IReadOnlyList<string> arguments, string parameterData)
    {
        var startInfo = new ProcessStartInfo(InertPowerShell())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) { startInfo.ArgumentList.Add(argument); }
        CookCredentialInjection.ScrubSecretEnv(startInfo.Environment);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the inert fixture.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteLineAsync(parameterData.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
        }
    }
}