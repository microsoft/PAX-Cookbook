using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PAXCookbook.Ipc;

// Per-user named pipe server. ACL grants ReadWrite+Synchronize to the
// current user's SID only, per paxcookbook-ipc-contract.md §5.
// Single instance (max = 1). Each connection reads exactly one request
// frame, returns one response, closes — per §6.
public sealed class NamedPipeIpcServer : IIpcServer
{
    private readonly string _name;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Thread? _worker;
    private IIpcVerbHandler? _handler;
    private bool _disposed;

    public string EndpointName => _name;

    public NamedPipeIpcServer(string endpointName)
    {
        _name = endpointName;
    }

    public void Start(IIpcVerbHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (_worker is not null) throw new InvalidOperationException("server already started");
            _handler = handler;
            _cts = new CancellationTokenSource();
            _worker = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "PAXCookbook.IpcServer" };
            _worker.Start();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
        }
        // Poke the listening pipe so the AcceptConnection unblocks.
        try
        {
            using var c = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.None);
            c.Connect(250);
        }
        catch { }
        try { _worker?.Join(2000); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? srv = null;
            try
            {
                var ps = BuildCurrentUserOnlyAcl();
                srv = NamedPipeServerStreamAcl.Create(
                    _name,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None,
                    inBufferSize: 4096,
                    outBufferSize: 4096,
                    pipeSecurity: ps);
                srv.WaitForConnection();
                if (ct.IsCancellationRequested) break;
                HandleOne(srv);
            }
            catch (IOException)
            {
                // pipe broken; loop and accept again
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // never crash the IPC thread; loop to keep serving
            }
            finally
            {
                try { srv?.Dispose(); } catch { }
            }
        }
    }

    private void HandleOne(NamedPipeServerStream srv)
    {
        var read = IpcFrame.ReadRequest(srv);
        IpcResponse resp;
        switch (read.Error)
        {
            case IpcFrame.ReadError.LengthExceeded:
                resp = new IpcResponse("", false, IpcResponseCodes.LengthExceeded, null);
                break;
            case IpcFrame.ReadError.TruncatedHeader:
            case IpcFrame.ReadError.TruncatedBody:
            case IpcFrame.ReadError.BadShape:
                resp = new IpcResponse("", false, IpcResponseCodes.BadFrame, null);
                break;
            case IpcFrame.ReadError.BadJson:
                resp = new IpcResponse("", false, IpcResponseCodes.BadJson, null);
                break;
            case IpcFrame.ReadError.Ok:
                var req = read.Request!;
                if (!IpcAllowlist.Verbs.Contains(req.Verb))
                {
                    resp = new IpcResponse(req.Id, false, IpcResponseCodes.UnknownVerb, null);
                }
                else
                {
                    try
                    {
                        resp = _handler!.Handle(req);
                    }
                    catch (Exception ex)
                    {
                        resp = new IpcResponse(req.Id, false, IpcResponseCodes.VerbFailed, ex.Message);
                    }
                }
                break;
            default:
                resp = new IpcResponse("", false, IpcResponseCodes.BadFrame, null);
                break;
        }
        try { IpcFrame.WriteResponse(srv, resp); } catch { }
    }

    private static PipeSecurity BuildCurrentUserOnlyAcl()
    {
        var sid = WindowsIdentity.GetCurrent().User
                  ?? throw new InvalidOperationException("could not resolve current user SID");
        var ps = new PipeSecurity();
        ps.SetOwner(sid);
        ps.AddAccessRule(new PipeAccessRule(
            sid,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        return ps;
    }
}
