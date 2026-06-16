namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- registry of currently-running cook processes.
// Populated by the cook execution path when a cook is spawned and
// cleared when the cook exits. Stage 3i-C wires the registry into
// the stop / kill routes; Stage 3j wires it into the production
// CookExecutionService + PaxProcessRunner so STOP / KILL actually
// reach a live process.
//
// In production at Stage 3i-C the registry is registered as an
// InMemoryCookProcessRegistry that is never populated by any
// other surface (because NativeBrokerHost is not yet wired through
// BrokerController). The /stop and /kill routes therefore return
// 404 cook_not_active for every real cookId. Tests pre-populate
// FakeCookProcessRegistry so the routes can exercise the active
// path.
public interface ICookProcessRegistry
{
    // Returns true if the cookId has an active process registered.
    bool TryGet(string cookId, out int processId);

    // Requests a cooperative stop on the active cook. Returns false
    // if the cook is not registered or the request could not be
    // delivered (e.g. signal pipe closed). The registry implementation
    // is responsible for routing the request to the cook's own
    // cancellation file / signal pipe (Stage 3j wiring).
    bool RequestStop(string cookId);

    // Force-terminates the active cook process. Returns false if the
    // cook is not registered or the termination call failed.
    bool ForceKill(string cookId);

    // Stage 3j -- production CookExecutionService publishes a
    // CookProcessHandle here as soon as the cook spawn is requested
    // (placeholder handle with processId=0) and re-publishes once
    // PaxProcessRunner returns the real OS pid. The handle carries
    // the requestStop / forceKill delegates the /stop and /kill
    // routes invoke via RequestStop / ForceKill above.
    void Register(string cookId, CookProcessHandle handle);

    // Stage 3j -- removes the handle when the cook exits (success,
    // failure, or spawn refusal). A subsequent /stop or /kill call
    // for the same cookId returns 404 cook_not_active.
    void Deregister(string cookId);
}
