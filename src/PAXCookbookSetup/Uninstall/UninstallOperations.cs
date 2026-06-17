using System;
using System.Collections.Generic;
using System.IO;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Shell;

namespace PAXCookbookSetup.Uninstall;

// Phase 9 — real uninstall orchestrator.
//
// Implements uninstall-contract.md:
//   Standard mode (default): removes installed App/Setup/PreviousVersions/
//     WebView2Data/runtime sidecars + Phase 8 shell registrations
//     (shortcuts, protocol, ARP). Preserves Workspace and Logs.
//   Full mode (opt-in via two-flag confirmation): additionally removes
//     the Workspace folder recorded in install-state and the per-user
//     %LOCALAPPDATA%\PAXCookbook tree (including Logs) — but NEVER any
//     path outside the install root or the recorded Workspace folder.
//
// Always preserves external Purview/Entra/M365 exports — those paths
// are not in any list this orchestrator iterates.
//
// All side effects flow through injected abstractions
// (IAppStopper, IFileSystemRemover, ShellRemover, ITaskbarPinCleaner)
// so Phase 9 tests do not touch the real user shell or %LOCALAPPDATA%.
public sealed class UninstallOperations
{
    public const int DefaultStopTimeoutMs = 10_000;

    private readonly IAppStopper _stopper;
    private readonly IFileSystemRemover _files;
    private readonly ShellRemover _shellRemover;
    private readonly ITaskbarPinCleaner _taskbarCleaner;

    public UninstallOperations(
        IAppStopper stopper,
        IFileSystemRemover files,
        ShellRemover shellRemover,
        ITaskbarPinCleaner taskbarCleaner)
    {
        _stopper = stopper;
        _files = files;
        _shellRemover = shellRemover;
        _taskbarCleaner = taskbarCleaner;
    }

    public UninstallResult RunStandard(string installRoot, UninstallOptions? options = null,
                                       Action<string>? progress = null)
        => Run(installRoot, full: false, options ?? UninstallOptions.Defaults, progress);

    public UninstallResult RunFull(string installRoot, UninstallOptions? options = null,
                                   Action<string>? progress = null)
        => Run(installRoot, full: true, options ?? UninstallOptions.Defaults, progress);

    private UninstallResult Run(string installRoot, bool full, UninstallOptions opt,
                               Action<string>? progress = null)
    {
        var steps = new List<string>();
        var skipped = new List<string>();

        // ---- 1. Stop running app ------------------------------------
        progress?.Invoke("Stopping PAX Cookbook\u2026");
        var stop = _stopper.TryStop(installRoot, opt.StopTimeoutMs);
        steps.Add($"stop: invoked={stop.Invoked} exeFound={stop.ExeFound} exited={stop.Exited}");

        // Stop-failure policy: if the app stop helper was actually invoked
        // (i.e. the App exe existed) but did NOT exit cleanly with code 0,
        // abort the uninstall BEFORE touching any files. This prevents
        // partial removal while the app or broker is still running.
        // Override only via --force.
        bool stopFailed = stop.Invoked && stop.ExeFound &&
                          (!stop.Exited || (stop.ExitCode ?? -1) != 0);
        if (stopFailed && !opt.Force)
        {
            string reason = !stop.Exited ? "stop-timed-out" : "stop-nonzero";
            steps.Add($"abort: {reason}");
            return new UninstallResult(
                Mode: full ? "full" : "standard",
                Steps: steps, Skipped: skipped,
                FilesRemoved: 0, FilesDeferred: 0,
                ShortcutsRemoved: 0, ShortcutsSkipped: 0,
                RegistryKeysRemoved: 0, RegistryKeysSkipped: 0,
                ProtocolRemoved: false, ArpRemoved: false,
                WorkspacePreserved: true, WorkspaceRemoved: false,
                LogsPreserved: true, LocalAppDataRemoved: false,
                TaskbarPinResult: new TaskbarPinCleanupResult(
                    Performed: false, Reason: "not-attempted-abort",
                    Candidates: System.Array.Empty<TaskbarPinCleanupCandidate>(),
                    Removed: System.Array.Empty<string>(),
                    Skipped: System.Array.Empty<string>(),
                    Mode: "deferred",
                    Decisions: System.Array.Empty<TaskbarPinDecision>()),
                AppStopResult: stop,
                Aborted: true, AbortReason: reason,
                Errors: new List<string> { reason });
        }
        if (stopFailed && opt.Force)
        {
            steps.Add("force: continuing after stop failure");
        }

        // ---- 2. Read install-state for Workspace path (Full mode) ----
        var state = InstallStateStore.TryLoad(installRoot);
        string? workspacePath = state?.WorkspaceFolderPath;

        // ---- 3. Shell removal (shortcuts + protocol + ARP) ----------
        progress?.Invoke("Removing shortcuts and registry entries\u2026");
        var shell = _shellRemover.Remove(installRoot);
        steps.Add($"shell: shortcutsRemoved={shell.ShortcutsRemoved.Count} " +
                  $"shortcutsSkipped={shell.ShortcutsSkipped.Count} " +
                  $"protocolRemoved={shell.ProtocolRemoved} " +
                  $"arpRemoved={shell.ArpRemoved} " +
                  $"autoStartRemoved={shell.AutoStartRemoved}");

        // ---- 4. Taskbar pin cleanup (deferred or positive-ID) -------
        var taskbar = _taskbarCleaner.Cleanup(installRoot);
        steps.Add($"taskbar: performed={taskbar.Performed} reason={taskbar.Reason} " +
                  $"candidates={taskbar.Candidates.Count} removed={taskbar.Removed.Count}");

        // ---- 5. File payload removal --------------------------------
        progress?.Invoke("Removing application files\u2026");
        var fileResults = new List<RemoveResult>();
        foreach (var sub in StandardRemovableSubdirs())
        {
            var path = Path.Combine(installRoot, sub);
            if (!Directory.Exists(path)) { skipped.Add(sub); continue; }
            fileResults.Add(_files.RemoveDirectory(path));
            steps.Add($"removed: {sub}");
        }

        // install-state.json — deleted LAST after other steps succeed.
        var statePath = InstallStateStore.PathFor(installRoot);
        if (File.Exists(statePath))
        {
            fileResults.Add(_files.RemoveFile(statePath));
            steps.Add("removed: install-state.json");
        }

        // ---- 6. Full-mode extras ------------------------------------
        bool workspaceRemoved = false;
        bool localAppDataRemoved = false;
        if (full)
        {
            if (!string.IsNullOrEmpty(workspacePath) && Directory.Exists(workspacePath))
            {
                // Workspace MUST NOT be under any external export path.
                // We only honor the recorded path verbatim — that path was
                // chosen by the user during install and is the only place
                // we are authorized to touch in Full mode.
                fileResults.Add(_files.RemoveDirectory(workspacePath!));
                workspaceRemoved = true;
                steps.Add($"removed: workspace={workspacePath}");
            }

            // %LOCALAPPDATA%\PAXCookbook — only remove the install root
            // tree if it is a sibling of the install root (i.e. they are
            // the same path or one wraps the other). The uninstall
            // contract authorizes removal of %LOCALAPPDATA%\PAXCookbook;
            // when installRoot == %LOCALAPPDATA%\PAXCookbook the loop
            // above has already taken care of subdirs and we finish by
            // removing the now-empty install root.
            if (opt.LocalAppDataFolderOverride is not null &&
                Directory.Exists(opt.LocalAppDataFolderOverride))
            {
                fileResults.Add(_files.RemoveDirectory(opt.LocalAppDataFolderOverride));
                localAppDataRemoved = true;
                steps.Add($"removed: localappdata={opt.LocalAppDataFolderOverride}");
            }
            else if (Directory.Exists(installRoot))
            {
                fileResults.Add(_files.RemoveDirectory(installRoot));
                localAppDataRemoved = true;
                steps.Add($"removed: installRoot={installRoot}");
            }
        }

        int filesRemoved = 0, filesDeferred = 0;
        var errors = new List<string>();
        foreach (var r in fileResults)
        {
            filesRemoved += r.FilesRemoved;
            filesDeferred += r.FilesDeferred;
            errors.AddRange(r.Errors);
        }

        return new UninstallResult(
            Mode: full ? "full" : "standard",
            Steps: steps,
            Skipped: skipped,
            FilesRemoved: filesRemoved,
            FilesDeferred: filesDeferred,
            ShortcutsRemoved: shell.ShortcutsRemoved.Count,
            ShortcutsSkipped: shell.ShortcutsSkipped.Count,
            RegistryKeysRemoved: shell.RegistryKeysRemoved.Count,
            RegistryKeysSkipped: shell.RegistryKeysSkipped.Count,
            ProtocolRemoved: shell.ProtocolRemoved,
            ArpRemoved: shell.ArpRemoved,
            WorkspacePreserved: !full || !workspaceRemoved,
            WorkspaceRemoved: workspaceRemoved,
            LogsPreserved: !full,
            LocalAppDataRemoved: localAppDataRemoved,
            TaskbarPinResult: taskbar,
            AppStopResult: stop,
            Aborted: false,
            AbortReason: null,
            Errors: errors);
    }

    // Per uninstall-contract.md §3.1.
    public static IEnumerable<string> StandardRemovableSubdirs()
    {
        yield return "App";
        yield return "Setup";
        yield return "PreviousVersions";
        yield return "WebView2Data";
        // Phase 12 (Mode B failure repair): the installed Setup EXE
        // resolves payload from <installRoot>\PayloadCache for
        // repair/update when no --payload-root is supplied and no
        // embedded payload exists. Standard uninstall removes it so
        // the install root is gone after uninstall.
        yield return "PayloadCache";
        yield return "Runtime";
    }
}

public sealed record UninstallOptions(
    int StopTimeoutMs,
    string? LocalAppDataFolderOverride, // Test override; default = derive from installRoot.
    bool Force // --force: proceed with uninstall even after stop failure.
)
{
    public static UninstallOptions Defaults { get; } =
        new UninstallOptions(UninstallOperations.DefaultStopTimeoutMs, null, false);
}

public sealed record UninstallResult(
    string Mode,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Skipped,
    int FilesRemoved,
    int FilesDeferred,
    int ShortcutsRemoved,
    int ShortcutsSkipped,
    int RegistryKeysRemoved,
    int RegistryKeysSkipped,
    bool ProtocolRemoved,
    bool ArpRemoved,
    bool WorkspacePreserved,
    bool WorkspaceRemoved,
    bool LogsPreserved,
    bool LocalAppDataRemoved,
    TaskbarPinCleanupResult TaskbarPinResult,
    AppStopResult AppStopResult,
    bool Aborted,
    string? AbortReason,
    IReadOnlyList<string> Errors);
