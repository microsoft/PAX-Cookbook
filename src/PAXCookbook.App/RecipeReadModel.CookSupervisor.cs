using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Native manual-cook child invocation + supervisor (X16). This is the first and
// only slice that is allowed to spawn a child process, and the single sanctioned
// child is `pwsh` executing the externally-acquired, byte-verified MANAGED PAX
// engine `.ps1`. It preserves the entire X15 gate/preparation pipeline and adds:
//   * gate 10 — per-operation manualCook re-auth (production fails closed with
//     401 reAuthRequired unless a real browser-owned WebAuthn step-up granted a
//     single-use authorization for this recipe; the CLI-only test seam
//     authorizes automated smoke without consuming a real authorization),
//   * the supervisor — spawn the approved engine, stream stdout/stderr to
//     cook.log, write started/finished/interrupted sentinels, and transition the
//     cook row to its terminal state.
// It NEVER spawns any other pwsh, NEVER hosts a PowerShell broker/runtime, NEVER
// reads or mutates the managed engine bytes (other than re-hashing to verify).
//
// CK-3 adds bake-time credential injection for App-registration recipes: the
// supervisor reads the recipe's bound Chef's Key from Windows Credential Manager
// and places the GRAPH_* variables (tenant / client / certificate-thumbprint, and
// for the secret mode the client secret) on the spawned PAX engine's CHILD
// environment ONLY. The broker's own process environment is never written, the
// secret byte[] is zeroed the instant it is decoded, and GRAPH_CLIENT_SECRET is
// removed from the parent dictionary the moment the child has inherited the env
// block. No GRAPH_* value is ever written to cook.log, the sentinels, the cook
// row, or any response (constraint 14). All of that lives in CookCredentialInjection.
internal static partial class RecipeReadModel
{
    // Which kind of cook is being started. Both kinds share the SINGLE cook
    // pipeline (StartCookCore) — there is one execution path, not a second
    // channel (constraint 8). The ONLY behavioral difference is the
    // authorization step: a Manual cook enforces the per-operation Windows Hello
    // step-up (gate 10); a Scheduled cook waives gate 10 (the Brian-approved
    // constraint-10 modification, X7) and is instead authorized by an enabled
    // schedule plus the recipe's bound Chef's Key. The Scheduled kind is reachable
    // through TWO sanctioned entry points: the standalone `--run-scheduled-recipe`
    // one-shot (StartScheduledCook, joinSupervisor: true) and — in the V2
    // two-process model — the daemon's POST /api/v1/recipes/{id}/cook/scheduled
    // route via StartScheduledCookViaHttp (joinSupervisor: false), which the
    // --bake CLI calls. The HTTP route carries the SAME scheduled-auth gate
    // (enabled schedule required) plus Bearer + CSRF + the broker-lock gate, so it
    // is not a gate-10 bypass for manual cooks.
    private enum CookKind
    {
        Manual,
        Scheduled,
    }

    // Public manual-cook entry point (the manual Bake route
    // POST /api/v1/recipes/{id}/cook). A thin wrapper over the shared cook core
    // with CookKind.Manual: it enforces gate 10 (Windows Hello step-up) exactly
    // as before and returns the 201 immediately while a background supervisor
    // finalizes the cook (joinSupervisor: false). Its observable behavior is
    // byte-for-byte identical to the pre-refactor StartManualCook. Returns
    // (httpStatus, body):
    //
    //   no manualCook re-auth   : 401 reAuthRequired (no folder, no row, no spawn).
    //   App-reg recipe, no key  : bounded 412 recipe_invalid (chefKeyId required /
    //                             chefKeyNotFound / chefKeyModeMismatch /
    //                             chefKeySecretMissing) from gate 14 -- before any
    //                             folder, row, or spawn.
    //   spawn failure           : bounded 500; row -> interrupted/spawn_failed,
    //                             interrupted.json written, NO finished.json.
    //   spawn success           : 201 { cookId, recipeId, cookFolder }; a
    //                             background supervisor finalizes the cook.
    public static (int Status, object Body) StartManualCook(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        string recipeId,
        string method,
        string requestPath,
        long minFreeDiskBytes,
        bool reAuthVerified,
        string? pwshPathOverride)
        => StartCookCore(
            workspacePath, versionInfo, engine, recipeId, method, requestPath, minFreeDiskBytes,
            CookKind.Manual, reAuthVerified, pwshPathOverride, joinSupervisor: false);

    // Public scheduled-cook entry point. Reachable ONLY from the
    // `--run-scheduled-recipe` one-shot (Program.RunScheduledRecipeOneShot); NO
    // HTTP route maps to it. It runs the SAME cook pipeline as a manual cook
    // (constraint 8) but with CookKind.Scheduled, which (a) WAIVES gate 10 (the
    // approved constraint-10 modification — a scheduled run does NOT perform the
    // per-operation Windows Hello step-up) and instead runs a scheduled-auth gate
    // requiring the recipe to have an enabled schedule, and (b) joins the
    // supervisor (joinSupervisor: true) so the call returns only AFTER the cook
    // has fully finalized — the one-shot process must not exit and orphan the
    // child or cut off finalize. All OTHER cook gates (recipe saved, validation,
    // acquisition, busy, disk, path, Chef's Key resolution, SHA re-verify) remain
    // enforced. reAuthVerified is hard-wired false: there is no manual re-auth on
    // this path, and gate 10 is never consulted for a scheduled cook.
    public static (int Status, object Body) StartScheduledCook(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        string recipeId,
        long minFreeDiskBytes,
        string? pwshPathOverride)
        => StartCookCore(
            workspacePath, versionInfo, engine, recipeId,
            method: "SCHEDULED", requestPath: "--run-scheduled-recipe", minFreeDiskBytes,
            CookKind.Scheduled, reAuthVerified: false, pwshPathOverride, joinSupervisor: true);

    // Public HTTP scheduled-cook entry point (V2 two-process). Reachable from the
    // daemon's POST /api/v1/recipes/{id}/cook/scheduled route, which the --bake
    // CLI (Windows Task Scheduler → daemon delegation) calls. It runs the SAME
    // CookKind.Scheduled pipeline as the --run-scheduled-recipe one-shot — gate 10
    // (Windows Hello step-up) is WAIVED and REPLACED by the scheduled-auth gate
    // (the recipe must have an ENABLED schedule, created while the app was
    // unlocked and Hello-verified); EVERY other gate stays enforced (recipe
    // saved/validation incl. QueryShapeGate/DateRangeGate, acquisition, busy,
    // disk, path, Chef's Key resolution, SHA re-verify). The ONLY difference from
    // StartScheduledCook is joinSupervisor: FALSE — unlike the standalone one-shot
    // (which must block so its process does not exit and orphan the child), the
    // long-lived daemon returns the prompt 201 { cookId, recipeId, cookFolder }
    // immediately while its background supervisor finalizes the cook, and --bake
    // tracks completion by polling GET /api/v1/cooks/{cookId}. Bearer, CSRF, and
    // the broker-lock gate (423 when Locked) are all enforced upstream by the
    // daemon's middleware, exactly as for the manual route. reAuthVerified is
    // hard-wired false: gate 10 is never consulted for a scheduled cook. SECURITY:
    // because the scheduled-auth gate requires an enabled schedule, this route is
    // NOT a universal gate-10 bypass — only recipes the user already authorized
    // for scheduling can run unattended; an unscheduled recipe is refused 409
    // recipe_not_scheduled before any folder, row, or child exists.
    public static (int Status, object Body) StartScheduledCookViaHttp(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        string recipeId,
        string method,
        string requestPath,
        long minFreeDiskBytes,
        string? pwshPathOverride)
        => StartCookCore(
            workspacePath, versionInfo, engine, recipeId,
            method, requestPath, minFreeDiskBytes,
            CookKind.Scheduled, reAuthVerified: false, pwshPathOverride, joinSupervisor: false);

    // The single cook pipeline shared by the manual and scheduled entry points
    // (constraint 8 — there is literally one execution path). Runs the X15
    // read-only gate chain (gates 1..9), the kind-specific AUTHORIZATION step,
    // the X15 pre-spawn preparation (gates 11..18, which resolves the bound
    // Chef's Key for App-registration recipes and returns a bounded, secret-free
    // error when it is unbound / missing / secret-less), the immediate
    // managed-engine re-hash, and finally the child spawn + supervision with
    // child-only GRAPH_* credential injection.
    private static (int Status, object Body) StartCookCore(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        string recipeId,
        string method,
        string requestPath,
        long minFreeDiskBytes,
        CookKind kind,
        bool reAuthVerified,
        string? pwshPathOverride,
        bool joinSupervisor)
    {
        // Gates 1..9 — recipe / acquisition / busy (read-only). Identical for both
        // kinds.
        CookGateOutcome gate = EvaluateCookGatesThroughBusy(
            workspacePath, engine, recipeId, method, requestPath);
        if (!gate.Passed)
        {
            return (gate.Status, gate.Body!);
        }
        Dictionary<string, object?> recipe = gate.Recipe!;

        // Authorization step — the ONLY place the pipeline branches on kind.
        if (kind == CookKind.Manual)
        {
            // Gate 10 — per-operation fresh manualCook re-auth (OpClass
            // 'manualCook'). UNCHANGED from the pre-refactor manual path.
            // Production consumes a single-use, recipe-bound, lock-generation-bound
            // in-memory authorization minted by a real browser-owned WebAuthn
            // step-up (POST /api/v1/broker/reauth/manual-cook/verify). The CLI-only
            // test seam (--test-seam-manual-cook-reauth-verified) authorizes
            // automated smoke without consuming a real authorization. A
            // non-verified state is NEVER coerced into success: the cook fails
            // closed with 401 reAuthRequired before any folder, row, or child
            // exists. This gate applies to MANUAL cooks ONLY.
            bool reAuthOk;
            if (reAuthVerified)
            {
                reAuthOk = true;
            }
            else
            {
                (bool consumed, _) = ManualCookReAuth.TryConsume(recipeId, BrokerLock.CurrentLockGeneration);
                reAuthOk = consumed;
            }

            if (!reAuthOk)
            {
                return (401, new
                {
                    code = "reAuthRequired",
                    error = "reAuthRequired",
                    opClass = "manualCook",
                    verificationResult = "Required",
                    message = "A fresh Windows Hello verification is required before starting a manual cook.",
                });
            }
        }
        else
        {
            // Scheduled-auth gate — the approved constraint-10 modification (X7).
            // A scheduled cook does NOT perform the per-operation Windows Hello
            // step-up (gate 10 is skipped entirely). It is authorized instead by
            // the recipe having an ENABLED schedule (created while the app was
            // unlocked and Hello-verified) plus its bound Chef's Key as the run
            // identity. This gate FAILS CLOSED: if the recipe has no schedule, or
            // the schedule is disabled, the scheduled run is refused with a
            // bounded, secret-free 409 BEFORE any folder, row, or child is
            // created. It reads ONLY the non-secret schedule.Enabled flag
            // (projected from the already-loaded, already-validated recipe tree)
            // — never a secret (constraint 14).
            ScheduleInfo? schedule = ProjectSchedule(recipe);
            if (schedule is null || !schedule.Enabled)
            {
                return (409, new
                {
                    error = "recipe_not_scheduled",
                    recipeId,
                    message = "This recipe has no enabled schedule; a scheduled run is not authorized.",
                });
            }
        }

        // CK-3: the bounded App-registration 501 boundary is gone. App-registration
        // recipes now flow into the standard preparation pipeline, where gate 14
        // (ResolveChefKeyForProjection) resolves the recipe's bound Chef's Key from
        // Windows Credential Manager and returns a bounded, secret-free error
        // (chefKeyId required / chefKeyNotFound / chefKeyModeMismatch /
        // chefKeySecretMissing) BEFORE any folder, row, or spawn when the binding
        // is absent or unusable. A usable binding is carried into PreparedCook and
        // injected as child-only GRAPH_* credentials by the supervisor at spawn.

        // Gates 11..18 — disk / path / exec-mode / Chef's Key / plan / folder /
        // files / row (the proven X15 preparation). The kind is threaded so the
        // execution-mode gate (gate 13) and the recorded cook trigger differ
        // between manual and scheduled; the projected PAX invocation plan is
        // identical for both kinds.
        (int? prepStatus, object? prepBody, PreparedCook prepared) = PrepareCookArtifacts(
            workspacePath, versionInfo, engine, recipe, recipeId, minFreeDiskBytes, kind);
        if (prepStatus.HasValue)
        {
            return (prepStatus.Value, prepBody!);
        }

        // The cook row now exists with status='running', pid/started_at NULL.
        // Everything below transitions it to a real spawn or a spawn failure. A
        // scheduled one-shot joins the supervisor so this call returns only after
        // the cook has fully finalized.
        (int spawnStatus, object spawnBody) = SpawnAndSupervise(
            workspacePath, versionInfo, engine, prepared, recipeId, pwshPathOverride, joinSupervisor);

        // For a scheduled one-shot the supervisor was joined, so the cook row is
        // already terminal. Surface the terminal status + trigger so the
        // `--run-scheduled-recipe` one-shot can map the outcome to a process exit
        // code. The MANUAL path returns the spawn body unchanged (byte-for-byte
        // identical 201 { cookId, recipeId, cookFolder }). The HTTP scheduled path
        // (StartScheduledCookViaHttp, joinSupervisor: false) ALSO returns the
        // prompt spawn body unchanged — the daemon's background supervisor is
        // still finalizing, so there is no terminal status yet; --bake tracks
        // completion by polling GET /api/v1/cooks/{cookId}. Only the joined
        // one-shot reads the terminal status here.
        if (kind == CookKind.Scheduled && spawnStatus == 201 && joinSupervisor)
        {
            string terminalStatus = ReadTerminalCookStatus(workspacePath, prepared.CookId);
            return (201, new
            {
                cookId = prepared.CookId,
                recipeId,
                cookFolder = prepared.CookFolderRel,
                status = terminalStatus,
                trigger = "scheduled",
            });
        }

        return (spawnStatus, spawnBody);
    }

    // Reads the cook row's terminal status after the supervisor has been joined.
    // joinSupervisor=true guarantees FinalizeCook (terminal row write) has already
    // completed before this is called, so the first read normally sees the
    // terminal state; the small bounded retry only absorbs a rare SQLite
    // read-after-write visibility lag. Never spawns, never reads a secret; returns
    // "unknown" only if the row truly cannot be read.
    private static string ReadTerminalCookStatus(string workspacePath, string cookId)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            string? status = ReadCookRow(workspacePath, cookId)?.Status;
            if (!string.IsNullOrEmpty(status) &&
                !string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return status;
            }
            Thread.Sleep(25);
        }
        return ReadCookRow(workspacePath, cookId)?.Status ?? "unknown";
    }

    // Re-hashes the managed engine immediately before spawn, launches the single
    // sanctioned pwsh child, and either (a) records the running pid + started_at
    // and hands off to a background finalizer, or (b) records a spawn failure.
    //
    // joinSupervisor controls whether the caller waits for the supervisor:
    //   false (manual cook) — start the supervisor on its background thread and
    //          return 201 immediately. The manual Bake route gets a prompt 201
    //          while the cook finalizes in the background. UNCHANGED behavior.
    //   true  (scheduled one-shot) — after starting the supervisor thread, Join()
    //          it so this call does NOT return until FinalizeCook has fully
    //          completed (terminal row + finished.json + outputs + notify). The
    //          `--run-scheduled-recipe` one-shot must block here; otherwise the
    //          process would exit and orphan the pwsh child / cut off finalize.
    //          The supervisor thread itself is unchanged; only the caller waits.
    private static (int Status, object Body) SpawnAndSupervise(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        PreparedCook prepared,
        string recipeId,
        string? pwshPathOverride,
        bool joinSupervisor)
    {
        string cookId = prepared.CookId;
        string cookFolderAbs = prepared.CookFolderAbs;
        string cookFolderRel = prepared.CookFolderRel;
        string host = Environment.MachineName;
        string? pwshPath = ResolvePwshPath(pwshPathOverride);
        if (pwshPath is null)
        {
            // PowerShell 7 is required to run a bake but was not found on this
            // machine (the prerequisite installer normally places it under
            // %ProgramFiles%\PowerShell\7). Fail with a clear, actionable message
            // instead of an opaque "cannot find the file specified" OS error.
            return RecordSpawnFailure(
                workspacePath, cookFolderAbs, cookFolderRel, recipeId, cookId, host,
                "PowerShell 7 is required to run bakes. Please reinstall PAX Cookbook to install it.");
        }
        string commandExpr = prepared.Plan.SpawnArgv[3];
        string paxScriptPath = prepared.Plan.PaxScriptPath;

        // Re-hash the managed engine bytes right now. Acquisition verified them a
        // moment ago, but the engine is invoked ONLY when its current bytes still
        // match the recorded approved hash. A miss is a spawn failure (the row was
        // already inserted), not a silent skip.
        string? currentHash = TryComputeManagedEngineSha256(engine.ManagedEnginePath);
        if (currentHash is null ||
            !string.Equals(currentHash, engine.RecordedSha256, StringComparison.OrdinalIgnoreCase))
        {
            string mismatchReason = currentHash is null
                ? "managed engine is not readable at spawn time"
                : "managed engine hash does not match the recorded approved hash at spawn time";
            return RecordSpawnFailure(
                workspacePath, cookFolderAbs, cookFolderRel, recipeId, cookId, host, mismatchReason);
        }

        var psi = new ProcessStartInfo
        {
            FileName = pwshPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            // WebLogin recipes perform interactive MSAL/WAM browser sign-in,
            // which requires a parent window handle: a headless child
            // (CreateNoWindow=true) makes MSAL fail with "A window handle must be
            // configured" and PAX self-exits without authenticating. So a
            // WebLogin cook allocates a console window (CreateNoWindow=false);
            // every other / unbound mode (DeviceCode, App-registration
            // cert/secret, unknown) stays headless. UseShellExecute stays false
            // so stdout/stderr redirection and child-only GRAPH_* injection keep
            // working; redirection still tees the child's stdout to cook.log even
            // when the console window is shown.
            CreateNoWindow = !prepared.RequiresInteractiveWindow,
            // WindowStyle=Hidden expresses the intent that the allocated console
            // start hidden, but .NET only honors it on the ShellExecuteEx path
            // (UseShellExecute=true); with UseShellExecute=false the console is
            // still created visible. The supervisor closes that gap below by
            // hiding the child's console window after Start (the HWND stays valid
            // so MSAL can still parent to it). Headless cooks keep the default.
            WindowStyle = prepared.RequiresInteractiveWindow
                ? ProcessWindowStyle.Hidden
                : ProcessWindowStyle.Normal,
            WorkingDirectory = cookFolderAbs,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(commandExpr);

        // CK-3 — child-only Graph credential injection. For App-registration
        // recipes the bound Chef's Key's non-secret fields (and, for the secret
        // mode, the client secret read from WCM here and immediately zeroed inside
        // the helper) are placed on the CHILD process environment ONLY:
        // psi.Environment is a per-child copy because UseShellExecute=false, so the
        // broker's own process environment is never written. Interactive / unbound
        // recipes inject nothing. A secret-mode recipe whose secret is no longer
        // readable fails the spawn here (bounded, secret-free) rather than launch a
        // child that cannot authenticate. GRAPH_CLIENT_SECRET is scrubbed from this
        // parent dictionary the moment the child has inherited the env block (after
        // proc.Start) and again on the spawn-failure path (constraint 14).
        CookCredentialInjection.CredentialInjectionOutcome credOutcome =
            CookCredentialInjection.ApplyChildCredentialEnv(psi.Environment, prepared.CredentialInjection);
        if (credOutcome == CookCredentialInjection.CredentialInjectionOutcome.SecretMissingAtSpawn)
        {
            return RecordSpawnFailure(
                workspacePath, cookFolderAbs, cookFolderRel, recipeId, cookId, host,
                "bound Chef's Key client secret was unavailable at spawn time");
        }

        // cook.log — UTF-8 no-BOM, append, auto-flush. stdout/stderr arrive on
        // separate threads, so every append is serialized through a lock.
        string cookLogPath = Path.Combine(cookFolderAbs, "cook.log");
        StreamWriter? logWriter = null;
        var logLock = new object();
        Process? proc = null;
        try
        {
            var logStream = new FileStream(cookLogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            logWriter = new StreamWriter(logStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };

            // Redacted invocation header, written BEFORE the child begins
            // streaming so cook.log is never empty even when the engine emits
            // no stdout. Only non-secret provenance is recorded here: the
            // command summary is redacted and no auth material exists in the
            // argv that reaches this point (constraint 14).
            string headerUtc = ToCookIso(DateTime.UtcNow);
            logWriter.WriteLine("=== PAX Cookbook managed cook ===");
            logWriter.WriteLine("startedUtc: " + headerUtc);
            logWriter.WriteLine("cookId: " + cookId);
            logWriter.WriteLine("recipeId: " + recipeId);
            logWriter.WriteLine("pwsh: " + pwshPath);
            logWriter.WriteLine("paxScript: " + paxScriptPath);
            logWriter.WriteLine("paxVersion: " + versionInfo.PaxVersion);
            logWriter.WriteLine("paxSha256: " + currentHash);
            logWriter.WriteLine("command (redacted): pwsh -NoProfile -NoLogo -Command <managed PAX engine; arguments redacted>");
            logWriter.WriteLine("=================================");

            proc = new Process { StartInfo = psi };
            StreamWriter writerRef = logWriter;

            // CK-4 — real-time Device Code sign-in relay guard. Fires at most once
            // per cook even if PAX re-prints the prompt while polling.
            int deviceCodeRelayed = 0;

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) { return; }
                lock (logLock) { writerRef.WriteLine(e.Data); }

                // CK-4 — detect PAX's Device Code prompt and relay the sign-in URL
                // + code to the user's configured Telegram chat in real time. The
                // relay is queued to the thread pool so it NEVER blocks the
                // stdout -> cook.log tee, is swallow-all, and fires at most once.
                // The one-time sign-in code is delivered ONLY to the chat; cook.log
                // records a redacted note that carries neither the URL nor the code
                // (constraint 14).
                if (TelegramNotifier.TryParseDeviceCodePrompt(e.Data, out string devUrl, out string devCode))
                {
                    if (Interlocked.Exchange(ref deviceCodeRelayed, 1) == 0)
                    {
                        lock (logLock)
                        {
                            writerRef.WriteLine("[notify] device sign-in prompt detected; relaying to the configured Telegram chat");
                        }
                        string urlCopy = devUrl;
                        string codeCopy = devCode;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                TelegramNotifier.RelayDeviceCode(TelegramNotifier.DefaultSender, urlCopy, codeCopy);
                            }
                            catch { /* relay is best-effort; never affects the cook */ }
                        });
                    }
                }
                else if (TelegramNotifier.TryParseDeviceCodeExpiry(e.Data))
                {
                    // Best-effort: relay only if PAX's stdout actually announces an
                    // expiry. No wall-clock timer is fabricated (it would risk false
                    // positives). If PAX emits no expiry line this never fires.
                    lock (logLock)
                    {
                        writerRef.WriteLine("[notify] device sign-in expiry detected; relaying to the configured Telegram chat");
                    }
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            TelegramNotifier.RelayDeviceCodeExpiry(TelegramNotifier.DefaultSender, null);
                        }
                        catch { /* best-effort */ }
                    });
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) { return; }
                lock (logLock) { writerRef.WriteLine("[STDERR] " + e.Data); }
            };

            proc.Start();
            // The child has now inherited the environment block. Drop
            // GRAPH_CLIENT_SECRET from this parent dictionary immediately to
            // minimize the secret string's lifetime (the child's copy is
            // unaffected). .NET string immutability prevents true zeroing, so
            // prompt removal is the realistic mitigation (constraint 14).
            CookCredentialInjection.ScrubSecretEnv(psi.Environment);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // BUG-1 window-hide. A WebLogin cook allocates a visible console
            // (CreateNoWindow=false) so MSAL/WAM has a parent HWND, but the user
            // must never see a terminal. WindowStyle=Hidden above does not hide it
            // under UseShellExecute=false, so hide the child's console window here
            // via a bounded, best-effort, thread-pool ShowWindow(SW_HIDE). The
            // HWND stays valid (hidden != closed) so MSAL can still parent to it.
            // It never blocks this hot path, never throws, and is a no-op for the
            // headless modes. A guaranteed no-window result is confirmed by the
            // operator's real re-test (a console app's window handle can resolve
            // late or be owned by a conhost host).
            if (prepared.RequiresInteractiveWindow)
            {
                CookConsoleWindow.QueueHideChildConsole(proc);
            }
        }
        catch (Exception ex)
        {
            // Ensure no injected secret lingers in the parent dictionary if the
            // spawn threw before/at Start.
            CookCredentialInjection.ScrubSecretEnv(psi.Environment);
            try { logWriter?.Dispose(); } catch { /* best-effort */ }
            try { proc?.Dispose(); } catch { /* best-effort */ }
            return RecordSpawnFailure(
                workspacePath, cookFolderAbs, cookFolderRel, recipeId, cookId, host, ex.Message);
        }

        // Spawn succeeded. Record the running pid + started_at and write the
        // started sentinel, then hand the live child to a background supervisor.
        int pid = proc.Id;
        DateTime startedUtc = DateTime.UtcNow;
        string startedAt = ToCookIso(startedUtc);

        try
        {
            UpdateCookStarted(workspacePath, cookId, pid, startedAt);
        }
        catch
        {
            // The child is already running; never kill the sanctioned bake just
            // because the index write hiccuped. The finalizer still records the
            // terminal state, and the sentinels remain the source of truth.
        }

        WriteStartedSentinel(
            cookFolderAbs, cookId, recipeId, pid, startedAt, pwshPath, commandExpr, paxScriptPath, host);

        Process superviseProc = proc;
        StreamWriter superviseWriter = logWriter!;
        object superviseLock = logLock;

        // X6 — register the live child with the cancellation registry BEFORE the
        // supervisor thread starts, so a near-instant Stop request can find and
        // kill the supervised process tree. This is the SAME live Process handed
        // to FinalizeCook; it is the only trustworthy kill target for this cook
        // (a stored pid is untrustworthy after a restart due to PID reuse).
        // FinalizeCook unregisters it in its finally.
        CookCancellation.Register(cookId, superviseProc, startedUtc);

        var supervisor = new Thread(() => FinalizeCook(
            workspacePath, cookFolderAbs, cookId, superviseProc, superviseWriter, superviseLock,
            startedUtc, startedAt))
        {
            IsBackground = true,
            Name = "pax-cook-supervisor-" + cookId,
        };
        supervisor.Start();

        // Scheduled one-shot: block until the supervisor has fully finalized the
        // cook (terminal row + finished.json + outputs + notify). This keeps the
        // `--run-scheduled-recipe` process alive for the whole cook so it never
        // exits and orphans the pwsh child or cuts off finalize. The manual Bake
        // route passes joinSupervisor=false and returns the 201 immediately while
        // the cook finalizes on the background supervisor thread (UNCHANGED).
        if (joinSupervisor)
        {
            supervisor.Join();
        }

        return (201, new
        {
            cookId,
            recipeId,
            cookFolder = cookFolderRel,
        });
    }

    // Background supervisor body: wait for the child, flush + close cook.log,
    // write finished.json, and transition the cook row to its terminal state.
    private static void FinalizeCook(
        string workspacePath,
        string cookFolderAbs,
        string cookId,
        Process proc,
        StreamWriter logWriter,
        object logLock,
        DateTime startedUtc,
        string startedAt)
    {
        try
        {
            int exitCode;
            try
            {
                // No-timeout WaitForExit also drains the async stdout/stderr readers.
                proc.WaitForExit();
                exitCode = proc.ExitCode;
            }
            catch
            {
                exitCode = -1;
            }

            DateTime finishedUtc = DateTime.UtcNow;
            string finishedAt = ToCookIso(finishedUtc);
            double durationSec = Math.Round((finishedUtc - startedUtc).TotalSeconds, 3);

            try
            {
                lock (logLock) { logWriter.Flush(); }
            }
            catch { /* best-effort */ }
            try { logWriter.Dispose(); } catch { /* best-effort */ }
            try { proc.Dispose(); } catch { /* best-effort */ }

            WriteFinishedSentinel(cookFolderAbs, exitCode, finishedAt, durationSec);

            // X6 — a user-initiated cancel killed the supervised tree, so the
            // observed exit code is a kill code, not a PAX failure. Record a clean
            // "canceled" terminal state (closureReason "user_canceled"), never
            // "errored". cook.log, the sentinels, and any partial outputs are all
            // preserved (nothing is deleted). The flag is read after WaitForExit,
            // so a cook that finished naturally an instant before the cancel
            // arrived still records its real completed/errored outcome.
            bool canceled = CookCancellation.IsCancelRequested(cookId);
            string status;
            string? errorClass;
            string closureReason;
            if (canceled)
            {
                status = "canceled";
                errorClass = null;
                closureReason = "user_canceled";
            }
            else
            {
                bool clean = exitCode == 0;
                status = clean ? "completed" : "errored";
                errorClass = clean ? null : "nonzero_exit";
                closureReason = clean ? "clean_exit" : "nonzero_exit";
            }

            try
            {
                UpdateCookTerminal(
                    workspacePath, cookId, status, exitCode, finishedAt, durationSec,
                    errorClass, errorMessage: null, closureReason);
            }
            catch { /* the sentinels remain authoritative if the index write fails */ }

            // Record discovered output destinations (path / size / existence only,
            // never contents) now that the cook has reached a terminal state.
            DiscoverAndRecordOutputs(workspacePath, cookFolderAbs);

            // CK-4 — fire the completion / failure Telegram notification AFTER the
            // terminal state and the output metadata are written. The notify path is
            // swallow-all and runs last: it can never throw into this supervisor and
            // never blocks or fails the bake (the cook row is already terminal and the
            // 201 was returned to the caller at spawn). When notifications are off or
            // unconfigured it is a no-op. The payload is metadata-only and the bot
            // token never leaves Windows Credential Manager. A canceled bake flows
            // through this same swallow-all path.
            TryNotifyBakeTerminal(
                workspacePath, cookFolderAbs, cookId, status, exitCode, durationSec, errorClass, closureReason);
        }
        finally
        {
            // X6 — always release the cancellation handle once the supervisor has
            // finished, whether the cook completed, errored, was canceled, or the
            // supervisor threw. The live Process was already disposed above; the
            // registry only held it so the Stop route could reach the live child.
            CookCancellation.Unregister(cookId);
        }
    }

    // CK-4 — builds the metadata-only bake notification from the authoritative
    // cook row + the discovered output metadata and sends it through the
    // swallow-all notifier. NEVER throws (a notification can never block or fail a
    // bake). Records a secret-free notify-status.json for diagnostics (no token,
    // no chat id, no message text).
    private static void TryNotifyBakeTerminal(
        string workspacePath,
        string cookFolderAbs,
        string cookId,
        string status,
        int exitCode,
        double durationSec,
        string? errorClass,
        string? closureReason)
    {
        TelegramNotifier.NotifyOutcome outcome = TelegramNotifier.NotifyOutcome.SendFailed;
        bool attempted = false;
        try
        {
            // Recipe name + trigger from the authoritative cook row. The recipe
            // name (identity.name) is user-chosen, not tenant data; trigger is
            // currently always "manual" (X7 will set "scheduled" for Task
            // Scheduler runs, at which point the completion message annotates it).
            string? recipeName = null;
            string trigger = "manual";
            CookRow? row = ReadCookRow(workspacePath, cookId);
            if (row is not null)
            {
                recipeName = ExtractRecipeName(row.Value.RecipeSnapshotJson);
                if (!string.IsNullOrEmpty(row.Value.Trigger)) { trigger = row.Value.Trigger!; }
            }

            // Output destination metadata — path + SIZE (bytes) ONLY, from the
            // outputs.json just written. The file CONTENTS (tenant rows) are never
            // read or included (constraint 1). A true record/row count would
            // require reading the tenant data, so it is intentionally omitted.
            (string? outPath, long? outSize) = ReadPrimaryOutputMetadata(cookFolderAbs);

            bool success = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
            string failureReason = errorClass ?? closureReason ?? "nonzero_exit";

            var metadata = new TelegramNotifier.BakeNotificationMetadata(
                RecipeName: recipeName,
                Status: success ? "completed" : "failed",
                ExitCode: exitCode,
                DurationSeconds: durationSec,
                Trigger: trigger,
                OutputPath: outPath,
                OutputSizeBytes: outSize,
                FailureReason: success ? null : failureReason);

            attempted = true;
            outcome = TelegramNotifier.NotifyBakeTerminal(TelegramNotifier.DefaultSender, metadata, success);
        }
        catch
        {
            // Swallow everything — the terminal state was already written.
        }

        WriteNotifyStatus(cookFolderAbs, attempted, outcome);
    }

    // Reads the fact (else first available) output destination's path + size from
    // outputs.json. Returns (null, null) on any absence. Never reads file
    // contents.
    private static (string? Path, long? SizeBytes) ReadPrimaryOutputMetadata(string cookFolderAbs)
    {
        try
        {
            if (ReadCookFolderJson(cookFolderAbs, "outputs.json") is not Dictionary<string, object?> doc)
            {
                return (null, null);
            }
            if (!(doc.TryGetValue("outputs", out object? oRaw) && oRaw is List<object?> outs))
            {
                return (null, null);
            }

            Dictionary<string, object?>? fact = null;
            Dictionary<string, object?>? any = null;
            foreach (object? item in outs)
            {
                if (item is not Dictionary<string, object?> o) { continue; }
                string? path = o.TryGetValue("path", out object? pp) && pp is not null ? JsonModel.Str(pp) : null;
                if (string.IsNullOrEmpty(path)) { continue; }
                any ??= o;
                string role = o.TryGetValue("role", out object? rr) ? JsonModel.Str(rr) : string.Empty;
                if (string.Equals(role, "fact", StringComparison.OrdinalIgnoreCase))
                {
                    fact = o;
                    break;
                }
            }

            Dictionary<string, object?>? chosen = fact ?? any;
            if (chosen is null) { return (null, null); }

            string? cp = chosen.TryGetValue("path", out object? cpp) && cpp is not null ? JsonModel.Str(cpp) : null;
            long? size = chosen.TryGetValue("sizeBytes", out object? ss) && ss is long sl ? sl : (long?)null;
            return (string.IsNullOrEmpty(cp) ? null : cp, size);
        }
        catch
        {
            return (null, null);
        }
    }

    // Records a secret-free record of the notification attempt. NEVER contains the
    // token, chat id, or message text — only whether a send was attempted and a
    // coarse outcome (advisory; never blocks finalization).
    private static void WriteNotifyStatus(
        string cookFolderAbs, bool attempted, TelegramNotifier.NotifyOutcome outcome)
    {
        try
        {
            var doc = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1L,
                ["attempted"] = attempted,
                ["outcome"] = outcome.ToString(),
                ["at"] = ToCookIso(DateTime.UtcNow),
            };
            WriteAtomicUtf8NoBom(
                Path.Combine(cookFolderAbs, "notify-status.json"),
                JsonModel.SerializeToUtf8Bytes(doc, indented: true));
        }
        catch
        {
            // notify-status.json is advisory; never block finalization.
        }
    }

    // Spawn-failure path: write interrupted.json (NO finished.json), transition
    // the row to interrupted/spawn_failed (duration 0), and return a bounded 500.
    private static (int Status, object Body) RecordSpawnFailure(
        string workspacePath,
        string cookFolderAbs,
        string cookFolderRel,
        string recipeId,
        string cookId,
        string host,
        string spawnErr)
    {
        string interruptedAt = ToCookIso(DateTime.UtcNow);
        WriteInterruptedSentinel(cookFolderAbs, interruptedAt, host, "spawn_failed: " + spawnErr);

        try
        {
            UpdateCookTerminal(
                workspacePath, cookId, "interrupted", exitCode: null, finishedAt: interruptedAt,
                durationSeconds: 0, errorClass: "spawn_failed", errorMessage: spawnErr,
                closureReason: "spawn_failed");
        }
        catch { /* the interrupted sentinel remains authoritative */ }

        return (500, new
        {
            error = "cook_spawn_failed",
            recipeId,
            cookId,
            cookFolder = cookFolderRel,
            reason = "spawn_failed",
            detail = spawnErr,
        });
    }

    // pwsh resolution. The test seam may force an explicit interpreter path;
    // otherwise the full path to PowerShell 7 is resolved via PwshLocator (PATH
    // then the standard install locations), returning null when it is not found
    // so the caller surfaces a clear, actionable error rather than an opaque OS
    // spawn failure.
    private static string? ResolvePwshPath(string? pwshPathOverride)
    {
        if (!string.IsNullOrWhiteSpace(pwshPathOverride))
        {
            return pwshPathOverride;
        }
        return PwshLocator.Resolve();
    }

    private static string ToCookIso(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string? TryComputeManagedEngineSha256(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(fs);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteStartedSentinel(
        string cookFolderAbs,
        string cookId,
        string recipeId,
        int pid,
        string startedAt,
        string pwshPath,
        string commandExpr,
        string paxScriptPath,
        string host)
    {
        var started = new Dictionary<string, object?>
        {
            ["cookId"] = cookId,
            ["recipeId"] = recipeId,
            ["pid"] = pid,
            ["startedAt"] = startedAt,
            ["command"] = new List<object?> { pwshPath, "-NoProfile", "-NoLogo", "-Command", commandExpr },
            ["pwshPath"] = pwshPath,
            ["paxScriptPath"] = paxScriptPath,
            ["host"] = host,
        };
        WriteAtomicUtf8NoBom(
            Path.Combine(cookFolderAbs, "started.json"),
            JsonModel.SerializeToUtf8Bytes(started, indented: true));
    }

    private static void WriteFinishedSentinel(
        string cookFolderAbs, int exitCode, string finishedAt, double durationSec)
    {
        var finished = new Dictionary<string, object?>
        {
            ["exitCode"] = exitCode,
            ["finishedAt"] = finishedAt,
            ["durationSec"] = durationSec,
        };
        WriteAtomicUtf8NoBom(
            Path.Combine(cookFolderAbs, "finished.json"),
            JsonModel.SerializeToUtf8Bytes(finished, indented: true));
    }

    private static void WriteInterruptedSentinel(
        string cookFolderAbs, string interruptedAt, string host, string reason)
    {
        var interrupted = new Dictionary<string, object?>
        {
            ["interruptedAt"] = interruptedAt,
            ["lastKnownPhase"] = null,
            ["pid"] = null,
            ["host"] = host,
            ["reason"] = reason,
        };
        WriteAtomicUtf8NoBom(
            Path.Combine(cookFolderAbs, "interrupted.json"),
            JsonModel.SerializeToUtf8Bytes(interrupted, indented: true));
    }

    // X6 — Stop / cancel lifecycle decision behind POST /api/v1/cooks/{id}/stop
    // and POST /api/v1/cooks/{id}/kill (both routes map here; there is no
    // divergent second path). Decides, from the authoritative cook row plus the
    // in-process cancellation registry, whether the named cook can be canceled
    // and, when it can, requests a kill of the supervised process tree. It reads
    // the DB read-only, spawns nothing, reads no secret, and returns
    // metadata-only bodies (constraint 14). Identity safety: a kill only ever
    // goes through the LIVE Process held by THIS broker's registry
    // (CookCancellation), never a stored pid. Cancellation never requires the
    // engine to be acquired (a running cook already had a valid engine) — it is
    // lifecycle control, not execution.
    //
    // Refusal order (after the upstream 401/403/423 gates):
    //   * 404 cook_not_found     — malformed id, or no such cook row.
    //   * 409 cook_not_running   — the cook is already terminal (idempotent-
    //                              friendly: cancelling a finished bake is a clean
    //                              409, never a 500). Echoes the current status.
    //   * 202 canceling          — running and supervised here: the tree kill was
    //                              requested; the supervisor transitions the row
    //                              to canceled / user_canceled asynchronously.
    //   * 409 cook_not_supervised — running row but no live handle in THIS broker
    //                              (defensive boundary; startup reconciliation
    //                              already heals truly orphaned 'running' rows).
    //                              NOTHING is killed in this branch.
    internal static (int Status, object Body) RequestCookStop(string workspacePath, string cookId)
    {
        // A malformed id is "not found" from the stop surface's perspective.
        if (string.IsNullOrWhiteSpace(cookId) || !IsValidCookId(cookId))
        {
            return (404, new { error = "cook_not_found", message = "No such bake.", cookId });
        }

        CookRow? maybe = ReadCookRow(workspacePath, cookId);
        if (maybe is null)
        {
            return (404, new { error = "cook_not_found", message = "No such bake.", cookId });
        }

        string status = CookStatuses.Normalize(maybe.Value.Status);
        if (status != CookStatuses.Running)
        {
            return (409, new
            {
                error = "cook_not_running",
                message = "This bake is not running.",
                status,
            });
        }

        CookCancellation.CancelResult result = CookCancellation.RequestCancel(cookId);
        if (result.Outcome == CookCancellation.CancelOutcome.Requested)
        {
            // The supervisor will transition the row to canceled / user_canceled
            // once the killed tree's WaitForExit returns; the SPA poll observes it.
            return (202, new { cookId, status = "canceling" });
        }

        // Running row but not supervised by this broker — never kill by a stored
        // pid here.
        return (409, new
        {
            error = "cook_not_supervised",
            message = "This bake is not being supervised by the running app and cannot be stopped here.",
        });
    }

    private static void UpdateCookStarted(
        string workspacePath, string cookId, int pid, string startedAt)
    {
        using SqliteConnection conn = OpenCookReadWrite(workspacePath);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE cooks SET pid = $pid, started_at = $started_at, updated_at = $updated_at " +
            "WHERE cook_id = $cook_id;";
        BindParam(cmd, "$pid", pid);
        BindParam(cmd, "$started_at", startedAt);
        BindParam(cmd, "$updated_at", startedAt);
        BindParam(cmd, "$cook_id", cookId);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateCookTerminal(
        string workspacePath,
        string cookId,
        string status,
        int? exitCode,
        string finishedAt,
        double durationSeconds,
        string? errorClass,
        string? errorMessage,
        string closureReason)
    {
        using SqliteConnection conn = OpenCookReadWrite(workspacePath);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE cooks SET status = $status, exit_code = $exit_code, finished_at = $finished_at, " +
            "duration_seconds = $duration_seconds, error_class = $error_class, " +
            "error_message = $error_message, closure_reason = $closure_reason, " +
            "updated_at = $updated_at WHERE cook_id = $cook_id;";
        BindParam(cmd, "$status", status);
        BindParam(cmd, "$exit_code", exitCode.HasValue ? exitCode.Value : (object?)null);
        BindParam(cmd, "$finished_at", finishedAt);
        BindParam(cmd, "$duration_seconds", durationSeconds);
        BindParam(cmd, "$error_class", errorClass);
        BindParam(cmd, "$error_message", errorMessage);
        BindParam(cmd, "$closure_reason", closureReason);
        BindParam(cmd, "$updated_at", finishedAt);
        BindParam(cmd, "$cook_id", cookId);
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection OpenCookReadWrite(string workspacePath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile(workspacePath),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();
        return conn;
    }

    private static void BindParam(SqliteCommand cmd, string name, object? value)
    {
        SqliteParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
