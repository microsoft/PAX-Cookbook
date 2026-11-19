using System.Text;
using Xunit;

namespace PAXCookbook.App.Tests;

public sealed class CookOutputContainmentTests
{
    private const string Secret = "synthetic-private-value";
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public CookOutputContainmentTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("")]
    [InlineData("[STDERR] ")]
    [InlineData("header: ")]
    [InlineData("exception: ")]
    public void ActualWriterSanitizesBeforeSink(string prefix)
    {
        static bool ContainsLiteral(string text) => text.Contains(Secret, StringComparison.Ordinal);
        Assert.True(ContainsLiteral("deliberate leak " + Secret));
        Assert.False(ContainsLiteral("synthetic-private-valuf"));
        using var redactor = new CookOutputRedactor(Secret);
        using var sink = new StringWriter();
        redactor.WriteLine(sink, "ordinary \u03bb " + Secret + " synthetic-private-valuf", prefix);
        Assert.False(ContainsLiteral(sink.ToString()));
        Assert.Equal(prefix + "ordinary \u03bb [REDACTED] synthetic-private-valuf" + Environment.NewLine, sink.ToString());
    }

    [Fact]
    public void MultilineKnownSecretIsRemovedFromCompleteLines()
    {
        using var redactor = new CookOutputRedactor("synthetic-first\r\nsynthetic-second\nsynthetic-third");
        using var sink = new StringWriter();
        foreach (string line in new[] { "synthetic-first", "synthetic-second", "synthetic-third" })
        {
            redactor.WriteLine(sink, line);
        }
        Assert.Equal(string.Concat(Enumerable.Repeat("[REDACTED]" + Environment.NewLine, 3)), sink.ToString());
    }

    [Fact]
    public void DevicePromptNeverReachesOutputSink()
    {
        using var redactor = new CookOutputRedactor(null);
        using var sink = new StringWriter();
        const string prompt = "To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ABCD1234 to authenticate.";
        Assert.True(TelegramNotifier.TryParseDeviceCodePrompt(prompt, out _, out _));
        redactor.WriteLine(sink, prompt);
        Assert.DoesNotContain("ABCD1234", sink.ToString());
        Assert.DoesNotContain("https://", sink.ToString());
        Assert.Contains("sign-in details omitted", sink.ToString());
    }

    [Fact]
    public void MissingSecretPreservesBenignTextAndDisposedWriterFailsClosed()
    {
        var redactor = new CookOutputRedactor(null);
        using var sink = new StringWriter();
        redactor.WriteLine(sink, "ordinary \u03bb\t\u001b[32m");
        Assert.Equal("ordinary \u03bb\t\u001b[32m" + Environment.NewLine, sink.ToString());
        redactor.Dispose();
        Assert.Throws<InvalidOperationException>(() => redactor.WriteLine(sink, Secret));
        Assert.True(redactor.CaptureFailed);
        Assert.DoesNotContain(Secret, sink.ToString());
    }

    [Fact]
    public void UnboundChildDoesNotReceiveAmbientGraphCredentials()
    {
        var child = new Dictionary<string, string?>
        {
            ["GRAPH_CLIENT_SECRET"] = "synthetic-private-value",
            ["graph_client_cert_password"] = "synthetic-password",
            ["GRAPH_CLIENT_ID"] = "synthetic-client",
            ["PATH"] = "ordinary-path",
        };
        Assert.Equal(CookCredentialInjection.CredentialInjectionOutcome.None,
            CookCredentialInjection.ApplyChildCredentialEnv(child, null));
        Assert.Single(child);
        Assert.Equal("ordinary-path", child["PATH"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuilderClearsSyntheticBytes(bool bound)
    {
        byte[] bytes = Encoding.Unicode.GetBytes("synthetic-private-value");
        var key = bound ? new ChefKeyModel.ChefKeyResolved("synthetic", ChefKeyModel.AuthAppRegSecret,
            "AppRegistrationSecret", "tenant", "client", null, true) : null;
        var result = CookCredentialInjection.BuildGraphCredentialEnv(key, bytes);
        Assert.All(bytes, value => Assert.Equal(0, value));
        Assert.Equal(bound, result.ContainsKey(CookCredentialInjection.GraphClientSecret));
        result.Clear();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InjectionOrCleanupFailureClearsBytesAndBoundsExceptions(bool cleanupFailure)
    {
        var child = new FailingEnvironment { FailRemoval = cleanupFailure };
        child.Add("GRAPH_CLIENT_SECRET", Secret);
        byte[] bytes = Encoding.Unicode.GetBytes(Secret);
        var key = new ChefKeyModel.ChefKeyResolved("synthetic", ChefKeyModel.AuthAppRegSecret,
            "AppRegistrationSecret", "tenant", "client", null, true);
        var failure = Assert.Throws<InvalidOperationException>(() => CookCredentialInjection.ApplyGraphCredentialEnv(child, key, bytes));
        Assert.All(bytes, value => Assert.Equal(0, value));
        Assert.DoesNotContain(Secret, failure.ToString());
        if (!cleanupFailure) { Assert.Empty(child); }
    }

    [Fact]
    public void AuthorizedSyntheticInjectionDoesNotRetainAmbientGraphValues()
    {
        var child = new Dictionary<string, string?> { ["GRAPH_CLIENT_CERT_PASSWORD"] = "synthetic-other" };
        byte[] bytes = Encoding.Unicode.GetBytes(Secret);
        var key = new ChefKeyModel.ChefKeyResolved("synthetic", ChefKeyModel.AuthAppRegSecret,
            "AppRegistrationSecret", "tenant", "client", null, true);
        Assert.Equal(CookCredentialInjection.CredentialInjectionOutcome.Injected,
            CookCredentialInjection.ApplyGraphCredentialEnv(child, key, bytes));
        Assert.Equal(Secret, child[CookCredentialInjection.GraphClientSecret]);
        Assert.Equal(3, child.Count);
        Assert.All(bytes, value => Assert.Equal(0, value));
        using var redactor = new CookOutputRedactor(child[CookCredentialInjection.GraphClientSecret]);
        CookCredentialInjection.ScrubSecretEnv(child);
        Assert.Empty(child);
        Assert.Equal("[REDACTED]", redactor.Sanitize(Secret));
    }

    [Fact]
    public void MissingSyntheticSecretRefusesInjection()
    {
        var child = new Dictionary<string, string?>();
        var key = new ChefKeyModel.ChefKeyResolved("synthetic", ChefKeyModel.AuthAppRegSecret,
            "AppRegistrationSecret", "tenant", "client", null, true);
        Assert.Equal(CookCredentialInjection.CredentialInjectionOutcome.SecretMissingAtSpawn,
            CookCredentialInjection.ApplyGraphCredentialEnv(child, key, null));
        Assert.Empty(child);
    }

    [Fact]
    public async Task CancellationRegistryHandlesUnstartedProcessAndReleasesHandle()
    {
        static bool IsRunning(System.Diagnostics.Process candidate)
        {
            try { return candidate.Id > 0 && !candidate.HasExited; }
            catch (InvalidOperationException) { return false; }
        }

        string cookId = "synthetic-" + Guid.NewGuid().ToString("N");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = @"C:\Program Files\PowerShell\7\pwsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            "-NoProfile", "-NoLogo", "-NonInteractive", "-Command",
            "[Console]::WriteLine('synthetic-ready'); [Console]::Out.Flush(); [void][Console]::ReadLine()",
        })
            startInfo.ArgumentList.Add(argument);
        foreach (string name in startInfo.Environment.Keys
            .Where(name => name.StartsWith("GRAPH_", StringComparison.OrdinalIgnoreCase)).ToArray())
            startInfo.Environment.Remove(name);
        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        try
        {
            Assert.True(File.Exists(startInfo.FileName));
            Assert.True(process.Start());
            using var unstartedControl = new System.Diagnostics.Process();
            bool negativeControlRejected = !IsRunning(unstartedControl);
            bool running = IsRunning(process);
            _output.WriteLine("CANCELLATION_RUNNING_CONTROL:" + System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = "Owned test process and unstarted control only",
                predicate = "Process.Id > 0 and !Process.HasExited; unstarted is false",
                positiveControl = running,
                negativeControlRejected,
                actual = running,
            }));
            Assert.True(negativeControlRejected);
            Assert.True(running, "The fixture must own a live started child before testing cancellation.");
            int childId = process.Id;
            Assert.True(childId > 0);
            string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("synthetic-ready", ready);
            Assert.False(process.HasExited);
            var unknown = CookCancellation.RequestCancel(cookId);
            Assert.Equal(CookCancellation.CancelOutcome.NotSupervised, unknown.Outcome);
            Assert.False(process.HasExited);
            _output.WriteLine("CANCELLATION_CHILD_READY:" + System.Text.Json.JsonSerializer.Serialize(new
            {
                childId,
                ready,
                hasExitedAfterUnknownCancel = process.HasExited,
                unknownOutcome = unknown.Outcome.ToString(),
            }));
            CookCancellation.Register(cookId, process, process.StartTime.ToUniversalTime());
            var outcome = CookCancellation.RequestCancel(cookId);
            Assert.Equal(CookCancellation.CancelOutcome.Requested, outcome.Outcome);
            Assert.False(outcome.KillThrew);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.HasExited);
            Assert.True(CookCancellation.IsCancelRequested(cookId));
            _output.WriteLine("CANCELLATION_CHILD_EXITED:" + System.Text.Json.JsonSerializer.Serialize(new
            {
                childId,
                outcome = outcome.Outcome.ToString(),
                outcome.KillThrew,
                process.HasExited,
                cancellationRequested = CookCancellation.IsCancelRequested(cookId),
            }));
            CookCancellation.Unregister(cookId);
            Assert.False(CookCancellation.IsCancelRequested(cookId));
            var unregistered = CookCancellation.RequestCancel(cookId);
            Assert.Equal(CookCancellation.CancelOutcome.NotSupervised, unregistered.Outcome);
            _output.WriteLine("CANCELLATION_UNREGISTERED:" + unregistered.Outcome);
        }
        finally
        {
            try
            {
                if (IsRunning(process))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
            }
            finally { CookCancellation.Unregister(cookId); }
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(17, true)]
    [InlineData(0, false)]
    public async Task ProductionSupervisorCompletesOwnedInertChild(int exitCode, bool joinSupervisor)
    {
        await using var fixture = new SupervisedChildFixture(_output);
        Task<(int Status, object Body)> supervision = fixture.Start(joinSupervisor);
        await fixture.AssertReadyAsync();
        if (joinSupervisor)
        {
            Assert.False(supervision.IsCompleted);
        }
        else
        {
            Assert.Equal(201, (await supervision.WaitAsync(TimeSpan.FromSeconds(20))).Status);
            Assert.False(fixture.Process.HasExited);
        }
        fixture.RecordReturnOrdering(supervision.IsCompleted, joinSupervisor);
        await fixture.Process.StandardInput.WriteLineAsync(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await fixture.Process.StandardInput.FlushAsync();
        var result = await supervision.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(201, result.Status);
        await fixture.WaitForFinalizerAsync();
        fixture.AssertTerminal(exitCode == 0 ? "completed" : "errored",
            exitCode == 0 ? "clean_exit" : "nonzero_exit", exitCode, result.Body);
    }

    [Fact]
    public async Task ProductionSupervisorCancelsOwnedChildThroughStopRoute()
    {
        await using var fixture = new SupervisedChildFixture(_output);
        Task<(int Status, object Body)> supervision = fixture.Start(joinSupervisor: false);
        await fixture.AssertReadyAsync();
        Assert.Equal(201, (await supervision.WaitAsync(TimeSpan.FromSeconds(20))).Status);
        var stop = RecipeReadModel.RequestCookStop(fixture.Workspace, fixture.CookId);
        Assert.Equal(202, stop.Status);
        await fixture.WaitForFinalizerAsync();
        fixture.AssertTerminal("canceled", "user_canceled", null, stop.Body);
    }

    [Fact]
    public async Task ProductionSupervisorContainsPostStartSentinelFailure()
    {
        await using var fixture = new SupervisedChildFixture(_output);
        Directory.CreateDirectory(Path.Combine(fixture.Folder, "started.json"));
        using var connection = RecipeCookReservationTests.Open(fixture.Workspace);
        using var transaction = connection.BeginTransaction();
        Task<(int Status, object Body)> supervision = fixture.Start(joinSupervisor: true);
        await fixture.AssertReadyAsync();
        Assert.False(supervision.IsCompleted);
        transaction.Commit();
        var result = await supervision.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(500, result.Status);
        fixture.AssertTerminal("interrupted", "spawn_failed", null, result.Body);
    }

    [Fact]
    public async Task ProductionSupervisorContainsWaitFailure()
    {
        await using var fixture = new SupervisedChildFixture(_output);
        Task<(int Status, object Body)> supervision = fixture.Start(joinSupervisor: true, failFirstWait: true);
        await fixture.AssertReadyAsync();
        fixture.RecordReturnOrdering(supervision.IsCompleted, joined: true);
        fixture.ReleaseWaitFailure();
        var result = await supervision.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(201, result.Status);
        await fixture.WaitForFinalizerAsync();
        fixture.AssertTerminal("errored", "nonzero_exit", -1, result.Body);
        fixture.AssertInjectedFaults(waitFailures: 1, notificationFailures: 0);
    }

    [Fact]
    public async Task ProductionSupervisorContainsTerminalNotificationFailure()
    {
        await using var fixture = new SupervisedChildFixture(_output);
        Task<(int Status, object Body)> supervision = fixture.Start(joinSupervisor: true, failNotification: true);
        await fixture.AssertReadyAsync();
        fixture.RecordReturnOrdering(supervision.IsCompleted, joined: true);
        await fixture.Process.StandardInput.WriteLineAsync("0");
        await fixture.Process.StandardInput.FlushAsync();
        var result = await supervision.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(201, result.Status);
        await fixture.WaitForFinalizerAsync();
        fixture.AssertTerminal("completed", "clean_exit", 0, result.Body);
        fixture.AssertInjectedFaults(waitFailures: 0, notificationFailures: 1);
    }

    [Theory]
    [InlineData("prefix[REDACTED]", "prefixprefix[REDACTED]")]
    [InlineData("aba", "aabababa")]
    [InlineData("REDACTED", "RREDACTEDEDACTED")]
    [InlineData("[REDACTED]tail", "[REDACTED]tailtail")]
    [InlineData("D", "DD ordinary")]
    [InlineData("\r\n", "ordinary")]
    [InlineData("\n", "ordinary")]
    [InlineData("\r", "ordinary")]
    [InlineData("a\r\naa", "aaaa")]
    [InlineData("\U0001F680[REDACTED]", "\U0001F680\U0001F680[REDACTED]")]
    public void FinalOutputContainsNoKnownLiteralAcrossReplacementAndLineBoundaries(string secret, string text)
    {
        using var redactor = new CookOutputRedactor(secret);
        AssertBoundaryContainsNoLiteral(redactor.Sanitize(text), secret);
        using var sink = new StringWriter();
        redactor.WriteLine(sink, text);
        redactor.WriteLine(sink, text, "[STDERR] ");
        AssertBoundaryContainsNoLiteral(sink.ToString(), secret);
        Assert.True(!sink.ToString().Contains('\uFFFD'), "Unexpected Unicode replacement character.");
    }

    [Theory]
    [InlineData("aba", "ab", "ordinary", "a")]
    [InlineData("prefix[REDACTED]", "prefix", "ordinary", "[REDACTED]")]
    [InlineData("\U0001F680tail", "\U0001F680", "ordinary", "tail")]
    public void SanitizedFramesRemainSafeAfterArbitraryIntermediateDrops(string secret, string first, string middle, string last)
    {
        using var redactor = new CookOutputRedactor(secret);
        string[] frames = [redactor.Sanitize(first), redactor.Sanitize(middle), redactor.Sanitize(last)];
        for (int mask = 0; mask < (1 << frames.Length); mask++)
        {
            string delivered = string.Concat(frames.Where((frame, index) => (mask & (1 << index)) != 0));
            AssertBoundaryContainsNoLiteral(delivered, secret);
        }
    }

    [Fact]
    public void RuntimeIdentityBindsLoadedAssembliesAndCurrentRedactionSources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var assemblies = new[] { typeof(CookOutputRedactor).Assembly, typeof(CookOutputContainmentTests).Assembly }
            .Select(assembly => new
            {
                path = assembly.Location,
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                mvid = assembly.ManifestModule.ModuleVersionId,
            }).ToArray();
        var sources = new[] { "src/PAXCookbook.App/CookOutputRedactor.cs", "tests/PAXCookbook.App.Tests/CookOutputContainmentTests.cs",
            "src/PAXCookbook.App/RecipeReadModel.CookSupervisor.cs", "src/PAXCookbook.App/CookCredentialInjection.cs" }
            .Select(relative => new
            {
                path = relative,
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(directory!.FullName, relative)))),
            }).ToArray();
        _output.WriteLine("BOUNDARY_IDENTITY:" + System.Text.Json.JsonSerializer.Serialize(new { assemblies, sources }));
    }

    private static void AssertBoundaryContainsNoLiteral(string actual, string secret)
    {
        string[] literals = new[] { secret }.Concat(secret.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string literal in literals)
        {
            bool ContainsLiteral(string candidate) => candidate.Contains(literal, StringComparison.Ordinal);
            Assert.True(ContainsLiteral(literal), "Positive literal control failed.");
            Assert.False(ContainsLiteral(string.Empty), "Negative literal control failed.");
            Assert.False(ContainsLiteral(actual), "Known literal present in complete decoded output.");
        }
    }

    private sealed class SupervisedChildFixture : IAsyncDisposable
    {
        private const string DeviceToken = "ABCD1234";
        private const string DeviceUrl = "https://microsoft.com/devicelogin";
        private const string ChildProgram =
            "[Console]::WriteLine('stdout ' + $env:GRAPH_CLIENT_SECRET); " +
            "[Console]::Error.WriteLine('stderr ' + $env:GRAPH_CLIENT_SECRET); " +
            "[Console]::WriteLine('To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ' + $env:PAX_TEST_DEVICE_CODE + ' to authenticate.'); " +
            "[Console]::WriteLine('To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ' + $env:PAX_TEST_DEVICE_CODE + ' to authenticate.'); " +
            "[Console]::WriteLine('device code has expired'); " +
            "[Console]::WriteLine('synthetic-ready'); [Console]::Out.Flush(); exit ([int][Console]::ReadLine())";
        private readonly Xunit.Abstractions.ITestOutputHelper _output;
        private readonly byte[] _secretBytes = Encoding.Unicode.GetBytes(Secret);
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stderrObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _relayCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _expiryCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _terminalObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowWaitFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<(int Status, object Body)>? _supervision;
        private Thread? _finalizerThread;
        private object? _notification;
        private int _injections;
        private int _factories;
        private int _notifications;
        private int _waitCalls;
        private int _waitFailures;
        private int _notificationFailures;
        private int _observedStarts;
        private int _deviceRelays;
        private int _expiryRelays;
        private int _rawStdoutSecret;
        private int _rawStderrSecret;
        private int _rawDevicePrompts;
        private bool _relayArgumentsMatch;
        private int _pid;
        internal string Workspace { get; }
        internal string CookId { get; } = Guid.NewGuid().ToString("D");
        internal string Folder { get; }
        internal System.Diagnostics.Process Process { get; }
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal SupervisedChildFixture(Xunit.Abstractions.ITestOutputHelper output)
        {
            _output = output;
            string? root = Environment.GetEnvironmentVariable("PAX_LIFECYCLE_EVIDENCE_ROOT");
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
                !Directory.Exists(root) || !Path.GetFullPath(root).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Explicit private OS-temp evidence root required.");
            Workspace = Path.Combine(root, "fixture-" + CookId);
            Folder = Path.Combine(Workspace, "Cooks", RecipeCookReservationTests.RecipeA, CookId);
            Directory.CreateDirectory(Folder);
            WorkspaceDatabase.EnsureInitialized(Workspace);
            using (var connection = RecipeCookReservationTests.Open(Workspace))
                RecipeCookReservationTests.SeedRecipeRow(connection, RecipeCookReservationTests.RecipeA);
            Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(Workspace, CookId, RecipeCookReservationTests.RecipeA).Reserved);
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = @"C:\Program Files\PowerShell\7\pwsh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Folder,
            };
            Assert.True(Folder.Length < 260, "Evidence root is too deep for a Windows child working directory.");
            foreach (string argument in new[] { "-NoProfile", "-NoLogo", "-NonInteractive", "-Command", ChildProgram })
                startInfo.ArgumentList.Add(argument);
            CookCredentialInjection.ScrubSecretEnv(startInfo.Environment);
            startInfo.Environment["PAX_TEST_DEVICE_CODE"] = DeviceToken;
            Process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
            Process.OutputDataReceived += (_, args) =>
            {
                if (ContainsKnownLiteral(args.Data, Secret))
                    Interlocked.Increment(ref _rawStdoutSecret);
                if (ContainsKnownLiteral(args.Data, DeviceToken))
                    Interlocked.Increment(ref _rawDevicePrompts);
                if (args.Data == "synthetic-ready") { Ready.TrySetResult(); }
            };
            Process.ErrorDataReceived += (_, args) =>
            {
                if (ContainsKnownLiteral(args.Data, Secret))
                {
                    Interlocked.Increment(ref _rawStderrSecret);
                    _stderrObserved.TrySetResult();
                }
            };
            Process.Exited += (_, _) => _exited.TrySetResult();
        }

        internal Task<(int Status, object Body)> Start(bool joinSupervisor, bool failFirstWait = false, bool failNotification = false)
        {
            var dependencies = new RecipeReadModel.CookProcessDependencies(
                (environment, _) =>
                {
                    Interlocked.Increment(ref _injections);
                    var key = new ChefKeyModel.ChefKeyResolved("synthetic", ChefKeyModel.AuthAppRegSecret,
                        "AppRegistrationSecret", "tenant", "client", null, true);
                    return CookCredentialInjection.ApplyGraphCredentialEnv(environment, key, _secretBytes);
                },
                (url, code) =>
                {
                    Interlocked.Increment(ref _deviceRelays);
                    _relayArgumentsMatch = url == DeviceUrl && code == DeviceToken;
                    _relayCompleted.TrySetResult();
                },
                () =>
                {
                    Interlocked.Increment(ref _expiryRelays);
                    _expiryCompleted.TrySetResult();
                },
                (workspace, folder, cookId, status, exitCode, duration, errorClass, closure) =>
                {
                    Interlocked.Increment(ref _notifications);
                    _notification = new { workspace, folder, cookId, status, exitCode, duration, errorClass, closure };
                    _finalizerThread = Thread.CurrentThread;
                    _terminalObserved.TrySetResult();
                    if (failNotification)
                    {
                        Interlocked.Increment(ref _notificationFailures);
                        throw new InvalidOperationException("Synthetic terminal notification failure.");
                    }
                },
                startInfo =>
                {
                    Interlocked.Increment(ref _factories);
                    Assert.Same(Process.StartInfo, startInfo);
                    return Process;
                },
                process =>
                {
                    int waitCall = Interlocked.Increment(ref _waitCalls);
                    Assert.Same(Process, process);
                    if (failFirstWait && waitCall == 1)
                    {
                        _allowWaitFailure.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                        Assert.True(IsRunning(process));
                        Interlocked.Increment(ref _waitFailures);
                        throw new InvalidOperationException("Synthetic first wait failure.");
                    }
                    process.WaitForExit();
                });
            _supervision = Task.Run(() => RecipeReadModel.TestSeamSuperviseInertChild(
                Workspace, CookId, RecipeCookReservationTests.RecipeA, Folder, Process.StartInfo, joinSupervisor, dependencies));
            return _supervision;
        }

        internal void ReleaseWaitFailure() => _allowWaitFailure.TrySetResult();

        internal void AssertInjectedFaults(int waitFailures, int notificationFailures)
        {
            bool Matches(int waits, int failedWaits, int failedNotifications) =>
                waits == 1 && failedWaits == waitFailures && failedNotifications == notificationFailures;
            bool positive = Matches(1, waitFailures, notificationFailures);
            bool negative = !Matches(1, waitFailures + 1, notificationFailures);
            bool actual = Matches(_waitCalls, _waitFailures, _notificationFailures);
            _output.WriteLine("LIFECYCLE_FAULTS:" + System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = "Mandatory per-call wait and terminal-notification delegates for this owned fixture",
                predicate = "One initial wait call and exactly the requested injected fault counts",
                positiveControl = positive, negativeControlRejected = negative, actual,
                waitCalls = _waitCalls, waitFailures = _waitFailures, notificationFailures = _notificationFailures,
                waitFailureReleasedAfterRunningObservation = _allowWaitFailure.Task.IsCompletedSuccessfully && _observedStarts == 1,
            }));
            Assert.True(positive && negative && actual);
        }

        private static bool IsRunning(System.Diagnostics.Process process)
        {
            try { return process.Id > 0 && !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }

        internal async Task AssertReadyAsync()
        {
            await Task.WhenAll(Ready.Task, _stderrObserved.Task, _relayCompleted.Task, _expiryCompleted.Task)
                .WaitAsync(TimeSpan.FromSeconds(20));
            using var unstarted = new System.Diagnostics.Process();
            bool positive = IsRunning(Process);
            bool negative = !IsRunning(unstarted);
            _output.WriteLine("LIFECYCLE_RUNNING:" + System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = "Owned fixture process and unstarted process control",
                predicate = "Id > 0 and !HasExited; unstarted is false",
                positiveControl = positive, negativeControlRejected = negative, actual = positive,
            }));
            Assert.True(positive);
            Assert.True(negative);
            _pid = Process.Id;
            _observedStarts++;
            Process.StartInfo.Environment.Remove("PAX_TEST_DEVICE_CODE");
        }

        internal void RecordReturnOrdering(bool returned, bool joined)
        {
            bool Matches(bool candidate) => candidate == !joined;
            bool positive = Matches(!joined);
            bool negative = !Matches(joined);
            bool actual = Matches(returned);
            _output.WriteLine("LIFECYCLE_JOIN:" + System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = "Supervision task while owned child is blocked on stdin before release",
                predicate = "Task completed iff joinSupervisor is false",
                positiveControl = positive, negativeControlRejected = negative, actual, joined, returned,
                liveOwnedProcess = IsRunning(Process),
            }));
            Assert.True(positive && negative && actual);
            Assert.True(IsRunning(Process));
        }

        internal async Task WaitForFinalizerAsync()
        {
            await _terminalObserved.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Thread finalizer = Assert.IsType<Thread>(_finalizerThread);
            Assert.True(await Task.Run(() => finalizer.Join(TimeSpan.FromSeconds(20))));
        }

        internal void AssertTerminal(string status, string closure, int? exitCode, object response)
        {
            using var connection = RecipeCookReservationTests.Open(Workspace);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM cooks WHERE cook_id = $cookId";
            command.Parameters.AddWithValue("$cookId", CookId);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            var row = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName,
                ordinal => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal));
            bool MatchesStatus(string candidate) => string.Equals(candidate, status, StringComparison.Ordinal);
            bool positive = MatchesStatus(status);
            bool negative = !MatchesStatus("running");
            bool actual = MatchesStatus((string)row["status"]!);
            _output.WriteLine("LIFECYCLE_ROW:" + System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = "Exact reserved fixture cooks row", predicate = "Ordinal status equality to expected terminal status",
                positiveControl = positive, negativeControlRejected = negative, actual, row,
            }));
            Assert.True(positive && negative && actual);
            Assert.Equal(closure, row["closure_reason"]);
            if (status == "canceled") { Assert.NotEqual(0L, Assert.IsType<long>(row["exit_code"])); }
            else { Assert.Equal(exitCode.HasValue ? (object)(long)exitCode.Value : null, row["exit_code"]); }
            Assert.Equal((long)_pid, row["pid"]);
            Assert.NotNull(row["started_at"]);
            Assert.NotNull(row["finished_at"]);
            Assert.False(reader.Read());
            bool spawnFailed = status == "interrupted";
            Assert.Equal(spawnFailed ? "spawn_failed" : status == "errored" ? "nonzero_exit" : null, row["error_class"]);
            Assert.Equal(spawnFailed ? CookOutputRedactor.SpawnFailureMessage : null, row["error_message"]);
            var sentinelPaths = new[] { "started.json", "finished.json", "interrupted.json" }
                .Select(name => Path.Combine(Folder, name)).ToArray();
            bool filePositive = File.Exists(Path.Combine(Folder, "cook.log"));
            bool fileNegative = !File.Exists(Path.Combine(Folder, "negative-control.missing"));
            var sentinels = sentinelPaths.Select(path => new { path, exists = File.Exists(path) }).ToArray();
            _output.WriteLine("LIFECYCLE_SENTINELS:" + System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = sentinelPaths, predicate = "File.Exists on the exact sentinel path",
                positiveControl = filePositive, negativeControlRejected = fileNegative, results = sentinels,
            }));
            Assert.True(filePositive && fileNegative);
            Assert.Equal(!spawnFailed, sentinels[0].exists);
            Assert.Equal(!spawnFailed, sentinels[1].exists);
            Assert.Equal(spawnFailed, sentinels[2].exists);
            if (spawnFailed)
            {
                Assert.True(Directory.Exists(sentinelPaths[0]));
                using var interrupted = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sentinels[2].path));
                Assert.Equal("spawn_failed: " + CookOutputRedactor.SpawnFailureMessage,
                    interrupted.RootElement.GetProperty("reason").GetString());
                using var body = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(response));
                Assert.Equal("cook_spawn_failed", body.RootElement.GetProperty("error").GetString());
                Assert.Equal(CookOutputRedactor.SpawnFailureMessage, body.RootElement.GetProperty("detail").GetString());
            }
            else
            {
                using var started = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sentinels[0].path));
                Assert.Equal(_pid, started.RootElement.GetProperty("pid").GetInt32());
                Assert.Equal(CookId, started.RootElement.GetProperty("cookId").GetString());
                using var finished = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sentinels[1].path));
                Assert.Equal((long)row["exit_code"]!, finished.RootElement.GetProperty("exitCode").GetInt64());
                using var notify = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(_notification));
                Assert.Equal(status, notify.RootElement.GetProperty("status").GetString());
                Assert.Equal(closure, notify.RootElement.GetProperty("closure").GetString());
            }
            string log = File.ReadAllText(Path.Combine(Folder, "cook.log"));
            Assert.Contains("stdout [REDACTED]", log);
            Assert.Contains("[STDERR] stderr [REDACTED]", log);
            Assert.Contains("sign-in details omitted", log);
            var sinks = Directory.GetFiles(Folder, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, path => path.EndsWith(".log", StringComparison.Ordinal)
                    ? File.ReadAllText(path) : DecodeJson(File.ReadAllText(path)));
            foreach (var field in row)
            {
                string value = Convert.ToString(field.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                sinks.Add("history:cooks:" + CookId + ":" + field.Key,
                    field.Key.EndsWith("_json", StringComparison.Ordinal) && value.Length > 0 ? DecodeJson(value) : value);
            }
            sinks.Add("response", DecodeJson(System.Text.Json.JsonSerializer.Serialize(response)));
            sinks.Add("terminal-notification-metadata", DecodeJson(System.Text.Json.JsonSerializer.Serialize(_notification)));
            foreach (string literal in new[] { Secret, DeviceToken })
            {
                bool ContainsLiteral(string candidate) => ContainsKnownLiteral(candidate, literal);
                bool leakControl = ContainsLiteral("deliberate " + literal);
                bool benignControl = !ContainsLiteral("ordinary benign text");
                var results = sinks.Select(sink => new { sink = sink.Key, containsLiteral = ContainsLiteral(sink.Value) }).ToArray();
                _output.WriteLine("LIFECYCLE_SINKS:" + System.Text.Json.JsonSerializer.Serialize(new
                {
                    universe = sinks.Keys, predicate = "Ordinal known synthetic literal in decoded log/JSON/history/response/terminal metadata",
                    literalKind = literal == Secret ? "synthetic-client-secret" : "synthetic-device-token",
                    positiveControl = leakControl, negativeControlRejected = benignControl, results,
                }));
                Assert.True(leakControl && benignControl);
                Assert.All(results, result => Assert.False(result.containsLiteral));
            }
            Assert.All(_secretBytes, value => Assert.Equal(0, value));
            Assert.DoesNotContain(Process.StartInfo.Environment.Keys, name => name.StartsWith("GRAPH_", StringComparison.OrdinalIgnoreCase));
            Assert.False(Process.StartInfo.Environment.ContainsKey("PAX_TEST_DEVICE_CODE"));
            Assert.True(_exited.Task.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => Process.Id);
            using var releasedLog = new FileStream(Path.Combine(Folder, "cook.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            AssertRegistryReleased();
            Assert.False(CookCancellation.IsCancelRequested(CookId));
            bool CountsMatch(int starts, int factories, int injections, int relays, int expiries, int notifications) =>
                starts == 1 && factories == 1 && injections == 1 && relays == 1 && expiries == 1 && notifications == (spawnFailed ? 0 : 1);
            bool countsPositive = CountsMatch(1, 1, 1, 1, 1, spawnFailed ? 0 : 1);
            bool countsNegative = !CountsMatch(0, 1, 1, 1, 1, spawnFailed ? 0 : 1);
            bool countsActual = CountsMatch(_observedStarts, _factories, _injections, _deviceRelays, _expiryRelays, _notifications);
            _output.WriteLine("LIFECYCLE_COUNTS:" + System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = "Owned process instance, mandatory recording delegates and actual stdout/stderr event observations",
                predicate = "One observed live process/factory/injection/device relay/expiry; one terminal notification unless spawn failed",
                positiveControl = countsPositive, negativeControlRejected = countsNegative, actual = countsActual,
                observedStarts = _observedStarts, factories = _factories, injections = _injections, notifications = _notifications,
                deviceRelays = _deviceRelays, expiryRelays = _expiryRelays, relayArgumentsMatch = _relayArgumentsMatch,
                rawStdoutSecretFrames = _rawStdoutSecret, rawStderrSecretFrames = _rawStderrSecret, rawDevicePrompts = _rawDevicePrompts,
                parentChildEnvironmentScrubbed = true, syntheticBytesCleared = true,
                ownedPid = _pid, processReleased = true, exclusiveLogOpen = releasedLog.CanRead,
            }));
            Assert.True(countsPositive && countsNegative && countsActual);
            Assert.True(_relayArgumentsMatch);
            Assert.Equal(1, _rawStdoutSecret);
            Assert.Equal(1, _rawStderrSecret);
            Assert.Equal(2, _rawDevicePrompts);
        }

        private void AssertRegistryReleased()
        {
            bool IsAbsent(string cookId) => CookCancellation.RequestCancel(cookId).Outcome == CookCancellation.CancelOutcome.NotSupervised;
            string controlId = Guid.NewGuid().ToString("D");
            using var control = new System.Diagnostics.Process();
            bool positive = IsAbsent(controlId);
            CookCancellation.Register(controlId, control, DateTime.UtcNow);
            try
            {
                bool negative = !IsAbsent(controlId);
                bool actual = IsAbsent(CookId);
                _output.WriteLine("LIFECYCLE_REGISTRY:" + System.Text.Json.JsonSerializer.Serialize(new
                {
                    universe = new[] { CookId, controlId }, predicate = "RequestCancel returns NotSupervised",
                    positiveControl = positive, negativeControlRejected = negative, actual,
                }));
                Assert.True(positive && negative && actual);
            }
            finally { CookCancellation.Unregister(controlId); }
        }

        private static bool ContainsKnownLiteral(string? candidate, string literal) =>
            candidate?.Contains(literal, StringComparison.Ordinal) == true;

        private static string DecodeJson(string json)
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            static IEnumerable<string> Values(System.Text.Json.JsonElement element) => element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Values(property.Value)),
                System.Text.Json.JsonValueKind.Array => element.EnumerateArray().SelectMany(Values),
                System.Text.Json.JsonValueKind.String => new[] { element.GetString()! },
                _ => new[] { element.GetRawText() },
            };
            return string.Join("\n", Values(document.RootElement));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (IsRunning(Process))
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                }
                if (_supervision is not null)
                    await _supervision.WaitAsync(TimeSpan.FromSeconds(20));
                if (_finalizerThread is not null)
                    Assert.True(await Task.Run(() => _finalizerThread.Join(TimeSpan.FromSeconds(20))));
            }
            finally
            {
                Process.Dispose();
                CookCancellation.Unregister(CookId);
                CookCredentialInjection.ScrubSecretEnv(Process.StartInfo.Environment);
                Process.StartInfo.Environment.Remove("PAX_TEST_DEVICE_CODE");
                Array.Clear(_secretBytes);
            }
        }
    }

    private sealed class FailingEnvironment : Dictionary<string, string?>, IDictionary<string, string?>
    {
        internal bool FailRemoval { get; init; }
        string? IDictionary<string, string?>.this[string name]
        {
            get => base[name];
            set { base[name] = value; throw new InvalidOperationException(Secret); }
        }
        bool IDictionary<string, string?>.Remove(string name)
        {
            if (FailRemoval) { throw new InvalidOperationException(Secret); }
            return Remove(name);
        }
    }
}