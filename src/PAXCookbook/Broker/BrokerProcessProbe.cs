using System.Diagnostics;

namespace PAXCookbook.Broker;

// Verifies whether a recorded broker PID corresponds to a live process.
//
// Rules:
//   - Never matches the current process.
//   - Never matches the parent process.
//   - A PID is "alive" iff Process.GetProcessById succeeds AND the
//     process has not yet exited.
//
// We deliberately do NOT inspect command lines here — the workspace.lock
// is already the contract of ownership. Walking Win32_Process is left to
// the launcher-side scripts which need broader sweep behavior.
public interface IBrokerProcessProbe
{
    bool IsAlive(int pid);
}

public sealed class DefaultBrokerProcessProbe : IBrokerProcessProbe
{
    public bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        if (pid == Environment.ProcessId) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
