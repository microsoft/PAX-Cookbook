#if MANAGED_INVENTORY_PROVISIONING
using System;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provisioning;

// ---------------------------------------------------------------------------
// Gated dispatch for the managed-inventory provisioning verb (NON-LIVE this
// cycle). It exists ONLY in a build compiled with /p:ManagedInventoryProvisioning
// =true. In the DEFAULT product build this file is not compiled and the verb is
// rejected by the ordinary ArgParser (unknown verb -> usage error) before any
// machine access.
//
// This cycle the dispatch DELIBERATELY does NOT construct the executor or the OS
// adapters: it recognizes the verb + the five closed operations, validates the
// operation token, and returns a bounded "live execution not enabled this cycle"
// code. No inventory is mutated, no ACL is touched, no UAC is requested, and no
// executor call site exists in the runtime path.
// ---------------------------------------------------------------------------
internal static class ManagedInventoryProvisioningDispatch
{
    public const string Verb = "provision-managed-inventory";

    // Bounded outcome codes for the gated dispatch. Distinct from the ordinary
    // SetupExitCodes so the gate stays self-contained.
    public const int UsageError = 2;
    public const int LiveExecutionNotEnabledThisCycle = 210;

    public static bool IsRequested(string? verb)
        => string.Equals(verb, Verb, StringComparison.Ordinal);

    // Recognize the verb + one of the five closed operations, then STOP with a
    // bounded non-live code. Never constructs the executor / OS adapters.
    public static int Run(string[] args)
    {
        if (args.Length < 1 || !IsRequested(args[0]))
        {
            return UsageError;
        }
        if (args.Length < 2 || !TryParseOperation(args[1], out _))
        {
            return UsageError;
        }

        // Intentional hard stop: the elevated executor is fully implemented and
        // gated but NOT invoked this cycle. Nothing runs live.
        return LiveExecutionNotEnabledThisCycle;
    }

    // The five closed operations the verb accepts. Any other token is a usage
    // error; there is no open-ended verb/command surface.
    public static bool TryParseOperation(string? token, out ProvisioningOperation operation)
    {
        switch (token)
        {
            case "plan":
                operation = ProvisioningOperation.Plan;
                return true;
            case "apply":
                operation = ProvisioningOperation.Apply;
                return true;
            case "verify":
                operation = ProvisioningOperation.Verify;
                return true;
            case "rollback":
                operation = ProvisioningOperation.Rollback;
                return true;
            case "remove":
                operation = ProvisioningOperation.Remove;
                return true;
            default:
                operation = ProvisioningOperation.Plan;
                return false;
        }
    }
}
#endif
