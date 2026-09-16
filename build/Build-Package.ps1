[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputRoot,[Parameter(Mandatory)][string]$CoreBinary)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $output){throw 'FreshOutputDirectoryRequired'}
$manifest=Get-Content (Join-Path $repo 'release-manifest.v1.json') -Raw|ConvertFrom-Json
$coreEntry=$manifest.files|Where-Object path -eq 'runtime/ARTVpnCore.exe'
if((Get-FileHash -LiteralPath $CoreBinary).Hash -cne $coreEntry.sha256){throw 'CoreHashMismatch'}
New-Item -ItemType Directory -Path $output|Out-Null
$payload=Join-Path $output 'payload'
Copy-Item -LiteralPath (Join-Path $repo 'payload-template') -Destination $payload -Recurse
function PublishComponent([string]$area,[string]$assembly,[string]$destination,[string[]]$extra=@()) {
 & dotnet publish (Join-Path $repo "standalone/$area/$assembly.csproj") -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $output 'intermediates') -o $destination -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ('-p:Version='+$manifest.version) @extra
 if($LASTEXITCODE){throw ('BuildFailed:'+ $area)}
}
PublishComponent service ARTVpn.Service (Join-Path $output 'service')
PublishComponent ui ARTVpn.UI (Join-Path $output 'ui')
PublishComponent installer ARTVpn.Setup (Join-Path $output 'maintenance')
Copy-Item (Join-Path $output 'service/ARTVpn.Service.exe') (Join-Path $payload 'runtime/ARTVpn.Service.exe')
Copy-Item (Join-Path $output 'ui/ARTVpn.UI.exe') (Join-Path $payload 'runtime/ARTVpn.UI.exe')
Copy-Item (Join-Path $output 'maintenance/ARTVpn.Setup.exe') (Join-Path $payload 'runtime/ARTVpn.Maintenance.exe')
Copy-Item -LiteralPath $CoreBinary -Destination (Join-Path $payload 'runtime/ARTVpnCore.exe')
foreach($entry in $manifest.files){$file=Join-Path $payload $entry.path;$entry.bytes=(Get-Item $file).Length;$entry.sha256=(Get-FileHash $file).Hash}
$manifest|ConvertTo-Json -Depth 8|Set-Content (Join-Path $payload 'manifest.v1.json') -Encoding utf8
$bundle=Join-Path $output 'payload.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $bundle
PublishComponent installer ARTVpn.Setup (Join-Path $output 'setup') @(('-p:PayloadBundlePath='+$bundle))
Copy-Item (Join-Path $output 'setup/ARTVpn.Setup.exe') (Join-Path $output 'ART-VPN-Setup.exe')
Write-Output 'Built unsigned setup. No installation, runtime tests, signing key or network changes were performed.'
