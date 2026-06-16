namespace PAXCookbook.Broker.Native.Models;

// Stage 3c -- minimal row projections matching the read-only column
// sets used by Routes/Recipes.ps1 (Get-RecipeRowsActive,
// Get-RecipeRow), Routes/Cooks.ps1 (Get-CookRow), and
// Routes/AuthProfiles.ps1 (Get-AuthProfileRowsAll, Get-AuthProfileRow).
//
// Nullable string fields surface as JSON null when the underlying
// column is NULL (matches the PowerShell broker, which projects
// IsDBNull -> $null and lets ConvertTo-Json emit null verbatim).

// Lightweight row used by GET /api/v1/recipes (list view).
// Mirrors Get-RecipeRowsActive in Routes/Recipes.ps1: filter
// WHERE deleted_at IS NULL, ORDER BY created_at DESC.
public sealed record RecipeListRow(
    string RecipeId,
    string Name,
    string Status,
    string? LastCookedAt,
    string? LastCookId,
    string CreatedAt,
    string UpdatedAt);

// Full row used by GET /api/v1/recipes/{ulid} (`meta` field of the
// response). Mirrors Get-RecipeRow exactly.
public sealed record RecipeMetaRow(
    string RecipeId,
    string Name,
    string FilePath,
    string FileHash,
    string Status,
    int IsPinned,
    string PaxAdapterVersion,
    int RecipeSchemaVersion,
    string Source,
    string? SourceRef,
    string? LastValidatedAt,
    string? LastValidationStatus,
    string? LastCookedAt,
    string? LastCookId,
    string CreatedAt,
    string UpdatedAt,
    string? DeletedAt);

// Cook row used by GET /api/v1/cooks (list view) and as the SQLite
// half of GET /api/v1/cooks/{id}. Mirrors Get-CookRow; the
// PowerShell broker enriches this with cook-folder evidence (snapshot
// name, cookbook version, artifact rollup, resumability) which is
// deferred from Stage 3c. CookFolder is the stored value verbatim --
// the PS broker's Resolve-CookFolder relocation logic is also
// deferred.
public sealed record CookRow(
    string CookId,
    string? RecipeId,
    string Status,
    int? ExitCode,
    int? Pid,
    string CookFolder,
    string PaxScriptPath,
    string PaxScriptVersion,
    string Trigger,
    string? StartedAt,
    string? FinishedAt,
    double? DurationSeconds,
    string? ErrorClass,
    string? ErrorMessage,
    string CreatedAt,
    string UpdatedAt,
    string? SummaryPath,
    string? ParentCookId);

// Auth profile row -- metadata only. The PowerShell broker's
// ConvertTo-AuthProfileRow projects exactly these columns; secret
// material is NEVER stored in this table -- it lives in Windows
// Credential Manager under CredManTarget (the LOOKUP KEY only, not
// the secret itself). Exposing the lookup key matches the existing
// PS broker contract.
public sealed record AuthProfileRow(
    string AuthProfileId,
    string Name,
    string Mode,
    string TenantId,
    string ClientId,
    string? CredManTarget,
    string? CertThumbprint,
    string? CertStore,
    string? Description,
    string? LastVerifiedAt,
    string? LastVerifiedResult,
    string CreatedAt,
    string UpdatedAt);
