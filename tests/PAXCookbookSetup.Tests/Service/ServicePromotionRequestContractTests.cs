using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 84 - CLOSED PROMOTION PROTOCOL (part 2)
// ===========================================================================
//
// SCOPE, stated plainly. The request protocol is PURE: it opens no file,
// composes no path, reads no environment variable, touches no certificate
// store, ACL, registry, SCM or ProgramData, and starts no process. Every
// fixture below is SYNTHETIC.
//
// THE STRUCTURAL ASSERTIONS ARE REFLECTION-BASED ON PURPOSE, following the
// cycle-82 precedent in ServiceOwnershipPromotedRecipeStoreTests: they were
// authored RED, before the production type existed. A compile-time reference
// to a type that does not exist yet does not fail one test, it stops the whole
// test assembly compiling, which would have destroyed the RED evidence for
// every other test in this project.
public sealed class ServicePromotionRequestContractRedTests
{
    private const string ParserTypeName =
        "PAXCookbookSetup.Service.ServicePromotionRequestParser";
    private const string PromotionRequestTypeName =
        "PAXCookbookSetup.Service.ServicePromotionRequest";
    private const string DepromotionRequestTypeName =
        "PAXCookbookSetup.Service.ServiceDepromotionRequest";
    private const string ParseResultTypeName =
        "PAXCookbookSetup.Service.ServicePromotionRequestParseResult";
    private const string OutcomeTypeName =
        "PAXCookbookSetup.Service.ServicePromotionRequestOutcome";

    private static Assembly SetupAssembly =>
        typeof(ServiceOwnershipLedgerReaderInterpreter).Assembly;

    [Fact]
    public void A_closed_promotion_request_protocol_exists()
    {
        Assert.NotNull(SetupAssembly.GetType(ParserTypeName, throwOnError: false));
        Assert.NotNull(SetupAssembly.GetType(PromotionRequestTypeName, throwOnError: false));
        Assert.NotNull(SetupAssembly.GetType(DepromotionRequestTypeName, throwOnError: false));
        Assert.NotNull(SetupAssembly.GetType(ParseResultTypeName, throwOnError: false));
        Assert.NotNull(SetupAssembly.GetType(OutcomeTypeName, throwOnError: false));
    }

    [Fact]
    public void There_are_exactly_two_operations_and_no_generic_verb()
    {
        Type? parser = SetupAssembly.GetType(ParserTypeName, throwOnError: false);
        Assert.NotNull(parser);

        string[] declared = parser!
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                        | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => !m.IsPrivate)
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "ParseDepromotion", "ParsePromotion" }, declared);
    }

    [Fact]
    public void No_generic_run_arbitrary_operation_verb_exists_on_the_protocol()
    {
        Type? parser = SetupAssembly.GetType(ParserTypeName, throwOnError: false);
        Assert.NotNull(parser);

        string[] forbidden =
        {
            "Execute", "Run", "Invoke", "Dispatch", "Perform", "Apply", "Do", "Call",
        };

        foreach (MethodInfo method in parser!.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                     | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (string token in forbidden)
            {
                Assert.False(
                    method.Name.Contains(token, StringComparison.OrdinalIgnoreCase),
                    "the closed promotion protocol must expose no generic '" + token + "' verb");
            }
        }
    }

    [Fact]
    public void Neither_parse_verb_accepts_an_authority_bearing_parameter()
    {
        Type? parser = SetupAssembly.GetType(ParserTypeName, throwOnError: false);
        Assert.NotNull(parser);

        string[] forbidden =
        {
            "path", "root", "directory", "file", "store", "sid", "provider",
            "descriptor", "dacl", "command", "executable", "registry", "service",
            "mask", "mechanism", "key",
        };

        foreach (MethodInfo method in parser!.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                     | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName || method.IsPrivate)
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            Assert.Single(parameters);
            Assert.Equal(typeof(string), parameters[0].ParameterType);

            foreach (string token in forbidden)
            {
                Assert.DoesNotContain(token, parameters[0].Name!, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void A_promotion_request_carries_exactly_the_seven_declared_fields()
    {
        Type? request = SetupAssembly.GetType(PromotionRequestTypeName, throwOnError: false);
        Assert.NotNull(request);

        string[] properties = request!
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                           | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "CertificateThumbprintSha1",
                "ExpectedInstallationOwnershipId",
                "ExpectedRecipeSha256",
                "OperationId",
                "PromotedJobId",
                "RecipeBytes",
                "SchemaVersion",
            },
            properties);
    }

    [Fact]
    public void A_depromotion_request_carries_exactly_the_four_declared_fields()
    {
        Type? request = SetupAssembly.GetType(DepromotionRequestTypeName, throwOnError: false);
        Assert.NotNull(request);

        string[] properties = request!
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                           | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ExpectedInstallationOwnershipId",
                "OperationId",
                "PromotedJobId",
                "SchemaVersion",
            },
            properties);
    }

    [Fact]
    public void A_request_can_only_be_produced_by_the_validating_parser()
    {
        foreach (string typeName in new[] { PromotionRequestTypeName, DepromotionRequestTypeName })
        {
            Type? request = SetupAssembly.GetType(typeName, throwOnError: false);
            Assert.NotNull(request);

            ConstructorInfo[] constructors = request!.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotEmpty(constructors);
            foreach (ConstructorInfo constructor in constructors)
            {
                Assert.True(
                    constructor.IsPrivate,
                    typeName + " must expose no non-private constructor");
            }
        }
    }

    [Fact]
    public void The_bounded_outcome_enum_refuses_at_zero()
    {
        Type? outcome = SetupAssembly.GetType(OutcomeTypeName, throwOnError: false);
        Assert.NotNull(outcome);
        Assert.True(outcome!.IsEnum);
        Assert.Equal("Unspecified", Enum.GetName(outcome, 0));
    }
}

// ===========================================================================
// BEHAVIOURAL - the closed protocol actually refuses what it claims to refuse
// ===========================================================================
//
// Every fixture is a SYNTHETIC in-memory string. Nothing in this class opens a
// file, composes a path, touches a certificate store, a private key, an ACL,
// the registry, the SCM or ProgramData, starts a process, or contacts a tenant.
public sealed class ServicePromotionRequestContractBehaviourTests
{
    private const string ValidUlid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string OperationId = "op-0001";
    private const string JobId = "job-0001";
    private const string InstallId = "install-0001";
    private const string Thumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";

    // ---- fixtures ----------------------------------------------------------

    private static string RecipeJson(string authJson) => $$"""
    {
      "recipeId": "{{ValidUlid}}",
      "recipeSchemaVersion": 1,
      "paxAdapterVersion": "1.11.11",
      "identity": { "name": "Promotion fixture" },
      "ingredients": {
        "m365Usage": { "includeM365Usage": false },
        "entraUserData": { "includeUserInfo": false }
      },
      "query": { "mode": "audit", "dateMode": "previous-day" },
      "processing": {},
      "destinations": { "fact": { "mode": "outputPath", "path": "C:\\PAX\\audit.csv" } },
      "auth": {{authJson}}
    }
    """;

    private static string CertificateAuth() => $$"""
    {
      "mode": "AppRegistrationCertificate",
      "tenantId": "{{TenantId}}"
    }
    """;

    private static string ModeAuth(string mode) => $$"""
    {
      "mode": "{{mode}}",
      "tenantId": "{{TenantId}}"
    }
    """;

    private static string OrganizationAuth() => $$"""
    {
      "mode": "AppRegistrationCertificate",
      "tenantId": "{{TenantId}}",
      "organizationKeyId": "contoso.managed_key-1"
    }
    """;

    private static byte[] ValidRecipeBytes() =>
        new UTF8Encoding(false, true).GetBytes(RecipeJson(CertificateAuth()));

    private static string UpperHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string PromotionEnvelope(
        string? schemaVersion = null,
        string? operationId = null,
        string? promotedJobId = null,
        string? thumbprint = null,
        byte[]? recipeBytes = null,
        string? expectedDigest = null,
        string? installationOwnershipId = null,
        string extraProperties = "")
    {
        byte[] bytes = recipeBytes ?? ValidRecipeBytes();
        return "{"
            + "\"schemaVersion\":" + (schemaVersion ?? "1") + ","
            + "\"operationId\":\"" + (operationId ?? OperationId) + "\","
            + "\"promotedJobId\":\"" + (promotedJobId ?? JobId) + "\","
            + "\"certificateThumbprintSha1\":\"" + (thumbprint ?? Thumbprint) + "\","
            + "\"recipeBase64\":\"" + Convert.ToBase64String(bytes) + "\","
            + "\"expectedRecipeSha256\":\"" + (expectedDigest ?? UpperHex(SHA256.HashData(bytes))) + "\","
            + "\"expectedInstallationOwnershipId\":\"" + (installationOwnershipId ?? InstallId) + "\""
            + extraProperties
            + "}";
    }

    // The optional overrides are declared AFTER extraProperties on purpose: every
    // pre-existing call site passes extraProperties positionally.
    private static string DepromotionEnvelope(
        string extraProperties = "",
        string? operationId = null,
        string? promotedJobId = null,
        string? installationOwnershipId = null) =>
        "{"
        + "\"schemaVersion\":1,"
        + "\"operationId\":\"" + (operationId ?? OperationId) + "\","
        + "\"promotedJobId\":\"" + (promotedJobId ?? JobId) + "\","
        + "\"expectedInstallationOwnershipId\":\"" + (installationOwnershipId ?? InstallId) + "\""
        + extraProperties
        + "}";

    // ---- acceptance --------------------------------------------------------

    [Fact]
    public void A_well_formed_promotion_request_is_accepted()
    {
        ServicePromotionRequestParseResult result =
            ServicePromotionRequestParser.ParsePromotion(PromotionEnvelope());

        Assert.Equal(ServicePromotionRequestOutcome.Accepted, result.Outcome);
        Assert.True(result.IsAccepted);
        Assert.NotNull(result.Promotion);
        Assert.Null(result.Depromotion);

        ServicePromotionRequest request = result.Promotion!;
        Assert.Equal(1, request.SchemaVersion);
        Assert.Equal(OperationId, request.OperationId);
        Assert.Equal(JobId, request.PromotedJobId);
        Assert.Equal(Thumbprint, request.CertificateThumbprintSha1);
        Assert.Equal(InstallId, request.ExpectedInstallationOwnershipId);
        Assert.Equal(UpperHex(SHA256.HashData(ValidRecipeBytes())), request.ExpectedRecipeSha256);
        Assert.Equal(ValidRecipeBytes(), request.RecipeBytes);
    }

    [Fact]
    public void A_well_formed_depromotion_request_is_accepted()
    {
        ServicePromotionRequestParseResult result =
            ServicePromotionRequestParser.ParseDepromotion(DepromotionEnvelope());

        Assert.Equal(ServicePromotionRequestOutcome.Accepted, result.Outcome);
        Assert.NotNull(result.Depromotion);
        Assert.Null(result.Promotion);
        Assert.Equal(JobId, result.Depromotion!.PromotedJobId);
    }

    [Fact]
    public void The_accepted_recipe_bytes_are_a_defensive_copy()
    {
        ServicePromotionRequest request =
            ServicePromotionRequestParser.ParsePromotion(PromotionEnvelope()).Promotion!;

        byte[] first = request.RecipeBytes;
        first[0] = (byte)'X';

        Assert.NotEqual(first, request.RecipeBytes);
        Assert.Equal(ValidRecipeBytes(), request.RecipeBytes);
    }

    [Theory]
    [InlineData("abcdef0123456789abcdef0123456789abcdef01")]
    [InlineData("AB CD EF 01 23 45 67 89 AB CD EF 01 23 45 67 89 AB CD EF 01")]
    // A JSON tab ESCAPE, not a raw tab: a raw control character inside a JSON
    // string is invalid JSON and is refused earlier, as its own test proves.
    [InlineData("\\tABCDEF0123456789abcdef0123456789ABCDEF01")]
    public void A_spaced_or_lowercase_thumbprint_normalizes_to_the_one_accepted_spelling(string supplied)
    {
        ServicePromotionRequestParseResult result =
            ServicePromotionRequestParser.ParsePromotion(PromotionEnvelope(thumbprint: supplied));

        Assert.Equal(ServicePromotionRequestOutcome.Accepted, result.Outcome);
        Assert.Equal(Thumbprint, result.Promotion!.CertificateThumbprintSha1);
    }

    // ---- envelope refusals -------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("{\"unterminated\":")]
    public void A_malformed_request_is_refused(string? requestJson)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedRequest,
            ServicePromotionRequestParser.ParsePromotion(requestJson).Outcome);
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedRequest,
            ServicePromotionRequestParser.ParseDepromotion(requestJson).Outcome);
    }

    [Fact]
    public void An_oversized_request_is_refused_before_it_is_parsed()
    {
        string oversized = new('x', ServicePromotionRequestParser.MaxRequestChars + 1);

        Assert.Equal(
            ServicePromotionRequestOutcome.OversizedRequest,
            ServicePromotionRequestParser.ParsePromotion(oversized).Outcome);
    }

    [Fact]
    public void An_unknown_property_is_refused()
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.UnknownProperty,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(extraProperties: ",\"note\":\"hello\"")).Outcome);
    }

    [Fact]
    public void A_duplicate_property_is_refused()
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.DuplicateProperty,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(extraProperties: ",\"promotedJobId\":\"job-0002\"")).Outcome);
    }

    [Fact]
    public void A_missing_property_is_refused()
    {
        string missing = "{"
            + "\"schemaVersion\":1,"
            + "\"operationId\":\"" + OperationId + "\","
            + "\"promotedJobId\":\"" + JobId + "\""
            + "}";

        Assert.Equal(
            ServicePromotionRequestOutcome.MissingProperty,
            ServicePromotionRequestParser.ParseDepromotion(missing).Outcome);
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    public void A_schema_version_of_the_wrong_type_is_refused(string schemaVersion)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.WrongPropertyType,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(schemaVersion: schemaVersion)).Outcome);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("-1")]
    public void An_unsupported_schema_version_is_refused(string schemaVersion)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.UnsupportedSchemaVersion,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(schemaVersion: schemaVersion)).Outcome);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("certificatePath")]
    [InlineData("storeLocation")]
    [InlineData("storeName")]
    [InlineData("serviceName")]
    [InlineData("serviceSid")]
    [InlineData("owningUserSid")]
    [InlineData("providerName")]
    [InlineData("keyStorageRoot")]
    [InlineData("machineRoot")]
    [InlineData("containmentRoot")]
    [InlineData("ledgerDirectory")]
    [InlineData("priorDaclBytesBase64")]
    [InlineData("securityDescriptor")]
    [InlineData("grantedRightsMask")]
    [InlineData("grantMechanism")]
    [InlineData("command")]
    [InlineData("executablePath")]
    [InlineData("registryKey")]
    [InlineData("clientSecret")]
    [InlineData("accessToken")]
    public void An_authority_bearing_field_is_refused_with_its_own_outcome(string propertyName)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.AuthorityBearingFieldPresent,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(extraProperties: ",\"" + propertyName + "\":\"x\"")).Outcome);

        Assert.Equal(
            ServicePromotionRequestOutcome.AuthorityBearingFieldPresent,
            ServicePromotionRequestParser.ParseDepromotion(
                DepromotionEnvelope(",\"" + propertyName + "\":\"x\"")).Outcome);
    }

    [Fact]
    public void A_depromotion_request_may_not_smuggle_a_recipe()
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.UnknownProperty,
            ServicePromotionRequestParser.ParseDepromotion(
                DepromotionEnvelope(",\"recipeBase64\":\"e30=\"")).Outcome);
    }

    // ---- bounded token refusals -------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has\\\\backslash")]
    // The ledger's own bounded grammar admits '.', so "." and ".." ARE legal
    // ledger tokens. The promotion protocol narrows them out, because an
    // identifier that spells a relative path must never enter a protocol whose
    // downstream surfaces compose file names from it.
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    public void A_malformed_operation_id_is_refused(string operationId)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedOperationId,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(operationId: operationId)).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData(".")]
    [InlineData("..")]
    public void A_malformed_promoted_job_id_is_refused(string jobId)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedPromotedJobId,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(promotedJobId: jobId)).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    public void A_malformed_installation_ownership_id_is_refused(string installId)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedInstallationOwnershipId,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(installationOwnershipId: installId)).Outcome);
    }

    // =======================================================================
    // CYCLE 86 (F1) - promotedJobId CARRIES THE LEDGER KEY-IDENTITY GRAMMAR
    // =======================================================================
    //
    // WHY ONLY THIS FIELD. promotedJobId is the ONLY request field any downstream
    // surface turns into a file name: ServiceOwnershipPromotedRecipeStore composes
    // storeDirectory + promotedJobId + ".json", stages under the same id, deletes
    // by the same id, and parses the leaf back by stripping the extension.
    // operationId and expectedInstallationOwnershipId never name anything, so they
    // keep the protocol's own bounded-token rule unchanged.
    //
    // CYCLE 84's DEFECT, NOW CLOSED. The parser's local predicate rejected only an
    // ALL-DOT token while its comment claimed parity with IsValidKeyIdentity, which
    // is strictly stronger. ".hidden", "..a" and "a..b" were therefore accepted by
    // a protocol whose comment promised they were not.
    //
    // WHY IsValidKeyIdentity AND NOT IsValidProviderUniqueName. The stricter
    // grammar additionally refuses a TRAILING dot/space and the reserved device
    // names. Neither hazard materialises at this boundary, because the ".json"
    // extension is ALWAYS appended: the composed leaf can never be a bare device
    // name and can never end in a dot. That negative result is proven, not assumed,
    // in ServiceOwnershipPromotedRecipeStoreTests.

    // The literal predicate cycle 84 shipped, reproduced here so each traversal
    // fixture can be shown to be refused BECAUSE of the key-identity rule rather
    // than by something the parser was already doing.
    private static bool Cycle84LocalBoundedToken(string? value)
    {
        if (!ServiceOwnershipLedgerContract.IsValidBoundedToken(
                value, ServiceOwnershipLedgerContract.MaxStringLength))
        {
            return false;
        }

        foreach (char c in value!)
        {
            if (c != '.')
            {
                return true;
            }
        }

        return false;
    }

    // Refused by BOTH grammars, so these prove nothing about the new rule on their
    // own - they are here because the protocol must keep refusing them.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("job 0001")]
    [InlineData("/")]
    [InlineData("job/0001")]
    // C# "\\\\" is TWO backslashes in the source text, which JSON decodes to ONE.
    // A single source backslash would make the envelope itself malformed and the
    // test would then be asserting the wrong refusal.
    [InlineData("\\\\")]
    [InlineData("job\\\\0001")]
    [InlineData("job:0001")]
    [InlineData("job*0001")]
    [InlineData("job?0001")]
    [InlineData("job|0001")]
    [InlineData("job<0001")]
    [InlineData("job>0001")]
    [InlineData("job%0001")]
    [InlineData("job$0001")]
    [InlineData("job~0001")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    public void A_promoted_job_id_outside_the_permitted_charset_is_refused_by_both_verbs(string jobId)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedPromotedJobId,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(promotedJobId: jobId)).Outcome);

        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedPromotedJobId,
            ServicePromotionRequestParser.ParseDepromotion(
                DepromotionEnvelope(promotedJobId: jobId)).Outcome);
    }

    // THE LOAD-BEARING SET. Every one of these passed cycle 84's predicate and is
    // refused only because promotedJobId now delegates to IsValidKeyIdentity.
    public static TheoryData<string> KeyIdentityOnlyRefusedJobIds() => new()
    {
        ".hidden",
        "..a",
        "a..b",
        ".a",
        ".-",
        "._x",
        ".0001",
        "a..b..c",
        "job..0001",
        "..job-0001",
        "a..",
    };

    [Theory]
    [MemberData(nameof(KeyIdentityOnlyRefusedJobIds))]
    public void A_traversal_shaped_promoted_job_id_is_refused_by_both_verbs(string jobId)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedPromotedJobId,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(promotedJobId: jobId)).Outcome);

        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedPromotedJobId,
            ServicePromotionRequestParser.ParseDepromotion(
                DepromotionEnvelope(promotedJobId: jobId)).Outcome);
    }

    [Theory]
    [MemberData(nameof(KeyIdentityOnlyRefusedJobIds))]
    public void DIRECTIONAL_removing_the_key_identity_check_makes_every_traversal_fixture_pass(string jobId)
    {
        // Without the key-identity rule the fixture is ACCEPTABLE...
        Assert.True(
            Cycle84LocalBoundedToken(jobId),
            "fixture " + jobId + " is refused even without the key-identity rule, so it proves nothing");

        // ...and with it, it is not. The check is therefore load-bearing for EVERY
        // fixture in this set, not merely for the set as a whole.
        Assert.False(ServiceOwnershipLedgerContract.IsValidKeyIdentity(jobId));
    }

    [Fact]
    public void An_over_length_promoted_job_id_is_refused_by_both_verbs()
    {
        string atBound = new('a', ServiceOwnershipLedgerContract.MaxKeyIdentityLength);
        string overBound = new('a', ServiceOwnershipLedgerContract.MaxKeyIdentityLength + 1);

        Assert.True(ServicePromotionRequestParser.ParsePromotion(
            PromotionEnvelope(promotedJobId: atBound)).IsAccepted);
        Assert.True(ServicePromotionRequestParser.ParseDepromotion(
            DepromotionEnvelope(promotedJobId: atBound)).IsAccepted);

        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedPromotedJobId,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(promotedJobId: overBound)).Outcome);
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedPromotedJobId,
            ServicePromotionRequestParser.ParseDepromotion(
                DepromotionEnvelope(promotedJobId: overBound)).Outcome);
    }

    // THE POSITIVE DIRECTION. Tightening a grammar is only correct if it did not
    // also refuse the shapes the product actually uses.
    public static TheoryData<string> AcceptedJobIds() => new()
    {
        "job-0001",
        "job_0001",
        "job.0001",
        "J0b.-_9",
        "a",
        "0",
        "job.0001.v2",
        "job-0001_retry.2",
        // A SAFE DOTTED ID: a single trailing dot is legal key-identity spelling.
        // It is retained deliberately, because the store always appends ".json", so
        // the composed leaf can never end in a dot.
        "job.",
        // RESERVED DEVICE NAMES are accepted for the same reason: the leaf is never
        // bare. The composition proof lives with the store, where it can be run.
        "CON",
        "COM1",
    };

    [Theory]
    [MemberData(nameof(AcceptedJobIds))]
    public void A_permitted_promoted_job_id_is_accepted_by_both_verbs(string jobId)
    {
        Assert.True(ServiceOwnershipLedgerContract.IsValidKeyIdentity(jobId));

        ServicePromotionRequestParseResult promotion =
            ServicePromotionRequestParser.ParsePromotion(PromotionEnvelope(promotedJobId: jobId));
        Assert.True(promotion.IsAccepted);
        Assert.Equal(jobId, promotion.Promotion!.PromotedJobId);

        ServicePromotionRequestParseResult depromotion =
            ServicePromotionRequestParser.ParseDepromotion(DepromotionEnvelope(promotedJobId: jobId));
        Assert.True(depromotion.IsAccepted);
        Assert.Equal(jobId, depromotion.Depromotion!.PromotedJobId);
    }

    // ---- the OTHER two identifiers did not change, in either direction -----

    [Theory]
    [MemberData(nameof(KeyIdentityOnlyRefusedJobIds))]
    public void UNCHANGED_the_operation_id_still_accepts_every_key_identity_only_shape(string token)
    {
        // The narrowing is scoped to promotedJobId. operationId names nothing, so
        // its grammar is deliberately untouched - asserted, not assumed.
        Assert.True(ServicePromotionRequestParser.ParsePromotion(
            PromotionEnvelope(operationId: token)).IsAccepted);
        Assert.True(ServicePromotionRequestParser.ParseDepromotion(
            DepromotionEnvelope(operationId: token)).IsAccepted);
    }

    [Theory]
    [MemberData(nameof(KeyIdentityOnlyRefusedJobIds))]
    public void UNCHANGED_the_installation_ownership_id_still_accepts_every_key_identity_only_shape(string token)
    {
        Assert.True(ServicePromotionRequestParser.ParsePromotion(
            PromotionEnvelope(installationOwnershipId: token)).IsAccepted);
        Assert.True(ServicePromotionRequestParser.ParseDepromotion(
            DepromotionEnvelope(installationOwnershipId: token)).IsAccepted);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("")]
    [InlineData("has space")]
    public void UNCHANGED_the_other_two_identifiers_still_refuse_what_they_always_refused(string token)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedOperationId,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(operationId: token)).Outcome);
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedOperationId,
            ServicePromotionRequestParser.ParseDepromotion(
                DepromotionEnvelope(operationId: token)).Outcome);

        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedInstallationOwnershipId,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(installationOwnershipId: token)).Outcome);
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedInstallationOwnershipId,
            ServicePromotionRequestParser.ParseDepromotion(
                DepromotionEnvelope(installationOwnershipId: token)).Outcome);
    }

    [Fact]
    public void The_promoted_job_id_rule_is_DELEGATED_to_the_contract_and_not_retyped()
    {
        // COMMENT-STRIPPED. Cycle 84's whole defect was a COMMENT that claimed an
        // invariant the CODE did not hold, so this must never be satisfiable by a
        // comment or a string literal.
        string code = SetupCSharpLexicalScanner.ExtractCode(ContractSource());

        // POSITIVE CONTROL: the scanner really saw this file's code.
        Assert.Contains("class ServicePromotionRequestParser", code, StringComparison.Ordinal);

        // Exactly ONE delegation call site, and the rules are not re-implemented.
        Assert.Equal(1, Occurrences(code, "ServiceOwnershipLedgerContract.IsValidKeyIdentity("));
        Assert.DoesNotContain("IsValidProviderUniqueName", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ReservedDeviceName", code, StringComparison.Ordinal);

        // ...and the OTHER two identifiers still go through the protocol's own
        // unchanged bounded-token predicate.
        Assert.Contains("IsBoundedToken(", code, StringComparison.Ordinal);

        // NEGATIVE CONTROL for the stripping itself: a comment mentioning the
        // predicate does NOT count as a call site.
        Assert.DoesNotContain(
            "ServiceOwnershipLedgerContract.IsValidKeyIdentity",
            SetupCSharpLexicalScanner.ExtractCode(
                "// ServiceOwnershipLedgerContract.IsValidKeyIdentity(v)\nclass X { }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_promotion_contract_gained_no_filesystem_capability()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(ContractSource());

        foreach (string token in new[]
                 {
                     "System.IO", "File.", "Directory.", "Path.Combine", "Path.GetFullPath",
                     "FileStream", "SpecialFolder", "GetEnvironmentVariable", "RegistryKey",
                     "Microsoft.Win32", "X509Store", "ServiceController", "Process.Start", "HttpClient",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);

            // POSITIVE CONTROL, per token: the scan can actually find this token.
            Assert.Contains(
                token,
                SetupCSharpLexicalScanner.ExtractCode("class X { void M() { var y = " + token + "z; } }"),
                StringComparison.Ordinal);
        }
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string ContractSource() =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServicePromotionRequestContract.cs"));

    private static string RepoRoot()
    {
        System.IO.DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null
               && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PAXCookbook.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF012")]
    [InlineData("GHIJKL0123456789ABCDEF0123456789ABCDEF01")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF-1")]
    public void A_malformed_thumbprint_is_refused(string thumbprint)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedCertificateThumbprint,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(thumbprint: thumbprint)).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("ABCDEF")]
    [InlineData("ZZCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public void A_malformed_expected_recipe_digest_is_refused(string digest)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedExpectedRecipeSha256,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(expectedDigest: digest)).Outcome);
    }

    // ---- recipe refusals ---------------------------------------------------

    [Fact]
    public void A_recipe_that_does_not_match_the_declared_digest_is_refused()
    {
        string wrongDigest = UpperHex(SHA256.HashData(new byte[] { 1, 2, 3 }));

        Assert.Equal(
            ServicePromotionRequestOutcome.RecipeDigestMismatch,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(expectedDigest: wrongDigest)).Outcome);
    }

    [Fact]
    public void A_raw_control_character_in_the_request_is_refused_as_malformed_json()
    {
        string envelope = PromotionEnvelope(thumbprint: "\tABCDEF0123456789ABCDEF0123456789ABCDEF01");

        Assert.Equal(
            ServicePromotionRequestOutcome.MalformedRequest,
            ServicePromotionRequestParser.ParsePromotion(envelope).Outcome);
    }

    [Fact]
    public void An_undecodable_recipe_payload_is_refused()
    {
        string envelope = PromotionEnvelope().Replace(
            "\"recipeBase64\":\"", "\"recipeBase64\":\"!!!not-base64!!!", StringComparison.Ordinal);

        Assert.Equal(
            ServicePromotionRequestOutcome.OversizedOrUndecodableRecipe,
            ServicePromotionRequestParser.ParsePromotion(envelope).Outcome);
    }

    [Fact]
    public void An_oversized_recipe_is_refused()
    {
        var oversized = new byte[ServiceOwnershipPromotedRecipeContent.MaxRecipeBytes + 1];
        Array.Fill(oversized, (byte)'a');

        Assert.Equal(
            ServicePromotionRequestOutcome.OversizedOrUndecodableRecipe,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(recipeBytes: oversized)).Outcome);
    }

    [Fact]
    public void A_recipe_carrying_a_byte_order_mark_is_refused()
    {
        byte[] valid = ValidRecipeBytes();
        var withBom = new byte[valid.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        Buffer.BlockCopy(valid, 0, withBom, 3, valid.Length);

        Assert.Equal(
            ServicePromotionRequestOutcome.RecipeNotUtf8,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(recipeBytes: withBom)).Outcome);
    }

    [Fact]
    public void A_recipe_that_is_not_strict_utf8_is_refused()
    {
        byte[] invalid = { 0x7B, 0xFF, 0xFE, 0x7D };

        Assert.Equal(
            ServicePromotionRequestOutcome.RecipeNotUtf8,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(recipeBytes: invalid)).Outcome);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void A_recipe_that_is_not_a_json_object_is_refused(string recipeText)
    {
        Assert.Equal(
            ServicePromotionRequestOutcome.RecipeNotJsonObject,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(recipeBytes: Encoding.UTF8.GetBytes(recipeText))).Outcome);
    }

    [Fact]
    public void A_semantically_invalid_recipe_is_refused_by_the_canonical_validator()
    {
        // A certificate Recipe with no tenantId fails AppRegistrationTenantGate.
        byte[] bytes = Encoding.UTF8.GetBytes(
            RecipeJson("{ \"mode\": \"AppRegistrationCertificate\" }"));

        Assert.Equal(
            ServicePromotionRequestOutcome.RecipeSemanticallyInvalid,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(recipeBytes: bytes)).Outcome);
    }

    [Theory]
    [InlineData("WebLogin")]
    [InlineData("DeviceCode")]
    [InlineData("ManagedIdentity")]
    [InlineData("AppRegistrationSecret")]
    public void A_recipe_that_is_not_certificate_only_app_registration_is_refused(string mode)
    {
        // ManagedIdentity is not valid for the default local-manual execution mode,
        // so it is refused by the canonical validator first; the other three reach
        // the certificate-only gate. Either way the request never succeeds.
        ServicePromotionRequestOutcome outcome =
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(recipeBytes: Encoding.UTF8.GetBytes(RecipeJson(ModeAuth(mode)))))
            .Outcome;

        Assert.NotEqual(ServicePromotionRequestOutcome.Accepted, outcome);
        Assert.True(
            outcome == ServicePromotionRequestOutcome.RecipeAuthModeNotAppRegistrationCertificate
            || outcome == ServicePromotionRequestOutcome.RecipeSemanticallyInvalid,
            "unexpected refusal outcome " + outcome);
    }

    [Fact]
    public void A_recipe_with_a_case_variant_auth_mode_is_refused_rather_than_normalized()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(RecipeJson(ModeAuth("appregistrationcertificate")));

        Assert.NotEqual(
            ServicePromotionRequestOutcome.Accepted,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(recipeBytes: bytes)).Outcome);
    }

    [Fact]
    public void A_recipe_carrying_an_organization_key_binding_is_refused()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(RecipeJson(OrganizationAuth()));

        Assert.Equal(
            ServicePromotionRequestOutcome.RecipeCarriesProhibitedBinding,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(recipeBytes: bytes)).Outcome);
    }

    [Fact]
    public void A_recipe_carrying_inline_secret_shaped_arguments_is_refused()
    {
        string withSecret = RecipeJson(CertificateAuth())
            .Replace(
                "\"processing\": {}",
                "\"processing\": {}, \"advanced\": { \"extraArguments\": \"-ClientSecret hunter2\" }",
                StringComparison.Ordinal);

        Assert.Equal(
            ServicePromotionRequestOutcome.RecipeSemanticallyInvalid,
            ServicePromotionRequestParser.ParsePromotion(
                PromotionEnvelope(recipeBytes: Encoding.UTF8.GetBytes(withSecret))).Outcome);
    }

    // ---- calibration -------------------------------------------------------

    [Fact]
    public void The_positive_control_fixture_really_is_acceptable()
    {
        // CALIBRATION. Every refusal test above mutates exactly one thing on this
        // fixture. If the fixture itself were unacceptable, all of them would pass
        // vacuously. This test is what makes the whole class non-vacuous.
        Assert.True(ServicePromotionRequestParser.ParsePromotion(PromotionEnvelope()).IsAccepted);
        Assert.True(ServicePromotionRequestParser.ParseDepromotion(DepromotionEnvelope()).IsAccepted);
    }

    [Fact]
    public void No_refusal_ever_carries_a_request()
    {
        string[] refusedInputs =
        {
            "not json",
            PromotionEnvelope(schemaVersion: "2"),
            PromotionEnvelope(thumbprint: "nope"),
            PromotionEnvelope(extraProperties: ",\"storePath\":\"C:\\\\x\""),
        };

        foreach (string input in refusedInputs)
        {
            ServicePromotionRequestParseResult result =
                ServicePromotionRequestParser.ParsePromotion(input);

            Assert.False(result.IsAccepted);
            Assert.Null(result.Promotion);
            Assert.Null(result.Depromotion);
            Assert.Equal(result.Outcome.ToString(), result.ToString());
        }
    }
}
