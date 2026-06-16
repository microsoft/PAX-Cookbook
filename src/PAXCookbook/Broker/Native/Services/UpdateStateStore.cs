using System.Collections.Generic;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-A -- in-memory mirror of $Script:UpdateState.
//
// Parity with Routes/Updates.ps1:
//   * Process-lifetime state. Resets on every broker start.
//   * lastCheck / lastDownload / manifestSnapshot mirror the
//     PowerShell hashtable fields.
//   * Thread-safe via a single gate -- the PS broker uses
//     [hashtable]::Synchronized which provides per-key locking;
//     we use a single lock because the assignment surface is tiny
//     (three fields, write-once-per-request).
public sealed class UpdateStateStore
{
    private readonly object              _gate           = new();
    private UpdateCheckResult?           _lastCheck;
    private UpdateDownloadResult?        _lastDownload;
    private IDictionary<string, object?>? _manifestSnapshot;

    public UpdateCheckResult? LastCheck
    {
        get { lock (_gate) { return _lastCheck; } }
    }

    public UpdateDownloadResult? LastDownload
    {
        get { lock (_gate) { return _lastDownload; } }
    }

    public IDictionary<string, object?>? ManifestSnapshot
    {
        get { lock (_gate) { return _manifestSnapshot; } }
    }

    public void SetCheckResult(
        UpdateCheckResult         check,
        IDictionary<string, object?>? manifestSnapshot)
    {
        lock (_gate)
        {
            _lastCheck         = check;
            _manifestSnapshot  = manifestSnapshot;
        }
    }

    public void SetDownloadResult(UpdateDownloadResult download)
    {
        lock (_gate)
        {
            _lastDownload = download;
        }
    }
}
