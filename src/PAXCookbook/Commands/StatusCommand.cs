using System.Text.Json;
using PAXCookbook.Broker;
using PAXCookbook.Ipc;
using PAXCookbook.Shared;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.WebView2;

namespace PAXCookbook.Commands;

// status — secret-free JSON snapshot. Phase 7 adds singleInstance +
// aumid to the ui surface. Single-instance is reported as true when a
// primary owns the mutex (i.e. status cannot acquire it).
public static class StatusCommand
{
    public static int Run(CommandContext ctx)
    {
        var state = ctx.InstallState.TryReadInstallState();
        string? wsPath = state?.WorkspaceFolderPath;
        if (string.IsNullOrWhiteSpace(wsPath))
        {
            wsPath = ctx.Bootstrap.TryRead()?.WorkspaceFolderPath;
        }

        BrokerStatus broker;
        if (string.IsNullOrWhiteSpace(wsPath))
        {
            broker = new BrokerStatus(false, null, null, null, null, "none");
        }
        else
        {
            broker = ctx.Broker.Probe(wsPath!);
        }

        var wv = ctx.WebView2Detector.Detect();
        string runtimeStatus = wv.Status switch
        {
            WebView2RuntimeStatus.Present => "present",
            WebView2RuntimeStatus.Missing => "missing",
            _ => "unknown"
        };

        // Probe the gate without holding it: if secondary, a primary
        // already owns the mutex.
        var probe = ctx.Gate.TryAcquirePrimary();
        bool primaryAlreadyOwned = probe.Role == InstanceRole.Secondary;
        try { probe.PrimaryHandle?.Dispose(); } catch { }

        var payload = new
        {
            product = ProductConstants.ProductName,
            appVersion = state?.AppVersion ?? "0.0.0",
            installRoot = ctx.InstallState.InstallRoot,
            installed = state is not null,
            installStatePresent = ctx.InstallState.InstallStatePresent,
            workspaceFolderPath = wsPath ?? "",
            broker = new
            {
                running = broker.Running,
                pid = broker.Pid,
                port = broker.Port,
                url = broker.Url,
                lockFile = broker.LockFile ?? "",
                source = broker.Source
            },
            ui = new
            {
                implemented = true,
                surface = "webview2",
                running = (bool?)broker.Running,
                singleInstance = primaryAlreadyOwned,
                aumid = ctx.Aumid.Aumid
            },
            webView2 = new
            {
                implemented = true,
                runtimeStatus = runtimeStatus,
                pv = wv.Pv ?? "",
                userDataFolder = ctx.WebView2Data.UserDataFolder
            }
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        ctx.Stdout.WriteLine(json);
        ctx.Log.Write("App", "status-emitted", "info", new Dictionary<string, object?>
        {
            ["brokerRunning"] = broker.Running,
            ["installed"] = state is not null,
            ["webView2RuntimeStatus"] = runtimeStatus,
            ["singleInstance"] = primaryAlreadyOwned
        });
        return AppExitCodes.Ok;
    }
}
