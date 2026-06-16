using Microsoft.Extensions.Hosting;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-A -- shutdown coordinator.
//
// Parity with Routes/BrokerShutdown.ps1:
//   * The 202 response is flushed BEFORE the shutdown is signalled
//     so the SPA receives the acceptance envelope. The route layer
//     enforces flush ordering; this service merely raises the
//     shutdown signal.
//   * Earliest-writer-wins: the first call to Signal records the
//     reason; subsequent calls are no-ops. The PS broker uses
//     `if ($null -eq $Script:ShutdownReason) { ... }` for the same
//     effect.
//   * No process kill. The host stops Kestrel cooperatively via
//     IHostApplicationLifetime.StopApplication.
//
// The coordinator is split behind an interface so the Stage 3i-A
// tests inject a fake (FakeBrokerShutdownCoordinator) and assert
// the shutdown signal without actually stopping the test host.
public interface IBrokerShutdownCoordinator
{
    bool   HasBeenSignalled { get; }
    string? Reason           { get; }
    void   Signal(string reason);
}

public sealed class BrokerShutdownCoordinator : IBrokerShutdownCoordinator
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly object                   _gate = new();
    private bool                              _signalled;
    private string?                           _reason;

    public BrokerShutdownCoordinator(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    public bool   HasBeenSignalled
    {
        get { lock (_gate) { return _signalled; } }
    }

    public string? Reason
    {
        get { lock (_gate) { return _reason; } }
    }

    public void Signal(string reason)
    {
        lock (_gate)
        {
            if (_signalled) return;
            _signalled = true;
            _reason    = reason;
        }
        try
        {
            _lifetime.StopApplication();
        }
        catch
        {
            // Parity with the PS broker, which swallows
            // $Script:Listener.Stop() exceptions. The shutdown
            // signal is best-effort -- the chef sees the 202 and
            // the launcher detaches independently.
        }
    }
}
