using System;
using System.Threading;
using System.Threading.Tasks;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Deterministic tests for the attached-window close-both lifecycle decision
// (T1-S2B TASK B). These pin the separate-daemon "Exit" behavior: an attached
// window must send the authenticated daemon shutdown, await it before closing,
// close on a confirmed stop, and surface a truthful bounded failure otherwise;
// a standalone window closes without contacting any daemon. No PAX/Bake path is
// involved anywhere in this decision.
public sealed class AttachedWindowCloseCoordinatorTests
{
    [Fact]
    public async Task Attached_close_both_sends_shutdown_and_closes_on_confirmed_stop()
    {
        int calls = 0;
        var coordinator = new AttachedWindowCloseCoordinator(
            CloseAppMode.Attached,
            _ =>
            {
                calls++;
                return Task.FromResult(true);
            });

        CloseAppAction action = await coordinator.DecideCloseAppAsync(CancellationToken.None);

        Assert.Equal(1, calls); // the daemon shutdown request was sent
        Assert.Equal(CloseAppAction.CloseWindow, action); // success -> both closed
    }

    [Fact]
    public async Task Attached_close_both_awaits_shutdown_before_deciding()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AttachedWindowCloseCoordinator(
            CloseAppMode.Attached,
            _ => gate.Task);

        Task<CloseAppAction> decision = coordinator.DecideCloseAppAsync(CancellationToken.None);

        // The decision must not complete until the daemon shutdown resolves, so
        // the window can never exit before the request is delivered and awaited.
        Assert.False(decision.IsCompleted);
        gate.SetResult(true);
        Assert.Equal(CloseAppAction.CloseWindow, await decision);
    }

    [Fact]
    public async Task Attached_close_both_surfaces_truthful_failure_when_daemon_refuses()
    {
        // e.g. HTTP 423 while Locked -> delegate returns false (not stopped).
        var coordinator = new AttachedWindowCloseCoordinator(
            CloseAppMode.Attached,
            _ => Task.FromResult(false));

        CloseAppAction action = await coordinator.DecideCloseAppAsync(CancellationToken.None);

        Assert.Equal(CloseAppAction.SurfaceDaemonFailure, action);
        Assert.NotEqual(CloseAppAction.CloseWindow, action); // never a silent "both closed"
    }

    [Fact]
    public async Task Attached_close_both_surfaces_truthful_failure_when_shutdown_throws()
    {
        var coordinator = new AttachedWindowCloseCoordinator(
            CloseAppMode.Attached,
            _ => throw new InvalidOperationException("network"));

        CloseAppAction action = await coordinator.DecideCloseAppAsync(CancellationToken.None);

        Assert.Equal(CloseAppAction.SurfaceDaemonFailure, action);
    }

    [Fact]
    public async Task Standalone_close_closes_window_without_contacting_any_daemon()
    {
        int calls = 0;
        var coordinator = new AttachedWindowCloseCoordinator(
            CloseAppMode.Standalone,
            _ =>
            {
                calls++;
                return Task.FromResult(true);
            });

        CloseAppAction action = await coordinator.DecideCloseAppAsync(CancellationToken.None);

        Assert.Equal(0, calls); // standalone owns its broker; no daemon shutdown is sent
        Assert.Equal(CloseAppAction.CloseWindow, action);
    }

    [Fact]
    public async Task Standalone_with_null_delegate_closes_window()
    {
        var coordinator = new AttachedWindowCloseCoordinator(CloseAppMode.Standalone, null);

        CloseAppAction action = await coordinator.DecideCloseAppAsync(CancellationToken.None);

        Assert.Equal(CloseAppAction.CloseWindow, action);
    }

    [Fact]
    public async Task Attached_with_missing_delegate_falls_back_to_close_not_false_success()
    {
        // Defensive: an Attached mode without a delegate cannot claim a daemon
        // stop it never attempted; it degrades to closing the window rather than
        // blocking. (Production always supplies the delegate in Attached mode.)
        var coordinator = new AttachedWindowCloseCoordinator(CloseAppMode.Attached, null);

        CloseAppAction action = await coordinator.DecideCloseAppAsync(CancellationToken.None);

        Assert.Equal(CloseAppAction.CloseWindow, action);
    }

    [Fact]
    public async Task Attached_close_both_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var coordinator = new AttachedWindowCloseCoordinator(
            CloseAppMode.Attached,
            ct => Task.FromCanceled<bool>(ct));

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.DecideCloseAppAsync(cts.Token));
    }
}
