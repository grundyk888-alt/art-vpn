[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:-beta\.\d+)?$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = Get-Item -LiteralPath $InstallerPath
$output = [IO.Path]::GetFullPath($OutputPath)
if ($source.Extension -ne '.exe' -or [IO.Path]::GetExtension($output) -ne '.zip') { throw 'DownloadPackageTypeRejected' }
if (Test-Path -LiteralPath $output) { throw 'DownloadPackageAlreadyExists' }
$signature = Get-AuthenticodeSignature -LiteralPath $source.FullName
if ($signature.Status -notin @('Valid','NotSigned')) { throw 'InstallerSignatureRejected' }
if ($source.VersionInfo.CompanyName -ne 'ARTSPORT' -or $source.VersionInfo.ProductName -ne 'ART VPN') {
    throw 'InstallerMetadataRejected'
}
$readme = Get-Item -LiteralPath (Join-Path $PSScriptRoot 'DOWNLOAD-README-RU.txt')
$expected = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
$readmeHash = (Get-FileHash -LiteralPath $readme.FullName -Algorithm SHA256).Hash
$checksums = "$expected  ART-VPN-Setup.exe`r`n$readmeHash  READ-ME-RU.txt`r`n"
$prefix = "ART-VPN-$Version/"
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($output)) -Force | Out-Null
$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$source.FullName,$prefix+'ART-VPN-Setup.exe',[IO.Compression.CompressionLevel]::Optimal) | Out-Null
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$readme.FullName,$prefix+'READ-ME-RU.txt',[IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $writer = [IO.StreamWriter]::new($archive.CreateEntry($prefix+'SHA256SUMS.txt').Open(),[Text.UTF8Encoding]::new($false))
        try { $writer.Write($checksums) } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
# Read the actual compressed bytes back; no installation or trust-store changes.
$archive = [IO.Compression.ZipFile]::OpenRead($output)
try {
    if ($archive.Entries.Count -ne 3) { throw 'DownloadArchiveShapeRejected' }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($item in @(@{Name='ART-VPN-Setup.exe';Hash=$expected},@{Name='READ-ME-RU.txt';Hash=$readmeHash})) {
            $input = $archive.GetEntry($prefix+$item.Name).Open()
            try { $actual = [BitConverter]::ToString($sha.ComputeHash($input)).Replace('-','') } finally { $input.Dispose() }
            if ($actual -ne $item.Hash) { throw 'DownloadArchiveRoundTripRejected' }
        }
    } finally { $sha.Dispose() }
} finally { $archive.Dispose() }
[pscustomobject]@{
    Status='Passed'; ZipPath=$output; ZipSha256=(Get-FileHash -LiteralPath $output).Hash
    InstallerSha256=$expected; InstallerSignature=[string]$signature.Status
    ZipBytes=(Get-Item -LiteralPath $output).Length; Files=3; RoundTripHashVerified=$true
    BrowserDownloadAcceptance='NotTested'; PublicReleaseEligible=$false
}
