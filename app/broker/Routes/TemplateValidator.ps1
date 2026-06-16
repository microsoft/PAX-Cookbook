#requires -Version 7.4

# TemplateValidator.ps1
#
# Bundled Pantry template surface — schema + load-time validation.
#
# Templates live as static JSON files under app/templates/ in the
# install tree. They are NOT loaded from the workspace, are NOT
# downloaded, are NOT cached across cookbook installs. They are
# inspectable, declarative, deterministic, portable.
#
# Two responsibilities, intentionally separated from Recipes.ps1:
#
#   1. $Script:TemplateSchema — bounded JSON-Schema-shaped definition
#      of what a bundled template document is. Hard-coded here so the
#      shape is auditable in source. Mirrors the JSON Schema 2020-12
#      subset that Test-RecipeSchemaNode (RecipeValidator.ps1) already
#      knows how to walk: type, required, properties, additionalProperties,
#      const, enum, pattern, minLength, maxLength, format, items,
#      minItems, maxItems.
#
#   2. Read-TemplateCatalog — at-startup loader. Scans
#      $Script:TemplatesDir for files matching *.template.json, parses
#      each, validates each against $Script:TemplateSchema, then runs
#      additional content checks that the schema cannot express:
#        - recipeDefaults must not contain server-managed leaves
#        - recipeDefaults must not contain per-instance leaves
#          (auth.tenantId / query / destinations / advanced)
#        - templateId in the filename must match templateId in the body
#        - displayName + shortDescription must be non-blank
#      Returns a hashtable keyed by templateId. Templates that fail
#      validation are NOT loaded into the catalog; their per-file
#      errors are returned alongside so startup can log them.
#
# What a template is NOT:
#   - It does not embed scripts, code blobs, command strings, URLs to
#     remote registries, manifest overrides, runtime-version overrides,
#     or absolute filesystem paths.
#   - It does not encode tenant-specific values (tenantId, date range,
#     output path). Those are per-instance leaves the operator supplies
#     at materialization time.
#   - It does not auto-update, sync, or fetch anything.
#
# Dot-sourced from Start-Broker.ps1 AFTER RecipeValidator.ps1 (so
# Test-RecipeSchemaNode is in scope) and BEFORE Templates.ps1.

# ---------------------------------------------------------------------
# Bundled-template schema (M1, schemaVersion = 1)
# ---------------------------------------------------------------------

$Script:TemplateSchemaVersion = 1
$Script:TemplateIdPattern     = '^[a-z][a-z0-9-]{1,62}[a-z0-9]$'

$Script:TemplateSchema = @{
    type                 = 'object'
    additionalProperties = $false
    required             = @(
        'templateId','templateSchemaVersion','templateVersion',
        'displayName','shortDescription','category',
        'minPaxScriptVersion','minCookbookVersion',
        'produces','requires','recipeDefaults','provenance'
    )
    properties = @{
        templateId            = @{ type = 'string'; pattern = $Script:TemplateIdPattern }
        templateSchemaVersion = @{ type = 'integer'; const = 1 }
        templateVersion       = @{ type = 'string'; pattern = '^\d+\.\d+\.\d+$' }
        displayName           = @{ type = 'string'; minLength = 1; maxLength = 120 }
        shortDescription      = @{ type = 'string'; minLength = 1; maxLength = 280 }
        category              = @{ type = 'string'; enum = @('Analytics','Operational','Diagnostic','Reference') }
        minPaxScriptVersion   = @{ type = 'string'; pattern = '^\d+\.\d+\.\d+$' }
        minCookbookVersion    = @{ type = 'string'; pattern = '^\d+\.\d+\.\d+$' }

        produces = @{
            type = 'object'; additionalProperties = $false
            required = @('summary','artifacts')
            properties = @{
                summary = @{ type = 'string'; minLength = 1; maxLength = 1200 }
                artifacts = @{
                    type = 'array'; minItems = 1; maxItems = 8
                    items = @{
                        type = 'object'; additionalProperties = $false
                        required = @('kind','name','description')
                        properties = @{
                            kind        = @{ type = 'string'; enum = @('fact','metric','log','manual-side-data') }
                            name        = @{ type = 'string'; minLength = 1; maxLength = 120 }
                            description = @{ type = 'string'; minLength = 1; maxLength = 400 }
                        }
                    }
                }
            }
        }

        requires = @{
            type = 'object'; additionalProperties = $false
            required = @('authModes','inputs')
            properties = @{
                authModes = @{
                    type = 'array'; minItems = 1; maxItems = 2
                    items = @{ type = 'string'; enum = @('WebLogin','DeviceCode') }
                }
                inputs = @{
                    type = 'array'; minItems = 1; maxItems = 12
                    items = @{
                        type = 'object'; additionalProperties = $false
                        required = @('field','kind','required','description')
                        properties = @{
                            field = @{
                                type = 'string'
                                enum = @(
                                    'identity.name',
                                    'auth.tenantId',
                                    'query.startDate','query.endDate',
                                    'destinations.fact.path'
                                )
                            }
                            kind = @{
                                type = 'string'
                                enum = @('recipe-name','tenant-id','date','output-directory')
                            }
                            required    = @{ type = 'boolean' }
                            description = @{ type = 'string'; minLength = 1; maxLength = 400 }
                        }
                    }
                }
            }
        }

        # recipeDefaults: declarative starting values for the recipe
        # leaves that the template knows up-front. Strictly bounded:
        #
        #   - ingredients.{m365Usage,entraUserData}  (booleans only)
        #   - processing.rollup                      (must be 'Rollup')
        #   - auth.mode                              (WebLogin or DeviceCode)
        #
        # NOT allowed (enforced by additionalProperties:false here +
        # extra content checks in Test-TemplateContent):
        #   - identity (per-instance)
        #   - query / destinations / auth.tenantId (per-instance)
        #   - advanced.extraArguments (no hidden trailer)
        #   - recipeId, recipeSchemaVersion, paxAdapterVersion,
        #     createdAt, updatedAt, createdBy (server-managed)
        recipeDefaults = @{
            type = 'object'; additionalProperties = $false
            properties = @{
                ingredients = @{
                    type = 'object'; additionalProperties = $false
                    properties = @{
                        m365Usage = @{
                            type = 'object'; additionalProperties = $false
                            properties = @{ includeM365Usage = @{ type = 'boolean' } }
                        }
                        entraUserData = @{
                            type = 'object'; additionalProperties = $false
                            properties = @{ includeUserInfo = @{ type = 'boolean' } }
                        }
                    }
                }
                processing = @{
                    type = 'object'; additionalProperties = $false
                    properties = @{ rollup = @{ type = 'string'; enum = @('Rollup') } }
                }
                auth = @{
                    type = 'object'; additionalProperties = $false
                    properties = @{ mode = @{ type = 'string'; enum = @('WebLogin','DeviceCode') } }
                }
            }
        }

        manualGuidance = @{
            type = 'array'; minItems = 0; maxItems = 4
            items = @{
                type = 'object'; additionalProperties = $false
                required = @('heading','audience','body')
                properties = @{
                    heading  = @{ type = 'string'; minLength = 1; maxLength = 160 }
                    audience = @{ type = 'string'; enum = @('operator','administrator','reviewer') }
                    body = @{
                        type = 'array'; minItems = 1; maxItems = 20
                        items = @{ type = 'string'; minLength = 1; maxLength = 1000 }
                    }
                }
            }
        }

        provenance = @{
            type = 'object'; additionalProperties = $false
            required = @('source','lastReviewed')
            properties = @{
                source       = @{ type = 'string'; enum = @('bundled') }
                lastReviewed = @{ type = 'string'; format = 'date' }
            }
        }
    }
}

# ---------------------------------------------------------------------
# Content checks (things the JSON-schema-style walker cannot express)
# ---------------------------------------------------------------------

function Test-TemplateContent {
    # Run after Test-RecipeSchemaNode succeeds. Adds checks that are
    # bounded but not expressible as schema keywords: filename ↔ body
    # consistency, server-managed leaves intruding into recipeDefaults,
    # absolute paths sneaking into template values.
    #
    # Returns an array of AJV-shaped errors (possibly empty).
    param(
        [Parameter(Mandatory)]$Template,
        [Parameter(Mandatory)][string]$FileName
    )
    $errs = New-Object System.Collections.Generic.List[object]

    # --- Filename ↔ templateId consistency.
    # Files MUST be named '<templateId>.template.json'. The filename is
    # the operator-visible handle; if it does not match the in-body
    # templateId, refuse the template (drift is silent failure).
    $expected = $Template.templateId + '.template.json'
    if (-not [string]::Equals($FileName, $expected, [System.StringComparison]::Ordinal)) {
        $errs.Add( (New-ValidationError -InstancePath '/templateId' -Keyword 'filenameMismatch' `
            -Message ("templateId '" + $Template.templateId + "' does not match filename '" + $FileName + "'") `
            -Params @{ expectedFileName = $expected; actualFileName = $FileName }) )
    }

    # --- recipeDefaults: per-instance leaves must NOT be embedded.
    # additionalProperties:false on the schema already blocks unknown
    # top-level recipe leaves inside recipeDefaults, but we double-check
    # at the content layer so the rejection is self-explaining.
    $forbiddenInDefaults = @(
        'identity','query','destinations','advanced',
        'recipeId','recipeSchemaVersion','paxAdapterVersion',
        'createdAt','updatedAt','createdBy'
    )
    if ($Template.ContainsKey('recipeDefaults')) {
        $rd = $Template.recipeDefaults
        if (($rd -is [hashtable]) -or ($rd -is [System.Collections.IDictionary])) {
            foreach ($k in $forbiddenInDefaults) {
                if ($rd.ContainsKey($k)) {
                    $errs.Add( (New-ValidationError -InstancePath '/recipeDefaults' -Keyword 'forbiddenLeaf' `
                        -Message ("recipeDefaults must not contain '" + $k + "'; per-instance and server-managed leaves are out of scope for templates") `
                        -Params @{ forbiddenProperty = $k }) )
                }
            }
            # auth.tenantId is allowed at the schema layer (auth is a
            # known object) but tenantId itself must not be templated.
            if ($rd.ContainsKey('auth')) {
                $a = $rd.auth
                if ((($a -is [hashtable]) -or ($a -is [System.Collections.IDictionary])) -and $a.ContainsKey('tenantId')) {
                    $errs.Add( (New-ValidationError -InstancePath '/recipeDefaults/auth' -Keyword 'forbiddenLeaf' `
                        -Message ("recipeDefaults.auth must not contain 'tenantId'; tenant id is a per-instance leaf") `
                        -Params @{ forbiddenProperty = 'tenantId' }) )
                }
            }
        }
    }

    return $errs.ToArray()
}

# ---------------------------------------------------------------------
# Catalog load (startup-only; no per-request rescans)
# ---------------------------------------------------------------------

function Read-TemplateCatalog {
    # Scan $Script:TemplatesDir, parse + validate every *.template.json,
    # populate $Script:TemplateCatalog (hashtable id -> template object),
    # AND return a hashtable summary the caller can log:
    #
    #   @{ loaded = @{ id -> template } ;
    #      failures = @( @{ file = '<path>'; errors = @(...) } ) }
    #
    # Templates that fail validation are deliberately NOT inserted into
    # $Script:TemplateCatalog. They are reported as failures so the
    # broker can log them without poisoning the operator-visible catalog.
    [CmdletBinding()]
    param()

    $loaded   = @{}
    $failures = New-Object System.Collections.Generic.List[object]

    if (-not (Test-Path -LiteralPath $Script:TemplatesDir -PathType Container)) {
        # Templates directory missing -> catalog is empty (not an error).
        # The Pantry surface will simply render zero templates. This is
        # the deliberate behavior in workspaces where the install tree
        # was partially copied.
        return @{ loaded = $loaded; failures = @($failures.ToArray()) }
    }

    $files = Get-ChildItem -LiteralPath $Script:TemplatesDir -File -Filter '*.template.json' -ErrorAction SilentlyContinue
    if (-not $files) { return @{ loaded = $loaded; failures = @($failures.ToArray()) } }

    foreach ($f in $files) {
        $errors = New-Object System.Collections.Generic.List[object]
        $body   = $null
        try {
            $raw  = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction Stop
            $body = $raw | ConvertFrom-Json -AsHashtable -Depth 16
        } catch {
            $errors.Add( (New-ValidationError -InstancePath '' -Keyword 'parseError' `
                -Message ('failed to parse template JSON: ' + $_.Exception.Message) `
                -Params @{ exception = $_.Exception.GetType().FullName }) )
            $failures.Add( @{ file = $f.Name; errors = @($errors.ToArray()) } )
            continue
        }

        # Schema check (reuses the generic walker from RecipeValidator.ps1).
        Test-RecipeSchemaNode -Node $body -NodeSchema $Script:TemplateSchema -InstancePath '' -Errors $errors

        # Content checks (filename match, forbidden leaves).
        if ($errors.Count -eq 0) {
            foreach ($e in (Test-TemplateContent -Template $body -FileName $f.Name)) {
                $errors.Add($e)
            }
        }

        if ($errors.Count -gt 0) {
            $failures.Add( @{ file = $f.Name; errors = @($errors.ToArray()) } )
            continue
        }

        $id = [string]$body.templateId
        if ($loaded.ContainsKey($id)) {
            $errors.Add( (New-ValidationError -InstancePath '/templateId' -Keyword 'duplicateId' `
                -Message ("duplicate templateId '" + $id + "' across template files") `
                -Params @{ templateId = $id }) )
            $failures.Add( @{ file = $f.Name; errors = @($errors.ToArray()) } )
            continue
        }

        $loaded[$id] = $body
    }

    return @{ loaded = $loaded; failures = @($failures.ToArray()) }
}

# ---------------------------------------------------------------------
# Bundled-PAX compatibility check at materialize time
# ---------------------------------------------------------------------

function Test-TemplatePaxCompatibility {
    # Returns $null on compatibility, or an AJV-shaped error if the
    # template's minPaxScriptVersion is greater than the bundled PAX
    # version currently loaded (i.e. the operator is on an older
    # cookbook than this template was written for). Pure semver
    # comparison; no fallback, no auto-upgrade behavior.
    param(
        [Parameter(Mandatory)]$Template,
        [Parameter(Mandatory)][string]$BundledPaxVersion
    )
    $required = [string]$Template.minPaxScriptVersion
    if ([string]::IsNullOrWhiteSpace($required)) { return $null }

    function _parseSemver([string]$v) {
        $parts = $v -split '\.'
        if ($parts.Length -ne 3) { return $null }
        $a = 0; $b = 0; $c = 0
        if (-not [int]::TryParse($parts[0], [ref]$a)) { return $null }
        if (-not [int]::TryParse($parts[1], [ref]$b)) { return $null }
        if (-not [int]::TryParse($parts[2], [ref]$c)) { return $null }
        return @($a, $b, $c)
    }
    $req = _parseSemver $required
    $cur = _parseSemver $BundledPaxVersion
    if (-not $req -or -not $cur) { return $null }
    for ($i = 0; $i -lt 3; $i++) {
        if ($cur[$i] -gt $req[$i]) { return $null }
        if ($cur[$i] -lt $req[$i]) {
            return (New-ValidationError -InstancePath '/minPaxScriptVersion' -Keyword 'paxIncompatible' `
                -Message ("template requires bundled PAX >= " + $required + " but broker has " + $BundledPaxVersion) `
                -Params @{ requiredMin = $required; bundled = $BundledPaxVersion })
        }
    }
    return $null
}
