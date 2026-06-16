#requires -Version 7.4

# RecipeLite.ps1
#
# Routes:
#   POST   /api/v1/recipe-lite/validate    validate
#                                          accepts a Mini-Kitchen lite
#                                          recipe envelope JSON body
#                                          (<= 256 KiB), validates
#                                          structure + defense-in-
#                                          depth, and returns a
#                                          preview describing the
#                                          import outcome plus a
#                                          nameSuggestion for the
#                                          chef-provided display name.
#   POST   /api/v1/recipe-lite/import      import
#                                          accepts a wrapper
#                                          { envelope, targetRecipeName }
#                                          of an already-validated
#                                          envelope plus the chef's
#                                          explicit display name. Maps
#                                          the lite envelope to a
#                                          structural Cookbook recipe
#                                          draft, materializes a fresh
#                                          local recipe (new ULID,
#                                          source='mini-kitchen-lite',
#                                          no authProfileId, no
#                                          filename / file-path
#                                          dependency on the source
#                                          file), and persists it via
#                                          the same Write-RecipeFile +
#                                          Add-RecipeRow pattern used
#                                          by recipe create / takeout
#                                          import.
#
# Behavior contract:
#   - Read-only against the source envelope. Never mutates the file,
#     never starts a bake, never calls PAX, never writes the lite
#     JSON to disk, never logs the envelope body, never touches the
#     network.
#   - The import payload becomes a Cookbook recipe DRAFT in Needs
#     Prep state. The broker persists with status='ready' (only
#     terminal status the schema permits) but BYPASSES full
#     Test-RecipeAll validation on insert so the chef can save a
#     deliberately incomplete recipe and finish it in Prep Station.
#     The next save through PUT /api/v1/recipes/{id} re-runs full
#     validation.
#   - Auto-execution of imported lite recipes is NEVER permitted by
#     this route; the route returns the new recipe payload only.
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - Read-RecipeFile / Write-RecipeFile / Initialize-RecipesDirs /
#     Get-RecipeFilePath / Add-RecipeRow / Get-RecipeRowsActive /
#     New-RecipeId / $Script:M1_RecipeSchemaVer (Routes\Recipes.ps1)
#   - Write-JsonResponse  (Start-Broker.ps1)
#   - $Script:CookbookVersion  / $Script:PaxScriptVersion /
#     $Script:ReleaseChannel   (Start-Broker.ps1)
#
# Reused via Import-Module:
#   - Test-RecipeTakeoutForbiddenFieldName, Test-RecipeTakeoutForbiddenSecretValue
#     (Modules\RecipeTakeoutSanitizer.psm1)
#   - Test-MiniKitchenLiteEnvelope, Resolve-MiniKitchenLiteTargetName,
#     New-CookbookDraftFromMiniKitchenLiteEnvelope
#     (Modules\MiniKitchenLiteImporter.psm1)
#   - Get-PaxRecipeImportEnvelopeClass  (Modules\RecipeImportClassifier.psm1)

Import-Module -Force (Join-Path (Split-Path -Parent $PSScriptRoot) 'Modules\RecipeTakeoutSanitizer.psm1')
Import-Module -Force (Join-Path (Split-Path -Parent $PSScriptRoot) 'Modules\MiniKitchenLiteImporter.psm1')
Import-Module -Force (Join-Path (Split-Path -Parent $PSScriptRoot) 'Modules\RecipeImportClassifier.psm1')

# Body cap. Matches the takeout endpoint (256 KiB raw bytes BEFORE
# decode or JSON parse). A lite envelope is structurally smaller than
# a full Cookbook envelope; the cap is a generous ceiling for forward
# growth.
$Script:LiteRouteBodyMaxBytes = 256 * 1024

# Recipe display-name window (matches takeout import).
$Script:LiteRouteMinNameLength = 1
$Script:LiteRouteMaxNameLength = 200

# Filename-reserved characters for targetRecipeName validation.
$Script:LiteRouteInvalidNameChars = @('<','>',':','"','/','\','|','?','*')

# Auth modes that require Chef's Key binding (mirrors takeout).
$Script:LiteRouteAppRegistrationModes = @(
    'AppRegistrationSecret',
    'AppRegistrationCertificate'
)

# ---------------------------------------------------------
# Helpers
# ---------------------------------------------------------

function Read-RecipeLiteBodyBytes {
    param($Context)
    $req = $Context.Request
    if (-not $req.HasEntityBody) {
        return @{ status = 'empty'; bytes = $null }
    }
    if ($req.ContentLength64 -gt $Script:LiteRouteBodyMaxBytes) {
        return @{ status = 'too_large'; bytes = $null }
    }
    $cap    = $Script:LiteRouteBodyMaxBytes
    $stream = $req.InputStream
    $ms     = New-Object System.IO.MemoryStream
    try {
        $buf  = New-Object byte[] 8192
        $read = 0
        while ($true) {
            $n = $stream.Read($buf, 0, $buf.Length)
            if ($n -le 0) { break }
            $read += $n
            if ($read -gt $cap) {
                return @{ status = 'too_large'; bytes = $null }
            }
            $ms.Write($buf, 0, $n)
        }
    } finally {
        try { $stream.Close() } catch {}
    }
    if ($ms.Length -eq 0) {
        return @{ status = 'empty'; bytes = $null }
    }
    return @{ status = 'ok'; bytes = $ms.ToArray() }
}

function Test-RecipeLiteNameWindowsValid {
    param([string]$Name)
    if ([string]::IsNullOrEmpty($Name)) {
        return @{ ok = $false; reason = 'length' }
    }
    if ($Name.Length -lt $Script:LiteRouteMinNameLength -or
        $Name.Length -gt $Script:LiteRouteMaxNameLength) {
        return @{ ok = $false; reason = 'length' }
    }
    foreach ($c in $Name.ToCharArray()) {
        if ([int]$c -lt 0x20) {
            return @{ ok = $false; reason = 'control' }
        }
    }
    foreach ($ch in $Script:LiteRouteInvalidNameChars) {
        if ($Name.Contains($ch)) {
            return @{ ok = $false; reason = 'invalid_char' }
        }
    }
    return @{ ok = $true }
}

function Get-RecipeLiteExistingNames {
    if (-not (Get-Command -Name 'Get-RecipeRowsActive' -ErrorAction SilentlyContinue)) {
        return @()
    }
    $rows = $null
    try {
        $rows = Get-RecipeRowsActive
    } catch {
        return @()
    }
    if ($null -eq $rows) { return @() }
    $out = New-Object System.Collections.ArrayList
    foreach ($r in $rows) {
        if ($null -eq $r) { continue }
        $n = $null
        try {
            if ($r -is [System.Collections.IDictionary] -and $r.Contains('name')) {
                $n = [string]$r['name']
            } else {
                $n = [string]$r.name
            }
        } catch { continue }
        if ([string]::IsNullOrEmpty($n)) { continue }
        [void]$out.Add($n)
    }
    return @($out.ToArray())
}

function Get-RecipeLiteValidateErrorCode {
    param($Errors)
    if ($null -eq $Errors -or $Errors.Count -eq 0) { return 'lite_shape_invalid' }
    $hasKind    = $false
    $hasVersion = $false
    foreach ($e in $Errors) {
        if (-not ($e -is [System.Collections.IDictionary])) { continue }
        $epath = [string]$e['path']
        if ($epath -eq '/kind')          { $hasKind    = $true }
        if ($epath -eq '/schemaVersion') { $hasVersion = $true }
    }
    if ($hasVersion) { return 'lite_schema_version_unsupported' }
    if ($hasKind)    { return 'lite_kind_invalid' }
    return 'lite_shape_invalid'
}

function New-RecipeLiteValidatePreview {
    # Returns the preview hashtable surfaced by the validate endpoint
    # AND used internally by the import endpoint to compute the
    # needsPrep flags and warning list. Mirrors the takeout preview
    # shape so the SPA can render either kind with the same view
    # template; adds the lite-specific 'source', 'previewMetadata',
    # and 'nameSuggestion' siblings.
    param(
        $Envelope,
        $MappingResult,
        [string[]]$ExistingNames
    )
    $recipe = $MappingResult.recipe

    $recipeName = $null
    if ($recipe -is [System.Collections.IDictionary] -and
        $recipe.Contains('identity') -and ($recipe['identity'] -is [System.Collections.IDictionary]) -and
        $recipe['identity'].Contains('name')) {
        $recipeName = [string]$recipe['identity']['name']
    }

    $chefKeyRequired = [bool]$MappingResult.needsChefKey
    $chefKeyMode     = $MappingResult.chefKeyMode

    # Walk MappingResult warnings to derive needsPrep flags.
    $hasPathWarn   = $false
    $hasTenantWarn = $false
    $warningsOut   = New-Object System.Collections.ArrayList
    foreach ($w in $MappingResult.warnings) {
        if (-not ($w -is [System.Collections.IDictionary])) { continue }
        [void]$warningsOut.Add($w)
        $code = ''
        if ($w.Contains('code')) { $code = [string]$w['code'] }
        if ($code -eq 'lite_path_rejected_by_cookbook' -or
            $code -eq 'lite_destination_tier_not_supported') {
            $hasPathWarn = $true
        }
        if ($code -eq 'lite_tenant_id_not_uuid') {
            $hasTenantWarn = $true
        }
    }

    # Lite envelopes always land as Needs Prep drafts. We still
    # compute the per-bucket flags so the Prep Station can surface
    # the specific reasons.
    $needsAny = $true
    $state    = 'needs_prep'

    $reasons = New-Object System.Collections.ArrayList
    if ($chefKeyRequired) { [void]$reasons.Add('chef key binding') }
    if ($hasPathWarn)     { [void]$reasons.Add('path review') }
    if ($hasTenantWarn)   { [void]$reasons.Add('tenant review') }
    if ($reasons.Count -eq 0) {
        [void]$reasons.Add('chef review (Mini-Kitchen lite recipes always open in Prep Station)')
    }
    $message = 'Mini-Kitchen lite recipe is valid. Will open in Prep Station after import: ' + ($reasons -join ', ') + '.'

    # nameSuggestion (mirror takeout shape).
    $names         = if ($null -ne $ExistingNames) { @($ExistingNames) } else { @() }
    $hasCollision  = $false
    $sourceName    = if ([string]::IsNullOrEmpty($recipeName)) { $null } else { $recipeName.Trim() }
    if (-not [string]::IsNullOrEmpty($sourceName)) {
        foreach ($n in $names) {
            if ([string]::IsNullOrEmpty([string]$n)) { continue }
            if ([string]::Equals(([string]$n).Trim(), $sourceName, [System.StringComparison]::OrdinalIgnoreCase)) {
                $hasCollision = $true
                break
            }
        }
    }
    $suggestion = if ([string]::IsNullOrEmpty($sourceName)) { $null }
                  elseif (-not $hasCollision) { $sourceName }
                  else {
                      $resolved = Resolve-MiniKitchenLiteTargetName -ProposedName $sourceName -ExistingNames $names
                      if ($resolved.resolved) { [string]$resolved.name } else { $null }
                  }
    $nameSuggestion = [ordered]@{
        sourceName    = $sourceName
        suggestedName = $suggestion
        collision     = $hasCollision
        collisionRule = 'windows_numeric_suffix'
        maxSuffix     = 99
    }

    return [ordered]@{
        ok      = $true
        valid   = $true
        source  = 'mini-kitchen-lite'
        state   = $state
        recipe  = [ordered]@{
            name           = $recipeName
            sourceRecipeId = $null
            sourceTemplate = $null
        }
        chefKey = [ordered]@{
            required           = $chefKeyRequired
            mode               = $chefKeyMode
            sourceDisplayLabel = $null
        }
        warnings  = @($warningsOut.ToArray())
        needsPrep = [ordered]@{
            chefKey = $chefKeyRequired
            paths   = $hasPathWarn
            tenant  = $hasTenantWarn
        }
        message         = $message
        nameSuggestion  = $nameSuggestion
        previewMetadata = $MappingResult.preview
    }
}

# ---------------------------------------------------------
# Validate handler
# ---------------------------------------------------------

function Invoke-RecipeLiteValidate {
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()

    if ($method -ne 'POST') {
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return
    }

    $bodyResult = Read-RecipeLiteBodyBytes -Context $Context
    if ($bodyResult.status -eq 'too_large') {
        Write-JsonResponse -Context $Context -Status 413 -Body @{
            error      = 'payload_too_large'
            limitBytes = $Script:LiteRouteBodyMaxBytes
        }
        return
    }
    if ($bodyResult.status -eq 'empty') {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    $raw = $null
    try {
        $raw = [System.Text.Encoding]::UTF8.GetString($bodyResult.bytes)
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    $envelope = $null
    try {
        $envelope = $raw | ConvertFrom-Json -AsHashtable -Depth 12 -DateKind String
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }
    if ($null -eq $envelope -or -not ($envelope -is [System.Collections.IDictionary])) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    # Defense-in-depth: classify by envelope kind FIRST. Reject any
    # envelope that does not advertise itself as a Mini-Kitchen lite
    # envelope on this endpoint; the SPA dispatches by kind and the
    # broker enforces it.
    $envClass = Get-PaxRecipeImportEnvelopeClass -ParsedEnvelope $envelope
    if ($envClass.class -ne 'lite') {
        $code = 'lite_kind_invalid'
        if ($envClass.reason -eq 'lite_schema_version_unsupported' -or
            $envClass.reason -eq 'lite_schema_version_missing' -or
            $envClass.reason -eq 'lite_schema_version_invalid_type') {
            $code = 'lite_schema_version_unsupported'
        }
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error    = $code
            kindSeen = $envClass.kindSeen
            reason   = $envClass.reason
        }
        return
    }

    # Defense-in-depth: forbidden field-name scan + secret-value scan
    # over the entire decoded envelope BEFORE structural validation.
    # Reuses the canonical Cookbook scanners so a lite envelope is
    # subject to the same secret-leakage refusal rules as a full
    # Cookbook takeout envelope.
    try {
        $forbiddenName = Test-RecipeTakeoutForbiddenFieldName -Tree $envelope
        if ($null -ne $forbiddenName) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error     = 'lite_contains_forbidden_secret_field'
                fieldName = [string]$forbiddenName
            }
            return
        }
        $secretTag = Test-RecipeTakeoutForbiddenSecretValue -Tree $envelope
        if ($null -ne $secretTag) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error = 'lite_contains_forbidden_secret_field'
                kind  = [string]$secretTag
            }
            return
        }
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error = 'lite_contains_forbidden_secret_field'
        }
        return
    }

    # Explicit authProfileId refusal at envelope.recipe.auth (belt-
    # and-braces; the lite contract does not even allow this field
    # but if a forged envelope carries it, refuse).
    if ($envelope.Contains('recipe') -and ($envelope['recipe'] -is [System.Collections.IDictionary])) {
        $recipeNode = $envelope['recipe']
        if ($recipeNode.Contains('auth') -and ($recipeNode['auth'] -is [System.Collections.IDictionary])) {
            if ($recipeNode['auth'].Contains('authProfileId')) {
                Write-JsonResponse -Context $Context -Status 400 -Body @{
                    error     = 'lite_contains_forbidden_secret_field'
                    fieldName = 'authProfileId'
                    path      = '/recipe/auth/authProfileId'
                }
                return
            }
        }
    }

    # Structural validation per the Mini-Kitchen lite contract.
    $structural = Test-MiniKitchenLiteEnvelope -Envelope $envelope
    if (-not $structural.ok) {
        $code = Get-RecipeLiteValidateErrorCode -Errors $structural.errors
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = $code
            errors = @($structural.errors)
        }
        return
    }

    # Map to a structural Cookbook recipe draft for preview-only use.
    # We do NOT persist this draft; the import endpoint re-runs the
    # mapper with a fresh ULID so a chef cannot pin a particular ULID
    # by validating, holding the response, and importing later.
    $mapping = $null
    try {
        $mapping = New-CookbookDraftFromMiniKitchenLiteEnvelope `
            -Envelope          $envelope `
            -NowUtc            ([datetime]::UtcNow) `
            -NewRecipeId       '00000000000000000000000000' `
            -CookbookVersion   $Script:CookbookVersion `
            -BundledPaxVersion $Script:PaxScriptVersion `
            -ReleaseChannel    $Script:ReleaseChannel
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'lite_persist_failed' }
        return
    }

    $preview = New-RecipeLiteValidatePreview `
        -Envelope      $envelope `
        -MappingResult $mapping `
        -ExistingNames (Get-RecipeLiteExistingNames)
    Write-JsonResponse -Context $Context -Status 200 -Body $preview
}

# ---------------------------------------------------------
# Import handler
# ---------------------------------------------------------

function Invoke-RecipeLiteImport {
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()

    if ($method -ne 'POST') {
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return
    }

    $bodyResult = Read-RecipeLiteBodyBytes -Context $Context
    if ($bodyResult.status -eq 'too_large') {
        Write-JsonResponse -Context $Context -Status 413 -Body @{
            error      = 'payload_too_large'
            limitBytes = $Script:LiteRouteBodyMaxBytes
        }
        return
    }
    if ($bodyResult.status -eq 'empty') {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    $raw = $null
    try {
        $raw = [System.Text.Encoding]::UTF8.GetString($bodyResult.bytes)
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    $wrapper = $null
    try {
        $wrapper = $raw | ConvertFrom-Json -AsHashtable -Depth 12 -DateKind String
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }
    if ($null -eq $wrapper -or -not ($wrapper -is [System.Collections.IDictionary])) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    # Wrapper shape: exactly { envelope, targetRecipeName }.
    $allowedTop = @('envelope','targetRecipeName')
    foreach ($k in @($wrapper.Keys)) {
        if ($allowedTop -notcontains [string]$k) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error  = 'lite_unknown_field'
                errors = @(@{ path = '/' + [string]$k; message = 'unknown top-level property' })
            }
            return
        }
    }

    # targetRecipeName presence + type.
    if (-not $wrapper.Contains('targetRecipeName')) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'recipe_name_required' }
        return
    }
    $rawName = $wrapper['targetRecipeName']
    if ($null -eq $rawName -or -not ($rawName -is [string])) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'recipe_name_required' }
        return
    }
    $trimmedName = ([string]$rawName).Trim()
    if ([string]::IsNullOrEmpty($trimmedName)) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'recipe_name_required' }
        return
    }
    $nameCheck = Test-RecipeLiteNameWindowsValid -Name $trimmedName
    if (-not $nameCheck.ok) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'recipe_name_invalid'
            reason = [string]$nameCheck.reason
        }
        return
    }

    # envelope presence + IDictionary.
    if (-not $wrapper.Contains('envelope')) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'lite_shape_invalid'
            errors = @(@{ path = '/envelope'; message = 'missing required property' })
        }
        return
    }
    $envelope = $wrapper['envelope']
    if ($null -eq $envelope -or -not ($envelope -is [System.Collections.IDictionary])) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'lite_shape_invalid'
            errors = @(@{ path = '/envelope'; message = 'must be an object' })
        }
        return
    }

    # Classify by envelope kind (must be lite on this endpoint).
    $envClass = Get-PaxRecipeImportEnvelopeClass -ParsedEnvelope $envelope
    if ($envClass.class -ne 'lite') {
        $code = 'lite_kind_invalid'
        if ($envClass.reason -eq 'lite_schema_version_unsupported' -or
            $envClass.reason -eq 'lite_schema_version_missing' -or
            $envClass.reason -eq 'lite_schema_version_invalid_type') {
            $code = 'lite_schema_version_unsupported'
        }
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error    = $code
            kindSeen = $envClass.kindSeen
            reason   = $envClass.reason
        }
        return
    }

    # Defense-in-depth: forbidden field-name + secret-value scans.
    try {
        $forbiddenName = Test-RecipeTakeoutForbiddenFieldName -Tree $envelope
        if ($null -ne $forbiddenName) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error     = 'lite_contains_forbidden_secret_field'
                fieldName = [string]$forbiddenName
            }
            return
        }
        $secretTag = Test-RecipeTakeoutForbiddenSecretValue -Tree $envelope
        if ($null -ne $secretTag) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error = 'lite_contains_forbidden_secret_field'
                kind  = [string]$secretTag
            }
            return
        }
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error = 'lite_contains_forbidden_secret_field'
        }
        return
    }

    # Explicit authProfileId refusal at envelope.recipe.auth.
    if ($envelope.Contains('recipe') -and ($envelope['recipe'] -is [System.Collections.IDictionary])) {
        $recipeNode = $envelope['recipe']
        if ($recipeNode.Contains('auth') -and ($recipeNode['auth'] -is [System.Collections.IDictionary])) {
            if ($recipeNode['auth'].Contains('authProfileId')) {
                Write-JsonResponse -Context $Context -Status 400 -Body @{
                    error     = 'lite_contains_forbidden_secret_field'
                    fieldName = 'authProfileId'
                    path      = '/recipe/auth/authProfileId'
                }
                return
            }
        }
    }

    # Structural validation.
    $structural = Test-MiniKitchenLiteEnvelope -Envelope $envelope
    if (-not $structural.ok) {
        $code = Get-RecipeLiteValidateErrorCode -Errors $structural.errors
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = $code
            errors = @($structural.errors)
        }
        return
    }

    # Collision check.
    $existingNames = Get-RecipeLiteExistingNames
    $hasCollision  = $false
    foreach ($n in $existingNames) {
        if ([string]::IsNullOrEmpty([string]$n)) { continue }
        if ([string]::Equals(([string]$n).Trim(), $trimmedName, [System.StringComparison]::OrdinalIgnoreCase)) {
            $hasCollision = $true
            break
        }
    }
    if ($hasCollision) {
        $resolved       = Resolve-MiniKitchenLiteTargetName -ProposedName $trimmedName -ExistingNames $existingNames
        $nextSuggestion = if ($resolved.resolved) { [string]$resolved.name } else { $null }
        Write-JsonResponse -Context $Context -Status 409 -Body @{
            error          = 'recipe_name_conflict'
            message        = ("A recipe named '{0}' already exists in this Cookbook." -f $trimmedName)
            nextSuggestion = $nextSuggestion
        }
        return
    }

    # Materialize Cookbook recipe draft from lite envelope.
    $newId   = $null
    $mapping = $null
    try {
        $newId   = New-RecipeId
        $mapping = New-CookbookDraftFromMiniKitchenLiteEnvelope `
            -Envelope          $envelope `
            -NowUtc            ([datetime]::UtcNow) `
            -NewRecipeId       $newId `
            -CookbookVersion   $Script:CookbookVersion `
            -BundledPaxVersion $Script:PaxScriptVersion `
            -ReleaseChannel    $Script:ReleaseChannel
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'lite_persist_failed' }
        return
    }
    if ($null -eq $mapping -or -not ($mapping -is [System.Collections.IDictionary]) -or
        -not $mapping.Contains('recipe') -or -not ($mapping['recipe'] -is [System.Collections.IDictionary])) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'lite_persist_failed' }
        return
    }
    $pending = $mapping['recipe']

    # OVERRIDE identity.name with the explicit trimmed targetRecipeName.
    if (-not ($pending['identity'] -is [System.Collections.IDictionary])) {
        $pending['identity'] = @{}
    }
    $pending['identity']['name'] = $trimmedName
    # Stamp destination's authoritative schema + adapter versions.
    $pending['recipeSchemaVersion'] = $Script:M1_RecipeSchemaVer
    $pending['paxAdapterVersion']   = $Script:PaxScriptVersion
    $pending['recipeId']            = $newId

    # Persist (file-first, row-second; mirror Invoke-RecipeTakeoutImport).
    try {
        Initialize-RecipesDirs
        $hash = Write-RecipeFile -RecipeId $newId -RecipeObject $pending
        $now  = [string]$pending['updatedAt']
        try {
            Add-RecipeRow -Row @{
                recipe_id             = $newId
                name                  = $trimmedName
                pax_adapter_version   = [string]$Script:PaxScriptVersion
                recipe_schema_version = [int]$Script:M1_RecipeSchemaVer
                source                = 'mini-kitchen-lite'
                source_ref            = $null
                file_path             = (Get-RecipeFilePath -RecipeId $newId)
                file_hash             = $hash
                created_at            = $now
                updated_at            = $now
            }
        } catch {
            $fp = Get-RecipeFilePath -RecipeId $newId
            if (Test-Path -LiteralPath $fp) {
                Remove-Item -LiteralPath $fp -Force -ErrorAction SilentlyContinue
            }
            throw
        }
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'lite_persist_failed' }
        return
    }

    # Recompute the warning surface against the persisted recipe so
    # the response carries the same needsPrep flags the chef will see
    # when opening the recipe in Prep Station.
    $preview = New-RecipeLiteValidatePreview `
        -Envelope      $envelope `
        -MappingResult $mapping `
        -ExistingNames @()

    Write-JsonResponse -Context $Context -Status 201 -Body @{
        ok              = $true
        imported        = $true
        source          = 'mini-kitchen-lite'
        recipeId        = $newId
        recipeName      = $trimmedName
        needsPrep       = [ordered]@{
            chefKey = [bool]$mapping.needsChefKey
            mode    = $mapping.chefKeyMode
            paths   = [bool]$preview.needsPrep.paths
            tenant  = [bool]$preview.needsPrep.tenant
        }
        warnings        = $preview.warnings
        previewMetadata = $mapping.preview
        recipe          = $pending
    }
}

# ---------------------------------------------------------
# Dispatcher
# ---------------------------------------------------------

function Invoke-RecipeLiteRoute {
    # Returns $true if the request was consumed by this handler.
    # Routes intercepted:
    #   POST /api/v1/recipe-lite/validate
    #   POST /api/v1/recipe-lite/import
    param($Context)
    $req  = $Context.Request
    $path = $req.Url.AbsolutePath

    if ($path -eq '/api/v1/recipe-lite/validate') {
        Invoke-RecipeLiteValidate -Context $Context
        return $true
    }
    if ($path -eq '/api/v1/recipe-lite/import') {
        Invoke-RecipeLiteImport -Context $Context
        return $true
    }
    return $false
}
