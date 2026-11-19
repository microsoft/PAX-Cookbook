#requires -Version 7.4
param([Parameter(Mandatory)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $EvidenceRoot ('output-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($runRoot)
[IO.File]::Copy($PSCommandPath, (Join-Path $runRoot 'test-source.ps1'))
$results = [Collections.Generic.List[object]]::new()
function Test-ContainsKnownLiteral([string]$Text, [string]$Secret) {
    return $Text.Contains($Secret, [StringComparison]::Ordinal)
}
function Test-OutputBoundary([string]$Text, [string]$Secret) {
    $checks = @(foreach ($literal in (@($Secret) + @($Secret -split '\r\n|\r|\n'))) {
        if ($literal.Length -eq 0) { continue }
        $positive = Test-ContainsKnownLiteral $literal $literal
        $negative = -not (Test-ContainsKnownLiteral '' $literal)
        if (-not ($positive -and $negative)) { throw 'Boundary calibration refused.' }
        @{positiveControl=$positive;negativeControl=$negative;leaked=(Test-ContainsKnownLiteral $Text $literal)}
    })
    return @{universe='Exact nonempty known literal plus nonempty CR/LF fragments in complete decoded sink text';checks=$checks;passed=(@($checks | Where-Object leaked).Count -eq 0)}
}
$knownSecret = 'synthetic-private-value'
$positiveControl = Test-ContainsKnownLiteral ('prefix ' + $knownSecret) $knownSecret
$negativeControl = -not (Test-ContainsKnownLiteral 'synthetic-private-valuf' $knownSecret)
$failure = $null
try {
    if (-not ($positiveControl -and $negativeControl)) { throw 'Calibration refused.' }
    $path = Join-Path $repoRoot 'launcher/Invoke-PAXScheduledRecipe.ps1'
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Wrapper parse failed.' }
    $helper = Join-Path $repoRoot 'app/broker/Cook/CookOutputRedactor.cs'
    if (Test-Path -LiteralPath $helper) { Add-Type -Path $helper }
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading.Tasks;
public sealed class SyntheticChunkReader : StringReader {
    private readonly int chunkSize;
    private readonly Task gate;
    public SyntheticChunkReader(string text, int chunkSize, Task gate) : base(text) {
        this.chunkSize = chunkSize; this.gate = gate;
    }
    public override async Task<int> ReadAsync(char[] buffer, int index, int count) {
        await gate.ConfigureAwait(false);
        return Read(buffer, index, Math.Min(count, chunkSize));
    }
}
'@
    $streams = @()
    foreach ($name in @('outTask', 'errTask')) {
        $assignment = @($ast.FindAll({param($node)
            $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -ceq ('$' + $name) -and
            $node.Right.Extent.Text -ne '$null'
        }, $true))
        if ($assignment.Count -ne 1) { throw 'Stream assignment selection failed.' }
        $inputBytes = [Text.Encoding]::UTF8.GetBytes("ordinary `u{03bb}`r`n" + $knownSecret + "`r`nend")
        $source = [IO.MemoryStream]::new($inputBytes)
        $reader = [IO.StreamReader]::new($source, [Text.UTF8Encoding]::new($false))
        $sink = [IO.MemoryStream]::new()
        $proc = [pscustomobject]@{StandardOutput=$reader;StandardError=$reader}
        $outFs = $sink; $errFs = $sink
        . ([scriptblock]::Create($assignment[0].Extent.Text))
        $task = Get-Variable -Name $name -ValueOnly
        $streams += @{task=$task;sink=$sink;reader=$reader;name=$name}
    }
    foreach ($stream in $streams) {
        [void]$stream.task.GetAwaiter().GetResult()
        $persisted = [Text.Encoding]::UTF8.GetString($stream.sink.ToArray())
        $leaked = Test-ContainsKnownLiteral $persisted $knownSecret
        $results.Add(@{name=$stream.name;passed=(-not $leaked);knownLiteralPersisted=$leaked;persisted=$persisted})
        $stream.reader.Dispose(); $stream.sink.Dispose()
    }
    $cases = @(
        @{name='literal';secret='synthetic-private-value';text='before synthetic-private-value after';expected='before [REDACTED] after'},
        @{name='crlf';secret="first-part`r`nother-part";text="first-part`r`nother-part";expected="[REDACTED]`r`n[REDACTED]";referenceExpected='[REDACTED]'},
        @{name='equal-length-fragments';secret="first-part`nother-part";text="other-part`nfirst-part";expected="[REDACTED]`n[REDACTED]"},
        @{name='benign-prefix-eof';secret='synthetic-private-value';text='synthetic-private-valuf synthetic-private';expected='synthetic-private-valuf synthetic-private';referenceExpected='synthetic-private-valuf ';logExpected='synthetic-private-valuf synthetic-private'},
        @{name='unicode-ansi';secret='synthetic-private-value';text="`u{03bb}`u{1F680}`e[32m`r`nsynthetic-private-value";expected="`u{03bb}`u{1F680}`e[32m`r`n[REDACTED]"},
        @{name='missing-secret';secret=$null;text="ordinary`r`n`u{1F680}";expected="ordinary`r`n`u{1F680}"},
        @{name='marker-is-secret';secret='REDACTED';text='REDACTED';expected=''}
    )
    $cases += @(
        @{name='marker-join';secret='prefix[REDACTED]';text='prefixprefix[REDACTED]';boundaryOnly=$true},
        @{name='marker-join-repeated';secret='prefix[REDACTED]';text='prefixprefixprefix[REDACTED]';boundaryOnly=$true},
        @{name='deletion-join';secret='REDACTED';text='RREDACTEDEDACTED';boundaryOnly=$true},
        @{name='marker-within-secret';secret='[REDACTED]tail';text='[REDACTED]tailtail';boundaryOnly=$true},
        @{name='overlap';secret='aba';text='aabababa';boundaryOnly=$true},
        @{name='fragment-overlap';secret="a`r`naa";text='aaaa';boundaryOnly=$true},
        @{name='single-character';secret='D';text='DD ordinary';boundaryOnly=$true},
        @{name='newline-only';secret="`n";text="ordinary`nordinary";boundaryOnly=$true},
        @{name='cr-only';secret="`r";text="ordinary`rordinary";boundaryOnly=$true},
        @{name='crlf-only';secret="`r`n";text="ordinary`r`nordinary";boundaryOnly=$true},
        @{name='unicode-marker-join';secret="`u{1F680}[REDACTED]";text="`u{1F680}`u{1F680}[REDACTED]";boundaryOnly=$true},
        @{name='empty-secret';secret='';text="ordinary`r`n`u{1F680}";expected="ordinary`r`n`u{1F680}"},
        @{name='long-chunk-boundary';secret='prefix[REDACTED]';text=(('x'*4090)+'prefixprefix[REDACTED]');boundaryOnly=$true}
    )
    foreach ($case in $cases) {
        foreach ($chunkSize in @(1, 2, 7, 4096)) {
            $gate = [Threading.Tasks.TaskCompletionSource[bool]]::new([Threading.Tasks.TaskCreationOptions]::RunContinuationsAsynchronously)
            $reader = [SyntheticChunkReader]::new($case.text, $chunkSize, $gate.Task)
            $errorReader = [SyntheticChunkReader]::new($case.text, $chunkSize, $gate.Task)
            $sink = [IO.MemoryStream]::new(); $errorSink = [IO.MemoryStream]::new()
            $outPump = [PAXCookbook.Output.CookOutputRedactor]::PumpAsync($reader, $sink, $case.secret)
            $errPump = [PAXCookbook.Output.CookOutputRedactor]::PumpAsync($errorReader, $errorSink, $case.secret)
            $bothPending = -not $outPump.IsCompleted -and -not $errPump.IsCompleted
            $gate.SetResult($true)
            [void][Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($outPump, $errPump)).GetAwaiter().GetResult()
            $actual = [Text.Encoding]::UTF8.GetString($sink.ToArray())
            $errorActual = [Text.Encoding]::UTF8.GetString($errorSink.ToArray())
            $literalResults = @()
            foreach ($literal in @($case.secret -split '\r\n|\r|\n' | Where-Object { $_.Length -gt 0 })) {
                $positive = Test-ContainsKnownLiteral ('deliberate ' + $literal) $literal
                $negative = -not (Test-ContainsKnownLiteral '' $literal)
                $literalResults += @{positiveControl=$positive;negativeControl=$negative;stdoutLeak=(Test-ContainsKnownLiteral $actual $literal);stderrLeak=(Test-ContainsKnownLiteral $errorActual $literal)}
            }
            $predicatePassed = @($literalResults | Where-Object { -not $_.positiveControl -or -not $_.negativeControl -or $_.stdoutLeak -or $_.stderrLeak }).Count -eq 0
            $stdoutBoundary = Test-OutputBoundary $actual $case.secret
            $stderrBoundary = Test-OutputBoundary $errorActual $case.secret
            $preserved = $case.boundaryOnly -or ($actual -ceq $case.expected -and $errorActual -ceq $case.expected)
            $unicodePreserved = -not $actual.Contains([char]0xfffd) -and -not $errorActual.Contains([char]0xfffd)
            $results.Add(@{name=($case.name+'-chunk-'+$chunkSize);passed=($bothPending -and $predicatePassed -and $stdoutBoundary.passed -and $stderrBoundary.passed -and $preserved -and $unicodePreserved);bothPendingBeforeRelease=$bothPending;literalResults=$literalResults;stdoutBoundary=$stdoutBoundary;stderrBoundary=$stderrBoundary;ordinaryOutputPreserved=$preserved;unicodePreserved=$unicodePreserved})
            $reader.Dispose(); $errorReader.Dispose(); $sink.Dispose(); $errorSink.Dispose()
        }
    }
    $supervisorPath = Join-Path $repoRoot 'app/broker/Cook/Supervisor.ps1'
    $supervisorAst = [Management.Automation.Language.Parser]::ParseFile($supervisorPath, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Reference supervisor parse failed.' }
    $functionAst = $supervisorAst.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Protect-CookOutputText'}, $true)
    . ([scriptblock]::Create($functionAst.Extent.Text))
    $sinkBlocks = @{}
    foreach ($name in @('logAppend','publish')) {
        $assignment = $supervisorAst.Find({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq ('$'+$name)}, $true)
        $sinkBlocks[$name] = $assignment.Right.Find({param($node) $node -is [Management.Automation.Language.ScriptBlockExpressionAst]}, $true).ScriptBlock.GetScriptBlock()
    }
    foreach ($case in $cases) {
        $knownSecret = $case.secret
        $CookId = 'synthetic'
        $subscriber = @{closed=$false;sendQueue=[Collections.Concurrent.ConcurrentQueue[string]]::new();sendQueueCap=32;droppedFrames=[int[]]@(0);sendSignal=[Threading.AutoResetEvent]::new($false)}
        $WsRegistry = @{$CookId=[Collections.ArrayList]::Synchronized([Collections.ArrayList]@($subscriber))}
        $sink = [IO.MemoryStream]::new()
        $writer = [IO.StreamWriter]::new($sink, [Text.UTF8Encoding]::new($false), 1024, $true)
        & $sinkBlocks.logAppend $writer $case.text
        & $sinkBlocks.publish 'stdout' $case.text
        $writer.Flush()
        $actual = [Text.Encoding]::UTF8.GetString($sink.ToArray())
        $frame = $subscriber.sendQueue.ToArray()[0] | ConvertFrom-Json
        $expected = if ($case.ContainsKey('referenceExpected')) { $case.referenceExpected } else { $case.expected }
        $expectedLog = if ($case.ContainsKey('logExpected')) { $case.logExpected } else { $expected }
        $logBoundary = Test-OutputBoundary $actual $case.secret
        $frameBoundary = Test-OutputBoundary $frame.data $case.secret
        $functionBoundary = Test-OutputBoundary (Protect-CookOutputText -Text $case.text -KnownSecret $case.secret) $case.secret
        $preserved = $case.boundaryOnly -or ($actual -ceq ($expectedLog+[Environment]::NewLine) -and $frame.data -ceq $expected)
        $results.Add(@{name=('reference-sinks-'+$case.name);passed=($logBoundary.passed -and $frameBoundary.passed -and $functionBoundary.passed -and $preserved);logBoundary=$logBoundary;frameBoundary=$frameBoundary;functionBoundary=$functionBoundary;ordinaryOutputPreserved=$preserved})
        $writer.Dispose(); $sink.Dispose(); $subscriber.sendSignal.Dispose()
    }
    foreach ($frameCase in @(
        @{secret='aba';frames=@('ab','ordinary','a')},
        @{secret='prefix[REDACTED]';frames=@('prefix','ordinary','[REDACTED]')},
        @{secret="`u{1F680}tail";frames=@("`u{1F680}",'ordinary','tail')}
    )) {
        for ($mask = 0; $mask -lt 8; $mask++) {
            $knownSecret = $frameCase.secret
            $CookId = 'synthetic'
            $subscriber = @{closed=$false;sendQueue=[Collections.Concurrent.ConcurrentQueue[string]]::new();sendQueueCap=1;droppedFrames=[int[]]@(0);sendSignal=[Threading.AutoResetEvent]::new($false)}
            $WsRegistry = @{$CookId=[Collections.ArrayList]::Synchronized([Collections.ArrayList]@($subscriber))}
            $delivered = ''
            for ($index = 0; $index -lt $frameCase.frames.Count; $index++) {
                & $sinkBlocks.publish 'stdout' $frameCase.frames[$index]
                if ($mask -band (1 -shl $index)) {
                    $encoded = $null
                    if (-not $subscriber.sendQueue.TryDequeue([ref]$encoded)) { throw 'Synthetic subscriber dequeue failed.' }
                    $delivered += ($encoded | ConvertFrom-Json).data
                }
            }
            $boundary = Test-OutputBoundary $delivered $knownSecret
            $dropExercised = $mask -ne 5 -or $subscriber.droppedFrames[0] -gt 0
            $results.Add(@{name=('decoded-subscriber-drop-mask-'+$mask);passed=($boundary.passed -and $dropExercised);boundary=$boundary;droppedFrames=$subscriber.droppedFrames[0];mask=$mask})
            $subscriber.sendSignal.Dispose()
        }
    }
    $mainTry = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })[-1]
    $catchText = $mainTry.CatchClauses[0].Body.Extent.Text
    $catchBlock = [scriptblock]::Create($catchText.Substring(1, $catchText.Length - 2))
    function Write-WrapperInternalError {
        param($CookFolder, $Reason, $Detail)
        $script:capturedFailure = @{reason=$Reason;detail=$Detail}
    }
    $jsonFunction = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Write-WrapperJson'}, $true)
    . ([scriptblock]::Create($jsonFunction.Extent.Text))
    $Script:CurrentCookId = $null
    foreach ($errorText in @('ordinary failure', 'synthetic-private-value')) {
        try { throw $errorText } catch { & $catchBlock }
        Write-WrapperJson -CookFolder $runRoot -FileName 'synthetic-failure.json' -Body $script:capturedFailure
        $persisted = [IO.File]::ReadAllText((Join-Path $runRoot 'synthetic-failure.json'))
        $body = $persisted | ConvertFrom-Json
        $results.Add(@{name='actual-wrapper-catch-and-json-sink';passed=($body.reason -ceq 'unhandled_exception' -and -not (Test-ContainsKnownLiteral $persisted 'synthetic-private-value'));persisted=$persisted})
    }
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.Environment['GRAPH_CLIENT_SECRET'] = 'synthetic-private-value'
    $psi.Environment['GRAPH_CLIENT_CERT_PASSWORD'] = 'synthetic-password'
    $clientSecretSecure = ConvertTo-SecureString 'synthetic-private-value' -AsPlainText -Force
    $secretBSTR = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($clientSecretSecure)
    $knownSecret = 'synthetic-private-value'; $plaintextSecret = $knownSecret
    $proc = $null; $startedSuccessfully = $false
    $outFs = [IO.MemoryStream]::new(); $errFs = [IO.MemoryStream]::new()
    $outTask = $null; $errTask = $null
    $environmentCopy = $psi.Environment
    $finallyText = $mainTry.Finally.Extent.Text
    . ([scriptblock]::Create($finallyText.Substring(1, $finallyText.Length - 2)))
    $results.Add(@{name='actual-wrapper-prestart-finally';passed=($secretBSTR -eq [IntPtr]::Zero -and $null -eq $clientSecretSecure -and $null -eq $knownSecret -and $null -eq $plaintextSecret -and -not $environmentCopy.ContainsKey('GRAPH_CLIENT_SECRET') -and -not $environmentCopy.ContainsKey('GRAPH_CLIENT_CERT_PASSWORD') -and -not $outFs.CanWrite -and -not $errFs.CanWrite);childStarted=$false;bstrReleased=($secretBSTR -eq [IntPtr]::Zero);secretReferenceReleased=($null -eq $knownSecret);streamsClosed=(-not $outFs.CanWrite -and -not $errFs.CanWrite)})
} catch { $failure = $_.ToString() }
$record = [ordered]@{
    universe='Actual wrapper stdout/stderr assignments, catch/JSON sink and prestart finally; production pump over gated chunk readers; actual reference logAppend and publish blocks; synthetic private fixtures only'
    predicate='Ordinal Contains of the known synthetic literal in the decoded persistence sink'
    positiveControl=$positiveControl;negativeControl=$negativeControl
    sourceHash=(Get-FileHash -LiteralPath $path).Hash
    testHash=(Get-FileHash -LiteralPath $PSCommandPath).Hash
    runtimeBinding=@{kind='Add-Type in-memory assembly';assemblyPath=[PAXCookbook.Output.CookOutputRedactor].Assembly.Location;mvid=[PAXCookbook.Output.CookOutputRedactor].Assembly.ManifestModule.ModuleVersionId.ToString();sourcePath=$helper;sourceHash=(Get-FileHash -LiteralPath $helper).Hash;supervisorPath=$supervisorPath;supervisorHash=(Get-FileHash -LiteralPath $supervisorPath).Hash}
    results=$results.ToArray();failed=@($results | Where-Object {-not $_.passed}).Count;failure=$failure
}
$record | ConvertTo-Json -Depth 8 -EscapeHandling EscapeNonAscii | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
[pscustomobject]@{path=(Join-Path $runRoot 'result.json');failed=$record.failed;failure=$failure} | ConvertTo-Json
if ($record.failed -or $failure -or $results.Count -eq 0) { throw 'Output containment checks failed.' }