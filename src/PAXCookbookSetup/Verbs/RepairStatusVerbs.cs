using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Shell;

namespace PAXCookbookSetup.Verbs;

public static class RepairVerb
{
    public static int Run(ParsedArgs args, Manifest m, string payloadRoot,
                          string installRoot, SetupLogger log,
                          IPayloadOperations? payloadOps = null,
                          IShellOperations? shellOps = null)
    {
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
