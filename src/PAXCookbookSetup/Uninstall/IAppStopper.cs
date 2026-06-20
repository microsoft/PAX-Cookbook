using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using PAXCookbook.Shared;

namespace PAXCookbookSetup.Uninstall;

// Stops the running PAX Cookbook app before uninstall deletes files.
//
// The native broker has no command-line "stop" verb; it is stopped the same
// way the in-app "Exit" stops it — an authenticated loopback HTTP shutdown:
//   POST http://127.0.0.1:<port>/api/v1/shutdown
//        Authorization: Bearer <token>   X-Cookbook-Request: 1
// The port is read from <workspace>\Runtime\workspace.lock and the session
// token from <workspace>\Runtime\broker.token — the files the broker itself
// writes (the workspace path comes from install-state, else <installRoot>\
// Workspace). The broker answers 200 immediately and then stops on a background
// task, so a clean stop is observed by waiting for the process to exit, not in
// the HTTP response. If the shutdown cannot be performed (no port/token, a
// connection refused, broker locked → 423, or timeout) the stopper falls back
// to terminating every process that has loaded this install's binaries — the
// background broker/tray daemon AND any open window instance — including their
// child processes (e.g. the WebView2 host). Termination is scoped to processes
// with a module under <installRoot>\App\bin; there is no broad process killing.
public interface IAppStopper
{
    AppStopResult TryStop(string installRoot, int timeoutMs);
}

public sealed record AppStopResult(
    bool Invoked,
    bool ExeFound,
    bool Exited,
    int? ExitCode,
    string? Detail);

public sealed class RealAppStopper : IAppStopper
{
    public AppStopResult TryStop(string installRoot, int timeoutMs)
    {
        var appExe = Path.Combine(installRoot, "App", "bin", ProductConstants.AppExeName);

        // Identify the broker process(es) launched from THIS install's app exe.
        // Nothing running means there is nothing to stop — uninstall proceeds.
        var procs = FindAppProcesses(installRoot, appExe);
        if (procs.Count == 0)
            return new AppStopResult(Invoked: false, ExeFound: File.Exists(appExe),
                Exited: true, ExitCode: null,
                Detail: "no running app process; nothing to stop");

        try
        {
            // 1. Ask the broker to shut itself down gracefully over loopback.
            //    Only the broker daemon answers /shutdown; a separately-launched
            //    window instance does not, so the graceful stop covers the
            //    broker and the terminate step below covers any window still
            //    holding a lock on App\bin.
            string detail = TryHttpShutdown(installRoot, timeoutMs);

            // 2. Give the process(es) a short grace to exit on their own. A 200
            //    from /shutdown only schedules the broker's stop, so the exit is
            //    the authoritative signal.
            int graceMs = Math.Min(timeoutMs, 5_000);
            if (WaitForAllExit(procs, graceMs))
                return new AppStopResult(true, true, true, 0, detail);

            // 3. Terminate any process(es) still running (the window instance,
            //    or a broker that did not exit), including child processes such
            //    as the WebView2 host. This is what frees the App\bin file locks
            //    so a reinstall/upgrade can overwrite them.
            KillAll(procs);
            if (WaitForAllExit(procs, timeoutMs))
                return new AppStopResult(true, true, true, 0,
                    detail + "; terminated after shutdown did not exit");

            return new AppStopResult(true, true, false, null,
                detail + "; process still running after shutdown and terminate");
        }
        finally
        {
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
    }

    // POST /api/v1/shutdown with Bearer + CSRF on the loopback interface.
    // Returns a short diagnostic string and never throws.
    private static string TryHttpShutdown(string installRoot, int timeoutMs)
    {
        try
        {
            // The broker records its loopback port INSIDE workspace.lock (there
            // is no separate broker.port file); the session token sits next to
            // it at <workspace>\Runtime\broker.token.
            var (_, lockPort) = ReadBrokerLock(installRoot);
            if (lockPort is not int port || port < 1 || port > 65535)
                return "no broker port in workspace.lock";

            string tokenFile = Path.Combine(ResolveWorkspace(installRoot), "Runtime", "broker.token");
            if (!File.Exists(tokenFile)) return "no broker.token";
            string token = File.ReadAllText(tokenFile).Trim();
            if (token.Length == 0) return "broker.token empty";

            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseDefaultCredentials = false,
                // No proxy: the Bearer token must never transit a proxy even if
                // the user has a system/PAC proxy that fails to bypass loopback.
                UseProxy = false,
            };
            using var http = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs)),
            };
            // 127.0.0.1 (never localhost) so the request stays on loopback.
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"http://127.0.0.1:{port}/api/v1/shutdown");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("X-Cookbook-Request", "1");
            using HttpResponseMessage resp = http.SendAsync(req).GetAwaiter().GetResult();
            return $"shutdown http {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return "shutdown http error: " + ex.Message;
        }
    }

    // Find EVERY running process that belongs to THIS install. A process is
    // ours when it has loaded a module from <installRoot>\App\bin — which is
    // true for:
    //   * the Microsoft-signed dotnet.exe host (it loads PAX Cookbook.dll from
    //     App\bin, the WDAC production launch shape), and
    //   * the unsigned apphost PAX Cookbook.exe (its main module is App\bin),
    // and crucially catches BOTH the background broker/tray daemon AND a
    // separately-launched window instance. Each loads — and therefore locks —
    // App\bin\PAX Cookbook.dll, so missing the window is exactly what left a
    // file locked and failed a reinstall/upgrade with exit 50.
    //
    // Candidates are narrowed by process name first (dotnet + the apphost name)
    // so we never enumerate the modules of unrelated processes. The broker PID
    // recorded in workspace.lock is added as a supplement (trusted when its
    // recorded port is reachable) so the broker is still stopped even if its
    // module list cannot be read. A process whose modules cannot be read is
    // never assumed to be ours.
    private static List<Process> FindAppProcesses(string installRoot, string appExeFull)
    {
        var matches = new List<Process>();
        var seen = new HashSet<int>();

        string binPrefix;
        try
        {
            binPrefix = Path.GetFullPath(Path.Combine(installRoot, "App", "bin"))
                        + Path.DirectorySeparatorChar;
        }
        catch { binPrefix = Path.Combine(installRoot, "App", "bin") + Path.DirectorySeparatorChar; }

        // True when any loaded module of p lives under <installRoot>\App\bin.
        bool LoadsOurBinaries(Process p)
        {
            try
            {
                foreach (ProcessModule m in p.Modules)
                {
                    string? f;
                    try { f = m.FileName; } catch { f = null; }
                    if (f is null) continue;
                    string full;
                    try { full = Path.GetFullPath(f); } catch { full = f; }
                    if (full.StartsWith(binPrefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* module list unreadable (denied/exited) — not treated as ours */ }
            return false;
        }

        // Candidate process names: the dotnet.exe host (production under WDAC)
        // and the apphost exe (dev / non-WDAC). Both are scanned so neither the
        // broker daemon nor an open window is missed.
        var names = new List<string> { "dotnet" };
        try
        {
            string apphostName = Path.GetFileNameWithoutExtension(appExeFull);
            if (!string.IsNullOrEmpty(apphostName)
                && !names.Contains(apphostName, StringComparer.OrdinalIgnoreCase))
                names.Add(apphostName);
        }
        catch { }

        foreach (var nm in names)
        {
            Process[] candidates;
            try { candidates = Process.GetProcessesByName(nm); }
            catch { candidates = Array.Empty<Process>(); }
            foreach (var p in candidates)
            {
                bool keep = !seen.Contains(p.Id) && LoadsOurBinaries(p);
                if (keep && seen.Add(p.Id)) matches.Add(p);
                else { try { p.Dispose(); } catch { } }
            }
        }

        // Supplement: the broker PID recorded in workspace.lock. Trusted when
        // its loaded modules confirm it, or when its recorded port is currently
        // reachable (a recycled stale-lock pid would not have an open port).
        var (lockPid, lockPort) = ReadBrokerLock(installRoot);
        if (lockPid is int pid && pid > 0 && !seen.Contains(pid))
        {
            Process? p = null;
            try { p = Process.GetProcessById(pid); }
            catch { p = null; }
            if (p is not null)
            {
                bool keep = LoadsOurBinaries(p)
                            || (lockPort is int port && port > 0 && BrokerPortReachable(port));
                if (keep && seen.Add(p.Id)) matches.Add(p);
                else { try { p.Dispose(); } catch { } }
            }
        }

        return matches;
    }

    // Reads (brokerProcessId, brokerPort) from <workspace>\Runtime\workspace.lock.
    // Returns (null, null) on any absence or parse error — the lock is advisory
    // and never required to stop the app.
    private static (int? Pid, int? Port) ReadBrokerLock(string installRoot)
    {
        try
        {
            string lockPath = Path.Combine(ResolveWorkspace(installRoot), "Runtime", "workspace.lock");
            if (!File.Exists(lockPath)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(lockPath));
            var root = doc.RootElement;
            int? pid = root.TryGetProperty("brokerProcessId", out var pidEl)
                       && pidEl.TryGetInt32(out int pidVal) ? pidVal : (int?)null;
            int? port = root.TryGetProperty("brokerPort", out var portEl)
                        && portEl.TryGetInt32(out int portVal) ? portVal : (int?)null;
            return (pid, port);
        }
        catch { return (null, null); }
    }

    // Resolve the workspace folder for this install: the path recorded in
    // install-state when present, else the canonical <installRoot>\Workspace.
    private static string ResolveWorkspace(string installRoot)
    {
        try
        {
            string? ws = InstallStateStore.TryLoad(installRoot)?.WorkspaceFolderPath;
            if (!string.IsNullOrWhiteSpace(ws)) return ws!;
        }
        catch { }
        return Path.Combine(installRoot, ProductConstants.WorkspaceFolderName);
    }

    // True when something is accepting TCP connections on the loopback broker
    // port — a cheap liveness probe so a stale lock's recycled pid is not killed.
    private static bool BrokerPortReachable(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", port);
            return connect.Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch { return false; }
    }

    private static bool WaitForAllExit(IReadOnlyList<Process> procs, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        foreach (var p in procs)
        {
            int remaining = (int)Math.Max(0, timeoutMs - sw.ElapsedMilliseconds);
            try { p.WaitForExit(remaining); }
            catch { /* the process may already have exited */ }
        }
        foreach (var p in procs)
        {
            try { if (!p.HasExited) return false; }
            catch { /* an unreadable handle is treated as exited */ }
        }
        return true;
    }

    private static void KillAll(IReadOnlyList<Process> procs)
    {
        foreach (var p in procs)
        {
            // entireProcessTree: true also terminates child processes (e.g. the
            // WebView2 host) that could otherwise keep a file handle open.
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }
    }

    // Public probe: is ANY PAX Cookbook process for this install currently
    // running? Used by the apply-update wait-until-clear loop, which RE-SCANS
    // (rather than trusting a single snapshot) because module discovery can
    // transiently miss a process that is exiting or under load.
    public bool AnyAppProcesses(string installRoot)
    {
        var appExe = Path.Combine(installRoot, "App", "bin", ProductConstants.AppExeName);
        var procs = FindAppProcesses(installRoot, appExe);
        try { return procs.Count > 0; }
        finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }
    }

    // Public force-kill: terminate (process tree) every PAX Cookbook process for
    // this install, freshly discovered, and give them a moment to exit. Safe to
    // call repeatedly. Used by the apply-update wait-until-clear loop.
    public void KillAppProcesses(string installRoot)
    {
        var appExe = Path.Combine(installRoot, "App", "bin", ProductConstants.AppExeName);
        var procs = FindAppProcesses(installRoot, appExe);
        try
        {
            KillAll(procs);
            WaitForAllExit(procs, 3_000);
        }
        finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }
    }
}

// Test stopper: records invocations and returns a configured result.
public sealed class RecordingAppStopper : IAppStopper
{
    public List<(string InstallRoot, int TimeoutMs)> Calls { get; } = new();
    public AppStopResult NextResult { get; set; } =
        new AppStopResult(true, true, true, 0, null);

    public AppStopResult TryStop(string installRoot, int timeoutMs)
    {
        Calls.Add((installRoot, timeoutMs));
        return NextResult;
    }
}
