namespace PAXCookbook.Ipc;

// Phase 4 single-instance coordinator.
//
// Reserves the cross-process mutex Local\PAXCookbook.PrimaryInstance so a
// future WebView2 phase can reliably detect when another App instance is
// already running. Today the coordinator is informational only — it does
// not gate command execution. Tests use a unique mutex name to avoid
// poisoning other instances.
public sealed class AppInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;

    public string MutexName { get; }
    public bool IsPrimary { get; }

    public AppInstanceCoordinator(string? mutexNameOverride = null)
    {
        MutexName = mutexNameOverride ?? IpcConstants.PrimaryInstanceMutexName;
        bool createdNew;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out createdNew);
            IsPrimary = createdNew;
        }
        catch
        {
            // Anonymous fallback — never fail the constructor on AccessDenied.
            _mutex = new Mutex(false);
            IsPrimary = false;
        }
    }

    public void Dispose()
    {
        try { if (IsPrimary) _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
