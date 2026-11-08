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
