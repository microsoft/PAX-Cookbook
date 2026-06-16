using System.Collections.Generic;

namespace PAXCookbook.Broker.Native.Models;

// Stage 3i-A -- cook readiness wire models.
//
// Mirrors Test-CookReadiness in app/broker/Routes/Cooks.ps1
// (the V1.S04 L3 readiness probes block, ~line 2547+). The wire
// shape is preserved verbatim: status / summary / checks[] with
// each check carrying id / label / scope / severity / status /
// detail / evidence / remediation.
//
// Scope notes for the native port:
//   * Read-only by doctrine -- no DB writes, no file writes, no
//     state mutation, no secret reads.
//   * Native subset of PS check coverage:
//       - resume.cook_id_format
//       - resume.cook_present
//       - resume.recipe_id_match
//       - resume.checkpoint_present (presence-only)
//       - recipe.recipe_id_format
//       - recipe.recipe_present
//       - recipe.snapshot_loadable
//       - pax.script_present
//       - pax.script_integrity      (SHA-256 vs VERSION.json baseline)
//       - workspace.directory_present
//       - workspace.database_present
//   * Checks the native broker has NOT yet ported (auth profile
//     presence, disk-space probe, recipe-PAX param matrix, network
//     reachability, M365 licensing) are emitted with status
//     "not_checked" and detail "<reason>" so the SPA renders honest
//     gaps instead of a fabricated green. This is the same pattern
//     the PS broker itself uses for the 'network' scope (its
//     `network.reachability` check is "not_checked" because PAX
//     owns reachability).
//   * Endpoint method is POST (matches the PS broker dispatcher in
//     Cooks.ps1 line 3320; the Stage 3i discovery doc's listing of
//     GET is corrected in the Stage 3i-A record).
public sealed record CookReadinessRequest(
    string? RecipeId,
    string? CookId);

public sealed record CookReadinessResult(
    string                       RecipeId,
    string                       ResumeCookId,
    string                       GeneratedAtUtc,
    string                       Status,
    CookReadinessSummary         Summary,
    IReadOnlyList<CookReadinessCheck> Checks);

public sealed record CookReadinessSummary(
    int Blocked,
    int Warning,
    int Ok,
    int NotChecked);

public sealed record CookReadinessCheck(
    string                              Id,
    string                              Label,
    string                              Scope,
    string                              Severity,
    string                              Status,
    string                              Detail,
    IReadOnlyDictionary<string, object?> Evidence,
    string                              Remediation);
