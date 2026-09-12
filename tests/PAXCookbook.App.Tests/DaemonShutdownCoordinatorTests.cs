using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Deterministic lifecycle tests for the shared daemon-shutdown coordinator
// (T1-S2B TASK B live-lifecycle repair). These pin the properties whose absence
// caused the live hang: the tray/menu teardown is marshaled to the UI thread,
// the Kestrel stop runs OFF the UI thread (Shutdown never blocks/waits on itself),
// duplicate triggers are harmless, and a stalled or throwing host stop still
// force-exits within a bounded interval. No PAX/Bake path is involved.
public sealed class DaemonShutdownCoordinatorTests
{
    private static async Task<bool> Completes(Task task, int ms = 3000)
        => await Task.WhenAny(task, Task.Delay(ms)).ConfigureAwait(false) == task;

    [Fact]
    public async Task First_shutdown_tears_down_ui_off_thread_stops_host_and_force_exits()
    {
        bool teardownRan = false;
        bool postToUiRan = false;
        bool stopHostRan = false;
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var c = new DaemonShutdownCoordinator(
            teardownTrayUi: () => teardownRan = true,
            postToUi: a => { postToUiRan = true; a(); },   // proves the teardown is marshaled
            stopHost: _ => { stopHostRan = true; return Task.CompletedTask; },
            forceExit: () => exited.TrySetResult(true),
            budget: TimeSpan.FromMilliseconds(100));

        Assert.True(c.Shutdown());
        Assert.True(postToUiRan);   // tray UI teardown went through the UI-thread marshaler
        Assert.True(teardownRan);   // NotifyIcon + ContextMenuStrip disposed / loop ended
        Assert.True(await Completes(exited.Task));
        Assert.True(stopHostRan);   // Kestrel stop was requested
    }

    [Fact]
    public async Task Duplicate_shutdown_requests_are_harmless_noops()
    {
        int teardowns = 0;
        int exits = 0;
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var c = new DaemonShutdownCoordinator(
            teardownTrayUi: () => Interlocked.Increment(ref teardowns),
            postToUi: a => a(),
            stopHost: _ => Task.CompletedTask,
            forceExit: () => { Interlocked.Increment(ref exits); exited.TrySetResult(true); },
            budget: TimeSpan.FromMilliseconds(50));

        Assert.True(c.Shutdown());   // first wins
        Assert.False(c.Shutdown());  // duplicates are safe no-ops
        Assert.False(c.Shutdown());
        Assert.True(await Completes(exited.Task));

        Assert.Equal(1, teardowns);  // teardown ran exactly once
        Assert.Equal(1, exits);      // force-exit ran exactly once
    }

    [Fact]
    public void Shutdown_returns_promptly_and_does_not_wait_on_the_host_stop()
    {
        // A hung host stop must never block the caller (the tray UI thread) — this
        // is the exact deadlock the old sync-over-async StopAsync().GetResult() hit.
        var c = new DaemonShutdownCoordinator(
            teardownTrayUi: () => { },
            postToUi: a => a(),
            stopHost: _ => new TaskCompletionSource<object>().Task, // never completes
            forceExit: () => { },
            budget: TimeSpan.FromMilliseconds(100));

        var sw = Stopwatch.StartNew();
        Assert.True(c.Shutdown());
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, "Shutdown must not await the host stop");
    }

    [Fact]
    public async Task Watchdog_force_exits_even_when_host_stop_hangs()
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var c = new DaemonShutdownCoordinator(
            teardownTrayUi: () => { },
            postToUi: a => a(),
            stopHost: _ => new TaskCompletionSource<object>().Task, // hung host, ignores cancellation
            forceExit: () => exited.TrySetResult(true),
            budget: TimeSpan.FromMilliseconds(100));

        Assert.True(c.Shutdown());
        Assert.True(await Completes(exited.Task), "watchdog must force-exit within the bounded budget");
    }

    [Fact]
    public async Task Force_exits_even_when_host_stop_throws()
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var c = new DaemonShutdownCoordinator(
            teardownTrayUi: () => { },
            postToUi: a => a(),
            stopHost: _ => throw new InvalidOperationException("host stop failed"),
            forceExit: () => exited.TrySetResult(true),
            budget: TimeSpan.FromMilliseconds(100));

        Assert.True(c.Shutdown());
        Assert.True(await Completes(exited.Task));
    }

    [Fact]
    public async Task Teardown_failure_still_force_exits()
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var c = new DaemonShutdownCoordinator(
            teardownTrayUi: () => throw new InvalidOperationException("dispose failed"),
            postToUi: a => a(),
            stopHost: _ => Task.CompletedTask,
            forceExit: () => exited.TrySetResult(true),
            budget: TimeSpan.FromMilliseconds(100));

        Assert.True(c.Shutdown());   // teardown throwing does not prevent shutdown
        Assert.True(await Completes(exited.Task));
    }
}
