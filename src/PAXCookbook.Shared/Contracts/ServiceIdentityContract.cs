// PAX Cookbook - FIXED SERVICE IDENTITY CONTRACT (cycle 39)
//
// SINGLE SOURCE OF TRUTH for the machine service's fixed name, fixed display
// name, and fixed qualified account name, plus the bounded sizes and the pure
// shape checks that anything resolving that identity must honour.
//
// SELF-CONTAINED AND PORTABLE. Portable framework namespaces only. This file
// compiles unchanged into PAXCookbook.Shared (used by Setup) and is LINKED
// (compiled in-place) into PAXCookbook.Service exactly as the other service
// contracts are, so both hosts use the same names and the same validation with
// no duplicated constants.
//
// WHAT THIS FILE CANNOT DO, by construction. It performs NO name lookup, NO
// SID derivation, NO service control, NO permission read or write, NO
// certificate or key access, NO registry access, NO credential-vault access,
// NO path composition, NO process start, NO network access, and NO file
// access. It validates supplied text and nothing else. Nothing here proves any
// account exists on any machine.
//
// DELEGATION, and why there is no third primitive. Service-SID SHAPE is NOT
// re-implemented here. It is delegated to
// ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid, which is already
// compile-linked into the service. Re-implementing the shape would create two
// definitions of "is this a per-service SID" that could drift; extracting a
// third shared primitive would churn a byte-frozen file for no behavioural
// gain. Delegation keeps exactly ONE definition.

namespace PAXCookbook.Shared.Contracts;

/// <summary>
/// Closed, fixed identity of the PAX Cookbook machine service. Every value here
/// is a compile-time constant. Nothing on this type may be supplied, overridden,
/// or redirected by a caller, an environment variable, a configuration file, a
/// command-line argument, or a machine configuration key.
/// </summary>
public static class ServiceIdentityContract
{
    /// <summary>The service's fixed short name. Never localized, never configurable.</summary>
    public const string ServiceName = "PAXCookbookService";

    /// <summary>The service's fixed display name. Never localized, never configurable.</summary>
    public const string ServiceDisplayName = "PAXCookbook Machine Service";

    /// <summary>
    /// The fixed account domain for a per-service virtual account. This is the
    /// invariant Windows authority token, not a localized display string.
    /// </summary>
    public const string ServiceAccountDomain = "NT SERVICE";

    /// <summary>The single separator between account domain and account name.</summary>
    public const string AccountDomainSeparator = "\\";

    /// <summary>
    /// The one account name any resolution may ever use, COMPOSED from the
    /// domain, the separator, and the service name so the three can never drift
    /// apart into a hand-typed fourth value.
    /// </summary>
    public const string QualifiedServiceAccountName =
        ServiceAccountDomain + AccountDomainSeparator + ServiceName;

    /// <summary>
    /// Smallest possible binary SID: revision byte, subauthority-count byte, and
    /// the six-byte identifier authority.
    /// </summary>
    public const int MinSidByteLength = 8;

    /// <summary>
    /// Outer allocation bound for a binary SID. Generous relative to any real
    /// SID, and bounded so a hostile or malfunctioning size can never drive an
    /// unbounded allocation.
    /// </summary>
    public const int MaxSidByteLength = 256;

    /// <summary>
    /// Outer allocation bound, in characters INCLUDING the terminating null, for
    /// the referenced-domain buffer the lookup API requires. The buffer's
    /// CONTENT is never interpreted, returned, logged, persisted, or compared.
    /// </summary>
    public const int MaxDomainNameCharLength = 256;

    /// <summary>Outer bound, in characters, for any account name this product may present.</summary>
    public const int MaxAccountNameCharLength = 256;

    /// <summary>True only for the exact fixed service name, ordinal and case-sensitive.</summary>
    public static bool IsFixedServiceName(string? value) =>
        string.Equals(value, ServiceName, StringComparison.Ordinal);

    /// <summary>True only for the exact fixed display name, ordinal and case-sensitive.</summary>
    public static bool IsFixedServiceDisplayName(string? value) =>
        string.Equals(value, ServiceDisplayName, StringComparison.Ordinal);

    /// <summary>
    /// True only for the exact composed qualified account name. A case variation,
    /// an alternate separator, an unqualified name, or a localized domain is
    /// refused: there is exactly one accepted spelling.
    /// </summary>
    public static bool IsFixedQualifiedServiceAccountName(string? value) =>
        string.Equals(value, QualifiedServiceAccountName, StringComparison.Ordinal);

    /// <summary>
    /// Canonical per-service virtual account SID shape. DELEGATED, not
    /// re-implemented: authority 5, first subauthority 80, at least one further
    /// bounded subauthority, <c>S-1-5-80-0</c> refused because it denotes the
    /// whole services group, and NO exact subauthority count asserted because
    /// none is documented for a per-service SID.
    /// </summary>
    public static bool IsServiceIdentitySid(string? sid) =>
        ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(sid);

    /// <summary>True when a reported binary SID size is usable and inside the allocation bound.</summary>
    public static bool IsBoundedSidByteLength(int byteLength) =>
        byteLength >= MinSidByteLength && byteLength <= MaxSidByteLength;

    /// <summary>True when a reported domain character count is usable and inside the allocation bound.</summary>
    public static bool IsBoundedDomainCharLength(int charLength) =>
        charLength >= 1 && charLength <= MaxDomainNameCharLength;
}
