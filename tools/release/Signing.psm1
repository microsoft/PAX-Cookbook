#requires -Version 7.4

# Signing.psm1 -- Detached-signature production primitives. THIS MODULE
# IS DEVELOPER/OPERATOR TOOLING. It is NOT bundled with the appliance
# (`tools/release/` is in the Phase Q exclusion list). The appliance
# only contains the *verification* surface (Signature.psm1).
#
# Hard rules enforced by this module:
#
#   - No private key is ever embedded in the repository. Operators
#     supply a certificate via thumbprint lookup in CurrentUser\My or
#     via a PFX path with a SecureString password.
#
#   - No certificate store mutation. The module reads from
#     CurrentUser\My when a thumbprint is supplied; it never writes,
#     imports, or moves certificates between stores.
#
#   - No auto-signing. Every entrypoint is parameterized and invoked
#     deliberately by an operator. The companion entrypoint
#     `Sign-Release.ps1` is the only operator-facing UX.
#
#   - No silent verification. After writing a signature, the module
#     immediately re-verifies it with the read-only verifier in
#     `app/broker/Update/Signature.psm1`. A self-verification failure
#     causes the produced files to be deleted and a hard error.
#
# Public surface:
#   - Get-SigningCertificateFromThumbprint   Look up cert in CurrentUser\My.
#   - Get-SigningCertificateFromPfx          Load cert + key from a PFX path.
#   - Test-SigningCertificate                Sanity-check a cert for code signing.
#   - New-SignatureEnvelopeObject            Build the JSON envelope hashtable.
#   - New-DetachedPackageSignature           Sign a package + write .sig + .signer.json.
#   - Write-JsonArtifact                     UTF-8 no-BOM, LF, deterministic.

# ---------------------------------------------------------------------
# Hard-pinned algorithm. This module currently produces RSA-PKCS1v15
# SHA-256 signatures only. New algorithms require a new schema version
# in both this module and the appliance-side Signature.psm1.
# ---------------------------------------------------------------------
$Script:SignatureEnvelopeSchemaVersion = 1
$Script:HashAlgorithmName              = 'SHA256'
$Script:SignatureAlgorithmName         = 'RSA-PKCS1v15-SHA256'
$Script:SignerMetadataSchemaVersion    = 1

# ---------------------------------------------------------------------
# Closed allow-lists. Duplicated locally so the signer cannot acquire a
# new schema field by accident. These MUST stay in sync with the
# appliance-side validators (Signature.psm1, Trust.psm1).
# ---------------------------------------------------------------------
$Script:AllowedEnvelopeKeys = @(
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

# ---------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------
function Format-Thumbprint {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $clean = ($Value -replace '[^0-9A-Fa-f]','').ToUpperInvariant()
    if ($clean.Length -ne 40) { return $null }
    return $clean
}

function Get-Sha256HexUpper {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path -ErrorAction Stop).Hash.ToUpperInvariant()
}

function ConvertTo-HexBytes {
    param([Parameter(Mandatory)][string]$Hex)
    $bytes = New-Object 'byte[]' ($Hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [System.Convert]::ToByte($Hex.Substring($i * 2, 2), 16)
    }
    return $bytes
}

function Write-JsonArtifact {
    # Deterministic JSON write: UTF-8 *without* BOM, LF line endings,
    # sorted top-level keys handled at the caller. We never use Out-File
    # because Windows PowerShell's default encoding is UTF-16 with BOM.
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Object
    )
    $json = $Object | ConvertTo-Json -Depth 10
    $json = $json -replace "`r`n", "`n"
    if (-not $json.EndsWith("`n")) { $json += "`n" }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

# ---------------------------------------------------------------------
# Certificate loaders. Two paths only.
# ---------------------------------------------------------------------
function Get-SigningCertificateFromThumbprint {
    # Resolve a code-signing cert from the *operator's* CurrentUser\My
    # store. This is the standard Windows pattern: operators import
    # their cert once (out of band), then reference it by thumbprint.
    # We never call Import-PfxCertificate, never call
    # Set-Location Cert:, never mutate the store.
    param([Parameter(Mandatory)][string]$Thumbprint)

    $normalized = Format-Thumbprint -Value $Thumbprint
    if (-not $normalized) {
        throw "Thumbprint '$Thumbprint' is not a 40-character SHA-1 hex thumbprint."
    }

    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::My,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $matches = @($store.Certificates | Where-Object { $_.Thumbprint.ToUpperInvariant() -eq $normalized })
        if ($matches.Count -eq 0) {
            throw "No certificate with thumbprint $normalized was found in CurrentUser\My. Import the cert manually first."
        }
        if ($matches.Count -gt 1) {
            throw "Multiple certificates with thumbprint $normalized exist in CurrentUser\My. Refusing to choose."
        }
        # Return a clone we own so we can dispose the store safely.
        return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($matches[0])
    } finally {
        $store.Close()
        $store.Dispose()
    }
}

function Get-SigningCertificateFromPfx {
    # Load a cert + private key from a PFX path using a SecureString
    # password. The password is never stored anywhere; it is consumed
    # by the X509Certificate2 constructor and the SecureString itself
    # is disposed by the caller (Sign-Release.ps1).
    param(
        [Parameter(Mandatory)][string]$PfxPath,
        [Parameter(Mandatory)][System.Security.SecureString]$Password
    )

    if (-not (Test-Path -LiteralPath $PfxPath -PathType Leaf)) {
        throw "PFX file not found: $PfxPath"
    }

    # X509KeyStorageFlags: keep the key in memory, do NOT persist to
    # the OS user-keys directory. `EphemeralKeySet` is the modern,
    # cross-platform flag for this; we don't fall back to PersistKey
    # if it isn't available because we explicitly do not want the key
    # written to disk.
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet -bor `
             [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxPath, $Password, $flags)
}

function Test-SigningCertificate {
    # Sanity-check a cert before we attempt to sign with it. Returns a
    # structured result; the caller decides whether to throw.
    param([Parameter(Mandatory)]$Certificate)

    $issues = @()
    if (-not $Certificate.HasPrivateKey) {
        $issues += 'certificate_has_no_private_key'
    }
    $now = [DateTime]::UtcNow
    if ($Certificate.NotBefore.ToUniversalTime() -gt $now) {
        $issues += 'certificate_not_yet_valid'
    }
    if ($Certificate.NotAfter.ToUniversalTime() -lt $now) {
        $issues += 'certificate_expired'
    }
    $rsa = $null
    try {
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
        if ($null -eq $rsa) {
            $issues += 'certificate_has_no_rsa_private_key'
        } elseif ($rsa.KeySize -lt 2048) {
            $issues += ('rsa_key_too_small:' + $rsa.KeySize)
        }
    } catch {
        $issues += ('rsa_private_key_read_failed:' + $_.Exception.Message)
    } finally {
        if ($null -ne $rsa) { $rsa.Dispose() }
    }

    return [pscustomobject]@{
        ok        = ($issues.Count -eq 0)
        issues    = $issues
        thumbprint = $Certificate.Thumbprint.ToUpperInvariant()
        subject   = $Certificate.Subject
        issuer    = $Certificate.Issuer
        notBefore = $Certificate.NotBefore.ToUniversalTime().ToString('o')
        notAfter  = $Certificate.NotAfter.ToUniversalTime().ToString('o')
    }
}

# ---------------------------------------------------------------------
# Envelope object builder. Closed schema. Every key validated.
# ---------------------------------------------------------------------
function New-SignatureEnvelopeObject {
    param(
        [Parameter(Mandatory)][string]$PackageFile,
        [Parameter(Mandatory)][string]$PackageSha256,
        [Parameter(Mandatory)][string]$SignatureBase64,
        [Parameter(Mandatory)][string]$SignerCertBase64,
        [Parameter(Mandatory)][string]$SignerCertThumbprint,
        [Parameter(Mandatory)][string]$SignedAtUtc
    )

    $obj = [ordered]@{
        schemaVersion        = $Script:SignatureEnvelopeSchemaVersion
        packageFile          = $PackageFile
        packageSha256        = $PackageSha256.ToUpperInvariant()
        hashAlgorithm        = $Script:HashAlgorithmName
        signatureAlgorithm   = $Script:SignatureAlgorithmName
        signatureBase64      = $SignatureBase64
        signerCertBase64     = $SignerCertBase64
        signerCertThumbprint = $SignerCertThumbprint.ToUpperInvariant()
        signedAtUtc          = $SignedAtUtc
    }

    # Self-check: every key is on the allow-list and every allow-list
    # key is present. Defense in depth against future drift.
    foreach ($k in $obj.Keys) {
        if ($Script:AllowedEnvelopeKeys -notcontains $k) {
            throw "Signature envelope builder produced an unknown key: $k"
        }
    }
    foreach ($req in $Script:AllowedEnvelopeKeys) {
        if (-not $obj.Contains($req)) {
            throw "Signature envelope builder missed a required key: $req"
        }
    }

    return $obj
}

function New-SignerMetadataObject {
    param(
        [Parameter(Mandatory)][string]$SignerName,
        [Parameter(Mandatory)][string]$SignerEmail,
        [Parameter(Mandatory)][string]$CertThumbprint,
        [Parameter(Mandatory)][string]$CertSubject,
        [Parameter(Mandatory)][string]$SignedAtUtc,
        [Parameter(Mandatory)][string]$SignatureFile
    )

    $obj = [ordered]@{
        schemaVersion      = $Script:SignerMetadataSchemaVersion
        signerName         = $SignerName
        signerEmail        = $SignerEmail
        certThumbprint     = $CertThumbprint.ToUpperInvariant()
        certSubject        = $CertSubject
        signedAtUtc        = $SignedAtUtc
        signatureAlgorithm = $Script:SignatureAlgorithmName
        signatureFile      = $SignatureFile
    }

    foreach ($k in $obj.Keys) {
        if ($Script:AllowedSignerMetadataKeys -notcontains $k) {
            throw "Signer metadata builder produced an unknown key: $k"
        }
    }
    foreach ($req in $Script:AllowedSignerMetadataKeys) {
        if (-not $obj.Contains($req)) {
            throw "Signer metadata builder missed a required key: $req"
        }
    }
    return $obj
}

# ---------------------------------------------------------------------
# Full signing pipeline. Operator-initiated, single explicit invocation
# per package. Self-verifies before returning.
# ---------------------------------------------------------------------
function New-DetachedPackageSignature {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)]$Certificate,
        [Parameter(Mandatory)][string]$SignerName,
        [string]$SignerEmail = '',
        [datetime]$SignedAtUtc = ([DateTime]::UtcNow),
        [string]$SignatureOutputPath,
        [string]$SignerMetadataOutputPath,
        [switch]$Force
    )

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "Package file not found: $PackagePath"
    }

    $sigPath    = if ([string]::IsNullOrWhiteSpace($SignatureOutputPath))       { "$PackagePath.sig" }        else { $SignatureOutputPath }
    $signerPath = if ([string]::IsNullOrWhiteSpace($SignerMetadataOutputPath))  { "$PackagePath.signer.json" } else { $SignerMetadataOutputPath }

    if ((Test-Path -LiteralPath $sigPath) -and -not $Force) {
        throw "Signature already exists at $sigPath. Pass -Force to overwrite."
    }
    if ((Test-Path -LiteralPath $signerPath) -and -not $Force) {
        throw "Signer metadata already exists at $signerPath. Pass -Force to overwrite."
    }

    $certCheck = Test-SigningCertificate -Certificate $Certificate
    if (-not $certCheck.ok) {
        throw ("Certificate is not usable for signing: " + ($certCheck.issues -join ', '))
    }

    # Compute package hash, sign, emit artifacts.
    $packageHashHex = Get-Sha256HexUpper -Path $PackagePath
    $hashBytes      = ConvertTo-HexBytes -Hex $packageHashHex

    $rsa = $null
    $sigBytes = $null
    try {
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
        if ($null -eq $rsa) { throw 'Certificate has no RSA private key.' }
        $sigBytes = $rsa.SignHash(
            $hashBytes,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    } finally {
        if ($null -ne $rsa) { $rsa.Dispose() }
    }

    $sigBase64    = [System.Convert]::ToBase64String($sigBytes)
    $certDer      = $Certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    $certBase64   = [System.Convert]::ToBase64String($certDer)
    $thumbprint   = $Certificate.Thumbprint.ToUpperInvariant()
    $packageFile  = Split-Path -Path $PackagePath -Leaf
    $signedAtIso  = $SignedAtUtc.ToUniversalTime().ToString('o')

    $envelope = New-SignatureEnvelopeObject `
        -PackageFile          $packageFile `
        -PackageSha256        $packageHashHex `
        -SignatureBase64      $sigBase64 `
        -SignerCertBase64     $certBase64 `
        -SignerCertThumbprint $thumbprint `
        -SignedAtUtc          $signedAtIso

    $signerMetadata = New-SignerMetadataObject `
        -SignerName         $SignerName `
        -SignerEmail        ($SignerEmail) `
        -CertThumbprint     $thumbprint `
        -CertSubject        $Certificate.Subject `
        -SignedAtUtc        $signedAtIso `
        -SignatureFile      (Split-Path -Path $sigPath -Leaf)

    Write-JsonArtifact -Path $sigPath    -Object $envelope
    Write-JsonArtifact -Path $signerPath -Object $signerMetadata

    # ---- Self-verification using the appliance-side verifier ----
    # If verification fails for any reason we delete the artifacts so
    # we never leave a half-signed package on disk that an operator
    # might publish by accident.
    $signatureModule = Join-Path (Split-Path -Path $PSScriptRoot -Parent) 'app\broker\Update\Signature.psm1'
    if (-not (Test-Path -LiteralPath $signatureModule -PathType Leaf)) {
        # Repo layout: tools/release/Signing.psm1 -> ../../app/broker/Update/Signature.psm1
        $signatureModule = Join-Path (Split-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -Parent) 'app\broker\Update\Signature.psm1'
    }
    if (-not (Test-Path -LiteralPath $signatureModule -PathType Leaf)) {
        Remove-Item -LiteralPath $sigPath, $signerPath -Force -ErrorAction SilentlyContinue
        throw "Cannot locate appliance-side Signature.psm1 for self-verification (expected at $signatureModule)."
    }
    Import-Module -Force $signatureModule -ErrorAction Stop | Out-Null

    $verify = Test-PackageSignature -PackagePath $PackagePath -EnvelopePath $sigPath
    if (-not $verify.ok) {
        Remove-Item -LiteralPath $sigPath, $signerPath -Force -ErrorAction SilentlyContinue
        throw ('Self-verification of the just-written signature FAILED (' + $verify.error + '): ' + $verify.errorMessage)
    }

    return [pscustomobject]@{
        PackagePath           = $PackagePath
        PackageSha256         = $packageHashHex
        SignaturePath         = $sigPath
        SignerMetadataPath    = $signerPath
        SignerCertThumbprint  = $thumbprint
        SignerCertSubject     = $Certificate.Subject
        SignedAtUtc           = $signedAtIso
        SignatureAlgorithm    = $Script:SignatureAlgorithmName
        SelfVerificationOk    = $verify.ok
    }
}

Export-ModuleMember -Function `
    Get-SigningCertificateFromThumbprint, `
    Get-SigningCertificateFromPfx, `
    Test-SigningCertificate, `
    New-SignatureEnvelopeObject, `
    New-SignerMetadataObject, `
    New-DetachedPackageSignature, `
    Write-JsonArtifact
