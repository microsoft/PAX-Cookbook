using System;
using System.Collections.Generic;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup;

// Setup-side activation of the authoritative TEST ISOLATION context, mirroring
// the App's TestIsolationRuntime. The isolation CONTRACT/validator always
// compiles (via the Shared reference); ACTIVATION is BUILD-GATED behind
// PAXCOOKBOOK_TEST_ISOLATION. A stable/customer Setup build has no activation
// path: a --test-isolation argument is ignored and Setup behaves exactly as it
// does in production.
//
// When active, Setup enforces (before any download / copy / shell write /
// process stop / relaunch):
//   - a mutating verb REQUIRES an explicit --install-root that equals the
//     descriptor's isolated install root (never the real per-user install),
//   - all real-machine shell integration is suppressed,
//   - the network payload download is forbidden (a local --payload-root inside
//     the isolated root must be supplied).
public static class SetupTestIsolation
{
#pragma warning disable CS0649
    private static TestIsolationContext? _current;
#pragma warning restore CS0649

    public static TestIsolationContext? Context => _current;

    public static bool IsActive => _current is not null;

    // Activates isolation from the parsed --test-isolation descriptor. Returns a
    // fail-closed error code when isolation was requested but invalid, else null.
    public static int? TryActivate(string? descriptorPath, out IReadOnlyList<string> errors)
    {
        errors = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(descriptorPath)) return null;

#if PAXCOOKBOOK_TEST_ISOLATION
        TestIsolationParseResult result = TestIsolationParser.ParseFile(descriptorPath);
        if (!result.Ok || result.Context is null)
        {
            errors = result.Errors;
            return PAXCookbook.Shared.ExitCodes.SetupExitCodes.TestIsolationViolation;
        }
        _current = result.Context;
        return null;
#else
        return null;
#endif
    }

    // Verbs that mutate the install root and therefore require a validated
    // isolated --install-root under isolation.
    public static bool IsMutatingVerb(string verb) =>
        verb is "install" or "update" or "repair" or "apply-update" or "uninstall";

    // Validates that the supplied --install-root override is present and exactly
    // equals the descriptor's isolated install root. Missing, mismatched, or
    // real-root values fail closed.
    public static bool ValidateInstallRoot(string? installRootOverride, out string reason)
    {
        reason = string.Empty;
        if (_current is null) { reason = "isolation not active"; return false; }
        if (string.IsNullOrWhiteSpace(installRootOverride))
        {
            reason = "test-isolation requires an explicit --install-root";
            return false;
        }
        string full;
        try { full = System.IO.Path.GetFullPath(installRootOverride); }
        catch { reason = "--install-root is not a valid path"; return false; }

        if (!string.Equals(full, _current.InstallRoot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "--install-root does not match the isolated install root";
            return false;
        }
        return true;
    }
}
