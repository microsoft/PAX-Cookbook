using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.Ipc;

// Length-prefixed UTF-8 JSON frame I/O per paxcookbook-ipc-contract.md §6.
// 4-byte little-endian uint32 length, then exactly that many UTF-8 bytes.
// Frames > MaxFrameBytes are rejected at read time.
public static class IpcFrame
{
    public const int MaxFrameBytes = 4096;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public enum ReadError
    {
        Ok,
        LengthExceeded,
        TruncatedHeader,
        TruncatedBody,
        BadJson,
        BadShape
    }

    public sealed record ReadResult(ReadError Error, IpcRequest? Request, string? RawJson);

    public static ReadResult ReadRequest(Stream s)
    {
        Span<byte> hdr = stackalloc byte[4];
        int read = ReadExact(s, hdr);
        if (read != 4) return new ReadResult(ReadError.TruncatedHeader, null, null);
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
        if (len == 0 || len > MaxFrameBytes) return new ReadResult(ReadError.LengthExceeded, null, null);
        var body = new byte[len];
        int br = ReadExact(s, body);
        if (br != (int)len) return new ReadResult(ReadError.TruncatedBody, null, null);
        var json = Encoding.UTF8.GetString(body);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ReadResult(ReadError.BadShape, null, json);
            if (!root.TryGetProperty("verb", out var vEl) || vEl.ValueKind != JsonValueKind.String) return new ReadResult(ReadError.BadShape, null, json);
            if (!root.TryGetProperty("id", out var iEl) || iEl.ValueKind != JsonValueKind.String) return new ReadResult(ReadError.BadShape, null, json);
            string ts = root.TryGetProperty("ts", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString()! : "";
            return new ReadResult(ReadError.Ok, new IpcRequest(vEl.GetString()!, iEl.GetString()!, ts), json);
        }
        catch (JsonException)
        {
            return new ReadResult(ReadError.BadJson, null, json);
        }
    }

    public static void WriteResponse(Stream s, IpcResponse resp)
    {
        var json = JsonSerializer.Serialize(resp, JsonOpts);
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxFrameBytes) throw new InvalidOperationException("response exceeds frame cap");
        Span<byte> hdr = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr, (uint)body.Length);
        s.Write(hdr);
        s.Write(body);
        s.Flush();
    }

    public static void WriteRequest(Stream s, IpcRequest req)
    {
        var json = JsonSerializer.Serialize(req, JsonOpts);
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxFrameBytes) throw new InvalidOperationException("request exceeds frame cap");
        Span<byte> hdr = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr, (uint)body.Length);
        s.Write(hdr);
        s.Write(body);
        s.Flush();
    }

    public static IpcResponse? ReadResponse(Stream s)
    {
        Span<byte> hdr = stackalloc byte[4];
        if (ReadExact(s, hdr) != 4) return null;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
        if (len == 0 || len > MaxFrameBytes) return null;
        var body = new byte[len];
        if (ReadExact(s, body) != (int)len) return null;
        try
        {
            return JsonSerializer.Deserialize<IpcResponse>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ReadExact(Stream s, Span<byte> buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf.Slice(total));
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
