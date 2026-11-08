#Requires -Version 5.1
<#
.SYNOPSIS
    One-command release: bump version, publish x86 single-file, package,
    create GitHub release (draft -> upload assets -> publish), verify the
    in-app updater, commit and push.

.DESCRIPTION
    Paths are resolved from the repository root, so the checkout can live
    anywhere. Assets are uploaded to a draft release first and the release is
    published only after both files are confirmed, which avoids the
    "release without assets" problem caused by upload timeouts.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\release.ps1 -NotesFile docs\notes-0.0.18.md
    powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 0.0.18 -DryRun
#>
param(
    [string]$Version,
    [string]$NotesFile,
    [string]$Notes,
    [switch]$DryRun,
    [switch]$KeepDraft,
    [switch]$NoCommit,
    [switch]$SkipUpdateCheck
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$projDir = Join-Path $root 'IPTVViewer'
$csproj = Join-Path $projDir 'IPTVViewer.csproj'
$distDir = Join-Path $root 'dist'
$publishDir = Join-Path $root 'publish\x86'
$repoSlug = 'jeysson89/IPTVViewer'

function Write-Step([string]$Text) { Write-Host "[release] $Text" }

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    $prev = $ErrorActionPreference
    # PowerShell 5.1 turns native stderr into error records; keep them as data.
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $File @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prev
    }
    [pscustomobject]@{ Code = $code; Output = ($output -join "`n") }
}

function Get-ProjectVersion {
    $text = [IO.File]::ReadAllText($csproj)
    $m = [regex]::Match($text, '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $m.Success) { throw "Cannot find <Version> in $csproj" }
    return $m.Groups[1].Value
}

function Set-ProjectVersion([string]$NewVersion) {
    $bytes = [IO.File]::ReadAllBytes($csproj)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [IO.File]::ReadAllText($csproj)
    $new = [regex]::Replace($text, '<Version>\s*[^<\s]+\s*</Version>', "<Version>$NewVersion</Version>")
    # Keep the original encoding (with or without BOM) to avoid noisy diffs.
    [IO.File]::WriteAllText($csproj, $new, (New-Object System.Text.UTF8Encoding($hasBom)))
}

function Find-Gh {
    $cmd = Get-Command gh -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        'C:\Program Files\GitHub CLI\gh.exe',
        (Join-Path $env:LOCALAPPDATA 'GitHubCLI\gh.exe'),
        (Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw 'GitHub CLI (gh) not found. Install it or add it to PATH.'
}

function Assert-Ok($Result, [string]$What) {
    if ($Result.Code -ne 0) {
        throw "$What failed (exit $($Result.Code)):`n$($Result.Output)"
    }
}

# ---------- version ----------
$prevVersion = Get-ProjectVersion
if (-not $Version) {
    $parts = $prevVersion.Split('.')
    if ($parts.Count -lt 3) { throw "Unexpected version format: $prevVersion" }
    $patch = [int]$parts[2] + 1
    $Version = "$($parts[0]).$($parts[1]).$patch"
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 0.0.18, got: $Version" }
$tag = "v$Version"

Write-Step "project: $root"
Write-Step "version: $prevVersion -> $Version (tag $tag)"

# ---------- build ----------
Get-Process -Name 'IPTVViewer' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($prevVersion -ne $Version) {
    Set-ProjectVersion $Version
    Write-Step "csproj version updated"
}

Push-Location $projDir
try {
    Write-Step 'publishing x86 single-file...'
    $pub = Invoke-Native 'dotnet' @(
        'publish', $csproj, '-c', 'Release', '-r', 'win-x86',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeAllContentForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-o', $publishDir
    )
    Assert-Ok $pub 'dotnet publish'
}
finally {
    Pop-Location
}

$publishedExe = Join-Path $publishDir 'IPTVViewer.exe'
if (-not (Test-Path $publishedExe)) { throw "Published exe not found: $publishedExe" }

# ---------- package ----------
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$distExe = Join-Path $distDir 'IPTVViewer.exe'
$distZip = Join-Path $distDir "IPTVViewer-$Version.zip"

Copy-Item $publishedExe $distExe -Force
if (Test-Path $distZip) { Remove-Item $distZip -Force }
Compress-Archive -Path $distExe -DestinationPath $distZip -CompressionLevel Optimal

$exeSize = (Get-Item $distExe).Length
$zipSize = (Get-Item $distZip).Length
Write-Step ("package: exe {0:N0} bytes, zip {1:N0} bytes" -f $exeSize, $zipSize)

if ($DryRun) {
    Write-Step 'DryRun: skipping GitHub release and git commit.'
    Write-Step "DryRun finished. Version in csproj is now $Version."
    return
}

# ---------- release notes ----------
$notesPath = $null
$tempNotes = $false
if ($NotesFile) {
    if (-not (Test-Path $NotesFile)) { throw "Notes file not found: $NotesFile" }
    $notesPath = (Resolve-Path $NotesFile).Path
}
elseif ($Notes) {
    $notesPath = Join-Path ([IO.Path]::GetTempPath()) "iptv-notes-$Version.md"
    [IO.File]::WriteAllText($notesPath, $Notes, (New-Object System.Text.UTF8Encoding($false)))
    $tempNotes = $true
}
else {
    $notesPath = Join-Path ([IO.Path]::GetTempPath()) "iptv-notes-$Version.md"
    $template = Join-Path $root 'docs\release-template.md'
    if (Test-Path $template) {
        # .NET reads UTF-8 (with or without BOM) correctly, unlike PowerShell 5.1.
        $body = [IO.File]::ReadAllText($template).Replace('{{VERSION}}', $Version)
    }
    else {
        $body = "IPTV Newline v$Version"
    }
    [IO.File]::WriteAllText($notesPath, $body, (New-Object System.Text.UTF8Encoding($false)))
    $tempNotes = $true
}

# ---------- github ----------
$gh = Find-Gh
Write-Step "gh: $gh"

$exists = Invoke-Native $gh @('release', 'view', $tag, '--repo', $repoSlug)
if ($exists.Code -eq 0) {
    throw "Release $tag already exists. Choose another version or delete the release first."
}

Write-Step "creating draft release $tag..."
$create = Invoke-Native $gh @(
    'release', 'create', $tag,
    '--draft',
    '--title', "IPTV Newline v$Version",
    '--notes-file', $notesPath,
    '--repo', $repoSlug
)
Assert-Ok $create "gh release create $tag"
Write-Step "draft created: $($create.Output.Trim())"

function Upload-Asset([string]$Path) {
    $name = Split-Path $Path -Leaf
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Write-Step "uploading $name (attempt $attempt)..."
        $r = Invoke-Native $gh @('release', 'upload', $tag, "$Path#$name", '--clobber', '--repo', $repoSlug)
        if ($r.Code -eq 0) { return }
        Write-Step "upload failed ($($r.Code)): $($r.Output)"
        Start-Sleep -Seconds ([Math]::Min(30, 5 * $attempt))
    }
    throw "Failed to upload $name after 3 attempts."
}

Upload-Asset $distExe
Upload-Asset $distZip

Write-Step 'verifying assets...'
$view = Invoke-Native $gh @('release', 'view', $tag, '--json', 'isDraft,assets', '--repo', $repoSlug)
Assert-Ok $view "gh release view $tag"
$info = $view.Output | ConvertFrom-Json

foreach ($expected in @(
    @{ Name = 'IPTVViewer.exe'; Size = $exeSize },
    @{ Name = "IPTVViewer-$Version.zip"; Size = $zipSize }
)) {
    $asset = $info.assets | Where-Object { $_.name -eq $expected.Name }
    if (-not $asset) { throw "Asset missing on release: $($expected.Name)" }
    if ([int64]$asset.size -ne [int64]$expected.Size) {
        throw "Asset size mismatch for $($expected.Name): $($asset.size) != $($expected.Size)"
    }
}
Write-Step 'assets verified'

if ($KeepDraft) {
    if ($tempNotes -and (Test-Path $notesPath)) { Remove-Item $notesPath -Force -ErrorAction SilentlyContinue }
    Write-Step "KeepDraft: release $tag left as a draft: https://github.com/$repoSlug/releases"
    Write-Step "Publish it later with: gh release edit $tag --draft=false --repo $repoSlug"
    return
}

if ($info.isDraft) {
    Write-Step 'publishing release (draft -> public)...'
    $edit = Invoke-Native $gh @('release', 'edit', $tag, '--draft=false', '--repo', $repoSlug)
    Assert-Ok $edit "gh release edit $tag --draft=false"
}

$final = Invoke-Native $gh @('release', 'view', $tag, '--json', 'isDraft,url', '--repo', $repoSlug)
Assert-Ok $final "gh release view $tag"
$finalInfo = $final.Output | ConvertFrom-Json
if ($finalInfo.isDraft) { throw "Release $tag is still a draft." }
Write-Step "release published: $($finalInfo.url)"

if ($tempNotes -and (Test-Path $notesPath)) { Remove-Item $notesPath -Force -ErrorAction SilentlyContinue }

# ---------- updater verification ----------
if (-not $SkipUpdateCheck) {
    Write-Step "verifying updater: $prevVersion -> $Version"
    & (Join-Path $PSScriptRoot 'check-update.ps1') -Current $prevVersion
    if ($LASTEXITCODE -ne 0) { throw 'Updater verification failed.' }
}

# ---------- git ----------
if (-not $NoCommit) {
    Write-Step 'committing...'
    $add = Invoke-Native 'git' @('-C', $root, 'add', '-A')
    Assert-Ok $add 'git add'

    $diff = Invoke-Native 'git' @('-C', $root, 'diff', '--cached', '--quiet')
    if ($diff.Code -eq 0) {
        Write-Step 'nothing to commit'
    }
    else {
        $msg = "v$Version"
        $commit = Invoke-Native 'git' @('-C', $root, 'commit', '-m', $msg)
        Assert-Ok $commit 'git commit'

        $push = Invoke-Native 'git' @('-C', $root, 'push', 'origin', 'HEAD')
        Assert-Ok $push 'git push'
        Write-Step 'pushed to origin'
    }
}

Write-Step "DONE: v$Version"
Write-Step "exe: $distExe"
Write-Step "zip: $distZip"
