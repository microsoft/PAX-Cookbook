using System.Text.Json.Nodes;

namespace PAXCookbook.Broker.Native.Models;

// Stage 3i-B1 -- recipe mutation surface models.
//
// All recipe payloads are modelled as System.Text.Json.Nodes.JsonObject
// rather than typed records. The PowerShell broker treats the recipe
// as an opaque hashtable, server-stamps a fixed set of fields, and
// hands the whole thing to Test-RecipeAll. We mirror that exactly so
// every field the SPA sends round-trips unmodified through write +
// read.
//
// ValidationError matches the PS "AJV-shape" envelope emitted by
// New-ValidationError in RecipeValidator.ps1:
//
//   { instancePath, keyword, message, params }
//
// Params may be empty -- never null -- so the serialised JSON always
// carries the four keys. Tests assert on instancePath + keyword +
// (optionally) params.

public sealed record ValidationError(
    string InstancePath,
    string Keyword,
    string Message,
    IReadOnlyDictionary<string, object?> Params);

public sealed record ValidationVerdict(
    bool Ok,
    IReadOnlyList<ValidationError> Errors);

// Crockford Base32 alphabet used by New-RecipeId (Recipes.ps1 line 103).
// Same character set as the public ULID spec.
public static class CrockfordBase32
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
}

// Server-stamped createdBy provenance. Matches Get-RecipeCreatedByBlock
// in Recipes.ps1 line 80. Constructed at create-time, preserved
// verbatim on update.
public sealed record RecipeCreatedBy(
    string CookbookVersion,
    string BundledPaxVersion,
    string ReleaseChannel);

// Result of writing a recipe snapshot to disk -- temp+rename atomic.
// FileHash is lowercase hex SHA-256 of the final bytes; persisted in
// the recipes.file_hash column.
public sealed record RecipeSnapshotWriteResult(
    string FilePath,
    string FileHash);

// Status discriminator returned by RecipeSnapshotStore.Read -- mirrors
// PS Read-RecipeFile's four-status envelope. The mutation routes map
// each status to the canonical HTTP shape (404 / 422 / 500).
public enum RecipeFileStatus
{
    Ok,
    Missing,
    Malformed,
    UnsupportedSchemaVersion,
}

public sealed record RecipeFileLoad(
    RecipeFileStatus Status,
    JsonObject?      Recipe,
    string?          Detail);

// Row payload for Stage 3i-B1 INSERT. Matches Add-RecipeRow in
// Recipes.ps1 line 382. source defaults to 'new' / source_ref null
// when caller omits them.
public sealed record RecipeInsertRow(
    string RecipeId,
    string Name,
    string PaxAdapterVersion,
    int    RecipeSchemaVersion,
    string FilePath,
    string FileHash,
    string CreatedAt,
    string UpdatedAt,
    string Source     = "new",
    string? SourceRef = null);
