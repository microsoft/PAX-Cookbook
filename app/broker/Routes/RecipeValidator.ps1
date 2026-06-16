#requires -Version 7.4

# RecipeValidator.ps1
#
# Two responsibilities, intentionally separated:
#
#   1. Test-RecipeSchema  — JSON Schema 2020-12 subset validator for the
#      M1 recipe shape defined in app/web/assets/schemas/recipe.schema.json.
#      Supports exactly the keywords used by the M1 schema: required, type,
#      const, enum, pattern, additionalProperties, minLength, maxLength,
#      format (date, date-time). Errors are AJV-shaped:
#         { instancePath, keyword, message, params }
#      The schema is hard-coded here (not loaded from disk) because:
#        - The schema file under app/web/assets/schemas/ is served to the
#          browser for client-side AJV. The server's source of truth must
#          not drift if that file is moved/renamed.
#        - The corpus rule "filesystem authoritative for recipes; SQLite is
#          metadata-only" deliberately doesn't extend to the schema itself.
#
#   2. Test-RecipeOutputPathTier — explicit M1 policy gate that rejects
#      OneLake / Fabric destinations. Kept OUT of the JSON Schema so the
#      schema file is standards-compliant and the M1 tier ban is a
#      single, inspectable code block.
#
# This file is dot-sourced from Start-Broker.ps1 so $Script:* variables
# resolve to the broker session. It does NOT register routes. Route
# wiring lives in Recipes.ps1.

# ---------------------------------------------------------------------
# M1 recipe schema (hybrid — corpus 04 nesting, M1 leaves only)
# ---------------------------------------------------------------------

$Script:RecipeSchema = @{
    type                 = 'object'
    additionalProperties = $false
    required             = @('recipeId','recipeSchemaVersion','paxAdapterVersion','identity','ingredients','query','processing','destinations','auth')
    properties           = @{
        recipeId            = @{ type = 'string'; pattern = '^[0-9A-HJKMNP-TV-Z]{26}$' }
        recipeSchemaVersion = @{ type = 'integer'; const = 1 }
        paxAdapterVersion   = @{ type = 'string'; pattern = '^\d+\.\d+\.\d+$' }
        # Phase AF: executionMode declares the operational environment
        # this recipe is intended for. The broker enforces an
        # execution-mode -> auth-mode matrix (Test-RecipeExecutionModeAuthMatrix)
        # so an authored recipe cannot specify an auth mode that the
        # target environment will reject at run time. Cookbook v1 only
        # SPAWNS local-manual / local-scheduled recipes on this
        # appliance; fabric-hosted / azure-hosted recipes are authored
        # here but consumed elsewhere -- the cook spawn path refuses to
        # run them locally and surfaces the policy at that moment.
        #
        # OPTIONAL in v1 (intentionally NOT in $required): pre-AF
        # recipes on disk do not carry this field, and Cookbook treats
        # absence as local-manual (the v1 default) when evaluating the
        # matrix. New recipes authored via the SPA stamp the field
        # explicitly; older recipes continue to validate and load.
        executionMode       = @{ type = 'string'; enum = @('local-manual','local-scheduled','fabric-hosted','azure-hosted') }
        createdAt           = @{ type = 'string'; format = 'date-time' }
        updatedAt           = @{ type = 'string'; format = 'date-time' }
        # createdBy: optional provenance block. Stamped by the broker at
        # recipe creation from authoritative startup state
        # ($Script:CookbookVersion, $Script:PaxScriptVersion,
        # $Script:ReleaseChannel — all sourced from VERSION.json). It is
        # never overwritten on update (provenance records who CREATED the
        # recipe, not who last edited it) and is never inferred onto
        # older recipes that lack it. Marked optional so older recipes
        # without provenance continue to validate (load + cook).
        #
        # createdBy.fromTemplate (optional, inside createdBy): present iff
        # the recipe was materialized from a bundled Pantry template. The
        # broker stamps it once at create time from the template's
        # templateId + templateVersion. Recipes created directly (not via
        # a template) omit this field. Like createdBy itself, it is
        # immutable across updates; it is provenance, not preference.
        createdBy = @{
            type = 'object'; additionalProperties = $false
            required = @('cookbookVersion','bundledPaxVersion','releaseChannel')
            properties = @{
                cookbookVersion   = @{ type = 'string'; minLength = 1 }
                bundledPaxVersion = @{ type = 'string'; pattern = '^\d+\.\d+\.\d+$' }
                releaseChannel    = @{ type = 'string'; minLength = 1 }
                fromTemplate = @{
                    type = 'object'; additionalProperties = $false
                    required = @('templateId','templateVersion')
                    properties = @{
                        templateId      = @{ type = 'string'; pattern = '^[a-z][a-z0-9-]{1,62}[a-z0-9]$' }
                        templateVersion = @{ type = 'string'; pattern = '^\d+\.\d+\.\d+$' }
                    }
                }
            }
        }
        identity = @{
            type = 'object'; additionalProperties = $false; required = @('name')
            properties = @{
                name = @{ type = 'string'; minLength = 1; maxLength = 200 }
            }
        }
        ingredients = @{
            type = 'object'; additionalProperties = $false; required = @('m365Usage','entraUserData')
            properties = @{
                m365Usage = @{
                    type = 'object'; additionalProperties = $false; required = @('includeM365Usage')
                    # V1.S26: includeCopilotInteraction is OPTIONAL (defaults to
                    # true at the adapter when absent). When explicitly false,
                    # ingredients.m365Usage.includeM365Usage MUST be true
                    # (procedurally enforced by Test-RecipeM365UsageGate). The
                    # adapter projects -ExcludeCopilotInteraction iff
                    # includeCopilotInteraction === $false.
                    properties = @{
                        includeM365Usage          = @{ type = 'boolean' }
                        includeCopilotInteraction = @{ type = 'boolean' }
                    }
                }
                entraUserData = @{
                    type = 'object'; additionalProperties = $false; required = @('includeUserInfo')
                    properties = @{ includeUserInfo = @{ type = 'boolean' } }
                }
            }
        }
        query = @{
            type = 'object'; additionalProperties = $false
            # V1.S26: query.mode names the supported run shape -- 'audit'
            # (Shape 1/2; the default when absent) or 'userInfoOnly'
            # (Shape 3 -- a separate top-level shape, NOT a checkbox).
            # Required/forbidden fields are SHAPE-CONDITIONAL and live
            # in Test-RecipeQueryShape, not in field-level 'required':
            #   - Audit shape (mode='audit' or absent) REQUIRES
            #     query.startDate, query.endDate, processing.rollup,
            #     destinations.fact.
            #   - Shape 3 (mode='userInfoOnly') FORBIDS query.startDate,
            #     query.endDate, processing.rollup, destinations.fact,
            #     ingredients.m365Usage.includeM365Usage=true,
            #     ingredients.m365Usage.includeCopilotInteraction, and
            #     the audit-only filter fields (activityTypes, userIds,
            #     groupNames, agentFilter, promptFilter); REQUIRES
            #     destinations.userInfo and ingredients.entraUserData.
            #     includeUserInfo=true.
            # activityTypes is the supported -ActivityTypes projection
            # surface; under rollup it must equal exactly
            # ['CopilotInteraction'] (the only rollup-compatible
            # activity, enforced by Test-RecipeActivityTypesUnderRollup).
            # userIds is the supported -UserIds projection surface
            # (non-empty when present). groupNames is the supported
            # -GroupNames projection surface (non-empty when present;
            # adds GroupMember.Read.All to the required permissions
            # list). agentFilter exposes the trio -AgentId / -AgentsOnly
            # / -ExcludeAgents as a single mutually-exclusive object via
            # a single mode enum: mode='agentIds' projects -AgentId
            # <values> (agentIds required); mode='agentsOnly' projects
            # -AgentsOnly (agentIds forbidden); mode='excludeAgents'
            # projects -ExcludeAgents (agentIds forbidden); mode='none'
            # projects nothing (agentIds forbidden). promptFilter is the
            # supported -PromptFilter projection surface; enum mirrors
            # the PAX ValidateSet ('Prompt','Response','Both','Null').
            # RecordTypes, ServiceTypes, and UseEOM are NOT in the
            # supported surface and are rejected by
            # Test-RecipeExtraArgumentsUnsupportedSwitches if found in
            # the verbatim trailer.
            properties = @{
                startDate     = @{ type = 'string'; format = 'date' }
                endDate       = @{ type = 'string'; format = 'date' }
                mode          = @{ type = 'string'; enum = @('audit','userInfoOnly') }
                activityTypes = @{ type = 'array'; minItems = 1; items = @{ type = 'string'; minLength = 1 } }
                userIds       = @{ type = 'array'; minItems = 1; items = @{ type = 'string'; minLength = 1 } }
                groupNames    = @{ type = 'array'; minItems = 1; items = @{ type = 'string'; minLength = 1 } }
                agentFilter   = @{
                    type = 'object'; additionalProperties = $false; required = @('mode')
                    properties = @{
                        mode     = @{ type = 'string'; enum = @('none','agentIds','agentsOnly','excludeAgents') }
                        agentIds = @{ type = 'array'; minItems = 1; items = @{ type = 'string'; minLength = 1 } }
                    }
                }
                promptFilter  = @{ type = 'string'; enum = @('Prompt','Response','Both','Null') }
            }
        }
        processing = @{
            type = 'object'; additionalProperties = $false
            # V1.S26 (amended): rollup is OPTIONAL for audit-shape recipes
            # (query.mode='audit' or absent) -- an operator may run PAX to
            # pull raw audit data with no rollup post-processing, so a
            # missing processing.rollup is allowed. rollup MUST BE ABSENT
            # for userInfoOnly-shape recipes (query.mode='userInfoOnly').
            # The forbidden-under-userInfoOnly rule is enforced by
            # Test-RecipeQueryShape; when rollup IS present its value
            # ('Rollup' | 'RollupPlusRaw') drives the rollup-only blocker
            # gates. There is no longer a required-under-audit rule.
            properties = @{
                rollup = @{ type = 'string'; enum = @('Rollup','RollupPlusRaw') }
            }
        }
        destinations = @{
            type = 'object'; additionalProperties = $false
            properties = @{
                fact = @{
                    type = 'object'; additionalProperties = $false
                    # V1.S26: destinations.fact.mode (outputPath | append)
                    # is the new authoritative S26 surface and replaces the
                    # M2.2 appendBehavior surface for newly-authored
                    # recipes. appendBehavior (fresh | append) is preserved
                    # as a legacy alias for back-compat (fresh ->
                    # outputPath, append -> append) so existing recipes on
                    # disk continue to validate without rewrite. When mode
                    # is present it is authoritative; appendBehavior, if
                    # also present, MUST agree with mode -- enforced by
                    # Test-RecipeFactOutputMode. The adapter NEVER emits
                    # both -OutputPath and -AppendFile simultaneously:
                    # effective mode='outputPath' projects -OutputPath
                    # <path> only; effective mode='append' projects
                    # -AppendFile <appendFile> only. Legacy recipes that
                    # carry both path and appendFile under
                    # appendBehavior='append' continue to load and project
                    # as append-only (path is treated as legacy inert
                    # debris). path is OPTIONAL at the schema level (M2.2
                    # required it; S26 relaxes so append-mode recipes can
                    # omit it); Test-RecipeFactOutputMode enforces "at
                    # least one of {path, appendFile} present" and the
                    # mode-driven required/forbidden rules.
                    properties = @{
                        path           = @{ type = 'string'; minLength = 1 }
                        mode           = @{ type = 'string'; enum = @('outputPath','append') }
                        appendBehavior = @{ type = 'string'; enum = @('fresh','append') }
                        appendFile     = @{ type = 'string'; minLength = 1 }
                    }
                }
                userInfo = @{
                    type = 'object'; additionalProperties = $false; required = @('mode')
                    # V1.S26: destinations.userInfo is the optional user-
                    # info output channel. mode='outputPath' projects
                    # -OutputPathUserInfo <path>; mode='append' projects
                    # -AppendUserInfo <appendFile>; the two are mutually
                    # exclusive (the adapter never emits both). userInfo
                    # is REQUIRED when query.mode='userInfoOnly' (Shape
                    # 3); OPTIONAL when query.mode='audit' (or absent)
                    # AND ingredients.entraUserData.includeUserInfo=true
                    # (the user-info channel is co-located with fact by
                    # default when userInfo is absent); FORBIDDEN when
                    # ingredients.entraUserData.includeUserInfo=false
                    # and query.mode!='userInfoOnly' (no user-info data
                    # would be produced). Cross-shape gating is enforced
                    # by Test-RecipeUserInfoChannelGate and
                    # Test-RecipeQueryShape; mode<->path/appendFile mutex
                    # is enforced by Test-RecipeUserInfoOutputMode.
                    properties = @{
                        mode       = @{ type = 'string'; enum = @('outputPath','append') }
                        path       = @{ type = 'string'; minLength = 1 }
                        appendFile = @{ type = 'string'; minLength = 1 }
                    }
                }
            }
        }
        auth = @{
            type = 'object'; additionalProperties = $false; required = @('mode')
            properties = @{
                # Phase AF: enum spans the five PAX-supported modes Cookbook
                # is willing to surface. Credential (stored-password
                # interactive) and Silent (opaque cached-token) remain
                # absent by design -- Cookbook never collects, hashes,
                # compares, or proxies a user's password, and the
                # appliance cannot truthfully claim provenance for an
                # opaque MSAL cache it did not create.
                #
                # The mode value alone does NOT determine validity for a
                # given recipe: the executionMode field constrains which
                # modes are doctrinally valid in the target environment.
                # That matrix is enforced by
                # Test-RecipeExecutionModeAuthMatrix.
                #
                # Binding rules (Test-RecipeAuthProfileBinding):
                #   - AppRegistrationSecret / AppRegistrationCertificate:
                #     authProfileId REQUIRED (Cookbook never accepts
                #     inline tenant/client/secret material on a recipe).
                #   - WebLogin / DeviceCode / ManagedIdentity:
                #     authProfileId FORBIDDEN (interactive modes are
                #     identity-by-prompt; ManagedIdentity is
                #     identity-by-environment; neither binds to a
                #     Cookbook-stored profile asset).
                mode          = @{ type = 'string'; enum = @('WebLogin','DeviceCode','AppRegistrationSecret','AppRegistrationCertificate','ManagedIdentity') }
                # tenantId is OPTIONAL at the schema level and is recipe
                # content only for the app-registration modes
                # (AppRegistrationSecret / AppRegistrationCertificate),
                # where a tenant must be declared up front. WebLogin /
                # DeviceCode / ManagedIdentity resolve the tenant at
                # runtime/readiness (identity-by-prompt for the
                # interactive modes, identity-by-environment for
                # ManagedIdentity), so they MUST NOT be forced to carry a
                # tenantId on the recipe. Only 'mode' is structurally
                # required (see required=@('mode') above). The pattern
                # below still validates the GUID shape whenever tenantId
                # is present; any app-registration tenant requirement is
                # procedural, not a blanket schema requirement.
                tenantId      = @{ type = 'string'; pattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' }
                authProfileId = @{ type = 'string'; pattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' }
            }
        }
        advanced = @{
            type = 'object'; additionalProperties = $false
            properties = @{ extraArguments = @{ type = 'string' } }
        }
        # importMetadata: OPTIONAL provenance bag stamped once at
        # recipe creation when the recipe was materialized from an
        # external import envelope (currently only Mini-Kitchen lite).
        # Always omitted for recipes authored directly in Cookbook.
        # Informational only -- does NOT affect cook behaviour,
        # scheduling, auth binding, destination resolution, or any
        # security gate. Preserved verbatim across PUT updates by
        # Invoke-RecipeUpdate (the SPA editor does not need to echo
        # it back, same pattern as createdBy). Every nested field is
        # OPTIONAL except 'source' so future import envelopes can be
        # added without breaking existing on-disk recipes. Tight
        # additionalProperties=false at every level so the bag can
        # never become a smuggling target for fields the schema does
        # not understand.
        importMetadata = @{
            type = 'object'; additionalProperties = $false
            required = @('source')
            properties = @{
                source = @{ type = 'string'; enum = @('mini-kitchen-lite') }
                importedAtUtc = @{ type = 'string'; format = 'date-time' }
                originalKind = @{ type = 'string'; enum = @('pax-cookbook-mini-recipe') }
                originalSchemaVersion = @{ type = 'string'; minLength = 1; maxLength = 32 }
                originalIdentity = @{
                    type = 'object'; additionalProperties = $false
                    properties = @{
                        description = @{ type = 'string'; maxLength = 4000 }
                        tags = @{ type = 'array'; maxItems = 64; items = @{ type = 'string'; minLength = 1; maxLength = 64 } }
                    }
                }
                originalCreatedBy = @{
                    type = 'object'; additionalProperties = $false
                    properties = @{
                        tool = @{ type = 'string'; minLength = 1; maxLength = 128 }
                        site = @{ type = 'string'; minLength = 1; maxLength = 512 }
                    }
                }
                compatibility = @{
                    type = 'object'; additionalProperties = $false
                    properties = @{
                        cookbookRecipeSchemaVersion = @{ type = 'integer' }
                    }
                }
                commandPreview = @{ type = 'string'; maxLength = 8000 }
                permissions = @{
                    type = 'array'; maxItems = 64
                    items = @{ type = 'string'; minLength = 1; maxLength = 256 }
                }
                importBehavior = @{
                    type = 'object'; additionalProperties = $false
                    properties = @{
                        state = @{ type = 'string'; enum = @('needsPrep') }
                        openInPrepStation = @{ type = 'boolean' }
                    }
                }
                mappingWarnings = @{
                    type = 'array'; maxItems = 128
                    items = @{
                        type = 'object'; additionalProperties = $false
                        required = @('code')
                        properties = @{
                            code   = @{ type = 'string'; minLength = 1; maxLength = 64 }
                            path   = @{ type = 'string'; maxLength = 256 }
                            detail = @{ type = 'string'; maxLength = 1024 }
                        }
                    }
                }
            }
        }
    }
}

# Output-path tier rejection rules (M1 forbids OneLake / Fabric).
# Each entry: pattern (regex, IgnoreCase) + machine keyword for the error.
$Script:OutputPathRejectRules = @(
    @{ pattern = '^abfss://';            keyword = 'onelake-abfss-uri' }
    @{ pattern = '^onelake://';          keyword = 'onelake-uri' }
    @{ pattern = '\.onelake\.';          keyword = 'onelake-host' }
    @{ pattern = 'fabric\.microsoft\.com'; keyword = 'fabric-host' }
)

# ---------------------------------------------------------------------
# Schema validator
# ---------------------------------------------------------------------

function New-ValidationError {
    param(
        [string]$InstancePath,
        [string]$Keyword,
        [string]$Message,
        [hashtable]$Params = @{}
    )
    [pscustomobject]@{
        instancePath = $InstancePath
        keyword      = $Keyword
        message      = $Message
        params       = $Params
    }
}

function Test-RecipeSchemaNode {
    # Recursive worker. $Node is the JSON value (any type). $NodeSchema is
    # the schema fragment that applies to it. $InstancePath is the JSON
    # pointer to $Node from the document root (empty string for root).
    param(
        $Node,
        [hashtable]$NodeSchema,
        [string]$InstancePath,
        [System.Collections.Generic.List[object]]$Errors
    )

    # ---- type ----
    if ($NodeSchema.ContainsKey('type')) {
        $expectedType = $NodeSchema.type
        $actualOk = $false
        switch ($expectedType) {
            'object'  { $actualOk = ($Node -is [hashtable]) -or ($Node -is [System.Collections.IDictionary]) }
            'string'  { $actualOk = ($Node -is [string]) }
            'integer' { $actualOk = (($Node -is [int]) -or ($Node -is [long]) -or ($Node -is [int64])) -and (-not ($Node -is [bool])) }
            'number'  { $actualOk = (($Node -is [int]) -or ($Node -is [long]) -or ($Node -is [double]) -or ($Node -is [decimal])) -and (-not ($Node -is [bool])) }
            'boolean' { $actualOk = ($Node -is [bool]) }
            'array'   { $actualOk = ($Node -is [array]) -or ($Node -is [System.Collections.IList]) }
            'null'    { $actualOk = ($null -eq $Node) }
        }
        if (-not $actualOk) {
            $Errors.Add( (New-ValidationError -InstancePath $InstancePath -Keyword 'type' -Message ('must be ' + $expectedType) -Params @{ type = $expectedType }) )
            # Further checks are meaningless once the type is wrong.
            return
        }
    }

    # ---- const ----
    if ($NodeSchema.ContainsKey('const')) {
        if ($Node -ne $NodeSchema.const) {
            $Errors.Add( (New-ValidationError -InstancePath $InstancePath -Keyword 'const' -Message ('must be equal to constant') -Params @{ allowedValue = $NodeSchema.const }) )
        }
    }

    # ---- enum ----
    if ($NodeSchema.ContainsKey('enum')) {
        $allowed = @($NodeSchema.enum)
        if (-not ($allowed -contains $Node)) {
            $Errors.Add( (New-ValidationError -InstancePath $InstancePath -Keyword 'enum' -Message 'must be equal to one of the allowed values' -Params @{ allowedValues = $allowed }) )
        }
    }

    # ---- string-specific ----
    if ($Node -is [string]) {
        if ($NodeSchema.ContainsKey('minLength') -and $Node.Length -lt [int]$NodeSchema.minLength) {
            $Errors.Add( (New-ValidationError -InstancePath $InstancePath -Keyword 'minLength' -Message ('must NOT have fewer than ' + $NodeSchema.minLength + ' characters') -Params @{ limit = $NodeSchema.minLength }) )
        }
        if ($NodeSchema.ContainsKey('maxLength') -and $Node.Length -gt [int]$NodeSchema.maxLength) {
            $Errors.Add( (New-ValidationError -InstancePath $InstancePath -Keyword 'maxLength' -Message ('must NOT have more than ' + $NodeSchema.maxLength + ' characters') -Params @{ limit = $NodeSchema.maxLength }) )
        }
        if ($NodeSchema.ContainsKey('pattern')) {
            $pat = [string]$NodeSchema.pattern
            if ($Node -notmatch $pat) {
                $Errors.Add( (New-ValidationError -InstancePath $InstancePath -Keyword 'pattern' -Message ('must match pattern "' + $pat + '"') -Params @{ pattern = $pat }) )
            }
        }
        if ($NodeSchema.ContainsKey('format')) {
            $fmt = [string]$NodeSchema.format
            $fmtOk = $true
            switch ($fmt) {
                'date' {
                    # ISO-8601 calendar date (YYYY-MM-DD). Reject everything else.
                    if ($Node -notmatch '^\d{4}-\d{2}-\d{2}$') { $fmtOk = $false }
                    else {
                        $parsed = [datetime]::MinValue
                        if (-not [datetime]::TryParseExact($Node, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$parsed)) {
                            $fmtOk = $false
                        }
                    }
                }
                'date-time' {
                    # RFC 3339 / ISO-8601 instant (any 'o'-roundtrip variant).
                    $parsed = [datetimeoffset]::MinValue
                    if (-not [datetimeoffset]::TryParse($Node, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsed)) {
                        $fmtOk = $false
                    }
                }
            }
            if (-not $fmtOk) {
                $Errors.Add( (New-ValidationError -InstancePath $InstancePath -Keyword 'format' -Message ('must match format "' + $fmt + '"') -Params @{ format = $fmt }) )
            }
        }
    }

    # ---- object-specific ----
    if (($Node -is [hashtable]) -or ($Node -is [System.Collections.IDictionary])) {
        $nodeKeys = New-Object System.Collections.Generic.List[string]
        foreach ($k in $Node.Keys) { [void]$nodeKeys.Add([string]$k) }

        if ($NodeSchema.ContainsKey('required')) {
            foreach ($missing in $NodeSchema.required) {
                if (-not $nodeKeys.Contains([string]$missing)) {
                    $Errors.Add( (New-ValidationError -InstancePath $InstancePath -Keyword 'required' -Message ("must have required property '" + $missing + "'") -Params @{ missingProperty = $missing }) )
                }
            }
        }

        $childSchemas = @{}
        if ($NodeSchema.ContainsKey('properties')) { $childSchemas = $NodeSchema.properties }

        if ($NodeSchema.ContainsKey('additionalProperties') -and ($NodeSchema.additionalProperties -eq $false)) {
            foreach ($k in $nodeKeys) {
                if (-not $childSchemas.ContainsKey($k)) {
                    $Errors.Add( (New-ValidationError -InstancePath $InstancePath -Keyword 'additionalProperties' -Message ("must NOT have additional property '" + $k + "'") -Params @{ additionalProperty = $k }) )
                }
            }
        }

        foreach ($k in $nodeKeys) {
            if ($childSchemas.ContainsKey($k)) {
                $childSchema = $childSchemas[$k]
                $childPath   = $InstancePath + '/' + $k
                Test-RecipeSchemaNode -Node $Node[$k] -NodeSchema $childSchema -InstancePath $childPath -Errors $Errors
            }
        }
    }

    # ---- array-specific ----
    # The walker is generic so it can validate the bundled Pantry
    # template schema (Routes/TemplateValidator.ps1) too. The M1 recipe
    # schema itself contains no arrays, so these keywords are simply
    # unused on the recipe path. Bounded subset: items, minItems,
    # maxItems. No prefixItems, no tuple validation, no uniqueItems.
    if (($Node -is [array]) -or ($Node -is [System.Collections.IList])) {
        $count = 0
        if ($Node -is [array]) { $count = $Node.Length } else { $count = $Node.Count }

        if ($NodeSchema.ContainsKey('minItems')) {
            $min = [int]$NodeSchema.minItems
            if ($count -lt $min) {
                $Errors.Add( (New-ValidationError -InstancePath $InstancePath -Keyword 'minItems' -Message ('must NOT have fewer than ' + $min + ' items') -Params @{ limit = $min }) )
            }
        }
        if ($NodeSchema.ContainsKey('maxItems')) {
            $max = [int]$NodeSchema.maxItems
            if ($count -gt $max) {
                $Errors.Add( (New-ValidationError -InstancePath $InstancePath -Keyword 'maxItems' -Message ('must NOT have more than ' + $max + ' items') -Params @{ limit = $max }) )
            }
        }
        if ($NodeSchema.ContainsKey('items')) {
            $itemSchema = $NodeSchema.items
            for ($i = 0; $i -lt $count; $i++) {
                $childPath = $InstancePath + '/' + $i
                Test-RecipeSchemaNode -Node $Node[$i] -NodeSchema $itemSchema -InstancePath $childPath -Errors $Errors
            }
        }
    }
}

function Test-RecipeSchema {
    # Validates a recipe JSON document (parsed as a hashtable tree by
    # ConvertFrom-Json -AsHashtable) against the M1 schema. Returns:
    #   @{ ok = $true;  errors = @() }  on success
    #   @{ ok = $false; errors = @(...) } on failure (errors are AJV-shaped)
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    Test-RecipeSchemaNode -Node $Recipe -NodeSchema $Script:RecipeSchema -InstancePath '' -Errors $errors
    return @{ ok = ($errors.Count -eq 0); errors = @($errors.ToArray()) }
}

# ---------------------------------------------------------------------
# Output-path tier policy (M1 forbids OneLake / Fabric)
# ---------------------------------------------------------------------

function Test-RecipeOutputPathTier {
    # Inspects $Recipe.destinations.fact.path and rejects any value that
    # looks like a OneLake / Fabric destination. Returns a list (possibly
    # empty) of AJV-shaped errors.
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('destinations')) { return @($errors.ToArray()) }
    $dest = $Recipe.destinations
    if (-not (($dest -is [hashtable]) -or ($dest -is [System.Collections.IDictionary]))) { return @($errors.ToArray()) }
    if (-not $dest.ContainsKey('fact')) { return @($errors.ToArray()) }
    $fact = $dest.fact
    if (-not (($fact -is [hashtable]) -or ($fact -is [System.Collections.IDictionary]))) { return @($errors.ToArray()) }
    if (-not $fact.ContainsKey('path')) { return @($errors.ToArray()) }
    $path = [string]$fact.path
    if ([string]::IsNullOrEmpty($path)) { return @($errors.ToArray()) }

    foreach ($rule in $Script:OutputPathRejectRules) {
        if ($path -imatch $rule.pattern) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/destinations/fact/path' `
                -Keyword 'm1OutputTier' `
                -Message ('OneLake / Fabric destinations are not supported in M1') `
                -Params @{ rejectedBy = $rule.keyword; pattern = $rule.pattern }) )
            break
        }
    }
    return @($errors.ToArray())
}

# ---------------------------------------------------------------------
# Date-range gate
# ---------------------------------------------------------------------
#
# Phase J adds a deterministic structural rule: query.startDate must be
# on or before query.endDate. This is not a JSON Schema 2020-12 keyword
# (there is no cross-field constraint without custom keywords / vocabs),
# so it lives here as a separate inspectable code block, returns
# AJV-shaped errors anchored on /query/endDate (so the editor's
# PATH_TO_FIELD lookup places the error next to the end-date input),
# and is purely a rejection — no auto-swap, no auto-correction. Only
# meaningful when both dates parsed cleanly through the schema; if
# either is missing or malformed, schema errors will already be present
# and this gate skips silently.
function Test-RecipeQueryDateRange {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('query')) { return @($errors.ToArray()) }
    $query = $Recipe.query
    if (-not (($query -is [hashtable]) -or ($query -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $query.ContainsKey('startDate')) { return @($errors.ToArray()) }
    if (-not $query.ContainsKey('endDate'))   { return @($errors.ToArray()) }
    $s = [string]$query.startDate
    $e = [string]$query.endDate
    # Cheap shape gate (schema already enforces format=date; defend
    # against unparseable input that would crash ParseExact).
    if ($s -notmatch '^\d{4}-\d{2}-\d{2}$') { return @($errors.ToArray()) }
    if ($e -notmatch '^\d{4}-\d{2}-\d{2}$') { return @($errors.ToArray()) }
    try {
        $ds = [datetime]::ParseExact($s, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
        $de = [datetime]::ParseExact($e, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
    } catch {
        return @($errors.ToArray())
    }
    if ($de -lt $ds) {
        $errors.Add( (New-ValidationError `
            -InstancePath '/query/endDate' `
            -Keyword 'dateRange' `
            -Message 'End date must be on or after start date.' `
            -Params @{ startDate = $s; endDate = $e }) )
    }
    return @($errors.ToArray())
}

# ---------------------------------------------------------------------
# Removed-switch trailer gate
# ---------------------------------------------------------------------
#
# Phase J: surface the projection-side removed-switch rejection
# (Test-ExtraArgumentsForRemovedSwitches in Pax\Adapter.psm1) at SAVE
# time, not only at preview/cook time. The contract is identical: the
# verbatim advanced.extraArguments trailer is the operator's escape
# hatch, but the v1.11.2 removed switches must not re-enter via that
# trailer. Pre-existing recipes already on disk are NOT re-validated
# on load; the gate only fires when the recipe is being saved (POST
# /api/v1/recipes or PUT /api/v1/recipes/<id>) — that matches the
# slice rule "Removed switches cannot re-enter through editing."
#
# Implementation: call the adapter helper (Import-Module'd by the
# broker before this script is dot-sourced, so the function is
# resolvable) inside try/catch, translate the throw into an
# AJV-shaped error anchored on /advanced/extraArguments. No throw
# escapes; the route layer renders a 412 validation_failed exactly
# as it does for other validation errors.
function Test-RecipeExtraArgumentsRemovedSwitches {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('advanced')) { return @($errors.ToArray()) }
    $adv = $Recipe.advanced
    if (-not (($adv -is [hashtable]) -or ($adv -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $adv.ContainsKey('extraArguments')) { return @($errors.ToArray()) }
    $extra = [string]$adv.extraArguments
    if ([string]::IsNullOrWhiteSpace($extra)) { return @($errors.ToArray()) }
    try {
        Test-ExtraArgumentsForRemovedSwitches -ExtraArguments $extra
    } catch {
        $errors.Add( (New-ValidationError `
            -InstancePath '/advanced/extraArguments' `
            -Keyword 'removedSwitch' `
            -Message ([string]$_.Exception.Message) `
            -Params @{}) )
    }
    return @($errors.ToArray())
}

# ---------------------------------------------------------------------
# Phase AF: auth.authProfileId conditional gate
# ---------------------------------------------------------------------
#
# The recipe.schema.json file uses JSON Schema 2020-12 allOf / if-then to
# enforce "App* mode requires authProfileId; Web/Device mode forbids it".
# This in-broker validator is intentionally a small subset and does not
# implement those keywords, so we encode the same rule procedurally and
# emit AJV-shaped errors that match what the SPA-side AJV would emit.
#
# Two errors possible:
#   - /auth/authProfileId  keyword=required    "must have authProfileId for AppRegistration* mode"
#   - /auth/authProfileId  keyword=forbidden   "must NOT have authProfileId for WebLogin/DeviceCode mode"
#
# Cookbook deliberately accepts the App* modes without verifying the
# referenced authProfileId resolves to a real row at SAVE time. The cook
# spawn path is the authoritative consumer; if the profile is missing or
# its secret is absent, the cook fails with a labelled error class.
# Validating at save-time would require the recipe layer to take a hard
# dependency on the auth_profiles table; the appliance contract treats
# recipes as filesystem-authoritative (the JSON file is the truth) and
# the profile id as a portable reference into a separate concern.
function Test-RecipeAuthProfileBinding {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('auth')) { return @($errors.ToArray()) }
    $auth = $Recipe.auth
    if (-not (($auth -is [hashtable]) -or ($auth -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    $mode = ''
    if ($auth.ContainsKey('mode')) { $mode = [string]$auth.mode }
    $hasProfile = $auth.ContainsKey('authProfileId') -and -not [string]::IsNullOrWhiteSpace([string]$auth.authProfileId)

    $appMode      = ($mode -eq 'AppRegistrationSecret') -or ($mode -eq 'AppRegistrationCertificate')
    $unboundMode  = ($mode -eq 'WebLogin') -or ($mode -eq 'DeviceCode') -or ($mode -eq 'ManagedIdentity')

    if ($appMode -and -not $hasProfile) {
        $errors.Add( (New-ValidationError `
            -InstancePath '/auth/authProfileId' `
            -Keyword 'required' `
            -Message 'must have authProfileId when auth.mode is AppRegistrationSecret or AppRegistrationCertificate' `
            -Params @{ mode = $mode }) )
    }
    if ($unboundMode -and $hasProfile) {
        $errors.Add( (New-ValidationError `
            -InstancePath '/auth/authProfileId' `
            -Keyword 'forbidden' `
            -Message 'must NOT have authProfileId when auth.mode is WebLogin, DeviceCode, or ManagedIdentity (interactive modes are identity-by-prompt; ManagedIdentity is identity-by-environment; neither binds to a stored profile)' `
            -Params @{ mode = $mode }) )
    }
    return @($errors.ToArray())
}

# ---------------------------------------------------------------------
# Phase AF: extraArguments secret-shape gate
# ---------------------------------------------------------------------
#
# Defensive complement to Test-ExtraArgumentsForRemovedSwitches. The
# advanced.extraArguments trailer is the operator's verbatim escape
# hatch and is the ONLY recipe-leaf surface that could plausibly carry
# inline secret material. The Phase AF doctrine is:
#
#   - Secrets live only in Windows Credential Manager.
#   - Recipes never persist secret material.
#   - The argv emitted at spawn time has secret material delivered via
#     GRAPH_CLIENT_SECRET (env var) -- never via -ClientSecret on the
#     command line.
#
# Therefore any -ClientSecret / -Password / -Token / -Bearer / etc.
# token appearing in the verbatim trailer must be rejected at SAVE time
# and again at COOK time. The structural rule is identical to the
# removed-switch gate: match a hyphen + token name at a token boundary,
# case-insensitively, followed by end-of-string, whitespace, or '='.
#
# These flag names are NOT PAX switches Cookbook intends to honour --
# they are deny-listed because they are the standard shapes an operator
# might paste in believing they "just work". The denylist intentionally
# covers names from neighbouring ecosystems (Graph, Azure CLI, MSAL)
# because operators reach for the trailer when official paths feel
# blocked, and Cookbook must surface the policy at that exact moment.
$Script:ExtraArgumentsSecretShapeFlags = @(
    'ClientSecret',
    'Password',
    'Pwd',
    'AppKey',
    'Secret',
    'Token',
    'AccessToken',
    'RefreshToken',
    'Bearer',
    'CertificatePassword',
    'CertPassword',
    'PfxPassword',
    'ApiKey'
)

function Test-ExtraArgumentsForSecretShape {
    param([string]$ExtraArguments)
    if ([string]::IsNullOrWhiteSpace($ExtraArguments)) { return }
    foreach ($name in $Script:ExtraArgumentsSecretShapeFlags) {
        $pattern = '(^|\s)-' + [regex]::Escape($name) + '($|\s|=|:)'
        if ([regex]::IsMatch($ExtraArguments, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            throw "advanced.extraArguments contains a secret-shape flag '-$name'. " +
                  "Cookbook never accepts inline secret material in recipe extraArguments. " +
                  "Use an Auth Profile (Settings -> Auth Profiles) so the secret lives only in Windows Credential Manager, " +
                  "or remove the flag entirely if it is not needed."
        }
    }
}

function Test-RecipeExtraArgumentsSecretShape {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('advanced')) { return @($errors.ToArray()) }
    $adv = $Recipe.advanced
    if (-not (($adv -is [hashtable]) -or ($adv -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $adv.ContainsKey('extraArguments')) { return @($errors.ToArray()) }
    $extra = [string]$adv.extraArguments
    if ([string]::IsNullOrWhiteSpace($extra)) { return @($errors.ToArray()) }
    try {
        Test-ExtraArgumentsForSecretShape -ExtraArguments $extra
    } catch {
        $errors.Add( (New-ValidationError `
            -InstancePath '/advanced/extraArguments' `
            -Keyword 'secretShape' `
            -Message ([string]$_.Exception.Message) `
            -Params @{}) )
    }
    return @($errors.ToArray())
}

# ---------------------------------------------------------------------
# Phase AF: execution-mode x auth-mode matrix
# ---------------------------------------------------------------------
#
# The matrix codifies the operational truth that an auth mode is only
# valid in environments where its capability actually exists:
#
#   local-manual    : WebLogin, DeviceCode, AppRegistrationSecret,
#                     AppRegistrationCertificate
#                     (interactive prompts are valid because a chef is
#                      present at the keyboard; App* modes work
#                      anywhere PAX runs)
#   local-scheduled : AppRegistrationSecret, AppRegistrationCertificate
#                     (no human is present at scheduled-task spawn time,
#                      so interactive modes would simply hang; Managed
#                      Identity is blocked because local Windows does
#                      not expose an IMDS endpoint in a bounded/truthful
#                      way)
#   fabric-hosted   : ManagedIdentity, AppRegistrationSecret,
#                     AppRegistrationCertificate
#                     (no interactive surface in Fabric runtimes; MI
#                      becomes valid because the host exposes a token
#                      endpoint Cookbook can truthfully delegate to)
#   azure-hosted    : ManagedIdentity, AppRegistrationSecret,
#                     AppRegistrationCertificate
#                     (same rationale as fabric-hosted)
#
# Always blocked everywhere: Credential, Silent. These are filtered out
# at the auth.mode enum layer; this matrix never sees them.
#
# Cookbook does NOT emulate Managed Identity locally. If a chef wants to
# author a recipe that uses MI, they must declare executionMode =
# fabric-hosted or azure-hosted. Cookbook v1's local broker will refuse
# to SPAWN such a recipe (the cook-spawn path is the place that gate is
# enforced; the recipe is still author-able here so it can be exported /
# consumed by the eventual host).
$Script:RecipeExecutionModeAuthMatrix = @{
    'local-manual'    = @('WebLogin','DeviceCode','AppRegistrationSecret','AppRegistrationCertificate')
    'local-scheduled' = @('AppRegistrationSecret','AppRegistrationCertificate')
    'fabric-hosted'   = @('ManagedIdentity','AppRegistrationSecret','AppRegistrationCertificate')
    'azure-hosted'    = @('ManagedIdentity','AppRegistrationSecret','AppRegistrationCertificate')
}

function Test-RecipeExecutionModeAuthMatrix {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('auth')) { return @($errors.ToArray()) }
    $auth = $Recipe.auth
    if (-not (($auth -is [hashtable]) -or ($auth -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $auth.ContainsKey('mode')) { return @($errors.ToArray()) }
    $mode = [string]$auth.mode
    # executionMode is OPTIONAL in v1: pre-AF recipes lack it. We treat
    # absence as 'local-manual' (the v1 default) so existing recipes
    # continue to validate. New recipes stamp the field explicitly.
    $exec = 'local-manual'
    if ($Recipe.ContainsKey('executionMode')) {
        $exec = [string]$Recipe.executionMode
    }

    if (-not $Script:RecipeExecutionModeAuthMatrix.ContainsKey($exec)) {
        # Unknown executionMode is already flagged by the enum-level
        # schema check; do not double-report here.
        return @($errors.ToArray())
    }
    $allowed = $Script:RecipeExecutionModeAuthMatrix[$exec]
    if ($allowed -notcontains $mode) {
        $rationale = switch ($exec) {
            'local-manual'    { 'local-manual permits WebLogin, DeviceCode, AppRegistrationSecret, or AppRegistrationCertificate (interactive modes require a chef at the keyboard)' }
            'local-scheduled' { 'local-scheduled permits AppRegistrationSecret or AppRegistrationCertificate only (no human is present at scheduled-task spawn time; Managed Identity is not available on a local Windows desktop)' }
            'fabric-hosted'   { 'fabric-hosted permits ManagedIdentity, AppRegistrationSecret, or AppRegistrationCertificate only (no interactive surface in Fabric runtimes)' }
            'azure-hosted'    { 'azure-hosted permits ManagedIdentity, AppRegistrationSecret, or AppRegistrationCertificate only (no interactive surface in Azure-hosted runtimes)' }
            default           { '' }
        }
        $errors.Add( (New-ValidationError `
            -InstancePath '/auth/mode' `
            -Keyword 'executionModeMismatch' `
            -Message ("auth.mode '$mode' is not valid for executionMode '$exec'. $rationale.") `
            -Params @{ executionMode = $exec; mode = $mode; allowed = $allowed }) )
    }
    return @($errors.ToArray())
}

# ---------------------------------------------------------------------
# Combined gate used by the route layer
# ---------------------------------------------------------------------

function Test-RecipeFactOutputMode {
    # V1.S26 cross-field gate on destinations.fact.
    #
    # S26 introduces destinations.fact.mode (outputPath | append) as the
    # new authoritative surface. M2.2's destinations.fact.appendBehavior
    # (fresh | append) is preserved as a legacy alias for back-compat
    # (no destructive on-disk migration). The procedural rule below
    # normalizes both fields into a single "effective mode" and enforces
    # the path / appendFile cross-field constraints against it.
    #
    # Effective mode resolution (authority order, per Brian's S26
    # decision):
    #
    #   1. mode='outputPath' present                          -> outputPath
    #   2. mode='append'      present                         -> append
    #   3. mode absent, appendBehavior='fresh'  or absent     -> outputPath
    #   4. mode absent, appendBehavior='append'               -> append
    #
    # Cross-field rules emitted as errors (AJV-shaped) against the
    # offending property path:
    #
    #   - mode and appendBehavior, when BOTH present, must agree
    #     (outputPath<->fresh, append<->append). A disagreement is
    #     rejected on /destinations/fact/appendBehavior.
    #
    #   - Effective mode='outputPath':
    #       path REQUIRED on /destinations/fact/path.
    #       appendFile FORBIDDEN on /destinations/fact/appendFile.
    #
    #   - Effective mode='append':
    #       appendFile REQUIRED on /destinations/fact/appendFile.
    #       path is ACCEPTED (legacy inert; the adapter ignores it),
    #       BUT when the operator authored the new mode='append' shape
    #       explicitly AND included path, the operator gets a warning-
    #       style error on /destinations/fact/path so the recipe is
    #       repaired rather than carrying inert fields forward. The
    #       error is suppressed when mode is absent (pure legacy
    #       appendBehavior='append' path).
    #
    #   - At least one of {path, appendFile} MUST be present overall so
    #     the recipe carries a writeable fact-output target. (Shape 3
    #     userInfoOnly recipes still carry a placeholder path because
    #     destinations.fact remains structurally required; that gate
    #     does not fire there.)
    #
    # Anchored at the most-actionable instancePath in each case so the
    # editor's PATH_TO_FIELD lookup places the error next to the right
    # input.
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('destinations')) { return @($errors.ToArray()) }
    $dest = $Recipe.destinations
    if (-not (($dest -is [hashtable]) -or ($dest -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $dest.ContainsKey('fact')) { return @($errors.ToArray()) }
    $fact = $dest.fact
    if (-not (($fact -is [hashtable]) -or ($fact -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }

    $hasMode     = $fact.ContainsKey('mode')
    $hasBehavior = $fact.ContainsKey('appendBehavior')
    $hasPath     = $fact.ContainsKey('path')
    $hasFile     = $fact.ContainsKey('appendFile')
    $mode        = if ($hasMode)     { [string]$fact.mode }           else { '' }
    $behavior    = if ($hasBehavior) { [string]$fact.appendBehavior } else { '' }
    $path        = if ($hasPath)     { [string]$fact.path }           else { '' }
    $file        = if ($hasFile)     { [string]$fact.appendFile }     else { '' }

    # ---- mode <-> appendBehavior consistency (when both present) ----
    if ($hasMode -and $hasBehavior) {
        $expectedBehavior = switch ($mode) {
            'outputPath' { 'fresh' }
            'append'     { 'append' }
            default      { '' }
        }
        if ($expectedBehavior -ne '' -and $behavior -ne $expectedBehavior) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/destinations/fact/appendBehavior' `
                -Keyword 'factModeBehaviorMismatch' `
                -Message ("destinations.fact.appendBehavior '$behavior' contradicts destinations.fact.mode '$mode'. " +
                          "mode='outputPath' requires appendBehavior='fresh' (or omit appendBehavior); " +
                          "mode='append' requires appendBehavior='append' (or omit appendBehavior). " +
                          "Cookbook prefers writing only 'mode' going forward; 'appendBehavior' is preserved only as a legacy alias.") `
                -Params @{ mode = $mode; appendBehavior = $behavior; expectedAppendBehavior = $expectedBehavior }) )
            # Continue evaluating other rules so the operator sees the
            # full picture, but skip the effective-mode-driven checks
            # because the recipe shape is internally inconsistent.
            return @($errors.ToArray())
        }
    }

    # ---- Effective mode resolution ----
    $effectiveMode = ''
    if ($hasMode) {
        $effectiveMode = $mode
    }
    elseif ($hasBehavior -and $behavior -eq 'append') {
        $effectiveMode = 'append'
    }
    else {
        $effectiveMode = 'outputPath'
    }

    # ---- At least one of {path, appendFile} must be present ----
    if (-not $hasPath -and -not $hasFile) {
        $errors.Add( (New-ValidationError `
            -InstancePath '/destinations/fact' `
            -Keyword 'factOutputTargetRequired' `
            -Message "destinations.fact must contain at least one of {path, appendFile} so a fact-output target exists." `
            -Params @{ effectiveMode = $effectiveMode }) )
        return @($errors.ToArray())
    }

    if ($effectiveMode -eq 'outputPath') {
        if (-not $hasPath -or [string]::IsNullOrEmpty($path)) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/destinations/fact/path' `
                -Keyword 'factPathRequired' `
                -Message "destinations.fact.path is required and must be non-empty when destinations.fact.mode='outputPath' (or appendBehavior is 'fresh'/absent)." `
                -Params @{ effectiveMode = $effectiveMode; mode = $mode; appendBehavior = $behavior }) )
        }
        if ($hasFile) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/destinations/fact/appendFile' `
                -Keyword 'factAppendFileForbidden' `
                -Message "destinations.fact.appendFile must be absent when destinations.fact.mode='outputPath' (or appendBehavior is 'fresh'/absent)." `
                -Params @{ effectiveMode = $effectiveMode; mode = $mode; appendBehavior = $behavior }) )
        }
    }
    elseif ($effectiveMode -eq 'append') {
        if (-not $hasFile -or [string]::IsNullOrEmpty($file)) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/destinations/fact/appendFile' `
                -Keyword 'factAppendFileRequired' `
                -Message "destinations.fact.appendFile is required and must be non-empty when destinations.fact.mode='append' (or appendBehavior='append')." `
                -Params @{ effectiveMode = $effectiveMode; mode = $mode; appendBehavior = $behavior }) )
        }
        # path under explicit mode='append': flag for repair. Legacy
        # appendBehavior='append' recipes (no explicit mode) keep path
        # silently because they were authored under the M2.2 surface
        # and the adapter ignores path under append mode anyway.
        if ($hasMode -and $hasPath) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/destinations/fact/path' `
                -Keyword 'factPathInertUnderAppendMode' `
                -Message ("destinations.fact.path is inert when destinations.fact.mode='append'; the adapter projects only -AppendFile. " +
                          "Remove path from the recipe so the shape matches the S26 contract (one of {path, appendFile} per mode).") `
                -Params @{ effectiveMode = $effectiveMode; mode = $mode }) )
        }
    }
    return @($errors.ToArray())
}

# V1.S26: legacy-name alias. Some callers (e.g. older smoke harnesses,
# corpus docs, third-party tooling) may still call Test-RecipeAppendBehavior
# by name. Preserving the symbol so they continue to compose; new code
# should call Test-RecipeFactOutputMode directly.
function Test-RecipeAppendBehavior {
    param([Parameter(Mandatory)]$Recipe)
    return Test-RecipeFactOutputMode -Recipe $Recipe
}

# V1.S26 cross-field gate on destinations.userInfo.
#
# Mirrors the fact-output-mode rules for the user-info channel:
#
#   - userInfo.mode='outputPath' -> path REQUIRED, appendFile FORBIDDEN
#   - userInfo.mode='append'     -> appendFile REQUIRED, path FORBIDDEN
#
# Unlike fact, userInfo has no legacy alias (M2.2 did not expose this
# channel structurally); mode is mandatory in the JSON schema and the
# hashtable mirror, so we only need to enforce mode<->path/appendFile.
# The "userInfo presence requires includeUserInfo=true under audit"
# rule lives in Test-RecipeUserInfoChannelGate; the "userInfo required
# under Shape 3" rule lives in Test-RecipeQueryShape.
function Test-RecipeUserInfoOutputMode {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('destinations')) { return @($errors.ToArray()) }
    $dest = $Recipe.destinations
    if (-not (($dest -is [hashtable]) -or ($dest -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $dest.ContainsKey('userInfo')) { return @($errors.ToArray()) }
    $ui = $dest.userInfo
    if (-not (($ui -is [hashtable]) -or ($ui -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }

    $hasMode = $ui.ContainsKey('mode')
    $hasPath = $ui.ContainsKey('path')
    $hasFile = $ui.ContainsKey('appendFile')
    $mode    = if ($hasMode) { [string]$ui.mode } else { '' }

    if (-not $hasMode -or [string]::IsNullOrEmpty($mode)) {
        # The schema hashtable marks 'mode' as required so the schema
        # walker will already have emitted a required-error here. Skip
        # silently to avoid double-reporting.
        return @($errors.ToArray())
    }

    if ($mode -eq 'outputPath') {
        if (-not $hasPath) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/destinations/userInfo/path' `
                -Keyword 'userInfoPathRequired' `
                -Message "destinations.userInfo.path is required when destinations.userInfo.mode='outputPath'." `
                -Params @{ mode = $mode }) )
        }
        if ($hasFile) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/destinations/userInfo/appendFile' `
                -Keyword 'userInfoAppendFileForbidden' `
                -Message "destinations.userInfo.appendFile must be absent when destinations.userInfo.mode='outputPath' (-OutputPathUserInfo and -AppendUserInfo are mutually exclusive)." `
                -Params @{ mode = $mode }) )
        }
    }
    elseif ($mode -eq 'append') {
        if (-not $hasFile) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/destinations/userInfo/appendFile' `
                -Keyword 'userInfoAppendFileRequired' `
                -Message "destinations.userInfo.appendFile is required when destinations.userInfo.mode='append'." `
                -Params @{ mode = $mode }) )
        }
        if ($hasPath) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/destinations/userInfo/path' `
                -Keyword 'userInfoPathForbidden' `
                -Message "destinations.userInfo.path must be absent when destinations.userInfo.mode='append' (-OutputPathUserInfo and -AppendUserInfo are mutually exclusive)." `
                -Params @{ mode = $mode }) )
        }
    }
    return @($errors.ToArray())
}

# V1.S26 cross-shape gate for the two supported run shapes:
#
# Audit shape (query.mode='audit' or absent) REQUIRES:
#   - processing.rollup
#   - query.startDate
#   - query.endDate
#   - destinations.fact
#
# Shape 3 (query.mode='userInfoOnly') is a separate top-level run shape
# -- NOT a checkbox -- and FORBIDS:
#   - processing.rollup (PAX itself blocks -OnlyUserInfo under rollup;
#     mirrors the engine's existing rollup blocker)
#   - ingredients.m365Usage.includeM365Usage=true (no audit query runs)
#   - ingredients.m365Usage.includeCopilotInteraction (no audit query)
#   - audit-only filter fields:
#       query.activityTypes, query.userIds, query.groupNames,
#       query.agentFilter, query.promptFilter
#   - query.startDate, query.endDate (no audit query runs)
#   - destinations.fact (Shape 3 emits user-info only)
# and REQUIRES:
#   - destinations.userInfo (Shape 3 must declare its output target)
#   - ingredients.entraUserData.includeUserInfo=true (the data the
#     Shape 3 run is fetching)
#
# Each violation is anchored on the offending path so the editor can
# place the error next to the right control.
function Test-RecipeQueryShape {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('query')) { return @($errors.ToArray()) }
    $query = $Recipe.query
    if (-not (($query -is [hashtable]) -or ($query -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    $mode = ''
    if ($query.ContainsKey('mode')) { $mode = [string]$query.mode }
    # Audit shape (mode='audit' or absent): query.startDate, query.endDate,
    # and destinations.fact are REQUIRED. processing.rollup is OPTIONAL
    # under audit (an operator may pull raw audit data with no rollup
    # post-processing); when present, its value drives the rollup-only
    # blocker gates elsewhere. There is intentionally no rollup-required
    # check here.
    if ($mode -eq '' -or $mode -eq 'audit') {
        if (-not $query.ContainsKey('startDate')) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/query/startDate' `
                -Keyword 'startDateRequiredUnderAudit' `
                -Message "query.startDate is required for audit-shape recipes (query.mode='audit' or absent)." `
                -Params @{ queryMode = $mode }) )
        }
        if (-not $query.ContainsKey('endDate')) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/query/endDate' `
                -Keyword 'endDateRequiredUnderAudit' `
                -Message "query.endDate is required for audit-shape recipes (query.mode='audit' or absent)." `
                -Params @{ queryMode = $mode }) )
        }
        $hasFact = $false
        if ($Recipe.ContainsKey('destinations')) {
            $dest = $Recipe.destinations
            if (($dest -is [hashtable]) -or ($dest -is [System.Collections.IDictionary])) {
                if ($dest.ContainsKey('fact')) { $hasFact = $true }
            }
        }
        if (-not $hasFact) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/destinations/fact' `
                -Keyword 'factDestinationRequiredUnderAudit' `
                -Message "destinations.fact is required for audit-shape recipes (query.mode='audit' or absent). The audit query must declare where its fact output is written." `
                -Params @{ queryMode = $mode }) )
        }
        return @($errors.ToArray())
    }

    if ($mode -ne 'userInfoOnly') {
        # Unknown values are already flagged by the enum-level schema
        # check; do not double-report here.
        return @($errors.ToArray())
    }

    # ----- Shape 3 gating -----
    if ($Recipe.ContainsKey('processing')) {
        $proc = $Recipe.processing
        if (($proc -is [hashtable]) -or ($proc -is [System.Collections.IDictionary])) {
            if ($proc.ContainsKey('rollup')) {
                $errors.Add( (New-ValidationError `
                    -InstancePath '/processing/rollup' `
                    -Keyword 'rollupForbiddenUnderUserInfoOnly' `
                    -Message "processing.rollup must be absent when query.mode='userInfoOnly' (Shape 3 -- user-info-only runs skip the audit query and the rollup post-processor)." `
                    -Params @{ queryMode = $mode; rollup = [string]$proc.rollup }) )
            }
        }
    }

    if ($Recipe.ContainsKey('ingredients')) {
        $ing = $Recipe.ingredients
        if (($ing -is [hashtable]) -or ($ing -is [System.Collections.IDictionary])) {
            if ($ing.ContainsKey('m365Usage')) {
                $m = $ing.m365Usage
                if (($m -is [hashtable]) -or ($m -is [System.Collections.IDictionary])) {
                    if ($m.ContainsKey('includeM365Usage') -and [bool]$m.includeM365Usage) {
                        $errors.Add( (New-ValidationError `
                            -InstancePath '/ingredients/m365Usage/includeM365Usage' `
                            -Keyword 'm365UsageForbiddenUnderUserInfoOnly' `
                            -Message "ingredients.m365Usage.includeM365Usage must be false when query.mode='userInfoOnly' (no audit query runs in Shape 3)." `
                            -Params @{ queryMode = $mode }) )
                    }
                    if ($m.ContainsKey('includeCopilotInteraction')) {
                        $errors.Add( (New-ValidationError `
                            -InstancePath '/ingredients/m365Usage/includeCopilotInteraction' `
                            -Keyword 'includeCopilotInteractionForbiddenUnderUserInfoOnly' `
                            -Message "ingredients.m365Usage.includeCopilotInteraction must be absent when query.mode='userInfoOnly' (no audit query runs)." `
                            -Params @{ queryMode = $mode }) )
                    }
                }
            }
            if ($ing.ContainsKey('entraUserData')) {
                $eud = $ing.entraUserData
                if (($eud -is [hashtable]) -or ($eud -is [System.Collections.IDictionary])) {
                    $hasIUI = $eud.ContainsKey('includeUserInfo')
                    $iuiVal = $false
                    if ($hasIUI) { $iuiVal = [bool]$eud.includeUserInfo }
                    if ((-not $hasIUI) -or (-not $iuiVal)) {
                        $errors.Add( (New-ValidationError `
                            -InstancePath '/ingredients/entraUserData/includeUserInfo' `
                            -Keyword 'userInfoOnlyRequiresIncludeUserInfoTrue' `
                            -Message "ingredients.entraUserData.includeUserInfo must be true when query.mode='userInfoOnly' (Shape 3's whole purpose is to fetch user-info data)." `
                            -Params @{ queryMode = $mode }) )
                    }
                }
            }
        }
    }

    foreach ($k in @('activityTypes','userIds','groupNames','agentFilter','promptFilter')) {
        if ($query.ContainsKey($k)) {
            $errors.Add( (New-ValidationError `
                -InstancePath ('/query/' + $k) `
                -Keyword 'auditFilterForbiddenUnderUserInfoOnly' `
                -Message ("query.$k must be absent when query.mode='userInfoOnly' (audit-only filter fields are not applicable to user-info-only runs).") `
                -Params @{ queryMode = $mode; field = $k }) )
        }
    }

    if ($query.ContainsKey('startDate')) {
        $errors.Add( (New-ValidationError `
            -InstancePath '/query/startDate' `
            -Keyword 'userInfoOnlyForbidsStartDate' `
            -Message "query.startDate must be absent when query.mode='userInfoOnly' (no audit query runs in Shape 3; user-info data has no date-range parameter)." `
            -Params @{ queryMode = $mode }) )
    }
    if ($query.ContainsKey('endDate')) {
        $errors.Add( (New-ValidationError `
            -InstancePath '/query/endDate' `
            -Keyword 'userInfoOnlyForbidsEndDate' `
            -Message "query.endDate must be absent when query.mode='userInfoOnly' (no audit query runs in Shape 3; user-info data has no date-range parameter)." `
            -Params @{ queryMode = $mode }) )
    }

    # destinations.userInfo REQUIRED; destinations.fact FORBIDDEN.
    $hasUserInfo = $false
    $hasFact     = $false
    if ($Recipe.ContainsKey('destinations')) {
        $dest = $Recipe.destinations
        if (($dest -is [hashtable]) -or ($dest -is [System.Collections.IDictionary])) {
            if ($dest.ContainsKey('userInfo')) { $hasUserInfo = $true }
            if ($dest.ContainsKey('fact'))     { $hasFact     = $true }
        }
    }
    if (-not $hasUserInfo) {
        $errors.Add( (New-ValidationError `
            -InstancePath '/destinations/userInfo' `
            -Keyword 'userInfoRequiredUnderUserInfoOnly' `
            -Message "destinations.userInfo is required when query.mode='userInfoOnly' (Shape 3 must declare where user-info output is written; -OutputPathUserInfo or -AppendUserInfo)." `
            -Params @{ queryMode = $mode }) )
    }
    if ($hasFact) {
        $errors.Add( (New-ValidationError `
            -InstancePath '/destinations/fact' `
            -Keyword 'userInfoOnlyForbidsFactDestination' `
            -Message "destinations.fact must be absent when query.mode='userInfoOnly' (Shape 3 emits user-info output only; no audit/fact rows are produced)." `
            -Params @{ queryMode = $mode }) )
    }

    return @($errors.ToArray())
}

# V1.S26: under rollup (Shape 1), query.activityTypes (if present) MUST
# equal exactly ['CopilotInteraction']. PAX's rollup post-processor is
# CopilotInteraction-specific; any other activity type would cause the
# rollup to silently drop or mislabel rows. This is enforced at SAVE
# time so the operator can't author a rollup recipe that PAX will then
# either reject at spawn or produce incorrect rollup output for.
function Test-RecipeActivityTypesUnderRollup {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    # Gate only fires when processing.rollup is present (rollup run).
    $hasRollup = $false
    if ($Recipe.ContainsKey('processing')) {
        $proc = $Recipe.processing
        if (($proc -is [hashtable]) -or ($proc -is [System.Collections.IDictionary])) {
            if ($proc.ContainsKey('rollup')) {
                $rv = [string]$proc.rollup
                if ($rv -eq 'Rollup' -or $rv -eq 'RollupPlusRaw') { $hasRollup = $true }
            }
        }
    }
    if (-not $hasRollup) { return @($errors.ToArray()) }

    if (-not $Recipe.ContainsKey('query')) { return @($errors.ToArray()) }
    $query = $Recipe.query
    if (-not (($query -is [hashtable]) -or ($query -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $query.ContainsKey('activityTypes')) { return @($errors.ToArray()) }
    $at = $query.activityTypes
    # Empty/non-array -- schema walker already flagged.
    if (-not (($at -is [array]) -or ($at -is [System.Collections.IList]))) {
        return @($errors.ToArray())
    }
    $count = if ($at -is [array]) { $at.Length } else { $at.Count }
    if ($count -ne 1 -or [string]$at[0] -ne 'CopilotInteraction') {
        $errors.Add( (New-ValidationError `
            -InstancePath '/query/activityTypes' `
            -Keyword 'activityTypesRollupConstraint' `
            -Message ("query.activityTypes must equal exactly ['CopilotInteraction'] when processing.rollup is set. " +
                      "PAX's rollup post-processor is CopilotInteraction-specific; other activity types would silently mislabel rolled-up rows. " +
                      "Remove query.activityTypes to use the default, or change processing.rollup to omit the rollup post-processor.") `
            -Params @{ activityTypes = $at; rollup = [string]$Recipe.processing.rollup }) )
    }
    return @($errors.ToArray())
}

# V1.S26: ingredients.m365Usage.includeCopilotInteraction=false is only
# valid when includeM365Usage=true. The PAX engine itself enforces this
# at rollup gate time (existing -ExcludeCopilotInteraction without
# -IncludeM365Usage blocker; rule L2.ROLLUP.EXCLUDE_COPILOT_REQUIRES_M365_USAGE
# in Test-RecipeRollupBlockers). S26 adds the structured-shape mirror:
# the new ingredients.m365Usage.includeCopilotInteraction structured
# field projects to -ExcludeCopilotInteraction when false, so the same
# conjunction rule must apply at the structured layer.
function Test-RecipeM365UsageGate {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('ingredients')) { return @($errors.ToArray()) }
    $ing = $Recipe.ingredients
    if (-not (($ing -is [hashtable]) -or ($ing -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $ing.ContainsKey('m365Usage')) { return @($errors.ToArray()) }
    $m = $ing.m365Usage
    if (-not (($m -is [hashtable]) -or ($m -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $m.ContainsKey('includeCopilotInteraction')) { return @($errors.ToArray()) }
    $excludeCopilot = -not [bool]$m.includeCopilotInteraction
    if (-not $excludeCopilot) { return @($errors.ToArray()) }
    $includeM365 = $false
    if ($m.ContainsKey('includeM365Usage')) { $includeM365 = [bool]$m.includeM365Usage }
    if (-not $includeM365) {
        $errors.Add( (New-ValidationError `
            -InstancePath '/ingredients/m365Usage/includeCopilotInteraction' `
            -Keyword 'excludeCopilotInteractionRequiresM365Usage' `
            -Message ("ingredients.m365Usage.includeCopilotInteraction=false is only valid when ingredients.m365Usage.includeM365Usage=true " +
                      "(excluding CopilotInteraction without the M365 usage bundle would leave the recipe with no audit data to fetch). " +
                      "Either set includeM365Usage=true, or set includeCopilotInteraction=true (or remove the field).") `
            -Params @{ includeCopilotInteraction = $false; includeM365Usage = $includeM365 }) )
    }
    return @($errors.ToArray())
}

# V1.S26: destinations.userInfo presence is only valid under audit
# shape (query.mode='audit' or absent) when ingredients.entraUserData.
# includeUserInfo=true. Shape 3 (query.mode='userInfoOnly') requires
# destinations.userInfo regardless; that branch is handled by
# Test-RecipeQueryShape. This gate covers the audit-shape case so a
# recipe can't carry a user-info channel that PAX won't populate.
function Test-RecipeUserInfoChannelGate {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    $queryMode = ''
    if ($Recipe.ContainsKey('query')) {
        $q = $Recipe.query
        if (($q -is [hashtable]) -or ($q -is [System.Collections.IDictionary])) {
            if ($q.ContainsKey('mode')) { $queryMode = [string]$q.mode }
        }
    }
    if ($queryMode -eq 'userInfoOnly') { return @($errors.ToArray()) }

    $hasUserInfo = $false
    if ($Recipe.ContainsKey('destinations')) {
        $dest = $Recipe.destinations
        if (($dest -is [hashtable]) -or ($dest -is [System.Collections.IDictionary])) {
            if ($dest.ContainsKey('userInfo')) { $hasUserInfo = $true }
        }
    }
    if (-not $hasUserInfo) { return @($errors.ToArray()) }

    $includeUI = $false
    if ($Recipe.ContainsKey('ingredients')) {
        $ing = $Recipe.ingredients
        if (($ing -is [hashtable]) -or ($ing -is [System.Collections.IDictionary])) {
            if ($ing.ContainsKey('entraUserData')) {
                $eud = $ing.entraUserData
                if (($eud -is [hashtable]) -or ($eud -is [System.Collections.IDictionary])) {
                    if ($eud.ContainsKey('includeUserInfo')) { $includeUI = [bool]$eud.includeUserInfo }
                }
            }
        }
    }
    if (-not $includeUI) {
        $errors.Add( (New-ValidationError `
            -InstancePath '/destinations/userInfo' `
            -Keyword 'userInfoChannelRequiresIncludeUserInfo' `
            -Message ("destinations.userInfo is only valid under audit shape when ingredients.entraUserData.includeUserInfo=true " +
                      "(no user-info data would be produced otherwise). Either set includeUserInfo=true, " +
                      "remove destinations.userInfo, or change query.mode to 'userInfoOnly' for a Shape 3 user-info-only run.") `
            -Params @{ queryMode = $queryMode; includeUserInfo = $includeUI }) )
    }
    return @($errors.ToArray())
}

# V1.S26: agentFilter cross-field guard. The schema hashtable enforces
# mode is required and that the enum is one of
# {none, agentIds, agentsOnly, excludeAgents}; this function enforces
# the per-mode rules for the agentIds field:
#   mode='none'           -> agentIds forbidden (no filtering applied,
#                            ids would be inert)
#   mode='agentIds'       -> agentIds REQUIRED (the id list IS the
#                            filter; the adapter will project -AgentId)
#   mode='agentsOnly'     -> agentIds forbidden (-AgentsOnly is a
#                            parameterless PAX switch)
#   mode='excludeAgents'  -> agentIds forbidden (-ExcludeAgents is a
#                            parameterless PAX switch)
# Because mode is a single enum field, the four operations are
# automatically mutually exclusive at the schema layer; this function
# only enforces the per-mode agentIds presence rule.
function Test-RecipeAgentFilterShape {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('query')) { return @($errors.ToArray()) }
    $q = $Recipe.query
    if (-not (($q -is [hashtable]) -or ($q -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $q.ContainsKey('agentFilter')) { return @($errors.ToArray()) }
    $af = $q.agentFilter
    if (-not (($af -is [hashtable]) -or ($af -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    $mode = ''
    if ($af.ContainsKey('mode')) { $mode = [string]$af.mode }
    $hasIds = $af.ContainsKey('agentIds')
    switch ($mode) {
        'none' {
            if ($hasIds) {
                $errors.Add( (New-ValidationError `
                    -InstancePath '/query/agentFilter/agentIds' `
                    -Keyword 'agentFilterAgentIdsForbiddenUnderNone' `
                    -Message "query.agentFilter.agentIds must be absent when query.agentFilter.mode='none' (no agent filtering is being applied, so the ids would be inert). Remove agentIds, or change mode to 'agentIds'." `
                    -Params @{ agentFilterMode = $mode }) )
            }
        }
        'agentIds' {
            if (-not $hasIds) {
                $errors.Add( (New-ValidationError `
                    -InstancePath '/query/agentFilter/agentIds' `
                    -Keyword 'agentFilterAgentIdsRequiredUnderAgentIds' `
                    -Message "query.agentFilter.agentIds is required when query.agentFilter.mode='agentIds' (the id list IS the filter and is projected as -AgentId <values>). Add a non-empty agentIds array, or change mode to 'none', 'agentsOnly', or 'excludeAgents'." `
                    -Params @{ agentFilterMode = $mode }) )
            }
        }
        'agentsOnly' {
            if ($hasIds) {
                $errors.Add( (New-ValidationError `
                    -InstancePath '/query/agentFilter/agentIds' `
                    -Keyword 'agentFilterAgentIdsForbiddenUnderAgentsOnly' `
                    -Message "query.agentFilter.agentIds must be absent when query.agentFilter.mode='agentsOnly' (PAX -AgentsOnly is a parameterless switch). Remove agentIds, or change mode to 'agentIds' to filter to a specific list." `
                    -Params @{ agentFilterMode = $mode }) )
            }
        }
        'excludeAgents' {
            if ($hasIds) {
                $errors.Add( (New-ValidationError `
                    -InstancePath '/query/agentFilter/agentIds' `
                    -Keyword 'agentFilterAgentIdsForbiddenUnderExcludeAgents' `
                    -Message "query.agentFilter.agentIds must be absent when query.agentFilter.mode='excludeAgents' (PAX -ExcludeAgents is a parameterless switch). Remove agentIds, or change mode to 'agentIds' to filter to a specific list." `
                    -Params @{ agentFilterMode = $mode }) )
            }
        }
    }
    return @($errors.ToArray())
}

# V1.S26: trailer-string deny-list for switches that are unsupported
# under the S26 contract. These are the switches Brian explicitly
# declined to expose in the Cookbook supported surface:
#
#   -RecordTypes / -ServiceTypes : PAX low-level filter knobs that
#     are too coarse / overlap with -ActivityTypes; structured surface
#     is query.activityTypes only.
#
#   -UseEOM : Exchange-Online-Management mode (serial-only, slow,
#     contract-incompatible with the partitioned PAX runtime Cookbook
#     bundles); removed from the supported surface entirely.
#
# Detection mirrors Test-ExtraArgumentsForRemovedSwitches: case-
# insensitive '(^|\s)-<name>($|\s|=)'. Anchored on
# /advanced/extraArguments.
$Script:S26UnsupportedSwitches = @(
    @{ Switch = 'RecordTypes';  Hint = "RecordTypes is not in the Cookbook supported surface; use query.activityTypes for activity scoping." }
    @{ Switch = 'ServiceTypes'; Hint = "ServiceTypes is not in the Cookbook supported surface; use ingredients.m365Usage.includeM365Usage and query.activityTypes for service/activity scoping." }
    @{ Switch = 'UseEOM';       Hint = "UseEOM (Exchange Online Management mode) is incompatible with Cookbook's partitioned runtime contract and is not in the supported surface." }
)
function Test-RecipeExtraArgumentsUnsupportedSwitches {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('advanced')) { return @($errors.ToArray()) }
    $adv = $Recipe.advanced
    if (-not (($adv -is [hashtable]) -or ($adv -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $adv.ContainsKey('extraArguments')) { return @($errors.ToArray()) }
    $extra = [string]$adv.extraArguments
    if ([string]::IsNullOrWhiteSpace($extra)) { return @($errors.ToArray()) }
    foreach ($entry in $Script:S26UnsupportedSwitches) {
        $pattern = '(^|\s)-' + [regex]::Escape($entry.Switch) + '($|\s|=)'
        if ([regex]::IsMatch($extra, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/advanced/extraArguments' `
                -Keyword 'unsupportedSwitch' `
                -Message ("advanced.extraArguments contains unsupported switch '-{0}'. {1}" -f $entry.Switch, $entry.Hint) `
                -Params @{ switch = ('-' + $entry.Switch) }) )
        }
    }
    return @($errors.ToArray())
}

# V1.S26: trailer-string deny-list for switches that are now owned by
# the structured recipe surface and therefore must not be re-supplied
# via the verbatim trailer. Mixing structured emissions with trailer
# emissions of the same switch is ambiguous and forbidden -- the
# adapter would project the switch twice with potentially conflicting
# values.
#
# Detection mirrors Test-ExtraArgumentsForRemovedSwitches: case-
# insensitive '(^|\s)-<name>($|\s|=)'.
$Script:S26StructurallyOwnedSwitches = @(
    'OutputPath', 'AppendFile',
    'OutputPathUserInfo', 'AppendUserInfo',
    'IncludeUserInfo', 'OnlyUserInfo',
    'IncludeM365Usage', 'ExcludeCopilotInteraction',
    'ActivityTypes', 'UserIds', 'GroupNames',
    'AgentId', 'AgentsOnly', 'ExcludeAgents',
    'PromptFilter', 'ClientCertificatePath',
    'StartDate', 'EndDate', 'Rollup', 'RollupPlusRaw'
)
function Test-RecipeExtraArgumentsStructurallyOwned {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $Recipe.ContainsKey('advanced')) { return @($errors.ToArray()) }
    $adv = $Recipe.advanced
    if (-not (($adv -is [hashtable]) -or ($adv -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }
    if (-not $adv.ContainsKey('extraArguments')) { return @($errors.ToArray()) }
    $extra = [string]$adv.extraArguments
    if ([string]::IsNullOrWhiteSpace($extra)) { return @($errors.ToArray()) }
    foreach ($name in $Script:S26StructurallyOwnedSwitches) {
        $pattern = '(^|\s)-' + [regex]::Escape($name) + '($|\s|=)'
        if ([regex]::IsMatch($extra, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $msg = ("advanced.extraArguments contains structurally-owned switch '-{0}'. " +
                    "This switch is owned by the structured recipe surface; supply it via the corresponding recipe field instead so the adapter projects exactly once and validation can verify the value.") -f $name
            $errors.Add( (New-ValidationError `
                -InstancePath '/advanced/extraArguments' `
                -Keyword 'structurallyOwnedSwitch' `
                -Message $msg `
                -Params @{ switch = ('-' + $name) }) )
        }
    }
    return @($errors.ToArray())
}

# ---------------------------------------------------------------------
# M2.3: rollup-blocker L2 pre-validation
# ---------------------------------------------------------------------
#
# Mirrors the bundled PAX engine's rollup-blocker logic from
# PAX_Purview_Audit_Log_Processor.ps1 so Cookbook rejects a known-bad
# combination BEFORE spawning PAX rather than after PAX exits non-zero.
#
# Engine source (PAX, gating section "Rollup post-processor gating"):
#
#   if ($Rollup -or $RollupPlusRaw) {
#       ...
#       $rollupBlockers = @()
#       if ($UseEOM)              { $rollupBlockers += '-UseEOM' }
#       if ($ExportWorkbook)      { $rollupBlockers += '-ExportWorkbook' }
#       if ($OnlyUserInfo)        { $rollupBlockers += '-OnlyUserInfo' }
#       if ($OnlyAgent365Info)    { $rollupBlockers += '-OnlyAgent365Info' }
#       if ($RAWInputCSV)         { $rollupBlockers += '-RAWInputCSV' }
#       if ($ExcludeCopilotInteraction -and -not $IncludeM365Usage) {
#           $rollupBlockers += '-ExcludeCopilotInteraction'
#       }
#       if ($rollupBlockers.Count -gt 0) { ... exit 1 }
#   }
#
# Cookbook semantics:
#
#   - processing.rollup is OPTIONAL. When present the enum spans
#     'Rollup' and 'RollupPlusRaw', so the recipe is a rollup run and
#     this gate applies. When processing.rollup is absent (raw audit
#     pull) or carries a future non-rollup value, the gate skips. Both
#     rollup modes are equivalent for the purposes of this gate.
#
#   - None of the six engine blockers have a structured leaf in the M1
#     recipe schema. The only carrier is the verbatim escape hatch
#     advanced.extraArguments. Detection mirrors the existing
#     Test-ExtraArgumentsForRemovedSwitches token shape: case-insensitive
#     '(^|\s)-<name>($|\s|=)'.
#
#   - The first five engine blockers translate to a per-switch rejection
#     keyed on the switch name. The sixth blocker also consults the
#     structured leaf ingredients.m365Usage.includeM365Usage to mirror
#     the engine's conjunction ('-ExcludeCopilotInteraction AND NOT
#     -IncludeM365Usage').
#
#   - The engine also forbids passing BOTH -Rollup and -RollupPlusRaw
#     at the same time. Cookbook's recipe shape carries rollup as a
#     single enum field, so the conjunction can never form here.
#
# Rule IDs (per slice spec) and the keyword shape they map to (the
# existing validator convention is camelCase keywords, so the L2 rule
# id is carried in error.params.ruleId while the keyword stays in
# convention):
#
#   L2.ROLLUP.NO_USEEOM                              -> rollupBlockedByUseEOM
#   L2.ROLLUP.NO_EXPORTWORKBOOK                      -> rollupBlockedByExportWorkbook
#   L2.ROLLUP.NO_ONLYUSERINFO                        -> rollupBlockedByOnlyUserInfo
#   L2.ROLLUP.NO_ONLYAGENT365INFO                    -> rollupBlockedByOnlyAgent365Info
#   L2.ROLLUP.NO_RAWINPUTCSV                         -> rollupBlockedByRawInputCsv
#   L2.ROLLUP.EXCLUDE_COPILOT_REQUIRES_M365_USAGE    -> rollupExcludeCopilotRequiresM365Usage
$Script:RollupBlockerSwitches = @(
    @{ Switch = 'UseEOM';            Keyword = 'rollupBlockedByUseEOM';            RuleId = 'L2.ROLLUP.NO_USEEOM' }
    @{ Switch = 'ExportWorkbook';    Keyword = 'rollupBlockedByExportWorkbook';    RuleId = 'L2.ROLLUP.NO_EXPORTWORKBOOK' }
    @{ Switch = 'OnlyUserInfo';      Keyword = 'rollupBlockedByOnlyUserInfo';      RuleId = 'L2.ROLLUP.NO_ONLYUSERINFO' }
    @{ Switch = 'OnlyAgent365Info';  Keyword = 'rollupBlockedByOnlyAgent365Info';  RuleId = 'L2.ROLLUP.NO_ONLYAGENT365INFO' }
    @{ Switch = 'RAWInputCSV';       Keyword = 'rollupBlockedByRawInputCsv';       RuleId = 'L2.ROLLUP.NO_RAWINPUTCSV' }
)

function Test-RecipeRollupBlockers {
    param([Parameter(Mandatory)]$Recipe)
    $errors = New-Object System.Collections.Generic.List[object]
    if (-not (($Recipe -is [hashtable]) -or ($Recipe -is [System.Collections.IDictionary]))) {
        return @($errors.ToArray())
    }

    # Gate only fires for rollup runs. processing.rollup is OPTIONAL, so
    # a recipe is a rollup run only when processing.rollup is present and
    # carries a rollup value ('Rollup' | 'RollupPlusRaw'); when it is
    # absent (raw audit pull) or carries a future non-rollup value the
    # rollup-only rules do not apply. Both rollup modes are equivalent
    # for the purposes of the engine's rollup-blocker gate.
    $isRollup = $false
    if ($Recipe.ContainsKey('processing')) {
        $proc = $Recipe.processing
        if (($proc -is [hashtable]) -or ($proc -is [System.Collections.IDictionary])) {
            if ($proc.ContainsKey('rollup')) {
                $rollupVal = [string]$proc.rollup
                if ($rollupVal -eq 'Rollup' -or $rollupVal -eq 'RollupPlusRaw') {
                    $isRollup = $true
                }
            }
        }
    }
    if (-not $isRollup) { return @($errors.ToArray()) }

    # Pull the verbatim trailer. Absence is normal and means no blocker
    # can be present.
    $extra = ''
    if ($Recipe.ContainsKey('advanced')) {
        $adv = $Recipe.advanced
        if (($adv -is [hashtable]) -or ($adv -is [System.Collections.IDictionary])) {
            if ($adv.ContainsKey('extraArguments')) {
                $extra = [string]$adv.extraArguments
            }
        }
    }

    # Pull the structured M365 leaf for the conjunction rule. Defaults
    # to $false when absent so the conjunction degrades safely.
    $includeM365 = $false
    if ($Recipe.ContainsKey('ingredients')) {
        $ing = $Recipe.ingredients
        if (($ing -is [hashtable]) -or ($ing -is [System.Collections.IDictionary])) {
            if ($ing.ContainsKey('m365Usage')) {
                $m365 = $ing.m365Usage
                if (($m365 -is [hashtable]) -or ($m365 -is [System.Collections.IDictionary])) {
                    if ($m365.ContainsKey('includeM365Usage')) {
                        $includeM365 = [bool]$m365.includeM365Usage
                    }
                }
            }
        }
    }

    # Helper: case-insensitive '(^|\s)-<name>($|\s|=)' token match,
    # matching the existing Test-ExtraArgumentsForRemovedSwitches shape.
    function Script:_HasSwitch {
        param([string]$Trailer,[string]$Name)
        if ([string]::IsNullOrWhiteSpace($Trailer)) { return $false }
        $pattern = '(^|\s)-' + [regex]::Escape($Name) + '($|\s|=)'
        return [regex]::IsMatch($Trailer, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }

    # Five direct per-switch blockers (UseEOM, ExportWorkbook,
    # OnlyUserInfo, OnlyAgent365Info, RAWInputCSV). Each match emits a
    # distinct error keyed to the switch name.
    foreach ($entry in $Script:RollupBlockerSwitches) {
        if (Script:_HasSwitch -Trailer $extra -Name $entry.Switch) {
            $errors.Add( (New-ValidationError `
                -InstancePath '/advanced/extraArguments' `
                -Keyword $entry.Keyword `
                -Message ("rollup runs cannot include -{0} (mirrors the bundled PAX engine's rollup-blocker gate; rule {1})" -f $entry.Switch, $entry.RuleId) `
                -Params @{ ruleId = $entry.RuleId; switch = ('-' + $entry.Switch) }) )
        }
    }

    # Conjunction rule: -ExcludeCopilotInteraction is only a rollup
    # blocker when ingredients.m365Usage.includeM365Usage is NOT true.
    # The engine condition is ($ExcludeCopilotInteraction -and -not
    # $IncludeM365Usage). Anchored at the structured leaf because the
    # remedy is to flip that leaf (or remove the trailer switch).
    if ((Script:_HasSwitch -Trailer $extra -Name 'ExcludeCopilotInteraction') -and (-not $includeM365)) {
        $errors.Add( (New-ValidationError `
            -InstancePath '/ingredients/m365Usage/includeM365Usage' `
            -Keyword 'rollupExcludeCopilotRequiresM365Usage' `
            -Message 'rollup runs that pass -ExcludeCopilotInteraction in advanced.extraArguments must also set ingredients.m365Usage.includeM365Usage = true (mirrors the bundled PAX engine''s rollup-blocker gate; rule L2.ROLLUP.EXCLUDE_COPILOT_REQUIRES_M365_USAGE)' `
            -Params @{ ruleId = 'L2.ROLLUP.EXCLUDE_COPILOT_REQUIRES_M365_USAGE'; switch = '-ExcludeCopilotInteraction'; includeM365Usage = $includeM365 }) )
    }

    return @($errors.ToArray())
}

function Test-RecipeAll {
    # Runs every gate and returns a single combined result. Order
    # matters: schema first (cheaper, catches structural problems),
    # tier policy second (only meaningful if the destinations branch
    # is well-formed), date-range third (only meaningful if both
    # query dates parsed cleanly), removed-switch trailer fourth
    # (cheapest defensive check, runs even if earlier gates already
    # flagged errors so the operator sees the full picture), Phase AF
    # auth-profile binding fifth (structural conditional that AJV runs
    # client-side via allOf/if-then), Phase AF execution-mode x
    # auth-mode matrix sixth (operational-truth gate; blocks chef from
    # authoring impossible combinations like local-scheduled + WebLogin
    # or local-manual + ManagedIdentity), Phase AF secret-shape trailer
    # seventh (denies inline secret material in the verbatim escape
    # hatch), V1.S26 fact-output-mode cross-field eighth (mirrors the
    # client-side allOf/if-then in destinations.fact for mode /
    # appendBehavior / path / appendFile), V1.S26 userInfo-output-mode
    # cross-field ninth (mirrors destinations.userInfo mode/path/
    # appendFile mutex), V1.S26 query-shape gate tenth (Shape 3 -- query.
    # mode='userInfoOnly' -- forbids rollup, m365Usage.includeM365Usage=
    # true, audit-only filter fields; requires destinations.userInfo;
    # audit shape requires query.startDate/endDate and destinations.fact
    # but NOT processing.rollup, which is optional under audit), V1.S26
    # activityTypes-under-rollup eleventh (under rollup, activityTypes
    # MUST equal exactly ['CopilotInteraction']), V1.S26 m365Usage gate
    # twelfth (includeCopilotInteraction=false requires includeM365Usage
    # =true), V1.S26 userInfo-channel gate thirteenth (under audit shape,
    # destinations.userInfo requires ingredients.entraUserData.
    # includeUserInfo=true), V1.S26 agentFilter shape fourteenth (mode=
    # 'none' forbids agentIds), V1.S26 unsupported-switches trailer
    # fifteenth (denies RecordTypes/ServiceTypes/UseEOM in
    # extraArguments), V1.S26 structurally-owned-switches trailer
    # sixteenth (denies trailer-emission of switches that have a
    # structured recipe field), M2.3 rollup-blocker pre-validation
    # seventeenth (mirrors the bundled PAX engine's "Rollup post-
    # processor gating" so known-bad rollup combinations are rejected
    # before PAX spawn rather than after PAX exits non-zero).
    param([Parameter(Mandatory)]$Recipe)
    $schemaResult       = Test-RecipeSchema -Recipe $Recipe
    $tierErrors         = @(Test-RecipeOutputPathTier -Recipe $Recipe)
    $rangeErrors        = @(Test-RecipeQueryDateRange -Recipe $Recipe)
    $removedSw          = @(Test-RecipeExtraArgumentsRemovedSwitches -Recipe $Recipe)
    $authBinding        = @(Test-RecipeAuthProfileBinding -Recipe $Recipe)
    $execMatrix         = @(Test-RecipeExecutionModeAuthMatrix -Recipe $Recipe)
    $secretShape        = @(Test-RecipeExtraArgumentsSecretShape -Recipe $Recipe)
    $factOutputMode     = @(Test-RecipeFactOutputMode -Recipe $Recipe)
    $userInfoOutputMode = @(Test-RecipeUserInfoOutputMode -Recipe $Recipe)
    $queryShape         = @(Test-RecipeQueryShape -Recipe $Recipe)
    $activityRollup     = @(Test-RecipeActivityTypesUnderRollup -Recipe $Recipe)
    $m365UsageGate      = @(Test-RecipeM365UsageGate -Recipe $Recipe)
    $userInfoChannel    = @(Test-RecipeUserInfoChannelGate -Recipe $Recipe)
    $agentFilterShape   = @(Test-RecipeAgentFilterShape -Recipe $Recipe)
    $unsupportedSw      = @(Test-RecipeExtraArgumentsUnsupportedSwitches -Recipe $Recipe)
    $structurallyOwned  = @(Test-RecipeExtraArgumentsStructurallyOwned -Recipe $Recipe)
    $rollupBlock        = @(Test-RecipeRollupBlockers -Recipe $Recipe)
    $all = New-Object System.Collections.Generic.List[object]
    foreach ($e in $schemaResult.errors) { $all.Add($e) }
    foreach ($e in $tierErrors)          { $all.Add($e) }
    foreach ($e in $rangeErrors)         { $all.Add($e) }
    foreach ($e in $removedSw)           { $all.Add($e) }
    foreach ($e in $authBinding)         { $all.Add($e) }
    foreach ($e in $execMatrix)          { $all.Add($e) }
    foreach ($e in $secretShape)         { $all.Add($e) }
    foreach ($e in $factOutputMode)      { $all.Add($e) }
    foreach ($e in $userInfoOutputMode)  { $all.Add($e) }
    foreach ($e in $queryShape)          { $all.Add($e) }
    foreach ($e in $activityRollup)      { $all.Add($e) }
    foreach ($e in $m365UsageGate)       { $all.Add($e) }
    foreach ($e in $userInfoChannel)     { $all.Add($e) }
    foreach ($e in $agentFilterShape)    { $all.Add($e) }
    foreach ($e in $unsupportedSw)       { $all.Add($e) }
    foreach ($e in $structurallyOwned)   { $all.Add($e) }
    foreach ($e in $rollupBlock)         { $all.Add($e) }
    return @{ ok = ($all.Count -eq 0); errors = @($all.ToArray()) }
}
