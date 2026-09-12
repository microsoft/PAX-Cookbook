Set-StrictMode -Version Latest

function Get-PaxCanonicalPath {
    param([Parameter(Mandatory)][string]$Path)

    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Test-PaxPathWithin {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )

    $candidate = Get-PaxCanonicalPath $Path
    $container = Get-PaxCanonicalPath $Root
    return $candidate.StartsWith($container + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-PaxSetupReleaseContract {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$SourceVersionPath,
        [string]$AppVersion = '',
        [string]$SetupVersion = '',
        [string]$BuildId = '',
        [string]$Channel = ''
    )

    $root = Get-PaxCanonicalPath $RepositoryRoot
    $sourcePath = Get-PaxCanonicalPath $SourceVersionPath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "VERSION.json missing at: $sourcePath"
    }

    try {
        $source = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json -Depth 32
    } catch {
        throw "VERSION.json is not valid JSON: $sourcePath"
    }

    $resolvedChannel = if ([string]::IsNullOrWhiteSpace($Channel)) { [string]$source.channel } else { $Channel }
    if ([string]::IsNullOrWhiteSpace($resolvedChannel)) { $resolvedChannel = 'stable' }
    $resolvedChannel = $resolvedChannel.Trim().ToLowerInvariant()
    if ($resolvedChannel -notin @('stable', 'experimental', 'internal')) {
        throw "Invalid channel '$resolvedChannel'. Valid values: stable, experimental, internal."
    }

    $sourceVersion = [string]$source.cookbook.version
    $displayVersion = if ([string]::IsNullOrWhiteSpace($AppVersion)) { $sourceVersion } else { $AppVersion.Trim() }
    if ([string]::IsNullOrWhiteSpace($displayVersion)) {
        throw 'VERSION.json: cookbook.version is empty.'
    }
    if (-not [string]::IsNullOrWhiteSpace($SetupVersion) -and $SetupVersion.Trim() -cne $displayVersion) {
        throw "SetupVersion '$($SetupVersion.Trim())' must equal AppVersion '$displayVersion'."
    }

    # Closed display-version grammar:
    # stable       major.minor.patch[.fileBuild]
    # experimental major.minor.patch-exp.numericOrdinal
    # internal     major.minor.patch-internal.numericOrdinal
    $stablePattern = '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:\.(?<build>0|[1-9]\d*))?$'
    $experimentalPattern = '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)-exp\.(?<build>[1-9]\d*)$'
    $internalPattern = '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)-internal\.(?<build>[1-9]\d*)$'
    $pattern = switch ($resolvedChannel) {
        'stable' { $stablePattern }
        'experimental' { $experimentalPattern }
        'internal' { $internalPattern }
    }
    $match = [regex]::Match($displayVersion, $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw "Version '$displayVersion' is not valid for channel '$resolvedChannel'."
    }

    $major = [int]$match.Groups['major'].Value
    $minor = [int]$match.Groups['minor'].Value
    $patch = [int]$match.Groups['patch'].Value
    $fileBuild = if ($match.Groups['build'].Success) { [int]$match.Groups['build'].Value } else { 0 }
    foreach ($component in @($major, $minor, $patch, $fileBuild)) {
        if ($component -gt 65534) {
            throw "Version '$displayVersion' contains a component above the managed metadata limit 65534."
        }
    }

    if ([string]::IsNullOrWhiteSpace($BuildId)) {
        throw 'BuildId is required; provenance is never synthesized from a timestamp or Git.'
    }
    $provenance = $BuildId.Trim()
    if ($provenance -cnotmatch '^(?:commit\.[0-9A-Fa-f]{7,64}|build\.[0-9A-Za-z][0-9A-Za-z-]{0,63})$') {
        throw "BuildId '$provenance' must be commit.<7-64 hex> or build.<1-64 ASCII alphanumeric/hyphen>."
    }

    $assemblyVersion = "$major.$minor.0.0"
    $fileVersion = "$major.$minor.$patch.$fileBuild"
    if ($fileVersion -ceq '1.0.0.0') {
        throw 'Refusing the default 1.0.0.0 file version.'
    }

    $buildRoot = Join-Path $root "artifacts\setup\$resolvedChannel"
    $distRoot = Join-Path $root "dist\$resolvedChannel"
    $versionsPath = if ($resolvedChannel -ceq 'stable') {
        Join-Path $root 'versions.json'
    } else {
        Join-Path $distRoot 'versions.json'
    }

    return [pscustomobject][ordered]@{
        Channel = $resolvedChannel
        DisplayVersion = $displayVersion
        AssemblyVersion = $assemblyVersion
        FileVersion = $fileVersion
        InformationalVersion = "$displayVersion+$provenance"
        Provenance = $provenance
        BuildRoot = Get-PaxCanonicalPath $buildRoot
        DistRoot = Get-PaxCanonicalPath $distRoot
        VersionsPath = Get-PaxCanonicalPath $versionsPath
        BuildVersionPath = Get-PaxCanonicalPath (Join-Path $buildRoot 'VERSION.json')
        SourceVersionPath = $sourcePath
    }
}

function Assert-PaxSetupReleaseWritePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][psobject]$Contract,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidateSet('build', 'distribution', 'manifest')][string]$Purpose
    )

    $candidate = Get-PaxCanonicalPath $Path
    $repositoryRoot = Get-PaxCanonicalPath (Split-Path (Split-Path (Split-Path $Contract.BuildRoot -Parent) -Parent) -Parent)
    $retiredRoot = Get-PaxCanonicalPath (Join-Path $repositoryRoot 'dist\setup')
    if ($candidate -eq $retiredRoot -or (Test-PaxPathWithin $candidate $retiredRoot)) {
        throw "The retired dist/setup destination is forbidden: $candidate"
    }

    $allowed = switch ($Purpose) {
        'build' { Test-PaxPathWithin $candidate $Contract.BuildRoot }
        'distribution' { Test-PaxPathWithin $candidate $Contract.DistRoot }
        'manifest' { $candidate -ceq (Get-PaxCanonicalPath $Contract.VersionsPath) }
    }
    if (-not $allowed) {
        throw "Write path '$candidate' is outside the '$($Contract.Channel)' $Purpose destination."
    }
    return $candidate
}

function Test-PaxFileVersionArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedFileVersion
    )

    $fullPath = Get-PaxCanonicalPath $Path
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Produced artifact missing: $fullPath"
    }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($fullPath)
    $observed = [string]$info.FileVersion
    if ([string]::IsNullOrWhiteSpace($observed) -or $observed -in @('1.0.0', '1.0.0.0')) {
        throw "Produced artifact retained the default file version '$observed': $fullPath"
    }
    if ($observed -cne $ExpectedFileVersion) {
        throw "Produced artifact file version mismatch: expected '$ExpectedFileVersion', observed '$observed'."
    }
    return [pscustomobject][ordered]@{
        Path = $fullPath
        FileVersion = $observed
        ProductVersion = [string]$info.ProductVersion
    }
}

function Get-PaxManagedAssemblyMetadata {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = Get-PaxCanonicalPath $Path
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Managed artifact missing: $fullPath"
    }

    $stream = [IO.File]::OpenRead($fullPath)
    try {
        $peReader = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if (-not $peReader.HasMetadata) { throw "Artifact has no managed metadata: $fullPath" }
            $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
            if (-not $reader.IsAssembly) { throw "Managed artifact is not an assembly: $fullPath" }
            $definition = $reader.GetAssemblyDefinition()
            $informationalVersion = $null
            $paxChannel = $null

            foreach ($attributeHandle in $definition.GetCustomAttributes()) {
                $attribute = $reader.GetCustomAttribute($attributeHandle)
                if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
                $member = $reader.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
                if ($member.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
                $type = $reader.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$member.Parent)
                $typeName = $reader.GetString($type.Name)
                $blob = $reader.GetBlobReader($attribute.Value)
                if ($blob.ReadUInt16() -ne 1) { throw "Invalid custom-attribute prolog in: $fullPath" }

                if ($typeName -ceq 'AssemblyInformationalVersionAttribute') {
                    $informationalVersion = $blob.ReadSerializedString()
                } elseif ($typeName -ceq 'AssemblyMetadataAttribute') {
                    $key = $blob.ReadSerializedString()
                    $value = $blob.ReadSerializedString()
                    if ($key -ceq 'PaxChannel') { $paxChannel = $value }
                }
            }

            return [pscustomobject][ordered]@{
                Path = $fullPath
                AssemblyVersion = [string]$definition.Version
                InformationalVersion = [string]$informationalVersion
                PaxChannel = [string]$paxChannel
            }
        } finally {
            $peReader.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Test-PaxManagedAssemblyMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedAssemblyVersion,
        [Parameter(Mandatory)][string]$ExpectedInformationalVersion,
        [ValidateSet('', 'stable', 'experimental', 'internal')][string]$ExpectedPaxChannel = ''
    )

    $observed = Get-PaxManagedAssemblyMetadata -Path $Path
    if ($observed.AssemblyVersion -in @('1.0.0', '1.0.0.0')) {
        throw "Produced artifact retained the default assembly version '$($observed.AssemblyVersion)'."
    }
    if ($observed.AssemblyVersion -cne $ExpectedAssemblyVersion) {
        throw "AssemblyVersion mismatch: expected '$ExpectedAssemblyVersion', observed '$($observed.AssemblyVersion)'."
    }
    if ($observed.InformationalVersion -cne $ExpectedInformationalVersion) {
        throw "InformationalVersion mismatch: expected '$ExpectedInformationalVersion', observed '$($observed.InformationalVersion)'."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPaxChannel) -and $observed.PaxChannel -cne $ExpectedPaxChannel) {
        throw "PaxChannel mismatch: expected '$ExpectedPaxChannel', observed '$($observed.PaxChannel)'."
    }
    return $observed
}

Export-ModuleMember -Function @(
    'Resolve-PaxSetupReleaseContract',
    'Assert-PaxSetupReleaseWritePath',
    'Test-PaxFileVersionArtifact',
    'Get-PaxManagedAssemblyMetadata',
    'Test-PaxManagedAssemblyMetadata'
)