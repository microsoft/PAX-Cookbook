#if MANAGED_INVENTORY_PROVISIONING
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Provisioning;
using Xunit;

namespace PAXCookbookSetup.Tests.Provisioning;

// Cycle-8 gated dispatch / verb-surface proof (GATED, NON-LIVE). These tests
// exercise the ONLY runtime entry point that the managed-inventory provisioning
// feature adds to Setup: the gated verb dispatch. They prove the verb is
// recognized, exposes EXACTLY the five closed operations and nothing else,
// rejects every other token as a usage error, and — this cycle — always stops
// with a bounded "live execution not enabled" code WITHOUT ever constructing the
// executor, touching an ACL, mutating ProgramData, or requesting elevation.
//
// The complementary proof that the DEFAULT product build (compiled WITHOUT the
// managed-inventory switch) rejects the verb before any machine access is a
// build-level check: in that build this file and the dispatch are not compiled,
// so the verb falls through to the ordinary ArgParser as an unknown verb.
public sealed class ManagedInventoryProvisioningDispatchTests
{
    [Fact] // D01 — The verb token is recognized by the gated dispatch.
    public void D01_IsRequested_MatchesExactVerb()
    {
        Assert.True(ManagedInventoryProvisioningDispatch.IsRequested("provision-managed-inventory"));
        Assert.Equal("provision-managed-inventory", ManagedInventoryProvisioningDispatch.Verb);
    }

    [Theory] // D02 — No other verb spelling is ever recognized (closed, exact-match surface).
    [InlineData("provision")]
    [InlineData("Provision-Managed-Inventory")]
    [InlineData("provision-managed-inventory ")]
    [InlineData("bake")]
    [InlineData("run")]
    [InlineData("")]
    [InlineData(null)]
    public void D02_IsRequested_RejectsEveryOtherToken(string? verb)
    {
        Assert.False(ManagedInventoryProvisioningDispatch.IsRequested(verb));
    }

    [Theory] // D03 — Exactly the five closed operations parse; each maps to its enum.
    [InlineData("plan", ProvisioningOperation.Plan)]
    [InlineData("apply", ProvisioningOperation.Apply)]
    [InlineData("verify", ProvisioningOperation.Verify)]
    [InlineData("rollback", ProvisioningOperation.Rollback)]
    [InlineData("remove", ProvisioningOperation.Remove)]
    public void D03_TryParseOperation_AcceptsExactlyTheFiveClosedOperations(string token, ProvisioningOperation expected)
    {
        Assert.True(ManagedInventoryProvisioningDispatch.TryParseOperation(token, out ProvisioningOperation op));
        Assert.Equal(expected, op);
    }

    [Theory] // D04 — Any other operation token is rejected (no open-ended surface).
    [InlineData("Plan")]
    [InlineData("APPLY")]
    [InlineData("cook")]
    [InlineData("bake")]
    [InlineData("delete")]
    [InlineData("uninstall")]
    [InlineData("plan ")]
    [InlineData("")]
    [InlineData(null)]
    public void D04_TryParseOperation_RejectsEveryOtherToken(string? token)
    {
        Assert.False(ManagedInventoryProvisioningDispatch.TryParseOperation(token, out _));
    }

    [Fact] // D05 — Exactly five operations exist in the closed enum surface.
    public void D05_ExactlyFiveClosedOperations()
    {
        string[] accepted = { "plan", "apply", "verify", "rollback", "remove" };
        int parsed = 0;
        foreach (string token in accepted)
        {
            if (ManagedInventoryProvisioningDispatch.TryParseOperation(token, out _))
            {
                parsed++;
            }
        }

        Assert.Equal(5, parsed);
        Assert.Equal(5, System.Enum.GetValues(typeof(ProvisioningOperation)).Length);
    }

    [Fact] // D06 — Missing verb argument is a usage error (no machine access).
    public void D06_Run_NoArgs_UsageError()
    {
        Assert.Equal(ManagedInventoryProvisioningDispatch.UsageError,
            ManagedInventoryProvisioningDispatch.Run(System.Array.Empty<string>()));
    }

    [Fact] // D07 — A non-matching first argument is a usage error.
    public void D07_Run_WrongVerb_UsageError()
    {
        Assert.Equal(ManagedInventoryProvisioningDispatch.UsageError,
            ManagedInventoryProvisioningDispatch.Run(new[] { "bake", "apply" }));
    }

    [Fact] // D08 — The verb with NO operation is a usage error.
    public void D08_Run_VerbWithoutOperation_UsageError()
    {
        Assert.Equal(ManagedInventoryProvisioningDispatch.UsageError,
            ManagedInventoryProvisioningDispatch.Run(new[] { "provision-managed-inventory" }));
    }

    [Theory] // D09 — The verb with an UNKNOWN operation is a usage error.
    [InlineData("cook")]
    [InlineData("delete")]
    [InlineData("Plan")]
    [InlineData("")]
    public void D09_Run_VerbWithUnknownOperation_UsageError(string operation)
    {
        Assert.Equal(ManagedInventoryProvisioningDispatch.UsageError,
            ManagedInventoryProvisioningDispatch.Run(new[] { "provision-managed-inventory", operation }));
    }

    [Theory] // D10 — The verb with a VALID operation stops with the bounded non-live code.
    [InlineData("plan")]
    [InlineData("apply")]
    [InlineData("verify")]
    [InlineData("rollback")]
    [InlineData("remove")]
    public void D10_Run_VerbWithValidOperation_LiveExecutionNotEnabledThisCycle(string operation)
    {
        int code = ManagedInventoryProvisioningDispatch.Run(new[] { "provision-managed-inventory", operation });

        Assert.Equal(ManagedInventoryProvisioningDispatch.LiveExecutionNotEnabledThisCycle, code);
        Assert.Equal(210, code);
        // The non-live code is distinct from the usage error, so a recognized-but-
        // gated request is never confused with a rejected one.
        Assert.NotEqual(ManagedInventoryProvisioningDispatch.UsageError, code);
    }

    [Fact] // D11 — Extra trailing arguments after a valid verb+operation do not enable live execution.
    public void D11_Run_ExtraArgsAfterValidOperation_StillNonLive()
    {
        int code = ManagedInventoryProvisioningDispatch.Run(
            new[] { "provision-managed-inventory", "apply", "--whatever", "ignored" });

        Assert.Equal(ManagedInventoryProvisioningDispatch.LiveExecutionNotEnabledThisCycle, code);
    }
}
#endif
