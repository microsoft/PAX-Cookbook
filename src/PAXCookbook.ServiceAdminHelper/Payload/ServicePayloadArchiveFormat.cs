// PAX Cookbook - SERVICE PAYLOAD ARCHIVE FORMAT (cycle 61)
//
// SECURITY CLASSIFICATION. Everything described here is an inner CORRUPTION
// and CONSISTENCY contract, never a tamper contract. The manifest that
// describes these members travels inside the same archive the members live in,
// and that archive travels inside this UNSIGNED PRERELEASE helper, so a
// consistent archive proves only that nothing was truncated or bit-rotted. An
// unsigned helper does not resist replacement by a local user; UAC may show an
// unknown publisher; prerelease validation proves functionality, not production
// tamper resistance. That is unacceptable for GA, and GA activation stays
// BLOCKED until this exact helper is Authenticode signed and the expected
// publisher policy is configured and verified (cycle-60 ruling).
//
// WHAT THIS FILE IS. Compile-time constants only: the fixed embedded-resource
// name, the fixed inner manifest name, the fixed archive shape, and the
// EMPIRICALLY FROZEN required-member list. It composes no path, reads no
// environment variable, touches no filesystem and exposes no settable value.
//
// HOW THE MEMBER LIST WAS FROZEN. It is the literal output inventory of
//   dotnet publish src\PAXCookbook.Service\PAXCookbook.Service.csproj
//     -c Release --self-contained false -p:UseAppHost=false
//     -p:DebugType=none -p:DebugSymbols=false
// captured in this cycle's evidence. It was NOT predicted. Note in particular
// that PAXCookbook.Shared.dll is NOT a member: the service compile-links its
// Shared contracts rather than referencing that assembly, so assuming it would
// be present would have frozen a wrong contract.
using System;
using System.Collections.Generic;
using System.IO.Compression;

namespace PAXCookbook.ServiceAdminHelper.Payload;

/// <summary>
/// The fixed, closed description of the deterministic embedded service payload
/// archive. Every value here is a compile-time constant or a frozen readonly
/// list; nothing is configurable, overridable or caller-supplied.
/// </summary>
internal static class ServicePayloadArchiveFormat
{
    /// <summary>
    /// The ONE managed-resource logical name the helper will look for. It must
    /// stay byte-identical with the LogicalName in
    /// PAXCookbook.ServiceAdminHelper.csproj. Any other resource name is not a
    /// payload as far as this helper is concerned.
    /// </summary>
    internal const string ResourceName = "PAXCookbook.ServicePayload.zip";

    /// <summary>The single permitted non-runtime member of the archive.</summary>
    internal const string ManifestEntryName = "service-payload-manifest.json";

    /// <summary>The only inner manifest schema version this helper accepts.</summary>
    internal const int SchemaVersion = 1;

    /// <summary>The only target OS this helper accepts.</summary>
    internal const string TargetOs = "windows";

    /// <summary>The only target architecture this helper accepts.</summary>
    internal const string TargetArch = "x64";

    /// <summary>
    /// The fixed payload-relative install location of the helper itself. Exactly
    /// ONE self-contained helper ships, at exactly this path. Derived from the
    /// linked launch contract so the packaging path and the launch resolver can
    /// never drift into two spellings.
    /// </summary>
    internal const string HelperPayloadRelativePath =
        "Setup/" + PAXCookbookSetup.Service.ServiceAdminHelperLocationContract.HelperFileName;

    /// <summary>
    /// The fixed ZIP entry timestamp. DOS time has a 1980 floor and 2-second
    /// precision, so 1980-01-01T00:00:00 is the only value that is both legal
    /// and exactly representable, which makes it reproducible on any machine in
    /// any time zone. The offset is fixed at zero so the stored clock value can
    /// never vary with the builder's local time.
    /// </summary>
    internal static DateTimeOffset FixedEntryTimestamp { get; } =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The single compression method used for every member.
    /// <see cref="CompressionLevel.NoCompression"/> stores members verbatim
    /// (ZIP method 0). Deflate output is only guaranteed stable for a given
    /// runtime version, so storing verbatim is the choice that keeps the archive
    /// byte-reproducible across runtimes as well as across runs.
    /// </summary>
    internal const CompressionLevel FixedCompressionLevel = CompressionLevel.NoCompression;

    /// <summary>The manifest is UTF-8 WITHOUT a byte-order mark.</summary>
    internal const bool ManifestUsesByteOrderMark = false;

    /// <summary>The manifest uses LF line endings only - never CR, never CRLF.</summary>
    internal const string ManifestNewline = "\n";

    /// <summary>The only path separator permitted in an entry or manifest name.</summary>
    internal const char PathSeparator = '/';

    /// <summary>
    /// The closed extension allow-list. The frozen member list contains only
    /// managed assemblies and runtime JSON, so anything else is refused before
    /// membership is even considered.
    /// </summary>
    internal static IReadOnlyList<string> AllowedFileExtensions { get; } = new[]
    {
        ".dll",
        ".json",
    };

    /// <summary>
    /// Extensions that must never appear. This is defence in depth on top of
    /// exact membership: it names apphosts, symbols, every script host the
    /// product could plausibly be asked to carry, certificate and key material,
    /// and web assets, so a refusal says WHY rather than merely "unexpected".
    /// </summary>
    internal static IReadOnlyList<string> ProhibitedFileExtensions { get; } = new[]
    {
        ".exe",
        ".pdb",
        ".ps1",
        ".psm1",
        ".psd1",
        ".cmd",
        ".bat",
        ".vbs",
        ".js",
        ".msi",
        ".pfx",
        ".p12",
        ".cer",
        ".crt",
        ".der",
        ".pem",
        ".key",
        ".snk",
        ".html",
        ".htm",
        ".css",
        ".map",
    };

    /// <summary>
    /// Exact artifact names that must never appear, matched case-insensitively
    /// on the LEAF name. These are named EXACTLY rather than by substring on
    /// purpose: a naive "contains pax" rule would reject the legitimate member
    /// PAXCookbook.Service.dll, which is precisely the kind of guess that has
    /// produced false failures in this project before.
    /// </summary>
    internal static IReadOnlyList<string> ProhibitedLeafNames { get; } = new[]
    {
        // PAX engine bytes. Constraint 7: the engine never travels in a bundle.
        "PAX_Purview_Audit_Log_Processor.ps1",
        // MSAL / WAM broker surface. The service never runs an interactive
        // identity provider (constraint 17).
        "Microsoft.Identity.Client.dll",
        "Microsoft.Identity.Client.Broker.dll",
        "Microsoft.Identity.Client.NativeInterop.dll",
        "msalruntime.dll",
        "msalruntime_arm64.dll",
        // WebView2. The service has no UI.
        "Microsoft.Web.WebView2.Core.dll",
        "Microsoft.Web.WebView2.WinForms.dll",
        "Microsoft.Web.WebView2.Wpf.dll",
        "WebView2Loader.dll",
        // Recipes, Chef's Keys and secrets never travel in a payload.
        "recipes.json",
        "recipe.json",
        "chef-keys.json",
        "chefkeys.json",
        "secrets.json",
        "credentials.json",
    };

    /// <summary>
    /// THE FROZEN REQUIRED MEMBERS, in ordinal order. Derived empirically from
    /// this cycle's real service publish - 36 files, no apphost, no symbols and
    /// no PAXCookbook.Shared.dll.
    /// </summary>
    internal static IReadOnlyList<string> RequiredMemberNames { get; } = new[]
    {
        "Microsoft.Extensions.Configuration.Abstractions.dll",
        "Microsoft.Extensions.Configuration.Binder.dll",
        "Microsoft.Extensions.Configuration.CommandLine.dll",
        "Microsoft.Extensions.Configuration.EnvironmentVariables.dll",
        "Microsoft.Extensions.Configuration.FileExtensions.dll",
        "Microsoft.Extensions.Configuration.Json.dll",
        "Microsoft.Extensions.Configuration.UserSecrets.dll",
        "Microsoft.Extensions.Configuration.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.DependencyInjection.dll",
        "Microsoft.Extensions.Diagnostics.Abstractions.dll",
        "Microsoft.Extensions.Diagnostics.dll",
        "Microsoft.Extensions.FileProviders.Abstractions.dll",
        "Microsoft.Extensions.FileProviders.Physical.dll",
        "Microsoft.Extensions.FileSystemGlobbing.dll",
        "Microsoft.Extensions.Hosting.Abstractions.dll",
        "Microsoft.Extensions.Hosting.WindowsServices.dll",
        "Microsoft.Extensions.Hosting.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
        "Microsoft.Extensions.Logging.Configuration.dll",
        "Microsoft.Extensions.Logging.Console.dll",
        "Microsoft.Extensions.Logging.Debug.dll",
        "Microsoft.Extensions.Logging.EventLog.dll",
        "Microsoft.Extensions.Logging.EventSource.dll",
        "Microsoft.Extensions.Logging.dll",
        "Microsoft.Extensions.Options.ConfigurationExtensions.dll",
        "Microsoft.Extensions.Options.dll",
        "Microsoft.Extensions.Primitives.dll",
        "PAXCookbook.Service.deps.json",
        "PAXCookbook.Service.dll",
        "PAXCookbook.Service.runtimeconfig.json",
        "System.Diagnostics.EventLog.dll",
        "System.ServiceProcess.ServiceController.dll",
        "runtimes/win/lib/net8.0/System.Diagnostics.EventLog.Messages.dll",
        "runtimes/win/lib/net8.0/System.Diagnostics.EventLog.dll",
        "runtimes/win/lib/net8.0/System.ServiceProcess.ServiceController.dll",
    };

    /// <summary>
    /// The FIXED archive entry order: the manifest first, then every required
    /// member in ordinal order. A builder that emits any other order produces a
    /// different archive, and the verifier refuses it.
    /// </summary>
    internal static IReadOnlyList<string> OrderedEntryNames { get; } = BuildOrderedEntryNames();

    private static string[] BuildOrderedEntryNames()
    {
        var ordered = new List<string>(RequiredMemberNames.Count + 1) { ManifestEntryName };
        ordered.AddRange(RequiredMemberNames);
        return ordered.ToArray();
    }

    /// <summary>
    /// Structural path safety, applied identically to archive entry names and
    /// manifest names. Rejects empty, rooted, drive-qualified, UNC, parent-
    /// relative, current-relative, backslash-separated, trailing-separator,
    /// double-separator and control-character forms.
    /// </summary>
    internal static bool IsSafeRelativeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 240)
        {
            return false;
        }

        if (name.IndexOf('\\') >= 0 || name.IndexOf(':') >= 0)
        {
            return false;
        }

        if (name[0] == PathSeparator || name[^1] == PathSeparator)
        {
            return false;
        }

        if (name.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (char.IsControl(c) || c == '"' || c == '*' || c == '?' || c == '<' || c == '>' || c == '|')
            {
                return false;
            }
        }

        foreach (string segment in name.Split(PathSeparator))
        {
            if (segment.Length == 0 || segment == "." || segment == ".."
                || segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Defence-in-depth content refusal, applied to entry names and manifest
    /// names alike. A name is prohibited when its extension is on the prohibited
    /// list, when its LEAF matches a prohibited exact name, or when its
    /// extension is not on the closed allow-list. Matching is on the exact leaf
    /// rather than a substring, so the legitimate member PAXCookbook.Service.dll
    /// is never mistaken for engine bytes.
    /// </summary>
    internal static bool IsProhibitedMemberName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        int lastSeparator = name.LastIndexOf(PathSeparator);
        string leaf = lastSeparator >= 0 ? name[(lastSeparator + 1)..] : name;
        string extension = System.IO.Path.GetExtension(leaf);

        foreach (string prohibited in ProhibitedFileExtensions)
        {
            if (string.Equals(extension, prohibited, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string prohibited in ProhibitedLeafNames)
        {
            if (string.Equals(leaf, prohibited, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string allowed in AllowedFileExtensions)
        {
            if (string.Equals(extension, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
