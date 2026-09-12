using System;
using System.Text.Json;

namespace PAXCookbook.App;

// Deprovision plan/execute orchestration (Track 1 / T1-S3).
//
// Two-step, safe by construction:
//   * Prepare runs the fixed helper in read-only plan mode and mints a
//     short-lived plan token BOUND to the current configuration fingerprint.
//     Nothing is deleted.
//   * Execute requires the exact, unexpired, single-use plan token AND a
//     still-matching fingerprint, then runs the helper's real deprovision. Local
//     configuration is removed ONLY after verified complete cloud cleanup; a
//     partial failure preserves the local configuration with retry details. A
//     completed or replayed plan is refused.
internal static class ExperimentalWamDeprovisionCoordinator
{
    private static readonly TimeSpan PlanTtl = TimeSpan.FromMinutes(10);
    private static readonly object Gate = new();
    private static string? _planId;
    private static string? _planFingerprint;
    private static DateTimeOffset _planExpiry;

    internal sealed class PrepareResult
    {
        internal bool Ok { get; init; }
        internal string? Reason { get; init; }
        internal string? PlanId { get; init; }
        internal object? Categories { get; init; }
    }

    internal static PrepareResult Prepare(
        ExperimentalWamRuntime runtime,
        ExperimentalWamHelperRunner runner,
        bool isCompiled,
        DateTimeOffset nowUtc)
    {
        if (!isCompiled)
        {
            return new PrepareResult { Ok = false, Reason = "unavailable_in_this_build" };
        }

        ExperimentalWamAdminDetails? details = runtime.GetAdminDetails();
        if (details is null)
        {
            return new PrepareResult { Ok = false, Reason = "not_configured" };
        }

        ExperimentalWamHelperRunResult run = runner.Run(ExperimentalWamHelperOperation.DeprovisionPlan);
        if (!run.IsSuccess)
        {
            return new PrepareResult { Ok = false, Reason = ExperimentalWamHelperRunResult.ToReasonCode(run.Status) };
        }

        // Bounded, read-only plan categories (no identifiers).
        object? categories = TryReadCategories(run.RawJson);

        string fingerprint = ExperimentalWamVerificationStore.ComputeFingerprint(details.TenantId, details.ClientId);
        string planId = Guid.NewGuid().ToString("N");
        lock (Gate)
        {
            _planId = planId;
            _planFingerprint = fingerprint;
            _planExpiry = nowUtc.Add(PlanTtl);
        }

        return new PrepareResult { Ok = true, PlanId = planId, Categories = categories };
    }

    internal static ExperimentalWamActionResult Execute(
        ExperimentalWamRuntime runtime,
        ExperimentalWamHelperRunner runner,
        bool isCompiled,
        string? planId,
        DateTimeOffset nowUtc)
    {
        if (!isCompiled)
        {
            return ExperimentalWamActionResult.Fail("unavailable_in_this_build", runtime.GetState());
        }

        ExperimentalWamAdminDetails? details = runtime.GetAdminDetails();
        if (details is null)
        {
            return ExperimentalWamActionResult.Fail("not_configured", runtime.GetState());
        }

        string currentFingerprint = ExperimentalWamVerificationStore.ComputeFingerprint(details.TenantId, details.ClientId);

        // Validate + consume the single-use plan token under lock.
        lock (Gate)
        {
            if (string.IsNullOrEmpty(planId) ||
                _planId is null ||
                !string.Equals(planId, _planId, StringComparison.Ordinal))
            {
                return ExperimentalWamActionResult.Fail("invalid_or_replayed_plan", runtime.GetState());
            }
            if (nowUtc > _planExpiry)
            {
                _planId = null;
                _planFingerprint = null;
                return ExperimentalWamActionResult.Fail("plan_expired", runtime.GetState());
            }
            if (!string.Equals(_planFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                _planId = null;
                _planFingerprint = null;
                return ExperimentalWamActionResult.Fail("configuration_fingerprint_mismatch", runtime.GetState());
            }
            // Consume: a completed or replayed plan can never be reused.
            _planId = null;
            _planFingerprint = null;
        }

        ExperimentalWamHelperRunResult run = runner.Run(
            ExperimentalWamHelperOperation.Deprovision,
            extraArgs: new[] { "-Confirmed" });
        if (!run.IsSuccess)
        {
            // Helper failed: local configuration is preserved for retry.
            return ExperimentalWamActionResult.Fail(
                ExperimentalWamHelperRunResult.ToReasonCode(run.Status),
                runtime.GetState());
        }

        (bool absenceVerified, bool partial) = TryReadDeprovisionOutcome(run.RawJson);
        if (absenceVerified && !partial)
        {
            // Verified complete cleanup: remove local configuration.
            return runtime.RemoveLocalConfiguration();
        }

        // Partial failure: preserve local configuration + retry details.
        return ExperimentalWamActionResult.Fail("partial_deprovision", runtime.GetState());
    }

    private static object? TryReadCategories(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("objectCategories", out JsonElement cats) &&
                cats.ValueKind == JsonValueKind.Object)
            {
                // Return only booleans/counts (no identifiers).
                var result = new System.Collections.Generic.Dictionary<string, object?>();
                foreach (JsonProperty p in cats.EnumerateObject())
                {
                    result[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => p.Value.TryGetInt32(out int n) ? n : null,
                        _ => null,
                    };
                }
                return result;
            }
        }
        catch (JsonException)
        {
            // Ignore; categories are best-effort display only.
        }
        return null;
    }

    private static (bool AbsenceVerified, bool Partial) TryReadDeprovisionOutcome(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (false, true);
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            bool absence = root.TryGetProperty("absenceVerified", out JsonElement a) && a.ValueKind == JsonValueKind.True;
            bool partial = root.TryGetProperty("partialFailure", out JsonElement pf) &&
                           pf.ValueKind == JsonValueKind.Array &&
                           pf.GetArrayLength() > 0;
            return (absence, partial);
        }
        catch (JsonException)
        {
            return (false, true);
        }
    }
}
