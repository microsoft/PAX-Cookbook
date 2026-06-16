namespace PAXCookbook.Ipc;

// Single-instance gate per paxcookbook-ipc-contract.md §3. The first
// process to claim the named mutex is the primary. Secondaries forward
// over the named pipe and exit.
public enum InstanceRole
{
    Primary,
    Secondary
}

public sealed record GateAcquireResult(InstanceRole Role, bool StaleRecovered, IDisposable? PrimaryHandle);

public interface IAppInstanceGate
{
    GateAcquireResult TryAcquirePrimary();
}

// Production gate: real OS mutex.
public sealed class MutexAppInstanceGate : IAppInstanceGate
{
    private readonly string _mutexName;

    public MutexAppInstanceGate(string? mutexNameOverride = null)
    {
        _mutexName = mutexNameOverride ?? Shared.ProductConstants.PrimaryInstanceMutexName;
    }

    public GateAcquireResult TryAcquirePrimary()
    {
        bool createdNew = false;
        bool stale = false;
        Mutex mtx;
        try
        {
            mtx = new Mutex(initiallyOwned: false, name: _mutexName, createdNew: out createdNew);
        }
        catch (Exception)
        {
            // AccessDenied or platform issue: treat as secondary so we
            // never inflate the primary count by accident.
            return new GateAcquireResult(InstanceRole.Secondary, false, null);
        }
        bool acquired = false;
        try
        {
            acquired = mtx.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
            stale = true;
        }
        if (!acquired)
        {
            try { mtx.Dispose(); } catch { }
            return new GateAcquireResult(InstanceRole.Secondary, false, null);
        }
        return new GateAcquireResult(InstanceRole.Primary, stale, new PrimaryHandle(mtx));
    }

    private sealed class PrimaryHandle : IDisposable
    {
        private readonly Mutex _mtx;
        private bool _released;
        public PrimaryHandle(Mutex m) => _mtx = m;
        public void Dispose()
        {
            if (_released) return;
            _released = true;
            try { _mtx.ReleaseMutex(); } catch { }
            try { _mtx.Dispose(); } catch { }
        }
    }
}

// Test/default gate: always claims primary. Used by Phase 4/6 tests
// that never exercise the gate path.
public sealed class AlwaysPrimaryGate : IAppInstanceGate
{
    public GateAcquireResult TryAcquirePrimary()
        => new(InstanceRole.Primary, false, new Sentinel());

    private sealed class Sentinel : IDisposable { public void Dispose() { } }
}
