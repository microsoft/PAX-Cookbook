using System.IO;

namespace PAXCookbookSetup.Payload;

public sealed class DirectoryPayloadSourceResolver : IPayloadSourceResolver
{
    private readonly string _payloadRoot;
    public DirectoryPayloadSourceResolver(string payloadRoot) { _payloadRoot = payloadRoot; }
    public PayloadSource Resolve()
    {
        if (string.IsNullOrWhiteSpace(_payloadRoot))
            return new PayloadSource(false, null, "directory", null, "payload-root is empty");
        var full = Path.GetFullPath(_payloadRoot);
        if (!Directory.Exists(full))
            return new PayloadSource(false, null, "directory", null,
                $"payload root does not exist: {full}");
        if (!File.Exists(Path.Combine(full, "manifest.json")))
            return new PayloadSource(false, null, "directory", null,
                $"manifest.json not found in payload root: {full}");
        return new PayloadSource(true, full, "directory", null, null);
    }
}
