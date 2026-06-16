using System.Text.Json;

namespace PAXCookbook.Shared.Logging;

// NDJSON event model per logging-contract.md. Phase 2 ships a minimal
// writer that emits one JSON object per line. Rotation, retention,
// sanitization, and per-component file routing are deferred to later
// phases; this scaffold is enough for skeleton commands to emit a
// well-formed event without touching disk by default.
public sealed record NdjsonLogEvent
{
    public string Ts { get; init; } = DateTime.UtcNow.ToString("O");
    public string Component { get; init; } = "";
    public int Pid { get; init; } = Environment.ProcessId;
    public string SessionId { get; init; } = "";
    public string Event { get; init; } = "";
    public string Level { get; init; } = "info";
    public Dictionary<string, object?>? Fields { get; init; }
}

public static class NdjsonLogWriter
{
    public static string Serialize(NdjsonLogEvent ev)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        return JsonSerializer.Serialize(ev, opts);
    }

    // Writes a single NDJSON line to the supplied TextWriter. Caller owns
    // the writer and is responsible for thread safety, rotation, and
    // retention. Phase 2 callers pass Console.Out or a TempPath stream.
    public static void Write(TextWriter writer, NdjsonLogEvent ev)
    {
        writer.WriteLine(Serialize(ev));
    }
}
