using System;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.App;

// App-side activation of the authoritative TEST ISOLATION context.
//
// The isolation CONTRACT + validator (TestIsolationContext / TestIsolationParser)
// always compiles so it can be unit-tested and referenced. ACTIVATION — actually
// honoring a --test-isolation descriptor and placing this process into isolated
// mode — is BUILD-GATED behind PAXCOOKBOOK_TEST_ISOLATION, defined only by an
// explicit /p:TestIsolation=true build. A stable/customer build contains NO
// activation path: the --test-isolation argument is ignored, Current stays null,
// and every real-machine surface behaves exactly as it does today.
//
// When active, Current is an immutable, fully-validated context proving that:
//   - every isolated root is absolute and under one declared isolated root,
//   - that isolated root is wholly disjoint from the real install / workspace /
//     profile shell locations,
//   - update checks / network payload download / update apply / installed-product
//     discovery / shell integration are governed by explicit booleans,
// so no isolated process can resolve, write to, update, or discover the real
// per-user PAX Cookbook installation.
internal static class TestIsolationRuntime
{
    // Assigned only inside the PAXCOOKBOOK_TEST_ISOLATION activation branch;
    // in a stable build they stay null (isolation is impossible), which is the
    // intended behavior — suppress the "never assigned" note for the gated field.
#pragma warning disable CS0649
    private static TestIsolationContext? _current;
    private static string? _descriptorPath;
#pragma warning restore CS0649

    public static TestIsolationContext? Current => _current;

    public static bool IsActive => _current is not null;

    // The validated descriptor path, forwarded verbatim to every child process /
    // relaunch so the entire isolation context is preserved end to end.
    public static string? DescriptorPath => _descriptorPath;

    // Distinct nonzero exit code for a fail-closed isolation activation error.
    public const int IsolationActivationFailureExit = 78;

    // Reads --test-isolation <descriptorPath> and, in a test-isolation build,
    // validates it and enters isolated mode. Returns:
    //   - a nonzero exit code when isolation was REQUESTED but INVALID (fail
    //     closed — the caller must return it and start nothing), or
    //   - null when isolation is inactive (arg absent, or non-gated build) or
    //     activated successfully.
    public static int? TryActivate(string[] args)
    {
        string? descriptorPath = ReadArg(args, TestIsolationParser.DescriptorArg);
        if (descriptorPath is null) return null;

#if PAXCOOKBOOK_TEST_ISOLATION
        TestIsolationParseResult result = TestIsolationParser.ParseFile(descriptorPath);
        if (!result.Ok || result.Context is null)
        {
            try
            {
                Console.Error.WriteLine("FATAL: test-isolation descriptor rejected (fail closed):");
                foreach (string e in result.Errors) Console.Error.WriteLine("  " + e);
            }
            catch { /* console may be detached */ }
            return IsolationActivationFailureExit;
        }
        _current = result.Context;
        _descriptorPath = System.IO.Path.GetFullPath(descriptorPath);
        return null;
#else
        // Stable/customer build: isolation is impossible. The argument is ignored
        // and the process runs normally. It never enters isolated mode.
        return null;
#endif
    }

    private static string? ReadArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    // Exposes the exact descriptor-argument extraction the activation path uses,
    // so a launcher-quoting regression test can prove that a descriptor path
    // containing spaces MUST arrive as ONE argv element (returned intact) and
    // that a caller which lets the shell split the path on spaces yields only the
    // truncated first token — the real failure mode a naive Start-Process array
    // launcher introduces.
    internal static string? ReadDescriptorArg(string[] args) =>
        ReadArg(args, TestIsolationParser.DescriptorArg);
}
