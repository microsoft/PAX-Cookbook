using System.Text;
using System.Text.Json;

namespace PAXCookbook.Service;

/// <summary>
/// CYCLE 64. The runtime-document seam.
///
/// EVERY OPERATION IS PURPOSE-SPECIFIC AND NONE OF THEM TAKES A PATH. That is the
/// whole point. A general "write this document to that path" seam would hand the
/// worker - and anything that ever reached the worker - arbitrary authority over
/// the filesystem, which is precisely the authority the two-root boundary exists
/// to deny. Here the destination of every operation is fixed by the ONE production
/// implementation and cannot be influenced by a caller.
///
/// The three predicates are equally bounded: each answers a single yes/no question
/// about a single fixed document, takes no argument, and returns no path.
/// </summary>
internal interface IRuntimeDocumentWriter
{
    /// <summary>True when the fixed runtime root already exists. Never creates it.</summary>
    bool RuntimeRootExists();

    /// <summary>True when the fixed probe RESULT document already exists.</summary>
    bool ProbeResultExists();

    /// <summary>True when the fixed probe REQUEST document already exists.</summary>
    bool ProbeRequestExists();

    void WriteStatus(ServiceStatusDocument document);

    void WriteHeartbeat(ServiceHeartbeatDocument document);

    void WriteProbeResult(StartupProbeResultDocument document);

    /// <summary>Consumes the fixed probe REQUEST document. It is the only removal in the seam.</summary>
    void RemoveProbeRequest();
}

/// <summary>
/// The ONE production implementation: the real atomic filesystem writer, behaviourally
/// identical to the private WriteAtomicJson it replaces. Same-directory unique staging
/// file, UTF-8 without BOM, atomic replace, staging cleanup on failure, and no
/// exception text ever persisted.
///
/// It holds the resolved <see cref="ServicePaths"/> privately. There is no property,
/// no accessor and no operation through which a caller can supply or read back a
/// destination, so binding this writer is not the same as granting filesystem reach.
/// </summary>
internal sealed class AtomicRuntimeDocumentWriter : IRuntimeDocumentWriter
{
    private const string StagingMarker = ".staging-";

    private readonly ServicePaths _paths;

    internal AtomicRuntimeDocumentWriter(ServicePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// CYCLE 64. Composes the per-attempt staging name for a destination: a fresh
    /// identity every call, always in the destination's OWN directory, always
    /// prefixed by the destination's own file name.
    ///
    /// It is internal and PURE - it performs no filesystem access and reports nothing
    /// about what a running service actually did. It exists so a focused test can
    /// prove uniqueness and same-directory placement directly, instead of inferring
    /// them from filesystem notifications. Nothing outside this assembly can call it,
    /// and the service process never emits, logs or persists its result.
    /// </summary>
    internal static string ComposeStagingPath(string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;

        return Path.Combine(
            directory,
            Path.GetFileName(destination) + StagingMarker + Guid.NewGuid().ToString("N"));
    }

    public bool RuntimeRootExists() => Directory.Exists(_paths.Root);

    public bool ProbeResultExists() => File.Exists(_paths.ProbeResultFile);

    public bool ProbeRequestExists() => File.Exists(_paths.ProbeRequestFile);

    public void WriteStatus(ServiceStatusDocument document) =>
        WriteAtomicJson(_paths.StatusFile, document);

    public void WriteHeartbeat(ServiceHeartbeatDocument document) =>
        WriteAtomicJson(_paths.HeartbeatFile, document);

    public void WriteProbeResult(StartupProbeResultDocument document) =>
        WriteAtomicJson(_paths.ProbeResultFile, document);

    public void RemoveProbeRequest() => File.Delete(_paths.ProbeRequestFile);

    private static void WriteAtomicJson<T>(string destination, T document)
    {
        var staging = ComposeStagingPath(destination);
        var payload = JsonSerializer.Serialize(document, ServiceContract.JsonOptions);

        try
        {
            File.WriteAllText(staging, payload, new UTF8Encoding(false));
            File.Move(staging, destination, overwrite: true);
        }
        catch
        {
            TryRemove(staging);
            throw;
        }
    }

    private static void TryRemove(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort staging cleanup.
        }
    }
}
