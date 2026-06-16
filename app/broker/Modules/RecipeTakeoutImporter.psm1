Set-StrictMode -Version Latest

# RecipeTakeoutImporter.psm1
#
# Helper module for the import side of Recipe Takeout. Pure
# helper: no broker route registration, no SQLite access,
# no live FS writes, no patch to RecipeValidator.ps1.
#
# F2A scope is structural only. Test-RecipeTakeoutEnvelope
# performs the structural validation that the JSON Schema
# 2020-12 subset validator in RecipeValidator.ps1 would do
# against the takeout schema — but without invoking the
# production validator. F2B will wire Test-RecipeSchemaNode
# against the takeout schema at the route layer.

# ---------------------------------------------------------
# Constants
# ---------------------------------------------------------

$Script:ImporterSchemaVersion = 1
$Script:ImporterKindConstant  = 'pax-cookbook.recipe-takeout'

$Script:ImporterAllowedTopLevel = @(
    'takeoutSchemaVersion',
    'kind',
    'exportedAtUtc',
    'exportedBy',
    'recipe',
    'chefKey',
    'sourceRecipe',
    'warnings',
    'excluded',
    'extensions'
)

$Script:ImporterRequiredTopLevel = @(
    'takeoutSchemaVersion',
    'kind',
    'exportedAtUtc',
    'recipe',
    'excluded'
)

$Script:ImporterAppRegistrationModes = @(
    'AppRegistrationSecret',
    'AppRegistrationCertificate'
)

# K-5 (revised in UXR_F2D): Windows-style numeric suffix walk.
# Resolve-RecipeTakeoutTargetName walks "Name", "Name (1)",
# "Name (2)", ... up to "Name (99)". The first non-collision wins.
# After "Name (99)" also collides, the helper returns
# resolved=$false / name=$null so the caller (import handler or
# validate suggestion API) can surface the "pick a manual name"
# state to the chef.
#
# 99 is the literal max — there is NO 100th candidate. The legacy
# "(Imported)" / "(Imported 2)" / "(Imported 3)" sequence was
# removed in UXR_F2D-0; the doctrine is now Windows-style
# numeric suffix only.
$Script:ImporterMaxNumericSuffix = 99

# ---------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------

function ConvertTo-ImporterHashtable {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [hashtable]) {
        $copy = @{}
        foreach ($k in $Value.Keys) {
            $copy[[string]$k] = ConvertTo-ImporterHashtable -Value $Value[$k]
        }
        return $copy
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $copy = @{}
        foreach ($k in $Value.Keys) {
            $copy[[string]$k] = ConvertTo-ImporterHashtable -Value $Value[$k]
        }
        return $copy
    }
    if ($Value -is [System.Management.Automation.PSCustomObject] -or
        $Value -is [pscustomobject]) {
        $copy = @{}
        foreach ($p in $Value.PSObject.Properties) {
            $copy[[string]$p.Name] = ConvertTo-ImporterHashtable -Value $p.Value
        }
        return $copy
    }
    if ($Value -is [string]) { return [string]$Value }
    if ($Value -is [bool])   { return [bool]$Value }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) {
        return $Value
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $arr = New-Object System.Collections.ArrayList
        foreach ($item in $Value) {
            [void]$arr.Add( (ConvertTo-ImporterHashtable -Value $item) )
        }
        return ,@($arr.ToArray())
    }
    return [string]$Value
}

function Test-ImporterNameEqual {
    # Recipe identity.name uniqueness is case-insensitive at
    # save time. K-5 collision resolution mirrors that.
    param([string]$A, [string]$B)
    return [string]::Equals(
        ([string]$A).Trim(),
        ([string]$B).Trim(),
        [System.StringComparison]::OrdinalIgnoreCase)
}

# ---------------------------------------------------------
# Exported helpers
# ---------------------------------------------------------

function Resolve-RecipeTakeoutTargetName {
    # K-5 (revised in UXR_F2D): Windows-style numeric suffix.
    # Returns @{ name = <string>; resolved = $true } on success,
    # @{ name = $null; resolved = $false; reason = 'takeout_name_unresolvable' }
    # when every candidate through "Name (99)" already exists.
    #
    # Walk order:
    #   Name           (the trimmed proposed name, returned as-is on no
    #                   collision)
    #   Name (1)       (first collision suffix)
    #   Name (2)
    #   ...
    #   Name (99)      ($Script:ImporterMaxNumericSuffix; last candidate)
    #
    # Comparison against ExistingNames is case-insensitive and trim-aware
    # (Test-ImporterNameEqual). The legacy "(Imported)" / "(Imported N)"
    # forms are NO LONGER produced by this helper and the import handler
    # explicitly forbids them in the doctrine smokes.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProposedName,
        [string[]]$ExistingNames
    )
    if ([string]::IsNullOrWhiteSpace($ProposedName)) {
        return @{ name = $null; resolved = $false; reason = 'empty_proposed_name' }
    }
    $ProposedName = $ProposedName.Trim()
    $existing = @()
    if ($null -ne $ExistingNames) { $existing = @($ExistingNames) }

    $hasCollision = $false
    foreach ($n in $existing) {
        if (Test-ImporterNameEqual -A $n -B $ProposedName) { $hasCollision = $true; break }
    }
    if (-not $hasCollision) {
        return @{ name = $ProposedName; resolved = $true }
    }

    for ($i = 1; $i -le $Script:ImporterMaxNumericSuffix; $i++) {
        $candidate = ('{0} ({1})' -f $ProposedName, $i)
        $hasCollision = $false
        foreach ($n in $existing) {
            if (Test-ImporterNameEqual -A $n -B $candidate) { $hasCollision = $true; break }
        }
        if (-not $hasCollision) {
            return @{ name = $candidate; resolved = $true }
        }
    }

    return @{ name = $null; resolved = $false; reason = 'takeout_name_unresolvable' }
}

function Test-RecipeTakeoutEnvelope {
    # Structural check ONLY. Does NOT invoke the JSON Schema
    # validator engine. Returns @{ ok = $bool; errors = @(...) }.
    # F2B is expected to add full schema-driven validation by
    # passing the takeout schema to Test-RecipeSchemaNode (the
    # JSON Schema 2020-12 subset validator already present in
    # app\broker\Routes\RecipeValidator.ps1).
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Envelope)

    $errors = New-Object System.Collections.ArrayList

    if ($null -eq $Envelope) {
        [void]$errors.Add(@{ path = ''; message = 'envelope is null' })
        return @{ ok = $false; errors = @($errors.ToArray()) }
    }

    $env = ConvertTo-ImporterHashtable -Value $Envelope
    if (-not ($env -is [hashtable])) {
        [void]$errors.Add(@{ path = ''; message = 'envelope must be an object' })
        return @{ ok = $false; errors = @($errors.ToArray()) }
    }

    foreach ($req in $Script:ImporterRequiredTopLevel) {
        if (-not $env.ContainsKey($req)) {
            [void]$errors.Add(@{ path = "/$req"; message = "required property missing" })
        }
    }

    foreach ($k in $env.Keys) {
        if ($Script:ImporterAllowedTopLevel -notcontains $k) {
            [void]$errors.Add(@{ path = "/$k"; message = "unknown top-level property" })
        }
    }

    if ($env.ContainsKey('takeoutSchemaVersion')) {
        if ($env['takeoutSchemaVersion'] -ne $Script:ImporterSchemaVersion) {
            [void]$errors.Add(@{ path = '/takeoutSchemaVersion'; message = "takeoutSchemaVersion must equal $($Script:ImporterSchemaVersion)" })
        }
    }
    if ($env.ContainsKey('kind')) {
        if ([string]$env['kind'] -ne $Script:ImporterKindConstant) {
            [void]$errors.Add(@{ path = '/kind'; message = "kind must equal '$($Script:ImporterKindConstant)'" })
        }
    }

    if ($env.ContainsKey('exportedAtUtc')) {
        $dt = [datetime]::MinValue
        if (-not [datetime]::TryParse([string]$env['exportedAtUtc'], [ref]$dt)) {
            [void]$errors.Add(@{ path = '/exportedAtUtc'; message = 'exportedAtUtc must be a valid date-time string' })
        }
    }

    if ($env.ContainsKey('recipe')) {
        $r = $env['recipe']
        if (-not ($r -is [hashtable])) {
            [void]$errors.Add(@{ path = '/recipe'; message = 'recipe must be an object' })
        } else {
            if (-not $r.ContainsKey('identity')) {
                [void]$errors.Add(@{ path = '/recipe/identity'; message = 'recipe.identity is required' })
            } elseif (-not ($r['identity'] -is [hashtable])) {
                [void]$errors.Add(@{ path = '/recipe/identity'; message = 'recipe.identity must be an object' })
            } elseif (-not $r['identity'].ContainsKey('name') -or
                      [string]::IsNullOrWhiteSpace([string]$r['identity']['name'])) {
                [void]$errors.Add(@{ path = '/recipe/identity/name'; message = 'recipe.identity.name is required' })
            }
        }
    }

    if ($env.ContainsKey('chefKey')) {
        $ck = $env['chefKey']
        if (-not ($ck -is [hashtable])) {
            [void]$errors.Add(@{ path = '/chefKey'; message = 'chefKey must be an object when present' })
        } else {
            if (-not $ck.ContainsKey('requirement')) {
                [void]$errors.Add(@{ path = '/chefKey/requirement'; message = 'chefKey.requirement is required when chefKey is present' })
            } elseif (@('required','none') -notcontains [string]$ck['requirement']) {
                [void]$errors.Add(@{ path = '/chefKey/requirement'; message = "chefKey.requirement must be 'required' or 'none'" })
            }
        }
    }

    if ($env.ContainsKey('excluded')) {
        $ex = $env['excluded']
        if (-not ($ex -is [System.Collections.IEnumerable]) -or ($ex -is [string])) {
            [void]$errors.Add(@{ path = '/excluded'; message = 'excluded must be an array' })
        } else {
            $count = 0
            foreach ($x in $ex) { $count++ }
            if ($count -lt 1) {
                [void]$errors.Add(@{ path = '/excluded'; message = 'excluded must contain at least one entry' })
            }
        }
    }

    if ($env.ContainsKey('extensions')) {
        [void]$errors.Add(@{ path = '/extensions'; message = "v1 broker refuses 'extensions' until takeoutSchemaVersion advances" })
    }

    return @{ ok = ($errors.Count -eq 0); errors = @($errors.ToArray()) }
}

function Get-RecipeTakeoutImportWarnings {
    # Combines warnings carried in the envelope with import-time
    # warnings (template not present in destination, destination
    # template older than source). Does NOT touch the DB; the
    # caller supplies the destination template inventory.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Envelope,
        [hashtable]$DestinationTemplates
    )

    $warnings = New-Object System.Collections.ArrayList

    $env = ConvertTo-ImporterHashtable -Value $Envelope
    if (-not ($env -is [hashtable])) {
        return ,@()
    }

    if ($env.ContainsKey('warnings')) {
        $ws = $env['warnings']
        if ($ws -is [System.Collections.IEnumerable] -and -not ($ws -is [string])) {
            foreach ($w in $ws) {
                if ($w -is [hashtable]) {
                    $wpath = $null
                    if ($w.ContainsKey('path')) { $wpath = [string]$w['path'] }
                    [void]$warnings.Add( @{ code = [string]$w['code']; path = $wpath; origin = 'export' } )
                }
            }
        }
    }

    $srcTemplateId = $null
    $srcTemplateVersion = $null
    if ($env.ContainsKey('sourceRecipe') -and ($env['sourceRecipe'] -is [hashtable]) -and $env['sourceRecipe'].ContainsKey('sourceTemplate')) {
        $st = $env['sourceRecipe']['sourceTemplate']
        if ($st -is [hashtable]) {
            if ($st.ContainsKey('templateId'))      { $srcTemplateId      = [string]$st['templateId'] }
            if ($st.ContainsKey('templateVersion')) { $srcTemplateVersion = [string]$st['templateVersion'] }
        }
    }

    if ($null -ne $srcTemplateId) {
        if ($null -eq $DestinationTemplates -or -not $DestinationTemplates.ContainsKey($srcTemplateId)) {
            [void]$warnings.Add(@{ code = 'template_not_present_in_destination'; path = '/sourceRecipe/sourceTemplate'; origin = 'import' })
        } else {
            $destVersion = [string]$DestinationTemplates[$srcTemplateId]
            if ((-not [string]::IsNullOrWhiteSpace($srcTemplateVersion)) -and
                (-not [string]::IsNullOrWhiteSpace($destVersion))) {
                $srcParsed  = $null
                $destParsed = $null
                if ([System.Version]::TryParse($srcTemplateVersion,  [ref]$srcParsed) -and
                    [System.Version]::TryParse($destVersion, [ref]$destParsed)) {
                    if ($destParsed.CompareTo($srcParsed) -lt 0) {
                        [void]$warnings.Add(@{ code = 'template_destination_older_than_source'; path = '/sourceRecipe/sourceTemplate'; origin = 'import' })
                    }
                }
            }
        }
    }

    return ,@($warnings.ToArray())
}

function New-RecipeFromTakeoutEnvelope {
    # Returns a "pending recipe" hashtable derived from the
    # envelope payload. Does NOT write to disk, does NOT call
    # the production validator, does NOT touch SQLite.
    #
    # The caller is responsible for:
    #   * Assigning a fresh ULID via -NewRecipeId (or letting
    #     this function leave recipeId absent so the caller's
    #     save path generates one).
    #   * Calling the production validator at save time.
    #   * Re-binding auth.authProfileId to a local Chef's Key.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Envelope,
        [datetime]$NowUtc       = [datetime]::UtcNow,
        $NewRecipeId            = $null,
        $CookbookVersion        = $null,
        $BundledPaxVersion      = $null,
        $ReleaseChannel         = $null,
        [string[]]$ExistingNames = @()
    )

    $env = ConvertTo-ImporterHashtable -Value $Envelope
    if (-not ($env -is [hashtable]) -or -not $env.ContainsKey('recipe')) {
        throw "New-RecipeFromTakeoutEnvelope: envelope must contain a 'recipe' object"
    }
    $payload = $env['recipe']
    if (-not ($payload -is [hashtable])) {
        throw "New-RecipeFromTakeoutEnvelope: envelope.recipe must be an object"
    }

    # Defensive copy of the payload.
    $pending = ConvertTo-ImporterHashtable -Value $payload

    # 1. Strip any residual authProfileId. The source export
    #    pipeline already does this; defense in depth.
    if ($pending.ContainsKey('auth') -and ($pending['auth'] -is [hashtable])) {
        if ($pending['auth'].ContainsKey('authProfileId')) {
            [void]$pending['auth'].Remove('authProfileId')
        }
    }

    # 2. Resolve identity.name against the destination workspace.
    if ($pending.ContainsKey('identity') -and ($pending['identity'] -is [hashtable]) -and
        $pending['identity'].ContainsKey('name')) {
        $proposed = [string]$pending['identity']['name']
        $resolved = Resolve-RecipeTakeoutTargetName -ProposedName $proposed -ExistingNames $ExistingNames
        if (-not $resolved.resolved) {
            throw "New-RecipeFromTakeoutEnvelope: takeout_name_unresolvable (100 collisions on '$proposed')"
        }
        $pending['identity']['name'] = $resolved.name
    }

    # 3. Restamp timestamps.
    $iso = $NowUtc.ToUniversalTime().ToString('o')
    $pending['createdAt'] = $iso
    $pending['updatedAt'] = $iso

    # 4. Restamp createdBy.{cookbookVersion,bundledPaxVersion,releaseChannel}
    #    while preserving createdBy.fromTemplate verbatim.
    $existingFromTemplate = $null
    if ($pending.ContainsKey('createdBy') -and ($pending['createdBy'] -is [hashtable]) -and
        $pending['createdBy'].ContainsKey('fromTemplate')) {
        $existingFromTemplate = $pending['createdBy']['fromTemplate']
    }
    $newCreatedBy = @{}
    if (-not [string]::IsNullOrWhiteSpace([string]$CookbookVersion))   { $newCreatedBy['cookbookVersion']   = [string]$CookbookVersion }
    if (-not [string]::IsNullOrWhiteSpace([string]$BundledPaxVersion)) { $newCreatedBy['bundledPaxVersion'] = [string]$BundledPaxVersion }
    if (-not [string]::IsNullOrWhiteSpace([string]$ReleaseChannel))    { $newCreatedBy['releaseChannel']    = [string]$ReleaseChannel }
    if ($null -ne $existingFromTemplate)                               { $newCreatedBy['fromTemplate']      = $existingFromTemplate }
    $pending['createdBy'] = $newCreatedBy

    # 5. Optionally set the fresh recipeId.
    if (-not [string]::IsNullOrWhiteSpace([string]$NewRecipeId)) {
        $pending['recipeId'] = [string]$NewRecipeId
    }

    # 6. Determine whether the pending recipe is "Needs Prep"
    #    because the chef must select a local Chef's Key binding.
    $needsChefKey = $false
    $chefKeyMode  = $null
    if ($env.ContainsKey('chefKey') -and ($env['chefKey'] -is [hashtable])) {
        $ck = $env['chefKey']
        if ($ck.ContainsKey('requirement') -and [string]$ck['requirement'] -eq 'required') {
            $needsChefKey = $true
            if ($ck.ContainsKey('mode')) { $chefKeyMode = [string]$ck['mode'] }
        }
    }
    if (-not $needsChefKey -and $pending.ContainsKey('auth') -and ($pending['auth'] -is [hashtable]) -and
        $pending['auth'].ContainsKey('mode')) {
        $mode = [string]$pending['auth']['mode']
        if ($Script:ImporterAppRegistrationModes -contains $mode) {
            $needsChefKey = $true
            $chefKeyMode  = $mode
        }
    }

    $importedFromId = $null
    if ($env.ContainsKey('sourceRecipe') -and ($env['sourceRecipe'] -is [hashtable]) -and $env['sourceRecipe'].ContainsKey('id')) {
        $importedFromId = [string]$env['sourceRecipe']['id']
    }
    return @{
        recipe          = $pending
        needsChefKey    = $needsChefKey
        chefKeyMode     = $chefKeyMode
        importedFromId  = $importedFromId
    }
}

Export-ModuleMember -Function @(
    'Resolve-RecipeTakeoutTargetName',
    'New-RecipeFromTakeoutEnvelope',
    'Test-RecipeTakeoutEnvelope',
    'Get-RecipeTakeoutImportWarnings'
)
