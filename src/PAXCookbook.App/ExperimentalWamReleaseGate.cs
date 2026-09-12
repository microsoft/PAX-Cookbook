using System;
using System.Collections.Generic;

namespace PAXCookbook.App;

// Experimental Entra WAM release gates (Track 1 / T1-S2A) — internal code
// doctrine only.
//
// This type is NOT customer-facing: it adds no public documentation, no
// Settings text, and no HTTP route. It records, in code, the hard release
// blockers that must ALL be true before OAuth may enter stable/customer
// packaging. Until a separately authorized release-validation cycle passes with
// every gate satisfied, StableReleaseBlocked is true and the experimental
// provider stays disabled and unadvertised.
//
// It also states the binding POL-1 threat-model wording so the doctrine travels
// with the code: OAuth may resist other Windows users, casual same-user access,
// replay, and expired/cross-session authorization; it does NOT claim resistance
// to same-SID malware, same-integrity injection, broker-token theft,
// administrator compromise, or Hello-equivalent attacks. The experimental app
// is NOT a Microsoft first-party application.
internal static class ExperimentalWamReleaseGate
{
    internal const string Pol1ThreatModel =
        "POL-1: OAuth may resist other Windows users, casual same-user access, replay, and " +
        "expired/cross-session authorization. OAuth does NOT claim resistance to same-SID malware, " +
        "same-integrity injection, broker-token theft, administrator compromise, or Hello-equivalent " +
        "attacks. The experimental registration is not a Microsoft first-party application and is not " +
        "production-approved.";

    // The hard release blockers. Every flag is false in code: satisfaction is
    // ORGANIZATIONAL GOVERNANCE, not an application-code property, and is
    // asserted by a separately authorized release-validation cycle — never by
    // this build.
    internal sealed record ReleaseGate(string Id, string Requirement, bool Satisfied);

    internal static IReadOnlyList<ReleaseGate> Gates { get; } = new[]
    {
        new ReleaseGate("prod-registration",
            "A separate production Microsoft-owned Entra app registration exists (not the experimental registration).", false),
        new ReleaseGate("verified-publisher",
            "Verified publisher displays 'Microsoft Corporation' with the expected verified badge.", false),
        new ReleaseGate("internal-approvals",
            "Microsoft internal identity, security, privacy, legal, support, and application-governance approvals are complete.", false),
        new ReleaseGate("prod-audience",
            "The production tenant/account audience is approved.", false),
        new ReleaseGate("consent-experience",
            "The customer consent / admin-acquisition experience is approved.", false),
        new ReleaseGate("no-experimental-ids",
            "No experimental tenant/client/object identifiers remain in source, assets, metadata, docs, logs, or evidence.", false),
        new ReleaseGate("pol1-bounded",
            "Threat-model wording remains POL-1 bounded (no Hello-equivalent or same-SID-malware-resistance claim).", false),
        new ReleaseGate("release-validation",
            "A separately authorized release-validation cycle passes.", false),
    };

    // True unless EVERY hard gate is satisfied. This build cannot satisfy the
    // organizational gates, so it always evaluates to true: OAuth cannot enter
    // stable/customer packaging from this cycle.
    internal static bool StableReleaseBlocked
    {
        get
        {
            foreach (ReleaseGate g in Gates)
            {
                if (!g.Satisfied)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
