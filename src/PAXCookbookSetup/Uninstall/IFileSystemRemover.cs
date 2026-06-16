using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PAXCookbookSetup.Uninstall;

// File-system removal abstraction with retry + MoveFileEx-on-reboot
// fallback per uninstall-contract.md §9. Tests inject a fake that
// records what would have been deleted without touching real disk.
public interface IFileSystemRemover
{
    // Removes a single file with retry + deferred-delete fallback.
    // Returns true if the file was removed in this call; false if it
    // was scheduled for deferred deletion. Missing files return true.
    RemoveResult RemoveFile(string path);

    // Recursively removes a directory and everything under it.
    // Per-file errors fall back to deferred-delete; the call itself
    // does not throw on locked files.
    RemoveResult RemoveDirectory(string path);
}

public sealed record RemoveResult(
    bool Removed,
    bool DeferredToReboot,
    int FilesRemoved,
    int FilesDeferred,
    IReadOnlyList<string> Errors);

public sealed class RealFileSystemRemover : IFileSystemRemover
{
    private readonly IDeferredDeleter _deferred;
    private readonly int _retryCount;
    private readonly int _backoffMs;

    public RealFileSystemRemover(IDeferredDeleter? deferred = null,
                                 int retryCount = 3, int backoffMs = 250)
    {
        _deferred = deferred ?? new Win32DeferredDeleter();
        _retryCount = retryCount;
        _backoffMs = backoffMs;
    }

    public RemoveResult RemoveFile(string path)
    {
        var errors = new List<string>();
        if (!File.Exists(path))
            return new RemoveResult(true, false, 0, 0, errors);

        for (int attempt = 0; attempt < _retryCount; attempt++)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return new RemoveResult(true, false, 1, 0, errors);
            }
            catch (Exception ex)
            {
                errors.Add($"attempt {attempt + 1}: {ex.Message}");
                if (attempt < _retryCount - 1)
                    Thread.Sleep(_backoffMs);
            }
        }

        // Fall back: schedule for MoveFileEx delete-on-reboot.
        var scheduled = _deferred.ScheduleDeleteOnReboot(path);
        return new RemoveResult(scheduled, scheduled, 0, scheduled ? 1 : 0, errors);
    }

    public RemoveResult RemoveDirectory(string path)
    {
        var errors = new List<string>();
        if (!Directory.Exists(path))
            return new RemoveResult(true, false, 0, 0, errors);

        int removed = 0, deferred = 0;

        // Delete files depth-first.
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories); }
        catch (Exception ex)
        {
            errors.Add($"enumerate {path}: {ex.Message}");
            return new RemoveResult(false, false, 0, 0, errors);
        }

        foreach (var f in files)
        {
            var r = RemoveFile(f);
            removed += r.FilesRemoved;
            deferred += r.FilesDeferred;
            errors.AddRange(r.Errors);
        }

        // Then directories deepest-first.
        try
        {
            var dirs = new List<string>(Directory.EnumerateDirectories(
                path, "*", SearchOption.AllDirectories));
            dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
            foreach (var d in dirs)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, recursive: false); }
                catch (Exception ex) { errors.Add($"rmdir {d}: {ex.Message}"); }
            }
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: false); }
                catch (Exception ex)
                {
                    errors.Add($"rmdir {path}: {ex.Message}");
                    if (_deferred.ScheduleDeleteOnReboot(path)) deferred++;
                }
            }
        }
        catch (Exception ex) { errors.Add(ex.Message); }

        return new RemoveResult(
            Removed: !Directory.Exists(path),
            DeferredToReboot: deferred > 0,
            FilesRemoved: removed,
            FilesDeferred: deferred,
            Errors: errors);
    }
}

// Test fake: records every path it was asked to delete; the underlying
// real disk is not touched by tests that wire this in.
public sealed class RecordingFileSystemRemover : IFileSystemRemover
{
    public List<string> FilesRemoved { get; } = new();
    public List<string> DirsRemoved { get; } = new();

    // When true, the recorder also performs the real removal so tests
    // that prepare a temp filesystem can verify on-disk state.
    public bool PassThrough { get; set; }

    public RemoveResult RemoveFile(string path)
    {
        FilesRemoved.Add(path);
        if (PassThrough && File.Exists(path))
            try { File.Delete(path); } catch { }
        return new RemoveResult(true, false, 1, 0, System.Array.Empty<string>());
    }

    public RemoveResult RemoveDirectory(string path)
    {
        DirsRemoved.Add(path);
        if (PassThrough && Directory.Exists(path))
            try { Directory.Delete(path, recursive: true); } catch { }
        return new RemoveResult(true, false, 0, 0, System.Array.Empty<string>());
    }
}
