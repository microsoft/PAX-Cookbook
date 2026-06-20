using System.Globalization;
using System.Text.Json;

namespace PAXCookbook.App;

// Skip-next-bake marker store. The Bakes page "Skip next bake" action writes a
// per-recipe marker file recording the single scheduled run the operator chose
// to skip. The scheduled cook startup path (StartCookCore, CookKind.Scheduled)
// consults this store BEFORE any folder, row, or child is created: when a marker
// matches the run that is firing now, the run is skipped and the marker is
// consumed (deleted), so the FOLLOWING scheduled run proceeds normally.
//
// This deliberately does NOT touch the Windows Task Scheduler trigger (the OS
// task keeps firing on its normal cadence); the skip is enforced entirely inside
// the broker's own cook pipeline. The marker carries only non-secret metadata (a
// recipe id and two timestamps) — constraint 14 — and lives under the workspace
// the broker already owns.
internal static class ScheduleSkipMarker
{
    // A marker matches the firing run when the fire time is within this window of
    // the recorded skipped run time. The supported recurrences (daily / weekly)
    // are at least 24h apart, so a ±12h window uniquely identifies the intended
    // run while absorbing Task Scheduler fire jitter and clock skew.
    private static readonly TimeSpan MatchWindow = TimeSpan.FromHours(12);

    private static string MarkerDir(string workspacePath) =>
        Path.Combine(workspacePath, "Scheduler", "skip-markers");

    private static string MarkerPath(string workspacePath, string recipeId) =>
        Path.Combine(MarkerDir(workspacePath), recipeId + ".json");

    // Records that the run at skipRunAt should be skipped for this recipe.
    // Overwrites any existing marker (skipping again simply re-targets the next
    // run). Returns the ISO timestamp that was stored.
    internal static string Write(string workspacePath, string recipeId, DateTimeOffset skipRunAt)
    {
        Directory.CreateDirectory(MarkerDir(workspacePath));
        string iso = skipRunAt.ToString("o", CultureInfo.InvariantCulture);
        var payload = new
        {
            recipeId,
            skipRunAt = iso,
            createdAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
        };
        string json = JsonSerializer.Serialize(payload);
        File.WriteAllText(MarkerPath(workspacePath, recipeId), json);
        return iso;
    }

    // Returns true (and deletes the marker) when a marker exists for this recipe
    // whose skipped run time matches the supplied fire time. Best-effort and
    // never throws: a missing / unreadable / unparseable marker means "do not
    // skip". A stale marker whose skipped run is far in the past is treated as
    // expired and swept without skipping the current run.
    internal static bool ShouldSkipAndConsume(string workspacePath, string recipeId, DateTimeOffset now)
    {
        string path = MarkerPath(workspacePath, recipeId);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            DateTimeOffset? skipRunAt = ReadSkipRunAt(path);
            if (skipRunAt is null)
            {
                // Unreadable / malformed marker: sweep it so it cannot wedge runs.
                TryDelete(path);
                return false;
            }

            TimeSpan delta = now - skipRunAt.Value;
            if (delta < MatchWindow && delta > -MatchWindow)
            {
                // This is the run the operator chose to skip. Consume the marker
                // so the next scheduled run is not affected.
                TryDelete(path);
                return true;
            }

            // The fire time is not the skipped run. If the skipped run is well in
            // the past the marker is stale (the skipped run never fired); sweep it
            // so it cannot skip a future run by accident.
            if (delta >= MatchWindow)
            {
                TryDelete(path);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // Removes any marker for this recipe (called when a schedule is canceled or a
    // recipe is saved without a schedule, so no stale marker lingers).
    internal static void Clear(string workspacePath, string recipeId)
    {
        try
        {
            TryDelete(MarkerPath(workspacePath, recipeId));
        }
        catch
        {
            // best-effort
        }
    }

    private static DateTimeOffset? ReadSkipRunAt(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("skipRunAt", out JsonElement el) ||
                el.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            string? raw = el.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }
            if (DateTimeOffset.TryParse(
                    raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
            {
                return parsed;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
