namespace PAXCookbook.App;

// Read-only schedule projection (X7a.1). Reuses the existing read-only recipe
// load (LoadForPreview) and projects ONLY the optional schedule block into a
// typed, secret-free record for the later scheduling slices (X7a.2 / X7a.3).
// Adds NO new I/O surface and NO HTTP route: it opens the workspace read-only
// exactly like the GET detail route, never writes, never repairs a file, and
// never touches the PAX engine. A schedule carries only non-secret metadata
// (a bool, a ULID task id, recurrence ints, a timestamp) -- constraint 14.
internal static partial class RecipeReadModel
{
    // Typed projection of a recipe's optional schedule block. DaysOfWeek is null
    // for a daily schedule and populated (0=Sunday..6=Saturday) for a weekly one.
    internal sealed record ScheduleInfo(
        bool Enabled,
        string? ScheduledTaskId,
        string Kind,
        int Hour,
        int Minute,
        int[]? DaysOfWeek,
        string? UpdatedAt);

    // Returns the recipe's schedule projection, or null when the recipe has no
    // schedule block (or cannot be loaded / is deleted / missing). Read-only:
    // loads through LoadForPreview, the same read path the GET detail route uses.
    internal static ScheduleInfo? ReadRecipeSchedule(string workspacePath, string recipeId)
    {
        PreviewLoadResult loaded = LoadForPreview(workspacePath, recipeId);
        if (loaded.Status != 200 || loaded.Recipe is null)
        {
            return null;
        }
        return ProjectSchedule(loaded.Recipe);
    }

    // Projects the schedule block from an already-loaded recipe tree, or null when
    // absent. A faithful read-only projection of the persisted (already-validated)
    // values; no mutation, no secret. Numeric leaves are coerced through
    // JsonModel.TryInt because a JSON integer arrives as long.
    internal static ScheduleInfo? ProjectSchedule(Dictionary<string, object?> recipe)
    {
        if (!recipe.TryGetValue("schedule", out object? scheduleObj) ||
            scheduleObj is not Dictionary<string, object?> schedule)
        {
            return null;
        }

        bool enabled = schedule.TryGetValue("enabled", out object? e) && e is bool eb && eb;
        string? scheduledTaskId =
            schedule.TryGetValue("scheduledTaskId", out object? t) && t is string ts ? ts : null;
        string? updatedAt =
            schedule.TryGetValue("updatedAt", out object? u) && u is string us ? us : null;

        string kind = string.Empty;
        int hour = 0;
        int minute = 0;
        int[]? daysOfWeek = null;

        if (schedule.TryGetValue("recurrence", out object? recurrenceObj) &&
            recurrenceObj is Dictionary<string, object?> recurrence)
        {
            if (recurrence.TryGetValue("kind", out object? k) && k is string ks)
            {
                kind = ks;
            }
            if (recurrence.TryGetValue("hour", out object? h) && JsonModel.TryInt(h, out int hv))
            {
                hour = hv;
            }
            if (recurrence.TryGetValue("minute", out object? m) && JsonModel.TryInt(m, out int mv))
            {
                minute = mv;
            }
            if (recurrence.TryGetValue("daysOfWeek", out object? d) && d is List<object?> dayList)
            {
                var days = new List<int>(dayList.Count);
                foreach (object? item in dayList)
                {
                    if (JsonModel.TryInt(item, out int di))
                    {
                        days.Add(di);
                    }
                }
                daysOfWeek = days.ToArray();
            }
        }

        return new ScheduleInfo(enabled, scheduledTaskId, kind, hour, minute, daysOfWeek, updatedAt);
    }
}
