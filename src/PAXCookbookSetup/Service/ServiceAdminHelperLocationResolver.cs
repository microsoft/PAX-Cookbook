// PAX Cookbook - SIBLING SERVICE ADMIN HELPER LOCATION RESOLVER (cycle 61)
//
// WHAT THIS FILE IS. Launch resolution for ONE fixed sibling file: the
// directly launched service administrative helper that ships beside the
// installed Setup runtime. It answers exactly one question - "where is my
// helper" - and refuses in every case where the answer is not unambiguous.
//
// WHY IT IS NOT CYCLE 59's RESOLVER. Cycle 59's IsDotnetHostShape resolver
// answers "how do I relaunch THIS Setup elevated". It resolves the CURRENT
// image, never a sibling, and reusing it as though it resolved a sibling would
// silently accept whatever executable happened to be running. This resolver
// requires the installed framework-dependent dotnet-hosted Setup shape and then
// derives ONE fixed leaf name from that installed location.
//
// IT IS UNINVOKED IN THIS SLICE. No production caller exists, deliberately: the
// helper it locates is UNSIGNED PRERELEASE CODE and locating something is one
// step away from launching it. Compile-time existence is authorized here;
// reachability is not (the cycle-51 / cycle-58 precedent).
//
// TRUST. Resolving a path establishes NOTHING about the trustworthiness of the
// file found. The installed Setup directory is under %LOCALAPPDATA% and is
// USER-WRITABLE, so a resolved helper is exactly as trustworthy as its outer
// Authenticode signature - which, in a prerelease build, does not exist. This
// type must never be read as evidence that the located file is safe to elevate.
//
// WHAT THIS FILE CANNOT DO, by construction. It starts no process, elevates
// nothing, writes no file, reads no file CONTENT, opens no registry key, no
// certificate store, no key, no ACL and no credential vault, creates/changes/
// starts/stops no service, opens no socket, touches no PAX and starts no Bake.
// It reads file-system METADATA (existence, attributes) and nothing more.
//
// PRIVACY - FAIL CLOSED. Every failure is a bounded token. ToString() never
// carries a path, an attribute set, an exception or a native status.
using System;
using System.IO;
using System.Reflection;

namespace PAXCookbookSetup.Service;

/// <summary>
/// The three fixed names this resolver is allowed to know. Compile-time
/// constants only: no path composition, no environment read, no filesystem
/// access. Compile-linked into the helper assembly so the helper and the
/// launcher can never drift into two spellings of the same file name.
/// </summary>
internal static class ServiceAdminHelperLocationContract
{
    /// <summary>The Microsoft-signed host that runs the installed Setup.</summary>
    internal const string DotnetHostFileName = "dotnet.exe";

    /// <summary>The installed framework-dependent Setup managed entry assembly.</summary>
    internal const string SetupAssemblyFileName = "PAXCookbookSetup.dll";

    /// <summary>
    /// The ONE fixed leaf name of the directly launched, self-contained service
    /// administrative helper. It is a sibling of the Setup assembly above.
    /// </summary>
    internal const string HelperFileName = "PAXCookbookServiceAdminHelper.exe";
}

/// <summary>
/// Bounded outcome of sibling helper resolution. Zero is the permanent, safe
/// default, so an uninitialised value can never read as resolved.
/// </summary>
internal enum ServiceAdminHelperLocationOutcome
{
    Unspecified = 0,
    Resolved = 1,

    /// <summary>Not Windows. Nothing is probed.</summary>
    UnsupportedPlatform = 2,

    /// <summary>
    /// The running image is not the dotnet host, or the entry assembly is not
    /// the installed Setup assembly. The installed dotnet-hosted Setup shape is
    /// REQUIRED: any other shape (a self-contained stage-1 apphost, a test host,
    /// a renamed image) is refused rather than guessed at.
    /// </summary>
    NotDotnetHostedSetup = 3,

    /// <summary>The installed Setup directory could not be determined.</summary>
    SetupLocationUnavailable = 4,

    /// <summary>The fixed sibling name does not exist.</summary>
    HelperMissing = 5,

    /// <summary>Something exists at the fixed sibling name but is not a regular file.</summary>
    HelperNotAFile = 6,

    /// <summary>The Setup directory or the helper itself is a reparse point.</summary>
    ReparsePointRefused = 7,

    /// <summary>The composed path did not canonicalize back to the expected fixed sibling.</summary>
    PathCanonicalizationRefused = 8,

    /// <summary>A bounded access or stability failure. Never a partial success.</summary>
    Unavailable = 9,
}

/// <summary>
/// The resolution result. The resolved path is available ONLY on success and
/// ONLY to this assembly; <see cref="ToString"/> carries the bounded token and
/// never the path.
/// </summary>
internal readonly struct ServiceAdminHelperLocationResult
{
    private ServiceAdminHelperLocationResult(ServiceAdminHelperLocationOutcome outcome, string? resolvedPath)
    {
        Outcome = outcome;
        ResolvedPath = resolvedPath;
    }

    internal ServiceAdminHelperLocationOutcome Outcome { get; }

    /// <summary>Non-null only when <see cref="Outcome"/> is Resolved.</summary>
    internal string? ResolvedPath { get; }

    internal static ServiceAdminHelperLocationResult Resolved(string path) =>
        new(ServiceAdminHelperLocationOutcome.Resolved, path);

    internal static ServiceAdminHelperLocationResult Refused(ServiceAdminHelperLocationOutcome outcome) =>
        new(outcome == ServiceAdminHelperLocationOutcome.Resolved
            ? ServiceAdminHelperLocationOutcome.Unavailable
            : outcome, null);

    public override string ToString() => Outcome.ToString();
}

/// <summary>
/// The FIXED sibling resolver. <see cref="TryResolveSiblingHelper"/> takes NO
/// argument: there is no caller-supplied path, directory, leaf name, delegate,
/// options object or settable static field, so a caller can never redirect what
/// is resolved.
/// </summary>
internal static class ServiceAdminHelperLocationResolver
{
    internal static ServiceAdminHelperLocationResult TryResolveSiblingHelper()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceAdminHelperLocationResult.Refused(
                ServiceAdminHelperLocationOutcome.UnsupportedPlatform);
        }

        try
        {
            string? hostImage = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(hostImage)
                || !string.Equals(
                    Path.GetFileName(hostImage),
                    ServiceAdminHelperLocationContract.DotnetHostFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ServiceAdminHelperLocationResult.Refused(
                    ServiceAdminHelperLocationOutcome.NotDotnetHostedSetup);
            }

            string? entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssemblyPath)
                || !string.Equals(
                    Path.GetFileName(entryAssemblyPath),
                    ServiceAdminHelperLocationContract.SetupAssemblyFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ServiceAdminHelperLocationResult.Refused(
                    ServiceAdminHelperLocationOutcome.NotDotnetHostedSetup);
            }

            string? setupDirectory = Path.GetDirectoryName(Path.GetFullPath(entryAssemblyPath));
            if (string.IsNullOrWhiteSpace(setupDirectory))
            {
                return ServiceAdminHelperLocationResult.Refused(
                    ServiceAdminHelperLocationOutcome.SetupLocationUnavailable);
            }

            return ResolveWithin(setupDirectory);
        }
        catch (Exception)
        {
            return ServiceAdminHelperLocationResult.Refused(
                ServiceAdminHelperLocationOutcome.Unavailable);
        }
    }

    /// <summary>
    /// The disclosed test seam. INTERNAL, and it accepts a DIRECTORY only: the
    /// helper leaf name always comes from
    /// <see cref="ServiceAdminHelperLocationContract.HelperFileName"/> and can
    /// never be supplied. It exists so the focused tests can exercise the real
    /// canonicalization, reparse-point and file-type rules against an OS temp
    /// directory without an installed product. Same bounding as the cycle-58
    /// anchor-store seam.
    /// </summary>
    internal static ServiceAdminHelperLocationResult ResolveWithin(string setupDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceAdminHelperLocationResult.Refused(
                ServiceAdminHelperLocationOutcome.UnsupportedPlatform);
        }

        if (string.IsNullOrWhiteSpace(setupDirectory))
        {
            return ServiceAdminHelperLocationResult.Refused(
                ServiceAdminHelperLocationOutcome.SetupLocationUnavailable);
        }

        try
        {
            string canonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(setupDirectory));
            var directoryInfo = new DirectoryInfo(canonicalDirectory);
            if (!directoryInfo.Exists)
            {
                return ServiceAdminHelperLocationResult.Refused(
                    ServiceAdminHelperLocationOutcome.SetupLocationUnavailable);
            }

            if (directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return ServiceAdminHelperLocationResult.Refused(
                    ServiceAdminHelperLocationOutcome.ReparsePointRefused);
            }

            string composed = Path.Combine(canonicalDirectory, ServiceAdminHelperLocationContract.HelperFileName);
            string canonical = Path.GetFullPath(composed);

            // The canonical form must still be the fixed leaf inside the exact
            // canonical directory. Anything else means the composition did not
            // survive canonicalization and is refused rather than trusted.
            if (!string.Equals(canonical, composed, StringComparison.Ordinal)
                || !string.Equals(
                    Path.GetFileName(canonical),
                    ServiceAdminHelperLocationContract.HelperFileName,
                    StringComparison.Ordinal)
                || !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(canonical) ?? string.Empty),
                    canonicalDirectory,
                    StringComparison.Ordinal))
            {
                return ServiceAdminHelperLocationResult.Refused(
                    ServiceAdminHelperLocationOutcome.PathCanonicalizationRefused);
            }

            if (Directory.Exists(canonical))
            {
                return ServiceAdminHelperLocationResult.Refused(
                    ServiceAdminHelperLocationOutcome.HelperNotAFile);
            }

            var fileInfo = new FileInfo(canonical);
            if (!fileInfo.Exists)
            {
                return ServiceAdminHelperLocationResult.Refused(
                    ServiceAdminHelperLocationOutcome.HelperMissing);
            }

            if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return ServiceAdminHelperLocationResult.Refused(
                    ServiceAdminHelperLocationOutcome.ReparsePointRefused);
            }

            return ServiceAdminHelperLocationResult.Resolved(canonical);
        }
        catch (Exception)
        {
            return ServiceAdminHelperLocationResult.Refused(
                ServiceAdminHelperLocationOutcome.Unavailable);
        }
    }
}
