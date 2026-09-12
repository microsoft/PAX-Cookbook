using System;
using System.IO;
using PAXCookbook.Shared;
using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Paths;
using PAXCookbookSetup.Provider;

namespace PAXCookbookSetup.Verbs;

// Bounded, support-suitable CLI for pre-unlock provider repair. It never unlocks
// the app and never authenticates through the currently selected provider. All
// output is bounded/non-secret (provider names, health flags, action names) —
// no tenant/client/account identifier is ever printed.
//
//   PAXCookbookSetup provider-status  [--install-root <path>]
//   PAXCookbookSetup provider-repair  --to-hello | --remove-work-config
//                                     [--install-root <path>]
//
// (--to-work is available in an interactive session where the native-WAM test
//  window can appear; scripted runs use --to-hello / --remove-work-config.)
public static class ProviderRepairCli
{
    public const string StatusVerb = "provider-status";
    public const string RepairVerb = "provider-repair";

    public static bool IsRequested(string verb)
        => string.Equals(verb, StatusVerb, StringComparison.OrdinalIgnoreCase)
        || string.Equals(verb, RepairVerb, StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args, TextWriter outWriter)
    {
        string verb = args[0];
        string? installRoot = null;
        bool toHello = false, removeWorkConfig = false, toWork = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--install-root":
                    if (i + 1 >= args.Length) { outWriter.WriteLine("error: --install-root requires a value"); return SetupExitCodes.UsageError; }
                    installRoot = args[++i];
                    break;
                case "--to-hello": toHello = true; break;
                case "--remove-work-config": removeWorkConfig = true; break;
                case "--to-work": toWork = true; break;
                default:
                    outWriter.WriteLine($"error: unknown argument: {args[i]}");
                    return SetupExitCodes.UsageError;
            }
        }

        string root = installRoot ?? AppPaths.InstallRoot();
        string localAppDataBase = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)) ?? root;
        bool experimental = SetupChannel.IsExperimental(SetupChannel.Resolve());
        var service = new ProviderRepairService(localAppDataBase, new WindowsHelloSupportProbe());

        if (string.Equals(verb, StatusVerb, StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus(service, experimental, outWriter);
            return SetupExitCodes.Ok;
        }

        // provider-repair
        int chosen = (toHello ? 1 : 0) + (removeWorkConfig ? 1 : 0) + (toWork ? 1 : 0);
        if (chosen != 1)
        {
            outWriter.WriteLine("error: choose exactly one of --to-hello, --remove-work-config, --to-work");
            return SetupExitCodes.UsageError;
        }

        if (toHello)
        {
            ProviderSetupResult r = service.SwitchToWindowsHello();
            outWriter.WriteLine(r.Succeeded ? "switched to windows_hello" : $"switch failed: {r.Status}");
            return r.Succeeded ? SetupExitCodes.Ok : SetupExitCodes.GenericError;
        }

        if (removeWorkConfig)
        {
            bool removed = service.RemoveLocalWorkConfig();
            outWriter.WriteLine(removed ? "removed local work-account configuration" : "no local work-account configuration to remove");
            return SetupExitCodes.Ok;
        }

        // --to-work: requires the interactive native-WAM test.
        string appExe = Path.Combine(AppPaths.BinRoot(localAppDataBase), ProductConstants.AppExeName);
        string resultFile = Path.Combine(Path.GetTempPath(), "pax_provider_repair_" + Guid.NewGuid().ToString("N") + ".json");
        var gate = new AppLaunchWorkAccountGate(localAppDataBase, appExe, new RealProcessLauncher(), resultFile);
        ProviderSetupResult wr = service.SwitchToWorkAccount(gate);
        try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }
        outWriter.WriteLine(wr.Succeeded ? "switched to work_account" : $"switch failed: {wr.Status}");
        return wr.Succeeded ? SetupExitCodes.Ok : SetupExitCodes.GenericError;
    }

    private static void PrintStatus(ProviderRepairService service, bool experimental, TextWriter outWriter)
    {
        ProviderRepairStatus s = service.ReadStatus();
        string provider = s.Selection.RecoveryRequired
            ? "recovery_required"
            : SessionProviderStore.ToWire(s.Selection.Provider);
        outWriter.WriteLine("selected provider: " + provider);
        outWriter.WriteLine("hello available: " + (s.HelloAvailable ? "yes" : "no"));
        outWriter.WriteLine("work-account local config: " + (s.WorkAccountConfigPresent ? "present" : "absent"));
        outWriter.WriteLine("work-account verified: " + (s.WorkAccountVerified ? "yes" : "no"));
        outWriter.WriteLine("available repair actions:");
        foreach (ProviderRepairAction a in service.AvailableActions(experimental, tenantAdminAuthorized: false))
        {
            outWriter.WriteLine("  - " + a);
        }
    }
}
