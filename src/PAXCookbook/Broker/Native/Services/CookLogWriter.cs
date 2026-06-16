using System.Text;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3e -- shared-read cook.log writer. Parity with the PS
// supervisor's open of the per-cook log file:
//
//   [System.IO.File]::Open(<logPath>, Append, Write, FileShare.Read)
//   StreamWriter(stream, [System.Text.UTF8Encoding]::new($false))
//   { AutoFlush=true }
//
// Doctrine:
//   - The reader side (CookReadRoutes / chef-tool tail) must be able
//     to hold a shared-read handle while the writer appends. FileShare
//     is set to Read accordingly.
//   - AutoFlush is true so each WriteLine is durable before control
//     returns -- the PS supervisor relies on this for the WebSocket
//     streamer's tail. Stage 3e does NOT implement the streamer but
//     keeps the flush semantics so the writes appear in tail polls.
//   - stderr lines are prefixed with "[STDERR] " verbatim. This is
//     the exact prefix the PS supervisor writes; downstream consumers
//     (and the SPA log viewer) match on it.
//   - The writer is thread-safe via a lock around the underlying
//     StreamWriter -- the runner publishes stdout and stderr from
//     two separate async callbacks so concurrent writes are expected.
public sealed class CookLogWriter : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly StreamWriter _writer;
    private readonly FileStream _stream;
    private readonly object _gate = new();
    private bool _disposed;

    public string LogPath { get; }

    public CookLogWriter(string logPath)
    {
        LogPath = logPath;
        _stream = new FileStream(
            logPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(_stream, Utf8NoBom)
        {
            AutoFlush = true,
            NewLine   = "\r\n",
        };
    }

    public void WriteStdoutLine(string line)
    {
        if (line is null) return;
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine(line);
        }
    }

    public void WriteStderrLine(string line)
    {
        if (line is null) return;
        lock (_gate)
        {
            if (_disposed) return;
            _writer.Write("[STDERR] ");
            _writer.WriteLine(line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _writer.Flush();
            }
            await _writer.DisposeAsync().ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort dispose. The OS reclaims the handle on
            // process exit even if our flush failed.
        }
    }
}
