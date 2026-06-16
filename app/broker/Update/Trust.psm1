#requires -Version 7.4

# Trust.psm1 -- Deterministic, read-only trust evaluation for staged
# update packages.
#
# This module is INTENTIONALLY a read-only surface. The broker never
# writes, creates, or modifies any trust artifact. Every sidecar this
# module reads is either:
#
#   - produced by the update download path itself (.metadata.json,
#     written once at staging time and containing the SHA-256 that
#     was successfully verified against the manifest snapshot), OR
#
#   - produced by the release-signing pipeline
#     (`tools/release/Sign-Release.ps1`) and shipped alongside the
#     package (.sig, .signer.json, .sha256), OR
#
#   - operator-supplied (`Trust/trusted-signers.json`).
#
# As of Phase R this module DOES perform real cryptographic signature
# verification via the sibling `Signature.psm1` module. The verifier
# is itself read-only: it cannot produce signatures, cannot load a
# private key, and cannot mutate the OS trust store.
#
# Truthful trust-state contract:
#
#   `signatureVerified = $true` is emitted IF AND ONLY IF every step
#   succeeded:
#       * `.sig` envelope present, parsed, and schema-valid
#       * embedded cert's own SHA-1 thumbprint matched the envelope
#       * recomputed SHA-256 of the package matched the envelope
#       * RSA-PKCS1v15-SHA256 verification of the signature succeeded
#       * the cert's thumbprint appears in trusted-signers.json
#
#   Any other configuration yields `signatureVerified = $false` and a
#   non-terminal `overallStatus` describing the actual on-disk truth
#   (`unsigned`, `signatureInvalid`, `signerUnknown`, etc.).
#
# Public surface:
#   - Get-PackageTrustState     -- Evaluate a single staged package.
#   - Read-TrustedSigners       -- Load the operator allow-list (or @()).
#   - Test-SignerMetadataSchema -- Validate a .signer.json document.
#   - Test-TrustedSignersSchema -- Validate the trusted-signers.json doc.

# ---------------------------------------------------------------------
# Schema constants. Same hard-reject doctrine as Manifest.psm1: any
# unknown top-level field is rejected outright, no silent forward-
# compatibility.
# ---------------------------------------------------------------------
$Script:TrustedSignersSchemaVersions = @(1)
$Script:SignerMetadataSchemaVersions  = @(1)

$Script:AllowedTrustedSignersTopKeys = @('schemaVersion', 'signers')
$Script:AllowedTrustedSignerEntryKeys = @('name', 'certThumbprint', 'addedAtUtc', 'notes')

$Script:AllowedSignerMetadataKeys = @(
    'schemaVersion',
    'signerName',
    'signerEmail',
    'certThumbprint',
    'certSubject',
    'signedAtUtc',
    'signatureAlgorithm',
    'signatureFile'
)

# Filesystem layout. Sidecars live next to the package; the trusted-
# signers allowlist lives under <workspace>\Trust\.
$Script:TrustSubfolder            = 'Trust'
$Script:TrustedSignersFileName    = 'trusted-signers.json'

# ---------------------------------------------------------------------
# Lazy import of the cryptographic verifier. If the sibling module is
# absent (e.g. partial install, or a deliberately-stripped-down test
# harness), Get-PackageTrustState degrades to its pre-Phase-R behavior:
# `signatureValid` and `signatureVerified` both stay $false and the
# overall status falls back to the truthful `signaturePresentNotVerified`.
# ---------------------------------------------------------------------
$Script:SignatureModulePath = Join-Path $PSScriptRoot 'Signature.psm1'
$Script:SignatureModuleLoaded = $false
if (Test-Path -LiteralPath $Script:SignatureModulePath -PathType Leaf) {
    try {
        Import-Module -Force $Script:SignatureModulePath -ErrorAction Stop | Out-Null
        $Script:SignatureModuleLoaded = $true
    } catch {
        $Script:SignatureModuleLoaded = $false
    }
}

# ---------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------
function Test-Hashtable {
    param($Value)
    return ($Value -is [hashtable] -or $Value -is [System.Collections.Specialized.OrderedDictionary])
}

function Get-HashtableKeys {
    param($Value)
    if ($Value -is [hashtable]) { return [string[]]@($Value.Keys) }
    if ($Value -is [System.Collections.Specialized.OrderedDictionary]) { return [string[]]@($Value.Keys) }
    return @()
}

function Test-RequiredString {
    param($Value)
    return ($Value -is [string] -and -not [string]::IsNullOrWhiteSpace($Value))
}

function Format-Thumbprint {
    # Normalize a SHA-1 cert thumbprint to bare uppercase hex (no
    # colons, no whitespace). Returns $null if the input isn't a
    # plausible thumbprint.
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $clean = ($Value -replace '[^0-9A-Fa-f]','').ToUpperInvariant()
    if ($clean.Length -ne 40) { return $null }
    return $clean
}

# ---------------------------------------------------------------------
# Schema: trusted-signers.json
# ---------------------------------------------------------------------
function Test-TrustedSignersSchema {
    param([Parameter(Mandatory)]$Document)

    if (-not (Test-Hashtable $Document)) {
        return @{ ok = $false; error = 'document_not_object'; message = 'trusted-signers.json is not a JSON object.' }
    }

    foreach ($k in (Get-HashtableKeys $Document)) {
        if ($Script:AllowedTrustedSignersTopKeys -notcontains $k) {
            return @{ ok = $false; error = 'unknown_field'; message = ('Unknown top-level field in trusted-signers.json: "' + $k + '".') }
        }
    }

    if (-not $Document.Contains('schemaVersion')) {
        return @{ ok = $false; error = 'missing_field'; message = '"schemaVersion" is required in trusted-signers.json.' }
    }
    $sv = $Document['schemaVersion']
    if (-not ($sv -is [int] -or $sv -is [long])) {
        return @{ ok = $false; error = 'type_mismatch'; message = '"schemaVersion" must be an integer.' }
    }
    if ($Script:TrustedSignersSchemaVersions -notcontains [int]$sv) {
        return @{ ok = $false; error = 'unsupported_schema_version'; message = ('trusted-signers.json schemaVersion ' + $sv + ' is not supported.') }
    }

    if (-not $Document.Contains('signers')) {
        return @{ ok = $false; error = 'missing_field'; message = '"signers" is required in trusted-signers.json.' }
    }
    $signers = $Document['signers']
    if (-not ($signers -is [System.Collections.IEnumerable]) -or $signers -is [string]) {
        return @{ ok = $false; error = 'type_mismatch'; message = '"signers" must be a JSON array.' }
    }

    $idx = -1
    foreach ($entry in $signers) {
        $idx++
        if (-not (Test-Hashtable $entry)) {
            return @{ ok = $false; error = 'type_mismatch'; message = ('signers[' + $idx + '] is not a JSON object.') }
        }
        foreach ($k in (Get-HashtableKeys $entry)) {
            if ($Script:AllowedTrustedSignerEntryKeys -notcontains $k) {
                return @{ ok = $false; error = 'unknown_field'; message = ('signers[' + $idx + '] has an unknown field: "' + $k + '".') }
            }
        }
        if (-not (Test-RequiredString $entry['name'])) {
            return @{ ok = $false; error = 'missing_field'; message = ('signers[' + $idx + '].name is required.') }
        }
        $tp = Format-Thumbprint -Value ([string]$entry['certThumbprint'])
        if (-not $tp) {
            return @{ ok = $false; error = 'invalid_thumbprint'; message = ('signers[' + $idx + '].certThumbprint must be a 40-character SHA-1 hex thumbprint.') }
        }
    }

    return @{ ok = $true }
}

# ---------------------------------------------------------------------
# Schema: per-package <name>.signer.json
# ---------------------------------------------------------------------
function Test-SignerMetadataSchema {
    param([Parameter(Mandatory)]$Metadata)

    if (-not (Test-Hashtable $Metadata)) {
        return @{ ok = $false; error = 'metadata_not_object'; message = 'signer metadata is not a JSON object.' }
    }
    foreach ($k in (Get-HashtableKeys $Metadata)) {
        if ($Script:AllowedSignerMetadataKeys -notcontains $k) {
            return @{ ok = $false; error = 'unknown_field'; message = ('signer metadata has an unknown field: "' + $k + '".') }
        }
    }

    if (-not $Metadata.Contains('schemaVersion')) {
        return @{ ok = $false; error = 'missing_field'; message = '"schemaVersion" is required.' }
    }
    $sv = $Metadata['schemaVersion']
    if (-not ($sv -is [int] -or $sv -is [long])) {
        return @{ ok = $false; error = 'type_mismatch'; message = '"schemaVersion" must be an integer.' }
    }
    if ($Script:SignerMetadataSchemaVersions -notcontains [int]$sv) {
        return @{ ok = $false; error = 'unsupported_schema_version'; message = ('signer metadata schemaVersion ' + $sv + ' is not supported.') }
    }

    foreach ($req in @('signerName','certThumbprint','signedAtUtc','signatureAlgorithm','signatureFile')) {
        # Coerce the raw JSON-decoded value to a string. PowerShell's
        # ConvertFrom-Json may auto-promote ISO-8601 timestamp strings
        # like signedAtUtc to [DateTime] objects, so we accept any
        # non-null value whose string form is non-empty.
        $raw    = $Metadata[$req]
        $valStr = if ($null -eq $raw) { '' } else { [string]$raw }
        if (-not (Test-RequiredString $valStr)) {
            return @{ ok = $false; error = 'missing_field'; message = ('"' + $req + '" is required in signer metadata.') }
        }
    }
    $tp = Format-Thumbprint -Value ([string]$Metadata['certThumbprint'])
    if (-not $tp) {
        return @{ ok = $false; error = 'invalid_thumbprint'; message = '"certThumbprint" must be a 40-character SHA-1 hex thumbprint.' }
    }

    return @{ ok = $true; normalizedThumbprint = $tp }
}

# ---------------------------------------------------------------------
# trusted-signers.json loader
# ---------------------------------------------------------------------
function Read-TrustedSigners {
    param([Parameter(Mandatory)][string]$WorkspacePath)

    $path = Join-Path (Join-Path $WorkspacePath $Script:TrustSubfolder) $Script:TrustedSignersFileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [pscustomobject]@{
            present = $false
            path    = $path
            error   = $null
            signers = @()
        }
    }

    $doc = $null
    try {
        $doc = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -AsHashtable -Depth 10
    } catch {
        return [pscustomobject]@{
            present = $true
            path    = $path
            error   = 'parse_failed'
            message = $_.Exception.Message
            signers = @()
        }
    }

    $schema = Test-TrustedSignersSchema -Document $doc
    if (-not $schema.ok) {
        return [pscustomobject]@{
            present = $true
            path    = $path
            error   = $schema.error
            message = $schema.message
            signers = @()
        }
    }

    # Normalize each entry's thumbprint to uppercase hex with no
    # separators so callers can compare without re-formatting.
    $normalized = @()
    foreach ($entry in @($doc['signers'])) {
        $normalized += [pscustomobject]@{
            name           = [string]$entry['name']
            certThumbprint = (Format-Thumbprint -Value ([string]$entry['certThumbprint']))
            addedAtUtc     = if ($entry.Contains('addedAtUtc')) { [string]$entry['addedAtUtc'] } else { $null }
            notes          = if ($entry.Contains('notes'))      { [string]$entry['notes'] }      else { $null }
        }
    }

    return [pscustomobject]@{
        present = $true
        path    = $path
        error   = $null
        signers = $normalized
    }
}

# ---------------------------------------------------------------------
# Per-package trust evaluation. Read-only; never writes.
# ---------------------------------------------------------------------
function Get-PackageTrustState {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$WorkspacePath
    )

    # Sidecar paths derived deterministically from the package path.
    $metadataPath = $PackagePath + '.metadata.json'
    $sha256Path   = $PackagePath + '.sha256'
    $sigPath      = $PackagePath + '.sig'
    $signerPath   = $PackagePath + '.signer.json'

    $filename = Split-Path -Path $PackagePath -Leaf

    $result = [ordered]@{
        schemaVersion         = 1
        filename              = $filename
        path                  = $PackagePath
        sidecarPaths          = [ordered]@{
            metadata = $metadataPath
            sha256   = $sha256Path
            sig      = $sigPath
            signer   = $signerPath
        }
        hashKnown             = $false
        hashSource            = 'none'
        expectedSha256        = $null
        actualSha256          = $null
        hashMatches           = $false
        signaturePresent      = $false
        signerMetadataPresent = $false
        signerMetadataValid   = $false
        signerName            = $null
        signerCertThumbprint  = $null
        signerKnown           = $false
        # signatureValid: result of real cryptographic verification of
        # the .sig envelope against the package bytes. Independent of
        # whether the signer is trusted.
        signatureValid        = $false
        signatureError        = $null
        # signatureVerified: composed below from signatureValid AND
        # signerKnown. Phase R replaced the prior hard-coded $false
        # with a deterministic conditional.
        signatureVerified     = $false
        overallStatus         = 'hashUnknown'
        notes                 = @()
    }

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        $result.overallStatus = 'missing'
        $result.notes += 'Package file does not exist on disk.'
        return [pscustomobject]$result
    }

    # ----------- Recompute actual SHA-256 -----------
    try {
        $result.actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $PackagePath).Hash.ToUpperInvariant()
    } catch {
        $result.notes += ('Could not compute SHA-256: ' + $_.Exception.Message)
        $result.overallStatus = 'hashUnknown'
        return [pscustomobject]$result
    }

    # ----------- Discover expected hash from sidecars -----------
    # Preference order: .metadata.json (broker-verified at staging
    # time, highest provenance) > .sha256 (operator-supplied).
    if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
        try {
            $meta = Get-Content -LiteralPath $metadataPath -Raw -ErrorAction Stop | ConvertFrom-Json -AsHashtable -Depth 12
            if ($meta -and (Test-RequiredString $meta['sha256'])) {
                $candidate = ([string]$meta['sha256']).ToUpperInvariant()
                if ($candidate -match '^[0-9A-F]{64}$') {
                    $result.expectedSha256 = $candidate
                    $result.hashKnown      = $true
                    $result.hashSource     = 'metadata'
                }
            }
        } catch {
            $result.notes += ('Could not parse metadata sidecar: ' + $_.Exception.Message)
        }
    }

    if (-not $result.hashKnown -and (Test-Path -LiteralPath $sha256Path -PathType Leaf)) {
        try {
            $raw = Get-Content -LiteralPath $sha256Path -Raw -ErrorAction Stop
            # Accept both "<hex>" and "<hex>  <filename>" (sha256sum format).
            $m = [regex]::Match($raw, '([0-9A-Fa-f]{64})')
            if ($m.Success) {
                $result.expectedSha256 = $m.Groups[1].Value.ToUpperInvariant()
                $result.hashKnown      = $true
                $result.hashSource     = 'sha256File'
            }
        } catch {
            $result.notes += ('Could not parse .sha256 sidecar: ' + $_.Exception.Message)
        }
    }

    if ($result.hashKnown) {
        $result.hashMatches = ($result.actualSha256 -eq $result.expectedSha256)
    }

    # ----------- Signature + signer sidecars -----------
    $result.signaturePresent      = Test-Path -LiteralPath $sigPath    -PathType Leaf
    $result.signerMetadataPresent = Test-Path -LiteralPath $signerPath -PathType Leaf

    if ($result.signerMetadataPresent) {
        try {
            $sm = Get-Content -LiteralPath $signerPath -Raw -ErrorAction Stop | ConvertFrom-Json -AsHashtable -Depth 8
            $schema = Test-SignerMetadataSchema -Metadata $sm
            if ($schema.ok) {
                $result.signerMetadataValid  = $true
                $result.signerName           = [string]$sm['signerName']
                $result.signerCertThumbprint = $schema.normalizedThumbprint
            } else {
                $result.notes += ('signer metadata invalid: ' + $schema.message)
            }
        } catch {
            $result.notes += ('Could not parse signer metadata: ' + $_.Exception.Message)
        }
    }

    # ----------- Cryptographic verification of .sig (Phase R) -----------
    # Real verification only runs if (a) Signature.psm1 successfully
    # loaded, (b) the package hash is known and matches, and (c) a
    # .sig file is on disk. We never short-circuit the verifier on a
    # "trusted-looking" signer -- crypto first, identity second.
    $verifierThumbprint = $null
    if ($Script:SignatureModuleLoaded -and $result.signaturePresent) {
        $verifyCmd = Get-Command -Name 'Test-PackageSignature' -ErrorAction SilentlyContinue
        if ($verifyCmd) {
            try {
                $verify = & $verifyCmd -PackagePath $PackagePath -EnvelopePath $sigPath
                if ($verify.ok) {
                    $result.signatureValid = $true
                    $verifierThumbprint    = $verify.certThumbprint
                } else {
                    $result.signatureValid = $false
                    $result.signatureError = $verify.error
                    if ($verify.errorMessage) {
                        $result.notes += ('signature verification failed: ' + $verify.errorMessage)
                    }
                }
            } catch {
                $result.signatureValid = $false
                $result.signatureError = 'verifier_threw'
                $result.notes += ('signature verifier threw: ' + $_.Exception.Message)
            }
        }
    }

    # ----------- Allowlist lookup (identity match) -----------
    # We allow EITHER the verified embedded-cert thumbprint OR the
    # .signer.json thumbprint to drive identity matching, but the
    # crypto-derived thumbprint wins when both are present. This
    # ensures `signatureVerified = $true` can never be claimed against
    # a thumbprint that doesn't appear in the cryptographic envelope.
    $thumbForLookup = if ($verifierThumbprint) { $verifierThumbprint } else { $result.signerCertThumbprint }
    if ($thumbForLookup) {
        $trusted = Read-TrustedSigners -WorkspacePath $WorkspacePath
        if ($trusted.error) {
            $result.notes += ('trusted-signers.json invalid: ' + $trusted.error)
        }
        foreach ($entry in @($trusted.signers)) {
            if ($entry.certThumbprint -eq $thumbForLookup) {
                $result.signerKnown = $true
                break
            }
        }
        # If the verifier produced a thumbprint and .signer.json was
        # parsed, record the authoritative thumbprint on the result.
        if ($verifierThumbprint -and -not $result.signerCertThumbprint) {
            $result.signerCertThumbprint = $verifierThumbprint
        }
    }

    # ----------- Overall status decision tree -----------
    # The terminal status `hashMismatch` overrides every other state:
    # if the bytes on disk don't match the only hash we have for them,
    # nothing else is trustworthy.
    if (-not $result.hashKnown) {
        $result.overallStatus = 'hashUnknown'
    } elseif (-not $result.hashMatches) {
        $result.overallStatus = 'hashMismatch'
    } elseif (-not $result.signaturePresent) {
        # Hash matches, no signature on disk. Truthful state.
        $result.overallStatus = 'unsigned'
    } elseif (-not $Script:SignatureModuleLoaded) {
        # Signature module unavailable. Cannot make any cryptographic
        # claim. Truthful fallback to pre-Phase-R behavior.
        $result.overallStatus = 'signaturePresentNotVerified'
    } elseif (-not $result.signatureValid) {
        # .sig file present, but crypto verification failed for any
        # reason (parse error, schema mismatch, bad signature bytes,
        # cert/thumbprint mismatch, etc.). Emit `signatureInvalid`.
        $result.overallStatus = 'signatureInvalid'
    } elseif (-not $result.signerKnown) {
        # Crypto verified, but signer is not in trusted-signers.json.
        $result.overallStatus = 'signerUnknown'
    } else {
        # Hash matched + signature cryptographically verified +
        # signer is in the allowlist. This is the only path that
        # produces signatureVerified = $true.
        $result.overallStatus = 'verified'
    }

    # Phase R invariant: signatureVerified is derived from real
    # cryptographic state and the operator allowlist. It MUST NEVER
    # be $true in any other branch. This is a defense-in-depth check;
    # if a future edit changes the decision tree above, this guard
    # prevents accidental escalation.
    $result.signatureVerified = (
        $result.hashKnown -and
        $result.hashMatches -and
        $result.signaturePresent -and
        $result.signatureValid -and
        $result.signerKnown -and
        $result.overallStatus -eq 'verified')

    return [pscustomobject]$result
}

Export-ModuleMember -Function `
    Get-PackageTrustState, `
    Read-TrustedSigners, `
    Test-SignerMetadataSchema, `
    Test-TrustedSignersSchema
