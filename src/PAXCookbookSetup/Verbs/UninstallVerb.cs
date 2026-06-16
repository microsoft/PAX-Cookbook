using System;
using System.Collections.Generic;
using System.IO;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Uninstall;

namespace PAXCookbookSetup.Verbs;

// Phase 9 — real uninstall verb.
//
// Standard uninstall (default): removes App/Setup/PreviousVersions/
// WebView2Data/Runtime + Phase 8 shell registrations. Preserves
// Workspace and Logs. Taskbar pin cleanup is deferred (see
// taskbar-pin-cleanup-contract.md §3 — the IPinnedList3 reliability
// probe is required first); a positive-ID identifier exists for the
// future phase that owns the probe.
//
// Full uninstall (opt-in): requires BOTH --remove-user-data AND
// --confirm-remove-user-data. With both set, Workspace and the
// per-user install root tree (Logs included) are also removed. Without
// the confirm flag the verb refuses and exits non-zero.
//
// External Purview/Entra/M365 exports are NEVER touched.
public static class UninstallVerb
{
    public const string ConfirmMissingMessage =
        "uninstall: --remove-user-data requires --confirm-remove-user-data " +
        "(prevents accidental Workspace deletion).";

    public static int Run(
        string installRoot,
        ParsedArgs args,
        SetupLogger log,
        TextWriter @out,
        UninstallOperations? operations = null)
    {
        // Validate opt-in flag pair before doing anything destructive.
        if (args.RemoveUserData && !args.ConfirmRemoveUserData)
        {
            @out.WriteLine(ConfirmMissingMessage);
            log.Write("uninstall-confirm-missing", "warn",
                new Dictionary<string, object?> { ["installRoot"] = installRoot });
            return SetupExitCodes.UsageError;
        }
        if (args.ConfirmRemoveUserData && !args.RemoveUserData)
        {
            @out.WriteLine("uninstall: --confirm-remove-user-data requires --remove-user-data.");
            log.Write("uninstall-confirm-without-flag", "warn",
                new Dictionary<string, object?> { ["installRoot"] = installRoot });
            return SetupExitCodes.UsageError;
        }

        bool full = args.RemoveUserData && args.ConfirmRemoveUserData;

        // Phase 12 (Mode B failure repair): --dry-run on the uninstall
        // verb is documented as non-mutating but the original
        // implementation forwarded it through self-handoff and then
        // ignored it on the receiving side, performing a real uninstall.
        // Until a proper preview path is implemented, refuse to run
        // when --dry-run is requested so we never silently mutate.
        if (args.DryRun)
        {
            log.Write("uninstall-dryrun-refused", "warn",
                new Dictionary<string, object?>
                {
                    ["installRoot"] = installRoot,
                    ["mode"] = full ? "full" : "standard",
                    ["force"] = args.Force
                });
            @out.WriteLine(
                "uninstall: --dry-run is not yet implemented. Refusing to run to avoid data loss. " +
                "Use a copy of the install root with a separate Workspace if you need to rehearse uninstall.");
            return SetupExitCodes.UsageError;
        }

        operations ??= BuildDefault();
        log.Write("uninstall-begin", fields: new Dictionary<string, object?>
        {
            ["installRoot"] = installRoot,
            ["mode"] = full ? "full" : "standard",
            ["force"] = args.Force
        });

        var opt = UninstallOptions.Defaults with { Force = args.Force };

        UninstallResult result;
        try
        {
            result = full
                ? operations.RunFull(installRoot, opt)
                : operations.RunStandard(installRoot, opt);
        }
        catch (Exception ex)
        {
            log.Write("uninstall-failed", "error",
                new Dictionary<string, object?>
                {
                    ["installRoot"] = installRoot, ["detail"] = ex.Message
                });
            @out.WriteLine($"uninstall failed: {ex.Message}");
            return SetupExitCodes.UninstallFailed;
        }

        if (result.Aborted)
        {
            log.Write("uninstall-aborted", "error",
                new Dictionary<string, object?>
                {
                    ["installRoot"] = installRoot,
                    ["reason"] = result.AbortReason,
                    ["stopExitCode"] = result.AppStopResult.ExitCode,
                    ["stopDetail"] = result.AppStopResult.Detail
                });
            @out.WriteLine("uninstall: PAX Cookbook is still running and could not be stopped. " +
                           "Close PAX Cookbook and try again.");
            @out.WriteLine($"  reason={result.AbortReason} stopExitCode={result.AppStopResult.ExitCode}");
            @out.WriteLine("  (use --force to override; preserves the installed app otherwise.)");
            return SetupExitCodes.UninstallFailed;
        }

        log.Write("uninstall-summary", fields: new Dictionary<string, object?>
        {
            ["mode"] = result.Mode,
            ["filesRemoved"] = result.FilesRemoved,
            ["filesDeferred"] = result.FilesDeferred,
            ["shortcutsRemoved"] = result.ShortcutsRemoved,
            ["shortcutsSkipped"] = result.ShortcutsSkipped,
            ["registryKeysRemoved"] = result.RegistryKeysRemoved,
            ["protocolRemoved"] = result.ProtocolRemoved,
            ["arpRemoved"] = result.ArpRemoved,
            ["workspacePreserved"] = result.WorkspacePreserved,
            ["workspaceRemoved"] = result.WorkspaceRemoved,
            ["logsPreserved"] = result.LogsPreserved,
            ["taskbarPerformed"] = result.TaskbarPinResult.Performed,
            ["taskbarReason"] = result.TaskbarPinResult.Reason,
            ["taskbarMode"] = result.TaskbarPinResult.Mode,
            ["pinsScanned"] = (result.TaskbarPinResult.Decisions?.Count ?? 0),
            ["pinsRemoved"] = result.TaskbarPinResult.Removed.Count,
            ["pinsSkipped"] = result.TaskbarPinResult.Skipped.Count
        });

        @out.WriteLine($"uninstall: mode={result.Mode}");
        @out.WriteLine($"  filesRemoved={result.FilesRemoved} " +
                       $"shortcutsRemoved={result.ShortcutsRemoved} " +
                       $"registryKeysRemoved={result.RegistryKeysRemoved}");
        @out.WriteLine($"  protocolRemoved={result.ProtocolRemoved} " +
                       $"arpRemoved={result.ArpRemoved}");
        @out.WriteLine($"  workspacePreserved={result.WorkspacePreserved} " +
                       $"logsPreserved={result.LogsPreserved}");
        @out.WriteLine($"  taskbarPinCleanup={result.TaskbarPinResult.Reason} " +
                       $"mode={result.TaskbarPinResult.Mode} " +
                       $"scanned={(result.TaskbarPinResult.Decisions?.Count ?? 0)} " +
                       $"removed={result.TaskbarPinResult.Removed.Count} " +
                       $"skipped={result.TaskbarPinResult.Skipped.Count}");

        // If any positive-ID pin failed to remove, surface a one-line
        // guidance hint. Live taskbar refresh may still lag behind .lnk
        // removal until the shell refreshes its pin cache.
        if (result.TaskbarPinResult.Decisions is not null)
        {
            int failed = 0;
            foreach (var d in result.TaskbarPinResult.Decisions)
                if (d.Outcome == "skipped-error") failed++;
            if (failed > 0)
            {
                @out.WriteLine($"  note: {failed} PAX taskbar pin(s) could not be removed; " +
                               "right-click and choose Unpin from taskbar to finish cleanup.");
            }
        }

        if (result.FilesDeferred > 0)
            @out.WriteLine($"  note: {result.FilesDeferred} file(s) scheduled for delete-on-reboot.");

        return SetupExitCodes.Ok;
    }

    private static UninstallOperations BuildDefault()
    {
        IRegistryWriter registry = TestShellGate.IsActive()
            ? new NoOpRegistryWriter() : new HkcuRegistryWriter();
        IShortcutWriter shortcutWriter = TestShellGate.IsActive()
            ? new NoOpShortcutWriter() : new Win32ShortcutWriter();
        var manifestStore = new ShortcutManifestStore();
        var shellRemover = new ShellRemover(shortcutWriter, registry, manifestStore);
        var stopper = new RealAppStopper();
        var files = new RealFileSystemRemover();
        // Phase 10: default cleaner is now the positive-ID .lnk cleaner.
        // It scans only %APPDATA%\Microsoft\Internet Explorer\Quick Launch\
        // User Pinned\TaskBar\*.lnk (no recursion) and removes ONLY pins
        // whose target / working directory positively identify as PAX
        // (under installRoot) or whose AUMID matches PAXCookbook.App.v1.
        // Unrelated pins (Edge, Chrome, user apps) are skipped.
        // IPinnedList3 live unpin remains DEFERRED until the cross-OS
        // reliability probe (Win10 22H2 + Win11 23H2) is executed in a
        // dedicated phase; until then the Windows taskbar may continue
        // to show a stale visual icon until the shell refreshes its
        // cache or the user clicks it.
        //
        // Test/E2E mode (PAXCOOKBOOK_TEST_NO_SHELL=1) falls back to the
        // deferred null cleaner so the installed-Setup self-handoff
        // E2E never touches the real user taskbar pin folder.
        ITaskbarPinCleaner taskbar = TestShellGate.IsActive()
            ? new NullTaskbarPinCleaner()
            : new LnkTaskbarPinCleaner(new ShellLinkResolver());
        return new UninstallOperations(stopper, files, shellRemover, taskbar);
    }
}
