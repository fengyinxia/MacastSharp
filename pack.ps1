<#
.SYNOPSIS
Publishes the Windows x64 GUI only, without a player or bundled .NET runtime.
#>
[CmdletBinding()]
param(
    [switch]$SkipZip,
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$PackageName = 'MacastSharp-gui-win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$name = $PackageName
$dist = Join-Path $root "dist\$name"
$zip = Join-Path $root "dist\$name.zip"
$stage = Join-Path $root (".work\publish-" + [Guid]::NewGuid().ToString('N'))

try {
    Write-Host '== publish GUI (framework-dependent, no player) ==' -ForegroundColor Cyan
    dotnet publish (Join-Path $root 'src\Macast.Tray\Macast.Tray.csproj') `
        -c Release -r win-x64 --self-contained false `
        -p:SelfContained=false -p:PublishSelfContained=false -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false `
        -p:DebugType=None -p:DebugSymbols=false -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'macast-gui publish failed' }

    $runtimeConfig = Get-Content (Join-Path $root 'src\Macast.Tray\bin\Release\net8.0-windows\win-x64\macast-gui.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtimeConfig.runtimeOptions.includedFrameworks -or
        $runtimeConfig.runtimeOptions.frameworks.name -contains 'Microsoft.AspNetCore.App') {
        throw 'publish must depend on the installed Desktop Runtime, without bundled frameworks or ASP.NET Core'
    }

    Copy-Item (Join-Path $root 'src\Macast.Core\Assets\*') (Join-Path $stage 'Assets') -Recurse -Force
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE')
    Copy-Item (Join-Path $root 'licenses') (Join-Path $stage 'licenses') -Recurse -Force
    Copy-Item (Join-Path $root 'README.md') (Join-Path $stage 'README.md')

    foreach ($file in @('macast-gui.exe', 'LICENSE', 'licenses/AntdUI-LICENSE.txt', 'Assets\icon.ico',
        'Assets\xml\Description.xml', 'Assets\xml\AVTransport.xml',
        'Assets\xml\RenderingControl.xml', 'Assets\xml\ConnectionManager.xml', 'Assets\xml\NirvanaControl.xml')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $file) -PathType Leaf)) {
            throw "package is incomplete: $file"
        }
    }
    if ((Test-Path (Join-Path $stage 'bin')) -or
        (Test-Path (Join-Path $stage 'macast-cli.exe')) -or
        (Test-Path (Join-Path $stage 'coreclr.dll'))) {
        throw 'unexpected player, CLI or .NET runtime in GUI-only package'
    }

    $manifest = Join-Path $stage 'SHA256SUMS.txt'
    Get-ChildItem $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
        "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $([IO.Path]::GetRelativePath($stage, $_.FullName))"
    } | Set-Content $manifest -Encoding utf8

    # Only replace the previous GUI release after publish and validation succeed.
    if (Test-Path $dist) {
        $probe = [IO.File]::Open((Join-Path $dist 'macast-gui.exe'), 'Open', 'ReadWrite', 'None')
        $probe.Dispose()
        Remove-Item $dist -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $dist) | Out-Null
    Move-Item $stage $dist

    $size = (Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ("dist: {0} ({1:N2} MiB)" -f $dist, ($size / 1MB)) -ForegroundColor Green
    Write-Host '.NET 8 Desktop Runtime x64 and an external MPV/PotPlayer are required. ASP.NET Core is not required.'
    if ($SkipZip) { return }

    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -Force
    Write-Host ("zip: {0} ({1:N2} MiB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
    Write-Host ("sha256: {0}" -f (Get-FileHash $zip -Algorithm SHA256).Hash)
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
