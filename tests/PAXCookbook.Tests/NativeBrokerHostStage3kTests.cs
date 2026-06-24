using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PAXCookbook.Broker;
using PAXCookbook.Logging;
using PAXCookbook.WebView2;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3k -- native close prompt truthfulness tests.
//
// Stage 3j moved the broker in-process: PAXCookbook.exe owns Kestrel,
// and closing the only WinForms message loop terminates the broker
// with the process. The Phase 7 three-button close prompt
// ("Close app only" / "Close app and stop server" / "Cancel") was
// written for the split-process model and is no longer truthful:
// "Close app only" cannot keep the broker running after the window
// closes because there is no other message pump to host Kestrel.
//
// Stage 3k replaces the prompt with a two-button truthful prompt:
//   * Close PAX Cookbook  -- stops the native broker cleanly via
//                            NativeBrokerController.Stop, removes
//                            workspace.lock, then closes the window
//                            (and the process exits).
//   * Cancel              -- cancels FormClosing; window and broker
//                            stay running.
//
// This file enforces the new contract from three angles:
//   1. Enum shape: CloseChoice / CloseGestureOutcome no longer expose
//      the invalidated split-process values.
//   2. Win32CloseDialogService production source contains the exact
//      truthful labels, copy, and Cancel-default semantics, and does
//      not contain the legacy labels.
//   3. CloseGestureCoordinator behavior with a fake dialog and a fake
//      broker controller: Cancel does nothing; ClosePaxCookbook calls
//      NativeBrokerController.Stop exactly once and tolerates failure.
//   4. WebView2WinFormsHost / OpenCommand / Program composition wires
//      the new dialog service through unchanged seams and preserves
//      the silent-close bypass and recursion guard.
//   5. No close path reaches Start-Broker.ps1 or spawns visible pwsh.
//   6. PAX script source hash is unchanged.
public class NativeBrokerHostStage3kTests
{
    private const string PaxScriptBaselineHash =
        "5893B42807079CD8E321FE19C50C97188AD39A545BA7B90945657FDAE0BCE390";

    // ============================================================
    // A. CloseChoice / CloseGestureOutcome enum shape
    // ============================================================

    [Fact]
    public void T01_CloseChoice_has_exactly_Cancel_and_ClosePaxCookbook()
    {
        var names = Enum.GetNames(typeof(CloseChoice));
        Array.Sort(names);
        Assert.Equal(new[] { "Cancel", "ClosePaxCookbook" }, names);
    }

    [Fact]
    public void T02_CloseChoice_does_not_contain_legacy_CloseAppOnly()
    {
        Assert.False(Enum.IsDefined(typeof(CloseChoice), "CloseAppOnly"));
    }

    [Fact]
    public void T03_CloseChoice_does_not_contain_legacy_CloseAppAndStopServer()
    {
        Assert.False(Enum.IsDefined(typeof(CloseChoice), "CloseAppAndStopServer"));
    }

    [Fact]
    public void T04_CloseGestureOutcome_has_exactly_CancelClose_and_ClosedWithBrokerStopped()
    {
        var names = Enum.GetNames(typeof(CloseGestureOutcome));
        Array.Sort(names);
        Assert.Equal(new[] { "CancelClose", "ClosedWithBrokerStopped" }, names);
    }

    [Fact]
    public void T05_CloseGestureOutcome_does_not_contain_legacy_CloseUiOnly()
    {
        Assert.False(Enum.IsDefined(typeof(CloseGestureOutcome), "CloseUiOnly"));
    }

    [Fact]
    public void T06_CloseGestureOutcome_does_not_contain_legacy_CloseUiAndStoppedBroker()
    {
        Assert.False(Enum.IsDefined(typeof(CloseGestureOutcome), "CloseUiAndStoppedBroker"));
    }

    // ============================================================
    // B. Win32CloseDialogService production source
    // ============================================================

    [Fact]
    public void T07_Win32CloseDialog_source_contains_Close_PAX_Cookbook_button()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        Assert.Contains("Text = \"Close PAX Cookbook\"", src);
    }

    [Fact]
    public void T08_Win32CloseDialog_source_contains_Cancel_button()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        Assert.Contains("Text = \"Cancel\"", src);
    }

    [Fact]
    public void T09_Win32CloseDialog_source_does_not_contain_Close_app_only_label()
    {
        var src = StripCSharpComments(ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs"));
        Assert.DoesNotContain("\"Close app only\"", src);
    }

    [Fact]
    public void T10_Win32CloseDialog_source_does_not_contain_Close_app_and_stop_server_label()
    {
        var src = StripCSharpComments(ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs"));
        Assert.DoesNotContain("\"Close app and stop server\"", src);
    }

    [Fact]
    public void T11_Win32CloseDialog_source_contains_new_content_about_local_broker()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        Assert.Contains("PAX Cookbook will stop the local broker and close the app.", src);
    }

    [Fact]
    public void T12_Win32CloseDialog_source_contains_main_instruction()
    {
        // The header label inside the prompt Form must render the
        // "Close PAX Cookbook?" prompt verbatim.
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        Assert.Contains("Text = \"Close PAX Cookbook?\"", src);
    }

    [Fact]
    public void T13_Win32CloseDialog_source_contains_window_title()
    {
        // The prompt Form's window title must be "PAX Cookbook".
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        Assert.Contains("Text = \"PAX Cookbook\"", src);
    }

    [Fact]
    public void T14_Win32CloseDialog_source_defaults_to_Cancel()
    {
        // The prompt Form's Choice property must default to Cancel so
        // that X-click, owned-close, and any unexpected exit path all
        // resolve to Cancel and never silently close the app.
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        Assert.Contains("Choice { get; private set; } = CloseChoice.Cancel;", src);
    }

    [Fact]
    public void T15_Win32CloseDialog_source_allows_dialog_cancellation_for_X_and_Esc()
    {
        // Esc must dismiss the prompt as Cancel. WinForms wires Esc to
        // the form's CancelButton automatically.
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        Assert.Contains("CancelButton = btnCancel;", src);
    }

    [Fact]
    public void T16_Win32CloseDialog_source_falls_back_to_Cancel_on_HR_failure()
    {
        // No TaskDialogIndirect HRESULT path exists anymore. The
        // equivalent safety property is that the Cancel button handler
        // assigns Choice = CloseChoice.Cancel before closing the form.
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        Assert.Contains("Choice = CloseChoice.Cancel; Close();", src);
    }

    [Fact]
    public void T17_Win32CloseDialog_source_maps_button_id_to_ClosePaxCookbook()
    {
        // Clicking the Close PAX Cookbook button must assign
        // Choice = CloseChoice.ClosePaxCookbook before closing the form.
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        Assert.Contains("Choice = CloseChoice.ClosePaxCookbook; Close();", src);
    }

    // ============================================================
    // C. CloseGestureCoordinator behavior with fakes
    // ============================================================

    [Fact]
    public void T18_Cancel_choice_returns_CancelClose_and_does_not_stop_broker()
    {
        var dialog = new FixedChoiceCloseDialog(CloseChoice.Cancel);
        var broker = new FakeBrokerControllerForCloseTests
        {
            NextProbe = new BrokerStatus(true, 4321, 17654, "http://localhost:17654", "ws", "native-in-process")
        };
        var coord = new CloseGestureCoordinator(
            dialog, broker, () => "ws", TimeSpan.FromSeconds(2));

        var r = coord.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);

        Assert.Equal(CloseGestureOutcome.CancelClose, r.Outcome);
        Assert.Equal(1, dialog.Calls);
        Assert.Equal(0, broker.StopCalls);
        Assert.Equal(0, broker.ProbeCalls);
        Assert.Null(r.FailureDetail);
    }

    [Fact]
    public void T19_ClosePaxCookbook_choice_with_running_broker_stops_exactly_once()
    {
        var dialog = new FixedChoiceCloseDialog(CloseChoice.ClosePaxCookbook);
        var broker = new FakeBrokerControllerForCloseTests
        {
            NextProbe = new BrokerStatus(true, 4321, 17654, "http://localhost:17654", "ws", "native-in-process")
        };
        var coord = new CloseGestureCoordinator(
            dialog, broker, () => "ws", TimeSpan.FromSeconds(2));

        var r = coord.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);

        Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
        Assert.Equal(1, broker.StopCalls);
        Assert.Equal(4321, broker.LastStop!.BrokerPid);
        Assert.Equal(TimeSpan.FromSeconds(2), broker.LastStop!.ExitTimeout);
    }

    [Fact]
    public void T20_ClosePaxCookbook_choice_with_dormant_broker_is_idempotent()
    {
        var dialog = new FixedChoiceCloseDialog(CloseChoice.ClosePaxCookbook);
        var broker = new FakeBrokerControllerForCloseTests
        {
            NextProbe = new BrokerStatus(false, null, null, null, null, "none")
        };
        var coord = new CloseGestureCoordinator(
            dialog, broker, () => "ws", TimeSpan.FromSeconds(2));

        var r = coord.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);

        Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
        Assert.Equal(0, broker.StopCalls);
        Assert.Equal(BrokerStopOutcome.AlreadyStopped, r.BrokerStopOutcome);
    }

    [Fact]
    public void T21_ClosePaxCookbook_with_stop_failure_still_resolves_to_close_with_detail()
    {
        var dialog = new FixedChoiceCloseDialog(CloseChoice.ClosePaxCookbook);
        var broker = new FakeBrokerControllerForCloseTests
        {
            NextProbe = new BrokerStatus(true, 4321, 17654, "http://localhost:17654", "ws", "native-in-process"),
            NextStop  = new BrokerStopResult(BrokerStopOutcome.Failed, "native_broker_stop_timeout")
        };
        var coord = new CloseGestureCoordinator(
            dialog, broker, () => "ws", TimeSpan.FromMilliseconds(50));

        var r = coord.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);

        // Contract: the user is never trapped. Window proceeds to close
        // even if broker stop fails; the coordinator returns the failure
        // detail so the host can log it.
        Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
        Assert.Equal(BrokerStopOutcome.Failed, r.BrokerStopOutcome);
        Assert.Equal("native_broker_stop_timeout", r.FailureDetail);
    }

    [Fact]
    public void T22_ClosePaxCookbook_with_no_workspace_known_resolves_to_close()
    {
        var dialog = new FixedChoiceCloseDialog(CloseChoice.ClosePaxCookbook);
        var broker = new FakeBrokerControllerForCloseTests();
        var coord = new CloseGestureCoordinator(
            dialog, broker, () => null, TimeSpan.FromSeconds(2));

        var r = coord.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);

        Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
        Assert.Equal(0, broker.StopCalls);
        Assert.Equal(0, broker.ProbeCalls);
        Assert.Equal(BrokerStopOutcome.AlreadyStopped, r.BrokerStopOutcome);
    }

    [Fact]
    public void T23_Single_Handle_call_never_invokes_dialog_more_than_once()
    {
        var dialog = new FixedChoiceCloseDialog(CloseChoice.Cancel);
        var broker = new FakeBrokerControllerForCloseTests
        {
            NextProbe = new BrokerStatus(false, null, null, null, null, "none")
        };
        var coord = new CloseGestureCoordinator(
            dialog, broker, () => "ws", TimeSpan.FromSeconds(2));

        coord.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);

        Assert.Equal(1, dialog.Calls);
    }

    [Fact]
    public void T24_All_close_triggers_produce_same_decision_for_same_choice()
    {
        // The prompt does not vary by trigger source -- X, Alt+F4, system
        // menu, taskbar, WM_CLOSE, IPC stop all funnel through the same
        // dialog and the same outcome mapping.
        foreach (var trig in (CloseTrigger[])Enum.GetValues(typeof(CloseTrigger)))
        {
            var dialog = new FixedChoiceCloseDialog(CloseChoice.ClosePaxCookbook);
            var broker = new FakeBrokerControllerForCloseTests
            {
                NextProbe = new BrokerStatus(true, 4321, 17654, "http://localhost:17654", "ws", "native-in-process")
            };
            var coord = new CloseGestureCoordinator(
                dialog, broker, () => "ws", TimeSpan.FromSeconds(2));

            var r = coord.Handle(trig, IntPtr.Zero);

            Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
            Assert.Equal(1, broker.StopCalls);
        }
    }

    // ============================================================
    // D. WebView2WinFormsHost.cs source -- close handler invariants
    // ============================================================

    [Fact]
    public void T25_Form_handler_preserves_silent_close_bypass()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/WebView2WinFormsHost.cs");
        Assert.Contains("if (_silentClose || _req.CloseCoordinator is null) return;", src);
    }

    [Fact]
    public void T26_Form_handler_preserves_recursion_guard()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/WebView2WinFormsHost.cs");
        Assert.Contains("if (_dialogOpen) { e.Cancel = true; return; }", src);
    }

    [Fact]
    public void T27_Form_handler_cancels_close_on_Cancel_choice_and_kicks_off_async_shutdown_on_ClosePaxCookbook()
    {
        // Stage 5 AB: the handler no longer routes the full close
        // through CloseGestureCoordinator.Handle. PromptForChoice
        // runs synchronously on the UI thread; on Cancel we set
        // e.Cancel=true to keep the window; on ClosePaxCookbook we
        // set e.Cancel=true (so the form does NOT close yet) and
        // hand off to the async shutdown which calls Close()
        // programmatically with _silentClose=true once the broker
        // stop completes (or the watchdog fires).
        var src = ReadProductionSource("src/PAXCookbook/WebView2/WebView2WinFormsHost.cs");
        Assert.Contains("if (prompt.Choice == CloseChoice.Cancel)", src);
        Assert.Contains("_shutdownInProgress = true;", src);
        Assert.Contains("BeginAsyncShutdown(", src);
    }

    [Fact]
    public void T28_Form_handler_does_not_reference_legacy_CloseUiOnly()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/WebView2WinFormsHost.cs");
        Assert.DoesNotContain("CloseUiOnly", src);
    }

    // ============================================================
    // E. No close path reaches Start-Broker.ps1 or spawns pwsh
    // ============================================================

    [Fact]
    public void T29_CloseDialog_source_has_no_Start_Broker_reference()
    {
        var src = StripCSharpComments(ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs"));
        Assert.DoesNotContain("Start-Broker", src, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwsh.exe", src, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T30_CloseGestureCoordinator_source_has_no_Start_Broker_reference()
    {
        var src = StripCSharpComments(ReadProductionSource("src/PAXCookbook/WebView2/CloseGestureCoordinator.cs"));
        Assert.DoesNotContain("Start-Broker", src, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwsh.exe", src, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T31_CloseGestureCoordinator_source_has_no_Process_Start_or_Process_Kill()
    {
        var src = StripCSharpComments(ReadProductionSource("src/PAXCookbook/WebView2/CloseGestureCoordinator.cs"));
        Assert.DoesNotContain("Process.Start", src);
        Assert.DoesNotContain("Process.Kill", src);
    }

    [Fact]
    public void T32_WebView2_host_source_has_no_pwsh_or_Start_Broker_reference()
    {
        var src = StripCSharpComments(ReadProductionSource("src/PAXCookbook/WebView2/WebView2WinFormsHost.cs"));
        Assert.DoesNotContain("Start-Broker", src, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwsh.exe", src, StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================
    // F. Composition still wires Win32CloseDialogService through the
    //    Stage 3j NativeBrokerController seam
    // ============================================================

    [Fact]
    public void T33_Program_cs_injects_Win32CloseDialogService_into_CommandContext()
    {
        var src = ReadProductionSource("src/PAXCookbook/Program.cs");
        Assert.Contains("closeDialog: new Win32CloseDialogService()", src);
    }

    [Fact]
    public void T34_OpenCommand_constructs_CloseGestureCoordinator_with_native_broker()
    {
        var src = ReadProductionSource("src/PAXCookbook/Commands/OpenCommand.cs");
        Assert.Contains("new CloseGestureCoordinator(", src);
        Assert.Contains("ctx.Broker", src);
    }

    [Fact]
    public void T35_OpenCommand_threads_CloseCoordinator_into_UiHostLaunchRequest()
    {
        var src = ReadProductionSource("src/PAXCookbook/Commands/OpenCommand.cs");
        Assert.Contains("CloseCoordinator: coord", src);
    }

    [Fact]
    public void T36_CloseGestureCoordinator_uses_broker_Stop_with_BrokerStopOptions()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/CloseGestureCoordinator.cs");
        Assert.Contains("_broker.Stop(new BrokerStopOptions(", src);
    }

    [Fact]
    public void T37_PrimaryVerbHandler_still_routes_Stop_verb_through_NativeBrokerController()
    {
        // The IPC Stop verb path (used by `paxcookbook stop` against a
        // primary that is already running PAX Cookbook) must continue to
        // dispatch through the same NativeBrokerController.Stop the
        // close prompt uses. No parallel kill path.
        var src = ReadProductionSource("src/PAXCookbook/Commands/OpenCommand.cs");
        Assert.Contains("_broker.Stop(new BrokerStopOptions(", src);
    }

    // ============================================================
    // G. PAX baseline tripwire
    // ============================================================

    [Fact]
    public void T38_PAX_script_source_hash_unchanged_after_stage3k()
    {
        var repoRoot = FindRepoRoot();
        var paxScript = Path.Combine(repoRoot, "app", "resources", "pax", "PAX_Purview_Audit_Log_Processor.ps1");
        Assert.True(File.Exists(paxScript), "PAX script not found: " + paxScript);
        using var s = File.OpenRead(paxScript);
        var hash = Convert.ToHexString(SHA256.HashData(s));
        Assert.Equal(PaxScriptBaselineHash, hash);
    }

    // ============================================================
    // H. Stage 5 H retest3 product repair -- TaskDialogIndirect was
    //    proven unreliable in the staged install run: it returned S_OK
    //    within ~10 ms without painting a UI, the default button
    //    (Cancel) was reported back, and the user was left with an
    //    app that ignored every X-click. The fix abandons
    //    TaskDialogIndirect entirely and renders the prompt as an
    //    owned modal WinForms Form with two ordinary push buttons.
    //    Two exception boundaries from the retest2 repair remain in
    //    place so any future regression still surfaces in the log
    //    and the window NEVER closes silently.
    // ============================================================

    [Fact]
    public void T39_Win32CloseDialog_source_uses_owned_WinForms_ShowDialog()
    {
        var src = StripCSharpComments(ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs"));
        // The prompt must be a modal Form shown via ShowDialog so the
        // call blocks until the user picks a button.
        Assert.Contains("form.ShowDialog(", src);
        Assert.Contains("private sealed class ClosePromptForm : Form", src);
    }

    [Fact]
    public void T40_Win32CloseDialog_source_declares_Close_and_Cancel_buttons()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        // Two literal button instances with the visible labels and the
        // Choice-assignment click handlers.
        Assert.Contains("Text = \"Close PAX Cookbook\"", src);
        Assert.Contains("Text = \"Cancel\"", src);
        Assert.Contains("AcceptButton = btnClose;", src);
        Assert.Contains("CancelButton = btnCancel;", src);
    }

    [Fact]
    public void T41_Win32CloseDialog_source_has_no_TaskDialog_PInvoke_residue()
    {
        var src = StripCSharpComments(ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs"));
        // Regression guard: the broken comctl32 TaskDialogIndirect path
        // must never come back into this file. All references to the
        // legacy native types and flags must stay out of production
        // source forever.
        Assert.DoesNotContain("TaskDialogIndirect", src);
        Assert.DoesNotContain("TASKDIALOGCONFIG", src);
        Assert.DoesNotContain("TASKDIALOG_BUTTON", src);
        Assert.DoesNotContain("comctl32", src);
        Assert.DoesNotContain("Pack = 1", src);
        Assert.DoesNotContain("Pack = 8", src);
        Assert.DoesNotContain("TDF_", src);
        Assert.DoesNotContain("pszMainInstruction", src);
        Assert.DoesNotContain("pszWindowTitle", src);
        Assert.DoesNotContain("nDefaultButton", src);
        Assert.DoesNotContain("cbSize", src);
    }

    [Fact]
    public void T42_CloseGestureCoordinator_source_wraps_dialog_Prompt_in_try_catch()
    {
        var src = StripCSharpComments(ReadProductionSource("src/PAXCookbook/WebView2/CloseGestureCoordinator.cs"));
        Assert.Contains("choice = _dialog.Prompt(ownerHwnd);", src);
        Assert.Contains("catch (Exception ex)", src);
    }

    [Fact]
    public void T43_CloseGestureCoordinator_source_logs_close_prompt_exception_event()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/CloseGestureCoordinator.cs");
        Assert.Contains("\"close-prompt-exception\"", src);
        Assert.Contains("\"error\"", src);
        Assert.Contains("ex.ToString()", src);
    }

    [Fact]
    public void T44_CloseGestureCoordinator_exposes_Log_for_host_handler()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/CloseGestureCoordinator.cs");
        Assert.Contains("public AppLogger? Log =>", src);
    }

    [Fact]
    public void T45_WebView2WinFormsHost_source_wraps_CloseCoordinator_PromptForChoice_in_try_catch()
    {
        // Stage 5 AB: handler now calls PromptForChoice (UI-thread
        // safe modal) and RunBrokerStop (background-thread safe)
        // separately so the broker stop never deadlocks the UI
        // thread on a Kestrel drain that depends on WebView2
        // sockets closing. The Try/Catch around PromptForChoice is
        // a belt-and-suspenders guard; PromptForChoice itself
        // already catches dialog exceptions and returns Cancel.
        var src = StripCSharpComments(ReadProductionSource("src/PAXCookbook/WebView2/WebView2WinFormsHost.cs"));
        Assert.Contains("prompt = _req.CloseCoordinator.PromptForChoice(trigger, Handle);", src);
        Assert.Contains("catch (Exception ex)", src);
        Assert.Contains("coord.RunBrokerStop(", src);
    }

    [Fact]
    public void T46_WebView2WinFormsHost_source_logs_close_prompt_handler_exception_event()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/WebView2WinFormsHost.cs");
        Assert.Contains("\"close-prompt-handler-exception\"", src);
        Assert.Contains("_req.CloseCoordinator.Log?.Write(", src);
    }

    [Fact]
    public void T47_WebView2WinFormsHost_handler_catch_sets_eCancel_true_to_keep_window()
    {
        var src = StripCSharpComments(ReadProductionSource("src/PAXCookbook/WebView2/WebView2WinFormsHost.cs"));
        var idx = src.IndexOf("close-prompt-handler-exception", StringComparison.Ordinal);
        Assert.True(idx > 0, "close-prompt-handler-exception not found in WebView2WinFormsHost source");
        var windowStart = Math.Max(0, idx - 400);
        var windowEnd = Math.Min(src.Length, idx + 400);
        var window = src.Substring(windowStart, windowEnd - windowStart);
        Assert.Contains("catch (Exception ex)", window);
        Assert.Contains("e.Cancel = true;", window);
    }

    [Fact]
    public void T48_ThrowingDialog_returns_CancelClose_and_does_not_invoke_broker()
    {
        var dialog = new ThrowingCloseDialog();
        var broker = new FakeBrokerControllerForCloseTests
        {
            NextProbe = new BrokerStatus(true, 1234, 17654, "http://localhost:17654", "ws", "workspace-lock")
        };
        var coord = new CloseGestureCoordinator(
            dialog, broker, () => "ws", TimeSpan.FromSeconds(2));

        var r = coord.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);

        Assert.Equal(CloseGestureOutcome.CancelClose, r.Outcome);
        Assert.Equal("close-prompt-exception", r.FailureDetail);
        Assert.Equal(1, dialog.Calls);
        Assert.Equal(0, broker.ProbeCalls);
        Assert.Equal(0, broker.StopCalls);
    }

    [Fact]
    public void T49_ThrowingDialog_writes_close_prompt_exception_event_to_AppLogger()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PAX5H_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new AppLogger(dir);
            var dialog = new ThrowingCloseDialog
            {
                Ex = new System.Runtime.InteropServices.MarshalDirectiveException("simulated marshalling fault")
            };
            var broker = new FakeBrokerControllerForCloseTests
            {
                NextProbe = new BrokerStatus(true, 1234, 17654, "http://localhost:17654", "ws", "workspace-lock")
            };
            var coord = new CloseGestureCoordinator(
                dialog, broker, () => "ws", TimeSpan.FromSeconds(2), log);

            coord.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);

            var logFile = log.CurrentLogFile;
            Assert.True(File.Exists(logFile), "AppLogger log file not written: " + logFile);
            var lines = File.ReadAllLines(logFile);
            Assert.Contains(lines, l => l.Contains("\"event\":\"close-prompt-open\""));
            var exceptionLine = lines.FirstOrDefault(l => l.Contains("\"event\":\"close-prompt-exception\""));
            Assert.NotNull(exceptionLine);
            Assert.Contains("\"level\":\"error\"", exceptionLine!);
            Assert.Contains("MarshalDirectiveException", exceptionLine!);
            Assert.Contains("simulated marshalling fault", exceptionLine!);
            Assert.DoesNotContain(lines, l => l.Contains("\"event\":\"close-prompt-result\""));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void T50_Log_property_returns_same_logger_passed_to_constructor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PAX5H_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new AppLogger(dir);
            var dialog = new FixedChoiceCloseDialog(CloseChoice.Cancel);
            var broker = new FakeBrokerControllerForCloseTests();
            var coord = new CloseGestureCoordinator(
                dialog, broker, () => "ws", TimeSpan.FromSeconds(2), log);
            Assert.Same(log, coord.Log);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void T51_Log_property_is_null_when_no_logger_provided()
    {
        var dialog = new FixedChoiceCloseDialog(CloseChoice.Cancel);
        var broker = new FakeBrokerControllerForCloseTests();
        var coord = new CloseGestureCoordinator(
            dialog, broker, () => "ws", TimeSpan.FromSeconds(2));
        Assert.Null(coord.Log);
    }

    [Fact]
    public void T52_Win32CloseDialog_source_imports_WinForms_namespace()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        // The new implementation depends on System.Windows.Forms for
        // the modal Form and IWin32Window owner.
        Assert.Contains("using System.Windows.Forms;", src);
        Assert.Contains(": ICloseDialogService", src);
    }

    [Fact]
    public void T53_Win32CloseDialog_owner_wraps_native_hwnd_as_IWin32Window()
    {
        var src = ReadProductionSource("src/PAXCookbook/WebView2/Win32CloseDialogService.cs");
        // The native owner HWND coming in from WebView2WinFormsHost.Handle
        // must be wrapped as IWin32Window so ShowDialog can parent the
        // prompt against the WebView2 host window.
        Assert.Contains("IWin32Window", src);
        Assert.Contains("new OwnerWindow(ownerHwnd)", src);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private sealed class FakeBrokerControllerForCloseTests : IBrokerController
    {
        public BrokerStatus NextProbe { get; set; } = new(false, null, null, null, null, "none");
        public BrokerStopResult NextStop { get; set; } = new(BrokerStopOutcome.Stopped, null);
        public int ProbeCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int StartCalls { get; private set; }
        public BrokerStopOptions? LastStop { get; private set; }

        public BrokerStatus Probe(string workspaceFolderPath)
        {
            ProbeCalls++;
            return NextProbe;
        }

        public BrokerStartResult Start(BrokerStartOptions options)
        {
            StartCalls++;
            return new BrokerStartResult(BrokerStartOutcome.Started, NextProbe, null);
        }

        public BrokerStopResult Stop(BrokerStopOptions options)
        {
            StopCalls++;
            LastStop = options;
            return NextStop;
        }
    }

    // Stage 5 H -- fake dialog that throws on Prompt to exercise the
    // CloseGestureCoordinator try/catch boundary.
    private sealed class ThrowingCloseDialog : ICloseDialogService
    {
        public Exception Ex { get; set; } = new InvalidOperationException("simulated prompt failure");
        public int Calls { get; private set; }
        public CloseChoice Prompt(IntPtr ownerHwnd)
        {
            Calls++;
            throw Ex;
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int i = 0;
        while (true)
        {
            int j = haystack.IndexOf(needle, i, StringComparison.Ordinal);
            if (j < 0) break;
            count++;
            i = j + needle.Length;
        }
        return count;
    }

    private static string ReadProductionSource(string repoRelativePath)
    {
        var repoRoot = FindRepoRoot();
        var abs = Path.Combine(repoRoot, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(abs), "Production source not found: " + abs);
        return File.ReadAllText(abs);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate PAXCookbook.sln above " + AppContext.BaseDirectory);
    }

    private static string StripCSharpComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0;
        while (i < src.Length)
        {
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
            }
            else if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                if (i + 1 < src.Length) i += 2;
            }
            else
            {
                sb.Append(src[i]);
                i++;
            }
        }
        return sb.ToString();
    }
}
