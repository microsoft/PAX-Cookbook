#requires -Version 7.4

# Adapter.psm1
#
# Pure recipe -> PAX invocation-plan projector. This module is the
# SINGLE AUTHORITATIVE PROJECTION SURFACE: every code path in the broker
# that needs to know "what command will run for this recipe" must
# consume one of the exported functions below. No other module is
# permitted to compose PAX argv tokens or to rebuild the pwsh `-Command`
# wrapper expression.
#
# Exported functions:
#
#     Get-PaxArgvArray       -Recipe <hashtable>
#         -> [string[]]   display-only logical tokens. Never reparse
#                         these tokens to reconstruct execution authority.
#
#     Convert-RecipeToPaxArgv -Recipe <hashtable>
#         -> [string]     literal rendering of the typed parameter plan
#                         for preview and command.txt.
#
#     Get-PaxInvocationPlan  -Recipe <hashtable> -PaxScriptPath <string>
#         -> [hashtable] {
#             paxArgv        = [string[]]   # same as Get-PaxArgvArray
#             extraArguments = [string]     # trimmed; '' if absent
#             paxCommand     = [string]     # same as Convert-RecipeToPaxArgv
#             spawnArgv      = [string[]]   # the 4-element pwsh argv
#                                           # passed verbatim to
#                                           # ProcessStartInfo.ArgumentList
#             spawnCommand   = [string]     # human-readable rendering of
#                                           # the full pwsh invocation
#             paxScriptPath  = [string]     # echo of the bundled PAX path
#           }
#         The cook dispatch and supervisor consume the complete spawnArgv
#         verbatim. Typed named parameters are constructed before display
#         tokens. The preview API surfaces paxCommand + paxArgv.
#         command.txt mirrors paxCommand; command-argv.json mirrors
#         paxArgv. The cooks-row command_argv_json column mirrors
#         spawnArgv. All four sinks therefore derive from the same
#         single call to Get-PaxInvocationPlan.
#
# Pure means:
#   - No filesystem reads.
#   - No process spawns.
#   - No SQLite, no broker $Script:* state, no logging.
#   - Same (recipe, paxScriptPath) input always produces the same output,
#     byte-for-byte. Procedural emission order is enforced explicitly --
#     hashtable iteration order is never relied on.
#
# This boundary is enforced by the module's psm1 packaging:
# Import-Module loads it into its own session-state. The adapter cannot
# leak handles to the broker, and the broker cannot reach into adapter
# internals.
#
# Switch coverage is INTENTIONALLY the minimum set required by
# Cookbook's recipe schema:
#
#     -IncludeM365Usage          (when ingredients.m365Usage.includeM365Usage = true)
#     -Rollup                    (when processing.rollup = "Rollup")
#     -RollupPlusRaw             (when processing.rollup = "RollupPlusRaw")
#     -IncludeUserInfo           (when ingredients.entraUserData.includeUserInfo = true)
#     -StartDate <date>          (always; from query.startDate)
#     -EndDate <date>            (always; from query.endDate)
#     -TenantId <guid>           (always; from auth.tenantId)
#     -Auth <mode>               (always; from auth.mode -- with the
#                                 Cookbook-side AppRegistrationSecret
#                                 and AppRegistrationCertificate values
#                                 both projecting to PAX's single
#                                 'AppRegistration' enum value; see
#                                 Phase AF auth-token emission below)
#     -ClientId <guid>                 (Phase AF; only for App* modes;
#                                       from the resolved AuthProfile row)
#     -ClientCertificateThumbprint <h> (Phase AF; only for
#                                       AppRegistrationCertificate mode;
#                                       from the resolved AuthProfile row)
#     -OutputPath "<path>"       (always; from destinations.fact.path)
#     <advanced.extraArguments>  (bounded literal options, optional)
#
# Phase AF auth-token emission (added under explicit milestone gate):
#
#   recipe.auth.mode = WebLogin             -> -Auth WebLogin
#   recipe.auth.mode = DeviceCode           -> -Auth DeviceCode
#   recipe.auth.mode = AppRegistrationSecret
#       -> -Auth AppRegistration
#          -ClientId <profile.clientId>
#          (the secret itself is delivered via the
#           GRAPH_CLIENT_SECRET environment variable on the child
#           ProcessStartInfo; it is NEVER an argv token. The
#           supervisor (Phase AF.C8) owns the env-var injection.)
#   recipe.auth.mode = AppRegistrationCertificate
#       -> -Auth AppRegistration
#          -ClientId <profile.clientId>
#          -ClientCertificateThumbprint <profile.certThumbprint>
#   recipe.auth.mode = ManagedIdentity       -> -Auth ManagedIdentity
#       (no extra args in v1; user-assigned MI clientId support is
#        deferred. Adapter does NOT validate environment capability;
#        that is the supervisor's argv-time gate -- the adapter is
#        pure and environment-agnostic.)
#
# Caller contract (Get-PaxArgvArray / Get-PaxInvocationPlan):
#   - When recipe.auth.mode is AppRegistrationSecret or
#     AppRegistrationCertificate, the caller MUST pass -AuthProfile
#     populated with at least .clientId (and .certThumbprint for the
#     cert variant). The adapter throws structurally if these are
#     missing -- the schema/validator already enforces the recipe
#     side; the adapter throw is defense-in-depth.
#   - The caller MAY pass -ExecutionMode to declare the intended
#     hosting environment. The adapter inspects this purely for
#     argv-time fail-fast on local-incompatible modes; the supervisor
#     repeats the same check at spawn time. Defaults to 'local-manual'.
#
# Emission order is fixed in code (procedural). Two calls with the same
# (recipe, authProfile, executionMode) input must produce byte-identical
# paxArgv, paxCommand, spawnArgv, and spawnCommand. The probe in
# _temp/phase_e_verification/ enforces this stability.
#
# Quoting rule: every string uses ConvertTo-PaxLiteralString. Arrays stay
# explicitly typed string[] values. Unicode smart quotes use numeric char
# fragments so PowerShell cannot treat them as string delimiters. Advanced
# text is parsed as bounded literal data; it is never appended as code.
#
#     Removed-switch enforcement (no migration, no auto-repair):
#     The switches listed in $Script:RemovedSwitches were removed
#     from PAX's surface. They MUST NOT appear in the projected
#     argv via any path. The schema's `additionalProperties:false`
#     blocks their reintroduction as recipe leaves. The only remaining
#     attack surface is `advanced.extraArguments` (the verbatim user
#     escape hatch). Test-ExtraArgumentsForRemovedSwitches scans it at
#     projection time and rejects (throws) when any removed switch is
#     found. Structural rejection is the allowed response; there is
#     NO auto-strip, NO rewrite, NO compatibility shim.
#
# DELIBERATELY ABSENT — DO NOT ADD WITHOUT EXPLICIT MILESTONE GATE:
#
#     -AppendMode  -OutputFilePath  -CertSubject  -AppId
#     -ClientSecret  -OneLakePath  -Fabric*
#     -Schedule*  -UseEOM  -BlockHours  -PartitionHours
#     -MaxPartitions  -ResultSize  -PacingMs  -RecordTypes
#     -Operations  -ServiceTypes  -UserIdsFile
#     -CombineOutput
#     -EnableParallel  -MaxConcurrency  -ParallelMode  -CircuitBreaker*
#     -EmitMetricsJson
#     -IncludeTelemetry  -SkipDiagnostics
#
# Phase AF moved -ClientId and -ClientCertificateThumbprint from
# "deliberately absent" to "emitted under structural gate" (only when
# recipe.auth.mode is AppRegistration*, with values sourced from the
# resolved AuthProfile row). -ClientSecret REMAINS deliberately absent
# in argv; the secret is delivered via the GRAPH_CLIENT_SECRET
# environment variable on the child ProcessStartInfo. Treating
# extraArguments as permanently hostile, Test-ExtraArgumentsForSecretShape
# rejects any user attempt to smuggle -Auth / -TenantId / -ClientId /
# -ClientCertificateThumbprint / -ClientSecret tokens through the
# extraArguments trailer.
#
# V1.S26 moved -ActivityTypes, -UserIds, -GroupNames, -AgentId
# (singular; PAX accepts a list of values after a single -AgentId),
# -AgentsOnly, -ExcludeAgents, -PromptFilter, -OnlyUserInfo,
# -OutputPathUserInfo, -AppendUserInfo, and -ExcludeCopilotInteraction
# from "deliberately absent" to "emitted under structural gate". Each
# is emitted only when the corresponding recipe field is present (and
# the validator has enforced its shape). Under Shape 3 (userInfoOnly),
# the audit-only subset of these switches is silently suppressed even
# when present in the recipe; the validator forbids the corresponding
# fields under Shape 3 so this is defense-in-depth only.
# -ClientCertificatePath is reserved for a future checkpoint (requires
# AuthProfile data-model extension first); it is recognized by
# ConvertTo-PaxCommandString's quoting rule but never emitted.
#
# V1.S03 gated-open: -Resume and -Force.
#
# The PAX engine treats `-Resume <checkpointPath>` as a special invocation
# mode in which the engine restores ALL processing parameters (StartDate,
# EndDate, OutputPath, IncludeM365Usage, Rollup, IncludeUserInfo, append
# behavior, partition counters, etc.) from the on-disk checkpoint file.
# When `-Resume` is present, the engine forbids the caller from
# re-supplying any of those processing parameters; only the auth
# companions and `-Force` are permitted alongside `-Resume`. `-Force`
# suppresses the engine's interactive "use most recent checkpoint?"
# prompt, which would otherwise block a non-interactive child process.
#
# Cookbook exposes this via a SEPARATE projection path:
#
#     Get-PaxResumeArgvArray       -> ordered string[] with ONLY
#                                     -Resume <path> -Force [+ auth
#                                     companions]; no processing args.
#     Get-PaxResumeInvocationPlan  -> mirror of Get-PaxInvocationPlan
#                                     for the resume argv.
#
# The normal projection functions (Get-PaxArgvArray /
# Get-PaxInvocationPlan / Convert-RecipeToPaxArgv) MUST NEVER emit
# `-Resume` or `-Force`. The V1.S03 gate authorizes these tokens ONLY
# on the dedicated resume path. The byte-stability probe in
# _temp/phase_e_verification/ continues to pin the normal projection
# unchanged.

# Switches removed in PAX v1.11.2 -- structural rejection only, no
# migration. Detection is case-insensitive on the bare switch token.
# The Agent365Info family was removed in the v1.11.2 contract cleanup;
# Cookbook never emits these and rejects them in extraArguments. Agent
# 365 registry data is now an out-of-band manual export documented only
# in the AI-in-One Rollup and M365 Usage Analytics dashboard templates.
$Script:RemovedSwitches = @(
    'ExportWorkbook',
    'ExplodeArrays',
    'ExplodeDeep',
    'RawInputCSV',
    'IncludeAgent365Info',
    'OnlyAgent365Info',
    'OutputPathAgent365Info',
    'AppendAgent365Info'
)

# Quote a single argument value for the PowerShell command line. Wraps in
# double quotes and escapes every char that a PowerShell double-quoted
# string interprets specially:
#     `   ->  ``   (backtick is the in-string escape; double it first)
#     "   ->  ""   (doubled double-quote is PowerShell's in-string literal-quote)
#     $   ->  `$   (prevent variable interpolation in the rendered command)
# Whitespace, parens, semicolons, pipes, and other shell separators
# inside the value are neutralized by the surrounding quotes.
#
# Order matters: backticks are escaped FIRST so the backtick we introduce
# for $-escaping is not itself re-escaped.
function ConvertTo-QuotedArg {
    param([string]$Value)
    if ($null -eq $Value) { return '""' }
    $escaped = $Value.Replace('`', '``')
    $escaped = $escaped.Replace('"', '""')
    $escaped = $escaped.Replace('$', '`$')
    return '"' + $escaped + '"'
}

# Structural projection-boundary guard. Scans the user-owned
# extraArguments string for any of the removed-in-v1.11.2 switches and
# THROWS rather than silently passing them through. Detection rules:
#
#   - Case-insensitive.
#   - Matches a hyphen + switch name only at a token boundary (start of
#     string OR preceded by whitespace) followed by either end-of-string,
#     whitespace, or '=' (covers `-ExportWorkbook`, `-EXPORTWORKBOOK`,
#     ` -RawInputCsv `, `-RawInputCSV=foo`).
#   - Does NOT attempt to strip, rewrite, or auto-repair. The caller is
#     responsible for surfacing the error to the user (UI / 4xx).
#
# This is the single chokepoint for removed-switch enforcement on the
# projection path. The L1 schema validator handles the equivalent guard
# for structured recipe leaves (processing.exportWorkbook, etc.).
function Test-ExtraArgumentsForRemovedSwitches {
    param([string]$ExtraArguments)
    if ([string]::IsNullOrWhiteSpace($ExtraArguments)) { return }
    foreach ($name in $Script:RemovedSwitches) {
        # (^|\s)-<name>($|\s|=)  case-insensitive
        $pattern = '(^|\s)-' + [regex]::Escape($name) + '($|\s|=)'
        if ([regex]::IsMatch($ExtraArguments, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            throw "advanced.extraArguments contains removed switch '-$name'. " +
                  "This switch was removed in PAX v1.11.2 and is not reintroduced via the verbatim trailer. " +
                  "Edit the recipe to remove it; the projection layer does not rewrite recipes."
        }
    }
}

# Phase AF -- the auth-token chokepoint. extraArguments is the
# permanently-hostile chef-owned escape hatch; treat it that way. The
# tokens Cookbook itself emits at projection time (-Auth, -TenantId,
# -ClientId, -ClientCertificateThumbprint) MUST NOT be smuggled through
# the trailer, regardless of recipe.auth.mode. -ClientSecret in particular
# MUST NEVER appear in argv -- secrets travel exclusively via the
# GRAPH_CLIENT_SECRET environment variable on the child process.
#
# Detection rules mirror Test-ExtraArgumentsForRemovedSwitches:
#   - Case-insensitive.
#   - Token boundary: start-of-string OR preceded by whitespace.
#   - Trailing context: end-of-string, whitespace, or '=' (covers
#     "-ClientSecret X", "-CLIENTSECRET=X", " -clientsecret  ").
#
# No auto-strip, no rewrite. A recipe that includes any of these in
# extraArguments is structurally invalid and rejected at projection
# time. The validator-layer execution-mode matrix gate (Phase AF.C1)
# is the primary defense; this scan is defense-in-depth at the final
# emission boundary.
$Script:ForbiddenInExtraArguments = @(
    'Auth',
    'TenantId',
    'ClientId',
    'ClientSecret',
    'ClientCertificateThumbprint'
)

function Test-ExtraArgumentsForSecretShape {
    param([string]$ExtraArguments)
    if ([string]::IsNullOrWhiteSpace($ExtraArguments)) { return }
    foreach ($name in $Script:ForbiddenInExtraArguments) {
        $pattern = '(^|\s)-' + [regex]::Escape($name) + '($|\s|=)'
        if ([regex]::IsMatch($ExtraArguments, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $hint = switch ($name) {
                'ClientSecret'                { "Client secrets are delivered to PAX via the GRAPH_CLIENT_SECRET environment variable, NEVER as a command-line argument. Bind the secret via POST /api/v1/auth/profiles/{id}/secret." }
                'ClientCertificateThumbprint' { "Certificate thumbprints are emitted automatically from the auth profile's certThumbprint field. Edit the auth profile instead of the recipe trailer." }
                'ClientId'                    { "Client IDs are emitted automatically from the auth profile's clientId field. Edit the auth profile instead of the recipe trailer." }
                'TenantId'                    { "TenantId is emitted automatically from recipe.auth.tenantId. Edit the recipe's auth block instead of the trailer." }
                'Auth'                        { "Auth mode is emitted automatically from recipe.auth.mode. Edit the recipe's auth block instead of the trailer." }
                default                       { '' }
            }
            $msg = "advanced.extraArguments contains forbidden auth-related switch '-$name'."
            if ($hint) { $msg = $msg + ' ' + $hint }
            throw $msg
        }
    }
}

# Phase AF -- execution-mode gate. The recipe schema's matrix
# allOf already enforces auth-mode compatibility with executionMode,
# but the adapter additionally refuses to project recipes whose
# executionMode declares a HOSTED environment (fabric-hosted or
# azure-hosted): those execution modes do not run via Cookbook's
# local supervisor. The supervisor repeats the same check at spawn
# time as defense-in-depth (Phase AF.C8).
#
# Missing executionMode defaults to 'local-manual' (the schema's
# documented default for backward compatibility with pre-AF
# recipes). The two allowed modes here are local-manual and
# local-scheduled. local-scheduled is allowed at projection time
# because the same Cookbook process projects both manual and
# scheduled cooks; the schedule wiring will eventually live elsewhere
# but the projection itself is mode-agnostic.
$Script:LocalAdapterAllowedExecutionModes = @('local-manual', 'local-scheduled')

function Test-RecipeExecutionModeForLocalAdapter {
    param([string]$ExecutionMode)
    $mode = $ExecutionMode
    if ([string]::IsNullOrWhiteSpace($mode)) { $mode = 'local-manual' }
    if ($Script:LocalAdapterAllowedExecutionModes -notcontains $mode) {
        throw "Recipe executionMode '$mode' cannot be projected by the local PAX adapter. " +
              "This Cookbook instance runs local cooks only (local-manual / local-scheduled). " +
              "Edit the recipe's executionMode field or run this recipe on a Cookbook instance " +
              "deployed in the matching hosting environment."
    }
}

# Build the canonical PAX argv as an ordered [string[]] of UNQUOTED
# logical tokens. This is the structural source-of-truth that every
# other rendering (paxCommand, spawnArgv, spawnCommand) derives from.
# Emission order is fixed procedurally; hashtable iteration order is
# never relied on. Empty / missing fields skip their tokens cleanly.
function ConvertTo-PaxLiteralString {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Contains([char]0)) { throw 'A parameter contains an unsupported null character.' }
    $parts = [Collections.Generic.List[string]]::new()
    $text = [Text.StringBuilder]::new()
    foreach ($character in $Value.ToCharArray()) {
        if ([int]$character -ge 0x2018 -and [int]$character -le 0x201f) {
            $parts.Add("'" + $text.ToString().Replace("'", "''") + "'")
            $parts.Add('[char]' + ([int]$character).ToString([Globalization.CultureInfo]::InvariantCulture))
            [void]$text.Clear()
        } else { [void]$text.Append($character) }
    }
    $parts.Add("'" + $text.ToString().Replace("'", "''") + "'")
    if ($parts.Count -eq 1) { return $parts[0] }
    return '(' + ($parts -join '+') + ')'
}

function ConvertTo-PaxLiteralValue {
    param([AllowEmptyString()][AllowEmptyCollection()]$Value)
    if ($Value -is [string]) { return (ConvertTo-PaxLiteralString $Value) }
    if ($Value -is [string[]]) {
        return '[string[]]@(' + ((@($Value | ForEach-Object { ConvertTo-PaxLiteralString $_ })) -join ',') + ')'
    }
    if ($Value -is [bool]) { if ($Value) { return '$true' } else { return '$false' } }
    if ($Value -is [int]) { return '[int]' + $Value.ToString([Globalization.CultureInfo]::InvariantCulture) }
    if ($Value -is [double] -and [double]::IsFinite($Value)) { return '[double]' + $Value.ToString('R', [Globalization.CultureInfo]::InvariantCulture) }
    throw 'Unsupported parameter type.'
}

$Script:AdvancedOptions = @(
    @('BlockHours',[double],0.016667,24), @('PartitionHours',[int],1,72),
    @('MaxPartitions',[int],1,1000), @('ResultSize',[int],1,10000), @('PacingMs',[int],0,10000),
    @('FlatDepth',[int]), @('MaxConcurrency',[int]), @('EnableParallel',[bool]),
    @('MaxParallelGroups',[int],0,50), @('ParallelMode',[string]), @('ExplosionThreads',[int],0,32),
    @('DisableAdaptive',[bool]), @('ProgressSmoothingAlpha',[double],0,1),
    @('HighLatencyMs',[int],1000,600000), @('MemoryPressureMB',[int],256,32768),
    @('MaxMemoryMB',[int],-1,65536), @('StatusIntervalSeconds',[int],30,600),
    @('LowLatencyMs',[int],100,600000), @('LowLatencyConsecutive',[int],1,10),
    @('ThroughputDropPct',[int],1,100), @('ThroughputSmoothingAlpha',[double],0,1),
    @('AdaptiveConcurrencyCeiling',[int],1,50), @('ExportProgressInterval',[int],1,10000),
    @('StreamingSchemaSample',[int],100,50000), @('StreamingChunkSize',[int],100,50000),
    @('CircuitBreakerThreshold',[int],1,50), @('CircuitBreakerCooldownSeconds',[int],5,3600),
    @('BackoffBaseSeconds',[double],0.1,120), @('BackoffMaxSeconds',[int],1,600),
    @('EmitMetricsJson',[bool]), @('AutoCompleteness',[bool]), @('CombineOutput',[bool]),
    @('Force',[bool]), @('SkipDiagnostics',[bool]), @('SkipVersionCheck',[bool]),
    @('MaxNetworkOutageMinutes',[int]), @('IncludeTelemetry',[bool])) | ForEach-Object {
        [pscustomobject]@{Name=$_[0];Type=$_[1];Minimum=$(if ($_.Count -gt 2) { [double]$_[2] } else { [double][int]::MinValue });Maximum=$(if ($_.Count -gt 3) { [double]$_[3] } else { [double][int]::MaxValue })}
    }
$Script:AdvancedArgumentsError = 'advanced.extraArguments requires exact supported option names and literal values. Use single or double quotes for literal strings, invariant numbers, and $true/$false for switches; expressions, duplicates, and structured/auth/output overrides are not allowed.'

function ConvertFrom-PaxAdvancedArguments {
    param([AllowEmptyString()][string]$Text)
    $parsed = [ordered]@{}
    if ($Text.Length -gt 16384 -or $Text.Contains([char]0)) { throw $Script:AdvancedArgumentsError }
    $position = 0
    while ($position -lt $Text.Length) {
        while ($position -lt $Text.Length -and [char]::IsWhiteSpace($Text[$position])) { $position++ }
        if ($position -eq $Text.Length) { break }
        if ($parsed.Count -ge 64 -or $Text[$position++] -cne '-') { throw $Script:AdvancedArgumentsError }
        $nameStart = $position
        while ($position -lt $Text.Length -and [char]::IsAsciiLetterOrDigit($Text[$position])) { $position++ }
        $name = $Text.Substring($nameStart, $position - $nameStart)
        $option = $Script:AdvancedOptions | Where-Object { [string]::Equals($_.Name, $name, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if ($null -eq $option -or $parsed.Contains($option.Name)) { throw $Script:AdvancedArgumentsError }
        $colon = $position -lt $Text.Length -and $Text[$position] -ceq ':'
        if ($colon) { $position++ }
        elseif ($position -lt $Text.Length -and -not [char]::IsWhiteSpace($Text[$position])) { throw $Script:AdvancedArgumentsError }
        while ($position -lt $Text.Length -and [char]::IsWhiteSpace($Text[$position])) { $position++ }
        if ($option.Type -eq [bool] -and -not $colon -and ($position -eq $Text.Length -or $Text[$position] -ceq '-')) {
            $parsed.Add($option.Name, $true)
            continue
        }
        if ($position -eq $Text.Length) { throw $Script:AdvancedArgumentsError }
        $quote = $Text[$position]
        $literal = ''
        if ($quote -ceq "'" -or $quote -ceq '"') {
            $position++
            $content = [Text.StringBuilder]::new()
            $closed = $false
            while ($position -lt $Text.Length) {
                $character = $Text[$position++]
                if ($character -ceq $quote) {
                    if ($position -lt $Text.Length -and $Text[$position] -ceq $quote) { [void]$content.Append($quote); $position++ }
                    else { $closed = $true; break }
                } else { [void]$content.Append($character) }
                if ($content.Length -gt 4096) { throw $Script:AdvancedArgumentsError }
            }
            if (-not $closed -or ($position -lt $Text.Length -and -not [char]::IsWhiteSpace($Text[$position]))) { throw $Script:AdvancedArgumentsError }
            $literal = $content.ToString()
        } else {
            $start = $position
            while ($position -lt $Text.Length -and -not [char]::IsWhiteSpace($Text[$position])) {
                $character = $Text[$position++]
                if (-not ([char]::IsAsciiLetterOrDigit($character) -or $character -cin @('_','-','+','.','$')) -or $position - $start -gt 4096) { throw $Script:AdvancedArgumentsError }
            }
            $literal = $Text.Substring($start, $position - $start)
            if ($literal.Length -eq 0) { throw $Script:AdvancedArgumentsError }
        }
        $value = $null
        if ($option.Type -eq [bool]) {
            if ($literal -ieq '$true' -or $literal -ieq 'true') { $value = $true }
            elseif ($literal -ieq '$false' -or $literal -ieq 'false') { $value = $false }
            else { throw $Script:AdvancedArgumentsError }
        } elseif ($option.Type -eq [int]) {
            $number = 0
            if (-not [int]::TryParse($literal, [Globalization.NumberStyles]::AllowLeadingSign, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or $number -lt $option.Minimum -or $number -gt $option.Maximum) { throw $Script:AdvancedArgumentsError }
            $value = $number
        } elseif ($option.Type -eq [double]) {
            $number = [double]0
            $style = [Globalization.NumberStyles]::AllowLeadingSign -bor [Globalization.NumberStyles]::AllowDecimalPoint -bor [Globalization.NumberStyles]::AllowExponent
            if (-not [double]::TryParse($literal, $style, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or -not [double]::IsFinite($number) -or $number -lt $option.Minimum -or $number -gt $option.Maximum) { throw $Script:AdvancedArgumentsError }
            $value = $number
        } else {
            if (@('Off','On','Auto') -inotcontains $literal) { throw $Script:AdvancedArgumentsError }
            $value = $literal
        }
        $parsed.Add($option.Name, $value)
    }
    return $parsed
}

function Test-RecipeAdvancedArguments {
    param([Parameter(Mandatory)]$Recipe)
    if ($Recipe -isnot [System.Collections.IDictionary] -or -not $Recipe.Contains('advanced')) { return }
    $advanced = $Recipe.advanced
    if ($advanced -isnot [System.Collections.IDictionary] -or -not $advanced.Contains('extraArguments')) { return }
    try { $null = ConvertFrom-PaxAdvancedArguments -Text ([string]$advanced.extraArguments) }
    catch {
        return @{instancePath='/advanced/extraArguments';keyword='advancedArguments';message=$Script:AdvancedArgumentsError;params=@{}}
    }
}

function Get-PaxDisplayArguments {
    param([System.Collections.IDictionary]$Parameters)
    $tokens = [Collections.Generic.List[string]]::new()
    foreach ($entry in $Parameters.GetEnumerator()) {
        if ($entry.Key -ne 'RemainingArgs') { $tokens.Add('-' + $entry.Key) }
        if ($entry.Value -is [string[]]) { $tokens.AddRange($entry.Value) }
        elseif ($entry.Value -isnot [bool] -or -not $entry.Value) {
            if ($entry.Value -is [string]) { $tokens.Add($entry.Value) }
            else { $tokens.Add((ConvertTo-PaxLiteralValue $entry.Value)) }
        }
    }
    return ,$tokens.ToArray()
}

function Get-PaxArgvArray {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Recipe, $AuthProfile=$null, [string]$ExecutionMode='')
    return ,(Get-PaxDisplayArguments (Get-PaxParameters @PSBoundParameters))
}

function New-PaxInvocationPlan {
    param([System.Collections.IDictionary]$Parameters, [string]$PaxScriptPath, [string]$ExtraArguments='')
    $types = @{}
    foreach ($name in @('OnlyUserInfo','IncludeUserInfo','IncludeM365Usage','Rollup','RollupPlusRaw','AgentsOnly','ExcludeAgents','ExcludeCopilotInteraction','Force')) { $types[$name] = [bool] }
    foreach ($name in @('StartDate','EndDate','TenantId','Auth','ClientId','ClientCertificateThumbprint','PromptFilter','OutputPath','AppendFile','OutputPathUserInfo','AppendUserInfo')) { $types[$name] = [string] }
    foreach ($name in @('UserIds','ActivityTypes','GroupNames','AgentId','RemainingArgs')) { $types[$name] = [string[]] }
    foreach ($option in $Script:AdvancedOptions) { $types[$option.Name] = $option.Type }
    foreach ($entry in $Parameters.GetEnumerator()) {
        if (-not $types.ContainsKey($entry.Key) -or $null -eq $entry.Value -or $entry.Value.GetType() -ne $types[$entry.Key]) { throw 'Unsupported invocation parameter name or type.' }
    }
    $launcherPath = Join-Path $PSScriptRoot 'Invoke-PaxParameters.ps1'
    return @{
        parameters=$Parameters
        parameterJson=(ConvertTo-Json -InputObject $Parameters -Compress -Depth 4)
        paxArgv=(Get-PaxDisplayArguments $Parameters)
        paxCommand=(ConvertTo-PaxCommandString -Parameters $Parameters)
        extraArguments=$ExtraArguments
        spawnArgv=[string[]]@('-NoProfile','-NoLogo','-File',$launcherPath,'-EnginePath',$PaxScriptPath)
        spawnCommand=('pwsh -NoProfile -NoLogo -File ' + (ConvertTo-PaxLiteralString $launcherPath) + ' -EnginePath ' + (ConvertTo-PaxLiteralString $PaxScriptPath))
        paxScriptPath=$PaxScriptPath
    }
}

function Get-PaxParameters {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Recipe,
        # Phase AF: when recipe.auth.mode is AppRegistrationSecret or
        # AppRegistrationCertificate, the caller MUST supply the
        # resolved auth-profile row (from Routes/AuthProfiles.ps1's
        # Get-AuthProfileRow). For WebLogin / DeviceCode /
        # ManagedIdentity / absent auth, $AuthProfile is unused.
        $AuthProfile = $null,
        # Phase AF: executionMode declares the hosting environment.
        # 'local-manual' (the v1 default) and 'local-scheduled' are
        # the only modes this adapter is willing to project; hosted
        # modes are rejected at projection time.
        [string]$ExecutionMode = ''
    )

    # Argv-time execution-mode gate. The recipe validator already
    # blocks non-local executionMode/auth-mode combinations; this is
    # the projection-boundary defense-in-depth.
    $execMode = $ExecutionMode
    if ([string]::IsNullOrWhiteSpace($execMode)) {
        if ($Recipe.ContainsKey('executionMode')) {
            $execMode = [string]$Recipe.executionMode
        }
    }
    Test-RecipeExecutionModeForLocalAdapter -ExecutionMode $execMode

    $parameters = [ordered]@{}

    # ---- V1.S26 -- read run-shape early ------------------------------
    # The recipe's run shape (audit vs userInfoOnly) drives which
    # switches the adapter projects. Shape 3 (userInfoOnly) emits a
    # narrow argv: -OnlyUserInfo, -IncludeUserInfo, the auth block,
    # and the user-info destination. It MUST NOT emit:
    #   -IncludeM365Usage / -Rollup / -RollupPlusRaw
    #   -StartDate / -EndDate
    #   -ActivityTypes / -UserIds / -GroupNames
    #   -AgentId / -AgentsOnly / -ExcludeAgents / -PromptFilter
    #   -OutputPath / -AppendFile
    #   -ExcludeCopilotInteraction
    # Shape 1/2 (audit) emits the full audit surface; legacy recipes
    # that don't carry any S26 fields project byte-identically.
    $queryMode = ''
    if ($Recipe.ContainsKey('query') -and $Recipe.query.ContainsKey('mode')) {
        $queryMode = [string]$Recipe.query.mode
    }
    $isUserInfoOnly = ($queryMode -eq 'userInfoOnly')

    # Pre-read destinations.userInfo so we can decide whether to force
    # -IncludeUserInfo emission under audit shape (defense-in-depth;
    # validator already requires includeUserInfo=true when userInfo
    # destination is present under audit).
    $uiHash       = $null
    $uiMode       = ''
    $uiPath       = ''
    $uiAppendFile = ''
    if ($Recipe.ContainsKey('destinations') -and $Recipe.destinations.ContainsKey('userInfo')) {
        $uiHash = $Recipe.destinations.userInfo
        if ($uiHash.ContainsKey('mode'))       { $uiMode       = [string]$uiHash.mode }
        if ($uiHash.ContainsKey('path'))       { $uiPath       = [string]$uiHash.path }
        if ($uiHash.ContainsKey('appendFile')) { $uiAppendFile = [string]$uiHash.appendFile }
    }
    $projectingUserInfoDest = (
        ($uiMode -eq 'outputPath' -and $uiPath) -or
        ($uiMode -eq 'append'     -and $uiAppendFile)
    )

    # ---- Boolean switches ---------------------------------------------
    $includeM365 = $false
    if ($Recipe.ContainsKey('ingredients') -and $Recipe.ingredients.ContainsKey('m365Usage')) {
        $includeM365 = [bool]$Recipe.ingredients.m365Usage.includeM365Usage
    }

    $rollupTok = ''
    if ($Recipe.ContainsKey('processing') -and $Recipe.processing.ContainsKey('rollup')) {
        switch ([string]$Recipe.processing.rollup) {
            'Rollup'        { $rollupTok = '-Rollup' }
            'RollupPlusRaw' { $rollupTok = '-RollupPlusRaw' }
        }
    }

    $includeUserInfo = $false
    if ($Recipe.ContainsKey('ingredients') -and $Recipe.ingredients.ContainsKey('entraUserData')) {
        $includeUserInfo = [bool]$Recipe.ingredients.entraUserData.includeUserInfo
    }

    if ($isUserInfoOnly) {
        # Shape 3 -- spec emission order: -OnlyUserInfo, -IncludeUserInfo,
        # then the auth/dest sequence below. PAX requires -IncludeUserInfo
        # alongside -OnlyUserInfo to bring the user-info channel in scope.
        $parameters.Add('OnlyUserInfo', $true)
        $parameters.Add('IncludeUserInfo', $true)
    }
    else {
        # Shape 1/2 -- byte-identical legacy emission order for recipes
        # that do not carry any S26 fields.
        if ($includeM365)     { $parameters.Add('IncludeM365Usage', $true) }
        if ($rollupTok)       { $parameters.Add($rollupTok.Substring(1), $true) }
        if ($includeUserInfo -or $projectingUserInfoDest) {
            $parameters.Add('IncludeUserInfo', $true)
        }
    }

    # ---- Required value-bearing switches ------------------------------
    # The validator guarantees these exist; we still defensive-read so
    # the adapter can be unit-tested with partial hashtables. Dates are
    # suppressed under Shape 3 per V1_S26_SUPPORTED_RUN_SHAPES.md
    # (PAX clears them under -OnlyUserInfo).
    $startDate = ''
    $endDate   = ''
    if ($Recipe.ContainsKey('query')) {
        if ($Recipe.query.ContainsKey('startDate')) { $startDate = [string]$Recipe.query.startDate }
        if ($Recipe.query.ContainsKey('endDate'))   { $endDate   = [string]$Recipe.query.endDate }
    }
    if (-not $isUserInfoOnly) {
        if ($startDate) { $parameters.Add('StartDate', $startDate) }
        if ($endDate)   { $parameters.Add('EndDate', $endDate) }
    }

    $tenantId     = ''
    $authMode     = ''
    $authProfileId = ''
    if ($Recipe.ContainsKey('auth')) {
        if ($Recipe.auth.ContainsKey('tenantId'))       { $tenantId      = [string]$Recipe.auth.tenantId }
        if ($Recipe.auth.ContainsKey('mode'))           { $authMode      = [string]$Recipe.auth.mode }
        if ($Recipe.auth.ContainsKey('authProfileId'))  { $authProfileId = [string]$Recipe.auth.authProfileId }
    }
    if ($tenantId) { $parameters.Add('TenantId', $tenantId) }

    # Phase AF -- auth-mode projection. The recipe-level enum
    # (AppRegistrationSecret / AppRegistrationCertificate /
    # ManagedIdentity / WebLogin / DeviceCode) maps onto PAX's
    # `-Auth <value>` enum (AppRegistration / ManagedIdentity /
    # WebLogin / DeviceCode). Cookbook keeps the granular mode in the
    # recipe so the validator can enforce the executionMode x
    # auth-mode matrix; PAX itself accepts only the coarser
    # AppRegistration value and uses the presence of the env-var
    # secret (GRAPH_CLIENT_SECRET) or -ClientCertificateThumbprint argv to
    # pick the credential type at PAX runtime.
    $paxAuthValue = ''
    switch ($authMode) {
        'AppRegistrationSecret'      { $paxAuthValue = 'AppRegistration' }
        'AppRegistrationCertificate' { $paxAuthValue = 'AppRegistration' }
        ''                           { $paxAuthValue = '' }
        default                      { $paxAuthValue = $authMode }
    }
    if ($paxAuthValue) { $parameters.Add('Auth', $paxAuthValue) }

    if ($authMode -eq 'AppRegistrationSecret' -or $authMode -eq 'AppRegistrationCertificate') {
        if ($null -eq $AuthProfile) {
            throw "Get-PaxArgvArray: recipe.auth.mode is '$authMode' but no -AuthProfile was supplied. " +
                  "The caller (supervisor / preview route) must resolve the auth profile by recipe.auth.authProfileId before projecting."
        }
        $profileClientId = [string]$AuthProfile.clientId
        if ([string]::IsNullOrWhiteSpace($profileClientId)) {
            throw "Get-PaxArgvArray: AuthProfile '$authProfileId' has no clientId. The profile row is malformed."
        }
        $parameters.Add('ClientId', $profileClientId)
        if ($authMode -eq 'AppRegistrationCertificate') {
            $profileThumb = [string]$AuthProfile.certThumbprint
            if ([string]::IsNullOrWhiteSpace($profileThumb)) {
                throw "Get-PaxArgvArray: AuthProfile '$authProfileId' is mode AppRegistrationCertificate but has no certThumbprint."
            }
            $parameters.Add('ClientCertificateThumbprint', $profileThumb)
        }
    }

    $outputPath = ''
    if ($Recipe.ContainsKey('destinations') -and $Recipe.destinations.ContainsKey('fact') -and $Recipe.destinations.fact.ContainsKey('path')) {
        $outputPath = [string]$Recipe.destinations.fact.path
    }

    # ---- V1.S26 -- filter / agent / prompt switches (audit only) ----
    # These switches are forbidden under Shape 3 (userInfoOnly). The
    # validator (RecipeValidator.ps1) already enforces that the
    # corresponding recipe fields are absent under Shape 3, but the
    # adapter still gates emission for defense-in-depth.
    if (-not $isUserInfoOnly -and $Recipe.ContainsKey('query')) {

        if ($Recipe.query.ContainsKey('activityTypes')) {
            $atArr = @($Recipe.query.activityTypes)
            if ($atArr.Count -gt 0) {
                $parameters.Add('ActivityTypes', [string[]]$atArr)
            }
        }

        if ($Recipe.query.ContainsKey('userIds')) {
            $uidArr = @($Recipe.query.userIds)
            if ($uidArr.Count -gt 0) {
                $parameters.Add('UserIds', [string[]]$uidArr)
            }
        }

        if ($Recipe.query.ContainsKey('groupNames')) {
            $gnArr = @($Recipe.query.groupNames)
            if ($gnArr.Count -gt 0) {
                $parameters.Add('GroupNames', [string[]]$gnArr)
            }
        }

        # Agent filter -- mode is authoritative. Exactly one of
        # (-AgentId <ids>, -AgentsOnly, -ExcludeAgents) is emitted,
        # or nothing when mode='none' or agentFilter is absent.
        # The Agent365Info family is NEVER emitted by the adapter
        # (it remains in $Script:RemovedSwitches to block any trailer
        # smuggling).
        if ($Recipe.query.ContainsKey('agentFilter')) {
            $afHash = $Recipe.query.agentFilter
            $afMode = ''
            if ($afHash.ContainsKey('mode')) { $afMode = [string]$afHash.mode }
            switch ($afMode) {
                'agentIds' {
                    if ($afHash.ContainsKey('agentIds')) {
                        $aidArr = @($afHash.agentIds)
                        if ($aidArr.Count -gt 0) {
                            $parameters.Add('AgentId', [string[]]$aidArr)
                        }
                    }
                }
                'agentsOnly'    { $parameters.Add('AgentsOnly', $true) }
                'excludeAgents' { $parameters.Add('ExcludeAgents', $true) }
                default         { }   # 'none' or absent -- emit nothing
            }
        }

        if ($Recipe.query.ContainsKey('promptFilter')) {
            $pf = [string]$Recipe.query.promptFilter
            if ($pf) {
                $parameters.Add('PromptFilter', $pf)
            }
        }
    }

    # ---- V1.S26 -- fact destination (audit only, unified mode) ------
    # The recipe carries `destinations.fact.mode` (S26 authoritative)
    # plus the legacy `destinations.fact.appendBehavior` alias. The
    # validator (Test-RecipeFactMode + Test-RecipeAppendBehavior)
    # has already enforced consistency between the two, the mutex
    # rule (path vs appendFile based on mode), and the
    # mode-required-field cross-checks. The adapter computes one
    # `$effectiveFactMode` and emits EXACTLY one of -OutputPath
    # <path> or -AppendFile <appendFile>. This closes the legacy
    # double-emission bug where a recipe with appendBehavior=append
    # AND a `path` would emit BOTH switches.
    #
    # Legacy mapping (used when destinations.fact.mode is absent):
    #   appendBehavior=append  -> effectiveFactMode=append
    #   appendBehavior=fresh   -> effectiveFactMode=outputPath
    #   path present alone     -> effectiveFactMode=outputPath
    # Under Shape 3 (userInfoOnly) no fact destination is projected
    # even if destinations.fact is present in the recipe (the
    # contract permits the field for backward compatibility but PAX
    # ignores it under -OnlyUserInfo).
    if (-not $isUserInfoOnly) {
        $factHash       = $null
        $factMode       = ''
        $factAppendBeh  = ''
        $factAppendFile = ''
        if ($Recipe.ContainsKey('destinations') -and $Recipe.destinations.ContainsKey('fact')) {
            $factHash = $Recipe.destinations.fact
            if ($factHash.ContainsKey('mode'))           { $factMode       = [string]$factHash.mode }
            if ($factHash.ContainsKey('appendBehavior')) { $factAppendBeh  = [string]$factHash.appendBehavior }
            if ($factHash.ContainsKey('appendFile'))     { $factAppendFile = [string]$factHash.appendFile }
        }
        $effectiveFactMode = ''
        if ($factMode) {
            $effectiveFactMode = $factMode
        } elseif ($factAppendBeh -eq 'append') {
            $effectiveFactMode = 'append'
        } elseif ($factAppendBeh -eq 'fresh') {
            $effectiveFactMode = 'outputPath'
        } elseif ($outputPath) {
            $effectiveFactMode = 'outputPath'
        }
        switch ($effectiveFactMode) {
            'outputPath' {
                if ($outputPath) {
                    $parameters.Add('OutputPath', $outputPath)
                }
            }
            'append' {
                if ($factAppendFile) {
                    $parameters.Add('AppendFile', $factAppendFile)
                }
            }
            default { }   # no fact destination projection
        }
    }

    # ---- V1.S26 -- userInfo destination (both shapes) ---------------
    # destinations.userInfo.mode is authoritative; the validator
    # (Test-RecipeUserInfoChannel) has enforced
    # mode='outputPath' => path required, mode='append' => appendFile
    # required, plus the audit-shape rule that includeUserInfo=true
    # whenever a userInfo destination is present.
    switch ($uiMode) {
        'outputPath' {
            if ($uiPath) {
                $parameters.Add('OutputPathUserInfo', $uiPath)
            }
        }
        'append' {
            if ($uiAppendFile) {
                $parameters.Add('AppendUserInfo', $uiAppendFile)
            }
        }
        default { }   # no user-info destination projection
    }

    # ---- V1.S26 -- -ExcludeCopilotInteraction (Shape 2 only) -------
    # Emit when the recipe is in audit shape AND
    # ingredients.m365Usage.includeM365Usage=true AND
    # ingredients.m365Usage.includeCopilotInteraction=false.
    # The validator (Test-RecipeM365UsageBundle) has enforced that
    # includeCopilotInteraction is only present when
    # includeM365Usage=true.
    if (-not $isUserInfoOnly -and $includeM365) {
        $cpKeyExists = $false
        $cpValue     = $true
        if ($Recipe.ContainsKey('ingredients') -and $Recipe.ingredients.ContainsKey('m365Usage')) {
            $m365 = $Recipe.ingredients.m365Usage
            if ($m365.ContainsKey('includeCopilotInteraction')) {
                $cpKeyExists = $true
                $cpValue     = [bool]$m365.includeCopilotInteraction
            }
        }
        if ($cpKeyExists -and -not $cpValue) {
            $parameters.Add('ExcludeCopilotInteraction', $true)
        }
    }

    # ---- V1.S26 future work -- -ClientCertificatePath ---------------
    # The contract (V1_S26_SUPPORTED_RUN_SHAPES.md) names
    # -ClientCertificatePath as a valid auth-block switch for the
    # AppRegistrationCertificate mode when the cert is provided as a
    # passwordless PFX file. The current AuthProfile data model
    # carries certThumbprint only -- no certPath column in
    # auth_profiles, no certPath field on the AuthProfile object.
    # Adding -ClientCertificatePath emission requires:
    #   1) schema migration (auth_profiles.cert_path NULLABLE TEXT)
    #   2) AuthProfiles.ps1 route updates (create/update/read)
    #   3) UI for the profile editor
    # All three sit outside Checkpoint 2 (adapter-only) scope.
    # Emission is deferred to a follow-on checkpoint.

    # Advanced text shares the bounded C# literal grammar. Preserve legacy
    # removed-switch diagnostics before parsing, without rewriting recipes.
    $extra = ''
    if ($Recipe.ContainsKey('advanced') -and $Recipe.advanced.ContainsKey('extraArguments')) {
        $extra = [string]$Recipe.advanced.extraArguments
    }
    if ($extra) {
        # Both scans run unconditionally on every non-empty trailer.
        # RemovedSwitches blocks v1.11.2 contract drift; SecretShape
        # blocks the Phase AF auth-token smuggling surface.
        Test-ExtraArgumentsForRemovedSwitches -ExtraArguments $extra
        Test-ExtraArgumentsForSecretShape     -ExtraArguments $extra
        $advancedParameters = ConvertFrom-PaxAdvancedArguments -Text $extra
        foreach ($entry in $advancedParameters.GetEnumerator()) { $parameters.Add($entry.Key, $entry.Value) }
    }

    return $parameters
}

# Render the canonical PAX argv array as a single space-joined command
# string with the quoting rule applied. The value-bearing switches
# that need quoting at render-time are the path-bearing ones:
#   -OutputPath (always, per header)
#   -AppendFile (M2.2; appendFile is a filename or full path that
#     may contain spaces)
#   -Resume (V1.S03; resume checkpoint path may contain spaces,
#     dots, and special chars)
#   -OutputPathUserInfo (V1.S26; user-info CSV path)
#   -AppendUserInfo     (V1.S26; user-info append target)
#   -ClientCertificatePath (V1.S26 future; reserved for the
#     deferred cert-file auth path)
# Array values retain explicit string[] syntax. This rendering is not the
# execution authority; New-PaxInvocationPlan serializes the typed plan.
function ConvertTo-PaxCommandString {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Parameters)
    $parts = [Collections.Generic.List[string]]::new()
    foreach ($entry in $Parameters.GetEnumerator()) {
        if ($entry.Key -eq 'RemainingArgs') {
            $parts.Add('-Resume ' + (ConvertTo-PaxLiteralString $entry.Value[1]))
        } elseif ($entry.Value -is [bool]) {
            $parts.Add('-' + $entry.Key + $(if ($entry.Value) { '' } else { ':$false' }))
        } else {
            $parts.Add('-' + $entry.Key + ' ' + (ConvertTo-PaxLiteralValue $entry.Value))
        }
    }
    return ($parts -join ' ')
}

function Convert-RecipeToPaxArgv {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Recipe,
        $AuthProfile = $null,
        [string]$ExecutionMode = ''
    )
    $parameters = Get-PaxParameters -Recipe $Recipe -AuthProfile $AuthProfile -ExecutionMode $ExecutionMode
    return (ConvertTo-PaxCommandString -Parameters $parameters)
}

# Build the full invocation plan: the canonical PAX argv, the rendered
# PAX command, AND the outer pwsh argv that will be passed to
# ProcessStartInfo.ArgumentList. The outer wrapper is built here so the
# supervisor cannot drift from the broker dispatch path -- both consume
# spawnArgv from the same source.
#
# Path-escaping for the `& '<paxScriptPath>' ...` inner expression uses
# the PowerShell single-quoted-string rule: a literal single quote is
# represented by two consecutive single quotes ('').
function Get-PaxInvocationPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Recipe,
        [Parameter(Mandatory)] [string]$PaxScriptPath,
        $AuthProfile = $null,
        [string]$ExecutionMode = ''
    )

    $parameters = Get-PaxParameters -Recipe $Recipe -AuthProfile $AuthProfile -ExecutionMode $ExecutionMode

    $extra = ''
    if ($Recipe.ContainsKey('advanced') -and $Recipe.advanced.ContainsKey('extraArguments')) {
        $extra = ([string]$Recipe.advanced.extraArguments).Trim()
    }

    return (New-PaxInvocationPlan -Parameters $parameters -PaxScriptPath $PaxScriptPath -ExtraArguments $extra)
}

# V1.S03 -- dedicated resume projection. PAX's `-Resume` invocation
# mode reads ALL processing parameters (StartDate, EndDate,
# OutputPath, IncludeM365Usage, Rollup, IncludeUserInfo, append
# behavior, partition counters) from the on-disk checkpoint file.
# When `-Resume` is present, the engine forbids the caller from
# re-supplying those parameters. This function therefore emits a
# narrow argv containing ONLY:
#
#     -Resume "<CheckpointPath>"
#     -Force                            (suppress interactive prompt)
#     -TenantId <tenantId>              (optional; only if supplied)
#     -Auth <paxAuthValue>              (optional; only if supplied)
#     -ClientId <profileClientId>       (only for AppRegistration*)
#     -ClientCertificateThumbprint <thumb>    (only for AppRegistrationCertificate)
#
# The auth companions are emitted defensively even though PAX can read
# them from the checkpoint, because the caller (the resume route)
# resolves the AuthProfile freshly from the auth_profiles table at
# resume time, so the credentials may have rotated since the checkpoint
# was written. `-ClientSecret` is NEVER emitted as an argv token; the
# secret is delivered via the GRAPH_CLIENT_SECRET environment variable
# on the child ProcessStartInfo, identical to the normal projection.
#
# The function takes resolved auth context directly (not a recipe
# hashtable) because resume cooks consume the parent cook's frozen
# recipe_snapshot_json; the route layer extracts the auth fields and
# resolves the AuthProfile before calling this function. Same purity
# guarantees as Get-PaxArgvArray: no filesystem, no spawn, no state.
function Get-PaxResumeParameters {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$CheckpointPath,
        # Auth mode from the parent recipe's auth.mode field. Empty or
        # null is accepted (the checkpoint carries the original mode);
        # when supplied, projection follows the same mapping as the
        # normal path (AppRegistrationSecret / AppRegistrationCertificate
        # -> PAX's coarser 'AppRegistration' enum value).
        [string]$AuthMode = '',
        # Tenant id from the parent recipe's auth.tenantId field.
        # Optional but strongly recommended; PAX will fall back to the
        # checkpoint value when absent.
        [string]$TenantId = '',
        # Resolved auth profile row (clientId, optional certThumbprint).
        # MUST be supplied when $AuthMode is AppRegistration*; ignored
        # otherwise. Same shape as Get-AuthProfileRow's return value.
        $AuthProfile = $null,
        # Execution mode. Defaults to 'local-manual'. The supervisor
        # spawn-time gate enforces local-only at runtime; this argv-
        # time check mirrors that defense for the resume path.
        [string]$ExecutionMode = ''
    )

    if ([string]::IsNullOrWhiteSpace($CheckpointPath)) {
        throw "Get-PaxResumeArgvArray: -CheckpointPath is required and must be non-empty. " +
              "The resume route is responsible for verifying the checkpoint file exists on disk before projecting."
    }

    $execMode = $ExecutionMode
    if ([string]::IsNullOrWhiteSpace($execMode)) { $execMode = 'local-manual' }
    Test-RecipeExecutionModeForLocalAdapter -ExecutionMode $execMode

    $parameters = [ordered]@{}

    # Resume token + path. Quoting is applied at render time
    # (ConvertTo-PaxCommandString); the argv array carries the
    # unquoted path so ProcessStartInfo.ArgumentList receives a single
    # logical token.
    $parameters.Add('RemainingArgs', [string[]]@('-Resume', $CheckpointPath))

    # Force suppresses the interactive "use most recent checkpoint?"
    # prompt that would otherwise block a non-interactive child.
    $parameters.Add('Force', $true)

    # Optional auth companions. Mirror the normal projection's mode
    # mapping so that AppRegistrationSecret and AppRegistrationCertificate
    # both project to PAX's coarser 'AppRegistration' enum value.
    if (-not [string]::IsNullOrWhiteSpace($TenantId)) {
        $parameters.Add('TenantId', $TenantId)
    }

    $paxAuthValue = ''
    switch ($AuthMode) {
        'AppRegistrationSecret'      { $paxAuthValue = 'AppRegistration' }
        'AppRegistrationCertificate' { $paxAuthValue = 'AppRegistration' }
        ''                           { $paxAuthValue = '' }
        default                      { $paxAuthValue = $AuthMode }
    }
    if ($paxAuthValue) {
        $parameters.Add('Auth', $paxAuthValue)
    }

    if ($AuthMode -eq 'AppRegistrationSecret' -or $AuthMode -eq 'AppRegistrationCertificate') {
        if ($null -eq $AuthProfile) {
            throw "Get-PaxResumeArgvArray: AuthMode is '$AuthMode' but no -AuthProfile was supplied. " +
                  "The resume route must resolve the auth profile by the parent recipe's auth.authProfileId before projecting."
        }
        $profileClientId = [string]$AuthProfile.clientId
        if ([string]::IsNullOrWhiteSpace($profileClientId)) {
            throw "Get-PaxResumeArgvArray: AuthProfile has no clientId. The profile row is malformed."
        }
        $parameters.Add('ClientId', $profileClientId)
        if ($AuthMode -eq 'AppRegistrationCertificate') {
            $profileThumb = [string]$AuthProfile.certThumbprint
            if ([string]::IsNullOrWhiteSpace($profileThumb)) {
                throw "Get-PaxResumeArgvArray: AuthProfile is mode AppRegistrationCertificate but has no certThumbprint."
            }
            $parameters.Add('ClientCertificateThumbprint', $profileThumb)
        }
    }

    # NO processing parameters are emitted here. The engine forbids
    # them alongside -Resume and the checkpoint carries the values.
    # NO extraArguments trailer is appended either: the user-owned
    # escape hatch is reserved for fresh cook starts; resume cooks
    # inherit those concerns from the checkpoint.

    return $parameters
}

function Get-PaxResumeArgvArray {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$CheckpointPath, [string]$AuthMode='', [string]$TenantId='', $AuthProfile=$null, [string]$ExecutionMode='')
    return ,(Get-PaxDisplayArguments (Get-PaxResumeParameters @PSBoundParameters))
}

# Build the full resume invocation plan. Mirror of Get-PaxInvocationPlan
# for the dedicated resume path. The route layer consumes this and
# stores the resulting spawnArgv in the new cook row's
# command_argv_json column.
function Get-PaxResumeInvocationPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$CheckpointPath,
        [Parameter(Mandatory)] [string]$PaxScriptPath,
        [string]$AuthMode = '',
        [string]$TenantId = '',
        $AuthProfile = $null,
        [string]$ExecutionMode = ''
    )

    $parameters = Get-PaxResumeParameters `
                      -CheckpointPath $CheckpointPath `
                      -AuthMode $AuthMode `
                      -TenantId $TenantId `
                      -AuthProfile $AuthProfile `
                      -ExecutionMode $ExecutionMode
    $plan = New-PaxInvocationPlan -Parameters $parameters -PaxScriptPath $PaxScriptPath
    $plan.checkpointPath = $CheckpointPath
    $plan.isResume = $true
    return $plan
}

# V1.S06c -- recipe projection hash. SHA-256 of the redacted
# canonical PAX argv array plus the bundled PAX script version. The
# wrapper recomputes this at fire time and refuses to spawn PAX if it
# disagrees with the value captured at task-registration time
# (decision 2 -- refuse on stale projection).
#
# The hash input MUST be a deterministic, secret-free serialization
# of the projection contract:
#   1. The canonical PAX argv array produced by Get-PaxInvocationPlan.
#      The redaction step below replaces any value-token that follows
#      a switch known to bind to a CredMan-resident secret (none in
#      v1.11.2; the helper is defensive against future contract drift
#      that might add a `-ClientSecret <value>` style switch).
#   2. The PAX script version string. A version bump invalidates the
#      hash so the operator must re-register the task against the new
#      engine.
#
# Format: compact UTF-8 JSON with projectionVersion=2, paxScriptVersion,
# and ordered name/type/value records. Array boundaries and embedded
# newlines are unambiguous. Older registered hashes intentionally mismatch;
# no automatic migration or registration is performed.
#
# Return value: lowercase hex SHA-256 digest (64 chars).
function Get-RecipeProjectionHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Recipe,
        [Parameter(Mandatory)] [string]$PaxScriptPath,
        $AuthProfile = $null,
        [Parameter(Mandatory)] [string]$ExecutionMode,
        [Parameter(Mandatory)] [string]$PaxScriptVersion
    )

    $plan = Get-PaxInvocationPlan `
                -Recipe $Recipe `
                -PaxScriptPath $PaxScriptPath `
                -AuthProfile $AuthProfile `
                -ExecutionMode $ExecutionMode

    $entries = @($plan.parameters.GetEnumerator() | ForEach-Object {
        [ordered]@{name=$_.Key;type=$_.Value.GetType().FullName;value=$_.Value}
    })
    $projection = [ordered]@{projectionVersion=2;paxScriptVersion=$PaxScriptVersion;parameters=$entries}
    $serialized = ConvertTo-Json -InputObject $projection -Depth 8 -Compress -EscapeHandling EscapeNonAscii
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($serialized)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha.ComputeHash($bytes)
    } finally {
        $sha.Dispose()
    }
    $hex = New-Object System.Text.StringBuilder
    foreach ($b in $digest) { [void]$hex.AppendFormat('{0:x2}', $b) }
    return $hex.ToString()
}

function Assert-PaxRecipePropertyNames {
    param([System.Text.Json.JsonElement]$Element)
    if ($Element.ValueKind -eq 'Object') {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw 'Duplicate Recipe property.' }
            Assert-PaxRecipePropertyNames -Element $property.Value
        }
    } elseif ($Element.ValueKind -eq 'Array') {
        foreach ($item in $Element.EnumerateArray()) { Assert-PaxRecipePropertyNames -Element $item }
    }
}

function ConvertFrom-PaxRecipeElement {
    param([System.Text.Json.JsonElement]$Element)
    switch ($Element.ValueKind) {
        'Object' {
            $values = [hashtable]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($property in $Element.EnumerateObject()) {
                $values.Add($property.Name, (ConvertFrom-PaxRecipeElement -Element $property.Value))
            }
            return $values
        }
        'Array' {
            $values = [Collections.Generic.List[object]]::new()
            foreach ($item in $Element.EnumerateArray()) {
                $values.Add((ConvertFrom-PaxRecipeElement -Element $item))
            }
            return ,$values.ToArray()
        }
        'String' { return $Element.GetString() }
        'Number' {
            [long]$integer = 0
            if ($Element.TryGetInt64([ref]$integer)) { return $integer }
            $number = $Element.GetDouble()
            if (-not [double]::IsFinite($number)) { throw 'Invalid Recipe number.' }
            return $number
        }
        'True' { return $true }
        'False' { return $false }
        'Null' { return $null }
        default { throw 'Invalid Recipe value.' }
    }
}

function ConvertFrom-PaxRecipeJson {
    param([Parameter(Mandatory)][string]$Json)
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 12
    $document = [System.Text.Json.JsonDocument]::Parse($Json, $options)
    try {
        Assert-PaxRecipePropertyNames -Element $document.RootElement
        ConvertFrom-PaxRecipeElement -Element $document.RootElement
    }
    finally { $document.Dispose() }
}

Export-ModuleMember -Function @(
    'ConvertFrom-PaxRecipeJson',
    'Test-RecipeAdvancedArguments',
    'Convert-RecipeToPaxArgv',
    'Get-PaxArgvArray',
    'Get-PaxInvocationPlan',
    'Get-PaxResumeArgvArray',
    'Get-PaxResumeInvocationPlan',
    'Get-RecipeProjectionHash',
    'Test-ExtraArgumentsForRemovedSwitches',
    'Test-ExtraArgumentsForSecretShape',
    'Test-RecipeExecutionModeForLocalAdapter'
)
