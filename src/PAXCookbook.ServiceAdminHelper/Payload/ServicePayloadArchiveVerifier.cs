// PAX Cookbook - SERVICE PAYLOAD ARCHIVE VERIFIER (cycle 61)
//
// SECURITY CLASSIFICATION. A PASS from this verifier means "the embedded bytes
// are internally consistent", NOT "the embedded bytes are trustworthy". The
// verifier, the manifest and the members all travel inside the same UNSIGNED
// PRERELEASE helper, so this detects truncation and bit-rot and detects NO
// adversary (cycle-60 finding). An unsigned helper does not resist replacement
// by a local user, UAC may show an unknown publisher, and prerelease validation
// proves functionality, not production tamper resistance. Unacceptable for GA;
// GA activation stays BLOCKED until this exact helper is Authenticode signed and
// the expected publisher policy is configured and verified.
//
// WHAT THIS FILE IS. A BYTES-ONLY inspector. It reads an in-memory archive and
// answers one bounded question. It exposes NO extraction path, NO destination
// directory, NO stream-to-disk member, and it performs NO write of any kind. It
// starts no process, opens no registry key, certificate store, key or credential
// vault, creates/changes/starts/stops no service, elevates nothing, opens no
// socket, touches no PAX and starts no Bake.
//
// PRIVACY - FAIL CLOSED. No result carries a member name, a hash, a size, an
// exception or archive bytes. ToString() carries only the bounded token.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace PAXCookbook.ServiceAdminHelper.Payload;

/// <summary>
/// Bounded outcome of payload verification. Zero is the permanent, safe default
/// so an uninitialised value can never read as verified.
/// </summary>
internal enum ServicePayloadVerificationOutcome
{
    Unspecified = 0,

    /// <summary>Internally consistent. NOT a statement about trust.</summary>
    Verified = 1,

    /// <summary>No resource with the fixed name is embedded in this assembly.</summary>
    ResourceMissing = 2,

    /// <summary>More than one candidate resource was found. Never resolved by preference.</summary>
    ResourceAmbiguous = 3,

    /// <summary>The bytes are not a readable ZIP archive.</summary>
    ArchiveUnreadable = 4,

    /// <summary>A directory entry is present. The archive carries files only.</summary>
    DirectoryEntryPresent = 5,

    /// <summary>An archive-level or entry-level comment is present.</summary>
    CommentPresent = 6,

    /// <summary>The entry sequence is not the fixed ordinal order.</summary>
    EntryOrderUnexpected = 7,

    /// <summary>An entry timestamp is not the fixed deterministic value.</summary>
    EntryTimestampUnexpected = 8,

    /// <summary>An entry does not use the single fixed compression method.</summary>
    EntryCompressionUnexpected = 9,

    /// <summary>The inner manifest is absent.</summary>
    ManifestMissing = 10,

    /// <summary>The inner manifest bytes are not UTF-8 without BOM and LF-only.</summary>
    ManifestEncodingUnexpected = 11,

    /// <summary>The inner manifest does not satisfy the closed schema.</summary>
    ManifestInvalid = 12,

    /// <summary>A frozen required member is absent from the archive or the manifest.</summary>
    MemberMissing = 13,

    /// <summary>A member is present that the frozen contract does not declare.</summary>
    UndeclaredMember = 14,

    /// <summary>A member's byte length differs from its declared size.</summary>
    MemberSizeMismatch = 15,

    /// <summary>A member's SHA-256 differs from its declared hash.</summary>
    MemberHashMismatch = 16,

    /// <summary>A member carries a prohibited extension or prohibited exact leaf name.</summary>
    ProhibitedMember = 17,

    /// <summary>A bounded stability failure. Never a partial success.</summary>
    Unavailable = 18,
}

/// <summary>
/// The verification result. Its whole observable surface is a bounded outcome
/// and, when the manifest was parsed at all, the bounded manifest outcome.
/// </summary>
internal readonly struct ServicePayloadVerificationResult
{
    private ServicePayloadVerificationResult(
        ServicePayloadVerificationOutcome outcome, ServicePayloadManifestOutcome manifestOutcome)
    {
        Outcome = outcome;
        ManifestOutcome = manifestOutcome;
    }

    internal ServicePayloadVerificationOutcome Outcome { get; }

    /// <summary>
    /// <see cref="ServicePayloadManifestOutcome.Unspecified"/> when the manifest
    /// was never reached.
    /// </summary>
    internal ServicePayloadManifestOutcome ManifestOutcome { get; }

    internal bool IsVerified => Outcome == ServicePayloadVerificationOutcome.Verified;

    internal static ServicePayloadVerificationResult Verified() =>
        new(ServicePayloadVerificationOutcome.Verified, ServicePayloadManifestOutcome.Valid);

    internal static ServicePayloadVerificationResult Refused(
        ServicePayloadVerificationOutcome outcome,
        ServicePayloadManifestOutcome manifestOutcome = ServicePayloadManifestOutcome.Unspecified) =>
        new(
            outcome == ServicePayloadVerificationOutcome.Verified
                ? ServicePayloadVerificationOutcome.Unavailable
                : outcome,
            manifestOutcome);

    /// <summary>Carries the bounded outcome token only.</summary>
    public override string ToString() => Outcome.ToString();
}

/// <summary>
/// The bytes-only payload verifier. There is deliberately no overload that
/// takes a destination, a file path, a stream to write to, a delegate or an
/// options object: this type cannot be persuaded to put anything on disk.
/// </summary>
internal static class ServicePayloadArchiveVerifier
{
    /// <summary>
    /// Verify archive bytes against the frozen format contract. Reads only; the
    /// archive never touches disk and nothing is ever extracted.
    /// </summary>
    internal static ServicePayloadVerificationResult Verify(ReadOnlyMemory<byte> archiveBytes)
    {
        if (archiveBytes.Length == 0)
        {
            return ServicePayloadVerificationResult.Refused(ServicePayloadVerificationOutcome.ArchiveUnreadable);
        }

        try
        {
            using var buffer = new MemoryStream(archiveBytes.ToArray(), writable: false);
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

            if (!string.IsNullOrEmpty(archive.Comment))
            {
                return ServicePayloadVerificationResult.Refused(ServicePayloadVerificationOutcome.CommentPresent);
            }

            IReadOnlyList<ZipArchiveEntry> entries = archive.Entries;

            // Structure: files only, and no optional metadata anywhere.
            foreach (ZipArchiveEntry entry in entries)
            {
                if (entry.FullName.EndsWith(ServicePayloadArchiveFormat.PathSeparator) || entry.Name.Length == 0)
                {
                    return ServicePayloadVerificationResult.Refused(
                        ServicePayloadVerificationOutcome.DirectoryEntryPresent);
                }

                if (!string.IsNullOrEmpty(entry.Comment))
                {
                    return ServicePayloadVerificationResult.Refused(ServicePayloadVerificationOutcome.CommentPresent);
                }
            }

            // Prohibited content is refused BEFORE membership so the reason is
            // the specific one, mirroring the cycle-58 anchor precedent.
            foreach (ZipArchiveEntry entry in entries)
            {
                if (ServicePayloadArchiveFormat.IsProhibitedMemberName(entry.FullName))
                {
                    return ServicePayloadVerificationResult.Refused(
                        ServicePayloadVerificationOutcome.ProhibitedMember);
                }
            }

            int manifestCount = 0;
            foreach (ZipArchiveEntry entry in entries)
            {
                if (string.Equals(
                        entry.FullName, ServicePayloadArchiveFormat.ManifestEntryName, StringComparison.Ordinal))
                {
                    manifestCount++;
                }
            }

            if (manifestCount == 0)
            {
                return ServicePayloadVerificationResult.Refused(ServicePayloadVerificationOutcome.ManifestMissing);
            }

            if (manifestCount > 1)
            {
                return ServicePayloadVerificationResult.Refused(ServicePayloadVerificationOutcome.UndeclaredMember);
            }

            var expected = new HashSet<string>(ServicePayloadArchiveFormat.OrderedEntryNames, StringComparer.Ordinal);
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in entries)
            {
                actual.Add(entry.FullName);
                if (!expected.Contains(entry.FullName))
                {
                    return ServicePayloadVerificationResult.Refused(
                        ServicePayloadVerificationOutcome.UndeclaredMember);
                }
            }

            foreach (string required in ServicePayloadArchiveFormat.OrderedEntryNames)
            {
                if (!actual.Contains(required))
                {
                    return ServicePayloadVerificationResult.Refused(ServicePayloadVerificationOutcome.MemberMissing);
                }
            }

            // Exact fixed sequence, not merely the right set.
            if (entries.Count != ServicePayloadArchiveFormat.OrderedEntryNames.Count)
            {
                return ServicePayloadVerificationResult.Refused(
                    ServicePayloadVerificationOutcome.EntryOrderUnexpected);
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (!string.Equals(
                        entries[i].FullName,
                        ServicePayloadArchiveFormat.OrderedEntryNames[i],
                        StringComparison.Ordinal))
                {
                    return ServicePayloadVerificationResult.Refused(
                        ServicePayloadVerificationOutcome.EntryOrderUnexpected);
                }
            }

            // The DOS timestamp round-trips as a CLOCK value; the offset a
            // DateTimeOffset getter reports is the reader's local one, so the
            // comparison is deliberately on .DateTime.
            DateTime expectedClock = ServicePayloadArchiveFormat.FixedEntryTimestamp.DateTime;
            foreach (ZipArchiveEntry entry in entries)
            {
                if (entry.LastWriteTime.DateTime != expectedClock)
                {
                    return ServicePayloadVerificationResult.Refused(
                        ServicePayloadVerificationOutcome.EntryTimestampUnexpected);
                }
            }

            // One fixed method for every member. Stored members satisfy
            // CompressedLength == Length; anything deflated does not.
            foreach (ZipArchiveEntry entry in entries)
            {
                if (entry.CompressedLength != entry.Length)
                {
                    return ServicePayloadVerificationResult.Refused(
                        ServicePayloadVerificationOutcome.EntryCompressionUnexpected);
                }
            }

            ZipArchiveEntry manifestEntry =
                archive.GetEntry(ServicePayloadArchiveFormat.ManifestEntryName)!;
            byte[] manifestBytes = ReadEntry(manifestEntry);

            if (manifestBytes.Length >= 3
                && manifestBytes[0] == 0xEF && manifestBytes[1] == 0xBB && manifestBytes[2] == 0xBF)
            {
                return ServicePayloadVerificationResult.Refused(
                    ServicePayloadVerificationOutcome.ManifestEncodingUnexpected);
            }

            foreach (byte b in manifestBytes)
            {
                if (b == 0x0D)
                {
                    return ServicePayloadVerificationResult.Refused(
                        ServicePayloadVerificationOutcome.ManifestEncodingUnexpected);
                }
            }

            ServicePayloadManifestResult manifest = ServicePayloadManifestContract.Parse(manifestBytes);
            if (!manifest.IsValid)
            {
                return ServicePayloadVerificationResult.Refused(
                    ServicePayloadVerificationOutcome.ManifestInvalid, manifest.Outcome);
            }

            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (ServicePayloadManifestFileEntry entry in manifest.Files)
            {
                declared.Add(entry.Name);
                if (!actual.Contains(entry.Name))
                {
                    return ServicePayloadVerificationResult.Refused(
                        ServicePayloadVerificationOutcome.MemberMissing, manifest.Outcome);
                }
            }

            foreach (string required in ServicePayloadArchiveFormat.RequiredMemberNames)
            {
                if (!declared.Contains(required))
                {
                    return ServicePayloadVerificationResult.Refused(
                        ServicePayloadVerificationOutcome.MemberMissing, manifest.Outcome);
                }
            }

            foreach (ServicePayloadManifestFileEntry entry in manifest.Files)
            {
                ZipArchiveEntry member = archive.GetEntry(entry.Name)!;
                byte[] bytes = ReadEntry(member);

                if (bytes.LongLength != entry.SizeBytes)
                {
                    return ServicePayloadVerificationResult.Refused(
                        ServicePayloadVerificationOutcome.MemberSizeMismatch, manifest.Outcome);
                }

                if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256, StringComparison.Ordinal))
                {
                    return ServicePayloadVerificationResult.Refused(
                        ServicePayloadVerificationOutcome.MemberHashMismatch, manifest.Outcome);
                }
            }

            return ServicePayloadVerificationResult.Verified();
        }
        catch (InvalidDataException)
        {
            return ServicePayloadVerificationResult.Refused(ServicePayloadVerificationOutcome.ArchiveUnreadable);
        }
        catch (Exception)
        {
            return ServicePayloadVerificationResult.Refused(ServicePayloadVerificationOutcome.Unavailable);
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using Stream source = entry.Open();
        using var target = new MemoryStream();
        source.CopyTo(target);
        return target.ToArray();
    }
}
