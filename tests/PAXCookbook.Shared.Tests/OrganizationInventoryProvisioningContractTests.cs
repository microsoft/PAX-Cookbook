using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// Cycle-07 focused matrix for the FUTURE elevated organization-inventory
// provisioning helper CONTRACT + PURE PLANNER. Every test exercises only the
// portable, I/O-free contract types in PAXCookbook.Shared. NOTHING here launches
// a process, touches ProgramData, an ACL, the registry, a certificate store, or a
// network endpoint; the planner is proven purely against synthetic inputs.
public sealed class OrganizationInventoryProvisioningContractTests
{
    // A fixed, well-formed 64-hex SHA-256 stand-in for a "current" inventory hash
    // that is NOT the hash of the sample document (so replacement, not no-op).
    private const string HexA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HexB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static string ValidInventoryJson(string id = "org-key-1")
        => "{\"schemaVersion\":1,\"entries\":[{\"entryVersion\":1,\"organizationKeyId\":\""
           + id
           + "\",\"displayName\":\"Key One\",\"certificateReferenceType\":\"app_registration_certificate\","
           + "\"adminState\":\"enabled\",\"tenantReference\":\"t-ref\",\"clientReference\":\"c-ref\"}]}";

    private static string RequestJson(
        string operation,
        string operationId = "op-1",
        string? inventory = null,
        string? expectedInvHash = null,
        int? expectedGeneration = null,
        string? expectedLedgerHash = null,
        bool planOnly = false)
    {
        var sb = new StringBuilder();
        sb.Append("{\"requestSchemaVersion\":1,\"operation\":\"").Append(operation).Append("\",");
        sb.Append("\"operationId\":\"").Append(operationId).Append("\",");
        sb.Append("\"inventoryDocument\":").Append(JsonString(inventory ?? string.Empty)).Append(',');
        if (expectedInvHash is not null)
        {
            sb.Append("\"expectedCurrentInventorySha256\":\"").Append(expectedInvHash).Append("\",");
        }
        if (expectedGeneration is int g)
        {
            sb.Append("\"expectedLedgerGeneration\":").Append(g).Append(',');
        }
        if (expectedLedgerHash is not null)
        {
            sb.Append("\"expectedCurrentLedgerSha256\":\"").Append(expectedLedgerHash).Append("\",");
        }
        sb.Append("\"planOnly\":").Append(planOnly ? "true" : "false").Append('}');
        return sb.ToString();
    }

    private static string JsonString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string Sha256Hex(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var sb = new StringBuilder(64);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static ProvisioningRequest Validated(string json)
    {
        ProvisioningRequestValidationResult r = ProvisioningRequestValidator.Validate(json);
        Assert.Equal(ProvisioningRequestOutcome.Valid, r.Outcome);
        return r.Request!;
    }

    private static OwnershipLedger Ledger(
        int currentGeneration = 1,
        string currentInventorySha256 = HexA,
        int? previousGeneration = null,
        string? previousInventorySha256 = null,
        ProvisioningTransactionState state = ProvisioningTransactionState.Idle)
        => new(
            ProvisioningContract.LedgerSchemaVersion,
            ProvisioningContract.ProductOwnershipMarker,
            ProvisioningContract.ManagedFeatureId,
            currentGeneration,
            currentInventorySha256,
            previousGeneration,
            previousInventorySha256,
            state,
            ProvisioningContract.OwnedFileNames,
            ProvisioningContract.AclPolicyVersion,
            "2026-07-23T00:00:00Z",
            "2026-07-23T00:00:00Z",
            "op-1");

    // ---- Request: verb boundary --------------------------------------------

    [Fact]
    public void T01_UnknownVerb_IsUnrepresentable()
    {
        ProvisioningRequestValidationResult r = ProvisioningRequestValidator.Validate(RequestJson("execute"));
        Assert.Equal(ProvisioningRequestOutcome.Invalid, r.Outcome);
        Assert.Equal(ProvisioningRequestInvalidReason.InvalidOperation, r.Reason);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("copy")]
    [InlineData("delete-file")]
    [InlineData("set-acl")]
    [InlineData("set-registry")]
    [InlineData("install-service")]
    [InlineData("invoke")]
    [InlineData("custom")]
    public void T02_ProhibitedVerbs_Rejected(string verb)
    {
        ProvisioningRequestValidationResult r = ProvisioningRequestValidator.Validate(RequestJson(verb));
        Assert.Equal(ProvisioningRequestInvalidReason.InvalidOperation, r.Reason);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    [InlineData("verify")]
    [InlineData("rollback")]
    [InlineData("remove")]
    public void T03_SupportedVerbs_MapToClosedEnum(string verb)
    {
        string? inv = verb is "plan" or "apply" ? ValidInventoryJson() : null;
        ProvisioningRequestValidationResult r = ProvisioningRequestValidator.Validate(RequestJson(verb, inventory: inv));
        Assert.Equal(ProvisioningRequestOutcome.Valid, r.Outcome);
    }

    // ---- Request: field boundaries -----------------------------------------

    [Fact]
    public void T04_UnknownRequestField_Rejected()
    {
        string json = "{\"requestSchemaVersion\":1,\"operation\":\"verify\",\"operationId\":\"op-1\",\"inventoryDocument\":\"\",\"planOnly\":false,\"surprise\":1}";
        Assert.Equal(ProvisioningRequestInvalidReason.UnknownField, ProvisioningRequestValidator.Validate(json).Reason);
    }

    [Fact]
    public void T05_OversizedRequest_RejectedBeforeParse()
    {
        var sb = new StringBuilder();
        sb.Append("{\"requestSchemaVersion\":1,\"operation\":\"apply\",\"operationId\":\"op-1\",\"inventoryDocument\":\"");
        sb.Append(new string('x', ProvisioningContract.MaxRequestBytes + 16));
        sb.Append("\",\"planOnly\":false}");
        Assert.Equal(ProvisioningRequestInvalidReason.OversizedRequest, ProvisioningRequestValidator.Validate(sb.ToString()).Reason);
    }

    [Fact]
    public void T06_MissingRequiredField_Rejected()
    {
        string json = "{\"requestSchemaVersion\":1,\"operation\":\"verify\",\"inventoryDocument\":\"\",\"planOnly\":false}";
        Assert.Equal(ProvisioningRequestInvalidReason.MissingField, ProvisioningRequestValidator.Validate(json).Reason);
    }

    [Fact]
    public void T07_UnsupportedSchema_Rejected()
    {
        string json = "{\"requestSchemaVersion\":2,\"operation\":\"verify\",\"operationId\":\"op-1\",\"inventoryDocument\":\"\",\"planOnly\":false}";
        Assert.Equal(ProvisioningRequestInvalidReason.UnsupportedSchema, ProvisioningRequestValidator.Validate(json).Reason);
    }

    [Fact]
    public void T08_DuplicateProperty_Rejected()
    {
        string json = "{\"requestSchemaVersion\":1,\"requestSchemaVersion\":1,\"operation\":\"verify\",\"operationId\":\"op-1\",\"inventoryDocument\":\"\",\"planOnly\":false}";
        Assert.Equal(ProvisioningRequestInvalidReason.DuplicateProperty, ProvisioningRequestValidator.Validate(json).Reason);
    }

    [Fact]
    public void T09_WrongType_Rejected()
    {
        string json = "{\"requestSchemaVersion\":1,\"operation\":\"verify\",\"operationId\":123,\"inventoryDocument\":\"\",\"planOnly\":false}";
        Assert.Equal(ProvisioningRequestInvalidReason.WrongType, ProvisioningRequestValidator.Validate(json).Reason);
    }

    // ---- Request: prohibited-key family (secret smuggling) ------------------

    [Theory]
    [InlineData("clientSecret")]
    [InlineData("secret")]
    [InlineData("certificate")]
    [InlineData("certificateBytes")]
    [InlineData("pfx")]
    [InlineData("privateKey")]
    [InlineData("privateKeyBytes")]
    [InlineData("token")]
    [InlineData("claim")]
    [InlineData("claims")]
    [InlineData("password")]
    [InlineData("upn")]
    [InlineData("account")]
    [InlineData("wamAccount")]
    [InlineData("credentialManagerTarget")]
    public void T10_ProhibitedSecretFields_RejectedEvenWhenNull(string key)
    {
        string json = "{\"requestSchemaVersion\":1,\"operation\":\"verify\",\"operationId\":\"op-1\",\"inventoryDocument\":\"\",\"planOnly\":false,\"" + key + "\":null}";
        Assert.Equal(ProvisioningRequestInvalidReason.ProhibitedField, ProvisioningRequestValidator.Validate(json).Reason);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("filePath")]
    [InlineData("inventoryPath")]
    [InlineData("destination")]
    [InlineData("registryPath")]
    public void T11_ProhibitedPathFields_RejectedEvenWhenEmpty(string key)
    {
        string json = "{\"requestSchemaVersion\":1,\"operation\":\"verify\",\"operationId\":\"op-1\",\"inventoryDocument\":\"\",\"planOnly\":false,\"" + key + "\":\"\"}";
        Assert.Equal(ProvisioningRequestInvalidReason.ProhibitedField, ProvisioningRequestValidator.Validate(json).Reason);
    }

    [Theory]
    [InlineData("executable")]
    [InlineData("command")]
    [InlineData("script")]
    [InlineData("arguments")]
    [InlineData("args")]
    [InlineData("workingDirectory")]
    [InlineData("env")]
    [InlineData("environment")]
    [InlineData("dll")]
    public void T12_ProhibitedCommandFields_Rejected(string key)
    {
        string json = "{\"requestSchemaVersion\":1,\"operation\":\"verify\",\"operationId\":\"op-1\",\"inventoryDocument\":\"\",\"planOnly\":false,\"" + key + "\":\"x\"}";
        Assert.Equal(ProvisioningRequestInvalidReason.ProhibitedField, ProvisioningRequestValidator.Validate(json).Reason);
    }

    [Theory]
    [InlineData("serviceName")]
    [InlineData("storeName")]
    [InlineData("certStore")]
    public void T13_ProhibitedServiceCertStoreFields_Rejected(string key)
    {
        string json = "{\"requestSchemaVersion\":1,\"operation\":\"verify\",\"operationId\":\"op-1\",\"inventoryDocument\":\"\",\"planOnly\":false,\"" + key + "\":\"x\"}";
        Assert.Equal(ProvisioningRequestInvalidReason.ProhibitedField, ProvisioningRequestValidator.Validate(json).Reason);
    }

    [Theory]
    [InlineData("uri")]
    [InlineData("graphEndpoint")]
    [InlineData("scope")]
    [InlineData("scopes")]
    [InlineData("recipe")]
    [InlineData("bake")]
    [InlineData("cookCommand")]
    [InlineData("paxArgs")]
    public void T14_ProhibitedCloudRecipeBakeFields_Rejected(string key)
    {
        string json = "{\"requestSchemaVersion\":1,\"operation\":\"verify\",\"operationId\":\"op-1\",\"inventoryDocument\":\"\",\"planOnly\":false,\"" + key + "\":\"x\"}";
        Assert.Equal(ProvisioningRequestInvalidReason.ProhibitedField, ProvisioningRequestValidator.Validate(json).Reason);
    }

    [Fact]
    public void T15_ProhibitedField_CaseInsensitive()
    {
        string json = "{\"requestSchemaVersion\":1,\"operation\":\"verify\",\"operationId\":\"op-1\",\"inventoryDocument\":\"\",\"planOnly\":false,\"SeCrEt\":\"\"}";
        Assert.Equal(ProvisioningRequestInvalidReason.ProhibitedField, ProvisioningRequestValidator.Validate(json).Reason);
    }

    // ---- Request: operationId + hashes -------------------------------------

    [Theory]
    [InlineData("bad id")]
    [InlineData("has:colon")]
    [InlineData("slash/here")]
    [InlineData("star*char")]
    public void T16_InvalidOperationIdCharset_Rejected(string opId)
    {
        Assert.Equal(ProvisioningRequestInvalidReason.InvalidOperationId,
            ProvisioningRequestValidator.Validate(RequestJson("verify", opId)).Reason);
    }

    [Fact]
    public void T17_OperationIdTooLong_Rejected()
    {
        string opId = new string('a', ProvisioningContract.MaxOperationIdLength + 1);
        Assert.Equal(ProvisioningRequestInvalidReason.InvalidOperationId,
            ProvisioningRequestValidator.Validate(RequestJson("verify", opId)).Reason);
    }

    [Fact]
    public void T18_InvalidExpectedHashFormat_Rejected()
    {
        Assert.Equal(ProvisioningRequestInvalidReason.InvalidHashFormat,
            ProvisioningRequestValidator.Validate(RequestJson("verify", expectedInvHash: "nothex")).Reason);
    }

    [Fact]
    public void T19_InvalidExpectedGeneration_Rejected()
    {
        Assert.Equal(ProvisioningRequestInvalidReason.InvalidGeneration,
            ProvisioningRequestValidator.Validate(RequestJson("verify", expectedGeneration: 0)).Reason);
    }

    // ---- Request: inventory presence ---------------------------------------

    [Fact]
    public void T20_InvalidInventory_RejectedBeforePlan()
    {
        ProvisioningRequestValidationResult r = ProvisioningRequestValidator.Validate(RequestJson("apply", inventory: "{ not json"));
        Assert.Equal(ProvisioningRequestOutcome.Invalid, r.Outcome);
        Assert.Equal(ProvisioningRequestInvalidReason.InvalidInventory, r.Reason);
    }

    [Fact]
    public void T21_ValidInventory_AcceptedForApply()
    {
        ProvisioningRequestValidationResult r = ProvisioningRequestValidator.Validate(RequestJson("apply", inventory: ValidInventoryJson()));
        Assert.Equal(ProvisioningRequestOutcome.Valid, r.Outcome);
        Assert.Equal(ProvisioningOperation.Apply, r.Request!.Operation);
    }

    [Fact]
    public void T22_ApplyWithoutInventory_Rejected()
    {
        Assert.Equal(ProvisioningRequestInvalidReason.InventoryRequiredForApply,
            ProvisioningRequestValidator.Validate(RequestJson("apply")).Reason);
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("remove")]
    [InlineData("rollback")]
    public void T23_InventoryNotAllowedForNonApply_Rejected(string verb)
    {
        Assert.Equal(ProvisioningRequestInvalidReason.InventoryNotAllowed,
            ProvisioningRequestValidator.Validate(RequestJson(verb, inventory: ValidInventoryJson())).Reason);
    }

    // ---- Planner: plan verb + no mutation ----------------------------------

    [Fact]
    public void T24_PlanVerb_ProducesActionsButNoMutation()
    {
        ProvisioningRequest req = Validated(RequestJson("plan", inventory: ValidInventoryJson()));
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, SyntheticSourceState.Absent(), SyntheticLedgerState.Absent(), SyntheticTrustState.AbsentDirectory());
        Assert.True(plan.IsPlanned);
        Assert.False(plan.WouldMutate);
        Assert.NotEmpty(plan.Actions);
    }

    [Fact]
    public void T25_PlanOnlyApply_IsNonMutating()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson(), planOnly: true));
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, SyntheticSourceState.Absent(), SyntheticLedgerState.Absent(), SyntheticTrustState.AbsentDirectory());
        Assert.True(plan.IsPlanned);
        Assert.False(plan.WouldMutate);
        Assert.Equal(ProvisioningPlanState.CreatePlanned, plan.ResultState);
    }

    // ---- Planner: apply-absent ---------------------------------------------

    [Fact]
    public void T26_ApplyAbsent_PlansGenerationOneCreate()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson()));
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, SyntheticSourceState.Absent(), SyntheticLedgerState.Absent(), SyntheticTrustState.AbsentDirectory());
        Assert.Equal(ProvisioningPlanState.CreatePlanned, plan.ResultState);
        Assert.Equal(1, plan.TargetGeneration);
        Assert.True(plan.WouldMutate);
        Assert.Contains(plan.Actions, a => a.Kind == ProvisioningActionKind.EnsureManagedDirectory);
        Assert.Contains(plan.Actions, a => a.Kind == ProvisioningActionKind.StampAcl);
        Assert.Contains(plan.Actions, a => a.Kind == ProvisioningActionKind.ReplaceLedger && a.TargetGeneration == 1);
    }

    [Fact]
    public void T27_ApplyAbsent_UntrustedEnvironment_FailsClosed()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson()));
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, SyntheticSourceState.Absent(), SyntheticLedgerState.Absent(), SyntheticTrustState.Untrusted());
        Assert.False(plan.IsPlanned);
        Assert.Equal(ProvisioningRejectionReason.UntrustedEnvironment, plan.RejectionReason);
    }

    // ---- Planner: foreign / disagreement -----------------------------------

    [Fact]
    public void T28_ApplyForeignInventory_FailsClosed()
    {
        // Inventory + ledger both present, but ledger not valid -> not owned.
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson()));
        var source = SyntheticSourceState.Present(HexA);
        var ledger = SyntheticLedgerState.Present(Ledger(), ledgerValid: false);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.False(plan.IsPlanned);
        Assert.Equal(ProvisioningRejectionReason.ForeignArtifactPresent, plan.RejectionReason);
    }

    [Fact]
    public void T29_ApplyForeignLedgerAcl_FailsClosed()
    {
        // Present + valid ledger with matching hash, but ACL untrusted -> not owned.
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson()));
        var source = SyntheticSourceState.Present(HexA);
        var ledger = SyntheticLedgerState.Present(Ledger(), ledgerValid: true);
        var trust = new SyntheticTrustState(true, ownerTrusted: false, aclTrusted: false, noReparsePoint: true, pathContained: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, trust);
        Assert.False(plan.IsPlanned);
        Assert.Equal(ProvisioningRejectionReason.UntrustedEnvironment, plan.RejectionReason);
    }

    [Fact]
    public void T30_InventoryWithoutLedger_BlocksApply()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson()));
        var source = SyntheticSourceState.Present(HexA);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, SyntheticLedgerState.Absent(), SyntheticTrustState.FullyTrusted());
        Assert.False(plan.IsPlanned);
        Assert.Equal(ProvisioningRejectionReason.LedgerInventoryDisagree, plan.RejectionReason);
    }

    [Fact]
    public void T31_LedgerWithoutInventory_BlocksApply()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson()));
        var ledger = SyntheticLedgerState.Present(Ledger(), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, SyntheticSourceState.Absent(), ledger, SyntheticTrustState.FullyTrusted());
        Assert.False(plan.IsPlanned);
        Assert.Equal(ProvisioningRejectionReason.LedgerInventoryDisagree, plan.RejectionReason);
    }

    [Fact]
    public void T32_UnknownSibling_BlocksApply()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson()));
        var source = new SyntheticSourceState(ArtifactPresence.Present, HexA, unknownSiblingPresent: true);
        var ledger = SyntheticLedgerState.Present(Ledger(), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.False(plan.IsPlanned);
        Assert.Equal(ProvisioningRejectionReason.UnknownSiblingArtifact, plan.RejectionReason);
    }

    // ---- Planner: owned match replacement + concurrency --------------------

    [Fact]
    public void T33_OwnedMatch_PermitsReplacement()
    {
        // Generation-2 owned state (current + prev backup both present) -> replace
        // advances to generation 3 and retains a single prev backup.
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson("org-key-2")));
        var source = SyntheticSourceState.PresentWithPrevious(HexA, HexB);
        var ledger = SyntheticLedgerState.Present(Ledger(currentGeneration: 2, currentInventorySha256: HexA, previousGeneration: 1, previousInventorySha256: HexB), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningPlanState.ReplacePlanned, plan.ResultState);
        Assert.Equal(3, plan.TargetGeneration);
        Assert.True(plan.OwnershipProven);
        Assert.True(plan.WouldMutate);
    }

    [Fact]
    public void T34_IdenticalContent_IsIdempotentNoOp()
    {
        string doc = ValidInventoryJson();
        string docHash = Sha256Hex(doc);
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: doc));
        var source = SyntheticSourceState.PresentWithPrevious(docHash, HexB);
        var ledger = SyntheticLedgerState.Present(Ledger(currentGeneration: 2, currentInventorySha256: docHash, previousGeneration: 1, previousInventorySha256: HexB), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningPlanState.NoOpUpToDate, plan.ResultState);
        Assert.False(plan.WouldMutate);
        Assert.DoesNotContain(plan.Actions, a => a.Kind == ProvisioningActionKind.ReplaceInventory);
        // A no-op never rewrites the prev backup.
        Assert.DoesNotContain(plan.Actions, a => a.Kind == ProvisioningActionKind.ReplacePreviousInventory);
    }

    [Fact]
    public void T35_ExpectedGenerationMismatch_FailsClosed()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson("k2"), expectedGeneration: 99));
        var source = SyntheticSourceState.PresentWithPrevious(HexA, HexB);
        var ledger = SyntheticLedgerState.Present(Ledger(currentGeneration: 2, currentInventorySha256: HexA, previousGeneration: 1, previousInventorySha256: HexB), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningRejectionReason.GenerationMismatch, plan.RejectionReason);
    }

    [Fact]
    public void T36_ExpectedInventoryHashMismatch_FailsClosed()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson("k2"), expectedInvHash: HexB));
        var source = SyntheticSourceState.PresentWithPrevious(HexA, HexB);
        var ledger = SyntheticLedgerState.Present(Ledger(currentGeneration: 2, currentInventorySha256: HexA, previousGeneration: 1, previousInventorySha256: HexB), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningRejectionReason.StateMismatch, plan.RejectionReason);
    }

    // ---- Planner: verify ---------------------------------------------------

    [Fact]
    public void T37_Verify_IsReadOnly_NoWriteActions()
    {
        ProvisioningRequest req = Validated(RequestJson("verify"));
        var source = SyntheticSourceState.Present(HexA);
        var ledger = SyntheticLedgerState.Present(Ledger(currentInventorySha256: HexA), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningPlanState.VerifyPlanned, plan.ResultState);
        Assert.False(plan.WouldMutate);
        Assert.All(plan.Actions, a => Assert.Equal(ProvisioningActionKind.VerifyInventoryLedgerPair, a.Kind));
    }

    [Fact]
    public void T38_Verify_NeverRepairs_EvenWhenNotOwned()
    {
        ProvisioningRequest req = Validated(RequestJson("verify"));
        var source = SyntheticSourceState.Present(HexA);
        var ledger = SyntheticLedgerState.Present(Ledger(currentInventorySha256: HexB), ledgerValid: true); // hash disagreement
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningPlanState.VerifyPlanned, plan.ResultState);
        Assert.False(plan.WouldMutate);
        Assert.DoesNotContain(plan.Actions, a => a.Kind == ProvisioningActionKind.ReplaceInventory || a.Kind == ProvisioningActionKind.ReplaceLedger || a.Kind == ProvisioningActionKind.StampAcl);
    }

    // ---- Planner: rollback -------------------------------------------------

    [Fact]
    public void T39_RollbackWithoutPreviousGeneration_Rejected()
    {
        // A generation-1 owned state has no previous generation to roll back to.
        ProvisioningRequest req = Validated(RequestJson("rollback"));
        var source = SyntheticSourceState.Present(HexA);
        var ledger = SyntheticLedgerState.Present(Ledger(currentGeneration: 1, currentInventorySha256: HexA), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningRejectionReason.NoPreviousGeneration, plan.RejectionReason);
    }

    [Fact]
    public void T40_RollbackWithoutOwnership_Rejected()
    {
        ProvisioningRequest req = Validated(RequestJson("rollback"));
        var source = SyntheticSourceState.Present(HexB); // hash != ledger -> not owned
        var ledger = SyntheticLedgerState.Present(Ledger(currentGeneration: 2, currentInventorySha256: HexA, previousGeneration: 1, previousInventorySha256: HexB), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningRejectionReason.OwnershipNotProven, plan.RejectionReason);
    }

    [Fact]
    public void T41_RollbackWithMismatchedExpectedGeneration_Rejected()
    {
        ProvisioningRequest req = Validated(RequestJson("rollback", expectedGeneration: 5));
        var source = SyntheticSourceState.PresentWithPrevious(HexA, HexB);
        var ledger = SyntheticLedgerState.Present(Ledger(currentGeneration: 2, currentInventorySha256: HexA, previousGeneration: 1, previousInventorySha256: HexB), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningRejectionReason.GenerationMismatch, plan.RejectionReason);
    }

    [Fact]
    public void T42_ValidOwnedRollback_ReversibleSwapMonotonicGeneration()
    {
        // Reversible swap: current(A) and prev(B) exchange roles through a NEW
        // monotonic generation N+1 (never a decremented/reused generation).
        ProvisioningRequest req = Validated(RequestJson("rollback", expectedGeneration: 2));
        var source = SyntheticSourceState.PresentWithPrevious(HexA, HexB);
        var ledger = SyntheticLedgerState.Present(Ledger(currentGeneration: 2, currentInventorySha256: HexA, previousGeneration: 1, previousInventorySha256: HexB), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningPlanState.RollbackPlanned, plan.ResultState);
        Assert.Equal(3, plan.TargetGeneration);
        Assert.Contains(plan.Actions, a => a.Kind == ProvisioningActionKind.RestorePreviousGeneration && a.TargetGeneration == 3);
        Assert.Contains(plan.Actions, a => a.Kind == ProvisioningActionKind.ReplacePreviousInventory);
    }

    [Fact]
    public void T43_Rollback_CannotUseCallerBytes()
    {
        // A rollback request may not carry inventory bytes at all.
        Assert.Equal(ProvisioningRequestInvalidReason.InventoryNotAllowed,
            ProvisioningRequestValidator.Validate(RequestJson("rollback", inventory: ValidInventoryJson())).Reason);
    }

    // ---- Planner: remove ---------------------------------------------------

    [Fact]
    public void T44_RemoveWithoutOwnership_Rejected()
    {
        ProvisioningRequest req = Validated(RequestJson("remove"));
        var source = SyntheticSourceState.Present(HexB); // not owned
        var ledger = SyntheticLedgerState.Present(Ledger(currentInventorySha256: HexA), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningRejectionReason.OwnershipNotProven, plan.RejectionReason);
    }

    [Fact]
    public void T45_RemoveWithForeignSibling_Rejected()
    {
        ProvisioningRequest req = Validated(RequestJson("remove"));
        var source = new SyntheticSourceState(ArtifactPresence.Present, HexA, unknownSiblingPresent: true);
        var ledger = SyntheticLedgerState.Present(Ledger(currentInventorySha256: HexA), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningRejectionReason.UnknownSiblingArtifact, plan.RejectionReason);
    }

    [Fact]
    public void T46_ValidOwnedRemove_RemovesExactOwnedThenDirIfEmpty()
    {
        ProvisioningRequest req = Validated(RequestJson("remove"));
        var source = SyntheticSourceState.Present(HexA);
        var ledger = SyntheticLedgerState.Present(Ledger(currentInventorySha256: HexA), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningPlanState.RemovePlanned, plan.ResultState);
        Assert.Contains(plan.Actions, a => a.Kind == ProvisioningActionKind.RemoveInventory);
        Assert.Contains(plan.Actions, a => a.Kind == ProvisioningActionKind.RemoveLedger);
        Assert.Equal(ProvisioningActionKind.RemoveManagedDirectoryIfEmpty, plan.Actions[^1].Kind);
    }

    [Fact]
    public void T47_Remove_NeverTargetsPersonalOrCertStoreArtifacts()
    {
        ProvisioningRequest req = Validated(RequestJson("remove"));
        var source = SyntheticSourceState.Present(HexA);
        var ledger = SyntheticLedgerState.Present(Ledger(currentInventorySha256: HexA), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        // Only the symbolic owned artifacts + managed dir are referenced.
        Assert.All(plan.Actions, a => Assert.Contains(a.Artifact, new[]
        {
            ProvisioningArtifact.Inventory, ProvisioningArtifact.Ledger,
            ProvisioningArtifact.TransactionMarker, ProvisioningArtifact.ManagedDirectory,
        }));
    }

    // ---- Planner: partial / recovery ---------------------------------------

    [Fact]
    public void T48_PartialTransaction_CannotReportSuccess()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson("k2")));
        var source = SyntheticSourceState.Present(HexA);
        var ledger = SyntheticLedgerState.Present(Ledger(currentInventorySha256: HexA, state: ProvisioningTransactionState.InventoryCommitted), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.Equal(ProvisioningPlanState.RecoveryPlanned, plan.ResultState);
        ProvisioningResult result = ProvisioningResult.FromPlan(plan);
        Assert.NotEqual(ProvisioningResultCodes.Ok, result.Code);
        Assert.Equal(ProvisioningResultCodes.RecoveryRequired, result.Code);
    }

    [Fact]
    public void T49_Recovery_RequiresOwnership()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson("k2")));
        var source = SyntheticSourceState.Present(HexB); // hash != ledger -> not owned
        var ledger = SyntheticLedgerState.Present(Ledger(currentInventorySha256: HexA, state: ProvisioningTransactionState.LedgerCommitted), ledgerValid: true);
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, source, ledger, SyntheticTrustState.FullyTrusted());
        Assert.False(plan.IsPlanned);
        Assert.Equal(ProvisioningRejectionReason.PartialTransaction, plan.RejectionReason);
    }

    // ---- Ledger validator ---------------------------------------------------

    private static string LedgerJson(
        string schema = "1",
        string marker = ProvisioningContract.ProductOwnershipMarker,
        string featureId = ProvisioningContract.ManagedFeatureId,
        string state = "idle",
        string owned = "[\"organization-key-inventory.json\",\"ownership-ledger.json\",\"organization-key-inventory.prev.json\"]",
        string? extra = null,
        string hash = HexA)
    {
        var sb = new StringBuilder();
        sb.Append("{\"ledgerSchemaVersion\":").Append(schema).Append(',');
        sb.Append("\"productOwnershipMarker\":\"").Append(marker).Append("\",");
        sb.Append("\"managedFeatureId\":\"").Append(featureId).Append("\",");
        sb.Append("\"currentGeneration\":1,");
        sb.Append("\"currentInventorySha256\":\"").Append(hash).Append("\",");
        sb.Append("\"transactionState\":\"").Append(state).Append("\",");
        sb.Append("\"ownedFileNames\":").Append(owned).Append(',');
        sb.Append("\"aclPolicyVersion\":1,");
        sb.Append("\"createdUtc\":\"2026-07-23T00:00:00Z\",");
        sb.Append("\"updatedUtc\":\"2026-07-23T00:00:00Z\",");
        sb.Append("\"lastOperationId\":\"op-1\"");
        if (extra is not null)
        {
            sb.Append(',').Append(extra);
        }
        sb.Append('}');
        return sb.ToString();
    }

    [Fact]
    public void T50_ValidLedger_Accepted()
    {
        OwnershipLedgerValidationResult r = OwnershipLedgerValidator.Validate(LedgerJson());
        Assert.Equal(OwnershipLedgerOutcome.Valid, r.Outcome);
    }

    [Fact]
    public void T51_LedgerUnknownField_Rejected()
    {
        Assert.Equal(OwnershipLedgerInvalidReason.UnknownField,
            OwnershipLedgerValidator.Validate(LedgerJson(extra: "\"surprise\":1")).Reason);
    }

    [Fact]
    public void T52_LedgerProhibitedField_Rejected()
    {
        Assert.Equal(OwnershipLedgerInvalidReason.ProhibitedField,
            OwnershipLedgerValidator.Validate(LedgerJson(extra: "\"secret\":null")).Reason);
    }

    [Fact]
    public void T53_LedgerWrongSchema_Rejected()
    {
        Assert.Equal(OwnershipLedgerInvalidReason.UnsupportedSchema,
            OwnershipLedgerValidator.Validate(LedgerJson(schema: "2")).Reason);
    }

    [Fact]
    public void T54_LedgerWrongMarker_Rejected()
    {
        Assert.Equal(OwnershipLedgerInvalidReason.WrongMarker,
            OwnershipLedgerValidator.Validate(LedgerJson(marker: "SomethingElse")).Reason);
    }

    [Fact]
    public void T55_LedgerWrongFeatureId_Rejected()
    {
        Assert.Equal(OwnershipLedgerInvalidReason.WrongFeatureId,
            OwnershipLedgerValidator.Validate(LedgerJson(featureId: "other-feature")).Reason);
    }

    [Fact]
    public void T56_LedgerWrongOwnedFileNames_Rejected()
    {
        Assert.Equal(OwnershipLedgerInvalidReason.WrongOwnedFileNames,
            OwnershipLedgerValidator.Validate(LedgerJson(owned: "[\"foreign.json\"]")).Reason);
    }

    [Fact]
    public void T57_LedgerUnknownTransactionState_Rejected()
    {
        Assert.Equal(OwnershipLedgerInvalidReason.InvalidTransactionState,
            OwnershipLedgerValidator.Validate(LedgerJson(state: "frozen")).Reason);
    }

    [Fact]
    public void T58_LedgerBadHashFormat_Rejected()
    {
        Assert.Equal(OwnershipLedgerInvalidReason.InvalidHashFormat,
            OwnershipLedgerValidator.Validate(LedgerJson(hash: "nothex")).Reason);
    }

    [Fact]
    public void T59_LedgerNonMonotonicGeneration_Rejected()
    {
        var ledger = Ledger(currentGeneration: 1, previousGeneration: 1, previousInventorySha256: HexB);
        Assert.Equal(OwnershipLedgerInvalidReason.NonMonotonicGeneration,
            OwnershipLedgerValidator.Validate(ledger).Reason);
    }

    [Fact]
    public void T60_LedgerNoOrgKeyMetadata_TenantReferenceRejected()
    {
        Assert.Equal(OwnershipLedgerOutcome.Invalid,
            OwnershipLedgerValidator.Validate(LedgerJson(extra: "\"tenantReference\":\"x\"")).Outcome);
    }

    // ---- ACL descriptor -----------------------------------------------------

    [Fact]
    public void T61_AclOwnerLocalSystem_WithSystemWrite_Valid()
    {
        var acl = new AclDescriptor(AclDescriptor.SidLocalSystem, new[]
        {
            new AclEntry(AclDescriptor.SidLocalSystem, AclAccess.Write),
            new AclEntry(AclDescriptor.SidBuiltinAdministrators, AclAccess.Write),
            new AclEntry(AclDescriptor.SidAuthenticatedUsers, AclAccess.Read),
        }, protectedDacl: true);
        Assert.Equal(AclDescriptorOutcome.Valid, AclDescriptorValidator.Validate(acl).Outcome);
    }

    [Fact]
    public void T62_AclOwnerAdministrators_Valid()
    {
        var acl = new AclDescriptor(AclDescriptor.SidBuiltinAdministrators, new[]
        {
            new AclEntry(AclDescriptor.SidBuiltinAdministrators, AclAccess.Write),
        }, protectedDacl: true);
        Assert.Equal(AclDescriptorOutcome.Valid, AclDescriptorValidator.Validate(acl).Outcome);
    }

    [Fact]
    public void T63_AclOwnerNotTrusted_Rejected()
    {
        var acl = new AclDescriptor(AclDescriptor.SidAuthenticatedUsers, new[]
        {
            new AclEntry(AclDescriptor.SidLocalSystem, AclAccess.Write),
        }, protectedDacl: true);
        Assert.Equal(AclDescriptorInvalidReason.OwnerNotTrusted, AclDescriptorValidator.Validate(acl).Reason);
    }

    [Theory]
    [InlineData(AclDescriptor.SidEveryone)]
    [InlineData(AclDescriptor.SidAuthenticatedUsers)]
    [InlineData(AclDescriptor.SidUsers)]
    [InlineData(AclDescriptor.SidInteractive)]
    public void T64_AclBroadWrite_Rejected(string sid)
    {
        var acl = new AclDescriptor(AclDescriptor.SidLocalSystem, new[]
        {
            new AclEntry(AclDescriptor.SidLocalSystem, AclAccess.Write),
            new AclEntry(sid, AclAccess.Write),
        }, protectedDacl: true);
        Assert.Equal(AclDescriptorInvalidReason.BroadWriteGranted, AclDescriptorValidator.Validate(acl).Reason);
    }

    [Fact]
    public void T65_AclOrdinaryUserWrite_Rejected()
    {
        var acl = new AclDescriptor(AclDescriptor.SidLocalSystem, new[]
        {
            new AclEntry("S-1-5-21-1111111111-2222222222-3333333333-1001", AclAccess.Write),
        }, protectedDacl: true);
        Assert.Equal(AclDescriptorInvalidReason.NonTrustedWriteGranted, AclDescriptorValidator.Validate(acl).Reason);
    }

    [Fact]
    public void T66_AclUnknownSidWrite_Rejected()
    {
        var acl = new AclDescriptor(AclDescriptor.SidLocalSystem, new[]
        {
            new AclEntry("S-1-99-999", AclAccess.Write),
        }, protectedDacl: true);
        Assert.Equal(AclDescriptorInvalidReason.NonTrustedWriteGranted, AclDescriptorValidator.Validate(acl).Reason);
    }

    [Fact]
    public void T67_AclLocalizedNameOwner_Rejected()
    {
        var acl = new AclDescriptor("Administrators", new[]
        {
            new AclEntry(AclDescriptor.SidLocalSystem, AclAccess.Write),
        }, protectedDacl: true);
        Assert.Equal(AclDescriptorInvalidReason.LocalizedNameNotAllowed, AclDescriptorValidator.Validate(acl).Reason);
    }

    [Fact]
    public void T68_AclLocalizedNameAce_Rejected()
    {
        var acl = new AclDescriptor(AclDescriptor.SidLocalSystem, new[]
        {
            new AclEntry("Everyone", AclAccess.Read),
        }, protectedDacl: true);
        Assert.Equal(AclDescriptorInvalidReason.LocalizedNameNotAllowed, AclDescriptorValidator.Validate(acl).Reason);
    }

    [Fact]
    public void T69_AclUnprotectedDacl_Rejected()
    {
        var acl = new AclDescriptor(AclDescriptor.SidLocalSystem, new[]
        {
            new AclEntry(AclDescriptor.SidLocalSystem, AclAccess.Write),
        }, protectedDacl: false);
        Assert.Equal(AclDescriptorInvalidReason.DaclNotProtected, AclDescriptorValidator.Validate(acl).Reason);
    }

    [Fact]
    public void T70_AclContract_TrustedSetMatchesCycle6Reader()
    {
        Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544" }, AclDescriptor.RequiredOwnerSids);
        Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544" }, AclDescriptor.WriteCapableSids);
        Assert.Equal(new[] { "S-1-1-0", "S-1-5-11", "S-1-5-32-545", "S-1-5-4" }, AclDescriptor.BroadSids);
    }

    // ---- Structural: no arbitrary path/command anywhere in the plan ---------

    [Fact]
    public void T71_PlanActions_HaveNoStringPathOrCommandField()
    {
        // A plan action carries only enums + a nullable int generation; there is
        // no string-typed member that could smuggle a path/command/executable.
        PropertyInfo[] props = typeof(ProvisioningPlanAction).GetProperties();
        Assert.DoesNotContain(props, p => p.PropertyType == typeof(string));
        Assert.Contains(props, p => p.PropertyType == typeof(ProvisioningActionKind));
        Assert.Contains(props, p => p.PropertyType == typeof(ProvisioningArtifact));
    }

    [Fact]
    public void T72_Result_ProjectsBoundedAuditFactsOnly()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson()));
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, SyntheticSourceState.Absent(), SyntheticLedgerState.Absent(), SyntheticTrustState.AbsentDirectory());
        ProvisioningResult result = ProvisioningResult.FromPlan(plan);
        Assert.Equal(ProvisioningOperation.Apply, result.Operation);
        Assert.Equal(ProvisioningResultCodes.Ok, result.Code);
        // Only bounded scalar audit members exist.
        PropertyInfo[] props = typeof(ProvisioningResult).GetProperties();
        Assert.All(props, p => Assert.True(
            p.PropertyType == typeof(int)
            || p.PropertyType == typeof(bool)
            || p.PropertyType.IsEnum));
    }

    [Fact]
    public void T73_Planner_IsDeterministic()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson()));
        ProvisioningPlan a = ProvisioningPlanner.Plan(req, SyntheticSourceState.Absent(), SyntheticLedgerState.Absent(), SyntheticTrustState.AbsentDirectory());
        ProvisioningPlan b = ProvisioningPlanner.Plan(req, SyntheticSourceState.Absent(), SyntheticLedgerState.Absent(), SyntheticTrustState.AbsentDirectory());
        Assert.Equal(a.ResultState, b.ResultState);
        Assert.Equal(a.Actions.Count, b.Actions.Count);
    }

    [Fact]
    public void T74_NullValidatedRequest_FailsClosed()
    {
        ProvisioningPlan plan = ProvisioningPlanner.Plan(null, SyntheticSourceState.Absent(), SyntheticLedgerState.Absent(), SyntheticTrustState.AbsentDirectory());
        Assert.False(plan.IsPlanned);
        Assert.Equal(ProvisioningRejectionReason.RequestNotValidated, plan.RejectionReason);
    }

    [Fact]
    public void T75_RejectionResult_MapsToBoundedCode()
    {
        ProvisioningRequest req = Validated(RequestJson("apply", inventory: ValidInventoryJson()));
        ProvisioningPlan plan = ProvisioningPlanner.Plan(req, SyntheticSourceState.Absent(), SyntheticLedgerState.Absent(), SyntheticTrustState.Untrusted());
        ProvisioningResult result = ProvisioningResult.FromPlan(plan);
        Assert.Equal(ProvisioningResultCodes.UntrustedEnvironment, result.Code);
    }
}
