using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PAXCookbook.App;

// Native-only two-stage IPC channel for the experimental WAM authority path
// (Track 1 / T1-S2A native request-binding + restart-continuity repair).
//
// The renderer is entirely off this channel: it only initiates an opaque
// requestId over HTTP and polls a bounded status. The HWND-owning attached-
// window process performs the security-critical exchange with the daemon over a
// same-user, ACL-restricted named pipe in TWO explicitly typed stages:
//
//   Stage 1 (kind="lookup"): the window sends ONLY the opaque requestId; the
//     daemon returns the minimal, fully daemon-authored descriptor (found +
//     reason). This lookup is NON-CONSUMING.
//   Stage 2 (kind="result"): the window submits the bounded neutral result
//     (requestId + category + booleans). This is single-use and is the ONLY
//     path that grants authority.
//
// The typed "kind" discriminator makes it impossible to confuse a descriptor
// lookup with a result submission. No token, raw claim, account handle, recipe,
// generation, or granted-scope array ever crosses this pipe.
//
// POL-1 honesty (no overclaim): the pipe grants access only to the CURRENT
// USER's SID (explicit ACL) and the client verifies the server owner is the same
// user (PipeOptions.CurrentUserOnly). It does NOT resist same-SID malware: any
// process running as the same user can open the pipe. This channel asserts NO
// code-origin, inherited-secret, inherited-handle, or same-SID-malware
// resistance. All authority (challenge) remains daemon-owned regardless of the
// caller.
internal static class ExperimentalWamPipe
{
    private const string KindLookup = "lookup";
    private const string KindResult = "result";

    // Per-user, per-workspace pipe name so distinct workspaces do not collide.
    internal static string PipeName(string workspacePath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(workspacePath ?? string.Empty));
        string tag = Convert.ToHexString(hash, 0, 8);
        return $"PAXCookbook.ExperimentalWam.{tag}";
    }

    // ---- Stage 1: descriptor lookup ------------------------------------

    internal static string SerializeLookupRequest(string requestId) => JsonSerializer.Serialize(new
    {
        kind = KindLookup,
        requestId,
    });

    internal static string SerializeDescriptorResponse(WamNativeDescriptor descriptor) => JsonSerializer.Serialize(new
    {
        found = descriptor.Found,
        reason = descriptor.Reason.ToString(),
    });

    internal static WamNativeDescriptor? TryParseDescriptorResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            bool found = GetBool(root, "found");
            if (!found)
            {
                WamDescriptorReason r = Enum.TryParse(GetString(root, "reason"), out WamDescriptorReason parsed)
                    ? parsed
                    : WamDescriptorReason.Unknown;
                return WamNativeDescriptor.NotFound(r);
            }

            return WamNativeDescriptor.ForSession();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- Stage 2: result submission ------------------------------------

    internal static string SerializeResultRequest(NeutralWamResult result) => JsonSerializer.Serialize(new
    {
        kind = KindResult,
        requestId = result.RequestId,
        category = result.Category.ToString(),
        scopeValid = result.ScopeValid,
        identityValid = result.IdentityValid,
    });

    internal static NeutralWamResult? TryParseResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? requestId = GetString(root, "requestId");
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return null;
            }

            NeutralWamCategory category = Enum.TryParse(GetString(root, "category"), ignoreCase: true, out NeutralWamCategory c)
                ? c
                : NeutralWamCategory.Denied;
            bool scopeValid = GetBool(root, "scopeValid");
            bool identityValid = GetBool(root, "identityValid");

            return new NeutralWamResult(requestId!, category, scopeValid, identityValid);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- Server dispatch (daemon side) ---------------------------------

    // Daemon-side handling of a single native message. The typed "kind" selects
    // the non-consuming descriptor lookup or the single-use result application
    // (the ONLY grant authority). An unknown or malformed message fails closed.
    internal static string HandleServerMessage(ExperimentalWamDaemonEndpoint endpoint, string? requestJson)
    {
        string? kind = null;
        if (!string.IsNullOrWhiteSpace(requestJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(requestJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    kind = GetString(doc.RootElement, "kind");
                }
            }
            catch (JsonException)
            {
                kind = null;
            }
        }

        if (string.Equals(kind, KindLookup, StringComparison.Ordinal))
        {
            string? requestId = ReadRequestId(requestJson);
            WamNativeDescriptor descriptor = endpoint.LookupDescriptor(requestId);
            return SerializeDescriptorResponse(descriptor);
        }

        if (string.Equals(kind, KindResult, StringComparison.Ordinal))
        {
            NeutralWamResult? result = TryParseResult(requestJson);
            if (result is null)
            {
                return JsonSerializer.Serialize(new { approved = false, reason = "malformed" });
            }

            DaemonWamOutcome outcome = endpoint.ApplyNativeResult(result);
            return JsonSerializer.Serialize(new { approved = outcome.Approved, reason = outcome.Reason.ToString() });
        }

        return JsonSerializer.Serialize(new { approved = false, reason = "malformed" });
    }

    private static string? ReadRequestId(string? json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json!);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? GetString(doc.RootElement, "requestId") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}

// The window-side native channel abstraction: a non-consuming descriptor lookup
// and a single-use result submission. Implemented by ExperimentalWamPipeClient
// over the same-user pipe; faked deterministically in tests.
internal interface IExperimentalWamNativeChannel
{
    // Stage 1: resolve the daemon-authored descriptor for the requestId. Returns
    // false (with a NotFound descriptor) when the request is unknown, expired,
    // terminal, consumed, or the pipe is unreachable.
    bool TryLookup(string requestId, out WamNativeDescriptor descriptor);

    // Stage 2: submit the bounded neutral result. Returns false on transport
    // failure (the daemon never grants on a failed submission).
    bool TrySubmit(NeutralWamResult result);
}

// Fail-closed activation wiring for the experimental provider (T1-S2A). The
// endpoint is advertised as usable ONLY when the native pipe server is created
// successfully (synchronous readiness). If the pipe factory returns null — the
// current-user SID could not be resolved or the ACL'd pipe could not be
// constructed — this returns null so the caller disables the provider and the
// routes report not-enabled instead of minting requests that could never
// complete. This is the single, deterministically testable decision point.
internal static class ExperimentalWamActivation
{
    internal static ExperimentalWamDaemonEndpoint? ActivateIfPipeReady(
        ExperimentalWamDaemonEndpoint? endpoint,
        Func<ExperimentalWamPipeServer?> pipeFactory,
        out ExperimentalWamPipeServer? server)
    {
        server = null;
        if (endpoint is null || pipeFactory is null)
        {
            return null;
        }

        server = pipeFactory();
        return server is null ? null : endpoint;
    }
}

// Daemon-side named-pipe server. Created ONLY after the current-user SID is
// resolved and the first restricted pipe instance is constructed synchronously
// (fail-closed readiness). There is NO WorldSid fallback: if the SID cannot be
// resolved or the ACL'd pipe cannot be created, TryCreate returns null and the
// experimental provider is not advertised.
internal sealed class ExperimentalWamPipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly ExperimentalWamDaemonEndpoint _endpoint;
    private readonly SecurityIdentifier _user;
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeServerStream? _initialServer;
    private Thread? _thread;

    private ExperimentalWamPipeServer(
        string pipeName,
        ExperimentalWamDaemonEndpoint endpoint,
        SecurityIdentifier user,
        NamedPipeServerStream initialServer)
    {
        _pipeName = pipeName;
        _endpoint = endpoint;
        _user = user;
        _initialServer = initialServer;
    }

    // Fail-closed synchronous factory. Resolves the current-user SID (no
    // WorldSid fallback) and constructs the first restricted pipe instance to
    // prove the ACL and pipe name are usable BEFORE any route is exposed. Returns
    // null on any failure so the caller disables the experimental provider.
    internal static ExperimentalWamPipeServer? TryCreate(string pipeName, ExperimentalWamDaemonEndpoint endpoint)
    {
        SecurityIdentifier? user;
        try
        {
            user = WindowsIdentity.GetCurrent().User;
        }
        catch (Exception)
        {
            user = null;
        }

        if (user is null)
        {
            // Fail closed: no world-accessible fallback, no pipe, no provider.
            return null;
        }

        NamedPipeServerStream initial;
        try
        {
            initial = CreateRestrictedServer(pipeName, user);
        }
        catch (Exception)
        {
            // Initial ACL/server-creation failure disables the provider; it is
            // never swallowed into an apparently enabled provider.
            return null;
        }

        return new ExperimentalWamPipeServer(pipeName, endpoint, user, initial);
    }

    internal void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ExperimentalWamPipeServer" };
        _thread.Start();
    }

    private void Loop()
    {
        // Serve the pre-created first instance, then create fresh instances.
        NamedPipeServerStream? server = _initialServer;
        _initialServer = null;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                server ??= CreateRestrictedServer(_pipeName, _user);
                server.WaitForConnection();

                using (var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true))
                {
                    string? line = reader.ReadLine();
                    string response = ExperimentalWamPipe.HandleServerMessage(_endpoint, line);
                    using var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
                    writer.WriteLine(response);
                }
            }
            catch (Exception) when (!_cts.IsCancellationRequested)
            {
                // A single connection failure must never take the daemon down.
            }
            catch (Exception)
            {
                break;
            }
            finally
            {
                server?.Dispose();
                server = null;
            }
        }
    }

    // Creates a pipe whose ACL grants access only to the current user's SID.
    // There is deliberately NO WorldSid fallback: the SID is resolved once in
    // TryCreate and passed in, so a resolution failure disables the provider.
    private static NamedPipeServerStream CreateRestrictedServer(string pipeName, SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _initialServer?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            // Unblock a pending WaitForConnection by opening a throwaway client.
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(200);
        }
        catch
        {
            // Best effort.
        }

        _cts.Dispose();
    }
}

// Window-side named-pipe client implementing the two-stage native channel. The
// client uses PipeOptions.CurrentUserOnly so it only connects to a server owned
// by the same user. It never sends a token or raw claim.
internal sealed class ExperimentalWamPipeClient : IExperimentalWamNativeChannel
{
    private readonly string _pipeName;

    internal ExperimentalWamPipeClient(string pipeName) => _pipeName = pipeName;

    public bool TryLookup(string requestId, out WamNativeDescriptor descriptor)
    {
        descriptor = WamNativeDescriptor.NotFound(WamDescriptorReason.Unknown);
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        string? response = Exchange(ExperimentalWamPipe.SerializeLookupRequest(requestId));
        WamNativeDescriptor? parsed = ExperimentalWamPipe.TryParseDescriptorResponse(response);
        if (parsed is null)
        {
            return false;
        }

        descriptor = parsed;
        return parsed.Found;
    }

    public bool TrySubmit(NeutralWamResult result)
    {
        if (result is null)
        {
            return false;
        }

        string? response = Exchange(ExperimentalWamPipe.SerializeResultRequest(result));
        return response is not null;
    }

    private string? Exchange(string requestLine, int connectTimeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(connectTimeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(requestLine);
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
            return reader.ReadLine();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
