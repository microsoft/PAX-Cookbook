#requires -Version 7.4

<#
.SYNOPSIS
    Sign a PAX Cookbook release package, producing detached signature
    sidecars.

.DESCRIPTION
    This script is the distributor entrypoint for signing a
    release package that was produced by Build-Release.ps1. It:

      1. Loads a signing certificate -- EITHER from CurrentUser\My by
         thumbprint OR from a PFX path using a SecureString password.
      2. Confirms the cert has an RSA private key, has not expired,
         and has a key size >= 2048 bits.
      3. Computes the SHA-256 of the package.
      4. Signs the hash with the cert's private key using
         RSA-PKCS1v15-SHA256.
      5. Writes two sidecars next to the package:
            <package>.sig             the signature envelope JSON
            <package>.signer.json     UI/identity metadata JSON
      6. Self-verifies the just-written signature using the appliance-
         side verifier (Signature.psm1). A self-verification failure
         deletes the artifacts and the script exits non-zero.

    SECURITY RULES enforced by this entrypoint:

      - No plaintext password parameter. The PFX password is
        a [SecureString]; if not provided, the script prompts
        interactively via Read-Host -AsSecureString.
      - The script does NOT install, import, or move any certificate.
        It does not touch the OS trust store.
      - The script does NOT contact the network, does not push to a
        cloud signing service, and does not invoke signtool /
        AzureSignTool / Set-AuthenticodeSignature /
        New-SelfSignedCertificate.
      - The script does NOT mutate git or any repository state.

.PARAMETER PackagePath
    The .zip package to sign. Required.

.PARAMETER CertificateThumbprint
    SHA-1 thumbprint (40 hex chars) of a code-signing certificate
    already imported into CurrentUser\My. Mutually exclusive with
    -PfxPath.

.PARAMETER PfxPath
    Path to a PFX (PKCS#12) file containing the cert + private key.
    Mutually exclusive with -CertificateThumbprint.

.PARAMETER PfxPassword
    SecureString containing the PFX password. Optional; if omitted
    when -PfxPath is used, the script prompts interactively.

.PARAMETER SignerName
    Human-readable signer name written into the .signer.json sidecar.
    Required.

.PARAMETER SignerEmail
    Optional signer email address written into the .signer.json
    sidecar. Default empty string.

.PARAMETER SignedAtUtc
    Optional override for the signing timestamp (DateTime, will be
    converted to UTC). Defaults to the current UTC time. Useful for
    deterministic test scenarios.

.PARAMETER Force
    Overwrite existing .sig and .signer.json files. Without this flag
    the script refuses to clobber sidecars that already exist.

.EXAMPLE
    pwsh -File .\tools\release\Sign-Release.ps1 `
        -PackagePath dist\stable\pax-cookbook-1.0.0\pax-cookbook-1.0.0.zip `
        -CertificateThumbprint AABBCC112233...40chars... `
        -SignerName 'Aggregated Copilot Analytics Team' `
        -SignerEmail 'team@example.invalid'

.EXAMPLE
    $pw = Read-Host -AsSecureString -Prompt 'PFX password'
    pwsh -File .\tools\release\Sign-Release.ps1 `
        -PackagePath dist\stable\pax-cookbook-1.0.0\pax-cookbook-1.0.0.zip `
        -PfxPath .\my-signing-cert.pfx `
        -PfxPassword $pw `
        -SignerName 'Aggregated Copilot Analytics Team'
#>

[CmdletBinding(DefaultParameterSetName = 'Thumbprint')]
param(
    [Parameter(Mandatory)][string]$PackagePath,

    [Parameter(Mandatory, ParameterSetName = 'Thumbprint')]
    [string]$CertificateThumbprint,

    [Parameter(Mandatory, ParameterSetName = 'Pfx')]
    [string]$PfxPath,

    [Parameter(ParameterSetName = 'Pfx')]
    [System.Security.SecureString]$PfxPassword,

    [Parameter(Mandatory)][string]$SignerName,
    [string]$SignerEmail = '',
    [datetime]$SignedAtUtc,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---- Resolve module paths relative to this script ----
$SigningModulePath = Join-Path $PSScriptRoot 'Signing.psm1'
if (-not (Test-Path -LiteralPath $SigningModulePath -PathType Leaf)) {
    Write-Error "Cannot find Signing.psm1 at $SigningModulePath"
    exit 2
}
Import-Module -Force $SigningModulePath -ErrorAction Stop | Out-Null

# ---- Resolve package path ----
try {
    $resolvedPackage = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
} catch {
    Write-Error "Package path not found: $PackagePath"
    exit 2
}
if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Leaf)) {
    Write-Error "Package path is not a file: $resolvedPackage"
    exit 2
}

# ---- Load certificate ----
$cert = $null
try {
    if ($PSCmdlet.ParameterSetName -eq 'Thumbprint') {
        Write-Host "Loading certificate $CertificateThumbprint from CurrentUser\My ..."
        $cert = Get-SigningCertificateFromThumbprint -Thumbprint $CertificateThumbprint
    } else {
        # PFX path. Prompt for password if not supplied.
        $resolvedPfx = (Resolve-Path -LiteralPath $PfxPath -ErrorAction Stop).Path
        $pw = $PfxPassword
        if ($null -eq $pw) {
            $pw = Read-Host -AsSecureString -Prompt "Enter PFX password for $resolvedPfx"
        }
        Write-Host "Loading certificate from $resolvedPfx ..."
        $cert = Get-SigningCertificateFromPfx -PfxPath $resolvedPfx -Password $pw
    }
} catch {
    Write-Error ("Could not load signing certificate: " + $_.Exception.Message)
    exit 3
}

# ---- Sanity check the cert ----
$certCheck = Test-SigningCertificate -Certificate $cert
if (-not $certCheck.ok) {
    Write-Error ("Certificate is not usable for signing: " + ($certCheck.issues -join ', '))
    if ($null -ne $cert) { $cert.Dispose() }
    exit 4
}

Write-Host ""
Write-Host "Signer:"
Write-Host ("  Thumbprint: " + $certCheck.thumbprint)
Write-Host ("  Subject:    " + $certCheck.subject)
Write-Host ("  Issuer:     " + $certCheck.issuer)
Write-Host ("  NotBefore:  " + $certCheck.notBefore)
Write-Host ("  NotAfter:   " + $certCheck.notAfter)
Write-Host ""

# ---- Build signing parameters ----
$signParams = @{
    PackagePath = $resolvedPackage
    Certificate = $cert
    SignerName  = $SignerName
    SignerEmail = $SignerEmail
    Force       = [bool]$Force
}
if ($PSBoundParameters.ContainsKey('SignedAtUtc')) {
    $signParams['SignedAtUtc'] = $SignedAtUtc
}

# ---- Sign ----
try {
    $result = New-DetachedPackageSignature @signParams
} catch {
    Write-Error ("Signing failed: " + $_.Exception.Message)
    if ($null -ne $cert) { $cert.Dispose() }
    exit 5
} finally {
    if ($null -ne $cert) { $cert.Dispose() }
}

Write-Host "Signed."
Write-Host ("  Signature file:   " + $result.SignaturePath)
Write-Host ("  Signer metadata:  " + $result.SignerMetadataPath)
Write-Host ("  Package SHA-256:  " + $result.PackageSha256)
Write-Host ("  Algorithm:        " + $result.SignatureAlgorithm)
Write-Host ("  Signed at (UTC):  " + $result.SignedAtUtc)
Write-Host ""
Write-Host "Self-verification: OK (Test-PackageSignature passed)."
Write-Host ""
Write-Host "Next steps (distributor):"
Write-Host "  1. Publish the package, .sig, .signer.json, and .sha256 sidecars together."
Write-Host "  2. Inform downstream chefs of the signer thumbprint:"
Write-Host ("       " + $result.SignerCertThumbprint)
Write-Host "     Each chef adds it to <workspace>\Trust\trusted-signers.json"
Write-Host "     out of band (this script does NOT push to any allowlist)."
Write-Host ""

# Final result object for programmatic consumers.
return $result
