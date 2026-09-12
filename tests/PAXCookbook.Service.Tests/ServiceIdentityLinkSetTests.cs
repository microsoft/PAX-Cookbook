using System;
using System.IO;
using System.Text.RegularExpressions;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Service.Tests;

// ===========================================================================
// CYCLE 39 - SERVICE-SIDE LINK SET FOR THE FIXED SERVICE IDENTITY
// ===========================================================================
//
// WHY THIS FILE EXISTS. ServiceIdentityContract DELEGATES service-SID shape to
// ServiceOwnershipLedgerContract (ruling E1) instead of re-implementing it. That
// delegation is only safe if BOTH files land in the SAME compilation as
// ServiceContract. If a future edit ever linked the identity contract without the
// ledger contract, the service would not compile - but if it ever linked a
// DIFFERENT copy of either, the service and Setup could silently disagree about
// what a service SID is. These tests pin the link set.
//
// SCOPE. Linking two PURE contracts adds NO capability to the service host: no
// certificate store, no private key handle, no ACL API, no registry handle, no
// credential vault, no service-control channel, no elevation path, no network
// stack, no native lookup, and no executor. The RESOLVER is NOT linked here and
// is not reachable from this assembly at all - that is asserted below.
public sealed class ServiceIdentityLinkSetTests
{
    // ---- both contracts are in the same compilation ---------------------------

    [Fact]
    public void The_identity_contract_and_the_ledger_contract_share_the_service_compilation()
    {
        Assert.Equal(
            typeof(ServiceContract).Assembly,
            typeof(ServiceIdentityContract).Assembly);

        Assert.Equal(
            typeof(ServiceContract).Assembly,
            typeof(ServiceOwnershipLedgerContract).Assembly);

        Assert.Equal(
            "PAXCookbook.Service",
            typeof(ServiceIdentityContract).Assembly.GetName().Name);
    }

    [Fact]
    public void The_delegation_target_resolves_inside_the_service_compilation()
    {
        // Delegation is not merely declared: it EXECUTES here, in the service
        // assembly, and agrees with the delegate target case for case.
        foreach (string sid in new[] { "S-1-5-80-1", "S-1-5-80-1-2-3-4-5-6" })
        {
            Assert.True(ServiceIdentityContract.IsServiceIdentitySid(sid));
            Assert.Equal(
                ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(sid),
                ServiceIdentityContract.IsServiceIdentitySid(sid));
        }

        foreach (string sid in new[] { "S-1-5-80-0", "S-1-5-80", "S-1-5-18", "not-a-sid" })
        {
            Assert.False(ServiceIdentityContract.IsServiceIdentitySid(sid));
            Assert.Equal(
                ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(sid),
                ServiceIdentityContract.IsServiceIdentitySid(sid));
        }
    }

    // ---- ServiceContract derives, and preserves, the two fixed names ----------

    [Fact]
    public void The_service_contract_derives_both_names_from_the_identity_contract()
    {
        Assert.Equal(ServiceIdentityContract.ServiceName, ServiceContract.ServiceName);
        Assert.Equal(ServiceIdentityContract.ServiceDisplayName, ServiceContract.ServiceDisplayName);

        // The exact shipped values are UNCHANGED by the derivation.
        Assert.Equal("PAXCookbookService", ServiceContract.ServiceName);
        Assert.Equal("PAXCookbook Machine Service", ServiceContract.ServiceDisplayName);
    }

    [Fact]
    public void The_service_contract_source_derives_rather_than_retypes_the_names()
    {
        string source = File.ReadAllText(Path.Combine(
            ServiceCookPreparationBoundaryTests.ServiceSourceRoot(), "ServiceContract.cs"));
        string code = CSharpLexicalScanner.ExtractCode(source);

        // POSITIVE CONTROL: the scanner really read this file's code.
        Assert.Contains("class ServiceContract", code, StringComparison.Ordinal);

        Assert.Contains("ServiceIdentityContract.ServiceName", code, StringComparison.Ordinal);
        Assert.Contains("ServiceIdentityContract.ServiceDisplayName", code, StringComparison.Ordinal);

        // The literals are no longer retyped in the service contract's CODE, so
        // the two sources cannot drift. (The scanner removes literal CONTENT, so
        // a match here would mean a real code-position occurrence.)
        Assert.DoesNotContain("PAXCookbookService", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PAXCookbook Machine Service", code, StringComparison.Ordinal);

        // POSITIVE CONTROL for the two refusals above: the same scanner applied to
        // the PRE-CYCLE-39 shape of that declaration DOES see the retyped literal.
        Assert.Contains(
            "PAXCookbookService",
            CSharpLexicalScanner.ExtractCode(
                "internal const string ServiceName = \"PAXCookbookService\" + PAXCookbookService;"),
            StringComparison.Ordinal);
    }

    // ---- the csproj link set --------------------------------------------------

    [Fact]
    public void The_service_project_links_the_identity_contract_and_still_adds_no_project_reference()
    {
        string root = ServiceCookPreparationBoundaryTests.RepoRoot();
        string csproj = File.ReadAllText(Path.Combine(
            root, "src", "PAXCookbook.Service", "PAXCookbook.Service.csproj"));

        Assert.Contains(
            @"..\PAXCookbook.Shared\Contracts\ServiceIdentityContract.cs",
            csproj,
            StringComparison.Ordinal);
        Assert.Contains(
            @"..\PAXCookbook.Shared\Contracts\ServiceOwnershipLedgerContract.cs",
            csproj,
            StringComparison.Ordinal);

        // A project reference would defeat the whole point of linking.
        Assert.DoesNotContain("<ProjectReference", csproj, StringComparison.Ordinal);
        Assert.Equal(0, Regex.Matches(csproj, "<ProjectReference").Count);

        // Exactly five linked contract files (cycle 51 deliberately added
        // ServiceMachineStorageContract.cs), so an accidental sixth link is a
        // deliberate decision rather than a silent one.
        Assert.Equal(5, Regex.Matches(csproj, "<Compile Include=").Count);

        // The linked paths resolve to real files.
        foreach (string leaf in new[]
                 {
                     "CookPreparationSequence.cs",
                     "ServiceOwnershipLedgerContract.cs",
                     "ServiceOwnershipLifecyclePlanner.cs",
                     "ServiceIdentityContract.cs",
                     "ServiceMachineStorageContract.cs",
                 })
        {
            Assert.True(
                File.Exists(Path.Combine(root, "src", "PAXCookbook.Shared", "Contracts", leaf)),
                leaf + " was expected to exist");
        }
    }

    // ---- the resolver never entered the service -------------------------------

    private static readonly string[] ResolverTokens =
    {
        "ServiceSidResolver",
        "ResolveFixedServiceSid",
        "ServiceSidResolution",
        "ServiceSidResolutionState",
        "ServiceSidLookupInterpreter",
        "ServiceSidBufferPlan",
        "LookupAccountName",
    };

    [Fact]
    public void No_service_source_file_references_the_setup_side_resolver()
    {
        string[] files = ServiceCookPreparationBoundaryTests.ServiceSourceFiles();

        // "No offenders" is meaningless if the enumeration is empty.
        Assert.NotEmpty(files);
        Assert.True(files.Length >= 5, "expected the authored service sources, found " + files.Length);

        var offenders = new System.Collections.Generic.List<string>();
        foreach (string f in files)
        {
            string code = CSharpLexicalScanner.ExtractCode(File.ReadAllText(f));
            foreach (string token in ResolverTokens)
            {
                if (code.Contains(token, StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetFileName(f) + ":" + token);
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_resolver_scan_can_actually_fire()
    {
        // POSITIVE CONTROL for every token above. A scanner that can never match
        // would make the previous test unfalsifiable.
        foreach (string token in ResolverTokens)
        {
            Assert.False(string.IsNullOrWhiteSpace(token));

            string synthetic = "class X { void M() { var y = " + token + "; } }";
            Assert.Contains(
                token,
                CSharpLexicalScanner.ExtractCode(synthetic),
                StringComparison.Ordinal);

            // ...and the same token in COMMENT or STRING position is correctly
            // NOT treated as code, so the scan is string-aware in both directions.
            Assert.DoesNotContain(
                token,
                CSharpLexicalScanner.ExtractCode("// " + token + "\nclass Y { }"),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                token,
                CSharpLexicalScanner.ExtractCode("class Z { string s = \"" + token + "\"; }"),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_service_assembly_contains_no_resolver_type()
    {
        foreach (Type t in typeof(ServiceContract).Assembly.GetTypes())
        {
            foreach (string token in ResolverTokens)
            {
                Assert.DoesNotContain(token, t.Name, StringComparison.Ordinal);
            }
        }

        // POSITIVE CONTROL: the same enumeration DOES see the linked contracts.
        Assert.Contains(
            typeof(ServiceContract).Assembly.GetTypes(),
            t => t.Name == "ServiceIdentityContract");
        Assert.Contains(
            typeof(ServiceContract).Assembly.GetTypes(),
            t => t.Name == "ServiceOwnershipLedgerContract");
    }
}
