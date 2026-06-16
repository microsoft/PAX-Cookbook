using System.Globalization;
using System.Text.Json.Nodes;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B1 -- recipe mutation orchestrator. Three operations:
// Create / Update / Delete. Each one runs validate + persist + roll
// back on failure. The HTTP route layer translates outcomes into the
// PS-parity envelopes; this layer never touches HttpContext.
//
// PS source: Invoke-RecipeCreate / Invoke-RecipeUpdate /
// Invoke-RecipeDelete in Recipes.ps1 lines 543, 597, 717.
public sealed class RecipeMutationService
{
    private readonly RecipeSnapshotStore     _snapshots;
    private readonly RecipeMutationStore     _rows;
    private readonly RecipeValidator         _validator;
    private readonly Func<DateTimeOffset>    _clock;
    private readonly Func<string>            _idFactory;
    private readonly string                  _paxAdapterVersion;
    private readonly RecipeCreatedBy         _createdBy;

    public RecipeMutationService(
        RecipeSnapshotStore  snapshots,
        RecipeMutationStore  rows,
        RecipeValidator      validator,
        Func<DateTimeOffset> clock,
        Func<string>         idFactory,
        string               paxAdapterVersion,
        RecipeCreatedBy      createdBy)
    {
        _snapshots         = snapshots;
        _rows              = rows;
        _validator         = validator;
        _clock             = clock;
        _idFactory         = idFactory;
        _paxAdapterVersion = paxAdapterVersion;
        _createdBy         = createdBy;
    }

    // ============================================================
    //  CREATE
    // ============================================================

    public CreateOutcome Create(JsonNode? requestBody)
    {
        if (requestBody is not JsonObject body)
            return CreateOutcome.InvalidJson;

        var now = ToIso(_clock());
        var id  = _idFactory();

        // Server-stamp identity fields. Matches Invoke-RecipeCreate.
        body["recipeId"]            = id;
        body["recipeSchemaVersion"] = RecipeValidator.SupportedSchemaVersion;
        body["paxAdapterVersion"]   = _paxAdapterVersion;
        body["createdAt"]           = now;
        body["updatedAt"]           = now;
        body["createdBy"] = new JsonObject
        {
            ["cookbookVersion"]   = _createdBy.CookbookVersion,
            ["bundledPaxVersion"] = _createdBy.BundledPaxVersion,
            ["releaseChannel"]    = _createdBy.ReleaseChannel,
        };

        var verdict = _validator.TestAll(body);
        if (!verdict.Ok) return CreateOutcome.ValidationFailed(verdict.Errors);

        // File-first, row-second. On row-insert failure delete the file.
        var write = _snapshots.Write(id, body);
        try
        {
            _rows.Insert(new RecipeInsertRow(
                RecipeId:            id,
                Name:                GetString(body, "identity", "name") ?? "",
                PaxAdapterVersion:   _paxAdapterVersion,
                RecipeSchemaVersion: RecipeValidator.SupportedSchemaVersion,
                FilePath:            write.FilePath,
                FileHash:            write.FileHash,
                CreatedAt:           now,
                UpdatedAt:           now));
        }
        catch
        {
            try { File.Delete(write.FilePath); } catch { /* best-effort rollback */ }
            throw;
        }

        return CreateOutcome.Created(id, body);
    }

    // ============================================================
    //  UPDATE
    // ============================================================

    public UpdateOutcome Update(string recipeId, JsonNode? requestBody)
    {
        var row = _rows.GetActiveRow(recipeId);
        if (row is null) return UpdateOutcome.NotFound;

        if (requestBody is not JsonObject body) return UpdateOutcome.InvalidJson;

        if (body.TryGetPropertyValue("recipeId", out var rid)
            && rid is JsonValue rv
            && rv.TryGetValue<string>(out var bodyId)
            && bodyId != recipeId)
        {
            return UpdateOutcome.IdMismatch(recipeId, bodyId);
        }

        var loaded = _snapshots.Read(recipeId, RecipeValidator.SupportedSchemaVersion);
        switch (loaded.Status)
        {
            case RecipeFileStatus.Missing:
                return UpdateOutcome.FileMissing(recipeId);
            case RecipeFileStatus.Malformed:
                return UpdateOutcome.FileMalformed(recipeId, loaded.Detail);
            case RecipeFileStatus.UnsupportedSchemaVersion:
                return UpdateOutcome.UnsupportedSchemaVersion(recipeId, loaded.Detail);
        }
        var existing = loaded.Recipe!;

        var now = ToIso(_clock());
        body["recipeId"]            = recipeId;
        body["recipeSchemaVersion"] = RecipeValidator.SupportedSchemaVersion;
        body["paxAdapterVersion"]   = _paxAdapterVersion;
        body["createdAt"]           = row.CreatedAt;
        body["updatedAt"]           = now;

        // Preserve provenance verbatim if present on disk; do NOT
        // invent createdBy when the on-disk recipe lacks it (legacy
        // pre-provenance behaviour).
        if (existing.TryGetPropertyValue("createdBy", out var cb) && cb is not null)
        {
            body["createdBy"] = cb.DeepClone();
        }
        else
        {
            body.Remove("createdBy");
        }

        var verdict = _validator.TestAll(body);
        if (!verdict.Ok) return UpdateOutcome.ValidationFailed(verdict.Errors);

        // File-first, row-second. Capture rollback bytes.
        var oldBytes = _snapshots.ReadRawBytes(recipeId);
        var write    = _snapshots.Write(recipeId, body);
        try
        {
            var affected = _rows.UpdateNameHashTimestamp(
                recipeId:  recipeId,
                name:      GetString(body, "identity", "name") ?? "",
                fileHash:  write.FileHash,
                updatedAt: now);
            if (affected != 1)
                throw new InvalidOperationException("row update affected " + affected + " rows; expected 1");
        }
        catch
        {
            if (oldBytes is not null) _snapshots.RestoreRawBytes(recipeId, oldBytes);
            throw;
        }

        return UpdateOutcome.Updated(recipeId, body);
    }

    // ============================================================
    //  DELETE
    // ============================================================

    public DeleteOutcome Delete(string recipeId)
    {
        var row = _rows.GetActiveRow(recipeId);
        if (row is null) return DeleteOutcome.NotFound;

        var nowDto = _clock();
        var now    = ToIso(nowDto);
        var stamp  = now.Replace(":", "").Replace("-", "").Replace(".", "");

        _snapshots.EnsureDirs();
        var moved = _snapshots.MoveToTrash(recipeId, stamp, out var trashPath);

        var affected = _rows.SetDeletedAt(recipeId, now);
        if (affected != 1)
        {
            // Rollback: move the file back.
            if (moved) _snapshots.MoveFromTrash(trashPath, recipeId);
            return DeleteOutcome.PersistFailure;
        }
        return DeleteOutcome.Deleted(recipeId, now, trashPath);
    }

    // ============================================================
    //  Helpers
    // ============================================================

    private static string ToIso(DateTimeOffset dto) =>
        dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string? GetString(JsonObject root, params string[] path)
    {
        JsonNode? cursor = root;
        foreach (var seg in path)
        {
            if (cursor is not JsonObject obj || !obj.TryGetPropertyValue(seg, out var next)) return null;
            cursor = next;
        }
        return cursor is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}

public abstract record CreateOutcome
{
    public sealed record CreatedResult(string RecipeId, JsonObject Recipe) : CreateOutcome;
    public sealed record InvalidJsonResult : CreateOutcome;
    public sealed record ValidationFailedResult(IReadOnlyList<ValidationError> Errors) : CreateOutcome;

    public static CreateOutcome InvalidJson = new InvalidJsonResult();
    public static CreateOutcome ValidationFailed(IReadOnlyList<ValidationError> errors) => new ValidationFailedResult(errors);
    public static CreateOutcome Created(string id, JsonObject body) => new CreatedResult(id, body);
}

public abstract record UpdateOutcome
{
    public sealed record UpdatedResult(string RecipeId, JsonObject Recipe) : UpdateOutcome;
    public sealed record NotFoundResult : UpdateOutcome;
    public sealed record InvalidJsonResult : UpdateOutcome;
    public sealed record IdMismatchResult(string UrlRecipeId, string BodyRecipeId) : UpdateOutcome;
    public sealed record ValidationFailedResult(IReadOnlyList<ValidationError> Errors) : UpdateOutcome;
    public sealed record FileMissingResult(string RecipeId) : UpdateOutcome;
    public sealed record FileMalformedResult(string RecipeId, string? Detail) : UpdateOutcome;
    public sealed record UnsupportedSchemaVersionResult(string RecipeId, string? Detail) : UpdateOutcome;

    public static UpdateOutcome NotFound    = new NotFoundResult();
    public static UpdateOutcome InvalidJson = new InvalidJsonResult();
    public static UpdateOutcome IdMismatch(string url, string body) => new IdMismatchResult(url, body);
    public static UpdateOutcome ValidationFailed(IReadOnlyList<ValidationError> errors) => new ValidationFailedResult(errors);
    public static UpdateOutcome FileMissing(string id) => new FileMissingResult(id);
    public static UpdateOutcome FileMalformed(string id, string? detail) => new FileMalformedResult(id, detail);
    public static UpdateOutcome UnsupportedSchemaVersion(string id, string? detail) => new UnsupportedSchemaVersionResult(id, detail);
    public static UpdateOutcome Updated(string id, JsonObject body) => new UpdatedResult(id, body);
}

public abstract record DeleteOutcome
{
    public sealed record DeletedResult(string RecipeId, string DeletedAt, string TrashPath) : DeleteOutcome;
    public sealed record NotFoundResult : DeleteOutcome;
    public sealed record PersistFailureResult : DeleteOutcome;

    public static DeleteOutcome NotFound       = new NotFoundResult();
    public static DeleteOutcome PersistFailure = new PersistFailureResult();
    public static DeleteOutcome Deleted(string id, string deletedAt, string trashPath) => new DeletedResult(id, deletedAt, trashPath);
}
