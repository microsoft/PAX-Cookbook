using PAXCookbook.Broker;
using PAXCookbook.Ipc;
using PAXCookbook.Shared.ExitCodes;

namespace PAXCookbook.Commands;

// stop — Phase 7: coordinate UI close + broker stop.
//
// If a primary instance is running (we cannot acquire the mutex), we
// forward a "stop" verb over IPC. The primary closes the UI silently
// and stops the broker.
//
// If no primary owns the mutex, we behave like Phase 4: probe and stop
// the broker directly from workspace.lock. Idempotent.
public static class StopCommand
{
    public static int Run(CommandContext ctx)
    {
        var gate = ctx.Gate.TryAcquirePrimary();
        if (gate.Role == InstanceRole.Secondary)
        {
            var fwd = ctx.IpcClient.Forward(ctx.IpcEndpoint.GetEndpointName(), IpcAllowlist.Stop, TimeSpan.FromSeconds(5));
            ctx.Log.Write("App", "stop-forwarded", "info", new Dictionary<string, object?>
            {
                ["outcome"] = fwd.Outcome.ToString()
            });
            if (fwd.Outcome == IpcClientOutcome.Accepted)
            {
                ctx.Stdout.WriteLine("stop: forwarded to running instance.");
                return AppExitCodes.Ok;
            }
            ctx.Log.Write("App", "stop-forward-failed-fallback-direct", "warn");
        }
        else
        {
            try { gate.PrimaryHandle?.Dispose(); } catch { }
        }

        var workspace = OpenCommand.ResolveWorkspace(ctx);
        if (workspace is null)
        {
            ctx.Stdout.WriteLine("stop: workspace not configured; nothing to stop.");
            ctx.Log.Write("App", "stop-no-workspace", "info");
            return AppExitCodes.Ok;
        }

        var probe = ctx.Broker.Probe(workspace);
        if (!probe.Running || probe.Pid is null)
        {
            ctx.Stdout.WriteLine("stop: broker not running; already stopped.");
            ctx.Log.Write("App", "stop-already-stopped", "info");
            return AppExitCodes.Ok;
        }

        ctx.Log.Write("App", "stop-broker-attempt", "info", new Dictionary<string, object?>
        {
            ["pid"] = probe.Pid
        });
        var r = ctx.Broker.Stop(new BrokerStopOptions(probe.Pid.Value, ctx.BrokerStopTimeout));
        if (r.Outcome is BrokerStopOutcome.Stopped or BrokerStopOutcome.AlreadyStopped)
        {
            ctx.Stdout.WriteLine("stop: " + (r.Outcome == BrokerStopOutcome.Stopped ? "broker stopped." : "broker already stopped."));
            ctx.Log.Write("App", "stop-broker-stopped", "info", new Dictionary<string, object?>
            {
                ["outcome"] = r.Outcome.ToString()
            });
            return AppExitCodes.Ok;
        }
        ctx.Stderr.WriteLine("stop: failed to stop broker: " + r.FailureDetail);
        ctx.Log.Write("App", "stop-broker-failed", "error", new Dictionary<string, object?>
        {
            ["detail"] = r.FailureDetail
        });
        return AppExitCodes.InternalError;
    }
}
