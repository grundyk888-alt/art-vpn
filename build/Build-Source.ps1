[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw 'FreshOutputDirectoryRequired' }
New-Item -ItemType Directory -Path $output | Out-Null
$version = (Get-Content -Raw -LiteralPath (Join-Path $repo 'version.json') | ConvertFrom-Json).version
$receipts = @()
foreach ($name in @('service','ui','installer')) {
    $projectName = @{service='ARTVpn.Service';ui='ARTVpn.UI';installer='ARTVpn.Setup'}[$name]
    $project = Join-Path $repo "standalone/$name/$projectName.csproj"
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        --artifacts-path (Join-Path $output 'intermediates') -o (Join-Path $output $name) `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true "-p:Version=$version"
    if ($LASTEXITCODE -ne 0) { throw "PublishFailed:$name" }
}
# These entry points execute bounded fixtures, not the installed service.
$exe = Join-Path $output 'service/ARTVpn.Service.exe'
foreach ($test in @('--contract-test','--first-connection-test','--engine-test',
    '--controller-test','--provider-replacement-test','--route-mode-test',
    '--qualification-policy-test','--node-qualification-test',
    '--release-continuity-test','--connection-diagnosis-test','--shutdown-lifetime-test',
    '--resource-budget-test','--bypass-refresh-test','--provider-adapter-test')) {
    $result = & $exe $test | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $result.status -cne 'Passed') { throw "RegressionFailed:$test" }
    if ($result.PSObject.Properties['secretDisplayed'] -and $result.secretDisplayed) { throw 'FixtureSecretDisplayed' }
    if ($result.PSObject.Properties['productionNetworkChanged'] -and $result.productionNetworkChanged) { throw 'FixtureChangedProduction' }
    $receipts += [ordered]@{test=$test;result=$result}
    Write-Host "$test Passed"
}
[IO.File]::WriteAllText((Join-Path $output 'fixture-results.json'),
    ($receipts | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Host 'Source checks complete. Outputs are component binaries, NOT a consumer installer.'
