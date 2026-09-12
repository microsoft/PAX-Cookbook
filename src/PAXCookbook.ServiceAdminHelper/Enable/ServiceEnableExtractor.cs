// PAX Cookbook - SERVICE-ENABLE EXTRACTOR (cycle 62, helper only)
//
// WHAT THIS FILE IS. The ONLY code in this product that writes the service
// payload to disk. It extracts a VERIFIED in-memory archive to the FIXED
// staging directory and promotes it to the FIXED final directory.
//
// WHERE THE DESTINATIONS COME FROM. Exclusively from
// ServiceEnableFixedPathResolver. There is no caller path parameter, and no
// destination is ever derived from archive text: an entry name is only ever
// MATCHED against the frozen member list, never used to compose a location.
//
// THE BINDING SEQUENCE.
//   1. Create ONLY the fixed root and the fixed staging directory.
//   2. Apply the protected staging access control BEFORE any member is written,
//      so no payload byte ever exists at a weaker protection level.
//   3. Write the manifest and the frozen members, and nothing else.
//   4. Create only manifest-declared subdirectories.
//   5. Refuse any reparse point encountered on the way.
//   6. Write with EXCLUSIVE CREATION, so an existing file is a refusal rather
//      than a silent overwrite.
//   7. Flush every file to disk.
//   8. Reread every file FROM ITS OWN FINAL OPENED HANDLE and verify size and
//      SHA-256 against the manifest.
//   9. Refuse any unknown member or unexpected sibling.
//  10. Move staging to final on the SAME volume.
//  11. Verify exact final membership and bytes AGAIN.
//
// NEVER OVERWRITE A FOREIGN OR MISMATCHED FINAL DIRECTORY. Promotion happens
// only into an absent final path. An existing final directory is the preflight's
// business, not this file's, and this file will not delete one.
//
// PRIVACY - FAIL CLOSED. Every failure is a bounded token. Nothing here returns
// a path, a member name, a hash, an exception or a native status.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using PAXCookbook.ServiceAdminHelper.Payload;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>Bounded outcome of one extraction. Zero can never read as extracted.</summary>
internal enum ServiceEnableExtractionState
{
    Unspecified = 0,

    /// <summary>Every member is present in the final directory with verified size and bytes.</summary>
    Extracted = 1,

    /// <summary>The supplied archive did not verify. Nothing was written.</summary>
    ArchiveRefused = 2,

    /// <summary>The fixed destination could not be created, or already exists when it must not.</summary>
    DestinationRefused = 3,

    /// <summary>A reparse point was found on a path this extraction must traverse or create.</summary>
    ReparsePointRefused = 4,

    /// <summary>An entry outside the frozen member list, or an unexpected sibling, was found.</summary>
    MemberRefused = 5,

    /// <summary>A write failed, or exclusive creation found an existing file.</summary>
    WriteFailed = 6,

    /// <summary>A post-write reread did not reproduce the declared size or hash.</summary>
    VerificationFailed = 7,

    /// <summary>Staging could not be promoted to the fixed final location.</summary>
    PromotionFailed = 8,

    /// <summary>A bounded access or stability failure. Never a partial success.</summary>
    Unavailable = 9,
}

/// <summary>
/// The extraction result, including exactly which fixed directories THIS
/// extraction created. Compensation depends on that distinction: a directory
/// this transaction did not create is never removed.
/// </summary>
internal readonly struct ServiceEnableExtractionResult
{
    private ServiceEnableExtractionResult(
        ServiceEnableExtractionState state, bool rootCreated, bool stagingCreated, bool finalCreated)
    {
        State = state;
        RootCreated = rootCreated;
        StagingCreated = stagingCreated;
        FinalCreated = finalCreated;
    }

    internal ServiceEnableExtractionState State { get; }

    internal bool RootCreated { get; }

    internal bool StagingCreated { get; }

    internal bool FinalCreated { get; }

    internal bool IsExtracted => State == ServiceEnableExtractionState.Extracted;

    internal static ServiceEnableExtractionResult Extracted(bool rootCreated) =>
        new(ServiceEnableExtractionState.Extracted, rootCreated, stagingCreated: false, finalCreated: true);

    internal static ServiceEnableExtractionResult Refused(
        ServiceEnableExtractionState state,
        bool rootCreated = false,
        bool stagingCreated = false,
        bool finalCreated = false) =>
        new(
            state == ServiceEnableExtractionState.Extracted ? ServiceEnableExtractionState.Unavailable : state,
            rootCreated,
            stagingCreated,
            finalCreated);

    public override string ToString() => State.ToString();
}

/// <summary>
/// The fixed extractor. There is deliberately no overload that takes a
/// destination, a member list, a filter, a delegate or an options object.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceEnableExtractor
{
    /// <summary>
    /// Extracts a VERIFIED archive to the fixed staging directory and promotes
    /// it to the fixed final directory. The caller must already have proven the
    /// archive verifies; this method re-checks rather than trusting that claim.
    /// </summary>
    internal static ServiceEnableExtractionResult ExtractVerifiedArchive(
        ReadOnlyMemory<byte> archiveBytes,
        ServiceEnableFixedPaths paths,
        IServiceEnableSecurityAdapter security)
    {
        if (security is null
            || string.IsNullOrEmpty(paths.Root)
            || string.IsNullOrEmpty(paths.Final)
            || string.IsNullOrEmpty(paths.Staging))
        {
            return ServiceEnableExtractionResult.Refused(ServiceEnableExtractionState.DestinationRefused);
        }

        // 0. The archive is RE-VERIFIED here rather than trusted from a caller
        //    claim, so nothing can be written from bytes this file never checked.
        if (!ServicePayloadResource.InspectBytes(archiveBytes).IsVerified)
        {
            return ServiceEnableExtractionResult.Refused(ServiceEnableExtractionState.ArchiveRefused);
        }

        bool rootCreated = false;
        bool stagingCreated = false;
        try
        {
            // 1. The final path must be ABSENT. A foreign or mismatched final
            //    directory is never overwritten and never deleted here.
            if (Directory.Exists(paths.Final) || File.Exists(paths.Final))
            {
                return ServiceEnableExtractionResult.Refused(ServiceEnableExtractionState.DestinationRefused);
            }

            // 2. Nothing on the destination chain may be a reparse point.
            string? programFilesBase = Path.GetDirectoryName(paths.Root);
            if (string.IsNullOrEmpty(programFilesBase)
                || AnyComponentIsReparsePoint(paths.Root, programFilesBase))
            {
                return ServiceEnableExtractionResult.Refused(ServiceEnableExtractionState.ReparsePointRefused);
            }

            // 3. ONLY the fixed root and the fixed staging directory are created.
            if (!Directory.Exists(paths.Root))
            {
                Directory.CreateDirectory(paths.Root);
                rootCreated = true;
            }

            if (Directory.Exists(paths.Staging))
            {
                // A remnant is removed rather than merged into: merging is how a
                // stale member would survive into a "verified" installation.
                Directory.Delete(paths.Staging, recursive: true);
            }

            Directory.CreateDirectory(paths.Staging);
            stagingCreated = true;

            // 4. PROTECT BEFORE WRITING. No payload byte ever exists at a weaker
            //    protection level than the one it will finally carry.
            if (!security.TryApplyStagingProtection(paths.Staging))
            {
                return CleanupAndRefuse(
                    paths, ServiceEnableExtractionState.DestinationRefused, rootCreated, stagingCreated);
            }

            using var buffer = new MemoryStream(archiveBytes.ToArray(), writable: false);
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

            var expected = new HashSet<string>(
                ServicePayloadArchiveFormat.OrderedEntryNames, StringComparer.Ordinal);
            var written = new HashSet<string>(StringComparer.Ordinal);
            var facts = new List<ServicePayloadManifestFileEntryFacts>();

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                // 5. An entry name is only ever MATCHED. It never composes a path
                //    that was not already in the frozen contract.
                if (!expected.Contains(entry.FullName) || !written.Add(entry.FullName))
                {
                    return CleanupAndRefuse(
                        paths, ServiceEnableExtractionState.MemberRefused, rootCreated, stagingCreated);
                }

                string relative = entry.FullName.Replace(
                    ServicePayloadArchiveFormat.PathSeparator, Path.DirectorySeparatorChar);
                string destination = Path.GetFullPath(Path.Combine(paths.Staging, relative));

                // Belt and braces: the composed destination must still be inside
                // staging after canonicalization.
                if (!destination.StartsWith(
                        Path.TrimEndingDirectorySeparator(paths.Staging) + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal))
                {
                    return CleanupAndRefuse(
                        paths, ServiceEnableExtractionState.MemberRefused, rootCreated, stagingCreated);
                }

                string? parent = Path.GetDirectoryName(destination);
                if (string.IsNullOrEmpty(parent))
                {
                    return CleanupAndRefuse(
                        paths, ServiceEnableExtractionState.DestinationRefused, rootCreated, stagingCreated);
                }

                // 6. Only manifest-declared subdirectories are created.
                Directory.CreateDirectory(parent);

                if (AnyComponentIsReparsePoint(parent, paths.Staging))
                {
                    return CleanupAndRefuse(
                        paths, ServiceEnableExtractionState.ReparsePointRefused, rootCreated, stagingCreated);
                }

                byte[] bytes;
                using (Stream source = entry.Open())
                using (var member = new MemoryStream())
                {
                    source.CopyTo(member);
                    bytes = member.ToArray();
                }

                try
                {
                    // 7. EXCLUSIVE CREATION plus an explicit flush to disk. An
                    //    existing file is a refusal, never a silent overwrite.
                    using var stream = new FileStream(
                        destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                catch (Exception)
                {
                    return CleanupAndRefuse(
                        paths, ServiceEnableExtractionState.WriteFailed, rootCreated, stagingCreated);
                }

                // 8. Reread from the file's own final opened handle and verify.
                byte[] reread;
                try
                {
                    using var verify = new FileStream(
                        destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var target = new MemoryStream();
                    verify.CopyTo(target);
                    reread = target.ToArray();
                }
                catch (Exception)
                {
                    return CleanupAndRefuse(
                        paths, ServiceEnableExtractionState.VerificationFailed, rootCreated, stagingCreated);
                }

                string hash = Convert.ToHexString(SHA256.HashData(reread));
                if (reread.LongLength != bytes.LongLength
                    || !string.Equals(hash, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.Ordinal))
                {
                    return CleanupAndRefuse(
                        paths, ServiceEnableExtractionState.VerificationFailed, rootCreated, stagingCreated);
                }

                facts.Add(new ServicePayloadManifestFileEntryFacts(entry.FullName, reread.LongLength, hash));
            }

            // 9. Exactly the frozen membership, no more and no less.
            if (written.Count != ServicePayloadArchiveFormat.OrderedEntryNames.Count
                || !VerifyExactMembership(paths.Staging, facts))
            {
                return CleanupAndRefuse(
                    paths, ServiceEnableExtractionState.MemberRefused, rootCreated, stagingCreated);
            }

            // 10. Promote on the SAME volume - staging is a sibling of final by
            //     construction, so Directory.Move is a rename, not a copy.
            try
            {
                Directory.Move(paths.Staging, paths.Final);
            }
            catch (Exception)
            {
                return CleanupAndRefuse(
                    paths, ServiceEnableExtractionState.PromotionFailed, rootCreated, stagingCreated);
            }

            // 11. Verify the exact membership and bytes AGAIN, at the final path.
            if (!VerifyExactMembership(paths.Final, facts))
            {
                return CleanupAndRefuse(
                    paths, ServiceEnableExtractionState.VerificationFailed, rootCreated, stagingCreated: false,
                    finalCreated: true);
            }

            return ServiceEnableExtractionResult.Extracted(rootCreated);
        }
        catch (InvalidDataException)
        {
            return CleanupAndRefuse(
                paths, ServiceEnableExtractionState.ArchiveRefused, rootCreated, stagingCreated);
        }
        catch (Exception)
        {
            return CleanupAndRefuse(
                paths, ServiceEnableExtractionState.Unavailable, rootCreated, stagingCreated);
        }
    }

    /// <summary>
    /// Removes ONLY what this extraction created, then reports the refusal. It
    /// never touches a pre-existing final directory.
    /// </summary>
    private static ServiceEnableExtractionResult CleanupAndRefuse(
        ServiceEnableFixedPaths paths,
        ServiceEnableExtractionState state,
        bool rootCreated,
        bool stagingCreated,
        bool finalCreated = false)
    {
        bool stagingRemains = stagingCreated;
        bool finalRemains = finalCreated;
        bool rootRemains = rootCreated;

        try
        {
            if (finalCreated && Directory.Exists(paths.Final))
            {
                Directory.Delete(paths.Final, recursive: true);
                finalRemains = false;
            }

            if (stagingCreated && Directory.Exists(paths.Staging))
            {
                Directory.Delete(paths.Staging, recursive: true);
                stagingRemains = false;
            }

            // A transaction-created directory is removed only when it is empty.
            if (rootCreated && Directory.Exists(paths.Root)
                && Directory.GetFileSystemEntries(paths.Root).Length == 0)
            {
                Directory.Delete(paths.Root, recursive: false);
                rootRemains = false;
            }
        }
        catch (Exception)
        {
            // What could not be proven gone is reported as still created, so the
            // caller compensates or escalates rather than assuming closure.
        }

        return ServiceEnableExtractionResult.Refused(state, rootRemains, stagingRemains, finalRemains);
    }

    /// <summary>
    /// Verifies that a directory contains EXACTLY the frozen membership and that
    /// every member reproduces its declared size and SHA-256. Read-only.
    /// </summary>
    internal static bool VerifyExactMembership(
        string directory, IReadOnlyList<ServicePayloadManifestFileEntryFacts> expected)
    {
        if (string.IsNullOrEmpty(directory) || expected is null || expected.Count == 0)
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                actual.Add(Path.GetRelativePath(directory, file)
                    .Replace(Path.DirectorySeparatorChar, ServicePayloadArchiveFormat.PathSeparator));
            }

            // MEMBERSHIP IS SET EQUALITY AGAINST THE FROZEN CONTRACT, not
            // against the caller's fact list. That is what makes an unexpected
            // sibling a refusal regardless of which subset of facts was supplied
            // for byte verification.
            var frozen = new HashSet<string>(
                ServicePayloadArchiveFormat.OrderedEntryNames, StringComparer.Ordinal);
            if (!actual.SetEquals(frozen))
            {
                return false;
            }

            foreach (ServicePayloadManifestFileEntryFacts entry in expected)
            {
                if (!actual.Contains(entry.Name))
                {
                    return false;
                }

                string path = Path.Combine(
                    directory, entry.Name.Replace(ServicePayloadArchiveFormat.PathSeparator, Path.DirectorySeparatorChar));

                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.LongLength != entry.SizeBytes
                    || !string.Equals(
                        Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the path itself, or any of its ancestors up to and including
    /// the supplied boundary, is a reparse point. Read-only metadata only.
    /// </summary>
    internal static bool AnyComponentIsReparsePoint(string path, string boundary)
    {
        try
        {
            string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundary));

            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current))
                {
                    if (new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        return true;
                    }
                }
                else if (File.Exists(current)
                         && new FileInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }

                if (string.Equals(current, stop, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                {
                    return false;
                }

                current = Path.TrimEndingDirectorySeparator(parent);
            }

            return false;
        }
        catch (Exception)
        {
            // An unreadable component is treated as unsafe. Fail closed.
            return true;
        }
    }
}

/// <summary>
/// A member's declared facts, copied out of the inner manifest so verification
/// does not have to hold the archive open. It carries no capability.
/// </summary>
internal readonly struct ServicePayloadManifestFileEntryFacts
{
    internal ServicePayloadManifestFileEntryFacts(string name, long sizeBytes, string sha256)
    {
        Name = name;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
    }

    internal string Name { get; }

    internal long SizeBytes { get; }

    internal string Sha256 { get; }

    /// <summary>Carries the bounded type name only - never the name, size or hash.</summary>
    public override string ToString() => nameof(ServicePayloadManifestFileEntryFacts);
}
