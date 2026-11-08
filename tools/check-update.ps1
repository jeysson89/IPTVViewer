#Requires -Version 5.1
<#
.SYNOPSIS
    Verify the in-app updater against the latest published release.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\check-update.ps1
    powershell -ExecutionPolicy Bypass -File tools\check-update.ps1 -Current 0.0.16
#>
param(
    [string]$Current,
    [int]$TimeoutSeconds = 90,
    [switch]$ExpectNoUpdate
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\IPTVViewer.exe'
if (-not (Test-Path $exe)) {
    $exe = Join-Path $root 'publish\x86\IPTVViewer.exe'
}
if (-not (Test-Path $exe)) {
    throw "IPTVViewer.exe not found. Run tools\build.ps1 -Publish first."
}

$dataDir = Join-Path $env:LOCALAPPDATA 'IPTVViewer'
$log = Join-Path $dataDir 'updatecheck.log'
if (Test-Path $log) { Remove-Item $log -Force }

$argsList = @('--check-updates')
if ($Current) { $argsList += @('--current', $Current) }

Write-Host "[check-update] running: $exe $($argsList -join ' ')"
$p = Start-Process -FilePath $exe -ArgumentList $argsList -PassThru -WindowStyle Hidden

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    if (Test-Path $log) {
        Start-Sleep -Milliseconds 300
        $text = Get-Content $log -Raw -Encoding UTF8
        if ($text -match 'hasUpdate=') { break }
    }
    Start-Sleep -Milliseconds 400
}

if ($p -and -not $p.HasExited) {
    try { $p.WaitForExit(5000) | Out-Null } catch { }
    if (-not $p.HasExited) { $p.Kill() }
}

if (-not (Test-Path $log)) {
    throw "Update check did not produce a log within $TimeoutSeconds s: $log"
}

$result = Get-Content $log -Raw -Encoding UTF8
Write-Host '--- updatecheck.log ---'
Write-Host $result

$map = @{}
foreach ($line in ($result -split "`r?`n")) {
    $i = $line.IndexOf('=')
    if ($i -gt 0) { $map[$line.Substring(0, $i)] = $line.Substring($i + 1) }
}

if ($map['error']) {
    throw "Updater error: $($map['error'])"
}

if ($ExpectNoUpdate) {
    if ($map['hasUpdate'] -ne 'False') {
        throw "Expected hasUpdate=False (current=$($map['current']), latest=$($map['latest']))"
    }
    Write-Host "[check-update] OK: no update expected (latest=$($map['latest']))"
}
else {
    if ($map['hasUpdate'] -ne 'True') {
        throw "Expected hasUpdate=True (current=$($map['current']), latest=$($map['latest']))"
    }
    Write-Host "[check-update] OK: $($map['current']) -> $($map['latest']) ($($map['tag']))"
}
