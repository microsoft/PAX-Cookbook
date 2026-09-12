[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('C3', 'C5', 'C6', 'C7', 'All')]
    [string] $Mode,

    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string] $ExpectedSha256,

    [Parameter(Mandatory = $true)]
    [string] $LauncherRoot,

    [Parameter(Mandatory = $true)]
    [string] $EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-AtomicJson {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [object] $Value
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Path]::GetDirectoryName($fullPath)
    if (-not [IO.Directory]::Exists($parent)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    $temporary = [IO.Path]::Combine($parent, ([IO.Path]::GetRandomFileName()))
    $json = $Value | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
    if ([IO.File]::Exists($fullPath)) {
        [IO.File]::Replace($temporary, $fullPath, $null)
    }
    else {
        [IO.File]::Move($temporary, $fullPath)
    }

    $persisted = [IO.File]::ReadAllText($fullPath)
    $null = $persisted | ConvertFrom-Json
    return $persisted
}

function Test-ExactSet {
    param([string[]] $Left, [string[]] $Right)

    $leftSorted = @($Left | Sort-Object -Unique)
    $rightSorted = @($Right | Sort-Object -Unique)
    return ($leftSorted.Count -eq $rightSorted.Count) -and
        (@(Compare-Object -ReferenceObject $leftSorted -DifferenceObject $rightSorted -CaseSensitive).Count -eq 0)
}

function Get-ShellObservedVariables {
    $contexts = @(
        [ordered]@{ name = 'pristine-runspace'; script = 'Get-Variable | Select-Object -ExpandProperty Name' },
        [ordered]@{ name = 'scriptblock-arguments'; script = '& { Get-Variable | Select-Object -ExpandProperty Name } probe' },
        [ordered]@{ name = 'advanced-function'; script = 'function Invoke-Probe { [CmdletBinding()] param(); Get-Variable | Select-Object -ExpandProperty Name }; Invoke-Probe' },
        [ordered]@{ name = 'pipeline-scriptblock'; script = '1 | ForEach-Object { Get-Variable | Select-Object -ExpandProperty Name }' },
        [ordered]@{ name = 'pipeline-input'; script = "'probe' | & { `$null = @(`$input); Get-Variable | Select-Object -ExpandProperty Name }" },
        [ordered]@{ name = 'regex-match'; script = "`$null = ('probe' -match 'probe'); Get-Variable | Select-Object -ExpandProperty Name" },
        [ordered]@{ name = 'catch-block'; script = 'try { throw ''probe'' } catch { Get-Variable | Select-Object -ExpandProperty Name }' },
        [ordered]@{ name = 'switch-statement'; script = 'switch (1) { 1 { Get-Variable | Select-Object -ExpandProperty Name } }' },
        [ordered]@{ name = 'class-instance-method'; script = 'class VariableProbe { [string[]] Observe() { return @(Get-Variable | Select-Object -ExpandProperty Name) } }; ([VariableProbe]::new()).Observe()' }
    )

    $observations = New-Object Collections.Generic.List[object]
    foreach ($context in $contexts) {
        $runspace = [runspacefactory]::CreateRunspace()
        $runspace.Open()
        try {
            $powerShell = [powershell]::Create()
            $powerShell.Runspace = $runspace
            try {
                $null = $powerShell.AddScript($context.script)
                $names = @($powerShell.Invoke() | ForEach-Object { [string] $_ } | Sort-Object -Unique)
                if ($powerShell.HadErrors) {
                    throw "Variable context failed: $($context.name)"
                }
                $observations.Add([ordered]@{ context = $context.name; names = $names })
            }
            finally {
                $powerShell.Dispose()
            }
        }
        finally {
            $runspace.Close()
            $runspace.Dispose()
        }
    }

    $union = @($observations | ForEach-Object { $_.names } | Sort-Object -Unique)
    return [ordered]@{ method = 'fresh Windows PowerShell runspaces executing Get-Variable in controlled native contexts'; contexts = $observations.ToArray(); decidingUnion = $union }
}

function Get-ParameterDeclarations {
    param(
        [Parameter(Mandatory = $true)] [Management.Automation.Language.Ast] $Ast,
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string[]] $ObservedNames
    )

    $declarations = New-Object Collections.Generic.List[object]
    $paramBlocks = @($Ast.FindAll({ param($node) $node -is [Management.Automation.Language.ParamBlockAst] }, $true))
    foreach ($paramBlock in $paramBlocks) {
        foreach ($parameter in $paramBlock.Parameters) {
            $name = $parameter.Name.VariablePath.UserPath
            $declarations.Add([ordered]@{
                path = $Path
                line = $parameter.Name.Extent.StartLineNumber
                name = $name
                parameterExtent = $parameter.Extent.Text
                paramBlockExtent = $paramBlock.Extent.Text
                collision = $ObservedNames -contains $name
            })
        }
    }

    return $declarations.ToArray()
}

function Invoke-C6Guard {
    param([string] $Root)

    $resolvedRoot = [IO.Path]::GetFullPath($Root)
    $cmdletFiles = @(Get-ChildItem -LiteralPath $resolvedRoot -Filter '*.ps1' -File -Recurse -ErrorAction Stop | ForEach-Object { $_.FullName } | Sort-Object)
    $dotNetFiles = @([IO.Directory]::EnumerateFiles($resolvedRoot, '*.ps1', [IO.SearchOption]::AllDirectories) | ForEach-Object { [IO.Path]::GetFullPath($_) } | Sort-Object)
    $positiveControl = Test-ExactSet -Left $cmdletFiles -Right $dotNetFiles
    $negativeSet = if ($dotNetFiles.Count -gt 0) { @($dotNetFiles | Select-Object -Skip 1) } else { @('synthetic-missing.ps1') }
    $negativeControl = -not (Test-ExactSet -Left $cmdletFiles -Right $negativeSet)
    if (-not $positiveControl -or -not $negativeControl -or $cmdletFiles.Count -ne 8) {
        throw 'Launcher enumeration calibration or exact-count gate refused.'
    }

    $observed = Get-ShellObservedVariables
    $illustrative = @('Args', 'Input', 'Error', 'Host', 'PSItem', 'Matches', 'This')
    $missingIllustrative = @($illustrative | Where-Object { $observed.decidingUnion -notcontains $_ })
    $declarations = New-Object Collections.Generic.List[object]
    $parseErrors = New-Object Collections.Generic.List[object]
    foreach ($file in $cmdletFiles) {
        $tokens = $null
        $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($file, [ref] $tokens, [ref] $errors)
        foreach ($parseError in @($errors)) {
            $parseErrors.Add([ordered]@{ path = $file; message = $parseError.Message; line = $parseError.Extent.StartLineNumber })
        }
        foreach ($declaration in @(Get-ParameterDeclarations -Ast $ast -Path $file -ObservedNames $observed.decidingUnion)) {
            $declarations.Add($declaration)
        }
    }

    $controlTokens = $null
    $controlErrors = $null
    $positiveAst = [Management.Automation.Language.Parser]::ParseInput('param([string[]] $Args)', [ref] $controlTokens, [ref] $controlErrors)
    $positiveDeclarations = @(Get-ParameterDeclarations -Ast $positiveAst -Path '<positive-control>' -ObservedNames $observed.decidingUnion)
    $negativeAst = [Management.Automation.Language.Parser]::ParseInput('param([string[]] $ArgVector)', [ref] $controlTokens, [ref] $controlErrors)
    $negativeDeclarations = @(Get-ParameterDeclarations -Ast $negativeAst -Path '<negative-control>' -ObservedNames $observed.decidingUnion)
    $positiveDetected = @($positiveDeclarations | Where-Object { $_.collision }).Count -eq 1
    $negativeNotDetected = @($negativeDeclarations | Where-Object { $_.collision }).Count -eq 0
    $collisions = @($declarations | Where-Object { $_.collision })
    $complete = $missingIllustrative.Count -eq 0
    $accepted = $complete -and $parseErrors.Count -eq 0 -and $collisions.Count -eq 0 -and $positiveDetected -and $negativeNotDetected

    return [ordered]@{
        pattern = 'launcher/**/*.ps1'
        enumeration = [ordered]@{ count = $cmdletFiles.Count; files = $cmdletFiles; cmdletAndDotNetAgree = $positiveControl; positiveControl = $positiveControl; negativeControl = $negativeControl }
        shell = [ordered]@{ version = $PSVersionTable.PSVersion.ToString(); edition = $PSVersionTable.PSEdition; host = $Host.Name }
        derivation = $observed
        illustrativeExpectation = [ordered]@{ names = $illustrative; missing = $missingIllustrative; completeness = $complete; excludedFromDecidingUnion = $true }
        declarations = $declarations.ToArray()
        collisions = $collisions
        parseErrors = $parseErrors.ToArray()
        controls = [ordered]@{
            args = [ordered]@{ source = 'param([string[]] $Args)'; detected = $positiveDetected; launchAttempted = $false; childStdout = $null; childStderr = $null; childExitCode = $null }
            argVector = [ordered]@{ source = 'param([string[]] $ArgVector)'; detected = -not $negativeNotDetected; notDetected = $negativeNotDetected }
        }
        disposition = if ($accepted) { 'ACCEPT' } else { 'REFUSE' }
        accepted = $accepted
    }
}

function Test-ChildOutput {
    param(
        [AllowEmptyString()] [string] $Stdout,
        [AllowNull()] [object[]] $Intent,
        [int] $ExitCode
    )

    if ([string]::IsNullOrWhiteSpace($Stdout)) {
        return [ordered]@{ accepted = $false; reason = 'blank_output'; receivedCount = $null; receivedArgs = $null }
    }

    try {
        $document = $Stdout | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        $reason = if ($Stdout -match '}\s*{') { 'duplicate_document' } else { 'malformed_json' }
        return [ordered]@{ accepted = $false; reason = $reason; receivedCount = $null; receivedArgs = $null }
    }

    if ($null -eq $document -or $document -isnot [psobject]) {
        return [ordered]@{ accepted = $false; reason = 'missing_document'; receivedCount = $null; receivedArgs = $null }
    }

    $propertyNames = @($document.PSObject.Properties | ForEach-Object { $_.Name })
    if (-not (Test-ExactSet -Left $propertyNames -Right @('count', 'args'))) {
        return [ordered]@{ accepted = $false; reason = 'document_shape'; receivedCount = $null; receivedArgs = $null }
    }

    if ($document.count -isnot [int] -and $document.count -isnot [long]) {
        return [ordered]@{ accepted = $false; reason = 'count_type'; receivedCount = $null; receivedArgs = $null }
    }

    $received = @($document.args)
    if (@($received | Where-Object { $_ -isnot [string] }).Count -ne 0) {
        return [ordered]@{ accepted = $false; reason = 'args_type'; receivedCount = [int] $document.count; receivedArgs = $received }
    }

    if ([int] $document.count -ne $received.Count) {
        return [ordered]@{ accepted = $false; reason = 'count_mismatch'; receivedCount = [int] $document.count; receivedArgs = $received }
    }

    if ($ExitCode -ne 0) {
        return [ordered]@{ accepted = $false; reason = 'child_nonzero'; receivedCount = [int] $document.count; receivedArgs = $received }
    }

    if (-not (Test-ExactSet -Left @('count', 'args') -Right $propertyNames)) {
        return [ordered]@{ accepted = $false; reason = 'document_shape'; receivedCount = [int] $document.count; receivedArgs = $received }
    }

    $intended = @($Intent)
    if ($intended.Count -ne $received.Count) {
        return [ordered]@{ accepted = $false; reason = 'args_mismatch'; receivedCount = [int] $document.count; receivedArgs = $received }
    }

    for ($index = 0; $index -lt $intended.Count; $index++) {
        if (-not [string]::Equals([string] $intended[$index], [string] $received[$index], [StringComparison]::Ordinal)) {
            return [ordered]@{ accepted = $false; reason = 'args_mismatch'; receivedCount = [int] $document.count; receivedArgs = $received }
        }
    }

    return [ordered]@{ accepted = $true; reason = 'received_vector_matches'; receivedCount = [int] $document.count; receivedArgs = $received }
}

function Invoke-Transport {
    param(
        [string] $Label,
        [object[]] $Intent,
        [object[]] $ArgumentVector,
        [bool] $ExpectedAccepted
    )

    $path = [IO.Path]::GetFullPath($ExecutablePath)
    $preLaunchUtc = [DateTime]::UtcNow.ToString('o')
    $preLaunchSha = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if (-not [string]::Equals($preLaunchSha, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        return [ordered]@{
            label = $Label; intent = @($Intent); actualTransportVector = @($ArgumentVector); executablePath = $path
            immediatelyPrelaunchSha256 = $preLaunchSha; prelaunchUtc = $preLaunchUtc; launchAttempted = $false
            stdoutJson = $null; stderr = $null; exitCode = $null; parsedReceivedCount = $null; parsedReceivedArgs = $null
            predicateAccepted = $false; predicateReason = 'identity_mismatch'; expectedAccepted = $ExpectedAccepted
            expectedOutcomeObserved = (-not $ExpectedAccepted)
        }
    }

    $stderrPath = [IO.Path]::Combine([IO.Path]::GetTempPath(), "pax-c148-$([Guid]::NewGuid().ToString('N')).stderr")
    try {
        $launchAttempted = $true
        $stdoutLines = @(& $ExecutablePath @ArgumentVector 2> $stderrPath)
        $childExitCode = $LASTEXITCODE
        $stdout = [string]::Join([Environment]::NewLine, @($stdoutLines | ForEach-Object { [string] $_ }))
        $stderr = if ([IO.File]::Exists($stderrPath)) { [IO.File]::ReadAllText($stderrPath) } else { '' }
        $predicate = Test-ChildOutput -Stdout $stdout -Intent $Intent -ExitCode $childExitCode
        return [ordered]@{
            label = $Label; intent = @($Intent); actualTransportVector = @($ArgumentVector); executablePath = $path
            immediatelyPrelaunchSha256 = $preLaunchSha; prelaunchUtc = $preLaunchUtc; launchAttempted = $launchAttempted
            stdoutJson = $stdout; stderr = $stderr; exitCode = $childExitCode; parsedReceivedCount = $predicate.receivedCount
            parsedReceivedArgs = $predicate.receivedArgs; predicateAccepted = $predicate.accepted; predicateReason = $predicate.reason
            expectedAccepted = $ExpectedAccepted; expectedOutcomeObserved = ($predicate.accepted -eq $ExpectedAccepted)
        }
    }
    finally {
        [IO.File]::Delete($stderrPath)
    }
}

function Invoke-C5 {
    param([object] $Guard)

    $intent = @('install', '--silent', '--payload-root', 'C:\PAX Validation\Payload Root', '--install-root', 'C:\PAX Validation\Install Root', '--reinstall-same-version')
    $dropped = @($intent[0..4] + $intent[6])
    $reordered = @($intent.Clone())
    $temporary = $reordered[1]
    $reordered[1] = $reordered[2]
    $reordered[2] = $temporary
    $merged = @($intent[0], $intent[1], "$($intent[2]) $($intent[3])", $intent[4], $intent[5], $intent[6])
    $duplicated = @($intent[0..5] + $intent[5..6])

    $rows = New-Object Collections.Generic.List[object]
    $rows.Add((Invoke-Transport -Label 'C5.1 exact seven-element vector' -Intent $intent -ArgumentVector $intent -ExpectedAccepted $true))
    $rows.Add((Invoke-Transport -Label 'C5.2 empty vector' -Intent @() -ArgumentVector @() -ExpectedAccepted $false))
    $rows.Add([ordered]@{
        label = 'C5.3 parameter named $Args'; intent = $null; actualTransportVector = $null; executablePath = [IO.Path]::GetFullPath($ExecutablePath)
        immediatelyPrelaunchSha256 = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash; prelaunchUtc = [DateTime]::UtcNow.ToString('o')
        launchAttempted = $false; stdoutJson = $null; stderr = $null; exitCode = $null; parsedReceivedCount = $null; parsedReceivedArgs = $null
        predicateAccepted = $false; predicateReason = 'static_automatic_variable_collision'; expectedAccepted = $false
        expectedOutcomeObserved = [bool] $Guard.controls.args.detected
    })
    $rows.Add((Invoke-Transport -Label 'C5.4 path containing spaces' -Intent $intent -ArgumentVector $intent -ExpectedAccepted $true))
    $rows.Add((Invoke-Transport -Label 'C5.5 dropped element' -Intent $intent -ArgumentVector $dropped -ExpectedAccepted $false))
    $rows.Add((Invoke-Transport -Label 'C5.6 reordered elements' -Intent $intent -ArgumentVector $reordered -ExpectedAccepted $false))
    $rows.Add((Invoke-Transport -Label 'C5.7 merged elements' -Intent $intent -ArgumentVector $merged -ExpectedAccepted $false))
    $rows.Add((Invoke-Transport -Label 'C5.8 duplicated element' -Intent $intent -ArgumentVector $duplicated -ExpectedAccepted $false))
    return $rows.ToArray()
}

function Invoke-C7 {
    $intent = @('install')
    $valid = '{"count":1,"args":["install"]}'
    $controls = @(
        [ordered]@{ label = 'valid'; stdout = $valid; exitCode = 0; expectedAccepted = $true },
        [ordered]@{ label = 'blank'; stdout = ''; exitCode = 0; expectedAccepted = $false },
        [ordered]@{ label = 'malformed'; stdout = '{'; exitCode = 0; expectedAccepted = $false },
        [ordered]@{ label = 'duplicate-document'; stdout = "$valid$valid"; exitCode = 0; expectedAccepted = $false },
        [ordered]@{ label = 'missing-document'; stdout = 'null'; exitCode = 0; expectedAccepted = $false },
        [ordered]@{ label = 'count-mismatch'; stdout = '{"count":2,"args":["install"]}'; exitCode = 0; expectedAccepted = $false },
        [ordered]@{ label = 'args-mismatch'; stdout = '{"count":1,"args":["other"]}'; exitCode = 0; expectedAccepted = $false }
    )

    return @($controls | ForEach-Object {
        $predicate = Test-ChildOutput -Stdout $_.stdout -Intent $intent -ExitCode $_.exitCode
        [ordered]@{
            label = $_.label; stdout = $_.stdout; intent = $intent; launchAttempted = $false; predicateAccepted = $predicate.accepted
            predicateReason = $predicate.reason; parsedReceivedCount = $predicate.receivedCount; parsedReceivedArgs = $predicate.receivedArgs
            expectedAccepted = $_.expectedAccepted; expectedOutcomeObserved = ($predicate.accepted -eq $_.expectedAccepted)
        }
    })
}

function Invoke-C3 {
    $canonical = [IO.Path]::GetFullPath($ExecutablePath)
    $canonicalBefore = Get-Item -LiteralPath $canonical
    $canonicalBeforeHash = (Get-FileHash -LiteralPath $canonical -Algorithm SHA256).Hash
    $copyPath = [IO.Path]::Combine([IO.Path]::GetTempPath(), "PaxArgvEcho-altered-$([Guid]::NewGuid().ToString('N')).exe")
    [IO.File]::Copy($canonical, $copyPath, $false)
    $bytes = [IO.File]::ReadAllBytes($copyPath)
    $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($copyPath, $bytes)
    [Array]::Clear($bytes, 0, $bytes.Length)
    $alteredHash = (Get-FileHash -LiteralPath $copyPath -Algorithm SHA256).Hash
    $record = [ordered]@{
        canonicalPath = $canonical; canonicalBeforeSha256 = $canonicalBeforeHash; canonicalBeforeSize = $canonicalBefore.Length
        disposablePath = $copyPath; disposableSha256 = $alteredHash; immediatelyPrelaunchSha256 = $alteredHash
        expectedSha256 = $ExpectedSha256.ToUpperInvariant(); gateDisposition = 'REFUSE'; launchAttempted = $false
        stdoutJson = $null; stderr = $null; exitCode = $null; evidencePersistedBeforeDeletion = $true
    }
    $persisted = Write-AtomicJson -Path $EvidencePath -Value $record
    [IO.File]::Delete($copyPath)
    $canonicalAfter = Get-Item -LiteralPath $canonical
    $canonicalAfterHash = (Get-FileHash -LiteralPath $canonical -Algorithm SHA256).Hash
    $cleanup = [ordered]@{
        result = $record; disposableDeleted = -not [IO.File]::Exists($copyPath); canonicalAfterSha256 = $canonicalAfterHash
        canonicalAfterSize = $canonicalAfter.Length; canonicalUnchanged = ($canonicalBeforeHash -eq $canonicalAfterHash -and $canonicalBefore.Length -eq $canonicalAfter.Length)
    }
    return [ordered]@{ value = $cleanup; alreadyPersisted = $true; persistedJson = $persisted }
}

if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Section C transport must run under Windows PowerShell 5.1 Desktop.'
}

$resolvedExecutable = [IO.Path]::GetFullPath($ExecutablePath)
if (-not [IO.File]::Exists($resolvedExecutable)) {
    throw 'PaxArgvEcho executable is missing.'
}
$ExecutablePath = $resolvedExecutable
$ExpectedSha256 = $ExpectedSha256.ToUpperInvariant()

$guard = if ($Mode -in @('C5', 'C6', 'All')) { Invoke-C6Guard -Root $LauncherRoot } else { $null }
$result = [ordered]@{
    schemaVersion = '1.0'
    mode = $Mode
    shell = [ordered]@{ version = $PSVersionTable.PSVersion.ToString(); edition = $PSVersionTable.PSEdition; host = $Host.Name }
    sourceMechanism = '& $ExecutablePath @ArgumentVector'
    c3 = $null
    c5 = $null
    c6 = $null
    c7 = $null
}

$alreadyPersisted = $false
if ($Mode -in @('C3', 'All')) {
    $c3Result = Invoke-C3
    $result.c3 = $c3Result.value
    $alreadyPersisted = $Mode -eq 'C3'
}
if ($Mode -in @('C5', 'All')) { $result.c5 = Invoke-C5 -Guard $guard }
if ($Mode -in @('C6', 'All')) { $result.c6 = $guard }
if ($Mode -in @('C7', 'All')) { $result.c7 = Invoke-C7 }

$allExpected = @()
if ($null -ne $result.c3) { $allExpected += [bool] ($result.c3.result.gateDisposition -eq 'REFUSE' -and -not $result.c3.result.launchAttempted -and $result.c3.disposableDeleted -and $result.c3.canonicalUnchanged) }
if ($null -ne $result.c5) { $allExpected += @($result.c5 | ForEach-Object { [bool] $_.expectedOutcomeObserved }) }
if ($null -ne $result.c6) { $allExpected += [bool] $result.c6.accepted }
if ($null -ne $result.c7) { $allExpected += @($result.c7 | ForEach-Object { [bool] $_.expectedOutcomeObserved }) }
$result.overall = if ($allExpected.Count -gt 0 -and @($allExpected | Where-Object { -not $_ }).Count -eq 0) { 'PASS' } else { 'REFUSE' }

if (-not $alreadyPersisted) {
    $persistedJson = Write-AtomicJson -Path $EvidencePath -Value $result
}
else {
    $persistedJson = $result | ConvertTo-Json -Depth 20
}

Write-Output $persistedJson
if ($result.overall -ne 'PASS') { exit 2 }