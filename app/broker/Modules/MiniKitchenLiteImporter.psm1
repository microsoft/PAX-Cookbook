Set-StrictMode -Version Latest

# MiniKitchenLiteImporter.psm1
#
# Validator + structural mapper for the Mini-Kitchen lite recipe
# envelope ("pax-cookbook-mini-recipe" / schemaVersion "1.0"). Pure
# helper module: no I/O, no broker routes, no external services, no
# PAX invocation, no SQLite.
#
# Imports the existing RecipeTakeoutSanitizer.psm1 to reuse the
# canonical Cookbook secret-name / secret-value scanners. Mini-Kitchen
# lite envelopes are subject to the same defense-in-depth scans as
# Full Cookbook Recipe Takeout envelopes; the lite contract says auth
# does not carry clientSecret, but the broker still treats the
# envelope as untrusted JSON crossing a security boundary and runs
# the full scan.
#
# Authority:
#   - Canonical Mini-Kitchen lite contract (kind, schemaVersion,
#     compatibility, importBehavior, recipe.* enums, auth shape).
#   - Cookbook recipe schema in Routes\RecipeValidator.ps1 (target
#     of the structural mapping).
#   - User decisions L2 / B3: lite imports become Cookbook drafts in
#     Needs Prep state; the broker is the single source of truth for
#     destination shape; no auto-execution under any condition.

Import-Module -Force (Join-Path $PSScriptRoot 'RecipeTakeoutSanitizer.psm1')

# ---------------------------------------------------------
# Constants
# ---------------------------------------------------------

$Script:LiteKindConstant            = 'pax-cookbook-mini-recipe'
$Script:LiteSchemaVersionConstant   = '1.0'
$Script:LiteCookbookSchemaVersion   = 1
$Script:LiteImportBehaviorState     = 'needsPrep'

# Mini-Kitchen lite enums. These mirror the canonical contract and
# are validated verbatim.
$Script:LiteAuthModes = @(
    'WebLogin','DeviceCode','AppRegistrationSecret',
    'AppRegistrationCertificate','ManagedIdentity'
)
$Script:LiteExecutionModes = @(
    'local-manual','local-scheduled','fabric-hosted','azure-hosted'
)
$Script:LiteQueryModes        = @('audit-query','user-info-only')
$Script:LiteRollupValues      = @('rollup','rollup-plus-raw')
$Script:LiteDestinationTiers  = @('local','sharepoint','fabric')
$Script:LiteDestinationModes  = @('write-new','append')
$Script:LiteAgentFilterModes  = @('none','agentIds','agentsOnly','excludeAgents')
$Script:LitePromptFilterValues = @('Prompt','Response','Both','Null')

# Auth modes that require a Chef's Key binding in Cookbook. Mirrors
# the constant in RecipeTakeout.ps1 / RecipeTakeoutImporter.psm1.
$Script:LiteAppRegistrationModes = @(
    'AppRegistrationSecret',
    'AppRegistrationCertificate'
)

# Tenant ID UUID pattern (Cookbook recipe.auth.tenantId requirement).
$Script:LiteTenantIdPattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'

# Output path rejection rules — keep aligned with the broker's
# Routes\RecipeValidator.ps1 $Script:OutputPathRejectRules. Local
# duplicate so this module remains independent of the route file's
# load order.
$Script:LiteOutputPathRejectPatterns = @(
    '(?i)^abfss://',
    '(?i)^onelake://',
    '(?i)\.onelake\.',
    '(?i)fabric\.microsoft\.com'
)

# ---------------------------------------------------------
# Defensive deep-copy
# ---------------------------------------------------------

function ConvertTo-LiteHashtable {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [hashtable]) {
        $copy = @{}
        foreach ($k in $Value.Keys) {
            $copy[[string]$k] = ConvertTo-LiteHashtable -Value $Value[$k]
        }
        return $copy
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $copy = @{}
        foreach ($k in $Value.Keys) {
            $copy[[string]$k] = ConvertTo-LiteHashtable -Value $Value[$k]
        }
        return $copy
    }
    if ($Value -is [System.Management.Automation.PSCustomObject] -or
        $Value -is [pscustomobject]) {
        $copy = @{}
        foreach ($p in $Value.PSObject.Properties) {
            $copy[[string]$p.Name] = ConvertTo-LiteHashtable -Value $p.Value
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
            [void]$arr.Add( (ConvertTo-LiteHashtable -Value $item) )
        }
        return ,@($arr.ToArray())
    }
    return [string]$Value
}

# ---------------------------------------------------------
# Validator
# ---------------------------------------------------------

function Add-LiteValidationError {
    param(
        [System.Collections.IList]$Errors,
        [string]$Path,
        [string]$Message
    )
    [void]$Errors.Add(@{ path = $Path; message = $Message })
}

function Test-LiteIsString {
    param($Value)
    return ($null -ne $Value -and $Value -is [string])
}

function Test-LiteIsBool {
    param($Value)
    return ($null -ne $Value -and $Value -is [bool])
}

function Test-LiteIsObject {
    param($Value)
    return ($null -ne $Value -and ($Value -is [System.Collections.IDictionary]))
}

function Test-LiteIsArray {
    param($Value)
    if ($null -eq $Value) { return $false }
    if ($Value -is [string]) { return $false }
    return ($Value -is [System.Collections.IEnumerable])
}

function Test-LiteIsInt {
    param($Value)
    if ($null -eq $Value) { return $false }
    if ($Value -is [bool]) { return $false }
    return ($Value -is [int] -or $Value -is [long] -or $Value -is [int64])
}

function Test-MiniKitchenLiteEnvelope {
    # Returns @{ ok = $bool; errors = @(@{path; message}, ...) }.
    # Validates structural shape, enums, and known field types against
    # the canonical Mini-Kitchen lite contract. Does NOT scan for
    # secret-shaped values or forbidden field names -- the route
    # layer runs those separately via the existing Cookbook scanners
    # so defense-in-depth ordering matches recipe-takeout/validate.
    param($Envelope)

    $errs = New-Object System.Collections.ArrayList

    if (-not (Test-LiteIsObject -Value $Envelope)) {
        Add-LiteValidationError -Errors $errs -Path '/' -Message 'must be an object'
        return @{ ok = $false; errors = @($errs.ToArray()) }
    }

    # ---- kind ----
    if (-not $Envelope.Contains('kind')) {
        Add-LiteValidationError -Errors $errs -Path '/kind' -Message 'missing required property'
    } elseif (-not (Test-LiteIsString -Value $Envelope['kind'])) {
        Add-LiteValidationError -Errors $errs -Path '/kind' -Message 'must be a string'
    } elseif ([string]$Envelope['kind'] -ne $Script:LiteKindConstant) {
        Add-LiteValidationError -Errors $errs -Path '/kind' -Message ("must equal '{0}'" -f $Script:LiteKindConstant)
    }

    # ---- schemaVersion (STRING, not number) ----
    if (-not $Envelope.Contains('schemaVersion')) {
        Add-LiteValidationError -Errors $errs -Path '/schemaVersion' -Message 'missing required property'
    } elseif (-not (Test-LiteIsString -Value $Envelope['schemaVersion'])) {
        Add-LiteValidationError -Errors $errs -Path '/schemaVersion' -Message 'must be a string'
    } elseif ([string]$Envelope['schemaVersion'] -ne $Script:LiteSchemaVersionConstant) {
        Add-LiteValidationError -Errors $errs -Path '/schemaVersion' -Message ("must equal '{0}'" -f $Script:LiteSchemaVersionConstant)
    }

    # ---- compatibility (optional, but if present cookbookRecipeSchemaVersion must equal 1 number) ----
    if ($Envelope.Contains('compatibility')) {
        $compat = $Envelope['compatibility']
        if (-not (Test-LiteIsObject -Value $compat)) {
            Add-LiteValidationError -Errors $errs -Path '/compatibility' -Message 'must be an object'
        } elseif ($compat.Contains('cookbookRecipeSchemaVersion')) {
            $v = $compat['cookbookRecipeSchemaVersion']
            if (-not (Test-LiteIsInt -Value $v)) {
                Add-LiteValidationError -Errors $errs -Path '/compatibility/cookbookRecipeSchemaVersion' -Message 'must be an integer'
            } elseif ([int]$v -ne $Script:LiteCookbookSchemaVersion) {
                Add-LiteValidationError -Errors $errs -Path '/compatibility/cookbookRecipeSchemaVersion' -Message ("must equal {0}" -f $Script:LiteCookbookSchemaVersion)
            }
        }
    }

    # ---- importBehavior (optional, but state if present must be 'needsPrep') ----
    if ($Envelope.Contains('importBehavior')) {
        $ib = $Envelope['importBehavior']
        if (-not (Test-LiteIsObject -Value $ib)) {
            Add-LiteValidationError -Errors $errs -Path '/importBehavior' -Message 'must be an object'
        } else {
            if ($ib.Contains('state')) {
                if (-not (Test-LiteIsString -Value $ib['state'])) {
                    Add-LiteValidationError -Errors $errs -Path '/importBehavior/state' -Message 'must be a string'
                } elseif ([string]$ib['state'] -ne $Script:LiteImportBehaviorState) {
                    Add-LiteValidationError -Errors $errs -Path '/importBehavior/state' -Message ("must equal '{0}'" -f $Script:LiteImportBehaviorState)
                }
            }
            if ($ib.Contains('openInPrepStation') -and -not (Test-LiteIsBool -Value $ib['openInPrepStation'])) {
                Add-LiteValidationError -Errors $errs -Path '/importBehavior/openInPrepStation' -Message 'must be a boolean'
            }
        }
    }

    # ---- recipe (required object) ----
    if (-not $Envelope.Contains('recipe')) {
        Add-LiteValidationError -Errors $errs -Path '/recipe' -Message 'missing required property'
        return @{ ok = ($errs.Count -eq 0); errors = @($errs.ToArray()) }
    }
    $recipe = $Envelope['recipe']
    if (-not (Test-LiteIsObject -Value $recipe)) {
        Add-LiteValidationError -Errors $errs -Path '/recipe' -Message 'must be an object'
        return @{ ok = ($errs.Count -eq 0); errors = @($errs.ToArray()) }
    }

    # ---- recipe.identity.name ----
    if (-not $recipe.Contains('identity')) {
        Add-LiteValidationError -Errors $errs -Path '/recipe/identity' -Message 'missing required property'
    } else {
        $identity = $recipe['identity']
        if (-not (Test-LiteIsObject -Value $identity)) {
            Add-LiteValidationError -Errors $errs -Path '/recipe/identity' -Message 'must be an object'
        } else {
            if (-not $identity.Contains('name')) {
                Add-LiteValidationError -Errors $errs -Path '/recipe/identity/name' -Message 'missing required property'
            } elseif (-not (Test-LiteIsString -Value $identity['name'])) {
                Add-LiteValidationError -Errors $errs -Path '/recipe/identity/name' -Message 'must be a string'
            } elseif ([string]::IsNullOrWhiteSpace([string]$identity['name'])) {
                Add-LiteValidationError -Errors $errs -Path '/recipe/identity/name' -Message 'must be a non-empty string'
            } elseif (([string]$identity['name']).Length -gt 200) {
                Add-LiteValidationError -Errors $errs -Path '/recipe/identity/name' -Message 'must be 200 characters or fewer'
            }
            if ($identity.Contains('description') -and -not (Test-LiteIsString -Value $identity['description'])) {
                Add-LiteValidationError -Errors $errs -Path '/recipe/identity/description' -Message 'must be a string'
            }
            if ($identity.Contains('tags')) {
                if (-not (Test-LiteIsArray -Value $identity['tags'])) {
                    Add-LiteValidationError -Errors $errs -Path '/recipe/identity/tags' -Message 'must be an array'
                } else {
                    $i = 0
                    foreach ($t in $identity['tags']) {
                        if (-not (Test-LiteIsString -Value $t)) {
                            Add-LiteValidationError -Errors $errs -Path ("/recipe/identity/tags/{0}" -f $i) -Message 'must be a string'
                        }
                        $i++
                    }
                }
            }
        }
    }

    # ---- recipe.query (optional object; field-level checks if present) ----
    if ($recipe.Contains('query')) {
        $query = $recipe['query']
        if (-not (Test-LiteIsObject -Value $query)) {
            Add-LiteValidationError -Errors $errs -Path '/recipe/query' -Message 'must be an object'
        } else {
            if ($query.Contains('mode')) {
                if (-not (Test-LiteIsString -Value $query['mode'])) {
                    Add-LiteValidationError -Errors $errs -Path '/recipe/query/mode' -Message 'must be a string'
                } elseif ($Script:LiteQueryModes -notcontains [string]$query['mode']) {
                    Add-LiteValidationError -Errors $errs -Path '/recipe/query/mode' -Message ("must be one of: {0}" -f ($Script:LiteQueryModes -join ', '))
                }
            }
            foreach ($df in @('startDate','endDate')) {
                if ($query.Contains($df) -and -not (Test-LiteIsString -Value $query[$df])) {
                    Add-LiteValidationError -Errors $errs -Path ("/recipe/query/{0}" -f $df) -Message 'must be a string'
                }
            }
            foreach ($bf in @('includeM365Usage','excludeCopilotInteraction','includeUserInfo','onlyUserInfo')) {
                if ($query.Contains($bf) -and -not (Test-LiteIsBool -Value $query[$bf])) {
                    Add-LiteValidationError -Errors $errs -Path ("/recipe/query/{0}" -f $bf) -Message 'must be a boolean'
                }
            }
        }
    }

    # ---- recipe.processing (optional object; enum checks if present) ----
    if ($recipe.Contains('processing')) {
        $proc = $recipe['processing']
        if (-not (Test-LiteIsObject -Value $proc)) {
            Add-LiteValidationError -Errors $errs -Path '/recipe/processing' -Message 'must be an object'
        } else {
            if ($proc.Contains('rollup')) {
                if (-not (Test-LiteIsString -Value $proc['rollup'])) {
                    Add-LiteValidationError -Errors $errs -Path '/recipe/processing/rollup' -Message 'must be a string'
                } elseif ($Script:LiteRollupValues -notcontains [string]$proc['rollup']) {
                    Add-LiteValidationError -Errors $errs -Path '/recipe/processing/rollup' -Message ("must be one of: {0}" -f ($Script:LiteRollupValues -join ', '))
                }
            }
            foreach ($af in @('activityTypes','userIds','groupNames')) {
                if ($proc.Contains($af)) {
                    if (-not (Test-LiteIsArray -Value $proc[$af])) {
                        Add-LiteValidationError -Errors $errs -Path ("/recipe/processing/{0}" -f $af) -Message 'must be an array'
                    } else {
                        $i = 0
                        foreach ($x in $proc[$af]) {
                            if (-not (Test-LiteIsString -Value $x)) {
                                Add-LiteValidationError -Errors $errs -Path ("/recipe/processing/{0}/{1}" -f $af, $i) -Message 'must be a string'
                            }
                            $i++
                        }
                    }
                }
            }
            if ($proc.Contains('agentFilter')) {
                $af = $proc['agentFilter']
                if (-not (Test-LiteIsObject -Value $af)) {
                    Add-LiteValidationError -Errors $errs -Path '/recipe/processing/agentFilter' -Message 'must be an object'
                } else {
                    if ($af.Contains('mode')) {
                        if (-not (Test-LiteIsString -Value $af['mode'])) {
                            Add-LiteValidationError -Errors $errs -Path '/recipe/processing/agentFilter/mode' -Message 'must be a string'
                        } elseif ($Script:LiteAgentFilterModes -notcontains [string]$af['mode']) {
                            Add-LiteValidationError -Errors $errs -Path '/recipe/processing/agentFilter/mode' -Message ("must be one of: {0}" -f ($Script:LiteAgentFilterModes -join ', '))
                        }
                    }
                    if ($af.Contains('agentIds')) {
                        if (-not (Test-LiteIsArray -Value $af['agentIds'])) {
                            Add-LiteValidationError -Errors $errs -Path '/recipe/processing/agentFilter/agentIds' -Message 'must be an array'
                        }
                    }
                }
            }
            if ($proc.Contains('promptFilter')) {
                if (-not (Test-LiteIsString -Value $proc['promptFilter'])) {
                    Add-LiteValidationError -Errors $errs -Path '/recipe/processing/promptFilter' -Message 'must be a string'
                } elseif ($Script:LitePromptFilterValues -notcontains [string]$proc['promptFilter']) {
                    Add-LiteValidationError -Errors $errs -Path '/recipe/processing/promptFilter' -Message ("must be one of: {0}" -f ($Script:LitePromptFilterValues -join ', '))
                }
            }
        }
    }

    # ---- recipe.destinations (optional object; nested enum checks) ----
    if ($recipe.Contains('destinations')) {
        $dests = $recipe['destinations']
        if (-not (Test-LiteIsObject -Value $dests)) {
            Add-LiteValidationError -Errors $errs -Path '/recipe/destinations' -Message 'must be an object'
        } else {
            foreach ($channel in @('fact','userInfo')) {
                if ($dests.Contains($channel)) {
                    $d = $dests[$channel]
                    if (-not (Test-LiteIsObject -Value $d)) {
                        Add-LiteValidationError -Errors $errs -Path ("/recipe/destinations/{0}" -f $channel) -Message 'must be an object'
                    } else {
                        if ($d.Contains('mode')) {
                            if (-not (Test-LiteIsString -Value $d['mode'])) {
                                Add-LiteValidationError -Errors $errs -Path ("/recipe/destinations/{0}/mode" -f $channel) -Message 'must be a string'
                            } elseif ($Script:LiteDestinationModes -notcontains [string]$d['mode']) {
                                Add-LiteValidationError -Errors $errs -Path ("/recipe/destinations/{0}/mode" -f $channel) -Message ("must be one of: {0}" -f ($Script:LiteDestinationModes -join ', '))
                            }
                        }
                        if ($d.Contains('tier')) {
                            if (-not (Test-LiteIsString -Value $d['tier'])) {
                                Add-LiteValidationError -Errors $errs -Path ("/recipe/destinations/{0}/tier" -f $channel) -Message 'must be a string'
                            } elseif ($Script:LiteDestinationTiers -notcontains [string]$d['tier']) {
                                Add-LiteValidationError -Errors $errs -Path ("/recipe/destinations/{0}/tier" -f $channel) -Message ("must be one of: {0}" -f ($Script:LiteDestinationTiers -join ', '))
                            }
                        }
                        if ($d.Contains('path') -and -not (Test-LiteIsString -Value $d['path'])) {
                            Add-LiteValidationError -Errors $errs -Path ("/recipe/destinations/{0}/path" -f $channel) -Message 'must be a string'
                        }
                    }
                }
            }
        }
    }

    # ---- recipe.auth (optional object; clientSecret FORBIDDEN by contract) ----
    if ($recipe.Contains('auth')) {
        $auth = $recipe['auth']
        if (-not (Test-LiteIsObject -Value $auth)) {
            Add-LiteValidationError -Errors $errs -Path '/recipe/auth' -Message 'must be an object'
        } else {
            if ($auth.Contains('clientSecret')) {
                Add-LiteValidationError -Errors $errs -Path '/recipe/auth/clientSecret' -Message 'forbidden by Mini-Kitchen lite contract'
            }
            if ($auth.Contains('mode')) {
                if (-not (Test-LiteIsString -Value $auth['mode'])) {
                    Add-LiteValidationError -Errors $errs -Path '/recipe/auth/mode' -Message 'must be a string'
                } elseif ($Script:LiteAuthModes -notcontains [string]$auth['mode']) {
                    Add-LiteValidationError -Errors $errs -Path '/recipe/auth/mode' -Message ("must be one of: {0}" -f ($Script:LiteAuthModes -join ', '))
                }
            }
            foreach ($sf in @('tenantId','clientId','certificateThumbprint')) {
                if ($auth.Contains($sf) -and -not (Test-LiteIsString -Value $auth[$sf])) {
                    Add-LiteValidationError -Errors $errs -Path ("/recipe/auth/{0}" -f $sf) -Message 'must be a string'
                }
            }
        }
    }

    # ---- recipe.executionMode (optional string with enum) ----
    if ($recipe.Contains('executionMode')) {
        if (-not (Test-LiteIsString -Value $recipe['executionMode'])) {
            Add-LiteValidationError -Errors $errs -Path '/recipe/executionMode' -Message 'must be a string'
        } elseif ($Script:LiteExecutionModes -notcontains [string]$recipe['executionMode']) {
            Add-LiteValidationError -Errors $errs -Path '/recipe/executionMode' -Message ("must be one of: {0}" -f ($Script:LiteExecutionModes -join ', '))
        }
    }

    # ---- recipe.advanced.extraArguments (optional string) ----
    if ($recipe.Contains('advanced')) {
        $adv = $recipe['advanced']
        if (-not (Test-LiteIsObject -Value $adv)) {
            Add-LiteValidationError -Errors $errs -Path '/recipe/advanced' -Message 'must be an object'
        } elseif ($adv.Contains('extraArguments') -and -not (Test-LiteIsString -Value $adv['extraArguments'])) {
            Add-LiteValidationError -Errors $errs -Path '/recipe/advanced/extraArguments' -Message 'must be a string'
        }
    }

    # ---- commandPreview / permissions / createdBy (optional metadata) ----
    if ($Envelope.Contains('commandPreview') -and -not (Test-LiteIsString -Value $Envelope['commandPreview'])) {
        Add-LiteValidationError -Errors $errs -Path '/commandPreview' -Message 'must be a string'
    }
    if ($Envelope.Contains('permissions')) {
        if (-not (Test-LiteIsArray -Value $Envelope['permissions'])) {
            Add-LiteValidationError -Errors $errs -Path '/permissions' -Message 'must be an array'
        } else {
            $i = 0
            foreach ($p in $Envelope['permissions']) {
                if (-not (Test-LiteIsString -Value $p)) {
                    Add-LiteValidationError -Errors $errs -Path ("/permissions/{0}" -f $i) -Message 'must be a string'
                }
                $i++
            }
        }
    }
    if ($Envelope.Contains('createdBy') -and -not (Test-LiteIsObject -Value $Envelope['createdBy'])) {
        Add-LiteValidationError -Errors $errs -Path '/createdBy' -Message 'must be an object'
    }

    return @{ ok = ($errs.Count -eq 0); errors = @($errs.ToArray()) }
}

# ---------------------------------------------------------
# Resolve target name (Windows-style numeric suffix walker, mirrors
# Resolve-RecipeTakeoutTargetName so lite imports use the same
# collision-resolution rule).
# ---------------------------------------------------------

function Resolve-MiniKitchenLiteTargetName {
    param(
        [string]$ProposedName,
        [string[]]$ExistingNames
    )
    $proposed = if ($null -eq $ProposedName) { '' } else { $ProposedName.Trim() }
    $names    = @()
    if ($null -ne $ExistingNames) { $names = @($ExistingNames) }

    $hasCollision = $false
    foreach ($n in $names) {
        if ([string]::IsNullOrEmpty([string]$n)) { continue }
        if ([string]::Equals(([string]$n).Trim(), $proposed, [System.StringComparison]::OrdinalIgnoreCase)) {
            $hasCollision = $true
            break
        }
    }
    if (-not $hasCollision) {
        return @{ resolved = $true; name = $proposed }
    }
    for ($i = 1; $i -le 99; $i++) {
        $candidate = ("{0} ({1})" -f $proposed, $i)
        $clash = $false
        foreach ($n in $names) {
            if ([string]::IsNullOrEmpty([string]$n)) { continue }
            if ([string]::Equals(([string]$n).Trim(), $candidate, [System.StringComparison]::OrdinalIgnoreCase)) {
                $clash = $true
                break
            }
        }
        if (-not $clash) {
            return @{ resolved = $true; name = $candidate }
        }
    }
    return @{ resolved = $false; name = $null }
}

# ---------------------------------------------------------
# Path classification (warn-only)
# ---------------------------------------------------------

function Test-LitePathIsRejectableByCookbook {
    # Returns $true when a path matches one of the Cookbook output-
    # path reject patterns (OneLake / Fabric). Used to decide whether
    # to surface a warning and drop the path so the chef must set a
    # local path in Prep Station before saving.
    param([string]$PathValue)
    if ([string]::IsNullOrWhiteSpace($PathValue)) { return $false }
    foreach ($pat in $Script:LiteOutputPathRejectPatterns) {
        if ([regex]::IsMatch($PathValue, $pat)) { return $true }
    }
    return $false
}

# ---------------------------------------------------------
# Field-level mappers
# ---------------------------------------------------------

function ConvertTo-CookbookQueryMode {
    param([string]$LiteMode)
    if ($LiteMode -eq 'audit-query')    { return 'audit' }
    if ($LiteMode -eq 'user-info-only') { return 'userInfoOnly' }
    return $null
}

function ConvertTo-CookbookRollup {
    param([string]$LiteRollup)
    if ($LiteRollup -eq 'rollup')          { return 'Rollup' }
    if ($LiteRollup -eq 'rollup-plus-raw') { return 'RollupPlusRaw' }
    return $null
}

function ConvertTo-CookbookDestinationMode {
    param([string]$LiteMode)
    if ($LiteMode -eq 'write-new') { return 'outputPath' }
    if ($LiteMode -eq 'append')    { return 'append' }
    return $null
}

function New-LiteWarning {
    param(
        [string]$Code,
        [string]$Path,
        [string]$Detail
    )
    $w = [ordered]@{ code = $Code; origin = 'lite-import' }
    if (-not [string]::IsNullOrEmpty($Path))   { $w['path']   = $Path }
    if (-not [string]::IsNullOrEmpty($Detail)) { $w['detail'] = $Detail }
    return $w
}

# ---------------------------------------------------------
# Map Mini-Kitchen lite envelope -> Cookbook recipe draft
# ---------------------------------------------------------

function New-CookbookDraftFromMiniKitchenLiteEnvelope {
    # Pure transform. Returns:
    #   @{ recipe       = <Cookbook recipe hashtable>
    #      needsChefKey = $bool
    #      chefKeyMode  = $string|$null
    #      warnings     = @( {code, origin, path?, detail?}, ... )
    #      preview      = @{ commandPreview, permissions, compatibility,
    #                         miniKitchenCreatedBy, importBehavior }
    #    }
    #
    # The recipe is a structural draft. It is INTENTIONALLY missing
    # fields where the lite envelope does not carry them (e.g.
    # processing.rollup for an audit-mode recipe without a rollup
    # selection, destinations.fact.path for a remote-tier source)
    # so the Cookbook recipe validator surfaces them as Needs Prep
    # gates the next time the chef saves the recipe. We never
    # invent reasonable defaults for a missing structural field.
    #
    # The 'preview' bundle is returned to the validate / import HTTP
    # response so the SPA banner can show import provenance without
    # an extra round-trip. The same provenance is ALSO persisted on
    # the recipe under recipe.importMetadata (a schema-defined
    # OPTIONAL root bag with additionalProperties=false at every
    # level) so it survives the validate/import response into the
    # recipe file and SQLite-backed list, and is preserved verbatim
    # across chef edits by Invoke-RecipeUpdate. The 'preview' bundle
    # is intentionally informational-only: it does not gate Needs
    # Prep, does not bind a Chef's Key, and does not affect the cook.
    param(
        $Envelope,
        [datetime]$NowUtc,
        [string]$NewRecipeId,
        [string]$CookbookVersion,
        [string]$BundledPaxVersion,
        [string]$ReleaseChannel
    )

    $env      = ConvertTo-LiteHashtable -Value $Envelope
    $warnings = New-Object System.Collections.ArrayList

    if (-not (Test-LiteIsObject -Value $env) -or
        -not $env.Contains('recipe') -or
        -not (Test-LiteIsObject -Value $env['recipe'])) {
        throw 'envelope is not a valid Mini-Kitchen lite envelope'
    }
    $src    = $env['recipe']
    $recipe = @{}

    # ---- top-level required Cookbook scalars ----
    $recipe['recipeId']            = $NewRecipeId
    $recipe['recipeSchemaVersion'] = 1
    $recipe['paxAdapterVersion']   = $BundledPaxVersion
    $recipe['createdAt']           = $NowUtc.ToString('o')
    $recipe['updatedAt']           = $NowUtc.ToString('o')
    $recipe['createdBy']           = @{
        cookbookVersion   = $CookbookVersion
        bundledPaxVersion = $BundledPaxVersion
        releaseChannel    = $ReleaseChannel
    }

    # ---- identity (lite description and tags discarded with warning) ----
    $identityName = $null
    if ($src.Contains('identity') -and (Test-LiteIsObject -Value $src['identity']) -and
        $src['identity'].Contains('name')) {
        $identityName = [string]$src['identity']['name']
    }
    $recipe['identity'] = @{ name = $identityName }
    if ($src.Contains('identity') -and (Test-LiteIsObject -Value $src['identity'])) {
        if ($src['identity'].Contains('description')) {
            [void]$warnings.Add( (New-LiteWarning -Code 'lite_field_discarded' -Path '/recipe/identity/description' -Detail 'Cookbook identity has no description slot; field discarded') )
        }
        if ($src['identity'].Contains('tags')) {
            [void]$warnings.Add( (New-LiteWarning -Code 'lite_field_discarded' -Path '/recipe/identity/tags' -Detail 'Cookbook identity has no tags slot; field discarded') )
        }
    }

    # ---- ingredients (assembled from lite query.* booleans) ----
    $liteQuery     = if ($src.Contains('query')      -and (Test-LiteIsObject -Value $src['query']))      { $src['query'] }      else { @{} }
    $liteProcess   = if ($src.Contains('processing') -and (Test-LiteIsObject -Value $src['processing'])) { $src['processing'] } else { @{} }
    $liteDests     = if ($src.Contains('destinations') -and (Test-LiteIsObject -Value $src['destinations'])) { $src['destinations'] } else { @{} }
    $liteAuth      = if ($src.Contains('auth')       -and (Test-LiteIsObject -Value $src['auth']))       { $src['auth'] }       else { @{} }
    $liteAdvanced  = if ($src.Contains('advanced')   -and (Test-LiteIsObject -Value $src['advanced']))   { $src['advanced'] }   else { @{} }

    $includeM365 = $false
    if ($liteQuery.Contains('includeM365Usage') -and (Test-LiteIsBool -Value $liteQuery['includeM365Usage'])) {
        $includeM365 = [bool]$liteQuery['includeM365Usage']
    }
    $m365 = @{ includeM365Usage = $includeM365 }
    if ($liteQuery.Contains('excludeCopilotInteraction') -and (Test-LiteIsBool -Value $liteQuery['excludeCopilotInteraction'])) {
        if ([bool]$liteQuery['excludeCopilotInteraction']) {
            $m365['includeCopilotInteraction'] = $false
        }
    }

    $includeUserInfo = $false
    if ($liteQuery.Contains('includeUserInfo') -and (Test-LiteIsBool -Value $liteQuery['includeUserInfo'])) {
        $includeUserInfo = [bool]$liteQuery['includeUserInfo']
    } elseif ($liteQuery.Contains('onlyUserInfo') -and (Test-LiteIsBool -Value $liteQuery['onlyUserInfo'])) {
        $includeUserInfo = [bool]$liteQuery['onlyUserInfo']
    }

    $recipe['ingredients'] = @{
        m365Usage     = $m365
        entraUserData = @{ includeUserInfo = $includeUserInfo }
    }

    # ---- query ----
    $query = @{}
    if ($liteQuery.Contains('mode') -and (Test-LiteIsString -Value $liteQuery['mode'])) {
        $cm = ConvertTo-CookbookQueryMode -LiteMode ([string]$liteQuery['mode'])
        if ($null -ne $cm) { $query['mode'] = $cm }
    }
    foreach ($df in @('startDate','endDate')) {
        if ($liteQuery.Contains($df) -and (Test-LiteIsString -Value $liteQuery[$df])) {
            $query[$df] = [string]$liteQuery[$df]
        }
    }
    foreach ($af in @('activityTypes','userIds','groupNames')) {
        if ($liteProcess.Contains($af) -and (Test-LiteIsArray -Value $liteProcess[$af])) {
            $arr = New-Object System.Collections.ArrayList
            foreach ($x in $liteProcess[$af]) {
                if (Test-LiteIsString -Value $x) { [void]$arr.Add([string]$x) }
            }
            if ($arr.Count -gt 0) { $query[$af] = @($arr.ToArray()) }
        }
    }
    if ($liteProcess.Contains('agentFilter') -and (Test-LiteIsObject -Value $liteProcess['agentFilter'])) {
        $afSrc = $liteProcess['agentFilter']
        $afDst = @{}
        if ($afSrc.Contains('mode') -and (Test-LiteIsString -Value $afSrc['mode'])) {
            $afDst['mode'] = [string]$afSrc['mode']
        }
        if ($afSrc.Contains('agentIds') -and (Test-LiteIsArray -Value $afSrc['agentIds'])) {
            $arr = New-Object System.Collections.ArrayList
            foreach ($x in $afSrc['agentIds']) {
                if (Test-LiteIsString -Value $x) { [void]$arr.Add([string]$x) }
            }
            if ($arr.Count -gt 0) { $afDst['agentIds'] = @($arr.ToArray()) }
        }
        if ($afDst.Count -gt 0 -and $afDst.ContainsKey('mode')) {
            $query['agentFilter'] = $afDst
        }
    }
    if ($liteProcess.Contains('promptFilter') -and (Test-LiteIsString -Value $liteProcess['promptFilter'])) {
        $query['promptFilter'] = [string]$liteProcess['promptFilter']
    }
    $recipe['query'] = $query

    # ---- processing.rollup ----
    if ($liteProcess.Contains('rollup') -and (Test-LiteIsString -Value $liteProcess['rollup'])) {
        $cr = ConvertTo-CookbookRollup -LiteRollup ([string]$liteProcess['rollup'])
        if ($null -ne $cr) {
            $recipe['processing'] = @{ rollup = $cr }
        } else {
            $recipe['processing'] = @{}
        }
    } else {
        $recipe['processing'] = @{}
    }

    # ---- destinations ----
    $dests = @{}
    foreach ($channel in @('fact','userInfo')) {
        if (-not $liteDests.Contains($channel)) { continue }
        $d = $liteDests[$channel]
        if (-not (Test-LiteIsObject -Value $d)) { continue }
        $dst  = @{}
        $tier = $null
        if ($d.Contains('tier') -and (Test-LiteIsString -Value $d['tier'])) { $tier = [string]$d['tier'] }
        $mode = $null
        if ($d.Contains('mode') -and (Test-LiteIsString -Value $d['mode'])) {
            $mode = ConvertTo-CookbookDestinationMode -LiteMode ([string]$d['mode'])
        }
        $path = $null
        if ($d.Contains('path') -and (Test-LiteIsString -Value $d['path'])) { $path = [string]$d['path'] }

        if ($null -ne $mode) { $dst['mode'] = $mode }

        if ($tier -eq 'local' -and -not [string]::IsNullOrWhiteSpace($path)) {
            if (Test-LitePathIsRejectableByCookbook -PathValue $path) {
                [void]$warnings.Add( (New-LiteWarning -Code 'lite_path_rejected_by_cookbook' -Path ("/recipe/destinations/{0}/path" -f $channel) -Detail 'path matches a destination tier Cookbook v1 does not support; remove or replace in Prep Station') )
            } else {
                if ($mode -eq 'append') {
                    $dst['appendFile'] = $path
                } else {
                    $dst['path'] = $path
                }
            }
        } elseif ($tier -eq 'sharepoint' -or $tier -eq 'fabric') {
            [void]$warnings.Add( (New-LiteWarning -Code 'lite_destination_tier_not_supported' -Path ("/recipe/destinations/{0}/tier" -f $channel) -Detail ("tier '{0}' is not supported by Cookbook v1; chef must set a local path in Prep Station" -f $tier)) )
        } elseif (-not [string]::IsNullOrWhiteSpace($path)) {
            # tier missing/unknown but path provided -- keep the path
            # only if it passes the Cookbook reject rules.
            if (-not (Test-LitePathIsRejectableByCookbook -PathValue $path)) {
                if ($mode -eq 'append') { $dst['appendFile'] = $path } else { $dst['path'] = $path }
            } else {
                [void]$warnings.Add( (New-LiteWarning -Code 'lite_path_rejected_by_cookbook' -Path ("/recipe/destinations/{0}/path" -f $channel) -Detail 'path matches a destination tier Cookbook v1 does not support; remove or replace in Prep Station') )
            }
        }
        if ($dst.Count -gt 0) {
            $dests[$channel] = $dst
        }
    }
    $recipe['destinations'] = $dests

    # ---- auth ----
    $auth = @{}
    $authMode = $null
    if ($liteAuth.Contains('mode') -and (Test-LiteIsString -Value $liteAuth['mode'])) {
        $authMode = [string]$liteAuth['mode']
        $auth['mode'] = $authMode
    }
    if ($liteAuth.Contains('tenantId') -and (Test-LiteIsString -Value $liteAuth['tenantId'])) {
        $tid = [string]$liteAuth['tenantId']
        if ($tid -match $Script:LiteTenantIdPattern) {
            $auth['tenantId'] = $tid
        } else {
            [void]$warnings.Add( (New-LiteWarning -Code 'lite_tenant_id_not_uuid' -Path '/recipe/auth/tenantId' -Detail 'tenantId is not a UUID; field discarded, chef must set tenant in Prep Station') )
        }
    }
    if ($liteAuth.Contains('clientId') -and (Test-LiteIsString -Value $liteAuth['clientId'])) {
        [void]$warnings.Add( (New-LiteWarning -Code 'lite_field_discarded' -Path '/recipe/auth/clientId' -Detail 'Cookbook stores clientId on the Chef Key, not the recipe; chef must bind a local Chef Key in Prep Station') )
    }
    if ($liteAuth.Contains('certificateThumbprint') -and (Test-LiteIsString -Value $liteAuth['certificateThumbprint'])) {
        [void]$warnings.Add( (New-LiteWarning -Code 'lite_field_discarded' -Path '/recipe/auth/certificateThumbprint' -Detail 'Cookbook stores certificate binding on the Chef Key, not the recipe; chef must bind a local Chef Key in Prep Station') )
    }
    # authProfileId is NEVER carried from a lite envelope. Even if a
    # rogue envelope tried to set it, the lite validator rejects
    # forbidden secret fields and the route layer strips it.
    $recipe['auth'] = $auth

    # ---- executionMode (1:1 when present) ----
    if ($src.Contains('executionMode') -and (Test-LiteIsString -Value $src['executionMode'])) {
        $em = [string]$src['executionMode']
        if ($Script:LiteExecutionModes -contains $em) {
            $recipe['executionMode'] = $em
        }
    }

    # ---- advanced.extraArguments (1:1 when present) ----
    if ($liteAdvanced.Contains('extraArguments') -and (Test-LiteIsString -Value $liteAdvanced['extraArguments'])) {
        $recipe['advanced'] = @{ extraArguments = [string]$liteAdvanced['extraArguments'] }
    }

    # ---- needsChefKey + chefKeyMode (App* modes require local binding) ----
    $needsChefKey = $false
    $chefKeyMode  = $null
    if ($null -ne $authMode -and $Script:LiteAppRegistrationModes -contains $authMode) {
        $needsChefKey = $true
        $chefKeyMode  = $authMode
    }

    # ---- preview metadata (return to HTTP response for the SPA banner) ----
    $preview = @{}
    if ($env.Contains('commandPreview') -and (Test-LiteIsString -Value $env['commandPreview'])) {
        $preview['commandPreview'] = [string]$env['commandPreview']
    }
    if ($env.Contains('permissions') -and (Test-LiteIsArray -Value $env['permissions'])) {
        $arr = New-Object System.Collections.ArrayList
        foreach ($p in $env['permissions']) {
            if (Test-LiteIsString -Value $p) { [void]$arr.Add([string]$p) }
        }
        $preview['permissions'] = @($arr.ToArray())
    }
    if ($env.Contains('compatibility') -and (Test-LiteIsObject -Value $env['compatibility'])) {
        $preview['compatibility'] = ConvertTo-LiteHashtable -Value $env['compatibility']
    }
    if ($env.Contains('createdBy') -and (Test-LiteIsObject -Value $env['createdBy'])) {
        $preview['miniKitchenCreatedBy'] = ConvertTo-LiteHashtable -Value $env['createdBy']
    }
    if ($env.Contains('importBehavior') -and (Test-LiteIsObject -Value $env['importBehavior'])) {
        $preview['importBehavior'] = ConvertTo-LiteHashtable -Value $env['importBehavior']
    }

    # ---- importMetadata (PERSISTED on recipe under additionalProperties=false bag) ----
    # Schema-tight subset of the lite envelope fields the Cookbook
    # recipe shape has no native slot for. Each sub-object below is
    # build piecewise so we only persist values that pass shape
    # checks; unrecognized envelope keys are dropped. The bag is
    # validated server-side by Test-RecipeAll on PUT and client-side
    # by AJV (recipe.schema.json) when the SPA loads the recipe.
    $importMetadata = @{
        source         = 'mini-kitchen-lite'
        importedAtUtc  = $NowUtc.ToString('o')
    }
    if ($env.Contains('kind') -and (Test-LiteIsString -Value $env['kind'])) {
        $importMetadata['originalKind'] = [string]$env['kind']
    }
    if ($env.Contains('schemaVersion') -and (Test-LiteIsString -Value $env['schemaVersion'])) {
        $importMetadata['originalSchemaVersion'] = [string]$env['schemaVersion']
    }

    # originalIdentity: preserve description + tags the Cookbook
    # identity slot rejects. Pass-through only what's a string / array
    # of strings; everything else is dropped.
    if ($src.Contains('identity') -and (Test-LiteIsObject -Value $src['identity'])) {
        $idBag = @{}
        $liteIdent = $src['identity']
        if ($liteIdent.Contains('description') -and (Test-LiteIsString -Value $liteIdent['description'])) {
            $idBag['description'] = [string]$liteIdent['description']
        }
        if ($liteIdent.Contains('tags') -and (Test-LiteIsArray -Value $liteIdent['tags'])) {
            $tagArr = New-Object System.Collections.ArrayList
            foreach ($t in $liteIdent['tags']) {
                if ((Test-LiteIsString -Value $t) -and -not [string]::IsNullOrWhiteSpace([string]$t)) {
                    [void]$tagArr.Add([string]$t)
                }
            }
            if ($tagArr.Count -gt 0) { $idBag['tags'] = @($tagArr.ToArray()) }
        }
        if ($idBag.Count -gt 0) { $importMetadata['originalIdentity'] = $idBag }
    }

    # originalCreatedBy: preserve only the two scalar fields the bag
    # schema defines (tool, site). Drop unknown keys.
    if ($env.Contains('createdBy') -and (Test-LiteIsObject -Value $env['createdBy'])) {
        $cbBag = @{}
        $liteCb = $env['createdBy']
        if ($liteCb.Contains('tool') -and (Test-LiteIsString -Value $liteCb['tool'])) {
            $cbBag['tool'] = [string]$liteCb['tool']
        }
        if ($liteCb.Contains('site') -and (Test-LiteIsString -Value $liteCb['site'])) {
            $cbBag['site'] = [string]$liteCb['site']
        }
        if ($cbBag.Count -gt 0) { $importMetadata['originalCreatedBy'] = $cbBag }
    }

    # compatibility: only cookbookRecipeSchemaVersion (integer) is
    # carried; drops anything else from the envelope.
    if ($env.Contains('compatibility') -and (Test-LiteIsObject -Value $env['compatibility'])) {
        $coBag = @{}
        $liteCo = $env['compatibility']
        if ($liteCo.Contains('cookbookRecipeSchemaVersion') -and (Test-LiteIsInt -Value $liteCo['cookbookRecipeSchemaVersion'])) {
            $coBag['cookbookRecipeSchemaVersion'] = [int]$liteCo['cookbookRecipeSchemaVersion']
        }
        if ($coBag.Count -gt 0) { $importMetadata['compatibility'] = $coBag }
    }

    if ($env.Contains('commandPreview') -and (Test-LiteIsString -Value $env['commandPreview'])) {
        $importMetadata['commandPreview'] = [string]$env['commandPreview']
    }

    if ($env.Contains('permissions') -and (Test-LiteIsArray -Value $env['permissions'])) {
        $permArr = New-Object System.Collections.ArrayList
        foreach ($p in $env['permissions']) {
            if ((Test-LiteIsString -Value $p) -and -not [string]::IsNullOrWhiteSpace([string]$p)) {
                [void]$permArr.Add([string]$p)
            }
        }
        if ($permArr.Count -gt 0) { $importMetadata['permissions'] = @($permArr.ToArray()) }
    }

    # importBehavior: persist state + openInPrepStation only.
    if ($env.Contains('importBehavior') -and (Test-LiteIsObject -Value $env['importBehavior'])) {
        $ibBag = @{}
        $liteIb = $env['importBehavior']
        if ($liteIb.Contains('state') -and (Test-LiteIsString -Value $liteIb['state'])) {
            $ibBag['state'] = [string]$liteIb['state']
        }
        if ($liteIb.Contains('openInPrepStation') -and (Test-LiteIsBool -Value $liteIb['openInPrepStation'])) {
            $ibBag['openInPrepStation'] = [bool]$liteIb['openInPrepStation']
        }
        if ($ibBag.Count -gt 0) { $importMetadata['importBehavior'] = $ibBag }
    }

    # mappingWarnings: persist the structural warnings the mapper just
    # produced so the chef sees the same import-time diagnostics in
    # Prep Station after a reload. Each warning is normalised to
    # {code, path, detail} -- 'origin' is mapper-internal and dropped.
    if ($warnings.Count -gt 0) {
        $mwArr = New-Object System.Collections.ArrayList
        foreach ($w in $warnings) {
            if (-not ($w -is [System.Collections.IDictionary])) { continue }
            if (-not $w.Contains('code')) { continue }
            $mw = @{ code = [string]$w['code'] }
            if ($w.Contains('path') -and -not [string]::IsNullOrEmpty([string]$w['path'])) {
                $mw['path'] = [string]$w['path']
            }
            if ($w.Contains('detail') -and -not [string]::IsNullOrEmpty([string]$w['detail'])) {
                $mw['detail'] = [string]$w['detail']
            }
            [void]$mwArr.Add($mw)
        }
        if ($mwArr.Count -gt 0) { $importMetadata['mappingWarnings'] = @($mwArr.ToArray()) }
    }

    $recipe['importMetadata'] = $importMetadata

    return @{
        recipe       = $recipe
        needsChefKey = $needsChefKey
        chefKeyMode  = $chefKeyMode
        warnings     = @($warnings.ToArray())
        preview      = $preview
    }
}

Export-ModuleMember -Function @(
    'Test-MiniKitchenLiteEnvelope'
    'Resolve-MiniKitchenLiteTargetName'
    'New-CookbookDraftFromMiniKitchenLiteEnvelope'
    'Test-LitePathIsRejectableByCookbook'
)
