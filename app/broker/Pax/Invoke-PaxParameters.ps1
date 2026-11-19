param([Parameter(Mandatory)][string]$EnginePath)

try {
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false, $true)
    $document = [System.Text.Json.JsonDocument]::Parse([Console]::ReadLine())
    $parameters = @{}
    foreach ($entry in $document.RootElement.EnumerateObject()) {
        $name = $entry.Name
        $value = $entry.Value
        if ($name -cnotmatch '^[A-Za-z][A-Za-z0-9]*$' -or $parameters.ContainsKey($name)) {
            throw 'Invalid invocation parameter.'
        }
        switch ($value.ValueKind) {
            'String' { $parameters.Add($name, $value.GetString()) }
            'True' { $parameters.Add($name, $true) }
            'False' { $parameters.Add($name, $false) }
            'Array' {
                $items = [Collections.Generic.List[string]]::new()
                foreach ($item in $value.EnumerateArray()) {
                    if ($item.ValueKind -ne 'String') { throw 'Invalid invocation array.' }
                    $items.Add($item.GetString())
                }
                $parameters.Add($name, $items.ToArray())
            }
            'Number' {
                $integer = 0
                if ($value.TryGetInt32([ref]$integer)) { $parameters.Add($name, $integer) }
                else {
                    $number = $value.GetDouble()
                    if (-not [double]::IsFinite($number)) { throw 'Invalid invocation number.' }
                    $parameters.Add($name, $number)
                }
            }
            default { throw 'Invalid invocation value.' }
        }
    }
} catch {
    [Console]::Error.WriteLine('PAX Cookbook refused invalid invocation data.')
    exit 1
} finally {
    if ($null -ne $document) { $document.Dispose() }
}

$global:LASTEXITCODE = 0
& $EnginePath @parameters
$targetSucceeded = $?
if ($targetSucceeded) { exit 0 }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
exit 1