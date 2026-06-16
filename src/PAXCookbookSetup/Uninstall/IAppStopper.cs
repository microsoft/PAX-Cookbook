using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
        var procs = FindAppProcesses(appExe);
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

    // Find running processes whose main module is exactly this install's app
    // exe. Any process whose module path cannot be read (access denied) is
    // skipped, never assumed to be ours.
    private static List<Process> FindAppProcesses(string appExeFull)
    {
        var matches = new List<Process>();
        string name;
        try { name = Path.GetFileNameWithoutExtension(appExeFull); }
        catch { return matches; }
        if (string.IsNullOrEmpty(name)) return matches;

        Process[] candidates;
        try { candidates = Process.GetProcessesByName(name); }
        catch { return matches; }

        string target;
        try { target = Path.GetFullPath(appExeFull); }
        catch { target = appExeFull; }

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
            if (keep) matches.Add(p);
            else { try { p.Dispose(); } catch { } }
        }
        return matches;
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
