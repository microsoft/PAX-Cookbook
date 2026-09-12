namespace PAXCookbook.Shared.Contracts;

// PAX Cookbook - FIXED SERVICE MACHINE-STORAGE NAME AUTHORITY (cycle 51,
// extended cycle 63, extended cycle 82)
//
// WHAT THIS IS. A pure, portable, compile-time-only set of SIX fixed names
// describing where the service's machine-scoped METADATA directory lives, what
// its ownership-ledger and installation-anchor files are called, what its
// separate writable RUNTIME subdirectory is called, and what its fixed
// promoted-RECIPE store subdirectory is called. It composes NO path, reads
// NO environment variable, touches NO filesystem, and exposes NO configurable
// or settable value: every member here is a compile-time constant.
//
// CYCLE 82 - WHY A SIXTH NAME EXISTS. The promoted-Recipe persistence surface
// must derive its destination INTERNALLY and must never accept a
// caller-supplied path. It therefore needs a fixed leaf name for the machine
// Recipe store, and that name belongs here rather than as a local literal in
// the persistence file: retyping these names elsewhere is exactly the defect
// cycle 51 created this file to end.
//
// CYCLE 63 - WHY A FIFTH NAME EXISTS. Before this cycle the service's writable
// runtime documents (status, heartbeat, probe request, probe result) lived in
// the SAME directory as the installation anchor and the ownership ledger. That
// gave the service account write authority over the namespace that holds the
// records proving who owns the installation. RuntimeDataFolderName names a
// dedicated child directory so the service can be granted Modify on its own
// runtime state while holding only Traverse on the metadata parent. The
// separation is a SAFETY boundary, not a tidiness preference.
//
// WHY IT EXISTS. Before cycle 51, PAXCookbook.Service.ServiceContract retyped
// these three names as its own literals. Setup's fixed, read-only ownership-
// ledger reader (ServiceOwnershipLedgerReader, Setup-only) needs the SAME
// three names to compose the SAME path the service's own ServicePaths
// composes, without inventing a second spelling and without giving Setup or
// the service any new capability. This file is the single source of truth
// both sides now derive from - the SAME mechanism ServiceContract already
// uses for ServiceName/ServiceDisplayName (see ServiceIdentityContract).
//
// PORTABILITY. The service assembly targets net8.0 (not net8.0-windows), and
// this file is compile-linked into it, so it must never reference a
// Windows-only type such as System.Environment.SpecialFolder or anything
// under System.Security. It is five strings and nothing else.
//
// LEDGER NAME - DELIBERATE SHARED IDENTITY, NOT A HOMONYM (cycle 51 doctrine
// correction). OwnershipLedgerFileName below is DERIVED from
// ServiceOwnershipLedgerContract.LedgerFileName on purpose:
// ServiceContract.OwnershipLedgerFileName and ServiceOwnershipLedgerContract
// .LedgerFileName have always named the SAME file
// (%ProgramData%\PAXCookbook\Service\ownership-ledger.json) with the SAME
// schema, so deriving one from the other is architecturally correct, not a
// boundary violation. That is DIFFERENT from ServiceOwnershipLedgerContract
// .LedgerFileName's relationship to ProvisioningContract.LedgerFileName, which
// remains a genuine COINCIDENTAL HOMONYM (a different feature, directory and
// schema - the ManagedChefKeys ledger). See
// tests/PAXCookbook.Service.Tests/ServiceOwnershipLedgerBoundaryTests.cs for
// the corrected test doctrine.
public static class ServiceMachineStorageContract
{
    /// <summary>The fixed machine-root folder name beneath CommonApplicationData.</summary>
    public const string MachineRootFolderName = "PAXCookbook";

    /// <summary>
    /// The fixed service METADATA folder name beneath the machine root. It holds
    /// the ownership ledger and the installation anchor, and the service account
    /// is never granted write access to it.
    /// </summary>
    public const string ServiceDataFolderName = "Service";

    /// <summary>
    /// CYCLE 63. The fixed WRITABLE runtime folder name, a CHILD of the metadata
    /// folder. It is the only machine location the service account may write.
    /// Its contents are closed: the status document, the heartbeat document, the
    /// startup-probe request and result, and the fixed same-directory staging
    /// files the service itself generates. Nothing else may be placed here, and
    /// no ownership record may ever be moved into it.
    /// </summary>
    public const string RuntimeDataFolderName = "Runtime";

    /// <summary>
    /// The ownership-ledger leaf name. DERIVED from
    /// <see cref="ServiceOwnershipLedgerContract.LedgerFileName"/> - see the file
    /// header above. A const may reference another const, so this stays a
    /// compile-time constant and the shipped value is byte-for-byte unchanged.
    /// </summary>
    public const string OwnershipLedgerFileName = ServiceOwnershipLedgerContract.LedgerFileName;

    /// <summary>
    /// The installation-anchor leaf name (cycle 58), a SIBLING of the ownership
    /// ledger in the SAME Service folder and a COMPLETELY SEPARATE artifact: a
    /// different file, a different schema (ServiceInstallationAnchorContract)
    /// and a different feature identity. It is declared here as a plain literal
    /// rather than derived from the anchor contract on purpose - this type is
    /// compile-linked into PAXCookbook.Service, and deriving the value would drag
    /// the anchor contract into that assembly and hand the service a schema it
    /// has no reason to carry. This file is the single name authority; nothing
    /// else may retype it.
    /// </summary>
    public const string InstallationAnchorFileName = "installation-anchor.json";

    /// <summary>
    /// CYCLE 82. The fixed machine RECIPE-STORE folder name, a CHILD of the
    /// service metadata folder and a SIBLING of the runtime folder. It holds the
    /// Recipes a promotion transaction copied to machine scope, keyed by a
    /// bounded promoted-job id, and nothing else. It is DISTINCT from the runtime
    /// folder because a promoted Recipe is an ownership-transaction artifact, not
    /// service-writable runtime state, and it is DISTINCT from the metadata
    /// folder because the ownership records must never share a namespace with
    /// transaction payloads. Nothing composes a path from this name except the
    /// fixed promoted-Recipe persistence surface, which derives its destination
    /// internally and accepts no caller-supplied path.
    /// </summary>
    public const string MachineRecipeStoreFolderName = "Recipes";
}
