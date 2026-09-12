// PAX Cookbook - SERVICE ADMIN HELPER ENTRY POINT (cycle 61)
//
// SECURITY CLASSIFICATION. This executable is UNSIGNED PRERELEASE CODE,
// accepted only for internal prerelease development and attended validation. An
// unsigned helper does not resist replacement by a local user; UAC may display
// an unknown publisher; prerelease validation proves functionality, not
// production tamper resistance. This is unacceptable for GA, and GA activation
// remains BLOCKED until this exact helper is Authenticode signed and the
// expected publisher policy is configured and verified (cycle-60 ruling).
//
// WHAT THIS ENTRY POINT DOES (cycle 94). It dispatches EXACTLY FOUR verbs -
// service-enable-elevated, service-disable-elevated, ownership-promote-elevated
// and ownership-depromote-elevated - each through its own single shared protocol
// authority, and REFUSES everything else. There is no fifth verb, no flag, no
// default action and no path, service name, account or command an argument could
// carry: the whole elevated vocabulary is a verb plus an endpoint name, a
// process id and a creation FILETIME, and all three of those values are compared
// against kernel-derived facts.
//
// THE VERB SELECTS THE OPERATION. The two ownership verbs each map to a CLOSED
// operation enum HERE, before dispatch, so the operation is fixed before any
// payload exists and is never inferred from a document's shape.
//
// IT IS NOT LAUNCHED IN THIS SLICE. Compile-time and packaging existence are
// authorized; execution is not. Nothing in this cycle ran this executable, and
// no UAC prompt, Program Files write, ProgramData write or Service Control
// Manager call occurred.
//
// WHAT THIS PROCESS STILL CANNOT DO, by construction. It starts, stops and
// controls no service - registration is where this cycle stops. It performs no
// PAX execution and no Bake, opens no certificate store, private key or
// credential vault, and writes no registry key.
using System;
using PAXCookbook.ServiceAdminHelper.Enable;
using PAXCookbookSetup.Service;

namespace PAXCookbook.ServiceAdminHelper;

internal static class Program
{
    /// <summary>
    /// The single bounded exit code for an unrecognised invocation. It says only
    /// that the helper refused; it carries no path, no state and no diagnostic
    /// detail.
    /// </summary>
    internal const int ExitRefusedNoAuthorizedOperation = 2;

    /// <summary>The single bounded refusal message. No path, no identity, no state.</summary>
    internal const string RefusalMessage =
        "PAX Cookbook service administrative helper: no authorized operation is available in this build.";

    private static int Main(string[] args)
    {
        if (args is not null && args.Length > 0
            && ServiceEnableVerbs.IsElevatedHelperRequested(args[0]))
        {
            return ServiceEnableElevatedDispatch.Run(args);
        }

        // CYCLE 63R. The SECOND elevated verb. It binds the NeverCreate
        // anchor policy, so a disable attempt can never mint an ownership record.
        if (args is not null && args.Length > 0
            && ServiceDisableVerbs.IsElevatedHelperRequested(args[0]))
        {
            return ServiceDisableElevatedDispatch.Run(args);
        }

        // CYCLE 94 PASS C. The THIRD and FOURTH - and last - elevated verbs. Each
        // one resolves its CLOSED operation from the VERB alone and hands that
        // operation to the dispatch, so the operation is decided before any
        // payload exists and can never be inferred from a document's shape. Both
        // bind the NeverCreate anchor policy.
        if (args is not null && args.Length > 0
            && ServiceOwnershipElevatedVerbs.IsPromoteRequested(args[0]))
        {
            return ServiceOwnershipElevatedDispatch.Run(args, ServiceOwnershipElevatedOperation.Promote);
        }

        if (args is not null && args.Length > 0
            && ServiceOwnershipElevatedVerbs.IsDepromoteRequested(args[0]))
        {
            return ServiceOwnershipElevatedDispatch.Run(args, ServiceOwnershipElevatedOperation.Depromote);
        }

        Console.Error.WriteLine(RefusalMessage);
        return ExitRefusedNoAuthorizedOperation;
    }
}
