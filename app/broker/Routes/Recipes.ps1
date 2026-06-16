#requires -Version 7.4

# Recipes.ps1
#
# Routes:
#   GET    /api/v1/recipes             -> list (filters out soft-deleted)
#   GET    /api/v1/recipes/{id}        -> single
#   POST   /api/v1/recipes             -> create
#   PUT    /api/v1/recipes/{id}        -> update
#   DELETE /api/v1/recipes/{id}        -> soft-delete (move file to _trash/, set deleted_at)
#   POST   /api/v1/recipes/preview     -> preview PAX argv (stateless;
#                                         body may be a full recipe draft
#                                         OR `{ "recipeId": "..." }` to
#                                         preview a stored copy)
#
# Persistence model:
#   - The .recipe.json file at <Workspace>/Recipes/<id>.recipe.json is the
#     source of truth. SQLite is a metadata-only index.
#   - Writes are file-first, row-second. On any row-write failure the file
#     is rolled back so the two never diverge silently.
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - $Script:SqliteConn      (open SqliteConnection)
#   - $Script:RecipesDir      (workspace Recipes path)
#   - $Script:RecipesTrashDir (workspace Recipes/_trash path)
#   - Get-UtcNowIso           (helper from broker)
#   - Write-JsonResponse      (helper from broker)
#   - Read-RequestJson        (helper from broker)
#   - Test-RecipeAll          (from RecipeValidator.ps1)
#   - Convert-RecipeToPaxArgv (from Pax\Adapter.psm1, Import-Module'd)
#   - Get-PaxInvocationPlan   (from Pax\Adapter.psm1, Import-Module'd)
#   - $Script:PaxScriptPath   (bundled PAX path; required by
#                              Get-PaxInvocationPlan to compose the
#                              spawn-side fields in the preview response.
#                              Populated at broker startup by
#                              Test-BundledPaxIntegrity.)
#   - $Script:PaxScriptVersion (bundled PAX version; populated at broker
#                               startup by Test-BundledPaxIntegrity from
#                               VERSION.json.paxScript.version; the single
#                               source of truth for the bundled PAX version
#                               that stamps newly-created and updated recipe
#                               rows + files).
#   - $Script:CookbookVersion   (Cookbook app version; populated at broker
#                               startup by Test-BundledPaxIntegrity from
#                               VERSION.json.cookbook.version; single source
#                               for stamping createdBy.cookbookVersion on
#                               newly-created recipes).
#   - $Script:ReleaseChannel    (release channel; populated at broker startup
#                               by Test-BundledPaxIntegrity from
#                               VERSION.json.channel; single source for
#                               stamping createdBy.releaseChannel on
#                               newly-created recipes).

$Script:M1_RecipeSchemaVer   = 1

# ---------------------------------------------------------------------
# Recipe provenance helper
# ---------------------------------------------------------------------
#
# Get-RecipeCreatedByBlock returns the bounded provenance structure
# stamped onto every newly-created recipe. It is the single point at
# which the broker captures "who created this recipe" — sourced
# exclusively from authoritative startup state ($Script:CookbookVersion,
# $Script:PaxScriptVersion, $Script:ReleaseChannel — all loaded by
# Test-BundledPaxIntegrity from VERSION.json at broker startup).
#
# Contract:
#   - Called ONLY on recipe CREATE and on draft PREVIEW (when filling
#     missing server-managed fields for validation).
#   - NEVER called on UPDATE. createdBy records who created the recipe,
#     not who last edited it, and must be preserved from the on-disk
#     copy across every PUT.
#   - NEVER called on LOAD. Older recipes that lack createdBy are
#     tolerated as-is; no auto-stamp, no inference, no rewrite.
#
# This function is the only place in the broker that synthesizes a
# createdBy object. Any future code path that needs to record creation
# provenance MUST call this helper rather than building the structure
# inline.
function Get-RecipeCreatedByBlock {
    if ([string]::IsNullOrWhiteSpace($Script:CookbookVersion) `
            -or [string]::IsNullOrWhiteSpace($Script:PaxScriptVersion) `
            -or [string]::IsNullOrWhiteSpace($Script:ReleaseChannel)) {
        # Broker startup populates all three; reaching here means a
        # startup invariant was violated. Throw rather than persisting a
        # half-populated provenance block.
        throw "recipe provenance state not initialized: cookbookVersion='$($Script:CookbookVersion)' bundledPaxVersion='$($Script:PaxScriptVersion)' releaseChannel='$($Script:ReleaseChannel)'"
    }
    return [ordered]@{
        cookbookVersion   = [string]$Script:CookbookVersion
        bundledPaxVersion = [string]$Script:PaxScriptVersion
        releaseChannel    = [string]$Script:ReleaseChannel
    }
}

# ---------------------------------------------------------------------
# ULID generator (Crockford base32, 26 chars, lexicographically sorted by
# time). Spec: https://github.com/ulid/spec. Hand-rolled because no
# stdlib helper exists and the corpus 04 schema mandates this exact
# pattern. ~30 lines, surgical helper.
# ---------------------------------------------------------------------

$Script:UlidAlphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'.ToCharArray()

function New-RecipeId {
    # 128-bit ULID: 48-bit ms-since-epoch timestamp (10 chars) + 80-bit
    # random (16 chars). Crockford base32 encoded big-endian.
    $msSinceEpoch = [int64]([datetimeoffset]::UtcNow.ToUnixTimeMilliseconds())

    # Timestamp portion (10 chars, big-endian base32 of 48-bit value).
    $tsChars = New-Object 'System.Char[]' 10
    $v = $msSinceEpoch
    for ($i = 9; $i -ge 0; $i--) {
        $tsChars[$i] = $Script:UlidAlphabet[[int]($v -band 0x1F)]
        $v = $v -shr 5
    }

    # Randomness portion (16 chars from 10 cryptographic-random bytes).
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $randBytes = New-Object 'System.Byte[]' 10
    $rng.GetBytes($randBytes)

    # Pack 80 bits across 16 base32 chars (5 bits each). Process the
    # bytes big-endian one bit at a time into an accumulator.
    $rndChars = New-Object 'System.Char[]' 16
    $bitBuf = [int64]0
    $bitCount = 0
    $outIdx = 0
    foreach ($b in $randBytes) {
        $bitBuf = ($bitBuf -shl 8) -bor [int64]$b
        $bitCount += 8
        while ($bitCount -ge 5) {
            $bitCount -= 5
            $val = [int](($bitBuf -shr $bitCount) -band 0x1F)
            $rndChars[$outIdx] = $Script:UlidAlphabet[$val]
            $outIdx++
        }
    }

    return (-join $tsChars) + (-join $rndChars)
}

# ---------------------------------------------------------------------
# Path helpers
# ---------------------------------------------------------------------

function Get-RecipeFilePath {
    param([string]$RecipeId)
    return (Join-Path $Script:RecipesDir ($RecipeId + '.recipe.json'))
}

function Get-RecipeTrashFilePath {
    param([string]$RecipeId, [string]$Timestamp)
    # Timestamp suffix prevents collision if a recipe is created, deleted,
    # re-created with the same id (vanishingly unlikely with ULID, but
    # cheap defense), and deleted again.
    return (Join-Path $Script:RecipesTrashDir ($RecipeId + '.recipe.' + $Timestamp + '.json'))
}

function Initialize-RecipesDirs {
    if (-not (Test-Path -LiteralPath $Script:RecipesDir -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $Script:RecipesDir -Force
    }
    if (-not (Test-Path -LiteralPath $Script:RecipesTrashDir -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $Script:RecipesTrashDir -Force
    }
}

# ---------------------------------------------------------------------
# File I/O
# ---------------------------------------------------------------------

function Write-RecipeFile {
    # Write-temp + rename atomic write so a concurrent reader never sees a
    # half-written file. Returns the SHA-256 file hash (hex, lowercase) of
    # the final bytes for storage in the index row.
    param([string]$RecipeId, $RecipeObject)

    $finalPath = Get-RecipeFilePath -RecipeId $RecipeId
    $tempPath  = $finalPath + '.tmp'
    $json      = $RecipeObject | ConvertTo-Json -Depth 12

    # UTF-8 no BOM for portability with non-Windows JSON tooling.
    [System.IO.File]::WriteAllText($tempPath, $json, [System.Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $finalPath) { Remove-Item -LiteralPath $finalPath -Force }
    Move-Item -LiteralPath $tempPath -Destination $finalPath -Force

    $hashObj = Get-FileHash -LiteralPath $finalPath -Algorithm SHA256
    return $hashObj.Hash.ToLowerInvariant()
}

function Read-RecipeFile {
    # Discriminated load result. Phase AD: load-side failure modes are
    # surfaced verbatim — the recipe surface refuses to silently coerce
    # invalid recipes, auto-heal malformed JSON, or flatten distinct
    # failures into one undifferentiated error.
    #
    # Returns a hashtable with .status ∈ { 'ok', 'missing', 'malformed',
    # 'unsupported_schema_version' }:
    #
    #   ok                          — file present, JSON parsed,
    #                                 recipeSchemaVersion = M1; .recipe
    #                                 holds the parsed object.
    #   missing                     — file does not exist on disk; the
    #                                 SQLite row (if present) is stale.
    #                                 .recipe = $null.
    #   malformed                   — file exists but failed JSON parse
    #                                 OR parsed to a non-object root.
    #                                 .recipe = $null; .detail holds the
    #                                 trimmed parser message.
    #   unsupported_schema_version  — file parsed, but recipeSchemaVersion
    #                                 is missing, non-integer, or ≠ the
    #                                 broker's supported version. The
    #                                 parsed object is returned in .recipe
    #                                 so the caller can introspect it
    #                                 without having to re-parse; .detail
    #                                 names the observed version. The
    #                                 caller MUST NOT treat such a value
    #                                 as authoritative — schema gates
    #                                 above this function still run for
    #                                 paths that mutate.
    #
    # This function is read-only. It performs NO auto-repair, NO
    # rewrites, NO logging, NO side effects. Callers translate the
    # status into the appropriate HTTP shape; this layer does not know
    # about HTTP.
    param([string]$RecipeId)
    $path = Get-RecipeFilePath -RecipeId $RecipeId
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return @{ status = 'missing'; recipe = $null; detail = $null }
    }

    try {
        $raw = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
    } catch {
        return @{
            status = 'malformed'
            recipe = $null
            detail = ('file_read_failed: ' + [string]$_.Exception.Message)
        }
    }

    # -DateKind String prevents PowerShell 7.5+ from auto-coercing ISO 8601
    # timestamp strings (createdAt, updatedAt) into [datetime] objects on the
    # round-trip; the recipe schema declares those fields as type=string and
    # the validator's `-is [string]` check rejects DateTime values.
    $parsed = $null
    try {
        $parsed = $raw | ConvertFrom-Json -AsHashtable -Depth 12 -DateKind String
    } catch {
        return @{
            status = 'malformed'
            recipe = $null
            detail = ('json_parse_failed: ' + [string]$_.Exception.Message)
        }
    }

    # ConvertFrom-Json returns $null on the JSON literal `null`. A null
    # root is not a valid recipe. Treat it as malformed (structural
    # mismatch) rather than letting it propagate as a "successful" load.
    if ($null -eq $parsed -or -not ($parsed -is [hashtable])) {
        return @{
            status = 'malformed'
            recipe = $null
            detail = 'json_root_not_object'
        }
    }

    # Schema-version gate. The broker supports exactly one schema
    # version (M1 = $Script:M1_RecipeSchemaVer). A recipe that parsed
    # cleanly but declares a different version is "unsupported", NOT
    # "malformed" — the file is structurally JSON, the schema version
    # is simply outside this broker's compatibility window. The
    # distinction matters: malformed = chef's text editor did the
    # wrong thing; unsupported = a future broker wrote this file and
    # the current broker has no migration path.
    if (-not $parsed.ContainsKey('recipeSchemaVersion')) {
        return @{
            status = 'unsupported_schema_version'
            recipe = $parsed
            detail = 'absent'
        }
    }
    $observedVersion = $parsed.recipeSchemaVersion
    # Be tolerant of strings here even though the schema declares an
    # integer — the goal is to identify the unsupported-version state
    # truthfully, not to perform full schema validation in the loader.
    $observedInt = $null
    if ($observedVersion -is [int]) {
        $observedInt = [int]$observedVersion
    } elseif ($observedVersion -is [long]) {
        $observedInt = [int]$observedVersion
    } elseif ($observedVersion -is [string] -and ($observedVersion -match '^\d+$')) {
        $observedInt = [int]$observedVersion
    }
    if ($null -eq $observedInt -or $observedInt -ne $Script:M1_RecipeSchemaVer) {
        return @{
            status = 'unsupported_schema_version'
            recipe = $parsed
            detail = ('observed=' + [string]$observedVersion)
        }
    }

    return @{ status = 'ok'; recipe = $parsed; detail = $null }
}

# ---------------------------------------------------------------------
# SQLite row helpers (parameterized)
# ---------------------------------------------------------------------

function Get-RecipeRow {
    param([string]$RecipeId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
SELECT recipe_id, name, file_path, file_hash, status, is_pinned,
       pax_adapter_version, recipe_schema_version, source, source_ref,
       last_validated_at, last_validation_status,
       last_cooked_at, last_cook_id,
       created_at, updated_at, deleted_at
FROM recipes WHERE recipe_id = $id;
'@
    $p = $cmd.CreateParameter(); $p.ParameterName = '$id'; $p.Value = $RecipeId; [void]$cmd.Parameters.Add($p)
    $reader = $cmd.ExecuteReader()
    try {
        if (-not $reader.Read()) { return $null }
        return [ordered]@{
            recipe_id              = $reader.GetString(0)
            name                   = $reader.GetString(1)
            file_path              = $reader.GetString(2)
            file_hash              = $reader.GetString(3)
            status                 = $reader.GetString(4)
            is_pinned              = [int]$reader.GetValue(5)
            pax_adapter_version    = $reader.GetString(6)
            recipe_schema_version  = [int]$reader.GetValue(7)
            source                 = $reader.GetString(8)
            source_ref             = if ($reader.IsDBNull(9))  { $null } else { $reader.GetString(9) }
            last_validated_at      = if ($reader.IsDBNull(10)) { $null } else { $reader.GetString(10) }
            last_validation_status = if ($reader.IsDBNull(11)) { $null } else { $reader.GetString(11) }
            last_cooked_at         = if ($reader.IsDBNull(12)) { $null } else { $reader.GetString(12) }
            last_cook_id           = if ($reader.IsDBNull(13)) { $null } else { $reader.GetString(13) }
            created_at             = $reader.GetString(14)
            updated_at             = $reader.GetString(15)
            deleted_at             = if ($reader.IsDBNull(16)) { $null } else { $reader.GetString(16) }
        }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
}

function Get-RecipeRowsActive {
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
SELECT recipe_id, name, status,
       last_cooked_at, last_cook_id,
       created_at, updated_at
FROM recipes
WHERE deleted_at IS NULL
ORDER BY created_at DESC;
'@
    $reader = $cmd.ExecuteReader()
    $rows = New-Object System.Collections.Generic.List[object]
    try {
        while ($reader.Read()) {
            $rows.Add( [ordered]@{
                recipeId       = $reader.GetString(0)
                name           = $reader.GetString(1)
                status         = $reader.GetString(2)
                lastCookedAt   = if ($reader.IsDBNull(3)) { $null } else { $reader.GetString(3) }
                lastCookId     = if ($reader.IsDBNull(4)) { $null } else { $reader.GetString(4) }
                createdAt      = $reader.GetString(5)
                updatedAt      = $reader.GetString(6)
            } )
        }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
    return $rows.ToArray()
}

function Add-RecipeRow {
    # Inserts a new row into the recipes table. The `source` and
    # `source_ref` columns record where the recipe came from:
    #
    #   source = 'new'        source_ref = NULL                       (recipe created directly by the operator)
    #   source = 'template'   source_ref = '<templateId>@<version>'   (recipe materialized from a bundled Pantry template)
    #
    # Both are write-once at create time and not rewritten by update or
    # rename. The Row hashtable carries the values explicitly — the
    # caller is responsible for setting them coherently. Default values
    # match the legacy 'new' caller so existing call sites keep working
    # without change.
    param([hashtable]$Row)
    $source    = if ($Row.ContainsKey('source')    -and -not [string]::IsNullOrWhiteSpace([string]$Row.source))    { [string]$Row.source }    else { 'new' }
    $sourceRef = if ($Row.ContainsKey('source_ref') -and -not [string]::IsNullOrWhiteSpace([string]$Row.source_ref)) { [string]$Row.source_ref } else { $null }

    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
INSERT INTO recipes
    (recipe_id, name, description, tags_json,
     pax_adapter_version, recipe_schema_version,
     source, source_ref, file_path, file_hash,
     status, is_pinned, created_at, updated_at)
VALUES
    ($recipe_id, $name, NULL, '[]',
     $pax_adapter_version, $recipe_schema_version,
     $source, $source_ref, $file_path, $file_hash,
     'ready', 0, $created_at, $updated_at);
'@
    $pairs = @(
        @('$recipe_id',             $Row.recipe_id),
        @('$name',                  $Row.name),
        @('$pax_adapter_version',   $Row.pax_adapter_version),
        @('$recipe_schema_version', $Row.recipe_schema_version),
        @('$source',                $source),
        @('$source_ref',            ($(if ($null -eq $sourceRef) { [System.DBNull]::Value } else { $sourceRef }))),
        @('$file_path',             $Row.file_path),
        @('$file_hash',             $Row.file_hash),
        @('$created_at',            $Row.created_at),
        @('$updated_at',            $Row.updated_at)
    )
    foreach ($pair in $pairs) {
        $p = $cmd.CreateParameter(); $p.ParameterName = $pair[0]; $p.Value = $pair[1]; [void]$cmd.Parameters.Add($p)
    }
    try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Update-RecipeRow {
    param([string]$RecipeId, [string]$Name, [string]$FileHash, [string]$UpdatedAt)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
UPDATE recipes
SET name = $name,
    file_hash = $file_hash,
    status = 'ready',
    updated_at = $updated_at
WHERE recipe_id = $recipe_id AND deleted_at IS NULL;
'@
    foreach ($pair in @(
        @('$name',       $Name),
        @('$file_hash',  $FileHash),
        @('$updated_at', $UpdatedAt),
        @('$recipe_id',  $RecipeId)
    )) {
        $p = $cmd.CreateParameter(); $p.ParameterName = $pair[0]; $p.Value = $pair[1]; [void]$cmd.Parameters.Add($p)
    }
    try { return [int]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Set-RecipeRowDeleted {
    param([string]$RecipeId, [string]$DeletedAt)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'UPDATE recipes SET deleted_at = $deleted_at WHERE recipe_id = $recipe_id AND deleted_at IS NULL;'
    foreach ($pair in @(
        @('$deleted_at', $DeletedAt),
        @('$recipe_id',  $RecipeId)
    )) {
        $p = $cmd.CreateParameter(); $p.ParameterName = $pair[0]; $p.Value = $pair[1]; [void]$cmd.Parameters.Add($p)
    }
    try { return [int]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

# ---------------------------------------------------------------------
# Route handlers
# ---------------------------------------------------------------------

function Write-RecipeValidationErrorResponse {
    param($Context, [array]$Errors)
    Write-JsonResponse -Context $Context -Status 400 -Body @{
        error  = 'validation_failed'
        errors = $Errors
    }
}

function Invoke-RecipesList {
    param($Context)
    $rows = Get-RecipeRowsActive
    Write-JsonResponse -Context $Context -Status 200 -Body @{ recipes = $rows }
}

function Invoke-RecipeGet {
    param($Context, [string]$RecipeId)
    $row = Get-RecipeRow -RecipeId $RecipeId
    if (-not $row -or $row.deleted_at) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found' }
        return
    }
    # Phase AD: load-side failure modes are surfaced distinctly so the
    # chef can tell missing (workspace state drift) from malformed
    # (text-editor damage) from unsupported_schema_version (a future
    # broker wrote this file). The single-label collapse used before
    # this phase ("recipe_file_missing" for all three) violated the
    # slice mandate: "Do NOT flatten all failures into 'invalid recipe.'"
    $loaded = Read-RecipeFile -RecipeId $RecipeId
    switch ($loaded.status) {
        'ok' {
            Write-JsonResponse -Context $Context -Status 200 -Body @{ recipe = $loaded.recipe; meta = $row }
            return
        }
        'missing' {
            Write-JsonResponse -Context $Context -Status 404 -Body @{
                error    = 'recipe_file_missing'
                recipeId = $RecipeId
            }
            return
        }
        'malformed' {
            Write-JsonResponse -Context $Context -Status 422 -Body @{
                error    = 'recipe_file_malformed'
                recipeId = $RecipeId
                detail   = [string]$loaded.detail
            }
            return
        }
        'unsupported_schema_version' {
            Write-JsonResponse -Context $Context -Status 422 -Body @{
                error                  = 'recipe_unsupported_schema_version'
                recipeId               = $RecipeId
                supportedSchemaVersion = $Script:M1_RecipeSchemaVer
                detail                 = [string]$loaded.detail
            }
            return
        }
        default {
            # Defensive — Read-RecipeFile is the only source of these
            # values. An unknown status is a programming error in this
            # broker, not chef-recoverable, so surface it as 500.
            Write-JsonResponse -Context $Context -Status 500 -Body @{
                error    = 'recipe_load_unknown_status'
                recipeId = $RecipeId
                status   = [string]$loaded.status
            }
            return
        }
    }
}

function Invoke-RecipeCreate {
    param($Context)
    $body = Read-RequestJson -Context $Context
    if ($null -eq $body) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    # Server assigns recipeId, schema version, bundled-PAX version,
    # timestamps, and the createdBy provenance block. Any client-supplied
    # values for these are overwritten. All version fields read from
    # $Script:* state populated at broker startup by
    # Test-BundledPaxIntegrity from VERSION.json — the single source of
    # truth.
    $now    = Get-UtcNowIso
    $id     = New-RecipeId
    $body.recipeId            = $id
    $body.recipeSchemaVersion = $Script:M1_RecipeSchemaVer
    $body.paxAdapterVersion   = $Script:PaxScriptVersion
    $body.createdAt           = $now
    $body.updatedAt           = $now
    # createdBy is set ONCE at create from authoritative startup state
    # and preserved verbatim across every subsequent update. It is the
    # explicit, persisted provenance future migration decisions will
    # rely on — never inferred, never auto-rewritten.
    $body.createdBy           = Get-RecipeCreatedByBlock

    $verdict = Test-RecipeAll -Recipe $body
    if (-not $verdict.ok) {
        Write-RecipeValidationErrorResponse -Context $Context -Errors $verdict.errors
        return
    }

    Initialize-RecipesDirs

    # File-first, row-second. On row-insert failure, delete the file so the
    # two never diverge.
    $hash = Write-RecipeFile -RecipeId $id -RecipeObject $body
    try {
        Add-RecipeRow -Row @{
            recipe_id             = $id
            name                  = [string]$body.identity.name
            pax_adapter_version   = $Script:PaxScriptVersion
            recipe_schema_version = $Script:M1_RecipeSchemaVer
            file_path             = (Get-RecipeFilePath -RecipeId $id)
            file_hash             = $hash
            created_at            = $now
            updated_at            = $now
        }
    } catch {
        $fp = Get-RecipeFilePath -RecipeId $id
        if (Test-Path -LiteralPath $fp) { Remove-Item -LiteralPath $fp -Force -ErrorAction SilentlyContinue }
        throw
    }

    Write-JsonResponse -Context $Context -Status 201 -Body @{ recipeId = $id; recipe = $body }
}

function Invoke-RecipeUpdate {
    param($Context, [string]$RecipeId)
    $row = Get-RecipeRow -RecipeId $RecipeId
    if (-not $row -or $row.deleted_at) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found' }
        return
    }

    $body = Read-RequestJson -Context $Context
    if ($null -eq $body) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    if ($body.ContainsKey('recipeId') -and ([string]$body.recipeId -ne $RecipeId)) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error       = 'id_mismatch'
            urlRecipeId = $RecipeId
            bodyRecipeId = [string]$body.recipeId
        }
        return
    }

    # Load the existing on-disk recipe so we can preserve provenance
    # (createdAt + the createdBy block) verbatim across this update.
    # Provenance records who CREATED the recipe; PUT only changes the
    # logical leaves. If the on-disk recipe lacks createdBy (older
    # recipe pre-provenance), we deliberately do NOT infer one — the
    # absence itself is meaningful and is the signal future migration
    # logic will rely on.
    #
    # Phase AD: before this slice the loader's exception was silently
    # swallowed (try{}catch{$existing=$null}) so a malformed-JSON file
    # was overwritten as if it were "legacy with no createdBy". That
    # auto-heal behavior is exactly what the slice mandate forbids:
    # "Do NOT auto-heal malformed JSON." We now refuse the PUT in any
    # non-ok load state, with the same distinguishable labels GET uses.
    $loaded = Read-RecipeFile -RecipeId $RecipeId
    switch ($loaded.status) {
        'ok' { } # fall through — proceed with the update
        'missing' {
            Write-JsonResponse -Context $Context -Status 422 -Body @{
                error    = 'recipe_file_missing'
                recipeId = $RecipeId
            }
            return
        }
        'malformed' {
            Write-JsonResponse -Context $Context -Status 422 -Body @{
                error    = 'recipe_file_malformed'
                recipeId = $RecipeId
                detail   = [string]$loaded.detail
            }
            return
        }
        'unsupported_schema_version' {
            Write-JsonResponse -Context $Context -Status 422 -Body @{
                error                  = 'recipe_unsupported_schema_version'
                recipeId               = $RecipeId
                supportedSchemaVersion = $Script:M1_RecipeSchemaVer
                detail                 = [string]$loaded.detail
            }
            return
        }
        default {
            Write-JsonResponse -Context $Context -Status 500 -Body @{
                error    = 'recipe_load_unknown_status'
                recipeId = $RecipeId
                status   = [string]$loaded.status
            }
            return
        }
    }
    $existing = $loaded.recipe

    $now = Get-UtcNowIso
    $body.recipeId            = $RecipeId
    $body.recipeSchemaVersion = $Script:M1_RecipeSchemaVer
    $body.paxAdapterVersion   = $Script:PaxScriptVersion
    $body.createdAt           = $row.created_at
    $body.updatedAt           = $now
    if ($null -ne $existing -and $existing.ContainsKey('createdBy')) {
        $body.createdBy = $existing.createdBy
    } else {
        # Legacy recipe with no createdBy on disk. We deliberately do
        # NOT infer one — drop any client-supplied createdBy so we don't
        # invent provenance and so the absence stays observable.
        if ($body.ContainsKey('createdBy')) { [void]$body.Remove('createdBy') }
    }
    # importMetadata is OPTIONAL provenance stamped once at recipe
    # creation (Mini-Kitchen lite import path). Treat it the same way
    # as createdBy: preserve the on-disk value verbatim across PUT,
    # never invent one, never let the client overwrite it. The SPA
    # editor does not need to know about importMetadata — recipes
    # without it round-trip cleanly, recipes with it keep it.
    if ($null -ne $existing -and $existing.ContainsKey('importMetadata')) {
        $body.importMetadata = $existing.importMetadata
    } else {
        if ($body.ContainsKey('importMetadata')) { [void]$body.Remove('importMetadata') }
    }

    $verdict = Test-RecipeAll -Recipe $body
    if (-not $verdict.ok) {
        Write-RecipeValidationErrorResponse -Context $Context -Errors $verdict.errors
        return
    }

    # File-first. Capture old bytes so we can roll back if the row update
    # fails. (The old bytes have already been checksummed in the row.)
    $filePath = Get-RecipeFilePath -RecipeId $RecipeId
    $oldBytes = $null
    if (Test-Path -LiteralPath $filePath -PathType Leaf) {
        $oldBytes = [System.IO.File]::ReadAllBytes($filePath)
    }
    $hash = Write-RecipeFile -RecipeId $RecipeId -RecipeObject $body
    try {
        $affected = Update-RecipeRow -RecipeId $RecipeId -Name ([string]$body.identity.name) -FileHash $hash -UpdatedAt $now
        if ($affected -ne 1) {
            throw "row update affected $affected rows; expected 1"
        }
    } catch {
        if ($null -ne $oldBytes) {
            [System.IO.File]::WriteAllBytes($filePath, $oldBytes)
        }
        throw
    }

    Write-JsonResponse -Context $Context -Status 200 -Body @{ recipeId = $RecipeId; recipe = $body }
}

function Invoke-RecipeDelete {
    param($Context, [string]$RecipeId)
    $row = Get-RecipeRow -RecipeId $RecipeId
    if (-not $row -or $row.deleted_at) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found' }
        return
    }

    Initialize-RecipesDirs
    $now      = Get-UtcNowIso
    $stamp    = $now.Replace(':','').Replace('-','').Replace('.','')
    $filePath = Get-RecipeFilePath -RecipeId $RecipeId
    $trashPath = Get-RecipeTrashFilePath -RecipeId $RecipeId -Timestamp $stamp

    if (Test-Path -LiteralPath $filePath -PathType Leaf) {
        Move-Item -LiteralPath $filePath -Destination $trashPath -Force
    }

    $affected = Set-RecipeRowDeleted -RecipeId $RecipeId -DeletedAt $now
    if ($affected -ne 1) {
        # Roll the file back if the row update unexpectedly fails.
        if (Test-Path -LiteralPath $trashPath -PathType Leaf) {
            Move-Item -LiteralPath $trashPath -Destination $filePath -Force
        }
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'delete_failed' }
        return
    }

    Write-JsonResponse -Context $Context -Status 200 -Body @{ recipeId = $RecipeId; deletedAt = $now; trashPath = $trashPath }
}

function Invoke-RecipePreview {
    param($Context)
    $body = Read-RequestJson -Context $Context
    if ($null -eq $body) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    # Preview accepts either { recipeId: "..." } (load and preview stored
    # copy) or a full recipe draft body. The discriminator is "is this
    # only a recipeId, or does it look like a full recipe?".
    $isLookup = $false
    if ($body.ContainsKey('recipeId') -and -not $body.ContainsKey('identity')) {
        $isLookup = $true
    }

    if ($isLookup) {
        $rid = [string]$body.recipeId
        $row = Get-RecipeRow -RecipeId $rid
        if (-not $row -or $row.deleted_at) {
            Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found'; recipeId = $rid }
            return
        }
        # Phase AD: preview's stored-recipe path uses the same
        # discriminated load shape as GET so the editor sees one
        # consistent error vocabulary across read surfaces. A malformed
        # or unsupported file is NOT silently treated as "missing".
        $loaded = Read-RecipeFile -RecipeId $rid
        switch ($loaded.status) {
            'ok' {
                $recipe = $loaded.recipe
            }
            'missing' {
                Write-JsonResponse -Context $Context -Status 404 -Body @{
                    error    = 'recipe_file_missing'
                    recipeId = $rid
                }
                return
            }
            'malformed' {
                Write-JsonResponse -Context $Context -Status 422 -Body @{
                    error    = 'recipe_file_malformed'
                    recipeId = $rid
                    detail   = [string]$loaded.detail
                }
                return
            }
            'unsupported_schema_version' {
                Write-JsonResponse -Context $Context -Status 422 -Body @{
                    error                  = 'recipe_unsupported_schema_version'
                    recipeId               = $rid
                    supportedSchemaVersion = $Script:M1_RecipeSchemaVer
                    detail                 = [string]$loaded.detail
                }
                return
            }
            default {
                Write-JsonResponse -Context $Context -Status 500 -Body @{
                    error    = 'recipe_load_unknown_status'
                    recipeId = $rid
                    status   = [string]$loaded.status
                }
                return
            }
        }
    } else {
        # Draft preview. Server fills the fields that the editor doesn't
        # ask the user to manage so validation succeeds for a UI body
        # that has only the human-managed leaves. None of these fills
        # persist; preview is stateless.
        if (-not $body.ContainsKey('recipeId'))            { $body.recipeId            = (New-RecipeId) }
        if (-not $body.ContainsKey('recipeSchemaVersion')) { $body.recipeSchemaVersion = $Script:M1_RecipeSchemaVer }
        if (-not $body.ContainsKey('paxAdapterVersion'))   { $body.paxAdapterVersion   = $Script:PaxScriptVersion }
        if (-not $body.ContainsKey('createdBy'))           { $body.createdBy           = Get-RecipeCreatedByBlock }
        $verdict = Test-RecipeAll -Recipe $body
        if (-not $verdict.ok) {
            Write-RecipeValidationErrorResponse -Context $Context -Errors $verdict.errors
            return
        }
        $recipe = $body
    }

    # Render the full invocation plan. The trust-surface contract is:
    # "preview shows the same authoritative projection that cook spawn
    # uses". Both call Get-PaxInvocationPlan with the same recipe and
    # the same bundled PAX path, so the two cannot drift.
    #
    # Response fields (additive; existing `command` consumer remains
    # the canonical PAX command string):
    #   command        - rendered single-line PAX command (== command.txt)
    #   argv           - canonical PAX argv tokens (UNQUOTED logical args)
    #   extraArguments - trimmed verbatim trailer (or '')
    #   spawn.command  - human-readable rendering of the actual subprocess
    #                    invocation (`pwsh -NoProfile -NoLogo -Command ...`)
    #   spawn.argv     - 4-element ProcessStartInfo.ArgumentList that
    #                    would be passed to pwsh.exe at cook time
    #                    (matches cooks.command_argv_json on cook).
    # Projection-time errors (e.g. removed-switch detected in
    # extraArguments) surface as a 412 validation error.
    #
    # Phase AF: when recipe.auth.mode is AppRegistrationSecret or
    # AppRegistrationCertificate, resolve the auth profile row before
    # projecting so the adapter can emit -ClientId (and
    # -ClientCertificateThumbprint for cert mode). Profile lookup failures
    # surface as AJV-shape errors anchored on /auth/authProfileId so
    # the editor places them on the right field. The recipe's own
    # executionMode is forwarded so the adapter's projection-boundary
    # local/hosted gate fires before the supervisor's spawn gate.
    $authMode = ''
    $authProfileId = ''
    if ($recipe.ContainsKey('auth')) {
        if ($recipe.auth.ContainsKey('mode'))          { $authMode      = [string]$recipe.auth.mode }
        if ($recipe.auth.ContainsKey('authProfileId')) { $authProfileId = [string]$recipe.auth.authProfileId }
    }
    $executionMode = ''
    if ($recipe.ContainsKey('executionMode')) {
        $executionMode = [string]$recipe.executionMode
    }
    $authProfileRow = $null
    if ($authMode -eq 'AppRegistrationSecret' -or $authMode -eq 'AppRegistrationCertificate') {
        if ([string]::IsNullOrWhiteSpace($authProfileId)) {
            Write-RecipeValidationErrorResponse -Context $Context -Errors @(
                (New-ValidationError -InstancePath '/auth/authProfileId' `
                                     -Keyword 'required' `
                                     -Message "must have required property 'authProfileId' when auth.mode is '$authMode'" `
                                     -Params @{ missingProperty = 'authProfileId' })
            )
            return
        }
        $authProfileRow = Get-AuthProfileRow -AuthProfileId $authProfileId
        if ($null -eq $authProfileRow) {
            Write-RecipeValidationErrorResponse -Context $Context -Errors @(
                (New-ValidationError -InstancePath '/auth/authProfileId' `
                                     -Keyword 'profileNotFound' `
                                     -Message "auth profile '$authProfileId' does not exist" `
                                     -Params @{ authProfileId = $authProfileId })
            )
            return
        }
        if ([string]$authProfileRow.mode -ne $authMode) {
            Write-RecipeValidationErrorResponse -Context $Context -Errors @(
                (New-ValidationError -InstancePath '/auth/authProfileId' `
                                     -Keyword 'profileModeMismatch' `
                                     -Message ("auth profile '$authProfileId' is mode '" + [string]$authProfileRow.mode + "' but recipe.auth.mode is '$authMode'") `
                                     -Params @{ recipeMode = $authMode; profileMode = [string]$authProfileRow.mode })
            )
            return
        }
    }
    try {
        $plan = Get-PaxInvocationPlan -Recipe $recipe -PaxScriptPath $Script:PaxScriptPath -AuthProfile $authProfileRow -ExecutionMode $executionMode
    } catch {
        # Defensive fallback. With the Phase J Test-RecipeAll additions,
        # the removed-switch trailer is already caught up-front and a
        # well-anchored AJV-shape error has been emitted. Any throw
        # that still reaches this catch is an unexpected projection-
        # time failure; surface it as a validation error anchored on
        # /advanced/extraArguments (the only operator-editable input
        # that can influence projection) using the AJV shape so the
        # editor places it correctly.
        Write-RecipeValidationErrorResponse -Context $Context -Errors @(
            (New-ValidationError -InstancePath '/advanced/extraArguments' `
                                 -Keyword 'projection' `
                                 -Message ([string]$_.Exception.Message) `
                                 -Params @{})
        )
        return
    }

    Write-JsonResponse -Context $Context -Status 200 -Body @{
        recipeId       = [string]$recipe.recipeId
        command        = $plan.paxCommand
        argv           = $plan.paxArgv
        extraArguments = $plan.extraArguments
        spawn          = @{
            command = $plan.spawnCommand
            argv    = $plan.spawnArgv
        }
    }
}

# ---------------------------------------------------------------------
# Route dispatch entry point
# ---------------------------------------------------------------------

# Pattern: /api/v1/recipes(/<id>)?(/preview)?
$Script:RecipeIdPattern = '^[0-9A-HJKMNP-TV-Z]{26}$'

function Invoke-RecipesRoute {
    # Returns $true if the request was consumed by this handler.
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    # /api/v1/recipes/preview (stateless preview, no id in URL).
    if ($path -eq '/api/v1/recipes/preview' -and $method -eq 'POST') {
        Invoke-RecipePreview -Context $Context
        return $true
    }

    # /api/v1/recipes
    if ($path -eq '/api/v1/recipes') {
        switch ($method) {
            'GET'  { Invoke-RecipesList   -Context $Context; return $true }
            'POST' { Invoke-RecipeCreate  -Context $Context; return $true }
            default {
                Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
                return $true
            }
        }
    }

    # /api/v1/recipes/<id>
    if ($path -match '^/api/v1/recipes/([^/]+)$') {
        $rid = $matches[1]
        if ($rid -notmatch $Script:RecipeIdPattern) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_recipe_id'; recipeId = $rid }
            return $true
        }
        switch ($method) {
            'GET'    { Invoke-RecipeGet    -Context $Context -RecipeId $rid; return $true }
            'PUT'    { Invoke-RecipeUpdate -Context $Context -RecipeId $rid; return $true }
            'DELETE' { Invoke-RecipeDelete -Context $Context -RecipeId $rid; return $true }
            default {
                Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
                return $true
            }
        }
    }

    return $false
}
