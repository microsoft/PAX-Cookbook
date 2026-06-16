#requires -Version 7.4

# Signature.psm1 -- Deterministic, read-only cryptographic verification
# of detached package signatures. This module is INTENTIONALLY a
# verification-only surface. It can never sign, never load a private
# key, never mutate the OS trust store, never reach over the network.
#
# A `.sig` sidecar is a small UTF-8 JSON document next to the package
# file. Its schema is closed: every recognized key must be present and
# every unknown key is a hard reject.
#
# .sig envelope schema (closed):
#   {
#     "schemaVersion":        1,
#     "packageFile":          "pax-cookbook-1.0.0.zip",
#     "packageSha256":        "<UPPERCASE 64-hex>",
#     "hashAlgorithm":        "SHA256",
#     "signatureAlgorithm":   "RSA-PKCS1v15-SHA256",
#     "signatureBase64":      "<base64 of raw signature bytes>",
#     "signerCertBase64":     "<base64 of DER-encoded signer cert>",
#     "signerCertThumbprint": "<UPPERCASE 40-hex SHA-1 thumbprint>",
#     "signedAtUtc":          "ISO-8601 UTC timestamp"
#   }
#
# Verification flow (atomic, deterministic, no fallbacks):
#   1. Read .sig file; parse JSON; validate envelope schema (hard reject).
#   2. Decode signer cert from base64; recompute its SHA-1 thumbprint;
#      must equal envelope `signerCertThumbprint` (self-consistency).
#   3. Recompute SHA-256 of the package file; must equal envelope
#      `packageSha256` (hash match).
#   4. Use the signer cert's RSA public key to verify the raw signature
#      bytes against the SHA-256 hash with PKCS#1 v1.5 padding.
#   5. Return a deterministic result object describing exactly which
#      step succeeded and which failed. No exceptions surface to the
#      caller for documented failure modes.
#
# Public surface:
#   - Test-SignatureEnvelopeSchema   Validate envelope JSON shape.
#   - Test-PackageSignature          Full cryptographic verification.

# ---------------------------------------------------------------------
# Schema constants. Closed allow-list; unknown keys hard-rejected.
# ---------------------------------------------------------------------
$Script:SignatureEnvelopeSchemaVersions = @(1)

$Script:AllowedSignatureEnvelopeKeys = @(
    'schemaVersion',
    'packageFile',
    'packageSha256',
    'hashAlgorithm',
    'signatureAlgorithm',
    'signatureBase64',
    'signerCertBase64',
    'signerCertThumbprint',
    'signedAtUtc'
)

# Algorithms this version of the verifier supports. New algorithms
# require a new schema version. Unknown algorithm string is a hard
# reject, NOT a fallback.
$Script:SupportedHashAlgorithms      = @('SHA256')
$Script:SupportedSignatureAlgorithms = @('RSA-PKCS1v15-SHA256')

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

function Format-HexUpper {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $clean = ($Value -replace '[^0-9A-Fa-f]','').ToUpperInvariant()
    return $clean
}

# ---------------------------------------------------------------------
# Envelope schema validator.
# ---------------------------------------------------------------------
function Test-SignatureEnvelopeSchema {
    param([Parameter(Mandatory)]$Envelope)

    if (-not (Test-Hashtable $Envelope)) {
        return @{ ok = $false; error = 'envelope_not_object'; message = 'Signature envelope is not a JSON object.' }
    }

    foreach ($k in (Get-HashtableKeys $Envelope)) {
        if ($Script:AllowedSignatureEnvelopeKeys -notcontains $k) {
            return @{ ok = $false; error = 'unknown_field'; message = ('Signature envelope has an unknown field: "' + $k + '".') }
        }
    }

    foreach ($req in $Script:AllowedSignatureEnvelopeKeys) {
        if (-not $Envelope.Contains($req)) {
            return @{ ok = $false; error = 'missing_field'; message = ('Signature envelope is missing required field: "' + $req + '".') }
        }
    }

    $sv = $Envelope['schemaVersion']
    if (-not ($sv -is [int] -or $sv -is [long])) {
        return @{ ok = $false; error = 'type_mismatch'; message = '"schemaVersion" must be an integer.' }
    }
    if ($Script:SignatureEnvelopeSchemaVersions -notcontains [int]$sv) {
        return @{ ok = $false; error = 'unsupported_schema_version'; message = ('Signature envelope schemaVersion ' + $sv + ' is not supported.') }
    }

    foreach ($strField in @('packageFile','signedAtUtc','signatureBase64','signerCertBase64')) {
        $raw = $Envelope[$strField]
        $valStr = if ($null -eq $raw) { '' } else { [string]$raw }
        if (-not (Test-RequiredString $valStr)) {
            return @{ ok = $false; error = 'missing_field'; message = ('"' + $strField + '" is required and must be a non-empty string.') }
        }
    }

    $hashAlg = [string]$Envelope['hashAlgorithm']
    if ($Script:SupportedHashAlgorithms -notcontains $hashAlg) {
        return @{ ok = $false; error = 'unsupported_hash_algorithm'; message = ('hashAlgorithm "' + $hashAlg + '" is not supported.') }
    }
    $sigAlg = [string]$Envelope['signatureAlgorithm']
    if ($Script:SupportedSignatureAlgorithms -notcontains $sigAlg) {
        return @{ ok = $false; error = 'unsupported_signature_algorithm'; message = ('signatureAlgorithm "' + $sigAlg + '" is not supported.') }
    }

    $tp = Format-HexUpper -Value ([string]$Envelope['signerCertThumbprint'])
    if (-not $tp -or $tp.Length -ne 40) {
        return @{ ok = $false; error = 'invalid_thumbprint'; message = '"signerCertThumbprint" must be a 40-character SHA-1 hex thumbprint.' }
    }

    $pkg = Format-HexUpper -Value ([string]$Envelope['packageSha256'])
    if (-not $pkg -or $pkg.Length -ne 64) {
        return @{ ok = $false; error = 'invalid_package_hash'; message = '"packageSha256" must be a 64-character SHA-256 hex digest.' }
    }

    return @{
        ok                = $true
        normalizedHash    = $pkg
        normalizedThumb   = $tp
        hashAlgorithm     = $hashAlg
        signatureAlgorithm = $sigAlg
    }
}

# ---------------------------------------------------------------------
# Full cryptographic verification of a package + envelope pair.
#
# Returns an object with these always-present fields:
#   ok                  -- $true only when every check passes.
#   error               -- short machine-readable error tag or $null.
#   errorMessage        -- human-readable explanation or $null.
#   envelopePresent     -- $true if the .sig file exists.
#   envelopeParsed      -- $true if the .sig parsed as JSON.
#   envelopeSchemaValid -- $true if the .sig matches the closed schema.
#   certParsed          -- $true if signerCertBase64 decoded to an X509
#                          certificate.
#   certSelfConsistent  -- $true if the cert's own SHA-1 thumbprint
#                          matches signerCertThumbprint.
#   hashMatches         -- $true if recomputed SHA-256 equals
#                          envelope.packageSha256.
#   signatureValid      -- $true if the RSA public key verified the
#                          signature over the package hash.
#   certThumbprint      -- normalized 40-hex thumbprint from the cert.
#   certSubject         -- distinguished-name string from the cert.
#   certIssuer          -- distinguished-name string from the cert.
#   certNotBefore       -- ISO-8601 UTC string or $null.
#   certNotAfter        -- ISO-8601 UTC string or $null.
#   packageSha256       -- recomputed package digest, uppercase hex.
#
# This function NEVER throws for any documented failure mode. It only
# throws on programmer error (e.g. missing -PackagePath parameter).
# ---------------------------------------------------------------------
function Test-PackageSignature {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$EnvelopePath
    )

    $result = [ordered]@{
        ok                  = $false
        error               = $null
        errorMessage        = $null
        envelopePresent     = $false
        envelopeParsed      = $false
        envelopeSchemaValid = $false
        certParsed          = $false
        certSelfConsistent  = $false
        hashMatches         = $false
        signatureValid      = $false
        certThumbprint      = $null
        certSubject         = $null
        certIssuer          = $null
        certNotBefore       = $null
        certNotAfter        = $null
        packageSha256       = $null
    }

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        $result.error = 'package_missing'
        $result.errorMessage = 'Package file does not exist on disk.'
        return [pscustomobject]$result
    }
    if (-not (Test-Path -LiteralPath $EnvelopePath -PathType Leaf)) {
        $result.error = 'envelope_missing'
        $result.errorMessage = 'Signature envelope file does not exist on disk.'
        return [pscustomobject]$result
    }
    $result.envelopePresent = $true

    # ----- Parse envelope -----
    $env = $null
    try {
        $env = Get-Content -LiteralPath $EnvelopePath -Raw -ErrorAction Stop | ConvertFrom-Json -AsHashtable -Depth 8
    } catch {
        $result.error = 'envelope_parse_failed'
        $result.errorMessage = 'Could not parse signature envelope JSON: ' + $_.Exception.Message
        return [pscustomobject]$result
    }
    $result.envelopeParsed = $true

    $schema = Test-SignatureEnvelopeSchema -Envelope $env
    if (-not $schema.ok) {
        $result.error = $schema.error
        $result.errorMessage = $schema.message
        return [pscustomobject]$result
    }
    $result.envelopeSchemaValid = $true

    # ----- Decode signer certificate -----
    $cert = $null
    try {
        $certBytes = [System.Convert]::FromBase64String([string]$env['signerCertBase64'])
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certBytes)
    } catch {
        $result.error = 'cert_decode_failed'
        $result.errorMessage = 'Could not decode signer certificate from base64: ' + $_.Exception.Message
        return [pscustomobject]$result
    }
    $result.certParsed = $true
    $result.certThumbprint = $cert.Thumbprint.ToUpperInvariant()
    $result.certSubject    = $cert.Subject
    $result.certIssuer     = $cert.Issuer
    try {
        $result.certNotBefore = $cert.NotBefore.ToUniversalTime().ToString('o')
        $result.certNotAfter  = $cert.NotAfter.ToUniversalTime().ToString('o')
    } catch {
        # NotBefore/NotAfter must always be present on a real cert; if
        # they are not we still proceed -- crypto verification is what
        # gates trust.
        $result.certNotBefore = $null
        $result.certNotAfter  = $null
    }

    # ----- Self-consistency: envelope thumbprint must match cert -----
    if ($result.certThumbprint -ne $schema.normalizedThumb) {
        $result.error = 'thumbprint_mismatch'
        $result.errorMessage = 'Embedded cert thumbprint (' + $result.certThumbprint + ') does not match envelope signerCertThumbprint (' + $schema.normalizedThumb + ').'
        return [pscustomobject]$result
    }
    $result.certSelfConsistent = $true

    # ----- Recompute package hash -----
    try {
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $PackagePath -ErrorAction Stop).Hash.ToUpperInvariant()
        $result.packageSha256 = $actual
    } catch {
        $result.error = 'hash_compute_failed'
        $result.errorMessage = 'Could not compute SHA-256 of package: ' + $_.Exception.Message
        return [pscustomobject]$result
    }

    if ($actual -ne $schema.normalizedHash) {
        $result.error = 'hash_mismatch'
        $result.errorMessage = 'Package SHA-256 (' + $actual + ') does not match envelope packageSha256 (' + $schema.normalizedHash + ').'
        return [pscustomobject]$result
    }
    $result.hashMatches = $true

    # ----- Verify signature using public key -----
    $rsa = $null
    try {
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($cert)
        if ($null -eq $rsa) {
            $result.error = 'no_rsa_public_key'
            $result.errorMessage = 'Signer certificate does not expose an RSA public key.'
            return [pscustomobject]$result
        }

        $sigBytes = [System.Convert]::FromBase64String([string]$env['signatureBase64'])
        $hashBytes = [byte[]]@()
        # Convert hex hash back to bytes for VerifyHash.
        $hashHex = $actual
        $hashBytes = New-Object 'byte[]' ($hashHex.Length / 2)
        for ($i = 0; $i -lt $hashBytes.Length; $i++) {
            $hashBytes[$i] = [System.Convert]::ToByte($hashHex.Substring($i * 2, 2), 16)
        }

        $ok = $rsa.VerifyHash(
            $hashBytes,
            $sigBytes,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

        if (-not $ok) {
            $result.error = 'signature_invalid'
            $result.errorMessage = 'RSA public-key verification of the signature failed.'
            return [pscustomobject]$result
        }
        $result.signatureValid = $true
    } catch {
        $result.error = 'verification_threw'
        $result.errorMessage = 'Verification raised an exception: ' + $_.Exception.Message
        return [pscustomobject]$result
    } finally {
        if ($null -ne $rsa) { $rsa.Dispose() }
        if ($null -ne $cert) { $cert.Dispose() }
    }

    $result.ok = $true
    return [pscustomobject]$result
}

Export-ModuleMember -Function `
    Test-SignatureEnvelopeSchema, `
    Test-PackageSignature
