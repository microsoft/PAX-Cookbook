using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Uninstall;

namespace PAXCookbookSetup.Verbs;

public static class RepairVerb
{
    // How long to wait for a running PAX Cookbook to close before repairing.
    private const int AppStopTimeoutMs = 15_000;

    public static int Run(ParsedArgs args, Manifest m, string payloadRoot,
                          string installRoot, SetupLogger log,
                          IPayloadOperations? payloadOps = null,
                          IShellOperations? shellOps = null,
                          IAppStopper? appStopper = null)
    {
        appStopper ??= new RealAppStopper();

        // Repair (including the Add/Remove Programs "Modify" button) runs while
        // PAX Cookbook may still be open; its window, tray, and in-process broker
        // hold locks on App\bin. Stop it first — the same stop InstallVerb does.
        // Best-effort: a stop that cannot complete never fails repair on its own;
        // the file-replace step reports any file that is still locked.
        if (Directory.Exists(installRoot))
        {
            try
            {
                var stop = appStopper.TryStop(installRoot, AppStopTimeoutMs);
                log.Write("repair-app-stop", fields: new Dictionary<string, object?>
                {
                    ["invoked"] = stop.Invoked, ["exeFound"] = stop.ExeFound,
                    ["exited"] = stop.Exited, ["exitCode"] = stop.ExitCode, ["detail"] = stop.Detail
                });
            }
            catch (Exception ex)
            {
                log.Write("repair-app-stop-error", "warn",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
            }
        }

        // Repair routes through UpdateVerb with isRepair=true so the same
        // snapshot/replace/rollback path executes.
        return UpdateVerb.Run(args, m, payloadRoot, installRoot, log,
                              isRepair: true, payloadOps, shellOps);
    }
}

public static class StatusVerb
{
    public static int Run(string installRoot, SetupLogger log, TextWriter @out,
                          IShellOperations? shellOps = null)
    {
        var state = InstallStateStore.TryLoad(installRoot);
        if (state is null)
        {
            @out.WriteLine("status: not-installed");
            log.Write("status", fields: new Dictionary<string, object?>
            { ["installed"] = false, ["installRoot"] = installRoot });
            return SetupExitCodes.Ok;
        }
        @out.WriteLine($"status: installed");
        @out.WriteLine($"appVersion:   {state.AppVersion}");
        @out.WriteLine($"setupVersion: {state.SetupVersion}");
        @out.WriteLine($"installRoot:  {state.InstallRoot}");
        @out.WriteLine($"lastOp:       {state.LastOperation.Kind}/{state.LastOperation.Status} @ {state.LastOperation.At}");

        var fields = new Dictionary<string, object?>
        {
            ["installed"] = true, ["appVersion"] = state.AppVersion,
            ["setupVersion"] = state.SetupVersion
        };
        if (shellOps is not null)
        {
            var sh = shellOps.Inspect(installRoot);
            @out.WriteLine($"shortcutsCreated:   {sh.ShortcutsCount}");
            @out.WriteLine($"protocolRegistered: {sh.ProtocolRegistered}");
            @out.WriteLine($"uninstallRegistered:{sh.UninstallRegistered}");
            @out.WriteLine($"appIconPresent:     {sh.AppIconPresent}");
            @out.WriteLine($"fileAssociations:   {sh.FileAssociationsRegistered}");
            fields["shortcutsCreated"] = sh.ShortcutsCount;
            fields["protocolRegistered"] = sh.ProtocolRegistered;
            fields["uninstallRegistered"] = sh.UninstallRegistered;
            fields["appIconPresent"] = sh.AppIconPresent;
            fields["fileAssociationsRegistered"] = sh.FileAssociationsRegistered;
        }
        log.Write("status", fields: fields);
        return SetupExitCodes.Ok;
    }
}
