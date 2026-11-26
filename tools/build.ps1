#Requires -Version 5.1
<#
.SYNOPSIS
    Build or publish IPTV Newline.

.DESCRIPTION
    Build    : dotnet build of IPTVViewer.csproj.
    Publish  : self-contained single-file win-x86 build into <root>\publish\x86.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build.ps1
    powershell -ExecutionPolicy Bypass -File tools\build.ps1 -Publish
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projDir = Join-Path $root 'IPTVViewer'
$csproj = Join-Path $projDir 'IPTVViewer.csproj'

if (-not (Test-Path $csproj)) {
    throw "Project not found: $csproj"
}

# VLC встраивается в exe только при наличии vlc-data\vlc-*.zip.
# На чистом клоне папка отсутствует (она в .gitignore), поэтому
# собираем архивы из NuGet-пакета до build/publish.
function Ensure-VlcData {
    $x64 = Join-Path $projDir 'vlc-data\vlc-x64.zip'
    $x86 = Join-Path $projDir 'vlc-data\vlc-x86.zip'
    if ((Test-Path $x64) -and (Test-Path $x86)) { return }

    Write-Host '[vlc] vlc-data archives missing; packing from NuGet package...'
    & dotnet restore $csproj -r win-x86
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

    & (Join-Path $projDir 'pack_vlc.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'pack_vlc.ps1 failed' }

    if (-not (Test-Path $x64) -or -not (Test-Path $x86)) {
        throw "VLC archives were not created: $x64 / $x86"
    }
    Write-Host '[vlc] vlc-data ready'
}

Ensure-VlcData

# Stop a running copy so the build output is not locked.
Get-Process -Name 'IPTVViewer' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Push-Location $projDir
try {
    if ($Publish) {
        $out = Join-Path $root 'publish\x86'
        Write-Host "[publish] $Configuration win-x86 -> $out"
        & dotnet publish $csproj -c $Configuration -r win-x86 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true -o $out
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

        $exe = Join-Path $out 'IPTVViewer.exe'
        if (-not (Test-Path $exe)) { throw "Published exe not found: $exe" }
        $sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
        Write-Host "[publish] OK: $exe ($sizeMb MB)"
    }
    else {
        Write-Host "[build] $Configuration"
        & dotnet build $csproj -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
        Write-Host '[build] OK'
    }
}
finally {
    Pop-Location
}
