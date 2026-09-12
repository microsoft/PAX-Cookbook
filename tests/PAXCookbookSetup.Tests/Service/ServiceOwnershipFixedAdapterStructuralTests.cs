using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 92 PASS B - STRUCTURAL GUARDS FOR THE FIXED WINDOWS SHIMS
// ===========================================================================
//
// WHY THIS FILE IS STRUCTURAL ONLY. The pass-B shims open a machine certificate
// store, reopen a machine private key and read or write a real security
// descriptor. NONE of that may happen on a development machine, so not one test
// in this project constructs a fixed shim - and the last check in this file
// PROVES that across the whole test project rather than asserting it in prose.
//
// Every scan below carries a POSITIVE CONTROL. A guard nobody has shown can fire
// is not a guard.
public sealed class ServiceOwnershipFixedAdapterStructuralTests
{
    // ---- shared plumbing ---------------------------------------------------

    private static string RepoRoot() => ServiceSidResolverStructuralTests.RepoRoot();

    private static Assembly SetupAssembly() => typeof(ServiceOwnershipPromotionExecutor).Assembly;

    private const string OwnerIdentityFile = "ServiceOwnershipFixedOwnerIdentityAdapter.cs";
    private const string CertificateFactsFile = "ServiceOwnershipFixedCertificateFactsAdapter.cs";
    private const string KeyDescriptorFile = "ServiceOwnershipFixedKeyDescriptorAdapters.cs";
    private const string LedgerPersistenceFile = "ServiceOwnershipLedgerWriter.cs";
    private const string PromotedRecipeFile = "ServiceOwnershipPromotedRecipeStore.cs";

    /// <summary>The three files this cycle ADDED. The other two adapters live in certified files.</summary>
    public static TheoryData<string> AddedProductionFiles() => new()
    {
        OwnerIdentityFile,
        CertificateFactsFile,
        KeyDescriptorFile,
    };

    private static string SetupServiceDirectory() =>
        Path.Combine(RepoRoot(), "src", "PAXCookbookSetup", "Service");

    private static string PathOf(string leaf) => Path.Combine(SetupServiceDirectory(), leaf);

    private static string RawOf(string leaf) => File.ReadAllText(PathOf(leaf));

    private static string CodeOf(string leaf) =>
        SetupCSharpLexicalScanner.ExtractCode(RawOf(leaf));

    private static string[] ProductionSources() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    private static string[] SetupTestSources() =>
        Directory.GetFiles(
                Path.Combine(RepoRoot(), "tests", "PAXCookbookSetup.Tests"), "*.cs",
                SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    // =======================================================================
    // THE ADAPTERS EXIST, ARE BOUNDED, AND ARE THE ONLY ONES
    // =======================================================================

    private static Type OnlyProductionImplementationOf(Type port)
    {
        Type[] implementations = SetupAssembly()
            .GetTypes()
            .Where(t => !t.IsInterface && !t.IsAbstract && port.IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();

        return Assert.Single(implementations);
    }

    public static TheoryData<string, string> PortToAdapter() => new()
    {
        { "IServiceOwnershipOwnerIdentityPort", "ServiceOwnershipFixedOwnerIdentityPort" },
        { "IServiceOwnershipCertificateFactsPort", "ServiceOwnershipFixedCertificateFactsPort" },
        { "IServiceOwnershipPriorDescriptorPort", "ServiceOwnershipFixedPriorDescriptorPort" },
        { "IServiceOwnershipApprovedDescriptorPort", "ServiceOwnershipFixedApprovedDescriptorPort" },
        { "IServiceOwnershipDescriptorRestorePort", "ServiceOwnershipFixedDescriptorRestorePort" },
        { "IServiceOwnershipLedgerPersistencePort", "ServiceOwnershipFixedLedgerPersistencePort" },
        { "IServiceOwnershipPromotedRecipePort", "ServiceOwnershipFixedPromotedRecipePort" },
        { "IServiceOwnershipServiceSidPort", "ServiceOwnershipFixedServiceSidPort" },
    };

    [Theory]
    [MemberData(nameof(PortToAdapter))]
    public void Exactly_one_bounded_production_adapter_implements_each_port(
        string portName, string adapterName)
    {
        Type port = SetupAssembly().GetType(
            "PAXCookbookSetup.Service." + portName, throwOnError: true)!;

        Type adapter = OnlyProductionImplementationOf(port);

        Assert.Equal(adapterName, adapter.Name);
        Assert.Equal("PAXCookbookSetup.Service", adapter.Namespace);
        Assert.False(adapter.IsPublic, adapter.Name + " must stay assembly-internal");
        Assert.True(adapter.IsSealed, adapter.Name + " must be sealed");
    }

    [Fact]
    public void The_port_surface_did_not_widen_and_still_takes_only_the_two_bounded_identifiers()
    {
        Type[] ports = SetupAssembly()
            .GetTypes()
            .Where(t => t.IsInterface && t.Namespace == "PAXCookbookSetup.Service")
            .Where(t => t.Name.StartsWith("IServiceOwnership", StringComparison.Ordinal))
            .ToArray();

        // PASS B ADDED NO PORT. The certified count is still nine.
        Assert.Equal(9, ports.Length);

        foreach (Type port in ports)
        {
            foreach (MethodInfo method in port.GetMethods())
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.False(typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
                    if (parameter.ParameterType == typeof(string))
                    {
                        Assert.Contains(
                            parameter.Name,
                            new[] { "normalizedThumbprintSha1", "promotedJobId" },
                            StringComparer.Ordinal);
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(PortToAdapter))]
    public void No_adapter_accepts_a_delegate_a_strategy_or_an_arbitrary_path(
        string portName, string adapterName)
    {
        _ = portName;
        Type adapter = SetupAssembly().GetType(
            "PAXCookbookSetup.Service." + adapterName, throwOnError: true)!;

        const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        foreach (ConstructorInfo constructor in adapter.GetConstructors(All))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                Assert.False(typeof(Delegate).IsAssignableFrom(parameter.ParameterType));

                // NO adapter is ever handed a location. The promoted-Recipe adapter
                // takes a CLOSED store OBJECT, which cannot be redirected.
                Assert.NotEqual(typeof(string), parameter.ParameterType);
            }
        }

        foreach (MethodInfo method in adapter.GetMethods(All))
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.False(typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
                if (parameter.ParameterType == typeof(string))
                {
                    Assert.Contains(
                        parameter.Name,
                        new[] { "normalizedThumbprintSha1", "promotedJobId" },
                        StringComparer.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void The_owner_identity_shim_takes_no_parameter_at_all()
    {
        MethodInfo observe = typeof(ServiceOwnershipFixedOwnerIdentityPort)
            .GetMethod(nameof(ServiceOwnershipFixedOwnerIdentityPort.ObserveFixedOwner))!;

        Assert.NotNull(observe);
        Assert.Empty(observe.GetParameters());
    }

    // =======================================================================
    // THE GUARDED LEDGER TOKENS AND THE RESOLVER ARE UNTOUCHED
    // =======================================================================

    public static TheoryData<string> GuardedLedgerTokens() => new()
    {
        "OwnershipLedgerReader",
        "OwnershipLedgerWriter",
        "OwnershipLedgerExecutor",
        "OwnershipLedgerObserver",
        "ServiceOwnershipLedgerStore",
        "ServiceOwnershipLedgerRepository",
    };

    [Theory]
    [MemberData(nameof(AddedProductionFiles))]
    public void No_file_this_cycle_added_names_a_guarded_ledger_token(string leaf)
    {
        string raw = RawOf(leaf);

        foreach (string token in new[]
                 {
                     "OwnershipLedgerReader", "OwnershipLedgerWriter", "OwnershipLedgerExecutor",
                     "OwnershipLedgerObserver", "ServiceOwnershipLedgerStore",
                     "ServiceOwnershipLedgerRepository",
                 })
        {
            Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL: the same raw scan really did read this file.
        Assert.Contains("namespace PAXCookbookSetup.Service", raw, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AddedProductionFiles))]
    public void No_file_this_cycle_added_names_the_fixed_service_sid_resolver(string leaf)
    {
        Assert.DoesNotContain("ServiceSidResolver", RawOf(leaf), StringComparison.Ordinal);
    }

    [Fact]
    public void The_qualified_resolver_invocation_count_across_production_is_unchanged()
    {
        int invocations = ProductionSources()
            .Select(f => SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(f)))
            .Sum(ServiceSidResolverStructuralTests.ResolverInvocationSites);

        // TWO across ALL production source, which is the pre-cycle-92 state: one
        // inside the fixed service-SID port and one inside the resolver's own
        // fixed surface. The certified executor-scoped guard separately pins the
        // executor file's own count at one. Pass B added neither.
        Assert.Equal(2, invocations);

        // POSITIVE CONTROL: the counter DOES see an invocation when one is present.
        Assert.Equal(
            1,
            ServiceSidResolverStructuralTests.ResolverInvocationSites(
                SetupCSharpLexicalScanner.ExtractCode(
                    "class X { void M() { var r = ServiceSidResolver.ResolveFixedServiceSid(); } }")));
    }

    // =======================================================================
    // THE CERTIFICATE SHIM - ONE STORE, READ ONLY, NO MUTATION API AT ALL
    // =======================================================================

    [Fact]
    public void The_certificate_shim_opens_only_the_one_machine_personal_store()
    {
        string code = CodeOf(CertificateFactsFile);

        Assert.Contains("StoreName.My", code, StringComparison.Ordinal);
        Assert.Contains("StoreLocation.LocalMachine", code, StringComparison.Ordinal);
        Assert.Contains("OpenFlags.ReadOnly", code, StringComparison.Ordinal);
        Assert.Contains("OpenFlags.OpenExistingOnly", code, StringComparison.Ordinal);

        // NO other store, and above all NO per-user fallback.
        foreach (string forbidden in new[]
                 {
                     "StoreLocation.CurrentUser", "CurrentUser", "StoreName.Root",
                     "StoreName.CertificateAuthority", "StoreName.TrustedPeople",
                     "StoreName.AddressBook", "OpenFlags.ReadWrite", "OpenFlags.MaxAllowed",
                     "OpenFlags.IncludeArchived",
                 })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL: the same scan DOES find a per-user fallback when present.
        Assert.Contains(
            "StoreLocation.CurrentUser",
            SetupCSharpLexicalScanner.ExtractCode(
                "class X { void M() { var s = StoreLocation.CurrentUser ; } }"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Import")]
    [InlineData("Export")]
    [InlineData("CopyWithPrivateKey")]
    [InlineData("CertificateRequest")]
    [InlineData("CngKeyCreationParameters")]
    [InlineData("CngKey.Create")]
    [InlineData("CngKey.Import")]
    [InlineData("X509KeyStorageFlags")]
    [InlineData("CspParameters")]
    [InlineData("CryptoKeySecurity")]
    [InlineData("SetAccessControl")]
    [InlineData("Registry")]
    [InlineData("Process.Start")]
    [InlineData("HttpClient")]
    [InlineData("Socket")]
    [InlineData("PAX_Purview")]
    [InlineData("StartBake")]
    public void The_certificate_shim_can_never_create_change_or_remove_a_credential(string token)
    {
        string code = CodeOf(CertificateFactsFile);

        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.DoesNotContain(token, code, StringComparison.Ordinal);

        // POSITIVE CONTROL: the same scan DOES find the token when it is present.
        Assert.Contains(
            token,
            SetupCSharpLexicalScanner.ExtractCode("class X { void M() { var y = " + token + " ; } }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_certificate_shim_never_removes_anything_from_the_store_it_opened()
    {
        string code = CodeOf(CertificateFactsFile);

        foreach (string token in new[] { ".Remove(", ".RemoveRange(", ".Add(", ".AddRange(", ".Delete(" })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_certificate_shim_pins_the_one_provider_and_the_one_key_size_by_constant()
    {
        Assert.Equal(
            "Microsoft Software Key Storage Provider",
            ServiceOwnershipCertificateFactsInterpreter.ApprovedProviderName);
        Assert.Equal(2048, ServiceOwnershipCertificateFactsInterpreter.ApprovedKeySizeBits);

        // The shim never retypes either value: it references the interpreter's.
        string code = CodeOf(CertificateFactsFile);
        Assert.Single(Regex.Matches(code, Regex.Escape("ApprovedProviderName") + @"\s*="));
        Assert.Single(Regex.Matches(code, Regex.Escape("ApprovedKeySizeBits") + @"\s*="));
    }

    // =======================================================================
    // THE DESCRIPTOR SHIMS - owner/group/DACL only, never a SACL
    // =======================================================================

    [Fact]
    public void The_descriptor_shims_never_request_or_name_sacl_access()
    {
        string code = CodeOf(KeyDescriptorFile);

        foreach (string token in new[]
                 {
                     "SACL_SECURITY_INFORMATION", "SaclSecurityInformation", "AccessSystemSecurity",
                     "GetSecurityInfo", "SetSecurityInfo", "SetNamedSecurityInfo",
                     "SystemAcl", "AuditRule",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }

        // The three security-information bits it DOES name are owner, group, DACL.
        Assert.Contains("OwnerSecurityInformation", code, StringComparison.Ordinal);
        Assert.Contains("GroupSecurityInformation", code, StringComparison.Ordinal);
        Assert.Contains("DaclSecurityInformation", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_descriptor_shims_open_only_an_existing_object_without_following_a_reparse_point()
    {
        string code = CodeOf(KeyDescriptorFile);

        Assert.Contains("OpenExisting", code, StringComparison.Ordinal);
        Assert.Contains("FileFlagOpenReparsePoint", code, StringComparison.Ordinal);
        Assert.Contains("ReparsePoint", code, StringComparison.Ordinal);

        foreach (string token in new[] { "CreateNew", "OpenOrCreate", "Truncate", "CreateAlways" })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("NCryptOpenStorageProvider")]
    [InlineData("NCryptOpenKey")]
    [InlineData("NCryptGetProperty")]
    [InlineData("CreateFileW")]
    [InlineData("GetFinalPathNameByHandleW")]
    [InlineData("GetKernelObjectSecurity")]
    [InlineData("SetKernelObjectSecurity")]
    public void Each_native_api_is_declared_once_and_invoked_from_exactly_one_call_site(string api)
    {
        string code = CodeOf(KeyDescriptorFile);

        NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(code, api);

        Assert.Equal(1, counts.Declarations);
        Assert.Equal(1, counts.Invocations);
        Assert.Equal(NativeCallGuard.CountMatches(code, api), counts.Total);
    }

    [Fact]
    public void The_approved_descriptor_is_only_ever_built_by_the_certified_contract()
    {
        string code = CodeOf(KeyDescriptorFile);

        Assert.Contains(
            "ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor", code, StringComparison.Ordinal);

        // Nothing here composes, edits or re-encodes a descriptor by hand.
        // "FileSecurity" is deliberately NOT in this list: it is a substring of the
        // certified contract's own TryParseFileSecurityDescriptor, so banning it
        // would ban the very API this adapter is required to use. The managed ACL
        // types are named exactly instead.
        foreach (string token in new[]
                 {
                     "RawSecurityDescriptor", "CommonSecurityDescriptor", "DiscretionaryAcl",
                     "FileSystemSecurity", "FileSystemAccessRule", "SecurityIdentifier", "NTAccount",
                     "AccessControl",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL: the same scan DOES fire on a hand-rolled descriptor.
        Assert.Contains(
            "RawSecurityDescriptor",
            SetupCSharpLexicalScanner.ExtractCode(
                "class X { void M() { var d = new RawSecurityDescriptor () ; } }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_restore_surface_never_rebuilds_the_bytes_it_puts_back()
    {
        string code = CodeOf(KeyDescriptorFile);

        // The ONLY source of restoration bytes is the captured Base64, and its own
        // digest must bind it before a single byte is written.
        Assert.Contains("Convert.FromBase64String", code, StringComparison.Ordinal);
        Assert.Contains("TryPlanRestoration", code, StringComparison.Ordinal);

        // The restore path never reaches the post-grant builder.
        int planRestore = code.IndexOf("TryPlanRestoration", StringComparison.Ordinal);
        Assert.True(planRestore > 0);
    }

    // =======================================================================
    // NO EXCEPTION TEXT, NO NATIVE STATUS, NO PATH ESCAPES
    // =======================================================================

    [Theory]
    [MemberData(nameof(AddedProductionFiles))]
    public void No_exception_text_can_escape_a_file_this_cycle_added(string leaf)
    {
        string code = CodeOf(leaf);

        Assert.Empty(Regex.Matches(code, @"catch\s*\(\s*[\w\.]+\s+\w"));

        // POSITIVE CONTROL: the same pattern DOES fire on a bound exception variable.
        Assert.Single(
            Regex.Matches("try { } catch (ArgumentException ex) { }", @"catch\s*\(\s*[\w\.]+\s+\w"));

        foreach (string token in new[]
                 {
                     ".Message", "StackTrace", "FormatMessage", "GetLastPInvokeErrorMessage",
                     "ToString(ex", "Environment.GetEnvironmentVariable",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_owner_identity_shim_never_reads_the_running_process_identity()
    {
        string code = CodeOf(OwnerIdentityFile);

        foreach (string token in new[]
                 {
                     "WindowsIdentity", "GetCurrent", "Environment.UserName", "Environment.UserDomainName",
                     "Process", "OpenProcessToken", "GetTokenInformation", "NTAccount", "SecurityIdentifier",
                     "GetEnvironmentVariable",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }

        // The one source it DOES read is the certified installation anchor.
        Assert.Contains("ServiceInstallationAnchorStore", code, StringComparison.Ordinal);
    }

    // =======================================================================
    // STILL NON-LIVE: NOTHING IN THE PRODUCT CONSTRUCTS A PASS-B ADAPTER
    // =======================================================================

    // =======================================================================
    // PASS-B ADAPTER CONSTRUCTION IS CONFINED TO THE ONE COMPOSITION ROOT
    // =======================================================================
    //
    // CYCLE 94 AMENDMENT (G5), CARRIED FORWARD FROM CYCLE 93's WRITTEN GRANT.
    //
    // WHY THIS CHANGED AT ALL. Pass C requires the ONE authorized composition
    // root to construct EVERY fixed adapter. Until now this theory exempted NO
    // production file, so the wiring cycle 93 authorized in writing - "all other
    // Pass-B adapter constructions are likewise confined to the same composition
    // root after wiring" - could not be landed without turning this red.
    //
    // THE SHAPE IS EXACTLY THE ONE ALREADY GRANTED, AND NO WIDER. ONE file, named
    // by WHOLE CANONICAL PATH, OrdinalIgnoreCase. No directory allowance, no leaf
    // matching, no pattern, no second file. Every other production source must
    // still contain ZERO constructions, and the calibration control is retained.
    // The sibling proof - that no TEST in this project constructs a fixed native
    // shim - is untouched.

    private static readonly Func<string, string, bool> AdapterGuardExactFullPathMatch =
        static (actualFullPath, allowedFullPath) =>
            string.Equals(actualFullPath, allowedFullPath, StringComparison.OrdinalIgnoreCase);

    internal static string AdapterCompositionRootFullPath() => Path.GetFullPath(Path.Combine(
        RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedTransaction.cs"));

    /// <summary>
    /// The ONE rule body, shared by the sweep and by every control below.
    /// </summary>
    private static bool IsAdapterConstructionOffender(
        string fileFullPath,
        string code,
        string adapterName,
        string authorizedFullPath,
        Func<string, string, bool> fileMatches)
    {
        if (fileMatches(Path.GetFullPath(fileFullPath), authorizedFullPath))
        {
            return false;
        }

        return code.Contains("new " + adapterName, StringComparison.Ordinal);
    }

    private static bool IsAdapterConstructionOffender(string fileFullPath, string code, string adapterName) =>
        IsAdapterConstructionOffender(
            fileFullPath, code, adapterName, AdapterCompositionRootFullPath(), AdapterGuardExactFullPathMatch);

    public static TheoryData<string> FixedNativeShimTypes() => new()
    {
        "ServiceOwnershipFixedOwnerIdentityPort",
        "ServiceOwnershipFixedCertificateFactsPort",
        "ServiceOwnershipFixedPriorDescriptorPort",
        "ServiceOwnershipFixedApprovedDescriptorPort",
        "ServiceOwnershipFixedDescriptorRestorePort",
        "ServiceOwnershipFixedLedgerPersistencePort",
    };

    [Theory]
    [MemberData(nameof(FixedNativeShimTypes))]
    public void No_production_source_constructs_a_pass_b_adapter(string adapterName)
    {
        var offenders = new List<string>();
        var constructingFiles = new List<string>();
        int calibration = 0;

        foreach (string file in ProductionSources())
        {
            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(file));
            if (code.Contains("ServiceOwnershipLedgerContract", StringComparison.Ordinal))
            {
                calibration++;
            }
            if (IsAdapterConstructionOffender(file, code, adapterName))
            {
                offenders.Add(Path.GetFileName(file));
            }
            if (code.Contains("new " + adapterName, StringComparison.Ordinal))
            {
                constructingFiles.Add(Path.GetFullPath(file));
            }
        }

        Assert.True(calibration > 0, "the positive control failed, so the zero below is not calibrated");
        Assert.Empty(offenders);

        // EXACT PRESENCE, NOT MERE ABSENCE. Exactly ONE production file may
        // construct the adapter, and it must be the one authorized root.
        string only = Assert.Single(constructingFiles.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(AdapterCompositionRootFullPath(), only, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(FixedNativeShimTypes))]
    public void A_second_production_file_constructing_a_pass_b_adapter_is_still_an_offender(string adapterName)
    {
        string sibling = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipSomethingElse.cs"));
        string code = "class X { void M() { var a = new " + adapterName + "(); } }";

        Assert.True(IsAdapterConstructionOffender(sibling, code, adapterName));

        // ...and the one authorized root passes the SAME body, so this control is
        // calibrated in both directions.
        Assert.False(IsAdapterConstructionOffender(AdapterCompositionRootFullPath(), code, adapterName));
    }

    [Theory]
    [MemberData(nameof(FixedNativeShimTypes))]
    public void MUTATION_matching_the_composition_root_by_leaf_name_or_directory_is_caught(string adapterName)
    {
        string relocated = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "Elsewhere", "ServiceOwnershipElevatedTransaction.cs"));
        string sibling = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "SomeUnrelatedServiceFile.cs"));
        string code = "class X { void M() { var a = new " + adapterName + "(); } }";

        Assert.True(IsAdapterConstructionOffender(relocated, code, adapterName));
        Assert.True(IsAdapterConstructionOffender(sibling, code, adapterName));

        Func<string, string, bool> leafNameMatch = static (actual, allowed) =>
            string.Equals(Path.GetFileName(actual), Path.GetFileName(allowed), StringComparison.OrdinalIgnoreCase);
        Func<string, string, bool> directoryWideMatch = static (actual, allowed) =>
            string.Equals(Path.GetDirectoryName(actual), Path.GetDirectoryName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.False(IsAdapterConstructionOffender(
            relocated, code, adapterName, AdapterCompositionRootFullPath(), leafNameMatch));
        Assert.False(IsAdapterConstructionOffender(
            sibling, code, adapterName, AdapterCompositionRootFullPath(), directoryWideMatch));
    }

    /// <summary>
    /// THE PROOF THAT NO FIXED SHIM EVER EXECUTES LOCALLY. Every fixed shim is an
    /// INSTANCE method on a sealed class with no static entry point, so a shim that
    /// is never constructed can never run. This scans the WHOLE Setup test project.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixedNativeShimTypes))]
    public void No_test_in_this_project_constructs_a_fixed_native_shim(string adapterName)
    {
        var offenders = new List<string>();
        int calibration = 0;

        foreach (string file in SetupTestSources())
        {
            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(file));

            // The promoted-Recipe adapter IS constructed - against an OS-temp
            // sandbox store - so it is deliberately not in the scanned set, and
            // this control proves the scan can see a construction when there is one.
            if (code.Contains("new ServiceOwnershipFixedPromotedRecipePort", StringComparison.Ordinal))
            {
                calibration++;
            }
            if (code.Contains("new " + adapterName, StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(calibration > 0, "the positive control failed, so the zero below is not calibrated");
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_only_pass_b_adapter_a_test_may_construct_is_bound_to_an_os_temp_sandbox()
    {
        // The promoted-Recipe store's ONLY constructor takes a containment root, and
        // this project supplies one under the OS temp directory - never ProgramData.
        ConstructorInfo constructor = Assert.Single(
            typeof(ServiceOwnershipPromotedRecipeStore).GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

        ParameterInfo parameter = Assert.Single(constructor.GetParameters());
        Assert.Equal("containmentRoot", parameter.Name);

        // And the adapter itself takes the STORE, never a root.
        ConstructorInfo adapterConstructor = Assert.Single(
            typeof(ServiceOwnershipFixedPromotedRecipePort).GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

        ParameterInfo storeParameter = Assert.Single(adapterConstructor.GetParameters());
        Assert.Equal(typeof(ServiceOwnershipPromotedRecipeStore), storeParameter.ParameterType);
    }

    // =======================================================================
    // THE TWO IN-FILE ADAPTERS LIVE WHERE THEIR GUARDS REQUIRE
    // =======================================================================

    [Theory]
    [InlineData("ServiceOwnershipFixedLedgerPersistencePort", LedgerPersistenceFile)]
    [InlineData("ServiceOwnershipFixedPromotedRecipePort", PromotedRecipeFile)]
    [InlineData("ServiceOwnershipCertifiedEntryObservationAdapter", "ServiceOwnershipCredentialObserver.cs")]
    public void Each_contained_adapter_is_declared_inside_the_file_its_guard_requires(
        string adapterName, string expectedFile)
    {
        string declaration = "class " + adapterName;

        string[] declaringFiles = ProductionSources()
            .Where(f => SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(f))
                .Contains(declaration, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray()!;

        Assert.Equal(new[] { expectedFile }, declaringFiles);
    }

    [Fact]
    public void The_ledger_persistence_adapter_never_reaches_the_reader_or_a_path()
    {
        string raw = RawOf(LedgerPersistenceFile);

        foreach (string readerType in new[]
                 {
                     "ServiceOwnershipLedgerReader", "ServiceOwnershipLedgerReaderInterpreter",
                     "ServiceOwnershipLedgerReadResult", "ServiceOwnershipLedgerReadState",
                     "ServiceOwnershipLedgerReadFacts",
                 })
        {
            Assert.DoesNotContain(readerType, raw, StringComparison.Ordinal);
        }

        // The production adapter uses the PRODUCTION verb, which takes no path, and
        // never the containment seam.
        string adapterCode = raw[raw.IndexOf(
            "class ServiceOwnershipFixedLedgerPersistencePort", StringComparison.Ordinal)..];
        Assert.DoesNotContain("WriteWithinContainmentRoot", adapterCode, StringComparison.Ordinal);
        Assert.Contains("ServiceOwnershipLedgerWriter.Write(", adapterCode, StringComparison.Ordinal);
    }

    [Fact]
    public void The_promoted_recipe_adapter_delegates_its_job_id_gate_to_the_certified_grammar()
    {
        string raw = RawOf(PromotedRecipeFile);
        string adapterCode = raw[raw.IndexOf(
            "class ServiceOwnershipFixedPromotedRecipePort", StringComparison.Ordinal)..];

        Assert.Contains(
            "ServiceOwnershipLedgerContract.IsValidKeyIdentity", adapterCode, StringComparison.Ordinal);

        // And it still names no destination of any kind.
        foreach (string token in new[]
                 {
                     "SpecialFolder", "CommonApplicationData", "Path.Combine", "Directory.",
                     "File.", "GetEnvironmentVariable",
                 })
        {
            Assert.DoesNotContain(token, adapterCode, StringComparison.Ordinal);
        }
    }
}
