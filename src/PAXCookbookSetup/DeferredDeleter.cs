using System.Runtime.InteropServices;

namespace PAXCookbookSetup;

// MoveFileEx with MOVEFILE_DELAY_UNTIL_REBOOT, used as a last-resort
// fallback when the temp handoff folder cannot be deleted (e.g. an
// AV scanner has the file open). Injectable for tests.
public interface IDeferredDeleter
{
    bool ScheduleDeleteOnReboot(string path);
}

public sealed class Win32DeferredDeleter : IDeferredDeleter
{
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName,
                                          string? lpNewFileName, int dwFlags);

    public bool ScheduleDeleteOnReboot(string path)
    {
        // MOVEFILE_DELAY_UNTIL_REBOOT requires admin for HKLM-style targets,
        // but for per-user %TEMP% it works without elevation in practice.
        try { return MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
        catch { return false; }
    }
}

public sealed class RecordingDeferredDeleter : IDeferredDeleter
{
    public List<string> Scheduled { get; } = new();
    public bool ScheduleDeleteOnReboot(string path)
    {
        Scheduled.Add(path);
        return true;
    }
}
