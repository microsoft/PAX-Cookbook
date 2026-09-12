using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 94 (PASS C) - THE COMPOSITION ROOT AND THE CLOSED ELEVATED PROTOCOL
// ===========================================================================
//
// SCOPE, STATED PLAINLY AND HONESTLY.
//
// WHAT THIS FILE EXERCISES. The two phases that are provably free of machine
// effect: the CLOSED elevated protocol parse, and the composition root's
// mutation-free payload ACCEPTANCE phase, plus the ORDERING refusals that
// short-circuit BEFORE any effect can be reached. Every fixture is a synthetic
// in-memory string.
//
// WHAT THIS FILE DELIBERATELY DOES NOT DO, AND WHY.
//   * It never reaches a successful Preflight. Preflight calls the real fixed
//     ledger reader, which has NO seam to redirect it away from the real fixed
//     %ProgramData% path - the exact reason the cycle-51 reader tests never call
//     Read() either. This file only exercises the refusals that return BEFORE
//     that call, and it PROVES they returned before it by asserting the root's
//     own read counter is still zero.
//   * It never reaches Apply's construction phase. Apply constructs the fixed
//     native adapters, which open a machine certificate store, reopen a machine
//     private key and read or write a real security descriptor. None of that may
//     happen on a development machine. This file only exercises Apply's ordering
//     refusal, which returns before the store root is even derived.
//   * It constructs NO fixed native shim. A sibling guard proves that across the
//     whole test project, and this file must not be the first exception.
//
// ORDER OF AUTHORING - DISCLOSED, NOT BURIED. The four guard transformations and
// the structural Pass-C proofs in
// ServiceOwnershipElevatedTransactionStructuralTests were written BEFORE any
// production code and were captured failing RED against an absent composition
// root. THIS file was written AFTER the composition root existed, because a
// compile-time reference to a type that does not exist does not fail an
// assertion - it stops the whole test assembly compiling, which would have
// destroyed the RED evidence for every other test in this project.
public sealed class ServiceOwnershipElevatedProtocolTests
{
    private const string Endpoint = "PAXCookbook.InitiatingUserIdentity.0123456789ABCDEF0123456789ABCDEF";

    private static string[] Argv(string verb) =>
        new[]
        {
            verb,
            ServiceEnableVerbs.EndpointOption, Endpoint,
            ServiceEnableVerbs.InitiatorProcessIdOption, "4321",
            ServiceEnableVerbs.InitiatorCreatedOption, "133700000000000000",
        };

    [Fact]
    public void There_are_exactly_two_elevated_ownership_verbs_and_they_are_distinct()
    {
        Assert.Equal("ownership-promote-elevated", ServiceOwnershipElevatedVerbs.PromoteElevatedVerb);
        Assert.Equal("ownership-depromote-elevated", ServiceOwnershipElevatedVerbs.DepromoteElevatedVerb);
        Assert.NotEqual(
            ServiceOwnershipElevatedVerbs.PromoteElevatedVerb,
            ServiceOwnershipElevatedVerbs.DepromoteElevatedVerb,
            StringComparer.OrdinalIgnoreCase);

        // The closed operation vocabulary is exactly three members, and zero is
        // the safe default.
        ServiceOwnershipElevatedOperation[] declared =
            Enum.GetValues<ServiceOwnershipElevatedOperation>();
        Assert.Equal(3, declared.Length);
        Assert.Equal(ServiceOwnershipElevatedOperation.Unspecified, default);
        Assert.Equal(0, (int)ServiceOwnershipElevatedOperation.Unspecified);
    }

    // THE THEORY PARAMETER IS THE OPERATION'S NAME, NEVER THE OPERATION ITSELF.
    // xunit's public theory surface cannot expose a less-accessible type, and the
    // closed operation enum is internal to Setup, so the name is carried as a
    // plain string and mapped INSIDE the method body - the same convention the
    // cycle-51 reader theories already use for their bounded fact names.
    private static ServiceOwnershipElevatedOperation OperationByName(string name) => name switch
    {
        "Promote" => ServiceOwnershipElevatedOperation.Promote,
        "Depromote" => ServiceOwnershipElevatedOperation.Depromote,
        "Unspecified" => ServiceOwnershipElevatedOperation.Unspecified,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown operation name"),
    };

    [Theory]
    [InlineData("ownership-promote-elevated", "Promote")]
    [InlineData("OWNERSHIP-PROMOTE-ELEVATED", "Promote")]
    [InlineData("ownership-depromote-elevated", "Depromote")]
    [InlineData("OwNeRsHiP-DePrOmOtE-eLeVaTeD", "Depromote")]
    public void The_verb_alone_selects_the_operation(string verb, string expected)
    {
        Assert.Equal(OperationByName(expected), ServiceOwnershipElevatedVerbs.OperationFor(verb));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ownership-promote")]
    [InlineData("ownership-promote-elevated ")]
    [InlineData("ownership-elevate")]
    [InlineData("service-enable-elevated")]
    [InlineData("service-disable-elevated")]
    public void Anything_that_is_not_one_of_the_two_exact_verbs_maps_to_unspecified(string? verb)
    {
        Assert.Equal(
            ServiceOwnershipElevatedOperation.Unspecified,
            ServiceOwnershipElevatedVerbs.OperationFor(verb));
    }

    [Theory]
    [InlineData("ownership-promote-elevated", "Promote")]
    [InlineData("ownership-depromote-elevated", "Depromote")]
    public void A_well_formed_elevated_vector_parses_for_its_own_operation(
        string verb, string operationName)
    {
        Assert.True(ServiceOwnershipElevatedProtocol.TryParseElevated(
            Argv(verb), OperationByName(operationName), out ServiceEnableElevatedArguments? parsed));

        Assert.NotNull(parsed);
        Assert.Equal(Endpoint, parsed!.EndpointName);
        Assert.Equal(4321u, parsed.Initiator.ProcessId);
        Assert.Equal(133700000000000000L, parsed.Initiator.CreationFileTime);
    }

    [Theory]
    [InlineData("ownership-promote-elevated", "Depromote")]
    [InlineData("ownership-depromote-elevated", "Promote")]
    public void A_vector_for_the_other_operation_is_refused(
        string verb, string dispatchedOperationName)
    {
        // A promote dispatch can never be reached with a depromote vector, or the
        // reverse: the two facts must agree or the parse fails.
        Assert.False(ServiceOwnershipElevatedProtocol.TryParseElevated(
            Argv(verb), OperationByName(dispatchedOperationName), out ServiceEnableElevatedArguments? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void An_unspecified_operation_can_never_parse_anything()
    {
        Assert.False(ServiceOwnershipElevatedProtocol.TryParseElevated(
            Argv(ServiceOwnershipElevatedVerbs.PromoteElevatedVerb),
            ServiceOwnershipElevatedOperation.Unspecified,
            out ServiceEnableElevatedArguments? parsed));
        Assert.Null(parsed);
    }

    public static TheoryData<string[]?> MalformedVectors() => new()
    {
        null,
        Array.Empty<string>(),
        new[] { "ownership-promote-elevated" },
        new[] { "ownership-promote-elevated", ServiceEnableVerbs.EndpointOption, Endpoint },
        // wrong option order
        new[]
        {
            "ownership-promote-elevated",
            ServiceEnableVerbs.InitiatorProcessIdOption, "4321",
            ServiceEnableVerbs.EndpointOption, Endpoint,
            ServiceEnableVerbs.InitiatorCreatedOption, "133700000000000000",
        },
        // a signed / hex / padded process id
        new[]
        {
            "ownership-promote-elevated",
            ServiceEnableVerbs.EndpointOption, Endpoint,
            ServiceEnableVerbs.InitiatorProcessIdOption, "+4321",
            ServiceEnableVerbs.InitiatorCreatedOption, "133700000000000000",
        },
        new[]
        {
            "ownership-promote-elevated",
            ServiceEnableVerbs.EndpointOption, Endpoint,
            ServiceEnableVerbs.InitiatorProcessIdOption, "0x10",
            ServiceEnableVerbs.InitiatorCreatedOption, "133700000000000000",
        },
        // process id zero
        new[]
        {
            "ownership-promote-elevated",
            ServiceEnableVerbs.EndpointOption, Endpoint,
            ServiceEnableVerbs.InitiatorProcessIdOption, "0",
            ServiceEnableVerbs.InitiatorCreatedOption, "133700000000000000",
        },
        // non-positive creation FILETIME
        new[]
        {
            "ownership-promote-elevated",
            ServiceEnableVerbs.EndpointOption, Endpoint,
            ServiceEnableVerbs.InitiatorProcessIdOption, "4321",
            ServiceEnableVerbs.InitiatorCreatedOption, "0",
        },
        // a non-canonical endpoint name
        new[]
        {
            "ownership-promote-elevated",
            ServiceEnableVerbs.EndpointOption, "PAXCookbook.InitiatingUserIdentity.notHex",
            ServiceEnableVerbs.InitiatorProcessIdOption, "4321",
            ServiceEnableVerbs.InitiatorCreatedOption, "133700000000000000",
        },
        // a token that would carry authority
        new[]
        {
            "ownership-promote-elevated",
            ServiceEnableVerbs.EndpointOption, Endpoint,
            ServiceEnableVerbs.InitiatorProcessIdOption, "4321",
            ServiceEnableVerbs.InitiatorCreatedOption, "133700000000000000",
            "C:\\ProgramData\\PAXCookbook",
        },
    };

    [Theory]
    [MemberData(nameof(MalformedVectors))]
    public void Every_malformed_vector_is_refused(string[]? argv)
    {
        Assert.False(ServiceOwnershipElevatedProtocol.TryParseElevated(
            argv, ServiceOwnershipElevatedOperation.Promote, out ServiceEnableElevatedArguments? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void The_elevated_token_count_is_the_same_seven_the_other_two_verbs_use()
    {
        Assert.Equal(7, ServiceOwnershipElevatedVerbs.ElevatedTokenCount);
        Assert.Equal(ServiceEnableVerbs.ElevatedTokenCount, ServiceOwnershipElevatedVerbs.ElevatedTokenCount);
        Assert.Equal(ServiceDisableVerbs.ElevatedTokenCount, ServiceOwnershipElevatedVerbs.ElevatedTokenCount);
    }
}

/// <summary>
/// The composition root's MUTATION-FREE phases and its ordering refusals.
/// </summary>
public sealed class ServiceOwnershipElevatedTransactionBehaviourTests
{
    private const string ValidUlid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string OperationId = "op-c94-0001";
    private const string JobId = "job-c94-0001";
    private const string InstallId = "install-c94-0001";
    private const string Thumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";

    // ---- synthetic in-memory fixtures --------------------------------------

    private static string RecipeJson() => $$"""
    {
      "recipeId": "{{ValidUlid}}",
      "recipeSchemaVersion": 1,
      "paxAdapterVersion": "1.11.11",
      "identity": { "name": "Cycle 94 pass C fixture" },
      "ingredients": {
        "m365Usage": { "includeM365Usage": false },
        "entraUserData": { "includeUserInfo": false }
      },
      "query": { "mode": "audit", "dateMode": "previous-day" },
      "processing": {},
      "destinations": { "fact": { "mode": "outputPath", "path": "C:\\PAX\\audit.csv" } },
      "auth": { "mode": "AppRegistrationCertificate", "tenantId": "{{TenantId}}" }
    }
    """;

    private static byte[] RecipeBytes() => new UTF8Encoding(false, true).GetBytes(RecipeJson());

    private static string UpperHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string PromotionPayload(string? installationOwnershipId = null)
    {
        byte[] bytes = RecipeBytes();
        return "{"
            + "\"schemaVersion\":1,"
            + "\"operationId\":\"" + OperationId + "\","
            + "\"promotedJobId\":\"" + JobId + "\","
            + "\"certificateThumbprintSha1\":\"" + Thumbprint + "\","
            + "\"recipeBase64\":\"" + Convert.ToBase64String(bytes) + "\","
            + "\"expectedRecipeSha256\":\"" + UpperHex(SHA256.HashData(bytes)) + "\","
            + "\"expectedInstallationOwnershipId\":\"" + (installationOwnershipId ?? InstallId) + "\""
            + "}";
    }

    private static string DepromotionPayload(string? installationOwnershipId = null) =>
        "{"
        + "\"schemaVersion\":1,"
        + "\"operationId\":\"" + OperationId + "\","
        + "\"promotedJobId\":\"" + JobId + "\","
        + "\"expectedInstallationOwnershipId\":\"" + (installationOwnershipId ?? InstallId) + "\""
        + "}";

    private static ServiceOwnershipElevatedTransaction Transaction(
        ServiceOwnershipElevatedOperation operation) => new(operation);

    // ---- the fixtures really are what the closed contract accepts ----------

    [Fact]
    public void The_fixtures_are_calibrated_against_the_real_closed_contract()
    {
        // Without this, every "Accepted" below could be passing for the wrong
        // reason, and every "Refused" could be vacuous.
        Assert.True(ServicePromotionRequestParser.ParsePromotion(PromotionPayload()).IsAccepted);
        Assert.True(ServicePromotionRequestParser.ParseDepromotion(DepromotionPayload()).IsAccepted);
    }

    // ---- acceptance --------------------------------------------------------

    [Fact]
    public void A_promote_transaction_accepts_a_promotion_payload_bound_to_the_anchor_id()
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Promote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Accepted,
            transaction.AcceptPayload(PromotionPayload(), InstallId));

        Assert.Equal(ServiceOwnershipElevatedTransactionOutcome.Unspecified, transaction.Outcome);
        Assert.Equal(0, transaction.LedgerReadCount);
    }

    [Fact]
    public void A_depromote_transaction_accepts_a_depromotion_payload_bound_to_the_anchor_id()
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Depromote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Accepted,
            transaction.AcceptPayload(DepromotionPayload(), InstallId));

        Assert.Equal(0, transaction.LedgerReadCount);
    }

    // ---- THE VERB, NOT THE PAYLOAD, SELECTS THE OPERATION ------------------

    [Fact]
    public void A_promote_transaction_refuses_a_depromotion_shaped_payload()
    {
        // THE CORE PROPERTY. The depromotion document is perfectly well formed -
        // the calibration above proves the depromotion parser accepts it - and it
        // is still refused, because the PROMOTION parser is the only one this
        // transaction will ever call.
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Promote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Refused,
            transaction.AcceptPayload(DepromotionPayload(), InstallId));

        Assert.Equal(ServiceOwnershipElevatedTransactionOutcome.PayloadRefused, transaction.Outcome);
    }

    [Fact]
    public void A_depromote_transaction_refuses_a_promotion_shaped_payload()
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Depromote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Refused,
            transaction.AcceptPayload(PromotionPayload(), InstallId));

        Assert.Equal(ServiceOwnershipElevatedTransactionOutcome.PayloadRefused, transaction.Outcome);
    }

    // The theory parameter is the operation's NAME, never the internal operation
    // type itself - xunit's public theory surface cannot expose a less-accessible
    // type. The mapping happens inside each method body.
    private static ServiceOwnershipElevatedOperation OperationByName(string name) => name switch
    {
        "Promote" => ServiceOwnershipElevatedOperation.Promote,
        "Depromote" => ServiceOwnershipElevatedOperation.Depromote,
        "Unspecified" => ServiceOwnershipElevatedOperation.Unspecified,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown operation name"),
    };

    [Theory]
    [InlineData("Promote")]
    [InlineData("Depromote")]
    public void An_unspecified_operation_accepts_nothing(string shapedName)
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Unspecified);

        string payload = OperationByName(shapedName) == ServiceOwnershipElevatedOperation.Promote
            ? PromotionPayload()
            : DepromotionPayload();

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Refused,
            transaction.AcceptPayload(payload, InstallId));
    }

    // ---- installation ownership binding ------------------------------------

    [Fact]
    public void A_promotion_aimed_at_another_installation_is_a_mismatch_not_a_generic_refusal()
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Promote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.InstallationOwnershipMismatch,
            transaction.AcceptPayload(PromotionPayload("install-somebody-else"), InstallId));

        Assert.Equal(
            ServiceOwnershipElevatedTransactionOutcome.InstallationOwnershipMismatch,
            transaction.Outcome);
    }

    [Fact]
    public void A_depromotion_aimed_at_another_installation_is_a_mismatch_not_a_generic_refusal()
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Depromote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.InstallationOwnershipMismatch,
            transaction.AcceptPayload(DepromotionPayload("install-somebody-else"), InstallId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("install id with spaces")]
    [InlineData("install/../..")]
    public void A_malformed_anchor_installation_id_is_refused_before_any_parse(string anchorId)
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Promote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Refused,
            transaction.AcceptPayload(PromotionPayload(), anchorId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    public void A_malformed_payload_is_refused(string payload)
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Promote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Refused,
            transaction.AcceptPayload(payload, InstallId));
    }

    // ---- at most once ------------------------------------------------------

    [Fact]
    public void Accept_payload_runs_at_most_once_even_when_the_second_payload_is_valid()
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Promote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Accepted,
            transaction.AcceptPayload(PromotionPayload(), InstallId));

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Refused,
            transaction.AcceptPayload(PromotionPayload(), InstallId));
    }

    [Fact]
    public void A_refused_first_payload_still_consumes_the_one_acceptance()
    {
        // Single-shot means single-shot: a rejected attempt does not buy a retry.
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Promote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Refused,
            transaction.AcceptPayload("{}", InstallId));

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Refused,
            transaction.AcceptPayload(PromotionPayload(), InstallId));
    }

    // ---- ORDERING: every refusal below returns BEFORE any effect -----------

    [Theory]
    [InlineData("Promote")]
    [InlineData("Depromote")]
    public void Preflight_without_an_accepted_payload_refuses_without_reading_the_ledger(
        string operationName)
    {
        ServiceOwnershipElevatedTransaction transaction = Transaction(OperationByName(operationName));

        Assert.Equal(
            ServiceIdentityBoundTransactionPreflightState.Refused,
            transaction.Preflight());

        // THE PROOF THAT NOTHING WAS TOUCHED. The root counts its own reader
        // invocations, and this path never reached one.
        Assert.Equal(0, transaction.LedgerReadCount);
        Assert.Equal(ServiceOwnershipElevatedTransactionOutcome.PreflightRefused, transaction.Outcome);
    }

    [Fact]
    public void Preflight_after_a_refused_payload_still_refuses_without_reading_the_ledger()
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Promote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Refused,
            transaction.AcceptPayload(DepromotionPayload(), InstallId));

        Assert.Equal(
            ServiceIdentityBoundTransactionPreflightState.Refused,
            transaction.Preflight());
        Assert.Equal(0, transaction.LedgerReadCount);
    }

    [Theory]
    [InlineData("Promote")]
    [InlineData("Depromote")]
    public void Apply_without_a_successful_preflight_refuses_and_never_reports_completed(
        string operationName)
    {
        ServiceOwnershipElevatedTransaction transaction = Transaction(OperationByName(operationName));

        ServiceIdentityBoundTransactionResult result = transaction.Apply(default);

        Assert.NotEqual(ServiceIdentityBoundTransactionState.Completed, result.State);
        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        Assert.False(result.AnchorRemoved);
        Assert.Equal(0, transaction.LedgerReadCount);
        Assert.Equal(ServiceOwnershipElevatedTransactionOutcome.ApplyRefused, transaction.Outcome);
    }

    [Fact]
    public void Apply_after_an_accepted_payload_but_no_preflight_still_refuses()
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Promote);

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.Accepted,
            transaction.AcceptPayload(PromotionPayload(), InstallId));

        Assert.Equal(
            ServiceIdentityBoundTransactionState.Compensated,
            transaction.Apply(default).State);
        Assert.Equal(0, transaction.LedgerReadCount);
    }

    // ---- STRUCTURE: no retained capability, no widened surface -------------

    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    [Fact]
    public void The_root_is_the_payload_transaction_seam_and_takes_only_the_closed_operation()
    {
        Type root = typeof(ServiceOwnershipElevatedTransaction);

        Assert.True(root.IsSealed);
        Assert.Contains(typeof(IServiceIdentityBoundPayloadTransaction), root.GetInterfaces());
        Assert.Contains(typeof(IServiceIdentityBoundTransaction), root.GetInterfaces());

        ConstructorInfo constructor = Assert.Single(root.GetConstructors(AllMembers));
        ParameterInfo parameter = Assert.Single(constructor.GetParameters());
        Assert.Equal(typeof(ServiceOwnershipElevatedOperation), parameter.ParameterType);
    }

    [Fact]
    public void The_root_retains_no_executor_no_store_and_no_native_adapter()
    {
        // "Retains nothing globally or across transactions", asserted against the
        // TYPE rather than against its source text. A field of any of these types
        // would be a capability that outlives one Apply.
        Type root = typeof(ServiceOwnershipElevatedTransaction);
        Type[] mustNotBeRetained =
        {
            typeof(ServiceOwnershipPromotionExecutor),
            typeof(ServiceOwnershipPromotedRecipeStore),
            typeof(ServiceOwnershipFixedPromotedRecipePort),
            typeof(IServiceOwnershipOwnerIdentityPort),
            typeof(IServiceOwnershipServiceSidPort),
            typeof(IServiceOwnershipCertificateFactsPort),
            typeof(IServiceOwnershipPriorDescriptorPort),
            typeof(IServiceOwnershipApprovedDescriptorPort),
            typeof(IServiceOwnershipDescriptorRestorePort),
            typeof(IServiceOwnershipCredentialObservationPort),
            typeof(IServiceOwnershipLedgerPersistencePort),
            typeof(IServiceOwnershipPromotedRecipePort),
        };

        var offenders = new List<string>();
        foreach (FieldInfo field in root.GetFields(AllMembers))
        {
            foreach (Type forbidden in mustNotBeRetained)
            {
                if (forbidden.IsAssignableFrom(field.FieldType))
                {
                    offenders.Add("field " + field.Name);
                }
            }
        }
        foreach (PropertyInfo property in root.GetProperties(AllMembers))
        {
            foreach (Type forbidden in mustNotBeRetained)
            {
                if (forbidden.IsAssignableFrom(property.PropertyType))
                {
                    offenders.Add("property " + property.Name);
                }
            }
        }

        Assert.Empty(offenders);

        // POSITIVE CONTROL: the same predicate DOES fire on a type that genuinely
        // retains one, so the empty result above is calibrated.
        Assert.Contains(
            mustNotBeRetained,
            t => t.IsAssignableFrom(typeof(ServiceOwnershipFixedPromotedRecipePort)));
    }

    [Fact]
    public void The_root_declares_no_static_mutable_state_and_no_delegate_shaped_member()
    {
        Type root = typeof(ServiceOwnershipElevatedTransaction);

        foreach (FieldInfo field in root.GetFields(AllMembers))
        {
            if (field.IsStatic)
            {
                Assert.True(
                    field.IsLiteral || field.IsInitOnly,
                    "static field " + field.Name + " is mutable");
            }
            Assert.False(
                typeof(Delegate).IsAssignableFrom(field.FieldType),
                "field " + field.Name + " is a delegate");
        }

        foreach (MethodInfo method in root.GetMethods(AllMembers))
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.False(
                    typeof(Delegate).IsAssignableFrom(parameter.ParameterType),
                    method.Name + " accepts a delegate");
            }
        }
    }

    [Fact]
    public void No_bounded_result_or_tostring_can_carry_an_identifier_or_a_path()
    {
        ServiceOwnershipElevatedTransaction transaction =
            Transaction(ServiceOwnershipElevatedOperation.Promote);

        Assert.Equal("Unspecified", transaction.ToString());

        Assert.Equal(
            ServiceIdentityPayloadAcceptanceState.InstallationOwnershipMismatch,
            transaction.AcceptPayload(PromotionPayload("install-somebody-else"), InstallId));

        string rendered = transaction.ToString();
        Assert.Equal(transaction.Outcome.ToString(), rendered);

        foreach (string token in new[]
                 {
                     "ProgramData", "PAXCookbook", "\\", "/", InstallId, JobId, OperationId, Thumbprint,
                 })
        {
            Assert.DoesNotContain(token, rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_bounded_outcome_vocabulary_is_closed_and_zero_never_reads_as_success()
    {
        ServiceOwnershipElevatedTransactionOutcome[] declared =
            Enum.GetValues<ServiceOwnershipElevatedTransactionOutcome>();

        Assert.Equal(10, declared.Length);
        Assert.Equal(0, (int)ServiceOwnershipElevatedTransactionOutcome.Unspecified);
        Assert.NotEqual(
            ServiceOwnershipElevatedTransactionOutcome.Completed,
            default(ServiceOwnershipElevatedTransactionOutcome));

        // Every member is distinct, so two states can never be confused.
        Assert.Equal(declared.Length, declared.Select(v => (int)v).Distinct().Count());
    }
}
