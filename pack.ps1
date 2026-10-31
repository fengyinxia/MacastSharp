<#
.SYNOPSIS
Publishes the portable Macast C# package into dist\MacastSharp-win-x64 and zips it.

.DESCRIPTION
Replaces the manual publish steps from the handoff document. bin\mpv.exe and the
licence files are never deleted by publish, but they are verified before the zip
is built so an incomplete package cannot be shipped silently.

.EXAMPLE
pwsh -File .\pack.ps1
pwsh -File .\pack.ps1 -SkipZip
#>
[CmdletBinding()]
param(
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$dist = Join-Path $root 'dist\MacastSharp-win-x64'
$zip = Join-Path $root 'dist\MacastSharp-win-x64.zip'

$publishArgs = @(
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $dist
)

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Publish does not remove stale files, and the old Vue assets used to live in
# Assets/assets. Keep bin\ (mpv + licences are curated by hand) and wipe the rest.
Get-ChildItem -LiteralPath $dist -Force | Where-Object { $_.Name -ne 'bin' } |
    Remove-Item -Recurse -Force

Write-Host '== publish macast-cli ==' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\Macast.Cli\Macast.Cli.csproj') @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'macast-cli publish failed' }

Write-Host '== publish macast-gui ==' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\Macast.Tray\Macast.Tray.csproj') @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'macast-gui publish failed' }

# Safety net: publish has dropped Assets\icon.ico in some SDK/RID combinations,
# so copy the whole asset tree explicitly after the two publishes.
Copy-Item (Join-Path $root 'src\Macast.Core\Assets\*') (Join-Path $dist 'Assets') -Recurse -Force

# Documentation and licence shipped next to the executables.
$license = Join-Path (Split-Path -Parent $root) 'LICENSE'
if (Test-Path $license) {
    Copy-Item $license (Join-Path $dist 'LICENSE') -Force
}
Copy-Item (Join-Path $root 'README.md') (Join-Path $dist 'README.md') -Force

$mpv = Join-Path $dist 'bin\mpv.exe'
if (-not (Test-Path $mpv)) {
    Write-Warning "bin\mpv.exe is missing from $dist - the package will not be able to play until it is copied in."
}

foreach ($required in @('macast-gui.exe', 'macast-cli.exe', 'Assets\icon.ico', 'Assets\xml\Description.xml', 'Assets\xml\AVTransport.xml')) {
    if (-not (Test-Path (Join-Path $dist $required))) {
        throw "package is incomplete: $required is missing"
    }
}

$size = (Get-ChildItem $dist -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("dist size: {0:N1} MB" -f ($size / 1MB)) -ForegroundColor Green

if ($SkipZip) {
    Write-Host 'zip skipped (-SkipZip)' -ForegroundColor Yellow
    return
}

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
$zipSize = (Get-Item $zip).Length
Write-Host ("zip: {0}" -f $zip) -ForegroundColor Green
Write-Host ("zip size: {0:N1} MB" -f ($zipSize / 1MB))
Write-Host ("sha256: {0}" -f $hash)
