#requires -Version 7.4

# RecipeTakeout.ps1
#
# Routes:
#   POST   /api/v1/recipes/{id}/takeout         export
#                                               returns the sanitized
#                                               recipe-takeout envelope
#                                               as a downloadable JSON
#                                               file.
#   POST   /api/v1/recipe-takeout/validate      validate
#                                               accepts a takeout
#                                               envelope JSON body
#                                               (<= 256 KiB), validates
#                                               structure + defense-in-
#                                               depth, and returns a
#                                               preview describing the
#                                               import outcome plus a
#                                               nameSuggestion for the
#                                               chef-provided display
#                                               name. No DB writes, no
#                                               FS writes, no PAX
#                                               invocation, no network,
#                                               no logs of envelope
#                                               content.
#   POST   /api/v1/recipe-takeout/import        import
#                                               accepts a wrapper
#                                               { takeout, targetRecipeName }
#                                               of an already-validated
#                                               envelope plus the chef's
#                                               explicit display name.
#                                               Materializes a fresh
#                                               local recipe (new ULID,
#                                               source='takeout', no
#                                               authProfileId, no
#                                               filename / file-path
#                                               dependency on the
#                                               .paxrecipe.json), and
#                                               persists it via the
#                                               same Write-RecipeFile +
#                                               Add-RecipeRow pattern
#                                               used by recipe create.
#                                               BYPASSES Test-RecipeAll
#                                               on insert: App* recipes
#                                               always land in Needs
#                                               Prep until the chef
#                                               binds a local Chef's
#                                               Key. Next save through
#                                               /api/v1/recipes/{id}
#                                               re-runs full validation.
#
# Behavior contract:
#   - Read-only against the source recipe. Never mutates the file,
#     never inserts/updates a SQLite row, never starts a bake, never
#     calls PAX, never writes the takeout JSON to disk, never logs the
#     envelope body, never touches the network.
#   - Loads the source recipe via the existing Read-RecipeFile helper
#     in Routes\Recipes.ps1 (already dot-sourced by Start-Broker.ps1).
#   - Resolves the optional Chef's Key source display label via the
#     existing Get-AuthProfileRow helper. The lookup is best-effort:
#     if the recipe's auth.authProfileId resolves to a profile row,
#     the row's `name` is passed to the sanitizer as the display
#     label; otherwise the field is omitted from the envelope.
#   - Composes the envelope via Get-RecipeTakeoutEnvelope from
#     Modules\RecipeTakeoutSanitizer.psm1, passing the broker's
#     authoritative provenance state ($Script:CookbookVersion,
#     $Script:PaxScriptVersion, $Script:ReleaseChannel) and the
#     workspace path ($Script:Workspace.Path) for fingerprint.
#   - Performs a strict structural validation on the produced envelope
#     before writing the response (defense in depth on top of the
#     sanitizer's internal forbidden-name / forbidden-value scans).
#     This is intentionally NOT a full JSON Schema 2020-12 validation:
#     the broker's only validator (Test-RecipeSchemaNode in
#     Routes\RecipeValidator.ps1) is engine-generic but the F2A
#     pre-flight noted it is bound by caller to the recipe schema and
#     a clean route-level entry point against the takeout schema does
#     not exist without editing RecipeValidator.ps1. F2B does NOT
#     touch that file; full schema validation is deferred to F2C/F2D
#     (where the validate endpoint already has to import the schema
#     file and run it through the same engine).
#   - Failure modes:
#       404 recipe_not_found       -> recipe id unknown, soft-deleted,
#                                     file missing, malformed, or
#                                     unsupported_schema_version.
#       400 invalid_recipe_id      -> id segment is not a valid ULID.
#       405 method_not_allowed     -> path matched but verb is not POST.
#       500 takeout_sanitization_failed -> sanitizer threw. The original
#                                     exception message is NOT leaked
#                                     to the body (the sanitizer guards
#                                     against secret-name / secret-value
#                                     leakage by design; we still keep
#                                     the body bounded so a stack trace
#                                     never reaches the client).
#       500 takeout_envelope_invalid -> structural post-check rejected
#                                     the envelope (would indicate an
#                                     F2A regression; never expected
#                                     to fire in normal operation).
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - Read-RecipeFile          (Routes\Recipes.ps1)
#   - Get-AuthProfileRow       (Routes\AuthProfiles.ps1)
#   - Write-JsonResponse       (Start-Broker.ps1)
#   - $Script:CookbookVersion  (Start-Broker.ps1)
#   - $Script:PaxScriptVersion (Start-Broker.ps1)
#   - $Script:ReleaseChannel   (Start-Broker.ps1)
#   - $Script:WorkspacePath    (Start-Broker.ps1; workspace install root)

# Import the sanitizer module unconditionally. -Force makes development
# iteration safe (the module is re-imported into the broker process on
# every Start-Broker.ps1 dot-source pass). Get-RecipeTakeoutEnvelope is
# the only public function this route calls; other sanitizer exports
# stay inside the module.
Import-Module -Force (Join-Path (Split-Path -Parent $PSScriptRoot) 'Modules\RecipeTakeoutSanitizer.psm1')

# Imported in F2C for the validate endpoint and extended in F2D for
# the import endpoint. Provides Test-RecipeTakeoutEnvelope (structural
# validation), New-RecipeFromTakeoutEnvelope (materialize pending
# recipe payload from envelope), Resolve-RecipeTakeoutTargetName
# (Windows-style numeric suffix walker), and
# Get-RecipeTakeoutImportWarnings.
Import-Module -Force (Join-Path (Split-Path -Parent $PSScriptRoot) 'Modules\RecipeTakeoutImporter.psm1')

# ULID pattern. Identical to $Script:RecipeIdPattern in Routes\Recipes.ps1;
# duplicated here so the takeout route stays self-contained and does not
# depend on the load order between Recipes.ps1 and RecipeTakeout.ps1.
$Script:TakeoutRecipeIdPattern = '^[0-9A-HJKMNP-TV-Z]{26}$'

# Validate endpoint body cap. Locked at 256 KiB by
# recipe_takeout_api_contract_draft.md and
# recipe_takeout_import_model.md. The largest recipe Cookbook generates
# today is well under 16 KiB; the cap is a generous ceiling for
# forward growth (extensions object, more provenance metadata) while
# still protecting the broker from accidental upload of unrelated
# payloads. Enforced on raw bytes BEFORE text decode or JSON parse.
$Script:TakeoutBodyMaxBytes = 256 * 1024

# Auth modes that require a Chef's Key binding on import. Duplicated
# locally because the sanitizer's $Script:AppRegistrationModes lives
# inside the module's own script scope and is not visible from this
# route file. Kept in sync with RecipeTakeoutSanitizer.psm1.
$Script:TakeoutAppRegistrationModes = @(
    'AppRegistrationSecret',
    'AppRegistrationCertificate'
)

# Filename slug rules:
#   - lowercase recipe.identity.name
#   - replace any character not in [a-z0-9] with '-'
#   - collapse repeated '-' into one
#   - trim leading/trailing '-'
#   - truncate to 60 chars
#   - fallback to 'recipe' if empty after trim/truncate
#   - append '.json.pax' (preferred extension; broker still accepts
#     '.paxrecipe.json' and bare '.json' on import for back-compat)
#   - no timestamp in v1
function Get-RecipeTakeoutFilenameSlug {
    param([string]$RecipeName)
    if ([string]::IsNullOrWhiteSpace($RecipeName)) {
        return 'recipe.json.pax'
    }
    $lower = $RecipeName.ToLowerInvariant()

    # Replace non-alphanumeric with '-'. Done char-by-char (no regex
    # -replace) per project style; the slug grammar is small enough
    # that the loop reads cleanly.
    $sb = New-Object System.Text.StringBuilder
    foreach ($c in $lower.ToCharArray()) {
        if (($c -ge 'a' -and $c -le 'z') -or ($c -ge '0' -and $c -le '9')) {
            [void]$sb.Append($c)
        } else {
            [void]$sb.Append('-')
        }
    }
    $s = $sb.ToString()

    # Collapse repeated '-' into one.
    while ($s.Contains('--')) { $s = $s.Replace('--','-') }

    # Trim leading/trailing '-'.
    $s = $s.Trim('-')

    # Truncate to 60 chars (trim trailing '-' again post-truncate).
    if ($s.Length -gt 60) { $s = $s.Substring(0, 60).TrimEnd('-') }

    if ([string]::IsNullOrEmpty($s)) { $s = 'recipe' }

    return ($s + '.json.pax')
}

# Structural post-check on the produced envelope. Confirms the sanitizer
# emitted the expected top-level shape so a regression in the F2A module
# fails closed at the route boundary rather than silently shipping a
# malformed file. Returns $true on pass, $false on fail.
function Test-RecipeTakeoutEnvelopeStructure {
    # Accepts any IDictionary because Get-RecipeTakeoutEnvelope returns
    # [ordered]@{} (System.Collections.Specialized.OrderedDictionary)
    # at the top level. Inner 'recipe' / 'auth' payloads are plain
    # hashtables built by the sanitizer's allow-list copy step.
    param($Envelope)
    if ($null -eq $Envelope)                                 { return $false }
    if (-not ($Envelope -is [System.Collections.IDictionary])) { return $false }
    if (-not $Envelope.Contains('takeoutSchemaVersion'))     { return $false }
    if ([int]$Envelope['takeoutSchemaVersion'] -ne 1)        { return $false }
    if (-not $Envelope.Contains('kind'))                     { return $false }
    if ([string]$Envelope['kind'] -ne 'pax-cookbook.recipe-takeout') { return $false }
    if (-not $Envelope.Contains('exportedAtUtc'))            { return $false }
    if (-not $Envelope.Contains('recipe'))                   { return $false }
    if (-not ($Envelope['recipe'] -is [System.Collections.IDictionary])) { return $false }
    if (-not $Envelope.Contains('excluded'))                 { return $false }

    # The exported recipe payload MUST NOT carry auth.authProfileId
    # under any circumstance. The sanitizer strips it; this is the
    # belt-and-braces check that fires loudly if a future change ever
    # forgets to.
    $auth = $Envelope['recipe']['auth']
    if (($auth -is [System.Collections.IDictionary]) -and $auth.Contains('authProfileId')) {
        return $false
    }
    return $true
}

# Resolve the optional Chef's Key source display label. Returns $null
# when the source recipe has no AppRegistration-mode auth, no
# authProfileId, or the id does not resolve to a known profile row.
# Never throws; the export must succeed even when the source binding
# was removed after the recipe was last saved.
function Get-RecipeTakeoutSourceChefKeyLabel {
    param([hashtable]$Recipe)
    if (-not ($Recipe -is [hashtable]))                     { return $null }
    if (-not $Recipe.ContainsKey('auth'))                   { return $null }
    $auth = $Recipe['auth']
    if (-not ($auth -is [hashtable]))                       { return $null }
    if (-not $auth.ContainsKey('authProfileId'))            { return $null }
    $apid = [string]$auth['authProfileId']
    if ([string]::IsNullOrWhiteSpace($apid))                { return $null }
    try {
        $row = Get-AuthProfileRow -AuthProfileId $apid
    } catch {
        return $null
    }
    if ($null -eq $row)                                     { return $null }
    if (-not ($row.PSObject.Properties.Name -contains 'name')) { return $null }
    return [string]$row.name
}

# Handler for POST /api/v1/recipes/<ulid>/takeout. Caller has already
# matched method+path; this function only translates the matched id
# into a 200/4xx/5xx response.
function Invoke-RecipeTakeoutExport {
    param(
        $Context,
        [string]$RecipeId
    )

    # Load the source recipe via the existing Read-RecipeFile helper.
    # That function returns a discriminated result; we translate every
    # non-'ok' status into 404 recipe_not_found because the chef cannot
    # export something the broker cannot authoritatively read.
    $loaded = Read-RecipeFile -RecipeId $RecipeId
    if ($null -eq $loaded -or $loaded.status -ne 'ok') {
        Write-JsonResponse -Context $Context -Status 404 -Body @{
            error    = 'recipe_not_found'
            recipeId = $RecipeId
        }
        return
    }
    $recipe = $loaded.recipe

    # Resolve the optional Chef's Key source display label. Lookup
    # failure is non-fatal -- the label is purely informational.
    $sourceLabel = Get-RecipeTakeoutSourceChefKeyLabel -Recipe $recipe

    # Build the envelope. The sanitizer is the single source of truth
    # for the envelope shape, the strip-list, and the warning taxonomy;
    # this route only wraps it with HTTP semantics.
    $exportedAt = [datetime]::UtcNow
    $envelope   = $null
    try {
        $envelope = Get-RecipeTakeoutEnvelope `
            -Recipe               $recipe `
            -ExportedAtUtc        $exportedAt `
            -CookbookVersion      $Script:CookbookVersion `
            -BundledPaxVersion    $Script:PaxScriptVersion `
            -ReleaseChannel       $Script:ReleaseChannel `
            -WorkspaceInstallPath $Script:WorkspacePath `
            -ChefKeySourceLabel   $sourceLabel
    } catch {
        # Fail closed. The sanitizer's defense-in-depth scans throw on
        # forbidden field names or obvious secret values; the route
        # body never leaks the original exception.
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error = 'takeout_sanitization_failed'
        }
        return
    }

    if (-not (Test-RecipeTakeoutEnvelopeStructure -Envelope $envelope)) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{
            error = 'takeout_envelope_invalid'
        }
        return
    }

    # Filename slug from the source recipe name (not the envelope
    # payload -- they are identical at this point but reading from the
    # source recipe makes the dependency explicit).
    $sourceName = $null
    if ($recipe -is [hashtable] -and $recipe.ContainsKey('identity') -and ($recipe['identity'] -is [hashtable])) {
        if ($recipe['identity'].ContainsKey('name')) {
            $sourceName = [string]$recipe['identity']['name']
        }
    }
    $filename = Get-RecipeTakeoutFilenameSlug -RecipeName $sourceName

    # Serialize the envelope and write the response. Standard
    # Content-Disposition: attachment pattern for a JSON download —
    # Content-Type stays application/json because the body IS JSON.
    $json  = $envelope | ConvertTo-Json -Depth 12 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)

    $Context.Response.StatusCode      = 200
    $Context.Response.ContentType     = 'application/json; charset=utf-8'
    $Context.Response.ContentLength64 = $bytes.LongLength
    $Context.Response.Headers['Cache-Control']                 = 'no-store'
    $Context.Response.Headers['Content-Disposition']           = 'attachment; filename="' + $filename + '"'
    $Context.Response.Headers['Access-Control-Expose-Headers'] = 'Content-Disposition'
    try {
        $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    } finally {
        try { $Context.Response.OutputStream.Close() } catch {}
        try { $Context.Response.Close() } catch {}
    }
}

# ---------------------------------------------------------------------
# Validate endpoint (F2C)
# ---------------------------------------------------------------------

# Read the request body as raw bytes with a hard cap at
# $Script:TakeoutBodyMaxBytes. Returns a discriminated hashtable:
#   @{ status = 'ok';        bytes = <byte[]> }
#   @{ status = 'too_large'; bytes = $null    }
#   @{ status = 'empty';     bytes = $null    }
# Used by the validate handler so the broker NEVER decodes or parses
# more than $Script:TakeoutBodyMaxBytes of unbounded client input. The
# cap is enforced on raw bytes BEFORE any text decode or JSON parse so
# a malicious client cannot pad a tiny JSON body with megabytes of
# whitespace.
function Read-RecipeTakeoutBodyBytes {
    param($Context)
    $req = $Context.Request
    if (-not $req.HasEntityBody) {
        return @{ status = 'empty'; bytes = $null }
    }
    # Fast-path: trust a positive Content-Length when the client
    # advertises one. Reject before reading even one byte so abusive
    # clients cannot tie up the broker stream-reading megabytes.
    if ($req.ContentLength64 -gt $Script:TakeoutBodyMaxBytes) {
        return @{ status = 'too_large'; bytes = $null }
    }
    $cap    = $Script:TakeoutBodyMaxBytes
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

# Build the validate preview response shape locked in the F2C prompt
# and recipe_takeout_api_contract_draft.md. Pure transform: walks the
# already-validated envelope hashtable and returns a plain hashtable
# the broker can ConvertTo-Json straight to the wire.
#
# Preview keys:
#   ok                       (bool, always $true at this layer)
#   valid                    (bool, always $true here -- callers route
#                             invalid envelopes through the error path)
#   state                    'needs_prep' | 'ready_after_import'
#   recipe.name              envelope.recipe.identity.name
#   recipe.sourceRecipeId    envelope.sourceRecipe.id (or $null)
#   recipe.sourceTemplate    envelope.sourceRecipe.sourceTemplate (or $null)
#   chefKey.required         envelope.chefKey.requirement -eq 'required'
#                             OR recipe.auth.mode in AppRegistration*
#   chefKey.mode             envelope.chefKey.mode (or recipe.auth.mode)
#   chefKey.sourceDisplayLabel envelope.chefKey.sourceDisplayLabel
#   warnings[]               objects: { code, origin='export'; path?, detail? }
#   needsPrep.chefKey        true if Chef's Key binding required on import
#   needsPrep.paths          true if envelope warnings contain any path_* code
#   needsPrep.tenant         true if envelope warnings contain
#                             tenant_id_present_review_recommended
#   message                  human-readable summary
function New-RecipeTakeoutValidatePreview {
    param($Envelope)
    $recipe = $null
    if ($Envelope.Contains('recipe') -and ($Envelope['recipe'] -is [System.Collections.IDictionary])) {
        $recipe = $Envelope['recipe']
    }
    $recipeName = $null
    if ($recipe -and $recipe.Contains('identity') -and ($recipe['identity'] -is [System.Collections.IDictionary])) {
        if ($recipe['identity'].Contains('name')) {
            $recipeName = [string]$recipe['identity']['name']
        }
    }
    $sourceRecipeId = $null
    $sourceTemplate = $null
    if ($Envelope.Contains('sourceRecipe') -and ($Envelope['sourceRecipe'] -is [System.Collections.IDictionary])) {
        $sr = $Envelope['sourceRecipe']
        if ($sr.Contains('id'))             { $sourceRecipeId = [string]$sr['id'] }
        if ($sr.Contains('sourceTemplate')) { $sourceTemplate = $sr['sourceTemplate'] }
    }

    $chefKeyRequired = $false
    $chefKeyMode     = $null
    $chefKeyLabel    = $null
    if ($Envelope.Contains('chefKey') -and ($Envelope['chefKey'] -is [System.Collections.IDictionary])) {
        $ck = $Envelope['chefKey']
        if ($ck.Contains('requirement') -and ([string]$ck['requirement'] -eq 'required')) {
            $chefKeyRequired = $true
        }
        if ($ck.Contains('mode'))               { $chefKeyMode  = [string]$ck['mode'] }
        if ($ck.Contains('sourceDisplayLabel')) { $chefKeyLabel = [string]$ck['sourceDisplayLabel'] }
    }
    if (-not $chefKeyRequired -and $recipe -and $recipe.Contains('auth') -and ($recipe['auth'] -is [System.Collections.IDictionary])) {
        if ($recipe['auth'].Contains('mode')) {
            $authMode = [string]$recipe['auth']['mode']
            if ($Script:TakeoutAppRegistrationModes -contains $authMode) {
                $chefKeyRequired = $true
                if ($null -eq $chefKeyMode) { $chefKeyMode = $authMode }
            }
        }
    }

    # Walk envelope warnings and normalise into the preview shape
    # (origin = 'export' since validate runs before the broker has
    # any import-time context). Each entry surfaces code; path and
    # detail are optional and only emitted when the export wrote one.
    $warnings      = New-Object System.Collections.ArrayList
    $hasPathWarn   = $false
    $hasTenantWarn = $false
    if ($Envelope.Contains('warnings') -and ($Envelope['warnings'] -is [System.Collections.IEnumerable]) -and -not ($Envelope['warnings'] -is [string])) {
        foreach ($w in $Envelope['warnings']) {
            if (-not ($w -is [System.Collections.IDictionary])) { continue }
            $code   = ''
            $wpath  = $null
            $detail = $null
            if ($w.Contains('code'))   { $code   = [string]$w['code'] }
            if ($w.Contains('path'))   { $wpath  = [string]$w['path'] }
            if ($w.Contains('detail')) { $detail = [string]$w['detail'] }
            $entry = [ordered]@{ code = $code; origin = 'export' }
            if ($null -ne $wpath)  { $entry['path']   = $wpath }
            if ($null -ne $detail) { $entry['detail'] = $detail }
            [void]$warnings.Add($entry)
            if ($code -like 'path_*')                             { $hasPathWarn   = $true }
            if ($code -eq 'tenant_id_present_review_recommended') { $hasTenantWarn = $true }
        }
    }

    $needsChefKey = $chefKeyRequired
    $needsPaths   = $hasPathWarn
    $needsTenant  = $hasTenantWarn
    $needsAny     = $needsChefKey -or $needsPaths -or $needsTenant
    $state        = if ($needsAny) { 'needs_prep' } else { 'ready_after_import' }

    $reasons = New-Object System.Collections.ArrayList
    if ($needsChefKey) { [void]$reasons.Add('chef key binding') }
    if ($needsPaths)   { [void]$reasons.Add('path review') }
    if ($needsTenant)  { [void]$reasons.Add('tenant review') }
    $message = if ($needsAny) {
        'Envelope is valid. Prep Station required after import: ' + ($reasons -join ', ') + '.'
    } else {
        'Envelope is valid. Recipe is ready to bake after import.'
    }

    return [ordered]@{
        ok      = $true
        valid   = $true
        state   = $state
        recipe  = [ordered]@{
            name           = $recipeName
            sourceRecipeId = $sourceRecipeId
            sourceTemplate = $sourceTemplate
        }
        chefKey = [ordered]@{
            required           = $chefKeyRequired
            mode               = $chefKeyMode
            sourceDisplayLabel = $chefKeyLabel
        }
        warnings  = @($warnings.ToArray())
        needsPrep = [ordered]@{
            chefKey = $needsChefKey
            paths   = $needsPaths
            tenant  = $needsTenant
        }
        message = $message
    }
}

# Translate the importer's flat structural-error list into a single
# HTTP error code from the F1 taxonomy. Order of precedence picks the
# most specific applicable code; the catch-all is takeout_shape_invalid.
function Get-RecipeTakeoutValidateErrorCode {
    param($Errors)
    if ($null -eq $Errors -or $Errors.Count -eq 0) { return 'takeout_shape_invalid' }
    $hasKind    = $false
    $hasVersion = $false
    $hasUnknown = $false
    foreach ($e in $Errors) {
        if (-not ($e -is [System.Collections.IDictionary])) { continue }
        $epath = [string]$e['path']
        $emsg  = [string]$e['message']
        if ($epath -eq '/kind')                     { $hasKind    = $true }
        if ($epath -eq '/takeoutSchemaVersion')     { $hasVersion = $true }
        if ($emsg  -eq 'unknown top-level property'){ $hasUnknown = $true }
    }
    if ($hasVersion) { return 'takeout_schema_version_unsupported' }
    if ($hasKind)    { return 'takeout_kind_invalid' }
    if ($hasUnknown) { return 'takeout_unknown_field' }
    return 'takeout_shape_invalid'
}

# Handler for POST /api/v1/recipe-takeout/validate. Caller has already
# matched path; this function enforces the body cap, parses JSON, runs
# defense-in-depth + structural checks, and returns either a preview
# hashtable (200) or a structured error (400 / 405 / 413).
#
# No DB writes, no file writes, no PAX invocation, no network calls.
# The envelope body is parsed into an in-memory hashtable and never
# logged or echoed back verbatim. Error responses surface a short
# diagnostic shape (error code + path/field hints) without ever
# returning the original envelope payload.
function Invoke-RecipeTakeoutValidate {
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()

    if ($method -ne 'POST') {
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return
    }

    $bodyResult = Read-RecipeTakeoutBodyBytes -Context $Context
    if ($bodyResult.status -eq 'too_large') {
        Write-JsonResponse -Context $Context -Status 413 -Body @{
            error      = 'payload_too_large'
            limitBytes = $Script:TakeoutBodyMaxBytes
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

    # Defense-in-depth: forbidden field-name scan + secret-value scan
    # over the entire decoded envelope BEFORE structural validation.
    # Either of these tripping means the envelope leaked something
    # that should never have left the source workspace; short-circuit
    # with a fixed error code rather than handing the envelope further
    # down the pipeline.
    try {
        $forbiddenName = Test-RecipeTakeoutForbiddenFieldName -Tree $envelope
        if ($null -ne $forbiddenName) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error     = 'takeout_contains_forbidden_secret_field'
                fieldName = [string]$forbiddenName
            }
            return
        }
        $secretTag = Test-RecipeTakeoutForbiddenSecretValue -Tree $envelope
        if ($null -ne $secretTag) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error = 'takeout_contains_forbidden_secret_field'
                kind  = [string]$secretTag
            }
            return
        }
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error = 'takeout_contains_forbidden_secret_field'
        }
        return
    }

    # Explicit authProfileId leakage check at envelope.recipe.auth.
    # The sanitizer strips it on export; the import side refuses to
    # accept it back. Belt-and-braces, runs before the importer's
    # structural validation so the error path is unambiguous.
    if ($envelope.Contains('recipe') -and ($envelope['recipe'] -is [System.Collections.IDictionary])) {
        $recipeNode = $envelope['recipe']
        if ($recipeNode.Contains('auth') -and ($recipeNode['auth'] -is [System.Collections.IDictionary])) {
            if ($recipeNode['auth'].Contains('authProfileId')) {
                Write-JsonResponse -Context $Context -Status 400 -Body @{
                    error     = 'takeout_contains_forbidden_secret_field'
                    fieldName = 'authProfileId'
                    path      = '/recipe/auth/authProfileId'
                }
                return
            }
        }
    }

    # Structural validation via the importer module. Returns a flat
    # @{ ok = $bool; errors = @(@{path; message}, ...) } shape; map
    # the error pattern to one of the F1 taxonomy codes.
    $structural = Test-RecipeTakeoutEnvelope -Envelope $envelope
    if (-not $structural.ok) {
        $code = Get-RecipeTakeoutValidateErrorCode -Errors $structural.errors
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = $code
            errors = @($structural.errors)
        }
        return
    }

    # All gates green -- produce the preview.
    $preview = New-RecipeTakeoutValidatePreview -Envelope $envelope
    # F2D: emit nameSuggestion sibling. Uses the destination workspace's
    # current set of active recipe display names to compute the
    # Windows-style numeric-suffix walk over the source name. Returns a
    # null sourceName when the envelope has no usable identity.name.
    $preview['nameSuggestion'] = Get-RecipeTakeoutNameSuggestion -Envelope $envelope -ExistingNames (Get-RecipeTakeoutExistingNames)
    Write-JsonResponse -Context $Context -Status 200 -Body $preview
}

# ---------------------------------------------------------------------
# F2D helpers
# ---------------------------------------------------------------------

# Resolve the destination workspace's set of active recipe display
# names. Defensive against contract-smoke environments that dot-source
# this route file without dot-sourcing Routes\Recipes.ps1 (where
# Get-RecipeRowsActive lives).
function Get-RecipeTakeoutExistingNames {
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

# Compute the nameSuggestion shape returned alongside the validate
# preview. Walks the envelope.recipe.identity.name through the
# importer's Windows-style numeric suffix walker against the
# destination workspace's existing recipe names.
#
# Result shape:
#   sourceName     -- trimmed envelope.recipe.identity.name or $null
#   suggestedName  -- sourceName (no collision) | "Name (N)" | $null
#                     (when even Name (99) collides)
#   collision      -- $true when sourceName already exists in
#                     ExistingNames (case-insensitive, trim-aware)
#   collisionRule  -- always 'windows_numeric_suffix'
#   maxSuffix      -- always 99
function Get-RecipeTakeoutNameSuggestion {
    param($Envelope, [string[]]$ExistingNames)
    $source = $null
    if ($Envelope -is [System.Collections.IDictionary] -and $Envelope.Contains('recipe') -and
        ($Envelope['recipe'] -is [System.Collections.IDictionary]) -and
        $Envelope['recipe'].Contains('identity') -and
        ($Envelope['recipe']['identity'] -is [System.Collections.IDictionary]) -and
        $Envelope['recipe']['identity'].Contains('name')) {
        $source = [string]$Envelope['recipe']['identity']['name']
    }
    if ($null -ne $source) { $source = $source.Trim() }
    if ([string]::IsNullOrEmpty($source)) {
        return [ordered]@{
            sourceName    = $null
            suggestedName = $null
            collision     = $false
            collisionRule = 'windows_numeric_suffix'
            maxSuffix     = 99
        }
    }
    $names = @()
    if ($null -ne $ExistingNames) { $names = @($ExistingNames) }
    $hasCollision = $false
    foreach ($n in $names) {
        if ([string]::IsNullOrEmpty([string]$n)) { continue }
        if ([string]::Equals(([string]$n).Trim(), $source, [System.StringComparison]::OrdinalIgnoreCase)) {
            $hasCollision = $true
            break
        }
    }
    if (-not $hasCollision) {
        return [ordered]@{
            sourceName    = $source
            suggestedName = $source
            collision     = $false
            collisionRule = 'windows_numeric_suffix'
            maxSuffix     = 99
        }
    }
    $resolved  = Resolve-RecipeTakeoutTargetName -ProposedName $source -ExistingNames $names
    $suggested = if ($resolved.resolved) { [string]$resolved.name } else { $null }
    return [ordered]@{
        sourceName    = $source
        suggestedName = $suggested
        collision     = $true
        collisionRule = 'windows_numeric_suffix'
        maxSuffix     = 99
    }
}

# Validate a chef-provided targetRecipeName per F2D rules:
#   * length 1-200 codepoints after trim
#   * no control codepoints < 0x20 (includes tab, CR, LF)
#   * no filename-reserved characters: < > : " / \ | ? *
# Returns @{ ok = $true } on success, otherwise
# @{ ok = $false; reason = 'length' | 'control' | 'invalid_char' }.
function Test-RecipeTakeoutNameWindowsValid {
    param([string]$Name)
    if ([string]::IsNullOrEmpty($Name)) {
        return @{ ok = $false; reason = 'length' }
    }
    if ($Name.Length -lt 1 -or $Name.Length -gt 200) {
        return @{ ok = $false; reason = 'length' }
    }
    foreach ($c in $Name.ToCharArray()) {
        if ([int]$c -lt 0x20) {
            return @{ ok = $false; reason = 'control' }
        }
    }
    $invalidChars = @('<','>',':','"','/','\','|','?','*')
    foreach ($ch in $invalidChars) {
        if ($Name.Contains($ch)) {
            return @{ ok = $false; reason = 'invalid_char' }
        }
    }
    return @{ ok = $true }
}

# Handler for POST /api/v1/recipe-takeout/import. Request body is a
# JSON wrapper of EXACTLY two top-level fields:
#   {
#     "takeout":          { ... already-validated envelope ... },
#     "targetRecipeName": "Display name chosen by the chef"
#   }
# No 'confirmed', no 'resolvedName', no 'force', no 'sourceMachineId',
# no client-supplied recipeId. The handler:
#   1. Enforces method = POST.
#   2. Re-enforces body cap, decode, JSON parse.
#   3. Validates wrapper shape (exactly 'takeout' + 'targetRecipeName').
#   4. Validates targetRecipeName (length 1-200, no control / no
#      filename-reserved chars).
#   5. Re-runs the F2C envelope defense-in-depth + structural checks
#      against wrapper.takeout (idempotent with what validate ran).
#   6. Looks up existing display names from the recipes table and
#      rejects 409 recipe_name_conflict when targetRecipeName collides
#      (with a nextSuggestion field carrying the next free Windows-
#      style numeric suffix candidate, or $null when exhausted).
#   7. Materializes a fresh pending recipe via the importer module
#      (strips authProfileId, restamps createdBy / timestamps,
#      assigns a fresh recipeId), explicitly OVERRIDES identity.name
#      with the trimmed targetRecipeName (the explicit name wins), and
#      stamps the destination broker's recipeSchemaVersion +
#      paxAdapterVersion.
#   8. Persists file-first then row (mirrors Invoke-RecipeCreate):
#      atomic Write-RecipeFile, then Add-RecipeRow with source='takeout'
#      and source_ref = envelope.sourceRecipe.id (NULL if not present).
#      Rolls back the file on row failure.
#   9. Returns 201 with recipeId / recipeName / needsPrep / recipe
#      payload. BYPASSES Test-RecipeAll: App* recipes always land in
#      Needs Prep until the chef binds a local Chef's Key. Next save
#      through PUT /api/v1/recipes/{id} re-runs full validation.
# What the import handler MUST NOT do:
#   * restore source recipe id as active id (always fresh ULID)
#   * restore auth.authProfileId from the envelope
#   * require a Chef's Key at import time
#   * create a bake or write cook.log
#   * write a .paxrecipe.json file to disk
#   * store the import filename / file path on the new recipe
#   * keep any dependency on the original takeout file
#   * write envelope contents to logs
#   * dedupe on envelope hash / sourceRecipe.id / exportedAtUtc
function Invoke-RecipeTakeoutImport {
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()

    if ($method -ne 'POST') {
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return
    }

    $bodyResult = Read-RecipeTakeoutBodyBytes -Context $Context
    if ($bodyResult.status -eq 'too_large') {
        Write-JsonResponse -Context $Context -Status 413 -Body @{
            error      = 'payload_too_large'
            limitBytes = $Script:TakeoutBodyMaxBytes
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

    # Wrapper shape: exactly { takeout, targetRecipeName }.
    $allowedTop = @('takeout','targetRecipeName')
    foreach ($k in @($wrapper.Keys)) {
        if ($allowedTop -notcontains [string]$k) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error  = 'takeout_unknown_field'
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
    $nameCheck = Test-RecipeTakeoutNameWindowsValid -Name $trimmedName
    if (-not $nameCheck.ok) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'recipe_name_invalid'
            reason = [string]$nameCheck.reason
        }
        return
    }

    # takeout presence + IDictionary.
    if (-not $wrapper.Contains('takeout')) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'takeout_shape_invalid'
            errors = @(@{ path = '/takeout'; message = 'missing required property' })
        }
        return
    }
    $envelope = $wrapper['takeout']
    if ($null -eq $envelope -or -not ($envelope -is [System.Collections.IDictionary])) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = 'takeout_shape_invalid'
            errors = @(@{ path = '/takeout'; message = 'must be an object' })
        }
        return
    }

    # Reuse F2C defense-in-depth on envelope.
    try {
        $forbiddenName = Test-RecipeTakeoutForbiddenFieldName -Tree $envelope
        if ($null -ne $forbiddenName) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error     = 'takeout_contains_forbidden_secret_field'
                fieldName = [string]$forbiddenName
            }
            return
        }
        $secretTag = Test-RecipeTakeoutForbiddenSecretValue -Tree $envelope
        if ($null -ne $secretTag) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error = 'takeout_contains_forbidden_secret_field'
                kind  = [string]$secretTag
            }
            return
        }
    } catch {
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error = 'takeout_contains_forbidden_secret_field'
        }
        return
    }

    # Explicit authProfileId refusal at envelope.recipe.auth.
    if ($envelope.Contains('recipe') -and ($envelope['recipe'] -is [System.Collections.IDictionary])) {
        $recipeNode = $envelope['recipe']
        if ($recipeNode.Contains('auth') -and ($recipeNode['auth'] -is [System.Collections.IDictionary])) {
            if ($recipeNode['auth'].Contains('authProfileId')) {
                Write-JsonResponse -Context $Context -Status 400 -Body @{
                    error     = 'takeout_contains_forbidden_secret_field'
                    fieldName = 'authProfileId'
                    path      = '/recipe/auth/authProfileId'
                }
                return
            }
        }
    }

    # Structural validation.
    $structural = Test-RecipeTakeoutEnvelope -Envelope $envelope
    if (-not $structural.ok) {
        $code = Get-RecipeTakeoutValidateErrorCode -Errors $structural.errors
        Write-JsonResponse -Context $Context -Status 400 -Body @{
            error  = $code
            errors = @($structural.errors)
        }
        return
    }

    # Collision check (case-insensitive, trim-aware) against existing
    # active recipe display names.
    $existingNames = Get-RecipeTakeoutExistingNames
    $hasCollision  = $false
    foreach ($n in $existingNames) {
        if ([string]::IsNullOrEmpty([string]$n)) { continue }
        if ([string]::Equals(([string]$n).Trim(), $trimmedName, [System.StringComparison]::OrdinalIgnoreCase)) {
            $hasCollision = $true
            break
        }
    }
    if ($hasCollision) {
        $resolved       = Resolve-RecipeTakeoutTargetName -ProposedName $trimmedName -ExistingNames $existingNames
        $nextSuggestion = if ($resolved.resolved) { [string]$resolved.name } else { $null }
        Write-JsonResponse -Context $Context -Status 409 -Body @{
            error          = 'recipe_name_conflict'
            message        = ("A recipe named '{0}' already exists in this Cookbook." -f $trimmedName)
            nextSuggestion = $nextSuggestion
        }
        return
    }

    # Materialize pending recipe payload from envelope.
    $newId    = $null
    $material = $null
    try {
        $newId    = New-RecipeId
        $material = New-RecipeFromTakeoutEnvelope `
            -Envelope          $envelope `
            -NowUtc            ([datetime]::UtcNow) `
            -NewRecipeId       $newId `
            -CookbookVersion   $Script:CookbookVersion `
            -BundledPaxVersion $Script:PaxScriptVersion `
            -ReleaseChannel    $Script:ReleaseChannel `
            -ExistingNames     @()
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'takeout_persist_failed' }
        return
    }
    if ($null -eq $material -or -not ($material -is [System.Collections.IDictionary]) -or
        -not $material.Contains('recipe') -or -not ($material['recipe'] -is [System.Collections.IDictionary])) {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'takeout_persist_failed' }
        return
    }
    $pending = $material['recipe']

    # OVERRIDE identity.name with the explicit trimmed targetRecipeName.
    # Explicit name wins over the importer module's resolver path.
    if (-not ($pending['identity'] -is [System.Collections.IDictionary])) {
        $pending['identity'] = @{}
    }
    $pending['identity']['name'] = $trimmedName
    # Stamp destination's authoritative schema + adapter versions.
    $pending['recipeSchemaVersion'] = $Script:M1_RecipeSchemaVer
    $pending['paxAdapterVersion']   = $Script:PaxScriptVersion
    # Ensure recipeId is the fresh one.
    $pending['recipeId'] = $newId

    # Capture sourceRecipe.id for the recipes.source_ref column.
    # Informational provenance; never used for identity, dedupe, or
    # active-recipe lookup. NULL when the envelope omits it.
    $sourceRecipeId = $null
    if ($envelope.Contains('sourceRecipe') -and ($envelope['sourceRecipe'] -is [System.Collections.IDictionary]) -and
        $envelope['sourceRecipe'].Contains('id')) {
        $sourceRecipeId = [string]$envelope['sourceRecipe']['id']
    }

    # Persist (file-first, row-second; mirror Invoke-RecipeCreate).
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
                source                = 'takeout'
                source_ref            = $sourceRecipeId
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
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'takeout_persist_failed' }
        return
    }

    Write-JsonResponse -Context $Context -Status 201 -Body @{
        ok         = $true
        imported   = $true
        recipeId   = $newId
        recipeName = $trimmedName
        needsPrep  = [ordered]@{
            chefKey = [bool]$material.needsChefKey
            mode    = $material.chefKeyMode
        }
        recipe     = $pending
    }
}

# ---------------------------------------------------------------------
# Route dispatch entry point
# ---------------------------------------------------------------------

function Invoke-RecipeTakeoutRoute {
    # Returns $true if the request was consumed by this handler.
    # Routes intercepted by this dispatch:
    #   POST /api/v1/recipes/<ulid>/takeout    export
    #   POST /api/v1/recipe-takeout/validate   validate
    #   POST /api/v1/recipe-takeout/import     import
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    if ($path -match '^/api/v1/recipes/([^/]+)/takeout$') {
        $rid = $matches[1]
        if ($rid -notmatch $Script:TakeoutRecipeIdPattern) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{
                error    = 'invalid_recipe_id'
                recipeId = $rid
            }
            return $true
        }
        if ($method -ne 'POST') {
            Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
            return $true
        }
        Invoke-RecipeTakeoutExport -Context $Context -RecipeId $rid
        return $true
    }

    if ($path -eq '/api/v1/recipe-takeout/validate') {
        Invoke-RecipeTakeoutValidate -Context $Context
        return $true
    }

    if ($path -eq '/api/v1/recipe-takeout/import') {
        Invoke-RecipeTakeoutImport -Context $Context
        return $true
    }

    return $false
}
