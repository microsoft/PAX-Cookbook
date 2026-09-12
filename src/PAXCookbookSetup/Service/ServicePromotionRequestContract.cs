using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PAXCookbook.App;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

// ---------------------------------------------------------------------------
// CLOSED CERTIFICATE-PROMOTION PROTOCOL - cycle 84 (part 2). NO PRODUCTION
// CALLER.
//
// WHAT THIS IS. The complete, closed request vocabulary for the only TWO
// internal operations this product will ever expose over a promotion channel:
//
//   1. PROMOTE   - grant the fixed service identity read access to ONE already
//                  installed, REFERENCED machine certificate's private key, and
//                  place ONE validated Recipe in the machine Recipe store.
//   2. DEPROMOTE - unwind ONE promoted job.
//
// THERE IS NO GENERIC VERB, and there is deliberately no way to add one without
// changing this file: the parser declares exactly two methods, each taking
// exactly one string, and a structural test asserts that.
//
// WHAT A REQUEST MAY CARRY, EXHAUSTIVELY. A promotion request has SEVEN
// properties and a depromotion request has FOUR. Every other property name is
// refused. Nothing here accepts - or can be widened to accept - a path, a
// directory, a root, a store location, a store name, a service name, a SID, a
// provider name, a key storage root, an ACL or security descriptor, a rights
// mask, a grant mechanism, a command, an executable, or a registry path. Those
// values are FIXED INTERNAL DERIVATIONS elsewhere and are not negotiable by a
// caller. The denylist below exists so such a field is refused with a SPECIFIC
// outcome rather than a generic "unknown property", because the difference
// matters when reading an audit trail.
//
// WHAT THIS FILE CANNOT DO, by construction. It opens no file, composes no
// path, reads no environment variable, touches no certificate store, private
// key, ACL, registry, SCM or ProgramData, starts no process, performs no
// network access, and mutates nothing anywhere. It is a parser. Every refusal
// therefore happens BEFORE any mutation could exist, because this type has no
// mutation to reach.
//
// THE RECIPE GATE IS THE CANONICAL ONE. Semantic validation runs through
// RecipeValidationModel - the SAME source the App compiles (cycle 83B-R) - so a
// Recipe accepted here is accepted identically on the desktop side, with no
// second implementation to drift. On top of it this file requires
// CERTIFICATE-ONLY app-registration semantics and refuses the bindings this
// feature does not support at all.
//
// FAIL CLOSED, AND SILENT. Every unusable input maps to a bounded enum value.
// No reason string, path, exception text, Recipe byte, thumbprint, tenant id or
// identifier ever escapes through a result or a ToString().
//
// CYCLE 86 CORRECTION (F1). promotedJobId is the ONLY field a downstream surface
// composes a file name from, so it now delegates to
// ServiceOwnershipLedgerContract.IsValidKeyIdentity. operationId and
// expectedInstallationOwnershipId name nothing and keep the protocol's own,
// deliberately weaker, bounded-token rule - which cycle 84 wrongly documented as
// having key-identity parity.
// ---------------------------------------------------------------------------

/// <summary>
/// Bounded promotion-protocol parse outcome. Zero always refuses, so an
/// uninitialised value can never read as an accepted request.
/// </summary>
internal enum ServicePromotionRequestOutcome
{
    Unspecified = 0,

    /// <summary>The request is well formed, bounded, and semantically admissible.</summary>
    Accepted = 1,

    /// <summary>Not strict UTF-8 text, carries a byte-order mark, or is not a JSON object.</summary>
    MalformedRequest = 2,

    /// <summary>The request text exceeds the bounded size.</summary>
    OversizedRequest = 3,

    /// <summary>The declared schema version is not the one supported version.</summary>
    UnsupportedSchemaVersion = 4,

    /// <summary>A property name outside the closed set for this operation.</summary>
    UnknownProperty = 5,

    /// <summary>The same property name appeared more than once.</summary>
    DuplicateProperty = 6,

    /// <summary>A required property is absent.</summary>
    MissingProperty = 7,

    /// <summary>A property is present with the wrong JSON type.</summary>
    WrongPropertyType = 8,

    /// <summary>The operation id is not a bounded token.</summary>
    MalformedOperationId = 9,

    /// <summary>
    /// The promoted-job id is not a valid ledger KEY IDENTITY. This is stricter
    /// than the bounded-token rule the other two identifiers use, because this is
    /// the only field a downstream surface composes a file name from.
    /// </summary>
    MalformedPromotedJobId = 10,

    /// <summary>The thumbprint is not forty hexadecimal digits after normalization.</summary>
    MalformedCertificateThumbprint = 11,

    /// <summary>The expected Recipe digest is not an uppercase SHA-256 hex string.</summary>
    MalformedExpectedRecipeSha256 = 12,

    /// <summary>The expected installation-ownership id is not a bounded token.</summary>
    MalformedInstallationOwnershipId = 13,

    /// <summary>The Recipe payload is absent, not base64, or exceeds the bounded size.</summary>
    OversizedOrUndecodableRecipe = 14,

    /// <summary>The Recipe bytes do not hash to the digest the request declared.</summary>
    RecipeDigestMismatch = 15,

    /// <summary>The Recipe bytes are not strict UTF-8, or carry a byte-order mark.</summary>
    RecipeNotUtf8 = 16,

    /// <summary>The Recipe text is not a JSON object.</summary>
    RecipeNotJsonObject = 17,

    /// <summary>The canonical Recipe validator refused the Recipe.</summary>
    RecipeSemanticallyInvalid = 18,

    /// <summary>The Recipe does not use certificate-only app-registration sign-in.</summary>
    RecipeAuthModeNotAppRegistrationCertificate = 19,

    /// <summary>
    /// The Recipe carries a binding this feature does not support at all - an
    /// organization key, or inline secret-shaped material.
    /// </summary>
    RecipeCarriesProhibitedBinding = 20,

    /// <summary>The request carries a field that would supply authority to the caller.</summary>
    AuthorityBearingFieldPresent = 21,
}

/// <summary>
/// An ACCEPTED promotion request. The only constructor is PRIVATE, so an
/// instance exists only if <see cref="ServicePromotionRequestParser"/> accepted
/// every field. It carries exactly SEVEN properties and no path of any kind.
/// </summary>
internal sealed class ServicePromotionRequest
{
    private readonly byte[] recipeBytes;

    private ServicePromotionRequest(
        int schemaVersion,
        string operationId,
        string promotedJobId,
        string certificateThumbprintSha1,
        byte[] recipeBytes,
        string expectedRecipeSha256,
        string expectedInstallationOwnershipId)
    {
        SchemaVersion = schemaVersion;
        OperationId = operationId;
        PromotedJobId = promotedJobId;
        CertificateThumbprintSha1 = certificateThumbprintSha1;
        this.recipeBytes = recipeBytes;
        ExpectedRecipeSha256 = expectedRecipeSha256;
        ExpectedInstallationOwnershipId = expectedInstallationOwnershipId;
    }

    internal int SchemaVersion { get; }

    internal string OperationId { get; }

    internal string PromotedJobId { get; }

    /// <summary>Normalized: forty uppercase hexadecimal digits, never spaced.</summary>
    internal string CertificateThumbprintSha1 { get; }

    /// <summary>
    /// A DEFENSIVE COPY on every read. The accepted request can never be mutated
    /// after validation by a caller holding the array.
    /// </summary>
    internal byte[] RecipeBytes
    {
        get
        {
            var copy = new byte[recipeBytes.Length];
            Buffer.BlockCopy(recipeBytes, 0, copy, 0, recipeBytes.Length);
            return copy;
        }
    }

    internal string ExpectedRecipeSha256 { get; }

    internal string ExpectedInstallationOwnershipId { get; }

    internal static ServicePromotionRequest Accepted(
        int schemaVersion,
        string operationId,
        string promotedJobId,
        string certificateThumbprintSha1,
        byte[] recipeBytes,
        string expectedRecipeSha256,
        string expectedInstallationOwnershipId) =>
        new(schemaVersion, operationId, promotedJobId, certificateThumbprintSha1,
            recipeBytes, expectedRecipeSha256, expectedInstallationOwnershipId);

    /// <summary>Carries only the type name - never a field value.</summary>
    public override string ToString() => nameof(ServicePromotionRequest);
}

/// <summary>
/// An ACCEPTED depromotion request. Four properties, no certificate reference
/// and no Recipe: depromotion names a job the ledger already owns and nothing
/// else.
/// </summary>
internal sealed class ServiceDepromotionRequest
{
    private ServiceDepromotionRequest(
        int schemaVersion,
        string operationId,
        string promotedJobId,
        string expectedInstallationOwnershipId)
    {
        SchemaVersion = schemaVersion;
        OperationId = operationId;
        PromotedJobId = promotedJobId;
        ExpectedInstallationOwnershipId = expectedInstallationOwnershipId;
    }

    internal int SchemaVersion { get; }

    internal string OperationId { get; }

    internal string PromotedJobId { get; }

    internal string ExpectedInstallationOwnershipId { get; }

    internal static ServiceDepromotionRequest Accepted(
        int schemaVersion,
        string operationId,
        string promotedJobId,
        string expectedInstallationOwnershipId) =>
        new(schemaVersion, operationId, promotedJobId, expectedInstallationOwnershipId);

    /// <summary>Carries only the type name - never a field value.</summary>
    public override string ToString() => nameof(ServiceDepromotionRequest);
}

/// <summary>
/// Bounded parse result. At most ONE of the two request properties is ever
/// non-null, and both are null unless <see cref="Outcome"/> is
/// <see cref="ServicePromotionRequestOutcome.Accepted"/>.
/// </summary>
internal sealed class ServicePromotionRequestParseResult
{
    private ServicePromotionRequestParseResult(
        ServicePromotionRequestOutcome outcome,
        ServicePromotionRequest? promotion,
        ServiceDepromotionRequest? depromotion)
    {
        Outcome = outcome;
        Promotion = promotion;
        Depromotion = depromotion;
    }

    internal ServicePromotionRequestOutcome Outcome { get; }

    internal ServicePromotionRequest? Promotion { get; }

    internal ServiceDepromotionRequest? Depromotion { get; }

    internal bool IsAccepted => Outcome == ServicePromotionRequestOutcome.Accepted;

    internal static ServicePromotionRequestParseResult Refused(
        ServicePromotionRequestOutcome outcome) => new(outcome, null, null);

    internal static ServicePromotionRequestParseResult AcceptedPromotion(
        ServicePromotionRequest request) =>
        new(ServicePromotionRequestOutcome.Accepted, request, null);

    internal static ServicePromotionRequestParseResult AcceptedDepromotion(
        ServiceDepromotionRequest request) =>
        new(ServicePromotionRequestOutcome.Accepted, null, request);

    /// <summary>Carries only the bounded outcome token.</summary>
    public override string ToString() => Outcome.ToString();
}

/// <summary>
/// The closed protocol parser. TWO verbs, one string each, no options, no
/// overloads, no settable static field, and no way to reach either one with a
/// caller-supplied path, identity or policy value.
/// </summary>
internal static class ServicePromotionRequestParser
{
    /// <summary>The one supported request schema version.</summary>
    internal const int SupportedSchemaVersion = 1;

    /// <summary>
    /// Outer bound on the whole request text. Generous relative to a
    /// base64-encoded maximum Recipe plus its envelope, and bounded so a hostile
    /// payload can never drive an unbounded parse.
    /// </summary>
    internal const int MaxRequestChars = 524288;

    private static readonly string[] PromotionProperties =
    {
        "schemaVersion",
        "operationId",
        "promotedJobId",
        "certificateThumbprintSha1",
        "recipeBase64",
        "expectedRecipeSha256",
        "expectedInstallationOwnershipId",
    };

    private static readonly string[] DepromotionProperties =
    {
        "schemaVersion",
        "operationId",
        "promotedJobId",
        "expectedInstallationOwnershipId",
    };

    /// <summary>
    /// Property names that would hand the caller authority this protocol reserves
    /// as a fixed internal derivation. Matched CASE-INSENSITIVELY and as a
    /// SUBSTRING, so a near-spelling cannot slip past into the generic
    /// unknown-property arm and be misread later as a typo.
    /// </summary>
    private static readonly string[] AuthorityBearingFragments =
    {
        "path", "root", "directory", "folder", "store", "location",
        "sid", "account", "service", "provider", "keystorage",
        "dacl", "acl", "descriptor", "security", "rights", "mask", "mechanism",
        "command", "executable", "process", "registry", "privilege", "token",
        "secret", "password", "credential",
    };

    /// <summary>
    /// Recipe auth modes this feature refuses outright. The Recipe schema also
    /// enumerates them, but naming them here makes the refusal explicit and
    /// independent of a schema change upstream.
    /// </summary>
    private static readonly string[] ProhibitedAuthModes =
    {
        "WebLogin", "DeviceCode", "ManagedIdentity", "AppRegistrationSecret",
    };

    /// <summary>The one accepted auth mode, ordinal and case-sensitive.</summary>
    private const string RequiredAuthMode = "AppRegistrationCertificate";

    internal static ServicePromotionRequestParseResult ParsePromotion(string? requestJson)
    {
        try
        {
            ServicePromotionRequestOutcome envelope = ReadEnvelope(
                requestJson, PromotionProperties, out Dictionary<string, JsonElement> values);
            if (envelope != ServicePromotionRequestOutcome.Accepted)
            {
                return ServicePromotionRequestParseResult.Refused(envelope);
            }

            ServicePromotionRequestOutcome common = ReadCommonFields(
                values,
                out int schemaVersion,
                out string operationId,
                out string promotedJobId,
                out string installationOwnershipId);
            if (common != ServicePromotionRequestOutcome.Accepted)
            {
                return ServicePromotionRequestParseResult.Refused(common);
            }

            if (values["certificateThumbprintSha1"].ValueKind != JsonValueKind.String
                || values["recipeBase64"].ValueKind != JsonValueKind.String
                || values["expectedRecipeSha256"].ValueKind != JsonValueKind.String)
            {
                return ServicePromotionRequestParseResult.Refused(
                    ServicePromotionRequestOutcome.WrongPropertyType);
            }

            string? thumbprint = NormalizeThumbprint(values["certificateThumbprintSha1"].GetString());
            if (thumbprint is null)
            {
                return ServicePromotionRequestParseResult.Refused(
                    ServicePromotionRequestOutcome.MalformedCertificateThumbprint);
            }

            string expectedDigest = values["expectedRecipeSha256"].GetString() ?? string.Empty;
            if (!ServiceOwnershipLedgerContract.IsUppercaseSha256Hex(expectedDigest))
            {
                return ServicePromotionRequestParseResult.Refused(
                    ServicePromotionRequestOutcome.MalformedExpectedRecipeSha256);
            }

            byte[]? recipeBytes = DecodeBoundedBase64(values["recipeBase64"].GetString());
            if (recipeBytes is null)
            {
                return ServicePromotionRequestParseResult.Refused(
                    ServicePromotionRequestOutcome.OversizedOrUndecodableRecipe);
            }

            // THE DIGEST IS CHECKED BEFORE THE RECIPE IS INTERPRETED. A payload
            // that is not the payload the caller committed to is never parsed.
            if (!string.Equals(ToUpperHex(SHA256.HashData(recipeBytes)), expectedDigest, StringComparison.Ordinal))
            {
                return ServicePromotionRequestParseResult.Refused(
                    ServicePromotionRequestOutcome.RecipeDigestMismatch);
            }

            ServicePromotionRequestOutcome recipeOutcome = ValidateRecipe(recipeBytes);
            if (recipeOutcome != ServicePromotionRequestOutcome.Accepted)
            {
                return ServicePromotionRequestParseResult.Refused(recipeOutcome);
            }

            return ServicePromotionRequestParseResult.AcceptedPromotion(
                ServicePromotionRequest.Accepted(
                    schemaVersion, operationId, promotedJobId, thumbprint,
                    recipeBytes, expectedDigest, installationOwnershipId));
        }
        catch (Exception)
        {
            // Fail closed exactly like the ledger validator: no exception text
            // escapes, and an unexpected shape is never accepted.
            return ServicePromotionRequestParseResult.Refused(
                ServicePromotionRequestOutcome.MalformedRequest);
        }
    }

    internal static ServicePromotionRequestParseResult ParseDepromotion(string? requestJson)
    {
        try
        {
            ServicePromotionRequestOutcome envelope = ReadEnvelope(
                requestJson, DepromotionProperties, out Dictionary<string, JsonElement> values);
            if (envelope != ServicePromotionRequestOutcome.Accepted)
            {
                return ServicePromotionRequestParseResult.Refused(envelope);
            }

            ServicePromotionRequestOutcome common = ReadCommonFields(
                values,
                out int schemaVersion,
                out string operationId,
                out string promotedJobId,
                out string installationOwnershipId);
            if (common != ServicePromotionRequestOutcome.Accepted)
            {
                return ServicePromotionRequestParseResult.Refused(common);
            }

            return ServicePromotionRequestParseResult.AcceptedDepromotion(
                ServiceDepromotionRequest.Accepted(
                    schemaVersion, operationId, promotedJobId, installationOwnershipId));
        }
        catch (Exception)
        {
            return ServicePromotionRequestParseResult.Refused(
                ServicePromotionRequestOutcome.MalformedRequest);
        }
    }

    // -----------------------------------------------------------------------
    // ENVELOPE
    // -----------------------------------------------------------------------

    private static ServicePromotionRequestOutcome ReadEnvelope(
        string? requestJson,
        string[] closedPropertySet,
        out Dictionary<string, JsonElement> values)
    {
        values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(requestJson))
        {
            return ServicePromotionRequestOutcome.MalformedRequest;
        }
        if (requestJson!.Length > MaxRequestChars)
        {
            return ServicePromotionRequestOutcome.OversizedRequest;
        }
        if (requestJson[0] == '\uFEFF')
        {
            return ServicePromotionRequestOutcome.MalformedRequest;
        }

        using JsonDocument parsed = JsonDocument.Parse(requestJson);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
        {
            return ServicePromotionRequestOutcome.MalformedRequest;
        }

        var closed = new HashSet<string>(closedPropertySet, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
        {
            // AUTHORITY FIRST. A field that would supply authority is refused with
            // its own outcome even when it is also simply unknown, because the two
            // read very differently in an audit trail.
            if (!closed.Contains(property.Name) && IsAuthorityBearing(property.Name))
            {
                return ServicePromotionRequestOutcome.AuthorityBearingFieldPresent;
            }
            if (!closed.Contains(property.Name))
            {
                return ServicePromotionRequestOutcome.UnknownProperty;
            }
            if (!seen.Add(property.Name))
            {
                return ServicePromotionRequestOutcome.DuplicateProperty;
            }

            // Clone: the JsonDocument is disposed when this method returns.
            values[property.Name] = property.Value.Clone();
        }

        foreach (string required in closedPropertySet)
        {
            if (!values.ContainsKey(required))
            {
                return ServicePromotionRequestOutcome.MissingProperty;
            }
        }

        return ServicePromotionRequestOutcome.Accepted;
    }

    private static ServicePromotionRequestOutcome ReadCommonFields(
        Dictionary<string, JsonElement> values,
        out int schemaVersion,
        out string operationId,
        out string promotedJobId,
        out string installationOwnershipId)
    {
        schemaVersion = 0;
        operationId = string.Empty;
        promotedJobId = string.Empty;
        installationOwnershipId = string.Empty;

        JsonElement version = values["schemaVersion"];
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out schemaVersion))
        {
            return ServicePromotionRequestOutcome.WrongPropertyType;
        }
        if (schemaVersion != SupportedSchemaVersion)
        {
            return ServicePromotionRequestOutcome.UnsupportedSchemaVersion;
        }

        if (values["operationId"].ValueKind != JsonValueKind.String
            || values["promotedJobId"].ValueKind != JsonValueKind.String
            || values["expectedInstallationOwnershipId"].ValueKind != JsonValueKind.String)
        {
            return ServicePromotionRequestOutcome.WrongPropertyType;
        }

        operationId = values["operationId"].GetString() ?? string.Empty;
        promotedJobId = values["promotedJobId"].GetString() ?? string.Empty;
        installationOwnershipId = values["expectedInstallationOwnershipId"].GetString() ?? string.Empty;

        // operationId and expectedInstallationOwnershipId NAME NOTHING, so they keep
        // the protocol's own bounded-token rule. promotedJobId is different: it is
        // the ONE request field a downstream surface turns into a file name, so it
        // delegates to the ledger's KEY-IDENTITY grammar rather than to a local
        // approximation of it.
        if (!IsBoundedToken(operationId))
        {
            return ServicePromotionRequestOutcome.MalformedOperationId;
        }
        if (!ServiceOwnershipLedgerContract.IsValidKeyIdentity(promotedJobId))
        {
            return ServicePromotionRequestOutcome.MalformedPromotedJobId;
        }
        if (!IsBoundedToken(installationOwnershipId))
        {
            return ServicePromotionRequestOutcome.MalformedInstallationOwnershipId;
        }

        return ServicePromotionRequestOutcome.Accepted;
    }

    // -----------------------------------------------------------------------
    // RECIPE
    // -----------------------------------------------------------------------

    private static ServicePromotionRequestOutcome ValidateRecipe(byte[] recipeBytes)
    {
        if (recipeBytes.Length >= 3
            && recipeBytes[0] == 0xEF && recipeBytes[1] == 0xBB && recipeBytes[2] == 0xBF)
        {
            return ServicePromotionRequestOutcome.RecipeNotUtf8;
        }

        string text;
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = strict.GetString(recipeBytes);
        }
        catch (Exception)
        {
            return ServicePromotionRequestOutcome.RecipeNotUtf8;
        }

        object? model;
        try
        {
            model = JsonModel.Parse(text);
        }
        catch (Exception)
        {
            return ServicePromotionRequestOutcome.RecipeNotJsonObject;
        }

        if (model is not Dictionary<string, object?> recipe)
        {
            return ServicePromotionRequestOutcome.RecipeNotJsonObject;
        }

        // THE CANONICAL SEMANTIC GATE - the same source the App compiles, so a
        // Recipe accepted here is accepted identically on the desktop side.
        (bool ok, List<object> _) = RecipeValidationModel.ValidateAll(recipe);
        if (!ok)
        {
            return ServicePromotionRequestOutcome.RecipeSemanticallyInvalid;
        }

        if (recipe.TryGetValue("auth", out object? authNode)
            && authNode is Dictionary<string, object?> auth)
        {
            string mode = auth.TryGetValue("mode", out object? modeNode)
                ? JsonModel.Str(modeNode)
                : string.Empty;

            foreach (string prohibited in ProhibitedAuthModes)
            {
                if (string.Equals(mode, prohibited, StringComparison.OrdinalIgnoreCase))
                {
                    return ServicePromotionRequestOutcome.RecipeAuthModeNotAppRegistrationCertificate;
                }
            }

            // ONE ACCEPTED SPELLING. A case variant is refused rather than
            // normalized: capability is never granted by a lenient comparison.
            if (!string.Equals(mode, RequiredAuthMode, StringComparison.Ordinal))
            {
                return ServicePromotionRequestOutcome.RecipeAuthModeNotAppRegistrationCertificate;
            }

            // Organization keys are OUT OF SCOPE for service promotion. The
            // canonical validator permits the binding for certificate mode, so
            // this refusal is genuinely additional, not a restatement.
            if (auth.ContainsKey("organizationKeyId"))
            {
                return ServicePromotionRequestOutcome.RecipeCarriesProhibitedBinding;
            }
        }
        else
        {
            return ServicePromotionRequestOutcome.RecipeAuthModeNotAppRegistrationCertificate;
        }

        return ServicePromotionRequestOutcome.Accepted;
    }

    // -----------------------------------------------------------------------
    // BOUNDED PRIMITIVES
    // -----------------------------------------------------------------------

    private static bool IsAuthorityBearing(string propertyName)
    {
        foreach (string fragment in AuthorityBearingFragments)
        {
            if (propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The bounded-token grammar for the request identifiers that NAME NOTHING -
    /// operationId and expectedInstallationOwnershipId. It is the ledger's own
    /// bounded-token rule, taken from the contract rather than retyped, plus ONE
    /// local exclusion: an ALL-DOT token, because the ledger grammar admits '.' so
    /// "." and ".." would otherwise be legal identifiers.
    ///
    /// THIS IS DELIBERATELY WEAKER THAN
    /// <see cref="ServiceOwnershipLedgerContract.IsValidKeyIdentity"/>, which also
    /// refuses a LEADING '.' and any embedded "..". Cycle 84 claimed parity here
    /// and did not have it; the claim, not the code, was the defect for these two
    /// fields. promotedJobId does NOT use this predicate - it delegates directly to
    /// IsValidKeyIdentity, because it is the only field a downstream surface
    /// composes a file name from.
    /// </summary>
    private static bool IsBoundedToken(string? value)
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

    /// <summary>
    /// Normalizes a SHA-1 thumbprint to the ONE spelling the ledger accepts:
    /// forty uppercase hexadecimal digits. ASCII space and tab are removed
    /// because certificate tooling routinely emits a spaced thumbprint; ANY other
    /// character refuses. Returns null when the value cannot be normalized.
    /// </summary>
    private static string? NormalizeThumbprint(string? value)
    {
        if (value is null || value.Length > 128)
        {
            return null;
        }

        var sb = new StringBuilder(40);
        foreach (char c in value)
        {
            if (c is ' ' or '\t')
            {
                continue;
            }
            if (c is >= '0' and <= '9')
            {
                sb.Append(c);
            }
            else if (c is >= 'A' and <= 'F')
            {
                sb.Append(c);
            }
            else if (c is >= 'a' and <= 'f')
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else
            {
                return null;
            }

            if (sb.Length > 40)
            {
                return null;
            }
        }

        string normalized = sb.ToString();
        return ServiceOwnershipLedgerContract.IsUppercaseSha1Thumbprint(normalized) ? normalized : null;
    }

    /// <summary>
    /// Decodes base64 into the SAME bound the promoted-Recipe store enforces,
    /// taken from that surface rather than retyped. The length is bounded BEFORE
    /// the decode allocates.
    /// </summary>
    private static byte[]? DecodeBoundedBase64(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // 4 base64 characters carry at most 3 bytes, so this rejects an oversized
        // payload without ever materialising it.
        long maxChars = ((long)ServiceOwnershipPromotedRecipeContent.MaxRecipeBytes + 2) / 3 * 4;
        if (value!.Length > maxChars)
        {
            return null;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }

        if (decoded.Length == 0
            || decoded.Length > ServiceOwnershipPromotedRecipeContent.MaxRecipeBytes)
        {
            return null;
        }

        return decoded;
    }

    private static string ToUpperHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
