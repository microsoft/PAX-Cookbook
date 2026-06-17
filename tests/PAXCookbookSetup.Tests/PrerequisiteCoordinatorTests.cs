using System;
using System.Collections.Generic;
using System.Linq;
using PAXCookbookSetup.Gui;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Slice D: unit tests for PrerequisiteCoordinator — the retry/skip orchestration
// over the prerequisite installers, exercised with fake installers (no network,
// no process). The wizard wiring itself is GUI and validated in the Slice F
// sandbox; this covers the decision logic.
public class PrerequisiteCoordinatorTests
{
    private sealed class FakeInstaller : IPrerequisiteInstaller
    {
        public PrerequisiteKind Kind { get; }
        public int Calls;
        public Func<int, PrerequisiteInstallResult> ResultForCall;

        public FakeInstaller(PrerequisiteKind kind, PrerequisiteInstallResult fixedResult)
        {
            Kind = kind;
            ResultForCall = _ => fixedResult;
        }
        public FakeInstaller(PrerequisiteKind kind, Func<int, PrerequisiteInstallResult> perCall)
        {
            Kind = kind;
            ResultForCall = perCall;
        }

        public PrerequisiteInstallResult Install(string tempDir, Action<string> progress)
        {
            Calls++;
            return ResultForCall(Calls);
        }
    }

    private static Dictionary<PrerequisiteKind, bool> Wanted(bool ps7, bool py) => new()
    {
        [PrerequisiteKind.PowerShell7] = ps7,
        [PrerequisiteKind.Python] = py
    };

    private static NamedPrerequisiteResult ResultFor(IReadOnlyList<NamedPrerequisiteResult> all, PrerequisiteKind k)
        => all.First(r => r.Kind == k);

    [Fact]
    public void NotWanted_RecordsSkippedWithoutRunning()
    {
        var ps7 = new FakeInstaller(PrerequisiteKind.PowerShell7,
            PrerequisiteInstallResult.Installed("should not run"));
        var py = new FakeInstaller(PrerequisiteKind.Python,
            PrerequisiteInstallResult.Installed("should not run"));
        var coord = new PrerequisiteCoordinator(new IPrerequisiteInstaller[] { ps7, py });

        var results = coord.Run(Wanted(false, false), "C:\\tmp", _ => { }, (_, _) => RetrySkipDecision.Skip);

        Assert.Equal(0, ps7.Calls);
        Assert.Equal(0, py.Calls);
        Assert.Equal(PrerequisiteInstallOutcome.Skipped, ResultFor(results, PrerequisiteKind.PowerShell7).Result.Outcome);
        Assert.Equal(PrerequisiteInstallOutcome.Skipped, ResultFor(results, PrerequisiteKind.Python).Result.Outcome);
    }

    [Fact]
    public void Wanted_Success_RunsOnce()
    {
        var ps7 = new FakeInstaller(PrerequisiteKind.PowerShell7,
            PrerequisiteInstallResult.Installed("ok"));
        var py = new FakeInstaller(PrerequisiteKind.Python,
            PrerequisiteInstallResult.AlreadyPresent("already"));
        var coord = new PrerequisiteCoordinator(new IPrerequisiteInstaller[] { ps7, py });

        var results = coord.Run(Wanted(true, true), "C:\\tmp", _ => { }, (_, _) => RetrySkipDecision.Skip);

        Assert.Equal(1, ps7.Calls);
        Assert.Equal(1, py.Calls);
        Assert.Equal(PrerequisiteInstallOutcome.Installed, ResultFor(results, PrerequisiteKind.PowerShell7).Result.Outcome);
    }

    [Fact]
    public void Failure_Skip_RecordsFailedAndContinues()
    {
        var ps7 = new FakeInstaller(PrerequisiteKind.PowerShell7,
            PrerequisiteInstallResult.Failed("boom"));
        var py = new FakeInstaller(PrerequisiteKind.Python,
            PrerequisiteInstallResult.Installed("ok"));
        var coord = new PrerequisiteCoordinator(new IPrerequisiteInstaller[] { ps7, py });

        var results = coord.Run(Wanted(true, true), "C:\\tmp", _ => { }, (_, _) => RetrySkipDecision.Skip);

        Assert.Equal(1, ps7.Calls);                 // tried once, then skipped
        Assert.Equal(1, py.Calls);                  // sequence continued to Python
        Assert.Equal(PrerequisiteInstallOutcome.Failed, ResultFor(results, PrerequisiteKind.PowerShell7).Result.Outcome);
        Assert.Equal(PrerequisiteInstallOutcome.Installed, ResultFor(results, PrerequisiteKind.Python).Result.Outcome);
    }

    [Fact]
    public void Failure_Retry_ThenSuccess_RunsTwice()
    {
        // Fail on the first call, succeed on the second.
        var ps7 = new FakeInstaller(PrerequisiteKind.PowerShell7,
            call => call == 1
                ? PrerequisiteInstallResult.Failed("first try")
                : PrerequisiteInstallResult.Installed("second try"));
        var coord = new PrerequisiteCoordinator(new IPrerequisiteInstaller[] { ps7 });

        // onError returns Retry the first time, then would return Skip — but the
        // second install succeeds, so onError is consulted only once.
        int errorCalls = 0;
        var results = coord.Run(Wanted(true, false), "C:\\tmp", _ => { },
            (_, _) => { errorCalls++; return RetrySkipDecision.Retry; });

        Assert.Equal(2, ps7.Calls);
        Assert.Equal(1, errorCalls);
        Assert.Equal(PrerequisiteInstallOutcome.Installed, ResultFor(results, PrerequisiteKind.PowerShell7).Result.Outcome);
    }

    [Fact]
    public void UserDeclined_TriggersErrorPrompt()
    {
        var ps7 = new FakeInstaller(PrerequisiteKind.PowerShell7,
            PrerequisiteInstallResult.Declined("declined"));
        var coord = new PrerequisiteCoordinator(new IPrerequisiteInstaller[] { ps7 });

        int errorCalls = 0;
        var results = coord.Run(Wanted(true, false), "C:\\tmp", _ => { },
            (_, _) => { errorCalls++; return RetrySkipDecision.Skip; });

        Assert.Equal(1, errorCalls); // declined is treated as a retryable error prompt
        Assert.Equal(PrerequisiteInstallOutcome.UserDeclined, ResultFor(results, PrerequisiteKind.PowerShell7).Result.Outcome);
    }

    [Fact]
    public void Order_IsPowerShellThenPython()
    {
        var order = new List<PrerequisiteKind>();
        var ps7 = new FakeInstaller(PrerequisiteKind.PowerShell7, _ =>
        {
            order.Add(PrerequisiteKind.PowerShell7);
            return PrerequisiteInstallResult.Installed("ok");
        });
        var py = new FakeInstaller(PrerequisiteKind.Python, _ =>
        {
            order.Add(PrerequisiteKind.Python);
            return PrerequisiteInstallResult.Installed("ok");
        });
        var coord = new PrerequisiteCoordinator(new IPrerequisiteInstaller[] { ps7, py });

        coord.Run(Wanted(true, true), "C:\\tmp", _ => { }, (_, _) => RetrySkipDecision.Skip);

        Assert.Equal(new[] { PrerequisiteKind.PowerShell7, PrerequisiteKind.Python }, order);
    }
}
