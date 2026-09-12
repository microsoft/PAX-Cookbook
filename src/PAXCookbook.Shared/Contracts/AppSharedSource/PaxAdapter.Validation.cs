using System.Text.RegularExpressions;

namespace PAXCookbook.App;

// Canonical shared source. This partial carries ONLY the members the Recipe
// validator binds, so PAXCookbookSetup can run the same removed-switch refusal
// without compiling the argv projection, the secret-shape scan or engine acquisition.
internal static partial class PaxAdapter
{
    // Thrown by the projection-boundary guards. The preview route translates
    // this into an AJV-shaped validation error anchored on
    // /advanced/extraArguments (oracle parity).
    public sealed class ProjectionException : Exception
    {
        public ProjectionException(string message) : base(message) { }
    }

    // Oracle: $Script:RemovedSwitches (Adapter.psm1).
    private static readonly string[] RemovedSwitches =
    {
        "ExportWorkbook", "ExplodeArrays", "ExplodeDeep", "RawInputCSV",
        "IncludeAgent365Info", "OnlyAgent365Info", "OutputPathAgent365Info", "AppendAgent365Info",
    };

    // Case-insensitive '(^|\s)-<name>($|\s|=)' token match.
    private static bool HasSwitch(string trailer, string name)
    {
        if (string.IsNullOrWhiteSpace(trailer))
        {
            return false;
        }
        string pattern = @"(^|\s)-" + Regex.Escape(name) + @"($|\s|=)";
        return Regex.IsMatch(trailer, pattern, RegexOptions.IgnoreCase);
    }

    // Oracle: Test-ExtraArgumentsForRemovedSwitches (throws).
    public static void ScanRemovedSwitches(string extraArguments)
    {
        if (string.IsNullOrWhiteSpace(extraArguments))
        {
            return;
        }
        foreach (string name in RemovedSwitches)
        {
            if (HasSwitch(extraArguments, name))
            {
                throw new ProjectionException(
                    $"advanced.extraArguments contains removed switch '-{name}'. " +
                    "This switch was removed in PAX v1.11.2 and is not reintroduced via the verbatim trailer. " +
                    "Edit the recipe to remove it; the projection layer does not rewrite recipes.");
            }
        }
    }
}
