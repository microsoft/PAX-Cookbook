using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.Shared.Contracts;

// ---------------------------------------------------------------------------
// SERVICE OWNERSHIP LEDGER - schema v2.
//
// SCHEMA v2 REPLACES v1 OUTRIGHT. There is no migration and no fallback: a document
// declaring schema v1 is refused with UnsupportedSchemaVersion, permanently. v1
// cannot return, because the v1 entry shape has no way to express the required
// rightsProfileId property that v2 binds every entry to.
//
// THE ONE APPROVED RIGHTS PROFILE. Cycle 41e MEASURED a provider-specific minimum
// private-key mask. v2 encodes exactly that measurement and nothing wider:
// Microsoft Software Key Storage Provider, RSA 2048, PS256, Azure.Identity 1.18.0,
// MSAL 4.82.1 and Microsoft.Graph.Authentication 2.39.0, with mask 0x00120009 -
// DERIVED below as ReadData | ReadExtendedAttributes | ReadPermissions |
// Synchronize. ReadAttributes is NOT part of it. The result is provider-specific
// and must never be generalised to TPM, smart card, a third-party KSP, legacy CSP
// or ECDSA.
//
// AN APPROVED PROFILE IS STILL NOT A BLANKET ACTIVATION (amended cycle 82).
// Until cycle 82 EVERY entry declaring lifecycleState "active" was refused with
// ActiveLifecycleAcceptanceNotAuthorized, including one matching the approved
// provider, profile and mask exactly. Brian authorised that acceptance (D1), so
// the unconditional brake is gone and the reason is now parser-unreachable,
// retained only as a bounded historical vocabulary value.
//
// WHAT REPLACED IT IS NARROWER, NOT WIDER. An active entry is accepted ONLY when
// TryAuthorizeActiveRights succeeds for the exact approved provider kind,
// rights-profile id, backing-file DACL mechanism, descriptor format, policy
// version and mask, AND every other entry gate still passes: valid prior DACL
// bytes and digest, a supported prior descriptor shape carrying NO pre-existing
// service ACE, a matching captured-state binding, valid owner and service SIDs,
// and every cross-entry and collision check. A one-field near miss is refused
// exactly as firmly as it was before.
//
// WHAT THIS IS. A pure, portable, deterministic schema and validator for the
// bounded record of "this installation asked for a service identity to be granted
// read access to a personal app-registration certificate's private key, and here
// is exactly enough non-secret provenance to undo it". It is compiled into
// PAXCookbook.Shared (used by Setup) and LINKED into PAXCookbook.Service, which
// references neither Shared nor App. It must therefore stay ENTIRELY
// SELF-CONTAINED: portable framework namespaces only, no dependency on any other
// contract type in this assembly.
//
// WHAT THIS IS NOT. It opens no certificate store, touches no private key, reads
// or writes no ACL, reads no registry, reads no credential vault, installs or
// starts no service, elevates nothing, reads or writes no machine data directory,
// opens no socket, starts no executable, and performs no file access of any kind.
// It validates strings. Nothing in the product reads or writes the file it
// describes; there is no reader and no writer in this cycle.
//
// COINCIDENTAL HOMONYM - READ THIS BEFORE CHANGING ANY FILE NAME.
// ServiceOwnershipLedgerContract.LedgerFileName and
// ProvisioningContract.LedgerFileName both spell "ownership-ledger.json". They are
// a COINCIDENTAL HOMONYM: two DIFFERENT files, in two DIFFERENT directories, with
// two DIFFERENT schemas, two DIFFERENT ownership markers and two DIFFERENT feature
// identities, owned by two DIFFERENT features. Neither constant is derived from
// the other. Neither schema is derived from the other. Each validator REJECTS the
// other's document, and there are tests in both directions proving it. Do not
// redirect either constant at the other, and do not describe them as one artifact.
//
// FAIL-CLOSED DOCTRINE. Any unknown, contradictory, oversized, foreign or
// ambiguous input fails the WHOLE ledger. There is no partial acceptance. Every
// enum's zero value is Unspecified and always refuses. No reason, message, path,
// exception text, certificate byte, private key, provider display name or command
// is representable anywhere in the schema or in any result.
// ---------------------------------------------------------------------------

/// <summary>Kind of credential an ownership entry describes. Zero always refuses.</summary>
public enum ServiceOwnershipCredentialKind
{
    Unspecified = 0,
    PersonalAppRegistrationCertificate = 1,
}

/// <summary>How the credential came to be known. Zero always refuses.</summary>
public enum ServiceOwnershipProvenance
{
    Unspecified = 0,
    Referenced = 1,
}

/// <summary>
/// Private key provider classification. Zero always refuses.
///
/// <c>Cng</c> and <c>LegacyCsp</c> are BROAD classifications and are deliberately
/// retained as bounded UNSUPPORTED-FOR-THE-PROFILE values: neither may ever inherit
/// the approved rights profile, and each is refused by its own specific reason.
/// <c>MicrosoftSoftwareKeyStorageProvider</c> is the ONE exact provider the
/// cycle-41e measurement covers.
/// </summary>
public enum ServiceOwnershipPrivateKeyProviderKind
{
    Unspecified = 0,
    Cng = 1,
    LegacyCsp = 2,
    MicrosoftSoftwareKeyStorageProvider = 3,
}

/// <summary>
/// Closed set of rights-profile identifiers. Zero always refuses, and there is
/// exactly ONE approved member. Its name states the complete measured scope on
/// purpose: a generic name such as "cng", "rsa" or "certificate" would invite the
/// exact over-generalisation cycle 41e forbade.
/// </summary>
public enum ServiceOwnershipRightsProfileId
{
    Unspecified = 0,

    /// <summary>
    /// RETIRED, cycle 48. This named the CNG key-object descriptor mechanism, which
    /// cycle 46 MEASURED AND REJECTED as a one-way lock (not merely "not selected").
    /// No profile may ever authorize it again. The numeric value is preserved and
    /// never reused.
    /// </summary>
    MicrosoftSoftwareKspRsa2048Ps256AzureIdentity1_18_0Msal4_82_1GraphAuth2_39_0 = 1,

    /// <summary>
    /// APPROVED, cycle 48 (schema v3). Cycle 47 MEASURED a REVERSIBLE Microsoft
    /// Software KSP backing-file DACL grant: FileSystemRights mask 0x00120009 over
    /// the exact captured owner/group/DACL self-relative file security descriptor
    /// shape. The name states the complete measured scope on purpose.
    /// </summary>
    MicrosoftSoftwareKspBackingFileRsa2048Ps256AzureIdentity1_18_0Msal4_82_1GraphAuth2_39_0FileSystemRights = 2,
}

/// <summary>How a grant would be expressed. Zero always refuses.</summary>
public enum ServiceOwnershipGrantMechanism
{
    Unspecified = 0,

    /// <summary>
    /// RETIRED, cycle 48. Cycle 46 MEASURED this CNG key-object security-descriptor
    /// mechanism AND REJECTED it as a non-reversible one-way lock. It is kept as a
    /// bounded, explicitly UNSUPPORTED historical classification for closed
    /// parsing only. No profile may ever authorize it. The numeric value is
    /// preserved and never reused.
    /// </summary>
    CngSecurityDescriptor = 1,
    CspKeyFileDacl = 2,

    /// <summary>
    /// APPROVED, cycle 48 (schema v3). Cycle 47 MEASURED this Microsoft Software
    /// KSP backing-file DACL grant as REVERSIBLE across all 52 measured rows.
    /// </summary>
    MicrosoftSoftwareKspBackingFileDacl = 3,
}

/// <summary>
/// Closed identifier for the exact captured owner/group/DACL self-relative file
/// security descriptor representation this contract can structurally parse and
/// classify. Zero always refuses. There is exactly ONE supported member: any other
/// shape fails closed as unsupported for promotion.
/// </summary>
public enum ServiceOwnershipDescriptorFormat
{
    Unspecified = 0,
    MicrosoftSoftwareKspBackingFileSelfRelativeV1 = 1,
}

/// <summary>
/// A FIXED key-storage-root IDENTIFIER - an enum/token, NEVER an arbitrary path
/// string. The pure contract must never compose or expose a filesystem path; a
/// future fixed I/O shim combines a validated leaf name with the ONE hard-coded
/// root this identifier names. Zero always refuses.
/// </summary>
public enum ServiceOwnershipKeyStorageRoot
{
    Unspecified = 0,
    MicrosoftSoftwareKeyStorageProviderMachineKeys = 1,
}

/// <summary>
/// Bounded classification of an OBSERVED descriptor against captured/recorded
/// state, derived from PARSED structural semantics only - never from a
/// caller-supplied boolean. Zero always refuses. There is deliberately NO
/// "Unavailable" member (that is a future I/O-boundary concern) and NO
/// "KeyIdentityMismatch" member (that is a future fixed-mapping-observer concern).
/// </summary>
public enum ServiceOwnershipDescriptorClassification
{
    Unspecified = 0,
    MatchesCapturedPriorState = 1,
    MatchesRecordedGrant = 2,
    Diverged = 3,
}

/// <summary>Bounded classification of the prior permission state. Zero always refuses.</summary>
public enum ServiceOwnershipPriorDaclState
{
    Unspecified = 0,
    Absent = 1,
    Empty = 2,
    Present = 3,
}

/// <summary>Lifecycle of a single ownership entry. Zero always refuses.</summary>
public enum ServiceOwnershipLifecycleState
{
    Unspecified = 0,
    Intended = 1,
    Active = 2,
    Restoring = 3,
    Restored = 4,
    Stale = 5,
    Foreign = 6,
}

/// <summary>Crash-consistent transaction state of the whole ledger. Zero always refuses.</summary>
public enum ServiceOwnershipTransactionState
{
    Unspecified = 0,
    Idle = 1,
    Preparing = 2,
    CredentialMutated = 3,
    LedgerCommitted = 4,
    Restoring = 5,
    Done = 6,
}

/// <summary>
/// Closed set of ledger outcomes. Values are EXPLICIT so a reorder cannot silently
/// re-point a persisted or interop value at a different meaning.
///
/// SLOT 2 IS RETIRED, PERMANENTLY. It held <c>ValidActive</c>. At the time slot 2
/// was retired, no approved rights profile existed, and cycle 38b removed it
/// outright rather than leaving an unreachable success state in the vocabulary.
/// The slot stays empty forever and its number must NEVER be reused.
///
/// CYCLE 82 AMENDMENT - THIS IS WHAT CHANGED. Active ledger acceptance IS now
/// authorized, at the NEW value <c>Active = 9</c>, under the EXACT approved
/// profile only. Retiring slot 2 and authorizing active acceptance are separate
/// facts: the retirement was about a number that had already been published with
/// a different meaning, so the authorization is expressed at a fresh number
/// rather than by resurrecting the old one. The parser's former unconditional
/// active-state brake is GONE; what controls now is
/// <see cref="ServiceOwnershipLedgerContract.TryAuthorizeActiveRights"/> plus
/// every other entry, cross-entry and document gate.
/// </summary>
public enum ServiceOwnershipLedgerOutcome
{
    Absent = 0,
    ValidEmpty = 1,
    Malformed = 3,
    Unsupported = 4,
    Foreign = 5,
    Inconsistent = 6,
    InProgress = 7,
    Stale = 8,

    /// <summary>
    /// CYCLE 82 (D1). A COMPLETE, COHERENT, fully authorized active ledger: the
    /// document is <c>done</c>, it holds at least one entry, no entry is stale,
    /// EVERY entry is <c>active</c>, and every one of them passed exact approved
    /// profile authorization. Mixed, contradictory, incomplete, foreign, stale or
    /// unsupported states can never produce this value.
    /// </summary>
    Active = 9,
}

/// <summary>
/// Closed, bounded refusal vocabulary. Every refusal maps to exactly one of these;
/// no exception text, path or free-form message ever escapes the validator.
/// </summary>
public enum ServiceOwnershipLedgerInvalidReason
{
    None,
    MalformedJson,
    OversizedInput,
    UnsupportedSchemaVersion,
    UnknownProperty,
    ProhibitedProperty,
    DuplicateProperty,
    MissingProperty,
    WrongType,
    WrongMarker,
    WrongFeatureId,
    InvalidGeneration,
    InvalidTransactionState,
    InvalidTimestamp,
    InvalidOperationId,
    InvalidInstallationOwnershipId,
    TooManyEntries,
    TooManyJobIds,
    InvalidEntryId,
    DuplicateEntryId,
    InvalidJobId,
    DuplicateJobId,
    InvalidOwnerSid,
    InvalidServiceSid,
    OwnerSidEqualsServiceSid,
    InvalidCredentialKind,
    InvalidProvenance,
    InvalidProviderKind,
    InvalidGrantMechanism,
    InvalidThumbprint,
    InvalidKeyIdentity,
    InvalidRightsMaskFormat,
    ZeroRightsMask,
    UnknownRightsBit,
    ProhibitedManagementRight,
    NoApprovedRightsProfile,
    UnsupportedProviderRightsVocabulary,
    ProviderMechanismMismatch,
    InvalidRightsPolicyVersion,
    InvalidPriorDaclState,
    PriorDaclPayloadNotAllowed,
    PriorDaclPayloadMissing,
    InvalidBase64,
    OversizedPriorDacl,
    PriorDaclHashMismatch,
    InvalidCapturedStateBinding,
    CapturedStateBindingMismatch,
    CrossOwnerCertificateCollision,
    CrossOwnerJobCollision,
    InconsistentSharedCertificateFields,
    InvalidLifecycleState,
    ForeignOwnership,

    // ---- schema v2 additions, appended so no existing value shifts -----------

    /// <summary>The rightsProfileId token is not a declared profile identifier.</summary>
    InvalidRightsProfileId,

    /// <summary>The declared profile does not belong to the declared provider kind.</summary>
    ProviderProfileMismatch,

    /// <summary>
    /// A BROAD provider classification (<c>cng</c>) cannot carry the approved
    /// profile. Kept distinct from the legacy-CSP vocabulary refusal because the
    /// CNG rights vocabulary IS understood here; what is not approved is the broad
    /// classification standing in for the one measured provider.
    /// </summary>
    BroadProviderClassificationNotApproved,

    /// <summary>The mask omits at least one right the approved profile requires.</summary>
    ApprovedRightsProfileBitMissing,

    /// <summary>The mask carries a known right the approved profile does not include.</summary>
    ApprovedRightsProfileBitNotPermitted,

    /// <summary>
    /// GenericRead. Refused by its OWN reason and never normalised into the
    /// composite Windows would persist for it.
    /// </summary>
    GenericReadRightNotAuthorized,

    /// <summary>
    /// RETIRED AS A PRODUCED REASON, cycle 82 (D1). This was the terminal
    /// active-lifecycle brake: it refused every active entry, including one the
    /// exact approved profile authorised. Brian authorised narrow active
    /// acceptance, so the parser no longer produces this value at all - an active
    /// entry either passes every gate or is refused by the SPECIFIC gate it
    /// failed. The member is KEPT, never renumbered and never removed, because
    /// <see cref="ServiceOwnershipLedgerInvalidReason"/> has no explicit values
    /// and deleting it would silently shift every later reason.
    /// </summary>
    ActiveLifecycleAcceptanceNotAuthorized,

    // ---- schema v3 additions (cycle 48), appended so no existing value shifts ----

    /// <summary>
    /// The CNG key-object descriptor mechanism was MEASURED AND REJECTED (cycle 46
    /// one-way lock). It is non-reversible and permanently unsupported: no profile,
    /// provider pairing or lifecycle state may ever authorize it.
    /// </summary>
    RetiredCngSecurityDescriptorMechanismUnsupported,

    /// <summary>The provider-returned unique name is not a single bounded leaf token.</summary>
    InvalidProviderUniqueName,

    /// <summary>The key-storage-root token is not a declared fixed-root identifier.</summary>
    InvalidKeyStorageRoot,

    /// <summary>The descriptor-format token is not a declared closed format identifier.</summary>
    InvalidDescriptorFormat,

    /// <summary>The prior descriptor bytes could not be structurally parsed at all.</summary>
    PriorDescriptorMalformed,

    /// <summary>The prior descriptor parsed, but its shape is not the one supported shape.</summary>
    PriorDescriptorUnsupportedShape,

    /// <summary>The prior descriptor carries a SACL, which is out of scope for promotion.</summary>
    PriorDescriptorContainsSacl,

    /// <summary>The prior descriptor's DACL is not protected (SDDL "D:P").</summary>
    PriorDescriptorNotProtected,

    /// <summary>
    /// The prior descriptor already carries an ACE for the entry's own service SID,
    /// including GenericRead or any superset. Nothing may be granted twice.
    /// </summary>
    PriorDescriptorServiceAceAlreadyPresent,
}

/// <summary>
/// A validated key-rights mask. Deliberately a distinct UNSIGNED type: the highest
/// published right is negative as a signed 32-bit integer, so any round trip
/// through a signed value would corrupt it. The wire form is always exactly eight
/// UPPERCASE hexadecimal characters.
/// </summary>
public readonly struct ServiceOwnershipRightsMask : IEquatable<ServiceOwnershipRightsMask>
{
    public ServiceOwnershipRightsMask(uint value) => Value = value;

    public uint Value { get; }

    public string ToWireText() => Value.ToString("X8", CultureInfo.InvariantCulture);

    public bool Equals(ServiceOwnershipRightsMask other) => Value == other.Value;

    public override bool Equals(object? obj) =>
        obj is ServiceOwnershipRightsMask other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => ToWireText();

    public static bool operator ==(ServiceOwnershipRightsMask left, ServiceOwnershipRightsMask right) =>
        left.Equals(right);

    public static bool operator !=(ServiceOwnershipRightsMask left, ServiceOwnershipRightsMask right) =>
        !left.Equals(right);
}

/// <summary>
/// One structurally parsed ACE from a self-relative file security descriptor.
/// Deliberately a plain data record - no dependency on any Windows ACL type.
/// </summary>
public sealed class ServiceOwnershipParsedAce
{
    public byte AceType { get; init; }

    public byte AceFlags { get; init; }

    public uint Mask { get; init; }

    public string Sid { get; init; } = string.Empty;
}

/// <summary>
/// A structurally parsed self-relative file security descriptor: owner, group,
/// control flags and the DACL's ACEs (a SACL, if present, is recorded only as
/// present/absent - its content is out of scope because its mere presence already
/// disqualifies the shape from being supported for promotion).
/// </summary>
public sealed class ServiceOwnershipParsedFileSecurityDescriptor
{
    public byte Revision { get; init; }

    public ushort ControlFlags { get; init; }

    public string OwnerSid { get; init; } = string.Empty;

    public string GroupSid { get; init; } = string.Empty;

    public bool DaclPresent { get; init; }

    public bool SaclPresent { get; init; }

    public IReadOnlyList<ServiceOwnershipParsedAce> Aces { get; init; } = Array.Empty<ServiceOwnershipParsedAce>();
}

/// <summary>
/// Compile-time boundaries, wire tokens, the grounded rights vocabulary and the
/// captured-state binding grammar. Nothing here is configurable at run time: there
/// is no environment variable, command-line switch, configuration file or machine
/// key that can widen any bound or add any token.
/// </summary>
public static class ServiceOwnershipLedgerContract
{
    // ---- identity ----------------------------------------------------------

    /// <summary>
    /// The ONLY supported schema version. Schema v1 and v2 are PERMANENTLY retired
    /// and are refused as <c>UnsupportedSchemaVersion</c>; there is no migration
    /// path. Cycle 48 bumped v2 to v3 because the covered entry shape changed:
    /// v3 adds providerUniqueName, keyStorageRoot and descriptorFormat, and retires
    /// the CNG key-object descriptor mechanism as an authorizable profile.
    /// </summary>
    public const int LedgerSchemaVersion = 3;

    /// <summary>The exact marker that proves this feature owns a ledger document.</summary>
    public const string ProductOwnershipMarker = "PAXCookbook.ServiceOwnership.v1";

    /// <summary>The exact managed-feature identity this ledger governs.</summary>
    public const string ManagedFeatureId = "service-credential-ownership";

    /// <summary>
    /// The service ownership ledger leaf name.
    ///
    /// This is a COINCIDENTAL HOMONYM of ProvisioningContract.LedgerFileName. That
    /// constant names the ManagedChefKeys ledger, a DIFFERENT file in a DIFFERENT
    /// directory with a DIFFERENT schema, a DIFFERENT ownership marker and a
    /// DIFFERENT feature id, owned by a DIFFERENT feature that has live readers and
    /// writers. This one has neither. Neither constant is derived from the other,
    /// and neither schema is derived from the other. They simply spell the same
    /// words. Do not redirect either at the other.
    /// </summary>
    public const string LedgerFileName = "ownership-ledger.json";

    /// <summary>The only rights-policy version an entry may declare.</summary>
    public const int RightsPolicyVersion = 3;

    // ---- bounds ------------------------------------------------------------

    public const int MaxLedgerBytes = 262144;
    public const int MaxEntries = 64;
    public const int MaxJobIdsPerEntry = 64;
    public const int MaxStringLength = 128;
    public const int MaxKeyIdentityLength = 128;
    public const int MaxProviderUniqueNameLength = 128;
    public const int MaxPriorDaclDecodedBytes = 65536;
    public const int MaxPriorDaclBase64Length = 87400;
    public const int MaxSidLength = 191;
    public const int MaxSidSubAuthorities = 15;

    private const int TimestampLength = 20;
    private const int ThumbprintSha1Length = 40;
    private const int Sha256HexLength = 64;
    private const int RightsMaskTextLength = 8;
    private const ulong MaxIdentifierAuthority = 281474976710655UL;
    private const ulong MaxSubAuthority = 4294967295UL;

    // ---- grounded rights vocabulary ----------------------------------------
    //
    // These are the published key-rights values (Microsoft Learn, updated
    // 2026-05-27). They are restated here as plain unsigned constants precisely so
    // this file never has to reference the Windows-only permission API. Nothing is
    // invented and nothing is extended.

    public const uint ReadDataRight = 0x00000001u;
    public const uint WriteDataRight = 0x00000002u;
    public const uint ReadExtendedAttributesRight = 0x00000008u;
    public const uint WriteExtendedAttributesRight = 0x00000010u;
    public const uint ReadAttributesRight = 0x00000080u;
    public const uint WriteAttributesRight = 0x00000100u;
    public const uint DeleteRight = 0x00010000u;
    public const uint ReadPermissionsRight = 0x00020000u;
    public const uint ChangePermissionsRight = 0x00040000u;
    public const uint TakeOwnershipRight = 0x00080000u;
    public const uint SynchronizeRight = 0x00100000u;

    /// <summary>The published aggregate; equal to the OR of the eleven specific bits.</summary>
    public const uint FullControlRights = 0x001F019Bu;

    public const uint GenericAllRight = 0x10000000u;
    public const uint GenericExecuteRight = 0x20000000u;
    public const uint GenericWriteRight = 0x40000000u;
    public const uint GenericReadRight = 0x80000000u;

    /// <summary>The OR of every published member. A bit outside this is unknown.</summary>
    public const uint KnownRightsBits = 0xF01F019Bu;

    /// <summary>
    /// Rights that would let the grantee manage the object itself rather than merely
    /// use it: Delete, ChangePermissions, TakeOwnership, GenericAll, GenericWrite.
    /// Kept SEPARATE from the known set and from the approved profile on purpose.
    /// </summary>
    public const uint ProhibitedManagementRightsBits = 0x500D0000u;

    /// <summary>
    /// GenericExecute. Its published description is "Not used.", so it is refused
    /// rather than silently accepted as a harmless known bit.
    /// </summary>
    public const uint NotUsedRightsBits = 0x20000000u;

    // ---- FileSystemRights vocabulary (cycle 48, schema v3) -------------------
    //
    // A SEPARATE, DISTINCT vocabulary from the CryptoKeyRights constants above.
    // Published FileSystemRights values (Microsoft Learn). Four of the fifteen
    // specific bits happen to share numeric values with their CryptoKeyRights
    // counterparts, but the two FullControl aggregates DIFFER (file 0x001F01FF vs
    // key-object 0x001F019B), proving the vocabularies are genuinely distinct.
    // Nothing here is aliased to a CryptoKeyRights constant.

    public const uint FileReadDataRight = 0x00000001u;
    public const uint FileWriteDataRight = 0x00000002u;
    public const uint FileAppendDataRight = 0x00000004u;
    public const uint FileReadExtendedAttributesRight = 0x00000008u;
    public const uint FileWriteExtendedAttributesRight = 0x00000010u;
    public const uint FileExecuteFileRight = 0x00000020u;
    public const uint FileDeleteSubdirectoriesAndFilesRight = 0x00000040u;
    public const uint FileReadAttributesRight = 0x00000080u;
    public const uint FileWriteAttributesRight = 0x00000100u;
    public const uint FileDeleteRight = 0x00010000u;
    public const uint FileReadPermissionsRight = 0x00020000u;
    public const uint FileChangePermissionsRight = 0x00040000u;
    public const uint FileTakeOwnershipRight = 0x00080000u;
    public const uint FileSynchronizeRight = 0x00100000u;

    /// <summary>The published FILE aggregate; the OR of the fifteen specific file bits. DISTINCT from the key-object FullControlRights.</summary>
    public const uint FileFullControlRights =
        FileReadDataRight | FileWriteDataRight | FileAppendDataRight | FileReadExtendedAttributesRight
        | FileWriteExtendedAttributesRight | FileExecuteFileRight | FileDeleteSubdirectoriesAndFilesRight
        | FileReadAttributesRight | FileWriteAttributesRight | FileDeleteRight | FileReadPermissionsRight
        | FileChangePermissionsRight | FileTakeOwnershipRight | FileSynchronizeRight;

    public const uint FileGenericAllRight = 0x10000000u;
    public const uint FileGenericExecuteRight = 0x20000000u;
    public const uint FileGenericWriteRight = 0x40000000u;
    public const uint FileGenericReadRight = 0x80000000u;

    /// <summary>The OR of every published FILE member. A bit outside this is unknown for the FILE vocabulary.</summary>
    public const uint FileKnownRightsBits =
        FileFullControlRights | FileGenericAllRight | FileGenericExecuteRight | FileGenericWriteRight | FileGenericReadRight;

    /// <summary>
    /// Prohibited for the FILE vocabulary: every write, delete, ownership,
    /// permission-management, execute and generic (all/execute/write) right.
    /// </summary>
    public const uint FileProhibitedManagementRightsBits =
        FileWriteDataRight | FileAppendDataRight | FileWriteExtendedAttributesRight | FileExecuteFileRight
        | FileDeleteSubdirectoriesAndFilesRight | FileWriteAttributesRight | FileDeleteRight
        | FileChangePermissionsRight | FileTakeOwnershipRight
        | FileGenericAllRight | FileGenericExecuteRight | FileGenericWriteRight;

    /// <summary>
    /// THE ONE APPROVED FILE MASK. DERIVED from the four measured rights, never a
    /// literal. Cycle 47 measured this exact combination as the reversible
    /// Microsoft Software KSP backing-file grant. FileReadAttributes is
    /// deliberately EXCLUDED, matching the cycle-41e measurement this profile
    /// inherits scope from.
    /// </summary>
    public const uint ApprovedFileRightsProfileMask =
        FileReadDataRight | FileReadExtendedAttributesRight | FileReadPermissionsRight | FileSynchronizeRight;

    /// <summary>The one approved rights-profile identifier (schema v3: the backing-file profile).</summary>
    public const ServiceOwnershipRightsProfileId ApprovedRightsProfileId =
        ServiceOwnershipRightsProfileId.MicrosoftSoftwareKspBackingFileRsa2048Ps256AzureIdentity1_18_0Msal4_82_1GraphAuth2_39_0FileSystemRights;

    /// <summary>The one provider kind the approved profile covers.</summary>
    public const ServiceOwnershipPrivateKeyProviderKind ApprovedRightsProfileProviderKind =
        ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider;

    /// <summary>The one grant mechanism the approved profile covers (schema v3: the backing-file DACL mechanism, NOT the retired CNG mechanism).</summary>
    public const ServiceOwnershipGrantMechanism ApprovedRightsProfileGrantMechanism =
        ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl;

    /// <summary>The one descriptor format the approved profile covers.</summary>
    public const ServiceOwnershipDescriptorFormat ApprovedDescriptorFormat =
        ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1;

    /// <summary>
    /// Numerically equal to <see cref="ApprovedFileRightsProfileMask"/>. Kept as a
    /// separate name for readability at call sites that predate the vocabulary
    /// split; never independently defined.
    /// </summary>
    public const uint ApprovedRightsProfileMask = ApprovedFileRightsProfileMask;

    /// <summary>
    /// TRUE only in the narrow sense that ONE exact profile exists. Amended cycle
    /// 82: an entry MAY now activate, but ONLY on that one exact profile and only
    /// after every other entry, cross-entry and document gate passes. It still does
    /// NOT mean any broader provider, algorithm, key size, assertion algorithm or
    /// library version is covered, and it authorises no grant by itself.
    /// </summary>
    public static bool HasApprovedRightsProfile => true;

    /// <summary>True only for the ONE exactly measured provider kind.</summary>
    public static bool IsExactApprovedProviderKind(ServiceOwnershipPrivateKeyProviderKind kind) =>
        kind == ApprovedRightsProfileProviderKind;

    /// <summary>
    /// True only when the profile is the approved one AND it is paired with the
    /// exact provider kind it was measured on. A broad classification can never
    /// inherit it.
    /// </summary>
    public static bool IsApprovedRightsProfile(
        ServiceOwnershipRightsProfileId profileId,
        ServiceOwnershipPrivateKeyProviderKind kind) =>
        profileId == ApprovedRightsProfileId && IsExactApprovedProviderKind(kind);

    private static readonly uint[] SpecificRightsBitsStorage =
    {
        ReadDataRight, WriteDataRight, ReadExtendedAttributesRight,
        WriteExtendedAttributesRight, ReadAttributesRight, WriteAttributesRight,
        DeleteRight, ReadPermissionsRight, ChangePermissionsRight,
        TakeOwnershipRight, SynchronizeRight,
    };

    public static IReadOnlyList<uint> SpecificRightsBits { get; } =
        Array.AsReadOnly(SpecificRightsBitsStorage);

    private static readonly uint[] SpecificFileRightsBitsStorage =
    {
        FileReadDataRight, FileWriteDataRight, FileAppendDataRight, FileReadExtendedAttributesRight,
        FileWriteExtendedAttributesRight, FileExecuteFileRight, FileDeleteSubdirectoriesAndFilesRight,
        FileReadAttributesRight, FileWriteAttributesRight, FileDeleteRight, FileReadPermissionsRight,
        FileChangePermissionsRight, FileTakeOwnershipRight, FileSynchronizeRight,
    };

    /// <summary>The fifteen specific FILE rights bits, distinct from <see cref="SpecificRightsBits"/>.</summary>
    public static IReadOnlyList<uint> SpecificFileRightsBits { get; } =
        Array.AsReadOnly(SpecificFileRightsBitsStorage);

    // ---- closed property sets ----------------------------------------------

    private static readonly string[] DocumentPropertyNamesStorage =
    {
        "schemaVersion", "productOwnershipMarker", "managedFeatureId",
        "installationOwnershipId", "generation", "transactionState", "entries",
        "createdUtc", "updatedUtc", "lastOperationId",
    };

    private static readonly string[] EntryPropertyNamesStorage =
    {
        "entryId", "owningUserSid", "serviceSid", "credentialKind",
        "certificateThumbprintSha1", "provenance", "privateKeyProviderKind",
        "rightsProfileId", "keyIdentity", "grantMechanism", "grantedRightsMask",
        "rightsPolicyVersion", "priorDaclState", "priorDaclBytesBase64",
        "priorDaclSha256", "capturedStateBindingSha256", "associatedPromotedJobIds",
        "lifecycleState", "createdUtc", "updatedUtc",
        "providerUniqueName", "keyStorageRoot", "descriptorFormat",
    };

    private static readonly string[] ProhibitedPropertyNamesStorage =
    {
        "clientSecret", "secret", "password", "token", "claim", "claims", "tenantId",
        "clientId", "upn", "account", "privateKey", "privateKeyBytes", "pfx",
        "certificate", "certificateBytes", "path", "filePath", "registryPath",
        "command", "arguments", "environment", "subject", "issuer", "serialNumber",
        "message", "error", "exception", "stackTrace",
    };

    private static readonly string[] BindingFieldOrderStorage =
    {
        "generation", "owningUserSid", "serviceSid", "certificateThumbprintSha1",
        "privateKeyProviderKind", "rightsProfileId", "keyIdentity", "grantMechanism",
        "grantedRightsMask", "priorDaclState", "priorDaclSha256",
        "providerUniqueName", "keyStorageRoot", "descriptorFormat",
    };

    public static IReadOnlyList<string> DocumentPropertyNames { get; } =
        Array.AsReadOnly(DocumentPropertyNamesStorage);

    public static IReadOnlyList<string> EntryPropertyNames { get; } =
        Array.AsReadOnly(EntryPropertyNamesStorage);

    public static IReadOnlyList<string> ProhibitedPropertyNames { get; } =
        Array.AsReadOnly(ProhibitedPropertyNamesStorage);

    public static IReadOnlyList<string> BindingFieldOrder { get; } =
        Array.AsReadOnly(BindingFieldOrderStorage);

    internal static readonly HashSet<string> DocumentPropertySet =
        new(DocumentPropertyNamesStorage, StringComparer.Ordinal);

    internal static readonly HashSet<string> EntryPropertySet =
        new(EntryPropertyNamesStorage, StringComparer.Ordinal);

    private static readonly HashSet<string> ProhibitedPropertySet =
        new(ProhibitedPropertyNamesStorage, StringComparer.OrdinalIgnoreCase);

    internal static bool IsProhibitedPropertyName(string name) =>
        ProhibitedPropertySet.Contains(name);

    // ---- wire tokens -------------------------------------------------------
    //
    // Lowercase-hyphen, CASE SENSITIVE. The zero value has no token at all, so an
    // Unspecified value can never be written or read back.

    public static string ToWireToken(ServiceOwnershipCredentialKind value) => value switch
    {
        ServiceOwnershipCredentialKind.PersonalAppRegistrationCertificate => "personal-app-registration-certificate",
        _ => string.Empty,
    };

    public static string ToWireToken(ServiceOwnershipProvenance value) => value switch
    {
        ServiceOwnershipProvenance.Referenced => "referenced",
        _ => string.Empty,
    };

    public static string ToWireToken(ServiceOwnershipPrivateKeyProviderKind value) => value switch
    {
        ServiceOwnershipPrivateKeyProviderKind.Cng => "cng",
        ServiceOwnershipPrivateKeyProviderKind.LegacyCsp => "legacy-csp",
        ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider =>
            "microsoft-software-key-storage-provider",
        _ => string.Empty,
    };

    public static string ToWireToken(ServiceOwnershipRightsProfileId value) => value switch
    {
        ServiceOwnershipRightsProfileId.MicrosoftSoftwareKspRsa2048Ps256AzureIdentity1_18_0Msal4_82_1GraphAuth2_39_0 =>
            "microsoft-software-ksp-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0",
        ServiceOwnershipRightsProfileId.MicrosoftSoftwareKspBackingFileRsa2048Ps256AzureIdentity1_18_0Msal4_82_1GraphAuth2_39_0FileSystemRights =>
            "microsoft-software-ksp-backing-file-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0-filesystemrights",
        _ => string.Empty,
    };

    public static string ToWireToken(ServiceOwnershipGrantMechanism value) => value switch
    {
        ServiceOwnershipGrantMechanism.CngSecurityDescriptor => "cng-security-descriptor",
        ServiceOwnershipGrantMechanism.CspKeyFileDacl => "csp-key-file-dacl",
        ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl => "microsoft-software-ksp-backing-file-dacl",
        _ => string.Empty,
    };

    public static string ToWireToken(ServiceOwnershipDescriptorFormat value) => value switch
    {
        ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1 =>
            "microsoft-software-ksp-backing-file-self-relative-v1",
        _ => string.Empty,
    };

    public static string ToWireToken(ServiceOwnershipKeyStorageRoot value) => value switch
    {
        ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys =>
            "microsoft-software-key-storage-provider-machine-keys",
        _ => string.Empty,
    };

    public static string ToWireToken(ServiceOwnershipPriorDaclState value) => value switch
    {
        ServiceOwnershipPriorDaclState.Absent => "absent",
        ServiceOwnershipPriorDaclState.Empty => "empty",
        ServiceOwnershipPriorDaclState.Present => "present",
        _ => string.Empty,
    };

    public static string ToWireToken(ServiceOwnershipLifecycleState value) => value switch
    {
        ServiceOwnershipLifecycleState.Intended => "intended",
        ServiceOwnershipLifecycleState.Active => "active",
        ServiceOwnershipLifecycleState.Restoring => "restoring",
        ServiceOwnershipLifecycleState.Restored => "restored",
        ServiceOwnershipLifecycleState.Stale => "stale",
        ServiceOwnershipLifecycleState.Foreign => "foreign",
        _ => string.Empty,
    };

    public static string ToWireToken(ServiceOwnershipTransactionState value) => value switch
    {
        ServiceOwnershipTransactionState.Idle => "idle",
        ServiceOwnershipTransactionState.Preparing => "preparing",
        ServiceOwnershipTransactionState.CredentialMutated => "credential-mutated",
        ServiceOwnershipTransactionState.LedgerCommitted => "ledger-committed",
        ServiceOwnershipTransactionState.Restoring => "restoring",
        ServiceOwnershipTransactionState.Done => "done",
        _ => string.Empty,
    };

    public static bool TryParseWireToken(string? token, out ServiceOwnershipCredentialKind value)
    {
        value = ServiceOwnershipCredentialKind.Unspecified;
        if (token == "personal-app-registration-certificate")
        {
            value = ServiceOwnershipCredentialKind.PersonalAppRegistrationCertificate;
            return true;
        }
        return false;
    }

    public static bool TryParseWireToken(string? token, out ServiceOwnershipProvenance value)
    {
        value = ServiceOwnershipProvenance.Unspecified;
        if (token == "referenced")
        {
            value = ServiceOwnershipProvenance.Referenced;
            return true;
        }
        return false;
    }

    public static bool TryParseWireToken(string? token, out ServiceOwnershipPrivateKeyProviderKind value)
    {
        switch (token)
        {
            case "cng": value = ServiceOwnershipPrivateKeyProviderKind.Cng; return true;
            case "legacy-csp": value = ServiceOwnershipPrivateKeyProviderKind.LegacyCsp; return true;
            case "microsoft-software-key-storage-provider":
                value = ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider;
                return true;
            default: value = ServiceOwnershipPrivateKeyProviderKind.Unspecified; return false;
        }
    }

    public static bool TryParseWireToken(string? token, out ServiceOwnershipRightsProfileId value)
    {
        switch (token)
        {
            case "microsoft-software-ksp-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0":
                value = ServiceOwnershipRightsProfileId
                    .MicrosoftSoftwareKspRsa2048Ps256AzureIdentity1_18_0Msal4_82_1GraphAuth2_39_0;
                return true;
            case "microsoft-software-ksp-backing-file-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0-filesystemrights":
                value = ServiceOwnershipRightsProfileId
                    .MicrosoftSoftwareKspBackingFileRsa2048Ps256AzureIdentity1_18_0Msal4_82_1GraphAuth2_39_0FileSystemRights;
                return true;
            default:
                value = ServiceOwnershipRightsProfileId.Unspecified;
                return false;
        }
    }

    public static bool TryParseWireToken(string? token, out ServiceOwnershipGrantMechanism value)
    {
        switch (token)
        {
            case "cng-security-descriptor": value = ServiceOwnershipGrantMechanism.CngSecurityDescriptor; return true;
            case "csp-key-file-dacl": value = ServiceOwnershipGrantMechanism.CspKeyFileDacl; return true;
            case "microsoft-software-ksp-backing-file-dacl":
                value = ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl;
                return true;
            default: value = ServiceOwnershipGrantMechanism.Unspecified; return false;
        }
    }

    public static bool TryParseWireToken(string? token, out ServiceOwnershipDescriptorFormat value)
    {
        if (token == "microsoft-software-ksp-backing-file-self-relative-v1")
        {
            value = ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1;
            return true;
        }
        value = ServiceOwnershipDescriptorFormat.Unspecified;
        return false;
    }

    public static bool TryParseWireToken(string? token, out ServiceOwnershipKeyStorageRoot value)
    {
        if (token == "microsoft-software-key-storage-provider-machine-keys")
        {
            value = ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys;
            return true;
        }
        value = ServiceOwnershipKeyStorageRoot.Unspecified;
        return false;
    }

    public static bool TryParseWireToken(string? token, out ServiceOwnershipPriorDaclState value)
    {
        switch (token)
        {
            case "absent": value = ServiceOwnershipPriorDaclState.Absent; return true;
            case "empty": value = ServiceOwnershipPriorDaclState.Empty; return true;
            case "present": value = ServiceOwnershipPriorDaclState.Present; return true;
            default: value = ServiceOwnershipPriorDaclState.Unspecified; return false;
        }
    }

    public static bool TryParseWireToken(string? token, out ServiceOwnershipLifecycleState value)
    {
        switch (token)
        {
            case "intended": value = ServiceOwnershipLifecycleState.Intended; return true;
            case "active": value = ServiceOwnershipLifecycleState.Active; return true;
            case "restoring": value = ServiceOwnershipLifecycleState.Restoring; return true;
            case "restored": value = ServiceOwnershipLifecycleState.Restored; return true;
            case "stale": value = ServiceOwnershipLifecycleState.Stale; return true;
            case "foreign": value = ServiceOwnershipLifecycleState.Foreign; return true;
            default: value = ServiceOwnershipLifecycleState.Unspecified; return false;
        }
    }

    public static bool TryParseWireToken(string? token, out ServiceOwnershipTransactionState value)
    {
        switch (token)
        {
            case "idle": value = ServiceOwnershipTransactionState.Idle; return true;
            case "preparing": value = ServiceOwnershipTransactionState.Preparing; return true;
            case "credential-mutated": value = ServiceOwnershipTransactionState.CredentialMutated; return true;
            case "ledger-committed": value = ServiceOwnershipTransactionState.LedgerCommitted; return true;
            case "restoring": value = ServiceOwnershipTransactionState.Restoring; return true;
            case "done": value = ServiceOwnershipTransactionState.Done; return true;
            default: value = ServiceOwnershipTransactionState.Unspecified; return false;
        }
    }

    // ---- captured-state binding --------------------------------------------

    /// <summary>
    /// Version 3. The prefix is bumped because the COVERED FIELD SET changed again:
    /// cycle 48 appends providerUniqueName, keyStorageRoot and descriptorFormat
    /// after priorDaclSha256. A v1 or v2 digest can therefore never be mistaken for
    /// a v3 digest.
    /// </summary>
    public const string BindingPreimagePrefix = "PAXCookbook.ServiceOwnership.Binding.v3";

    /// <summary>
    /// Escapes one preimage VALUE so the grammar stays unambiguous. The constrained
    /// field grammars make this a no-op in practice today; it is implemented and
    /// tested anyway so widening a grammar later cannot silently create a collision.
    /// Backslash is escaped first so an escape sequence can never be forged.
    /// </summary>
    public static string EscapeBindingValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value!.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '=': sb.Append("\\x3D"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds the exact binding preimage: a fixed prefix line, then the eleven
    /// covered fields in a fixed order as <c>name=value</c>, LF separated, no
    /// trailing newline. The prior hash is normalised to empty whenever no prior
    /// permission payload exists, so the same captured state always yields the same
    /// preimage.
    /// </summary>
    public static string BuildCapturedStateBindingPreimage(
        int generation,
        string? owningUserSid,
        string? serviceSid,
        string? certificateThumbprintSha1,
        ServiceOwnershipPrivateKeyProviderKind privateKeyProviderKind,
        ServiceOwnershipRightsProfileId rightsProfileId,
        string? keyIdentity,
        ServiceOwnershipGrantMechanism grantMechanism,
        string? grantedRightsMask,
        ServiceOwnershipPriorDaclState priorDaclState,
        string? priorDaclSha256,
        string? providerUniqueName,
        ServiceOwnershipKeyStorageRoot keyStorageRoot,
        ServiceOwnershipDescriptorFormat descriptorFormat)
    {
        string priorHash =
            priorDaclState is ServiceOwnershipPriorDaclState.Absent or ServiceOwnershipPriorDaclState.Empty
                ? string.Empty
                : priorDaclSha256 ?? string.Empty;

        var sb = new StringBuilder(BindingPreimagePrefix);
        AppendBindingField(sb, "generation", generation.ToString(CultureInfo.InvariantCulture));
        AppendBindingField(sb, "owningUserSid", owningUserSid);
        AppendBindingField(sb, "serviceSid", serviceSid);
        AppendBindingField(sb, "certificateThumbprintSha1", certificateThumbprintSha1);
        AppendBindingField(sb, "privateKeyProviderKind", ToWireToken(privateKeyProviderKind));
        AppendBindingField(sb, "rightsProfileId", ToWireToken(rightsProfileId));
        AppendBindingField(sb, "keyIdentity", keyIdentity);
        AppendBindingField(sb, "grantMechanism", ToWireToken(grantMechanism));
        AppendBindingField(sb, "grantedRightsMask", grantedRightsMask);
        AppendBindingField(sb, "priorDaclState", ToWireToken(priorDaclState));
        AppendBindingField(sb, "priorDaclSha256", priorHash);
        AppendBindingField(sb, "providerUniqueName", providerUniqueName);
        AppendBindingField(sb, "keyStorageRoot", ToWireToken(keyStorageRoot));
        AppendBindingField(sb, "descriptorFormat", ToWireToken(descriptorFormat));
        return sb.ToString();
    }

    private static void AppendBindingField(StringBuilder sb, string name, string? value)
    {
        sb.Append('\n').Append(name).Append('=').Append(EscapeBindingValue(value));
    }

    /// <summary>SHA-256 over the UTF-8 (no BOM) preimage bytes, as 64 uppercase hex characters.</summary>
    public static string ComputeCapturedStateBinding(
        int generation,
        string? owningUserSid,
        string? serviceSid,
        string? certificateThumbprintSha1,
        ServiceOwnershipPrivateKeyProviderKind privateKeyProviderKind,
        ServiceOwnershipRightsProfileId rightsProfileId,
        string? keyIdentity,
        ServiceOwnershipGrantMechanism grantMechanism,
        string? grantedRightsMask,
        ServiceOwnershipPriorDaclState priorDaclState,
        string? priorDaclSha256,
        string? providerUniqueName,
        ServiceOwnershipKeyStorageRoot keyStorageRoot,
        ServiceOwnershipDescriptorFormat descriptorFormat)
    {
        string preimage = BuildCapturedStateBindingPreimage(
            generation, owningUserSid, serviceSid, certificateThumbprintSha1,
            privateKeyProviderKind, rightsProfileId, keyIdentity, grantMechanism,
            grantedRightsMask, priorDaclState, priorDaclSha256,
            providerUniqueName, keyStorageRoot, descriptorFormat);

        byte[] bytes = new UTF8Encoding(false).GetBytes(preimage);
        return ToUpperHex(SHA256.HashData(bytes));
    }

    internal static string ToUpperHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // ---- bounded grammars --------------------------------------------------

    /// <summary>A 1..maxLength token drawn from [A-Za-z0-9._-] only.</summary>
    public static bool IsValidBoundedToken(string? value, int maxLength)
    {
        if (value is null || value.Length == 0 || value.Length > maxLength)
        {
            return false;
        }
        foreach (char c in value)
        {
            if (!IsTokenChar(c))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsTokenChar(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
        || c == '.' || c == '_' || c == '-';

    /// <summary>
    /// An OPAQUE provider identifier. Never a path. The allow-listed grammar alone
    /// already excludes every separator, drive letter, environment-expansion token
    /// and quoting character; the extra rules exclude relative-path spellings.
    /// </summary>
    public static bool IsValidKeyIdentity(string? keyIdentity)
    {
        if (!IsValidBoundedToken(keyIdentity, MaxKeyIdentityLength))
        {
            return false;
        }
        if (keyIdentity![0] == '.')
        {
            return false;
        }
        return !keyIdentity.Contains("..", StringComparison.Ordinal);
    }

    private static readonly string[] ReservedDeviceNamesStorage =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// THE PROVIDER-RETURNED UNIQUE NAME. A single bounded LEAF token - never a
    /// path. This is the strict grammar cycle 48 introduces on top of
    /// <see cref="IsValidBoundedToken"/>: it additionally refuses every reparse,
    /// traversal, drive, ADS, wildcard and reserved-device-name spelling, and a
    /// leading dot or a trailing dot/space, so the pure contract can never be made
    /// to compose or expose an arbitrary filesystem path. A future fixed I/O shim
    /// combines this validated leaf with the ONE hard-coded storage root.
    /// </summary>
    public static bool IsValidProviderUniqueName(string? value)
    {
        if (!IsValidBoundedToken(value, MaxProviderUniqueNameLength))
        {
            return false;
        }

        // The allow-listed charset in IsValidBoundedToken ([A-Za-z0-9._-]) already
        // excludes '\\', '/', ':', '*', '?', '<', '>', '|', '"' and whitespace, so a
        // separator, drive letter (C:) or ADS stream (name:stream) can never appear.
        // The remaining checks are spellings that charset alone does not exclude.
        if (value![0] == '.')
        {
            return false;
        }
        if (value[^1] == '.' || value[^1] == ' ')
        {
            return false;
        }
        if (value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string baseName = value;
        int dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            baseName = value[..dot];
        }
        foreach (string reserved in ReservedDeviceNamesStorage)
        {
            if (string.Equals(baseName, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsUppercaseSha1Thumbprint(string? value) =>
        IsUppercaseHex(value, ThumbprintSha1Length);

    public static bool IsUppercaseSha256Hex(string? value) =>
        IsUppercaseHex(value, Sha256HexLength);

    private static bool IsUppercaseHex(string? value, int length)
    {
        if (value is null || value.Length != length)
        {
            return false;
        }
        foreach (char c in value)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Canonical SID STRING form only: <c>S-1-&lt;authority&gt;-&lt;sub&gt;(-&lt;sub&gt;)*</c>,
    /// decimal components, no leading zeros, no whitespace, never a localised account
    /// name. This contract DERIVES no identifier; it only validates supplied text.
    /// </summary>
    public static bool IsCanonicalSidString(string? sid) =>
        TryParseSid(sid, out _, out _);

    /// <summary>A USER SID: authority 5, domain prefix 21, and at least one further subauthority.</summary>
    public static bool IsUserOwnerSid(string? sid)
    {
        if (IsWellKnownNonUserSid(sid))
        {
            return false;
        }
        if (!TryParseSid(sid, out ulong authority, out uint[] subs))
        {
            return false;
        }
        return authority == 5UL && subs.Length >= 2 && subs[0] == 21U;
    }

    private static bool IsWellKnownNonUserSid(string? sid)
    {
        if (sid is null)
        {
            return false;
        }
        switch (sid)
        {
            case "S-1-5-18":
            case "S-1-5-19":
            case "S-1-5-20":
            case "S-1-1-0":
            case "S-1-5-11":
                return true;
            default:
                break;
        }
        return sid.StartsWith("S-1-5-32-", StringComparison.Ordinal)
            || sid.StartsWith("S-1-5-80", StringComparison.Ordinal);
    }

    /// <summary>
    /// A per-service virtual account SID: authority 5, prefix 80, and at least one
    /// further subauthority. <c>S-1-5-80-0</c> is refused: it denotes the whole
    /// services group, not a specific service. No exact subauthority count is
    /// asserted, because no count is documented for a per-service SID.
    /// </summary>
    public static bool IsServiceVirtualAccountSid(string? sid)
    {
        if (!TryParseSid(sid, out ulong authority, out uint[] subs))
        {
            return false;
        }
        if (authority != 5UL || subs.Length < 2 || subs[0] != 80U)
        {
            return false;
        }
        return !(subs.Length == 2 && subs[1] == 0U);
    }

    private static bool TryParseSid(string? sid, out ulong authority, out uint[] subAuthorities)
    {
        authority = 0UL;
        subAuthorities = Array.Empty<uint>();

        if (sid is null || sid.Length == 0 || sid.Length > MaxSidLength)
        {
            return false;
        }

        string[] parts = sid.Split('-');
        if (parts.Length < 4)
        {
            return false;
        }
        if (!string.Equals(parts[0], "S", StringComparison.Ordinal)
            || !string.Equals(parts[1], "1", StringComparison.Ordinal))
        {
            return false;
        }
        if (!TryParseCanonicalDecimal(parts[2], out authority) || authority > MaxIdentifierAuthority)
        {
            return false;
        }

        int count = parts.Length - 3;
        if (count < 1 || count > MaxSidSubAuthorities)
        {
            return false;
        }

        var subs = new uint[count];
        for (int i = 0; i < count; i++)
        {
            if (!TryParseCanonicalDecimal(parts[3 + i], out ulong value) || value > MaxSubAuthority)
            {
                return false;
            }
            subs[i] = (uint)value;
        }

        subAuthorities = subs;
        return true;
    }

    private static bool TryParseCanonicalDecimal(string text, out ulong value)
    {
        value = 0UL;
        if (text.Length == 0 || text.Length > 20)
        {
            return false;
        }
        if (text.Length > 1 && text[0] == '0')
        {
            return false;
        }
        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // ---- rights handling ---------------------------------------------------

    /// <summary>
    /// Whether the CryptoKeyRights bit vocabulary applies at all. It does for CNG
    /// providers, broad or exact. A legacy CSP key is protected by a FILE DACL whose
    /// vocabulary differs, so its mask is never interpreted here. This says nothing
    /// about the approved PROFILE; see <see cref="IsApprovedRightsProfile"/>.
    /// </summary>
    public static bool IsRightsVocabularySupported(ServiceOwnershipPrivateKeyProviderKind kind) =>
        kind is ServiceOwnershipPrivateKeyProviderKind.Cng
            or ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider;

    /// <summary>
    /// True for the retired CNG key-object descriptor mechanism ONLY. Cycle 46
    /// measured and rejected it as a non-reversible one-way lock; cycle 48 keeps it
    /// representable for closed parsing but permanently unsupported for any grant.
    /// </summary>
    public static bool IsRetiredGrantMechanism(ServiceOwnershipGrantMechanism mechanism) =>
        mechanism == ServiceOwnershipGrantMechanism.CngSecurityDescriptor;

    public static bool IsMechanismPairedWith(
        ServiceOwnershipPrivateKeyProviderKind kind,
        ServiceOwnershipGrantMechanism mechanism) => kind switch
        {
            ServiceOwnershipPrivateKeyProviderKind.Cng =>
                mechanism == ServiceOwnershipGrantMechanism.CngSecurityDescriptor,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider =>
                mechanism == ServiceOwnershipGrantMechanism.CngSecurityDescriptor
                    || mechanism == ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipPrivateKeyProviderKind.LegacyCsp =>
                mechanism == ServiceOwnershipGrantMechanism.CspKeyFileDacl,
            _ => false,
        };

    /// <summary>
    /// Validates a wire mask: exactly eight UPPERCASE hexadecimal characters, parsed
    /// as UNSIGNED 32-bit, non-zero, no unknown bit, no prohibited management bit and
    /// no not-used bit. It deliberately does NOT decide activation; see
    /// <see cref="TryAuthorizeActiveRights"/>.
    /// </summary>
    public static bool TryValidateRightsMask(
        string? text,
        out ServiceOwnershipRightsMask mask,
        out ServiceOwnershipLedgerInvalidReason reason)
    {
        mask = default;

        if (text is null || text.Length != RightsMaskTextLength)
        {
            reason = ServiceOwnershipLedgerInvalidReason.InvalidRightsMaskFormat;
            return false;
        }
        foreach (char c in text)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                reason = ServiceOwnershipLedgerInvalidReason.InvalidRightsMaskFormat;
                return false;
            }
        }
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            reason = ServiceOwnershipLedgerInvalidReason.InvalidRightsMaskFormat;
            return false;
        }
        if (value == 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.ZeroRightsMask;
            return false;
        }
        if ((value & ~KnownRightsBits) != 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.UnknownRightsBit;
            return false;
        }
        if ((value & (ProhibitedManagementRightsBits | NotUsedRightsBits)) != 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight;
            return false;
        }

        mask = new ServiceOwnershipRightsMask(value);
        reason = ServiceOwnershipLedgerInvalidReason.None;
        return true;
    }

    /// <summary>
    /// Validates a wire mask against the SEPARATE FileSystemRights vocabulary
    /// (cycle 48, schema v3). Mirrors <see cref="TryValidateRightsMask"/> exactly,
    /// but against <see cref="FileKnownRightsBits"/> and
    /// <see cref="FileProhibitedManagementRightsBits"/> - never the CryptoKeyRights
    /// constants.
    /// </summary>
    public static bool TryValidateFileRightsMask(
        string? text,
        out ServiceOwnershipRightsMask mask,
        out ServiceOwnershipLedgerInvalidReason reason)
    {
        mask = default;

        if (text is null || text.Length != RightsMaskTextLength)
        {
            reason = ServiceOwnershipLedgerInvalidReason.InvalidRightsMaskFormat;
            return false;
        }
        foreach (char c in text)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                reason = ServiceOwnershipLedgerInvalidReason.InvalidRightsMaskFormat;
                return false;
            }
        }
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            reason = ServiceOwnershipLedgerInvalidReason.InvalidRightsMaskFormat;
            return false;
        }
        if (value == 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.ZeroRightsMask;
            return false;
        }
        if ((value & GenericReadRight) != 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.GenericReadRightNotAuthorized;
            return false;
        }
        if ((value & ~FileKnownRightsBits) != 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.UnknownRightsBit;
            return false;
        }
        if ((value & FileProhibitedManagementRightsBits) != 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight;
            return false;
        }

        mask = new ServiceOwnershipRightsMask(value);
        reason = ServiceOwnershipLedgerInvalidReason.None;
        return true;
    }

    /// <summary>
    /// THE EXACT PURE AUTHORIZER (cycle 48, schema v3). Returns true for EXACTLY ONE
    /// combination: the exact Microsoft Software KSP provider kind, the backing-file
    /// DACL mechanism, the one approved backing-file profile identifier, the one
    /// approved descriptor format, rights policy version 3, and a mask that equals
    /// <see cref="ApprovedFileRightsProfileMask"/> bit for bit. There are no
    /// supersets: an extra bit is refused just as firmly as a missing one.
    ///
    /// The RETIRED CNG key-object mechanism is checked FIRST and gets its own
    /// bounded reason: it is non-reversible and unsupported, never a mismatch.
    ///
    /// GENERIC READ IS CHECKED BEFORE the unknown-bit and prohibited-bit gates and
    /// gets its OWN reason. It is a KNOWN bit, so the unknown-bit gate would never
    /// catch it. It is also never normalised into the composite Windows persists
    /// for it.
    ///
    /// Amended cycle 82 (D1): returning true is now a NECESSARY condition for the
    /// raw parser to accept an active entry, but it is not a SUFFICIENT one. The
    /// entry must additionally pass every prior-descriptor, captured-state binding,
    /// promoted-job, timestamp, cross-entry and collision gate, and the document
    /// must be coherent, before an active ledger is reported.
    /// </summary>
    public static bool TryAuthorizeActiveRights(
        ServiceOwnershipRightsMask mask,
        ServiceOwnershipPrivateKeyProviderKind kind,
        ServiceOwnershipGrantMechanism mechanism,
        ServiceOwnershipRightsProfileId profileId,
        int rightsPolicyVersion,
        ServiceOwnershipDescriptorFormat descriptorFormat,
        out ServiceOwnershipLedgerInvalidReason reason)
    {
        if (IsRetiredGrantMechanism(mechanism))
        {
            reason = ServiceOwnershipLedgerInvalidReason.RetiredCngSecurityDescriptorMechanismUnsupported;
            return false;
        }
        if (!IsMechanismPairedWith(kind, mechanism))
        {
            reason = ServiceOwnershipLedgerInvalidReason.ProviderMechanismMismatch;
            return false;
        }
        if (mechanism != ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl)
        {
            reason = ServiceOwnershipLedgerInvalidReason.UnsupportedProviderRightsVocabulary;
            return false;
        }
        if (!IsExactApprovedProviderKind(kind))
        {
            reason = ServiceOwnershipLedgerInvalidReason.BroadProviderClassificationNotApproved;
            return false;
        }
        if (profileId == ServiceOwnershipRightsProfileId.Unspecified)
        {
            reason = ServiceOwnershipLedgerInvalidReason.NoApprovedRightsProfile;
            return false;
        }
        if (!IsApprovedRightsProfile(profileId, kind))
        {
            reason = ServiceOwnershipLedgerInvalidReason.ProviderProfileMismatch;
            return false;
        }
        if (descriptorFormat != ApprovedDescriptorFormat)
        {
            reason = ServiceOwnershipLedgerInvalidReason.InvalidDescriptorFormat;
            return false;
        }
        if (rightsPolicyVersion != RightsPolicyVersion)
        {
            reason = ServiceOwnershipLedgerInvalidReason.InvalidRightsPolicyVersion;
            return false;
        }
        if (mask.Value == 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.ZeroRightsMask;
            return false;
        }
        if ((mask.Value & GenericReadRight) != 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.GenericReadRightNotAuthorized;
            return false;
        }
        if ((mask.Value & ~FileKnownRightsBits) != 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.UnknownRightsBit;
            return false;
        }
        if ((mask.Value & FileProhibitedManagementRightsBits) != 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight;
            return false;
        }
        if ((mask.Value & ApprovedFileRightsProfileMask) != ApprovedFileRightsProfileMask)
        {
            reason = ServiceOwnershipLedgerInvalidReason.ApprovedRightsProfileBitMissing;
            return false;
        }
        if ((mask.Value & ~ApprovedFileRightsProfileMask) != 0u)
        {
            reason = ServiceOwnershipLedgerInvalidReason.ApprovedRightsProfileBitNotPermitted;
            return false;
        }
        if (!HasApprovedRightsProfile)
        {
            reason = ServiceOwnershipLedgerInvalidReason.NoApprovedRightsProfile;
            return false;
        }

        reason = ServiceOwnershipLedgerInvalidReason.None;
        return true;
    }

    // ---- structural file security descriptor parsing (cycle 48, schema v3) -
    //
    // A hand-rolled, pure, bounds-checked parser for the self-relative binary
    // SECURITY_DESCRIPTOR layout. It never references any Windows-only ACL type:
    // every offset, header and SID is walked byte by byte from plain published
    // layouts (SECURITY_DESCRIPTOR, SID, ACL, ACCESS_ALLOWED_ACE).

    private const int DescriptorHeaderLength = 20;
    private const ushort ControlSelfRelative = 0x8000;
    private const ushort ControlDaclPresent = 0x0004;
    private const ushort ControlDaclProtected = 0x1000;
    private const ushort ControlSaclPresent = 0x0010;

    private const byte AceTypeAccessAllowed = 0x00;
    private const byte RequiredAceFlags = 0x03; // ObjectInherit (0x01) | ContainerInherit (0x02)
    private const byte AceFlagInherited = 0x10;

    private const string RequiredOwnerSid = "S-1-5-32-544";
    private const string LocalSystemSid = "S-1-5-18";

    private const int MaxParsedAceCount = 256;

    /// <summary>
    /// Structurally parses a self-relative file security descriptor: header,
    /// owner SID, group SID, DACL header and every ACE header/mask/SID. Never
    /// throws; any bounds violation or inconsistency simply fails the parse.
    /// </summary>
    public static bool TryParseFileSecurityDescriptor(
        byte[]? bytes,
        out ServiceOwnershipParsedFileSecurityDescriptor? parsed)
    {
        parsed = null;
        if (bytes is null || bytes.Length < DescriptorHeaderLength)
        {
            return false;
        }

        try
        {
            byte revision = bytes[0];
            if (revision != 1)
            {
                return false;
            }

            ushort control = ReadUInt16LE(bytes, 2);
            if ((control & ControlSelfRelative) == 0)
            {
                return false;
            }

            uint offsetOwner = ReadUInt32LE(bytes, 4);
            uint offsetGroup = ReadUInt32LE(bytes, 8);
            uint offsetSacl = ReadUInt32LE(bytes, 12);
            uint offsetDacl = ReadUInt32LE(bytes, 16);

            if (!TryParseSidAt(bytes, offsetOwner, out string ownerSid))
            {
                return false;
            }
            if (!TryParseSidAt(bytes, offsetGroup, out string groupSid))
            {
                return false;
            }

            bool daclPresent = (control & ControlDaclPresent) != 0;
            bool saclPresent = (control & ControlSaclPresent) != 0;

            var aces = new List<ServiceOwnershipParsedAce>();
            if (daclPresent)
            {
                if (!TryParseAcl(bytes, offsetDacl, aces))
                {
                    return false;
                }
            }
            else if (offsetDacl != 0)
            {
                return false;
            }

            if (saclPresent)
            {
                var saclAces = new List<ServiceOwnershipParsedAce>();
                if (!TryParseAcl(bytes, offsetSacl, saclAces))
                {
                    return false;
                }
            }
            else if (offsetSacl != 0)
            {
                return false;
            }

            parsed = new ServiceOwnershipParsedFileSecurityDescriptor
            {
                Revision = revision,
                ControlFlags = control,
                OwnerSid = ownerSid,
                GroupSid = groupSid,
                DaclPresent = daclPresent,
                SaclPresent = saclPresent,
                Aces = aces.AsReadOnly(),
            };
            return true;
        }
        catch (Exception)
        {
            parsed = null;
            return false;
        }
    }

    private static bool TryParseAcl(byte[] bytes, uint offset, List<ServiceOwnershipParsedAce> aces)
    {
        if (offset == 0 || offset + 8 > (uint)bytes.Length)
        {
            return false;
        }

        ushort aclSize = ReadUInt16LE(bytes, offset + 2);
        ushort aceCount = ReadUInt16LE(bytes, offset + 4);
        if (aceCount > MaxParsedAceCount || offset + aclSize > (uint)bytes.Length)
        {
            return false;
        }

        uint cursor = offset + 8;
        for (int i = 0; i < aceCount; i++)
        {
            if (cursor + 4 > (uint)bytes.Length)
            {
                return false;
            }

            byte aceType = bytes[cursor];
            byte aceFlags = bytes[cursor + 1];
            ushort aceSize = ReadUInt16LE(bytes, cursor + 2);
            if (aceSize < 4 || cursor + aceSize > (uint)bytes.Length)
            {
                return false;
            }

            uint mask = 0u;
            string sid = string.Empty;
            if (aceType == AceTypeAccessAllowed)
            {
                if (aceSize < 8 || !TryParseSidAt(bytes, cursor + 8, out sid))
                {
                    return false;
                }
                mask = ReadUInt32LE(bytes, cursor + 4);
                int sidLength = ComputeSidByteLength(bytes, cursor + 8);
                if (8 + sidLength != aceSize)
                {
                    return false;
                }
            }

            aces.Add(new ServiceOwnershipParsedAce { AceType = aceType, AceFlags = aceFlags, Mask = mask, Sid = sid });
            cursor += aceSize;
        }

        return cursor <= offset + aclSize;
    }

    private static bool TryParseSidAt(byte[] bytes, uint offset, out string sid)
    {
        sid = string.Empty;
        if (offset == 0 || offset + 8 > (uint)bytes.Length)
        {
            return false;
        }

        byte revision = bytes[offset];
        if (revision != 1)
        {
            return false;
        }

        byte subCount = bytes[offset + 1];
        if (subCount > MaxSidSubAuthorities)
        {
            return false;
        }

        ulong authority = 0UL;
        for (int i = 0; i < 6; i++)
        {
            authority = (authority << 8) | bytes[offset + 2 + i];
        }

        int total = 8 + (4 * subCount);
        if (offset + total > (uint)bytes.Length)
        {
            return false;
        }

        var sb = new StringBuilder("S-1-");
        sb.Append(authority.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < subCount; i++)
        {
            uint sub = ReadUInt32LE(bytes, offset + 8 + (uint)(4 * i));
            sb.Append('-').Append(sub.ToString(CultureInfo.InvariantCulture));
        }

        sid = sb.ToString();
        return true;
    }

    private static int ComputeSidByteLength(byte[] bytes, uint offset)
    {
        byte subCount = bytes[offset + 1];
        return 8 + (4 * subCount);
    }

    private static bool TryEncodeSid(string sid, out byte[] encoded)
    {
        encoded = Array.Empty<byte>();
        if (!TryParseSid(sid, out ulong authority, out uint[] subAuthorities))
        {
            return false;
        }

        var bytes = new byte[8 + (4 * subAuthorities.Length)];
        bytes[0] = 1;
        bytes[1] = (byte)subAuthorities.Length;
        for (int i = 0; i < 6; i++)
        {
            bytes[2 + i] = (byte)((authority >> (8 * (5 - i))) & 0xFF);
        }
        for (int i = 0; i < subAuthorities.Length; i++)
        {
            WriteUInt32LE(bytes, (uint)(8 + (4 * i)), subAuthorities[i]);
        }

        encoded = bytes;
        return true;
    }

    private static uint ReadUInt32LE(byte[] bytes, uint offset) =>
        (uint)bytes[offset] | ((uint)bytes[offset + 1] << 8) | ((uint)bytes[offset + 2] << 16) | ((uint)bytes[offset + 3] << 24);

    private static ushort ReadUInt16LE(byte[] bytes, uint offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static void WriteUInt32LE(byte[] bytes, uint offset, uint value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteUInt16LE(byte[] bytes, uint offset, ushort value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>
    /// TRAP 1 and TRAP 2, encoded exactly as cycle 47 measured them. The supported
    /// shape is: owner S-1-5-32-544, a canonical group SID (captured, never a
    /// constant), control flags DaclPresent|DaclProtected|SelfRelative with NO
    /// SaclPresent, a DACL with EXACTLY TWO ACEs - AccessAllowed, AceFlags exactly
    /// ObjectInherit|ContainerInherit (0x03, NOT the Inherited flag 0x10), mask
    /// exactly FileFullControlRights, SID SYSTEM then BUILTIN\Administrators, in
    /// that order. Everything else fails closed as unsupported for promotion.
    /// </summary>
    public static bool IsSupportedPriorDescriptorShape(
        ServiceOwnershipParsedFileSecurityDescriptor parsed,
        out ServiceOwnershipLedgerInvalidReason reason)
    {
        if (parsed.SaclPresent)
        {
            reason = ServiceOwnershipLedgerInvalidReason.PriorDescriptorContainsSacl;
            return false;
        }
        if (!parsed.DaclPresent || (parsed.ControlFlags & ControlDaclProtected) == 0)
        {
            reason = ServiceOwnershipLedgerInvalidReason.PriorDescriptorNotProtected;
            return false;
        }
        if (!string.Equals(parsed.OwnerSid, RequiredOwnerSid, StringComparison.Ordinal)
            || !IsCanonicalSidString(parsed.GroupSid)
            || parsed.Aces.Count != 2
            || !IsSupportedAce(parsed.Aces[0], LocalSystemSid)
            || !IsSupportedAce(parsed.Aces[1], RequiredOwnerSid))
        {
            reason = ServiceOwnershipLedgerInvalidReason.PriorDescriptorUnsupportedShape;
            return false;
        }

        reason = ServiceOwnershipLedgerInvalidReason.None;
        return true;
    }

    private static bool IsSupportedAce(ServiceOwnershipParsedAce ace, string expectedSid) =>
        ace.AceType == AceTypeAccessAllowed
        && ace.AceFlags == RequiredAceFlags
        && (ace.AceFlags & AceFlagInherited) == 0
        && ace.Mask == FileFullControlRights
        && string.Equals(ace.Sid, expectedSid, StringComparison.Ordinal);

    /// <summary>
    /// THE PURE TRANSITION (cycle 48, schema v3). Accepts only a supported prior
    /// descriptor; preserves the ENTIRE prior descriptor unchanged; adds EXACTLY
    /// ONE explicit AccessAllowed ACE for <paramref name="serviceSid"/> with
    /// <see cref="ApprovedFileRightsProfileMask"/>, AceFlags <c>0</c> (a direct,
    /// non-propagating leaf grant - never Inherited); rejects a prior descriptor
    /// that already carries any ACE for that SID. NEVER mutates the input array: a
    /// defensive copy is taken and only the copy is read from.
    /// </summary>
    public static bool TryBuildPostGrantDescriptor(
        byte[] priorBytes,
        string serviceSid,
        out byte[] postGrantBytes,
        out string postGrantSha256,
        out ServiceOwnershipLedgerInvalidReason reason)
    {
        postGrantBytes = Array.Empty<byte>();
        postGrantSha256 = string.Empty;

        byte[] defensiveCopy = priorBytes is null ? Array.Empty<byte>() : (byte[])priorBytes.Clone();

        if (!TryParseFileSecurityDescriptor(defensiveCopy, out ServiceOwnershipParsedFileSecurityDescriptor? parsed)
            || parsed is null)
        {
            reason = ServiceOwnershipLedgerInvalidReason.PriorDescriptorMalformed;
            return false;
        }
        if (!IsSupportedPriorDescriptorShape(parsed, out reason))
        {
            return false;
        }
        if (!IsServiceVirtualAccountSid(serviceSid))
        {
            reason = ServiceOwnershipLedgerInvalidReason.InvalidServiceSid;
            return false;
        }
        foreach (ServiceOwnershipParsedAce ace in parsed.Aces)
        {
            if (string.Equals(ace.Sid, serviceSid, StringComparison.Ordinal))
            {
                reason = ServiceOwnershipLedgerInvalidReason.PriorDescriptorServiceAceAlreadyPresent;
                return false;
            }
        }

        // The shape check above pins the ONE measured layout, in which the DACL is
        // the LAST structure in the buffer. That is re-verified here, defensively,
        // before growing the buffer at its end.
        uint offsetDacl = ReadUInt32LE(defensiveCopy, 16);
        ushort oldAclSize = ReadUInt16LE(defensiveCopy, offsetDacl + 2);
        if (offsetDacl + oldAclSize != (uint)defensiveCopy.Length)
        {
            reason = ServiceOwnershipLedgerInvalidReason.PriorDescriptorUnsupportedShape;
            return false;
        }

        if (!TryEncodeSid(serviceSid, out byte[] serviceSidBytes))
        {
            reason = ServiceOwnershipLedgerInvalidReason.InvalidServiceSid;
            return false;
        }

        ushort newAceSize = (ushort)(8 + serviceSidBytes.Length);
        var output = new byte[defensiveCopy.Length + newAceSize];
        Array.Copy(defensiveCopy, 0, output, 0, defensiveCopy.Length);

        ushort newAclSize = (ushort)(oldAclSize + newAceSize);
        WriteUInt16LE(output, offsetDacl + 2, newAclSize);
        WriteUInt16LE(output, offsetDacl + 4, (ushort)(parsed.Aces.Count + 1));

        uint newAceOffset = (uint)defensiveCopy.Length;
        output[newAceOffset] = AceTypeAccessAllowed;
        output[newAceOffset + 1] = 0; // no inherit-propagation flags: a direct leaf grant
        WriteUInt16LE(output, newAceOffset + 2, newAceSize);
        WriteUInt32LE(output, newAceOffset + 4, ApprovedFileRightsProfileMask);
        Array.Copy(serviceSidBytes, 0, output, newAceOffset + 8, serviceSidBytes.Length);

        postGrantBytes = output;
        postGrantSha256 = ToUpperHex(SHA256.HashData(output));
        reason = ServiceOwnershipLedgerInvalidReason.None;
        return true;
    }

    /// <summary>
    /// Derives MatchesCapturedPriorState / MatchesRecordedGrant / Diverged from
    /// PARSED structural semantics only, never from a caller-supplied boolean.
    /// Equivalent effective rights, a differently persisted GenericRead, a
    /// superset, a duplicate ACE or a reordered foreign change all fail structural
    /// equality and are therefore Diverged.
    /// </summary>
    public static ServiceOwnershipDescriptorClassification ClassifyObservedDescriptor(
        byte[]? observedBytes,
        byte[]? capturedPriorBytes,
        byte[]? recordedGrantBytes)
    {
        if (!TryParseFileSecurityDescriptor(observedBytes, out ServiceOwnershipParsedFileSecurityDescriptor? observed)
            || observed is null)
        {
            return ServiceOwnershipDescriptorClassification.Diverged;
        }

        if (TryParseFileSecurityDescriptor(capturedPriorBytes, out ServiceOwnershipParsedFileSecurityDescriptor? prior)
            && prior is not null
            && DescriptorsStructurallyEqual(observed, prior))
        {
            return ServiceOwnershipDescriptorClassification.MatchesCapturedPriorState;
        }

        if (TryParseFileSecurityDescriptor(recordedGrantBytes, out ServiceOwnershipParsedFileSecurityDescriptor? grant)
            && grant is not null
            && DescriptorsStructurallyEqual(observed, grant))
        {
            return ServiceOwnershipDescriptorClassification.MatchesRecordedGrant;
        }

        return ServiceOwnershipDescriptorClassification.Diverged;
    }

    private static bool DescriptorsStructurallyEqual(
        ServiceOwnershipParsedFileSecurityDescriptor a,
        ServiceOwnershipParsedFileSecurityDescriptor b)
    {
        if (!string.Equals(a.OwnerSid, b.OwnerSid, StringComparison.Ordinal)
            || !string.Equals(a.GroupSid, b.GroupSid, StringComparison.Ordinal)
            || a.DaclPresent != b.DaclPresent
            || a.SaclPresent != b.SaclPresent
            || a.Aces.Count != b.Aces.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Aces.Count; i++)
        {
            ServiceOwnershipParsedAce left = a.Aces[i];
            ServiceOwnershipParsedAce right = b.Aces[i];
            if (left.AceType != right.AceType
                || left.AceFlags != right.AceFlags
                || left.Mask != right.Mask
                || !string.Equals(left.Sid, right.Sid, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // ---- refusal mapping ---------------------------------------------------

    /// <summary>
    /// Maps a refusal reason to its bounded outcome. Every reason - including
    /// <c>None</c>, which is never passed - maps to a REFUSED outcome, so no refusal
    /// path can ever produce an accepted outcome.
    /// </summary>
    public static ServiceOwnershipLedgerOutcome MapRefusalOutcome(
        ServiceOwnershipLedgerInvalidReason reason) => reason switch
        {
            ServiceOwnershipLedgerInvalidReason.UnsupportedSchemaVersion => ServiceOwnershipLedgerOutcome.Unsupported,
            ServiceOwnershipLedgerInvalidReason.UnsupportedProviderRightsVocabulary => ServiceOwnershipLedgerOutcome.Unsupported,
            ServiceOwnershipLedgerInvalidReason.BroadProviderClassificationNotApproved => ServiceOwnershipLedgerOutcome.Unsupported,

            ServiceOwnershipLedgerInvalidReason.WrongMarker => ServiceOwnershipLedgerOutcome.Foreign,
            ServiceOwnershipLedgerInvalidReason.WrongFeatureId => ServiceOwnershipLedgerOutcome.Foreign,
            ServiceOwnershipLedgerInvalidReason.ForeignOwnership => ServiceOwnershipLedgerOutcome.Foreign,

            ServiceOwnershipLedgerInvalidReason.OwnerSidEqualsServiceSid => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.ZeroRightsMask => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.UnknownRightsBit => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.NoApprovedRightsProfile => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.ProviderMechanismMismatch => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.ProviderProfileMismatch => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.ApprovedRightsProfileBitMissing => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.ApprovedRightsProfileBitNotPermitted => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.GenericReadRightNotAuthorized => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.ActiveLifecycleAcceptanceNotAuthorized => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.PriorDaclPayloadNotAllowed => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.PriorDaclPayloadMissing => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.PriorDaclHashMismatch => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.CapturedStateBindingMismatch => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.CrossOwnerCertificateCollision => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.CrossOwnerJobCollision => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.InconsistentSharedCertificateFields => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.DuplicateEntryId => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.DuplicateJobId => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.PriorDescriptorUnsupportedShape => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.PriorDescriptorContainsSacl => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.PriorDescriptorNotProtected => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.PriorDescriptorServiceAceAlreadyPresent => ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerInvalidReason.RetiredCngSecurityDescriptorMechanismUnsupported => ServiceOwnershipLedgerOutcome.Unsupported,

            _ => ServiceOwnershipLedgerOutcome.Malformed,
        };
}

/// <summary>One bounded ownership record. Every field is non-secret and closed.</summary>
public sealed class ServiceOwnershipLedgerEntry
{
    internal ServiceOwnershipLedgerEntry(
        string entryId,
        string owningUserSid,
        string serviceSid,
        ServiceOwnershipCredentialKind credentialKind,
        string certificateThumbprintSha1,
        ServiceOwnershipProvenance provenance,
        ServiceOwnershipPrivateKeyProviderKind privateKeyProviderKind,
        ServiceOwnershipRightsProfileId rightsProfileId,
        string keyIdentity,
        ServiceOwnershipGrantMechanism grantMechanism,
        ServiceOwnershipRightsMask grantedRightsMask,
        int rightsPolicyVersion,
        ServiceOwnershipPriorDaclState priorDaclState,
        string priorDaclBytesBase64,
        string priorDaclSha256,
        string capturedStateBindingSha256,
        IReadOnlyList<string> associatedPromotedJobIds,
        ServiceOwnershipLifecycleState lifecycleState,
        string createdUtc,
        string updatedUtc,
        string providerUniqueName,
        ServiceOwnershipKeyStorageRoot keyStorageRoot,
        ServiceOwnershipDescriptorFormat descriptorFormat)
    {
        EntryId = entryId;
        OwningUserSid = owningUserSid;
        ServiceSid = serviceSid;
        CredentialKind = credentialKind;
        CertificateThumbprintSha1 = certificateThumbprintSha1;
        Provenance = provenance;
        PrivateKeyProviderKind = privateKeyProviderKind;
        RightsProfileId = rightsProfileId;
        KeyIdentity = keyIdentity;
        GrantMechanism = grantMechanism;
        GrantedRightsMask = grantedRightsMask;
        RightsPolicyVersion = rightsPolicyVersion;
        PriorDaclState = priorDaclState;
        PriorDaclBytesBase64 = priorDaclBytesBase64;
        PriorDaclSha256 = priorDaclSha256;
        CapturedStateBindingSha256 = capturedStateBindingSha256;
        AssociatedPromotedJobIds = associatedPromotedJobIds;
        LifecycleState = lifecycleState;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        ProviderUniqueName = providerUniqueName;
        KeyStorageRoot = keyStorageRoot;
        DescriptorFormat = descriptorFormat;
    }

    public string EntryId { get; }

    public string OwningUserSid { get; }

    public string ServiceSid { get; }

    public ServiceOwnershipCredentialKind CredentialKind { get; }

    public string CertificateThumbprintSha1 { get; }

    public ServiceOwnershipProvenance Provenance { get; }

    public ServiceOwnershipPrivateKeyProviderKind PrivateKeyProviderKind { get; }

    public ServiceOwnershipRightsProfileId RightsProfileId { get; }

    public string KeyIdentity { get; }

    public ServiceOwnershipGrantMechanism GrantMechanism { get; }

    public ServiceOwnershipRightsMask GrantedRightsMask { get; }

    public int RightsPolicyVersion { get; }

    public ServiceOwnershipPriorDaclState PriorDaclState { get; }

    public string PriorDaclBytesBase64 { get; }

    public string PriorDaclSha256 { get; }

    public string CapturedStateBindingSha256 { get; }

    public IReadOnlyList<string> AssociatedPromotedJobIds { get; }

    public ServiceOwnershipLifecycleState LifecycleState { get; }

    public string CreatedUtc { get; }

    public string UpdatedUtc { get; }

    public string ProviderUniqueName { get; }

    public ServiceOwnershipKeyStorageRoot KeyStorageRoot { get; }

    public ServiceOwnershipDescriptorFormat DescriptorFormat { get; }
}

/// <summary>The whole ledger document. Ten closed properties, nothing else.</summary>
public sealed class ServiceOwnershipLedgerDocument
{
    internal ServiceOwnershipLedgerDocument(
        int schemaVersion,
        string productOwnershipMarker,
        string managedFeatureId,
        string installationOwnershipId,
        int generation,
        ServiceOwnershipTransactionState transactionState,
        IReadOnlyList<ServiceOwnershipLedgerEntry> entries,
        string createdUtc,
        string updatedUtc,
        string lastOperationId)
    {
        SchemaVersion = schemaVersion;
        ProductOwnershipMarker = productOwnershipMarker;
        ManagedFeatureId = managedFeatureId;
        InstallationOwnershipId = installationOwnershipId;
        Generation = generation;
        TransactionState = transactionState;
        Entries = entries;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        LastOperationId = lastOperationId;
    }

    public int SchemaVersion { get; }

    public string ProductOwnershipMarker { get; }

    public string ManagedFeatureId { get; }

    public string InstallationOwnershipId { get; }

    public int Generation { get; }

    public ServiceOwnershipTransactionState TransactionState { get; }

    public IReadOnlyList<ServiceOwnershipLedgerEntry> Entries { get; }

    public string CreatedUtc { get; }

    public string UpdatedUtc { get; }

    public string LastOperationId { get; }
}

/// <summary>Bounded validation result. Carries a document only when accepted.</summary>
public sealed class ServiceOwnershipLedgerValidationResult
{
    private ServiceOwnershipLedgerValidationResult(
        ServiceOwnershipLedgerOutcome outcome,
        ServiceOwnershipLedgerInvalidReason reason,
        ServiceOwnershipLedgerDocument? document)
    {
        Outcome = outcome;
        Reason = reason;
        Document = document;
    }

    public ServiceOwnershipLedgerOutcome Outcome { get; }

    public ServiceOwnershipLedgerInvalidReason Reason { get; }

    public ServiceOwnershipLedgerDocument? Document { get; }

    public bool IsAccepted =>
        Reason == ServiceOwnershipLedgerInvalidReason.None
        && Outcome is ServiceOwnershipLedgerOutcome.Absent
            or ServiceOwnershipLedgerOutcome.ValidEmpty
            or ServiceOwnershipLedgerOutcome.InProgress
            or ServiceOwnershipLedgerOutcome.Stale
            or ServiceOwnershipLedgerOutcome.Active;

    public bool IsRefused => !IsAccepted;

    internal static ServiceOwnershipLedgerValidationResult Absent() =>
        new(ServiceOwnershipLedgerOutcome.Absent, ServiceOwnershipLedgerInvalidReason.None, null);

    internal static ServiceOwnershipLedgerValidationResult Accepted(
        ServiceOwnershipLedgerOutcome outcome,
        ServiceOwnershipLedgerDocument document) =>
        new(outcome, ServiceOwnershipLedgerInvalidReason.None, document);

    internal static ServiceOwnershipLedgerValidationResult Invalid(
        ServiceOwnershipLedgerInvalidReason reason) =>
        new(ServiceOwnershipLedgerContract.MapRefusalOutcome(reason), reason, null);
}

/// <summary>
/// The pure, deterministic ledger validator. It reads nothing, writes nothing and
/// throws nothing: every failure - including a hostile or truncated input - maps to
/// a bounded refusal reason.
/// </summary>
public static class ServiceOwnershipLedgerValidator
{
    /// <summary>
    /// The ONLY way to obtain <c>Absent</c>. A caller that has genuinely observed
    /// "no ledger file" says so explicitly; null, empty or blank text is never
    /// treated as absence, because that would let a truncated write read as a clean
    /// machine.
    /// </summary>
    public static ServiceOwnershipLedgerValidationResult ForAbsentLedger() =>
        ServiceOwnershipLedgerValidationResult.Absent();

    public static ServiceOwnershipLedgerValidationResult Validate(string? ledgerJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ledgerJson))
            {
                return Invalid(ServiceOwnershipLedgerInvalidReason.MalformedJson);
            }

            int byteCount;
            try
            {
                byteCount = Encoding.UTF8.GetByteCount(ledgerJson);
            }
            catch (Exception)
            {
                return Invalid(ServiceOwnershipLedgerInvalidReason.MalformedJson);
            }
            if (byteCount > ServiceOwnershipLedgerContract.MaxLedgerBytes)
            {
                return Invalid(ServiceOwnershipLedgerInvalidReason.OversizedInput);
            }

            JsonDocument parsed;
            try
            {
                parsed = JsonDocument.Parse(ledgerJson);
            }
            catch (Exception)
            {
                return Invalid(ServiceOwnershipLedgerInvalidReason.MalformedJson);
            }

            using (parsed)
            {
                return ValidateRoot(parsed.RootElement);
            }
        }
        catch (Exception)
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.MalformedJson);
        }
    }

    private static ServiceOwnershipLedgerValidationResult Invalid(
        ServiceOwnershipLedgerInvalidReason reason) =>
        ServiceOwnershipLedgerValidationResult.Invalid(reason);

    private static ServiceOwnershipLedgerValidationResult ValidateRoot(JsonElement root)
    {
        ServiceOwnershipLedgerInvalidReason shape = ValidateClosedObject(
            root, ServiceOwnershipLedgerContract.DocumentPropertySet,
            ServiceOwnershipLedgerContract.DocumentPropertyNames);
        if (shape != ServiceOwnershipLedgerInvalidReason.None)
        {
            return Invalid(shape);
        }

        if (!TryStrictInt(root, "schemaVersion", out int schemaVersion))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.WrongType);
        }
        if (schemaVersion != ServiceOwnershipLedgerContract.LedgerSchemaVersion)
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.UnsupportedSchemaVersion);
        }

        if (!TryString(root, "productOwnershipMarker", out string marker))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.WrongType);
        }
        if (!string.Equals(marker, ServiceOwnershipLedgerContract.ProductOwnershipMarker, StringComparison.Ordinal))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.WrongMarker);
        }

        if (!TryString(root, "managedFeatureId", out string featureId))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.WrongType);
        }
        if (!string.Equals(featureId, ServiceOwnershipLedgerContract.ManagedFeatureId, StringComparison.Ordinal))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.WrongFeatureId);
        }

        if (!TryString(root, "installationOwnershipId", out string installationOwnershipId))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.WrongType);
        }
        if (!ServiceOwnershipLedgerContract.IsValidBoundedToken(
                installationOwnershipId, ServiceOwnershipLedgerContract.MaxStringLength))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.InvalidInstallationOwnershipId);
        }

        if (!TryStrictInt(root, "generation", out int generation))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.WrongType);
        }
        if (generation < 1)
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.InvalidGeneration);
        }

        if (!TryString(root, "transactionState", out string transactionToken))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.WrongType);
        }
        if (!ServiceOwnershipLedgerContract.TryParseWireToken(
                transactionToken, out ServiceOwnershipTransactionState transactionState))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.InvalidTransactionState);
        }

        if (!TryString(root, "createdUtc", out string createdUtc)
            || !TryString(root, "updatedUtc", out string updatedUtc))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.WrongType);
        }
        if (!TryParseRfc3339Utc(createdUtc, out DateTimeOffset created)
            || !TryParseRfc3339Utc(updatedUtc, out DateTimeOffset updated)
            || created > updated)
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.InvalidTimestamp);
        }

        if (!TryString(root, "lastOperationId", out string lastOperationId))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.WrongType);
        }
        if (!ServiceOwnershipLedgerContract.IsValidBoundedToken(
                lastOperationId, ServiceOwnershipLedgerContract.MaxStringLength))
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.InvalidOperationId);
        }

        JsonElement entriesElement = root.GetProperty("entries");
        if (entriesElement.ValueKind != JsonValueKind.Array)
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.WrongType);
        }
        int entryCount = entriesElement.GetArrayLength();
        if (entryCount > ServiceOwnershipLedgerContract.MaxEntries)
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.TooManyEntries);
        }

        var entries = new ServiceOwnershipLedgerEntry[entryCount];
        int index = 0;
        foreach (JsonElement element in entriesElement.EnumerateArray())
        {
            ServiceOwnershipLedgerInvalidReason entryReason =
                ValidateEntry(element, generation, out ServiceOwnershipLedgerEntry? entry);
            if (entryReason != ServiceOwnershipLedgerInvalidReason.None)
            {
                return Invalid(entryReason);
            }
            entries[index++] = entry!;
        }

        ServiceOwnershipLedgerInvalidReason crossReason = ValidateCrossEntryInvariants(entries);
        if (crossReason != ServiceOwnershipLedgerInvalidReason.None)
        {
            return Invalid(crossReason);
        }

        // A quiescent ledger cannot claim an in-flight transaction, and a ledger that
        // still holds entries cannot claim to be idle.
        bool quiescent = transactionState is ServiceOwnershipTransactionState.Idle
            or ServiceOwnershipTransactionState.Done;
        if (entryCount == 0 && !quiescent)
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.InvalidTransactionState);
        }
        if (entryCount > 0 && transactionState == ServiceOwnershipTransactionState.Idle)
        {
            return Invalid(ServiceOwnershipLedgerInvalidReason.InvalidTransactionState);
        }

        ServiceOwnershipLedgerOutcome outcome;
        if (entryCount == 0)
        {
            outcome = ServiceOwnershipLedgerOutcome.ValidEmpty;
        }
        else if (ContainsStaleEntry(entries))
        {
            outcome = ServiceOwnershipLedgerOutcome.Stale;
        }
        else if (transactionState == ServiceOwnershipTransactionState.Done
            && AllEntriesActive(entries))
        {
            // CYCLE 82 (D1). A COMPLETE active ledger, and the only shape that may
            // report it. Every entry here reached this point through the exact
            // approved-profile authorization in ValidateEntry, so there is no
            // second, weaker path to this value. A healthy active ledger is NEVER
            // reported as InProgress: that would describe a finished transaction as
            // in flight and invite a recovery pass to tear down a live grant.
            outcome = ServiceOwnershipLedgerOutcome.Active;
        }
        else
        {
            // Everything else with entries is genuinely in flight or incomplete:
            // a non-done transaction, or a done transaction whose entries are not
            // all active (for example a restored entry not yet reaped).
            outcome = ServiceOwnershipLedgerOutcome.InProgress;
        }

        var document = new ServiceOwnershipLedgerDocument(
            schemaVersion, marker, featureId, installationOwnershipId, generation,
            transactionState, Array.AsReadOnly(entries), createdUtc, updatedUtc,
            lastOperationId);

        return ServiceOwnershipLedgerValidationResult.Accepted(outcome, document);
    }

    private static bool ContainsStaleEntry(ServiceOwnershipLedgerEntry[] entries)
    {
        foreach (ServiceOwnershipLedgerEntry entry in entries)
        {
            if (entry.LifecycleState == ServiceOwnershipLifecycleState.Stale)
            {
                return true;
            }
        }
        return false;
    }

    private static bool AllEntriesActive(ServiceOwnershipLedgerEntry[] entries)
    {
        foreach (ServiceOwnershipLedgerEntry entry in entries)
        {
            if (entry.LifecycleState != ServiceOwnershipLifecycleState.Active)
            {
                return false;
            }
        }
        return true;
    }

    private static ServiceOwnershipLedgerInvalidReason ValidateEntry(
        JsonElement element,
        int generation,
        out ServiceOwnershipLedgerEntry? entry)
    {
        entry = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }

        ServiceOwnershipLedgerInvalidReason shape = ValidateClosedObject(
            element, ServiceOwnershipLedgerContract.EntryPropertySet,
            ServiceOwnershipLedgerContract.EntryPropertyNames);
        if (shape != ServiceOwnershipLedgerInvalidReason.None)
        {
            return shape;
        }

        if (!TryString(element, "entryId", out string entryId))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.IsValidBoundedToken(
                entryId, ServiceOwnershipLedgerContract.MaxStringLength))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidEntryId;
        }

        if (!TryString(element, "owningUserSid", out string owningUserSid))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.IsCanonicalSidString(owningUserSid))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidOwnerSid;
        }
        if (!TryString(element, "serviceSid", out string serviceSid))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.IsCanonicalSidString(serviceSid))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidServiceSid;
        }
        if (string.Equals(owningUserSid, serviceSid, StringComparison.Ordinal))
        {
            return ServiceOwnershipLedgerInvalidReason.OwnerSidEqualsServiceSid;
        }
        if (!ServiceOwnershipLedgerContract.IsUserOwnerSid(owningUserSid))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidOwnerSid;
        }
        if (!ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(serviceSid))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidServiceSid;
        }

        if (!TryString(element, "credentialKind", out string credentialToken))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.TryParseWireToken(
                credentialToken, out ServiceOwnershipCredentialKind credentialKind))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidCredentialKind;
        }

        if (!TryString(element, "provenance", out string provenanceToken))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.TryParseWireToken(
                provenanceToken, out ServiceOwnershipProvenance provenance))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidProvenance;
        }

        if (!TryString(element, "privateKeyProviderKind", out string providerToken))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.TryParseWireToken(
                providerToken, out ServiceOwnershipPrivateKeyProviderKind providerKind))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidProviderKind;
        }

        if (!TryString(element, "rightsProfileId", out string profileToken))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.TryParseWireToken(
                profileToken, out ServiceOwnershipRightsProfileId rightsProfileId))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidRightsProfileId;
        }

        if (!TryString(element, "grantMechanism", out string mechanismToken))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.TryParseWireToken(
                mechanismToken, out ServiceOwnershipGrantMechanism mechanism))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidGrantMechanism;
        }

        // THE RETIRED MECHANISM GATE. Cycle 46 measured and rejected the CNG
        // key-object descriptor mechanism as a non-reversible one-way lock. It is
        // refused here, for ANY provider pairing and ANY lifecycle state, before any
        // other mechanism check - so it never surfaces as a mere mismatch.
        if (ServiceOwnershipLedgerContract.IsRetiredGrantMechanism(mechanism))
        {
            return ServiceOwnershipLedgerInvalidReason.RetiredCngSecurityDescriptorMechanismUnsupported;
        }

        if (!ServiceOwnershipLedgerContract.IsMechanismPairedWith(providerKind, mechanism))
        {
            return ServiceOwnershipLedgerInvalidReason.ProviderMechanismMismatch;
        }
        // The legacy provider is REPRESENTABLE as a bounded classification, but its
        // rights vocabulary is not interpreted here and its mechanism is not
        // authorised. No legacy mask is ever parsed.
        if (!ServiceOwnershipLedgerContract.IsRightsVocabularySupported(providerKind))
        {
            return ServiceOwnershipLedgerInvalidReason.UnsupportedProviderRightsVocabulary;
        }
        // Schema v2 binds every entry to a rights profile, and the ONLY declared
        // profile belongs to the ONE exactly measured provider. A broad classification
        // therefore cannot carry it in ANY lifecycle state, not merely in "active".
        if (!ServiceOwnershipLedgerContract.IsApprovedRightsProfile(rightsProfileId, providerKind))
        {
            return ServiceOwnershipLedgerInvalidReason.ProviderProfileMismatch;
        }

        if (!TryString(element, "certificateThumbprintSha1", out string thumbprint))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.IsUppercaseSha1Thumbprint(thumbprint))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidThumbprint;
        }

        if (!TryString(element, "keyIdentity", out string keyIdentity))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.IsValidKeyIdentity(keyIdentity))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidKeyIdentity;
        }

        if (!TryString(element, "providerUniqueName", out string providerUniqueName))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.IsValidProviderUniqueName(providerUniqueName))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidProviderUniqueName;
        }

        if (!TryString(element, "keyStorageRoot", out string keyStorageRootToken))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.TryParseWireToken(
                keyStorageRootToken, out ServiceOwnershipKeyStorageRoot keyStorageRoot))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidKeyStorageRoot;
        }

        if (!TryString(element, "descriptorFormat", out string descriptorFormatToken))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.TryParseWireToken(
                descriptorFormatToken, out ServiceOwnershipDescriptorFormat descriptorFormat))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidDescriptorFormat;
        }

        if (!TryStrictInt(element, "rightsPolicyVersion", out int rightsPolicyVersion))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (rightsPolicyVersion != ServiceOwnershipLedgerContract.RightsPolicyVersion)
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidRightsPolicyVersion;
        }

        if (!TryString(element, "grantedRightsMask", out string maskText))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.TryValidateFileRightsMask(
                maskText, out ServiceOwnershipRightsMask mask,
                out ServiceOwnershipLedgerInvalidReason maskReason))
        {
            return maskReason;
        }

        if (!TryString(element, "priorDaclState", out string priorStateToken))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.TryParseWireToken(
                priorStateToken, out ServiceOwnershipPriorDaclState priorState))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidPriorDaclState;
        }

        if (!TryString(element, "priorDaclBytesBase64", out string priorBase64)
            || !TryString(element, "priorDaclSha256", out string priorSha256))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }

        ServiceOwnershipLedgerInvalidReason priorReason =
            ValidatePriorState(priorState, priorBase64, priorSha256);
        if (priorReason != ServiceOwnershipLedgerInvalidReason.None)
        {
            return priorReason;
        }

        // STRUCTURAL PARSING of the prior descriptor (cycle 48, schema v3). The
        // bytes are walked field by field - self-relative header, owner/group SIDs,
        // ACL header, each ACE header/mask/SID - never merely Base64-decoded and
        // hashed. Only the one measured shape is supported for promotion.
        if (priorState == ServiceOwnershipPriorDaclState.Present)
        {
            TryDecodeStrictBase64(priorBase64, out byte[] priorDescriptorBytes);
            if (!ServiceOwnershipLedgerContract.TryParseFileSecurityDescriptor(
                    priorDescriptorBytes, out ServiceOwnershipParsedFileSecurityDescriptor? parsedPrior)
                || parsedPrior is null)
            {
                return ServiceOwnershipLedgerInvalidReason.PriorDescriptorMalformed;
            }
            if (!ServiceOwnershipLedgerContract.IsSupportedPriorDescriptorShape(
                    parsedPrior, out ServiceOwnershipLedgerInvalidReason shapeReason))
            {
                return shapeReason;
            }
            foreach (ServiceOwnershipParsedAce ace in parsedPrior.Aces)
            {
                if (string.Equals(ace.Sid, serviceSid, StringComparison.Ordinal))
                {
                    return ServiceOwnershipLedgerInvalidReason.PriorDescriptorServiceAceAlreadyPresent;
                }
            }
        }

        if (!TryString(element, "lifecycleState", out string lifecycleToken))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.TryParseWireToken(
                lifecycleToken, out ServiceOwnershipLifecycleState lifecycleState))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidLifecycleState;
        }
        if (lifecycleState == ServiceOwnershipLifecycleState.Foreign)
        {
            return ServiceOwnershipLedgerInvalidReason.ForeignOwnership;
        }
        if (lifecycleState == ServiceOwnershipLifecycleState.Active)
        {
            if (!ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
                    mask, providerKind, mechanism, rightsProfileId, rightsPolicyVersion, descriptorFormat,
                    out ServiceOwnershipLedgerInvalidReason activeReason))
            {
                return activeReason;
            }

            // CYCLE 82 (D1). The unconditional fall-through brake that used to stand
            // here is GONE. What remains is a NARROW POSITIVE ALLOW-LIST: an active
            // entry survives only because TryAuthorizeActiveRights matched the exact
            // approved provider kind, rights-profile id, backing-file DACL mechanism,
            // descriptor format, policy version and mask bit for bit. Execution now
            // falls through to the SAME captured-state binding, promoted-job,
            // timestamp, cross-entry and collision gates every other lifecycle state
            // must pass - none of them is skipped for an active entry, and the prior
            // descriptor's shape and no-pre-existing-service-ACE checks above have
            // already run. Nothing here is inherited from an approved profile alone.
        }

        if (!TryString(element, "capturedStateBindingSha256", out string binding))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!ServiceOwnershipLedgerContract.IsUppercaseSha256Hex(binding))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidCapturedStateBinding;
        }
        string expectedBinding = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            generation, owningUserSid, serviceSid, thumbprint, providerKind, rightsProfileId,
            keyIdentity, mechanism, maskText, priorState, priorSha256,
            providerUniqueName, keyStorageRoot, descriptorFormat);
        if (!string.Equals(expectedBinding, binding, StringComparison.Ordinal))
        {
            return ServiceOwnershipLedgerInvalidReason.CapturedStateBindingMismatch;
        }

        JsonElement jobsElement = element.GetProperty("associatedPromotedJobIds");
        if (jobsElement.ValueKind != JsonValueKind.Array)
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        int jobCount = jobsElement.GetArrayLength();
        if (jobCount > ServiceOwnershipLedgerContract.MaxJobIdsPerEntry)
        {
            return ServiceOwnershipLedgerInvalidReason.TooManyJobIds;
        }
        var jobIds = new string[jobCount];
        var jobSeen = new HashSet<string>(StringComparer.Ordinal);
        int jobIndex = 0;
        foreach (JsonElement item in jobsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return ServiceOwnershipLedgerInvalidReason.WrongType;
            }
            string jobId = item.GetString() ?? string.Empty;
            if (!ServiceOwnershipLedgerContract.IsValidBoundedToken(
                    jobId, ServiceOwnershipLedgerContract.MaxStringLength))
            {
                return ServiceOwnershipLedgerInvalidReason.InvalidJobId;
            }
            if (!jobSeen.Add(jobId))
            {
                return ServiceOwnershipLedgerInvalidReason.DuplicateJobId;
            }
            jobIds[jobIndex++] = jobId;
        }

        if (!TryString(element, "createdUtc", out string entryCreatedUtc)
            || !TryString(element, "updatedUtc", out string entryUpdatedUtc))
        {
            return ServiceOwnershipLedgerInvalidReason.WrongType;
        }
        if (!TryParseRfc3339Utc(entryCreatedUtc, out DateTimeOffset entryCreated)
            || !TryParseRfc3339Utc(entryUpdatedUtc, out DateTimeOffset entryUpdated)
            || entryCreated > entryUpdated)
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidTimestamp;
        }

        entry = new ServiceOwnershipLedgerEntry(
            entryId, owningUserSid, serviceSid, credentialKind, thumbprint, provenance,
            providerKind, rightsProfileId, keyIdentity, mechanism, mask, rightsPolicyVersion,
            priorState, priorBase64, priorSha256, binding, Array.AsReadOnly(jobIds),
            lifecycleState, entryCreatedUtc, entryUpdatedUtc,
            providerUniqueName, keyStorageRoot, descriptorFormat);

        return ServiceOwnershipLedgerInvalidReason.None;
    }

    private static ServiceOwnershipLedgerInvalidReason ValidatePriorState(
        ServiceOwnershipPriorDaclState priorState,
        string priorBase64,
        string priorSha256)
    {
        if (priorState is ServiceOwnershipPriorDaclState.Absent or ServiceOwnershipPriorDaclState.Empty)
        {
            // Nothing existed, so nothing may be carried. Both payload fields must be
            // present-and-empty; anything else is a contradiction.
            return priorBase64.Length != 0 || priorSha256.Length != 0
                ? ServiceOwnershipLedgerInvalidReason.PriorDaclPayloadNotAllowed
                : ServiceOwnershipLedgerInvalidReason.None;
        }

        if (priorBase64.Length == 0 || priorSha256.Length == 0)
        {
            return ServiceOwnershipLedgerInvalidReason.PriorDaclPayloadMissing;
        }
        if (priorBase64.Length > ServiceOwnershipLedgerContract.MaxPriorDaclBase64Length)
        {
            return ServiceOwnershipLedgerInvalidReason.OversizedPriorDacl;
        }
        if (!TryDecodeStrictBase64(priorBase64, out byte[] decoded))
        {
            return ServiceOwnershipLedgerInvalidReason.InvalidBase64;
        }
        if (decoded.Length > ServiceOwnershipLedgerContract.MaxPriorDaclDecodedBytes)
        {
            return ServiceOwnershipLedgerInvalidReason.OversizedPriorDacl;
        }
        if (!ServiceOwnershipLedgerContract.IsUppercaseSha256Hex(priorSha256))
        {
            return ServiceOwnershipLedgerInvalidReason.PriorDaclHashMismatch;
        }
        string actual = ServiceOwnershipLedgerContract.ToUpperHex(SHA256.HashData(decoded));
        return string.Equals(actual, priorSha256, StringComparison.Ordinal)
            ? ServiceOwnershipLedgerInvalidReason.None
            : ServiceOwnershipLedgerInvalidReason.PriorDaclHashMismatch;
    }

    private static ServiceOwnershipLedgerInvalidReason ValidateCrossEntryInvariants(
        ServiceOwnershipLedgerEntry[] entries)
    {
        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ServiceOwnershipLedgerEntry entry in entries)
        {
            if (!entryIds.Add(entry.EntryId))
            {
                return ServiceOwnershipLedgerInvalidReason.DuplicateEntryId;
            }
        }

        // A promoted job belongs to exactly one owner, everywhere in the ledger.
        var jobOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ServiceOwnershipLedgerEntry entry in entries)
        {
            foreach (string jobId in entry.AssociatedPromotedJobIds)
            {
                if (jobOwners.TryGetValue(jobId, out string? existingOwner))
                {
                    return string.Equals(existingOwner, entry.OwningUserSid, StringComparison.Ordinal)
                        ? ServiceOwnershipLedgerInvalidReason.DuplicateJobId
                        : ServiceOwnershipLedgerInvalidReason.CrossOwnerJobCollision;
                }
                jobOwners[jobId] = entry.OwningUserSid;
            }
        }

        // A certificate belongs to exactly one owner, and every entry that shares it
        // must agree on the whole captured grant.
        var certificateOwners = new Dictionary<string, ServiceOwnershipLedgerEntry>(StringComparer.Ordinal);
        foreach (ServiceOwnershipLedgerEntry entry in entries)
        {
            if (certificateOwners.TryGetValue(entry.CertificateThumbprintSha1, out ServiceOwnershipLedgerEntry? first))
            {
                if (!string.Equals(first.OwningUserSid, entry.OwningUserSid, StringComparison.Ordinal))
                {
                    return ServiceOwnershipLedgerInvalidReason.CrossOwnerCertificateCollision;
                }
                if (!SharedCertificateFieldsAgree(first, entry))
                {
                    return ServiceOwnershipLedgerInvalidReason.InconsistentSharedCertificateFields;
                }
            }
            else
            {
                certificateOwners[entry.CertificateThumbprintSha1] = entry;
            }
        }

        return ServiceOwnershipLedgerInvalidReason.None;
    }

    private static bool SharedCertificateFieldsAgree(
        ServiceOwnershipLedgerEntry first,
        ServiceOwnershipLedgerEntry other) =>
        string.Equals(first.ServiceSid, other.ServiceSid, StringComparison.Ordinal)
        && string.Equals(first.KeyIdentity, other.KeyIdentity, StringComparison.Ordinal)
        && first.GrantMechanism == other.GrantMechanism
        && first.PrivateKeyProviderKind == other.PrivateKeyProviderKind
        && first.RightsProfileId == other.RightsProfileId
        && first.GrantedRightsMask == other.GrantedRightsMask
        && first.RightsPolicyVersion == other.RightsPolicyVersion
        && first.PriorDaclState == other.PriorDaclState
        && string.Equals(first.PriorDaclSha256, other.PriorDaclSha256, StringComparison.Ordinal)
        && string.Equals(first.ProviderUniqueName, other.ProviderUniqueName, StringComparison.Ordinal)
        && first.KeyStorageRoot == other.KeyStorageRoot
        && first.DescriptorFormat == other.DescriptorFormat;

    // ---- primitives --------------------------------------------------------

    private static ServiceOwnershipLedgerInvalidReason ValidateClosedObject(
        JsonElement element,
        HashSet<string> allowed,
        IReadOnlyList<string> required)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return ServiceOwnershipLedgerInvalidReason.MalformedJson;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return ServiceOwnershipLedgerInvalidReason.DuplicateProperty;
            }
        }

        // Prohibited names fail the WHOLE document even when the value is null or
        // empty: their presence means the caller is trying to smuggle authority or
        // secret material across a boundary that carries neither.
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (ServiceOwnershipLedgerContract.IsProhibitedPropertyName(property.Name))
            {
                return ServiceOwnershipLedgerInvalidReason.ProhibitedProperty;
            }
            if (!allowed.Contains(property.Name))
            {
                return ServiceOwnershipLedgerInvalidReason.UnknownProperty;
            }
        }

        for (int i = 0; i < required.Count; i++)
        {
            if (!seen.Contains(required[i]))
            {
                return ServiceOwnershipLedgerInvalidReason.MissingProperty;
            }
        }

        return ServiceOwnershipLedgerInvalidReason.None;
    }

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        string? text = element.GetString();
        if (text is null)
        {
            return false;
        }
        value = text;
        return true;
    }

    /// <summary>
    /// A STRICT integer: no float, no exponent, no leading plus, no leading zero and
    /// no value outside the 32-bit range. The raw token is inspected because the
    /// parser would otherwise accept spellings the schema does not.
    /// </summary>
    private static bool TryStrictInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        string raw = element.GetRawText();
        if (raw.Length == 0)
        {
            return false;
        }

        int start = raw[0] == '-' ? 1 : 0;
        if (start >= raw.Length)
        {
            return false;
        }
        for (int i = start; i < raw.Length; i++)
        {
            if (raw[i] < '0' || raw[i] > '9')
            {
                return false;
            }
        }
        if (raw.Length - start > 1 && raw[start] == '0')
        {
            return false;
        }

        return int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Exactly <c>yyyy-MM-ddTHH:mm:ssZ</c>. No offset form, no fractional seconds.</summary>
    private static bool TryParseRfc3339Utc(string value, out DateTimeOffset parsed)
    {
        parsed = default;
        if (value.Length != 20 || value[19] != 'Z')
        {
            return false;
        }
        return DateTimeOffset.TryParseExact(
            value,
            "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
    }

    /// <summary>
    /// Strict base64: length a multiple of four, the standard alphabet only, padding
    /// only at the very end and never more than two characters. Whitespace, which the
    /// framework decoder tolerates, is refused.
    /// </summary>
    private static bool TryDecodeStrictBase64(string text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (text.Length == 0 || (text.Length % 4) != 0)
        {
            return false;
        }

        int padding = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '=')
            {
                if (i < text.Length - 2)
                {
                    return false;
                }
                padding++;
                continue;
            }
            if (padding > 0)
            {
                return false;
            }
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9') || c == '+' || c == '/';
            if (!ok)
            {
                return false;
            }
        }
        if (padding > 2)
        {
            return false;
        }

        var buffer = new byte[(text.Length / 4) * 3];
        if (!Convert.TryFromBase64String(text, buffer, out int written))
        {
            return false;
        }

        var result = new byte[written];
        Array.Copy(buffer, result, written);
        bytes = result;
        return true;
    }
}
