using System.Collections.Concurrent;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- production registry of currently-running cook
// processes. Until Stage 3j wires CookExecutionService into the
// native broker the registry stays empty, so /stop and /kill return
// 404 cook_not_active for every real cookId. The registry is
// thread-safe (multi-process registration support is not required
// because all cook spawn calls flow through a single broker process).
public sealed class InMemoryCookProcessRegistry : ICookProcessRegistry
{
    private readonly ConcurrentDictionary<string, CookProcessHandle> _cooks = new();

    public bool TryGet(string cookId, out int processId)
    {
        if (_cooks.TryGetValue(cookId, out var handle))
        {
            processId = handle.ProcessId;
            return true;
        }
        processId = 0;
        return false;
    }

    public bool RequestStop(string cookId)
    {
        if (!_cooks.TryGetValue(cookId, out var handle)) return false;
        try
        {
            handle.RequestStop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool ForceKill(string cookId)
    {
        if (!_cooks.TryGetValue(cookId, out var handle)) return false;
        try
        {
            handle.ForceKill();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Stage 3j wiring point: CookExecutionService calls this after
    // PaxProcessRunner spawns the pwsh cook. The handle carries the
    // cookId, OS process id, and two delegates the broker uses to
    // signal stop / kill. The handle is removed when the cook exits.
    public void Register(string cookId, CookProcessHandle handle)
    {
        ArgumentException.ThrowIfNullOrEmpty(cookId);
        ArgumentNullException.ThrowIfNull(handle);
        _cooks[cookId] = handle;
    }

    public void Deregister(string cookId)
    {
        if (string.IsNullOrEmpty(cookId)) return;
        _cooks.TryRemove(cookId, out _);
    }
}

// Stage 3i-C -- handle the production registry carries per-cook.
// Two delegates: one for cooperative stop (write the cancellation
// file, raise the named-pipe event, etc.), one for force kill (call
// Process.Kill(true)). The handle wires both at registration time so
// the registry itself stays delegate-driven and the policy lives in
// CookExecutionService.
public sealed class CookProcessHandle
{
    public string CookId       { get; }
    public int    ProcessId    { get; }

    private readonly Action _requestStop;
    private readonly Action _forceKill;

    public CookProcessHandle(
        string cookId,
        int    processId,
        Action requestStop,
        Action forceKill)
    {
        ArgumentException.ThrowIfNullOrEmpty(cookId);
        CookId       = cookId;
        ProcessId    = processId;
        _requestStop = requestStop ?? throw new ArgumentNullException(nameof(requestStop));
        _forceKill   = forceKill   ?? throw new ArgumentNullException(nameof(forceKill));
    }

    public void RequestStop() => _requestStop();
    public void ForceKill()   => _forceKill();
}
