using PAXCookbook.Broker;
using PAXCookbook.Logging;

namespace PAXCookbook.WebView2;

// Pure-orchestration close-gesture coordinator. Asks the dialog for a
// choice, then on ClosePaxCookbook drives the native broker stop. The
// WebView2 host wires this from its FormClosing handler; tests exercise
// the coordinator directly with a fake dialog + fake broker controller.
//
// Stage 3k: the user-facing decision space is two-valued (Cancel or
// ClosePaxCookbook) because Stage 3j hosts Kestrel in-process. Outcomes
// follow the dialog choice: Cancel keeps the window and broker; the
// close action always stops the broker before the window closes.
//
// Stage 5 AB: the prompt and the broker-stop phases are exposed as
// independent methods (PromptForChoice / RunBrokerStop) so the host
// can run the modal prompt synchronously on the UI thread, dispose
// WebView2 to drop its loopback HTTP sockets, and only then drive the
// broker stop on a background thread. Blocking FormClosing on a
// synchronous broker stop deadlocks against the Kestrel drain because
// Kestrel waits for those WebView2 sockets to close, but they only
// close after WebView2 disposes, which only happens after FormClosing
// returns. The legacy Handle(...) entry point remains for tests and
// for any caller that wants the full sequence on one thread.
public enum CloseGestureOutcome
{
    CancelClose,             // keep the UI window open; broker untouched
    ClosedWithBrokerStopped  // close UI; broker stop attempted (success or failure)
}

public sealed record CloseGestureResult(CloseGestureOutcome Outcome, BrokerStopOutcome? BrokerStopOutcome, string? FailureDetail);

// Stage 5 AB: split result returned by PromptForChoice so the host
// can distinguish a "user clicked Cancel" from "dialog threw an
// exception and we defaulted to Cancel to keep the window open".
public readonly record struct PromptResult(CloseChoice Choice, string? FailureDetail);

public sealed class CloseGestureCoordinator
{
    private readonly ICloseDialogService _dialog;
    private readonly IBrokerController _broker;
    private readonly Func<string?> _workspaceLookup;
    private readonly TimeSpan _brokerStopTimeout;
    private readonly AppLogger? _log;

    public CloseGestureCoordinator(
        ICloseDialogService dialog,
        IBrokerController broker,
        Func<string?> workspaceLookup,
        TimeSpan brokerStopTimeout,
        AppLogger? log = null)
    {
        _dialog = dialog;
        _broker = broker;
        _workspaceLookup = workspaceLookup;
        _brokerStopTimeout = brokerStopTimeout;
        _log = log;
    }

    // Exposed so the host's FormClosing handler can write its own
    // close-prompt-handler-exception event with the same AppLogger
    // sink that this coordinator already uses.
    public AppLogger? Log => _log;

    // Stage 5 AB: exposed so the host's outer shutdown watchdog can
    // pick a hard timeout derived from the same value the broker
    // stop already honors internally.
    public TimeSpan BrokerStopTimeout => _brokerStopTimeout;

    // Stage 5 AB: prompt-only phase. Must run on the UI/STA thread
    // because the dialog is a modal WinForms Form. Logs
    // close-prompt-open before the call and close-prompt-result on
    // success, or close-prompt-exception on a dialog fault. On any
    // dialog exception the method returns Cancel so the caller keeps
    // the window open; the failure detail "close-prompt-exception" is
    // preserved on the PromptResult so Handle(...) can stamp it on
    // the legacy CloseGestureResult shape.
    public PromptResult PromptForChoice(CloseTrigger trigger, IntPtr ownerHwnd)
    {
        _log?.Write("App", "close-prompt-open", "info", new Dictionary<string, object?>
        {
            ["trigger"] = trigger.ToString()
        });
        CloseChoice choice;
        try
        {
            choice = _dialog.Prompt(ownerHwnd);
        }
        catch (Exception ex)
        {
            _log?.Write("App", "close-prompt-exception", "error", new Dictionary<string, object?>
            {
                ["trigger"] = trigger.ToString(),
                ["exception"] = ex.GetType().FullName,
                ["message"] = ex.Message,
                ["stack"] = ex.ToString()
            });
            return new PromptResult(CloseChoice.Cancel, "close-prompt-exception");
        }
        _log?.Write("App", "close-prompt-result", "info", new Dictionary<string, object?>
        {
            ["choice"] = choice.ToString()
        });
        return new PromptResult(choice, null);
    }

    // Stage 5 AB: broker-stop phase. Safe to call from any thread.
    // Logs broker-stop-start before the call and one of
    // broker-stop-complete (success / already-stopped),
    // broker-stop-failed (probe or stop exception), or
    // broker-stop-timeout (stop returned a non-success outcome with a
    // FailureDetail) afterward. The user is never trapped: every code
    // path resolves to ClosedWithBrokerStopped so the caller can
    // proceed to close the window.
    public CloseGestureResult RunBrokerStop()
    {
        _log?.Write("App", "broker-stop-start", "info", new Dictionary<string, object?>
        {
            ["timeoutMs"] = (int)_brokerStopTimeout.TotalMilliseconds
        });
        var ws = _workspaceLookup();
        if (string.IsNullOrWhiteSpace(ws))
        {
            _log?.Write("App", "broker-stop-complete", "info", new Dictionary<string, object?>
            {
                ["outcome"] = nameof(BrokerStopOutcome.AlreadyStopped),
                ["reason"]  = "no-workspace"
            });
            return new CloseGestureResult(CloseGestureOutcome.ClosedWithBrokerStopped, BrokerStopOutcome.AlreadyStopped, null);
        }
        BrokerStatus probe;
        try
        {
            probe = _broker.Probe(ws);
        }
        catch (Exception ex)
        {
            _log?.Write("App", "broker-stop-failed", "error", new Dictionary<string, object?>
            {
                ["phase"]     = "probe",
                ["exception"] = ex.GetType().FullName,
                ["message"]   = ex.Message
            });
            return new CloseGestureResult(CloseGestureOutcome.ClosedWithBrokerStopped, BrokerStopOutcome.Failed, "probe-failed: " + ex.Message);
        }
        if (!probe.Running || probe.Pid is null)
        {
            _log?.Write("App", "broker-stop-complete", "info", new Dictionary<string, object?>
            {
                ["outcome"] = nameof(BrokerStopOutcome.AlreadyStopped),
                ["reason"]  = "probe-not-running"
            });
            return new CloseGestureResult(CloseGestureOutcome.ClosedWithBrokerStopped, BrokerStopOutcome.AlreadyStopped, null);
        }
        BrokerStopResult r;
        try
        {
            r = _broker.Stop(new BrokerStopOptions(probe.Pid.Value, _brokerStopTimeout));
        }
        catch (Exception ex)
        {
            _log?.Write("App", "broker-stop-failed", "error", new Dictionary<string, object?>
            {
                ["phase"]     = "stop",
                ["exception"] = ex.GetType().FullName,
                ["message"]   = ex.Message
            });
            return new CloseGestureResult(CloseGestureOutcome.ClosedWithBrokerStopped, BrokerStopOutcome.Failed, "stop-threw: " + ex.Message);
        }
        if (r.Outcome == BrokerStopOutcome.Stopped || r.Outcome == BrokerStopOutcome.AlreadyStopped)
        {
            _log?.Write("App", "broker-stop-complete", "info", new Dictionary<string, object?>
            {
                ["outcome"] = r.Outcome.ToString()
            });
            return new CloseGestureResult(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome, null);
        }
        _log?.Write("App", "broker-stop-timeout", "warn", new Dictionary<string, object?>
        {
            ["waitedMs"] = (int)_brokerStopTimeout.TotalMilliseconds,
            ["detail"]   = r.FailureDetail
        });
        return new CloseGestureResult(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome, r.FailureDetail);
    }

    // Back-compat full-sequence entry point. Tests drive the
    // coordinator through this method to preserve the Stage 3k
    // contract. Production OnFormClosing calls PromptForChoice +
    // RunBrokerStop separately so the broker stop runs off the UI
    // thread.
    public CloseGestureResult Handle(CloseTrigger trigger, IntPtr ownerHwnd)
    {
        var p = PromptForChoice(trigger, ownerHwnd);
        switch (p.Choice)
        {
            case CloseChoice.Cancel:
                return new CloseGestureResult(CloseGestureOutcome.CancelClose, null, p.FailureDetail);

            case CloseChoice.ClosePaxCookbook:
                return RunBrokerStop();

            default:
                return new CloseGestureResult(CloseGestureOutcome.CancelClose, null, "unknown choice");
        }
    }
}
