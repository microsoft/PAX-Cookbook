using System.Net.WebSockets;
using System.Text;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3f -- per-socket cook.log tail loop. Native parity with
// app\broker\Routes\CookLogWs.ps1's $Script:CookLogTailScript:
//
//   1. Open <cookFolder>\cook.log with FileMode.Open + FileAccess.Read
//      + FileShare.ReadWrite. The writer side (CookLogWriter, opened
//      with FileShare.Read) and Stage 3c whole-file readers (also
//      FileShare.ReadWrite) coexist with this handle.
//   2. Seek to current EOF. Bytes 0..EOF are NOT sent on this socket;
//      the browser hydrates that prefix via GET /api/v1/cooks/<id>/log
//      before opening the WebSocket. No replay, no backlog, no ring
//      buffer. The PS broker is explicit about this design choice and
//      we mirror it.
//   3. Loop while the socket is Open and the host has not shut down:
//        a. Drain any bytes between $position and current Length to
//           the client as opaque UTF-8 TEXT frames. UTF-8 is decoded
//           with a stateful Decoder so a multi-byte rune that straddles
//           a read boundary survives.
//        b. Every 4 cycles (~1s), re-read the cook's status from the
//           cooks table via a fresh SqliteWorkspaceReader.GetCookById
//           call (the reader opens a fresh SqliteConnection per call
//           so this is thread-safe across concurrent tailers).
//        c. If status != 'running', drain residual bytes with the
//           decoder flushed and exit the loop.
//        d. Otherwise sleep 250ms.
//   4. Close the socket with NormalClosure (cook reached terminal)
//      or InternalServerError (IO error during tail) or NormalClosure
//      "host_shutdown" when the application stops.
//
// Thread-safety: each tailer owns its own FileStream, Decoder, and
// byte buffer. Multiple tailers can run concurrently against the same
// cook -- the FileShare.ReadWrite open and per-call SQLite connection
// make that safe.
public sealed class CookLogTailer
{
    private const int  ReadBufferBytes      = 65536;
    private const int  PollIntervalMs       = 250;
    private const int  TerminalCheckEveryNCycles = 4;
    private const int  SendBufferChars      = 65536;

    private readonly SqliteWorkspaceReader _reader;

    public CookLogTailer(SqliteWorkspaceReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task RunAsync(
        WebSocket socket,
        string cookId,
        FileStream fs,
        CancellationToken hostCancellation)
    {
        if (fs is null) throw new ArgumentNullException(nameof(fs));
        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeReason = CookLogStreamCloseReasons.CookTerminal;

        // Cooperative cancellation: stop the main tail/poll loop when
        // the host shuts down OR when the observer notices a client
        // Close frame. The observer uses its OWN CTS so we can keep
        // its ReceiveAsync alive through the server-side close path
        // (cancelling a pending WebSocket.ReceiveAsync aborts the
        // socket, which would skip our Close-frame send).
        using var loopCts     = CancellationTokenSource.CreateLinkedTokenSource(hostCancellation);
        using var observerCts = CancellationTokenSource.CreateLinkedTokenSource(hostCancellation);

        // Background observer: drain inbound frames so the .NET
        // WebSocket impl transitions to CloseReceived when the
        // client sends Close. Any non-Close inbound traffic is
        // ignored (the PS broker is upstream-only -- the SPA never
        // sends frames). When the observer sees a Close (or the
        // socket otherwise leaves Open), it cancels loopCts so the
        // tail loop's Task.Delay wakes immediately; it does NOT
        // cancel its own ReceiveAsync because the main flow needs
        // the socket non-aborted to send its Close frame.
        var clientCloseObserver = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                while (!observerCts.IsCancellationRequested &&
                       socket.State == WebSocketState.Open)
                {
                    var r = await socket
                        .ReceiveAsync(new ArraySegment<byte>(buf), observerCts.Token)
                        .ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        // Wake the main loop -- client wants out.
                        try { loopCts.Cancel(); } catch { }
                        return;
                    }
                }
            }
            catch
            {
                // Wake the main loop too -- if Receive threw the
                // socket is gone and there is no point polling.
                try { loopCts.Cancel(); } catch { }
            }
        });

        // The route opened the FileStream BEFORE AcceptWebSocketAsync
        // and already seeked to EOF. Bytes 0..fs.Position are
        // hydrated by the SPA via GET /api/v1/cooks/{id}/log; the
        // tail loop only emits bytes appended after that cutoff.
        FileStream? fsToDispose = fs;
        try
        {
            // Stateful UTF-8 decoder survives a multi-byte sequence
            // that straddles a Read() boundary -- without this we'd
            // emit a malformed code point per partial sequence and
            // the SPA log viewer would render the replacement
            // character.
            var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();
            var readBuf = new byte[ReadBufferBytes];
            var charBuf = new char[SendBufferChars];

            var cycle = 0;
            while (socket.State == WebSocketState.Open && !loopCts.IsCancellationRequested)
            {
                await DrainAsync(socket, fs, decoder, readBuf, charBuf, flush: false, loopCts.Token)
                    .ConfigureAwait(false);

                cycle++;
                if ((cycle % TerminalCheckEveryNCycles) == 0)
                {
                    if (IsTerminal(cookId))
                    {
                        // Final drain with decoder flush so any
                        // residual bytes between our last read and
                        // the cook's actual EOF are sent. Parity with
                        // PS broker's `$drain $true` final call.
                        await DrainAsync(
                                socket, fs, decoder, readBuf, charBuf,
                                flush: true, loopCts.Token)
                            .ConfigureAwait(false);
                        break;
                    }
                }

                try
                {
                    await Task.Delay(PollIntervalMs, loopCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Either host shutdown OR client closed the
                    // socket (the observer cancelled loopCts).
                    // Distinguish via hostCancellation.
                    if (hostCancellation.IsCancellationRequested)
                    {
                        closeReason = CookLogStreamCloseReasons.HostShutdown;
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (hostCancellation.IsCancellationRequested)
            {
                closeReason = CookLogStreamCloseReasons.HostShutdown;
            }
        }
        catch
        {
            // Any IO error mirrors PS broker's catch -> 1011.
            closeStatus = WebSocketCloseStatus.InternalServerError;
            closeReason = CookLogStreamCloseReasons.IoError;
        }
        finally
        {
            try { fsToDispose?.Dispose(); } catch { }

            // Wake the main loop -- not the observer. The observer's
            // ReceiveAsync must stay alive across our Close-frame
            // send so we don't abort the socket and lose the frame.
            try { loopCts.Cancel(); } catch { }

            try
            {
                // Use CloseOutputAsync (send-only) instead of
                // CloseAsync. CloseAsync internally calls Receive to
                // wait for the peer's Close ack -- that conflicts
                // with our observer's pending ReceiveAsync and is
                // also unnecessary: we just need to put our Close
                // frame on the wire so the client's CloseAsync or
                // its read-until-close helper completes. The
                // observer (still alive) will drain the client's ack
                // when it arrives, or the connection teardown will
                // make ReceiveAsync throw.
                //
                // Valid states for CloseOutputAsync: Open, CloseReceived.
                if (socket.State == WebSocketState.Open ||
                    socket.State == WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.CloseOutputAsync(closeStatus, closeReason, closeCts.Token)
                        .ConfigureAwait(false);

                    // Give Kestrel and the kernel a moment to flush
                    // the Close frame onto the wire before the route
                    // handler returns and the middleware aborts the
                    // underlying connection. Without this delay,
                    // localhost test clients see the abort before
                    // they see the Close.
                    try { await Task.Delay(200).ConfigureAwait(false); } catch { }
                }
            }
            catch
            {
                // Suppress -- the socket is going away regardless.
            }

            // Now cancel the observer (we no longer need to keep its
            // ReceiveAsync alive) and wait briefly for it to end.
            try { observerCts.Cancel(); } catch { }
            try
            {
                await clientCloseObserver
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            catch
            {
                try { socket.Abort(); } catch { }
            }
        }
    }

    // Read any bytes between fs.Position and fs.Length, decode them
    // through the stateful UTF-8 decoder, and ship the resulting text
    // as TEXT WebSocket frames. flush=true requests the decoder to
    // emit any pending state (called once at terminal cleanup).
    //
    // Returns when there is nothing left to read; the caller's loop
    // owns the polling cadence and the terminal-check cycle.
    private static async Task DrainAsync(
        WebSocket socket,
        FileStream fs,
        Decoder decoder,
        byte[] readBuf,
        char[] charBuf,
        bool flush,
        CancellationToken cancellation)
    {
        while (true)
        {
            if (cancellation.IsCancellationRequested) return;
            if (socket.State != WebSocketState.Open) return;

            var currentLength = fs.Length;
            var available = currentLength - fs.Position;
            if (available <= 0)
            {
                // Nothing to read. If flush was requested, give the
                // decoder one final chance to emit any pending state
                // (e.g., a lone trailing byte). PS broker does the
                // equivalent via $Flush on the last call.
                if (flush)
                {
                    var emitted = decoder.GetChars(
                        Array.Empty<byte>(), 0, 0,
                        charBuf, 0,
                        flush: true);
                    if (emitted > 0)
                    {
                        var text  = new string(charBuf, 0, emitted);
                        var bytes = Encoding.UTF8.GetBytes(text);
                        await socket
                            .SendAsync(new ArraySegment<byte>(bytes),
                                       WebSocketMessageType.Text,
                                       endOfMessage: true,
                                       cancellation)
                            .ConfigureAwait(false);
                    }
                }
                return;
            }

            var toRead = (int)Math.Min(readBuf.Length, available);
            var n = await fs.ReadAsync(readBuf.AsMemory(0, toRead), cancellation)
                .ConfigureAwait(false);
            if (n <= 0) return;

            // The decoder is fed in chunks. The "last chunk" flag is
            // true only when (a) the caller asked us to flush AND (b)
            // this read exhausted the available bytes -- otherwise we
            // keep the decoder's state for the next call.
            var isLastChunk = flush && ((available - n) <= 0);
            var charCount = decoder.GetChars(
                readBuf, 0, n,
                charBuf, 0,
                flush: isLastChunk);

            if (charCount > 0)
            {
                var text  = new string(charBuf, 0, charCount);
                var bytes = Encoding.UTF8.GetBytes(text);
                await socket
                    .SendAsync(new ArraySegment<byte>(bytes),
                               WebSocketMessageType.Text,
                               endOfMessage: true,
                               cancellation)
                    .ConfigureAwait(false);
            }
        }
    }

    // Re-read the cook's status via a fresh SqliteWorkspaceReader
    // call. The PS broker uses a new SqliteConnection per terminal-
    // check; the reader's per-call connection model gives us the same
    // behavior. A transient DB exception is swallowed and treated as
    // "not terminal" so a momentary lock contention does not kick a
    // browser off the live tail -- parity with the PS broker's
    // try/catch returning $false.
    private bool IsTerminal(string cookId)
    {
        try
        {
            var row = _reader.GetCookById(cookId);
            if (row is null) return true;            // row vanished -> terminal
            return !string.Equals(row.Status, "running", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
