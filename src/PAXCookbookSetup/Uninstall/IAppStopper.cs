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
// The port is read from <installRoot>\broker.port and the session token from
// <installRoot>\Workspace\Runtime\broker.token — the files the broker itself
// writes. The broker answers 200 immediately and then stops on a background
// task, so a clean stop is observed by waiting for the process to exit, not in
// the HTTP response. If the shutdown cannot be performed (no port/token file,
// connection refused, broker locked → 423, or timeout) the stopper falls back
// to terminating the broker process(es) launched from this install's app exe.
// Process termination is scoped to the exact app exe path under <installRoot>;
// there is no arbitrary command line and no broad process killing.
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
            string detail = TryHttpShutdown(installRoot, timeoutMs);

            // 2. Wait for the process(es) to exit. A 200 from /shutdown only
            //    schedules the stop, so the exit is the authoritative signal.
            if (WaitForAllExit(procs, timeoutMs))
                return new AppStopResult(true, true, true, 0, detail);

            // 3. Fallback: terminate the broker process(es) directly. This is
            //    the last resort when the broker is locked, unreachable, or the
            //    port/token sidecars are absent.
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
            string portFile = Path.Combine(installRoot, "broker.port");
            if (!File.Exists(portFile)) return "no broker.port";
            if (!int.TryParse(File.ReadAllText(portFile).Trim(), out int port)
                || port < 1 || port > 65535) return "broker.port malformed";

            string tokenFile = Path.Combine(installRoot, "Workspace", "Runtime", "broker.token");
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

    // Find running processes that belong to THIS install. Two discovery paths,
    // because under corporate WDAC the app runs as the Microsoft-signed
    // dotnet.exe host (with PAX Cookbook.dll as its argument), not the apphost:
    //   1. Any process whose main module is exactly this install's apphost exe.
    //   2. The broker process recorded in <workspace>\Runtime\workspace.lock
    //      (brokerProcessId) — this catches the dotnet.exe host. To guard
    //      against a stale lock whose pid was recycled by an unrelated app, the
    //      recorded pid is only trusted when it is the apphost OR it is a
    //      dotnet.exe whose broker port is currently accepting connections.
    // Any process whose module path cannot be read (access denied) is skipped,
    // never assumed to be ours.
    private static List<Process> FindAppProcesses(string installRoot, string appExeFull)
    {
        var matches = new List<Process>();
        var seen = new HashSet<int>();

        string target;
        try { target = Path.GetFullPath(appExeFull); }
        catch { target = appExeFull; }

        // 1. Name-based match on the apphost exe (exact path).
        string name;
        try { name = Path.GetFileNameWithoutExtension(appExeFull); }
        catch { name = string.Empty; }
        if (!string.IsNullOrEmpty(name))
        {
            Process[] candidates;
            try { candidates = Process.GetProcessesByName(name); }
            catch { candidates = Array.Empty<Process>(); }
            foreach (var p in candidates)
            {
                bool keep = false;
                try
                {
                    string? modPath = p.MainModule?.FileName;
                    if (modPath is not null
                        && string.Equals(Path.GetFullPath(modPath), target,
                                         StringComparison.OrdinalIgnoreCase))
                        keep = true;
                }
                catch { keep = false; }
                if (keep && seen.Add(p.Id)) matches.Add(p);
                else { try { p.Dispose(); } catch { } }
            }
        }

        // 2. PID recorded in workspace.lock (catches the dotnet.exe host).
        var (lockPid, lockPort) = ReadBrokerLock(installRoot);
        if (lockPid is int pid && pid > 0 && !seen.Contains(pid))
        {
            Process? p = null;
            try { p = Process.GetProcessById(pid); }
            catch { p = null; }
            if (p is not null)
            {
                bool keep = false;
                try
                {
                    string? modPath = p.MainModule?.FileName;
                    if (modPath is not null)
                    {
                        bool isApphost = string.Equals(Path.GetFullPath(modPath), target,
                                                       StringComparison.OrdinalIgnoreCase);
                        bool isDotnetHost = string.Equals(
                            Path.GetFileName(modPath), "dotnet.exe",
                            StringComparison.OrdinalIgnoreCase);
                        // A recycled stale-lock pid would not have an open broker
                        // port, so a live dotnet host is only ours when its port
                        // is reachable.
                        keep = isApphost ||
                               (isDotnetHost && lockPort is int port && port > 0
                                && BrokerPortReachable(port));
                    }
                }
                catch { keep = false; }
                if (keep && seen.Add(p.Id)) matches.Add(p);
                else { try { p.Dispose(); } catch { } }
            }
        }

        return matches;
    }

    // Reads (brokerProcessId, brokerPort) from <workspace>\Runtime\workspace.lock.
    // The workspace path comes from install-state when recorded, else the
    // canonical <installRoot>\Workspace. Returns (null, null) on any absence or
    // parse error — the lock is advisory and never required to stop the app.
    private static (int? Pid, int? Port) ReadBrokerLock(string installRoot)
    {
        try
        {
            string? workspace = InstallStateStore.TryLoad(installRoot)?.WorkspaceFolderPath;
            if (string.IsNullOrWhiteSpace(workspace))
                workspace = Path.Combine(installRoot, ProductConstants.WorkspaceFolderName);
            string lockPath = Path.Combine(workspace, "Runtime", "workspace.lock");
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
            try { if (!p.HasExited) p.Kill(entireProcessTree: false); }
            catch { /* best effort */ }
        }
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
