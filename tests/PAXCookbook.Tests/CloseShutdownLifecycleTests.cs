using System.Diagnostics;
using System.Text.Json;
using PAXCookbook.Broker;
using PAXCookbook.Logging;
using PAXCookbook.WebView2;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 5 AB regression coverage for the close shutdown lifecycle.
//
// Covers Brian's 13 required test points for the close-shutdown
// repair, divided into three groups:
//
//   A. PromptForChoice behaviour (UI-thread phase).
//      1. Cancel choice keeps the broker untouched.
//      2. ClosePaxCookbook choice does NOT call broker.Stop on this
//         method alone (broker stop is RunBrokerStop's job).
//      3. Dialog exception is swallowed and surfaces as Cancel with
//         FailureDetail = "close-prompt-exception".
//      4. close-prompt-open and close-prompt-result events appear in
//         the configured AppLogger.
//
//   B. RunBrokerStop behaviour (any-thread phase).
//      5. Normal stop logs broker-stop-start + broker-stop-complete
//         and surfaces ClosedWithBrokerStopped.
//      6. Broker stop failure logs broker-stop-timeout and STILL
//         surfaces ClosedWithBrokerStopped so the user is never
//         trapped.
//      7. Broker Stop() that throws is caught, logged as
//         broker-stop-failed, and still surfaces
//         ClosedWithBrokerStopped.
//      8. Dormant broker (probe says not running) returns
//         AlreadyStopped without calling broker.Stop.
//      9. No-workspace case returns AlreadyStopped without probing
//         the broker.
//     10. Configured BrokerStopTimeout flows into BrokerStopOptions.
//     11. RunBrokerStop returns within (timeout + grace) even when
//         broker.Stop honours its bound and returns Failed.
//
//   C. Source-shape guards that catch a regression in the host's
//      async shutdown orchestration:
//     12. WebView2WinFormsHost source disposes WebView2 (the
//         deadlock breaker) and runs broker stop via Task.Run with
//         the silent-close + BeginInvoke programmatic close pattern.
//     13. WebView2WinFormsHost source has the _shutdownInProgress
//         coalesce guard, has no Start-Broker / pwsh.exe references,
//         and NativeBrokerController.DisposeHostQuietly is bounded.
public class CloseShutdownLifecycleTests
{
    // -------------------------------------------------------------
    // Test fakes (named *ShutdownLifecycle* to avoid collision with
    // FakeBrokerControllerForCloseTests in NativeBrokerHostStage3kTests.cs
    // and FakeBrokerController in Phase7CloseSingleInstanceTests.cs)
    // -------------------------------------------------------------
    private sealed class ShutdownLifecycleDialog : ICloseDialogService
    {
        private readonly CloseChoice _next;
        public int Calls;
        public ShutdownLifecycleDialog(CloseChoice next) { _next = next; }
        public CloseChoice Prompt(IntPtr ownerHwnd) { Calls++; return _next; }
    }

    private sealed class ShutdownLifecycleThrowingDialog : ICloseDialogService
    {
        public Exception Ex { get; set; } = new InvalidOperationException("simulated dialog fault");
        public int Calls;
        public CloseChoice Prompt(IntPtr ownerHwnd) { Calls++; throw Ex; }
    }

    private sealed class ShutdownLifecycleBroker : IBrokerController
    {
        public BrokerStatus NextProbe { get; set; } = new BrokerStatus(true, 4321, 17654, "http://localhost:17654", "ws", "native-in-process");
        public BrokerStopResult NextStop { get; set; } = new BrokerStopResult(BrokerStopOutcome.Stopped, null);
        public Exception? StopThrows { get; set; }
        public TimeSpan StopDelay { get; set; } = TimeSpan.Zero;
        public int ProbeCalls;
        public int StopCalls;
        public BrokerStopOptions? LastStop;

        public BrokerStatus Probe(string workspaceFolderPath)
        {
            ProbeCalls++;
            return NextProbe;
        }
        public BrokerStopResult Stop(BrokerStopOptions options)
        {
            StopCalls++;
            LastStop = options;
            if (StopDelay > TimeSpan.Zero) Thread.Sleep(StopDelay);
            if (StopThrows is not null) throw StopThrows;
            return NextStop;
        }
        public BrokerStartResult Start(BrokerStartOptions options)
            => new BrokerStartResult(BrokerStartOutcome.Started, NextProbe, null);
    }

    private sealed class TempLogger : IDisposable
    {
        public string Dir { get; }
        public AppLogger Log { get; }
        public TempLogger()
        {
            Dir = Path.Combine(Path.GetTempPath(), "PAX5AB_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Log = new AppLogger(Dir);
        }
        public IReadOnlyList<JsonDocument> ReadEvents()
        {
            if (!File.Exists(Log.CurrentLogFile)) return Array.Empty<JsonDocument>();
            var lines = File.ReadAllLines(Log.CurrentLogFile);
            var docs = new List<JsonDocument>(lines.Length);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { docs.Add(JsonDocument.Parse(line)); } catch { }
            }
            return docs;
        }
        public IEnumerable<JsonDocument> Where(string evt)
            => ReadEvents().Where(d => d.RootElement.TryGetProperty("event", out var e) && e.GetString() == evt);
        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    // =============================================================
    // A. PromptForChoice behaviour
    // =============================================================

    // Coverage point 1.
    [Fact]
    public void PromptForChoice_returns_dialog_choice_and_does_not_call_broker()
    {
        var d = new ShutdownLifecycleDialog(CloseChoice.ClosePaxCookbook);
        var b = new ShutdownLifecycleBroker();
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromSeconds(2));
        var r = c.PromptForChoice(CloseTrigger.TitleBarX, IntPtr.Zero);
        Assert.Equal(CloseChoice.ClosePaxCookbook, r.Choice);
        Assert.Null(r.FailureDetail);
        Assert.Equal(1, d.Calls);
        Assert.Equal(0, b.ProbeCalls);
        Assert.Equal(0, b.StopCalls);
    }

    // Coverage point 2.
    [Fact]
    public void PromptForChoice_Cancel_returns_Cancel_without_FailureDetail()
    {
        var d = new ShutdownLifecycleDialog(CloseChoice.Cancel);
        var b = new ShutdownLifecycleBroker();
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromSeconds(2));
        var r = c.PromptForChoice(CloseTrigger.TitleBarX, IntPtr.Zero);
        Assert.Equal(CloseChoice.Cancel, r.Choice);
        Assert.Null(r.FailureDetail);
        Assert.Equal(0, b.StopCalls);
    }

    // Coverage point 3.
    [Fact]
    public void PromptForChoice_swallows_dialog_exception_returns_Cancel_with_FailureDetail()
    {
        var d = new ShutdownLifecycleThrowingDialog();
        var b = new ShutdownLifecycleBroker();
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromSeconds(2));
        var r = c.PromptForChoice(CloseTrigger.TitleBarX, IntPtr.Zero);
        Assert.Equal(CloseChoice.Cancel, r.Choice);
        Assert.Equal("close-prompt-exception", r.FailureDetail);
        Assert.Equal(0, b.StopCalls);
    }

    // Coverage point 4.
    [Fact]
    public void PromptForChoice_logs_close_prompt_open_and_close_prompt_result()
    {
        using var t = new TempLogger();
        var d = new ShutdownLifecycleDialog(CloseChoice.ClosePaxCookbook);
        var b = new ShutdownLifecycleBroker();
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromSeconds(2), t.Log);
        c.PromptForChoice(CloseTrigger.TitleBarX, IntPtr.Zero);
        var opens   = t.Where("close-prompt-open").ToList();
        var results = t.Where("close-prompt-result").ToList();
        Assert.Single(opens);
        Assert.Single(results);
        Assert.Equal("TitleBarX", opens[0].RootElement.GetProperty("fields").GetProperty("trigger").GetString());
        Assert.Equal("ClosePaxCookbook", results[0].RootElement.GetProperty("fields").GetProperty("choice").GetString());
    }

    // =============================================================
    // B. RunBrokerStop behaviour
    // =============================================================

    // Coverage point 5.
    [Fact]
    public void RunBrokerStop_normal_stop_logs_start_and_complete_and_returns_ClosedWithBrokerStopped()
    {
        using var t = new TempLogger();
        var d = new ShutdownLifecycleDialog(CloseChoice.ClosePaxCookbook);
        var b = new ShutdownLifecycleBroker
        {
            NextProbe = new BrokerStatus(true, 4321, 17654, "http://localhost:17654", "ws", "native-in-process"),
            NextStop  = new BrokerStopResult(BrokerStopOutcome.Stopped, null)
        };
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromSeconds(2), t.Log);
        var r = c.RunBrokerStop();
        Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
        Assert.Equal(BrokerStopOutcome.Stopped, r.BrokerStopOutcome);
        Assert.Equal(1, b.StopCalls);
        Assert.Single(t.Where("broker-stop-start"));
        Assert.Single(t.Where("broker-stop-complete"));
    }

    // Coverage point 6.
    [Fact]
    public void RunBrokerStop_stop_failure_logs_broker_stop_timeout_and_returns_ClosedWithBrokerStopped()
    {
        using var t = new TempLogger();
        var d = new ShutdownLifecycleDialog(CloseChoice.ClosePaxCookbook);
        var b = new ShutdownLifecycleBroker
        {
            NextProbe = new BrokerStatus(true, 4321, 17654, "http://localhost:17654", "ws", "native-in-process"),
            NextStop  = new BrokerStopResult(BrokerStopOutcome.Failed, "native_broker_stop_timeout")
        };
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromMilliseconds(50), t.Log);
        var r = c.RunBrokerStop();
        Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
        Assert.Equal(BrokerStopOutcome.Failed, r.BrokerStopOutcome);
        Assert.Equal("native_broker_stop_timeout", r.FailureDetail);
        Assert.Single(t.Where("broker-stop-timeout"));
    }

    // Coverage point 7.
    [Fact]
    public void RunBrokerStop_stop_throws_is_caught_and_returns_ClosedWithBrokerStopped()
    {
        using var t = new TempLogger();
        var d = new ShutdownLifecycleDialog(CloseChoice.ClosePaxCookbook);
        var b = new ShutdownLifecycleBroker
        {
            NextProbe  = new BrokerStatus(true, 4321, 17654, "http://localhost:17654", "ws", "native-in-process"),
            StopThrows = new InvalidOperationException("kaboom")
        };
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromSeconds(2), t.Log);
        var r = c.RunBrokerStop();
        Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
        Assert.Equal(BrokerStopOutcome.Failed, r.BrokerStopOutcome);
        Assert.NotNull(r.FailureDetail);
        Assert.Contains("kaboom", r.FailureDetail);
        Assert.Single(t.Where("broker-stop-failed"));
    }

    // Coverage point 8.
    [Fact]
    public void RunBrokerStop_dormant_broker_returns_AlreadyStopped_without_calling_Stop()
    {
        using var t = new TempLogger();
        var d = new ShutdownLifecycleDialog(CloseChoice.ClosePaxCookbook);
        var b = new ShutdownLifecycleBroker
        {
            NextProbe = new BrokerStatus(false, null, null, null, null, "none")
        };
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromSeconds(2), t.Log);
        var r = c.RunBrokerStop();
        Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
        Assert.Equal(BrokerStopOutcome.AlreadyStopped, r.BrokerStopOutcome);
        Assert.Equal(1, b.ProbeCalls);
        Assert.Equal(0, b.StopCalls);
        Assert.Single(t.Where("broker-stop-complete"));
    }

    // Coverage point 9.
    [Fact]
    public void RunBrokerStop_no_workspace_returns_AlreadyStopped_without_probing()
    {
        var d = new ShutdownLifecycleDialog(CloseChoice.ClosePaxCookbook);
        var b = new ShutdownLifecycleBroker();
        var c = new CloseGestureCoordinator(d, b, () => null, TimeSpan.FromSeconds(2));
        var r = c.RunBrokerStop();
        Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
        Assert.Equal(BrokerStopOutcome.AlreadyStopped, r.BrokerStopOutcome);
        Assert.Equal(0, b.ProbeCalls);
        Assert.Equal(0, b.StopCalls);
    }

    // Coverage point 10.
    [Fact]
    public void RunBrokerStop_passes_configured_timeout_into_BrokerStopOptions()
    {
        var d = new ShutdownLifecycleDialog(CloseChoice.ClosePaxCookbook);
        var b = new ShutdownLifecycleBroker
        {
            NextProbe = new BrokerStatus(true, 4321, 17654, "u", "ws", "src")
        };
        var configured = TimeSpan.FromSeconds(7);
        var c = new CloseGestureCoordinator(d, b, () => "ws", configured);
        c.RunBrokerStop();
        Assert.NotNull(b.LastStop);
        Assert.Equal(configured, b.LastStop!.ExitTimeout);
        Assert.Equal(4321, b.LastStop!.BrokerPid);
        Assert.Equal(configured, c.BrokerStopTimeout);
    }

    // Coverage point 11. Asserts the coordinator does not add any
    // hidden delay on top of broker.Stop's bound.
    [Fact]
    public void RunBrokerStop_returns_promptly_when_broker_honors_short_timeout()
    {
        var d = new ShutdownLifecycleDialog(CloseChoice.ClosePaxCookbook);
        var b = new ShutdownLifecycleBroker
        {
            NextProbe = new BrokerStatus(true, 1, 17654, "u", "ws", "src"),
            StopDelay = TimeSpan.FromMilliseconds(150),
            NextStop  = new BrokerStopResult(BrokerStopOutcome.Failed, "delayed-stop")
        };
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromMilliseconds(100));
        var sw = Stopwatch.StartNew();
        var r = c.RunBrokerStop();
        sw.Stop();
        Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"RunBrokerStop took {sw.Elapsed} (expected < 2s with 150ms simulated stop)");
    }

    // =============================================================
    // C. Source-shape guards for the host's async shutdown
    // =============================================================

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            var probe = Path.Combine(dir, "src", "PAXCookbook", "PAXCookbook.csproj");
            if (File.Exists(probe)) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not find repo root from " + AppContext.BaseDirectory);
    }

    private static string ReadSrc(params string[] segs)
        => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(segs).ToArray()));

    // Coverage point 12.
    [Fact]
    public void HostForm_source_disposes_WebView2_and_runs_broker_stop_on_Task_Run_with_silent_close()
    {
        var src = ReadSrc("src", "PAXCookbook", "WebView2", "WebView2WinFormsHost.cs");
        // WebView2 disposal IS the deadlock breaker; if this is ever
        // removed the close-prompt -> Kestrel-drain hang returns.
        Assert.Contains("DisposeWebView2Quietly", src);
        // Broker stop must run off the UI thread.
        Assert.Contains("Task.Run(", src);
        Assert.Contains("coord.RunBrokerStop", src);
        // The programmatic close path must set _silentClose so the
        // re-entrant OnFormClosing bypasses the prompt.
        Assert.Contains("_silentClose = true;", src);
        Assert.Contains("BeginInvoke(", src);
    }

    // Coverage point 13a.
    [Fact]
    public void HostForm_source_has_shutdown_in_progress_coalesce_guard()
    {
        var src = ReadSrc("src", "PAXCookbook", "WebView2", "WebView2WinFormsHost.cs");
        Assert.Contains("_shutdownInProgress", src);
        Assert.Contains("BeginAsyncShutdown(", src);
        // The guard MUST run before the dialog-open guard so that a
        // second close request while the shutdown is in flight gets
        // bounced without re-prompting.
        var idxShutdown = src.IndexOf("if (_shutdownInProgress) { e.Cancel = true; return; }", StringComparison.Ordinal);
        var idxDialog   = src.IndexOf("if (_dialogOpen) { e.Cancel = true; return; }", StringComparison.Ordinal);
        Assert.True(idxShutdown > 0, "shutdown-in-progress guard missing");
        Assert.True(idxDialog   > 0, "dialog-open guard missing");
        Assert.True(idxShutdown < idxDialog, "shutdown-in-progress guard must precede dialog-open guard");
    }

    // Coverage point 13b.
    [Fact]
    public void HostForm_source_has_no_powershell_broker_spawn_path()
    {
        var src = ReadSrc("src", "PAXCookbook", "WebView2", "WebView2WinFormsHost.cs");
        Assert.DoesNotContain("Start-Broker", src, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwsh.exe", src, StringComparison.OrdinalIgnoreCase);
    }

    // Coverage point 13c.
    [Fact]
    public void NativeBrokerController_DisposeHostQuietly_is_bounded()
    {
        var src = ReadSrc("src", "PAXCookbook", "Broker", "Native", "NativeBrokerController.cs");
        Assert.Contains("private static void DisposeHostQuietly", src);
        // Bound: must use Wait(TimeSpan) rather than the legacy
        // GetAwaiter().GetResult() that would block forever.
        Assert.Contains(".Wait(TimeSpan.From", src);
        Assert.DoesNotContain("DisposeAsync().AsTask().GetAwaiter().GetResult();", src);
    }

    // =============================================================
    // D. Back-compat: Handle(...) still drives both phases for the
    //    Stage 3k test contract.
    // =============================================================

    [Fact]
    public void Handle_Cancel_returns_CancelClose_and_does_not_call_broker()
    {
        var d = new ShutdownLifecycleDialog(CloseChoice.Cancel);
        var b = new ShutdownLifecycleBroker();
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromSeconds(2));
        var r = c.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);
        Assert.Equal(CloseGestureOutcome.CancelClose, r.Outcome);
        Assert.Null(r.FailureDetail);
        Assert.Equal(0, b.StopCalls);
    }

    [Fact]
    public void Handle_ClosePaxCookbook_calls_broker_Stop_exactly_once()
    {
        var d = new ShutdownLifecycleDialog(CloseChoice.ClosePaxCookbook);
        var b = new ShutdownLifecycleBroker
        {
            NextProbe = new BrokerStatus(true, 4321, 17654, "u", "ws", "src")
        };
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromSeconds(2));
        var r = c.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);
        Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
        Assert.Equal(1, b.StopCalls);
    }

    [Fact]
    public void Handle_dialog_exception_returns_CancelClose_with_close_prompt_exception_failure_detail()
    {
        var d = new ShutdownLifecycleThrowingDialog();
        var b = new ShutdownLifecycleBroker
        {
            NextProbe = new BrokerStatus(true, 4321, 17654, "u", "ws", "src")
        };
        var c = new CloseGestureCoordinator(d, b, () => "ws", TimeSpan.FromSeconds(2));
        var r = c.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);
        Assert.Equal(CloseGestureOutcome.CancelClose, r.Outcome);
        Assert.Equal("close-prompt-exception", r.FailureDetail);
        Assert.Equal(0, b.StopCalls);
    }
}
