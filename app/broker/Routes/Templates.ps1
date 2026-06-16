#requires -Version 7.4

# Templates.ps1 — HTTP routes for the bundled Pantry template surface.
#
#   GET  /api/v1/templates                            -> 200 { templates: [ <summary> ... ] }
#   GET  /api/v1/templates/<id>                       -> 200 { template: <full body> }
#   POST /api/v1/templates/<id>/materialize           -> 201 { recipeId, recipe }
#
# Scope guardrails — intentional non-features:
#   - No template upload, no template install, no template download,
#     no template registry, no template store, no template gallery,
#     no template marketplace, no template ratings/comments/sharing,
#     no template auto-update, no remote template catalog, no community
#     surface, no cloud sync, no online dependencies.
#   - No template execution. A template is metadata; it is materialized
#     into a recipe and persisted; the recipe (not the template) drives
#     the cook through the existing Get-PaxInvocationPlan projection.
#   - No dynamic templates. Templates are static JSON files bundled
#     under app/templates/ and loaded ONCE at broker startup by
#     Read-TemplateCatalog. There is no per-request rescan.
#   - No hidden runtime mutation. Materialize produces a recipe document
#     that the recipe schema validator and the projection adapter both
#     accept verbatim. Nothing in this file rewrites recipes after
#     creation, mutates the template catalog, or stores per-template
#     execution state.
#
# Dot-sourced from Start-Broker.ps1 AFTER RecipeValidator, Recipes,
# Runtime, AND TemplateValidator. Depends on these in-scope helpers:
#   - $Script:TemplateCatalog            (hashtable id -> template object)
#   - $Script:PaxScriptVersion           (string; bundled PAX version)
#   - $Script:CookbookVersion / ReleaseChannel
#   - $Script:M1_RecipeSchemaVer         (integer; from Recipes.ps1)
#   - Get-RecipeCreatedByBlock           (from Recipes.ps1)
#   - New-RecipeId                       (from Recipes.ps1)
#   - Get-RecipeFilePath                 (from Recipes.ps1)
#   - Write-RecipeFile                   (from Recipes.ps1)
#   - Add-RecipeRow                      (from Recipes.ps1)
#   - Initialize-RecipesDirs             (from Recipes.ps1)
#   - Test-RecipeAll                     (from RecipeValidator.ps1)
#   - Test-TemplatePaxCompatibility      (from TemplateValidator.ps1)
#   - Test-RecipeSchemaNode              (from RecipeValidator.ps1; reused for materialize body schema)
#   - Write-JsonResponse / Read-RequestJson / Get-UtcNowIso (broker)

# ---------------------------------------------------------------------
# Materialize-body schema (bounded; mirrors recipe leaves 1:1)
# ---------------------------------------------------------------------
#
# The operator supplies exactly the per-instance leaves the template
# declares as required inputs. No template-default leaves are accepted
# in the body (those come from the template), and no advanced /
# server-managed leaves are accepted (those are not template territory).
# additionalProperties:false at every level forbids hidden fields.

$Script:TemplateMaterializeBodySchema = @{
    type                 = 'object'
    additionalProperties = $false
    required             = @('identity','auth','query','destinations')
    properties = @{
        identity = @{
            type = 'object'; additionalProperties = $false; required = @('name')
            properties = @{ name = @{ type = 'string'; minLength = 1; maxLength = 200 } }
        }
        auth = @{
            type = 'object'; additionalProperties = $false; required = @('tenantId')
            properties = @{
                tenantId = @{ type = 'string'; pattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' }
            }
        }
        query = @{
            type = 'object'; additionalProperties = $false; required = @('startDate','endDate')
            properties = @{
                startDate = @{ type = 'string'; format = 'date' }
                endDate   = @{ type = 'string'; format = 'date' }
            }
        }
        destinations = @{
            type = 'object'; additionalProperties = $false; required = @('fact')
            properties = @{
                fact = @{
                    type = 'object'; additionalProperties = $false; required = @('path')
                    properties = @{ path = @{ type = 'string'; minLength = 1 } }
                }
            }
        }
    }
}

# ---------------------------------------------------------------------
# Template -> recipe materialization (pure builder; no I/O)
# ---------------------------------------------------------------------

function ConvertTo-MaterializedRecipe {
    # Pure builder. Given a validated template and a validated body,
    # return a fully populated recipe hashtable. Server-managed fields
    # (recipeId, recipeSchemaVersion, paxAdapterVersion, createdAt,
    # updatedAt, createdBy) are stamped from authoritative startup
    # state. All other leaves come from template.recipeDefaults +
    # body. The result is intended for Test-RecipeAll → Write-RecipeFile
    # → Add-RecipeRow.
    param(
        [Parameter(Mandatory)]$Template,
        [Parameter(Mandatory)]$Body,
        [Parameter(Mandatory)][string]$NowUtcIso,
        [Parameter(Mandatory)][string]$RecipeId
    )

    # Defaults from the template. Always read defensively so a template
    # with a sparse recipeDefaults block still produces a structurally
    # valid recipe (the recipe schema validator catches any missing
    # required leaves downstream).
    $defaultsRoot      = if ($Template.ContainsKey('recipeDefaults')) { $Template.recipeDefaults } else { @{} }
    $defaultsIng       = if ($defaultsRoot.ContainsKey('ingredients')) { $defaultsRoot.ingredients } else { @{} }
    $defaultsIngM365   = if ($defaultsIng.ContainsKey('m365Usage'))     { $defaultsIng.m365Usage }   else { @{} }
    $defaultsIngEntra  = if ($defaultsIng.ContainsKey('entraUserData')) { $defaultsIng.entraUserData } else { @{} }
    $defaultsProc      = if ($defaultsRoot.ContainsKey('processing'))   { $defaultsRoot.processing } else { @{} }
    $defaultsAuth      = if ($defaultsRoot.ContainsKey('auth'))         { $defaultsRoot.auth }       else { @{} }

    # Build createdBy with the per-template fromTemplate sub-block. This
    # is the inspectable provenance the slice contract calls out: the
    # recipe carries, in its own file, exactly which template and which
    # template version produced it.
    #
    # Note: the recipe is built as plain Hashtable (not [ordered]) for
    # symmetry with the manual-create path, which reads bodies via
    # ConvertFrom-Json -AsHashtable. Some validator helpers
    # (Test-RecipeOutputPathTier) call $Recipe.ContainsKey(...), which is
    # a Hashtable method that OrderedDictionary does not expose. Keeping
    # the same runtime type across both create paths means downstream
    # code does not have to discriminate.
    $createdBy = Get-RecipeCreatedByBlock
    $createdBy.fromTemplate = @{
        templateId      = [string]$Template.templateId
        templateVersion = [string]$Template.templateVersion
    }

    return @{
        recipeId            = $RecipeId
        recipeSchemaVersion = $Script:M1_RecipeSchemaVer
        paxAdapterVersion   = [string]$Script:PaxScriptVersion
        createdAt           = $NowUtcIso
        updatedAt           = $NowUtcIso
        createdBy           = $createdBy
        identity = @{
            name = [string]$Body.identity.name
        }
        ingredients = @{
            m365Usage = @{
                includeM365Usage = [bool]$defaultsIngM365.includeM365Usage
            }
            entraUserData = @{
                includeUserInfo = [bool]$defaultsIngEntra.includeUserInfo
            }
        }
        query = @{
            startDate = [string]$Body.query.startDate
            endDate   = [string]$Body.query.endDate
        }
        processing = @{
            rollup = [string]$defaultsProc.rollup
        }
        destinations = @{
            fact = @{
                path = [string]$Body.destinations.fact.path
            }
        }
        auth = @{
            mode     = [string]$defaultsAuth.mode
            tenantId = [string]$Body.auth.tenantId
        }
    }
}

# ---------------------------------------------------------------------
# Public projections used by the list/get endpoints
# ---------------------------------------------------------------------

function ConvertTo-TemplateSummary {
    # The list endpoint returns lightweight cards: id, displayName,
    # shortDescription, category, version metadata, and a count of
    # manual-guidance notes (so the operator can see at a glance that a
    # template carries auxiliary operator-facing guidance). Per-instance
    # input requirements are NOT projected into the summary — the
    # detail endpoint owns that. The summary is intentionally narrow so
    # the Pantry list view stays fast and inspectable.
    param([Parameter(Mandatory)]$Template)
    $guidanceCount = 0
    if ($Template.ContainsKey('manualGuidance') -and $null -ne $Template.manualGuidance) {
        $guidanceCount = @($Template.manualGuidance).Count
    }
    return [ordered]@{
        templateId            = [string]$Template.templateId
        templateVersion       = [string]$Template.templateVersion
        templateSchemaVersion = [int]$Template.templateSchemaVersion
        displayName           = [string]$Template.displayName
        shortDescription      = [string]$Template.shortDescription
        category              = [string]$Template.category
        minPaxScriptVersion   = [string]$Template.minPaxScriptVersion
        minCookbookVersion    = [string]$Template.minCookbookVersion
        manualGuidanceCount   = $guidanceCount
    }
}

# ---------------------------------------------------------------------
# Route handlers
# ---------------------------------------------------------------------

function Invoke-TemplatesList {
    param($Context)
    $summaries = New-Object System.Collections.Generic.List[object]
    foreach ($id in ($Script:TemplateCatalog.Keys | Sort-Object)) {
        $tpl = $Script:TemplateCatalog[$id]
        [void]$summaries.Add( (ConvertTo-TemplateSummary -Template $tpl) )
    }
    Write-JsonResponse -Context $Context -Status 200 -Body @{ templates = $summaries.ToArray() }
}

function Invoke-TemplateGet {
    param($Context, [string]$TemplateId)
    if (-not $Script:TemplateCatalog.ContainsKey($TemplateId)) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'template_not_found'; templateId = $TemplateId }
        return
    }
    Write-JsonResponse -Context $Context -Status 200 -Body @{ template = $Script:TemplateCatalog[$TemplateId] }
}

function Invoke-TemplateMaterialize {
    param($Context, [string]$TemplateId)

    if (-not $Script:TemplateCatalog.ContainsKey($TemplateId)) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'template_not_found'; templateId = $TemplateId }
        return
    }
    $template = $Script:TemplateCatalog[$TemplateId]

    # PAX-version gate. If the bundled PAX is older than the template
    # was authored for, refuse rather than emit an under-spec recipe.
    $paxErr = Test-TemplatePaxCompatibility -Template $template -BundledPaxVersion $Script:PaxScriptVersion
    if ($null -ne $paxErr) {
        Write-JsonResponse -Context $Context -Status 412 -Body @{
            error               = 'template_incompatible'
            templateId          = $TemplateId
            bundledPaxVersion   = [string]$Script:PaxScriptVersion
            minPaxScriptVersion = [string]$template.minPaxScriptVersion
            details             = @($paxErr)
        }
        return
    }

    $body = Read-RequestJson -Context $Context
    if ($null -eq $body) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }

    # Materialize-body schema check (per-instance leaves only).
    $bodyErrors = New-Object System.Collections.Generic.List[object]
    Test-RecipeSchemaNode -Node $body -NodeSchema $Script:TemplateMaterializeBodySchema -InstancePath '' -Errors $bodyErrors
    if ($bodyErrors.Count -gt 0) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'materialize_body_invalid'
            errors = @($bodyErrors.ToArray())
        }
        return
    }

    # Build and validate the merged recipe.
    $now      = Get-UtcNowIso
    $id       = New-RecipeId
    $recipe   = ConvertTo-MaterializedRecipe -Template $template -Body $body -NowUtcIso $now -RecipeId $id
    $verdict  = Test-RecipeAll -Recipe $recipe
    if (-not $verdict.ok) {
        # The materialize path generated an invalid recipe even after
        # body-schema passed — surface the validator's verdict verbatim
        # so the operator can see exactly which leaf is malformed.
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error      = 'materialize_recipe_invalid'
            templateId = $TemplateId
            recipeId   = $id
            errors     = $verdict.errors
        }
        return
    }

    Initialize-RecipesDirs

    # File-first, row-second; same invariant as the manual-create path.
    $hash = Write-RecipeFile -RecipeId $id -RecipeObject $recipe
    try {
        Add-RecipeRow -Row @{
            recipe_id             = $id
            name                  = [string]$recipe.identity.name
            pax_adapter_version   = [string]$Script:PaxScriptVersion
            recipe_schema_version = $Script:M1_RecipeSchemaVer
            source                = 'template'
            source_ref            = ([string]$template.templateId + '@' + [string]$template.templateVersion)
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

    Write-JsonResponse -Context $Context -Status 201 -Body @{ recipeId = $id; recipe = $recipe }
}

# ---------------------------------------------------------------------
# Dispatch
# ---------------------------------------------------------------------

function Invoke-TemplatesRoute {
    # Returns $true if the request was consumed by this handler.
    #
    # The id regex matches are case-SENSITIVE (-cmatch) because the
    # template id pattern is explicitly lower-case only; a request with
    # an upper-case path segment must fall through to the catch-all
    # 400 invalid_template_id branch rather than be accepted by the
    # default case-insensitive -match operator.
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -eq '/api/v1/templates') {
        if ($method -eq 'GET') { Invoke-TemplatesList -Context $Context; return $true }
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return $true
    }

    # /api/v1/templates/<id>/materialize
    if ($path -cmatch ('^/api/v1/templates/(' + $Script:TemplateIdPattern.TrimStart('^').TrimEnd('$') + ')/materialize$')) {
        $id = $matches[1]
        if ($method -eq 'POST') { Invoke-TemplateMaterialize -Context $Context -TemplateId $id; return $true }
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return $true
    }

    # /api/v1/templates/<id>
    if ($path -cmatch ('^/api/v1/templates/(' + $Script:TemplateIdPattern.TrimStart('^').TrimEnd('$') + ')$')) {
        $id = $matches[1]
        if ($method -eq 'GET') { Invoke-TemplateGet -Context $Context -TemplateId $id; return $true }
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return $true
    }

    # /api/v1/templates/<malformed-id>(/materialize)?  — refuse cleanly
    # so probing for invalid ids reports 400 not 404.
    if ($path -match '^/api/v1/templates/[^/]+(/materialize)?$') {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_template_id' }
        return $true
    }

    return $false
}
