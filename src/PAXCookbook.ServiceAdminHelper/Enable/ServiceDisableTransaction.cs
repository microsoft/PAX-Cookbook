// PAX Cookbook - SERVICE-DISABLE TRANSACTION (cycle 63R, helper only)
//
// WHAT THIS FILE IS. The ONE production implementation of a DISABLE
// IServiceIdentityBoundTransaction. It runs inside the elevated helper, inside
// the identity-bound window the cycle-58/59 channel opens, and it is the only
// place where the fixed service is stopped, deleted and its closed machine
// footprint removed.
//
// OWNER-BOUND, WITH NO OVERRIDE. The channel proves the LIVE kernel identity of
// the initiating user and refuses a validated anchor that names anyone else
// before this transaction is ever asked anything. There is no force flag, no
// takeover, no "disable anyway" and no administrative override in the grammar,
// in this file, or anywhere on the path. The three exceptional situations are
// spelled out and closed:
//
//   anchor ABSENT + ZERO footprint    -> AlreadyDisabled. Idempotent, safe.
//   anchor ABSENT + ANY footprint     -> RecoveryRequired. State nobody claims
//                                        is exactly what must NOT be deleted.
//   anchor different-owner, malformed
//   or unreadable                     -> RecoveryRequired (refused upstream by
//                                        the channel, and refused again here).
//
// THE REMOVAL ORDER, and it is not negotiable:
//   1. stop the fixed service if running, then WAIT for Stopped
//   2. delete the EXACT fixed service, then WAIT for proven SCM absence
//   3. remove ONLY the closed runtime files
//   4. remove the runtime directory, and only when EMPTY
//   5. remove ONLY the exact Program Files manifest members
//   6. remove the final directory, and only when EMPTY
//   7. remove the product root, and only when EMPTY
//   8. verify no service process remains
//   9. remove the MATCHING anchor - LAST
//  10. remove the metadata directory, and only when EMPTY
//  11. verify the complete closed footprint is absent
//
// WHAT IT WILL NEVER TOUCH. A service whose configuration is not the exact
// expected one. A path that is not one of the fixed composed locations. A
// directory that still holds an unknown member. A file that is not an exact
// manifest member or an exact closed runtime leaf. An owner or ACL it did not
// place. An ownership ledger - whose presence is probed for EXISTENCE ONLY and
// which is never opened, parsed, validated or enumerated. Any unknown sibling of
// any kind. When something unexpected is found, the operation REFUSES and
// preserves it; unknown state is preserved, never cleaned.
//
// FAILURE PRESERVES THE ANCHOR. A disable that cannot prove closure returns
// RecoveryRequired with the anchor intact, because the anchor is the record an
// attended recovery needs.
//
// PRIVACY - FAIL CLOSED. Every result is a bounded token. Nothing here returns a
// path, a SID, a member name, a hash, a native status or an exception.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// THE ONE FIXED SERVICE-DISABLE TRANSACTION. Every collaborator is injected so
/// no test can reach the real Program Files tree, the real Service Control
/// Manager or the real machine data directory; production supplies the real
/// adapters and no caller-supplied path enters through any of them.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServiceDisableTransaction : IServiceIdentityBoundTransaction
{
    private readonly string _metadataDirectory;
    private readonly IServiceEnableProgramFilesResolver _programFiles;
    private readonly IServiceControlManagerAdapter _serviceControl;
    private readonly IServiceEnablePayloadSource _payload;
    private readonly ServiceStartabilityCoordinator _waits;

    private ServiceEnableFixedPaths _paths;
    private IReadOnlyList<ServicePayloadManifestFileEntryFacts>? _manifestFacts;

    internal ServiceDisableTransaction(
        string metadataDirectory,
        IServiceEnableProgramFilesResolver programFiles,
        IServiceControlManagerAdapter serviceControl,
        IServiceEnablePayloadSource payload,
        ServiceStartabilityCoordinator waits)
    {
        _metadataDirectory = metadataDirectory;
        _programFiles = programFiles;
        _serviceControl = serviceControl;
        _payload = payload;
        _waits = waits;
    }

    /// <summary>The bounded outcome of the most recent phase.</summary>
    internal ServiceDisableOperationOutcome LastOutcome { get; private set; } =
        ServiceDisableOperationOutcome.Unspecified;

    /// <summary>The runtime root beneath the metadata directory. Composed, never supplied.</summary>
    internal string RuntimeDirectory =>
        Path.Combine(_metadataDirectory, ServiceMachineStorageContract.RuntimeDataFolderName);

    /// <summary>
    /// The CLOSED runtime leaf names. Nothing outside this set is ever removed
    /// from the runtime directory, and its presence makes the directory
    /// non-empty and therefore un-removable.
    /// </summary>
    internal static readonly string[] ClosedRuntimeLeafNames =
    {
        ServiceRuntimeDocumentContract.StatusFileName,
        ServiceRuntimeDocumentContract.HeartbeatFileName,
        ServiceRuntimeDocumentContract.ProbeRequestFileName,
        ServiceRuntimeDocumentContract.ProbeResultFileName,
    };

    /// <summary>
    /// MUTATION-FREE. It gathers observations and evaluates them. It removes
    /// nothing, creates nothing, changes no access control and performs no
    /// service control.
    /// </summary>
    public ServiceIdentityBoundTransactionPreflightState Preflight()
    {
        if (!OperatingSystem.IsWindows()
            || _programFiles is null || _serviceControl is null || _payload is null || _waits is null)
        {
            LastOutcome = ServiceDisableOperationOutcome.Unavailable;
            return ServiceIdentityBoundTransactionPreflightState.Refused;
        }

        if (ServiceEnableFixedPathResolver.TryResolve(_programFiles, out ServiceEnableFixedPaths paths)
            != ServiceEnablePathResolutionState.Resolved)
        {
            LastOutcome = ServiceDisableOperationOutcome.Unavailable;
            return ServiceIdentityBoundTransactionPreflightState.Refused;
        }

        _paths = paths;
        _manifestFacts = ServiceEnableTransaction.TryReadManifestFactsFrom(_payload);
        if (_manifestFacts is null)
        {
            // Without the exact expected member list nothing may be removed:
            // there is no way to tell an installed member from a stranger.
            LastOutcome = ServiceDisableOperationOutcome.Unavailable;
            return ServiceIdentityBoundTransactionPreflightState.Refused;
        }

        try
        {
            // A reparse point anywhere on the fixed path means a removal could
            // escape the intended location entirely.
            string? programFilesBase = Path.GetDirectoryName(paths.Root);
            if (string.IsNullOrEmpty(programFilesBase)
                || ServiceEnableExtractor.AnyComponentIsReparsePoint(paths.Root, programFilesBase))
            {
                LastOutcome = ServiceDisableOperationOutcome.FootprintRefused;
                return ServiceIdentityBoundTransactionPreflightState.RecoveryRequired;
            }

            // A present service must be EXACTLY ours. A mismatched service is
            // never stopped, never deleted and never adopted.
            ServiceQueryState presence = _serviceControl.QueryConfiguration(
                ServiceIdentityContract.ServiceName, out ServiceConfigurationSnapshot existing);

            if (presence == ServiceQueryState.Unavailable)
            {
                LastOutcome = ServiceDisableOperationOutcome.Unavailable;
                return ServiceIdentityBoundTransactionPreflightState.Refused;
            }

            if (presence == ServiceQueryState.Present
                && !ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final).MatchesExactly(existing))
            {
                LastOutcome = ServiceDisableOperationOutcome.RecoveryRequired;
                return ServiceIdentityBoundTransactionPreflightState.RecoveryRequired;
            }

            // A present final directory must hold EXACTLY the manifest members.
            if (Directory.Exists(paths.Final)
                && !ServiceEnableExtractor.VerifyExactMembership(paths.Final, _manifestFacts))
            {
                LastOutcome = ServiceDisableOperationOutcome.FootprintRefused;
                return ServiceIdentityBoundTransactionPreflightState.RecoveryRequired;
            }

            // A staging directory means a previous attempt did not finish. That
            // is unexplained state, and it is preserved rather than removed.
            if (Directory.Exists(paths.Staging) || File.Exists(paths.Staging))
            {
                LastOutcome = ServiceDisableOperationOutcome.FootprintRefused;
                return ServiceIdentityBoundTransactionPreflightState.RecoveryRequired;
            }

            // A present runtime directory must hold only closed leaves.
            if (Directory.Exists(RuntimeDirectory) && !RuntimeHoldsOnlyClosedLeaves())
            {
                LastOutcome = ServiceDisableOperationOutcome.FootprintRefused;
                return ServiceIdentityBoundTransactionPreflightState.RecoveryRequired;
            }

            LastOutcome = ServiceDisableOperationOutcome.Unspecified;
            return ServiceIdentityBoundTransactionPreflightState.Proceed;
        }
        catch (Exception)
        {
            LastOutcome = ServiceDisableOperationOutcome.Unavailable;
            return ServiceIdentityBoundTransactionPreflightState.Refused;
        }
    }

    /// <summary>
    /// The whole removal transaction, in the fixed order. It runs only after the
    /// channel proved the live initiating identity and classified the anchor.
    /// </summary>
    public ServiceIdentityBoundTransactionResult Apply(ServiceIdentityBoundTransactionContext context)
    {
        if (!OperatingSystem.IsWindows() || _manifestFacts is null || string.IsNullOrEmpty(_paths.Final))
        {
            LastOutcome = ServiceDisableOperationOutcome.Unavailable;
            return ServiceIdentityBoundTransactionResult.RecoveryRequired();
        }

        ServiceEnableFixedPaths paths = _paths;

        try
        {
            // ---- THE OWNER GATE, RESTATED HERE -----------------------------
            //
            // An anchor this attempt created would mean the disable path minted
            // an ownership record, which the NeverCreate policy forbids. Seeing
            // one is a contradiction, not a situation to work around.
            if (context.Presence == ServiceAnchorPresence.CreatedThisAttempt
                || context.AnchorCreatedThisAttempt)
            {
                LastOutcome = ServiceDisableOperationOutcome.RecoveryRequired;
                return ServiceIdentityBoundTransactionResult.RecoveryRequired();
            }

            if (context.Presence == ServiceAnchorPresence.Absent)
            {
                // No owner claims this machine state. Only the completely empty
                // case is safe, and it is idempotent rather than a removal.
                if (ClosedFootprintIsCompletelyAbsent(paths))
                {
                    LastOutcome = ServiceDisableOperationOutcome.AlreadyDisabled;
                    return ServiceIdentityBoundTransactionResult.Completed();
                }

                LastOutcome = ServiceDisableOperationOutcome.RecoveryRequired;
                return ServiceIdentityBoundTransactionResult.RecoveryRequired();
            }

            // ---- 1. STOP IF RUNNING, THEN WAIT FOR STOPPED -----------------
            ServiceQueryState presence = _serviceControl.QueryConfiguration(
                ServiceIdentityContract.ServiceName, out ServiceConfigurationSnapshot existing);

            if (presence == ServiceQueryState.Unavailable)
            {
                LastOutcome = ServiceDisableOperationOutcome.Unavailable;
                return ServiceIdentityBoundTransactionResult.RecoveryRequired();
            }

            if (presence == ServiceQueryState.Present)
            {
                if (!ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final)
                        .MatchesExactly(existing))
                {
                    LastOutcome = ServiceDisableOperationOutcome.RecoveryRequired;
                    return ServiceIdentityBoundTransactionResult.RecoveryRequired();
                }

                if (!_serviceControl.TryStopService(ServiceIdentityContract.ServiceName)
                    || !_waits.WaitForStopped())
                {
                    LastOutcome = ServiceDisableOperationOutcome.StopRefused;
                    return ServiceIdentityBoundTransactionResult.RecoveryRequired();
                }

                // ---- 2. DELETE THE EXACT SERVICE, THEN PROVE ABSENCE -------
                if (!_serviceControl.TryDeleteService(ServiceIdentityContract.ServiceName)
                    || !_waits.WaitForAbsence())
                {
                    LastOutcome = ServiceDisableOperationOutcome.RegistrationRemovalRefused;
                    return ServiceIdentityBoundTransactionResult.RecoveryRequired();
                }
            }

            // ---- 3 & 4. CLOSED RUNTIME FILES, THEN THE EMPTY RUNTIME DIR ---
            if (!RemoveClosedRuntimeState())
            {
                LastOutcome = ServiceDisableOperationOutcome.FootprintRefused;
                return ServiceIdentityBoundTransactionResult.RecoveryRequired();
            }

            // ---- 5, 6 & 7. EXACT MANIFEST MEMBERS, THEN EMPTY DIRECTORIES --
            if (!RemoveExactPayload(paths))
            {
                LastOutcome = ServiceDisableOperationOutcome.FootprintRefused;
                return ServiceIdentityBoundTransactionResult.RecoveryRequired();
            }

            // ---- 8. NO SERVICE PROCESS MAY REMAIN --------------------------
            if (_serviceControl.QueryConfiguration(ServiceIdentityContract.ServiceName, out _)
                != ServiceQueryState.Absent)
            {
                LastOutcome = ServiceDisableOperationOutcome.VerificationFailed;
                return ServiceIdentityBoundTransactionResult.RecoveryRequired();
            }

            // ---- 9. THE MATCHING ANCHOR, LAST ------------------------------
            //
            // The removal primitive refuses unless the bytes on disk still
            // validate AND carry the exact expected immutable identity, so a
            // changed, foreign or malformed anchor is never deleted.
            ServiceInstallationAnchorRemovalState removal =
                ServiceInstallationAnchorStore.RemoveAnchorCreatedThisTransactionFrom(
                    context.ServiceDirectory, context.Anchor);

            if (removal != ServiceInstallationAnchorRemovalState.Removed
                && removal != ServiceInstallationAnchorRemovalState.AlreadyAbsent)
            {
                LastOutcome = ServiceDisableOperationOutcome.RecoveryRequired;
                return ServiceIdentityBoundTransactionResult.RecoveryRequired();
            }

            // ---- 10. THE METADATA DIRECTORY, AND ONLY WHEN EMPTY -----------
            if (!RemoveDirectoryWhenEmpty(_metadataDirectory))
            {
                LastOutcome = ServiceDisableOperationOutcome.FootprintRefused;
                return ServiceIdentityBoundTransactionResult.RecoveryRequired();
            }

            // ---- 11. THE COMPLETE CLOSED FOOTPRINT MUST BE ABSENT ----------
            if (!ClosedFootprintIsCompletelyAbsent(paths))
            {
                LastOutcome = ServiceDisableOperationOutcome.VerificationFailed;
                return ServiceIdentityBoundTransactionResult.RecoveryRequired();
            }

            LastOutcome = ServiceDisableOperationOutcome.Completed;
            return ServiceIdentityBoundTransactionResult.Completed();
        }
        catch (Exception)
        {
            LastOutcome = ServiceDisableOperationOutcome.Unavailable;
            return ServiceIdentityBoundTransactionResult.RecoveryRequired();
        }
    }

    /// <summary>
    /// True when the runtime directory holds nothing except the closed leaves
    /// and the service's own same-directory staging files.
    /// </summary>
    internal bool RuntimeHoldsOnlyClosedLeaves()
    {
        try
        {
            if (Directory.GetDirectories(RuntimeDirectory).Length != 0)
            {
                return false;
            }

            foreach (string file in Directory.GetFiles(RuntimeDirectory))
            {
                if (!IsClosedRuntimeLeaf(Path.GetFileName(file)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A closed runtime leaf is one of the four fixed documents, or one of the
    /// service's own fixed same-directory staging files for those documents.
    /// </summary>
    internal static bool IsClosedRuntimeLeaf(string? leafName)
    {
        if (string.IsNullOrEmpty(leafName))
        {
            return false;
        }

        foreach (string closed in ClosedRuntimeLeafNames)
        {
            if (string.Equals(leafName, closed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (leafName.StartsWith(closed + ".staging-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool RemoveClosedRuntimeState()
    {
        try
        {
            if (!Directory.Exists(RuntimeDirectory))
            {
                return !File.Exists(RuntimeDirectory);
            }

            if (!RuntimeHoldsOnlyClosedLeaves())
            {
                return false;
            }

            foreach (string file in Directory.GetFiles(RuntimeDirectory))
            {
                File.Delete(file);
            }

            return RemoveDirectoryWhenEmpty(RuntimeDirectory);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool RemoveExactPayload(ServiceEnableFixedPaths paths)
    {
        try
        {
            if (Directory.Exists(paths.Final))
            {
                if (_manifestFacts is null
                    || !ServiceEnableExtractor.VerifyExactMembership(paths.Final, _manifestFacts))
                {
                    return false;
                }

                foreach (ServicePayloadManifestFileEntryFacts entry in _manifestFacts)
                {
                    string member = Path.GetFullPath(Path.Combine(paths.Final, entry.Name));

                    // Every removal target must still resolve INSIDE the final
                    // directory after canonicalization.
                    if (!member.StartsWith(
                            Path.TrimEndingDirectorySeparator(paths.Final) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (File.Exists(member))
                    {
                        File.Delete(member);
                    }
                }

                // Any directory the members occupied is removed only when empty,
                // deepest first, so an unknown sibling always blocks removal.
                foreach (string directory in Directory
                             .GetDirectories(paths.Final, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    if (!RemoveDirectoryWhenEmpty(directory))
                    {
                        return false;
                    }
                }

                if (!RemoveDirectoryWhenEmpty(paths.Final))
                {
                    return false;
                }
            }
            else if (File.Exists(paths.Final))
            {
                return false;
            }

            return RemoveDirectoryWhenEmpty(paths.Root);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a directory ONLY when it holds no member at all. An absent
    /// directory is already the desired state; a non-empty one is refused, never
    /// forced.
    /// </summary>
    internal static bool RemoveDirectoryWhenEmpty(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return !File.Exists(directoryPath);
            }

            if (Directory.GetFileSystemEntries(directoryPath).Length != 0)
            {
                return false;
            }

            Directory.Delete(directoryPath, recursive: false);
            return !Directory.Exists(directoryPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The COMPLETE closed footprint: the SCM entry, the Program Files final and
    /// staging locations, the runtime directory, the anchor, and the metadata
    /// directory. The ownership ledger is probed for EXISTENCE ONLY - it is
    /// never opened, parsed, validated or enumerated - because a surviving
    /// ledger means state this operation must not have removed.
    /// </summary>
    internal bool ClosedFootprintIsCompletelyAbsent(ServiceEnableFixedPaths paths)
    {
        try
        {
            if (_serviceControl.QueryConfiguration(ServiceIdentityContract.ServiceName, out _)
                != ServiceQueryState.Absent)
            {
                return false;
            }

            if (Directory.Exists(paths.Final) || File.Exists(paths.Final)
                || Directory.Exists(paths.Staging) || File.Exists(paths.Staging)
                || Directory.Exists(RuntimeDirectory) || File.Exists(RuntimeDirectory))
            {
                return false;
            }

            string anchorPath = Path.Combine(
                _metadataDirectory, ServiceMachineStorageContract.InstallationAnchorFileName);
            string ledgerPath = Path.Combine(
                _metadataDirectory, ServiceMachineStorageContract.OwnershipLedgerFileName);

            if (File.Exists(anchorPath) || Directory.Exists(anchorPath))
            {
                return false;
            }

            // EXISTENCE ONLY.
            if (File.Exists(ledgerPath) || Directory.Exists(ledgerPath))
            {
                return false;
            }

            // The metadata directory itself must be gone, or hold nothing at
            // all. An empty directory is not a footprint; a populated one is.
            return !Directory.Exists(_metadataDirectory)
                || Directory.GetFileSystemEntries(_metadataDirectory).Length == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
