#requires -Version 7.4
param([Parameter(Mandatory)][string]$EvidenceRoot)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appRoot = Join-Path $repoRoot 'app'
$runRoot = [IO.Directory]::CreateTempSubdirectory('pax-legacy-transport-').FullName
$interpreter = (Get-Process -Id $PID).Path
$synthetic = 'legacy-inert-fixture-credential-9F66'

function Assert-Fixture([bool]$Condition, [string]$Failure) {
    if (-not $Condition) { throw $Failure }
}

Import-Module (Join-Path $appRoot 'broker/Pax/Adapter.psm1') -Force -DisableNameChecking
$recipe = ConvertFrom-PaxRecipeJson -Json '{"query":{"startDate":"2026-01-02T03:04:05Z","endDate":"2026-01-03T03:04:05Z"},"advanced":{"extraArguments":"-ResultSize 50 -EnableParallel:$false"},"fixture":{"inner":[null,false,1,1.25],"empty":[]}}'
Assert-Fixture ($recipe.query.startDate -is [string] -and $recipe.query.startDate -ceq '2026-01-02T03:04:05Z') 'Date string changed type or value.'
Assert-Fixture ($recipe.fixture.inner.Count -eq 4 -and $null -eq $recipe.fixture.inner[0] -and
    $recipe.fixture.inner[1] -is [bool] -and $recipe.fixture.inner[2] -is [long] -and
    $recipe.fixture.inner[3] -is [double] -and $recipe.fixture.empty -is [array] -and
    $recipe.fixture.empty.Count -eq 0) 'JSON value shape changed.'
$refusals = 0
foreach ($duplicate in '{"name":"one","name":"two"}', '{"items":[{"name":"one","Name":"two"}]}') {
    try { $null = ConvertFrom-PaxRecipeJson -Json $duplicate }
    catch {
        if ($_.Exception.Message -ne 'Duplicate Recipe property.') { throw }
        $refusals++
    }
}
Assert-Fixture ($refusals -eq 2) 'Expected two duplicate property refusals.'

$sqliteRoot = Join-Path $appRoot 'lib/sqlite'
$oldPath = $env:PATH
$env:PATH = $sqliteRoot + [IO.Path]::PathSeparator + $oldPath
try {
    foreach ($assembly in 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'Microsoft.Data.Sqlite.dll') {
        Add-Type -Path (Join-Path $sqliteRoot $assembly)
    }
    [SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_e_sqlite3]::new())
    . (Join-Path $appRoot 'broker/Cook/Supervisor.ps1')
    $Script:BrokerRoot = Join-Path $runRoot 'no-notification-module'
    $Script:WorkspacePath = $runRoot
    $Script:WsRegistry = [hashtable]::Synchronized(@{})
    $Script:CookRegistry = [hashtable]::Synchronized(@{})

    $connectionString = 'Data Source=' + (Join-Path $runRoot 'inert.sqlite') + ';Pooling=False'
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new($connectionString)
    $connection.Open()
    try {
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = @'
CREATE TABLE cooks (cook_id TEXT PRIMARY KEY, status TEXT, pid INTEGER, started_at TEXT, updated_at TEXT, exit_code INTEGER,
    finished_at TEXT, duration_seconds REAL, error_class TEXT, closure_reason TEXT, closure_evidence_json TEXT,
    abnormal_close_recorded_utc TEXT, summary_path TEXT, error_message TEXT);
CREATE TABLE cook_artifacts (cook_artifact_id TEXT PRIMARY KEY, cook_id TEXT, stream TEXT, artifact_kind TEXT,
    tier TEXT, location TEXT, size_bytes INTEGER, row_count INTEGER, is_append INTEGER, pantry_dataset_id TEXT, created_at TEXT);
'@
            [void]$command.ExecuteNonQuery()
        } finally { $command.Dispose() }

        $results = [Collections.Generic.List[object]]::new()
        foreach ($fixture in @(@{ Trigger='manual'; ExitCode=17 }, @{ Trigger='scheduled'; ExitCode=37 }, @{ Trigger='resume'; ExitCode=0 })) {
            $trigger = $fixture.Trigger
            $target = Join-Path $runRoot "$trigger-inert.ps1"
            $cookFolder = [IO.Directory]::CreateDirectory((Join-Path $runRoot $trigger)).FullName
            $program = @'
param([string]$StartDate, [string]$EndDate, [int]$ResultSize, [switch]$EnableParallel, [string[]]$RemainingArgs, [switch]$Force)
$receipt = @{ StartDate=$StartDate; Count=$ResultSize; CountType=$ResultSize.GetType().FullName; Parallel=[bool]$EnableParallel; Remaining=$RemainingArgs; SawCredential=($env:GRAPH_CLIENT_SECRET -ceq 'legacy-inert-fixture-credential-9F66') }
[Console]::WriteLine('fixture=' + (ConvertTo-Json -InputObject $receipt -Compress))
[Console]::WriteLine($env:GRAPH_CLIENT_SECRET)
[Console]::Error.WriteLine($env:GRAPH_CLIENT_SECRET)
'@
            [IO.File]::WriteAllText($target, $program + "`nexit " + $fixture.ExitCode)
            $checkpoint = Join-Path $runRoot 'inert checkpoint.json'
            $mode = if ($trigger -eq 'scheduled') { 'local-scheduled' } else { 'local-manual' }
            $plan = if ($trigger -eq 'resume') {
                Get-PaxResumeInvocationPlan -CheckpointPath $checkpoint -PaxScriptPath $target -ExecutionMode $mode
            } else {
                Get-PaxInvocationPlan -Recipe $recipe -PaxScriptPath $target -ExecutionMode $mode
            }
            Assert-Fixture ($plan.spawnArgv[2] -ceq '-File' -and $plan.spawnArgv[-1] -ceq $target -and
                -not ($plan.spawnArgv -contains $synthetic)) 'Launch vector did not identify only the fixed launcher and inert target.'
            $command = $connection.CreateCommand()
            try {
                $command.CommandText = "INSERT INTO cooks (cook_id, status) VALUES (`$id, 'running');"
                [void]$command.Parameters.AddWithValue('$id', $trigger)
                [void]$command.ExecuteNonQuery()
            } finally { $command.Dispose() }

            $state = [hashtable]::Synchronized(@{})
            $secret = ConvertTo-SecureString $synthetic -AsPlainText -Force
            try {
                Start-CookSupervisor -CookId $trigger -RecipeId 'inert-fixture' -CookFolder $cookFolder -PwshPath $interpreter `
                    -PaxScriptPath $target -FactPath (Join-Path $runRoot 'unused.csv') -ConnectionString $connectionString `
                    -HostName 'inert-fixture' -CookState $state -ExecutionMode $mode -ClientSecretSecure $secret `
                    -SpawnArgv $plan.spawnArgv -ParameterJson $plan.parameterJson
                $job = Get-Job | Select-Object -Last 1
                $finishedJob = $job | Wait-Job -Timeout 40
                Assert-Fixture ($null -ne $finishedJob -and $finishedJob.State -eq 'Completed') 'Legacy supervisor did not complete.'
                $null = Receive-Job -Job $job -ErrorAction Stop
            } finally {
                if ($job) { Remove-Job -Job $job -Force }
                $secret.Dispose()
            }

            $finished = ConvertFrom-PaxRecipeJson -Json ([IO.File]::ReadAllText((Join-Path $cookFolder 'finished.json')))
            Assert-Fixture ($finished.exitCode -eq $fixture.ExitCode) 'Legacy sentinel lost the target exit status.'
            $command = $connection.CreateCommand()
            try {
                $command.CommandText = 'SELECT status, exit_code FROM cooks WHERE cook_id = $id;'
                [void]$command.Parameters.AddWithValue('$id', $trigger)
                $row = $command.ExecuteReader()
                try {
                    Assert-Fixture ($row.Read() -and $row.GetInt32(1) -eq $fixture.ExitCode -and
                        $row.GetString(0) -eq $(if ($fixture.ExitCode -eq 0) { 'completed' } else { 'errored' })) 'Legacy SQLite diagnostics did not record the target result.'
                } finally { $row.Dispose() }
            } finally { $command.Dispose() }

            $receiptLines = @(Get-Content -LiteralPath (Join-Path $cookFolder 'cook.log') | Where-Object { $_ -match 'fixture=\{' })
            Assert-Fixture ($receiptLines.Count -eq 1) 'No unique typed receipt from the inert target.'
            $receipt = ConvertFrom-PaxRecipeJson -Json ($receiptLines[0] -replace '^.*?fixture=', '')
            Assert-Fixture ($receipt.SawCredential -eq $true) 'Synthetic child credential did not reach the inert target.'
            if ($trigger -eq 'resume') {
                Assert-Fixture ($receipt.Remaining.Count -eq 2 -and $receipt.Remaining[0] -ceq '-Resume' -and
                    $receipt.Remaining[1] -ceq $checkpoint) 'Resume did not receive the exact checkpoint argument separately.'
            } else {
                Assert-Fixture ($receipt.Count -eq 50 -and $receipt.CountType -ceq 'System.Int32' -and
                    $receipt.Parallel -eq $false -and $receipt.StartDate -ceq $recipe.query.startDate) 'Manual or scheduled plan lost typed parameter values.'
            }
            foreach ($artifact in Get-ChildItem -LiteralPath $cookFolder -File) {
                Assert-Fixture (-not [IO.File]::ReadAllText($artifact.FullName).Contains($synthetic)) 'Legacy diagnostic contained a synthetic secret.'
            }
            $results.Add([pscustomobject]@{ trigger=$trigger; exitCode=$finished.exitCode; typedReceipt=$true; redact=$true })
        }
        $report = [ordered]@{
            shell = $PSVersionTable.PSVersion.ToString()
            sourceSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
            parser = @{ dateAndShape=$true; duplicateRefusals=$refusals }
            executed = $results.Count
            results = $results.ToArray()
        }
        $json = ConvertTo-Json -InputObject $report -Depth 6
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot ('legacy-transport-' + $report.shell + '.json')), $json)
        $json
    } finally { $connection.Dispose() }
} finally {
    $env:PATH = $oldPath
    [IO.Directory]::Delete($runRoot, $true)
}