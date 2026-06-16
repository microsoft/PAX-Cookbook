using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PAXCookbook.App;

// One-time, locally-staged file-open import handoff.
//
// When the Windows shell hands the EXE a double-clicked .paxlite / .pax file,
// the absolute path must never cross the WebView2 / HTTP boundary. Instead the
// path is staged on disk as a bounded, single-use, expiring "ticket". The
// browser learns only that an import is pending (id / kind / fileName), then
// consumes the ticket through an authenticated, lock-gated route that reads the
// staged file locally and returns its text (for .paxlite and .pax) — never its
// path.
//
// This type stages, lists, peeks, and consumes tickets. It never invokes PAX,
// never reads or mutates the PAX bytes, and never touches cook / scheduler
// state. For both .paxlite and .pax the staged file text is read and handed to
// the browser importer, which validates it; the file itself is never
// interpreted, executed, or modified.
internal static class ImportHandoffQueue
{
    private const string DirectoryName = "ImportHandoff";
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(10);
    // Upper bound on the staged import file the consume route will read into
    // memory. A recipe handoff file is small JSON; anything larger is rejected
    // (413) rather than read, so a malformed or hostile association target can
    // never be slurped wholesale. This bounds the read only — it never alters
    // or runs the file.
    private const long MaxImportBytes = 5L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex TicketIdPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    // Folder that holds staged tickets, alongside the per-workspace runtime
    // state. Callers resolve this once at startup and thread it through.
    internal static string ResolveDir(string workspacePath) =>
        Path.Combine(workspacePath, DirectoryName);

    // Stage a double-clicked file as a one-time, expiring ticket. Returns the
    // ticket id, or null when the request is not eligible: no usable path, a
    // missing file, or an extension other than .paxlite / .pax.
    internal static string? Enqueue(string handoffDir, FileOpenRequest request)
    {
        string kind = request.Kind;
        if (kind != "paxlite" && kind != "pax")
        {
            return null;
        }
        if (!request.Exists)
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(request.Path);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(handoffDir);
            PurgeExpired(handoffDir);

            string id = Guid.NewGuid().ToString("N");
            DateTime nowUtc = DateTime.UtcNow;
            var ticket = new HandoffTicket(
                Id: id,
                Kind: kind,
                Path: fullPath,
                FileName: Path.GetFileName(fullPath),
                CreatedUtc: nowUtc,
                ExpiresUtc: nowUtc + TicketLifetime);

            string json = JsonSerializer.Serialize(ticket, SerializerOptions);
            File.WriteAllText(TicketFilePath(handoffDir, id), json, Utf8NoBom);
            return id;
        }
        catch
        {
            return null;
        }
    }

    // Pending tickets (non-expired, file still present), newest first, shaped
    // for the GET endpoint. The absolute path is NEVER projected — only id,
    // kind, and fileName leave the process.
    internal static List<object> ListPending(string handoffDir)
    {
        var result = new List<object>();
        foreach (HandoffTicket ticket in LoadValidTickets(handoffDir))
        {
            result.Add(new { id = ticket.Id, kind = ticket.Kind, fileName = ticket.FileName });
        }
        return result;
    }

    // Newest valid pending ticket id, for the in-process WebView navigation
    // decision (same assembly, no HTTP). Returns null when there is nothing to
    // route to.
    internal static string? PeekLatestId(string handoffDir)
    {
        foreach (HandoffTicket ticket in LoadValidTickets(handoffDir))
        {
            return ticket.Id;
        }
        return null;
    }

    // Consume a ticket one time. Validates the id shape, expiry, and the staged
    // file, then deletes the ticket (single-use). For both .paxlite and .pax
    // the staged file text is read locally (size-bounded) and returned for the
    // browser importer to validate. The file is never interpreted, executed, or
    // modified, and the absolute path is never returned.
    internal static (int status, object body) HandleConsume(string handoffDir, object? requestBody)
    {
        string? id = ExtractId(requestBody);
        if (string.IsNullOrEmpty(id))
        {
            return (StatusCodes.Status400BadRequest, new { error = "invalid_request", message = "A ticket id is required." });
        }
        if (!IsValidTicketId(id))
        {
            return (StatusCodes.Status400BadRequest, new { error = "invalid_ticket_id" });
        }

        string ticketFile = TicketFilePath(handoffDir, id);
        HandoffTicket? loaded = TryLoad(ticketFile);
        if (loaded is not HandoffTicket ticket)
        {
            return (StatusCodes.Status404NotFound, new { error = "ticket_not_found", id });
        }

        if (DateTime.UtcNow > ticket.ExpiresUtc)
        {
            TryDelete(ticketFile);
            return (StatusCodes.Status410Gone, new { error = "ticket_expired", id });
        }

        bool fileStillPresent;
        try
        {
            fileStillPresent = File.Exists(ticket.Path);
        }
        catch
        {
            fileStillPresent = false;
        }

        if (!fileStillPresent)
        {
            TryDelete(ticketFile);
            return (StatusCodes.Status410Gone, new { error = "file_unavailable", id, fileName = ticket.FileName });
        }

        if (ticket.Kind != "paxlite" && ticket.Kind != "pax")
        {
            TryDelete(ticketFile);
            return (StatusCodes.Status400BadRequest, new { error = "unsupported_kind", id });
        }

        // Bound the read: reject an oversized staged file rather than slurping
        // it into memory. The file is measured, never modified.
        try
        {
            long length = new FileInfo(ticket.Path).Length;
            if (length > MaxImportBytes)
            {
                TryDelete(ticketFile);
                return (StatusCodes.Status413PayloadTooLarge, new { error = "file_too_large", id, fileName = ticket.FileName });
            }
        }
        catch
        {
            TryDelete(ticketFile);
            return (StatusCodes.Status500InternalServerError, new { error = "file_read_failed", id, fileName = ticket.FileName });
        }

        string text;
        try
        {
            text = File.ReadAllText(ticket.Path);
        }
        catch
        {
            TryDelete(ticketFile);
            return (StatusCodes.Status500InternalServerError, new { error = "file_read_failed", id, fileName = ticket.FileName });
        }

        TryDelete(ticketFile);
        return (StatusCodes.Status200OK, new { id = ticket.Id, kind = ticket.Kind, fileName = ticket.FileName, text });
    }

    private static string TicketFilePath(string handoffDir, string id) =>
        Path.Combine(handoffDir, id + ".json");

    private static bool IsValidTicketId(string id) => TicketIdPattern.IsMatch(id);

    private static string? ExtractId(object? requestBody)
    {
        if (requestBody is Dictionary<string, object?> dict &&
            dict.TryGetValue("id", out object? raw) &&
            raw is string s)
        {
            return s.Trim();
        }
        return null;
    }

    // Valid, non-expired tickets whose staged file is still present, newest
    // first (by ticket creation time).
    private static List<HandoffTicket> LoadValidTickets(string handoffDir)
    {
        var tickets = new List<HandoffTicket>();
        if (!Directory.Exists(handoffDir))
        {
            return tickets;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(handoffDir, "*.json");
        }
        catch
        {
            return tickets;
        }

        DateTime nowUtc = DateTime.UtcNow;
        foreach (string file in files)
        {
            HandoffTicket? loaded = TryLoad(file);
            if (loaded is not HandoffTicket ticket)
            {
                continue;
            }
            if (nowUtc > ticket.ExpiresUtc)
            {
                continue;
            }
            try
            {
                if (!File.Exists(ticket.Path))
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }
            tickets.Add(ticket);
        }

        tickets.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return tickets;
    }

    private static HandoffTicket? TryLoad(string ticketFile)
    {
        try
        {
            if (!File.Exists(ticketFile))
            {
                return null;
            }
            string json = File.ReadAllText(ticketFile);
            HandoffTicket? ticket = JsonSerializer.Deserialize<HandoffTicket>(json, SerializerOptions);
            if (ticket is not HandoffTicket value)
            {
                return null;
            }
            if (string.IsNullOrEmpty(value.Id) ||
                string.IsNullOrEmpty(value.Path) ||
                string.IsNullOrEmpty(value.Kind))
            {
                return null;
            }
            return value;
        }
        catch
        {
            return null;
        }
    }

    private static void PurgeExpired(string handoffDir)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(handoffDir, "*.json");
        }
        catch
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        foreach (string file in files)
        {
            HandoffTicket? loaded = TryLoad(file);
            if (loaded is HandoffTicket ticket && nowUtc <= ticket.ExpiresUtc)
            {
                continue;
            }
            TryDelete(file);
        }
    }

    private static void TryDelete(string ticketFile)
    {
        try
        {
            if (File.Exists(ticketFile))
            {
                File.Delete(ticketFile);
            }
        }
        catch
        {
            // A stale ticket that cannot be deleted will expire and be ignored.
        }
    }
}

// On-disk shape of a staged import ticket. Persisted locally only; the Path is
// never projected over HTTP.
internal sealed record HandoffTicket(
    string Id,
    string Kind,
    string Path,
    string FileName,
    DateTime CreatedUtc,
    DateTime ExpiresUtc);
