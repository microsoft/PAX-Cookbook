using System.IO.Pipes;

namespace PAXCookbook.Ipc;

// One-shot client: connect, write one request, read one response, close.
// Used by secondary invocations to forward a verb to the primary.
public sealed class NamedPipeIpcClient : IIpcClient
{
    public IpcClientForwardResult Forward(string endpointName, string verb, TimeSpan timeout)
    {
        var req = new IpcRequest(verb, Guid.NewGuid().ToString(), DateTime.UtcNow.ToString("o"));
        NamedPipeClientStream client;
        try
        {
            client = new NamedPipeClientStream(".", endpointName, PipeDirection.InOut, PipeOptions.None);
        }
        catch (Exception ex)
        {
            return new IpcClientForwardResult(IpcClientOutcome.NoPrimary, null, ex.Message);
        }
        try
        {
            client.Connect((int)Math.Max(50, timeout.TotalMilliseconds));
        }
        catch (TimeoutException)
        {
            try { client.Dispose(); } catch { }
            return new IpcClientForwardResult(IpcClientOutcome.NoPrimary, null, "no primary listener");
        }
        catch (Exception ex)
        {
            try { client.Dispose(); } catch { }
            return new IpcClientForwardResult(IpcClientOutcome.NoPrimary, null, ex.Message);
        }
        try
        {
            using (client)
            {
                // NamedPipeClientStream does not support Read/WriteTimeout;
                // connect timeout above bounds the wait. After connect,
                // server is single-shot and responds promptly.
                IpcFrame.WriteRequest(client, req);
                var resp = IpcFrame.ReadResponse(client);
                if (resp is null) return new IpcClientForwardResult(IpcClientOutcome.BadResponse, null, "no response");
                if (!resp.Ok) return new IpcClientForwardResult(IpcClientOutcome.VerbFailed, resp, resp.Code);
                return new IpcClientForwardResult(IpcClientOutcome.Accepted, resp, null);
            }
        }
        catch (IOException ex)
        {
            return new IpcClientForwardResult(IpcClientOutcome.BadResponse, null, ex.Message);
        }
    }
}
