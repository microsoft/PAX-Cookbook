using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Read-only upcoming-schedule projection. Backs GET /api/v1/schedule/upcoming,
// which the pre-update bake check uses to ask "is a scheduled bake about to
// fire?" before it lets the app replace itself. It enumerates every active
// recipe's stored schedule block (the same read-only LoadForPreview path the
// detail route uses), keeps the enabled ones, and computes each one's next
// local fire time from its recurrence. It NEVER touches the Windows Task
// Scheduler, never shells the registrar, never runs PAX, never reads a secret,
// and never writes anything. Every returned value is metadata only (a recipe
// id, a recipe name, and an ISO timestamp) — constraint 14.
internal static partial class RecipeReadModel
{
    // GET /api/v1/schedule/upcoming — enabled scheduled bakes with their
    // computed next fire time, newest-first is not meaningful here so the order
    // is whatever the index yields. The window decision (e.g. "within the next
    // ten minutes") is left to the caller, which compares nextRunAt to now.
    public static object ListUpcomingScheduled(string workspacePath)
    {
        return ListUpcomingScheduled(workspacePath, DateTimeOffset.Now);
    }

    // Overload taking an explicit clock so the next-run math is unit-testable.
    internal static object ListUpcomingScheduled(string workspacePath, DateTimeOffset now)
    {
        var rows = new List<(DateTimeOffset Next, object Entry)>();

        foreach ((string recipeId, string name) in EnumerateActiveRecipeRows(workspacePath))
        {
            ScheduleInfo? schedule = ReadRecipeSchedule(workspacePath, recipeId);
            if (schedule is null || !schedule.Enabled)
            {
                continue;
            }
            DateTimeOffset? next = ComputeNextRun(schedule, now);
            if (next is null)
            {
                continue;
            }
            rows.Add((next.Value, new
            {
                recipeId,
                name,
                nextRunAt = next.Value.ToString("o", CultureInfo.InvariantCulture),
                recurrence = new
                {
                    kind = schedule.Kind,
                    hour = schedule.Hour,
                    minute = schedule.Minute,
                    daysOfWeek = schedule.DaysOfWeek,
                },
            }));
        }

        // Soonest first.
        rows.Sort((a, b) => a.Next.CompareTo(b.Next));
        var scheduled = new List<object>(rows.Count);
        foreach ((DateTimeOffset _, object entry) in rows)
        {
            scheduled.Add(entry);
        }

        return new
        {
            now = now.ToString("o", CultureInfo.InvariantCulture),
            scheduled,
        };
    }

    // Reads (recipe_id, name) for every non-deleted recipe from the read-only
    // index. Mirrors ListActive's read path; returns an empty list when the
    // workspace has no index yet or the table is unreadable.
    private static IEnumerable<(string RecipeId, string Name)> EnumerateActiveRecipeRows(string workspacePath)
    {
        var rows = new List<(string, string)>();
        try
        {
            using SqliteConnection? conn = OpenReadOnly(workspacePath);
            if (conn is null)
            {
                return rows;
            }
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT recipe_id, name FROM recipes WHERE deleted_at IS NULL;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }
        }
        catch (SqliteException)
        {
            rows.Clear();
        }
        return rows;
    }

    // Computes the next local fire time for an enabled schedule from its
    // recurrence, or null when the recurrence is unusable (bad time-of-day, or a
    // weekly schedule with no days). Mirrors Windows Task Scheduler semantics:
    // a daily schedule fires every day at hour:minute; a weekly schedule fires
    // on each listed day-of-week (0=Sunday..6=Saturday) at hour:minute. Local
    // time, because the registrar writes a local-time trigger.
    internal static DateTimeOffset? ComputeNextRun(ScheduleInfo schedule, DateTimeOffset now)
    {
        if (schedule.Hour < 0 || schedule.Hour > 23 || schedule.Minute < 0 || schedule.Minute > 59)
        {
            return null;
        }

        string kind = (schedule.Kind ?? string.Empty).Trim().ToLowerInvariant();

        if (kind == "daily")
        {
            DateTimeOffset candidate = new(
                now.Year, now.Month, now.Day, schedule.Hour, schedule.Minute, 0, now.Offset);
            if (candidate <= now)
            {
                candidate = candidate.AddDays(1);
            }
            return candidate;
        }

        if (kind == "weekly")
        {
            int[]? days = schedule.DaysOfWeek;
            if (days is null || days.Length == 0)
            {
                return null;
            }
            DateTimeOffset? soonest = null;
            // Look across the next eight days so the same weekday a week out is
            // considered when today's time has already passed.
            for (int offset = 0; offset <= 7; offset++)
            {
                DateTimeOffset day = now.AddDays(offset);
                int dow = (int)day.DayOfWeek; // 0=Sunday..6=Saturday
                if (Array.IndexOf(days, dow) < 0)
                {
                    continue;
                }
                DateTimeOffset candidate = new(
                    day.Year, day.Month, day.Day, schedule.Hour, schedule.Minute, 0, now.Offset);
                if (candidate <= now)
                {
                    continue;
                }
                if (soonest is null || candidate < soonest.Value)
                {
                    soonest = candidate;
                }
            }
            return soonest;
        }

        return null;
    }
}
