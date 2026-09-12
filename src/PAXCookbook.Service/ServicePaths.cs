namespace PAXCookbook.Service;

/// <summary>
/// The path seam, and the TWO-ROOT namespace boundary (cycle 63R).
///
/// THERE ARE TWO ROOTS, AND THEY ARE NOT INTERCHANGEABLE.
///
///   <see cref="MetadataRoot"/> is the fixed machine metadata directory. It
///   holds the installation anchor and the ownership ledger - the records that
///   prove WHO owns the installation. The service account is granted Traverse
///   only there and must never write it.
///
///   <see cref="Root"/> is the RUNTIME root: the fixed
///   <c>RuntimeDataFolderName</c> child of <see cref="MetadataRoot"/>. It is the
///   only machine location the service account may write, and it holds only the
///   status document, the heartbeat document, the startup-probe request and
///   result, and the service's own same-directory staging files.
///
/// The separation is a SAFETY boundary, not tidiness: before it, the service
/// held writable authority over the namespace containing ownership records.
///
/// This type performs pure string composition only. It never reads
/// <see cref="Environment.SpecialFolder"/>, an environment variable, a
/// configuration file, a command-line argument, or a machine configuration key.
/// The single production read of <c>CommonApplicationData</c> lives in a private method of
/// <c>Program</c>, which is unreachable from the test assembly even through
/// <c>InternalsVisibleTo</c>. Tests therefore cannot reach the production root
/// by construction; they must build a root from <c>Path.GetTempPath()</c>.
/// </summary>
internal sealed class ServicePaths
{
    private ServicePaths(string metadataRoot)
    {
        MetadataRoot = metadataRoot;
        Root = Path.Combine(metadataRoot, ServiceContract.RuntimeDataFolderName);

        // RUNTIME namespace: everything this service writes.
        StatusFile = Path.Combine(Root, ServiceContract.StatusFileName);
        HeartbeatFile = Path.Combine(Root, ServiceContract.HeartbeatFileName);
        ProbeRequestFile = Path.Combine(Root, ServiceContract.ProbeRequestFileName);
        ProbeResultFile = Path.Combine(Root, ServiceContract.ProbeResultFileName);

        // METADATA namespace: ownership records. Composed so the boundary is
        // visible in one place; this assembly never opens the path.
        OwnershipLedgerFile = Path.Combine(metadataRoot, ServiceContract.OwnershipLedgerFileName);
    }

    /// <summary>
    /// The METADATA root. Ownership records sit directly beneath it and the
    /// service account holds Traverse only.
    /// </summary>
    internal string MetadataRoot { get; }

    /// <summary>
    /// The RUNTIME root - the fixed writable child of <see cref="MetadataRoot"/>.
    /// Every document this service writes lives beneath it.
    /// </summary>
    internal string Root { get; }

    internal string StatusFile { get; }

    internal string HeartbeatFile { get; }

    internal string ProbeRequestFile { get; }

    internal string ProbeResultFile { get; }

    /// <summary>
    /// Composed for boundary clarity and so tests can pin it OUTSIDE the runtime
    /// root. Nothing in this assembly opens, reads or writes it.
    /// </summary>
    internal string OwnershipLedgerFile { get; }

    /// <summary>
    /// Composes the fixed machine METADATA root beneath an already-resolved base
    /// directory. Pure: the caller supplies the base, so this method reads nothing.
    /// </summary>
    internal static string ComposeMetadataRoot(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("A base directory is required.", nameof(baseDirectory));
        }

        return Path.Combine(
            baseDirectory,
            ServiceContract.MachineRootFolderName,
            ServiceContract.MachineDataFolderName);
    }

    /// <summary>
    /// Binds both roots and every fixed leaf beneath an already-resolved METADATA
    /// root. The runtime root is DERIVED, never supplied, so no caller can point
    /// runtime writes at the metadata directory.
    /// </summary>
    internal static ServicePaths ForMetadataRoot(string metadataRoot)
    {
        if (string.IsNullOrWhiteSpace(metadataRoot))
        {
            throw new ArgumentException("A metadata root directory is required.", nameof(metadataRoot));
        }

        return new ServicePaths(Path.GetFullPath(metadataRoot));
    }
}
