using System.Net.Http;
using System.Text.Json;

namespace PAXCookbook.App;

// Two-process broker coordination (V2).
//
// PAX Cookbook can run as a long-lived headless broker (PAX Cookbook.exe
// --headless, started at login via the HKCU Run key) while one or more UI
// windows open and close on demand. To make a UI launch attach to an
// already-running broker instead of starting a second one, the broker that
// OWNS the in-process Kestrel host advertises its loopback port in a well-known
// per-user file and holds an exclusive write lock on that file for its whole
// lifetime. A UI launch reads the file and probes the health endpoint to
// confirm a genuine PAX Cookbook broker is answering before it attaches.
//
// Security posture:
//   * The port file lives under %LOCALAPPDATA%\PAXCookbook — a per-user path
//     whose ACL already restricts write access to the owning user, so another
//     user cannot plant or tamper with it. It carries only an integer port.
//   * Detection NEVER trusts the port number blindly. It calls
//     GET http://127.0.0.1:{port}/api/v1/health (loopback only, 2 s timeout)
//     and accepts the broker only when the JSON identifies THIS application
//     (app == "PAX Cookbook", runtime == "dotnet-kestrel"). A foreign service
//     squatting on the port is rejected, so a stale/wrong port can never cause
//     the UI to attach to something that is not our broker.
//   * The owning broker holds the port file open with FileShare.Read: other
//     processes may READ it (detection) but cannot take a second write lock, so
//     two brokers cannot both claim ownership. When the owner exits (cleanly or
//     by crash) the OS releases the handle, so a later launch can re-own it.
//   * It never runs PAX, never reads a secret, and only ever contacts the
//     loopback health endpoint.
internal static class BrokerDetection
{
    private const string AppIdentity = "PAX Cookbook";
    private const string RuntimeIdentity = "dotnet-kestrel";
    private const int HealthTimeoutMs = 2000;

    // Per-user coordination anchor: %LOCALAPPDATA%\PAXCookbook\broker.port.
    internal static string PortFilePath()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "PAXCookbook", "broker.port");
    }

    // Read the advertised port, probe its health endpoint, and return the port
    // only when a genuine PAX Cookbook broker answers. Returns null when no file
    // exists, the file is unreadable/malformed, nothing answers, or the
    // responder is not our broker. Pure detection: it never writes or locks.
    internal static int? TryGetRunningBrokerPort()
    {
        int? port = ReadPortFile();
        if (port is null)
        {
            return null;
        }
        return IsOurBroker(port.Value) ? port : null;
    }

    // Best-effort removal of a stale broker.port left behind by a broker that is
    // no longer answering (a previous crash/kill, or a process that lingered and
    // was force-terminated). Call this only AFTER detection has confirmed no live
    // broker is serving. It is safe: a LIVE owner keeps the file open with an
    // exclusive write lock, so File.Delete throws a sharing violation and is
    // swallowed — only a truly orphaned file is ever removed. Returns true when a
    // stale file was deleted. Never throws.
    internal static bool TryDeleteStalePortFile()
    {
        try
        {
            string path = PortFilePath();
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            return true;
        }
        catch
        {
            // Held by a live owner, or a transient IO error: leave it untouched.
            return false;
        }
    }

    // Probe GET http://127.0.0.1:{port}/api/v1/health and confirm the responder
    // identifies as THIS application. Loopback-only, short timeout, no token
    // (health is unauthenticated by design), no redirects followed.
    internal static bool IsOurBroker(int port)
    {
        if (port < 1 || port > 65535)
        {
            return false;
        }
        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseDefaultCredentials = false,
            };
            using var http = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromMilliseconds(HealthTimeoutMs),
            };
            // 127.0.0.1 (never localhost) so the probe cannot be redirected off
            // the loopback interface by a hosts-file entry.
            using HttpResponseMessage resp =
                http.GetAsync($"http://127.0.0.1:{port}/api/v1/health").GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            string app = root.TryGetProperty("app", out JsonElement a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() ?? string.Empty
                : string.Empty;
            string runtime = root.TryGetProperty("runtime", out JsonElement r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? string.Empty
                : string.Empty;
            return string.Equals(app, AppIdentity, StringComparison.Ordinal)
                && string.Equals(runtime, RuntimeIdentity, StringComparison.Ordinal);
        }
        catch
        {
            // Any failure (no listener, timeout, malformed JSON, non-PAX
            // responder) means "no usable broker here".
            return false;
        }
    }

    private static int? ReadPortFile()
    {
        try
        {
            string path = PortFilePath();
            if (!File.Exists(path))
            {
                return null;
            }
            // Read-only open tolerates the owner's FileShare.Read write lock.
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                text = reader.ReadToEnd();
            }
            if (int.TryParse(text.Trim(), out int port) && port >= 1 && port <= 65535)
            {
                return port;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // Take exclusive ownership of the port file and advertise this broker's
    // port. Returns the held FileStream (keep it open for the broker's whole
    // lifetime — disposing it releases ownership) or null when another broker
    // already owns the file (write-share denied) or the path is unavailable.
    // The caller treats null as "another broker owns the port" only after a
    // failed detect; in practice the owner writes the file it just locked.
    internal static FileStream? AcquirePortFile(int port)
    {
        try
        {
            string path = PortFilePath();
            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            if (dir.Length > 0)
            {
                Directory.CreateDirectory(dir);
            }
            // FileShare.Read: others may read the port (detection) but cannot
            // take a second write lock, so two owners cannot coexist.
            var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush();
            return fs;
        }
        catch
        {
            return null;
        }
    }

    // Release ownership: dispose the held stream and best-effort delete the
    // file so a stale port is not left advertised after a clean shutdown.
    internal static void ReleasePortFile(FileStream? held)
    {
        try
        {
            held?.Dispose();
        }
        catch
        {
            // Best-effort.
        }
        try
        {
            string path = PortFilePath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort: the OS releases the handle on exit regardless; a
            // stale file is rejected by the health probe on the next launch.
        }
    }
}
