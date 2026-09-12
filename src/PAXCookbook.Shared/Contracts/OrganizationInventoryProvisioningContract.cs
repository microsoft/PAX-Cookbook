using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.Shared.Contracts;

// Canonical, cross-component, PORTABLE contract for the FUTURE elevated
// organization-inventory PROVISIONING helper (Cycle 07).
//
// This file defines, and deterministically validates, the CONTRACT and a PURE,
// I/O-FREE OPERATION PLANNER for a FUTURE bounded elevated Setup/Admin verb that
// will provision / verify / replace / roll back / remove the machine-scoped
// organization Chef's Keys inventory that the Cycle-6 reader now reads. NOTHING
// in this cycle executes. There is:
//   * NO elevated executable, UAC prompt, or process launch.
//   * NO ProgramData read/write/create/delete.
//   * NO ACL read or mutation.
//   * NO registry write, service, certificate-store, or private-key access.
//   * NO Graph/tenant/cloud/Entra/auth/WAM/Hello access.
//   * NO Recipe/Cook/PaxAdapter/PAX/Bake surface.
//   * NO active Setup verb, broker route, or React invocation.
//   * NO runtime call site anywhere: only tests reference these types.
// The planner computes IMMUTABLE plans/rejections from SYNTHETIC inputs only; it
// derives NO real path and interprets NO command. Every plan action is a symbolic
// enum pair (action kind + symbolic artifact identity) with an optional target
// generation — never a path, command, argument, or executable string.
//
// PLACEMENT. Compiled into PAXCookbook.Shared (which Setup references) so a FUTURE
// cycle can consume it from an elevated Setup verb. It depends only on System,
// System.Collections.Generic, System.Text, and System.Text.Json — NO Win32, NO
// SecurityIdentifier, NO certificate/network/filesystem APIs — so it stays
// portable under Shared's net8.0 target. Principals are modeled as WELL-KNOWN SID
// STRING CONSTANTS (e.g. "S-1-5-18", "S-1-5-32-544"), never the Windows-only
// System.Security.Principal.SecurityIdentifier type. The ACL descriptor's trusted
// owner/write set aligns EXACTLY with the Cycle-6 reader's closed-SID policy
// (owner LocalSystem S-1-5-18 or BuiltinAdministrators S-1-5-32-544; broad
// principals never write-capable; no localized names).
//
// FAIL-CLOSED DOCTRINE (binding). An invalid/oversized/foreign/ambiguous/
// untrusted input NEVER yields a mutating plan or a success projection. Ownership
// is proven ONLY by the closed on-"disk" predicate (trusted owner + trusted ACL +
// no reparse + contained path + exact valid ledger + inventory/ledger hash
// agreement) — NEVER by a caller-supplied name, path, or hash alone (the request
// has no such field). Foreign artifacts are never overwritten or removed.

// ---------------------------------------------------------------------------
// Bounded limits, tokens, and closed allow/deny sets. Every constant is a
// compile-time boundary; there is no runtime override, environment variable,
// command-line switch, or user/HTTP-supplied configuration that can widen any.
// ---------------------------------------------------------------------------
public static class ProvisioningContract
{
    // The only representable request/ledger schema version.
    public const int RequestSchemaVersion = 1;
    public const int LedgerSchemaVersion = 1;

    // The current ACL policy version stamped into the ledger.
    public const int AclPolicyVersion = 1;

    // Raw request bytes above this (UTF-8, measured BEFORE any JSON parse) are
    // rejected outright. It exceeds the inventory bound so a maximal inventory
    // plus the small request envelope still fits, but a runaway blob does not.
    public const int MaxRequestBytes = 262144;

    // operationId charset is [A-Za-z0-9._-]; its length is bounded to this.
    public const int MaxOperationIdLength = 64;

    // A generic bounded-string cap for ledger string fields (RFC3339 stamps,
    // marker, feature id, lastOperationId).
    public const int MaxLedgerStringLength = 128;

    // The exact, immutable product ownership marker recorded in the ledger. A
    // ledger without this exact marker can never prove product ownership.
    public const string ProductOwnershipMarker = "PAXCookbook.ManagedChefKeys.v1";

    // The exact managed-feature identity the ledger governs.
    public const string ManagedFeatureId = "organization-key-inventory";

    // The EXACT owned artifact file names (the only files the future helper may
    // create/replace/remove). A ledger whose ownedFileNames differs is invalid.
    public const string InventoryFileName = "organization-key-inventory.json";
    public const string LedgerFileName = "ownership-ledger.json";

    // Cycle-8 amendment (Brian Option A): EXACTLY ONE previous-generation owned
    // artifact so a rollback can restore the exact prior inventory bytes from the
    // machine itself (never from caller-supplied bytes). AT MOST one previous
    // generation is ever retained; there is no second backup generation, backup
    // directory, caller-selectable name, or unbounded history.
    public const string PreviousInventoryFileName = "organization-key-inventory.prev.json";

    // The three canonical owned file names the feature owns. The ledger's
    // ownedFileNames ALWAYS lists all three (the SET the feature owns); PRESENCE
    // of the previous-inventory FILE is governed by the ledger's previousGeneration
    // (absent at generation 1, present at generation >= 2). This expresses the
    // no-prev(gen 1) vs prev-present(gen >= 2) distinction without a schema bump.
    public static readonly IReadOnlyList<string> OwnedFileNames =
        new[] { InventoryFileName, LedgerFileName, PreviousInventoryFileName };

    // Exactly the permitted top-level request property names.
    internal static readonly HashSet<string> AllowedRequestKeys =
        new(StringComparer.Ordinal)
        {
            "requestSchemaVersion", "operation", "operationId", "inventoryDocument",
            "expectedCurrentInventorySha256", "expectedLedgerGeneration",
            "expectedCurrentLedgerSha256", "planOnly",
        };

    // Keys that reject the WHOLE request even when present with a null/empty
    // value (ordinal-ignore-case). They name secret material, credential/vault
    // targets, filesystem/registry paths, service/command/executable surfaces,
    // cloud/Graph/auth surfaces, and Recipe/Cook/Bake/PAX wiring — none of which a
    // provisioning request may ever carry. Their presence means the caller is
    // trying to smuggle arbitrary authority across the boundary, so the request
    // fails closed before any plan.
    internal static readonly HashSet<string> ProhibitedRequestKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "clientSecret", "secret", "certificate", "certificateBytes", "pfx",
            "privateKey", "privateKeyBytes", "token", "claim", "claims", "password",
            "upn", "account", "wamAccount", "credentialManagerTarget", "path",
            "filePath", "inventoryPath", "destination", "registryPath", "serviceName",
            "storeName", "certStore", "executable", "command", "script", "arguments",
            "args", "workingDirectory", "env", "environment", "dll", "uri",
            "graphEndpoint", "scope", "scopes", "recipe", "bake", "cookCommand",
            "paxArgs",
        };

    // Exactly the permitted top-level ledger property names.
    internal static readonly HashSet<string> AllowedLedgerKeys =
        new(StringComparer.Ordinal)
        {
            "ledgerSchemaVersion", "productOwnershipMarker", "managedFeatureId",
            "currentGeneration", "currentInventorySha256", "previousGeneration",
            "previousInventorySha256", "transactionState", "ownedFileNames",
            "aclPolicyVersion", "createdUtc", "updatedUtc", "lastOperationId",
        };

    internal static bool IsProhibitedRequestKey(string name) => ProhibitedRequestKeys.Contains(name);

    // A bounded operationId: 1..MaxOperationIdLength of [A-Za-z0-9._-] only.
    // Forbidding ':' and path/whitespace characters keeps it a plain token that
    // can never be interpreted as a path, namespace, or command fragment.
    internal static bool IsValidOperationId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id!.Length > MaxOperationIdLength)
        {
            return false;
        }
        foreach (char c in id)
        {
            bool ok = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '.' || c == '_' || c == '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    // A lowercase-or-uppercase 64-character hexadecimal SHA-256 string.
    internal static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }
        foreach (char c in value)
        {
            bool ok = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    internal static bool IsBoundedString(string? value, int max)
        => value is not null && value.Length <= max;
}

// ---------------------------------------------------------------------------
// Operation verbs. This closed enum makes every other verb (execute, run, copy,
// move, write-file, delete-file, set-acl, set-registry, install-service, invoke,
// custom, ...) STRUCTURALLY UNREPRESENTABLE.
// ---------------------------------------------------------------------------
public enum ProvisioningOperation
{
    Plan,
    Apply,
    Verify,
    Rollback,
    Remove,
}

public enum ProvisioningRequestOutcome
{
    Valid,
    Invalid,
}

// Bounded, content-free rejection tokens for request validation. None carries a
// raw value, identifier, path, secret, or exception text.
public enum ProvisioningRequestInvalidReason
{
    None,
    OversizedRequest,
    MalformedJson,
    UnsupportedSchema,
    MissingField,
    UnknownField,
    ProhibitedField,
    WrongType,
    DuplicateProperty,
    InvalidOperation,
    InvalidOperationId,
    OversizedField,
    EmptyValue,
    InvalidHashFormat,
    InvalidGeneration,
    InventoryRequiredForApply,
    InventoryNotAllowed,
    InvalidInventory,
}

// A validated provisioning request. It can be constructed ONLY by the validator
// (private ctor + internal factory), so an unvalidated request is unrepresentable
// downstream. It carries NOTHING beyond the bounded fields below: no path,
// command, secret, credential, account, or arbitrary field is representable.
public sealed class ProvisioningRequest
{
    private ProvisioningRequest(
        int requestSchemaVersion,
        ProvisioningOperation operation,
        string operationId,
        string inventoryDocument,
        string? expectedCurrentInventorySha256,
        int? expectedLedgerGeneration,
        string? expectedCurrentLedgerSha256,
        bool planOnly)
    {
        RequestSchemaVersion = requestSchemaVersion;
        Operation = operation;
        OperationId = operationId;
        InventoryDocument = inventoryDocument;
        ExpectedCurrentInventorySha256 = expectedCurrentInventorySha256;
        ExpectedLedgerGeneration = expectedLedgerGeneration;
        ExpectedCurrentLedgerSha256 = expectedCurrentLedgerSha256;
        PlanOnly = planOnly;
    }

    public int RequestSchemaVersion { get; }

    public ProvisioningOperation Operation { get; }

    public string OperationId { get; }

    // The proposed inventory bytes (already Cycle-5-parsed as Valid for Apply/
    // Plan). Empty for verify/remove/rollback. Never a path or command.
    public string InventoryDocument { get; }

    public string? ExpectedCurrentInventorySha256 { get; }

    public int? ExpectedLedgerGeneration { get; }

    public string? ExpectedCurrentLedgerSha256 { get; }

    // A pure dry-run marker: when true, the produced plan is advisory only and is
    // reported as non-mutating regardless of operation.
    public bool PlanOnly { get; }

    internal static ProvisioningRequest Create(
        int requestSchemaVersion,
        ProvisioningOperation operation,
        string operationId,
        string inventoryDocument,
        string? expectedCurrentInventorySha256,
        int? expectedLedgerGeneration,
        string? expectedCurrentLedgerSha256,
        bool planOnly)
        => new(requestSchemaVersion, operation, operationId, inventoryDocument,
               expectedCurrentInventorySha256, expectedLedgerGeneration,
               expectedCurrentLedgerSha256, planOnly);
}

// The immutable, bounded result of validating a request JSON envelope.
public sealed class ProvisioningRequestValidationResult
{
    private ProvisioningRequestValidationResult(
        ProvisioningRequestOutcome outcome,
        ProvisioningRequestInvalidReason reason,
        ProvisioningRequest? request)
    {
        Outcome = outcome;
        Reason = reason;
        Request = request;
    }

    public ProvisioningRequestOutcome Outcome { get; }

    public bool IsValid => Outcome == ProvisioningRequestOutcome.Valid;

    public ProvisioningRequestInvalidReason Reason { get; }

    public ProvisioningRequest? Request { get; }

    internal static ProvisioningRequestValidationResult Valid(ProvisioningRequest request)
        => new(ProvisioningRequestOutcome.Valid, ProvisioningRequestInvalidReason.None, request);

    internal static ProvisioningRequestValidationResult Invalid(ProvisioningRequestInvalidReason reason)
        => new(ProvisioningRequestOutcome.Invalid, reason, null);
}

// The pure, deterministic, side-effect-free request validator. It reads only the
// string it is given, never touches the filesystem/registry/network/certificate
// store/credential vault, never throws through the boundary, and fails closed on
// the first violation. It NEVER echoes raw content, an identifier, or an
// exception across the boundary.
public static class ProvisioningRequestValidator
{
    public static ProvisioningRequestValidationResult Validate(string? requestJson)
    {
        if (requestJson is null)
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.MalformedJson);
        }

        // Size guard runs BEFORE the JSON reader so an oversized envelope is never
        // parsed for content.
        if (Encoding.UTF8.GetByteCount(requestJson) > ProvisioningContract.MaxRequestBytes)
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.OversizedRequest);
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(requestJson);
        }
        catch (JsonException)
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.MalformedJson);
        }

        using (parsed)
        {
            try
            {
                return ValidateRoot(parsed.RootElement);
            }
            catch (Exception)
            {
                return ProvisioningRequestValidationResult.Invalid(
                    ProvisioningRequestInvalidReason.MalformedJson);
            }
        }
    }

    private static ProvisioningRequestValidationResult ValidateRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.MalformedJson);
        }

        // Duplicate property names reject the request.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in root.EnumerateObject())
        {
            if (!seen.Add(p.Name))
            {
                return ProvisioningRequestValidationResult.Invalid(
                    ProvisioningRequestInvalidReason.DuplicateProperty);
            }
        }

        // Strict allow-list; a prohibited key is reported distinctly (and rejects
        // even when null/empty), an unrecognized key is UnknownField.
        foreach (JsonProperty p in root.EnumerateObject())
        {
            if (ProvisioningContract.AllowedRequestKeys.Contains(p.Name))
            {
                continue;
            }
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningContract.IsProhibitedRequestKey(p.Name)
                    ? ProvisioningRequestInvalidReason.ProhibitedField
                    : ProvisioningRequestInvalidReason.UnknownField);
        }

        // requestSchemaVersion (== 1).
        if (!root.TryGetProperty("requestSchemaVersion", out JsonElement schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out int schemaValue)
            || schemaValue != ProvisioningContract.RequestSchemaVersion)
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.UnsupportedSchema);
        }

        // operation (closed enum via a fixed token map).
        if (!root.TryGetProperty("operation", out JsonElement opElement)
            || opElement.ValueKind != JsonValueKind.String)
        {
            return ProvisioningRequestValidationResult.Invalid(
                opElement.ValueKind == JsonValueKind.Undefined
                    ? ProvisioningRequestInvalidReason.MissingField
                    : ProvisioningRequestInvalidReason.WrongType);
        }
        if (!TryMapOperation(opElement.GetString(), out ProvisioningOperation operation))
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.InvalidOperation);
        }

        // operationId (bounded charset + length).
        if (!root.TryGetProperty("operationId", out JsonElement opIdElement))
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.MissingField);
        }
        if (opIdElement.ValueKind != JsonValueKind.String)
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.WrongType);
        }
        string? operationId = opIdElement.GetString();
        if (!ProvisioningContract.IsValidOperationId(operationId))
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.InvalidOperationId);
        }

        // inventoryDocument (bounded string; presence required as a field, may be
        // empty for non-apply operations).
        if (!root.TryGetProperty("inventoryDocument", out JsonElement invElement))
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.MissingField);
        }
        if (invElement.ValueKind != JsonValueKind.String)
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.WrongType);
        }
        string inventoryDocument = invElement.GetString() ?? string.Empty;

        // Optional expected hashes / generation.
        string? expectedInvHash = null;
        if (root.TryGetProperty("expectedCurrentInventorySha256", out JsonElement eih)
            && eih.ValueKind != JsonValueKind.Null)
        {
            if (eih.ValueKind != JsonValueKind.String)
            {
                return ProvisioningRequestValidationResult.Invalid(
                    ProvisioningRequestInvalidReason.WrongType);
            }
            expectedInvHash = eih.GetString();
            if (!ProvisioningContract.IsSha256Hex(expectedInvHash))
            {
                return ProvisioningRequestValidationResult.Invalid(
                    ProvisioningRequestInvalidReason.InvalidHashFormat);
            }
        }

        int? expectedGeneration = null;
        if (root.TryGetProperty("expectedLedgerGeneration", out JsonElement egen)
            && egen.ValueKind != JsonValueKind.Null)
        {
            if (egen.ValueKind != JsonValueKind.Number || !egen.TryGetInt32(out int genValue))
            {
                return ProvisioningRequestValidationResult.Invalid(
                    ProvisioningRequestInvalidReason.WrongType);
            }
            if (genValue < 1)
            {
                return ProvisioningRequestValidationResult.Invalid(
                    ProvisioningRequestInvalidReason.InvalidGeneration);
            }
            expectedGeneration = genValue;
        }

        string? expectedLedgerHash = null;
        if (root.TryGetProperty("expectedCurrentLedgerSha256", out JsonElement elh)
            && elh.ValueKind != JsonValueKind.Null)
        {
            if (elh.ValueKind != JsonValueKind.String)
            {
                return ProvisioningRequestValidationResult.Invalid(
                    ProvisioningRequestInvalidReason.WrongType);
            }
            expectedLedgerHash = elh.GetString();
            if (!ProvisioningContract.IsSha256Hex(expectedLedgerHash))
            {
                return ProvisioningRequestValidationResult.Invalid(
                    ProvisioningRequestInvalidReason.InvalidHashFormat);
            }
        }

        // planOnly (bool; optional, default false).
        bool planOnly = false;
        if (root.TryGetProperty("planOnly", out JsonElement planOnlyElement)
            && planOnlyElement.ValueKind != JsonValueKind.Null)
        {
            if (planOnlyElement.ValueKind != JsonValueKind.True
                && planOnlyElement.ValueKind != JsonValueKind.False)
            {
                return ProvisioningRequestValidationResult.Invalid(
                    ProvisioningRequestInvalidReason.WrongType);
            }
            planOnly = planOnlyElement.GetBoolean();
        }

        // Inventory presence rules by operation. Apply/Plan MUST carry a
        // non-empty inventory that passes the Cycle-5 parser BEFORE any plan;
        // verify/remove/rollback MUST NOT carry inventory bytes (the helper never
        // trusts caller bytes for those).
        bool inventoryProvided = inventoryDocument.Length > 0;
        if (operation is ProvisioningOperation.Apply or ProvisioningOperation.Plan)
        {
            if (!inventoryProvided)
            {
                return ProvisioningRequestValidationResult.Invalid(
                    ProvisioningRequestInvalidReason.InventoryRequiredForApply);
            }
            OrganizationKeyInventoryParseResult inv =
                OrganizationKeyInventoryParser.Parse(inventoryDocument);
            if (!inv.IsValid)
            {
                return ProvisioningRequestValidationResult.Invalid(
                    ProvisioningRequestInvalidReason.InvalidInventory);
            }
        }
        else if (inventoryProvided)
        {
            return ProvisioningRequestValidationResult.Invalid(
                ProvisioningRequestInvalidReason.InventoryNotAllowed);
        }

        ProvisioningRequest request = ProvisioningRequest.Create(
            schemaValue, operation, operationId!, inventoryDocument,
            expectedInvHash, expectedGeneration, expectedLedgerHash, planOnly);
        return ProvisioningRequestValidationResult.Valid(request);
    }

    private static bool TryMapOperation(string? token, out ProvisioningOperation operation)
    {
        switch (token)
        {
            case "plan": operation = ProvisioningOperation.Plan; return true;
            case "apply": operation = ProvisioningOperation.Apply; return true;
            case "verify": operation = ProvisioningOperation.Verify; return true;
            case "rollback": operation = ProvisioningOperation.Rollback; return true;
            case "remove": operation = ProvisioningOperation.Remove; return true;
            default: operation = ProvisioningOperation.Plan; return false;
        }
    }
}

// ---------------------------------------------------------------------------
// Ownership ledger (versioned, crash-consistent). Records ONLY bounded non-secret
// provenance: no inventory content, org-key metadata, tenant/client/certificate
// identifier, thumbprint, account, secret, token, claim, arbitrary path, display
// name, or executable is representable.
// ---------------------------------------------------------------------------
public enum ProvisioningTransactionState
{
    Idle,
    Preparing,
    InventoryCommitted,
    LedgerCommitted,
    Done,
}

public enum OwnershipLedgerOutcome
{
    Valid,
    Invalid,
}

public enum OwnershipLedgerInvalidReason
{
    None,
    MalformedJson,
    OversizedField,
    UnsupportedSchema,
    UnknownField,
    ProhibitedField,
    MissingField,
    WrongType,
    DuplicateProperty,
    WrongMarker,
    WrongFeatureId,
    WrongOwnedFileNames,
    InvalidHashFormat,
    InvalidGeneration,
    NonMonotonicGeneration,
    InvalidTransactionState,
    InvalidAclPolicyVersion,
    InvalidTimestamp,
    InvalidOperationId,
}

public sealed class OwnershipLedger
{
    public OwnershipLedger(
        int ledgerSchemaVersion,
        string productOwnershipMarker,
        string managedFeatureId,
        int currentGeneration,
        string currentInventorySha256,
        int? previousGeneration,
        string? previousInventorySha256,
        ProvisioningTransactionState transactionState,
        IReadOnlyList<string> ownedFileNames,
        int aclPolicyVersion,
        string createdUtc,
        string updatedUtc,
        string lastOperationId)
    {
        LedgerSchemaVersion = ledgerSchemaVersion;
        ProductOwnershipMarker = productOwnershipMarker;
        ManagedFeatureId = managedFeatureId;
        CurrentGeneration = currentGeneration;
        CurrentInventorySha256 = currentInventorySha256;
        PreviousGeneration = previousGeneration;
        PreviousInventorySha256 = previousInventorySha256;
        TransactionState = transactionState;
        OwnedFileNames = ownedFileNames;
        AclPolicyVersion = aclPolicyVersion;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        LastOperationId = lastOperationId;
    }

    public int LedgerSchemaVersion { get; }

    public string ProductOwnershipMarker { get; }

    public string ManagedFeatureId { get; }

    public int CurrentGeneration { get; }

    public string CurrentInventorySha256 { get; }

    public int? PreviousGeneration { get; }

    public string? PreviousInventorySha256 { get; }

    public ProvisioningTransactionState TransactionState { get; }

    public IReadOnlyList<string> OwnedFileNames { get; }

    public int AclPolicyVersion { get; }

    public string CreatedUtc { get; }

    public string UpdatedUtc { get; }

    public string LastOperationId { get; }

    // True only when the ledger records a completed, quiescent generation.
    public bool IsQuiescent => TransactionState is ProvisioningTransactionState.Idle
        or ProvisioningTransactionState.Done;

    // True when the ledger records an in-progress transaction (crash window).
    public bool IsInProgress => TransactionState is ProvisioningTransactionState.Preparing
        or ProvisioningTransactionState.InventoryCommitted
        or ProvisioningTransactionState.LedgerCommitted;
}

public sealed class OwnershipLedgerValidationResult
{
    private OwnershipLedgerValidationResult(
        OwnershipLedgerOutcome outcome,
        OwnershipLedgerInvalidReason reason,
        OwnershipLedger? ledger)
    {
        Outcome = outcome;
        Reason = reason;
        Ledger = ledger;
    }

    public OwnershipLedgerOutcome Outcome { get; }

    public bool IsValid => Outcome == OwnershipLedgerOutcome.Valid;

    public OwnershipLedgerInvalidReason Reason { get; }

    public OwnershipLedger? Ledger { get; }

    internal static OwnershipLedgerValidationResult Valid(OwnershipLedger ledger)
        => new(OwnershipLedgerOutcome.Valid, OwnershipLedgerInvalidReason.None, ledger);

    internal static OwnershipLedgerValidationResult Invalid(OwnershipLedgerInvalidReason reason)
        => new(OwnershipLedgerOutcome.Invalid, reason, null);
}

// The pure, deterministic ledger validator. It validates either a parsed
// OwnershipLedger instance or a raw ledger JSON string; both paths enforce the
// same closed schema, marker, feature-id, owned-file-name, hash-hex, generation-
// monotonicity, transaction-state, and unknown-field rules with bounded reasons.
public static class OwnershipLedgerValidator
{
    public static OwnershipLedgerValidationResult Validate(OwnershipLedger? ledger)
    {
        if (ledger is null)
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.MissingField);
        }

        if (ledger.LedgerSchemaVersion != ProvisioningContract.LedgerSchemaVersion)
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.UnsupportedSchema);
        }
        if (!string.Equals(ledger.ProductOwnershipMarker, ProvisioningContract.ProductOwnershipMarker, StringComparison.Ordinal))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.WrongMarker);
        }
        if (!string.Equals(ledger.ManagedFeatureId, ProvisioningContract.ManagedFeatureId, StringComparison.Ordinal))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.WrongFeatureId);
        }
        if (!OwnedFileNamesExact(ledger.OwnedFileNames))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.WrongOwnedFileNames);
        }
        if (ledger.CurrentGeneration < 1)
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.InvalidGeneration);
        }
        if (!ProvisioningContract.IsSha256Hex(ledger.CurrentInventorySha256))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.InvalidHashFormat);
        }
        if (ledger.PreviousGeneration is int prevGen)
        {
            if (prevGen < 1 || prevGen >= ledger.CurrentGeneration)
            {
                return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.NonMonotonicGeneration);
            }
            if (!ProvisioningContract.IsSha256Hex(ledger.PreviousInventorySha256))
            {
                return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.InvalidHashFormat);
            }
        }
        else if (ledger.PreviousInventorySha256 is not null)
        {
            // A previous hash without a previous generation is inconsistent.
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.NonMonotonicGeneration);
        }
        if (!Enum.IsDefined(typeof(ProvisioningTransactionState), ledger.TransactionState))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.InvalidTransactionState);
        }
        if (ledger.AclPolicyVersion < 1)
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.InvalidAclPolicyVersion);
        }
        if (!ProvisioningContract.IsBoundedString(ledger.CreatedUtc, ProvisioningContract.MaxLedgerStringLength)
            || !IsRfc3339(ledger.CreatedUtc))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.InvalidTimestamp);
        }
        if (!ProvisioningContract.IsBoundedString(ledger.UpdatedUtc, ProvisioningContract.MaxLedgerStringLength)
            || !IsRfc3339(ledger.UpdatedUtc))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.InvalidTimestamp);
        }
        if (!ProvisioningContract.IsValidOperationId(ledger.LastOperationId))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.InvalidOperationId);
        }

        return OwnershipLedgerValidationResult.Valid(ledger);
    }

    public static OwnershipLedgerValidationResult Validate(string? ledgerJson)
    {
        if (ledgerJson is null)
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.MalformedJson);
        }
        if (Encoding.UTF8.GetByteCount(ledgerJson) > ProvisioningContract.MaxRequestBytes)
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.OversizedField);
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(ledgerJson);
        }
        catch (JsonException)
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.MalformedJson);
        }

        using (parsed)
        {
            try
            {
                return ValidateLedgerElement(parsed.RootElement);
            }
            catch (Exception)
            {
                return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.MalformedJson);
            }
        }
    }

    private static OwnershipLedgerValidationResult ValidateLedgerElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.MalformedJson);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in root.EnumerateObject())
        {
            if (!seen.Add(p.Name))
            {
                return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.DuplicateProperty);
            }
        }
        foreach (JsonProperty p in root.EnumerateObject())
        {
            if (ProvisioningContract.AllowedLedgerKeys.Contains(p.Name))
            {
                continue;
            }
            return OwnershipLedgerValidationResult.Invalid(
                ProvisioningContract.IsProhibitedRequestKey(p.Name)
                    ? OwnershipLedgerInvalidReason.ProhibitedField
                    : OwnershipLedgerInvalidReason.UnknownField);
        }

        if (!TryInt(root, "ledgerSchemaVersion", out int schemaVersion))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.MissingField);
        }
        if (!TryString(root, "productOwnershipMarker", out string? marker)
            || !TryString(root, "managedFeatureId", out string? featureId)
            || !TryString(root, "currentInventorySha256", out string? curHash)
            || !TryString(root, "transactionState", out string? stateToken)
            || !TryString(root, "createdUtc", out string? createdUtc)
            || !TryString(root, "updatedUtc", out string? updatedUtc)
            || !TryString(root, "lastOperationId", out string? lastOpId))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.MissingField);
        }
        if (!TryInt(root, "currentGeneration", out int currentGeneration)
            || !TryInt(root, "aclPolicyVersion", out int aclPolicyVersion))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.MissingField);
        }

        int? previousGeneration = null;
        if (root.TryGetProperty("previousGeneration", out JsonElement pgen)
            && pgen.ValueKind != JsonValueKind.Null)
        {
            if (pgen.ValueKind != JsonValueKind.Number || !pgen.TryGetInt32(out int pg))
            {
                return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.WrongType);
            }
            previousGeneration = pg;
        }
        string? previousHash = null;
        if (root.TryGetProperty("previousInventorySha256", out JsonElement phash)
            && phash.ValueKind != JsonValueKind.Null)
        {
            if (phash.ValueKind != JsonValueKind.String)
            {
                return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.WrongType);
            }
            previousHash = phash.GetString();
        }

        if (!root.TryGetProperty("ownedFileNames", out JsonElement ownedElement)
            || ownedElement.ValueKind != JsonValueKind.Array)
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.WrongOwnedFileNames);
        }
        var owned = new List<string>();
        foreach (JsonElement item in ownedElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.WrongOwnedFileNames);
            }
            owned.Add(item.GetString()!);
        }

        if (!TryMapTransactionState(stateToken, out ProvisioningTransactionState state))
        {
            return OwnershipLedgerValidationResult.Invalid(OwnershipLedgerInvalidReason.InvalidTransactionState);
        }

        var ledger = new OwnershipLedger(
            schemaVersion, marker!, featureId!, currentGeneration, curHash!,
            previousGeneration, previousHash, state, owned, aclPolicyVersion,
            createdUtc!, updatedUtc!, lastOpId!);
        return Validate(ledger);
    }

    private static bool OwnedFileNamesExact(IReadOnlyList<string> names)
    {
        if (names is null || names.Count != ProvisioningContract.OwnedFileNames.Count)
        {
            return false;
        }
        // Order-independent exact set match against the fixed allow-list.
        var required = new HashSet<string>(ProvisioningContract.OwnedFileNames, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string n in names)
        {
            if (!required.Contains(n) || !seen.Add(n))
            {
                return false;
            }
        }
        return seen.Count == required.Count;
    }

    private static bool TryString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out JsonElement e) || e.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = e.GetString();
        return value is not null;
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement e)
            && e.ValueKind == JsonValueKind.Number
            && e.TryGetInt32(out value);
    }

    private static bool TryMapTransactionState(string? token, out ProvisioningTransactionState state)
    {
        switch (token)
        {
            case "idle": state = ProvisioningTransactionState.Idle; return true;
            case "preparing": state = ProvisioningTransactionState.Preparing; return true;
            case "inventoryCommitted": state = ProvisioningTransactionState.InventoryCommitted; return true;
            case "ledgerCommitted": state = ProvisioningTransactionState.LedgerCommitted; return true;
            case "done": state = ProvisioningTransactionState.Done; return true;
            default: state = ProvisioningTransactionState.Idle; return false;
        }
    }

    // A minimal, bounded RFC3339 UTC check: "yyyy-MM-ddTHH:mm:ssZ" shape. This is
    // a shape gate only; it never parses a locale or timezone database.
    private static bool IsRfc3339(string? value)
    {
        if (value is null || value.Length < 20 || value.Length > ProvisioningContract.MaxLedgerStringLength)
        {
            return false;
        }
        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out _);
    }
}

// ---------------------------------------------------------------------------
// ACL descriptor (SID STRING constants, portable). Models the DACL the future
// helper would stamp. Never a SecurityIdentifier, never a localized name.
// ---------------------------------------------------------------------------
public enum AclAccess
{
    Read,
    Write,
}

public sealed class AclEntry
{
    public AclEntry(string sid, AclAccess access)
    {
        Sid = sid;
        Access = access;
    }

    public string Sid { get; }

    public AclAccess Access { get; }
}

public enum AclDescriptorOutcome
{
    Valid,
    Invalid,
}

public enum AclDescriptorInvalidReason
{
    None,
    OwnerNotTrusted,
    DaclNotProtected,
    BroadWriteGranted,
    NonTrustedWriteGranted,
    LocalizedNameNotAllowed,
    EmptyDacl,
}

public sealed class AclDescriptorValidationResult
{
    private AclDescriptorValidationResult(AclDescriptorOutcome outcome, AclDescriptorInvalidReason reason)
    {
        Outcome = outcome;
        Reason = reason;
    }

    public AclDescriptorOutcome Outcome { get; }

    public bool IsValid => Outcome == AclDescriptorOutcome.Valid;

    public AclDescriptorInvalidReason Reason { get; }

    internal static AclDescriptorValidationResult Valid()
        => new(AclDescriptorOutcome.Valid, AclDescriptorInvalidReason.None);

    internal static AclDescriptorValidationResult Invalid(AclDescriptorInvalidReason reason)
        => new(AclDescriptorOutcome.Invalid, reason);
}

// The declarative ACL descriptor. The trusted owner/write sets align EXACTLY with
// the Cycle-6 reader's closed-SID policy: owner must be LocalSystem (S-1-5-18) or
// BuiltinAdministrators (S-1-5-32-544); ONLY those two are write-capable; broad
// principals may receive read but never write; the DACL must be protected
// (non-inheriting) so an inherited broad write ACE can never apply.
public sealed class AclDescriptor
{
    public const string SidLocalSystem = "S-1-5-18";
    public const string SidBuiltinAdministrators = "S-1-5-32-544";
    public const string SidEveryone = "S-1-1-0";
    public const string SidAuthenticatedUsers = "S-1-5-11";
    public const string SidUsers = "S-1-5-32-545";
    public const string SidInteractive = "S-1-5-4";

    // The exact required owner allow-list (well-known SID strings only).
    public static readonly IReadOnlyList<string> RequiredOwnerSids =
        new[] { SidLocalSystem, SidBuiltinAdministrators };

    // The exact write-capable allow-list (identical to the owner allow-list).
    public static readonly IReadOnlyList<string> WriteCapableSids =
        new[] { SidLocalSystem, SidBuiltinAdministrators };

    // The broad principals that must NEVER be write-capable. Enumerated so a
    // broad-write attempt is reported distinctly.
    public static readonly IReadOnlyList<string> BroadSids =
        new[] { SidEveryone, SidAuthenticatedUsers, SidUsers, SidInteractive };

    internal static readonly HashSet<string> TrustedSet =
        new(StringComparer.Ordinal) { SidLocalSystem, SidBuiltinAdministrators };

    internal static readonly HashSet<string> BroadSet =
        new(StringComparer.Ordinal) { SidEveryone, SidAuthenticatedUsers, SidUsers, SidInteractive };

    public AclDescriptor(string ownerSid, IReadOnlyList<AclEntry> entries, bool protectedDacl)
    {
        OwnerSid = ownerSid;
        Entries = entries;
        ProtectedDacl = protectedDacl;
    }

    public string OwnerSid { get; }

    public IReadOnlyList<AclEntry> Entries { get; }

    // A protected DACL does not inherit ACEs from its parent, so no broad
    // inherited write ACE can leak in.
    public bool ProtectedDacl { get; }
}

// The pure ACL descriptor validator. It accepts only well-known SID STRING
// principals; it never resolves, localizes, or looks up an account name. A caller
// cannot supply an owner or ACE that widens write beyond the two trusted SIDs.
public static class AclDescriptorValidator
{
    public static AclDescriptorValidationResult Validate(AclDescriptor? descriptor)
    {
        if (descriptor is null || descriptor.Entries is null || descriptor.Entries.Count == 0)
        {
            return AclDescriptorValidationResult.Invalid(AclDescriptorInvalidReason.EmptyDacl);
        }

        // The DACL must be protected so an inherited broad write ACE cannot apply.
        if (!descriptor.ProtectedDacl)
        {
            return AclDescriptorValidationResult.Invalid(AclDescriptorInvalidReason.DaclNotProtected);
        }

        // Owner must be a plain well-known SID string and exactly one of the two
        // trusted owners.
        if (!IsWellKnownSidString(descriptor.OwnerSid))
        {
            return AclDescriptorValidationResult.Invalid(AclDescriptorInvalidReason.LocalizedNameNotAllowed);
        }
        if (!AclDescriptor.TrustedSet.Contains(descriptor.OwnerSid))
        {
            return AclDescriptorValidationResult.Invalid(AclDescriptorInvalidReason.OwnerNotTrusted);
        }

        foreach (AclEntry entry in descriptor.Entries)
        {
            if (!IsWellKnownSidString(entry.Sid))
            {
                return AclDescriptorValidationResult.Invalid(AclDescriptorInvalidReason.LocalizedNameNotAllowed);
            }
            if (entry.Access == AclAccess.Write && !AclDescriptor.TrustedSet.Contains(entry.Sid))
            {
                // A broad SID getting write is reported distinctly from any other
                // non-trusted (ordinary/current/unknown) SID getting write.
                return AclDescriptorValidationResult.Invalid(
                    AclDescriptor.BroadSet.Contains(entry.Sid)
                        ? AclDescriptorInvalidReason.BroadWriteGranted
                        : AclDescriptorInvalidReason.NonTrustedWriteGranted);
            }
        }

        return AclDescriptorValidationResult.Valid();
    }

    // A well-known SID string is "S-1-" followed by digits and hyphens only. A
    // localized or account name (e.g. "Administrators", "Everyone") is rejected,
    // preventing any name-based principal from entering the ACL model.
    private static bool IsWellKnownSidString(string? value)
    {
        if (value is null || value.Length < 4 || !value.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (char c in value)
        {
            bool ok = (c >= '0' && c <= '9') || c == '-' || c == 'S';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }
}

// ---------------------------------------------------------------------------
// Symbolic operation plan. Every action is a (kind, artifact, targetGeneration?)
// triple; there is NO path, command, argument, or executable field anywhere.
// ---------------------------------------------------------------------------
public enum ProvisioningActionKind
{
    EnsureManagedDirectory,
    WriteTempInventory,
    ValidateTempInventory,
    ReplaceInventory,
    WriteTempPreviousInventory,
    ValidateTempPreviousInventory,
    ReplacePreviousInventory,
    WriteTempLedger,
    ValidateTempLedger,
    ReplaceLedger,
    VerifyInventoryLedgerPair,
    RestorePreviousGeneration,
    RemoveInventory,
    RemovePreviousInventory,
    RemoveLedger,
    RemoveTransactionArtifact,
    RemoveManagedDirectoryIfEmpty,
    StampAcl,
}

// Symbolic artifact identities. The future helper derives ALL real paths itself;
// the plan never carries a path string.
public enum ProvisioningArtifact
{
    ManagedDirectory,
    Inventory,
    Ledger,
    PreviousInventory,
    TempInventory,
    TempPreviousInventory,
    TempLedger,
    TransactionMarker,
    Acl,
}

public sealed class ProvisioningPlanAction
{
    public ProvisioningPlanAction(ProvisioningActionKind kind, ProvisioningArtifact artifact, int? targetGeneration = null)
    {
        Kind = kind;
        Artifact = artifact;
        TargetGeneration = targetGeneration;
    }

    public ProvisioningActionKind Kind { get; }

    public ProvisioningArtifact Artifact { get; }

    // An optional symbolic target generation (e.g. the new generation for a
    // replace, or the restore target for a rollback). Never a path.
    public int? TargetGeneration { get; }
}

public enum ProvisioningPlanOutcome
{
    Planned,
    Rejected,
}

// The projected terminal state a plan would achieve. None of these is a raw
// value; each is a bounded classification.
public enum ProvisioningPlanState
{
    None,
    NoOpUpToDate,
    CreatePlanned,
    ReplacePlanned,
    VerifyPlanned,
    RollbackPlanned,
    RemovePlanned,
    RecoveryPlanned,
}

// Bounded, content-free rejection tokens for the planner.
public enum ProvisioningRejectionReason
{
    None,
    RequestNotValidated,
    UntrustedEnvironment,
    ForeignArtifactPresent,
    LedgerInvalid,
    LedgerInventoryDisagree,
    StateMismatch,
    GenerationMismatch,
    OwnershipNotProven,
    UnknownSiblingArtifact,
    NoPreviousGeneration,
    PartialTransaction,
    NothingToRemove,
    UnsupportedOperation,
}

// The immutable plan. A planned outcome carries an ordered symbolic action list;
// a rejected outcome carries a bounded reason and an empty action list. WouldMutate
// is false for Plan-verb / planOnly / verify previews, and for a no-op.
public sealed class ProvisioningPlan
{
    private static readonly IReadOnlyList<ProvisioningPlanAction> NoActions =
        Array.Empty<ProvisioningPlanAction>();

    private ProvisioningPlan(
        ProvisioningPlanOutcome outcome,
        ProvisioningOperation operation,
        ProvisioningPlanState resultState,
        bool ownershipProven,
        bool wouldMutate,
        int targetGeneration,
        IReadOnlyList<ProvisioningPlanAction> actions,
        ProvisioningRejectionReason rejectionReason)
    {
        Outcome = outcome;
        Operation = operation;
        ResultState = resultState;
        OwnershipProven = ownershipProven;
        WouldMutate = wouldMutate;
        TargetGeneration = targetGeneration;
        Actions = actions;
        RejectionReason = rejectionReason;
    }

    public ProvisioningPlanOutcome Outcome { get; }

    public bool IsPlanned => Outcome == ProvisioningPlanOutcome.Planned;

    public ProvisioningOperation Operation { get; }

    public ProvisioningPlanState ResultState { get; }

    public bool OwnershipProven { get; }

    // Whether executing this plan (in a FUTURE cycle) would mutate the machine.
    // Always false for a Plan verb, a planOnly request, a verify, or a no-op.
    public bool WouldMutate { get; }

    // The symbolic generation the plan targets (0 when not applicable).
    public int TargetGeneration { get; }

    public IReadOnlyList<ProvisioningPlanAction> Actions { get; }

    public ProvisioningRejectionReason RejectionReason { get; }

    internal static ProvisioningPlan Planned(
        ProvisioningOperation operation,
        ProvisioningPlanState state,
        bool ownershipProven,
        bool wouldMutate,
        int targetGeneration,
        IReadOnlyList<ProvisioningPlanAction> actions)
        => new(ProvisioningPlanOutcome.Planned, operation, state, ownershipProven,
               wouldMutate, targetGeneration, actions, ProvisioningRejectionReason.None);

    internal static ProvisioningPlan Rejected(
        ProvisioningOperation operation,
        ProvisioningRejectionReason reason,
        bool ownershipProven)
        => new(ProvisioningPlanOutcome.Rejected, operation, ProvisioningPlanState.None,
               ownershipProven, false, 0, NoActions, reason);
}

// ---------------------------------------------------------------------------
// Synthetic planner inputs. These model, purely, what the future elevated helper
// WOULD observe on the machine. They carry NO real path, handle, or content — only
// bounded facts — so the planner is fully unit-testable with no I/O.
// ---------------------------------------------------------------------------
public sealed class SyntheticTrustState
{
    public SyntheticTrustState(
        bool managedDirectoryPresent,
        bool ownerTrusted,
        bool aclTrusted,
        bool noReparsePoint,
        bool pathContained)
    {
        ManagedDirectoryPresent = managedDirectoryPresent;
        OwnerTrusted = ownerTrusted;
        AclTrusted = aclTrusted;
        NoReparsePoint = noReparsePoint;
        PathContained = pathContained;
    }

    public bool ManagedDirectoryPresent { get; }

    // Owner is one of the trusted well-known SIDs (S-1-5-18 / S-1-5-32-544).
    public bool OwnerTrusted { get; }

    // No broad write-capable ACE; protected DACL.
    public bool AclTrusted { get; }

    public bool NoReparsePoint { get; }

    public bool PathContained { get; }

    // The trust preconditions that must hold before ANY mutating action.
    public bool IsTrusted => OwnerTrusted && AclTrusted && NoReparsePoint && PathContained;

    public static SyntheticTrustState FullyTrusted(bool managedDirectoryPresent = true)
        => new(managedDirectoryPresent, true, true, true, true);

    public static SyntheticTrustState AbsentDirectory()
        => new(false, true, true, true, true);

    public static SyntheticTrustState Untrusted()
        => new(true, false, false, true, true);
}

public enum ArtifactPresence
{
    Absent,
    Present,
}

// The synthetic current source snapshot: what inventory bytes (if any) sit in the
// managed directory, plus whether an unknown sibling artifact is present. Cycle-8
// adds the previous-generation ("prev") artifact facts so the planner can prove
// gen>=2 ownership and plan the reversible-swap rollback from real prior bytes.
public sealed class SyntheticSourceState
{
    public SyntheticSourceState(
        ArtifactPresence inventoryPresence,
        string? inventorySha256,
        bool unknownSiblingPresent,
        ArtifactPresence previousInventoryPresence = ArtifactPresence.Absent,
        string? previousInventorySha256 = null,
        bool previousInventoryParses = true)
    {
        InventoryPresence = inventoryPresence;
        InventorySha256 = inventorySha256;
        UnknownSiblingPresent = unknownSiblingPresent;
        PreviousInventoryPresence = previousInventoryPresence;
        PreviousInventorySha256 = previousInventorySha256;
        PreviousInventoryParses = previousInventoryParses;
    }

    public ArtifactPresence InventoryPresence { get; }

    // The actual SHA-256 of the on-"disk" inventory (null when absent).
    public string? InventorySha256 { get; }

    // A file in the managed directory that is not an allow-listed owned artifact.
    public bool UnknownSiblingPresent { get; }

    // The previous-generation backup artifact (organization-key-inventory.prev.json).
    public ArtifactPresence PreviousInventoryPresence { get; }

    // The actual SHA-256 of the on-"disk" previous-inventory backup (null when absent).
    public string? PreviousInventorySha256 { get; }

    // Whether the on-"disk" previous-inventory backup parses under the Cycle-5
    // parser (a corrupt/foreign backup can never prove ownership or be restored from).
    public bool PreviousInventoryParses { get; }

    public bool InventoryPresent => InventoryPresence == ArtifactPresence.Present;

    public bool PreviousInventoryPresent => PreviousInventoryPresence == ArtifactPresence.Present;

    public static SyntheticSourceState Absent()
        => new(ArtifactPresence.Absent, null, false);

    // A generation-1 owned source: current inventory present, NO previous backup.
    public static SyntheticSourceState Present(string inventorySha256, bool unknownSiblingPresent = false)
        => new(ArtifactPresence.Present, inventorySha256, unknownSiblingPresent);

    // A generation-(>=2) owned source: current inventory AND the previous backup
    // both present, each with its actual on-"disk" hash.
    public static SyntheticSourceState PresentWithPrevious(
        string inventorySha256,
        string previousInventorySha256,
        bool unknownSiblingPresent = false,
        bool previousInventoryParses = true)
        => new(ArtifactPresence.Present, inventorySha256, unknownSiblingPresent,
               ArtifactPresence.Present, previousInventorySha256, previousInventoryParses);
}

// The synthetic current ledger snapshot: whether a ledger is present and, if so,
// the parsed ledger and whether it validated.
public sealed class SyntheticLedgerState
{
    public SyntheticLedgerState(ArtifactPresence presence, OwnershipLedger? ledger, bool ledgerValid)
    {
        Presence = presence;
        Ledger = ledger;
        LedgerValid = ledgerValid;
    }

    public ArtifactPresence Presence { get; }

    public OwnershipLedger? Ledger { get; }

    public bool LedgerValid { get; }

    public bool LedgerPresent => Presence == ArtifactPresence.Present;

    public static SyntheticLedgerState Absent()
        => new(ArtifactPresence.Absent, null, false);

    public static SyntheticLedgerState Present(OwnershipLedger ledger, bool ledgerValid)
        => new(ArtifactPresence.Present, ledger, ledgerValid);
}

// ---------------------------------------------------------------------------
// The pure, I/O-free operation planner. It computes an immutable plan or a bounded
// rejection from a VALIDATED request plus synthetic state. It performs NO I/O,
// derives NO real path, and never mutates anything.
// ---------------------------------------------------------------------------
public static class ProvisioningPlanner
{
    public static ProvisioningPlan Plan(
        ProvisioningRequest? validatedRequest,
        SyntheticSourceState syntheticCurrentSourceState,
        SyntheticLedgerState syntheticCurrentLedgerState,
        SyntheticTrustState syntheticTrustState)
    {
        if (validatedRequest is null)
        {
            return ProvisioningPlan.Rejected(
                ProvisioningOperation.Plan, ProvisioningRejectionReason.RequestNotValidated, false);
        }
        if (syntheticCurrentSourceState is null || syntheticCurrentLedgerState is null || syntheticTrustState is null)
        {
            return ProvisioningPlan.Rejected(
                validatedRequest.Operation, ProvisioningRejectionReason.RequestNotValidated, false);
        }

        ProvisioningOperation op = validatedRequest.Operation;

        // Ownership is proven ONLY by the closed on-"disk" predicate; a caller can
        // never assert it. Computed once, up front, for every operation.
        bool ownershipProven = IsOwnershipProven(
            syntheticCurrentSourceState, syntheticCurrentLedgerState, syntheticTrustState);

        // A crash-window (in-progress transaction) must be resolved by a bounded
        // recovery plan before any normal operation, and ONLY under ownership
        // proof. An ambiguous/foreign in-progress state fails closed.
        if (syntheticCurrentLedgerState.LedgerPresent
            && syntheticCurrentLedgerState.LedgerValid
            && syntheticCurrentLedgerState.Ledger is { IsInProgress: true })
        {
            return PlanRecovery(op, ownershipProven, syntheticCurrentLedgerState.Ledger!);
        }

        return op switch
        {
            ProvisioningOperation.Verify => PlanVerify(validatedRequest, ownershipProven),
            ProvisioningOperation.Plan => PlanApply(validatedRequest, syntheticCurrentSourceState, syntheticCurrentLedgerState, syntheticTrustState, ownershipProven, previewOnly: true),
            ProvisioningOperation.Apply => PlanApply(validatedRequest, syntheticCurrentSourceState, syntheticCurrentLedgerState, syntheticTrustState, ownershipProven, previewOnly: false),
            ProvisioningOperation.Rollback => PlanRollback(validatedRequest, syntheticCurrentSourceState, syntheticCurrentLedgerState, ownershipProven),
            ProvisioningOperation.Remove => PlanRemove(validatedRequest, syntheticCurrentSourceState, syntheticCurrentLedgerState, ownershipProven),
            _ => ProvisioningPlan.Rejected(op, ProvisioningRejectionReason.UnsupportedOperation, ownershipProven),
        };
    }

    // The closed ownership predicate. ALL must hold: trusted owner + ACL + no
    // reparse + contained path; a present, valid ledger; a present inventory; and
    // ledger.currentInventorySha256 == the actual on-"disk" inventory hash. Cycle-8
    // extends it with the generation-aware previous-artifact rule: at generation 1
    // there must be NO previous (ledger prev fields null AND no prev file); at
    // generation >= 2 the ledger must record previousGeneration == currentGeneration-1
    // (exactly one behind) with a 64-hex previous hash, and the prev file must be
    // PRESENT, PARSE (Cycle-5), and hash-match the ledger's previous hash. No
    // caller-supplied name/path/hash participates.
    private static bool IsOwnershipProven(
        SyntheticSourceState source, SyntheticLedgerState ledgerState, SyntheticTrustState trust)
    {
        if (!trust.IsTrusted)
        {
            return false;
        }
        if (!ledgerState.LedgerPresent || !ledgerState.LedgerValid || ledgerState.Ledger is null)
        {
            return false;
        }
        if (!source.InventoryPresent || source.InventorySha256 is null)
        {
            return false;
        }
        OwnershipLedger ledger = ledgerState.Ledger;
        if (!string.Equals(
                ledger.CurrentInventorySha256,
                source.InventorySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ledger.CurrentGeneration == 1)
        {
            // Generation 1: no previous generation. The ledger prev fields must be
            // null AND a prev file must be ABSENT (a prev file without a matching
            // later-generation ledger is foreign/ambiguous, never trusted).
            if (ledger.PreviousGeneration is not null || ledger.PreviousInventorySha256 is not null)
            {
                return false;
            }
            return !source.PreviousInventoryPresent;
        }

        // Generation >= 2: the previous generation must be exactly one behind and
        // the prev backup file must be present, parse, and hash-match the ledger.
        if (ledger.PreviousGeneration is not int prevGen
            || prevGen != ledger.CurrentGeneration - 1
            || ledger.PreviousInventorySha256 is null)
        {
            return false;
        }
        if (!source.PreviousInventoryPresent
            || source.PreviousInventorySha256 is null
            || !source.PreviousInventoryParses)
        {
            return false;
        }
        return string.Equals(
            ledger.PreviousInventorySha256,
            source.PreviousInventorySha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ProvisioningPlan PlanVerify(ProvisioningRequest request, bool ownershipProven)
    {
        // Verify is read-only: a single check action, never a write/replace/
        // remove/ACL action, and it never repairs.
        var actions = new List<ProvisioningPlanAction>
        {
            new(ProvisioningActionKind.VerifyInventoryLedgerPair, ProvisioningArtifact.Ledger),
        };
        return ProvisioningPlan.Planned(
            ProvisioningOperation.Verify, ProvisioningPlanState.VerifyPlanned,
            ownershipProven, wouldMutate: false, targetGeneration: 0, actions);
    }

    private static ProvisioningPlan PlanApply(
        ProvisioningRequest request,
        SyntheticSourceState source,
        SyntheticLedgerState ledgerState,
        SyntheticTrustState trust,
        bool ownershipProven,
        bool previewOnly)
    {
        ProvisioningOperation op = request.Operation;
        bool mutating = !previewOnly && !request.PlanOnly;

        // Trust preconditions must hold before any create/replace.
        if (!trust.IsTrusted)
        {
            return ProvisioningPlan.Rejected(op, ProvisioningRejectionReason.UntrustedEnvironment, ownershipProven);
        }

        bool inventoryPresent = source.InventoryPresent;
        bool ledgerPresent = ledgerState.LedgerPresent;

        // Case 1: safely absent -> create generation 1.
        if (!inventoryPresent && !ledgerPresent && !source.UnknownSiblingPresent)
        {
            var create = new List<ProvisioningPlanAction>
            {
                new(ProvisioningActionKind.EnsureManagedDirectory, ProvisioningArtifact.ManagedDirectory),
                new(ProvisioningActionKind.StampAcl, ProvisioningArtifact.Acl),
                new(ProvisioningActionKind.WriteTempInventory, ProvisioningArtifact.TempInventory, 1),
                new(ProvisioningActionKind.ValidateTempInventory, ProvisioningArtifact.TempInventory, 1),
                new(ProvisioningActionKind.ReplaceInventory, ProvisioningArtifact.Inventory, 1),
                new(ProvisioningActionKind.WriteTempLedger, ProvisioningArtifact.TempLedger, 1),
                new(ProvisioningActionKind.ValidateTempLedger, ProvisioningArtifact.TempLedger, 1),
                new(ProvisioningActionKind.ReplaceLedger, ProvisioningArtifact.Ledger, 1),
                new(ProvisioningActionKind.RemoveTransactionArtifact, ProvisioningArtifact.TransactionMarker, 1),
            };
            return ProvisioningPlan.Planned(op, ProvisioningPlanState.CreatePlanned, ownershipProven, mutating, 1, create);
        }

        // A foreign artifact (present without matching trusted ledger) or an
        // unknown sibling blocks apply. This also covers ledger-without-inventory
        // and inventory-without-ledger (disagreement) fail-closed cases.
        if (source.UnknownSiblingPresent)
        {
            return ProvisioningPlan.Rejected(op, ProvisioningRejectionReason.UnknownSiblingArtifact, ownershipProven);
        }
        if (!ownershipProven)
        {
            // Present but not provably owned: either a foreign file or an
            // inventory/ledger disagreement. Distinguish the two for the audit.
            if (inventoryPresent != ledgerPresent)
            {
                return ProvisioningPlan.Rejected(op, ProvisioningRejectionReason.LedgerInventoryDisagree, ownershipProven);
            }
            return ProvisioningPlan.Rejected(op, ProvisioningRejectionReason.ForeignArtifactPresent, ownershipProven);
        }

        // Ownership proven: optimistic-concurrency check against caller expected
        // generation/hashes before any replacement.
        OwnershipLedger current = ledgerState.Ledger!;
        if (request.ExpectedLedgerGeneration is int expGen && expGen != current.CurrentGeneration)
        {
            return ProvisioningPlan.Rejected(op, ProvisioningRejectionReason.GenerationMismatch, ownershipProven);
        }
        if (request.ExpectedCurrentInventorySha256 is string expInv
            && !string.Equals(expInv, current.CurrentInventorySha256, StringComparison.OrdinalIgnoreCase))
        {
            return ProvisioningPlan.Rejected(op, ProvisioningRejectionReason.StateMismatch, ownershipProven);
        }

        // Idempotence: identical proposed content -> bounded no-op.
        string proposedHash = ComputeSha256Hex(request.InventoryDocument);
        if (string.Equals(proposedHash, current.CurrentInventorySha256, StringComparison.OrdinalIgnoreCase))
        {
            var noop = new List<ProvisioningPlanAction>
            {
                new(ProvisioningActionKind.VerifyInventoryLedgerPair, ProvisioningArtifact.Ledger, current.CurrentGeneration),
            };
            return ProvisioningPlan.Planned(op, ProvisioningPlanState.NoOpUpToDate, ownershipProven, wouldMutate: false, current.CurrentGeneration, noop);
        }

        // Bounded replacement: new generation = current + 1. The previous
        // generation's inventory is retained as the single owned prev backup,
        // staged FROM the ownership-proven CURRENT inventory bytes (never caller
        // bytes). The new ledger records previousGeneration = current generation
        // and previousInventorySha256 = the current (soon-to-be-previous) hash.
        int nextGen = current.CurrentGeneration + 1;
        int backupGen = current.CurrentGeneration;
        var replace = new List<ProvisioningPlanAction>
        {
            new(ProvisioningActionKind.WriteTempInventory, ProvisioningArtifact.TempInventory, nextGen),
            new(ProvisioningActionKind.ValidateTempInventory, ProvisioningArtifact.TempInventory, nextGen),
            new(ProvisioningActionKind.WriteTempPreviousInventory, ProvisioningArtifact.TempPreviousInventory, backupGen),
            new(ProvisioningActionKind.ValidateTempPreviousInventory, ProvisioningArtifact.TempPreviousInventory, backupGen),
            new(ProvisioningActionKind.StampAcl, ProvisioningArtifact.Acl),
            new(ProvisioningActionKind.ReplacePreviousInventory, ProvisioningArtifact.PreviousInventory, backupGen),
            new(ProvisioningActionKind.ReplaceInventory, ProvisioningArtifact.Inventory, nextGen),
            new(ProvisioningActionKind.WriteTempLedger, ProvisioningArtifact.TempLedger, nextGen),
            new(ProvisioningActionKind.ValidateTempLedger, ProvisioningArtifact.TempLedger, nextGen),
            new(ProvisioningActionKind.ReplaceLedger, ProvisioningArtifact.Ledger, nextGen),
            new(ProvisioningActionKind.RemoveTransactionArtifact, ProvisioningArtifact.TransactionMarker, nextGen),
        };
        return ProvisioningPlan.Planned(op, ProvisioningPlanState.ReplacePlanned, ownershipProven, mutating, nextGen, replace);
    }

    private static ProvisioningPlan PlanRollback(
        ProvisioningRequest request,
        SyntheticSourceState source,
        SyntheticLedgerState ledgerState,
        bool ownershipProven)
    {
        // Rollback requires proven ownership.
        if (!ownershipProven)
        {
            return ProvisioningPlan.Rejected(ProvisioningOperation.Rollback, ProvisioningRejectionReason.OwnershipNotProven, ownershipProven);
        }

        OwnershipLedger current = ledgerState.Ledger!;

        // The caller must pin the generation being rolled back FROM; a missing or
        // mismatched expectation fails closed (no cross-generation restore).
        if (request.ExpectedLedgerGeneration is int expGen && expGen != current.CurrentGeneration)
        {
            return ProvisioningPlan.Rejected(ProvisioningOperation.Rollback, ProvisioningRejectionReason.GenerationMismatch, ownershipProven);
        }

        // A valid previous generation must be recorded.
        if (current.PreviousGeneration is not int prevGen || current.PreviousInventorySha256 is null)
        {
            return ProvisioningPlan.Rejected(ProvisioningOperation.Rollback, ProvisioningRejectionReason.NoPreviousGeneration, ownershipProven);
        }

        // Reversible SWAP through a NEW monotonic generation N+1 (§10). The current
        // inventory (A) and the previous backup (B) exchange content roles: B becomes
        // the new current, A becomes the new backup. Both byte sources are the
        // ownership-proven owned artifacts (never caller bytes). Generation numbers
        // are never decremented or reused; previousGeneration(N) < currentGeneration(N+1)
        // stays compatible with the ledger validator. Rollback-of-rollback swaps back
        // at N+2.
        int nextGen = current.CurrentGeneration + 1;
        int backupGen = current.CurrentGeneration;
        _ = prevGen;
        bool mutating = !request.PlanOnly;
        var actions = new List<ProvisioningPlanAction>
        {
            new(ProvisioningActionKind.WriteTempInventory, ProvisioningArtifact.TempInventory, nextGen),
            new(ProvisioningActionKind.ValidateTempInventory, ProvisioningArtifact.TempInventory, nextGen),
            new(ProvisioningActionKind.WriteTempPreviousInventory, ProvisioningArtifact.TempPreviousInventory, backupGen),
            new(ProvisioningActionKind.ValidateTempPreviousInventory, ProvisioningArtifact.TempPreviousInventory, backupGen),
            new(ProvisioningActionKind.StampAcl, ProvisioningArtifact.Acl),
            new(ProvisioningActionKind.RestorePreviousGeneration, ProvisioningArtifact.Inventory, nextGen),
            new(ProvisioningActionKind.ReplacePreviousInventory, ProvisioningArtifact.PreviousInventory, backupGen),
            new(ProvisioningActionKind.WriteTempLedger, ProvisioningArtifact.TempLedger, nextGen),
            new(ProvisioningActionKind.ValidateTempLedger, ProvisioningArtifact.TempLedger, nextGen),
            new(ProvisioningActionKind.ReplaceLedger, ProvisioningArtifact.Ledger, nextGen),
            new(ProvisioningActionKind.RemoveTransactionArtifact, ProvisioningArtifact.TransactionMarker, nextGen),
        };
        return ProvisioningPlan.Planned(ProvisioningOperation.Rollback, ProvisioningPlanState.RollbackPlanned, ownershipProven, mutating, nextGen, actions);
    }

    private static ProvisioningPlan PlanRemove(
        ProvisioningRequest request,
        SyntheticSourceState source,
        SyntheticLedgerState ledgerState,
        bool ownershipProven)
    {
        // Remove requires the full ownership proof AND no unknown sibling artifact.
        if (source.UnknownSiblingPresent)
        {
            return ProvisioningPlan.Rejected(ProvisioningOperation.Remove, ProvisioningRejectionReason.UnknownSiblingArtifact, ownershipProven);
        }
        if (!ownershipProven)
        {
            return ProvisioningPlan.Rejected(ProvisioningOperation.Remove, ProvisioningRejectionReason.OwnershipNotProven, ownershipProven);
        }

        OwnershipLedger current = ledgerState.Ledger!;
        if (request.ExpectedLedgerGeneration is int expGen && expGen != current.CurrentGeneration)
        {
            return ProvisioningPlan.Rejected(ProvisioningOperation.Remove, ProvisioningRejectionReason.GenerationMismatch, ownershipProven);
        }

        bool mutating = !request.PlanOnly;
        var actions = new List<ProvisioningPlanAction>
        {
            new(ProvisioningActionKind.RemoveInventory, ProvisioningArtifact.Inventory),
        };
        // Remove the single owned previous backup only when the ledger records a
        // previous generation (generation >= 2). At generation 1 no prev exists.
        if (current.PreviousGeneration is not null)
        {
            actions.Add(new(ProvisioningActionKind.RemovePreviousInventory, ProvisioningArtifact.PreviousInventory));
        }
        actions.Add(new(ProvisioningActionKind.RemoveLedger, ProvisioningArtifact.Ledger));
        actions.Add(new(ProvisioningActionKind.RemoveTransactionArtifact, ProvisioningArtifact.TransactionMarker));
        actions.Add(new(ProvisioningActionKind.RemoveManagedDirectoryIfEmpty, ProvisioningArtifact.ManagedDirectory));
        return ProvisioningPlan.Planned(ProvisioningOperation.Remove, ProvisioningPlanState.RemovePlanned, ownershipProven, mutating, current.CurrentGeneration, actions);
    }

    private static ProvisioningPlan PlanRecovery(ProvisioningOperation op, bool ownershipProven, OwnershipLedger ledger)
    {
        // Recovery from a crash window requires ownership proof; an unproven or
        // ambiguous in-progress state fails closed and can NEVER report success.
        if (!ownershipProven)
        {
            return ProvisioningPlan.Rejected(op, ProvisioningRejectionReason.PartialTransaction, ownershipProven);
        }

        // Under ownership proof, restore the last committed (previous) generation
        // if one exists, else roll the in-progress generation forward-cleanly by
        // re-stamping the current generation ledger. Either way it is a RECOVERY
        // plan, never a normal success no-op.
        int restoreGen = ledger.PreviousGeneration ?? ledger.CurrentGeneration;
        var actions = new List<ProvisioningPlanAction>
        {
            new(ProvisioningActionKind.RestorePreviousGeneration, ProvisioningArtifact.Inventory, restoreGen),
            new(ProvisioningActionKind.WriteTempLedger, ProvisioningArtifact.TempLedger, restoreGen),
            new(ProvisioningActionKind.ValidateTempLedger, ProvisioningArtifact.TempLedger, restoreGen),
            new(ProvisioningActionKind.ReplaceLedger, ProvisioningArtifact.Ledger, restoreGen),
            new(ProvisioningActionKind.RemoveTransactionArtifact, ProvisioningArtifact.TransactionMarker, restoreGen),
        };
        return ProvisioningPlan.Planned(op, ProvisioningPlanState.RecoveryPlanned, ownershipProven, wouldMutate: true, restoreGen, actions);
    }

    // A pure SHA-256 hex of the UTF-8 bytes of the proposed inventory content,
    // used only to detect idempotence. It touches no key, store, or private key.
    private static string ComputeSha256Hex(string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(bytes);
        var sb = new StringBuilder(64);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------
// Bounded, SetupExitCodes-style result projection. Carries ONLY the §41 audit
// facts: operation, code, state, ownership-proven, generation, reason token.
// Nothing else — no inventory, path, ACL/SID dump, identifier, or secret.
// ---------------------------------------------------------------------------
public static class ProvisioningResultCodes
{
    public const int Ok = 0;                 // clean planned success or no-op.
    public const int RecoveryRequired = 60;  // in-progress crash window; NON-success.
    public const int Rejected = 40;          // generic bounded rejection.
    public const int ForeignArtifact = 41;
    public const int StateMismatch = 42;
    public const int OwnershipNotProven = 43;
    public const int UnknownSibling = 44;
    public const int NoPreviousGeneration = 45;
    public const int GenerationMismatch = 46;
    public const int UntrustedEnvironment = 47;
    public const int LedgerInvalid = 48;
    public const int PartialTransaction = 49;
    public const int UnsupportedOperation = 50;
    public const int RequestNotValidated = 51;
    public const int LedgerInventoryDisagree = 52;
    public const int NothingToRemove = 53;
}

public sealed class ProvisioningResult
{
    private ProvisioningResult(
        ProvisioningOperation operation,
        int code,
        ProvisioningPlanState state,
        bool ownershipProven,
        int generation,
        ProvisioningRejectionReason reason)
    {
        Operation = operation;
        Code = code;
        State = state;
        OwnershipProven = ownershipProven;
        Generation = generation;
        Reason = reason;
    }

    public ProvisioningOperation Operation { get; }

    public int Code { get; }

    public ProvisioningPlanState State { get; }

    public bool OwnershipProven { get; }

    public int Generation { get; }

    public ProvisioningRejectionReason Reason { get; }

    // Projects a plan into the bounded audit result. A RecoveryPlanned plan is a
    // distinct NON-success code so a partial/interrupted state can never be
    // reported as success.
    public static ProvisioningResult FromPlan(ProvisioningPlan plan)
    {
        if (plan.IsPlanned)
        {
            int code = plan.ResultState == ProvisioningPlanState.RecoveryPlanned
                ? ProvisioningResultCodes.RecoveryRequired
                : ProvisioningResultCodes.Ok;
            return new ProvisioningResult(plan.Operation, code, plan.ResultState,
                plan.OwnershipProven, plan.TargetGeneration, ProvisioningRejectionReason.None);
        }

        int rejectCode = plan.RejectionReason switch
        {
            ProvisioningRejectionReason.ForeignArtifactPresent => ProvisioningResultCodes.ForeignArtifact,
            ProvisioningRejectionReason.StateMismatch => ProvisioningResultCodes.StateMismatch,
            ProvisioningRejectionReason.OwnershipNotProven => ProvisioningResultCodes.OwnershipNotProven,
            ProvisioningRejectionReason.UnknownSiblingArtifact => ProvisioningResultCodes.UnknownSibling,
            ProvisioningRejectionReason.NoPreviousGeneration => ProvisioningResultCodes.NoPreviousGeneration,
            ProvisioningRejectionReason.GenerationMismatch => ProvisioningResultCodes.GenerationMismatch,
            ProvisioningRejectionReason.UntrustedEnvironment => ProvisioningResultCodes.UntrustedEnvironment,
            ProvisioningRejectionReason.LedgerInvalid => ProvisioningResultCodes.LedgerInvalid,
            ProvisioningRejectionReason.PartialTransaction => ProvisioningResultCodes.PartialTransaction,
            ProvisioningRejectionReason.UnsupportedOperation => ProvisioningResultCodes.UnsupportedOperation,
            ProvisioningRejectionReason.RequestNotValidated => ProvisioningResultCodes.RequestNotValidated,
            ProvisioningRejectionReason.LedgerInventoryDisagree => ProvisioningResultCodes.LedgerInventoryDisagree,
            ProvisioningRejectionReason.NothingToRemove => ProvisioningResultCodes.NothingToRemove,
            _ => ProvisioningResultCodes.Rejected,
        };
        return new ProvisioningResult(plan.Operation, rejectCode, ProvisioningPlanState.None,
            plan.OwnershipProven, 0, plan.RejectionReason);
    }
}
