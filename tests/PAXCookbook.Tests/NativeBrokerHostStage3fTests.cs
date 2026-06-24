using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3f parity tests for the native broker's cook-view live-tail
// WebSocket transport. Each test uses an isolated Stage3fWorkspaceFixture
// (temp directory with cooks table + Cooks/<cookId>/cook.log on disk).
// The real installed workspace and the real app/ directory are never
// touched; tests do NOT invoke the real PAX script.
//
// Tests share the "NativeBrokerHostPortBinding" xUnit collection with
// Stage 3a-3e so port-17654 binding is serialised.
//
// The 19 facts in this class cover:
//
//    1. Pre-upgrade gates (5 cases)
//    2. Wire-format contract (4 cases: no-backlog, raw-text, [STDERR]
//       prefix, multibyte UTF-8 reconstruction)
//    3. Terminal lifecycle (3 cases: succeeded / failed / canceled)
//    4. Multi-client + disconnect behavior (3 cases)
//    5. Stage 3c regression (1 case: GET /log still works)
//    6. Static + API surface untouched (1 case: API 404 still JSON)
//    7. PAX script tripwire (1 case)
//    8. Host shutdown with active socket (1 case)
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3fTests
{
    private const string PaxScriptBaselineHash =
        "5893B42807079CD8E321FE19C50C97188AD39A545BA7B90945657FDAE0BCE390";

    // Bounded receive timeout for live-tail polls. The tailer's poll
    // cadence is 250ms; 5s gives the test 20 poll cycles to observe a
    // newly appended line. Tightened beyond this risks flakes on slow
    // CI; relaxed beyond this risks hangs when the contract regresses.
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    // ------------------------------------------------------------
    //  1. Pre-upgrade gates
    // ------------------------------------------------------------

    [Fact]
    public async Task LogWs_returns_400_when_request_is_not_websocket_upgrade()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_returns_400_when_cook_id_is_malformed()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // Add the Upgrade headers so the gate fails on cook-id, not
            // on missing upgrade.
            var req = new HttpRequestMessage(HttpMethod.Get,
                "/api/v1/cooks/not-a-real-id/log/ws");
            req.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            req.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            req.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            req.Headers.TryAddWithoutValidation("Sec-WebSocket-Key",
                Convert.ToBase64String(new byte[16]));
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_returns_404_when_cook_id_not_in_database()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + Guid.NewGuid().ToString() + "/log/ws");
            var ex = await Assert.ThrowsAsync<WebSocketException>(
                () => ws.ConnectAsync(url, CancellationToken.None));
            Assert.Contains("404", ex.Message);
            ws.Dispose();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_returns_409_when_cook_is_terminal()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.SucceededCookId + "/log/ws");
            var ex = await Assert.ThrowsAsync<WebSocketException>(
                () => ws.ConnectAsync(url, CancellationToken.None));
            Assert.Contains("409", ex.Message);
            ws.Dispose();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_returns_409_when_cook_log_file_missing()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        // Delete the running cook's log to force the "cook.log missing"
        // gate. The cook row is still 'running'.
        File.Delete(Path.Combine(fx.WorkspaceFolderPath,
            "Cooks", fx.RunningCookId, "cook.log"));
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            var ex = await Assert.ThrowsAsync<WebSocketException>(
                () => ws.ConnectAsync(url, CancellationToken.None));
            Assert.Contains("409", ex.Message);
            ws.Dispose();
        }
        finally { await host.StopAsync(); }
    }

    // ------------------------------------------------------------
    //  2. Wire-format contract
    // ------------------------------------------------------------

    [Fact]
    public async Task LogWs_upgrade_succeeds_for_running_cook_with_existing_log()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            await ws.ConnectAsync(url, CancellationToken.None);
            Assert.Equal(WebSocketState.Open, ws.State);
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test-close", closeCts.Token);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_does_not_replay_existing_cook_log_content()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        // Seed log with content BEFORE the socket connects. None of
        // these bytes should arrive over the socket; the client
        // hydrates them via GET /api/v1/cooks/<id>/log instead.
        var logPath = Path.Combine(fx.WorkspaceFolderPath,
            "Cooks", fx.RunningCookId, "cook.log");
        await File.WriteAllTextAsync(logPath,
            "pre-connect line 1\r\npre-connect line 2\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            await ws.ConnectAsync(url, CancellationToken.None);

            // Park a Receive that will sit Idle until the server
            // sends a frame. If the server (incorrectly) replays the
            // pre-connect backlog, the receive will complete with
            // Text within the poll window. If the server is well-
            // behaved (no backlog), the receive will only complete
            // when WE initiate Close and the server echoes Close.
            //
            // We deliberately do NOT cancel this receive (cancelling
            // a pending ReceiveAsync aborts the WebSocket, after
            // which ClientWebSocket.CloseAsync throws "Invalid state
            // Aborted"); we observe completion via IsCompleted after
            // a Task.Delay.
            var buf = new byte[4096];
            var receiveTask = ws
                .ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
            await Task.Delay(750);

            if (receiveTask.IsCompleted)
            {
                // Something arrived before our close. Backlog regression
                // is the only legitimate cause.
                var result = await receiveTask;
                Assert.NotEqual(WebSocketMessageType.Text, result.MessageType);
            }

            // Half-close: send our Close frame. Server's observer
            // will see Close and run its terminal cleanup, sending
            // its own Close back. The parked receive then completes
            // (with MessageType.Close) and we can shut down cleanly.
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure, "test-close", closeCts.Token);
            try { await receiveTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_streams_appended_stdout_bytes_verbatim()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        var logPath = Path.Combine(fx.WorkspaceFolderPath,
            "Cooks", fx.RunningCookId, "cook.log");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            await ws.ConnectAsync(url, CancellationToken.None);

            // Append AFTER the socket is open so the bytes traverse the
            // socket (no backlog gate).
            const string payload = "hello-from-cook\r\n";
            using (var fs = new FileStream(logPath,
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                { AutoFlush = true })
            {
                sw.Write(payload);
            }

            var received = await ReceiveTextUntilContainsAsync(ws, payload);
            Assert.Contains(payload, received);

            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test-close", closeCts.Token);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_streams_appended_stderr_lines_with_STDERR_prefix()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        var logPath = Path.Combine(fx.WorkspaceFolderPath,
            "Cooks", fx.RunningCookId, "cook.log");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            await ws.ConnectAsync(url, CancellationToken.None);

            // CookLogWriter writes "[STDERR] " + line + "\r\n" for
            // stderr. We simulate that exact byte sequence so the
            // tailer should ship it verbatim.
            const string payload = "[STDERR] something-went-wrong\r\n";
            using (var fs = new FileStream(logPath,
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                { AutoFlush = true })
            {
                sw.Write(payload);
            }

            var received = await ReceiveTextUntilContainsAsync(ws, payload);
            Assert.Contains("[STDERR] something-went-wrong", received);

            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test-close", closeCts.Token);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_reconstructs_multibyte_utf8_runes_across_read_boundaries()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        var logPath = Path.Combine(fx.WorkspaceFolderPath,
            "Cooks", fx.RunningCookId, "cook.log");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            await ws.ConnectAsync(url, CancellationToken.None);

            // 4-byte emoji + 3-byte CJK + 2-byte accented char. The
            // tailer's UTF-8 Decoder must reassemble these across
            // any Read() boundary it picks.
            const string payload = "\uD83D\uDE80 multi-byte: 漢字 café\r\n"; // 🚀 漢字 café
            using (var fs = new FileStream(logPath,
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                { AutoFlush = true })
            {
                sw.Write(payload);
            }

            var received = await ReceiveTextUntilContainsAsync(ws, "café");
            Assert.Contains("\uD83D\uDE80", received);
            Assert.Contains("漢字", received);
            Assert.Contains("café", received);

            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test-close", closeCts.Token);
        }
        finally { await host.StopAsync(); }
    }

    // ------------------------------------------------------------
    //  3. Terminal lifecycle
    // ------------------------------------------------------------

    [Fact]
    public async Task LogWs_closes_with_NormalClosure_when_cook_status_becomes_succeeded()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            await ws.ConnectAsync(url, CancellationToken.None);

            // Flip status in the database. The tailer's 4-cycle
            // terminal check runs every ~1 second.
            await fx.UpdateCookStatusAsync(fx.RunningCookId, "succeeded");

            var result = await ReceiveUntilCloseAsync(ws);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_closes_with_NormalClosure_when_cook_status_becomes_failed()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            await ws.ConnectAsync(url, CancellationToken.None);

            await fx.UpdateCookStatusAsync(fx.RunningCookId, "failed");

            var result = await ReceiveUntilCloseAsync(ws);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_drains_final_appended_bytes_before_terminal_close()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        var logPath = Path.Combine(fx.WorkspaceFolderPath,
            "Cooks", fx.RunningCookId, "cook.log");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            await ws.ConnectAsync(url, CancellationToken.None);

            // Append a tail-of-life line, then immediately flip
            // status to canceled. The tailer must drain the residual
            // bytes before issuing the NormalClosure -- otherwise
            // the final line never reaches the SPA.
            const string tail = "FINAL-LINE-BEFORE-TERMINAL\r\n";
            using (var fs = new FileStream(logPath,
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                { AutoFlush = true })
            {
                sw.Write(tail);
            }
            await fx.UpdateCookStatusAsync(fx.RunningCookId, "canceled");

            // Receive everything until close. The tail line MUST
            // appear in the accumulated text frames.
            var (received, closeResult) = await ReceiveAllUntilCloseAsync(ws);
            Assert.Contains("FINAL-LINE-BEFORE-TERMINAL", received);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, closeResult.CloseStatus);
        }
        finally { await host.StopAsync(); }
    }

    // ------------------------------------------------------------
    //  4. Multi-client + disconnect behavior
    // ------------------------------------------------------------

    [Fact]
    public async Task LogWs_two_concurrent_clients_each_receive_appended_bytes()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        var logPath = Path.Combine(fx.WorkspaceFolderPath,
            "Cooks", fx.RunningCookId, "cook.log");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var wsA = new ClientWebSocket();
            using var wsB = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            await wsA.ConnectAsync(url, CancellationToken.None);
            await wsB.ConnectAsync(url, CancellationToken.None);

            const string payload = "multi-client-broadcast\r\n";
            using (var fs = new FileStream(logPath,
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                { AutoFlush = true })
            {
                sw.Write(payload);
            }

            var receivedA = await ReceiveTextUntilContainsAsync(wsA, payload);
            var receivedB = await ReceiveTextUntilContainsAsync(wsB, payload);
            Assert.Contains(payload, receivedA);
            Assert.Contains(payload, receivedB);

            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", closeCts.Token);
            await wsB.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", closeCts.Token);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_client_abort_does_not_affect_other_client()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        var logPath = Path.Combine(fx.WorkspaceFolderPath,
            "Cooks", fx.RunningCookId, "cook.log");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");

            using var survivor = new ClientWebSocket();
            await survivor.ConnectAsync(url, CancellationToken.None);

            // Abandon a second connection without a clean close so
            // the server-side tailer must observe the abort. The
            // first client must keep ticking.
            var disposable = new ClientWebSocket();
            await disposable.ConnectAsync(url, CancellationToken.None);
            disposable.Abort();
            disposable.Dispose();

            const string payload = "after-disconnect\r\n";
            using (var fs = new FileStream(logPath,
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                { AutoFlush = true })
            {
                sw.Write(payload);
            }

            var received = await ReceiveTextUntilContainsAsync(survivor, payload);
            Assert.Contains(payload, received);

            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await survivor.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", closeCts.Token);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task LogWs_host_shutdown_terminates_active_socket_loop()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var ws = new ClientWebSocket();
            var url = new Uri("ws://localhost:" + start.Port +
                "/api/v1/cooks/" + fx.RunningCookId + "/log/ws");
            await ws.ConnectAsync(url, CancellationToken.None);

            // Stop the host while the socket is open. The tailer
            // must observe ApplicationStopping and exit; StopAsync
            // must NOT hang waiting on the socket.
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StopAsync(stopCts.Token);
            Assert.True(stopCts.Token.IsCancellationRequested == false,
                "Host shutdown should complete within 10s even with active socket.");
        }
        finally { await host.DisposeAsync(); }
    }

    // ------------------------------------------------------------
    //  5. Surface regression -- Stage 3c + API 404 must still work
    // ------------------------------------------------------------

    [Fact]
    public async Task Stage_3c_GET_cook_log_route_still_returns_full_file_content()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        var logPath = Path.Combine(fx.WorkspaceFolderPath,
            "Cooks", fx.RunningCookId, "cook.log");
        await File.WriteAllTextAsync(logPath,
            "stage-3c-regression-payload\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/cooks/" + fx.RunningCookId + "/log");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("stage-3c-regression-payload", body);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Unknown_api_path_still_returns_json_404_after_websocket_middleware_wired()
    {
        await using var fx = await Stage3fWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/this/route/does/not/exist");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("not_found", body);
        }
        finally { await host.StopAsync(); }
    }

    // ------------------------------------------------------------
    //  6. PAX script tripwire
    // ------------------------------------------------------------

    [Fact]
    public void PAX_script_hash_unchanged_at_stage_3f()
    {
        // Repo root is two directories above the test assembly path
        // (bin/Debug/net8.0-windows -> tests/PAXCookbook.Tests).
        var asmDir = Path.GetDirectoryName(typeof(NativeBrokerHostStage3fTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        var paxPath = Path.Combine(repoRoot,
            "app", "resources", "pax", "PAX_Purview_Audit_Log_Processor.ps1");
        Assert.True(File.Exists(paxPath),
            "PAX script not found at expected path: " + paxPath);
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(paxPath);
        var hex = Convert.ToHexString(sha.ComputeHash(fs));
        Assert.Equal(PaxScriptBaselineHash, hex);
    }

    // ------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------

    private static async Task<string> ReceiveTextUntilContainsAsync(
        ClientWebSocket ws, string marker)
    {
        var sb = new StringBuilder();
        var buf = new byte[8192];
        using var cts = new CancellationTokenSource(ReceiveTimeout);
        while (!cts.Token.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException(
                    "Did not receive marker '" + marker + "' within " +
                    ReceiveTimeout.TotalSeconds + "s. Got: " + sb);
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new Xunit.Sdk.XunitException(
                    "Server closed before marker arrived. CloseStatus=" +
                    result.CloseStatus + " Got: " + sb);
            }
            sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
            if (sb.ToString().Contains(marker)) return sb.ToString();
        }
        throw new Xunit.Sdk.XunitException(
            "Did not receive marker '" + marker + "'. Got: " + sb);
    }

    private static async Task<WebSocketReceiveResult> ReceiveUntilCloseAsync(ClientWebSocket ws)
    {
        var buf = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = await ws.ReceiveAsync(buf, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) return result;
        }
    }

    private static async Task<(string text, WebSocketReceiveResult close)>
        ReceiveAllUntilCloseAsync(ClientWebSocket ws)
    {
        var sb = new StringBuilder();
        var buf = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = await ws.ReceiveAsync(buf, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) return (sb.ToString(), result);
            sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
        }
    }

    // ------------------------------------------------------------
    //  Stage 3f workspace fixture
    // ------------------------------------------------------------

    private sealed class Stage3fWorkspaceFixture : IAsyncDisposable
    {
        public string Root { get; }
        public string WorkspaceFolderPath { get; }
        public string DatabaseFilePath { get; }
        public string RunningCookId { get; }
        public string SucceededCookId { get; }
        public NativeBrokerHostOptions Options { get; }

        private Stage3fWorkspaceFixture(
            string root, string workspace, string database,
            string runningCookId, string succeededCookId,
            NativeBrokerHostOptions options)
        {
            Root = root;
            WorkspaceFolderPath = workspace;
            DatabaseFilePath = database;
            RunningCookId = runningCookId;
            SucceededCookId = succeededCookId;
            Options = options;
        }

        public static async Task<Stage3fWorkspaceFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3f_" + Guid.NewGuid().ToString("N"));
            var workspace   = Path.Combine(root, "Workspace");
            var databaseDir = Path.Combine(workspace, "Database");
            var databaseFile = Path.Combine(databaseDir, "cookbook.sqlite");
            var cooksDir    = Path.Combine(workspace, "Cooks");

            var runningCookId   = Guid.NewGuid().ToString("D");
            var succeededCookId = Guid.NewGuid().ToString("D");

            Directory.CreateDirectory(databaseDir);
            Directory.CreateDirectory(Path.Combine(cooksDir, runningCookId));
            Directory.CreateDirectory(Path.Combine(cooksDir, succeededCookId));

            // Running cook starts with an empty cook.log (parity
            // with the Stage 3e CookFolderService boot).
            File.WriteAllText(
                Path.Combine(cooksDir, runningCookId, "cook.log"),
                string.Empty,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(
                Path.Combine(cooksDir, succeededCookId, "cook.log"),
                "succeeded cook content\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            await SeedDatabaseAsync(databaseFile, runningCookId, succeededCookId);

            // Stage 3f only needs WorkspaceFolderPath + WebRoot is
            // unused. Leave AppRoot/PaxScriptPath null so the cook-
            // start route is intentionally NOT registered -- Stage
            // 3f does not invoke the real PAX runner.
            var options = new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: workspace);

            return new Stage3fWorkspaceFixture(
                root, workspace, databaseFile,
                runningCookId, succeededCookId,
                options);
        }

        private static async Task SeedDatabaseAsync(
            string databaseFile, string runningCookId, string succeededCookId)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = databaseFile,
                Mode       = SqliteOpenMode.ReadWriteCreate,
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();

            using (var cmd = conn.CreateCommand())
            {
                // Minimal cooks-table schema (matches Stage 3c
                // schema; SqliteWorkspaceReader.GetCookById's SELECT
                // statement reads only these columns).
                cmd.CommandText = @"
CREATE TABLE cooks (
    cook_id TEXT PRIMARY KEY,
    recipe_id TEXT,
    status TEXT NOT NULL,
    exit_code INTEGER,
    pid INTEGER,
    cook_folder TEXT NOT NULL,
    pax_script_path TEXT NOT NULL,
    pax_script_version TEXT NOT NULL,
    trigger TEXT NOT NULL,
    started_at TEXT,
    finished_at TEXT,
    duration_seconds REAL,
    error_class TEXT,
    error_message TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    summary_path TEXT,
    closure_reason TEXT,
    closure_evidence_json TEXT,
    parent_cook_id TEXT
);";
                await cmd.ExecuteNonQueryAsync();
            }

            const string ins =
                "INSERT INTO cooks (cook_id, recipe_id, status, exit_code, pid, cook_folder, " +
                "pax_script_path, pax_script_version, trigger, started_at, finished_at, " +
                "duration_seconds, error_class, error_message, created_at, updated_at, " +
                "summary_path, parent_cook_id) VALUES " +
                "($id,$recipe_id,$status,$exit_code,NULL,$cook_folder,$pax_script_path," +
                "'0.0.0-fixture','manual',$started_at,$finished_at,$duration,NULL,NULL," +
                "$created_at,$updated_at,NULL,NULL);";

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = ins;
                cmd.Parameters.AddWithValue("$id", runningCookId);
                cmd.Parameters.AddWithValue("$recipe_id", "r-stage3f");
                cmd.Parameters.AddWithValue("$status", "running");
                cmd.Parameters.AddWithValue("$exit_code", DBNull.Value);
                cmd.Parameters.AddWithValue("$cook_folder", "Cooks/" + runningCookId);
                cmd.Parameters.AddWithValue("$pax_script_path",
                    @"C:\Workspace\AppRoot\resources\pax\fixture.ps1");
                cmd.Parameters.AddWithValue("$started_at", "2026-05-27T11:00:00Z");
                cmd.Parameters.AddWithValue("$finished_at", DBNull.Value);
                cmd.Parameters.AddWithValue("$duration", DBNull.Value);
                cmd.Parameters.AddWithValue("$created_at", "2026-05-27T11:00:00Z");
                cmd.Parameters.AddWithValue("$updated_at", "2026-05-27T11:00:00Z");
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = ins;
                cmd.Parameters.AddWithValue("$id", succeededCookId);
                cmd.Parameters.AddWithValue("$recipe_id", "r-stage3f");
                cmd.Parameters.AddWithValue("$status", "succeeded");
                cmd.Parameters.AddWithValue("$exit_code", 0);
                cmd.Parameters.AddWithValue("$cook_folder", "Cooks/" + succeededCookId);
                cmd.Parameters.AddWithValue("$pax_script_path",
                    @"C:\Workspace\AppRoot\resources\pax\fixture.ps1");
                cmd.Parameters.AddWithValue("$started_at", "2026-05-26T11:00:00Z");
                cmd.Parameters.AddWithValue("$finished_at", "2026-05-26T11:00:30Z");
                cmd.Parameters.AddWithValue("$duration", 30.0);
                cmd.Parameters.AddWithValue("$created_at", "2026-05-26T11:00:00Z");
                cmd.Parameters.AddWithValue("$updated_at", "2026-05-26T11:00:30Z");
                await cmd.ExecuteNonQueryAsync();
            }

            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public async Task UpdateCookStatusAsync(string cookId, string newStatus)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFilePath,
                Mode       = SqliteOpenMode.ReadWrite,
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE cooks SET status = $s, updated_at = $u WHERE cook_id = $id;";
                cmd.Parameters.AddWithValue("$s", newStatus);
                cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$id", cookId);
                await cmd.ExecuteNonQueryAsync();
            }
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup -- temp dir survival is harmless.
            }
            return ValueTask.CompletedTask;
        }
    }
}
