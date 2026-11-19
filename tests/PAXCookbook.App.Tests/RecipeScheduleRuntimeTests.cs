using System.Security.Cryptography;
using System.Text.Json;
using PAXCookbook.App;
using Xunit;
using Xunit.Abstractions;

namespace PAXCookbook.App.Tests;

public sealed class RecipeScheduleRuntimeTests(ITestOutputHelper evidence)
{
    private static readonly string[] ExpectedUnregister = ["unregister|" + MutationFixture.RecipeId + "|" + MutationFixture.TaskId];

    [Fact]
    public void OrdinarySaveUsesAuthoritativeScheduleForExplicitIntent()
    {
        using var fixture = new MutationFixture("unregistered");
        var before = fixture.ReadState();
        var preserved = fixture.SaveWithScheduleIntent("preserve", "Edited inert Recipe");
        var afterPreserve = fixture.ReadState();
        Assert.Equal(200, preserved.Status);
        Assert.Empty(fixture.Attempts());
        Assert.Equal(before.SkipMarker, afterPreserve.SkipMarker);
        Assert.Equal(before.Trash, afterPreserve.Trash);
        Assert.NotEqual(before.Index, afterPreserve.Index);
        using var oldRecipe = JsonDocument.Parse(Convert.FromBase64String(before.Recipe!));
        using var edited = JsonDocument.Parse(Convert.FromBase64String(afterPreserve.Recipe!));
        Assert.Equal(oldRecipe.RootElement.GetProperty("schedule").GetRawText(), edited.RootElement.GetProperty("schedule").GetRawText());
        Assert.Equal("Edited inert Recipe", edited.RootElement.GetProperty("identity").GetProperty("name").GetString());
        Assert.False(edited.RootElement.TryGetProperty("scheduleIntent", out _));
        using var index = JsonDocument.Parse(afterPreserve.Index);
        Assert.Equal("Edited inert Recipe", index.RootElement.GetProperty("name").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(afterPreserve.Recipe!))).ToLowerInvariant(),
            index.RootElement.GetProperty("file_hash").GetString());

        var disabled = fixture.SaveWithScheduleIntent("disable", "Edited inert Recipe");
        var afterDisable = fixture.ReadState();
        Assert.Equal(200, disabled.Status);
        Assert.Equal(ExpectedUnregister, fixture.Attempts());
        AssertCommittedMutation(afterPreserve, afterDisable, delete: false, markerCleared: true, expectedIndexName: "Edited inert Recipe");
        using var unscheduled = JsonDocument.Parse(Convert.FromBase64String(afterDisable.Recipe!));
        Assert.False(unscheduled.RootElement.TryGetProperty("schedule", out _));
        Record("ordinary-put-schedule-intent", [
            new("preserve", preserved.Status, [], before, afterPreserve, JsonSerializer.SerializeToElement(preserved.Body)),
            new("disable", disabled.Status, fixture.Attempts(), afterPreserve, afterDisable, JsonSerializer.SerializeToElement(disabled.Body)),
        ]);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(true, false)]
    [InlineData("Preserve", false)]
    [InlineData("preserve", true)]
    [InlineData("disable", true)]
    public void OrdinarySaveRejectsInvalidOrConflictingScheduleIntent(object? intent, bool includeSchedule)
    {
        using var fixture = new MutationFixture("unregistered");
        var before = fixture.ReadState();
        var result = fixture.SaveWithScheduleIntent(intent, "Edited inert Recipe", includeSchedule);
        var after = fixture.ReadState();
        Assert.Equal(400, result.Status);
        var body = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("invalid_schedule_intent", body.GetProperty("error").GetString());
        Assert.Empty(fixture.Attempts());
        Assert.Equal(before, after);
        Record("invalid-schedule-intent", [new($"intent={JsonSerializer.Serialize(intent)}; schedule={includeSchedule}", result.Status,
            fixture.Attempts(), before, after, body)]);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("persist failed")]
    [InlineData("marker warning")]
    public void OrdinaryExplicitDisablePreservesV4Outcomes(string outcome)
    {
        using var fixture = new MutationFixture(outcome == "refused" ? "refused" : "unregistered");
        if (outcome == "persist failed") fixture.RefuseIndexWrite();
        var before = fixture.ReadState();
        using var heldMarker = outcome == "marker warning" ? fixture.HoldSkipMarker() : null;
        var result = fixture.SaveWithScheduleIntent("disable", "Edited inert Recipe");
        var after = fixture.ReadState();
        var body = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal(ExpectedUnregister, fixture.Attempts());
        switch (outcome)
        {
            case "refused":
                AssertRegistrarRefusal(result);
                Assert.Equal(before, after);
                break;
            case "persist failed":
                Assert.Equal(500, result.Status);
                Assert.Equal("persist_failed", body.GetProperty("error").GetString());
                Assert.True(body.GetProperty("scheduleUnregistered").GetBoolean());
                Assert.Equal(before, after);
                break;
            default:
                Assert.Equal(500, result.Status);
                Assert.Equal("schedule_skip_marker_clear_failed", body.GetProperty("error").GetString());
                Assert.True(body.GetProperty("recipeSaved").GetBoolean());
                Assert.True(body.GetProperty("scheduleUnregistered").GetBoolean());
                AssertCommittedMutation(before, after, delete: false, markerCleared: false, expectedIndexName: "Edited inert Recipe");
                using (var saved = JsonDocument.Parse(Convert.FromBase64String(after.Recipe!)))
                {
                    Assert.False(saved.RootElement.TryGetProperty("schedule", out _));
                }
                break;
        }
        Record("explicit-disable", [new(outcome, result.Status, fixture.Attempts(), before, after, body)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryMutationControlsCompareEverySavedSurface(bool delete)
    {
        var controls = new List<Observation>();
        foreach (string outcome in new[] { "refused", "unregistered", "no_task_present" })
        {
            using var fixture = new MutationFixture(outcome, storedEnabled: !delete || outcome != "no_task_present");
            var before = fixture.ReadState();
            var result = delete ? fixture.Delete() : fixture.Save(enabled: false, requestedTaskId: MutationFixture.RecipeId,
                omitSchedule: outcome == "no_task_present");
            var after = fixture.ReadState();
            string[] attempts = fixture.Attempts();
            Assert.Equal(ExpectedUnregister, attempts);
            if (outcome == "refused")
            {
                AssertRegistrarRefusal(result);
                Assert.Equal(before, after);
            }
            else
            {
                Assert.Equal(200, result.Status);
                AssertCommittedMutation(before, after, delete, markerCleared: true);
                if (!delete)
                {
                    using var saved = JsonDocument.Parse(Convert.FromBase64String(after.Recipe!));
                    bool hasSchedule = saved.RootElement.TryGetProperty("schedule", out var schedule);
                    Assert.Equal(outcome != "no_task_present", hasSchedule);
                    if (hasSchedule) Assert.False(schedule.GetProperty("enabled").GetBoolean());
                }
            }
            controls.Add(new(outcome, result.Status, attempts, before, after));
        }
        Assert.Equal(new[] { true, false, false }, controls.Select(control => control.Before == control.After));
        Record(delete ? "delete" : "save", controls);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    [InlineData("schedule-delete")]
    public void ShippedLookupRefusalPreservesEverySavedSurface(string operation)
    {
        var controls = new List<Observation>();
        foreach (bool lookupFault in new[] { true, false })
        {
            using var fixture = new MutationFixture();
            fixture.UseShippedUnregister(lookupFault);
            var before = fixture.ReadState();
            var result = operation switch
            {
                "save" => fixture.Save(enabled: false, requestedTaskId: MutationFixture.RecipeId),
                "delete" => fixture.Delete(),
                "schedule-delete" => fixture.DeleteSchedule(),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            var after = fixture.ReadState();
            var attempts = fixture.Attempts();
            Assert.Equal(ExpectedUnregister, attempts);
            Assert.Equal(new[] { "lookup" }, fixture.SchedulerCalls());
            if (lookupFault)
            {
                AssertRegistrarRefusal(result);
                Assert.Equal(before, after);
            }
            else
            {
                Assert.Equal(200, result.Status);
                AssertCommittedMutation(before, after, delete: operation == "delete", markerCleared: true,
                    expectedIndexName: operation == "schedule-delete" ? "seeded" : "Inert Recipe");
            }
            controls.Add(new(lookupFault ? "shipped lookup refusal" : "shipped no task present", result.Status, attempts,
                before, after, JsonSerializer.SerializeToElement(result.Body), fixture.SchedulerCalls()));
        }
        Assert.Equal(new[] { true, false }, controls.Select(control => control.Before == control.After));
        Record(operation, controls);
    }

    [Theory]
    [InlineData("invalid-save", 400)]
    [InlineData("retain-schedule", 200)]
    [InlineData("delete-without-binding", 200)]
    [InlineData("delete-missing-live-row", 404)]
    [InlineData("delete-malformed-recipe", 422)]
    public void OrdinaryMutationsWithoutAValidRemovalDoNotCallUnregister(string scenario, int status)
    {
        using var fixture = new MutationFixture(storedBinding: scenario != "delete-without-binding");
        if (scenario == "delete-missing-live-row") fixture.PrepareAlreadyDeletedRow();
        if (scenario == "delete-malformed-recipe") fixture.MakeStoredRecipeMalformed();
        var before = fixture.ReadState();
        var result = scenario switch
        {
            "invalid-save" => fixture.SaveInvalid(),
            "retain-schedule" => fixture.Save(enabled: true),
            _ => fixture.Delete(),
        };
        var after = fixture.ReadState();
        Assert.Equal(status, result.Status);
        Assert.Empty(fixture.Attempts());
        if (status != 200)
        {
            Assert.Equal(before, after);
        }
        else
        {
            AssertCommittedMutation(before, after, delete: scenario == "delete-without-binding", markerCleared: false);
            if (scenario == "retain-schedule")
            {
                using var recipe = JsonDocument.Parse(Convert.FromBase64String(after.Recipe!));
                Assert.True(recipe.RootElement.GetProperty("schedule").GetProperty("enabled").GetBoolean());
            }
        }
        Record(scenario, [new(scenario, result.Status, fixture.Attempts(), before, after)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryMutationReportsPersistenceFailureAfterSuccessfulUnregister(bool delete)
    {
        using var fixture = new MutationFixture("unregistered");
        fixture.RefuseIndexWrite();
        var before = fixture.ReadState();
        var result = delete ? fixture.Delete() : fixture.Save(enabled: false);
        var after = fixture.ReadState();
        Assert.Equal(500, result.Status);
        var body = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal(delete ? "delete_failed" : "persist_failed", body.GetProperty("error").GetString());
        Assert.True(body.GetProperty("scheduleUnregistered").GetBoolean());
        Assert.Equal(ExpectedUnregister, fixture.Attempts());
        Assert.Equal(before, after);
        Record(delete ? "delete" : "save", [new("post-unregister persistence failure", result.Status, fixture.Attempts(), before, after)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryMutationReportsMarkerFailureAfterSuccessfulCommit(bool delete)
    {
        using var fixture = new MutationFixture("unregistered");
        var before = fixture.ReadState();
        using var heldMarker = fixture.HoldSkipMarker();
        var result = delete ? fixture.Delete() : fixture.Save(enabled: false);
        var after = fixture.ReadState();
        Assert.Equal(500, result.Status);
        var body = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("schedule_skip_marker_clear_failed", body.GetProperty("error").GetString());
        Assert.True(body.GetProperty(delete ? "recipeDeleted" : "recipeSaved").GetBoolean());
        Assert.True(body.GetProperty("scheduleUnregistered").GetBoolean());
        Assert.Equal(ExpectedUnregister, fixture.Attempts());
        AssertCommittedMutation(before, after, delete, markerCleared: false);
        Record(delete ? "delete" : "save", [new("post-commit locked marker", result.Status, fixture.Attempts(), before, after)]);
    }

    [Fact]
    public async Task OrdinarySaveHoldsSharedMutationGateThroughUnregister()
    {
        using var fixture = new MutationFixture("paused");
        var before = fixture.ReadState();
        object gate = RecipeScheduleModel.GetMutationGate(fixture.Workspace, MutationFixture.RecipeId);
        Assert.Same(gate, RecipeScheduleModel.GetMutationGate(fixture.Workspace + Path.DirectorySeparatorChar,
            MutationFixture.RecipeId.ToLowerInvariant()));
        Assert.True(CanEnter(gate, TimeSpan.FromSeconds(10)));
        Task<(int Status, object Body)> save = Task.Run(() => fixture.Save(enabled: false, omitSchedule: true));
        var bound = DateTime.UtcNow.AddSeconds(15);
        try
        {
            while (fixture.Attempts().Length == 0 && !save.IsCompleted && DateTime.UtcNow < bound)
            {
                await Task.Delay(20);
            }
            Assert.Equal(ExpectedUnregister, fixture.Attempts());
            Assert.False(save.IsCompleted);
            Assert.False(CanEnter(gate, TimeSpan.Zero));
            Assert.Equal(before, fixture.ReadState());
        }
        finally
        {
            fixture.ReleaseRegistrar();
            await save.WaitAsync(TimeSpan.FromSeconds(15));
        }
        Assert.Equal(200, (await save).Status);
        Assert.True(CanEnter(gate, TimeSpan.FromSeconds(10)));
        var after = fixture.ReadState();
        AssertCommittedMutation(before, after, delete: false, markerCleared: true);
        Record("save", [new("commit after releasing held registrar", (await save).Status, fixture.Attempts(), before, after)]);

        static bool CanEnter(object candidate, TimeSpan wait)
        {
            if (!Monitor.TryEnter(candidate, wait)) return false;
            Monitor.Exit(candidate);
            return true;
        }
    }

    private static void AssertCommittedMutation(SavedState before, SavedState after, bool delete, bool markerCleared,
        string expectedIndexName = "Inert Recipe")
    {
        Assert.NotEqual(before.Index, after.Index);
        Assert.Equal(markerCleared ? null : before.SkipMarker, after.SkipMarker);
        using var index = JsonDocument.Parse(after.Index);
        if (delete)
        {
            Assert.Null(after.Recipe);
            Assert.NotNull(index.RootElement.GetProperty("deleted_at").GetString());
            var beforeTrash = JsonSerializer.Deserialize<Dictionary<string, string>>(before.Trash)!;
            var afterTrash = JsonSerializer.Deserialize<Dictionary<string, string>>(after.Trash)!;
            foreach (var entry in beforeTrash) Assert.Equal(entry.Value, afterTrash[entry.Key]);
            var newEntry = Assert.Single(afterTrash.Where(entry => !beforeTrash.ContainsKey(entry.Key)));
            Assert.Equal(before.Recipe, newEntry.Value);
        }
        else
        {
            Assert.NotNull(after.Recipe);
            Assert.NotEqual(before.Recipe, after.Recipe);
            Assert.Equal(before.Trash, after.Trash);
            Assert.Equal(expectedIndexName, index.RootElement.GetProperty("name").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(after.Recipe))).ToLowerInvariant(),
                index.RootElement.GetProperty("file_hash").GetString());
        }
    }

    private void Record(string operation, IReadOnlyList<Observation> controls)
    {
        evidence.WriteLine(JsonSerializer.Serialize(new
        {
            operation,
            universe = "One temporary Recipe: live file bytes/absence; every SQLite recipes row column; relative file paths and bytes under Recipes/_trash; skip-next marker bytes/absence. Inert registrar transcripts record only action and synthetic bindings.",
            predicate = "Before and After are equal across all four saved surfaces",
            controls = controls.Select(control => new
            {
                control.Name, control.Status, control.Attempts, control.Before, control.After,
                control.Body, control.SchedulerCalls,
                stateUnchanged = control.Before == control.After,
            }),
        }));
    }

    private static void AssertRegistrarRefusal((int Status, object Body) result)
    {
        Assert.Equal(502, result.Status);
        var body = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("schedule_unregistration_failed", body.GetProperty("error").GetString());
        Assert.Equal(6, body.GetProperty("registrarExitCode").GetInt32());
        Assert.Equal("unregister_failed", body.GetProperty("token").GetString());
    }

    private sealed class MutationFixture : IDisposable
    {
        public const string RecipeId = RecipeCookReservationTests.RecipeA;
        public const string TaskId = RecipeCookReservationTests.RecipeB;
        private readonly string _workspace = Directory.CreateTempSubdirectory("pax-ordinary-schedule-").FullName;
        private readonly string _recipePath;
        private readonly string _trash;
        private readonly string _marker;
        private readonly string _registrar;
        private readonly string _appRoot = RecipeParameterTransportTests.FindAppRoot();
        private static readonly string TestShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        private string _shell = TestShell;
        public string Workspace => _workspace;

        public MutationFixture(string outcome = "refused", bool storedBinding = true, bool storedEnabled = true)
        {
            WorkspaceDatabase.EnsureInitialized(_workspace);
            using (var connection = RecipeCookReservationTests.Open(_workspace))
            {
                RecipeCookReservationTests.SeedRecipeRow(connection, RecipeId);
            }
            string recipes = Directory.CreateDirectory(Path.Combine(_workspace, "Recipes")).FullName;
            _recipePath = Path.Combine(recipes, RecipeId + ".recipe.json");
            _trash = Directory.CreateDirectory(Path.Combine(recipes, "_trash")).FullName;
            File.WriteAllText(Path.Combine(_trash, "existing.json"), "{}");
            File.WriteAllText(_recipePath, RecipeJson(storedEnabled, TaskId, omitSchedule: !storedBinding));
            ScheduleSkipMarker.Write(_workspace, RecipeId, DateTimeOffset.UtcNow.AddDays(1));
            _marker = Path.Combine(_workspace, "Scheduler", "skip-markers", RecipeId + ".json");
            _registrar = Path.Combine(_workspace, "inert-registrar.ps1");
            File.WriteAllText(_registrar, """
                param([string]$Action, [string]$WorkspacePath, [string]$RecipeId, [string]$ScheduledTaskId)
                [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'attempts.txt'), "$Action|$RecipeId|$ScheduledTaskId`n")
                if ($Action -cne 'unregister') { exit 7 }
                $outcome = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'outcome.txt'))
                if ($outcome -ceq 'refused') {
                    [Console]::Error.WriteLine('[Registrar] unregister_failed: inert refusal')
                    exit 6
                }
                if ($outcome -ceq 'paused') {
                    $release = Join-Path $PSScriptRoot 'registrar-release.txt'
                    $timer = [Diagnostics.Stopwatch]::StartNew()
                    $watcher = [IO.FileSystemWatcher]::new($PSScriptRoot, 'registrar-release.txt')
                    try {
                        while (-not [IO.File]::Exists($release)) {
                            if ($timer.ElapsedMilliseconds -gt 20000) { exit 8 }
                            [void]$watcher.WaitForChanged([IO.WatcherChangeTypes]::Created, 100)
                        }
                    } finally { $watcher.Dispose() }
                }
                [Console]::Out.WriteLine('[Registrar] ' + $outcome + ': inert success')
                exit 0
                """);
            File.WriteAllText(Path.Combine(_workspace, "outcome.txt"), outcome);
        }

        private string RecipeJson(bool enabled, string taskId, bool omitSchedule) => JsonSerializer.Serialize(new
        {
            recipeId = RecipeId, recipeSchemaVersion = 1, paxAdapterVersion = "1.11.14",
            executionMode = "local-manual", identity = new { name = "Inert Recipe" },
            ingredients = new { m365Usage = new { includeM365Usage = false }, entraUserData = new { includeUserInfo = false }, agent365 = new { onlyAgent365Info = true } },
            query = new { mode = "agent365Only" }, processing = new { }, auth = new { mode = "WebLogin" },
            destinations = new { agent365 = new { mode = "outputPath", path = Path.Combine(_workspace, "catalog.csv") } },
            schedule = omitSchedule ? null : new { enabled, scheduledTaskId = taskId, recurrence = new { kind = "daily", hour = 3, minute = 0 } },
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        public void UseShippedUnregister(bool lookupFault)
        {
            _shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            string shippedScript = Path.Combine(_appRoot, "install", "Register-PAXScheduledRecipe.ps1");
            Assert.True(File.Exists(shippedScript));
            File.WriteAllText(Path.Combine(_workspace, "shipped-script.txt"), shippedScript);
            File.WriteAllText(Path.Combine(_workspace, "outcome.txt"), lookupFault ? "lookup_fault" : "absent");
            File.WriteAllText(_registrar, """
                param([string]$Action, [string]$WorkspacePath, [string]$RecipeId, [string]$ScheduledTaskId)
                $ErrorActionPreference = 'Stop'
                $PSModuleAutoloadingPreference = 'None'
                if ($Action -cne 'unregister' -or $PSVersionTable.PSVersion -lt [version]'7.2') { exit 7 }
                Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Management', 'Microsoft.PowerShell.Management.psd1'))
                $trace = [IO.Path]::Combine($PSScriptRoot, 'scheduler-calls.txt')
                $lookupFault = [IO.File]::ReadAllText([IO.Path]::Combine($PSScriptRoot, 'outcome.txt')) -ceq 'lookup_fault'
                function Get-ScheduledTask {
                    [CmdletBinding()] param()
                    [IO.File]::AppendAllText($trace, "lookup`n")
                    if ($lookupFault) { Write-Error 'Synthetic scheduler failure with the words task not found.' }
                }
                function Unregister-ScheduledTask {
                    [CmdletBinding(SupportsShouldProcess)] param([string]$TaskPath, [string]$TaskName)
                    [IO.File]::AppendAllText($trace, "removal`n")
                    throw 'No removal expected in these controls.'
                }
                foreach ($name in 'Get-ScheduledTask', 'Unregister-ScheduledTask') {
                    $resolved = Get-Command -Name $name
                    if ($resolved.CommandType -ne 'Function' -or $resolved.ScriptBlock.File -cne $PSCommandPath) { exit 7 }
                }
                [IO.File]::AppendAllText([IO.Path]::Combine($PSScriptRoot, 'attempts.txt'), "$Action|$RecipeId|$ScheduledTaskId`n")
                $shippedScript = [IO.File]::ReadAllText([IO.Path]::Combine($PSScriptRoot, 'shipped-script.txt'))
                & $shippedScript -Action unregister -WorkspacePath $WorkspacePath -RecipeId $RecipeId -ScheduledTaskId $ScheduledTaskId -TaskFolderOverride 'Inert Review'
                exit $LASTEXITCODE
                """);
        }

        public (int Status, object Body) Save(bool enabled, string requestedTaskId = TaskId, bool omitSchedule = false) => RecipeUpdateModel.Handle(
            _workspace, _appRoot, ResumeCookPreparationCharacterizationTests.Version(), RecipeId,
            JsonModel.Parse(RecipeJson(enabled, requestedTaskId, omitSchedule)), _registrar, _shell);

        public (int Status, object Body) SaveWithScheduleIntent(object? intent, string name, bool includeSchedule = false)
        {
            var body = (Dictionary<string, object?>)JsonModel.Parse(RecipeJson(enabled: false, TaskId, omitSchedule: !includeSchedule))!;
            body["scheduleIntent"] = intent;
            body["identity"] = new Dictionary<string, object?> { ["name"] = name };
            return RecipeUpdateModel.Handle(_workspace, _appRoot, ResumeCookPreparationCharacterizationTests.Version(), RecipeId,
                body, _registrar, _shell);
        }

        public (int Status, object Body) SaveInvalid() => RecipeUpdateModel.Handle(
            _workspace, _appRoot, ResumeCookPreparationCharacterizationTests.Version(), RecipeId,
            JsonModel.Parse("{}"), _registrar, TestShell);

        public (int Status, object Body) Delete() => RecipeDeleteModel.Handle(_workspace, _appRoot, RecipeId, _registrar, _shell);

        public (int Status, object Body) DeleteSchedule() => RecipeScheduleModel.DeleteScheduledTask(
            _workspace, _appRoot, RecipeId, _registrar, taskFolderOverride: null, _shell);

        public void PrepareAlreadyDeletedRow() => ExecuteSql("UPDATE recipes SET deleted_at = '2026-01-02T00:00:00Z';");

        public void MakeStoredRecipeMalformed() => File.WriteAllText(_recipePath, "{");

        public void RefuseIndexWrite() => ExecuteSql("CREATE TRIGGER refuse_recipe_write BEFORE UPDATE ON recipes BEGIN SELECT RAISE(ABORT, 'inert index failure'); END;");

        public FileStream HoldSkipMarker() => File.Open(_marker, FileMode.Open, FileAccess.Read, FileShare.Read);

        public void ReleaseRegistrar() => File.WriteAllText(Path.Combine(_workspace, "registrar-release.txt"), string.Empty);

        private void ExecuteSql(string text)
        {
            using var connection = RecipeCookReservationTests.Open(_workspace);
            using var command = connection.CreateCommand();
            command.CommandText = text;
            command.ExecuteNonQuery();
        }

        public string[] Attempts() => File.Exists(Path.Combine(_workspace, "attempts.txt"))
            ? File.ReadAllLines(Path.Combine(_workspace, "attempts.txt")) : Array.Empty<string>();

        public string[] SchedulerCalls() => File.ReadAllLines(Path.Combine(_workspace, "scheduler-calls.txt"));

        public SavedState ReadState()
        {
            using var connection = RecipeCookReservationTests.Open(_workspace);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM recipes WHERE recipe_id = $id;";
            command.Parameters.AddWithValue("$id", RecipeId);
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            var fields = new SortedDictionary<string, object?>();
            for (int ordinal = 0; ordinal < row.FieldCount; ordinal++)
            {
                fields.Add(row.GetName(ordinal), row.IsDBNull(ordinal) ? null : row.GetValue(ordinal));
            }
            string trash = JsonSerializer.Serialize(Directory.GetFiles(_trash, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToDictionary(path => Path.GetRelativePath(_trash, path), path => ReadBytes(path)));
            return new(ReadBytes(_recipePath), JsonSerializer.Serialize(fields), trash, ReadBytes(_marker));
        }

        private static string? ReadBytes(string path) => File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null;

        public void Dispose() => Directory.Delete(_workspace, recursive: true);
    }

    private sealed record SavedState(string? Recipe, string Index, string Trash, string? SkipMarker);
    private sealed record Observation(string Name, int Status, string[] Attempts, SavedState Before, SavedState After,
        JsonElement? Body = null, string[]? SchedulerCalls = null);

    [Fact]
    public void UnsupportedRegistrarLeavesSavedScheduleUnchanged()
    {
        string workspace = Directory.CreateTempSubdirectory("pax-schedule-test-").FullName;
        try
        {
            const string recipeId = RecipeCookReservationTests.RecipeA;
            WorkspaceDatabase.EnsureInitialized(workspace);
            using (var connection = RecipeCookReservationTests.Open(workspace))
            {
                RecipeCookReservationTests.SeedRecipeRow(connection, recipeId);
            }
            string recipePath = Path.Combine(Directory.CreateDirectory(Path.Combine(workspace, "Recipes")).FullName,
                recipeId + ".recipe.json");
            byte[] saved = JsonSerializer.SerializeToUtf8Bytes(new
            {
                recipeId,
                recipeSchemaVersion = 1,
                schedule = new
                {
                    enabled = true,
                    scheduledTaskId = RecipeCookReservationTests.RecipeB,
                    recurrence = new { kind = "daily", hour = 3, minute = 0 },
                },
            });
            File.WriteAllBytes(recipePath, saved);
            string appRoot = RecipeParameterTransportTests.FindAppRoot();
            string legacyPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var result = RecipeScheduleModel.DeleteScheduledTask(workspace, appRoot, recipeId,
                registrarPathOverride: null, taskFolderOverride: @"\PAX Cookbook inert fixture\", pwshOverride: legacyPowerShell);

            Assert.Equal(502, result.Status);
            JsonElement body = JsonSerializer.SerializeToElement(result.Body);
            Assert.Equal("unsupported_powershell", body.GetProperty("token").GetString());
            Assert.Equal(saved, File.ReadAllBytes(recipePath));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}