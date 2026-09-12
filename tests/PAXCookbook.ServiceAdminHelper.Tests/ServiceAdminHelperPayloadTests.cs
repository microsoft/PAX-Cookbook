// PAX Cookbook - FOCUSED TESTS: PRERELEASE SERVICE ADMIN HELPER PAYLOAD (cycle 61)
//
// SECURITY CLASSIFICATION. Everything exercised here belongs to UNSIGNED
// PRERELEASE CODE. A green run proves the deterministic archive, the closed
// inner manifest and the bounded refusals behave as specified. It proves
// NOTHING about tamper resistance: an unsigned helper does not resist
// replacement by a local user, UAC may display an unknown publisher, and
// prerelease validation proves functionality only. That is unacceptable for GA.
// GA activation remains BLOCKED until this exact helper is Authenticode signed
// and the expected publisher policy is configured and verified.
//
// SCOPE. Exactly ONE test class, by cycle instruction. It runs entirely in
// memory and in an OS temp directory: it launches no process, elevates nothing,
// registers no service, writes nothing to ProgramData, HKLM, Program Files or a
// certificate store, executes no PAX and starts no Bake.
//
// The structural no-execution assertion is FILE-SCOPED (an explicit list of the
// helper's own production sources), never directory-scoped - a directory scan
// trips over sibling files, as cycle 59 established.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PAXCookbook.ServiceAdminHelper.Payload;
using PAXCookbook.ServiceAdminHelper.Signing;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbook.ServiceAdminHelper.Tests;

public sealed class ServiceAdminHelperPayloadTests
{
    // ------------------------------------------------------------------
    // Frozen contract pins
    // ------------------------------------------------------------------

    [Fact]
    public void FrozenRequiredMembers_MatchTheEmpiricalServicePublish()
    {
        IReadOnlyList<string> required = ServicePayloadArchiveFormat.RequiredMemberNames;

        Assert.Equal(36, required.Count);
        Assert.Contains("PAXCookbook.Service.dll", required);
        Assert.Contains("PAXCookbook.Service.deps.json", required);
        Assert.Contains("PAXCookbook.Service.runtimeconfig.json", required);
        Assert.Contains("runtimes/win/lib/net8.0/System.Diagnostics.EventLog.dll", required);

        // The service compile-links its Shared contracts, so this assembly is
        // NOT published. Assuming it would be present would have frozen a wrong
        // contract; the empirical inventory is the authority.
        Assert.DoesNotContain("PAXCookbook.Shared.dll", required);

        // No apphost, no symbols, no scripts, no native runtime outside the
        // helper: the frozen list is managed assemblies and runtime JSON only.
        Assert.All(required, name =>
        {
            string extension = Path.GetExtension(name);
            Assert.Contains(extension, ServicePayloadArchiveFormat.AllowedFileExtensions);
            Assert.DoesNotContain(extension, ServicePayloadArchiveFormat.ProhibitedFileExtensions);
            Assert.True(ServicePayloadArchiveFormat.IsSafeRelativeName(name));
        });

        // Exactly one self-contained helper, at one fixed payload path.
        Assert.Equal(
            "Setup/PAXCookbookServiceAdminHelper.exe",
            ServicePayloadArchiveFormat.HelperPayloadRelativePath);
    }

    [Fact]
    public void FrozenRequiredMembers_AreDistinctAndInOrdinalOrder()
    {
        IReadOnlyList<string> required = ServicePayloadArchiveFormat.RequiredMemberNames;

        Assert.Equal(required.Count, required.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            required.Count,
            required.Select(n => n.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count());

        string[] ordinal = required.ToArray();
        Array.Sort(ordinal, StringComparer.Ordinal);
        Assert.Equal(ordinal, required.ToArray());
    }

    [Fact]
    public void OrderedEntryNames_AreManifestFirstThenEveryRequiredMember()
    {
        IReadOnlyList<string> ordered = ServicePayloadArchiveFormat.OrderedEntryNames;

        Assert.Equal(ServicePayloadArchiveFormat.RequiredMemberNames.Count + 1, ordered.Count);
        Assert.Equal(ServicePayloadArchiveFormat.ManifestEntryName, ordered[0]);
        Assert.Equal(
            ServicePayloadArchiveFormat.RequiredMemberNames.ToArray(),
            ordered.Skip(1).ToArray());

        // The manifest is the ONLY permitted non-runtime member.
        Assert.DoesNotContain(
            ServicePayloadArchiveFormat.ManifestEntryName,
            ServicePayloadArchiveFormat.RequiredMemberNames);
    }

    [Fact]
    public void ArchiveFormat_PinsTheDeterministicShape()
    {
        Assert.Equal("PAXCookbook.ServicePayload.zip", ServicePayloadArchiveFormat.ResourceName);
        Assert.Equal("service-payload-manifest.json", ServicePayloadArchiveFormat.ManifestEntryName);
        Assert.Equal(1, ServicePayloadArchiveFormat.SchemaVersion);
        Assert.Equal("windows", ServicePayloadArchiveFormat.TargetOs);
        Assert.Equal("x64", ServicePayloadArchiveFormat.TargetArch);
        Assert.Equal('/', ServicePayloadArchiveFormat.PathSeparator);
        Assert.Equal("\n", ServicePayloadArchiveFormat.ManifestNewline);
        Assert.False(ServicePayloadArchiveFormat.ManifestUsesByteOrderMark);
        Assert.Equal(CompressionLevel.NoCompression, ServicePayloadArchiveFormat.FixedCompressionLevel);

        // DOS time has a 1980 floor and 2-second precision, so this is the only
        // legal exactly-representable fixed value.
        Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0), ServicePayloadArchiveFormat.FixedEntryTimestamp.DateTime);
        Assert.Equal(TimeSpan.Zero, ServicePayloadArchiveFormat.FixedEntryTimestamp.Offset);
    }

    [Fact]
    public void FixedResourceIdentity_IsTheOnlyNameAndHasNoCallerOverride()
    {
        Assert.Equal(ServicePayloadArchiveFormat.ResourceName, ServicePayloadResource.ResourceName);

        MethodInfo inspect = typeof(ServicePayloadResource)
            .GetMethod("InspectEmbeddedPayload", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        Assert.NotNull(inspect);
        Assert.Empty(inspect.GetParameters());

        // No path, directory, file, stream or assembly may reach the verifier or
        // the fixed inspection surface.
        foreach (Type surface in new[] { typeof(ServicePayloadResource), typeof(ServicePayloadArchiveVerifier) })
        {
            foreach (MethodInfo method in surface.GetMethods(
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.NotEqual(typeof(string), parameter.ParameterType);
                    Assert.NotEqual(typeof(FileInfo), parameter.ParameterType);
                    Assert.NotEqual(typeof(DirectoryInfo), parameter.ParameterType);
                    Assert.False(typeof(Stream).IsAssignableFrom(parameter.ParameterType));
                    Assert.NotEqual(typeof(Assembly), parameter.ParameterType);
                }
            }
        }
    }

    [Fact]
    public void PayloadAndPolicySurfaces_CarryNoMutableStaticState()
    {
        Type[] surfaces =
        {
            typeof(ServicePayloadArchiveFormat),
            typeof(ServicePayloadManifestContract),
            typeof(ServicePayloadArchiveVerifier),
            typeof(ServicePayloadResource),
            typeof(ServiceHelperSigningPolicy),
        };

        foreach (Type surface in surfaces)
        {
            foreach (FieldInfo field in surface.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Assert.True(
                    field.IsLiteral || field.IsInitOnly,
                    surface.Name + "." + field.Name + " is settable static state");
            }

            foreach (PropertyInfo property in surface.GetProperties(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Assert.False(property.CanWrite, surface.Name + "." + property.Name + " is settable");
            }
        }
    }

    [Fact]
    public void BoundedToString_NeverLeaksDetail()
    {
        Assert.Equal(
            "Unavailable",
            ServicePayloadVerificationResult.Refused(ServicePayloadVerificationOutcome.Unavailable).ToString());
        Assert.Equal(
            "UnsafePath",
            ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.UnsafePath).ToString());
        Assert.Equal(
            nameof(ServicePayloadManifestFileEntry),
            new ServicePayloadManifestFileEntry("a/b.dll", 1, new string('A', 64)).ToString());
        Assert.Equal(
            "HelperMissing",
            ServiceAdminHelperLocationResult.Refused(ServiceAdminHelperLocationOutcome.HelperMissing).ToString());
    }

    // ------------------------------------------------------------------
    // The canonical archive
    // ------------------------------------------------------------------

    [Fact]
    public void CanonicalArchive_IsVerified()
    {
        ServicePayloadVerificationResult result = Inspect(BuildArchive(CanonicalMembers()));

        Assert.Equal(ServicePayloadVerificationOutcome.Verified, result.Outcome);
        Assert.Equal(ServicePayloadManifestOutcome.Valid, result.ManifestOutcome);
        Assert.True(result.IsVerified);
    }

    [Fact]
    public void CanonicalArchive_IsByteIdenticalWhenBuiltTwice()
    {
        byte[] first = BuildArchive(CanonicalMembers());
        byte[] second = BuildArchive(CanonicalMembers());

        Assert.Equal(first, second);
        Assert.Equal(Sha256Upper(first), Sha256Upper(second));
    }

    // ------------------------------------------------------------------
    // Membership
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> EveryRequiredMember() =>
        ServicePayloadArchiveFormat.RequiredMemberNames.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(EveryRequiredMember))]
    public void EveryRequiredMemberMissingInTurn_IsRefused(string missing)
    {
        List<(string Name, byte[] Bytes)> members =
            CanonicalMembers().Where(m => !string.Equals(m.Name, missing, StringComparison.Ordinal)).ToList();

        ServicePayloadVerificationResult result = Inspect(BuildArchive(members));

        Assert.Equal(ServicePayloadVerificationOutcome.MemberMissing, result.Outcome);
    }

    [Fact]
    public void UndeclaredExtraMember_IsRefused()
    {
        List<(string Name, byte[] Bytes)> members = CanonicalMembers();
        members.Add(("Extra.Undeclared.dll", MemberContent("Extra.Undeclared.dll")));

        ServicePayloadVerificationResult result = Inspect(BuildArchive(members));

        Assert.Equal(ServicePayloadVerificationOutcome.UndeclaredMember, result.Outcome);
    }

    [Fact]
    public void ManifestMissingFromArchive_IsRefused()
    {
        byte[] archive = BuildArchive(CanonicalMembers(), includeManifest: false);

        Assert.Equal(ServicePayloadVerificationOutcome.ManifestMissing, Inspect(archive).Outcome);
    }

    // ------------------------------------------------------------------
    // Prohibited content
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("PAXCookbookServiceAdminHelper.exe")]            // apphost
    [InlineData("PAXCookbook.Service.pdb")]                      // symbols
    [InlineData("Install-Service.ps1")]                          // script
    [InlineData("PAX_Purview_Audit_Log_Processor.ps1")]          // engine bytes
    [InlineData("Microsoft.Identity.Client.dll")]                // MSAL
    [InlineData("msalruntime.dll")]                              // WAM broker
    [InlineData("WebView2Loader.dll")]                           // WebView2
    [InlineData("service-signing.pfx")]                          // certificate material
    [InlineData("secrets.json")]                                 // secrets
    [InlineData("recipes.json")]                                 // Recipes
    [InlineData("index.html")]                                   // web asset
    [InlineData("styles.css")]                                   // web asset
    public void ProhibitedArchiveMember_IsRefused(string prohibited)
    {
        List<(string Name, byte[] Bytes)> members = CanonicalMembers();
        members.Add((prohibited, MemberContent(prohibited)));

        ServicePayloadVerificationResult result = Inspect(BuildArchive(members));

        Assert.Equal(ServicePayloadVerificationOutcome.ProhibitedMember, result.Outcome);
    }

    [Fact]
    public void ProhibitedManifestName_IsRefusedByTheManifestContract()
    {
        string manifest = BuildManifestJson(CanonicalMembers(), firstEntryNameOverride: "Microsoft.Identity.Client.dll");

        ServicePayloadManifestResult result = ServicePayloadManifestContract.Parse(Utf8NoBom(manifest));

        Assert.Equal(ServicePayloadManifestOutcome.ProhibitedPath, result.Outcome);
    }

    // ------------------------------------------------------------------
    // Manifest schema
    // ------------------------------------------------------------------

    [Fact]
    public void ManifestUnknownProperty_IsRefused()
    {
        ServicePayloadVerificationResult result = InspectWithManifest(
            BuildManifestJson(CanonicalMembers(), extraTopLevelProperty: "installRoot"));

        Assert.Equal(ServicePayloadVerificationOutcome.ManifestInvalid, result.Outcome);
        Assert.Equal(ServicePayloadManifestOutcome.UnknownProperty, result.ManifestOutcome);
    }

    [Fact]
    public void ManifestDuplicateProperty_IsRefused()
    {
        ServicePayloadVerificationResult result = InspectWithManifest(
            BuildManifestJson(CanonicalMembers(), duplicateTargetOs: true));

        Assert.Equal(ServicePayloadVerificationOutcome.ManifestInvalid, result.Outcome);
        Assert.Equal(ServicePayloadManifestOutcome.DuplicateProperty, result.ManifestOutcome);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("targetOs")]
    [InlineData("targetArch")]
    [InlineData("files")]
    public void ManifestMissingTopLevelProperty_IsRefused(string omitted)
    {
        ServicePayloadVerificationResult result = InspectWithManifest(
            BuildManifestJson(CanonicalMembers(), omitTopLevelProperty: omitted));

        Assert.Equal(ServicePayloadVerificationOutcome.ManifestInvalid, result.Outcome);
        Assert.Equal(ServicePayloadManifestOutcome.MissingProperty, result.ManifestOutcome);
    }

    [Fact]
    public void ManifestMalformedJson_IsRefused()
    {
        ServicePayloadVerificationResult result = InspectWithManifest("{ this is not json");

        Assert.Equal(ServicePayloadVerificationOutcome.ManifestInvalid, result.Outcome);
        Assert.Equal(ServicePayloadManifestOutcome.Unreadable, result.ManifestOutcome);
    }

    [Fact]
    public void ManifestRootNotAnObject_IsRefused()
    {
        ServicePayloadVerificationResult result = InspectWithManifest("[]");

        Assert.Equal(ServicePayloadVerificationOutcome.ManifestInvalid, result.Outcome);
        Assert.Equal(ServicePayloadManifestOutcome.NotJsonObject, result.ManifestOutcome);
    }

    [Fact]
    public void ManifestWithByteOrderMark_IsRefused()
    {
        byte[] bom = { 0xEF, 0xBB, 0xBF };
        byte[] body = Utf8NoBom(BuildManifestJson(CanonicalMembers()));
        byte[] withBom = bom.Concat(body).ToArray();

        ServicePayloadVerificationResult result =
            Inspect(BuildArchive(CanonicalMembers(), manifestBytesOverride: withBom));

        Assert.Equal(ServicePayloadVerificationOutcome.ManifestEncodingUnexpected, result.Outcome);
    }

    [Fact]
    public void ManifestWithCarriageReturns_IsRefused()
    {
        string crlf = BuildManifestJson(CanonicalMembers()).Replace("\n", "\r\n", StringComparison.Ordinal);

        ServicePayloadVerificationResult result =
            Inspect(BuildArchive(CanonicalMembers(), manifestBytesOverride: Utf8NoBom(crlf)));

        Assert.Equal(ServicePayloadVerificationOutcome.ManifestEncodingUnexpected, result.Outcome);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("\"1\"")]
    public void ManifestUnsupportedSchemaVersion_IsRefused(string schemaVersionLiteral)
    {
        ServicePayloadManifestResult result = ServicePayloadManifestContract.Parse(
            Utf8NoBom(BuildManifestJson(CanonicalMembers(), schemaVersionLiteral: schemaVersionLiteral)));

        Assert.Equal(ServicePayloadManifestOutcome.UnsupportedSchemaVersion, result.Outcome);
    }

    [Fact]
    public void ManifestUnsupportedTargetOsOrArch_IsRefused()
    {
        Assert.Equal(
            ServicePayloadManifestOutcome.UnsupportedTargetOs,
            ServicePayloadManifestContract.Parse(
                Utf8NoBom(BuildManifestJson(CanonicalMembers(), targetOsOverride: "linux"))).Outcome);

        Assert.Equal(
            ServicePayloadManifestOutcome.UnsupportedTargetArch,
            ServicePayloadManifestContract.Parse(
                Utf8NoBom(BuildManifestJson(CanonicalMembers(), targetArchOverride: "arm64"))).Outcome);
    }

    [Fact]
    public void ManifestFilesNotAnArrayOrEmpty_IsRefused()
    {
        Assert.Equal(
            ServicePayloadManifestOutcome.FilesNotArray,
            ServicePayloadManifestContract.Parse(
                Utf8NoBom(BuildManifestJson(CanonicalMembers(), filesAsObject: true))).Outcome);

        Assert.Equal(
            ServicePayloadManifestOutcome.FilesEmpty,
            ServicePayloadManifestContract.Parse(
                Utf8NoBom(BuildManifestJson(new List<(string Name, byte[] Bytes)>()))).Outcome);
    }

    [Fact]
    public void ManifestFileEntryDefects_AreRefused()
    {
        Assert.Equal(
            ServicePayloadManifestOutcome.FileEntryNotObject,
            ParseManifest(BuildManifestJson(CanonicalMembers(), firstEntryNotObject: true)).Outcome);

        Assert.Equal(
            ServicePayloadManifestOutcome.FileEntryUnknownProperty,
            ParseManifest(BuildManifestJson(CanonicalMembers(), firstEntryExtraProperty: "installPath")).Outcome);

        Assert.Equal(
            ServicePayloadManifestOutcome.FileEntryDuplicateProperty,
            ParseManifest(BuildManifestJson(CanonicalMembers(), firstEntryDuplicateName: true)).Outcome);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("sizeBytes")]
    [InlineData("sha256")]
    public void ManifestFileEntryMissingProperty_IsRefused(string omitted)
    {
        Assert.Equal(
            ServicePayloadManifestOutcome.FileEntryMissingProperty,
            ParseManifest(BuildManifestJson(CanonicalMembers(), firstEntryOmitProperty: omitted)).Outcome);
    }

    // ------------------------------------------------------------------
    // Unsafe and colliding names
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("/absolute.dll")]
    [InlineData("C:/rooted.dll")]
    [InlineData("C:\\rooted.dll")]
    [InlineData("../escape.dll")]
    [InlineData("a/../escape.dll")]
    [InlineData("./relative.dll")]
    [InlineData("back\\slash.dll")]
    [InlineData("double//slash.dll")]
    [InlineData("trailing/")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("trailingspace /file.dll")]
    [InlineData("trailingdot./file.dll")]
    public void UnsafeManifestName_IsRefused(string unsafeName)
    {
        Assert.False(ServicePayloadArchiveFormat.IsSafeRelativeName(unsafeName));

        Assert.Equal(
            ServicePayloadManifestOutcome.UnsafePath,
            ParseManifest(BuildManifestJson(CanonicalMembers(), firstEntryNameOverride: unsafeName)).Outcome);
    }

    [Fact]
    public void SafeNames_AreAccepted()
    {
        Assert.True(ServicePayloadArchiveFormat.IsSafeRelativeName("a.dll"));
        Assert.True(ServicePayloadArchiveFormat.IsSafeRelativeName("runtimes/win/lib/net8.0/a.dll"));
    }

    [Fact]
    public void CaseCollidingManifestNames_AreRefused()
    {
        Assert.Equal(
            ServicePayloadManifestOutcome.DuplicatePath,
            ParseManifest(BuildManifestJson(CanonicalMembers(), appendCaseCollidingDuplicate: true)).Outcome);
    }

    // ------------------------------------------------------------------
    // Size and hash
    // ------------------------------------------------------------------

    [Fact]
    public void DeclaredSizeMismatch_IsRefused()
    {
        ServicePayloadVerificationResult result = InspectWithManifest(
            BuildManifestJson(CanonicalMembers(), firstEntrySizeOverride: 999999));

        Assert.Equal(ServicePayloadVerificationOutcome.MemberSizeMismatch, result.Outcome);
    }

    [Fact]
    public void DeclaredHashMismatch_IsRefused()
    {
        ServicePayloadVerificationResult result = InspectWithManifest(
            BuildManifestJson(CanonicalMembers(), firstEntryHashOverride: new string('B', 64)));

        Assert.Equal(ServicePayloadVerificationOutcome.MemberHashMismatch, result.Outcome);
    }

    [Theory]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")] // lowercase
    [InlineData("AABB")]                                                             // too short
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")] // not hex
    public void DeclaredHashNotUppercaseSha256_IsRefused(string badHash)
    {
        Assert.Equal(
            ServicePayloadManifestOutcome.InvalidHash,
            ParseManifest(BuildManifestJson(CanonicalMembers(), firstEntryHashOverride: badHash)).Outcome);
    }

    [Fact]
    public void DeclaredNegativeSize_IsRefused()
    {
        Assert.Equal(
            ServicePayloadManifestOutcome.InvalidSize,
            ParseManifest(BuildManifestJson(CanonicalMembers(), firstEntrySizeOverride: -1)).Outcome);
    }

    // ------------------------------------------------------------------
    // Determinism: ordering, timestamp, compression, structure
    // ------------------------------------------------------------------

    [Fact]
    public void ShuffledEntryOrder_IsRefused()
    {
        List<(string Name, byte[] Bytes)> members = CanonicalMembers();
        List<string> reversedOrder = ServicePayloadArchiveFormat.OrderedEntryNames.Reverse().ToList();

        ServicePayloadVerificationResult result = Inspect(BuildArchive(members, entryOrder: reversedOrder));

        Assert.Equal(ServicePayloadVerificationOutcome.EntryOrderUnexpected, result.Outcome);
    }

    [Fact]
    public void NonFixedEntryTimestamp_IsRefused()
    {
        ServicePayloadVerificationResult result =
            Inspect(BuildArchive(CanonicalMembers(), oddTimestampEntry: "PAXCookbook.Service.dll"));

        Assert.Equal(ServicePayloadVerificationOutcome.EntryTimestampUnexpected, result.Outcome);
    }

    [Fact]
    public void NonFixedCompressionMethod_IsRefused()
    {
        ServicePayloadVerificationResult result =
            Inspect(BuildArchive(CanonicalMembers(), oddCompressionEntry: "PAXCookbook.Service.dll"));

        Assert.Equal(ServicePayloadVerificationOutcome.EntryCompressionUnexpected, result.Outcome);
    }

    [Fact]
    public void DirectoryEntry_IsRefused()
    {
        ServicePayloadVerificationResult result =
            Inspect(BuildArchive(CanonicalMembers(), directoryEntryName: "runtimes/"));

        Assert.Equal(ServicePayloadVerificationOutcome.DirectoryEntryPresent, result.Outcome);
    }

    [Fact]
    public void ArchiveComment_IsRefused()
    {
        ServicePayloadVerificationResult result =
            Inspect(BuildArchive(CanonicalMembers(), archiveComment: "built by hand"));

        Assert.Equal(ServicePayloadVerificationOutcome.CommentPresent, result.Outcome);
    }

    [Fact]
    public void NonArchiveBytes_AreRefused()
    {
        Assert.Equal(
            ServicePayloadVerificationOutcome.ArchiveUnreadable,
            Inspect(Encoding.ASCII.GetBytes("this is not a zip archive at all")).Outcome);

        Assert.Equal(
            ServicePayloadVerificationOutcome.ArchiveUnreadable,
            Inspect(Array.Empty<byte>()).Outcome);
    }

    // ------------------------------------------------------------------
    // No extraction, no machine mutation
    // ------------------------------------------------------------------

    [Fact]
    public void Verification_WritesNothingToDisk()
    {
        string probe = Path.Combine(Path.GetTempPath(), "paxc61-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        string previousCurrent = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(probe);
            string[] before = Directory.GetFileSystemEntries(probe, "*", SearchOption.AllDirectories);

            Assert.Equal(ServicePayloadVerificationOutcome.Verified, Inspect(BuildArchive(CanonicalMembers())).Outcome);

            string[] after = Directory.GetFileSystemEntries(probe, "*", SearchOption.AllDirectories);
            Assert.Empty(before);
            Assert.Empty(after);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCurrent);
            Directory.Delete(probe, recursive: true);
        }
    }

    [Fact]
    public void HelperProductionSources_ContainNoExecutionOrMutationSurface()
    {
        string[] sources =
        {
            "src/PAXCookbook.ServiceAdminHelper/Program.cs",
            "src/PAXCookbook.ServiceAdminHelper/Payload/ServicePayloadArchiveFormat.cs",
            "src/PAXCookbook.ServiceAdminHelper/Payload/ServicePayloadManifestContract.cs",
            "src/PAXCookbook.ServiceAdminHelper/Payload/ServicePayloadArchiveVerifier.cs",
            "src/PAXCookbook.ServiceAdminHelper/Payload/ServicePayloadResource.cs",
            "src/PAXCookbook.ServiceAdminHelper/Signing/ServiceHelperSigningPolicy.cs",
        };

        // Tokens are the API FORMS, not bare words: "ServiceController" alone
        // would fire on the legitimate frozen member name
        // System.ServiceProcess.ServiceController.dll.
        string[] forbidden =
        {
            "Process.Start",
            "ProcessStartInfo",
            "using System.ServiceProcess;",
            "RegistryKey",
            "Microsoft.Win32.Registry",
            "ExtractToDirectory",
            "ExtractToFile",
            "File.WriteAll",
            "File.AppendAll",
            "File.Delete",
            "File.Move",
            "File.Copy",
            "File.Replace",
            "Directory.CreateDirectory",
            "Directory.Delete",
            "FileStream",
            "StreamWriter",
            "ShellExecute",
            "X509Certificate",
        };

        string root = RepoRoot();
        foreach (string relative in sources)
        {
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), "missing helper source: " + relative);

            string text = File.ReadAllText(full);
            foreach (string token in forbidden)
            {
                Assert.False(
                    text.Contains(token, StringComparison.Ordinal),
                    relative + " contains forbidden token " + token);
            }
        }
    }

    // ------------------------------------------------------------------
    // Signing policy - structure only, no live verification
    // ------------------------------------------------------------------

    [Fact]
    public void PrereleaseUnsignedAllowance_RequiresAnExplicitBuildOptIn()
    {
        // This test project is built WITHOUT /p:PrereleaseUnsignedHelper=true,
        // so the compile-time opt-in must be absent and the policy must not be
        // an allowance.
        Assert.False(ServiceHelperSigningPolicy.PrereleaseUnsignedOptInCompiledIn);
        Assert.NotEqual(
            ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed,
            ServiceHelperSigningPolicy.ResolveConfiguredPolicy());
    }

    [Fact]
    public void AbsentExpectedPublisher_FailsClosed()
    {
        Assert.False(ServiceHelperSigningPolicy.ExpectedPublisherConfigured);
        Assert.Equal(
            ServiceHelperSigningPolicyState.SigningPolicyUnavailable,
            ServiceHelperSigningPolicy.ResolveConfiguredPolicy());

        // Even a perfect-looking observation cannot become permissive while the
        // expected publisher is unconfigured.
        Assert.Equal(
            ServiceHelperSigningPolicyState.SigningPolicyUnavailable,
            ServiceHelperSigningPolicy.EvaluateObservation(
                ServiceHelperSignatureObservation.ValidWithPublisher, "CN=Anything"));
    }

    [Fact]
    public void NoObservationEverProducesATrustedResultInThisBuild()
    {
        // Enumerated in the test body rather than through InlineData because the
        // observation enum is internal to the helper.
        ServiceHelperSignatureObservation[] observations =
        {
            ServiceHelperSignatureObservation.Unspecified,
            ServiceHelperSignatureObservation.Absent,
            ServiceHelperSignatureObservation.Invalid,
            ServiceHelperSignatureObservation.UntrustedChain,
            ServiceHelperSignatureObservation.Revoked,
            ServiceHelperSignatureObservation.ExpiredWithoutValidTimestamp,
            ServiceHelperSignatureObservation.SelfSignedDevelopmentCertificate,
            ServiceHelperSignatureObservation.ValidWithPublisher,
        };

        foreach (ServiceHelperSignatureObservation observation in observations)
        {
            foreach (string? publisher in new[] { null, "", "CN=PAX Cookbook" })
            {
                ServiceHelperSigningPolicyState state =
                    ServiceHelperSigningPolicy.EvaluateObservation(observation, publisher);

                Assert.NotEqual(ServiceHelperSigningPolicyState.TrustedPublisherSatisfied, state);
                Assert.NotEqual(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed, state);
            }
        }
    }

    [Fact]
    public void SigningPolicy_HasNoRuntimeOverride()
    {
        MethodInfo resolve = typeof(ServiceHelperSigningPolicy)
            .GetMethod("ResolveConfiguredPolicy", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        Assert.NotNull(resolve);
        Assert.Empty(resolve.GetParameters());

        string policySource = File.ReadAllText(Path.Combine(
            RepoRoot(),
            "src".Replace('/', Path.DirectorySeparatorChar),
            "PAXCookbook.ServiceAdminHelper",
            "Signing",
            "ServiceHelperSigningPolicy.cs"));

        foreach (string token in new[]
        {
            "Environment.GetEnvironmentVariable",
            "GetCommandLineArgs",
            "AppContext.GetData",
            "ConfigurationManager",
            "Registry",
        })
        {
            Assert.False(
                policySource.Contains(token, StringComparison.Ordinal),
                "signing policy exposes a runtime override: " + token);
        }
    }

    // ------------------------------------------------------------------
    // Sibling launch resolution - defined, bounded, and uninvoked in product
    // ------------------------------------------------------------------

    [Fact]
    public void SiblingResolver_AcceptsNoCallerPath()
    {
        MethodInfo resolve = typeof(ServiceAdminHelperLocationResolver)
            .GetMethod("TryResolveSiblingHelper", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        Assert.NotNull(resolve);
        Assert.Empty(resolve.GetParameters());

        Assert.Equal("PAXCookbookServiceAdminHelper.exe", ServiceAdminHelperLocationContract.HelperFileName);
        Assert.Equal("PAXCookbookSetup.dll", ServiceAdminHelperLocationContract.SetupAssemblyFileName);
        Assert.Equal("dotnet.exe", ServiceAdminHelperLocationContract.DotnetHostFileName);
    }

    [Fact]
    public void SiblingResolver_RefusesAnythingButTheInstalledDotnetHostedSetup()
    {
        // The test host is neither dotnet.exe running PAXCookbookSetup.dll nor
        // anything else this resolver is allowed to accept.
        Assert.Equal(
            ServiceAdminHelperLocationOutcome.NotDotnetHostedSetup,
            ServiceAdminHelperLocationResolver.TryResolveSiblingHelper().Outcome);
    }

    [Fact]
    public void SiblingResolver_RefusesMissingDirectoryAndMissingHelper()
    {
        string absent = Path.Combine(Path.GetTempPath(), "paxc61-absent-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(
            ServiceAdminHelperLocationOutcome.SetupLocationUnavailable,
            ServiceAdminHelperLocationResolver.ResolveWithin(absent).Outcome);

        Assert.Equal(
            ServiceAdminHelperLocationOutcome.SetupLocationUnavailable,
            ServiceAdminHelperLocationResolver.ResolveWithin(string.Empty).Outcome);

        string empty = Path.Combine(Path.GetTempPath(), "paxc61-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Equal(
                ServiceAdminHelperLocationOutcome.HelperMissing,
                ServiceAdminHelperLocationResolver.ResolveWithin(empty).Outcome);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void SiblingResolver_RefusesWrongTypeAndResolvesARealFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "paxc61-shape-" + Guid.NewGuid().ToString("N"));
        string asDirectory = Path.Combine(root, ServiceAdminHelperLocationContract.HelperFileName);
        Directory.CreateDirectory(asDirectory);
        try
        {
            Assert.Equal(
                ServiceAdminHelperLocationOutcome.HelperNotAFile,
                ServiceAdminHelperLocationResolver.ResolveWithin(root).Outcome);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        string second = Path.Combine(Path.GetTempPath(), "paxc61-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(second);
        string helper = Path.Combine(second, ServiceAdminHelperLocationContract.HelperFileName);
        File.WriteAllBytes(helper, new byte[] { 0x4D, 0x5A });
        try
        {
            ServiceAdminHelperLocationResult resolved =
                ServiceAdminHelperLocationResolver.ResolveWithin(second + Path.DirectorySeparatorChar);

            Assert.Equal(ServiceAdminHelperLocationOutcome.Resolved, resolved.Outcome);
            Assert.Equal(helper, resolved.ResolvedPath);
        }
        finally
        {
            Directory.Delete(second, recursive: true);
        }
    }

    // ==================================================================
    // Private helpers. All in-memory; none of them touches the product.
    // ==================================================================

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static byte[] Utf8NoBom(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string Sha256Upper(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] MemberContent(string name) =>
        Encoding.ASCII.GetBytes(new string('A', 512) + name);

    private static List<(string Name, byte[] Bytes)> CanonicalMembers() =>
        ServicePayloadArchiveFormat.RequiredMemberNames
            .Select(name => (Name: name, Bytes: MemberContent(name)))
            .ToList();

    private static ServicePayloadVerificationResult Inspect(byte[] archive) =>
        ServicePayloadResource.InspectBytes(archive);

    private static ServicePayloadManifestResult ParseManifest(string manifestText) =>
        ServicePayloadManifestContract.Parse(Utf8NoBom(manifestText));

    private static ServicePayloadVerificationResult InspectWithManifest(string manifestText) =>
        Inspect(BuildArchive(CanonicalMembers(), manifestBytesOverride: Utf8NoBom(manifestText)));

    /// <summary>
    /// The test-side deterministic composer. It follows exactly the rules the
    /// production format constants declare: fixed ordinal entry order, a fixed
    /// DOS-representable timestamp, one fixed compression method, no directory
    /// entries and no comments.
    /// </summary>
    private static byte[] BuildArchive(
        List<(string Name, byte[] Bytes)> members,
        byte[]? manifestBytesOverride = null,
        bool includeManifest = true,
        IReadOnlyList<string>? entryOrder = null,
        string? oddTimestampEntry = null,
        string? oddCompressionEntry = null,
        string? archiveComment = null,
        string? directoryEntryName = null)
    {
        Dictionary<string, byte[]> content = members.ToDictionary(m => m.Name, m => m.Bytes, StringComparer.Ordinal);
        byte[] manifestBytes = manifestBytesOverride ?? Utf8NoBom(BuildManifestJson(members));

        List<string> order;
        if (entryOrder is not null)
        {
            order = entryOrder.ToList();
        }
        else
        {
            order = new List<string>();
            if (includeManifest)
            {
                order.Add(ServicePayloadArchiveFormat.ManifestEntryName);
            }
            order.AddRange(members.Select(m => m.Name));
        }

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (archiveComment is not null)
            {
                archive.Comment = archiveComment;
            }

            foreach (string name in order)
            {
                CompressionLevel level = string.Equals(name, oddCompressionEntry, StringComparison.Ordinal)
                    ? CompressionLevel.Optimal
                    : ServicePayloadArchiveFormat.FixedCompressionLevel;

                ZipArchiveEntry entry = archive.CreateEntry(name, level);
                entry.LastWriteTime = string.Equals(name, oddTimestampEntry, StringComparison.Ordinal)
                    ? new DateTimeOffset(1999, 6, 6, 0, 0, 0, TimeSpan.Zero)
                    : ServicePayloadArchiveFormat.FixedEntryTimestamp;

                byte[] bytes = string.Equals(name, ServicePayloadArchiveFormat.ManifestEntryName, StringComparison.Ordinal)
                    ? manifestBytes
                    : content[name];

                using Stream stream = entry.Open();
                stream.Write(bytes, 0, bytes.Length);
            }

            if (directoryEntryName is not null)
            {
                ZipArchiveEntry directory =
                    archive.CreateEntry(directoryEntryName, ServicePayloadArchiveFormat.FixedCompressionLevel);
                directory.LastWriteTime = ServicePayloadArchiveFormat.FixedEntryTimestamp;
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The canonical manifest text, plus every deliberate defect the focused
    /// tests need. Generated rather than string-patched so each defect is exact.
    /// </summary>
    private static string BuildManifestJson(
        List<(string Name, byte[] Bytes)> members,
        string? extraTopLevelProperty = null,
        bool duplicateTargetOs = false,
        string? omitTopLevelProperty = null,
        string? schemaVersionLiteral = null,
        string? targetOsOverride = null,
        string? targetArchOverride = null,
        bool filesAsObject = false,
        bool firstEntryNotObject = false,
        string? firstEntryExtraProperty = null,
        bool firstEntryDuplicateName = false,
        string? firstEntryOmitProperty = null,
        string? firstEntryNameOverride = null,
        long? firstEntrySizeOverride = null,
        string? firstEntryHashOverride = null,
        bool appendCaseCollidingDuplicate = false)
    {
        var entries = new List<string>();
        for (int i = 0; i < members.Count; i++)
        {
            bool first = i == 0;
            (string Name, byte[] Bytes) member = members[i];

            if (first && firstEntryNotObject)
            {
                entries.Add("    \"not-an-object\"");
                continue;
            }

            string name = first && firstEntryNameOverride is not null ? firstEntryNameOverride : member.Name;
            string size = (first && firstEntrySizeOverride.HasValue
                ? firstEntrySizeOverride.Value
                : member.Bytes.LongLength).ToString(CultureInfo.InvariantCulture);
            string hash = first && firstEntryHashOverride is not null
                ? firstEntryHashOverride
                : Sha256Upper(member.Bytes);

            var properties = new List<string>();
            if (first && firstEntryExtraProperty is not null)
            {
                properties.Add("      \"" + firstEntryExtraProperty + "\": \"x\"");
            }
            if (!(first && firstEntryOmitProperty == "name"))
            {
                properties.Add("      \"name\": \"" + name + "\"");
            }
            if (first && firstEntryDuplicateName)
            {
                properties.Add("      \"name\": \"" + name + "\"");
            }
            if (!(first && firstEntryOmitProperty == "sizeBytes"))
            {
                properties.Add("      \"sizeBytes\": " + size);
            }
            if (!(first && firstEntryOmitProperty == "sha256"))
            {
                properties.Add("      \"sha256\": \"" + hash + "\"");
            }

            entries.Add("    {\n" + string.Join(",\n", properties) + "\n    }");
        }

        if (appendCaseCollidingDuplicate && members.Count > 0)
        {
            (string Name, byte[] Bytes) first = members[0];
            entries.Add(
                "    {\n"
                + "      \"name\": \"" + first.Name.ToLowerInvariant() + "\",\n"
                + "      \"sizeBytes\": " + first.Bytes.LongLength.ToString(CultureInfo.InvariantCulture) + ",\n"
                + "      \"sha256\": \"" + Sha256Upper(first.Bytes) + "\"\n"
                + "    }");
        }

        string filesText = filesAsObject
            ? "{}"
            : entries.Count == 0
                ? "[]"
                : "[\n" + string.Join(",\n", entries) + "\n  ]";

        var topLevel = new List<string>();
        if (extraTopLevelProperty is not null)
        {
            topLevel.Add("  \"" + extraTopLevelProperty + "\": 1");
        }
        if (omitTopLevelProperty != "schemaVersion")
        {
            topLevel.Add("  \"schemaVersion\": "
                + (schemaVersionLiteral ?? ServicePayloadArchiveFormat.SchemaVersion.ToString(CultureInfo.InvariantCulture)));
        }
        if (omitTopLevelProperty != "targetOs")
        {
            topLevel.Add("  \"targetOs\": \"" + (targetOsOverride ?? ServicePayloadArchiveFormat.TargetOs) + "\"");
        }
        if (duplicateTargetOs)
        {
            topLevel.Add("  \"targetOs\": \"" + ServicePayloadArchiveFormat.TargetOs + "\"");
        }
        if (omitTopLevelProperty != "targetArch")
        {
            topLevel.Add("  \"targetArch\": \"" + (targetArchOverride ?? ServicePayloadArchiveFormat.TargetArch) + "\"");
        }
        if (omitTopLevelProperty != "files")
        {
            topLevel.Add("  \"files\": " + filesText);
        }

        return "{\n" + string.Join(",\n", topLevel) + "\n}";
    }
}
