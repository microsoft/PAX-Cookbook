using System;
using System.Collections.Generic;
using System.IO;
using PAXCookbook.Shared;

namespace PAXCookbookSetup.Uninstall;

// Taskbar pin cleanup per taskbar-pin-cleanup-contract.md.
//
// Phase 9 decision (recorded in PHASE_9_REPORT.md §17): the contract
// REQUIRES a Win10 22H2 + Win11 23H2 IPinnedList3 reliability probe
// BEFORE shipping live taskbar removal. That probe is a separate
// hardware-bound work item and has not been executed. Live taskbar
// pin removal is therefore DEFERRED.
//
// To keep the foundation testable and avoid re-introducing the
// problem in a later phase, the positive-identification logic that
// the contract demands (§4) IS implemented here and exercised by
// Phase 9 unit tests. It is wired to the default NullTaskbarPinCleaner
// at runtime so uninstall NEVER touches the live taskbar by default.
// A LnkTaskbarPinCleaner is provided for the future phase that owns
// the probe — it removes ONLY .lnk files whose positive identification
// matches the contract (target under installRoot, AUMID match, or
// working directory under installRoot).
public interface ITaskbarPinCleaner
{
    TaskbarPinCleanupResult Cleanup(string installRoot);
}

public sealed record TaskbarPinCleanupCandidate(
    string LnkPath,
    string Target,
    string WorkingDirectory,
    string Aumid,
    string MatchReason); // "target-under-installroot" | "aumid-match" | "workingdir-under-installroot"

public sealed record TaskbarPinCleanupResult(
    bool Performed,
    string Reason,                          // "deferred" | "performed" | "no-folder" | "not-attempted-abort"
    IReadOnlyList<TaskbarPinCleanupCandidate> Candidates,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Skipped,
    string Mode = "deferred",               // "lnk-positive-id" | "ipinnedlist3" | "deferred"
    IReadOnlyList<TaskbarPinDecision>? Decisions = null);

// Per-.lnk decision the cleaner reached. Used for structured logging
// and verifier inspection. Outcome values:
//   removed-pax-pin       — positive-ID match, .lnk deleted.
//   skipped-not-pax       — resolved but no positive-ID rule matched.
//   skipped-unresolvable  — resolver returned null (broken .lnk).
//   skipped-error         — DeleteLnk failed despite positive-ID.
//   skipped-dry-run       — positive-ID match, but cleaner is in dry-run mode.
public sealed record TaskbarPinDecision(
    string LnkPath,
    string Outcome,
    string? MatchReason);

// Default runtime impl: never touches the live taskbar. Records the
// deferral so logs/status can report it.
public sealed class NullTaskbarPinCleaner : ITaskbarPinCleaner
{
    public TaskbarPinCleanupResult Cleanup(string installRoot)
        => new TaskbarPinCleanupResult(
            Performed: false,
            Reason: "deferred",
            Candidates: System.Array.Empty<TaskbarPinCleanupCandidate>(),
            Removed: System.Array.Empty<string>(),
            Skipped: System.Array.Empty<string>(),
            Mode: "deferred",
            Decisions: System.Array.Empty<TaskbarPinDecision>());
}

// Positive-ID identifier. Reads a folder of .lnk files via an injected
// resolver (so unit tests do not need real .lnk files / COM).
public interface ITaskbarLnkResolver
{
    // Returns null if the .lnk cannot be resolved.
    TaskbarLnkInfo? Resolve(string lnkPath);
    IEnumerable<string> EnumerateLnkFiles(string folderPath);
    bool DeleteLnk(string lnkPath);
}

public sealed record TaskbarLnkInfo(
    string LnkPath,
    string Target,
    string WorkingDirectory,
    string Aumid);

public sealed class LnkTaskbarPinCleaner : ITaskbarPinCleaner
{
    public static string DefaultUserPinnedTaskbarFolder()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    private readonly ITaskbarLnkResolver _resolver;
    private readonly string _folder;
    private readonly bool _dryRun;

    public LnkTaskbarPinCleaner(ITaskbarLnkResolver resolver,
                                string? folderOverride = null,
                                bool dryRun = false)
    {
        _resolver = resolver;
        _folder = folderOverride ?? DefaultUserPinnedTaskbarFolder();
        _dryRun = dryRun;
    }

    // Pure positive-ID matcher exposed for tests.
    public static string? PositiveIdReason(
        string installRoot, TaskbarLnkInfo info)
    {
        var rootFull = Path.GetFullPath(installRoot)
                         .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!string.IsNullOrEmpty(info.Target))
        {
            var t = Path.GetFullPath(info.Target);
            if (t.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return "target-under-installroot";
        }
        if (string.Equals(info.Aumid, ProductConstants.Aumid, StringComparison.Ordinal))
            return "aumid-match";
        if (!string.IsNullOrEmpty(info.WorkingDirectory))
        {
            var w = Path.GetFullPath(info.WorkingDirectory)
                      .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (w.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return "workingdir-under-installroot";
        }
        return null;
    }

    public TaskbarPinCleanupResult Cleanup(string installRoot)
    {
        var candidates = new List<TaskbarPinCleanupCandidate>();
        var removed = new List<string>();
        var skipped = new List<string>();
        var decisions = new List<TaskbarPinDecision>();

        IEnumerable<string> lnks;
        try { lnks = _resolver.EnumerateLnkFiles(_folder); }
        catch
        {
            return new TaskbarPinCleanupResult(false, "no-folder",
                candidates, removed, skipped,
                Mode: "lnk-positive-id", Decisions: decisions);
        }

        foreach (var lnk in lnks)
        {
            var info = _resolver.Resolve(lnk);
            if (info is null)
            {
                skipped.Add(lnk);
                decisions.Add(new TaskbarPinDecision(lnk, "skipped-unresolvable", null));
                continue;
            }
            var reason = PositiveIdReason(installRoot, info);
            if (reason is null)
            {
                skipped.Add(lnk);
                decisions.Add(new TaskbarPinDecision(lnk, "skipped-not-pax", null));
                continue;
            }

            candidates.Add(new TaskbarPinCleanupCandidate(
                LnkPath: lnk, Target: info.Target,
                WorkingDirectory: info.WorkingDirectory,
                Aumid: info.Aumid, MatchReason: reason));

            if (_dryRun)
            {
                skipped.Add(lnk);
                decisions.Add(new TaskbarPinDecision(lnk, "skipped-dry-run", reason));
                continue;
            }

            if (_resolver.DeleteLnk(lnk))
            {
                removed.Add(lnk);
                decisions.Add(new TaskbarPinDecision(lnk, "removed-pax-pin", reason));
            }
            else
            {
                skipped.Add(lnk);
                decisions.Add(new TaskbarPinDecision(lnk, "skipped-error", reason));
            }
        }

        return new TaskbarPinCleanupResult(
            Performed: true,
            Reason: _dryRun ? "dry-run" : "performed",
            Candidates: candidates, Removed: removed, Skipped: skipped,
            Mode: "lnk-positive-id", Decisions: decisions);
    }
}

// Test resolver: stores a fixed map of lnkPath -> info; tests verify
// positive ID without any real .lnk parsing or filesystem touch.
public sealed class InMemoryTaskbarLnkResolver : ITaskbarLnkResolver
{
    public Dictionary<string, TaskbarLnkInfo?> Map { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Deleted { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public TaskbarLnkInfo? Resolve(string lnkPath)
        => Map.TryGetValue(lnkPath, out var v) ? v : null;

    public IEnumerable<string> EnumerateLnkFiles(string folderPath) => Map.Keys;

    public bool DeleteLnk(string lnkPath)
    {
        Deleted.Add(lnkPath);
        return true;
    }
}
