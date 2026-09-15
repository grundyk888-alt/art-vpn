[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$legal = Join-Path $repo 'third_party/core'
$lock = Get-Content -Raw -LiteralPath (Join-Path $legal 'ARTVpnCore.provenance.json') | ConvertFrom-Json
$zip = Join-Path $legal 'ARTVpnCore-corresponding-source.zip'
if ((Get-FileHash -LiteralPath $zip).Hash -ne $lock.sourceArchiveSha256) { throw 'SourceArchiveHashRejected' }
if ((& go version) -cne 'go version go1.25.5 windows/amd64') { throw 'Go1255WindowsAmd64Required' }
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw 'FreshOutputDirectoryRequired' }
New-Item -ItemType Directory -Path $output | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($entry in $archive.Entries) {
        $dest = [IO.Path]::GetFullPath((Join-Path $output $entry.FullName))
        if (-not $dest.StartsWith($output.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'ArchivePathRejected' }
    }
} finally { $archive.Dispose() }
Expand-Archive -LiteralPath $zip -DestinationPath $output
$manifestPath = Join-Path $output 'source-manifest.json'
if ((Get-FileHash -LiteralPath $manifestPath).Hash -ne $lock.sourceManifestSha256) { throw 'SourceManifestRejected' }
$source = Join-Path $output 'source'
foreach ($file in (Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json)) {
    $path = [IO.Path]::GetFullPath((Join-Path $source $file.Path))
    if (-not $path.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'ManifestPathRejected' }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $file.Sha256) { throw 'SourceFileHashRejected' }
}
$names = @('CGO_ENABLED','GOTOOLCHAIN','GOOS','GOARCH','GOCACHE','GOMODCACHE')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
try {
    $env:CGO_ENABLED='0'; $env:GOTOOLCHAIN='local'; $env:GOOS='windows'; $env:GOARCH='amd64'
    $env:GOCACHE=Join-Path $output 'go-cache'; $env:GOMODCACHE=Join-Path $output 'go-modules'
    Push-Location $source
    try {
        $tags = (Get-Content -Raw -LiteralPath 'release/DEFAULT_BUILD_TAGS_WINDOWS').Trim()
        & go build -p 2 -trimpath -buildvcs=false -tags $tags `
            -ldflags '-s -w -buildid= -checklinkname=0 -X github.com/sagernet/sing-box/constant.Version=art-vpn-continuity-20260910.1' `
            -o (Join-Path $output 'ARTVpnCore.exe') ./cmd/sing-box
        if ($LASTEXITCODE -ne 0) { throw 'CoreBuildFailed' }
    } finally { Pop-Location }
    if ((Get-FileHash -LiteralPath (Join-Path $output 'ARTVpnCore.exe')).Hash -ne $lock.sha256) { throw 'CoreReproducibilityMismatch' }
    Write-Host 'Core source/build hash verified. No service installed.'
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name,$saved[$name],'Process') }
}
