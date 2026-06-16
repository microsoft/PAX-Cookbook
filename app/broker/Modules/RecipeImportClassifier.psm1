Set-StrictMode -Version Latest

# RecipeImportClassifier.psm1
#
# Pure helper module. No I/O, no SQLite, no PAX, no network, no
# recipe mutation. Answers two independent questions:
#
#   1. What extension family does this filename belong to?
#         (.json.pax / .paxrecipe.json / .json.paxlite / .json / unknown)
#
#   2. What envelope class does this already-parsed JSON object belong to?
#         (Full Cookbook Recipe Takeout / Mini-Kitchen lite recipe / unknown)
#
# The envelope class is AUTHORITATIVE -- the extension is a hint
# only. A file named foo.json.pax that parses as a Mini-Kitchen lite
# envelope is processed as a lite import (and a soft warning is
# surfaced). The converse is also true: a file named foo.json.paxlite
# that parses as a Full Cookbook envelope is processed as a Cookbook
# import. This makes accidental renames non-fatal and ensures the
# import handler is driven by content, never by filename.
#
# Both predicate sets are intentionally independent so the broker and
# the SPA can call them in either order. The SPA classifies by
# envelope right after JSON.parse (extension is only used to filter
# the file picker); the broker route classifies by envelope as the
# first thing it does inside each handler.

# ---------------------------------------------------------
# Extension constants
# ---------------------------------------------------------

# Full Cookbook Recipe Takeout preferred extension (this is what the
# broker emits on export). Locked at .json.pax per the
# import/export extension contract.
$Script:ClassifierCookbookPreferredExt = '.json.pax'

# Full Cookbook Recipe Takeout legacy extensions accepted on import.
# .paxrecipe.json is the prior preferred extension and stays accepted
# verbatim so files exported by older Cookbook builds keep working
# without rename. Generic .json is accepted because chefs commonly
# strip extensions when emailing files around.
$Script:ClassifierCookbookAcceptedExts = @(
    '.json.pax',
    '.paxrecipe.json',
    '.json'
)

# Mini-Kitchen lite preferred extension (the GitHub-Pages Mini-Kitchen
# downloads its files with this suffix).
$Script:ClassifierLitePreferredExt = '.json.paxlite'

# Mini-Kitchen lite accepted extensions on import. .json is accepted
# for the same reason as above.
$Script:ClassifierLiteAcceptedExts = @(
    '.json.paxlite',
    '.json'
)

# Combined accept list for the file picker. Stable order: Cookbook
# preferred first, then lite preferred, then legacy and ambiguous.
$Script:ClassifierAllAcceptedExts = @(
    '.json.pax',
    '.json.paxlite',
    '.paxrecipe.json',
    '.json'
)

# ---------------------------------------------------------
# Envelope kind constants
# ---------------------------------------------------------

# Full Cookbook Recipe Takeout envelope kind. Locked verbatim;
# changing this value would break every existing exported takeout
# file the broker has ever produced.
$Script:ClassifierCookbookEnvelopeKind = 'pax-cookbook.recipe-takeout'

# Mini-Kitchen lite envelope kind. Locked by the canonical contract
# published at the GitHub-Pages Mini-Kitchen site. The string is
# matched verbatim (case sensitive) because the Mini-Kitchen emits
# this exact literal in every export.
$Script:ClassifierLiteEnvelopeKind = 'pax-cookbook-mini-recipe'

# Mini-Kitchen lite schema version. Note: the contract defines this
# as a STRING ('1.0'), not a number. Strict string equality is the
# guard against a future numeric schema-version drift slipping
# through type coercion.
$Script:ClassifierLiteSchemaVersion = '1.0'

# ---------------------------------------------------------
# Extension classifier
# ---------------------------------------------------------

function Get-PaxRecipeImportPreferredCookbookExtension {
    return $Script:ClassifierCookbookPreferredExt
}

function Get-PaxRecipeImportPreferredLiteExtension {
    return $Script:ClassifierLitePreferredExt
}

function Get-PaxRecipeImportCookbookAcceptedExtensions {
    return ,@($Script:ClassifierCookbookAcceptedExts)
}

function Get-PaxRecipeImportLiteAcceptedExtensions {
    return ,@($Script:ClassifierLiteAcceptedExts)
}

function Get-PaxRecipeImportAllAcceptedExtensions {
    return ,@($Script:ClassifierAllAcceptedExts)
}

function Get-PaxRecipeImportFilenameExtensionClass {
    # Returns one of:
    #   'cookbook-preferred' -- ends with .json.pax (case-insensitive)
    #   'cookbook-legacy'    -- ends with .paxrecipe.json
    #   'lite-preferred'     -- ends with .json.paxlite
    #   'ambiguous-json'     -- ends with .json but none of the above
    #   'unknown'            -- any other or empty
    #
    # Filename case is normalised before comparison; the doubled
    # suffixes (.json.pax, .json.paxlite, .paxrecipe.json) are checked
    # BEFORE the bare .json case so the more specific match wins.
    param([string]$Filename)
    if ([string]::IsNullOrWhiteSpace($Filename)) { return 'unknown' }
    $lower = $Filename.ToLowerInvariant()
    if ($lower.EndsWith('.json.pax'))      { return 'cookbook-preferred' }
    if ($lower.EndsWith('.json.paxlite'))  { return 'lite-preferred' }
    if ($lower.EndsWith('.paxrecipe.json')) { return 'cookbook-legacy' }
    if ($lower.EndsWith('.json'))          { return 'ambiguous-json' }
    return 'unknown'
}

# ---------------------------------------------------------
# Envelope classifier
# ---------------------------------------------------------

function Get-PaxRecipeImportEnvelopeClass {
    # Inspects an already-parsed JSON value and returns:
    #   @{ class = 'cookbook' | 'lite' | 'unknown'
    #      kindSeen = <string>|$null
    #      reason   = <short tag describing why> }
    #
    # The class is authoritative: routing decisions MUST be based on
    # this value, not on the filename. The 'reason' tag is for the
    # SPA / broker to surface a friendly error message when class is
    # 'unknown'.
    #
    # Recognised reasons (class='unknown'):
    #   'not_object'             -- value is null / array / scalar
    #   'kind_missing'           -- envelope has no 'kind' field
    #   'kind_not_string'        -- 'kind' is present but not a string
    #   'kind_unrecognised'      -- 'kind' is a string but not one of
    #                                the two recognised constants
    #   'lite_schema_version_missing'
    #                            -- 'kind' matches lite but
    #                                'schemaVersion' is absent
    #   'lite_schema_version_invalid_type'
    #                            -- 'schemaVersion' is not a string
    #   'lite_schema_version_unsupported'
    #                            -- 'schemaVersion' string is not '1.0'
    #
    # Recognised reasons (class='cookbook' | class='lite'):
    #   'ok'                     -- envelope matched the corresponding
    #                                kind constant (and, for lite, the
    #                                schemaVersion string check passed)
    param($ParsedEnvelope)

    if ($null -eq $ParsedEnvelope -or
        -not ($ParsedEnvelope -is [System.Collections.IDictionary])) {
        return @{
            class    = 'unknown'
            kindSeen = $null
            reason   = 'not_object'
        }
    }

    if (-not $ParsedEnvelope.Contains('kind')) {
        return @{
            class    = 'unknown'
            kindSeen = $null
            reason   = 'kind_missing'
        }
    }
    $rawKind = $ParsedEnvelope['kind']
    if ($null -eq $rawKind -or -not ($rawKind -is [string])) {
        return @{
            class    = 'unknown'
            kindSeen = $null
            reason   = 'kind_not_string'
        }
    }
    $kind = [string]$rawKind

    if ($kind -eq $Script:ClassifierCookbookEnvelopeKind) {
        return @{
            class    = 'cookbook'
            kindSeen = $kind
            reason   = 'ok'
        }
    }

    if ($kind -eq $Script:ClassifierLiteEnvelopeKind) {
        if (-not $ParsedEnvelope.Contains('schemaVersion')) {
            return @{
                class    = 'unknown'
                kindSeen = $kind
                reason   = 'lite_schema_version_missing'
            }
        }
        $rawVer = $ParsedEnvelope['schemaVersion']
        if ($null -eq $rawVer -or -not ($rawVer -is [string])) {
            return @{
                class    = 'unknown'
                kindSeen = $kind
                reason   = 'lite_schema_version_invalid_type'
            }
        }
        if ([string]$rawVer -ne $Script:ClassifierLiteSchemaVersion) {
            return @{
                class    = 'unknown'
                kindSeen = $kind
                reason   = 'lite_schema_version_unsupported'
            }
        }
        return @{
            class    = 'lite'
            kindSeen = $kind
            reason   = 'ok'
        }
    }

    return @{
        class    = 'unknown'
        kindSeen = $kind
        reason   = 'kind_unrecognised'
    }
}

# ---------------------------------------------------------
# Soft cross-check: does envelope class agree with filename class?
# ---------------------------------------------------------

function Test-PaxRecipeImportFilenameMatchesEnvelope {
    # Returns @{ ok = $bool; warning = <string>|$null }.
    #
    # ok = $true when the filename extension class and the envelope
    # class are consistent (or the filename is ambiguous-json, in
    # which case we make no judgement). ok = $false when they clearly
    # disagree -- e.g. a .json.paxlite file that parses as a Cookbook
    # envelope, or a .json.pax file that parses as a lite envelope.
    #
    # The warning string is the machine tag the caller can surface as
    # a soft notice. The broker MUST NOT use this function as a hard
    # gate -- the envelope class always wins. This is purely
    # advisory.
    param(
        [string]$Filename,
        $ParsedEnvelope
    )
    $extClass = Get-PaxRecipeImportFilenameExtensionClass -Filename $Filename
    $envClass = (Get-PaxRecipeImportEnvelopeClass -ParsedEnvelope $ParsedEnvelope).class

    if ($envClass -eq 'unknown') {
        return @{ ok = $true; warning = $null }
    }
    if ($extClass -eq 'unknown' -or $extClass -eq 'ambiguous-json') {
        return @{ ok = $true; warning = $null }
    }
    if (($extClass -eq 'cookbook-preferred' -or $extClass -eq 'cookbook-legacy') -and $envClass -eq 'cookbook') {
        return @{ ok = $true; warning = $null }
    }
    if ($extClass -eq 'lite-preferred' -and $envClass -eq 'lite') {
        return @{ ok = $true; warning = $null }
    }
    return @{
        ok      = $false
        warning = 'filename_extension_disagrees_with_envelope_class'
    }
}

Export-ModuleMember -Function @(
    'Get-PaxRecipeImportPreferredCookbookExtension'
    'Get-PaxRecipeImportPreferredLiteExtension'
    'Get-PaxRecipeImportCookbookAcceptedExtensions'
    'Get-PaxRecipeImportLiteAcceptedExtensions'
    'Get-PaxRecipeImportAllAcceptedExtensions'
    'Get-PaxRecipeImportFilenameExtensionClass'
    'Get-PaxRecipeImportEnvelopeClass'
    'Test-PaxRecipeImportFilenameMatchesEnvelope'
)
