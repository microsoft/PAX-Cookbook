// PAX Cookbook - SERVICE-ENABLE FIXED PATHS (cycle 62, helper only)
//
// WHAT THIS FILE IS. The ONE place that decides where the machine service
// payload lives. Every destination is FIXED and composed here from a single
// resolved base directory plus compile-time leaf names. There is no
// caller-supplied path, no environment variable, no registry value, no
// configuration file and no path derived from archive text.
//
// PROGRAM FILES x64 ONLY. The base directory comes exclusively from
// SHGetKnownFolderPath(FOLDERID_ProgramFilesX64). %ProgramFiles% and
// %ProgramW6432% are deliberately NOT read: an environment variable is
// caller-controlled state, and on a 32-bit process %ProgramFiles% names the
// x86 tree. The known-folder identifier is the only source that answers the
// question this product actually asks.
//
// WHAT THIS FILE CANNOT DO, by construction. It creates nothing, writes
// nothing, deletes nothing, starts no process, elevates nothing, opens no
// certificate store, key, credential vault or registry key, performs no service
// control, opens no socket, touches no PAX and starts no Bake. It resolves a
// base directory and composes strings.
//
// PRIVACY - FAIL CLOSED. Every failure is a bounded token. ToString() never
// carries a path or a native status.
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// The fixed leaf names of the service installation. Compile-time constants
/// only: nothing here is configurable, overridable or caller-supplied.
/// </summary>
internal static class ServiceEnableFixedPathContract
{
    /// <summary>The product root beneath Program Files (x64).</summary>
    internal const string RootFolderName = "PAXCookbook";

    /// <summary>The FINAL installed service directory, beneath the product root.</summary>
    internal const string FinalFolderName = "Service";

    /// <summary>The staging directory, a SIBLING of the final one on the same volume.</summary>
    internal const string StagingFolderName = "Service.staging";

    /// <summary>The machine-wide shared runtime folder beneath Program Files (x64).</summary>
    internal const string DotnetFolderName = "dotnet";

    /// <summary>The machine-wide shared runtime host that will run the framework-dependent service.</summary>
    internal const string DotnetHostFileName = "dotnet.exe";
}

/// <summary>Bounded outcome of fixed-path resolution.</summary>
internal enum ServiceEnablePathResolutionState
{
    Unspecified = 0,
    Resolved = 1,
    UnsupportedPlatform = 2,

    /// <summary>FOLDERID_ProgramFilesX64 could not be resolved, or is not a usable directory.</summary>
    ProgramFilesUnavailable = 3,

    /// <summary>A composed path did not canonicalize back to the exact expected fixed location.</summary>
    PathCanonicalizationRefused = 4,
}

/// <summary>
/// The four fixed locations of one service installation. Every value is derived
/// from the same resolved base directory; nothing here is settable.
/// </summary>
internal readonly struct ServiceEnableFixedPaths
{
    internal ServiceEnableFixedPaths(string root, string final, string staging, string runtimeHost)
    {
        Root = root;
        Final = final;
        Staging = staging;
        RuntimeHost = runtimeHost;
    }

    /// <summary>&lt;PFx64&gt;\PAXCookbook</summary>
    internal string Root { get; }

    /// <summary>&lt;PFx64&gt;\PAXCookbook\Service</summary>
    internal string Final { get; }

    /// <summary>&lt;PFx64&gt;\PAXCookbook\Service.staging</summary>
    internal string Staging { get; }

    /// <summary>&lt;PFx64&gt;\dotnet\dotnet.exe</summary>
    internal string RuntimeHost { get; }

    /// <summary>Carries the bounded type name only - never a path.</summary>
    public override string ToString() => nameof(ServiceEnableFixedPaths);
}

/// <summary>
/// The Program Files (x64) base directory. An interface ONLY so the focused
/// tests can point the whole fixed-path tree at an OS temp directory and prove
/// the composition rules without writing the real machine. Production always
/// uses <see cref="KnownFolderProgramFilesX64Resolver"/>, which takes no
/// argument and reads exactly one known-folder identifier.
/// </summary>
internal interface IServiceEnableProgramFilesResolver
{
    string? TryResolveProgramFilesX64();
}

/// <summary>
/// The real resolver. FOLDERID_ProgramFilesX64 and nothing else - no
/// environment variable, no registry value, no fallback.
/// </summary>
internal sealed class KnownFolderProgramFilesX64Resolver : IServiceEnableProgramFilesResolver
{
    // {6D809377-6AF0-444b-8957-A3773F02200E} - FOLDERID_ProgramFilesX64.
    private static readonly Guid FolderIdProgramFilesX64 =
        new("6D809377-6AF0-444B-8957-A3773F02200E");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public string? TryResolveProgramFilesX64()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            int hr = SHGetKnownFolderPath(in FolderIdProgramFilesX64, 0, IntPtr.Zero, out buffer);
            if (hr != 0 || buffer == IntPtr.Zero)
            {
                return null;
            }

            string? path = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }
}

/// <summary>
/// Composes the fixed installation paths. Pure string work over one resolved
/// base directory; it touches no filesystem.
/// </summary>
internal static class ServiceEnableFixedPathResolver
{
    internal static ServiceEnablePathResolutionState TryResolve(
        IServiceEnableProgramFilesResolver resolver, out ServiceEnableFixedPaths paths)
    {
        paths = default;

        if (!OperatingSystem.IsWindows())
        {
            return ServiceEnablePathResolutionState.UnsupportedPlatform;
        }

        if (resolver is null)
        {
            return ServiceEnablePathResolutionState.ProgramFilesUnavailable;
        }

        string? baseDirectory = resolver.TryResolveProgramFilesX64();
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return ServiceEnablePathResolutionState.ProgramFilesUnavailable;
        }

        try
        {
            string canonicalBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
            string root = Path.GetFullPath(
                Path.Combine(canonicalBase, ServiceEnableFixedPathContract.RootFolderName));
            string final = Path.GetFullPath(
                Path.Combine(root, ServiceEnableFixedPathContract.FinalFolderName));
            string staging = Path.GetFullPath(
                Path.Combine(root, ServiceEnableFixedPathContract.StagingFolderName));
            string runtimeDirectory = Path.GetFullPath(
                Path.Combine(canonicalBase, ServiceEnableFixedPathContract.DotnetFolderName));
            string runtimeHost = Path.GetFullPath(
                Path.Combine(runtimeDirectory, ServiceEnableFixedPathContract.DotnetHostFileName));

            // Every composed value must still be the exact fixed leaf inside the
            // exact expected parent. Anything else means the composition did not
            // survive canonicalization and is refused rather than trusted.
            if (!IsExactChild(canonicalBase, root, ServiceEnableFixedPathContract.RootFolderName)
                || !IsExactChild(root, final, ServiceEnableFixedPathContract.FinalFolderName)
                || !IsExactChild(root, staging, ServiceEnableFixedPathContract.StagingFolderName)
                || !IsExactChild(canonicalBase, runtimeDirectory, ServiceEnableFixedPathContract.DotnetFolderName)
                || !IsExactChild(
                    runtimeDirectory, runtimeHost, ServiceEnableFixedPathContract.DotnetHostFileName))
            {
                return ServiceEnablePathResolutionState.PathCanonicalizationRefused;
            }

            // Final and staging must be DIFFERENT siblings, or a move could
            // silently become an in-place overwrite.
            if (string.Equals(final, staging, StringComparison.OrdinalIgnoreCase))
            {
                return ServiceEnablePathResolutionState.PathCanonicalizationRefused;
            }

            paths = new ServiceEnableFixedPaths(root, final, staging, runtimeHost);
            return ServiceEnablePathResolutionState.Resolved;
        }
        catch (Exception)
        {
            return ServiceEnablePathResolutionState.ProgramFilesUnavailable;
        }
    }

    private static bool IsExactChild(string parent, string candidate, string expectedLeaf) =>
        string.Equals(Path.GetFileName(candidate), expectedLeaf, StringComparison.Ordinal)
        && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(candidate) ?? string.Empty),
            Path.TrimEndingDirectorySeparator(parent),
            StringComparison.Ordinal);
}
